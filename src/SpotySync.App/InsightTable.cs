using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// Virtual, custom-painted results table for the Library page: paints only the visible rows (fast with tens of
/// thousands of songs), scrolls pixel-smoothly, multi-select (Ctrl/Shift), double-click and context menu.
/// </summary>
internal sealed class InsightTable : Control
{
    public sealed record Column(string Header, float Weight, Func<InsightRow, string> Text, Func<Palette, Color>? Color = null, Func<InsightRow, Palette, Color>? RowColor = null);

    private const int HeaderH = 36, RowH = 30, Pad = 8;
    private readonly VScrollBar bar = new() { Dock = DockStyle.Right, Width = SystemInformation.VerticalScrollBarWidth };
    private readonly ScrollAnimator scroller;
    private readonly HashSet<int> selected = new();
    private IReadOnlyList<InsightRow> items = Array.Empty<InsightRow>();
    private int offset, current = -1, anchor = -1, hover = -1;
    private bool syncingBar;

    public List<Column> Columns { get; } = new();
    /// <summary>Rows drawn dimmed (e.g. excluded from downloads).</summary>
    public Func<InsightRow, bool>? Dimmed { get; set; }
    /// <summary>When set, a checkbox column is shown first (true = ticked). Clicking it raises <see cref="CheckToggled"/>.</summary>
    public Func<InsightRow, bool?>? Checked { get; set; } // null for a row = no checkbox on that row
    /// <summary>Raised with the rows to flip (the clicked row, or every selected row when the clicked one is selected).</summary>
    public event Action<List<InsightRow>>? CheckToggled;
    public event Action<InsightRow>? RowActivated;
    private const int CheckW = 34;

    // Each painted row is cached as a bitmap, so smooth scrolling only copies images (no text layout per frame).
    private readonly Dictionary<int, Bitmap> rowCache = new();
    private string rowCacheKey = "";

    /// <summary>Forget cached row images after the data a row shows changed (rows keep their place).</summary>
    public void RefreshRows() { ClearRowCache(); Invalidate(); }

    private void ClearRowCache()
    {
        foreach (var b in rowCache.Values) b.Dispose();
        rowCache.Clear();
    }

    private void DropRowImage(int index)
    {
        if (rowCache.Remove(index, out var b)) b.Dispose();
    }
    public event Action? SelectionChanged;

    public InsightTable()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Controls.Add(bar);
        Ui.DarkScrollbars(bar);
        bar.ValueChanged += (_, _) => { if (!syncingBar) { scroller!.Stop(); SetOffset(bar.Value); } };
        scroller = new ScrollAnimator(this, () => offset, SetOffset, () => MaxOffset);
        Theme.Changed += Invalidate;
    }

    public IReadOnlyList<InsightRow> Items
    {
        get => items;
        set
        {
            items = value; selected.Clear(); current = anchor = hover = -1; ClearRowCache();
            scroller.Stop(); offset = 0; UpdateBar(); Invalidate(); SelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// Swaps in a refreshed list without disturbing the view: scroll position, the highlighted rows and the
    /// current row are kept (matched by <paramref name="key"/>), so live library updates don't jump around.
    /// </summary>
    public void ReplaceItems(IReadOnlyList<InsightRow> rows, Func<InsightRow, string> key)
    {
        var selectedKeys = selected.Where(i => i < items.Count).Select(i => key(items[i])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentKey = CurrentItem is { } c ? key(c) : null;
        var keep = offset;
        items = rows; selected.Clear(); current = anchor = hover = -1; ClearRowCache();
        for (var i = 0; i < items.Count; i++)
        {
            var k = key(items[i]);
            if (selectedKeys.Contains(k)) selected.Add(i);
            if (currentKey is not null && k.Equals(currentKey, StringComparison.OrdinalIgnoreCase)) current = anchor = i;
        }
        offset = Math.Clamp(keep, 0, MaxOffset);
        UpdateBar(); Invalidate(); SelectionChanged?.Invoke();
    }

    public InsightRow? CurrentItem => current >= 0 && current < items.Count ? items[current] : null;
    public int SelectedCount => selected.Count;
    public List<InsightRow> SelectedItems => selected.OrderBy(i => i).Where(i => i < items.Count).Select(i => items[i]).ToList();

    private int ViewH => Math.Max(0, Height - HeaderH);
    private int ContentH => items.Count * RowH;
    private int MaxOffset => Math.Max(0, ContentH - ViewH);

    private void SetOffset(int value)
    {
        value = Math.Clamp(value, 0, MaxOffset);
        if (value == offset) return;
        offset = value;
        syncingBar = true;
        try { if (bar.Enabled) bar.Value = Math.Min(value, Math.Max(0, bar.Maximum - bar.LargeChange + 1)); }
        finally { syncingBar = false; }
        Invalidate(new Rectangle(0, HeaderH, Width, ViewH));
        Update(); // paint now: smooth at high refresh rates
    }

    private void UpdateBar()
    {
        var need = ContentH > ViewH;
        bar.Visible = need;
        bar.Enabled = need;
        if (!need) return;
        syncingBar = true;
        try
        {
            bar.Minimum = 0;
            bar.Maximum = ContentH;
            bar.LargeChange = Math.Max(1, ViewH);
            bar.SmallChange = RowH;
            bar.Value = Math.Min(offset, Math.Max(0, bar.Maximum - bar.LargeChange + 1));
        }
        finally { syncingBar = false; }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        offset = Math.Clamp(offset, 0, MaxOffset);
        UpdateBar();
    }

    private int TableW => Width - (bar.Visible ? bar.Width : 0);

    private int RowAt(Point p)
    {
        if (p.Y < HeaderH || p.X >= TableW) return -1;
        var index = (p.Y - HeaderH + offset) / RowH;
        return index >= 0 && index < items.Count ? index : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        using (var bg = new SolidBrush(p.Panel)) g.FillRectangle(bg, ClientRectangle);
        var width = TableW;
        var left = Checked is null ? 0 : CheckW;
        var total = Math.Max(0.01f, Columns.Sum(c => c.Weight));
        var xs = new int[Columns.Count + 1];
        xs[0] = left;
        for (var i = 0; i < Columns.Count; i++) xs[i + 1] = xs[i] + (int)((width - left) * Columns[i].Weight / total);
        xs[^1] = width;
        using var line = new Pen(ColorX.Mix(p.Line, p.Panel, 0.3));

        // rows (only the visible ones), each copied from its cached image
        var key = $"{width}|{p.Panel}|{p.Ink}|{p.Sel}|{p.Accent}|{Focused}|{Checked is not null}";
        if (key != rowCacheKey) { ClearRowCache(); rowCacheKey = key; }
        g.SetClip(new Rectangle(0, HeaderH, width, ViewH));
        var first = offset / RowH;
        var last = Math.Min(items.Count - 1, (offset + ViewH) / RowH);
        for (var i = first; i <= last; i++)
        {
            if (!rowCache.TryGetValue(i, out var image)) rowCache[i] = image = RenderRow(i, width, xs);
            g.DrawImageUnscaled(image, 0, HeaderH + i * RowH - offset);
        }
        if (rowCache.Count > 400) // keep memory small: drop rows far from the view
            foreach (var far in rowCache.Keys.Where(k => k < first - 60 || k > last + 60).ToList()) DropRowImage(far);
        g.ResetClip();

        // fixed header
        using (var hb = new SolidBrush(p.Panel)) g.FillRectangle(hb, 0, 0, width, HeaderH);
        var headerFont = Fonts.Body(8.25f, FontStyle.Bold);
        for (var c = 0; c < Columns.Count; c++)
            TextRenderer.DrawText(g, Columns[c].Header, headerFont, new Rectangle(xs[c] + Pad, 0, xs[c + 1] - xs[c] - Pad * 2, HeaderH), p.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        g.DrawLine(line, 0, HeaderH - 1, width, HeaderH - 1);
    }

    private Bitmap RenderRow(int i, int width, int[] xs)
    {
        var p = Theme.P;
        var image = new Bitmap(Math.Max(1, width), RowH);
        using var g = Graphics.FromImage(image);
        var back = selected.Contains(i) ? p.Sel : i == hover ? ColorX.Mix(p.Panel, p.Sel, 0.45) : i % 2 == 1 ? ColorX.Mix(p.Panel, p.Well, 0.35) : p.Panel;
        g.Clear(back);
        using (var line = new Pen(ColorX.Mix(p.Line, p.Panel, 0.3))) g.DrawLine(line, 0, RowH - 1, width, RowH - 1);
        var item = items[i];
        var dim = Dimmed?.Invoke(item) == true;
        if (Checked?.Invoke(item) is bool on)
        {
            var box = new RectangleF(11, (RowH - 16) / 2f, 16, 16);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (on) { Ui.FillRound(g, p.Accent, box, 4); Ui.DrawGlyph(g, Glyph.Check, 7.5f, p.AccentInk, Rectangle.Round(box)); }
            else Ui.FillRound(g, p.Well, box, 4, p.Border);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        }
        var font = Fonts.Body(9.5f);
        for (var c = 0; c < Columns.Count; c++)
        {
            var color = Columns[c].RowColor?.Invoke(item, p) ?? Columns[c].Color?.Invoke(p) ?? p.Ink;
            if (dim) color = ColorX.Mix(color, back, 0.55);
            TextRenderer.DrawText(g, Columns[c].Text(item), font, new Rectangle(xs[c] + Pad, 0, xs[c + 1] - xs[c] - Pad * 2, RowH), color, back,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        if (i == current && Focused) using (var focus = new Pen(ColorX.Alpha(p.Accent, 140))) g.DrawRectangle(focus, 0, 0, width - 1, RowH - 2);
        return image;
    }

    private int IndexOf(InsightRow row)
    {
        for (var i = 0; i < items.Count; i++) if (ReferenceEquals(items[i], row)) return i;
        return -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (MaxOffset <= 0) { base.OnMouseWheel(e); return; }
        if (Theme.Animations) scroller.Wheel(e.Delta);
        else SetOffset(offset - e.Delta / 120 * 3 * RowH);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = RowAt(e.Location);
        if (index == hover) return;
        InvalidateRow(hover); hover = index; InvalidateRow(hover);
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); InvalidateRow(hover); hover = -1; }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var index = RowAt(e.Location);
        if (index < 0) return;
        if (e.Button == MouseButtons.Left && Checked?.Invoke(items[index]) is not null && e.X < CheckW && ModifierKeys == Keys.None)
        {
            var rows = (selected.Contains(index) && selected.Count > 1 ? SelectedItems : new List<InsightRow> { items[index] })
                .Where(r => Checked(r) is not null).ToList();
            CheckToggled?.Invoke(rows);
            foreach (var r in rows) DropRowImage(IndexOf(r));
            Invalidate();
            return;
        }
        if (e.Button == MouseButtons.Right) { if (!selected.Contains(index)) Select(index, ModifierKeys & ~Keys.Control); return; }
        if (e.Button == MouseButtons.Left) Select(index, ModifierKeys);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var index = RowAt(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0) RowActivated?.Invoke(items[index]);
    }

    private void Select(int index, Keys modifiers)
    {
        foreach (var k in selected) DropRowImage(k);
        DropRowImage(current);
        if ((modifiers & Keys.Shift) != 0 && anchor >= 0)
        {
            if ((modifiers & Keys.Control) == 0) selected.Clear();
            for (var i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++) selected.Add(i);
        }
        else if ((modifiers & Keys.Control) != 0)
        {
            if (!selected.Remove(index)) selected.Add(index);
            anchor = index;
        }
        else
        {
            selected.Clear(); selected.Add(index); anchor = index;
        }
        current = index;
        foreach (var k in selected) DropRowImage(k);
        DropRowImage(index);
        EnsureVisible(index);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void EnsureVisible(int index)
    {
        var top = index * RowH;
        if (top < offset) scroller.To(top);
        else if (top + RowH > offset + ViewH) scroller.To(top + RowH - ViewH);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (items.Count == 0) return;
        var page = Math.Max(1, ViewH / RowH - 1);
        int? target = e.KeyCode switch
        {
            Keys.Up => Math.Max(0, current - 1), Keys.Down => Math.Min(items.Count - 1, current + 1),
            Keys.PageUp => Math.Max(0, current - page), Keys.PageDown => Math.Min(items.Count - 1, current + page),
            Keys.Home => 0, Keys.End => items.Count - 1, _ => null
        };
        if (target is { } t) { Select(t, e.Modifiers & Keys.Shift); e.Handled = true; }
        else if (e.KeyCode == Keys.A && e.Control) { for (var i = 0; i < items.Count; i++) selected.Add(i); ClearRowCache(); Invalidate(); SelectionChanged?.Invoke(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && CurrentItem is { } item) { RowActivated?.Invoke(item); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); InvalidateRow(current); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); InvalidateRow(current); }

    private void InvalidateRow(int index)
    {
        if (index < 0) return;
        DropRowImage(index);
        Invalidate(new Rectangle(0, HeaderH + index * RowH - offset, TableW, RowH));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { scroller.Stop(); Theme.Changed -= Invalidate; ClearRowCache(); }
        base.Dispose(disposing);
    }
}
