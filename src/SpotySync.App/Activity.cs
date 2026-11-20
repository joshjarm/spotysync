using System.Drawing.Drawing2D;

namespace SpotySyncApp;

/// <summary>Compact, scrollable list of the current sync preview (playlists with changes first).</summary>
internal sealed class PlanList : ThemedControl
{
    private List<SpotySync.PlaylistPlan> rows = new();
    private int scroll, hover = -1;
    private const int RowH = 30;
    public event Action<string>? Open;

    public void SetPlan(SpotySync.SyncPlan? plan)
    {
        rows = plan is null ? new() : plan.Playlists
            .OrderByDescending(x => x.IsBlocked).ThenByDescending(x => x.ChangeCount).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        animator?.Stop();
        scroll = 0;
        Invalidate();
    }

    private int MaxScroll => Math.Max(0, rows.Count * RowH - Height);
    private ScrollAnimator? animator;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        animator ??= new ScrollAnimator(this, () => scroll, v => { scroll = v; Invalidate(); }, () => MaxScroll);
        var goal = animator.Goal;
        // at the end of the list the wheel goes on to scroll the dashboard
        var moves = MaxScroll > 0 && (e.Delta > 0 ? goal > 0 : goal < MaxScroll);
        if (moves) animator.Wheel(e.Delta);
        if (e is HandledMouseEventArgs h) h.Handled = moves;
        base.OnMouseWheel(e);
    }

    protected override void OnResize(EventArgs e) { scroll = Math.Min(scroll, MaxScroll); base.OnResize(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = (e.Y + scroll) / RowH;
        if (i >= rows.Count) i = -1;
        if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (hover >= 0 && hover < rows.Count) Open?.Invoke(rows[hover].Name);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        if (rows.Count == 0)
        {
            Ui.Text(g, "No preview yet — build one to see every pending change here.", Fonts.Body(9.25f), new Rectangle(0, 0, Width, 40), p.Dim);
            return;
        }
        var first = scroll / RowH;
        for (var i = first; i < rows.Count; i++)
        {
            var y = i * RowH - scroll;
            if (y > Height) break;
            var r = rows[i];
            if (i == hover) Ui.FillRound(g, p.Hover, new RectangleF(0, y + 1, Width - 1, RowH - 2), Theme.R(6));
            var color = r.IsBlocked ? p.Err : r.SpotifyRemoveIds.Count + r.LocalRemoveFiles.Count > 0 ? p.Warn : r.ChangeCount == 0 ? p.Muted : p.Ink;
            var x = Width - 8;
            void Badge(int count, string prefix, Color tone)
            {
                if (count <= 0) return;
                var text = prefix + count;
                var w = TextRenderer.MeasureText(text, Fonts.Semibold(8.5f)).Width + 8;
                x -= w;
                Ui.FillRound(g, ColorX.Mix(p.Panel, tone, 0.18), new RectangleF(x, y + 6, w, RowH - 12), (RowH - 12) / 2f);
                Ui.Text(g, text, Fonts.Semibold(8.5f), new Rectangle(x, y + 6, w, RowH - 12), tone, TextFormatFlags.HorizontalCenter);
                x -= 6;
            }
            if (r.IsBlocked) Badge(1, "blocked ", p.Err);
            Badge(r.PlatformSpecific.Count, "• ", p.Protected);
            Badge(r.SpotifyRemoveIds.Count + r.LocalRemoveFiles.Count, "−", p.Removal);
            Badge(r.LocalAddFiles.Count, "local +", p.LocalAdd);
            Badge(r.SpotifyAddIds.Count, "spotify +", p.SpotifyAdd);
            if (r.ChangeCount == 0 && !r.IsBlocked) { var w = 60; x -= w; Ui.Text(g, "in sync", Fonts.Body(8.5f), new Rectangle(x, y, w, RowH), p.Dim, TextFormatFlags.Right); }
            Ui.Text(g, r.Name, Fonts.Body(9.5f), new Rectangle(6, y, Math.Max(40, x - 12), RowH), color);
        }
        if (MaxScroll > 0)
        {
            var track = Height - 4;
            var thumb = Math.Max(24, track * Height / (rows.Count * RowH));
            var ty = 2 + (track - thumb) * scroll / MaxScroll;
            Ui.FillRound(g, ColorX.Mix(p.Panel, p.Accent, 0.4), new RectangleF(Width - 3, ty, 3, thumb), 1.5f);
        }
    }
}

/// <summary>Library insight counts; each row opens the matching Library tab.</summary>
internal sealed class LibraryStats : ThemedControl
{
    private (string Label, string Value, string Glyph)[] items = Array.Empty<(string, string, string)>();
    private int hover = -1;
    private const int RowH = 30;
    public event Action<int>? Open;

    public void SetItems((string Label, string Value, string Glyph)[] values) { items = values; Invalidate(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = e.Y / RowH;
        if (i >= items.Length) i = -1;
        if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (hover >= 0) Open?.Invoke(hover); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        for (var i = 0; i < items.Length; i++)
        {
            var y = i * RowH;
            if (y + RowH > Height) break;
            if (i == hover) Ui.FillRound(g, p.Hover, new RectangleF(0, y + 1, Width - 1, RowH - 2), Theme.R(6));
            Ui.DrawGlyph(g, items[i].Glyph, 9.5f, p.Accent, new Rectangle(4, y, 20, RowH));
            Ui.Text(g, items[i].Label, Fonts.Body(9.25f), new Rectangle(30, y, Width - 100, RowH), p.Ink);
            Ui.Text(g, items[i].Value, Fonts.Display(12f), new Rectangle(Width - 74, y, 66, RowH), items[i].Value == "—" ? p.Dim : p.Accent2, TextFormatFlags.Right);
        }
    }
}

/// <summary>Click-through, borderless overlay that follows the cursor: a card snapshot or an accent outline/bar.</summary>
internal sealed class DragGhost : OverlayWindow
{
    private readonly bool outline;

    private DragGhost(bool outline)
    {
        this.outline = outline;
        TopMost = true;
    }

    public static DragGhost Snapshot(Control source)
    {
        var bitmap = new Bitmap(Math.Max(1, source.Width), Math.Max(1, source.Height));
        source.DrawToBitmap(bitmap, new Rectangle(Point.Empty, source.Size));
        return new DragGhost(false) { BackgroundImage = bitmap, Size = source.Size, Opacity = 0.82 };
    }

    public static DragGhost Outline() => new(true) { BackColor = Color.Magenta, TransparencyKey = Color.Magenta };

    public static DragGhost Bar() => new(false) { BackColor = Theme.P.Accent, Opacity = 0.95 };

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!outline) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.Rounded(new RectangleF(1.5f, 1.5f, Width - 3, Height - 3), Theme.R(14));
        using var pen = new Pen(Theme.P.Accent, 3f) { DashStyle = DashStyle.Dash };
        g.DrawPath(pen, path);
    }

}

/// <summary>Evenly spaced KPI tiles (Sync preview summary).</summary>
internal sealed class KpiStrip : Panel
{
    private readonly Dictionary<string, KpiTile> tiles = new();
    private readonly List<KpiTile> order = new();

    public void Add(string id, string caption)
    {
        var tile = new KpiTile { Caption = caption };
        tiles[id] = tile; order.Add(tile); Controls.Add(tile);
    }

    public void Set(string id, string value, Color? color = null)
    {
        if (!tiles.TryGetValue(id, out var tile)) return;
        tile.Value = value; tile.ValueColor = color;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (order.Count == 0 || Width <= 0) return;
        const int gap = 10;
        var perRow = Math.Clamp((Width + gap) / (130 + gap), 1, order.Count);
        var w = (Width - gap * (perRow - 1)) / perRow;
        for (var i = 0; i < order.Count; i++)
        {
            var visible = i < perRow;
            order[i].Visible = visible;
            if (visible) order[i].SetBounds(i * (w + gap), 2, w, 60);
        }
    }
}
