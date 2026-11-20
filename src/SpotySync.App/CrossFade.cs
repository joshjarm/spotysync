using System.Runtime.InteropServices;

namespace SpotySyncApp;

/// <summary>
/// Cross-fade for theme changes, page switches and module swaps, drawn INSIDE the window: a child layer on top
/// of the host shows the old look and blends into the new one. (It used to be a separate layered top-level
/// window; Windows sometimes showed that window as solid white for a frame — the white flash, worst when
/// maximized and when clicking quickly.) All image work is plain GDI on screen-format bitmaps (fast), and
/// rapid repeats reuse the running layer instead of stacking new ones.
/// </summary>
internal static class CrossFade
{
    private static FadeLayer? active;

    public static void Run(Control host, Action change, double seconds = 0.22)
    {
        var form = host.FindForm();
        if (!Theme.Animations || form is null || !form.Visible || form.WindowState == FormWindowState.Minimized ||
            !host.IsHandleCreated || host.Width < 4 || host.Height < 4 || form.Location.X < -10000)
        {
            active?.Finish();
            change();
            return;
        }

        // A fade is still running (fast clicking): don't capture/render again — just switch instantly. Without the
        // old overlay window an instant switch does not flash, and repeated fades made rapid clicking laggy.
        if (active is { IsDisposed: false })
        {
            active.Finish();
            change();
            return;
        }

        var layer = new FadeLayer(host.ClientSize) { Bounds = host.ClientRectangle };
        if (!layer.CaptureOld(host.RectangleToScreen(host.ClientRectangle))) { layer.Dispose(); change(); return; }
        host.Controls.Add(layer);
        layer.BringToFront();
        layer.Update(); // the old look is on screen before anything underneath changes
        active = layer;
        try
        {
            change();
            layer.Start(seconds);
        }
        catch
        {
            layer.Finish();
            throw;
        }
    }

    internal static void Ended(FadeLayer layer) { if (active == layer) active = null; }
}

/// <summary>Opaque, click-through child control that blends an old snapshot over the new look (GDI AlphaBlend).</summary>
internal sealed class FadeLayer : Control, ISelfBackground
{
    private readonly Size size;
    private IntPtr oldDc, newDc, bufferDc, oldBmp, newBmp, bufferBmp, oldPrev, newPrev, bufferPrev;
    private double elapsed, seconds = 0.22, sinceRepaint;
    private byte alpha = 255;
    private bool running, blending;

    public FadeLayer(Size size)
    {
        this.size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer, false);
        TabStop = false;
        var screen = GetDC(IntPtr.Zero);
        try
        {
            (oldDc, oldBmp, oldPrev) = Surface(screen);
            (newDc, newBmp, newPrev) = Surface(screen);
            (bufferDc, bufferBmp, bufferPrev) = Surface(screen);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    private (IntPtr dc, IntPtr bmp, IntPtr prev) Surface(IntPtr screen)
    {
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, size.Width, size.Height);
        return (dc, bmp, SelectObject(dc, bmp));
    }

    /// <summary>Copies what is on screen now (the old look).</summary>
    public bool CaptureOld(Rectangle screenRect)
    {
        var screen = GetDC(IntPtr.Zero);
        try { return BitBlt(oldDc, 0, 0, size.Width, size.Height, screen, screenRect.X, screenRect.Y, SRCCOPY); }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    public void Start(double duration)
    {
        RenderNew();
        seconds = Math.Max(0.05, duration);
        elapsed = 0; alpha = 255; blending = true;
        if (!running) { running = true; FrameClock.Subscribe(this, Frame); }
        Invalidate(); Update();
    }

    /// <summary>Has the window paint its new look into <see cref="newDc"/> (WM_PRINT, everything except this layer).</summary>
    private void RenderNew()
    {
        var host = Parent!;
        host.FindForm()?.PerformLayout();
        if (host is Form)
        {
            using var brush = new SolidBrushHandle(host.BackColor);
            FillRect(newDc, new RECT { Right = size.Width, Bottom = size.Height }, brush.Handle);
        }
        else SendMessage(host.Handle, WM_PRINT, newDc, (IntPtr)(PRF_CLIENT | PRF_ERASEBKGND)); // its own background/frame
        for (var i = host.Controls.Count - 1; i >= 0; i--) // bottom of the z-order first
        {
            var child = host.Controls[i];
            if (child == this || !child.Visible || !child.IsHandleCreated || child.Width <= 0 || child.Height <= 0) continue;
            SetViewportOrgEx(newDc, child.Left, child.Top, out var previous);
            SendMessage(child.Handle, WM_PRINT, newDc, (IntPtr)(PRF_CHILDREN | PRF_CLIENT | PRF_ERASEBKGND | PRF_NONCLIENT));
            SetViewportOrgEx(newDc, previous.X, previous.Y, out _);
        }
    }

    private void Frame(double dt)
    {
        if (IsDisposed) { running = false; FrameClock.Unsubscribe(Frame); return; }
        elapsed += dt;
        var t = Math.Clamp(elapsed / seconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        alpha = (byte)Math.Round((1 - eased) * 255);
        if (t >= 1) { Finish(); return; }
        // ~75 updates a second is plenty for a fade this short; each update repaints the whole host
        sinceRepaint += dt;
        if (sinceRepaint < 1.0 / 75) return;
        sinceRepaint = 0;
        Invalidate(); Update();
    }

    public void Finish()
    {
        if (running) { running = false; FrameClock.Unsubscribe(Frame); }
        CrossFade.Ended(this);
        if (IsDisposed) return;
        // underneath is already fully painted in the new look; removing the layer just uncovers it
        Parent?.Controls.Remove(this);
        Dispose();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084 /* WM_NCHITTEST */) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT: clicks go through
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (oldDc == IntPtr.Zero) return;
        var hdc = e.Graphics.GetHdc();
        try
        {
            if (!blending) { BitBlt(hdc, 0, 0, size.Width, size.Height, oldDc, 0, 0, SRCCOPY); return; }
            BitBlt(bufferDc, 0, 0, size.Width, size.Height, newDc, 0, 0, SRCCOPY);
            AlphaBlend(bufferDc, 0, 0, size.Width, size.Height, oldDc, 0, 0, size.Width, size.Height, new BLENDFUNCTION { SourceConstantAlpha = alpha });
            BitBlt(hdc, 0, 0, size.Width, size.Height, bufferDc, 0, 0, SRCCOPY); // one copy to screen: never a half-drawn frame
        }
        finally { e.Graphics.ReleaseHdc(hdc); }
    }

    protected override void Dispose(bool disposing)
    {
        if (running) { running = false; FrameClock.Unsubscribe(Frame); }
        void Free(ref IntPtr dc, ref IntPtr bmp, IntPtr prev)
        {
            if (dc != IntPtr.Zero) { SelectObject(dc, prev); DeleteDC(dc); dc = IntPtr.Zero; }
            if (bmp != IntPtr.Zero) { DeleteObject(bmp); bmp = IntPtr.Zero; }
        }
        Free(ref oldDc, ref oldBmp, oldPrev);
        Free(ref newDc, ref newBmp, newPrev);
        Free(ref bufferDc, ref bufferBmp, bufferPrev);
        base.Dispose(disposing);
    }

    private sealed class SolidBrushHandle : IDisposable
    {
        public IntPtr Handle { get; }
        public SolidBrushHandle(Color c) => Handle = CreateSolidBrush(c.R | (c.G << 8) | (c.B << 16));
        public void Dispose() => DeleteObject(Handle);
    }

    private const int SRCCOPY = 0x00CC0020, WM_PRINT = 0x0317;
    private const int PRF_NONCLIENT = 0x2, PRF_CLIENT = 0x4, PRF_ERASEBKGND = 0x8, PRF_CHILDREN = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, in RECT rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool SetViewportOrgEx(IntPtr dc, int x, int y, out POINT previous);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll", EntryPoint = "GdiAlphaBlend")]
    private static extern bool AlphaBlend(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION blend);
}
