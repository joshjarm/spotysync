using SpotySync;

namespace SpotySyncApp;

public sealed partial class MainForm
{
    private readonly SmoothGrid planGrid = new();
    private readonly SmoothRichText details = new();
    private readonly SplitContainer previewSplit = new();
    private readonly RoundedButton expandDetailsButton = RoundedButton.Make("Expand details", ButtonKind.Ghost, Glyph.Expand);
    private readonly RoundedButton ignoreChangesButton = RoundedButton.Make("Never sync…", ButtonKind.Secondary, Glyph.Filter);
    private readonly RoundedButton previewApplyButton = RoundedButton.Make("Apply plan", ButtonKind.Primary, Glyph.Check);
    private readonly FieldBox previewSearch = new() { LeadingGlyph = Glyph.Search, ClearButton = true, Placeholder = "Filter playlists…", Width = 250, Height = 34 };
    private readonly Chip changesOnly = new() { Text = "With changes", Glyph = Glyph.Sync };
    private readonly Chip attentionOnly = new() { Text = "Needs attention", Glyph = Glyph.Warning };
    private readonly KpiStrip previewKpis = new() { Dock = DockStyle.Top, Height = 66 };
    private readonly Card previewEmpty = new() { Title = "No preview yet", Eyebrow = "Nothing to review", Glyph = Glyph.Eye, Dock = DockStyle.Top, Height = 190 };
    private readonly Panel previewBody = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private Dictionary<string, TrackInfo> previewSpotifyTracks = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LocalTrack> previewLocalTracks = new(StringComparer.OrdinalIgnoreCase);
    private bool detailsExpanded;
    private bool previewSplitInitialized;
    private int splitBeforeExpand;
    private Action<double>? splitAnimation;
    private Label? previewNote;
    /// <summary>Set once the shown plan has been applied: it stays visible for reference but can't be applied twice.</summary>
    private DateTime? planAppliedAt;
    private const string DefaultPreviewNote = "Track-level changes  •  drag the divider to resize";

    private Control BuildPreview()
    {
        var page = new Panel { BackColor = Color.Transparent };

        // Toolbar
        var toolbar = new WrapRow();
        var left = new Row { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 0) };
        previewSearch.Margin = new Padding(0, 0, 10, 0);
        previewSearch.TextChanged += (_, _) => FilterPlanRows();
        changesOnly.Click += (_, _) => { changesOnly.Selected = !changesOnly.Selected; FilterPlanRows(); };
        attentionOnly.Click += (_, _) => { attentionOnly.Selected = !attentionOnly.Selected; FilterPlanRows(); };
        changesOnly.Margin = attentionOnly.Margin = new Padding(0, 2, 6, 0);
        tips.SetToolTip(changesOnly, "Hide playlists that are already in sync");
        tips.SetToolTip(attentionOnly, "Only playlists with removals, warnings or a safety block");
        left.Controls.Add(previewSearch); left.Controls.Add(changesOnly); left.Controls.Add(attentionOnly);
        var right = new Row(rightToLeft: true) { Dock = DockStyle.Right, Width = 340 };
        previewApplyButton.Enabled = false; previewApplyButton.Margin = new Padding(6, 0, 0, 0);
        previewApplyButton.Click += async (_, _) => await ApplyAsync();
        var rebuild = RoundedButton.Make("Rebuild preview", ButtonKind.Secondary, Glyph.Refresh, async (_, _) => await PreviewAsync());
        rebuild.Margin = new Padding(6, 0, 0, 0);
        busyButtons.Add(rebuild);
        tips.SetToolTip(rebuild, "Re-read Spotify and your library (F5)");
        right.Controls.Add(previewApplyButton); right.Controls.Add(rebuild);
        toolbar.Absorb(left, right);

        // KPIs
        previewKpis.Add("playlists", "Playlists");
        previewKpis.Add("changes", "Changes");
        previewKpis.Add("spotify", "Spotify +");
        previewKpis.Add("local", "Local +");
        previewKpis.Add("removals", "Removals");
        previewKpis.Add("protected", "Protected");
        previewKpis.Add("blocked", "Blocked");

        // Empty state
        var emptyBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        emptyBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); emptyBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var emptyText = Ui.Label("Build a preview to see exactly what would be added to Spotify and to your local playlists. Nothing changes until you apply the reviewed plan.", "muted", Fonts.Body(10f), autoSize: false);
        emptyText.Dock = DockStyle.Fill;
        var build = RoundedButton.Make("Build sync preview", ButtonKind.Primary, Glyph.Eye, async (_, _) => await PreviewAsync());
        build.Anchor = AnchorStyles.Left;
        busyButtons.Add(build);
        emptyBody.Controls.Add(emptyText, 0, 0); emptyBody.Controls.Add(build, 0, 1);
        previewEmpty.Controls.Add(emptyBody);

        // Grid + details
        previewSplit.Dock = DockStyle.Fill;
        previewSplit.Orientation = Orientation.Horizontal;
        previewSplit.SplitterWidth = 12;
        previewSplit.IsSplitterFixed = false;
        previewSplit.SizeChanged += (_, _) => InitializePreviewSplitter();
        previewSplit.Paint += (_, e) =>
        {
            var r = previewSplit.SplitterRectangle;
            using var grip = new SolidBrush(Theme.P.Border);
            var cx = r.X + r.Width / 2;
            e.Graphics.FillRectangle(grip, cx - 18, r.Y + r.Height / 2 - 1, 36, 3);
        };
        planGrid.Dock = DockStyle.Fill;
        planGrid.BorderStyle = BorderStyle.None;
        planGrid.EnableHeadersVisualStyles = false;
        planGrid.RowHeadersVisible = false; planGrid.AutoGenerateColumns = false; planGrid.AllowUserToAddRows = false; planGrid.AllowUserToDeleteRows = false; planGrid.ReadOnly = true;
        planGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; planGrid.MultiSelect = false;
        planGrid.AllowUserToResizeColumns = false; planGrid.AllowUserToOrderColumns = false; planGrid.AllowUserToResizeRows = false;
        planGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        planGrid.ColumnHeadersHeight = 38; planGrid.RowTemplate.Height = 32;
        planGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        planGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Playlist", Name = "Playlist", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 180, Resizable = DataGridViewTriState.False });
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Baseline", Name = "Baseline", Width = 92 });
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Spotify +", Name = "SpotifyAdd", Width = 86 });
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Spotify −", Name = "SpotifyRemove", Width = 86 });
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Local +", Name = "LocalAdd", Width = 78 });
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Local −", Name = "LocalRemove", Width = 78 });
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Protected", Name = "Protected", Width = 96 });
        planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", Name = "Status", Width = 132 });
        foreach (DataGridViewColumn column in planGrid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            if (column.Index > 0) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            if (column.Index > 0) column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        planGrid.CurrentCellChanged += (_, _) => ShowSelectedDetails();
        planGrid.CellDoubleClick += (_, _) => ToggleDetails();
        planGrid.HandleCreated += (_, _) => { foreach (Control c in planGrid.Controls) Ui.DarkScrollbars(c); };
        details.Dock = DockStyle.Fill; details.ReadOnly = true; details.BorderStyle = BorderStyle.None; details.DetectUrls = false;
        details.ScrollBars = RichTextBoxScrollBars.Vertical;
        Ui.DarkScrollbars(details);

        var gridCard = new Card { Dock = DockStyle.Fill, Inner = new Padding(10, 8, 6, 10) };
        gridCard.Controls.Add(new GridScrollHost(planGrid) { Dock = DockStyle.Fill });
        gridCard.Controls.Add(new GridHeader(planGrid) { Dock = DockStyle.Top });

        var detailCard = new Card { Dock = DockStyle.Fill, Inner = new Padding(14, 8, 8, 10) };
        var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var detailToolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent };
        detailToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); detailToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0)); // never-sync is now the row checkboxes
        detailToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152)); detailToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        detailToolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewNote = Ui.Label(DefaultPreviewNote, "muted", Fonts.Body(9.25f), autoSize: false);
        previewNote.Dock = DockStyle.Fill; previewNote.TextAlign = ContentAlignment.MiddleLeft; previewNote.AutoEllipsis = true;
        detailToolbar.Controls.Add(previewNote, 0, 0);
        ignoreChangesButton.AutoSize = false; ignoreChangesButton.Dock = DockStyle.Fill; ignoreChangesButton.Margin = new Padding(4); ignoreChangesButton.Enabled = false;
        ignoreChangesButton.Click += (_, _) => IgnoreSelectedPlaylistChanges();
        tips.SetToolTip(ignoreChangesButton, "Permanently leave chosen tracks of this playlist alone");
        expandDetailsButton.AutoSize = false; expandDetailsButton.Dock = DockStyle.Fill; expandDetailsButton.Margin = new Padding(4); expandDetailsButton.Click += (_, _) => ToggleDetails();
        var copyDetails = RoundedButton.Make("Copy details", ButtonKind.Ghost, Glyph.Copy, (_, _) => { if (details.TextLength > 0) Clipboard.SetText(details.Text); });
        copyDetails.AutoSize = false; copyDetails.Dock = DockStyle.Fill; copyDetails.Margin = new Padding(4);
        detailToolbar.Controls.Add(ignoreChangesButton, 1, 0); detailToolbar.Controls.Add(expandDetailsButton, 2, 0); detailToolbar.Controls.Add(copyDetails, 3, 0);
        ignoreChangesButton.Visible = false;
        detailLayout.Controls.Add(detailToolbar, 0, 0); detailLayout.Controls.Add(BuildDetailRows(), 0, 1);
        // `details` (rich text) stays off-screen: it backs "Copy details"
        detailCard.Controls.Add(detailLayout);
        previewSplit.Panel1.Controls.Add(gridCard);
        previewSplit.Panel2.Controls.Add(detailCard);

        var gap1 = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
        var gap2 = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent };
        previewBody.Controls.Add(previewSplit);
        page.Controls.Add(previewBody); page.Controls.Add(previewEmpty); page.Controls.Add(gap2); page.Controls.Add(previewKpis); page.Controls.Add(gap1); page.Controls.Add(toolbar);
        StyleGrid();
        UpdatePreviewState();
        return page;
    }

    private void StyleGrid()
    {
        var p = Theme.P;
        planGrid.BackgroundColor = p.Panel;
        planGrid.GridColor = ColorX.Mix(p.Line, p.Panel, 0.3);
        planGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = p.Panel, ForeColor = p.Dim, SelectionBackColor = p.Panel, SelectionForeColor = p.Dim,
            Font = Fonts.Body(8.25f, FontStyle.Bold), Padding = new Padding(6, 0, 6, 0)
        };
        planGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = p.Panel, ForeColor = p.Ink, SelectionBackColor = p.Sel, SelectionForeColor = p.Ink,
            Font = Fonts.Body(9.5f), Padding = new Padding(6, 0, 6, 0)
        };
        planGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = ColorX.Mix(p.Panel, p.Well, 0.35), SelectionBackColor = p.Sel, SelectionForeColor = p.Ink };
        foreach (DataGridViewColumn column in planGrid.Columns)
            if (column.Index > 0) { column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter; }
        details.BackColor = p.Panel; details.ForeColor = p.Ink; details.Font = Fonts.Mono(9.25f);
        previewSplit.BackColor = Color.Transparent;
        previewSplit.Panel1.BackColor = Color.Transparent; previewSplit.Panel2.BackColor = Color.Transparent;
    }

    private void UpdatePreviewState()
    {
        var has = prepared is not null;
        previewEmpty.Visible = !has;
        previewBody.Visible = has;
        previewKpis.Visible = has;
        var plan = prepared?.Plan;
        previewKpis.Set("playlists", plan?.Playlists.Count.ToString("N0") ?? "—");
        previewKpis.Set("changes", plan?.TotalChanges.ToString("N0") ?? "—", plan is { TotalChanges: > 0 } ? Theme.P.Accent : null);
        previewKpis.Set("spotify", plan?.Playlists.Sum(x => x.SpotifyAddIds.Count).ToString("N0") ?? "—", Theme.P.SpotifyAdd);
        previewKpis.Set("local", plan?.Playlists.Sum(x => x.LocalAddFiles.Count).ToString("N0") ?? "—", Theme.P.LocalAdd);
        var removals = plan?.Playlists.Sum(x => x.SpotifyRemoveIds.Count + x.LocalRemoveFiles.Count) ?? 0;
        previewKpis.Set("removals", plan is null ? "—" : removals.ToString("N0"), removals > 0 ? Theme.P.Err : null);
        previewKpis.Set("protected", plan?.Playlists.Sum(x => x.PlatformSpecific.Count).ToString("N0") ?? "—", Theme.P.Protected);
        var blocked = plan?.Playlists.Count(x => x.IsBlocked) ?? 0;
        previewKpis.Set("blocked", plan is null ? "—" : blocked.ToString("N0"), blocked > 0 ? Theme.P.Err : null);
        var nav = pages.Count > PagePreview ? pages[PagePreview].Nav : null;
        nav?.SetBadge(planAppliedAt is null ? plan?.TotalChanges ?? 0 : 0, null);
        if (previewNote is not null)
        {
            previewNote.Text = planAppliedAt is { } applied ? $"Applied at {applied:HH:mm}  •  Rebuild the preview to see what's left"
                : previewRestoredAt is { } restored ? $"From your last session ({restored:ddd d MMM HH:mm})  •  Build preview again to apply changes"
                : DefaultPreviewNote;
            previewNote.ForeColor = planAppliedAt is not null ? Theme.P.Ok : previewRestoredAt is not null ? Theme.P.Warn : Theme.P.Muted;
        }
        UpdateApplyButtons();
        RefreshDashboard();
    }

    private void ClearPlan()
    {
        planAppliedAt = null;
        previewRestoredAt = null;
        DeletePreviewSnapshot();
        planGrid.Rows.Clear();
        details.Clear();
        UpdatePreviewState();
    }

    private void PopulatePlan(SyncPlan plan) => PopulatePlan(plan, null);

    private void PopulatePlan(SyncPlan plan, string? selectName)
    {
        var p = Theme.P;
        previewSpotifyTracks = (prepared?.Liked.Tracks ?? new List<TrackInfo>())
            .Concat(prepared?.SpotifyPlaylists.SelectMany(x => x.Tracks) ?? Enumerable.Empty<TrackInfo>())
            .Where(x => x.SpotifyId.Length > 0)
            .GroupBy(x => x.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        previewLocalTracks = (prepared?.Library ?? new List<LocalTrack>())
            .Where(x => x.Path.Length > 0)
            .GroupBy(x => SafePathKey(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        planGrid.SuspendLayout();
        planGrid.Rows.Clear();
        // same order as the dashboard module: blocked first, then playlists with the most changes, then by name
        foreach (var item in plan.Playlists.OrderByDescending(x => x.IsBlocked).ThenByDescending(x => x.ChangeCount).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var index = planGrid.Rows.Add(item.Name, item.HasBaseline ? "Ready" : "First run", item.SpotifyAddIds.Count, item.SpotifyRemoveIds.Count, item.LocalAddFiles.Count, item.LocalRemoveFiles.Count, item.PlatformSpecific.Count, item.IsBlocked ? "Blocked" : item.ChangeCount == 0 ? "In sync" : "Ready");
            var row = planGrid.Rows[index];
            row.Tag = item;
            ColorCell(row, "SpotifyAdd", p.SpotifyAdd, item.SpotifyAddIds.Count > 0);
            ColorCell(row, "SpotifyRemove", p.Removal, item.SpotifyRemoveIds.Count > 0);
            ColorCell(row, "LocalAdd", p.LocalAdd, item.LocalAddFiles.Count > 0);
            ColorCell(row, "LocalRemove", p.Removal, item.LocalRemoveFiles.Count > 0);
            ColorCell(row, "Protected", p.Protected, item.PlatformSpecific.Count > 0);
            foreach (var name in new[] { "SpotifyAdd", "SpotifyRemove", "LocalAdd", "LocalRemove", "Protected" })
                if (row.Cells[name].Value is 0) { row.Cells[name].Style.ForeColor = p.Dim; row.Cells[name].Style.SelectionForeColor = p.Dim; }
            row.Cells["Baseline"].Style.ForeColor = item.HasBaseline ? p.Muted : p.Warn;
            var primary = item.IsBlocked ? p.Err
                : item.SpotifyRemoveIds.Count + item.LocalRemoveFiles.Count > 0 ? p.Warn
                : item.SpotifyAddIds.Count > 0 && item.LocalAddFiles.Count == 0 ? p.SpotifyAdd
                : item.LocalAddFiles.Count > 0 && item.SpotifyAddIds.Count == 0 ? p.LocalAdd
                : item.ChangeCount == 0 ? p.Muted : p.Ink;
            row.Cells[0].Style.ForeColor = primary; row.Cells[0].Style.SelectionForeColor = primary;
            row.Cells["Status"].Style.ForeColor = primary; row.Cells["Status"].Style.SelectionForeColor = primary;
        }
        planGrid.ResumeLayout();
        FilterPlanRows();
        var target = selectName is null ? null : planGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Visible && r.Tag is PlaylistPlan pl && pl.Name == selectName);
        target ??= planGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Visible);
        SelectPlanRowCore(target);
        UpdatePreviewState();
    }

    private void FilterPlanRows()
    {
        var words = previewSearch.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        planGrid.CurrentCell = null;
        foreach (DataGridViewRow row in planGrid.Rows)
        {
            if (row.Tag is not PlaylistPlan item) continue;
            var visible = words.All(w => item.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || item.LocalSourceName.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (changesOnly.Selected && item.ChangeCount == 0 && !item.IsBlocked) visible = false;
            if (attentionOnly.Selected && !(item.IsBlocked || item.Warnings.Count > 0 || item.SpotifyRemoveIds.Count + item.LocalRemoveFiles.Count > 0)) visible = false;
            row.Visible = visible;
        }
        SelectPlanRowCore(planGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Visible));
    }

    /// <summary>
    /// Makes <paramref name="row"/> the current row and always refreshes the details. (Selecting a row raises
    /// SelectionChanged before the current row moves, which used to leave the previous playlist on screen.)
    /// </summary>
    private void SelectPlanRowCore(DataGridViewRow? row)
    {
        if (row is not null)
        {
            if (planGrid.CurrentCell?.RowIndex != row.Index) planGrid.CurrentCell = row.Cells[0];
            planGrid.ClearSelection();
            row.Selected = true;
        }
        ShowSelectedDetails();
    }

    private void ShowSelectedDetails()
    {
        var p = Theme.P;
        details.BackColor = p.Panel;
        details.BeginUpdate();
        try
        {
            details.Clear();
            if (planGrid.CurrentRow?.Tag is not PlaylistPlan item || !planGrid.CurrentRow.Visible)
            {
                ignoreChangesButton.Enabled = false;
                FillDetailRows(null);
                if (prepared is not null) AppendDetail(details, "Select a playlist above to see its track-level changes.", p.Muted);
                return;
            }
            ignoreChangesButton.Enabled = planAppliedAt is null && (item.ReviewItems.Count > 0 || item.IgnoredByRule.Count > 0);
            WritePlanDetails(details, item, 9.25f);
            FillDetailRows(item);
            details.SelectionStart = 0; details.ScrollToCaret();
        }
        finally { details.EndUpdate(); }
    }

    /// <summary>Writes one playlist's planned changes (Sync preview page and the dashboard module).</summary>
    private void WritePlanDetails(RichTextBox box, PlaylistPlan item, float size)
    {
        var p = Theme.P;
        void Add(string text, Color color, FontStyle style = FontStyle.Regular) => AppendDetail(box, text, color, style, size);
        Add(item.Name + Environment.NewLine, p.Ink, FontStyle.Bold);
        if (item.LocalSourceName.Length > 0 && !item.LocalSourceName.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
            Add("MusicBee location  " + item.LocalSourceName + Environment.NewLine, p.Muted);
        if (planAppliedAt is { } applied) Add($"Applied at {applied:HH:mm} — rebuild the preview for the current state" + Environment.NewLine, p.Ok, FontStyle.Bold);
        Add((item.HasBaseline ? "Deletion baseline ready" : "First run — add-only baseline") + "    ", item.HasBaseline ? p.Ok : p.Warn, FontStyle.Bold);
        Add($"Spotify +{item.SpotifyAddIds.Count}/−{item.SpotifyRemoveIds.Count}    ", p.SpotifyAdd);
        Add($"Local +{item.LocalAddFiles.Count}/−{item.LocalRemoveFiles.Count}" + Environment.NewLine, p.LocalAdd);
        if (item.IsBlocked) Add("BLOCKED  " + item.BlockReason + Environment.NewLine, p.Err, FontStyle.Bold);
        foreach (var warning in item.Warnings) Add("WARNING  " + warning + Environment.NewLine, p.Warn);
        if (item.IgnoredByRule.Count > 0)
            Add($"SAVED IGNORE RULES  {item.IgnoredByRule.Count} item(s) suppressed" + Environment.NewLine, p.Muted);
        if (item.ChangeCount == 0 && item.PlatformSpecific.Count == 0 && !item.IsBlocked)
            Add(Environment.NewLine + "✓  Already in sync — nothing to do for this playlist." + Environment.NewLine, p.Muted);

        AppendTrackSection(box, "SPOTIFY ADDITIONS", item.SpotifyAddIds.Select(SpotifyTrackLabel), p.SpotifyAdd, "+", size);
        AppendTrackSection(box, "LOCAL ADDITIONS", item.LocalAddFiles.Select(LocalTrackLabel), p.LocalAdd, "+", size);
        AppendTrackSection(box, "SPOTIFY REMOVALS", item.SpotifyRemoveIds.Select(SpotifyTrackLabel), p.Removal, "−", size);
        AppendTrackSection(box, "LOCAL REMOVALS", item.LocalRemoveFiles.Select(LocalTrackLabel), p.Removal, "−", size);
        AppendTrackSection(box, "PROTECTED / PLATFORM-SPECIFIC", item.PlatformSpecific, p.Protected, "•", size);
    }

    private void IgnoreSelectedPlaylistChanges()
    {
        if (prepared is null || planGrid.CurrentRow?.Tag is not PlaylistPlan item) return;
        using var dialog = new ExclusionReviewDialog(item.Name, item.ReviewItems, service.LoadSyncExclusions());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ApplyExclusionChanges(dialog, item.Name);
    }

    /// <summary>Settings › Manage ignored tracks: every saved rule, untick to sync again.</summary>
    private void OpenIgnoredTracks()
    {
        using var dialog = new ExclusionReviewDialog(null, Array.Empty<TrackReviewItem>(), service.LoadSyncExclusions());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ApplyExclusionChanges(dialog, null);
    }

    private void ApplyExclusionChanges(ExclusionReviewDialog dialog, string? playlist)
    {
        var added = service.AddSyncExclusions(dialog.SelectedExclusions);
        var removed = service.RemoveSyncExclusions(dialog.RemovedExclusions);
        if (added + removed == 0) return;
        var where = playlist is null ? "" : $" for '{playlist}'";
        AppendLog($"Never-sync list updated{where}: {added} track(s) ignored, {removed} track(s) syncing again.");
        if (prepared is null) return;
        service.RebuildPlan(prepared);
        PopulatePlan(prepared.Plan, playlist);
        SetResult($"Preview updated: +{added} ignored, −{removed} un-ignored.", Led.Ok);
    }

    private static void AppendTrackSection(RichTextBox box, string title, IEnumerable<string> values, Color color, string marker, float size)
    {
        var tracks = values.ToList();
        if (tracks.Count == 0) return;
        AppendDetail(box, Environment.NewLine + title + $"  ({tracks.Count})" + Environment.NewLine, color, FontStyle.Bold, size);
        var tone = ColorX.Mix(color, Theme.P.Ink, 0.25);
        foreach (var track in tracks) AppendDetail(box, $"  {marker}  {track}" + Environment.NewLine, tone, FontStyle.Regular, size);
    }

    private static void AppendDetail(RichTextBox box, string text, Color color, FontStyle style = FontStyle.Regular, float size = 9.25f)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = color;
        box.SelectionFont = Fonts.Mono(size, style);
        box.AppendText(text);
    }

    private string SpotifyTrackLabel(string id) =>
        previewSpotifyTracks.TryGetValue(id, out var track) && track.DisplayName.Length > 0
            ? track.DisplayName
            : "Unknown Spotify track  ·  " + id;

    private string LocalTrackLabel(string path)
    {
        if (previewLocalTracks.TryGetValue(SafePathKey(path), out var track) && track.DisplayName.Length > 0)
            return track.DisplayName + "  ·  " + Path.GetFileName(path);
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string SafePathKey(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim(); }
    }

    private static void ColorCell(DataGridViewRow row, string name, Color color, bool active)
    {
        if (!active) return;
        row.Cells[name].Style.ForeColor = color;
        row.Cells[name].Style.SelectionForeColor = color;
        row.Cells[name].Style.Font = Fonts.Semibold(9.5f);
    }

    private void ToggleDetails()
    {
        detailsExpanded = !detailsExpanded;
        expandDetailsButton.Text = detailsExpanded ? "Show playlist list" : "Expand details";
        expandDetailsButton.Glyph = detailsExpanded ? Glyph.Collapse : Glyph.Expand;
        var room = previewSplit.Height - previewSplit.SplitterWidth;
        var animate = Theme.Animations && previewSplit.IsHandleCreated && previewSplit.Visible && room > 320;
        StopSplitAnimation();
        if (detailsExpanded)
        {
            if (!previewSplit.Panel1Collapsed) splitBeforeExpand = previewSplit.SplitterDistance;
            if (!animate) { previewSplit.Panel1Collapsed = true; return; }
            previewSplit.Panel1MinSize = 0;
            AnimateSplitter(previewSplit.SplitterDistance, 0, () => previewSplit.Panel1Collapsed = true);
        }
        else
        {
            var target = Math.Clamp(splitBeforeExpand > 0 ? splitBeforeExpand : room / 2, 150, Math.Max(150, room - previewSplit.Panel2MinSize));
            if (!animate)
            {
                previewSplit.Panel1Collapsed = false;
                if (room > 380) BeginInvoke(new Action(() => TrySetSplitter(target)));
                return;
            }
            previewSplit.Panel1MinSize = 0;
            previewSplit.Panel1Collapsed = false;
            TrySetSplitter(0);
            AnimateSplitter(0, target, () => previewSplit.Panel1MinSize = 150);
        }
    }

    /// <summary>Glides the preview divider between two positions (expand details / show the playlist list).</summary>
    private void AnimateSplitter(int from, int to, Action done)
    {
        const double seconds = 0.26;
        var elapsed = 0.0;
        void Frame(double dt)
        {
            if (previewSplit.IsDisposed) { StopSplitAnimation(); return; }
            elapsed += dt;
            var t = Math.Clamp(elapsed / seconds, 0, 1);
            var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            TrySetSplitter((int)Math.Round(from + (to - from) * eased));
            previewSplit.Update(); // paint this frame now rather than whenever the queue gets to it
            if (t >= 1) { StopSplitAnimation(); done(); }
        }
        splitAnimation = Frame;
        FrameClock.Subscribe(previewSplit, Frame);
    }

    private void StopSplitAnimation()
    {
        if (splitAnimation is null) return;
        FrameClock.Unsubscribe(splitAnimation);
        splitAnimation = null;
    }

    private void TrySetSplitter(int distance)
    {
        var max = previewSplit.Height - previewSplit.SplitterWidth - previewSplit.Panel2MinSize;
        try { previewSplit.SplitterDistance = Math.Clamp(distance, previewSplit.Panel1MinSize, Math.Max(previewSplit.Panel1MinSize, max)); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private void InitializePreviewSplitter()
    {
        if (previewSplitInitialized || previewSplit.Height < 420) return;
        var distance = Math.Clamp(previewSplit.Height / 2, 170, previewSplit.Height - 179);
        previewSplit.SplitterDistance = distance;
        previewSplit.Panel1MinSize = 150;
        previewSplit.Panel2MinSize = 150;
        previewSplitInitialized = true;
    }
}
