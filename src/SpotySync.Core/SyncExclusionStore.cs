using System.Text;

namespace SpotySync;

public sealed class SyncExclusion
{
    public string Playlist { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";

    public bool Matches(string playlist, string? artist, string? title)
    {
        if (!TextNormalizer.PlaylistKey(Playlist).Equals(TextNormalizer.PlaylistKey(playlist), StringComparison.Ordinal)) return false;
        var expectedTitle = TextNormalizer.Normalize(Title, true);
        var actualTitle = TextNormalizer.Normalize(title, true);
        if (expectedTitle.Length == 0 || !expectedTitle.Equals(actualTitle, StringComparison.Ordinal)) return false;
        var expectedArtist = TextNormalizer.Normalize(Artist);
        var actualArtist = TextNormalizer.Normalize(artist);
        return expectedArtist.Length > 0 && actualArtist.Length > 0 &&
               (expectedArtist.Equals(actualArtist, StringComparison.Ordinal) ||
                expectedArtist.Contains(actualArtist, StringComparison.Ordinal) ||
                actualArtist.Contains(expectedArtist, StringComparison.Ordinal));
    }

    public bool Matches(string playlist, TrackInfo track) =>
        track.Artists.Any(artist => Matches(playlist, artist, track.Title));

    public bool Matches(string playlist, LocalTrack track) => Matches(playlist, track.Artist, track.Title);
}

public sealed class SyncExclusionStore
{
    private const string Header = "# SpotySync permanent per-playlist track exclusions\r\n" +
                                  "# Delete a line to undo it, then rebuild the preview. Fields are TAB-separated:\r\n" +
                                  "# Playlist\tArtist\tTitle\r\n";
    private readonly string path;

    public SyncExclusionStore(string path) => this.path = path;

    public IReadOnlyList<SyncExclusion> Load()
    {
        EnsureExists();
        var result = new List<SyncExclusion>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = raw.Split('\t');
            if (fields.Length < 3) continue;
            var item = new SyncExclusion
            {
                Playlist = fields[0].Trim(), Artist = fields[1].Trim(),
                Title = string.Join("\t", fields.Skip(2)).Trim()
            };
            if (item.Playlist.Length > 0 && item.Artist.Length > 0 && item.Title.Length > 0) result.Add(item);
        }
        return result;
    }

    public int Add(IEnumerable<SyncExclusion> additions)
    {
        var existing = Load().ToList();
        var added = 0;
        foreach (var item in additions)
        {
            var clean = new SyncExclusion
            {
                Playlist = Clean(item.Playlist), Artist = Clean(item.Artist), Title = Clean(item.Title)
            };
            if (clean.Playlist.Length == 0 || clean.Artist.Length == 0 || clean.Title.Length == 0) continue;
            if (existing.Any(saved => saved.Matches(clean.Playlist, clean.Artist, clean.Title))) continue;
            existing.Add(clean);
            added++;
        }
        if (added > 0) Save(existing);
        return added;
    }

    /// <summary>Removes rules (exact playlist/artist/title match, case-insensitive). Returns how many were removed.</summary>
    public int Remove(IEnumerable<SyncExclusion> removals)
    {
        static string Key(SyncExclusion x) => (Clean(x.Playlist) + "\t" + Clean(x.Artist) + "\t" + Clean(x.Title)).ToLowerInvariant();
        var drop = removals.Select(Key).ToHashSet();
        if (drop.Count == 0) return 0;
        var existing = Load().ToList();
        var kept = existing.Where(x => !drop.Contains(Key(x))).ToList();
        var removed = existing.Count - kept.Count;
        if (removed > 0) Save(kept);
        return removed;
    }

    public void EnsureExists()
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Header, new UTF8Encoding(false));
    }

    private void Save(IEnumerable<SyncExclusion> items)
    {
        var lines = items
            .OrderBy(x => x.Playlist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{Clean(x.Playlist)}\t{Clean(x.Artist)}\t{Clean(x.Title)}");
        File.WriteAllText(path, Header + string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
    }

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
