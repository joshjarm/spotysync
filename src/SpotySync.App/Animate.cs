namespace SpotySyncApp;

/// <summary>
/// A 0..1 value that eases toward a target on the display frame clock (used for hover highlights,
/// switch knobs and similar). Calls <see cref="Changed"/> each frame so the owner can repaint.
/// </summary>
internal sealed class Tween
{
    private readonly Control owner;
    private readonly double seconds;
    private float target;
    private bool running;
    public float Value { get; private set; }
    public event Action? Changed;

    public Tween(Control owner, double seconds = 0.14) { this.owner = owner; this.seconds = seconds; }

    public void To(float value)
    {
        target = Math.Clamp(value, 0f, 1f);
        if (!Theme.Animations || !owner.IsHandleCreated) { Value = target; Changed?.Invoke(); return; }
        if (!running && Math.Abs(Value - target) > 0.001f) { running = true; FrameClock.Subscribe(owner, Frame); }
    }

    public void Snap(float value) { target = Value = Math.Clamp(value, 0f, 1f); Changed?.Invoke(); }

    private void Frame(double dt)
    {
        if (owner.IsDisposed) { Stop(); return; }
        var step = (float)(dt / seconds);
        Value = Value < target ? Math.Min(target, Value + step) : Math.Max(target, Value - step);
        if (Math.Abs(Value - target) < 0.001f) { Value = target; Stop(); }
        Changed?.Invoke();
    }

    private void Stop() { running = false; FrameClock.Unsubscribe(Frame); }

    /// <summary>Smooth ease-in-out of the raw value (for colors/positions).</summary>
    public float Eased => Value * Value * (3 - 2 * Value);
}


/// <summary>
/// Borderless, click-through helper window (cross-fades, drag previews). Windows normally shows a new window
/// with its default light background for a frame before the first paint - a bright white flash on a dark
/// UI. This window starts in the theme's dark color and stays cloaked (invisible to the compositor)
/// until its first real frame has been painted.
/// </summary>
internal class OverlayWindow : Form
{
    private bool cloaked;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.P.Bg; // never the light system default
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x20 | 0x80 | 0x08000000; return cp; } // click-through, tool window, no activation
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetCloak(true);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // after the first real paint, reveal the window - its first visible frame is the finished content
        if (m.Msg == 0x000F /* WM_PAINT */ && cloaked) BeginInvoke(() => SetCloak(false));
    }

    /// <summary>Paints the window now and uncloaks it, then waits for the compositor so it is really on screen.</summary>
    public void RevealNow()
    {
        if (!IsHandleCreated) return;
        Update(); // synchronous WM_PAINT
        SetCloak(false);
        try { DwmFlush(); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private void SetCloak(bool on)
    {
        if (!IsHandleCreated) return;
        try { var value = on ? 1 : 0; DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref value, sizeof(int)); cloaked = on; }
        catch { cloaked = false; }
    }

    protected override void Dispose(bool disposing) { if (disposing) BackgroundImage?.Dispose(); base.Dispose(disposing); }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
