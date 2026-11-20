using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpotySyncApp;

/// <summary>
/// Display-synchronised frame ticks (one per monitor refresh via DwmFlush, e.g. 144 Hz on a 144 Hz screen),
/// delivered on the UI thread. WinForms timers are capped at about 64 Hz, which judders on fast displays.
/// The background thread only runs while something is animating.
/// </summary>
internal static class FrameClock
{
    private static readonly object Gate = new();
    private static readonly List<Action<double>> Subscribers = new();
    private static Control? marshal; // a long-lived window on the UI thread
    private static Thread? thread;
    private static int pending;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double lastFrame;

    /// <summary>Calls <paramref name="onFrame"/> on <paramref name="owner"/>'s UI thread once per display refresh.</summary>
    public static void Subscribe(Control owner, Action<double> onFrame)
    {
        lock (Gate)
        {
            var host = owner.FindForm() ?? owner;
            if (marshal is null || marshal.IsDisposed || !marshal.IsHandleCreated) marshal = host;
            if (!Subscribers.Contains(onFrame)) Subscribers.Add(onFrame);
            if (thread is null)
            {
                lastFrame = Clock.Elapsed.TotalSeconds;
                thread = new Thread(Loop) { IsBackground = true, Name = "SpotySync frame clock", Priority = ThreadPriority.AboveNormal };
                thread.Start();
            }
        }
    }

    public static void Unsubscribe(Action<double> onFrame) { lock (Gate) Subscribers.Remove(onFrame); }

    private static void Loop()
    {
        while (true)
        {
            lock (Gate) { if (Subscribers.Count == 0) { thread = null; return; } }
            try { if (DwmFlush() != 0) Thread.Sleep(4); } catch { Thread.Sleep(4); }
            if (Interlocked.Exchange(ref pending, 1) == 1) continue; // the UI is still busy with the last frame
            var target = marshal;
            if (target is null || target.IsDisposed || !target.IsHandleCreated) { pending = 0; Thread.Sleep(10); continue; }
            try { target.BeginInvoke(new Action(RunFrame)); } catch { pending = 0; }
        }
    }

    private static void RunFrame()
    {
            {
                pending = 0;
                var now = Clock.Elapsed.TotalSeconds;
                var dt = Math.Clamp(now - lastFrame, 0.001, 0.05);
                lastFrame = now;
                Action<double>[] list;
                lock (Gate) list = Subscribers.ToArray();
                foreach (var subscriber in list) { try { subscriber(dt); } catch (Exception ex) { Debug.WriteLine(ex); Unsubscribe(subscriber); } }
            }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}

/// <summary>Eased, refresh-rate-smooth vertical scrolling for an AutoScroll panel.</summary>
internal sealed class SmoothScroller
{
    private readonly ScrollableControl target;
    private double current, goal;
    private bool running;
    public event Action? Moved;

    public SmoothScroller(ScrollableControl target) => this.target = target;

    private int Max => Math.Max(0, target.DisplayRectangle.Height - target.ClientSize.Height);

    public void Wheel(int delta)
    {
        var step = Math.Max(1, SystemInformation.MouseWheelScrollLines) * 40.0;
        if (!running) { current = -target.AutoScrollPosition.Y; goal = current; }
        ScrollTo(goal - delta / 120.0 * step);
    }

    public void ScrollTo(double y)
    {
        if (!running) current = -target.AutoScrollPosition.Y;
        goal = Math.Clamp(y, 0, Max);
        if (!Theme.Animations) { Apply(goal); return; }
        if (!running) { running = true; FrameClock.Subscribe(target, Frame); }
    }

    private void Frame(double dt)
    {
        if (target.IsDisposed) { Stop(); return; }
        current += (goal - current) * (1 - Math.Exp(-dt * 16));
        if (Math.Abs(goal - current) < 0.6) { current = goal; Stop(); }
        Apply(current);
    }

    private void Apply(double y)
    {
        var px = (int)Math.Round(y);
        if (-target.AutoScrollPosition.Y == px) return;
        target.AutoScrollPosition = new Point(0, px);
        // Paint the newly exposed strip NOW. Windows scrolls by copying pixels; if the next step's copy happens
        // before the exposed strip was painted, stale pixels get copied along (duplicated/trailing text).
        if (target.IsHandleCreated) target.Update();
        Moved?.Invoke();
    }

    private void Stop() { running = false; FrameClock.Unsubscribe(Frame); }
}
