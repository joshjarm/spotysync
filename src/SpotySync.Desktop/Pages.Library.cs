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
    private InsightKind libraryKind = InsightKind.LikedMissingLocally;
    private string librarySearch = "";

    private Control BuildLibraryPage()
    {
        var page = Ui.Column(16);
        page.Children.Add(Ui.Column(2, Ui.Title("Library"),
            Ui.Muted("What's missing, unplaylisted or duplicated, worked out from your music folder and Spotify.")));
        if (report is null || prepared is null)
        {
            page.Children.Add(Ui.Card(Ui.Column(10, Ui.Muted("Build a sync preview first: it reads your library and Spotify, and the Library page is worked out from it."),
                Action("Build preview", PreviewAsync, primary: true))));
            return page;
        }

        var kinds = new (InsightKind Kind, string Label)[]
        {
            (InsightKind.LikedMissingLocally, "Liked, not downloaded"), (InsightKind.Unplaylisted, "In no playlist"),
            (InsightKind.LikedNotInPlaylist, "Likes in no playlist"), (InsightKind.NoLocalPlaylist, "Not in a local playlist"), (InsightKind.Duplicate, "Duplicates")
        };
        var tabs = new WrapPanel();
        foreach (var (kind, label) in kinds)
        {
            var count = report.Get(kind).Count;
            var tab = Ui.Button($"{label}  {count:N0}", () => { libraryKind = kind; Refresh(); }, primary: kind == libraryKind);
            tab.Margin = new Thickness(0, 0, 8, 8);
            tabs.Children.Add(tab);
        }
        page.Children.Add(tabs);

        var rows = report.Get(libraryKind);
        var words = librarySearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var visible = rows.Where(r => words.All(w => r.Artist.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
            || r.Album.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var search = new TextBox { Text = librarySearch, Watermark = "Search artist, title or album", Width = 320 };
        search.KeyUp += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { librarySearch = search.Text ?? ""; Refresh(); } };
        var list = new ListBox
        {
            ItemsSource = visible, SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle, Background = Brushes.Transparent, MaxHeight = 520,
            ItemTemplate = new FuncDataTemplate<InsightRow>((r, _) => r is null ? new TextBlock() : InsightRowView(r))
        };
        var downloadable = visible.Where(r => r.SpotifyUrl.Length > 0).ToList();
        var actions = Ui.Row(10, search);
        if (downloadable.Count > 0)
        {
            actions.Children.Add(Action("Download selected", () =>
            {
                var links = list.SelectedItems?.OfType<InsightRow>().Where(r => r.SpotifyUrl.Length > 0).Select(r => r.SpotifyUrl).ToList() ?? new();
                return links.Count == 0 ? Task.CompletedTask : DownloadLinksAsync($"Downloading {links.Count} selected song(s)", links);
            }));
            actions.Children.Add(Action($"Download all {downloadable.Count:N0}", () => DownloadLinksAsync($"Downloading {downloadable.Count} song(s)", downloadable.Select(r => r.SpotifyUrl).ToList()), primary: true));
        }
        if (visible.Any(r => r.Path.Length > 0))
            actions.Children.Add(Ui.Button("Save as M3U playlist", () => _ = SaveInsightPlaylistAsync(visible)));
        page.Children.Add(actions);
        page.Children.Add(Ui.Muted(visible.Count == rows.Count ? $"{rows.Count:N0} song(s)." : $"{visible.Count:N0} of {rows.Count:N0} song(s) match the search.", 12));
        page.Children.Add(Ui.Card(list, 8));
        return page;
    }

    private static Control InsightRowView(InsightRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1.4*,*"), Margin = new Thickness(4, 2) };
        var name = new TextBlock { Text = row.DisplayName, Foreground = Pal.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
        var album = new TextBlock { Text = row.Album, Foreground = Pal.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        var detail = new TextBlock { Text = row.Detail.Length > 0 ? row.Detail : row.Path.Length > 0 ? Path.GetFileName(row.Path) : "", Foreground = Pal.Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(album, 1); Grid.SetColumn(detail, 2);
        grid.Children.Add(name); grid.Children.Add(album); grid.Children.Add(detail);
        return grid;
    }

    private async Task SaveInsightPlaylistAsync(IReadOnlyList<InsightRow> rows)
    {
        var folder = settings.ResolvePath(settings.M3uFolder);
        Directory.CreateDirectory(folder);
        var start = await StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as playlist", SuggestedFileName = "SpotySync - " + libraryKind + ".m3u8", DefaultExtension = "m3u8", SuggestedStartLocation = start
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        LibraryInsights.WriteM3u(path, rows.Where(r => r.Path.Length > 0));
        SetResult("Saved " + Path.GetFileName(path) + ".");
    }

    // ------------------------------------------------------------------ Download page
    private Control BuildDownloadPage()
    {
        var page = Ui.Column(16);
        page.Children.Add(Ui.Column(2, Ui.Title("Download"),
            Ui.Muted("Paste Spotify songs, albums or playlists, or YouTube links. One per line.")));
        var links = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, Watermark = "https://open.spotify.com/track/…\nhttps://open.spotify.com/playlist/…\nhttps://music.youtube.com/watch?v=…" };
        page.Children.Add(Ui.Card(Ui.Column(10, links, Action("Download", () =>
        {
            var parsed = ParseLinks(links.Text);
            if (parsed.Count == 0) { SetResult("Paste at least one link.", Pal.Warn); return Task.CompletedTask; }
            return DownloadLinksAsync($"Downloading {parsed.Count} link(s)", parsed);
        }, primary: true))));

        // songs YouTube Music didn't have
        var missing = NotFoundSongs.Load(settings).Songs.Values.OrderBy(s => s.Artists, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var section = Ui.Column(10, Ui.Heading($"Not on YouTube Music ({missing.Count})"),
            Ui.Muted("YouTube Music's search found no match for these, so later downloads leave them out for 30 days. Paste a YouTube link next to a song to download that video instead, or search them all again."));
        var fields = new Dictionary<string, TextBox>();
        foreach (var song in missing.Take(400))
        {
            var field = new TextBox { Text = song.ManualUrl, Watermark = "YouTube link", Width = 320 };
            fields[song.SpotifyId] = field;
            var name = new TextBlock { Text = song.DisplayName, Foreground = Pal.Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            var searchButton = Ui.Button("Search", () => Ui.Open("https://music.youtube.com/search?q=" + Uri.EscapeDataString(song.DisplayName)));
            var forget = Ui.Button("Forget", () => { NotFoundSongs.Forget(settings, [song.SpotifyId]); Refresh(); });
            searchButton.Margin = forget.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(field, 1); Grid.SetColumn(searchButton, 2); Grid.SetColumn(forget, 3);
            row.Children.Add(name); row.Children.Add(field); row.Children.Add(searchButton); row.Children.Add(forget);
            section.Children.Add(row);
        }
        if (missing.Count > 0)
        {
            section.Children.Add(Ui.Row(10,
                Action("Save links and download them", () =>
                {
                    foreach (var (id, field) in fields) NotFoundSongs.SetManualUrl(settings, id, (field.Text ?? "").Trim());
                    var linked = missing.Where(s => fields.TryGetValue(s.SpotifyId, out var f) && (f.Text ?? "").Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        .Select(s => s.SpotifyUrl).ToList();
                    if (linked.Count == 0) { SetResult("Paste a YouTube link next to a song first.", Pal.Warn); return Task.CompletedTask; }
                    return DownloadLinksAsync($"Downloading {linked.Count} song(s) from your YouTube links", linked);
                }, primary: true),
                Action("Search all again", () =>
                {
                    // clearing the last search date lets the download include them again
                    NotFoundSongs.Update(settings, l => { foreach (var s in l.Songs.Values) s.LastTried = DateTimeOffset.MinValue; });
                    return DownloadLinksAsync($"Searching {missing.Count} song(s) again", missing.Select(s => s.SpotifyUrl).ToList());
                })));
        }
        page.Children.Add(Ui.Card(section));
        return page;
    }

    private static List<string> ParseLinks(string? text) =>
        (text ?? "").Split(new[] { '\n', '\r', ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.StartsWith("http", StringComparison.OrdinalIgnoreCase) || x.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
