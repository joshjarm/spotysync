using System.Drawing.Drawing2D;
using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// The download speed dial: − and + buttons around a notched track, from "1 song at a time with pauses" (left) to
/// "32 songs at a time" (right). Click or drag the track, scroll the wheel, or use the arrow keys. It works during a
/// download: the running batch restarts at the new speed and finished songs are kept.
/// </summary>
internal sealed class SpeedDial : Control, ISelfBackground
{
    private int hoverPart = -1; // 0 = minus, 1 = track, 2 = plus
    private bool dragging;

    /// <summary>Compact (status bar) shows "12×"; the full version shows "12 songs at a time".</summary>
    public bool Compact { get; set; }

    public SpeedDial()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = Fonts.Body(9.5f);
        Size = new Size(300, 30);
        Cursor = Cursors.Hand;
        TabStop = true;
        DownloadSpeed.Changed += OnSpeedChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DownloadSpeed.Changed -= OnSpeedChanged;
        base.Dispose(disposing);
    }

    private void OnSpeedChanged(bool byUser)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(Invalidate); else Invalidate();
    }

    public string Caption => Compact ? DownloadSpeed.Current.Short + " at once" : DownloadSpeed.Current.Label;

    private int LabelWidth => Compact ? 78 : 168;
    private Rectangle MinusRect => new(0, (Height - 24) / 2, 24, 24);
    private Rectangle PlusRect => new(Width - LabelWidth - 24 - 6, (Height - 24) / 2, 24, 24);
    private RectangleF TrackRect => new(MinusRect.Right + 10, Height / 2f - 3, Math.Max(20, PlusRect.Left - 10 - (MinusRect.Right + 10)), 6);
    private Rectangle LabelRect => new(Width - LabelWidth, 0, LabelWidth, Height);

    private int PartAt(Point p) =>
        MinusRect.Contains(p) ? 0 : PlusRect.Contains(p) ? 2 :
        p.X >= TrackRect.Left - 6 && p.X <= TrackRect.Right + 6 ? 1 : -1;

    private int IndexAt(int x)
    {
        var t = TrackRect;
        var steps = DownloadSpeed.Levels.Length - 1;
        return (int)Math.Round(Math.Clamp((x - t.Left) / t.Width, 0, 1) * steps);
    }

    private static void Step(int delta) => DownloadSpeed.Set(DownloadSpeed.Index + delta, byUser: true);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        switch (PartAt(e.Location))
        {
            case 0: Step(-1); break;
            case 2: Step(+1); break;
            case 1: dragging = true; DownloadSpeed.Set(IndexAt(e.X), byUser: true); break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) { DownloadSpeed.Set(IndexAt(e.X), byUser: true); return; }
        var part = PartAt(e.Location);
        if (part != hoverPart) { hoverPart = part; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverPart = -1; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Step(e.Delta > 0 ? 1 : -1);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down or Keys.Subtract or Keys.OemMinus: Step(-1); e.Handled = true; break;
            case Keys.Right or Keys.Up or Keys.Add or Keys.Oemplus: Step(+1); e.Handled = true; break;
            case Keys.Home: DownloadSpeed.Set(0, true); e.Handled = true; break;
            case Keys.End: DownloadSpeed.Set(DownloadSpeed.Levels.Length - 1, true); e.Handled = true; break;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Ui.PaintBack(e.Graphics, this);
        var p = Theme.P;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var index = DownloadSpeed.Index;
        var steps = DownloadSpeed.Levels.Length - 1;

        DrawButton(g, MinusRect, plus: false, hoverPart == 0, index > 0);
        DrawButton(g, PlusRect, plus: true, hoverPart == 2, index < steps);

        var track = TrackRect;
        Ui.FillRound(g, ColorX.Mix(p.Field, p.Well, 0.4), track, 3, p.Line);
        var fraction = index / (float)steps;
        var knobX = track.Left + fraction * track.Width;
        if (knobX - track.Left > 1)
        {
            // slow end calm, fast end bright
            using var brush = new LinearGradientBrush(new RectangleF(track.Left - 1, track.Top, track.Width + 2, track.Height),
                ColorX.Mix(p.Accent, p.Panel, 0.55), p.Accent, LinearGradientMode.Horizontal);
            using var path = Ui.Rounded(new RectangleF(track.Left, track.Top, knobX - track.Left, track.Height), 3);
            g.FillPath(brush, path);
        }
        for (var i = 0; i <= steps; i++)
        {
            var x = track.Left + i / (float)steps * track.Width;
            using var tick = new SolidBrush(i <= index ? ColorX.Alpha(p.AccentInk, 140) : ColorX.Alpha(p.Muted, 120));
            g.FillEllipse(tick, x - 1.25f, track.Top + track.Height / 2 - 1.25f, 2.5f, 2.5f);
        }
        var knobSize = hoverPart == 1 || dragging ? 16f : 14f;
        var knob = new RectangleF(knobX - knobSize / 2, Height / 2f - knobSize / 2, knobSize, knobSize);
        using (var fill = new SolidBrush(p.Accent)) g.FillEllipse(fill, knob);
        using (var ring = new Pen(ColorX.Mix(p.AccentInk, p.Accent, 0.3), 2f)) g.DrawEllipse(ring, RectangleF.Inflate(knob, -3, -3));
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(ColorX.Alpha(p.Accent, 150), 1.5f);
            g.DrawEllipse(pen, RectangleF.Inflate(knob, 3, 3));
        }
        Ui.Text(g, Caption, Compact ? Fonts.Semibold(9.5f) : Font, LabelRect, Enabled ? p.Ink : p.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawButton(Graphics g, Rectangle r, bool plus, bool hover, bool enabled)
    {
        var p = Theme.P;
        Ui.FillRound(g, hover && enabled ? p.Hover : ColorX.Mix(p.Field, p.Well, 0.3), r, 7, hover && enabled ? ColorX.Mix(p.Border, p.Accent, 0.5) : p.Border);
        using var pen = new Pen(enabled ? p.Ink : p.Dim, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
        if (plus) g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
    }
}
