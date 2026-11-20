using System.Text.Json.Serialization;

namespace SpotySync;

public enum LocalBackendKind
{
    MusicBee,
    M3U,
    Both
}

public enum SyncDirection
{
    TwoWay,
    SpotifyToLocal,
    LocalToSpotify
}

public sealed class TrackInfo
{
    public string SpotifyId { get; set; } = "";
    public string Uri { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public string Album { get; set; } = "";
    public int DurationMs { get; set; }
    public DateTimeOffset? AddedAt { get; set; }
    public bool IsLocal { get; set; }
    public string DisplayName => $"{string.Join("; ", Artists)} - {Title}".Trim(' ', '-');
}

public sealed class LocalTrack
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Comment { get; set; } = "";
    public int DurationMs { get; set; }
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string SpotifyId { get; set; } = "";
    public string DisplayName => $"{Artist} - {Title}".Trim(' ', '-');
}

public sealed class PlaylistInfo
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public bool Collaborative { get; set; }
    public bool IsOwned { get; set; }
    public bool CanReadItems { get; set; } = true;
    public bool CanWrite { get; set; }
    public string SnapshotId { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = new();
}

public sealed class LocalPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

public sealed class MusicBeeSnapshot
{
    public string LibraryName { get; set; } = "MusicBee";
    public List<LocalTrack> Tracks { get; set; } = new();
    public List<LocalPlaylist> Playlists { get; set; } = new();
}

public sealed class TrackMatch
{
    public LocalTrack Local { get; set; } = new();
    public TrackInfo? Spotify { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = "";
    public bool IsConfident { get; set; }
}

public sealed class PlaylistPlan
{
    public string Name { get; set; } = "";
    public string SpotifySourceName { get; set; } = "";
    public string LocalSourceName { get; set; } = "";
    public string SpotifyPlaylistId { get; set; } = "";
    public string LocalPlaylistId { get; set; } = "";
    public string ExpectedSpotifySnapshot { get; set; } = "";
    public string ExpectedLocalDigest { get; set; } = "";
    public bool HasBaseline { get; set; }
    public bool IsBlocked { get; set; }
    public string BlockReason { get; set; } = "";
    public List<string> SpotifyAddIds { get; set; } = new();
    public List<string> SpotifyRemoveIds { get; set; } = new();
    public List<string> LocalAddFiles { get; set; } = new();
    public List<string> LocalRemoveFiles { get; set; } = new();
    public List<string> DesiredSpotifyIds { get; set; } = new();
    public List<string> DesiredLocalFiles { get; set; } = new();
    public List<string> PlatformSpecific { get; set; } = new();
    public List<string> IgnoredByRule { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<TrackReviewItem> ReviewItems { get; set; } = new();
    public int ChangeCount => SpotifyAddIds.Count + SpotifyRemoveIds.Count + LocalAddFiles.Count + LocalRemoveFiles.Count;
}

public sealed class TrackReviewItem
{
    public string Kind { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string DisplayName => $"{Artist} - {Title}".Trim(' ', '-');
}

public sealed class SyncPlan
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string SpotifyUserId { get; set; } = "";
    public List<PlaylistPlan> Playlists { get; set; } = new();
    public List<TrackMatch> Matches { get; set; } = new();
    public int ConfidentMatches => Matches.Count(x => x.IsConfident);
    public int UnmatchedLocal => Matches.Count(x => !x.IsConfident);
    public int TotalChanges => Playlists.Sum(x => x.ChangeCount);
}

public sealed class PlaylistBaseline
{
    public string Name { get; set; } = "";
    // Older releases only supported the default MusicBee/TwoWay baseline.
    // Keeping that as the deserialization default preserves existing users while
    // preventing the baseline from crossing into a different backend/direction.
    public string SyncMode { get; set; } = "MusicBee:TwoWay";
    public List<string> SpotifyIds { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SyncState
{
    public int Version { get; set; } = 1;
    public string SpotifyUserId { get; set; } = "";
    public Dictionary<string, PlaylistBaseline> Playlists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastSuccessfulBackupMarker { get; set; }
    public string LastSuccessfulBackupTrackId { get; set; } = "";
    public List<string> PendingTemporaryDownloadPlaylistIds { get; set; } = new();
}

public sealed class LikedTrackCache
{
    public int Version { get; set; } = 1;
    public string SpotifyUserId { get; set; } = "";
    public DateTimeOffset RefreshedAt { get; set; }
    public DateTimeOffset FullRefreshedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = new();
}

public sealed class PlaylistTrackCache
{
    public int Version { get; set; } = 1;
    public string SpotifyUserId { get; set; } = "";
    public Dictionary<string, PlaylistTrackCacheEntry> Playlists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlaylistTrackCacheEntry
{
    public string SnapshotId { get; set; } = "";
    public DateTimeOffset RefreshedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = new();
}

public sealed class SpotifyToken
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string Scope { get; set; } = "";
}

public sealed class SyncExecutionResult
{
    public bool Success { get; set; }
    public int SpotifyAdded { get; set; }
    public int SpotifyRemoved { get; set; }
    public int LocalAdded { get; set; }
    public int LocalRemoved { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class BackupResult
{
    public bool Success { get; set; }
    public int Candidates { get; set; }
    public int AlreadyLocal { get; set; }
    public int NewAudioFiles { get; set; }
    public int ExitCode { get; set; }
    public List<string> Files { get; set; } = new();
    public string ResolvedMusicFolder { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class ManualDownloadResult
{
    public bool Success { get; set; }
    public int NewAudioFiles { get; set; }
    public int ImportedFiles { get; set; }
    public int PrimedFiles { get; set; }
    public int ExitCode { get; set; }
    public List<string> Files { get; set; } = new();
    public string ResolvedMusicFolder { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class SoundtrackResult
{
    public bool Success { get; set; }
    public string Folder { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public int ImportedFiles { get; set; }
    public int PrimedFiles { get; set; }
    /// <summary>Files MusicBee moved away after import that SpotySync moved back.</summary>
    public int Relocated { get; set; }
    /// <summary>Files that still ended up outside the soundtrack folder.</summary>
    public List<string> Escaped { get; set; } = new();
    public string Error { get; set; } = "";
}
public sealed class QualityUpgradeResult
{
    public bool Success { get; set; }
    public int Candidates { get; set; }
    public int Upgraded { get; set; }
    public int StillLow { get; set; }
    public int Failed { get; set; }
    public string Error { get; set; } = "";
    public string Summary => Candidates == 0 && Error.Length == 0
        ? "Quality upgrade: every downloaded song is already Premium quality."
        : $"Quality upgrade: {Upgraded} of {Candidates} song(s) upgraded to Premium quality.";
}

public sealed class MusicBeeImportResult
{
    public int Requested { get; set; }
    public int Imported { get; set; }
    public int Primed { get; set; }
    public string PrimeWarning { get; set; } = "";
}

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }
    public bool IsQuotaExceeded => StatusCode == 429 && ResponseBody.Contains("QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase);

    public ApiException(int statusCode, string message, string responseBody = "") : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
