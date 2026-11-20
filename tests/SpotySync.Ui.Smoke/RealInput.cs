using System.Reflection;
using System.Runtime.InteropServices;
using SpotySync;
using SpotySyncApp;

/// <summary>
/// Optional real-mouse test (SPOTYSYNC_REAL_INPUT=1): drives the actual cursor with SendInput over an
/// on-screen, top-most test window to resize and move a dashboard module exactly like a user would.
/// It refuses to click unless the point under the cursor belongs to the test window.
/// </summary>
internal static class RealInput
{
    public static void Run(MainForm form, PortableSettings settings)
    {
        var originalCursor = Cursor.Position;
        var originalBounds = form.Bounds;
        try
        {
            form.TopMost = true;
            form.Bounds = new Rectangle(40, 40, 1340, 870);
            form.Activate();
            Pump(400);
            typeof(MainForm).GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [0]);
            settings.UiDashboardCards = "";
            typeof(MainForm).GetMethod("ApplyDashboardLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Pump(400);

            var card = Find(form, "card-actions");
            var before = card.Size;
            var grip = card.PointToScreen(new Point(card.Width - 8, card.Height - 8));
            Drag(form, grip, new Point(grip.X + 30, grip.Y + 160));
            Pump(900);
            Console.WriteLine($"real resize: {before.Width}x{before.Height} -> {card.Width}x{card.Height}; layout '{settings.UiDashboardCards}'");
            if (card.Height <= before.Height + 60) throw new InvalidOperationException("REAL-INPUT: dragging the resize grip did not resize the module.");

            var library = Find(form, "card-activity");
            var title = library.PointToScreen(new Point(80, 24));
            var target = card.PointToScreen(new Point(20, 60));
            Drag(form, title, target);
            Pump(600);
            Console.WriteLine($"real drag: layout '{settings.UiDashboardCards}'");
            if (!settings.UiDashboardCards.StartsWith("activity:")) throw new InvalidOperationException("REAL-INPUT: dragging a module by its title did not move it.");
            Console.WriteLine("real mouse input ok");
        }
        finally
        {
            Cursor.Position = originalCursor;
            form.TopMost = false;
            form.Bounds = originalBounds;
            settings.UiDashboardCards = "";
        }
    }

    private static Control Find(Control root, string name) =>
        root.Controls.Find(name, true).FirstOrDefault() ?? throw new InvalidOperationException("missing " + name);

    private static void Drag(Form form, Point from, Point to)
    {
        Cursor.Position = from;
        Pump(150);
        var hwnd = WindowFromPoint(from);
        if (Control.FromChildHandle(hwnd)?.FindForm() != form)
            throw new InvalidOperationException("REAL-INPUT: the point under the cursor is not the test window - not clicking.");
        Send(MOUSEEVENTF_LEFTDOWN);
        Pump(80);
        for (var i = 1; i <= 20; i++)
        {
            Cursor.Position = new Point(from.X + (to.X - from.X) * i / 20, from.Y + (to.Y - from.Y) * i / 20);
            Pump(25);
        }
        Pump(120);
        Send(MOUSEEVENTF_LEFTUP);
        Pump(250);
    }

    private static void Pump(int ms)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    private static void Send(uint flags)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
}
