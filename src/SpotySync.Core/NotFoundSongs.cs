namespace SpotySync;

/// <summary>A Spotify song spotDL couldn't find on YouTube Music.</summary>
public sealed class NotFoundSong
{
    public string SpotifyId { get; set; } = "";
    public string Artists { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public int DurationMs { get; set; }
    public DateTimeOffset FirstMissed { get; set; }
    public DateTimeOffset LastTried { get; set; }
    public int Tries { get; set; }
    /// <summary>A YouTube link the user found for this song. spotDL downloads from it and still tags the file from Spotify.</summary>
    public string ManualUrl { get; set; } = "";

    public string DisplayName => Artists.Length > 0 ? Artists + " - " + Title : Title;
    public string SpotifyUrl => "https://open.spotify.com/track/" + SpotifyId;
}

public sealed class NotFoundSongList
{
    public int Version { get; set; } = 1;
    public Dictionary<string, NotFoundSong> Songs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Remembers songs YouTube Music didn't have (Data\not-found-songs.json), so later downloads don't search for them again
/// every batch. They are tried again by themselves after <see cref="RetryAfter"/>, or right away from the list, and a
/// song with a YouTube link from the user is always downloaded.
/// </summary>
public static class NotFoundSongs
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(30);
    private static readonly object Gate = new();

    public static event Action? Changed;

    public static string PathFor(PortableSettings settings) => Path.Combine(settings.DataDirectory, "not-found-songs.json");

    public static NotFoundSongList Load(PortableSettings settings)
    {
        lock (Gate)
        {
            var list = JsonStore.Load(PathFor(settings), () => new NotFoundSongList());
            list.Songs = new Dictionary<string, NotFoundSong>(list.Songs ?? new(), StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }

    /// <summary>Loads, changes and saves the list in one step.</summary>
    public static void Update(PortableSettings settings, Action<NotFoundSongList> change)
    {
        lock (Gate)
        {
            var list = Load(settings);
            change(list);
            Directory.CreateDirectory(settings.DataDirectory);
            JsonStore.SaveAtomic(PathFor(settings), list);
        }
        Changed?.Invoke();
    }

    /// <summary>True when a download should leave this song out: not found recently and no link from the user.</summary>
    public static bool ShouldSkip(NotFoundSong song, DateTimeOffset now) =>
        song.ManualUrl.Length == 0 && now - song.LastTried < RetryAfter;

    public static void MarkMissing(PortableSettings settings, IEnumerable<TrackInfo> tracks)
    {
        var missing = tracks.Where(t => t.SpotifyId.Length > 0).ToList();
        if (missing.Count == 0) return;
        var now = DateTimeOffset.Now;
        Update(settings, list =>
        {
            foreach (var track in missing)
            {
                if (!list.Songs.TryGetValue(track.SpotifyId, out var song))
                    list.Songs[track.SpotifyId] = song = new NotFoundSong { SpotifyId = track.SpotifyId, FirstMissed = now };
                song.Artists = string.Join(", ", track.Artists);
                song.Title = track.Title;
                song.Album = track.Album;
                song.DurationMs = track.DurationMs;
                song.LastTried = now;
                song.Tries++;
                // a link that didn't work either is dropped, so the song is left out again instead of failing each time
                song.ManualUrl = "";
            }
        });
    }

    /// <summary>Removes songs that have now been downloaded.</summary>
    public static void MarkFound(PortableSettings settings, IEnumerable<string> spotifyIds)
    {
        var ids = spotifyIds.Where(id => id.Length > 0).ToList();
        if (ids.Count == 0) return;
        var list = Load(settings);
        if (!ids.Any(list.Songs.ContainsKey)) return;
        Update(settings, l => { foreach (var id in ids) l.Songs.Remove(id); });
    }

    public static void SetManualUrl(PortableSettings settings, string spotifyId, string url) =>
        Update(settings, list => { if (list.Songs.TryGetValue(spotifyId, out var song)) song.ManualUrl = url.Trim(); });

    public static void Forget(PortableSettings settings, IEnumerable<string> spotifyIds) =>
        Update(settings, list => { foreach (var id in spotifyIds) list.Songs.Remove(id); });

    /// <summary>
    /// Song names from spotDL's "not found" lines, e.g. "LookupError: No results found for song: Artist - Title" or
    /// "YouTube Music returned no usable results for artist, artist - title after 3 attempts".
    /// </summary>
    public static string? NameFromDownloaderLine(string line)
    {
        var lookup = System.Text.RegularExpressions.Regex.Match(line, @"No results found for song: (.+?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (lookup.Success) return lookup.Groups[1].Value.Trim();
        var empty = System.Text.RegularExpressions.Regex.Match(line, @"YouTube Music returned no usable results for (.+?) after \d+ attempts", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return empty.Success ? empty.Groups[1].Value.Trim() : null;
    }

    /// <summary>The expected songs that a list of "not found" names refers to.</summary>
    public static List<TrackInfo> Resolve(IEnumerable<string> names, IEnumerable<TrackInfo> candidates)
    {
        var pool = candidates.ToList();
        var result = new List<TrackInfo>();
        foreach (var name in names)
        {
            var value = TextNormalizer.Normalize(name);
            var match = pool.FirstOrDefault(track =>
            {
                var title = TextNormalizer.Normalize(track.Title);
                if (title.Length == 0 || !value.EndsWith(title, StringComparison.Ordinal)) return false;
                return track.Artists.Count == 0 || track.Artists.Select(a => TextNormalizer.Normalize(a)).Any(a => a.Length > 0 && value.Contains(a, StringComparison.Ordinal));
            });
            if (match is not null && !result.Contains(match)) result.Add(match);
        }
        return result;
    }
}
