using System.Runtime.InteropServices;
using SpotySync;

namespace SpotySyncApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var settings = PortableSettings.Load(AppContext.BaseDirectory);
        RememberCompanionPath();
        if (args.Length > 0)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            if (GetConsoleWindow() == IntPtr.Zero) AllocConsole();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            Environment.ExitCode = RunCommandLineAsync(args, settings).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, eventArgs) => ShowFatal(eventArgs.Exception, settings);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => ShowFatal(eventArgs.ExceptionObject as Exception ?? new Exception("Unknown fatal error"), settings);
        Application.Run(new MainForm(settings));
    }

    private static async Task<int> RunCommandLineAsync(string[] args, PortableSettings settings)
    {
        if (args.Any(x => x is "--help" or "-h" or "/?"))
        {
            Console.WriteLine("SpotySync command line\n\n  --backup       Download recent Liked Songs\n  --sync         Preview and apply playlist sync\n  --all          Backup, then sync\n  --dry-run      Preview only; do not change playlists\n  --refresh      Force full Spotify/cache refresh\n  --settings     Open SpotySync.ini\n");
            return 0;
        }
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(settings.FilePath) { UseShellExecute = true });
            return 0;
        }
        var logPath = Path.Combine(settings.LogDirectory, "SpotySync-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        Directory.CreateDirectory(settings.LogDirectory);
        void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            Console.WriteLine(line);
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine);
        }
        try
        {
            var backup = args.Contains("--backup", StringComparer.OrdinalIgnoreCase) || args.Contains("--all", StringComparer.OrdinalIgnoreCase);
            var sync = args.Contains("--sync", StringComparer.OrdinalIgnoreCase) || args.Contains("--all", StringComparer.OrdinalIgnoreCase);
            var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
            var refresh = args.Contains("--refresh", StringComparer.OrdinalIgnoreCase);
            if (!backup && !sync) { Console.Error.WriteLine("Specify --backup, --sync, or --all. Use --help for details."); return 2; }
            using var service = new SpotySyncService(settings, Log);
            if (backup)
            {
                var result = await service.BackupRecentLikesAsync(refresh);
                if (!result.Success) { Console.Error.WriteLine(result.Error); return 1; }
            }
            if (sync)
            {
                var prepared = await service.PrepareAsync(refresh, refresh);
                foreach (var plan in prepared.Plan.Playlists)
                    Log($"PLAN {plan.Name}: Spotify +{plan.SpotifyAddIds.Count}/-{plan.SpotifyRemoveIds.Count}; local +{plan.LocalAddFiles.Count}/-{plan.LocalRemoveFiles.Count}; protected {plan.PlatformSpecific.Count}{(plan.IsBlocked ? "; BLOCKED " + plan.BlockReason : "")}");
                var executed = await service.ExecuteAsync(prepared, dryRun);
                if (!executed.Success) { foreach (var error in executed.Errors) Console.Error.WriteLine(error); return 1; }
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RememberCompanionPath()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBeeSyncToService");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "CompanionPath.txt"), Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch { }
    }

    private static void ShowFatal(Exception exception, PortableSettings settings)
    {
        try
        {
            Directory.CreateDirectory(settings.LogDirectory);
            File.AppendAllText(Path.Combine(settings.LogDirectory, "crash.log"), DateTime.Now.ToString("O") + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine);
        }
        catch { }
        MessageBox.Show(exception.Message + "\r\n\r\nDetails were written to Logs\\crash.log.", "SpotySync", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
}
