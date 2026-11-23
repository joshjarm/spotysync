using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpotySync;

public sealed class SpotifyApi : IDisposable
{
    private const string ApiBase = "https://api.spotify.com/v1";
    private readonly PortableSettings settings;
    private readonly SpotifyAuth auth;
    private readonly HttpClient http;
    private readonly Action<string> log;
    private readonly bool ownsClient;

    public SpotifyApi(PortableSettings settings, SpotifyAuth auth, Action<string>? log = null, HttpClient? httpClient = null)
    {
        this.settings = settings;
        this.auth = auth;
        this.log = log ?? (_ => { });
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        ownsClient = httpClient is null;
    }

    public async Task<(string Id, string DisplayName)> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await SendJsonAsync(HttpMethod.Get, "/me", null, cancellationToken);
        var root = doc.RootElement;
        return (String(root, "id"), String(root, "display_name"));
    }

    public async Task<List<TrackInfo>> GetTracksAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var result = new List<TrackInfo>();
        var unique = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var batch in unique.Chunk(50))
        {
            using var doc = await SendJsonAsync(HttpMethod.Get, "/tracks?ids=" + string.Join(",", batch.Select(Uri.EscapeDataString)), null, cancellationToken);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in tracks.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var track = ParseTrack(item);
                    if (track.SpotifyId.Length > 0) result.Add(track);
                }
        }
        return result;
    }

    public async Task<LikedTrackCache> GetLikedTracksAsync(string userId, bool forceFullRefresh = false, CancellationToken cancellationToken = default)
    {
        var cache = JsonStore.Load(settings.LikedCachePath, () => new LikedTrackCache());
        var cacheMatchesUser = cache.SpotifyUserId.Equals(userId, StringComparison.OrdinalIgnoreCase);
        var cacheFresh = cacheMatchesUser && cache.Tracks.Count > 0 && settings.LikedCacheFullRefreshDays > 0 &&
            cache.FullRefreshedAt > DateTimeOffset.UtcNow.AddDays(-settings.LikedCacheFullRefreshDays);
        var full = forceFullRefresh || !cacheMatchesUser || cache.Tracks.Count == 0 || !cacheFresh;
        var known = cache.Tracks.Select(x => x.SpotifyId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fetched = new List<TrackInfo>();
        var next = "/me/tracks?limit=50";
        var pages = 0;
        var reachedKnown = false;
        do
        {
            using var doc = await SendJsonAsync(HttpMethod.Get, next, null, cancellationToken);
            pages++;
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var wrapper in items.EnumerateArray())
                {
                    var trackElement = wrapper.TryGetProperty("track", out var oldTrack) ? oldTrack :
                        wrapper.TryGetProperty("item", out var newItem) ? newItem : default;
                    if (trackElement.ValueKind != JsonValueKind.Object) continue;
                    var track = ParseTrack(trackElement);
                    if (wrapper.TryGetProperty("added_at", out var added) && added.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(added.GetString(), out var addedAt)) track.AddedAt = addedAt;
                    if (track.SpotifyId.Length == 0) continue;
                    if (!full && known.Contains(track.SpotifyId)) reachedKnown = true;
                    fetched.Add(track);
                }
            }
            next = String(root, "next");
            if (pages == 1 || pages % 5 == 0 || next.Length == 0)
                log($"Liked Songs cache: read page {pages} ({fetched.Count} tracks this refresh)…");
        } while (next.Length > 0 && (full || !reachedKnown));

        var tracks = full ? fetched : fetched.Concat(cache.Tracks).GroupBy(x => x.SpotifyId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        cache = new LikedTrackCache
        {
            SpotifyUserId = userId,
            RefreshedAt = DateTimeOffset.UtcNow,
            FullRefreshedAt = full ? DateTimeOffset.UtcNow : cache.FullRefreshedAt,
            Tracks = tracks
        };
        JsonStore.SaveAtomic(settings.LikedCachePath, cache);
        log($"Liked Songs index ready: {tracks.Count} tracks using {pages} API call(s)." + (full ? " Full refresh." : " Incremental refresh."));
        return cache;
    }

    public async Task<List<PlaylistInfo>> GetPlaylistsAsync(string userId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var playlists = new List<PlaylistInfo>();
        var next = "/me/playlists?limit=50";
        do
        {
            using var doc = await SendJsonAsync(HttpMethod.Get, next, null, cancellationToken);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var owner = item.TryGetProperty("owner", out var ownerElement) ? String(ownerElement, "id") : "";
                    var collaborative = Bool(item, "collaborative");
                    var owned = owner.Equals(userId, StringComparison.OrdinalIgnoreCase);
                    playlists.Add(new PlaylistInfo
                    {
                        Id = String(item, "id"), Name = String(item, "name"), OwnerId = owner,
                        SnapshotId = String(item, "snapshot_id"), Collaborative = collaborative,
                        IsOwned = owned, CanWrite = owned || collaborative,
                        CanReadItems = owned || collaborative || !settings.OwnedPlaylistsOnly
                    });
                }
            }
            next = String(root, "next");
        } while (next.Length > 0);

        var cache = JsonStore.Load(settings.PlaylistCachePath, () => new PlaylistTrackCache());
        if (!cache.SpotifyUserId.Equals(userId, StringComparison.OrdinalIgnoreCase)) cache = new PlaylistTrackCache { SpotifyUserId = userId };
        foreach (var playlist in playlists)
        {
            if (!playlist.CanReadItems)
            {
                playlist.CanReadItems = false;
                continue;
            }
            if (!forceRefresh && playlist.SnapshotId.Length > 0 && cache.Playlists.TryGetValue(playlist.Id, out var cached) && cached.SnapshotId == playlist.SnapshotId)
            {
                playlist.Tracks = cached.Tracks;
                continue;
            }
            try
            {
                playlist.Tracks = await GetPlaylistItemsAsync(playlist.Id, cancellationToken);
                cache.Playlists[playlist.Id] = new PlaylistTrackCacheEntry { SnapshotId = playlist.SnapshotId, RefreshedAt = DateTimeOffset.UtcNow, Tracks = playlist.Tracks };
                log($"Spotify playlist cache: refreshed {playlist.Name} ({playlist.Tracks.Count} tracks).");
            }
            catch (ApiException ex) when (ex.StatusCode is 403 or 404)
            {
                playlist.CanReadItems = false;
                playlist.CanWrite = false;
                log($"Skipping inaccessible Spotify playlist '{playlist.Name}': {ex.Message}");
            }
        }
        JsonStore.SaveAtomic(settings.PlaylistCachePath, cache);
        return playlists;
    }

    public async Task<List<string>> GetTemporaryDownloadPlaylistIdsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        var next = "/me/playlists?limit=50";
        do
        {
            using var doc = await SendJsonAsync(HttpMethod.Get, next, null, cancellationToken);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var owner = item.TryGetProperty("owner", out var ownerElement) ? String(ownerElement, "id") : "";
                    var name = String(item, "name");
                    var id = String(item, "id");
                    if (id.Length > 0 && owner.Equals(userId, StringComparison.OrdinalIgnoreCase) &&
                        name.StartsWith("__SpotySync Download ", StringComparison.OrdinalIgnoreCase))
                        ids.Add(id);
                }
            }
            next = String(root, "next");
        } while (next.Length > 0);
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<TrackInfo>> GetPlaylistItemsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var tracks = new List<TrackInfo>();
        var next = $"/playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50";
        do
        {
            using var doc = await SendJsonAsync(HttpMethod.Get, next, null, cancellationToken);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var wrapper in items.EnumerateArray())
                {
                    var item = wrapper.TryGetProperty("item", out var modern) ? modern :
                        wrapper.TryGetProperty("track", out var legacy) ? legacy : default;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var type = String(item, "type");
                    if (type.Length > 0 && type != "track") continue;
                    tracks.Add(ParseTrack(item));
                }
            }
            next = String(root, "next");
        } while (next.Length > 0);
        return tracks;
    }

    public async Task<PlaylistInfo> CreatePlaylistAsync(string name, string userId, CancellationToken cancellationToken = default)
    {
        using var doc = await SendJsonAsync(HttpMethod.Post, "/me/playlists", new { name, @public = false, description = "Managed by SpotySync" }, cancellationToken);
        var root = doc.RootElement;
        return new PlaylistInfo { Id = String(root, "id"), Name = String(root, "name"), OwnerId = userId, IsOwned = true, CanReadItems = true, CanWrite = true, SnapshotId = String(root, "snapshot_id") };
    }

    public async Task ApplyPlaylistChangesAsync(PlaylistPlan plan, string userId, CancellationToken cancellationToken = default)
    {
        var playlistId = plan.SpotifyPlaylistId;
        if (playlistId.Length == 0 && plan.SpotifyAddIds.Count > 0)
        {
            var created = await CreatePlaylistAsync(plan.Name, userId, cancellationToken);
            playlistId = created.Id;
            plan.SpotifyPlaylistId = playlistId;
            plan.ExpectedSpotifySnapshot = created.SnapshotId;
            log($"Created private Spotify playlist '{plan.Name}'.");
        }
        if (playlistId.Length == 0 || plan.SpotifyAddIds.Count + plan.SpotifyRemoveIds.Count == 0) return;

        var snapshot = plan.ExpectedSpotifySnapshot;
        foreach (var batch in plan.SpotifyRemoveIds.Chunk(100))
        {
            var payload = new { items = batch.Select(id => new { uri = "spotify:track:" + id }).ToArray(), snapshot_id = snapshot };
            using var doc = await SendJsonAsync(HttpMethod.Delete, $"/playlists/{Uri.EscapeDataString(playlistId)}/items", payload, cancellationToken);
            snapshot = String(doc.RootElement, "snapshot_id");
        }
        foreach (var batch in plan.SpotifyAddIds.Chunk(100))
        {
            using var doc = await SendJsonAsync(HttpMethod.Post, $"/playlists/{Uri.EscapeDataString(playlistId)}/items", new { uris = batch.Select(id => "spotify:track:" + id).ToArray() }, cancellationToken);
            snapshot = String(doc.RootElement, "snapshot_id");
        }
        plan.ExpectedSpotifySnapshot = snapshot;
        var cache = JsonStore.Load(settings.PlaylistCachePath, () => new PlaylistTrackCache());
        cache.Playlists.Remove(playlistId);
        JsonStore.SaveAtomic(settings.PlaylistCachePath, cache);
    }

    public async Task<PlaylistInfo> CreateTemporaryPlaylistAsync(IEnumerable<TrackInfo> tracks, string userId, CancellationToken cancellationToken = default)
    {
        var playlist = await CreatePlaylistAsync("__SpotySync Download " + DateTime.Now.ToString("yyyyMMdd-HHmmss"), userId, cancellationToken);
        try
        {
            var ids = tracks.Select(x => x.SpotifyId).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var batch in ids.Chunk(100))
            {
                using var ignored = await SendJsonAsync(HttpMethod.Post, $"/playlists/{playlist.Id}/items", new { uris = batch.Select(id => "spotify:track:" + id).ToArray() }, cancellationToken);
            }
            playlist.Url = "https://open.spotify.com/playlist/" + playlist.Id;
            return playlist;
        }
        catch
        {
            // Creation succeeded but population did not. Use a fresh token so a
            // cancelled caller cannot strand the half-created playlist.
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await RemovePlaylistFromLibraryAsync(playlist.Id, cleanup.Token);
            }
            catch { }
            throw;
        }
    }

    public async Task RemovePlaylistFromLibraryAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var ignored = await SendJsonAsync(HttpMethod.Delete, "/me/library", new { uris = new[] { "spotify:playlist:" + playlistId } }, cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405)
        {
            try
            {
                using var ignored = await SendJsonAsync(HttpMethod.Delete, $"/playlists/{playlistId}/followers", null, cancellationToken);
            }
            catch (ApiException fallback) when (fallback.StatusCode == 404)
            {
                // It was already removed; cleanup is complete.
            }
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string endpoint, object? body, CancellationToken cancellationToken)
    {
        var absolute = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : ApiBase + endpoint;
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, absolute);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(refreshed, cancellationToken));
            request.Headers.UserAgent.ParseAdd("SpotySync/1.0");
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonStore.Options), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                continue;
            }
            if ((int)response.StatusCode == 429 && !responseBody.Contains("QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase) && attempt < settings.ApiRetryCount)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)));
                log($"Spotify rate limit reached; waiting {Math.Ceiling(wait.TotalSeconds)} seconds as requested…");
                await Task.Delay(wait, cancellationToken);
                continue;
            }
            var quota = responseBody.Contains("QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase);
            if (quota)
            {
                var retry = response.Headers.RetryAfter;
                var wait = retry?.Delta ?? (retry?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null);
                var resetNote = wait is { } w && w > TimeSpan.Zero
                    ? $"Spotify says it resets in {FormatWait(w)} (about {DateTime.Now + w:g})."
                    : "Spotify sent no Retry-After; Development Mode quotas use a rolling ~24 hour window, so it should clear within 24 hours of the heavy use.";
                log($"Quota hit on {method} {new Uri(absolute).AbsolutePath}. {resetNote}");
                var trimmed = responseBody.Length > 400 ? responseBody[..400] + "…" : responseBody;
                log($"Spotify response: {trimmed.Trim()}");
                throw new ApiException((int)response.StatusCode,
                    $"Spotify Development Mode quota is exhausted. {resetNote} Cached data was preserved.", responseBody);
            }
            var explanation = $"Spotify API returned {(int)response.StatusCode} {response.ReasonPhrase} for {method} {new Uri(absolute).AbsolutePath}.";
            throw new ApiException((int)response.StatusCode, explanation, responseBody);
        }
    }

    private static string FormatWait(TimeSpan w) =>
        w.TotalHours >= 1 ? $"{(int)w.TotalHours}h {w.Minutes}m" : w.TotalMinutes >= 1 ? $"{(int)w.TotalMinutes}m {w.Seconds}s" : $"{(int)Math.Ceiling(w.TotalSeconds)}s";

    private static TrackInfo ParseTrack(JsonElement item)
    {
        var id = String(item, "id");
        var artists = new List<string>();
        if (item.TryGetProperty("artists", out var artistItems))
            artists.AddRange(artistItems.EnumerateArray().Select(x => String(x, "name")).Where(x => x.Length > 0));
        var hasAlbum = item.TryGetProperty("album", out var albumElement) && albumElement.ValueKind == JsonValueKind.Object;
        var album = hasAlbum ? String(albumElement, "name") : "";
        var cover = "";
        var albumArtist = "";
        if (hasAlbum)
        {
            // the largest picture (Spotify lists them biggest first)
            if (albumElement.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                cover = images.EnumerateArray().OrderByDescending(image => Int(image, "width")).Select(image => String(image, "url")).FirstOrDefault(url => url.Length > 0) ?? "";
            if (albumElement.TryGetProperty("artists", out var albumArtists) && albumArtists.ValueKind == JsonValueKind.Array)
                albumArtist = albumArtists.EnumerateArray().Select(x => String(x, "name")).FirstOrDefault(x => x.Length > 0) ?? "";
        }
        return new TrackInfo
        {
            SpotifyId = id, Uri = String(item, "uri") is { Length: > 0 } uri ? uri : (id.Length > 0 ? "spotify:track:" + id : ""),
            Url = item.TryGetProperty("external_urls", out var urls) ? String(urls, "spotify") : "",
            Title = String(item, "name"), Artists = artists, Album = album,
            DurationMs = Int(item, "duration_ms"), IsLocal = Bool(item, "is_local"),
            CoverUrl = cover, AlbumArtist = albumArtist,
            ReleaseDate = hasAlbum ? String(albumElement, "release_date") : "",
            AlbumTrackCount = hasAlbum ? Int(albumElement, "total_tracks") : 0,
            TrackNumber = Int(item, "track_number"), DiscNumber = Int(item, "disc_number"),
            Isrc = item.TryGetProperty("external_ids", out var externalIds) ? String(externalIds, "isrc") : "",
            Explicit = Bool(item, "explicit")
        };
    }

    internal static string String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static int Int(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : 0;
    internal static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True;

    public void Dispose() { if (ownsClient) http.Dispose(); }
}
