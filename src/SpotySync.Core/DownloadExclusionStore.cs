namespace SpotySync;

/// <summary>
/// Liked songs the user never wants downloaded ("Liked, not downloaded" → Don't download). Stored portably in
/// Data\download-exclusions.txt as "spotifyId&lt;TAB&gt;Artist - Title" lines (the name is only for people reading it).
/// </summary>
public sealed class DownloadExclusionStore
{
    private readonly string path;
    private readonly Dictionary<string, string> entries = new(StringComparer.Ordinal);

    public DownloadExclusionStore(string dataDirectory)
    {
        path = Path.Combine(dataDirectory, "download-exclusions.txt");
        try
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var tab = trimmed.IndexOf('\t');
                var id = tab >= 0 ? trimmed[..tab].Trim() : trimmed;
                if (id.Length > 0) entries[id] = tab >= 0 ? trimmed[(tab + 1)..].Trim() : "";
            }
        }
        catch { }
    }

    public int Count => entries.Count;
    public bool Contains(string spotifyId) => spotifyId.Length > 0 && entries.ContainsKey(spotifyId);

    public void Add(IEnumerable<(string Id, string Name)> songs)
    {
        foreach (var (id, name) in songs) if (id.Length > 0) entries[id] = name;
        Save();
    }

    public void Remove(IEnumerable<string> ids)
    {
        foreach (var id in ids) entries.Remove(id);
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string> { "# Liked songs SpotySync will not download (Library › Liked, not downloaded). One per line: Spotify id<TAB>name" };
        lines.AddRange(entries.OrderBy(e => e.Value, StringComparer.OrdinalIgnoreCase).Select(e => e.Key + "\t" + e.Value));
        File.WriteAllLines(path, lines);
    }
}
