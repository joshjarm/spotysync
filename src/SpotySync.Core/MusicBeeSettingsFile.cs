using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SpotySync;

/// <summary>
/// Safe, minimal edits to MusicBee3Settings.ini. MusicBee rewrites this file when it exits,
/// so edits are only made while MusicBee is closed, and a backup is always written first.
/// </summary>
public static class MusicBeeSettingsFile
{
    private static readonly Regex FilteredBlock = new(@"<NavigatorFilteredFolders>(?<body>.*?)</NavigatorFilteredFolders>|<NavigatorFilteredFolders\s*/>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StringItem = new(@"<string>(?<v>.*?)</string>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Portable MusicBee keeps settings beside MusicBee.exe; installed MusicBee uses %AppData%\MusicBee.</summary>
    public static string? Locate(PortableSettings settings)
    {
        try
        {
            var exe = settings.ResolvePath(settings.MusicBeeExe);
            var portable = Path.Combine(Path.GetDirectoryName(exe) ?? "", "MusicBee3Settings.ini");
            if (File.Exists(portable)) return portable;
        }
        catch { }
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBee", "MusicBee3Settings.ini");
        return File.Exists(roaming) ? roaming : null;
    }

    public static bool IsMusicBeeRunning() => Process.GetProcessesByName("MusicBee").Length > 0;

    public static string NormalizeFolder(string folder) => Path.GetFullPath(folder.Trim()).TrimEnd('\\', '/') + "\\";

    public static IReadOnlyList<string> ReadFilteredFolders(string settingsText)
    {
        var match = FilteredBlock.Match(settingsText);
        if (!match.Success) return Array.Empty<string>();
        return StringItem.Matches(match.Groups["body"].Value).Select(m => Unescape(m.Groups["v"].Value)).ToList();
    }

    /// <summary>Returns the settings text with the folder added to the Folders panel filter (sorted, no duplicates).</summary>
    public static (string Text, bool Changed) AddFilteredFolder(string settingsText, string folder)
    {
        var normalized = NormalizeFolder(folder);
        var match = FilteredBlock.Match(settingsText);
        if (!match.Success) throw new InvalidOperationException("MusicBee's settings have no Folders-panel filter section (NavigatorFilteredFolders). Open MusicBee's Folders tab once, choose “filtered folders”, then retry.");
        var existing = ReadFilteredFolders(settingsText).ToList();
        if (existing.Any(x => x.TrimEnd('\\', '/').Equals(normalized.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return (settingsText, false);
        existing.Add(normalized);
        existing = existing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var newline = settingsText.Contains("\r\n") ? "\r\n" : "\n";
        var body = new StringBuilder(newline);
        foreach (var item in existing) body.Append(" <string>").Append(Escape(item)).Append("</string>").Append(newline);
        var replacement = "<NavigatorFilteredFolders>" + body + "</NavigatorFilteredFolders>";
        return (settingsText[..match.Index] + replacement + settingsText[(match.Index + match.Length)..], true);
    }

    /// <summary>Adds folders to the filter on disk. MusicBee must be closed. Returns how many were newly added.</summary>
    public static int RegisterFilteredFolders(string settingsPath, IEnumerable<string> folders)
    {
        if (IsMusicBeeRunning()) throw new InvalidOperationException("MusicBee is running. Close it first — it would overwrite the change when it exits.");
        var bytes = File.ReadAllBytes(settingsPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var added = 0;
        foreach (var folder in folders)
        {
            var (updated, changed) = AddFilteredFolder(text, folder);
            if (changed) { text = updated; added++; }
        }
        if (added == 0) return 0;
        File.Copy(settingsPath, settingsPath + ".spotysync-backup", true);
        var temp = settingsPath + ".spotysync-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(hasBom));
        File.Move(temp, settingsPath, true);
        return added;
    }

    private static string Escape(string v) => v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Unescape(string v) => v.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    // ---- pending registrations (when MusicBee was open at the time) ----
    public static string PendingPath(PortableSettings settings) => Path.Combine(settings.DataDirectory, "musicbee-pending-folders.txt");

    public static List<string> ReadPending(PortableSettings settings)
    {
        var path = PendingPath(settings);
        return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
    }

    public static void WritePending(PortableSettings settings, IEnumerable<string> folders)
    {
        var list = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var path = PendingPath(settings);
        if (list.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(settings.DataDirectory);
        File.WriteAllLines(path, list);
    }
}
