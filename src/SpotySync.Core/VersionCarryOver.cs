namespace SpotySync;

/// <summary>
/// A new SpotySync version usually lives in its own folder (SpotySync-1.5.6-win-x64 next to SpotySync-1.5.5-win-x64).
/// On its very first start it takes over everything from the copy that was used last: settings, Spotify login, the
/// Spotify and library caches and the sync history. Without them the new copy re-reads every Liked Song and playlist
/// from Spotify, which is what used up the Spotify request allowance. The old folder is only read, never changed.
/// </summary>
public static class VersionCarryOver
{
    /// <summary>The folder copied from on this start, or null when nothing was carried over.</summary>
    public static string? CarriedFrom { get; private set; }
    public static int FilesCopied { get; private set; }

    private static readonly string[] SkippedDataFiles = ["existing-setup-declined.txt"];
    private static readonly string[] ToolFiles = ["spotify_credentials.txt", "browser_options.txt"];

    /// <summary>Runs before the settings are loaded. Does nothing unless this folder has never been started.</summary>
    public static void RunIfFresh(string baseDirectory, IEnumerable<string>? extraCandidates = null)
    {
        try
        {
            baseDirectory = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (File.Exists(Path.Combine(baseDirectory, "SpotySync.ini"))) return;
            var data = Path.Combine(baseDirectory, "Data");
            if (Directory.Exists(data) && Directory.EnumerateFiles(data).Any()) return;
            var source = FindLastUsed(baseDirectory, extraCandidates ?? []);
            if (source is null) return;
            FilesCopied = Copy(source, baseDirectory);
            CarriedFrom = source;
        }
        catch
        {
            // a failed carry-over must never stop the app from starting; it then starts like a fresh copy
        }
    }

    /// <summary>Other SpotySync folders next to this one (plus any extra candidates), newest activity first.</summary>
    internal static string? FindLastUsed(string baseDirectory, IEnumerable<string> extraCandidates)
    {
        var parent = Path.GetDirectoryName(baseDirectory);
        var candidates = new List<string>(extraCandidates.Where(c => !string.IsNullOrWhiteSpace(c)));
        if (parent is not null) candidates.AddRange(Directory.GetDirectories(parent, "SpotySync*"));
        return candidates
            .Select(c => { try { return Path.GetFullPath(c).TrimEnd(Path.DirectorySeparatorChar); } catch { return ""; } })
            .Where(c => c.Length > 0 && !c.Equals(baseDirectory, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => File.Exists(Path.Combine(c, "SpotySync.ini")))
            .Select(c => (Folder: c, Used: LastActivity(c)))
            .OrderByDescending(c => File.Exists(Path.Combine(c.Folder, "Data", "spotify-token.json")))
            .ThenByDescending(c => c.Used)
            .Select(c => c.Folder)
            .FirstOrDefault();
    }

    /// <summary>When the copy was last really used: the newest file in its Data folder (or its settings file).</summary>
    private static DateTime LastActivity(string folder)
    {
        var newest = File.GetLastWriteTimeUtc(Path.Combine(folder, "SpotySync.ini"));
        var data = Path.Combine(folder, "Data");
        if (Directory.Exists(data))
            foreach (var file in Directory.EnumerateFiles(data))
            {
                var time = File.GetLastWriteTimeUtc(file);
                if (time > newest) newest = time;
            }
        return newest;
    }

    private static int Copy(string source, string target)
    {
        var copied = 0;
        File.Copy(Path.Combine(source, "SpotySync.ini"), Path.Combine(target, "SpotySync.ini"), false);
        copied++;
        var sourceData = Path.Combine(source, "Data");
        if (Directory.Exists(sourceData))
        {
            var targetData = Path.Combine(target, "Data");
            Directory.CreateDirectory(targetData);
            foreach (var file in Directory.GetFiles(sourceData))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || SkippedDataFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                try { File.Copy(file, Path.Combine(targetData, name), false); copied++; } catch { }
            }
        }
        foreach (var name in ToolFiles)
        {
            var from = Path.Combine(source, "Tools", name);
            var to = Path.Combine(target, "Tools", name);
            if (!File.Exists(from) || File.Exists(to)) continue;
            try { Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, false); copied++; } catch { }
        }
        FixRelativeFolders(source, target);
        return copied;
    }

    /// <summary>Music and playlist folders written as paths relative to the old copy must keep pointing at the same place.</summary>
    private static void FixRelativeFolders(string source, string target)
    {
        var old = PortableSettings.Load(source); // the ini exists there, so this only reads
        var settings = PortableSettings.Load(target);
        var changed = false;
        if (!Path.IsPathRooted(settings.MusicFolder)) { settings.MusicFolder = old.ResolvePath(old.MusicFolder); changed = true; }
        if (!Path.IsPathRooted(settings.M3uFolder)) { settings.M3uFolder = old.ResolvePath(old.M3uFolder); changed = true; }
        if (changed) settings.Save();
    }
}
