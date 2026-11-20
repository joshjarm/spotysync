using SpotySync;

namespace SpotySyncApp;

public sealed partial class MainForm
{
    /// <summary>A dashboard module: a card with a default size the user can change.</summary>
    private sealed class DashCard
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required Card Card { get; init; }
        public int DefaultSpan { get; init; }
        public int DefaultHeight { get; init; }
        public Card.HeaderAction? MinimizeAction { get; set; }
    }

    private static readonly string[] DefaultDashOrder = ["actions", "soundtrack", "console", "activity", "preview", "library"];
    /// <summary>Default dashboard: Quick actions (wide) + Soundtrack Saver, then Console, Download activity and Sync preview; Library hidden.</summary>
    private const string DefaultDashLayout = "actions:2:320,soundtrack:1:320,console:1:480,activity:1:480,preview:1:480,-library:2:420";
    private const int MinCardHeight = 180, MaxCardHeight = 900, HeightSnap = 20;
    private readonly CardGrid dashGrid = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 6, 12), MinCardHeight = MinCardHeight, MaxCardHeight = MaxCardHeight, HeightSnap = HeightSnap };
    private readonly List<DashCard> dashCards = new();
    private readonly List<Button> busyButtons = new();
    private readonly RoundedButton dashboardApplyButton = RoundedButton.Make("Apply plan", ButtonKind.Secondary, Glyph.Check);
    private readonly ActivityGraph activityGraph = new() { Dock = DockStyle.Fill };
    private readonly DownloadMonitor downloadMonitor = new();
    private readonly PlanList planList = new() { Dock = DockStyle.Fill };
    private readonly Panel dashPlanListView = new();
    private readonly Panel dashPlanDetailView = new();
    private readonly SmoothRichText dashPlanDetail = new();
    private PlaylistPlan? dashPlanShown;
    private bool dashPlanStale; // colours or applied state changed: rewrite the open overview
    private readonly LibraryStats libraryStats = new() { Dock = DockStyle.Fill };
    private LogFeed? dashFeed;
    private ActivityWindow? activityWindow;

    // drag / resize state (the grid moves the real card; this only tracks the mouse)
    private DashCard? gestureModule;
    private bool gesturePending, dragging, resizing;
    private Point gestureStartScreen, grabOffset;
    // Gestures are tracked by polling the cursor on the display frame clock, so they stay smooth and never
    // depend on which control holds mouse capture. Indirection so UI tests can drive a gesture.
    private Func<Point> gestureCursor = () => Cursor.Position;
    private Func<bool> gestureButtonDown = PhysicalPrimaryButtonDown;

    private static bool PhysicalPrimaryButtonDown() =>
        (GetAsyncKeyState(SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private Control BuildDashboard()
    {
        var page = new Panel();
        Ui.DarkScrollbars(dashGrid);

        // Quick actions
        var actions = new ButtonStack();
        var backup = RoundedButton.Make("Back up recent Liked Songs", ButtonKind.Primary, Glyph.Heart, async (_, _) => await BackupAsync());
        var links = RoundedButton.Make("Download Spotify / YouTube links...", ButtonKind.Secondary, Glyph.Link, async (_, _) => await ManualDownloadAsync());
        var build = RoundedButton.Make("Build sync preview", ButtonKind.Secondary, Glyph.Eye, async (_, _) => await PreviewAsync());
        var runAll = RoundedButton.Make("Run everything", ButtonKind.Secondary, Glyph.Lightning, async (_, _) => await RunAllAsync());
        tips.SetToolTip(backup, "Downloads songs you Liked on Spotify since the last backup, then imports them into MusicBee. Playlists are not touched.");
        tips.SetToolTip(build, "Reads Spotify and your local playlists and shows every change before anything is written (F5).");
        tips.SetToolTip(runAll, "Backs up recent Likes, then builds the playlist preview and asks before applying it.");
        busyButtons.AddRange([backup, links, build, runAll]);
        actions.Add(backup, links, build, runAll);
        AddDashCard("actions", "Quick actions", "Backup & sync", Glyph.Lightning, actions, 1, 320);

        // Download activity (live graph)
        activityGraph.Monitor = downloadMonitor;
        activityGraph.Range = settings.UiActivityRange;
        activityGraph.RangeChanged += SaveActivityRange;
        activityGraph.FullScreenRequested += OpenActivityFullScreen;
        AddDashCard("activity", "Download activity", "Live", Glyph.Download, activityGraph, 2, 320,
            new Card.HeaderAction { Glyph = Glyph.Expand, Tip = "Full screen (double-click the graph too)", Click = OpenActivityFullScreen });

        // Sync preview: playlist list, or one playlist's planned changes (overview without leaving the dashboard)
        var previewBody = new Panel();
        var previewFooter = new Row { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
        var previewBuild = RoundedButton.Make("Build preview", ButtonKind.Primary, Glyph.Eye, async (_, _) => await PreviewAsync());
        dashboardApplyButton.Enabled = false;
        dashboardApplyButton.Click += async (_, _) => await ApplyAsync();
        busyButtons.Add(previewBuild);
        foreach (var b in new[] { previewBuild, dashboardApplyButton }) { b.Margin = new Padding(0, 0, 8, 0); previewFooter.Controls.Add(b); }
        tips.SetToolTip(planList, "Click a playlist to see what would change");
        planList.Open += name => ShowDashPlan(name, animate: true);
        dashPlanListView.Controls.Add(planList); dashPlanListView.Controls.Add(previewFooter);

        dashPlanDetail.ReadOnly = true; dashPlanDetail.BorderStyle = BorderStyle.None; dashPlanDetail.ScrollBars = RichTextBoxScrollBars.Vertical;
        dashPlanDetail.WordWrap = true; dashPlanDetail.Dock = DockStyle.Fill; dashPlanDetail.DetectUrls = false;
        dashPlanDetail.KeyDown += (_, e) => ConsoleView.CopyKeys(dashPlanDetail, e);
        Ui.DarkScrollbars(dashPlanDetail);
        var detailFooter = new Row { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
        var back = RoundedButton.Make("All playlists", ButtonKind.Ghost, Glyph.ChevronLeft, (_, _) => ShowDashPlanList(animate: true));
        var openFull = RoundedButton.Make("Open in Sync preview", ButtonKind.Secondary, Glyph.Expand, (_, _) =>
        {
            var name = dashPlanShown?.Name;
            SelectPage(PagePreview);
            if (name is not null) SelectPlanRow(name);
        });
        tips.SetToolTip(openFull, "Open this playlist on the Sync preview page");
        foreach (var b in new[] { back, openFull }) { b.Margin = new Padding(0, 0, 8, 0); detailFooter.Controls.Add(b); }
        // same rows as the Sync preview page: tick/untick to sync or never sync, protected and in-sync tracks below
        dashPlanDetailView.Controls.Add(BuildDetailRows(dashDetailTable, dashDetailSummary)); dashPlanDetailView.Controls.Add(detailFooter);
        dashPlanDetailView.Visible = false;

        dashPlanListView.Dock = dashPlanDetailView.Dock = DockStyle.Fill;
        previewBody.Controls.Add(dashPlanListView); previewBody.Controls.Add(dashPlanDetailView);
        AddDashCard("preview", "Sync preview", "Spotify ⇄ local", Glyph.Sync, previewBody, 2, 340);

        // Soundtrack Saver
        AddDashCard("soundtrack", "Soundtrack Saver", "Own folder per soundtrack", Glyph.Music, BuildSoundtrackCard(), 1, 340);

        // Console: the complete log, every line (the Console page has the filters)
        dashFeed = new LogFeed(console) { Dock = DockStyle.Fill };
        var feedHost = new Panel();
        feedHost.Controls.Add(dashFeed);
        AddDashCard("console", "Console", "Everything, live", Glyph.Console, feedHost, 2, 300,
            new Card.HeaderAction { Glyph = Glyph.Open, Tip = "Open the Console page (filters, search, logs)", Click = () => SelectPage(PageConsole) });

        // Library tools
        var libraryBody = new Panel();
        var libraryFooter = new Row { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
        var analyse = RoundedButton.Make("Analyse", ButtonKind.Primary, Glyph.Library, async (_, _) => await AnalyseLibraryAsync(false));
        busyButtons.Add(analyse);
        analyse.Margin = new Padding(0, 0, 8, 0);
        libraryFooter.Controls.Add(analyse);
        libraryStats.Open += index => { ShowInsight(InsightTabs[index].Kind); SelectPage(PageLibrary); };
        libraryBody.Controls.Add(libraryStats); libraryBody.Controls.Add(libraryFooter);
        AddDashCard("library", "Library tools", "Find gaps", Glyph.Library, libraryBody, 1, 300);

        dashGrid.Resize += (_, _) => { if (!dashGrid.Gesturing) dashGrid.PerformLayout(); };
        page.Controls.Add(dashGrid);
        ApplyDashboardLayout();
        return page;
    }

    private void SaveActivityRange(int seconds)
    {
        settings.UiActivityRange = seconds;
        activityGraph.Range = seconds;
        if (consoleGraph is not null) consoleGraph.Range = seconds;
        SaveAppearance();
    }

    /// <summary>Opens (or brings back) the full-screen download activity window.</summary>
    private void OpenActivityFullScreen()
    {
        if (activityWindow is { IsDisposed: false }) { activityWindow.Activate(); return; }
        activityWindow = new ActivityWindow(downloadMonitor, settings.UiActivityRange, TogglePause, () => cancellation?.Cancel(), () => CanPause, SaveActivityRange);
        activityWindow.FormClosed += (_, _) => activityWindow = null;
        activityWindow.ShowFullScreen(this);
    }

    /// <summary>True when a module is shown on the dashboard.</summary>
    private bool IsDashModuleVisible(string id) => DashLayouts().Any(l => l.Id == id && l.Visible && !l.Minimized);

    /// <summary>Stay on the dashboard (instead of jumping pages) while the user watches it with the Console module.</summary>
    private bool StayOnDashboard => currentPage == PageDashboard && IsDashModuleVisible("console");

    private void SelectPlanRow(string name)
    {
        bool Match(DataGridViewRow r) => r.Tag is PlaylistPlan pl && pl.Name == name;
        var row = planGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Visible && Match(r));
        if (row is null && planGrid.Rows.Cast<DataGridViewRow>().Any(Match))
        {
            // hidden by the search box or a filter chip: clear them so the playlist can be shown
            previewSearch.Text = ""; changesOnly.Selected = attentionOnly.Selected = false;
            FilterPlanRows();
            row = planGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Visible && Match(r));
        }
        if (row is not null) SelectPlanRowCore(row);
    }

    /// <summary>Shows one playlist's planned changes inside the dashboard module.</summary>
    private void ShowDashPlan(string name, bool animate)
    {
        var item = prepared?.Plan.Playlists.FirstOrDefault(x => x.Name == name);
        if (item is null) { ShowDashPlanList(animate); return; }
        void Swap()
        {
            dashPlanShown = item;
            WriteDashPlan();
            dashPlanListView.Visible = false;
            dashPlanDetailView.Visible = true;
        }
        if (animate && dashPlanDetailView.Parent?.Parent is Control card) CrossFade.Run(card, Swap, 0.16); else Swap();
    }

    private void ShowDashPlanList(bool animate)
    {
        void Swap()
        {
            dashPlanShown = null;
            dashPlanDetailView.Visible = false;
            dashPlanListView.Visible = true;
        }
        if (!dashPlanDetailView.Visible) { dashPlanShown = null; return; }
        if (animate && dashPlanDetailView.Parent?.Parent is Control card) CrossFade.Run(card, Swap, 0.16); else Swap();
    }

    private void WriteDashPlan()
    {
        if (dashPlanShown is not { } item) return;
        FillDetailRows(dashDetailTable, dashDetailSummary, item);
    }

    private void AddDashCard(string id, string title, string eyebrow, string glyph, Control body, int span, int height, params Card.HeaderAction[] extra)
    {
        var card = new Card { Title = title, Eyebrow = eyebrow, Glyph = glyph, Name = "card-" + id, Resizable = true, HoverHighlight = true, Inner = new Padding(16, 10, 16, 24) };
        body.Dock = DockStyle.Fill;
        card.Controls.Add(body);
        var module = new DashCard { Id = id, Title = title, Card = card, DefaultSpan = span, DefaultHeight = height };
        // header buttons, right to left: minimize, then any extras (full screen…)
        module.MinimizeAction = new Card.HeaderAction { Glyph = Glyph.Minimize, Tip = "Minimize (double-click the title too)", Click = () => ToggleDashCardMinimized(id) };
        card.HeaderActions.Add(module.MinimizeAction);
        card.HeaderActions.AddRange(extra);
        card.HeaderDoubleClick += () => ToggleDashCardMinimized(id);
        var menu = Ui.Menu();
        var minimizeItem = new ToolStripMenuItem("Minimize", null, (_, _) => ToggleDashCardMinimized(id));
        var widthMenu = new ToolStripMenuItem("Width");
        foreach (var (label, s) in new[] { ("Narrow (1 column)", 1), ("Wide (2 columns)", 2), ("Full width", 3) })
            widthMenu.DropDownItems.Add(label, null, (_, _) => SetDashCardSize(id, s, null));
        var heightMenu = new ToolStripMenuItem("Height");
        foreach (var (label, h) in new[] { ("Short", 220), ("Medium", 320), ("Tall", 440), ("Extra tall", 600) })
            heightMenu.DropDownItems.Add(label, null, (_, _) => SetDashCardSize(id, null, h));
        menu.Items.Add(minimizeItem);
        menu.Items.Add(widthMenu); menu.Items.Add(heightMenu);
        menu.Items.Add("Move earlier", null, (_, _) => MoveDashCard(id, -1));
        menu.Items.Add("Move later", null, (_, _) => MoveDashCard(id, +1));
        menu.Items.Add("Hide this module", null, (_, _) => SetDashCardVisible(id, false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Customize dashboard…", null, (_, _) => GoToSetting("dashboard modules"));
        menu.Items.Add("Reset dashboard layout", null, (_, _) => { settings.UiDashboardCards = ""; dashGrid.AnimateNextLayout = true; ApplyDashboardLayout(); SaveAppearance(); RebuildCardEditor(); });
        menu.Opening += (_, _) => minimizeItem.Text = card.Minimized ? "Restore" : "Minimize";
        card.HeaderMenu = menu;
        card.MouseDown += (_, e) => BeginCardGesture(module, e);
        card.MouseUp += (_, _) =>
        {
            if (gestureModule != module) return;
            MoveCardGesture(module);
            FrameClock.Unsubscribe(GestureFrame);
            EndCardGesture(module);
        };
        dashCards.Add(module);
        dashGrid.Controls.Add(card);
    }

    // ---------------------------------------------------------------- layout persistence
    // Format: "id:span:height:col:order,…" in reading order. A leading "-" hides a module, "~" minimizes it.
    // col/order are optional (older layouts): such modules are packed into the first free spot.
    private sealed record DashLayout(string Id, bool Visible, int Span, int Height, int? Col = null, int? Order = null, bool Minimized = false);

    private List<DashLayout> DashLayouts()
    {
        var result = new List<DashLayout>();
        var source = string.IsNullOrWhiteSpace(settings.UiDashboardCards) ? DefaultDashLayout : settings.UiDashboardCards;
        foreach (var raw in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var flags = raw[..(raw.Length - raw.TrimStart('-', '~').Length)];
            var parts = raw.TrimStart('-', '~').Split(':');
            var module = dashCards.FirstOrDefault(c => c.Id == parts[0]);
            if (module is null || result.Any(r => r.Id == module.Id)) continue;
            int? Part(int i) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : null;
            var span = Part(1) is { } s ? Math.Clamp(s, 1, 3) : module.DefaultSpan;
            var height = Part(2) is { } h ? Math.Clamp(h, MinCardHeight, MaxCardHeight) : module.DefaultHeight;
            var col = Part(3) is { } c ? Math.Clamp(c, 0, 2) : (int?)null;
            var order = Part(4);
            result.Add(new DashLayout(module.Id, !flags.Contains('-'), span, height, col, col is null ? null : order, flags.Contains('~')));
        }
        foreach (var id in DefaultDashOrder.Concat(dashCards.Select(c => c.Id)))
        {
            var module = dashCards.FirstOrDefault(c => c.Id == id);
            if (module is not null && result.All(r => r.Id != id)) result.Add(new DashLayout(id, true, module.DefaultSpan, module.DefaultHeight));
        }
        return result;
    }

    /// <summary>Order + visibility (used by the Settings editor), in reading order.</summary>
    private List<(string Id, bool Visible)> DashOrder() => DashLayouts().Select(l => (l.Id, l.Visible)).ToList();

    private static string FormatLayout(DashLayout l) =>
        $"{(l.Visible ? "" : "-")}{(l.Minimized ? "~" : "")}{l.Id}:{l.Span}:{l.Height}" + (l.Col is { } c && l.Order is { } o ? $":{c}:{o}" : "");

    private void SaveDashLayouts(List<DashLayout> layouts)
    {
        dashGrid.AnimateNextLayout = true;
        settings.UiDashboardCards = string.Join(",", layouts.Select(FormatLayout));
        ApplyDashboardLayout();
        StoreGridPositions(); // positions as laid out (reading order)
        SaveAppearance();
        RebuildCardEditor();
    }

    /// <summary>The layout as the grid has it now (columns and order of every card), in reading order.</summary>
    private List<DashLayout> GridLayouts()
    {
        var saved = DashLayouts();
        var list = saved.Select(l =>
        {
            var card = dashCards.First(c => c.Id == l.Id).Card;
            var col = dashGrid.Cols.TryGetValue(card, out var c) ? c : l.Col;
            var order = dashGrid.Orders.TryGetValue(card, out var o) ? (int)Math.Round(o) : l.Order;
            return l with
            {
                Span = dashGrid.Spans.TryGetValue(card, out var s) ? s : l.Span,
                Height = dashGrid.Heights.TryGetValue(card, out var h) ? h : l.Height,
                Col = col, Order = col is null ? null : order, Minimized = card.Minimized
            };
        }).ToList();
        // reading order: top to bottom (by each card's top edge), then left to right
        return list.OrderBy(l => l.Order is { } o ? o - l.Height / 2.0 : double.MaxValue).ThenBy(l => l.Col ?? 0).ToList();
    }

    private void StoreGridPositions() => settings.UiDashboardCards = string.Join(",", GridLayouts().Select(FormatLayout));

    private void MoveDashCard(string id, int delta)
    {
        // swap places with the previous / next visible module in reading order
        var layouts = GridLayouts();
        var visible = layouts.Where(l => l.Visible).ToList();
        var index = visible.FindIndex(o => o.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= visible.Count) return;
        var a = visible[index]; var b = visible[target];
        var ia = layouts.IndexOf(a); var ib = layouts.IndexOf(b);
        layouts[ia] = a with { Col = b.Col, Order = b.Order };
        layouts[ib] = b with { Col = a.Col, Order = a.Order };
        SaveDashLayouts(layouts);
    }

    private void SetDashCardVisible(string id, bool visible)
    {
        var layouts = GridLayouts();
        var index = layouts.FindIndex(o => o.Id == id);
        if (index < 0) return;
        layouts[index] = layouts[index] with { Visible = visible, Minimized = visible && layouts[index].Minimized };
        SaveDashLayouts(layouts);
    }

    private void SetDashCardSize(string id, int? span, int? height)
    {
        var layouts = GridLayouts();
        var index = layouts.FindIndex(o => o.Id == id);
        if (index < 0) return;
        layouts[index] = layouts[index] with { Span = span ?? layouts[index].Span, Height = height ?? layouts[index].Height, Minimized = false };
        SaveDashLayouts(layouts);
    }

    private void ToggleDashCardMinimized(string id)
    {
        var layouts = GridLayouts();
        var index = layouts.FindIndex(o => o.Id == id);
        if (index < 0) return;
        layouts[index] = layouts[index] with { Minimized = !layouts[index].Minimized };
        SaveDashLayouts(layouts);
    }

    private void ApplyDashboardLayout()
    {
        dashGrid.SuspendLayout();
        var layouts = DashLayouts();
        for (var i = 0; i < layouts.Count; i++)
        {
            var l = layouts[i];
            var module = dashCards.First(c => c.Id == l.Id);
            var card = module.Card;
            dashGrid.Controls.SetChildIndex(card, i); // older layouts without positions are packed in this order
            card.Visible = l.Visible;
            card.Minimized = l.Minimized;
            if (module.MinimizeAction is { } action)
            {
                action.Glyph = l.Minimized ? Glyph.Restore : Glyph.Minimize;
                action.Tip = l.Minimized ? "Restore (double-click the title too)" : "Minimize (double-click the title too)";
                card.Invalidate();
            }
            dashGrid.Spans[card] = l.Span;
            dashGrid.Heights[card] = l.Height;
            if (l.Col is { } col && l.Order is { } order) { dashGrid.Cols[card] = col; dashGrid.Orders[card] = order; }
            else { dashGrid.Cols.Remove(card); dashGrid.Orders.Remove(card); }
        }
        dashGrid.ResumeLayout(false);
        dashGrid.PerformLayout(); // size changes only touch dictionaries, so a layout must be forced
        dashGrid.Invalidate(true);
    }

    // ---------------------------------------------------------------- drag to move / grip to resize
    private void BeginCardGesture(DashCard module, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || gestureModule is not null) return;
        var card = module.Card;
        if (card.Resizable && card.GripBounds.Contains(e.Location))
        {
            gestureModule = module; resizing = true;
            dashGrid.BeginResize(card, e.Location);
        }
        else if (card.IsDragArea(e.Location))
        {
            gestureModule = module; gesturePending = true; dragging = false;
            grabOffset = e.Location; gestureStartScreen = gestureCursor();
        }
        else return;
        FrameClock.Subscribe(this, GestureFrame);
    }

    private void GestureFrame(double _) => GestureTick();

    private void GestureTick()
    {
        var module = gestureModule;
        if (module is null) { FrameClock.Unsubscribe(GestureFrame); return; }
        if (!gestureButtonDown()) { FrameClock.Unsubscribe(GestureFrame); EndCardGesture(module); return; }
        MoveCardGesture(module);
    }

    private void MoveCardGesture(DashCard module)
    {
        if (gestureModule != module) return;
        var pos = gestureCursor();
        if (gesturePending)
        {
            if (Math.Abs(pos.X - gestureStartScreen.X) + Math.Abs(pos.Y - gestureStartScreen.Y) < 5) return;
            gesturePending = false; dragging = true;
            module.Card.Cursor = Cursors.SizeAll;
            dashGrid.BeginMove(module.Card, grabOffset);
        }
        if (dragging || resizing) dashGrid.DragTo(dashGrid.PointToClient(pos));
    }

    private void EndCardGesture(DashCard module)
    {
        if (gestureModule != module) return;
        var moved = dragging || resizing;
        gestureModule = null; gesturePending = dragging = resizing = false;
        module.Card.Cursor = Cursors.Default;
        if (!moved) return;
        dashGrid.EndGesture();
        StoreGridPositions();
        SaveAppearance();
        RebuildCardEditor();
    }

    // ---------------------------------------------------------------- content refresh
    private void SetResult(string text, Led state)
    {
        downloadMonitor.SetResult(DateTime.Now.ToString("HH:mm") + "  ·  " + text, state);
    }

    private void RefreshDashboard()
    {
        if (dashCards.Count == 0) return;
        planList.SetPlan(prepared?.Plan);
        if (dashPlanShown is { } shown)
        {
            // keep the open playlist overview in step with the current preview (it may have been rebuilt)
            var current = prepared?.Plan.Playlists.FirstOrDefault(x => x.Name == shown.Name);
            if (current is null) ShowDashPlanList(animate: false);
            else if (!ReferenceEquals(current, shown) || dashPlanStale) { dashPlanShown = current; WriteDashPlan(); }
            dashPlanStale = false;
        }
        var r = libraryReport;
        libraryStats.SetItems(InsightTabs.Select(t => (t.Label, r is null ? "—" : r.Get(t.Kind).Count.ToString("N0"), t.Glyph)).ToArray());
    }
}

/// <summary>Full-width buttons stacked with even gaps (switches keep their natural height).</summary>
internal sealed class ButtonStack : Panel
{
    public void Add(params Control[] controls)
    {
        foreach (var c in controls) { c.Margin = new Padding(0); if (c is Button b) b.AutoSize = false; Controls.Add(c); }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var y = 0;
        foreach (Control c in Controls)
        {
            var h = c is ToggleSwitch ? 30 : 38;
            c.SetBounds(0, y, ClientSize.Width, h);
            c.Visible = y + h <= ClientSize.Height + 2;
            y += h + 8;
        }
    }
}
