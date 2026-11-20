using System.Diagnostics;
using System.Reflection;
using SpotySync;

namespace SpotySyncApp;

public sealed partial class MainForm : Form
{
    internal const int PageDashboard = 0, PagePreview = 1, PageLibrary = 2, PageConsole = 3, PageSettings = 4;

    private PortableSettings settings;
    private SpotySyncService service;
    private PreparedSync? prepared;
    private CancellationTokenSource? cancellation;
    private DateTime operationStarted;
    private string operationLabel = "";

    private readonly Sidebar sidebar;
    private readonly GlowHost content = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 16, 28, 14) };
    private readonly Panel pageHost = new() { Dock = DockStyle.Fill };
    private readonly List<PageInfo> pages = new();
    private int currentPage = -1;

    private readonly PageHeader header = new() { Dock = DockStyle.Top, Height = 76 };
    private readonly StatusPill spotifyStatus = new() { Text = "Spotify: checking…", ShortText = "Spotify" };
    private readonly StatusPill musicBeeStatus = new() { Text = "MusicBee: checking…", ShortText = "MusicBee" };
    private readonly StatusPill safetyStatus = new() { Text = "Safety: add-only", ShortText = "Safety" };
    private readonly StatusPill toolsStatus = new() { Text = "Tools: checking…", ShortText = "Tools" };

    private readonly StatusText statusText = new();
    private readonly SoftProgressBar progress = new() { Dock = DockStyle.Fill, Margin = new Padding(8, 24, 18, 24) };
    private readonly ToggleSwitch forceRefresh = new() { Text = "Force full refresh" };
    private readonly RoundedButton cancelButton = RoundedButton.Make("Cancel", ButtonKind.Secondary, Glyph.Stop);
    private readonly RoundedButton applyButton = RoundedButton.Make("Apply reviewed plan", ButtonKind.Primary, Glyph.Check);
    private readonly ConsoleView console;
    private readonly ToolTip tips = Ui.ToolTip();
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 8000 }; // MusicBee may start/stop any time

    /// <summary>Ask questions at startup (off for automated UI tests, which must never show dialogs).</summary>
    public bool StartupPrompts { get; set; } = true;

    private string musicBeeState = "checking";
    private int likedIndexed = -1;
    private DateTimeOffset likedRefreshed;

    private sealed record PageInfo(NavItem Nav, Control Page, string Eyebrow, string Title, string Subtitle);

    public MainForm(PortableSettings initialSettings)
    {
        settings = initialSettings;
        Theme.Apply(settings.UiTheme, settings.UiAccent, settings.UiFont, settings.UiRoundedCorners, settings.UiBackgroundGlow, settings.UiAnimations);
        service = new SpotySyncService(settings, AppendLog);
        console = new ConsoleView(settings.UiConsoleDownloaderDetail, settings.UiConsoleTimestamps, settings.UiConsoleSimple) { Dock = DockStyle.Fill, LogDirectory = settings.LogDirectory };
        sidebar = new Sidebar(AppVersion) { Dock = DockStyle.Left };
        Text = "SpotySync — Spotify, MusicBee & M3U";
        MinimumSize = new Size(1100, 720);
        Size = new Size(1340, 870);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Fonts.Body();
        KeyPreview = true;
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Ui.StyleWindow(this);
        BuildUi();
        LoadSettingsFields();
        ApplyThemeColors();
        Theme.Changed += OnThemeChanged;
        Shown += async (_, _) =>
        {
            // leftover from the 1.5.0-T3 embedded-browser experiment
            try { var web = Path.Combine(settings.DataDirectory, "WebView2"); if (Directory.Exists(web)) Directory.Delete(web, true); } catch { }
            RestorePreviewSnapshot();
            await RefreshStatusAsync();
            if (StartupPrompts) await OfferExistingSetupAsync(userInitiated: false);
            statusTimer.Tick += async (_, _) => { if (cancellation is null) await RefreshMusicBeePillAsync(); };
            statusTimer.Start();
        };
        FormClosed += (_, _) => { cancellation?.Cancel(); service.Dispose(); Theme.Changed -= OnThemeChanged; clock.Dispose(); statusTimer.Dispose(); downloadMonitor.Dispose(); };
        clock.Tick += (_, _) => UpdateStatusClock();
    }

    internal static string AppVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info.Length > 0 ? info : "1.5";
        }
    }

    private readonly WheelRouter wheelRouter = new();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(wheelRouter); // scroll whatever is under the mouse, not whatever has focus
        // Launch flash: Windows shows a new window in a default (white) colour for a frame before it paints.
        // Keep it cloaked (invisible to the compositor) until the first real frame is painted in OnShown.
        SetCloak(true);
    }

    protected override void OnShown(EventArgs e)
    {
        try { Update(); } catch { }
        SetCloak(false);
        base.OnShown(e);
    }

    private void SetCloak(bool on)
    {
        if (!IsHandleCreated) return;
        try { var value = on ? 1 : 0; DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref value, sizeof(int)); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(wheelRouter);
        base.OnHandleDestroyed(e);
    }

    // Smooth live resizing: suspend relayout of the content while the user drags the window edge.
    protected override void OnResizeBegin(EventArgs e) { content.SuspendLayout(); base.OnResizeBegin(e); }
    protected override void OnResizeEnd(EventArgs e) { content.ResumeLayout(true); base.OnResizeEnd(e); }

    // ------------------------------------------------------------------ shell
    private void BuildUi()
    {
        SuspendLayout();
        content.Controls.Add(pageHost);
        content.Controls.Add(BuildHeader());
        header.SendToBack();
        AddPage("Dashboard", Glyph.Home, BuildDashboard(), "Overview", "Dashboard", "Back up your Likes, preview playlist changes and keep everything in sync.");
        AddPage("Sync preview", Glyph.Sync, BuildPreview(), "Review before applying", "Sync preview", "Every change SpotySync would make, playlist by playlist. Nothing changes until you apply.");
        AddPage("Library", Glyph.Library, BuildLibrary(), "Library tools", "Library", "Songs in no playlist, Likes you haven't downloaded, Likes in no playlist and duplicate files.");
        AddPage("Console", Glyph.Console, BuildConsolePage(), "Activity", "Console", "What SpotySync is doing, grouped by run. Full logs are kept in the Logs folder.");
        AddPage("Settings", Glyph.Settings, BuildSettings(), "Preferences", "Settings", "Accounts, folders, sync safety, downloads and the look of the app.");
        sidebar.Toggled += () => { settings.UiCompactNavigation = sidebar.Mini; SaveAppearance(); };
        sidebar.ThemeClicked += () => { SelectPage(PageSettings); settingsView.ShowGroup("appearance"); };
        sidebar.RandomClicked += () => ApplyRandomTheme();
        sidebar.Mini = settings.UiCompactNavigation;
        Controls.Add(content);
        Controls.Add(BuildStatusBar());
        Controls.Add(sidebar);
        console.Appended += OnLogAppended;
        console.CountsChanged += UpdateConsoleBadge;
        console.SimpleChanged += simple =>
        {
            settings.UiConsoleSimple = simple;
            loadingFields = true; consoleSimpleDefault.Checked = simple; loadingFields = false;
            miniFeedDirty = true;
            SaveAppearance();
        };
        downloadMonitor.FileArrived += name =>
        {
            // only download-type runs report songs (MusicBee may move files around during other runs)
            var downloading = new[] { "Backing up", "Download", "Soundtrack", "Running complete", "missing" }.Any(w => operationLabel.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (downloading) AppendLog("Downloaded: " + name + (downloadMonitor.Target is { } t ? $"   {downloadMonitor.Files}/{t}" : ""));
        };
        console.PreferencesChanged += (detail, times) =>
        {
            if (settings.UiConsoleDownloaderDetail == detail && settings.UiConsoleTimestamps == times) return;
            settings.UiConsoleDownloaderDetail = detail; settings.UiConsoleTimestamps = times;
            consoleDetailDefault.Checked = detail; consoleTimesDefault.Checked = times;
            SaveAppearance();
        };
        ResumeLayout(true);
        SelectPage(PageDashboard);
    }

    private void AddPage(string navText, string glyph, Control page, string eyebrow, string title, string subtitle)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        pageHost.Controls.Add(page);
        var nav = new NavItem { Text = navText, Glyph = glyph };
        var index = pages.Count;
        nav.Click += (_, _) => SelectPage(index);
        tips.SetToolTip(nav, $"{navText}   (Ctrl+{index + 1})");
        sidebar.AddNav(nav);
        pages.Add(new PageInfo(nav, page, eyebrow, title, subtitle));
    }

    private void SelectPage(int index)
    {
        if (index < 0 || index >= pages.Count || index == currentPage) { if (index == currentPage && index >= 0) RefreshCurrentPage(index); return; }
        CrossFade.Run(content, () => SwitchPage(index), 0.12);
    }

    private void RefreshCurrentPage(int index)
    {
        if (index == PageDashboard) RefreshDashboard();
    }

    private void SwitchPage(int index)
    {
        currentPage = index;
        SuspendLayout();
        for (var i = 0; i < pages.Count; i++)
        {
            pages[i].Nav.Selected = i == index;
            pages[i].Page.Visible = i == index;
        }
        pages[index].Page.BringToFront();
        header.SetText(pages[index].Eyebrow.ToUpperInvariant(), pages[index].Title, pages[index].Subtitle);
        layoutPills?.Invoke();
        ResumeLayout(true);
        if (index == PageConsole) { console.UnseenIssues = 0; UpdateConsoleBadge(); }
        if (index == PageSettings) settingsView.Relayout();
        if (index == PageDashboard) { RefreshDashboard(); if (miniFeedDirty) RenderMiniFeed(); }
    }

    private Control BuildHeader()
    {
        var pills = new[] { spotifyStatus, musicBeeStatus, toolsStatus, safetyStatus };
        tips.SetToolTip(spotifyStatus, "Spotify login. Click for Spotify account settings.");
        tips.SetToolTip(musicBeeStatus, "MusicBee bridge plugin status. Click for MusicBee settings.");
        tips.SetToolTip(safetyStatus, "Whether playlist sync may remove tracks. Click to change.");
        tips.SetToolTip(toolsStatus, "Downloader, ffprobe and music folder. Click for folder & tool settings.");
        foreach (var pill in pills) { pill.Cursor = Cursors.Hand; header.Controls.Add(pill); pill.TextChanged += (_, _) => LayoutPills(); }
        spotifyStatus.Click += (_, _) => { if (!service.HasSavedLogin && string.IsNullOrWhiteSpace(settings.SpotifyClientId)) OpenSetupAssistant(); else GoToSetting("spotify"); };
        musicBeeStatus.Click += async (_, _) =>
        {
            if (musicBeeState.Contains("plugin")) await RunBusyAsync("Updating the MusicBee plugin", _ => UpdateMusicBeePluginAsync(force: true));
            else GoToSetting("musicbee");
        };
        safetyStatus.Click += (_, _) => GoToSetting("removal");
        toolsStatus.Click += (_, _) => GoToSetting(toolsQuery);
        header.Resize += (_, _) => LayoutPills();
        layoutPills = LayoutPills;
        return header;

        void LayoutPills()
        {
            toolsStatus.Visible = toolsStatus.State != Led.Ok;
            safetyStatus.Visible = settings.EnableRemovals;
            var shown = pills.Where(p => p.Visible).ToArray();
            foreach (var pill in pills) pill.Compact = false;
            var room = header.Width - header.TitleRight;
            if (shown.Sum(p => p.Width + 6) > room) foreach (var pill in pills) pill.Compact = true;
            var x = header.Width;
            foreach (var pill in shown.Reverse())
            {
                x -= pill.Width;
                pill.SetBounds(x, 8, pill.Width, 30);
                x -= 6;
            }
        }
    }

    private string toolsQuery = "folder";
    private Action? layoutPills;

    private Control BuildStatusBar()
    {
        var bar = new StatusBarPanel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(22, 0, 22, 0) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusText.Dock = DockStyle.Fill; statusText.Margin = new Padding(0, 10, 8, 10);
        statusText.Click += (_, _) => SelectPage(PageConsole);
        tips.SetToolTip(statusText, "Click to open the Console");
        forceRefresh.Anchor = AnchorStyles.Left; forceRefresh.Width = forceRefresh.GetPreferredSize(Size.Empty).Width; forceRefresh.Margin = new Padding(12, 0, 12, 0);
        tips.SetToolTip(forceRefresh, "Ignore Spotify and library caches for the next backup or preview (slower, but re-reads everything).");
        cancelButton.Dock = DockStyle.Fill; cancelButton.Margin = new Padding(4, 15, 4, 15); cancelButton.Enabled = false; cancelButton.AutoSize = false;
        cancelButton.Click += (_, _) => cancellation?.Cancel();
        applyButton.Dock = DockStyle.Fill; applyButton.Margin = new Padding(4, 15, 0, 15); applyButton.Enabled = false; applyButton.AutoSize = false;
        applyButton.Click += async (_, _) => await ApplyAsync();
        tips.SetToolTip(applyButton, "Apply exactly the reviewed preview (Ctrl+Enter)");
        grid.Controls.Add(statusText, 0, 0); grid.Controls.Add(progress, 1, 0); grid.Controls.Add(forceRefresh, 2, 0);
        grid.Controls.Add(cancelButton, 3, 0);
        bar.Controls.Add(grid);
        return bar;
    }

    private Control BuildConsolePage()
    {
        var page = new Panel { BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 4) };
        page.Controls.Add(console);
        return page;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.D1: SelectPage(0); return true;
            case Keys.Control | Keys.D2: SelectPage(1); return true;
            case Keys.Control | Keys.D3: SelectPage(2); return true;
            case Keys.Control | Keys.D4: SelectPage(3); return true;
            case Keys.Control | Keys.D5: SelectPage(4); return true;
            case Keys.Control | Keys.Oemcomma: SelectPage(PageSettings); return true;
            case Keys.Control | Keys.F:
                if (currentPage == PageSettings) { settingsView.Search.Inner.Focus(); settingsView.Search.Inner.SelectAll(); }
                else if (currentPage == PageConsole) console.FocusSearch();
                else if (currentPage == PagePreview) { previewSearch.Inner.Focus(); previewSearch.Inner.SelectAll(); }
                else if (currentPage == PageLibrary) { librarySearch.Inner.Focus(); librarySearch.Inner.SelectAll(); }
                else { SelectPage(PageSettings); settingsView.Search.Inner.Focus(); }
                return true;
            case Keys.Control | Keys.S:
                if (settingsDirty) { SaveSettingsFields(true); return true; }
                break;
            case Keys.F5: _ = PreviewAsync(); return true;
            case Keys.Control | Keys.Enter: if (applyButton.Enabled) _ = ApplyAsync(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------------ theme
    private void OnThemeChanged()
    {
        Font = Fonts.Body();
        ApplyThemeColors();
    }

    private void ApplyThemeColors()
    {
        SuspendLayout();
        BackColor = Theme.P.Bg; ForeColor = Theme.P.Ink;
        content.BackColor = Theme.P.Bg;
        Ui.StyleWindow(this);
        Ui.Restyle(this);
        StyleGrid();
        StyleLibraryGrid();
        dashPlanStale = true;
        if (prepared is not null) { var selected = planGrid.CurrentRow?.Tag as PlaylistPlan; PopulatePlan(prepared.Plan, selected?.Name); }
        else ShowSelectedDetails();
        RenderMiniFeed();
        UpdateSafetyPill();
        swatches.SelectedId = settings.UiTheme;
        UpdateAccentSwatch();
        ResumeLayout(true);
        Invalidate(true);
    }

    private void SaveAppearance()
    {
        try { settings.Save(); } catch (Exception ex) { AppendLog("WARNING: could not save appearance settings: " + ex.Message); }
    }

    private void ApplyRandomTheme()
    {
        var pool = ThemeCatalog.RandomPool.Concat(ThemeCatalog.All).Where(t => t.Id != settings.UiTheme).ToList();
        var pick = pool[Random.Shared.Next(pool.Count)];
        settings.UiTheme = pick.Id; settings.UiAccent = "";
        ReapplyTheme();
        AppendLog($"Theme changed to {pick.Name}.");
    }

    private void ReapplyTheme()
    {
        CrossFade.Run(this, () => Theme.Apply(settings.UiTheme, settings.UiAccent, settings.UiFont, settings.UiRoundedCorners, settings.UiBackgroundGlow, settings.UiAnimations), 0.2);
        sidebar.ThemeName = ThemeCatalog.Find(settings.UiTheme).Name;
        SaveAppearance();
    }

    // ------------------------------------------------------------------ operations
    private async Task ConnectAsync()
    {
        if (!SaveSettingsFields(false)) return;
        await RunBusyAsync("Connecting Spotify", async token =>
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
            _ = HintIfLoginStallsAsync(stall.Token);
            string accepted;
            try { accepted = await service.ConnectSpotifyAsync(token); }
            finally { stall.Cancel(); }
            if (!accepted.Equals(settings.RedirectUri, StringComparison.OrdinalIgnoreCase))
            {
                // remember the address Spotify accepted so the next sign-in goes straight through
                settings.RedirectUri = accepted;
                try { settings.Save(); } catch { }
                loadingFields = true; redirectUri.Text = accepted; loadingFields = false;
                AppendLog("Remembered the redirect address your Spotify app accepts: " + accepted);
            }
            AppendLog("Spotify connected successfully.");
        });
    }

    private async Task BackupAsync()
    {
        if (!SaveSettingsFields(false)) return;
        await RunBusyAsync("Backing up recent Liked Songs", async token =>
        {
            var result = await service.BackupRecentLikesAsync(forceRefresh.Checked, token);
            if (!result.Success)
            {
                if (!console.Entries.TakeLast(6).Any(e => e.Text.Contains(result.Error, StringComparison.Ordinal))) AppendLog("WARNING: " + result.Error);
                SetResult($"Backup partly completed: {result.NewAudioFiles} audio file(s) were kept. See the Console for details.", Led.Warn);
                return;
            }
            if (result.ResolvedMusicFolder.Length > 0) musicFolder.Text = result.ResolvedMusicFolder;
            SetResult($"Backup complete: {result.NewAudioFiles} new audio file(s); {result.AlreadyLocal} recent Likes were already local.", Led.Ok);
            AppendLog("Backup finished.");
        });
    }

    private async Task ManualDownloadAsync()
    {
        using var dialog = new DownloadLinksDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!SaveSettingsFields(false)) return;
        await RunBusyAsync("Downloading pasted links", async token =>
        {
            var result = await service.DownloadLinksAsync(dialog.Links, token);
            if (!result.Success)
            {
                AppendLog("WARNING: " + result.Error);
                SetResult($"Download partly completed: {result.NewAudioFiles} audio file(s) were kept. See the Console for details.", Led.Warn);
                return;
            }
            if (result.ResolvedMusicFolder.Length > 0) musicFolder.Text = result.ResolvedMusicFolder;
            SetResult($"Manual download: {result.NewAudioFiles} audio file(s); {result.ImportedFiles} verified in MusicBee; {result.PrimedFiles} silently initialized.", Led.Ok);
        });
    }

    private async Task PreviewAsync()
    {
        if (cancellation is not null) return;
        if (!SaveSettingsFields(false)) return;
        await RunBusyAsync("Building a safe sync preview", async token =>
        {
            prepared = await service.PrepareAsync(forceRefresh.Checked, forceRefresh.Checked, token);
            planAppliedAt = null;
            PopulatePlan(prepared.Plan);
            if (libraryReport is not null) BuildLibraryReport(); // keep the Library in step with the new preview
            SavePreviewSnapshot(newlyBuilt: true);
            if (!StayOnDashboard) SelectPage(PagePreview);
            SetResult($"Preview: {prepared.Plan.TotalChanges} changes across {prepared.Plan.Playlists.Count} playlists • {prepared.Plan.ConfidentMatches} Spotify metadata matches • {prepared.Plan.UnmatchedLocal} protected/unmatched local tracks.", Led.Ok);
        });
    }

    private async Task ApplyAsync()
    {
        if (cancellation is not null || planAppliedAt is not null) return;
        if (prepared is null || previewRestoredAt is not null) { await PreviewAsync(); return; } // never apply a plan from a previous session
        var removals = prepared.Plan.Playlists.Sum(x => x.SpotifyRemoveIds.Count + x.LocalRemoveFiles.Count);
        var message = removals > 0
            ? $"This reviewed plan contains {removals} guarded track removal(s). Safety backups will be written first.\r\n\r\nApply this exact plan?"
            : $"Apply {prepared.Plan.TotalChanges} add-only change(s) from the reviewed plan?";
        if (MessageBox.Show(this, message, "Apply SpotySync plan", MessageBoxButtons.YesNo, removals > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunBusyAsync("Applying reviewed playlist plan", async token =>
        {
            var result = await service.ExecuteAsync(prepared, false, token);
            foreach (var error in result.Errors) AppendLog("ERROR: " + error);
            SetResult($"Applied: Spotify +{result.SpotifyAdded}/−{result.SpotifyRemoved}; local +{result.LocalAdded}/−{result.LocalRemoved}.", result.Errors.Count > 0 ? Led.Warn : Led.Ok);
            // keep the reviewed plan on screen for reference; it just can't be applied a second time
            planAppliedAt = DateTime.Now;
            SavePreviewSnapshot();
            dashPlanStale = true;
            UpdatePreviewState();
            ShowSelectedDetails();
        });
    }

    /// <summary>Backup → fresh playlist preview → asks before applying. The current preview stays until the new one is ready.</summary>
    private async Task RunAllAsync()
    {
        if (cancellation is not null) return;
        if (!SaveSettingsFields(false)) return;
        var built = false;
        await RunBusyAsync("Running complete backup and sync", async token =>
        {
            var backup = await service.BackupRecentLikesAsync(forceRefresh.Checked, token);
            if (!backup.Success) AppendLog("WARNING: backup was partial; playlist preview will still continue. " + backup.Error);
            if (backup.ResolvedMusicFolder.Length > 0) musicFolder.Text = backup.ResolvedMusicFolder;
            prepared = await service.PrepareAsync(forceRefresh.Checked, true, token);
            planAppliedAt = null;
            PopulatePlan(prepared.Plan);
            if (libraryReport is not null) BuildLibraryReport();
            SavePreviewSnapshot(newlyBuilt: true);
            built = true;
        });
        if (built && prepared is not null)
        {
            if (!StayOnDashboard) SelectPage(PagePreview);
            await ApplyAsync();
        }
    }

    private async Task RunBusyAsync(string label, Func<CancellationToken, Task> operation)
    {
        if (cancellation is not null) return;
        cancellation = new CancellationTokenSource();
        operationLabel = label; operationStarted = DateTime.Now;
        SetBusy(true);
        console.BeginRun(label);
        WriteLogFile(label + "…");
        downloadMonitor.Start(label, WatchRoots(), this);
        var outcome = "ok";
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch (Exception ex) { AppendLog("ERROR: " + ex.Message); outcome = "failed"; }
        finally
        {
            downloadMonitor.Stop();
            var (warnings, errors, issues) = console.EndRun(outcome);
            WriteLogFile(outcome switch { "cancelled" => label + " cancelled.", "failed" => label + " failed.", _ => label + " complete." });
            if (warnings + errors > 0)
            {
                WriteLogFile($"----- Completed with {errors} error(s) and {warnings} warning(s) -----");
                foreach (var issue in issues) WriteLogFile("SUMMARY: " + issue);
            }
            cancellation.Dispose(); cancellation = null;
            SetBusy(false, outcome, warnings, errors);
            if ((errors > 0 || outcome == "failed") && !StayOnDashboard) SelectPage(PageConsole);
            await RefreshStatusAsync();
        }
    }

    /// <summary>Folders the download graph watches: the soundtrack root and the music folder, without nesting duplicates.</summary>
    private List<string> WatchRoots()
    {
        var roots = new List<string>();
        foreach (var folder in new[] { settings.ResolvePath(settings.SoundtrackFolder), settings.ResolvePath(settings.MusicFolder) }
                     .Where(Directory.Exists).OrderBy(x => x.Length))
        {
            var normalized = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!roots.Any(r => normalized.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                roots.Add(folder);
        }
        return roots;
    }

    private void SetBusy(bool busy, string outcome = "", int warnings = 0, int errors = 0)
    {
        progress.Active = busy;
        cancelButton.Enabled = busy;
        foreach (var button in busyButtons) button.Enabled = !busy;
        UpdateApplyButtons();
        if (busy)
        {
            statusText.State = Led.Busy;
            statusText.Title = operationLabel;
            statusText.Detail = "Starting…";
            clock.Start();
        }
        else
        {
            clock.Stop();
            var elapsed = DateTime.Now - operationStarted;
            statusText.State = outcome == "failed" || errors > 0 ? Led.Err : outcome == "cancelled" || warnings > 0 ? Led.Warn : Led.Ok;
            statusText.Title = outcome switch
            {
                "cancelled" => operationLabel + " — cancelled",
                "failed" => operationLabel + " — stopped with an error",
                _ => errors + warnings > 0 ? $"{operationLabel} — done with {errors + warnings} issue(s)" : operationLabel + " — done"
            };
            statusText.Detail = $"Finished {DateTime.Now:HH:mm} · took {FormatDuration(elapsed)} · click for details";
        }
    }

    private void UpdateStatusClock()
    {
        if (cancellation is null) return;
        statusText.Title = $"{operationLabel}  ·  {FormatDuration(DateTime.Now - operationStarted)}";
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";

    private void UpdateApplyButtons()
    {
        var can = cancellation is null && planAppliedAt is null && previewRestoredAt is null && prepared?.Plan.Playlists.Any(x => !x.IsBlocked) == true;
        applyButton.Enabled = can;
        dashboardApplyButton.Enabled = can;
        previewApplyButton.Enabled = can;
        var label = planAppliedAt is null ? "Apply plan" : "Applied";
        if (previewApplyButton.Text != label) { previewApplyButton.Text = label; dashboardApplyButton.Text = label; }
    }

    private async Task RefreshStatusAsync()
    {
        spotifyStatus.Text = service.HasSavedLogin ? "Spotify: connected" : "Spotify: not connected";
        spotifyStatus.State = service.HasSavedLogin ? Led.Ok : Led.Warn;
        await RefreshMusicBeePillAsync();
        UpdateSafetyPill();
        UpdateToolsPill();
        await LoadLikedIndexAsync();
        await ApplyPendingMusicBeeFoldersAsync();
        RefreshSoundtrackList();
        RefreshDashboard();
    }

    /// <summary>
    /// MusicBee pill: "ready", "closed" (it starts when needed), "plugin not responding" (running, no bridge)
    /// or "plugin update needed" (bridge answers with an old version). No answer is NOT an outdated plugin.
    /// </summary>
    private async Task RefreshMusicBeePillAsync()
    {
        int version;
        try { using var bridge = new MusicBeeBridgeClient(settings); version = await bridge.GetVersionAsync(); }
        catch { version = 0; }
        var running = MusicBeeSettingsFile.IsMusicBeeRunning();
        musicBeeState = settings.LocalBackend == LocalBackendKind.M3U ? "not required"
            : version >= 21 ? "ready"
            : version > 0 ? "plugin update needed"
            : running ? "plugin not responding"
            : "closed";
        var text = "MusicBee: " + musicBeeState;
        if (musicBeeStatus.Text != text) musicBeeStatus.Text = text;
        musicBeeStatus.State = musicBeeState switch { "ready" => Led.Ok, "not required" or "closed" => Led.Idle, _ => Led.Warn };
        tips.SetToolTip(musicBeeStatus, musicBeeState switch
        {
            "ready" => "MusicBee and the SpotySync plugin are ready.",
            "closed" => "MusicBee is closed - SpotySync starts it when needed.",
            "not required" => "M3U-only mode: MusicBee isn't needed.",
            _ => "Click to update the plugin and restart MusicBee."
        });
    }

    private void UpdateToolsPill()
    {
        var problems = new List<string>();
        if (!File.Exists(settings.ResolvePath(settings.DownloaderPath))) { problems.Add("downloader missing"); toolsQuery = "downloader"; }
        if (!File.Exists(settings.ResolvePath(settings.FfprobePath))) { problems.Add("ffprobe missing"); if (problems.Count == 1) toolsQuery = "ffprobe"; }
        if (!Directory.Exists(settings.ResolvePath(settings.MusicFolder))) { problems.Add("music folder missing"); if (problems.Count == 1) toolsQuery = "music folder"; }
        if (problems.Count == 0) toolsQuery = "folder";
        toolsStatus.Text = problems.Count == 0 ? "Tools: ready" : "Tools: " + problems[0] + (problems.Count > 1 ? $" (+{problems.Count - 1})" : "");
        toolsStatus.State = problems.Count == 0 ? Led.Ok : Led.Err;
        tips.SetToolTip(toolsStatus, problems.Count == 0 ? "Downloader, ffprobe and music folder are all in place." : "Needs attention: " + string.Join(", ", problems) + ". Click to fix.");
    }

    private void UpdateSafetyPill()
    {
        safetyStatus.Text = settings.EnableRemovals ? $"Safety: guarded ≤ {settings.MaxRemovalsPerPlaylist}" : "Safety: add-only";
        safetyStatus.State = settings.EnableRemovals ? Led.Warn : Led.Ok;
    }

    private async Task LoadLikedIndexAsync()
    {
        try
        {
            var path = settings.LikedCachePath;
            var cache = await Task.Run(() => JsonStore.Load(path, () => new LikedTrackCache()));
            likedIndexed = cache.Tracks.Count;
            likedRefreshed = cache.RefreshedAt;
        }
        catch { likedIndexed = -1; }
    }

    // ------------------------------------------------------------------ logging
    private void AppendLog(string message)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), message); return; }
        console.Append(message);
        WriteLogFile(message);
    }

    private void WriteLogFile(string message)
    {
        try
        {
            Directory.CreateDirectory(settings.LogDirectory);
            File.AppendAllText(Path.Combine(settings.LogDirectory, "SpotySync-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch { }
    }

    private void OnLogAppended(LogEntry entry)
    {
        if (cancellation is not null && entry.Level != LogLevel.Header)
            statusText.Detail = (entry.Tag.Length > 0 ? entry.Tag.ToLowerInvariant() + " · " : "") + entry.Text + (entry.Repeat > 1 ? $"  ×{entry.Repeat}" : "");
        miniFeedDirty = true;
        downloadMonitor.NoteLog(entry.Text);
    }

    private void UpdateConsoleBadge()
    {
        var nav = pages.Count > PageConsole ? pages[PageConsole].Nav : null;
        if (nav is null) return;
        nav.SetBadge(currentPage == PageConsole ? 0 : console.UnseenIssues, console.Errors > 0 ? Led.Err : Led.Warn);
    }

    private void GoToSetting(string query)
    {
        SelectPage(PageSettings);
        settingsView.Find(query);
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}

/// <summary>The left navigation rail: logo, pages, theme shortcuts and collapse toggle.</summary>
internal sealed class Sidebar : Panel
{
    private readonly List<NavItem> items = new();
    private readonly RoundedButton themeButton = RoundedButton.Make("", ButtonKind.Ghost, Glyph.Palette);
    private readonly RoundedButton randomButton = RoundedButton.Make("", ButtonKind.Ghost, Glyph.Shuffle);
    private readonly RoundedButton collapseButton = RoundedButton.Make("", ButtonKind.Ghost, Glyph.ChevronLeft);
    private readonly ToolTip tips = Ui.ToolTip();
    private readonly string version;
    private bool mini;
    private string themeName = "";
    public event Action? Toggled, ThemeClicked, RandomClicked;

    public static Color Fill => ColorX.Mix(Theme.P.Bg, Theme.P.Panel, 0.75);

    public Sidebar(string version)
    {
        this.version = version;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Width = 220;
        BackColor = Fill;
        themeButton.Click += (_, _) => ThemeClicked?.Invoke();
        randomButton.Click += (_, _) => RandomClicked?.Invoke();
        collapseButton.Click += (_, _) => CrossFade.Run(FindForm() ?? (Control)this, () => { Mini = !Mini; Toggled?.Invoke(); }, 0.18);
        tips.SetToolTip(randomButton, "Surprise me — random theme");
        tips.SetToolTip(collapseButton, "Collapse / expand the sidebar");
        foreach (var b in new[] { themeButton, randomButton, collapseButton }) { b.AutoSize = false; b.Font = Fonts.Body(9f); Controls.Add(b); }
        Theme.Changed += () => { BackColor = Fill; Invalidate(true); };
    }

    public string ThemeName
    {
        get => themeName;
        set { themeName = value; themeButton.Text = mini ? "" : value; tips.SetToolTip(themeButton, $"Theme: {value} — click to change"); }
    }

    public bool Mini
    {
        get => mini;
        set
        {
            mini = value;
            Width = mini ? 68 : 220;
            foreach (var item in items) item.Mini = mini;
            collapseButton.Glyph = mini ? Glyph.ChevronRight : Glyph.ChevronLeft;
            ThemeName = themeName.Length > 0 ? themeName : ThemeCatalog.Find(null).Name;
            PerformLayout(); Invalidate(true);
        }
    }

    public void AddNav(NavItem item) { item.Mini = mini; items.Add(item); Controls.Add(item); PerformLayout(); }

    private int BottomTop => mini ? Height - 132 : Height - 98;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var pad = mini ? 10 : 12;
        var y = 92;
        foreach (var item in items) { item.SetBounds(pad, y, Width - pad * 2, 40); y += 44; }
        // bottom block always sits below the nav items (no overlap even in a short window)
        var top = Math.Max(y + 16, BottomTop);
        if (mini)
        {
            themeButton.SetBounds(pad, top + 10, Width - pad * 2, 34);
            randomButton.SetBounds(pad, top + 48, Width - pad * 2, 34);
            collapseButton.SetBounds(pad, top + 86, Width - pad * 2, 34);
        }
        else
        {
            themeButton.SetBounds(pad, top + 12, Width - pad * 2, 34);
            randomButton.SetBounds(pad, top + 52, 40, 34);
            collapseButton.SetBounds(Width - pad - 40, top + 52, 40, 34);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        using (var bg = new SolidBrush(Fill)) g.FillRectangle(bg, ClientRectangle);
        using (var line = new Pen(p.Line)) g.DrawLine(line, Width - 1, 0, Width - 1, Height);
        var pad = mini ? 15 : 16;
        var logo = new RectangleF(pad, 22, 38, 38);
        Ui.FillRound(g, p.Accent, logo, Theme.R(10));
        Ui.DrawGlyph(g, Glyph.Sync, 15f, p.AccentInk, Rectangle.Round(logo));
        if (!mini)
        {
            Ui.Text(g, "SPOTYSYNC", Fonts.Display(15f), new Rectangle(pad + 48, 20, Width - pad - 52, 26), p.Ink);
            Ui.Text(g, "v" + version, Fonts.Mono(7.75f), new Rectangle(pad + 49, 44, Width - pad - 52, 18), p.Dim);
        }
        using var sep = new Pen(p.Line);
        g.DrawLine(sep, pad, 76, Width - pad, 76);
        var y = items.Count > 0 ? items[^1].Bottom : 92;
        var top = Math.Max(y + 16, BottomTop);
        g.DrawLine(sep, pad, top, Width - pad, top);
    }
}

/// <summary>Bottom bar background.</summary>
internal sealed class StatusBarPanel : Panel
{
    public static Color Fill => ColorX.Mix(Theme.P.Bg, Theme.P.Panel, 0.75);

    public StatusBarPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Fill;
        Theme.Changed += () => { BackColor = Fill; Invalidate(true); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var bg = new SolidBrush(Fill)) e.Graphics.FillRectangle(bg, ClientRectangle);
        using var line = new Pen(Theme.P.Line);
        e.Graphics.DrawLine(line, 0, 0, Width, 0);
    }
}

/// <summary>Sends mouse-wheel messages to the window under the cursor (Windows sends them to the focused one).</summary>
internal sealed class WheelRouter : IMessageFilter
{
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL && m.Msg != WM_MOUSEHWHEEL) return false;
        var screen = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
        var target = WindowFromPoint(screen);
        if (target == IntPtr.Zero || target == m.HWnd || Control.FromChildHandle(target) is null) return false;
        SendMessage(target, m.Msg, m.WParam, m.LParam);
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Two-line status read-out with a state LED (operation + latest console line).</summary>
internal sealed class StatusText : ThemedControl
{
    private string title = "Ready", detail = "Nothing running. Pick an action on the Dashboard.";
    private Led state = Led.Idle;
    public string Title { get => title; set { title = value; Invalidate(); } }
    public string Detail { get => detail; set { detail = value; Invalidate(); } }
    public Led State { get => state; set { state = value; Invalidate(); } }

    public StatusText() { Cursor = Cursors.Hand; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var led = StatusPill.LedColor(state);
        var cy = Height / 2f;
        if (state != Led.Idle) using (var glow = new SolidBrush(ColorX.Alpha(led, 60))) g.FillEllipse(glow, 0, cy - 8, 16, 16);
        using (var dot = new SolidBrush(led)) g.FillEllipse(dot, 4, cy - 4, 8, 8);
        Ui.Text(g, title, Fonts.Semibold(9.75f), new Rectangle(24, 0, Width - 26, Height / 2 + 1), Hovered ? p.Accent : p.Ink, TextFormatFlags.Bottom);
        TextRenderer.DrawText(g, detail, Fonts.Mono(8.25f), new Rectangle(24, Height / 2 + 2, Width - 26, Height / 2 - 2), p.Muted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}
