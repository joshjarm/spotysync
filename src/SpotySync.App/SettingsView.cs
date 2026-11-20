namespace SpotySyncApp;

internal enum EditorWidth { Narrow, Wide, Full }

/// <summary>One searchable setting: title + description on the left, its editor on the right (or below).</summary>
internal sealed class SettingRow : ThemedControl, IHeightForWidth, IHideable
{
    private const int PadX = 14, PadY = 11, Gap = 18;
    public string Title { get; }
    public string Description { get; }
    public string Keywords { get; }
    public Control Editor { get; }
    public EditorWidth Mode { get; }
    public bool Danger { get; set; }
    public bool Filtered { get; set; }
    public bool Highlight { get; set; }
    public bool IsHidden => Filtered;
    public SettingGroup? Group { get; set; }

    public SettingRow(string title, string description, Control editor, EditorWidth mode = EditorWidth.Narrow, string keywords = "")
    {
        Title = title; Description = description; Editor = editor; Mode = mode; Keywords = keywords;
        Margin = new Padding(0);
        Controls.Add(editor);
    }

    public string SearchText => (Title + " " + Description + " " + Keywords).ToLowerInvariant();

    private bool Stacked(int width) => Mode == EditorWidth.Full || (Mode == EditorWidth.Wide && width < 620);

    private int TextWidth(int width) => Stacked(width) ? width - PadX * 2
        : Mode == EditorWidth.Wide ? (int)((width - PadX * 2) * 0.40)
        : width - PadX * 2 - EditorPreferredWidth() - Gap;

    private int EditorPreferredWidth() => Editor is IHeightForWidth ? 300 : Math.Max(Editor.Width, Editor.GetPreferredSize(Size.Empty).Width);

    private int EditorHeight(int width) => Editor is IHeightForWidth hw ? hw.HeightFor(width) : Editor.Height;

    private int TextHeight(int textWidth)
    {
        var title = TextRenderer.MeasureText(Title, Fonts.Semibold(9.75f), new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        var desc = Description.Length == 0 ? 0 : 3 + TextRenderer.MeasureText(Description, Fonts.Body(8.75f), new Size(Math.Max(40, textWidth), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        return title + desc;
    }

    public int HeightFor(int width)
    {
        var tw = Math.Max(80, TextWidth(width));
        var text = TextHeight(tw);
        if (Stacked(width)) return PadY + text + 10 + EditorHeight(width - PadX * 2) + PadY;
        return PadY * 2 + Math.Max(text, EditorHeight(width - PadX * 2 - tw - Gap));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var width = Width;
        var tw = Math.Max(80, TextWidth(width));
        if (Stacked(width))
        {
            var y = PadY + TextHeight(tw) + 10;
            Editor.SetBounds(PadX, y, width - PadX * 2, EditorHeight(width - PadX * 2));
        }
        else
        {
            var x = PadX + tw + Gap;
            var w = Mode == EditorWidth.Wide ? width - x - PadX : EditorPreferredWidth();
            if (Mode == EditorWidth.Narrow) x = width - PadX - w;
            var h = EditorHeight(w);
            Editor.SetBounds(x, Math.Max(PadY, (Height - h) / 2), w, h);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        if (Hovered || Highlight)
        {
            using var bar = new SolidBrush(Highlight ? p.Accent : ColorX.Mix(p.Panel, p.Accent, 0.45));
            g.FillRectangle(bar, 2, 10, 3, Math.Max(4, Height - 20));
        }
        var tw = Math.Max(80, TextWidth(Width));
        var titleFont = Fonts.Semibold(9.75f);
        var titleH = TextRenderer.MeasureText(Title, titleFont, new Size(tw, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        var textTop = Stacked(Width) ? PadY : Math.Max(PadY, (Height - TextHeight(tw)) / 2);
        TextRenderer.DrawText(g, Title, titleFont, new Rectangle(PadX, textTop, tw, titleH), Danger ? ColorX.Mix(p.Err, p.Ink, 0.25) : p.Ink,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        if (Description.Length > 0)
            TextRenderer.DrawText(g, Description, Fonts.Body(8.75f), new Rectangle(PadX, textTop + titleH + 3, tw, Height), p.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

/// <summary>A titled card holding a group of setting rows.</summary>
internal sealed class SettingGroup : Card, IHeightForWidth, IHideable
{
    public string Id { get; }
    public string Note { get; }
    public VStack Rows { get; } = new() { Gap = 0, AutoHeight = true, Dock = DockStyle.Top };
    public List<SettingRow> Items { get; } = new();
    public bool IsHidden => Items.Count > 0 && Items.All(r => r.Filtered);
    public NavItem? RailItem { get; set; }

    public SettingGroup(string id, string title, string eyebrow, string glyph, string note)
    {
        Id = id; Title = title; Eyebrow = eyebrow; Glyph = glyph; Note = note;
        Inner = new Padding(8, 4, 8, 8);
        UpdatePadding();
        Controls.Add(Rows);
    }

    public SettingRow Add(SettingRow row)
    {
        row.Group = this;
        Items.Add(row);
        Rows.Controls.Add(row);
        return row;
    }

    public int HeightFor(int width) => HeaderHeight + Inner.Vertical + Rows.HeightFor(width - Inner.Horizontal);

    private readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 900 };

    /// <summary>Briefly outlines the group in the accent color after a rail jump.</summary>
    public void Flash()
    {
        AccentEdge = Theme.P.Accent;
        Invalidate();
        flashTimer.Stop();
        flashTimer.Tick -= EndFlash; flashTimer.Tick += EndFlash;
        flashTimer.Start();
    }

    private void EndFlash(object? sender, EventArgs e) { flashTimer.Stop(); AccentEdge = null; Invalidate(); }
}

/// <summary>The Settings page: category rail, live search and one scrolling column of groups.</summary>
internal sealed class SettingsView : Panel
{
    private readonly VStack rail = new() { Gap = 2, Dock = DockStyle.Left, Width = 240, Padding = new Padding(0, 4, 18, 0) };
    private readonly SettingsScroll scroll = new() { Gap = 14, AutoScroll = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 24) };
    private readonly Label empty = Ui.Label("", "muted", Fonts.Body(10.5f), autoSize: false);
    private readonly Label matches = Ui.Label("", "dim", Fonts.Body(9f));
    public FieldBox Search { get; } = new() { LeadingGlyph = Glyph.Search, ClearButton = true, Placeholder = "Search settings — try “removal”, “theme”, “music folder”…   (Ctrl+F)" };
    public List<SettingGroup> Groups { get; } = new();
    public Card UnsavedBar { get; } = new() { Raised = true, Radius = 12, Dock = DockStyle.Bottom, Height = 62, Visible = false, Inner = new Padding(18, 10, 12, 10) };
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 120 };
    private bool syncingRail;
    private DateTime holdRailUntil;
    private readonly System.Windows.Forms.Timer railRecheck = new();

    public SettingsView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Ui.DarkScrollbars(scroll);
        var top = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Color.Transparent };
        Search.SetBounds(0, 4, 520, 38);
        Search.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        matches.Location = new Point(536, 15);
        top.Controls.Add(Search); top.Controls.Add(matches);
        top.Resize += (_, _) => { Search.Width = Math.Clamp(top.Width - 230, 280, 620); matches.Left = Search.Right + 14; };
        empty.Height = 120; empty.TextAlign = ContentAlignment.MiddleCenter; empty.Tag = "hidden";
        scroll.Controls.Add(empty);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        body.Controls.Add(scroll); body.Controls.Add(rail);
        Controls.Add(body); Controls.Add(UnsavedBar); Controls.Add(top);
        debounce.Tick += (_, _) => { debounce.Stop(); ApplyFilter(); };
        Search.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        scroll.Scrolled += SyncRail;
        railRecheck.Tick += (_, _) => { railRecheck.Stop(); SyncRail(); };
    }

    public SettingGroup AddGroup(string id, string title, string eyebrow, string glyph, string note = "")
    {
        var group = new SettingGroup(id, title, eyebrow, glyph, note);
        Groups.Add(group);
        scroll.Controls.Add(group);
        var nav = new NavItem { Text = title, Glyph = glyph, Height = 36 };
        nav.Click += (_, _) =>
        {
            if (Search.Text.Trim().Length > 0) { Search.Text = ""; ApplyFilter(); }
            ScrollTo(group);
        };
        group.RailItem = nav;
        rail.Controls.Add(nav);
        if (Groups.Count == 1) nav.Selected = true;
        return group;
    }

    public void ScrollTo(SettingGroup group)
    {
        syncingRail = true;
        foreach (var g in Groups) if (g.RailItem is not null) g.RailItem.Selected = g == group;
        scroll.PerformLayout();
        var target = group.Top - scroll.AutoScrollPosition.Y - 4; // absolute offset of the group in the scroll area
        scroll.Smooth.ScrollTo(Math.Max(0, target));
        group.Flash();
        holdRailUntil = DateTime.Now.AddMilliseconds(900);
        syncingRail = false;
    }

    /// <summary>Clears any search and scrolls to a section (e.g. the sidebar's theme button → "appearance").</summary>
    public void ShowGroup(string id)
    {
        if (Search.Text.Trim().Length > 0) { Search.Text = ""; ApplyFilter(); }
        if (Groups.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } group) ScrollTo(group);
    }

    /// <summary>Current vertical scroll offset (for tests and diagnostics).</summary>
    public int ScrollOffset => -scroll.AutoScrollPosition.Y;

    private void SyncRail()
    {
        if (syncingRail) return;
        var hold = holdRailUntil - DateTime.Now;
        if (hold > TimeSpan.Zero)
        {
            // a section was just clicked: keep its highlight while scrolling to it, then re-check once
            railRecheck.Interval = Math.Max(1, (int)hold.TotalMilliseconds + 10);
            railRecheck.Stop(); railRecheck.Start();
            return;
        }
        var visible = Groups.Where(g => !g.IsHidden).ToList();
        if (visible.Count == 0) return;
        // the section whose top has passed the upper third of the view is "current"
        var line = Math.Max(40, scroll.ClientSize.Height / 3);
        var current = visible.LastOrDefault(g => g.Top <= line) ?? visible[0];
        var max = scroll.DisplayRectangle.Height - scroll.ClientSize.Height;
        if (max > 0 && -scroll.AutoScrollPosition.Y >= max - 4) current = visible[^1]; // at the very bottom: the last section
        if (visible[^1].Bottom <= scroll.ClientSize.Height && visible[^1].Top < scroll.ClientSize.Height) current = visible[^1].Top <= line ? visible[^1] : current;
        foreach (var g in Groups) if (g.RailItem is not null && g.RailItem.Selected != (g == current)) g.RailItem.Selected = g == current;
    }

    /// <summary>Show settings matching a query (used by “Fix” links elsewhere in the app).</summary>
    public void Find(string query)
    {
        Search.Text = query;
        ApplyFilter();
        Search.Inner.Focus();
        Search.Inner.SelectionStart = Search.Inner.TextLength;
    }

    public void ApplyFilter()
    {
        var words = Search.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = 0;
        foreach (var group in Groups)
        {
            var head = (group.Title + " " + group.Eyebrow + " " + group.Note).ToLowerInvariant();
            var count = 0;
            foreach (var row in group.Items)
            {
                var text = row.SearchText;
                var hit = words.Length == 0 || words.All(w => text.Contains(w) || head.Contains(w));
                row.Filtered = !hit;
                row.Highlight = words.Length > 0 && hit && words.All(w => text.Contains(w));
                if (hit) count++;
                row.Invalidate();
            }
            total += count;
            if (group.RailItem is not null)
            {
                group.RailItem.SetBadge(words.Length > 0 ? count : 0, null);
                group.RailItem.Enabled = true;
                group.RailItem.Invalidate();
            }
            group.Rows.PerformLayout();
        }
        empty.Tag = words.Length > 0 && total == 0 ? null : "hidden";
        empty.Text = $"No settings match “{Search.Text.Trim()}”.\r\nTry a broader word like “spotify”, “folder”, “download” or “theme”.";
        matches.Text = words.Length == 0 ? "" : total == 1 ? "1 match" : $"{total} matches";
        scroll.AutoScrollPosition = Point.Empty;
        scroll.PerformLayout();
        scroll.Invalidate(true);
        SyncRail();
    }

    public void Relayout() { foreach (var g in Groups) g.Rows.PerformLayout(); scroll.PerformLayout(); }

    protected override void Dispose(bool disposing) { if (disposing) { debounce.Dispose(); railRecheck.Dispose(); } base.Dispose(disposing); }
}

/// <summary>The settings column: a VStack that reports every scroll (wheel, bar or programmatic).</summary>
internal sealed class SettingsScroll : VStack
{
}

/// <summary>Lays out one filling control followed by fixed-width trailing controls (field + Browse…).</summary>
internal sealed class FieldStrip : Panel
{
    private readonly Control fill;
    private readonly Control[] trailing;

    public FieldStrip(Control fill, params Control[] trailing)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        this.fill = fill; this.trailing = trailing;
        Height = 36;
        Controls.Add(fill);
        foreach (var c in trailing) { c.Margin = new Padding(0); Controls.Add(c); }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(Width, 260), Height);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var x = Width;
        foreach (var c in trailing.Reverse())
        {
            var w = c.Width > 0 && !c.AutoSize ? c.Width : c.GetPreferredSize(Size.Empty).Width;
            x -= w;
            c.SetBounds(x, (Height - 34) / 2, w, 34);
            x -= 8;
        }
        fill.SetBounds(0, (Height - 34) / 2, Math.Max(40, x), 34);
    }
}
