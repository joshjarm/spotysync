using System.Runtime.InteropServices;

namespace SpotySyncApp;

/// <summary>
/// Eases any scroll offset toward a goal on the display frame clock. The scroll source is given as
/// get/set/max callbacks, so it works for custom-painted lists, RichTextBoxes and panels alike.
/// </summary>
internal sealed class ScrollAnimator
{
    private readonly Control owner;
    private readonly Func<int> get;
    private readonly Action<int> set;
    private readonly Func<int> max;
    private double current, goal;
    private bool running;

    public ScrollAnimator(Control owner, Func<int> get, Action<int> set, Func<int> max)
    {
        this.owner = owner; this.get = get; this.set = set; this.max = max;
    }

    public bool Running => running;

    /// <summary>The offset the animation is heading for (the current offset when idle).</summary>
    public int Goal => running ? (int)Math.Round(goal) : get();

    public void Wheel(int delta)
    {
        var step = Math.Max(1, SystemInformation.MouseWheelScrollLines) * 40.0;
        if (!running) { current = get(); goal = current; }
        To(goal - delta / 120.0 * step);
    }

    public void To(double y)
    {
        if (!running) current = get();
        goal = Math.Clamp(y, 0, Math.Max(0, max()));
        if (!Theme.Animations || !owner.IsHandleCreated) { set((int)Math.Round(goal)); return; }
        if (!running) { running = true; FrameClock.Subscribe(owner, Frame); }
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        FrameClock.Unsubscribe(Frame);
    }

    private void Frame(double dt)
    {
        if (owner.IsDisposed) { Stop(); return; }
        current += (goal - current) * (1 - Math.Exp(-dt * 16));
        if (Math.Abs(goal - current) < 0.6) { current = goal; Stop(); }
        set((int)Math.Round(current));
    }
}

/// <summary>RichTextBox with pixel-smooth eased wheel scrolling (RichEdit scrolls in pixels via EM_SETSCROLLPOS).</summary>
internal class SmoothRichText : RichTextBox
{
    private const int WM_MOUSEWHEEL = 0x020A, WM_VSCROLL = 0x0115, WM_SETREDRAW = 0x000B;
    private const int EM_GETSCROLLPOS = 0x04DD, EM_SETSCROLLPOS = 0x04DE;
    private ScrollAnimator? animator;
    private int updateDepth;

    private ScrollAnimator Animator => animator ??= new ScrollAnimator(this, () => ScrollY, v => ScrollY = v, () => MaxScroll);

    public int ScrollY
    {
        get
        {
            if (!IsHandleCreated) return 0;
            var p = new POINT();
            SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
            return p.Y;
        }
        set
        {
            if (!IsHandleCreated) return;
            var p = new POINT();
            SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
            p.Y = Math.Max(0, value);
            SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p);
        }
    }

    public int MaxScroll
    {
        get
        {
            if (!IsHandleCreated) return 0;
            var info = new SCROLLINFO { cbSize = Marshal.SizeOf<SCROLLINFO>(), fMask = 0x1 | 0x2 /* SIF_RANGE | SIF_PAGE */ };
            if (!GetScrollInfo(Handle, 1 /* SB_VERT */, ref info)) return 0;
            return Math.Max(0, info.nMax - Math.Max(1, info.nPage) + 1);
        }
    }

    /// <summary>Stops repainting while many styled runs are appended (much faster and no flashing).</summary>
    public void BeginUpdate()
    {
        if (updateDepth++ == 0 && IsHandleCreated) SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    public void EndUpdate()
    {
        if (updateDepth == 0 || --updateDepth > 0 || !IsHandleCreated) return;
        SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e) { animator?.Stop(); base.OnTextChanged(e); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL && Theme.Animations && (ModifierKeys & Keys.Control) == 0 && MaxScroll > 0)
        {
            var delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            Animator.Wheel(delta);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_VSCROLL) animator?.Stop(); // the user grabbed the scrollbar
        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCROLLINFO { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref POINT lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);
}

/// <summary>
/// DataGridView that never scrolls itself. It sits at full content height inside a <see cref="GridScrollHost"/>,
/// which scrolls it pixel by pixel (a DataGridView alone can only jump whole rows, which looks choppy).
/// Column headers are drawn by a separate <see cref="GridHeader"/> so they stay put.
/// </summary>
internal sealed class SmoothGrid : DataGridView
{
    public SmoothGrid()
    {
        ScrollBars = ScrollBars.None;
        ColumnHeadersVisible = false;
        DoubleBuffered = true;
    }

    public int ContentHeight => Rows.GetRowsHeight(DataGridViewElementStates.Visible) + 1;

    // no old-fashioned dotted focus rectangle around the current cell
    protected override bool ShowFocusCues => false;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Parent is GridScrollHost host && host.Wheel(e.Delta))
        {
            if (e is HandledMouseEventArgs h) h.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }
}

/// <summary>Scrolls a <see cref="SmoothGrid"/> smoothly and keeps the current row in view.</summary>
internal sealed class GridScrollHost : Panel
{
    private readonly SmoothGrid grid;
    private readonly SmoothScroller smooth;
    private bool laying;

    public GridScrollHost(SmoothGrid grid)
    {
        this.grid = grid;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        // vertical scrolling only
        AutoScroll = false;
        HorizontalScroll.Maximum = 0; HorizontalScroll.Enabled = false; HorizontalScroll.Visible = false;
        AutoScroll = true;
        grid.Dock = DockStyle.None;
        Controls.Add(grid);
        smooth = new SmoothScroller(this);
        grid.RowsAdded += (_, _) => PerformLayout();
        grid.RowsRemoved += (_, _) => PerformLayout();
        grid.RowStateChanged += (_, e) => { if (e.StateChanged == DataGridViewElementStates.Visible) PerformLayout(); };
        grid.RowHeightChanged += (_, _) => PerformLayout();
        grid.CurrentCellChanged += (_, _) => EnsureCurrentVisible();
        Ui.DarkScrollbars(this);
    }

    public bool Wheel(int delta)
    {
        if (!VerticalScroll.Visible) return false;
        smooth.Wheel(delta);
        return true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Wheel(e.Delta)) { if (e is HandledMouseEventArgs h) h.Handled = true; return; }
        base.OnMouseWheel(e);
    }

    // Focusing the grid must not make the panel jump to the grid's top edge.
    protected override Point ScrollToControl(Control activeControl) => AutoScrollPosition;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (laying) return;
        laying = true;
        try
        {
            var content = grid.ContentHeight;
            var needBar = content > Height;
            var width = Math.Max(0, Width - (needBar ? SystemInformation.VerticalScrollBarWidth : 0));
            var height = needBar ? content : Height;
            if (grid.Width != width || grid.Height != height || grid.Left != 0 || grid.Top != AutoScrollPosition.Y)
                grid.SetBounds(0, AutoScrollPosition.Y, width, height);
            base.OnLayout(levent);
        }
        finally { laying = false; }
    }

    /// <summary>Scrolls (eased) just enough to show the grid's current row.</summary>
    public void EnsureCurrentVisible()
    {
        var row = grid.CurrentRow;
        if (row is null || !row.Visible || ClientSize.Height <= 0) return;
        var top = 0;
        for (var i = 0; i < row.Index; i++)
            if (grid.Rows[i].Visible) top += grid.Rows[i].Height;
        var view = -AutoScrollPosition.Y;
        var h = ClientSize.Height;
        if (top < view) smooth.ScrollTo(top);
        else if (top + row.Height > view + h) smooth.ScrollTo(top + row.Height - h);
    }
}

/// <summary>Fixed column header strip for a <see cref="SmoothGrid"/> (matches its column widths).</summary>
internal sealed class GridHeader : ThemedControl
{
    private readonly DataGridView grid;

    public GridHeader(DataGridView grid)
    {
        this.grid = grid;
        Height = grid.ColumnHeadersHeight;
        grid.ColumnWidthChanged += (_, _) => Invalidate();
        grid.SizeChanged += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        var font = Fonts.Body(8.25f, FontStyle.Bold);
        var x = 0;
        foreach (var column in grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex))
        {
            var bounds = new Rectangle(x + 6, 0, Math.Max(0, column.Width - 12), Height);
            Ui.Text(g, column.HeaderText, font, bounds, p.Dim, column.Index == 0 ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
            x += column.Width;
        }
        using var line = new Pen(ColorX.Mix(p.Line, p.Panel, 0.3));
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}
