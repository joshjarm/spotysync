namespace SpotySync;

/// <summary>What differs between the Windows and the Linux build: tool file names, paths and the music player.</summary>
public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>A bundled tool's file name: "yt-dlp" is yt-dlp.exe on Windows and yt-dlp on Linux.</summary>
    public static string Exe(string name) => IsWindows ? name + ".exe" : name;

    /// <summary>
    /// Settings written on Windows use backslashes (Tools\spotdl.exe); on Linux they become Tools/spotdl and
    /// lose the .exe of the bundled tools, so a SpotySync.ini carried over from Windows keeps working.
    /// </summary>
    public static string LocalPath(string path)
    {
        if (IsWindows || string.IsNullOrEmpty(path)) return path;
        var unix = path.Replace('\\', '/');
        foreach (var tool in new[] { "spotdl", "ffprobe", "ffmpeg", "yt-dlp", "deno" })
            if (unix.EndsWith("/" + tool + ".exe", StringComparison.OrdinalIgnoreCase) || unix.Equals(tool + ".exe", StringComparison.OrdinalIgnoreCase))
                return unix[..^4];
        return unix;
    }

    /// <summary>MusicBee only exists on Windows; on Linux playlists are written as M3U files (Strawberry and others read them).</summary>
    public static bool HasMusicBee => IsWindows;

    /// <summary>The Flatpak Firefox keeps its profile in the app sandbox; yt-dlp finds it when given that folder.</summary>
    public static string? FirefoxCookieSource()
    {
        if (IsWindows) return "firefox";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = Path.Combine(home, ".mozilla", "firefox");
        var flatpak = Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox");
        var native2 = Path.Combine(home, ".config", "mozilla", "firefox"); // newer Firefox follows XDG
        if (HasCookies(native) || HasCookies(native2)) return "firefox";
        if (HasCookies(flatpak)) return "firefox:" + flatpak;
        return "firefox";
    }

    private static bool HasCookies(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "cookies.sqlite", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// <summary>The Linux default browser (from xdg-settings), as a yt-dlp --cookies-from-browser name.</summary>
    public static string? LinuxDefaultBrowser()
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("xdg-settings", "get default-web-browser")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            var name = process.StandardOutput.ReadToEnd().ToLowerInvariant();
            process.WaitForExit(3000);
            if (name.Contains("firefox")) return "firefox";
            if (name.Contains("brave")) return "brave";
            if (name.Contains("vivaldi")) return "vivaldi";
            if (name.Contains("opera")) return "opera";
            if (name.Contains("chromium")) return "chromium";
            if (name.Contains("chrome")) return "chrome";
            if (name.Contains("edge")) return "edge";
        }
        catch { }
        return null;
    }
}
