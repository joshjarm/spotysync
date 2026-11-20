using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// `SpotySync.Ui.Smoke.exe --watch <seconds> <outDir>`: records the running SpotySync window (read-only, never
/// touches input). Grabs two thin strips (bottom bar + a band across the main panel) as fast as possible and
/// flags frames whose brightness jumps above the recent normal level - a flash - saving the strips around it.
/// </summary>
internal static class Watcher
{
    public static int Run(int seconds, string output)
    {
        Directory.CreateDirectory(output);
        foreach (var f in Directory.GetFiles(output)) File.Delete(f);
        var watch = Stopwatch.StartNew();
        var log = new List<string>();
        var history = new Queue<(double bar, double main)>();
        int frames = 0, flashes = 0;
        Bitmap? prevBar = null, prevMain = null;
        var lastFlash = -1000L;
        while (watch.Elapsed.TotalSeconds < seconds && flashes < 60)
        {
            var hwnd = Process.GetProcessesByName("SpotySync").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
            if (hwnd == IntPtr.Zero || IsIconic(hwnd)) { Thread.Sleep(200); continue; }
            if (DwmGetWindowAttribute(hwnd, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(hwnd, out r);
            var rect = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), Screen.FromHandle(hwnd).Bounds);
            if (rect.Width < 400 || rect.Height < 300) { Thread.Sleep(100); continue; }
            var barRect = new Rectangle(rect.X, rect.Bottom - 80, rect.Width, 80);
            var mainRect = new Rectangle(rect.X, rect.Y + rect.Height / 2, rect.Width, 24);
            var bar = new Bitmap(barRect.Width, barRect.Height);
            var main = new Bitmap(mainRect.Width, mainRect.Height);
            using (var g = Graphics.FromImage(bar)) ScreenGrab.Copy(g, barRect);
            using (var g = Graphics.FromImage(main)) ScreenGrab.Copy(g, mainRect);
            frames++;
            var ms = watch.ElapsedMilliseconds;
            var b = Bright(bar); var m = Bright(main);
            var baseBar = history.Count > 0 ? history.Average(h => h.bar) : b;
            var baseMain = history.Count > 0 ? history.Average(h => h.main) : m;
            var flash = history.Count >= 10 && (b - baseBar > 0.12 || m - baseMain > 0.12);
            if (flash)
            {
                if (ms - lastFlash > 250) { flashes++; log.Add($"FLASH {flashes} at {ms} ms (window {rect})"); if (prevBar is not null) { prevBar.Save(Path.Combine(output, $"f{flashes:00}-{ms:000000}-0before-bar.png")); prevMain!.Save(Path.Combine(output, $"f{flashes:00}-{ms:000000}-0before-main.png")); } }
                lastFlash = ms;
                log.Add($"  {ms} ms bar {b:P0} (normal {baseBar:P0}) main {m:P0} (normal {baseMain:P0})");
                bar.Save(Path.Combine(output, $"f{flashes:00}-{ms:000000}-1bar.png"));
                main.Save(Path.Combine(output, $"f{flashes:00}-{ms:000000}-1main.png"));
            }
            else
            {
                history.Enqueue((b, m));
                if (history.Count > 40) history.Dequeue();
            }
            prevBar?.Dispose(); prevMain?.Dispose();
            prevBar = bar; prevMain = main;
        }
        prevBar?.Dispose(); prevMain?.Dispose();
        log.Insert(0, $"watched {watch.Elapsed.TotalSeconds:0}s, {frames} frames ({frames / Math.Max(1, watch.Elapsed.TotalSeconds):0} fps), {flashes} flash(es)");
        File.WriteAllLines(Path.Combine(output, "watch-log.txt"), log);
        foreach (var line in log.Take(80)) Console.WriteLine(line);
        return 0;
    }

    private static unsafe double Bright(Bitmap shot)
    {
        var data = shot.LockBits(new Rectangle(Point.Empty, shot.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int total = 0, bright = 0;
            for (var y = 0; y < shot.Height; y += 3)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < shot.Width; x += 4)
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

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
