using System.Diagnostics;
using SpotySync;

namespace SpotySyncApp;

public sealed partial class MainForm
{
    private static readonly (InsightKind Kind, string Label, string Glyph, string Help)[] InsightTabs =
    [
        (InsightKind.LikedMissingLocally, "Liked, not downloaded", Glyph.Heart, "Spotify Liked Songs with no matching file in your library. Right-click › Don't download to leave songs out for good."),
        (InsightKind.NoLocalPlaylist, "Local, in no playlist", Glyph.Music, "Local songs that are in none of your MusicBee/M3U playlists (“Why it's listed” says if one is in a Spotify playlist)."),
        (InsightKind.LikedNotInPlaylist, "Liked, in no playlist", Glyph.Spotify, "Spotify Liked Songs that are in none of your Spotify playlists."),
        (InsightKind.Duplicate, "Duplicates", Glyph.Copy, "Local files that look like the same recording (same title, artist and length)."),
    ];

    private readonly InsightTable libraryGrid = new();
    private DownloadExclusionStore? downloadExclusionStore;
    private DownloadExclusionStore DownloadExclusions => downloadExclusionStore ??= new DownloadExclusionStore(settings.DataDirectory);
    private readonly Dictionary<InsightKind, Chip> libraryChips = new();
    private readonly FieldBox librarySearch = new() { LeadingGlyph = Glyph.Search, ClearButton = true, Placeholder = "Filter results…", Width = 240, Height = 34 };
    private readonly Label libraryInfo = Ui.Label("", "muted", Fonts.Body(9.25f), autoSize: false);
    private readonly Label libraryHelp = Ui.Label("", "dim", Fonts.Body(9f), autoSize: false);
    private readonly ToggleSwitch libraryIgnoreSoundtracks = new() { Text = "Skip soundtrack folders", Checked = true };
    private readonly RoundedButton libraryDownload = RoundedButton.Make("Download missing", ButtonKind.Primary, Glyph.Download);
    private readonly RoundedButton librarySavePlaylist = RoundedButton.Make("Save as playlist", ButtonKind.Secondary, Glyph.List);
    private readonly RoundedButton librarySelectAll = RoundedButton.Make("Select all", ButtonKind.Secondary, Glyph.Check);
    /// <summary>Ticked rows (by song), kept across live refreshes. Nothing is ticked until you tick it.</summary>
    private readonly HashSet<string> libraryTicked = new(StringComparer.OrdinalIgnoreCase);

    private static string RowKey(InsightRow row) => row.Kind + "|" + (row.Path.Length > 0 ? row.Path : row.SpotifyId);

    private List<InsightRow> TickedInsights() => VisibleInsights().Where(r => !IsExcluded(r) && libraryTicked.Contains(RowKey(r))).ToList();
    private readonly Card libraryEmpty = new() { Title = "Find what's slipping through the cracks", Eyebrow = "Library tools", Glyph = Glyph.Library, Dock = DockStyle.Top, Height = 230 };
    private readonly Card libraryResults = new() { Dock = DockStyle.Fill, Inner = new Padding(10, 8, 6, 10) };
    private LibraryReport? libraryReport;
    private InsightKind libraryKind = InsightKind.LikedMissingLocally;

    private Control BuildLibrary()
    {
        var page = new Panel { BackColor = Color.Transparent };

        var toolbar = new WrapRow();
        var chips = new Row { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 0) };
        foreach (var tab in InsightTabs)
        {
            var chip = new Chip { Text = tab.Label, Glyph = tab.Glyph, Selected = tab.Kind == libraryKind, Margin = new Padding(0, 2, 6, 0) };
            var kind = tab.Kind;
            chip.Click += (_, _) => ShowInsight(kind);
            tips.SetToolTip(chip, tab.Help);
            libraryChips[kind] = chip;
            chips.Controls.Add(chip);
        }
        var right = new Row(rightToLeft: true) { Dock = DockStyle.Right, Width = 380 };
        var analyze = RoundedButton.Make("Analyze library", ButtonKind.Primary, Glyph.Refresh, async (_, _) => await AnalyseLibraryAsync(false));
        busyButtons.Add(analyze);
        tips.SetToolTip(analyze, "Reads your library and playlists (reuses the current sync preview when there is one). Read-only — nothing is changed.");
        right.Controls.Add(analyze);
        var upgrade = RoundedButton.Make("Upgrade quality", ButtonKind.Secondary, Glyph.Download, async (_, _) => await UpgradeLowQualityAsync());
        upgrade.Margin = new Padding(0, 0, 8, 0);
        busyButtons.Add(upgrade);
        tips.SetToolTip(upgrade, "Finds downloaded songs below ~200 kbps and re-downloads them at YouTube Premium quality (~256 kbps). Each file is replaced in place, so MusicBee playlists keep working.");
        right.Controls.Add(upgrade);
        toolbar.Absorb(chips, right);

        var actions = new WrapRow();
        var leftActions = new Row { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        librarySearch.Margin = new Padding(0, 0, 12, 0);
        librarySearch.TextChanged += (_, _) => FillLibraryGrid();
        libraryIgnoreSoundtracks.Width = libraryIgnoreSoundtracks.GetPreferredSize(Size.Empty).Width; libraryIgnoreSoundtracks.Margin = new Padding(0, 4, 12, 0);
        libraryIgnoreSoundtracks.CheckedChanged += (_, _) => { if (prepared is not null && libraryReport is not null) BuildLibraryReport(); };
        tips.SetToolTip(libraryIgnoreSoundtracks, "Leave out folders like Music\\Blockheads that Soundtrack Saver manages — soundtracks usually aren't in playlists on purpose.");
        libraryInfo.AutoSize = true; libraryInfo.Margin = new Padding(0, 9, 0, 0);
        leftActions.Controls.Add(librarySearch); leftActions.Controls.Add(libraryIgnoreSoundtracks); leftActions.Controls.Add(libraryInfo);
        var rightActions = new Row(rightToLeft: true) { Dock = DockStyle.Right, Width = 700, Padding = new Padding(0, 4, 0, 0) };
        libraryDownload.Click += async (_, _) => await DownloadMissingLikesAsync();
        busyButtons.Add(libraryDownload);
        librarySavePlaylist.Click += (_, _) => SaveInsightPlaylist();
        tips.SetToolTip(librarySavePlaylist, "Writes the ticked local files (or all shown, when none are ticked) to an M3U8 playlist in your M3U folder.");
        librarySelectAll.Click += (_, _) => ToggleSelectAll();
        tips.SetToolTip(librarySelectAll, "Tick every shown song — becomes Deselect once anything is ticked");
        var export = RoundedButton.Make("Export CSV", ButtonKind.Ghost, Glyph.Save, (_, _) => ExportInsightCsv());
        var copy = RoundedButton.Make("Copy", ButtonKind.Ghost, Glyph.Copy, (_, _) => CopyInsights());
        foreach (var b in new Control[] { libraryDownload, librarySavePlaylist, librarySelectAll, export, copy }) { b.Margin = new Padding(6, 0, 0, 0); rightActions.Controls.Add(b); }
        actions.Absorb(leftActions, rightActions);

        libraryHelp.Dock = DockStyle.Top; libraryHelp.Height = 24; libraryHelp.TextAlign = ContentAlignment.MiddleLeft;

        // Empty state
        var emptyBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        emptyBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); emptyBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var emptyText = Ui.Label("Analyze your library to find songs that aren't in any playlist (locally or on Spotify), Liked Songs you haven't downloaded yet, Likes that never made it into a playlist, and duplicate files. It is read-only: nothing is moved, deleted or changed.", "muted", Fonts.Body(10f), autoSize: false);
        emptyText.Dock = DockStyle.Fill;
        var start = RoundedButton.Make("Analyze library", ButtonKind.Primary, Glyph.Library, async (_, _) => await AnalyseLibraryAsync(false));
        start.Anchor = AnchorStyles.Left; busyButtons.Add(start);
        emptyBody.Controls.Add(emptyText, 0, 0); emptyBody.Controls.Add(start, 0, 1);
        libraryEmpty.Controls.Add(emptyBody);

        // Results grid
        libraryGrid.Dock = DockStyle.Fill;
        libraryGrid.Columns.Add(new("Artist", 80, r => r.Artist));
        libraryGrid.Columns.Add(new("Title", 110, r => r.Title));
        libraryGrid.Columns.Add(new("Album", 70, r => r.Album));
        libraryGrid.Columns.Add(new("Why it's listed", 85, r => IsExcluded(r) ? "Won't download (excluded)" : r.Detail, p => p.Muted));
        libraryGrid.Columns.Add(new("Location", 90, r => r.Path.Length > 0 ? Path.GetDirectoryName(r.Path) ?? "" : r.SpotifyUrl, p => p.Dim));
        libraryGrid.Dimmed = IsExcluded;
        libraryGrid.CheckToggled += rows =>
        {
            // flip as a group: if any is unticked, tick them all; otherwise untick them all
            var keys = rows.Where(r => !IsExcluded(r)).Select(RowKey).ToList();
            if (keys.Any(k => !libraryTicked.Contains(k))) libraryTicked.UnionWith(keys);
            else libraryTicked.ExceptWith(keys);
            UpdateLibraryActions();
        };
        libraryGrid.RowActivated += OpenInsight;
        var menu = Ui.Menu();
        var open = menu.Items.Add("Open", null, (_, _) => OpenInsight(libraryGrid.CurrentItem));
        var exclude = menu.Items.Add("Don't download (exclude)", null, (_, _) => SetDownloadExcluded(true));
        var include = menu.Items.Add("Allow downloading again", null, (_, _) => SetDownloadExcluded(false));
        menu.Items.Add("Copy selected", null, (_, _) => CopyInsights(selectedOnly: true));
        menu.Opening += (_, _) =>
        {
            var rows = libraryGrid.SelectedItems;
            open.Text = rows.FirstOrDefault() is { Path.Length: > 0 } ? "Show file in Explorer" : "Open in Spotify";
            var liked = libraryKind == InsightKind.LikedMissingLocally;
            exclude.Visible = liked && rows.Any(r => !IsExcluded(r));
            include.Visible = liked && rows.Any(IsExcluded);
        };
        libraryGrid.ContextMenuStrip = menu;
        libraryResults.Controls.Add(libraryGrid);

        var gap = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Color.Transparent };
        page.Controls.Add(libraryResults); page.Controls.Add(libraryEmpty); page.Controls.Add(libraryHelp); page.Controls.Add(gap); page.Controls.Add(actions); page.Controls.Add(toolbar);
        StyleLibraryGrid();
        UpdateLibraryState();
        return page;
    }

    private void StyleLibraryGrid()
    {
        libraryGrid.Invalidate(); // the table paints from the current palette
    }

    private bool IsExcluded(InsightRow row) => row.Kind == InsightKind.LikedMissingLocally && DownloadExclusions.Contains(row.SpotifyId);

    private void SetDownloadExcluded(bool excluded)
    {
        var rows = libraryGrid.SelectedItems.Where(r => r.SpotifyId.Length > 0).ToList();
        if (rows.Count == 0) return;
        if (excluded) DownloadExclusions.Add(rows.Select(r => (r.SpotifyId, r.DisplayName)));
        else DownloadExclusions.Remove(rows.Select(r => r.SpotifyId));
        AppendLog(excluded ? $"Won't download {rows.Count} Liked song(s) (Data\\download-exclusions.txt)." : $"{rows.Count} Liked song(s) can be downloaded again.");
        if (excluded) libraryTicked.ExceptWith(rows.Select(RowKey));
        UpdateLibraryState();
        libraryGrid.RefreshRows(); // rows stay in place (and selected); only their look changes
    }

    private void ToggleSelectAll()
    {
        var shown = VisibleInsights().Where(r => !IsExcluded(r)).ToList();
        if (TickedInsights().Count > 0) libraryTicked.ExceptWith(shown.Select(RowKey));
        else libraryTicked.UnionWith(shown.Select(RowKey));
        libraryGrid.RefreshRows();
        UpdateLibraryActions();
    }

    /// <summary>Select all ⇄ Deselect, and the Download button's count.</summary>
    private void UpdateLibraryActions()
    {
        var ticked = TickedInsights().Count;
        librarySelectAll.Text = ticked > 0 ? $"Deselect ({ticked:N0})" : "Select all";
        librarySelectAll.Glyph = ticked > 0 ? Glyph.Clear : Glyph.Check;
        libraryDownload.Text = ticked > 0 ? $"Download {ticked:N0} selected" : "Download selected";
        libraryDownload.Enabled = cancellation is null && ticked > 0;
        tips.SetToolTip(libraryDownload, ticked > 0 ? "Download the ticked Liked songs" : "Tick songs (or click Select all) to choose what to download");
    }

    private void UpdateLibraryState()
    {
        var has = libraryReport is not null;
        libraryEmpty.Visible = !has;
        libraryResults.Visible = has;
        libraryHelp.Text = InsightTabs.First(t => t.Kind == libraryKind).Help;
        foreach (var (kind, chip) in libraryChips)
        {
            chip.Selected = kind == libraryKind;
            chip.Count = libraryReport?.Get(kind).Count(r => !IsExcluded(r));
        }
        libraryDownload.Visible = libraryKind == InsightKind.LikedMissingLocally;
        librarySavePlaylist.Visible = libraryKind is not (InsightKind.LikedMissingLocally or InsightKind.LikedNotInPlaylist);
        libraryInfo.Text = libraryReport is null ? "" :
            $"{libraryReport.LibraryCount:N0} songs · {libraryReport.LocalPlaylistCount} local and {libraryReport.SpotifyPlaylistCount} Spotify playlists · updated {libraryReport.CreatedAt:HH:mm}";
        UpdateLibraryActions();
        RefreshDashboard();
    }

    private void ShowInsight(InsightKind kind)
    {
        libraryKind = kind;
        UpdateLibraryState();
        FillLibraryGrid();
    }

    private IEnumerable<string> SoundtrackFolders()
    {
        var root = settings.ResolvePath(settings.SoundtrackFolder);
        var music = settings.ResolvePath(settings.MusicFolder).TrimEnd('\\') + "\\";
        if (!Directory.Exists(root)) return Array.Empty<string>();
        try
        {
            return Directory.GetDirectories(root)
                .Where(d => !music.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .Where(d => !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).Equals("Playlists", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private async Task AnalyseLibraryAsync(bool rescan)
    {
        if (cancellation is not null) return;
        if (prepared is null || rescan)
        {
            if (!SaveSettingsFields(false)) return;
            await RunBusyAsync("Analyzing library and playlists", async token =>
            {
                prepared = await service.PrepareAsync(forceRefresh.Checked, forceRefresh.Checked, token);
                planAppliedAt = null;
                PopulatePlan(prepared.Plan);
                BuildLibraryReport();
                SavePreviewSnapshot(newlyBuilt: true);
            });
        }
        else
        {
            // re-read the music folder first, so songs downloaded since the preview was built are included
            await RefreshLibraryLiveAsync();
            BuildLibraryReport();
            SavePreviewSnapshot();
            AppendLog("Library analyzed (music folder re-checked; Spotify data from the current sync preview).");
        }
        if (!StayOnDashboard) SelectPage(PageLibrary);
    }

    private void BuildLibraryReport(bool quiet = false)
    {
        if (prepared is null) return;
        libraryReport = LibraryInsights.Build(prepared, libraryIgnoreSoundtracks.Checked ? SoundtrackFolders() : null);
        if (!quiet)
            AppendLog($"Library insights: {libraryReport.Unplaylisted.Count} song(s) in no playlist, {libraryReport.LikedMissingLocally.Count} Liked song(s) not downloaded, " +
                      $"{libraryReport.LikedNotInPlaylist.Count} Like(s) in no playlist, {libraryReport.Duplicates.Count} duplicate file(s).");
        UpdateLibraryState();
        FillLibraryGrid(preserveView: quiet);
    }

    private List<InsightRow> VisibleInsights()
    {
        if (libraryReport is null) return new List<InsightRow>();
        var words = librarySearch.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return libraryReport.Get(libraryKind).Where(r => words.All(w =>
            r.Artist.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
            r.Album.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void FillLibraryGrid(bool preserveView = false)
    {
        // tick = selected for the actions (download, save as playlist, copy, export); excluded songs have no box
        libraryGrid.Checked = r => IsExcluded(r) ? null : libraryTicked.Contains(RowKey(r));
        if (preserveView) libraryGrid.ReplaceItems(VisibleInsights(), RowKey);
        else libraryGrid.Items = VisibleInsights();
        UpdateLibraryActions();
    }

    /// <summary>The ticked rows; with none ticked, the highlighted rows (when asked) or everything shown.</summary>
    private List<InsightRow> SelectedOrAll(bool selectedOnly)
    {
        var ticked = TickedInsights();
        if (ticked.Count > 0) return ticked;
        if (selectedOnly && libraryGrid.SelectedCount > 0) return libraryGrid.SelectedItems;
        return VisibleInsights();
    }

    private void CopyInsights(bool selectedOnly = false)
    {
        var rows = SelectedOrAll(selectedOnly);
        if (rows.Count == 0) return;
        Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.DisplayName + "\t" + (r.Path.Length > 0 ? r.Path : r.SpotifyUrl))));
        statusText.Detail = $"Copied {rows.Count} row(s) to the clipboard";
    }

    private void OpenInsight(InsightRow? row)
    {
        if (row is null) return;
        if (row.Path.Length > 0 && File.Exists(row.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + row.Path + "\"") { UseShellExecute = true });
        else if (row.SpotifyId.Length > 0) OpenInSpotify(row.SpotifyId);
    }

    /// <summary>Opens a track in the Spotify desktop app when it is installed (spotify: links), otherwise in the browser.</summary>
    private static void OpenInSpotify(string trackId)
    {
        try
        {
            using var handler = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"spotify\shell\open\command");
            if (handler?.GetValue(null) is string { Length: > 0 })
            {
                Process.Start(new ProcessStartInfo("spotify:track:" + trackId) { UseShellExecute = true });
                return;
            }
        }
        catch { }
        OpenPath("https://open.spotify.com/track/" + trackId);
    }

    private string InsightFileStem() => "SpotySync - " + InsightTabs.First(t => t.Kind == libraryKind).Label + " " + DateTime.Now.ToString("yyyy-MM-dd");

    private void SaveInsightPlaylist()
    {
        var rows = SelectedOrAll(false).Where(r => r.Path.Length > 0).ToList();
        if (rows.Count == 0) { MessageBox.Show(this, "There are no local files in this list.", "SpotySync", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try
        {
            var path = Path.Combine(settings.ResolvePath(settings.M3uFolder), InsightFileStem() + ".m3u8");
            LibraryInsights.WriteM3u(path, rows);
            AppendLog($"Saved {rows.Count} song(s) to playlist {path}");
            if (MessageBox.Show(this, $"Saved {rows.Count} song(s) to:\r\n{path}\r\n\r\nShow it in Explorer?", "Playlist saved", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save playlist", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ExportInsightCsv()
    {
        var rows = SelectedOrAll(false);
        if (rows.Count == 0) return;
        using var dialog = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = InsightFileStem() + ".csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { LibraryInsights.WriteCsv(dialog.FileName, rows); AppendLog($"Exported {rows.Count} row(s) to {dialog.FileName}"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not export", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task UpgradeLowQualityAsync()
    {
        if (MessageBox.Show(this,
                "Re-download songs that came out below ~200 kbps at YouTube Premium quality (~256 kbps)?\r\n\r\n" +
                "SpotySync first checks that YouTube is offering Premium audio and stops if it isn't. A song is only replaced " +
                "when the new copy is better, and it runs at the same account-safe pace as normal downloads. You can cancel at any time.",
                "Upgrade audio quality", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!SaveSettingsFields(false)) return;
        await RunBusyAsync("Upgrading low-quality songs", async token =>
        {
            var result = await service.UpgradeLowQualityAsync(token);
            if (result.Error.Length > 0) AppendLog("WARNING: " + result.Error);
            SetResult(result.Summary, result.Success ? Led.Ok : Led.Warn);
        });
    }

    private async Task DownloadMissingLikesAsync()
    {
        var rows = TickedInsights().Where(r => r.SpotifyId.Length > 0).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Tick the songs to download first (or click Select all).", "Download missing Likes", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Download {rows.Count} ticked Liked song(s) that aren't in your library yet?\r\n\r\nYou can pause or cancel at any time; each finished song is added to the library right away.",
                "Download missing Likes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!SaveSettingsFields(false)) return;
        await RunBusyAsync($"Downloading {rows.Count} missing Liked song(s)", async token =>
        {
            var result = await service.DownloadLinksAsync(rows.Select(r => r.SpotifyUrl), token);
            if (!result.Success) AppendLog("WARNING: " + result.Error);
            SetResult($"Missing Likes: {result.NewAudioFiles} new audio file(s); {result.ImportedFiles} verified in MusicBee.", result.Success ? Led.Ok : Led.Warn);
        });
    }
}
