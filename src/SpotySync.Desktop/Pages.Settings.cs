using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SpotySync;

namespace SpotySyncDesktop;

public sealed partial class MainWindow
{
    // ------------------------------------------------------------------ Console
    private readonly ObservableCollection<LogLine> visibleLog = new();
    private ListBox? logList;
    private bool followLog = true;
    private string logLevel = "All";
    private string logSearch = "";
    private bool logDetail = true;

    private bool LogFilterPasses(LogLine line)
    {
        if (!logDetail && line.Text.StartsWith("download |", StringComparison.Ordinal) && line.Color != Pal.Err && line.Color != Pal.Warn) return false;
        if (logLevel == "Warnings" && line.Color != Pal.Warn && line.Color != Pal.Err) return false;
        if (logLevel == "Errors" && line.Color != Pal.Err) return false;
        return logSearch.Length == 0 || line.Text.Contains(logSearch, StringComparison.OrdinalIgnoreCase);
    }

    private void RefilterLog()
    {
        visibleLog.Clear();
        foreach (var line in logLines.Where(LogFilterPasses)) visibleLog.Add(line);
        if (visibleLog.Count > 0) logList?.ScrollIntoView(visibleLog[^1]);
    }

    private Control BuildConsolePage()
    {
        var level = new ComboBox { ItemsSource = new[] { "All", "Warnings", "Errors" }, SelectedItem = logLevel, Width = 130 };
        level.SelectionChanged += (_, _) => { logLevel = level.SelectedItem as string ?? "All"; RefilterLog(); };
        var search = new TextBox { Watermark = "Filter", Width = 240, Text = logSearch };
        search.TextChanged += (_, _) => { logSearch = search.Text?.Trim() ?? ""; RefilterLog(); };
        var detail = new CheckBox { Content = "Downloader detail", IsChecked = logDetail, Foreground = Pal.Muted };
        detail.IsCheckedChanged += (_, _) => { logDetail = detail.IsChecked == true; RefilterLog(); };
        var follow = new CheckBox { Content = "Follow", IsChecked = followLog, Foreground = Pal.Muted };
        follow.IsCheckedChanged += (_, _) => followLog = follow.IsChecked == true;
        var copy = Ui.Button("Copy", () => _ = Clipboard?.SetTextAsync(string.Join("\n", visibleLog.Select(l => l.Time + "  " + l.Text))));
        var logs = Ui.Button("Open logs folder", () => { Directory.CreateDirectory(settings.LogDirectory); Ui.Open(settings.LogDirectory); });
        var clear = Ui.Button("Clear", () => { logLines.Clear(); visibleLog.Clear(); });
        var toolbar = Ui.Row(10, level, search, detail, follow, copy, logs, clear);
        toolbar.Margin = new Thickness(20, 16, 20, 10);

        logList = new ListBox
        {
            ItemsSource = visibleLog, Background = Pal.Surface, Margin = new Thickness(20, 0, 20, 16),
            ItemTemplate = new FuncDataTemplate<LogLine>((line, _) => line is null ? new TextBlock() : new SelectableTextBlock
            {
                Inlines = new Avalonia.Controls.Documents.InlineCollection
                {
                    new Avalonia.Controls.Documents.Run(line.Time + "  ") { Foreground = Pal.Dim },
                    new Avalonia.Controls.Documents.Run(line.Text) { Foreground = line.Color }
                },
                FontFamily = Pal.Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap
            })
        };
        var panel = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        panel.Children.Add(toolbar);
        panel.Children.Add(logList);
        if (visibleLog.Count > 0) Avalonia.Threading.Dispatcher.UIThread.Post(() => logList.ScrollIntoView(visibleLog[^1]));
        return panel;
    }

    // ------------------------------------------------------------------ Settings
    private static readonly string[] Browsers = ["Automatic (default browser)", "firefox", "chrome", "brave", "chromium", "vivaldi", "opera", "None (signed out, ~128 kbps)"];

    private Control BuildSettingsPage()
    {
        var page = Ui.Column(16);
        page.Children.Add(Ui.Column(2, Ui.Title("Settings"), Ui.Muted($"Saved in {settings.FilePath}")));

        var clientId = new TextBox { Text = settings.SpotifyClientId, Width = 420 };
        var redirect = new TextBox { Text = settings.RedirectUri, Width = 420 };
        var connected = service.HasSavedLogin;
        System.Action saveFields = () => { }; // set below, once every field exists
        var spotify = Ui.Column(12, Ui.Heading("Spotify"),
            Ui.Field("Client ID", clientId, "From your app at developer.spotify.com/dashboard."),
            Ui.Field("Redirect URI", redirect, "Must also be listed on your Spotify app. On Linux use one with a port, e.g. http://127.0.0.1:8888/callback (port 80 needs admin rights here)."),
            Ui.Row(10, Action(connected ? "Sign in again" : "Connect Spotify", async () => { saveFields(); await ConnectAsync(); }, primary: !connected),
                connected ? Ui.Button("Disconnect", () => { service.DisconnectSpotify(); SetResult("Spotify disconnected.", Pal.Warn); Refresh(); }) : new Control()));
        page.Children.Add(Ui.Card(spotify));

        var music = new TextBox { Text = settings.MusicFolder, Width = 420 };
        var playlists = new TextBox { Text = settings.M3uFolder, Width = 420 };
        var format = new ComboBox { ItemsSource = Downloader.FormatNames, SelectedItem = Downloader.FormatNames.FirstOrDefault(f => f.Equals(settings.DownloadFormat, StringComparison.OrdinalIgnoreCase)) ?? "Opus", Width = 160 };
        var browser = new ComboBox { ItemsSource = Browsers, SelectedItem = CurrentBrowserChoice(), Width = 300 };
        page.Children.Add(Ui.Card(Ui.Column(12, Ui.Heading("Folders and downloads"),
            Ui.Field("Music folder", Ui.Row(8, music, Ui.Button("Browse…", () => _ = PickFolderAsync(music)))),
            Ui.Field("Playlists folder (M3U)", Ui.Row(8, playlists, Ui.Button("Browse…", () => _ = PickFolderAsync(playlists))), "Synced playlists are written here as .m3u8 files for Strawberry."),
            Ui.Field("Download format", format, "Opus keeps YouTube's audio unchanged; MP3 is 320 kbps and plays everywhere; M4A is AAC."),
            Ui.Field("Browser for YouTube Premium audio", browser, "Its signed-in YouTube session gives the ~256 kbps streams. Firefox works best (the Flatpak Firefox on Bazzite is found automatically)."))));

        var direction = new ComboBox { ItemsSource = Enum.GetNames<SyncDirection>(), SelectedItem = settings.Direction.ToString(), Width = 200 };
        var removals = new CheckBox { Content = "Allow removals (songs removed on one side are removed on the other)", IsChecked = settings.EnableRemovals, Foreground = Pal.Ink };
        var maxRemovals = new NumericUpDown { Value = settings.MaxRemovalsPerPlaylist, Minimum = 0, Maximum = 10000, Width = 140, FormatString = "0" };
        var owned = new CheckBox { Content = "Only sync playlists I own", IsChecked = settings.OwnedPlaylistsOnly, Foreground = Pal.Ink };
        var include = new TextBox { Text = settings.PlaylistInclude, Width = 420 };
        var exclude = new TextBox { Text = settings.PlaylistExclude, Width = 420 };
        page.Children.Add(Ui.Card(Ui.Column(12, Ui.Heading("Sync"),
            Ui.Field("Direction", direction, "TwoWay keeps both sides in step; the other two copy one way only."),
            removals, Ui.Field("Most removals per playlist", maxRemovals),
            owned,
            Ui.Field("Playlists to include", include, "Names separated by ; and * as a wildcard."),
            Ui.Field("Playlists to leave out", exclude))));

        void SaveFields()
        {
            settings.SpotifyClientId = clientId.Text?.Trim() ?? "";
            settings.RedirectUri = redirect.Text?.Trim() ?? settings.RedirectUri;
            settings.MusicFolder = music.Text?.Trim() ?? settings.MusicFolder;
            settings.M3uFolder = playlists.Text?.Trim() ?? settings.M3uFolder;
            settings.DownloadFormat = format.SelectedItem as string ?? "Opus";
            settings.Direction = Enum.TryParse<SyncDirection>(direction.SelectedItem as string, out var d) ? d : settings.Direction;
            settings.EnableRemovals = removals.IsChecked == true;
            settings.MaxRemovalsPerPlaylist = (int)(maxRemovals.Value ?? settings.MaxRemovalsPerPlaylist);
            settings.OwnedPlaylistsOnly = owned.IsChecked == true;
            settings.PlaylistInclude = include.Text?.Trim() ?? "*";
            settings.PlaylistExclude = exclude.Text?.Trim() ?? "";
            SaveBrowserChoice(browser.SelectedItem as string);
            settings.Save();
            if (cancellation is null)
            {
                service.Dispose();
                service = new SpotySyncService(settings, AppendLog);
            }
        }

        saveFields = SaveFields;
        var save = Action("Save settings", () =>
        {
            try { SaveFields(); SetResult("Settings saved."); }
            catch (Exception ex) { SetResult("Could not save the settings: " + ex.Message, Pal.Err); }
            return Task.CompletedTask;
        }, primary: true);
        page.Children.Add(Ui.Row(10, save, Ui.Button("Open data folder", () => Ui.Open(settings.DataDirectory)),
            Ui.Button("Open SpotySync folder", () => Ui.Open(settings.BaseDirectory))));
        return page;
    }

    private async Task PickFolderAsync(TextBox target)
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(settings.ResolvePath(target.Text ?? ""));
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pick a folder", SuggestedStartLocation = start });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) target.Text = path;
    }

    private string BrowserOptionsPath => Path.Combine(settings.ResolvePath("Tools"), "browser_options.txt");

    private string CurrentBrowserChoice()
    {
        try
        {
            if (!File.Exists(BrowserOptionsPath)) return Browsers[0];
            var values = File.ReadAllLines(BrowserOptionsPath).Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            var chosen = Browsers.Skip(1).Take(Browsers.Length - 2).FirstOrDefault(b => values.GetValueOrDefault(b));
            if (chosen is not null) return chosen;
            return values.GetValueOrDefault("auto") ? Browsers[0] : Browsers[^1];
        }
        catch { return Browsers[0]; }
    }

    private void SaveBrowserChoice(string? choice)
    {
        choice ??= Browsers[0];
        var lines = new List<string>
        {
            "# Which browser's signed-in YouTube session (YouTube Premium = ~256 kbps audio) downloads use. Set by SpotySync's Settings page.",
            "AUTO=" + (choice == Browsers[0] ? "true" : "false")
        };
        foreach (var name in new[] { "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "chromium" })
            lines.Add(name.ToUpperInvariant() + "=" + (choice == name ? "true" : "false"));
        Directory.CreateDirectory(Path.GetDirectoryName(BrowserOptionsPath)!);
        File.WriteAllLines(BrowserOptionsPath, lines);
    }
}
