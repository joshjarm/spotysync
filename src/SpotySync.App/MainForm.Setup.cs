using System.Diagnostics;
using System.Security.Cryptography;
using SpotySync;

namespace SpotySyncApp;

public sealed partial class MainForm
{
    /// <summary>
    /// One-click setup: reuse an existing working setup (or pick up new keys), save, bring the MusicBee
    /// plugin up to date (restarting MusicBee if needed), then connect Spotify only if no login came along.
    /// </summary>
    private async void OpenSetupAssistant()
    {
        if (cancellation is not null) return;
        // Truly one click when a working setup (with a saved login) already exists on this PC.
        if (await OfferExistingSetupAsync(userInitiated: true)) return;
        var existing = ExistingSetup.Find(settings).Where(x => !x.ClientId.Equals(settings.SpotifyClientId, StringComparison.OrdinalIgnoreCase) || !service.HasSavedLogin).ToList();
        using var assistant = new SetupAssistant(redirectUri.Text.Trim().Length > 0 ? redirectUri.Text.Trim() : "http://127.0.0.1/callback", clientId.Text.Trim(), existing);
        if (assistant.ShowDialog(this) != DialogResult.OK) return;

        if (assistant.Adopted is { } adopted) AdoptExistingSetup(adopted);
        else
        {
            clientId.Text = assistant.ClientId;
            clientSecret.Text = assistant.ClientSecret;
            AppendLog("Setup assistant: Spotify Client ID and secret filled in.");
        }
        if (!SaveSettingsFields(false)) return;
        if (settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
            await RunBusyAsync("Updating the MusicBee plugin", _ => UpdateMusicBeePluginAsync(force: false));
        if (service.HasSavedLogin) { AppendLog("Spotify login reused from your existing setup - you're connected."); await RefreshStatusAsync(); }
        else await ConnectAsync();
    }

    private string AdoptDeclinedPath => Path.Combine(settings.DataDirectory, "existing-setup-declined.txt");

    /// <summary>
    /// When this copy isn't signed in but an earlier SpotySync on this PC is, reuse it: keys, login, folders,
    /// options and sync history. From the setup button it happens straight away; at startup it asks once.
    /// Returns true when an existing setup was adopted.
    /// </summary>
    private async Task<bool> OfferExistingSetupAsync(bool userInitiated)
    {
        if (cancellation is not null || service.HasSavedLogin) return false;
        var ready = ExistingSetup.Find(settings).FirstOrDefault(x => x.HasLogin);
        if (ready is null) return false;
        if (!userInitiated)
        {
            if (File.Exists(AdoptDeclinedPath)) return false;
            var answer = MessageBox.Show(this,
                $"SpotySync found your working Spotify connection in:\r\n{ready.Folder}\r\n\r\nUse it here? This copies its Spotify keys, login, folders, options and sync history (nothing there is changed), so you don't have to set anything up.",
                "Use your existing setup?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                try { Directory.CreateDirectory(settings.DataDirectory); File.WriteAllText(AdoptDeclinedPath, ready.Folder); } catch { }
                return false;
            }
        }
        AdoptExistingSetup(ready);
        if (!SaveSettingsFields(false)) return false;
        if (settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
            await RunBusyAsync("Updating the MusicBee plugin", _ => UpdateMusicBeePluginAsync(force: false));
        AppendLog(service.HasSavedLogin ? "Spotify is connected using your existing login." : "Keys imported - connecting Spotify…");
        if (!service.HasSavedLogin) await ConnectAsync();
        await RefreshStatusAsync();
        return true;
    }

    /// <summary>Copies keys, folders/options, login and sync history from an earlier SpotySync folder (read-only there).</summary>
    private void AdoptExistingSetup(ExistingSetup source)
    {
        try
        {
            var old = PortableSettings.Load(source.Folder); // the ini exists, so this only reads
            settings.SpotifyClientId = old.SpotifyClientId;
            settings.RedirectUri = old.RedirectUri;
            settings.ApiRetryCount = old.ApiRetryCount;
            settings.MusicFolder = old.MusicFolder;
            settings.M3uFolder = Path.IsPathRooted(old.M3uFolder) ? old.M3uFolder : old.ResolvePath(old.M3uFolder);
            settings.MusicBeeExe = old.MusicBeeExe;
            settings.MusicBeeBridgePort = old.MusicBeeBridgePort;
            settings.LocalBackend = old.LocalBackend;
            settings.Direction = old.Direction;
            settings.EnableRemovals = old.EnableRemovals;
            settings.MaxRemovalsPerPlaylist = old.MaxRemovalsPerPlaylist;
            settings.LikedSongsOnlyForSpotify = old.LikedSongsOnlyForSpotify;
            settings.OwnedPlaylistsOnly = old.OwnedPlaylistsOnly;
            settings.RelativeM3uPaths = old.RelativeM3uPaths;
            settings.PlaylistInclude = old.PlaylistInclude;
            settings.PlaylistExclude = old.PlaylistExclude;
            settings.AutoSyncAfterBackup = old.AutoSyncAfterBackup;
            settings.LikedCacheFullRefreshDays = old.LikedCacheFullRefreshDays;
            settings.FirstRunLikedLookbackDays = old.FirstRunLikedLookbackDays;
            settings.DownloadAnchorFileCount = old.DownloadAnchorFileCount;
            settings.ImportDownloadsIntoMusicBee = old.ImportDownloadsIntoMusicBee;
            settings.PrimeDownloadsInMusicBee = old.PrimeDownloadsInMusicBee;
            settings.AutoStartMusicBee = old.AutoStartMusicBee;
            settings.AutoUpdateYtDlp = old.AutoUpdateYtDlp;
            settings.AutoYouTubeFallback = old.AutoYouTubeFallback;
        }
        catch (Exception ex) { AppendLog("WARNING: could not read the old settings, keys only: " + ex.Message); settings.SpotifyClientId = source.ClientId; }
        var copied = source.ImportData(settings);
        LoadSettingsFields();
        if (source.ClientSecret.Length > 0) clientSecret.Text = source.ClientSecret;
        AppendLog($"Reused your existing SpotySync setup from {source.Folder} (keys, folders, options and {copied} data file(s) incl. login and sync history).");
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<int> BridgeVersionAsync(PortableSettings s)
    {
        try { using var bridge = new MusicBeeBridgeClient(s); return await bridge.GetVersionAsync(); } catch { return 0; }
    }

    /// <summary>
    /// Makes sure MusicBee runs the bundled plugin: installs it when missing/different and restarts MusicBee
    /// (gracefully) so the new DLL is actually loaded, then waits for the bridge and refreshes the status.
    /// With <paramref name="force"/>, MusicBee is also restarted when the file is current but MusicBee still runs an old copy.
    /// </summary>
    private async Task UpdateMusicBeePluginAsync(bool force)
    {
        var source = PackagedPluginPath;
        if (!File.Exists(source)) { AppendLog("WARNING: the packaged MusicBee plugin is missing; re-extract the complete SpotySync folder."); return; }
        var (programCopy, appDataCopy, _) = PluginLocations();
        var installed = new[] { appDataCopy, programCopy }.FirstOrDefault(File.Exists);
        var fileCurrent = installed is not null && FileHash(installed) == FileHash(source);
        var running = MusicBeeSettingsFile.IsMusicBeeRunning();
        var loadedOld = running && await BridgeVersionAsync(settings) < 21;
        if (fileCurrent && !loadedOld)
        {
            AppendLog(running ? "MusicBee plugin is up to date and loaded." : "MusicBee plugin is up to date (it loads next time MusicBee starts).");
            await RefreshStatusAsync();
            return;
        }
        if (fileCurrent && loadedOld && !force && !running) return;

        if (running)
        {
            AppendLog(fileCurrent ? "MusicBee is still running the old plugin - restarting it to load the new one…" : "Closing MusicBee to update its SpotySync plugin (it reopens automatically)…");
            foreach (var process in Process.GetProcessesByName("MusicBee")) { try { process.CloseMainWindow(); } catch { } }
            for (var i = 0; i < 60 && MusicBeeSettingsFile.IsMusicBeeRunning(); i++) await Task.Delay(500);
            if (MusicBeeSettingsFile.IsMusicBeeRunning())
            {
                AppendLog("WARNING: MusicBee did not close (a dialog may be open in it). Close MusicBee yourself, then click Install MusicBee plugin again.");
                return;
            }
            await Task.Delay(800); // let MusicBee release the DLL
        }

        if (!fileCurrent)
        {
            try
            {
                var target = InstallPluginFiles(new List<string>());
                AppendLog((installed is null ? "Installed" : "Updated") + " the MusicBee plugin: " + target);
            }
            catch (UnauthorizedAccessException)
            {
                AppendLog("WARNING: the old plugin is in a protected folder. Install MusicBee Plugin\\SpotySync-MusicBee-Plugin.zip from MusicBee › Preferences › Plugins.");
            }
            catch (IOException ex) { AppendLog("WARNING: could not replace the MusicBee plugin: " + ex.Message); }
        }

        if (running)
        {
            try
            {
                var exe = settings.ResolvePath(settings.MusicBeeExe);
                if (!File.Exists(exe)) { AppendLog("Start MusicBee to load the updated plugin."); return; }
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                AppendLog("Reopened MusicBee - waiting for the updated plugin…");
                for (var i = 0; i < 90; i++)
                {
                    await Task.Delay(500);
                    if (await BridgeVersionAsync(settings) >= 21) { AppendLog("MusicBee plugin is loaded and ready."); break; }
                }
            }
            catch (Exception ex) { AppendLog("WARNING: could not reopen MusicBee: " + ex.Message); }
        }
        await RefreshStatusAsync();
    }

    /// <summary>Logs a clear hint if the Spotify login hasn't completed after a while (usually a redirect URI mismatch).</summary>
    private async Task HintIfLoginStallsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            AppendLog($"WARNING: still waiting for Spotify. If the browser shows “redirect_uri: Not matching configuration”, open your app on developer.spotify.com › Edit settings and add {settings.RedirectUri} as a Redirect URI (or use One-click setup › Use my existing setup).");
        }
        catch (OperationCanceledException) { }
    }
}
