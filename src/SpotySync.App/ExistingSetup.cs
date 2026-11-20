using SpotySync;

namespace SpotySyncApp;

/// <summary>A previous SpotySync installation whose working Spotify app (and login) can be reused.</summary>
public sealed record ExistingSetup(string Folder, string ClientId, string ClientSecret, bool HasLogin)
{
    /// <summary>
    /// Finds earlier SpotySync folders on this PC (installed copy, the folder the MusicBee plugin last saw,
    /// and older test builds next to this one). Read-only: nothing in those folders is changed.
    /// </summary>
    public static List<ExistingSetup> Find(PortableSettings current)
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SpotySync"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SpotySync"),
        };
        try
        {
            var companion = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBeeSyncToService", "CompanionPath.txt");
            if (File.Exists(companion)) candidates.Add(Path.GetDirectoryName(File.ReadAllText(companion).Trim()) ?? "");
        }
        catch { }
        try
        {
            var parent = Path.GetDirectoryName(current.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            if (parent is not null) candidates.AddRange(Directory.GetDirectories(parent, "SpotySync*"));
        }
        catch { }

        var found = new List<ExistingSetup>();
        foreach (var folder in candidates.Where(f => f.Length > 0).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (folder.TrimEnd('\\').Equals(current.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            var ini = Path.Combine(folder, "SpotySync.ini");
            if (!File.Exists(ini)) continue;
            try
            {
                var values = IniFile.Read(ini);
                var id = values.TryGetValue("Spotify", out var s) && s.TryGetValue("ClientId", out var v) ? v.Trim() : "";
                if (id.Length == 0) continue;
                var secret = ReadSecret(Path.Combine(folder, "Tools", "spotify_credentials.txt"));
                var hasLogin = File.Exists(Path.Combine(folder, "Data", "spotify-token.json"));
                if (found.All(x => !x.ClientId.Equals(id, StringComparison.OrdinalIgnoreCase) || hasLogin && !x.HasLogin))
                {
                    found.RemoveAll(x => x.ClientId.Equals(id, StringComparison.OrdinalIgnoreCase));
                    found.Add(new ExistingSetup(folder, id, secret, hasLogin));
                }
            }
            catch { }
        }
        // Prefer setups that are already logged in and have the downloader secret.
        return found.OrderByDescending(x => x.HasLogin).ThenByDescending(x => x.ClientSecret.Length > 0).ToList();
    }

    private static string ReadSecret(string path)
    {
        if (!File.Exists(path)) return "";
        try
        {
            return File.ReadAllLines(path)
                .Select(l => l.Split('=', 2))
                .FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals("SPOTIFY_CLIENT_SECRET", StringComparison.OrdinalIgnoreCase))?[1].Trim() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Copies login, caches and sync history into this copy (existing files here are kept unless they belong to another app).</summary>
    public int ImportData(PortableSettings target)
    {
        var source = Path.Combine(Folder, "Data");
        if (!Directory.Exists(source)) return 0;
        Directory.CreateDirectory(target.DataDirectory);
        var copied = 0;
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("sync-exclusions.txt", StringComparison.OrdinalIgnoreCase))
            {
                // never-sync rules are merged (deduplicated) into this copy's own portable list
                try { copied += new SyncExclusionStore(target.SyncExclusionsPath).Add(new SyncExclusionStore(file).Load()) > 0 ? 1 : 0; } catch { }
                continue;
            }
            var destination = Path.Combine(target.DataDirectory, name);
            // the login belongs to the Spotify app being adopted, so it always replaces an old one
            var replace = name.Equals("spotify-token.json", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(destination) && !replace) continue;
            try { File.Copy(file, destination, true); copied++; } catch { }
        }
        return copied;
    }
}
