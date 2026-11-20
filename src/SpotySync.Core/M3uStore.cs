using System.Text;

namespace SpotySync;

public sealed class M3uStore
{
    private readonly PortableSettings settings;
    private readonly Action<string> log;

    public M3uStore(PortableSettings settings, Action<string>? log = null)
    {
        this.settings = settings;
        this.log = log ?? (_ => { });
    }

    public List<LocalPlaylist> ReadPlaylists()
    {
        var folder = settings.ResolvePath(settings.M3uFolder);
        if (!Directory.Exists(folder)) return new();
        return Directory.EnumerateFiles(folder, "*.m3u*", SearchOption.AllDirectories)
            .Select(ReadPlaylist).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static LocalPlaylist ReadPlaylist(string path)
    {
        var baseDirectory = Path.GetDirectoryName(path) ?? "";
        var files = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            try { files.Add(Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(baseDirectory, line))); }
            catch { files.Add(line); }
        }
        return new LocalPlaylist { Id = path, Name = Path.GetFileNameWithoutExtension(path), Files = files };
    }

    public (int Added, int Removed) Apply(PlaylistPlan plan, IReadOnlyDictionary<string, LocalTrack> metadata, bool allowRemovals, int maxRemovals, bool dryRun)
    {
        var folder = settings.ResolvePath(settings.M3uFolder);
        Directory.CreateDirectory(folder);
        var target = plan.LocalPlaylistId;
        var targetWasM3u = !string.IsNullOrWhiteSpace(target) && (target.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
        if (!targetWasM3u)
            target = Path.Combine(folder, SanitizeName(plan.Name) + ".m3u8");
        var current = File.Exists(target) ? ReadPlaylist(target).Files : new List<string>();
        var digest = PlaylistDigests.ForFiles(current);
        if (targetWasM3u && File.Exists(target) && plan.ExpectedLocalDigest.Length > 0 && !digest.Equals(plan.ExpectedLocalDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"M3U playlist '{plan.Name}' changed after preview; refresh the preview before applying.");
        var desired = allowRemovals ? plan.DesiredLocalFiles : current.Concat(plan.DesiredLocalFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var added = desired.Except(current, StringComparer.OrdinalIgnoreCase).Count();
        var removed = current.Except(desired, StringComparer.OrdinalIgnoreCase).Count();
        if (removed > maxRemovals) throw new InvalidOperationException($"M3U removal safety cap exceeded for '{plan.Name}' ({removed} > {maxRemovals}).");
        if (dryRun || added + removed == 0) return (added, removed);

        Backup(target);
        var builder = new StringBuilder("#EXTM3U\r\n");
        foreach (var file in desired)
        {
            if (metadata.TryGetValue(SyncPlanner.PathKey(file), out var track))
            {
                var seconds = track.DurationMs > 0 ? track.DurationMs / 1000 : -1;
                builder.Append("#EXTINF:").Append(seconds).Append(',').Append(track.DisplayName.Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
            }
            var output = file;
            if (settings.RelativeM3uPaths)
            {
                try { output = Path.GetRelativePath(Path.GetDirectoryName(target) ?? folder, file); } catch { }
            }
            builder.Append(output).Append("\r\n");
        }
        var temp = target + ".tmp";
        File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
        File.Move(temp, target, true);
        log($"Wrote M3U playlist '{plan.Name}' ({desired.Count} files).");
        return (added, removed);
    }

    private void Backup(string target)
    {
        if (!File.Exists(target)) return;
        var backupDirectory = Path.Combine(settings.DataDirectory, "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDirectory);
        File.Copy(target, Path.Combine(backupDirectory, Path.GetFileName(target)), true);
    }

    private static string SanitizeName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "Playlist" : name.Trim();
    }
}
