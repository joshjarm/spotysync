using System.Text.RegularExpressions;
using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// Live behaviour: pausing downloads, the Console page's split view with the download graph, song names in the
/// status bar, and keeping the loaded library in step with the music folder while the app runs.
/// </summary>
public sealed partial class MainForm
{
    private readonly RoundedButton pauseButton = RoundedButton.Make("Pause", ButtonKind.Secondary, Glyph.Pause);
    private ActivityGraph? consoleGraph;

    /// <summary>Keep the loaded library in step with the music folder (off in automated UI tests with mock data).</summary>
    public bool LiveLibrary { get; set; } = true;
    private SplitContainer? consoleSplit;
    private RoundedButton? consoleSplitButton;

    // ------------------------------------------------------------------ pause
    /// <summary>A download (or another downloader process) is running and can be paused.</summary>
    private bool CanPause => cancellation is not null && (DownloadProcesses.HasRunning || DownloadProcesses.Paused || LooksLikeDownloadRun);

    private bool LooksLikeDownloadRun =>
        new[] { "Backing up", "Download", "Soundtrack", "Running complete", "missing", "Upgrading" }.Any(w => operationLabel.Contains(w, StringComparison.OrdinalIgnoreCase));

    private void TogglePause()
    {
        if (DownloadProcesses.Paused)
        {
            DownloadProcesses.Resume();
            downloadMonitor.SetPaused(false);
            AppendLog("Download resumed.");
        }
        else
        {
            if (!CanPause) return;
            DownloadProcesses.Pause();
            downloadMonitor.SetPaused(true);
            AppendLog("Download paused — press Resume to continue (nothing is lost).");
        }
        UpdatePauseButton();
    }

    private void UpdatePauseButton()
    {
        if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(UpdatePauseButton); return; }
        var paused = DownloadProcesses.Paused;
        pauseButton.Enabled = paused || CanPause;
        pauseButton.Text = paused ? "Resume" : "Pause";
        pauseButton.Glyph = paused ? Glyph.Play : Glyph.Pause;
        if (cancellation is not null) statusText.State = paused ? Led.Warn : Led.Busy;
    }

    private void InitLive()
    {
        pauseButton.Enabled = false;
        pauseButton.Click += (_, _) => TogglePause();
        tips.SetToolTip(pauseButton, "Pause the download without cancelling it (Space in full screen)");
        DownloadProcesses.StateChanged += UpdatePauseButton;
        libraryDebounce.Tick += async (_, _) => { libraryDebounce.Stop(); await RefreshLibraryLiveAsync(); };
        downloadMonitor.Lookup = LookupTrack;
        FormClosed += (_, _) =>
        {
            DownloadProcesses.StateChanged -= UpdatePauseButton;
            DownloadProcesses.Reset();
            libraryWatcher?.Dispose();
            libraryDebounce.Dispose();
        };
    }

    // ------------------------------------------------------------------ console page with the download graph beside it
    private Control BuildConsolePage()
    {
        var page = new Panel { BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 4) };
        consoleSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 14, BackColor = Color.Transparent,
            FixedPanel = FixedPanel.None, Panel2Collapsed = !settings.UiConsoleSplit
        };
        consoleSplit.Panel1.Controls.Add(console);
        var graphCard = new Card { Dock = DockStyle.Fill, Title = "Download activity", Eyebrow = "Live", Glyph = Glyph.Download, Inner = new Padding(16, 10, 16, 16) };
        consoleGraph = new ActivityGraph { Dock = DockStyle.Fill, Monitor = downloadMonitor, Range = settings.UiActivityRange };
        consoleGraph.RangeChanged += SaveActivityRange;
        consoleGraph.FullScreenRequested += OpenActivityFullScreen;
        graphCard.HeaderActions.Add(new Card.HeaderAction { Glyph = Glyph.Expand, Tip = "Full screen", Click = OpenActivityFullScreen });
        graphCard.HeaderActions.Add(new Card.HeaderAction { Glyph = Glyph.Close, Tip = "Hide the graph", Click = () => SetConsoleSplit(false) });
        graphCard.Controls.Add(consoleGraph);
        consoleSplit.Panel2.Controls.Add(graphCard);
        consoleSplit.SizeChanged += (_, _) => PlaceConsoleSplitter();
        consoleSplit.Paint += (_, e) =>
        {
            var r = consoleSplit.SplitterRectangle;
            using var grip = new SolidBrush(Theme.P.Border);
            e.Graphics.FillRectangle(grip, r.X + r.Width / 2 - 1, r.Y + r.Height / 2 - 18, 3, 36);
        };
        consoleSplitButton = RoundedButton.Make(settings.UiConsoleSplit ? "Hide graph" : "Show graph", ButtonKind.Ghost, Glyph.Split, (_, _) => SetConsoleSplit(!settings.UiConsoleSplit));
        tips.SetToolTip(consoleSplitButton, "Show the live download graph beside the console");
        console.AddToolbarControl(consoleSplitButton);
        page.Controls.Add(consoleSplit);
        return page;
    }

    private bool consoleSplitPlaced;

    private void PlaceConsoleSplitter()
    {
        if (consoleSplit is null || consoleSplit.Width < 600 || consoleSplit.Panel2Collapsed) return;
        if (consoleSplitPlaced && consoleSplit.SplitterDistance > 300 && consoleSplit.SplitterDistance < consoleSplit.Width - 300) return;
        try
        {
            consoleSplit.Panel1MinSize = 320; consoleSplit.Panel2MinSize = 320;
            consoleSplit.SplitterDistance = Math.Clamp((int)(consoleSplit.Width * 0.56), 320, consoleSplit.Width - 334);
            consoleSplitPlaced = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    private void SetConsoleSplit(bool on)
    {
        if (consoleSplit is null) return;
        settings.UiConsoleSplit = on;
        CrossFade.Run(consoleSplit, () =>
        {
            consoleSplit.Panel2Collapsed = !on;
            consoleSplitPlaced = false;
            PlaceConsoleSplitter();
            if (consoleSplitButton is not null) consoleSplitButton.Text = on ? "Hide graph" : "Show graph";
        }, 0.14);
        SaveAppearance();
    }

    // ------------------------------------------------------------------ song names for links
    private readonly Dictionary<string, TrackInfo> trackIndex = new(StringComparer.OrdinalIgnoreCase);

    private void IndexTracks(IEnumerable<TrackInfo> tracks)
    {
        foreach (var track in tracks)
            if (track.SpotifyId.Length > 0 && track.Title.Length > 0) trackIndex[track.SpotifyId] = track;
    }

    private TrackInfo? LookupTrack(string spotifyId)
    {
        if (trackIndex.Count == 0 && prepared is not null) IndexTracks(prepared.Liked.Tracks.Concat(prepared.SpotifyPlaylists.SelectMany(p => p.Tracks)));
        return trackIndex.TryGetValue(spotifyId, out var track) ? track : null;
    }

    private static readonly Regex SpotifyTrackUrl = new(@"https?://open\.spotify\.com/track/([A-Za-z0-9]{22})\S*", RegexOptions.Compiled);
    private static readonly Regex SpotDlDone = new("^Downloaded \"(.+?) - (.+?)\": (\\S+)", RegexOptions.Compiled);

    /// <summary>
    /// The status bar line for a log entry: links get the song's title and artist(s) in front, e.g.
    /// "Processing query: Song — Artist  ·  https://open.spotify.com/track/…".
    /// </summary>
    private string StatusLine(LogEntry entry)
    {
        var text = entry.Text;
        var done = SpotDlDone.Match(text);
        if (done.Success) text = $"Downloaded: {done.Groups[2].Value} — {done.Groups[1].Value}  ·  {done.Groups[3].Value}";
        else
        {
            var link = SpotifyTrackUrl.Match(text);
            if (link.Success && LookupTrack(link.Groups[1].Value) is { } track)
                text = text[..link.Index] + DownloadMonitor.TrackText(track) + "  ·  " + text[link.Index..];
        }
        return (entry.Tag.Length > 0 ? entry.Tag.ToLowerInvariant() + " · " : "") + text + (entry.Repeat > 1 ? $"  ×{entry.Repeat}" : "");
    }

    // ------------------------------------------------------------------ library kept up to date
    private FileSystemWatcher? libraryWatcher;
    private string watchedLibraryFolder = "";
    private readonly System.Windows.Forms.Timer libraryDebounce = new() { Interval = 2500 };
    private bool libraryRefreshRunning, libraryRefreshAgain;

    /// <summary>Watches the music folder: new, renamed or deleted songs update the loaded library within seconds.</summary>
    private void EnsureLibraryWatcher()
    {
        if (!LiveLibrary) return;
        var folder = settings.ResolvePath(settings.MusicFolder);
        if (folder.Equals(watchedLibraryFolder, StringComparison.OrdinalIgnoreCase) && libraryWatcher is not null) return;
        libraryWatcher?.Dispose(); libraryWatcher = null; watchedLibraryFolder = folder;
        if (!Directory.Exists(folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, SynchronizingObject = this,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            void Changed(string path) { if (LibraryScanner.IsAudioFile(path) && !LibraryScanner.IsPartialDownload(path) || !Path.HasExtension(path)) QueueLibraryRefresh(); }
            watcher.Created += (_, e) => Changed(e.FullPath);
            watcher.Deleted += (_, e) => Changed(e.FullPath);
            watcher.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
            watcher.Error += (_, _) => QueueLibraryRefresh();
            watcher.EnableRaisingEvents = true;
            libraryWatcher = watcher;
        }
        catch { /* without a watcher the library still refreshes after each run and on Analyse */ }
    }

    private void QueueLibraryRefresh()
    {
        if (prepared is null) return;
        libraryDebounce.Stop();
        libraryDebounce.Interval = cancellation is null ? 2500 : 6000; // calmer while a download is writing files
        libraryDebounce.Start();
    }

    /// <summary>
    /// Compares the loaded library with the music folder on a worker thread and applies the difference: the
    /// Library page, its dashboard counts and the sync preview update in place. Returns the number of changes.
    /// </summary>
    private async Task<int> RefreshLibraryLiveAsync()
    {
        if (prepared is null || !LiveLibrary) return 0;
        if (libraryRefreshRunning) { libraryRefreshAgain = true; return 0; }
        libraryRefreshRunning = true;
        try
        {
            var snapshot = prepared;
            var planBefore = snapshot.Plan;
            var rebuild = planAppliedAt is null;
            var library = snapshot.Library.ToList();
            var update = await Task.Run(async () =>
            {
                var (added, removed) = await service.CompareLibraryWithDiskAsync(library);
                if (added.Count == 0 && removed.Count == 0) return null;
                var copy = new PreparedSync { Plan = planBefore, Library = library, SpotifyPlaylists = snapshot.SpotifyPlaylists, LocalPlaylists = snapshot.LocalPlaylists, Liked = snapshot.Liked };
                service.ApplyLibraryChanges(copy, added, removed, rebuild);
                return new { copy.Library, copy.Plan, Added = added.Count, Removed = removed.Count };
            });
            if (update is null || !ReferenceEquals(snapshot, prepared)) return 0;
            prepared.Library = update.Library;
            if (rebuild && ReferenceEquals(prepared.Plan, planBefore) && planAppliedAt is null)
            {
                prepared.Plan = update.Plan;
                var selected = planGrid.CurrentRow?.Tag as PlaylistPlan;
                PopulatePlan(prepared.Plan, selected?.Name);
            }
            if (libraryReport is not null) BuildLibraryReport(quiet: true);
            SavePreviewSnapshot();
            RefreshDashboard();
            AppendLog($"Library updated: {update.Added} new song(s)" + (update.Removed > 0 ? $", {update.Removed} removed" : "") +
                      (libraryReport is not null ? " — the Library page is up to date." : "."));
            return update.Added + update.Removed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            WriteLogFile("Library refresh skipped: " + ex.Message);
            return 0;
        }
        finally
        {
            libraryRefreshRunning = false;
            if (libraryRefreshAgain) { libraryRefreshAgain = false; QueueLibraryRefresh(); }
        }
    }
}
