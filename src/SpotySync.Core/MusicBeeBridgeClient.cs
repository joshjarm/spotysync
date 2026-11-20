using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpotySync;

public sealed class MusicBeeBridgeClient : IDisposable
{
    private readonly PortableSettings settings;
    private readonly HttpClient http;
    private readonly Action<string> log;
    private readonly bool ownsClient;

    public MusicBeeBridgeClient(PortableSettings settings, Action<string>? log = null, HttpClient? httpClient = null)
    {
        this.settings = settings;
        this.log = log ?? (_ => { });
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        ownsClient = httpClient is null;
    }

    private string BaseUrl => $"http://127.0.0.1:{settings.MusicBeeBridgePort}/mbsync/";
    private string TokenPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBeeSyncToService", "AutomationToken.txt");

    public async Task<int> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(BaseUrl + "health", cancellationToken);
            if (!response.IsSuccessStatusCode) return 0;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("automationVersion", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var version) ? version : 0;
        }
        catch { return 0; }
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await GetVersionAsync(cancellationToken) >= 21) return;
        if (!settings.AutoStartMusicBee) throw new InvalidOperationException("MusicBee/SpotySync bridge is not available. Start MusicBee and verify the new plugin is installed.");
        var executable = settings.ResolvePath(settings.MusicBeeExe);
        if (!File.Exists(executable)) executable = settings.MusicBeeExe;
        if (!File.Exists(executable)) throw new FileNotFoundException("MusicBee.exe was not found. Update MusicBeeExe in SpotySync.ini.", executable);
        log("Starting MusicBee and waiting for the SpotySync plugin…");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(750, cancellationToken);
            if (await GetVersionAsync(cancellationToken) >= 21) return;
        }
        throw new InvalidOperationException("The MusicBee bridge did not become ready. Install mb_MusicBeeSyncToService.dll from the SpotySync Plugin folder, fully restart MusicBee, and retry.");
    }

    public async Task<MusicBeeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRunningAsync(cancellationToken);
        using var document = await PostAsync("v2/snapshot", new { includeLibrary = true, includePlaylists = true }, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new InvalidOperationException(Error(root));
        return root.TryGetProperty("snapshot", out var snapshot)
            ? snapshot.Deserialize<MusicBeeSnapshot>(JsonStore.Options) ?? new MusicBeeSnapshot()
            : new MusicBeeSnapshot();
    }

    public async Task<(int Added, int Removed)> ApplyPlaylistAsync(PlaylistPlan plan, bool allowRemovals, int maxRemovals, bool dryRun, CancellationToken cancellationToken = default)
    {
        using var document = await PostAsync("v2/apply", new
        {
            playlistId = plan.LocalPlaylistId,
            playlistName = plan.Name,
            desiredFiles = plan.DesiredLocalFiles,
            expectedDigest = plan.ExpectedLocalDigest,
            allowRemovals,
            maxRemovals,
            dryRun
        }, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new InvalidOperationException(Error(root));
        return (SpotifyApi.Int(root, "added"), SpotifyApi.Int(root, "removed"));
    }

    public async Task<MusicBeeImportResult> ImportFilesAsync(IEnumerable<string> files, bool primeAfterImport = false,
        CancellationToken cancellationToken = default)
    {
        // Keep paths that MusicBee may have renamed between the disk scan and
        // this request. Bridge v18 can reconcile those stale paths against the
        // live MusicBee library; filtering with File.Exists here silently
        // dropped exactly the files that needed reconciliation.
        var paths = files.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return new MusicBeeImportResult();
        using var document = await PostAsync("v2/import", new
        {
            files = paths, primeAfterImport, primeMilliseconds = 1000
        }, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new InvalidOperationException(Error(root));
        return new MusicBeeImportResult
        {
            Requested = SpotifyApi.Int(root, "requested"),
            Imported = SpotifyApi.Int(root, "imported"),
            Primed = SpotifyApi.Int(root, "primed"),
            PrimeWarning = root.TryGetProperty("primeWarning", out var warning) && warning.ValueKind == JsonValueKind.String ? warning.GetString() ?? "" : ""
        };
    }

    private async Task<JsonDocument> PostAsync(string endpoint, object payload, CancellationToken cancellationToken)
    {
        if (!File.Exists(TokenPath)) throw new FileNotFoundException("MusicBee automation token was not found. Fully restart MusicBee after installing the plugin.", TokenPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonStore.Options)
        };
        request.Headers.Add("X-MBSync-Token", File.ReadAllText(TokenPath).Trim());
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode, "MusicBee bridge request failed.", body);
        return JsonDocument.Parse(body);
    }

    private static string Error(JsonElement root) => root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? "MusicBee bridge operation failed." : "MusicBee bridge operation failed.";
    public void Dispose() { if (ownsClient) http.Dispose(); }
}
