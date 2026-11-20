using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotySync;

public sealed class SpotifyAuth : IDisposable
{
    public const string RequiredScopes = "user-library-read playlist-read-private playlist-read-collaborative playlist-modify-private playlist-modify-public user-read-private";
    private readonly PortableSettings settings;
    private readonly HttpClient http;
    private readonly Action<string> log;
    private readonly bool ownsClient;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private SpotifyToken? token;

    public SpotifyAuth(PortableSettings settings, Action<string>? log = null, HttpClient? httpClient = null)
    {
        this.settings = settings;
        this.log = log ?? (_ => { });
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        ownsClient = httpClient is null;
        token = JsonStore.Load<SpotifyToken?>(settings.TokenPath, () => null);
    }

    public bool HasSavedLogin => token is { RefreshToken.Length: > 0 } || token is { AccessToken.Length: > 0 };

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            token ??= JsonStore.Load<SpotifyToken?>(settings.TokenPath, () => null);
            if (!forceRefresh && token is { AccessToken.Length: > 0 } && token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return token.AccessToken;
            if (token is { RefreshToken.Length: > 0 })
            {
                token = await RefreshAsync(token.RefreshToken, cancellationToken);
                JsonStore.SaveAtomic(settings.TokenPath, token);
                return token.AccessToken;
            }
            throw new InvalidOperationException("Spotify is not connected. Use Connect Spotify first.");
        }
        finally { tokenLock.Release(); }
    }

    /// <summary>
    /// Signs in with Authorization Code + PKCE in the user's browser. Spotify only accepts redirect addresses
    /// registered on the app, and it only reveals a mismatch after sign-in, so several common loopback forms
    /// are tried in turn (the configured one first) while a listener waits on every one of them.
    /// Returns the redirect pattern that worked so it can be remembered.
    /// </summary>
    public async Task<string> LoginAsync(CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        var listeners = new List<(HttpListener Listener, Callback Callback)>();
        try
        {
            foreach (var candidate in CallbackCandidates(settings.RedirectUri))
            {
                var listener = new HttpListener();
                try { listener.Prefixes.Add(candidate.Prefix); listener.Start(); listeners.Add((listener, candidate)); }
                catch { listener.Close(); } // port busy (e.g. another app) - just skip that form
            }
            if (listeners.Count == 0) throw new InvalidOperationException("Could not open a local port for the Spotify login.");

            var waits = listeners.Select(l => WaitForCallbackAsync(l.Listener, l.Callback, cancellationToken)).ToList();
            var all = Task.WhenAny(waits);
            (Callback Callback, string Code, string State, string Error)? result = null;
            for (var i = 0; i < listeners.Count && result is null; i++)
            {
                var callback = listeners[i].Callback;
                if (i > 0) log($"Spotify did not accept the previous redirect address; trying {callback.Pattern} ({i + 1}/{listeners.Count}). You can ignore an error tab.");
                else log("Opening Spotify sign-in in your browser…");
                Process.Start(new ProcessStartInfo(AuthorizeUrl(callback.RedirectUri, challenge, state)) { UseShellExecute = true });
                var last = i == listeners.Count - 1;
                var finished = await Task.WhenAny(all, Task.Delay(last ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(12), cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (finished == all) result = await (await all);
            }
            if (result is null) throw new TimeoutException("Spotify sign-in timed out. Add http://127.0.0.1/callback as a Redirect URI on your Spotify app and try again.");

            var (used, code, returnedState, error) = result.Value;
            if (error.Length > 0) throw new InvalidOperationException("Spotify authorization failed: " + error);
            if (code.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returnedState)))
                throw new InvalidOperationException("Spotify authorization returned an invalid code or state. Please retry.");

            using var response = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = used.RedirectUri,
                ["client_id"] = settings.SpotifyClientId,
                ["code_verifier"] = verifier
            }), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode, "Spotify token exchange failed.", body);
            token = ParseToken(body, "");
            JsonStore.SaveAtomic(settings.TokenPath, token);
            log("Spotify authorization saved to the portable Data folder.");
            return used.Pattern;
        }
        finally
        {
            foreach (var (listener, _) in listeners) { try { listener.Close(); } catch { } }
        }
    }

    private string AuthorizeUrl(string redirectUri, string challenge, string state) =>
        "https://accounts.spotify.com/authorize?" + Form(new Dictionary<string, string>
        {
            ["client_id"] = settings.SpotifyClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = RequiredScopes,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["state"] = state
        });

    private static async Task<(Callback, string, string, string)> WaitForCallbackAsync(HttpListener listener, Callback callback, CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var query = context.Request.QueryString;
            var code = query["code"] ?? ""; var error = query["error"] ?? ""; var returned = query["state"] ?? "";
            if (code.Length == 0 && error.Length == 0) { context.Response.StatusCode = 404; context.Response.Close(); continue; } // favicon etc.
            var html = error.Length == 0
                ? "<html><body style='font:16px Segoe UI;background:#121212;color:#fff;padding:42px'><h2>SpotySync is connected</h2><p>You can close this tab and return to SpotySync.</p></body></html>"
                : "<html><body style='font:16px Segoe UI;background:#121212;color:#fff;padding:42px'><h2>Spotify login was not completed</h2><p>Return to SpotySync for details.</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
            return (callback, code, returned, error);
        }
    }

    /// <summary>A redirect address to try: the concrete URI, the listener prefix, and the pattern to remember.</summary>
    public sealed record Callback(string RedirectUri, string Prefix, string Pattern);

    /// <summary>
    /// The configured address first, then the common loopback forms other tools register
    /// (root path on any port, spotDL's 9900, spotipy's 8888). Port-less patterns get a free port,
    /// which Spotify accepts for 127.0.0.1 loopback addresses.
    /// </summary>
    public static List<Callback> CallbackCandidates(string configured)
    {
        var patterns = new List<string> { configured, "http://127.0.0.1/callback", "http://127.0.0.1/", "http://127.0.0.1:9900/", "http://127.0.0.1:8888/callback" };
        var result = new List<Callback>();
        foreach (var pattern in patterns.Where(p => Uri.TryCreate(p, UriKind.Absolute, out _)))
        {
            var uri = new Uri(pattern);
            var path = uri.AbsolutePath; // "/" or "/callback" etc.
            var key = (uri.IsDefaultPort ? "*" : uri.Port.ToString()) + path.TrimEnd('/');
            if (result.Any(r => r.Pattern.Length > 0 && KeyOf(r.Pattern) == key)) continue;
            var port = uri.IsDefaultPort ? FindFreePort() : uri.Port;
            var redirect = $"http://127.0.0.1:{port}{path}";
            var prefix = redirect.EndsWith('/') ? redirect : redirect + "/";
            result.Add(new Callback(redirect, prefix, pattern));
        }
        return result;

        static string KeyOf(string p) { var u = new Uri(p); return (u.IsDefaultPort ? "*" : u.Port.ToString()) + u.AbsolutePath.TrimEnd('/'); }
    }

    public void Logout()
    {
        token = null;
        if (File.Exists(settings.TokenPath)) File.Delete(settings.TokenPath);
    }

    private async Task<SpotifyToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = settings.SpotifyClientId
        }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)) Logout();
            throw new ApiException((int)response.StatusCode, "Spotify login could not be refreshed. Reconnect Spotify.", body);
        }
        return ParseToken(body, refreshToken);
    }

    private static SpotifyToken ParseToken(string json, string existingRefreshToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new SpotifyToken
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String ? refresh.GetString() ?? existingRefreshToken : existingRefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt32(out var seconds) ? seconds : 3600),
            Scope = root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String ? scope.GetString() ?? "" : ""
        };
    }

    private static int FindFreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        try { return ((IPEndPoint)socket.LocalEndpoint).Port; }
        finally { socket.Stop(); }
    }

    private static string Form(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        tokenLock.Dispose();
        if (ownsClient) http.Dispose();
    }
}
