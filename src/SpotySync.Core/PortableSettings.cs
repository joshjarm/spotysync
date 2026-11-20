using System.Globalization;

namespace SpotySync;

public sealed class PortableSettings
{
    public string BaseDirectory { get; private set; } = AppContext.BaseDirectory;
    public string FilePath => Path.Combine(BaseDirectory, "SpotySync.ini");

    public string SpotifyClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "http://127.0.0.1/callback";
    public string MusicFolder { get; set; } = DefaultMusicFolder();
    public string M3uFolder { get; set; } = "Playlists";
    public string DownloaderPath { get; set; } = @"Tools\spotdl.exe";
    public string FfprobePath { get; set; } = @"Tools\ffprobe.exe";
    public string MusicBeeExe { get; set; } = DefaultMusicBeePath();
    /// <summary>Parent folder for Soundtrack Saver; each soundtrack gets its own subfolder.</summary>
    public string SoundtrackFolder { get; set; } = DefaultSoundtrackFolder();
    public int MusicBeeBridgePort { get; set; } = 51983;
    public LocalBackendKind LocalBackend { get; set; } = LocalBackendKind.MusicBee;
    public SyncDirection Direction { get; set; } = SyncDirection.TwoWay;
    public bool EnableRemovals { get; set; }
    public int MaxRemovalsPerPlaylist { get; set; } = 25;
    public bool LikedSongsOnlyForSpotify { get; set; } = true;
    public bool OwnedPlaylistsOnly { get; set; } = true;
    public bool RelativeM3uPaths { get; set; } = true;
    public string PlaylistInclude { get; set; } = "*";
    public string PlaylistExclude { get; set; } = "__SpotySync*;Discover Weekly;Release Radar";
    public int LikedCacheFullRefreshDays { get; set; } = 7;
    public int FirstRunLikedLookbackDays { get; set; } = 14;
    public int DownloadAnchorFileCount { get; set; } = 5;
    public int BackupMarkerOverlapMinutes { get; set; } = 10;
    public bool CreateTemporaryDownloadPlaylist { get; set; } = true;
    public bool AutoUpdateYtDlp { get; set; } = true;
    public int YtDlpUpdateDays { get; set; } = 7;
    public bool AutoYouTubeFallback { get; set; } = true;
    /// <summary>Download pace: Safe, Balanced, Fast or Max (threads + pauses between songs).</summary>
    public string DownloadSpeed { get; set; } = "Fast";
    /// <summary>Audio file type for downloads: Opus (YouTube's original audio, no re-encode), MP3 (320 kbps, plays everywhere) or M4A (AAC).</summary>
    public string DownloadFormat { get; set; } = "Opus";
    public bool ImportDownloadsIntoMusicBee { get; set; } = true;
    public bool PrimeDownloadsInMusicBee { get; set; } = true;
    public bool AutoStartMusicBee { get; set; } = true;
    public bool AutoSyncAfterBackup { get; set; } = true;
    public int ApiRetryCount { get; set; } = 5;

    // Appearance and layout (used by the desktop UI only; harmless for the command line).
    public string UiTheme { get; set; } = "spotysync";
    public string UiAccent { get; set; } = "";
    public string UiFont { get; set; } = "system";
    public bool UiRoundedCorners { get; set; } = true;
    public bool UiBackgroundGlow { get; set; } = true;
    public bool UiAnimations { get; set; } = true;
    public bool UiCompactNavigation { get; set; }
    public string UiDashboardCards { get; set; } = "";
    public bool UiConsoleDownloaderDetail { get; set; }
    public bool UiConsoleTimestamps { get; set; } = true;
    public bool UiConsoleSimple { get; set; } // off: the console shows everything by default
    /// <summary>Console page shows the download graph beside the log.</summary>
    public bool UiConsoleSplit { get; set; }
    /// <summary>Download graph time range in seconds (0 = whole run).</summary>
    public int UiActivityRange { get; set; }

    public string DataDirectory => ResolvePath("Data");
    public string TokenPath => Path.Combine(DataDirectory, "spotify-token.json");
    public string StatePath => Path.Combine(DataDirectory, "sync-state.json");
    public string LikedCachePath => Path.Combine(DataDirectory, "liked-cache.json");
    public string PlaylistCachePath => Path.Combine(DataDirectory, "spotify-playlist-cache.json");
    public string LibraryCachePath => Path.Combine(DataDirectory, "local-library-cache.json");
    public string SyncExclusionsPath => Path.Combine(DataDirectory, "sync-exclusions.txt");
    public string LogDirectory => ResolvePath("Logs");

    public static PortableSettings Load(string? baseDirectory = null)
    {
        var settings = new PortableSettings
        {
            BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory)
        };
        if (!File.Exists(settings.FilePath))
        {
            settings.Save();
            return settings;
        }

        var values = IniFile.Read(settings.FilePath);
        settings.SpotifyClientId = Get(values, "Spotify", "ClientId", settings.SpotifyClientId);
        settings.RedirectUri = Get(values, "Spotify", "RedirectUri", settings.RedirectUri);
        settings.ApiRetryCount = GetInt(values, "Spotify", "ApiRetryCount", settings.ApiRetryCount, 0, 12);
        settings.LikedCacheFullRefreshDays = GetInt(values, "Spotify", "LikedCacheFullRefreshDays", settings.LikedCacheFullRefreshDays, 0, 365);

        settings.MusicFolder = Get(values, "Paths", "MusicFolder", settings.MusicFolder);
        settings.M3uFolder = Get(values, "Paths", "M3uFolder", settings.M3uFolder);
        settings.DownloaderPath = Get(values, "Paths", "Downloader", settings.DownloaderPath);
        // spot2yt was a private tool that is not distributed; older settings that point at a missing
        // spot2yt.exe use the bundled spotDL instead.
        if (Path.GetFileName(settings.DownloaderPath).StartsWith("spot2yt", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(settings.ResolvePath(settings.DownloaderPath)))
            settings.DownloaderPath = @"Tools\spotdl.exe";
        settings.FfprobePath = Get(values, "Paths", "Ffprobe", settings.FfprobePath);
        settings.MusicBeeExe = Get(values, "Paths", "MusicBeeExe", settings.MusicBeeExe);
        settings.SoundtrackFolder = Get(values, "Paths", "SoundtrackFolder", settings.SoundtrackFolder);

        settings.MusicBeeBridgePort = GetInt(values, "MusicBee", "BridgePort", settings.MusicBeeBridgePort, 1024, 65535);
        settings.ImportDownloadsIntoMusicBee = GetBool(values, "MusicBee", "ImportDownloads", settings.ImportDownloadsIntoMusicBee);
        settings.PrimeDownloadsInMusicBee = GetBool(values, "MusicBee", "PrimeDownloads", settings.PrimeDownloadsInMusicBee);
        settings.AutoStartMusicBee = GetBool(values, "MusicBee", "AutoStart", settings.AutoStartMusicBee);

        settings.LocalBackend = GetEnum(values, "Sync", "LocalBackend", settings.LocalBackend);
        settings.Direction = GetEnum(values, "Sync", "Direction", settings.Direction);
        settings.EnableRemovals = GetBool(values, "Sync", "EnableRemovals", settings.EnableRemovals);
        settings.MaxRemovalsPerPlaylist = GetInt(values, "Sync", "MaxRemovalsPerPlaylist", settings.MaxRemovalsPerPlaylist, 0, 10000);
        settings.LikedSongsOnlyForSpotify = GetBool(values, "Sync", "LikedSongsOnlyForSpotify", settings.LikedSongsOnlyForSpotify);
        settings.OwnedPlaylistsOnly = GetBool(values, "Sync", "OwnedPlaylistsOnly", settings.OwnedPlaylistsOnly);
        settings.RelativeM3uPaths = GetBool(values, "Sync", "RelativeM3uPaths", settings.RelativeM3uPaths);
        settings.PlaylistInclude = Get(values, "Sync", "PlaylistInclude", settings.PlaylistInclude);
        settings.PlaylistExclude = Get(values, "Sync", "PlaylistExclude", settings.PlaylistExclude);
        settings.AutoSyncAfterBackup = GetBool(values, "Sync", "AutoSyncAfterBackup", settings.AutoSyncAfterBackup);

        settings.FirstRunLikedLookbackDays = GetInt(values, "Backup", "FirstRunLikedLookbackDays", settings.FirstRunLikedLookbackDays, 1, 3650);
        settings.DownloadAnchorFileCount = GetInt(values, "Backup", "RecentDownloadAnchorCount", settings.DownloadAnchorFileCount, 1, 50);
        settings.BackupMarkerOverlapMinutes = GetInt(values, "Backup", "MarkerOverlapMinutes", settings.BackupMarkerOverlapMinutes, 0, 1440);
        settings.CreateTemporaryDownloadPlaylist = GetBool(values, "Backup", "CreateTemporaryPlaylist", settings.CreateTemporaryDownloadPlaylist);
        settings.AutoUpdateYtDlp = GetBool(values, "Backup", "AutoUpdateYtDlp", settings.AutoUpdateYtDlp);
        settings.YtDlpUpdateDays = GetInt(values, "Backup", "YtDlpUpdateDays", settings.YtDlpUpdateDays, 1, 30);
        settings.AutoYouTubeFallback = GetBool(values, "Backup", "AutoYouTubeFallback", settings.AutoYouTubeFallback);
        settings.DownloadSpeed = Get(values, "Backup", "DownloadSpeed", settings.DownloadSpeed);
        settings.DownloadFormat = Get(values, "Backup", "DownloadFormat", settings.DownloadFormat);

        settings.UiTheme = Get(values, "Appearance", "Theme", settings.UiTheme);
        settings.UiAccent = Get(values, "Appearance", "Accent", settings.UiAccent);
        settings.UiFont = Get(values, "Appearance", "Font", settings.UiFont);
        settings.UiRoundedCorners = GetBool(values, "Appearance", "RoundedCorners", settings.UiRoundedCorners);
        settings.UiBackgroundGlow = GetBool(values, "Appearance", "BackgroundGlow", settings.UiBackgroundGlow);
        settings.UiAnimations = GetBool(values, "Appearance", "Animations", settings.UiAnimations);
        settings.UiCompactNavigation = GetBool(values, "Appearance", "CompactNavigation", settings.UiCompactNavigation);
        settings.UiDashboardCards = Get(values, "Appearance", "DashboardCards", settings.UiDashboardCards);
        settings.UiConsoleDownloaderDetail = GetBool(values, "Appearance", "ConsoleDownloaderDetail", settings.UiConsoleDownloaderDetail);
        settings.UiConsoleTimestamps = GetBool(values, "Appearance", "ConsoleTimestamps", settings.UiConsoleTimestamps);
        settings.UiConsoleSimple = GetBool(values, "Appearance", "ConsoleSimple", settings.UiConsoleSimple);
        settings.UiConsoleSplit = GetBool(values, "Appearance", "ConsoleSplit", settings.UiConsoleSplit);
        settings.UiActivityRange = int.TryParse(Get(values, "Appearance", "ActivityRange", "0"), out var range) ? Math.Clamp(range, 0, 86400) : 0;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(BaseDirectory);
        var text = $"""
# SpotySync portable configuration. This file stays beside SpotySync.exe.
# A Client ID is required; a client secret is NOT used (Authorization Code + PKCE).

[Spotify]
ClientId={SpotifyClientId}
RedirectUri={RedirectUri}
ApiRetryCount={ApiRetryCount}
LikedCacheFullRefreshDays={LikedCacheFullRefreshDays}

[Paths]
MusicFolder={MusicFolder}
M3uFolder={M3uFolder}
Downloader={DownloaderPath}
Ffprobe={FfprobePath}
MusicBeeExe={MusicBeeExe}
SoundtrackFolder={SoundtrackFolder}

[MusicBee]
BridgePort={MusicBeeBridgePort}
ImportDownloads={ImportDownloadsIntoMusicBee}
PrimeDownloads={PrimeDownloadsInMusicBee}
AutoStart={AutoStartMusicBee}

[Sync]
# LocalBackend: MusicBee, M3U, or Both. Direction: TwoWay, SpotifyToLocal, or LocalToSpotify.
LocalBackend={LocalBackend}
Direction={Direction}
# Removals are ignored until this is true AND a successful baseline exists.
EnableRemovals={EnableRemovals}
MaxRemovalsPerPlaylist={MaxRemovalsPerPlaylist}
LikedSongsOnlyForSpotify={LikedSongsOnlyForSpotify}
OwnedPlaylistsOnly={OwnedPlaylistsOnly}
RelativeM3uPaths={RelativeM3uPaths}
PlaylistInclude={PlaylistInclude}
PlaylistExclude={PlaylistExclude}
AutoSyncAfterBackup={AutoSyncAfterBackup}

[Backup]
FirstRunLikedLookbackDays={FirstRunLikedLookbackDays}
RecentDownloadAnchorCount={DownloadAnchorFileCount}
MarkerOverlapMinutes={BackupMarkerOverlapMinutes}
CreateTemporaryPlaylist={CreateTemporaryDownloadPlaylist}
AutoUpdateYtDlp={AutoUpdateYtDlp}
YtDlpUpdateDays={YtDlpUpdateDays}
AutoYouTubeFallback={AutoYouTubeFallback}
# DownloadSpeed: Safe (1 song at a time, 4-10 s pauses), Balanced, Fast or Max. Slows down by itself if YouTube limits you.
DownloadSpeed={DownloadSpeed}
# DownloadFormat: Opus (YouTube's original audio, best quality, smallest files), MP3 (320 kbps, plays on anything) or M4A (AAC, good for Apple devices).
DownloadFormat={DownloadFormat}

[Appearance]
# Desktop look and layout. Change these from Settings > Appearance; they never affect syncing.
Theme={UiTheme}
Accent={UiAccent}
Font={UiFont}
RoundedCorners={UiRoundedCorners}
BackgroundGlow={UiBackgroundGlow}
Animations={UiAnimations}
CompactNavigation={UiCompactNavigation}
DashboardCards={UiDashboardCards}
ConsoleDownloaderDetail={UiConsoleDownloaderDetail}
ConsoleTimestamps={UiConsoleTimestamps}
ConsoleSimple={UiConsoleSimple}
ConsoleSplit={UiConsoleSplit}
ActivityRange={UiActivityRange}
""";
        File.WriteAllText(FilePath, text.Replace("\n", Environment.NewLine));
    }

    public string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BaseDirectory;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(BaseDirectory, path));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SpotifyClientId))
            throw new InvalidOperationException("Spotify ClientId is blank. Open SpotySync.ini and paste the Client ID from your Spotify Developer app.");
        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "http" || redirect.Host != "127.0.0.1")
            throw new InvalidOperationException("RedirectUri must be an HTTP loopback URI using 127.0.0.1, for example http://127.0.0.1/callback.");
        if (MaxRemovalsPerPlaylist < 0) throw new InvalidOperationException("MaxRemovalsPerPlaylist cannot be negative.");
    }

    private static string DefaultMusicFolder()
    {
        // Preserve the known MusicBee library location on this machine without
        // making it a requirement for portable installs elsewhere.
        const string knownMusicBeeLibrary = @"E:\Libraries\Music\Songs";
        if (Directory.Exists(knownMusicBeeLibrary)) return knownMusicBeeLibrary;
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        return string.IsNullOrWhiteSpace(music)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music")
            : music;
    }

    private static string DefaultSoundtrackFolder()
    {
        const string knownMusicRoot = @"E:\Libraries\Music";
        if (Directory.Exists(knownMusicRoot)) return knownMusicRoot;
        var parent = Path.GetDirectoryName(DefaultMusicFolder().TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(parent) ? Path.Combine(DefaultMusicFolder(), "Soundtracks") : parent;
    }

    private static string DefaultMusicBeePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MusicBee", "MusicBee.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MusicBee", "MusicBee.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string Get(Dictionary<string, Dictionary<string, string>> data, string section, string key, string fallback) =>
        data.TryGetValue(section, out var s) && s.TryGetValue(key, out var value) ? value.Trim() : fallback;
    private static bool GetBool(Dictionary<string, Dictionary<string, string>> data, string section, string key, bool fallback) =>
        bool.TryParse(Get(data, section, key, fallback.ToString()), out var value) ? value : fallback;
    private static int GetInt(Dictionary<string, Dictionary<string, string>> data, string section, string key, int fallback, int min, int max) =>
        int.TryParse(Get(data, section, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max) : fallback;
    private static T GetEnum<T>(Dictionary<string, Dictionary<string, string>> data, string section, string key, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(Get(data, section, key, fallback.ToString()), true, out var value) ? value : fallback;
}

public static class IniFile
{
    public static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "General";
        result[section] = new(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!result.ContainsKey(section)) result[section] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            result[section][line[..split].Trim()] = line[(split + 1)..].Trim();
        }
        return result;
    }
}
