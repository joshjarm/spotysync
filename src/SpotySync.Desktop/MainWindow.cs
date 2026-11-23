using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SpotySync;

namespace SpotySyncDesktop;

/// <summary>
/// The Linux window. Every engine call runs on a worker thread (Task.Run), so the window never freezes; the engine's
/// log lines arrive from any thread and are added to the console in batches.
/// </summary>
public sealed partial class MainWindow : Window
{
    private PortableSettings settings;
    private SpotySyncService service;
    private CancellationTokenSource? cancellation;
    private PreparedSync? prepared;
    private DateTime? previewRestoredAt;
    private DateTime? planAppliedAt;
    private LibraryReport? report;

    private readonly ContentControl pageHost = new();
    private readonly List<(Button Button, Func<Control> Page, string Name)> navItems = new();
    private readonly List<Control> busyControls = new();
    private readonly TextBlock statusText = new() { Foreground = Pal.Muted, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Width = 120, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button pauseButton = new() { Content = "Pause", IsVisible = false };
    private readonly Button stopButton = new() { Content = "Stop", IsEnabled = false };
    private readonly Slider speedSlider = new() { Minimum = 0, Maximum = DownloadSpeed.Levels.Length - 1, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock speedLabel = new() { Foreground = Pal.Muted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Width = 120 };
    private string currentPage = "Home";
    private int downloadedThisRun;

    public MainWindow()
    {
        settings = PortableSettings.Load();
        DownloadSpeed.Load(settings);
        service = new SpotySyncService(settings, AppendLog);
        Title = "SpotySync";
        Width = 1240; Height = 820; MinWidth = 900; MinHeight = 600;
        Background = Pal.Background;
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "SpotySync.png");
            if (File.Exists(icon)) Icon = new WindowIcon(icon);
        }
        catch { }

        Content = BuildShell();
        logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushLog());
        logTimer.Start();
        DownloadProcesses.StateChanged += () => Dispatcher.UIThread.Post(UpdatePauseButton);
        DownloadSpeed.Changed += _ => Dispatcher.UIThread.Post(UpdateSpeed);
        UpdateSpeed();
        ShowPage("Home");
        if (VersionCarryOver.CarriedFrom is not null)
            AppendLog($"New version: took over your settings, Spotify login and caches from {VersionCarryOver.CarriedFrom} ({VersionCarryOver.FilesCopied} file(s)).");
        AppendLog($"SpotySync for Linux. Music folder: {settings.ResolvePath(settings.MusicFolder)}; playlists (M3U) go to {settings.ResolvePath(settings.M3uFolder)}.");
        _ = RestorePreviewAsync();
        Closing += (_, _) =>
        {
            cancellation?.Cancel();
            DownloadProcesses.Reset(); // never leave a paused downloader behind
            FlushLog();
        };
    }

    // ------------------------------------------------------------------ shell
    private Control BuildShell()
    {
        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(12, 18, 12, 12) };
        nav.Children.Add(new TextBlock
        {
            Text = "SpotySync", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Pal.Accent, Margin = new Thickness(10, 0, 0, 18)
        });
        void Add(string name, Func<Control> page)
        {
            var button = new Button
            {
                Content = name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Foreground = Pal.Muted,
                FontSize = 14, FontWeight = FontWeight.SemiBold
            };
            button.Click += (_, _) => ShowPage(name);
            navItems.Add((button, page, name));
            nav.Children.Add(button);
        }
        Add("Home", BuildHomePage);
        Add("Sync", BuildSyncPage);
        Add("Library", BuildLibraryPage);
        Add("Download", BuildDownloadPage);
        Add("Console", BuildConsolePage);
        Add("Settings", BuildSettingsPage);

        var sidebar = new Border { Background = Pal.Surface, Child = nav, BorderBrush = Pal.Line, BorderThickness = new Thickness(0, 0, 1, 0) };

        pauseButton.Click += (_, _) => { if (DownloadProcesses.Paused) DownloadProcesses.Resume(); else DownloadProcesses.Pause(); };
        stopButton.Click += (_, _) => { cancellation?.Cancel(); AppendLog("Stopping…"); };
        speedSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || updatingSpeed) return;
            DownloadSpeed.Set((int)Math.Round(speedSlider.Value), byUser: true);
            DownloadSpeed.Store(settings);
            try { settings.Save(); } catch { }
        };
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(16, 8) };
        status.Children.Add(statusText);
        Place(progress, 1); Place(new TextBlock { Text = "Speed", Foreground = Pal.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) }, 2);
        Place(speedSlider, 3); Place(speedLabel, 4); Place(Ui.Row(8, pauseButton, stopButton), 5);
        void Place(Control control, int column) { Grid.SetColumn(control, column); status.Children.Add(control); }
        var statusBar = new Border { Background = Pal.Surface, BorderBrush = Pal.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = status };

        var main = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        main.Children.Add(statusBar);
        main.Children.Add(pageHost);

        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        shell.Children.Add(sidebar);
        Grid.SetColumn(main, 1);
        shell.Children.Add(main);
        statusText.Text = service.HasSavedLogin ? "Ready." : "Spotify isn't connected yet: open Settings, then Connect Spotify.";
        return shell;
    }

    private void ShowPage(string name)
    {
        currentPage = name;
        busyControls.Clear();
        foreach (var item in navItems)
        {
            var selected = item.Name == name;
            item.Button.Background = selected ? Pal.Card : Brushes.Transparent;
            item.Button.Foreground = selected ? Pal.Ink : Pal.Muted;
        }
        var page = navItems.First(n => n.Name == name).Page();
        pageHost.Content = name == "Console" ? page : new ScrollViewer { Content = new Border { Padding = new Thickness(28, 24), Child = page } };
        SetBusyControls(cancellation is not null);
    }

    private void Refresh() => ShowPage(currentPage);

    /// <summary>A button that is disabled while something is running.</summary>
    private Button Action(string text, Func<Task> onClick, bool primary = false)
    {
        var button = Ui.Button(text, () => _ = onClick(), primary);
        busyControls.Add(button);
        return button;
    }

    private void SetBusyControls(bool busy)
    {
        foreach (var control in busyControls) control.IsEnabled = !busy;
    }

    private void UpdatePauseButton()
    {
        pauseButton.IsVisible = cancellation is not null && (DownloadProcesses.HasRunning || DownloadProcesses.Paused);
        pauseButton.Content = DownloadProcesses.Paused ? "Resume" : "Pause";
    }

    private bool updatingSpeed;

    private void UpdateSpeed()
    {
        updatingSpeed = true;
        speedSlider.Value = DownloadSpeed.Index;
        updatingSpeed = false;
        speedLabel.Text = DownloadSpeed.Current.Label;
    }

    // ------------------------------------------------------------------ running engine work
    private async Task RunBusyAsync(string label, Func<CancellationToken, Task> operation)
    {
        if (cancellation is not null) return;
        cancellation = new CancellationTokenSource();
        downloadedThisRun = 0;
        SetBusyControls(true);
        progress.IsVisible = true;
        stopButton.IsEnabled = true;
        statusText.Text = label + "…";
        statusText.Foreground = Pal.Muted;
        AppendLog("── " + label + "…");
        UpdatePauseButton();
        var outcome = "complete";
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); outcome = "failed"; }
        finally
        {
            DownloadProcesses.Reset();
            cancellation.Dispose();
            cancellation = null;
            progress.IsVisible = false;
            stopButton.IsEnabled = false;
            SetBusyControls(false);
            UpdatePauseButton();
            AppendLog($"── {label} {outcome}.");
            if (outcome != "complete") SetResult($"{label} {outcome}.", outcome == "failed" ? Pal.Err : Pal.Warn);
        }
        if (downloadedThisRun > 0) await RefreshLibraryAsync();
    }

    private void SetResult(string text, IBrush? color = null)
    {
        statusText.Text = text;
        statusText.Foreground = color ?? Pal.Ok;
    }

    /// <summary>After downloads: re-read the music folder so the Library and the preview show the new songs.</summary>
    private async Task RefreshLibraryAsync()
    {
        if (prepared is null) return;
        var snapshot = prepared;
        try
        {
            var result = await Task.Run<(PreparedSync Copy, LibraryReport Report, int Added, int Removed)?>(async () =>
            {
                var library = snapshot.Library.ToList();
                var (added, removed) = await service.CompareLibraryWithDiskAsync(library);
                if (added.Count + removed.Count == 0) return null;
                var copy = new PreparedSync { Plan = snapshot.Plan, Library = library, SpotifyPlaylists = snapshot.SpotifyPlaylists, LocalPlaylists = snapshot.LocalPlaylists, Liked = snapshot.Liked };
                service.ApplyLibraryChanges(copy, added, removed, planAppliedAt is null);
                return (Copy: copy, Report: LibraryInsights.Build(copy), Added: added.Count, Removed: removed.Count);
            });
            if (result is null || !ReferenceEquals(snapshot, prepared)) return;
            prepared = result.Value.Copy;
            report = result.Value.Report;
            SavePreview();
            AppendLog($"Library updated: {result.Value.Added} new song(s)" + (result.Value.Removed > 0 ? $", {result.Value.Removed} removed." : "."));
            if (currentPage is "Library" or "Sync" or "Home") Refresh();
        }
        catch (Exception ex) { WriteLogFile("Library refresh skipped: " + ex.Message); }
    }

    // ------------------------------------------------------------------ last preview (same file as on Windows)
    private sealed class PreviewSnapshot
    {
        public DateTime BuiltAt { get; set; }
        public DateTime? AppliedAt { get; set; }
        public bool LibraryAnalysed { get; set; }
        public PreparedSync? Prepared { get; set; }
    }

    private static readonly JsonSerializerOptions SnapshotJson = new() { IncludeFields = true };
    private string PreviewPath => Path.Combine(settings.DataDirectory, "last-preview.json");
    private DateTime previewBuiltAt;

    private async Task RestorePreviewAsync()
    {
        var path = PreviewPath;
        var snapshot = await Task.Run<(PreviewSnapshot Snapshot, LibraryReport Report)?>(() =>
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize<PreviewSnapshot>(stream, SnapshotJson);
                if (loaded?.Prepared is null) return null;
                return (Snapshot: loaded, Report: LibraryInsights.Build(loaded.Prepared));
            }
            catch { return null; }
        });
        if (snapshot is null || prepared is not null) return;
        prepared = snapshot.Value.Snapshot.Prepared;
        report = snapshot.Value.Report;
        previewBuiltAt = snapshot.Value.Snapshot.BuiltAt; previewRestoredAt = previewBuiltAt;
        planAppliedAt = snapshot.Value.Snapshot.AppliedAt;
        AppendLog($"Restored the sync preview from {previewBuiltAt:ddd d MMM HH:mm}. Build a new one before applying changes.");
        if (currentPage is "Home" or "Sync" or "Library") Refresh();
        _ = RefreshLibraryAsync();
    }

    private void SavePreview()
    {
        if (prepared is null) return;
        var snapshot = new PreviewSnapshot { BuiltAt = previewBuiltAt, AppliedAt = planAppliedAt, LibraryAnalysed = true, Prepared = prepared };
        var path = PreviewPath;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, snapshot, SnapshotJson);
                File.Move(temp, path, true);
            }
            catch { }
        });
    }

    // ------------------------------------------------------------------ log
    public sealed record LogLine(string Time, string Text, IBrush Color);

    private readonly ConcurrentQueue<(DateTime At, string Text)> pendingLog = new();
    private readonly ObservableCollection<LogLine> logLines = new();
    private readonly DispatcherTimer logTimer;
    private const int MaxLogLines = 5000;

    /// <summary>Called by the engine from any thread.</summary>
    private void AppendLog(string message) => pendingLog.Enqueue((DateTime.Now, message));

    private void FlushLog()
    {
        if (pendingLog.IsEmpty) return;
        var fileLines = new List<string>();
        while (pendingLog.TryDequeue(out var item))
        {
            fileLines.Add(item.At.ToString("yyyy-MM-dd HH:mm:ss") + "  " + item.Text);
            if (item.Text.StartsWith("download | Downloaded \"", StringComparison.Ordinal) || item.Text.StartsWith("Downloaded \"", StringComparison.Ordinal)) downloadedThisRun++;
            var line = new LogLine(item.At.ToString("HH:mm:ss"), item.Text, ColorFor(item.Text));
            logLines.Add(line);
            if (LogFilterPasses(line)) visibleLog.Add(line);
            if (cancellation is not null && !item.Text.StartsWith("download |", StringComparison.Ordinal) && !item.Text.StartsWith("──"))
                statusText.Text = item.Text.Length > 160 ? item.Text[..160] + "…" : item.Text;
        }
        if (cancellation is not null && downloadedThisRun > 0 && statusText.Text?.StartsWith("Downloaded ") != false)
            statusText.Text = $"Downloaded {downloadedThisRun} song(s) so far…";
        while (logLines.Count > MaxLogLines) logLines.RemoveAt(0);
        while (visibleLog.Count > MaxLogLines) visibleLog.RemoveAt(0);
        if (followLog && visibleLog.Count > 0) logList?.ScrollIntoView(visibleLog[^1]);
        WriteLogFile(fileLines);
    }

    private static IBrush ColorFor(string text)
    {
        var t = text.TrimStart();
        if (t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || t.Contains("Error occurred", StringComparison.Ordinal) || t.Contains("AudioProviderError")) return Pal.Err;
        if (t.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) || t.Contains("Not on YouTube Music") || t.StartsWith("YouTube rate limit")) return Pal.Warn;
        if (t.StartsWith("──")) return Pal.Accent;
        if (t.StartsWith("download |", StringComparison.Ordinal)) return t.Contains("Downloaded \"") ? Pal.Ok : Pal.Dim;
        return Pal.Ink;
    }

    private void WriteLogFile(string line) => WriteLogFile(new List<string> { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line });

    private void WriteLogFile(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(settings.LogDirectory);
            File.AppendAllLines(Path.Combine(settings.LogDirectory, "SpotySync-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), lines);
        }
        catch { }
    }
}
