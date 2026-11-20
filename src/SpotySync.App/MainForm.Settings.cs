using System.Diagnostics;
using SpotySync;

namespace SpotySyncApp;

public sealed partial class MainForm
{
    private readonly SettingsView settingsView = new() { Dock = DockStyle.Fill };
    private bool settingsDirty;
    private bool firstRunPrompted;
    private bool loadingFields;

    private readonly FieldBox clientId = Field("Paste the Client ID from your Spotify developer app");
    private readonly FieldBox clientSecret = new() { Password = true, Placeholder = "Needed by the bundled spot2yt downloader" };
    private readonly FieldBox redirectUri = Field("http://127.0.0.1/callback");
    private readonly FieldBox musicFolder = Field();
    private readonly FieldBox m3uFolder = Field();
    private readonly FieldBox downloaderPath = Field();
    private readonly FieldBox ffprobePath = Field();
    private readonly FieldBox musicBeeExe = Field();
    private readonly FieldBox soundtrackRoot = Field();
    private readonly DarkComboBox backend = new() { Width = 220 };
    private readonly DarkComboBox direction = new() { Width = 220 };
    private readonly ToggleSwitch enableRemovals = new() { Danger = true };
    private readonly ToggleSwitch likedOnly = new();
    private readonly ToggleSwitch ownedOnly = new();
    private readonly ToggleSwitch relativeM3u = new();
    private readonly ToggleSwitch autoStartMusicBee = new();
    private readonly ToggleSwitch importDownloads = new();
    private readonly ToggleSwitch primeDownloads = new();
    private readonly ToggleSwitch autoUpdateYtDlp = new();
    private readonly ToggleSwitch autoYouTubeFallback = new();
    private readonly ToggleSwitch createTempPlaylist = new();
    private readonly NumberBox removalCap = new(0, 10000);
    private readonly NumberBox cacheDays = new(0, 365);
    private readonly NumberBox lookbackDays = new(1, 3650);
    private readonly NumberBox downloadAnchorCount = new(1, 50);
    private readonly NumberBox ytDlpUpdateDays = new(1, 30);
    private readonly NumberBox apiRetry = new(0, 12);
    private readonly NumberBox bridgePort = new(1024, 65535);
    private readonly NumberBox markerOverlap = new(0, 1440);
    private readonly FieldBox includePatterns = Field("*");
    private readonly FieldBox excludePatterns = Field("__SpotySync*;Discover Weekly");

    // appearance (applied instantly)
    private readonly SwatchGrid swatches = new();
    private readonly DarkComboBox fontChoice = new() { Width = 170 };
    private readonly ToggleSwitch roundedCorners = new();
    private readonly ToggleSwitch backgroundGlow = new();
    private readonly ToggleSwitch animations = new();
    private readonly ToggleSwitch compactNav = new();
    private readonly ToggleSwitch consoleDetailDefault = new();
    private readonly ToggleSwitch consoleTimesDefault = new();
    private readonly ToggleSwitch consoleSimpleDefault = new();
    private readonly AccentSwatch accentSwatch = new();
    private readonly VStack cardEditor = new() { Gap = 4, AutoHeight = true };

    private static FieldBox Field(string placeholder = "") => new() { Placeholder = placeholder };

    private Control BuildSettings()
    {
        var page = new Panel { BackColor = Color.Transparent };
        var v = settingsView;

        // ---- Spotify
        var g = v.AddGroup("spotify", "Spotify account", "Connection", Glyph.Account, "spotify login developer app api");
        var assistant = RoundedButton.Make("One-click setup", ButtonKind.Primary, Glyph.Lightning, (_, _) => OpenSetupAssistant());
        busyButtons.Add(assistant);
        g.Add(new SettingRow("Setup assistant", "Opens the Spotify Developer Dashboard in your browser (where you are already signed in), gives you one-click copies of the app form values, picks up your Client ID and secret as you copy them, updates the MusicBee plugin (closing and reopening MusicBee if needed), then connects.",
            assistant, EditorWidth.Narrow, "setup wizard assistant one click automatic client id secret create app"));
        g.Add(new SettingRow("Client ID", "From your app at developer.spotify.com. Required for everything Spotify-related.",
            new FieldStrip(clientId, LinkButton("Dashboard", "https://developer.spotify.com/dashboard")), EditorWidth.Wide, "spotify app developer id key"));
        var reveal = RoundedButton.Make("Show", ButtonKind.Secondary, Glyph.Eye);
        reveal.Click += (_, _) => { clientSecret.Password = !clientSecret.Password; reveal.Text = clientSecret.Password ? "Show" : "Hide"; };
        g.Add(new SettingRow("Client secret", "Stored privately in Tools\\spotify_credentials.txt for the bundled spot2yt downloader. Never shared.",
            new FieldStrip(clientSecret, reveal), EditorWidth.Wide, "password secret downloader spot2yt credentials"));
        g.Add(new SettingRow("Redirect URI", "Must match your Spotify app exactly and use the 127.0.0.1 loopback address.",
            redirectUri, EditorWidth.Wide, "callback loopback url oauth"));
        var connect = RoundedButton.Make("Connect Spotify", ButtonKind.Primary, Glyph.Link, async (_, _) => await ConnectAsync());
        var forget = RoundedButton.Make("Forget login", ButtonKind.Secondary, Glyph.Delete, (_, _) =>
        {
            service.DisconnectSpotify(); spotifyStatus.Text = "Spotify: not connected"; spotifyStatus.State = Led.Warn;
            AppendLog("Portable Spotify token removed."); RefreshDashboard();
        });
        busyButtons.Add(connect);
        g.Add(new SettingRow("Spotify login", "Opens Spotify in your browser to authorise SpotySync (PKCE). The token stays in the portable Data folder.",
            ButtonPair(connect, forget), EditorWidth.Narrow, "connect login authorize authorise token disconnect logout forget"));
        g.Add(new SettingRow("API retry count", "How many times a failed or rate-limited Spotify request is retried before giving up.",
            apiRetry, EditorWidth.Narrow, "rate limit network retries"));

        // ---- Library & tools
        g = v.AddGroup("library", "Library & tools", "Folders", Glyph.Library, "paths folders executables");
        g.Add(new SettingRow("Music folder", "Where your audio lives and where new downloads are verified.",
            new FieldStrip(musicFolder, BrowseFolderButton(musicFolder)), EditorWidth.Wide, "songs library path directory location"));
        g.Add(new SettingRow("M3U folder", "Where M3U/M3U8 playlists are read and written (relative to SpotySync.exe if not absolute).",
            new FieldStrip(m3uFolder, BrowseFolderButton(m3uFolder)), EditorWidth.Wide, "playlists m3u8 path"));
        g.Add(new SettingRow("Soundtrack folder", "Parent folder for Soundtrack Saver. Each saved soundtrack/playlist gets its own subfolder here (e.g. Music\\Blockheads).",
            new FieldStrip(soundtrackRoot, BrowseFolderButton(soundtrackRoot)), EditorWidth.Wide, "soundtrack saver album game ost folder"));
        g.Add(new SettingRow("Downloader", "The bundled spot2yt.exe by default. Any compatible downloader executable works.",
            new FieldStrip(downloaderPath, BrowseFileButton(downloaderPath, "Executables|*.exe|All files|*.*")), EditorWidth.Wide, "spot2yt spotdl exe tool"));
        g.Add(new SettingRow("ffprobe", "Used to read durations and tags of audio files.",
            new FieldStrip(ffprobePath, BrowseFileButton(ffprobePath, "ffprobe|ffprobe.exe|Executables|*.exe")), EditorWidth.Wide, "ffmpeg probe metadata tags"));
        g.Add(new SettingRow("MusicBee.exe", "Your MusicBee installation, used to start MusicBee and to locate its plugin folder.",
            new FieldStrip(musicBeeExe, BrowseFileButton(musicBeeExe, "MusicBee|MusicBee.exe|Executables|*.exe")), EditorWidth.Wide, "musicbee program path"));
        g.Add(new SettingRow("MusicBee bridge port", "Local port the SpotySync MusicBee plugin listens on. Only change it if another app uses 51983.",
            bridgePort, EditorWidth.Narrow, "musicbee plugin port network bridge"));
        var install = RoundedButton.Make("Install MusicBee plugin", ButtonKind.Secondary, Glyph.Plug,
            async (_, _) => await RunBusyAsync("Updating the MusicBee plugin", _ => UpdateMusicBeePluginAsync(force: true)));
        g.Add(new SettingRow("MusicBee plugin", "Installs or updates the bridge plugin, then restarts MusicBee so it is actually loaded (MusicBee keeps the old copy until it restarts).",
            install, EditorWidth.Narrow, "musicbee plugin install update bridge dll"));

        // ---- Sync policy
        g = v.AddGroup("sync", "Synchronization policy", "Sync safety", Glyph.Shield, "sync playlists safety");
        backend.Items.AddRange(Enum.GetNames<LocalBackendKind>());
        direction.Items.AddRange(Enum.GetNames<SyncDirection>());
        direction.Format += (_, e) => e.Value = e.ListItem?.ToString() switch { "TwoWay" => "Two-way", "SpotifyToLocal" => "Spotify → local", "LocalToSpotify" => "Local → Spotify", var s => s };
        backend.Format += (_, e) => e.Value = e.ListItem?.ToString() switch { "Both" => "MusicBee + M3U", var s => s };
        g.Add(new SettingRow("Local backend", "Which local playlists are synced with Spotify.", backend, EditorWidth.Narrow, "musicbee m3u both local playlists"));
        g.Add(new SettingRow("Direction", "Two-way keeps both sides equal; one-way only adds to the target side.", direction, EditorWidth.Narrow, "two-way one way spotify local"));
        var removals = new SettingRow("Enable guarded removals", "After a baseline exists, tracks removed on one side are removed on the other. Safety backups are written first.",
            enableRemovals, EditorWidth.Narrow, "delete removals remove baseline danger guarded") { Danger = true };
        g.Add(removals);
        g.Add(new SettingRow("Removal cap", "Maximum removals per playlist in one run. Playlists above the cap are blocked for review.",
            removalCap, EditorWidth.Narrow, "removal limit max safety block"));
        g.Add(new SettingRow("Liked Songs only", "Only add tracks to Spotify playlists that are in your Spotify Liked Songs.", likedOnly, EditorWidth.Narrow, "liked songs add filter"));
        g.Add(new SettingRow("Owned playlists only", "Only sync playlists you own or collaborate on (Spotify Development Mode only exposes these).", ownedOnly, EditorWidth.Narrow, "owner collaborative followed"));
        g.Add(new SettingRow("Relative M3U paths", "Write portable relative paths in M3U files.", relativeM3u, EditorWidth.Narrow, "m3u portable relative absolute"));
        g.Add(new SettingRow("Start MusicBee automatically", "Launch MusicBee when an operation needs it.", autoStartMusicBee, EditorWidth.Narrow, "musicbee launch start auto"));

        // ---- Filters
        g = v.AddGroup("filters", "Playlist filters", "Scope", Glyph.Filter, "include exclude patterns ignore");
        g.Add(new SettingRow("Include", "Playlist names to sync. Wildcards * and ?; separate several with ;", includePatterns, EditorWidth.Wide, "include patterns wildcard names"));
        g.Add(new SettingRow("Exclude", "Playlist names never to sync. Wildcards * and ?; separate several with ;", excludePatterns, EditorWidth.Wide, "exclude patterns wildcard skip discover weekly"));
        var ignored = RoundedButton.Make("Manage ignored tracks", ButtonKind.Secondary, Glyph.Filter, (_, _) => OpenIgnoredTracks());
        g.Add(new SettingRow("Ignored tracks", "Tracks you marked “Never sync…”. Delete a line to undo.", ignored, EditorWidth.Narrow, "never sync exclusions ignore list"));

        // ---- Backup & downloads
        g = v.AddGroup("backup", "Backup & downloads", "Liked Songs", Glyph.Download, "backup download likes youtube");
        g.Add(new SettingRow("Full Likes refresh (days)", "Re-read every Liked Song after this many days; otherwise only new Likes are fetched. 0 = always.", cacheDays, EditorWidth.Narrow, "cache refresh likes full"));
        g.Add(new SettingRow("No-history fallback (days)", "How far back the first backup looks when there is no previous backup.", lookbackDays, EditorWidth.Narrow, "lookback first run history days"));
        g.Add(new SettingRow("Recent download anchor count", "Recent Likes are anchored to the oldest of this many newest audio files.", downloadAnchorCount, EditorWidth.Narrow, "anchor recent files date modified"));
        g.Add(new SettingRow("Backup marker overlap (minutes)", "Re-checks this many minutes before the last backup marker so nothing slips through.", markerOverlap, EditorWidth.Narrow, "marker overlap minutes"));
        g.Add(new SettingRow("Temporary download playlist", "Batch many Likes into one temporary private Spotify playlist for faster downloading. It is removed afterwards.", createTempPlaylist, EditorWidth.Narrow, "temporary playlist batch concurrent"));
        g.Add(new SettingRow("Import downloads into MusicBee", "Verify new files appear in MusicBee's library.", importDownloads, EditorWidth.Narrow, "musicbee import library"));
        g.Add(new SettingRow("Initialize new downloads in MusicBee", "Silently plays each new file for one second so MusicBee reads it fully (play count is restored).", primeDownloads, EditorWidth.Narrow, "prime initialize musicbee play"));
        g.Add(new SettingRow("Auto-update yt-dlp", "Keep the bundled yt-dlp current before downloads.", autoUpdateYtDlp, EditorWidth.Narrow, "youtube update ytdlp"));
        g.Add(new SettingRow("yt-dlp update interval (days)", "How often the yt-dlp update check runs.", ytDlpUpdateDays, EditorWidth.Narrow, "youtube update interval"));
        g.Add(new SettingRow("Automatic public-source fallback", "When a track has no YouTube Music source, try YouTube, album chapters and SoundCloud.", autoYouTubeFallback, EditorWidth.Narrow, "youtube soundcloud fallback recovery"));

        // ---- Appearance
        g = v.AddGroup("appearance", "Appearance", "Look & feel", Glyph.Palette, "theme colour color look");
        swatches.Picked += def => { settings.UiTheme = def.Id; settings.UiAccent = ""; ReapplyTheme(); };
        g.Add(new SettingRow("Theme", "Pick a colour scheme. It applies instantly and is saved automatically.", swatches, EditorWidth.Full,
            "theme gallery dark oled qd colours colors palette " + string.Join(' ', ThemeCatalog.All.Select(t => t.Name.ToLowerInvariant()))));
        var pickAccent = RoundedButton.Make("Pick…", ButtonKind.Secondary, Glyph.Palette, (_, _) => PickAccent());
        var resetAccent = RoundedButton.Make("Theme default", ButtonKind.Ghost, Glyph.Undo, (_, _) => { settings.UiAccent = ""; ReapplyTheme(); });
        g.Add(new SettingRow("Accent colour", "Override the theme's accent with any colour you like.", ButtonRow(accentSwatch, pickAccent, resetAccent), EditorWidth.Narrow, "accent highlight custom colour color"));
        var surprise = RoundedButton.Make("Surprise me", ButtonKind.Secondary, Glyph.Shuffle, (_, _) => ApplyRandomTheme());
        g.Add(new SettingRow("Random theme", "Try a random theme, including extra two-tone schemes that are not in the gallery.", surprise, EditorWidth.Narrow, "random shuffle surprise"));
        fontChoice.Items.AddRange(["Modern", "Condensed", "Terminal"]);
        g.Add(new SettingRow("Font style", "Modern (Segoe UI with condensed headings), Condensed (Bahnschrift) or Terminal (Cascadia Mono).", fontChoice, EditorWidth.Narrow, "font typeface text mono condensed bahnschrift segoe cascadia"));
        g.Add(new SettingRow("Rounded corners", "Soft rounded cards and buttons, or sharp rack-style edges.", roundedCorners, EditorWidth.Narrow, "corners radius sharp round"));
        g.Add(new SettingRow("Background glow", "A soft accent-coloured glow at the top of the window.", backgroundGlow, EditorWidth.Narrow, "glow gradient background"));
        g.Add(new SettingRow("Animations", "Animated switches and progress bar.", animations, EditorWidth.Narrow, "motion animation reduce"));

        // ---- Layout
        g = v.AddGroup("layout", "Layout", "Modules", Glyph.Layout, "layout modules dashboard sidebar");
        g.Add(new SettingRow("Compact sidebar", "Show only icons in the navigation rail.", compactNav, EditorWidth.Narrow, "sidebar navigation collapse icons mini"));
        g.Add(new SettingRow("Dashboard modules", "Show or hide modules. Move and resize them on the dashboard: drag a title to move, drag the bottom-right corner to resize.",
            cardEditor, EditorWidth.Full, "dashboard cards modules widgets order hide show customize"));
        g.Add(new SettingRow("Console: simple view", "Show only what matters: what is downloading (with progress), what finished, and any problems. Turn off for the full technical log.",
            consoleSimpleDefault, EditorWidth.Narrow, "console simple clean progress verbose log"));
        g.Add(new SettingRow("Console: downloader detail", "Show every line from the downloader in the Console by default (they are always in the log file).", consoleDetailDefault, EditorWidth.Narrow, "console verbose detail downloader log"));
        g.Add(new SettingRow("Console: timestamps", "Show the time next to every Console line.", consoleTimesDefault, EditorWidth.Narrow, "console time timestamps"));

        // ---- Data
        g = v.AddGroup("data", "Data & maintenance", "Files", Glyph.Database, "data files folders maintenance");
        g.Add(new SettingRow("Settings file", "Everything here is stored in the portable SpotySync.ini beside SpotySync.exe.",
            RoundedButton.Make("Open SpotySync.ini", ButtonKind.Secondary, Glyph.Open, (_, _) => { SaveSettingsFields(false); OpenPath(settings.FilePath); }), EditorWidth.Narrow, "ini config file open"));
        g.Add(new SettingRow("Backups & caches", "Sync baselines, caches and safety backups. Deleting sync-state.json resets deletion baselines.",
            RoundedButton.Make("Open backups", ButtonKind.Secondary, Glyph.Folder, (_, _) => { Directory.CreateDirectory(settings.DataDirectory); OpenPath(settings.DataDirectory); }), EditorWidth.Narrow, "data folder backups cache baseline state"));
        g.Add(new SettingRow("Log files", "Full, unfiltered logs of every run.",
            RoundedButton.Make("Open logs", ButtonKind.Secondary, Glyph.Text, (_, _) => { Directory.CreateDirectory(settings.LogDirectory); OpenPath(settings.LogDirectory); }), EditorWidth.Narrow, "logs folder crash"));
        g.Add(new SettingRow("Reset appearance", "Back to the default SpotySync theme, font and dashboard layout.",
            RoundedButton.Make("Reset look", ButtonKind.Secondary, Glyph.Undo, (_, _) => ResetAppearance()), EditorWidth.Narrow, "reset default appearance theme"));

        // Unsaved-changes bar
        var bar = v.UnsavedBar;
        bar.AccentEdge = Theme.P.Accent;
        var barLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var barText = Ui.Label("You have unsaved changes.  Actions save automatically before they run, or save now (Ctrl+S).", "ink", Fonts.Body(9.75f), autoSize: false);
        barText.Dock = DockStyle.Fill; barText.TextAlign = ContentAlignment.MiddleLeft;
        var revert = RoundedButton.Make("Revert", ButtonKind.Ghost, Glyph.Undo, (_, _) => { LoadSettingsFields(); });
        var save = RoundedButton.Make("Save settings", ButtonKind.Primary, Glyph.Save, (_, _) => SaveSettingsFields(true));
        revert.Anchor = save.Anchor = AnchorStyles.Right;
        barLayout.Controls.Add(barText, 0, 0); barLayout.Controls.Add(revert, 1, 0); barLayout.Controls.Add(save, 2, 0);
        bar.Controls.Add(barLayout);

        HookDirtyTracking();
        HookAppearanceControls();
        RebuildCardEditor();
        sidebar.ThemeName = ThemeCatalog.Find(settings.UiTheme).Name;
        page.Controls.Add(v);
        return page;
    }

    private static Control ButtonPair(Control a, Control b) => ButtonRow(a, b);

    private static Control ButtonRow(params Control[] controls)
    {
        var row = new Row { Height = 36 };
        var width = 0;
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 0, 8, 0);
            if (c is RoundedButton b) { b.AutoSize = false; b.Size = b.GetPreferredSize(Size.Empty); }
            row.Controls.Add(c);
            width += c.Width + 8;
        }
        row.Width = width;
        return row;
    }

    private static RoundedButton LinkButton(string text, string url) =>
        RoundedButton.Make(text, ButtonKind.Secondary, Glyph.Open, (_, _) => OpenPath(url));

    private static RoundedButton BrowseFolderButton(FieldBox field) =>
        RoundedButton.Make("Browse…", ButtonKind.Secondary, Glyph.Folder, (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(field.Text) ? field.Text : "" };
            if (dialog.ShowDialog() == DialogResult.OK) field.Text = dialog.SelectedPath;
        });

    private static RoundedButton BrowseFileButton(FieldBox field, string filter) =>
        RoundedButton.Make("Browse…", ButtonKind.Secondary, Glyph.Folder, (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = filter, FileName = File.Exists(field.Text) ? field.Text : "" };
            if (dialog.ShowDialog() == DialogResult.OK) field.Text = dialog.FileName;
        });

    private void HookDirtyTracking()
    {
        void Dirty(object? _, EventArgs __) => MarkDirty();
        foreach (var f in new[] { clientId, clientSecret, redirectUri, musicFolder, m3uFolder, downloaderPath, ffprobePath, musicBeeExe, soundtrackRoot, includePatterns, excludePatterns })
            f.TextChanged += Dirty;
        foreach (var n in new[] { removalCap, cacheDays, lookbackDays, downloadAnchorCount, ytDlpUpdateDays, apiRetry, bridgePort, markerOverlap })
            n.ValueChanged += Dirty;
        foreach (var t in new[] { enableRemovals, likedOnly, ownedOnly, relativeM3u, autoStartMusicBee, importDownloads, primeDownloads, autoUpdateYtDlp, autoYouTubeFallback, createTempPlaylist })
            t.CheckedChanged += Dirty;
        backend.SelectedIndexChanged += Dirty;
        direction.SelectedIndexChanged += Dirty;
    }

    private void MarkDirty()
    {
        if (loadingFields) return;
        settingsDirty = true;
        settingsView.UnsavedBar.AccentEdge = Theme.P.Accent;
        settingsView.UnsavedBar.Visible = true;
        pages[PageSettings].Nav.SetBadge(1, Led.Warn);
    }

    private void MarkClean()
    {
        settingsDirty = false;
        settingsView.UnsavedBar.Visible = false;
        if (pages.Count > PageSettings) pages[PageSettings].Nav.SetBadge(0, null);
    }

    private void HookAppearanceControls()
    {
        roundedCorners.CheckedChanged += (_, _) => { if (loadingFields) return; settings.UiRoundedCorners = roundedCorners.Checked; ReapplyTheme(); };
        backgroundGlow.CheckedChanged += (_, _) => { if (loadingFields) return; settings.UiBackgroundGlow = backgroundGlow.Checked; ReapplyTheme(); };
        animations.CheckedChanged += (_, _) => { if (loadingFields) return; settings.UiAnimations = animations.Checked; ReapplyTheme(); };
        fontChoice.SelectedIndexChanged += (_, _) =>
        {
            if (loadingFields) return;
            settings.UiFont = fontChoice.SelectedIndex switch { 1 => "display", 2 => "mono", _ => "system" };
            ReapplyTheme();
        };
        compactNav.CheckedChanged += (_, _) => { if (loadingFields) return; sidebar.Mini = compactNav.Checked; settings.UiCompactNavigation = compactNav.Checked; SaveAppearance(); };
        sidebar.Toggled += () => { loadingFields = true; compactNav.Checked = sidebar.Mini; loadingFields = false; };
        consoleDetailDefault.CheckedChanged += (_, _) => { if (loadingFields) return; console.SetDetail(consoleDetailDefault.Checked); };
        consoleTimesDefault.CheckedChanged += (_, _) => { if (loadingFields) return; console.SetTimestamps(consoleTimesDefault.Checked); };
        consoleSimpleDefault.CheckedChanged += (_, _) => { if (loadingFields) return; console.SetSimple(consoleSimpleDefault.Checked); };
        accentSwatch.Click += (_, _) => PickAccent();
    }

    private void PickAccent()
    {
        using var dialog = new ColorDialog { Color = Theme.P.Accent, FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        settings.UiAccent = ColorX.ToHex(dialog.Color);
        ReapplyTheme();
    }

    private void UpdateAccentSwatch() => accentSwatch.Invalidate();

    private void ResetAppearance()
    {
        settings.UiTheme = ThemeCatalog.DefaultId; settings.UiAccent = ""; settings.UiFont = "system";
        settings.UiRoundedCorners = true; settings.UiBackgroundGlow = true; settings.UiAnimations = true;
        settings.UiCompactNavigation = false; settings.UiDashboardCards = "";
        loadingFields = true;
        LoadAppearanceFields();
        loadingFields = false;
        sidebar.Mini = false;
        ApplyDashboardLayout();
        RebuildCardEditor();
        ReapplyTheme();
    }

    private readonly Dictionary<string, ToggleSwitch> moduleToggles = new();

    /// <summary>Show/hide switches for dashboard modules (moving and resizing happen on the dashboard itself).</summary>
    private void RebuildCardEditor()
    {
        if (moduleToggles.Count == 0)
        {
            cardEditor.SuspendLayout();
            foreach (var module in dashCards)
            {
                var id = module.Id;
                var toggle = new ToggleSwitch { Text = module.Title, Height = 30 };
                toggle.Width = toggle.GetPreferredSize(Size.Empty).Width;
                toggle.CheckedChanged += (_, _) => { if (!loadingFields) SetDashCardVisible(id, toggle.Checked); };
                moduleToggles[id] = toggle;
                cardEditor.Controls.Add(toggle);
            }
            cardEditor.ResumeLayout(true);
            Ui.Restyle(cardEditor);
        }
        var wasLoading = loadingFields;
        loadingFields = true;
        foreach (var (id, visible) in DashOrder())
            if (moduleToggles.TryGetValue(id, out var toggle) && toggle.Checked != visible) toggle.Checked = visible;
        loadingFields = wasLoading;
        settingsView.Relayout();
    }

    private void LoadAppearanceFields()
    {
        swatches.SelectedId = settings.UiTheme;
        fontChoice.SelectedIndex = settings.UiFont switch { "display" => 1, "mono" => 2, _ => 0 };
        roundedCorners.Checked = settings.UiRoundedCorners;
        backgroundGlow.Checked = settings.UiBackgroundGlow;
        animations.Checked = settings.UiAnimations;
        compactNav.Checked = settings.UiCompactNavigation;
        consoleDetailDefault.Checked = settings.UiConsoleDownloaderDetail;
        consoleTimesDefault.Checked = settings.UiConsoleTimestamps;
        consoleSimpleDefault.Checked = settings.UiConsoleSimple;
    }

    private void LoadSettingsFields()
    {
        loadingFields = true;
        try
        {
            clientId.Text = settings.SpotifyClientId; clientSecret.Text = ReadDownloaderClientSecret(); redirectUri.Text = settings.RedirectUri;
            musicFolder.Text = settings.MusicFolder; m3uFolder.Text = settings.M3uFolder; soundtrackRoot.Text = settings.SoundtrackFolder;
            downloaderPath.Text = settings.DownloaderPath; ffprobePath.Text = settings.FfprobePath; musicBeeExe.Text = settings.MusicBeeExe;
            backend.SelectedItem = settings.LocalBackend.ToString(); direction.SelectedItem = settings.Direction.ToString(); enableRemovals.Checked = settings.EnableRemovals;
            likedOnly.Checked = settings.LikedSongsOnlyForSpotify; ownedOnly.Checked = settings.OwnedPlaylistsOnly; relativeM3u.Checked = settings.RelativeM3uPaths;
            autoStartMusicBee.Checked = settings.AutoStartMusicBee; importDownloads.Checked = settings.ImportDownloadsIntoMusicBee; primeDownloads.Checked = settings.PrimeDownloadsInMusicBee;
            removalCap.Value = settings.MaxRemovalsPerPlaylist; cacheDays.Value = settings.LikedCacheFullRefreshDays; lookbackDays.Value = settings.FirstRunLikedLookbackDays;
            downloadAnchorCount.Value = settings.DownloadAnchorFileCount; autoUpdateYtDlp.Checked = settings.AutoUpdateYtDlp; autoYouTubeFallback.Checked = settings.AutoYouTubeFallback; ytDlpUpdateDays.Value = settings.YtDlpUpdateDays;
            apiRetry.Value = settings.ApiRetryCount; bridgePort.Value = settings.MusicBeeBridgePort; markerOverlap.Value = settings.BackupMarkerOverlapMinutes; createTempPlaylist.Checked = settings.CreateTemporaryDownloadPlaylist;
            includePatterns.Text = settings.PlaylistInclude; excludePatterns.Text = settings.PlaylistExclude;
            LoadAppearanceFields();
        }
        finally { loadingFields = false; }
        MarkClean();
        if (string.IsNullOrWhiteSpace(settings.SpotifyClientId) && !firstRunPrompted)
        {
            firstRunPrompted = true;
            SelectPage(PageSettings);
            AppendLog("First setup: use “One-click setup” (Settings › Spotify account) to connect Spotify.");
            // with a working setup elsewhere on this PC the startup question offers it; otherwise open the assistant
            Shown += (_, _) => BeginInvoke(() => { if (StartupPrompts && !ExistingSetup.Find(settings).Any(x => x.HasLogin)) OpenSetupAssistant(); });
        }
    }

    private bool SaveSettingsFields(bool showConfirmation)
    {
        if (cancellation is not null)
        {
            if (showConfirmation)
                MessageBox.Show(this, "Wait for the current operation to finish or cancel it before changing settings.",
                    "SpotySync is busy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        var syncBefore = SyncFingerprint();
        try
        {
            settings.SpotifyClientId = clientId.Text.Trim(); settings.RedirectUri = redirectUri.Text.Trim(); settings.MusicFolder = musicFolder.Text.Trim(); settings.M3uFolder = m3uFolder.Text.Trim();
            settings.SoundtrackFolder = soundtrackRoot.Text.Trim();
            settings.DownloaderPath = downloaderPath.Text.Trim(); settings.FfprobePath = ffprobePath.Text.Trim(); settings.MusicBeeExe = musicBeeExe.Text.Trim();
            settings.LocalBackend = Enum.Parse<LocalBackendKind>(backend.SelectedItem?.ToString() ?? settings.LocalBackend.ToString());
            settings.Direction = Enum.Parse<SyncDirection>(direction.SelectedItem?.ToString() ?? settings.Direction.ToString());
            settings.EnableRemovals = enableRemovals.Checked;
            settings.LikedSongsOnlyForSpotify = likedOnly.Checked; settings.OwnedPlaylistsOnly = ownedOnly.Checked; settings.RelativeM3uPaths = relativeM3u.Checked;
            settings.AutoStartMusicBee = autoStartMusicBee.Checked; settings.ImportDownloadsIntoMusicBee = importDownloads.Checked; settings.PrimeDownloadsInMusicBee = primeDownloads.Checked;
            settings.MaxRemovalsPerPlaylist = (int)removalCap.Value; settings.LikedCacheFullRefreshDays = (int)cacheDays.Value; settings.FirstRunLikedLookbackDays = (int)lookbackDays.Value;
            settings.DownloadAnchorFileCount = (int)downloadAnchorCount.Value; settings.AutoUpdateYtDlp = autoUpdateYtDlp.Checked; settings.AutoYouTubeFallback = autoYouTubeFallback.Checked; settings.YtDlpUpdateDays = (int)ytDlpUpdateDays.Value;
            settings.ApiRetryCount = (int)apiRetry.Value; settings.MusicBeeBridgePort = (int)bridgePort.Value; settings.BackupMarkerOverlapMinutes = (int)markerOverlap.Value; settings.CreateTemporaryDownloadPlaylist = createTempPlaylist.Checked;
            settings.PlaylistInclude = includePatterns.Text.Trim(); settings.PlaylistExclude = excludePatterns.Text.Trim();
            if (Path.GetFileName(settings.DownloaderPath).StartsWith("spot2yt", StringComparison.OrdinalIgnoreCase) && clientSecret.Text.Trim().Length == 0)
                throw new InvalidOperationException("Enter the Spotify app Client secret. It is needed by the bundled downloader and is stored privately.");
            if (!string.IsNullOrWhiteSpace(settings.SpotifyClientId)) settings.Validate();
            else if (!Uri.TryCreate(settings.RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "http" || redirect.Host != "127.0.0.1")
                throw new InvalidOperationException("Redirect URI must use the 127.0.0.1 loopback address, for example http://127.0.0.1/callback.");
            settings.Save();
            SaveDownloaderCredentials();
            service.Dispose(); service = new SpotySyncService(settings, AppendLog);
            // Every action saves the settings first; only a change that alters the plan may discard the preview.
            if (prepared is not null && SyncFingerprint() != syncBefore)
            {
                prepared = null; ClearPlan();
                AppendLog("Sync settings changed - build a new preview to see the updated plan.");
            }
            var wasDirty = settingsDirty;
            MarkClean();
            if (showConfirmation || wasDirty) AppendLog("Settings saved beside SpotySync.exe.");
            if (showConfirmation) statusText.Detail = "Settings saved " + DateTime.Now.ToString("HH:mm:ss");
            _ = RefreshStatusAsync(); return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Settings need attention", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
    }

    /// <summary>The settings a sync plan depends on (if any of these change, a built preview is out of date).</summary>
    private string SyncFingerprint() => string.Join("\u001f",
        settings.SpotifyClientId, settings.MusicFolder, settings.M3uFolder, settings.LocalBackend, settings.Direction,
        settings.EnableRemovals, settings.MaxRemovalsPerPlaylist, settings.LikedSongsOnlyForSpotify, settings.OwnedPlaylistsOnly,
        settings.RelativeM3uPaths, settings.PlaylistInclude, settings.PlaylistExclude, settings.MusicBeeBridgePort);

    private string ReadDownloaderClientSecret()
    {
        var path = Path.Combine(settings.BaseDirectory, "Tools", "spotify_credentials.txt");
        if (!File.Exists(path)) return "";
        try
        {
            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
                .Select(line => line.Split('=', 2))
                .FirstOrDefault(parts => parts.Length == 2 && parts[0].Trim().Equals("SPOTIFY_CLIENT_SECRET", StringComparison.OrdinalIgnoreCase))?[1].Trim() ?? "";
        }
        catch { return ""; }
    }

    private void SaveDownloaderCredentials()
    {
        var secret = clientSecret.Text.Trim();
        var id = clientId.Text.Trim();
        var tools = Path.Combine(settings.BaseDirectory, "Tools");
        var executable = Path.Combine(tools, "spot2yt.exe");
        if (!File.Exists(executable) || id.Length == 0 || secret.Length == 0) return;
        Directory.CreateDirectory(tools);
        var credentials = Path.Combine(tools, "spotify_credentials.txt");
        File.WriteAllText(credentials,
            "# Private downloader credentials. SpotySync manages this file; do not share it.\r\n" +
            "SPOTIFY_CLIENT_ID=" + id + "\r\n" +
            "SPOTIFY_CLIENT_SECRET=" + secret + "\r\n");
        var browser = Path.Combine(tools, "browser_options.txt");
        var browserExample = Path.Combine(tools, "browser_options.example.txt");
        if (!File.Exists(browser) && File.Exists(browserExample)) File.Copy(browserExample, browser);
    }

    private string PackagedPluginPath => Path.Combine(settings.BaseDirectory, "MusicBee Plugin", "mb_MusicBeeSyncToService.dll");

    /// <summary>Where the plugin is (or would be) installed: (program folder copy, AppData copy, chosen target).</summary>
    private (string ProgramCopy, string AppDataCopy, string Target) PluginLocations()
    {
        var name = Path.GetFileName(PackagedPluginPath);
        var musicBeeDirectory = Path.GetDirectoryName(settings.ResolvePath(musicBeeExe.Text.Trim())) ?? string.Empty;
        var programPlugin = Path.Combine(musicBeeDirectory, "Plugins", name);
        var appDataPlugin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBee", "Plugins", name);
        var looksPortable = File.Exists(Path.Combine(musicBeeDirectory, "MusicBee3Settings.ini")) ||
                            File.Exists(Path.Combine(musicBeeDirectory, "MusicBee.ini")) ||
                            musicBeeDirectory.StartsWith(settings.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        var existing = new[] { programPlugin, appDataPlugin }.FirstOrDefault(File.Exists);
        var targetDirectory = existing is not null
            ? Path.GetDirectoryName(existing)!
            : looksPortable ? Path.Combine(musicBeeDirectory, "Plugins") : Path.GetDirectoryName(appDataPlugin)!;
        return (programPlugin, appDataPlugin, Path.Combine(targetDirectory, name));
    }

    /// <summary>Copies the packaged plugin into MusicBee (MusicBee must be closed). Returns the installed path.</summary>
    private string InstallPluginFiles(List<string> removed)
    {
        var source = PackagedPluginPath;
        if (!File.Exists(source)) throw new FileNotFoundException("The packaged MusicBee plugin is missing. Re-extract the complete SpotySync portable folder.", source);
        var (programPlugin, appDataPlugin, target) = PluginLocations();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        foreach (var oldCopy in new[] { programPlugin, appDataPlugin }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(oldCopy) || Path.GetFullPath(oldCopy).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(oldCopy);
            removed.Add(oldCopy);
        }
        File.Copy(source, target, true);
        return target;
    }

    private void InstallMusicBeePlugin()
    {
        try
        {
            var removed = new List<string>();
            var target = InstallPluginFiles(removed);
            var replacement = removed.Count > 0
                ? "Removed/replaced the previous plugin copy:\r\n" + string.Join("\r\n", removed) + "\r\n\r\n"
                : "No previous SpotySync plugin copy was found.\r\n\r\n";
            var response = MessageBox.Show(this,
                replacement + "Installed the new bridge to:\r\n" + target +
                "\r\n\r\nRestart MusicBee to load it. Open the installed file location now?",
                "MusicBee plugin installed", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            AppendLog((removed.Count > 0 ? "Previous MusicBee plugin removed/replaced; " : "") + "new plugin installed to " + target);
            if (response == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message + "\r\n\r\nIf MusicBee is open, close it and retry.", "Could not install plugin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message + "\r\n\r\nThe existing plugin is in a protected MusicBee folder. Install MusicBee Plugin\\SpotySync-MusicBee-Plugin.zip from MusicBee Preferences > Plugins, or run SpotySync as administrator once to update it.", "Administrator permission needed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not install plugin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>Round swatch showing the current accent colour (click to pick).</summary>
internal sealed class AccentSwatch : ThemedControl
{
    public AccentSwatch() { Size = new Size(34, 34); Cursor = Cursors.Hand; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var ring = new Pen(Hovered ? p.Ink : p.Border, 2)) e.Graphics.DrawEllipse(ring, 2, 2, Width - 5, Height - 5);
        using var fill = new SolidBrush(p.Accent);
        e.Graphics.FillEllipse(fill, 6, 6, Width - 13, Height - 13);
    }
}
