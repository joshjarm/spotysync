using SpotySync;

namespace SpotySyncDesktop;

/// <summary>
/// SpotySync without the window, for a terminal or a scheduled job:
///   SpotySync --backup              download new Likes
///   SpotySync --sync [--dry-run]    build the playlist plan and apply it (or only show it)
///   SpotySync --download LINK...    download Spotify / YouTube links
/// </summary>
internal static class CommandLine
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("SpotySync --backup | --sync [--dry-run] [--refresh] | --download LINK...");
            return 0;
        }
        var settings = PortableSettings.Load();
        DownloadSpeed.Load(settings);
        var logFile = Path.Combine(settings.LogDirectory, "SpotySync-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        void Log(string message)
        {
            Console.WriteLine(message);
            try { Directory.CreateDirectory(settings.LogDirectory); File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine); } catch { }
        }
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); Log("Stopping…"); };
        using var service = new SpotySyncService(settings, Log);
        var refresh = args.Contains("--refresh");
        try
        {
            if (args[0] == "--download")
            {
                var links = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
                if (links.Count == 0) { Log("Give at least one link after --download."); return 2; }
                var result = await service.DownloadLinksAsync(links, cancel.Token);
                Log($"Download {(result.Success ? "complete" : "partly done")}: {result.NewAudioFiles} new song(s)" + (result.NotFound > 0 ? $", {result.NotFound} not on YouTube Music." : ".") + (result.Error.Length > 0 ? " " + result.Error : ""));
                return result.Success ? 0 : 1;
            }
            if (args[0] == "--backup")
            {
                var result = await service.BackupRecentLikesAsync(refresh, cancel.Token);
                Log($"Backup {(result.Success ? "complete" : "partly done")}: {result.NewAudioFiles} new song(s)." + (result.Error.Length > 0 ? " " + result.Error : ""));
                return result.Success ? 0 : 1;
            }
            if (args[0] == "--sync")
            {
                var prepared = await service.PrepareAsync(refresh, refresh, cancel.Token);
                foreach (var plan in prepared.Plan.Playlists.Where(p => p.ChangeCount > 0 || p.IsBlocked))
                    Log($"  {plan.Name}: Spotify +{plan.SpotifyAddIds.Count}/-{plan.SpotifyRemoveIds.Count}, local +{plan.LocalAddFiles.Count}/-{plan.LocalRemoveFiles.Count}" + (plan.IsBlocked ? " (blocked: " + plan.BlockReason + ")" : ""));
                Log($"Plan: {prepared.Plan.TotalChanges} change(s) across {prepared.Plan.Playlists.Count} playlist(s).");
                if (args.Contains("--dry-run")) return 0;
                var executed = await service.ExecuteAsync(prepared, false, cancel.Token);
                foreach (var error in executed.Errors) Log("ERROR: " + error);
                Log($"Applied: Spotify +{executed.SpotifyAdded}/-{executed.SpotifyRemoved}; local +{executed.LocalAdded}/-{executed.LocalRemoved}.");
                return executed.Errors.Count == 0 ? 0 : 1;
            }
            Log("Unknown option " + args[0] + ". Try --help.");
            return 2;
        }
        catch (OperationCanceledException) { Log("Stopped."); return 130; }
        catch (Exception ex) { Log("ERROR: " + ex.Message); return 1; }
        finally { DownloadProcesses.Reset(); }
    }
}
