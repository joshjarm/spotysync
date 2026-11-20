using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using SpotySync;
using SpotySyncApp;

/// <summary>
/// Opt-in (SPOTYSYNC_FLICKER_PROBE=1): shows the window on screen, records the real screen from a background
/// thread every few milliseconds while switching pages, themes and windows, and reports any frame where the
/// main content area or the bottom bar turned bright (the "white flash"). Does not touch the mouse or keyboard.
/// </summary>
internal static class FlickerProbe
{
    private sealed record Frame(long Ms, string Step, double Content, double Bar, Bitmap? Shot);

    public static int Run(MainForm form, PortableSettings settings, string output)
    {
        var dir = Path.Combine(output, "flicker");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var screen = Screen.PrimaryScreen!.WorkingArea;
        form.FormBorderStyle = FormBorderStyle.Sizable; // a real window frame, like the app has
        settings.UiTheme = Environment.GetEnvironmentVariable("SPOTYSYNC_FLICKER_THEME") ?? "spotysync"; settings.UiAnimations = Environment.GetEnvironmentVariable("SPOTYSYNC_FLICKER_NOANIM") != "1"; Invoke(form, "ReapplyTheme");
        // A plain white window behind the app: any moment the app is not drawn shows up as pure white.
        using var backdrop = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Bounds = Screen.PrimaryScreen!.Bounds, BackColor = Color.White
        };
        backdrop.Show();
        Pump(0.2);
        form.Location = new Point(screen.Left + 40, screen.Top + 40);
        form.Size = new Size(Math.Min(1340, screen.Width - 80), Math.Min(870, screen.Height - 80));
        form.TopMost = Environment.GetEnvironmentVariable("SPOTYSYNC_FLICKER_TOPMOST") != "0";
        if (Environment.GetEnvironmentVariable("SPOTYSYNC_FLICKER_MAX") == "1") form.WindowState = FormWindowState.Maximized;
        form.Activate();
        Pump(0.8);

        if (Environment.GetEnvironmentVariable("SPOTYSYNC_TIMING") == "1")
        {
            var ids = new[] { "duo_crimson_cobalt", "qd_ion_cyan", "duo_aurora", "spotysync" };
            foreach (var anim in new[] { false, true })
            {
                settings.UiAnimations = anim; Invoke(form, "ReapplyTheme"); Pump(0.5);
                var times = new List<double>();
                for (var i = 0; i < 8; i++)
                {
                    settings.UiTheme = ids[i % ids.Length];
                    var sw = Stopwatch.StartNew();
                    Invoke(form, "ReapplyTheme");
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    var frameWatch = Stopwatch.StartNew(); var worst = 0.0; var last = 0.0;
                    while (frameWatch.Elapsed.TotalSeconds < 0.45) { Application.DoEvents(); var now = frameWatch.Elapsed.TotalMilliseconds; worst = Math.Max(worst, now - last); last = now; Thread.Sleep(1); }
                    times.Add(-worst);
                }
                var calls = times.Where(t => t >= 0).ToList(); var stalls = times.Where(t => t < 0).Select(t => -t).ToList();
                Console.WriteLine($"timing animations={anim}: click blocks {calls.Average():0} ms (max {calls.Max():0}), worst UI stall during fade {stalls.Average():0} ms (max {stalls.Max():0})");
            }
            var pageTimes = new List<double>();
            foreach (var page in new[] { 1, 2, 3, 4, 0, 1, 4, 0 }) { var sw = Stopwatch.StartNew(); Invoke(form, "SelectPage", page); pageTimes.Add(sw.Elapsed.TotalMilliseconds); Pump(0.4); }
            Console.WriteLine($"timing page switch blocks {pageTimes.Average():0} ms (max {pageTimes.Max():0})");
            return 0;
        }

        var content = (Control)Get(form, "content");
        var bar = form.Controls.Cast<Control>().First(c => c.GetType().Name == "StatusBarPanel");
        var contentRect = content.RectangleToScreen(content.ClientRectangle);
        var barRect = bar.RectangleToScreen(bar.ClientRectangle);
        // include anything between the bar and the bottom of the work area (maximized windows overhang it)
        barRect = Rectangle.FromLTRB(barRect.Left, barRect.Top, barRect.Right, Math.Max(barRect.Bottom, screen.Bottom));
        Console.WriteLine($"probe: window {form.WindowState}, form {form.Bounds}, client {form.RectangleToScreen(form.ClientRectangle)}, bar {bar.RectangleToScreen(bar.ClientRectangle)}, work area {screen}");
        // Thin strips are cheap to grab, so the recorder runs faster than the display refreshes: a band
        // across the middle of the content and (separately) the whole bottom bar.
        var band = new Rectangle(contentRect.Left, contentRect.Top + contentRect.Height * 3 / 5, contentRect.Width, 20);

        var frames = new ConcurrentQueue<Frame>();
        var step = "idle";
        var stop = false;
        var watch = Stopwatch.StartNew();
        var recorder = new Thread(() =>
        {
            using var bandShot = new Bitmap(band.Width, band.Height);
            using var barShot = new Bitmap(barRect.Width, barRect.Height);
            using var gBand = Graphics.FromImage(bandShot);
            using var gBar = Graphics.FromImage(barShot);
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    ScreenGrab.Copy(gBand, band);
                    ScreenGrab.Copy(gBar, barRect);
                    var c = Bright(bandShot, new Rectangle(Point.Empty, band.Size));
                    var b = Bright(barShot, new Rectangle(Point.Empty, barRect.Size));
                    var keep = c > 0.04 || b > 0.04;
                    Bitmap? kept = null;
                    if (keep)
                    {
                        kept = new Bitmap(barRect.Width, band.Height + barRect.Height + 4);
                        using var k = Graphics.FromImage(kept);
                        k.Clear(Color.Magenta);
                        k.DrawImageUnscaled(bandShot, 0, 0);
                        k.DrawImageUnscaled(barShot, 0, band.Height + 4);
                    }
                    frames.Enqueue(new Frame(watch.ElapsedMilliseconds, Volatile.Read(ref step), c, b, kept));
                }
                catch { }
            }
        }) { IsBackground = true };
        recorder.Start();

        void Do(string name, Action action, double settle = 0.45)
        {
            Volatile.Write(ref step, name);
            action();
            Pump(settle);
        }

        foreach (var page in new[] { 1, 2, 3, 4, 0, 1, 0, 4, 2, 0, 4, 3, 1, 4, 0 }) Do($"page {page}", () => Invoke(form, "SelectPage", page));
        foreach (var id in new[] { "duo_crimson_cobalt", "qd_ion_cyan", settings.UiTheme })
        {
            settings.UiTheme = id;
            Do($"theme {id}", () => Invoke(form, "ReapplyTheme"), 0.7);
        }
        var spamThemes = new[] { "duo_crimson_cobalt", "qd_ion_cyan", "duo_aurora", "spotysync", "duo_synth_sunset" };
        Do("spam themes", () =>
        {
            for (var i = 0; i < 25; i++) { settings.UiTheme = spamThemes[i % spamThemes.Length]; Invoke(form, "ReapplyTheme"); Pump(0.04); }
        }, 0.6);
        Do("sidebar collapse", () => Invoke(Get(form, "sidebar"), "set_Mini", true));
        Do("sidebar expand", () => Invoke(Get(form, "sidebar"), "set_Mini", false));
        using (var other = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(screen.Left + 300, screen.Top + 300, 260, 160), TopMost = true, Text = "probe", FormBorderStyle = FormBorderStyle.None, BackColor = Color.Black, ShowInTaskbar = false })
        {
            for (var i = 0; i < 3; i++)
            {
                Do("switch away", () => { form.TopMost = false; other.Show(); other.Activate(); }, 0.35);
                Do("switch back", () => { other.Hide(); form.Activate(); }, 0.45);
            }
            Do("minimise", () => form.WindowState = FormWindowState.Minimized, 0.4);
            Do("restore", () => { form.WindowState = FormWindowState.Normal; form.Activate(); }, 0.6);
        }

        Volatile.Write(ref stop, true);
        recorder.Join();
        form.TopMost = false;

        var list = frames.ToList();
        // minimise/restore play the system window animation over whatever is behind, so they are only reported
        var bad = list.Where(f => f.Shot is not null).ToList();
        var counted = bad.Count(f => f.Step is not ("minimise" or "restore"));
        Console.WriteLine($"flicker probe: {list.Count} frames recorded ({list.Count * 1000.0 / Math.Max(1, watch.ElapsedMilliseconds):0} fps)");
        var n = 0;
        foreach (var group in bad.GroupBy(f => f.Step))
        {
            Console.WriteLine($"  BRIGHT during '{group.Key}': {group.Count()} frame(s), worst content {group.Max(f => f.Content):P0}, bar {group.Max(f => f.Bar):P0}");
            foreach (var f in group.Take(3)) f.Shot!.Save(Path.Combine(dir, $"{n++:00}-{group.Key.Replace(' ', '_')}-{f.Ms}ms.png"));
        }
        foreach (var f in list) f.Shot?.Dispose();
        if (bad.Count == 0) Console.WriteLine("  no bright frames in the content area or bottom bar");
        return counted;
    }

    private static Rectangle Offset(Rectangle r, Rectangle origin) => new(r.X - origin.X, r.Y - origin.Y, r.Width, r.Height);

    /// <summary>Fraction of sampled pixels that are near-white (all themes tested are dark).</summary>
    private static unsafe double Bright(Bitmap shot, Rectangle area)
    {
        area.Intersect(new Rectangle(Point.Empty, shot.Size));
        if (area.Width <= 0 || area.Height <= 0) return 0;
        var data = shot.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int total = 0, bright = 0;
            for (var y = 0; y < area.Height; y += 5)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < area.Width; x += 5)
                {
                    var px = row + x * 4;
                    total++;
                    if (px[0] > 175 && px[1] > 175 && px[2] > 175) bright++;
                }
            }
            return total == 0 ? 0 : bright / (double)total;
        }
        finally { shot.UnlockBits(data); }
    }

    private static void Pump(double seconds)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(2); }
    }

    private static object Get(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(m => m.Name == name && m.GetParameters().Length == arguments.Length).Invoke(target, arguments);
}
