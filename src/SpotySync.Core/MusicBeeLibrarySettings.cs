using System.Text;
using System.Text.RegularExpressions;

namespace SpotySync;

/// <summary>
/// MusicBee's per-library settings (MusicBeeLibrarySettings.ini, beside the .mbl library file).
/// Its auto-organise rule moves every library song found under "MusicFromFolder" into the library music
/// folder. With MusicFromFolder = "E:\" that includes soundtrack folders like Music\Straftat, which is why
/// MusicBee kept pulling saved soundtracks into Songs. Narrowing MusicFromFolder to the Songs folder keeps
/// organising downloads exactly as before while leaving soundtrack folders alone.
/// </summary>
public static class MusicBeeLibrarySettings
{
    /// <summary>Looks for MusicBee\MusicBeeLibrarySettings.ini next to (or above) the music folder.</summary>
    public static string? Locate(PortableSettings settings)
    {
        try
        {
            var dir = new DirectoryInfo(settings.ResolvePath(settings.MusicFolder));
            for (var i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "MusicBee", "MusicBeeLibrarySettings.ini");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    private static string Value(string text, string tag)
    {
        var m = Regex.Match(text, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string Folder(string path) => path.Length == 0 ? "" : path.TrimEnd('\\', '/') + "\\";

    /// <summary>True when MusicBee's auto-organise would move songs out of <paramref name="folder"/>.</summary>
    public static bool OrganiseWouldMove(string settingsText, string folder)
    {
        if (!Value(settingsText, "MusicOrganiseEnabled").Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Value(settingsText, "OrganisationLibraryKeepOrganised").Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
        var from = Folder(Value(settingsText, "MusicFromFolder"));
        var library = Folder(Value(settingsText, "OrganisationLibraryMusicFolderPath"));
        var target = Folder(folder);
        if (from.Length == 0 || library.Length == 0) return false;
        return target.StartsWith(from, StringComparison.OrdinalIgnoreCase) && !target.StartsWith(library, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sets "organise from" to the library music folder (e.g. Songs). Returns the new text and whether it changed.</summary>
    public static (string Text, bool Changed) LimitOrganiseToLibraryFolder(string settingsText)
    {
        var library = Folder(Value(settingsText, "OrganisationLibraryMusicFolderPath"));
        var current = Folder(Value(settingsText, "MusicFromFolder"));
        if (library.Length == 0 || current.Equals(library, StringComparison.OrdinalIgnoreCase)) return (settingsText, false);
        var updated = Regex.Replace(settingsText, "<MusicFromFolder>.*?</MusicFromFolder>",
            "<MusicFromFolder>" + library.Replace("$", "$$") + "</MusicFromFolder>", RegexOptions.Singleline);
        return (updated, updated != settingsText);
    }

    /// <summary>Applies <see cref="LimitOrganiseToLibraryFolder"/> on disk (MusicBee must be closed; a backup is written).</summary>
    public static bool LimitOrganiseOnDisk(string path)
    {
        if (MusicBeeSettingsFile.IsMusicBeeRunning()) throw new InvalidOperationException("MusicBee is running. Close it first — it would overwrite the change when it exits.");
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var (updated, changed) = LimitOrganiseToLibraryFolder(text);
        if (!changed) return false;
        File.Copy(path, path + ".spotysync-backup", true);
        var temp = path + ".spotysync-tmp";
        File.WriteAllText(temp, updated, new UTF8Encoding(hasBom));
        File.Move(temp, path, true);
        return true;
    }
}

/// <summary>
/// Remembers which songs belong to a saved soundtrack (and when it was saved), so files that end up in the
/// Songs folder anyway can be moved back. Only files written at or after the save time are ever moved,
/// so older copies of the same song (e.g. a Liked Song) are never touched.
/// </summary>
public sealed class SoundtrackManifest
{
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public List<string> Files { get; set; } = new();

    public static string PathFor(PortableSettings settings, string name) =>
        System.IO.Path.Combine(settings.DataDirectory, "soundtracks", name + ".txt");

    public void Save(PortableSettings settings)
    {
        var path = PathFor(settings, Name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var lines = new List<string> { "# SpotySync soundtrack manifest", "saved=" + SavedUtc.ToString("O") };
        lines.AddRange(Files.Distinct(StringComparer.OrdinalIgnoreCase));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    public static SoundtrackManifest? Load(PortableSettings settings, string name)
    {
        var path = PathFor(settings, name);
        if (!File.Exists(path)) return null;
        var manifest = new SoundtrackManifest { Name = name };
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("saved=", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.TryParse(line[6..], null, System.Globalization.DateTimeStyles.RoundtripKind, out var saved)) manifest.SavedUtc = saved.ToUniversalTime();
                continue;
            }
            manifest.Files.Add(line);
        }
        return manifest;
    }

    /// <summary>Songs listed in the manifest that are not in the soundtrack folder.</summary>
    public List<string> MissingFrom(string folder) =>
        Files.Where(f => !File.Exists(System.IO.Path.Combine(folder, f))).ToList();

    private static readonly Regex Suffix = new(@"^(?<stem>.+?)(_\d+| \(\d+\))?$", RegexOptions.Compiled);

    /// <summary>
    /// Finds where a missing song went: same name or MusicBee's collision name ("Song_1.mp3") in the library
    /// folder, written at/after the save time. Returns null when nothing safe to move was found.
    /// </summary>
    public string? FindStray(string fileName, string libraryFolder)
    {
        if (!Directory.Exists(libraryFolder)) return null;
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        var cutoff = SavedUtc.AddMinutes(-2);
        try
        {
            return Directory.EnumerateFiles(libraryFolder, "*" + ext, SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var candidate = System.IO.Path.GetFileNameWithoutExtension(path);
                    var m = Suffix.Match(candidate);
                    return (candidate.Equals(stem, StringComparison.OrdinalIgnoreCase) || m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase))
                           && File.GetLastWriteTimeUtc(path) >= cutoff;
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
