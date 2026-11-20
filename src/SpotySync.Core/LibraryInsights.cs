namespace SpotySync;

public enum InsightKind { Unplaylisted, NoLocalPlaylist, LikedMissingLocally, LikedNotInPlaylist, Duplicate }

public sealed class InsightRow
{
    public InsightKind Kind { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public string Path { get; set; } = "";
    public string SpotifyId { get; set; } = "";
    public string Detail { get; set; } = "";
    public int DurationMs { get; set; }
    public long Size { get; set; }
    public string SpotifyUrl => SpotifyId.Length > 0 ? "https://open.spotify.com/track/" + SpotifyId : "";
    public string DisplayName => $"{Artist} - {Title}".Trim(' ', '-');
}

public sealed class LibraryReport
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int LibraryCount { get; set; }
    public int LocalPlaylistCount { get; set; }
    public int SpotifyPlaylistCount { get; set; }
    /// <summary>Local songs that are in no local playlist AND not (by match) in any Spotify playlist.</summary>
    public List<InsightRow> Unplaylisted { get; set; } = new();
    /// <summary>Local songs in no local playlist (they may still be in a Spotify playlist).</summary>
    public List<InsightRow> NoLocalPlaylist { get; set; } = new();
    /// <summary>Spotify Liked Songs with no matching local file.</summary>
    public List<InsightRow> LikedMissingLocally { get; set; } = new();
    /// <summary>Spotify Liked Songs that are in none of your Spotify playlists.</summary>
    public List<InsightRow> LikedNotInPlaylist { get; set; } = new();
    /// <summary>Local files that look like the same recording (same title, artist and length).</summary>
    public List<InsightRow> Duplicates { get; set; } = new();

    public List<InsightRow> Get(InsightKind kind) => kind switch
    {
        InsightKind.Unplaylisted => Unplaylisted,
        InsightKind.NoLocalPlaylist => NoLocalPlaylist,
        InsightKind.LikedMissingLocally => LikedMissingLocally,
        InsightKind.LikedNotInPlaylist => LikedNotInPlaylist,
        _ => Duplicates
    };
}

/// <summary>Read-only analysis of a prepared library/Spotify snapshot. Never changes anything.</summary>
public static class LibraryInsights
{
    public static LibraryReport Build(PreparedSync prepared, IEnumerable<string>? ignoredFolders = null)
    {
        var ignored = (ignoredFolders ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => SyncPlanner.PathKey(x).TrimEnd('\\', '/') + "\\")
            .ToList();
        bool IsIgnored(string path)
        {
            var key = SyncPlanner.PathKey(path);
            return ignored.Any(folder => key.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        }

        var library = prepared.Library.Where(t => t.Path.Length > 0)
            .GroupBy(t => SyncPlanner.PathKey(t.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var report = new LibraryReport
        {
            LibraryCount = library.Count,
            LocalPlaylistCount = prepared.LocalPlaylists.Count,
            SpotifyPlaylistCount = prepared.SpotifyPlaylists.Count
        };

        // Local playlist membership (by full path)
        var inLocalPlaylist = prepared.LocalPlaylists.SelectMany(p => p.Files)
            .Where(f => !string.IsNullOrWhiteSpace(f)).Select(SyncPlanner.PathKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Spotify playlist membership (by ID and by recording, so relinked IDs still count)
        var spotifyTracks = prepared.SpotifyPlaylists.SelectMany(p => p.Tracks).ToList();
        var inSpotifyIds = spotifyTracks.Select(t => t.SpotifyId).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inSpotifyKeys = spotifyTracks.Select(TrackMatcher.RecordingGroupKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

        // Local → Spotify identity from the planner's confident matches
        var matchByPath = prepared.Plan.Matches.Where(m => m.IsConfident && m.Spotify is not null)
            .GroupBy(m => SyncPlanner.PathKey(m.Local.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Spotify!, StringComparer.OrdinalIgnoreCase);

        foreach (var track in library)
        {
            if (IsIgnored(track.Path)) continue;
            var key = SyncPlanner.PathKey(track.Path);
            if (inLocalPlaylist.Contains(key)) continue;
            var row = Row(track, InsightKind.NoLocalPlaylist);
            matchByPath.TryGetValue(key, out var spotify);
            var spotifyId = spotify?.SpotifyId ?? track.SpotifyId;
            row.SpotifyId = spotifyId;
            var inSpotify = (spotifyId.Length > 0 && inSpotifyIds.Contains(spotifyId)) ||
                            (spotify is not null && inSpotifyKeys.Contains(TrackMatcher.RecordingGroupKey(spotify))) ||
                            inSpotifyKeys.Contains(LocalKey(track));
            row.Detail = inSpotify ? "In a Spotify playlist only" : spotifyId.Length > 0 ? "Matched on Spotify, but in no playlist" : "Not matched on Spotify";
            report.NoLocalPlaylist.Add(row);
            if (!inSpotify)
            {
                var copy = Row(track, InsightKind.Unplaylisted);
                copy.SpotifyId = spotifyId;
                copy.Detail = spotifyId.Length > 0 ? "Matched on Spotify, but in no playlist anywhere" : "In no playlist (local or Spotify)";
                report.Unplaylisted.Add(copy);
            }
        }

        // Liked Songs vs local library
        var localIds = library.Select(t => matchByPath.TryGetValue(SyncPlanner.PathKey(t.Path), out var s) ? s.SpotifyId : t.SpotifyId)
            .Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localKeys = library.Select(LocalKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var liked in prepared.Liked.Tracks.Where(t => t.SpotifyId.Length > 0 && !t.IsLocal))
        {
            var key = TrackMatcher.RecordingGroupKey(liked);
            if (!localIds.Contains(liked.SpotifyId) && (key.Length == 0 || !localKeys.Contains(key)))
                report.LikedMissingLocally.Add(Row(liked, InsightKind.LikedMissingLocally,
                    liked.AddedAt is { } added ? "Liked " + added.ToLocalTime().ToString("d MMM yyyy") : "Liked on Spotify"));
            if (!inSpotifyIds.Contains(liked.SpotifyId) && (key.Length == 0 || !inSpotifyKeys.Contains(key)))
                report.LikedNotInPlaylist.Add(Row(liked, InsightKind.LikedNotInPlaylist,
                    liked.AddedAt is { } added2 ? "Liked " + added2.ToLocalTime().ToString("d MMM yyyy") : "Liked on Spotify"));
        }

        // Duplicates: same recording key and durations within 3 s
        foreach (var group in library.Where(t => !IsIgnored(t.Path)).GroupBy(LocalKey).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            var clusters = new List<List<LocalTrack>>();
            foreach (var track in group.OrderBy(t => t.DurationMs))
            {
                var cluster = clusters.FirstOrDefault(c => c[0].DurationMs <= 0 || track.DurationMs <= 0 || Math.Abs(c[0].DurationMs - track.DurationMs) <= 3000);
                if (cluster is null) clusters.Add(new List<LocalTrack> { track }); else cluster.Add(track);
            }
            var groupNumber = 0;
            foreach (var cluster in clusters.Where(c => c.Count > 1))
            {
                groupNumber++;
                foreach (var track in cluster.OrderByDescending(t => t.Size))
                    report.Duplicates.Add(Row(track, InsightKind.Duplicate, $"{cluster.Count} copies" + (track.Size > 0 ? $" · {track.Size / 1048576.0:0.0} MB" : "")));
            }
        }

        report.Unplaylisted = Sort(report.Unplaylisted);
        report.NoLocalPlaylist = Sort(report.NoLocalPlaylist);
        report.Duplicates = report.Duplicates.OrderBy(r => r.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return report;
    }

    private static List<InsightRow> Sort(List<InsightRow> rows) =>
        rows.OrderBy(r => r.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Recording key for a local file (title + primary artist), compatible with Spotify keys.</summary>
    public static string LocalKey(LocalTrack track)
    {
        var artists = track.Artist.Split([";", ",", " & ", " feat. ", " ft. ", " x "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return TrackMatcher.RecordingGroupKey(new TrackInfo { Title = track.Title, Artists = artists.Count > 0 ? artists : [track.Artist] });
    }

    private static InsightRow Row(LocalTrack t, InsightKind kind, string detail = "") => new()
    {
        Kind = kind, Artist = t.Artist, Title = t.Title.Length > 0 ? t.Title : System.IO.Path.GetFileNameWithoutExtension(t.Path),
        Album = t.Album, Path = t.Path, SpotifyId = t.SpotifyId, DurationMs = t.DurationMs, Size = t.Size, Detail = detail
    };

    private static InsightRow Row(TrackInfo t, InsightKind kind, string detail) => new()
    {
        Kind = kind, Artist = string.Join("; ", t.Artists), Title = t.Title, Album = t.Album,
        SpotifyId = t.SpotifyId, DurationMs = t.DurationMs, Detail = detail
    };

    /// <summary>Writes rows as an extended M3U8 playlist (local files only).</summary>
    public static string WriteM3u(string path, IEnumerable<InsightRow> rows)
    {
        var lines = new List<string> { "#EXTM3U" };
        foreach (var row in rows.Where(r => r.Path.Length > 0))
        {
            lines.Add($"#EXTINF:{Math.Max(-1, row.DurationMs / 1000)},{row.DisplayName}");
            lines.Add(row.Path);
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
        return path;
    }

    public static string WriteCsv(string path, IEnumerable<InsightRow> rows)
    {
        static string Q(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "Artist,Title,Album,Detail,Path,Spotify URL" };
        lines.AddRange(rows.Select(r => string.Join(",", Q(r.Artist), Q(r.Title), Q(r.Album), Q(r.Detail), Q(r.Path), Q(r.SpotifyUrl))));
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(true));
        return path;
    }
}
