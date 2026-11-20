using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;

namespace SpotySyncApp;

/// <summary>
/// Watches the music/soundtrack folders while an operation runs and samples write speed once a second.
/// Purely observational: it never touches files.
/// </summary>
internal sealed class DownloadMonitor : IDisposable
{
    public readonly record struct Sample(double Seconds, double BytesPerSecond, int Files);

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wav", ".wma", ".aac", ".aiff", ".wv" };
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".lrc", ".txt", ".json", ".jpg", ".jpeg", ".png", ".webp", ".m3u", ".m3u8", ".ini", ".log" };
    private static readonly Regex TargetPattern = new(@"(\d+) to download", RegexOptions.Compiled);
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly Dictionary<string, long> sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> finished = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private long lastTotal;

    public List<Sample> Samples { get; } = new();
    public DateTime Started { get; private set; }
    public DateTime? Ended { get; private set; }
    public bool Running { get; private set; }
    public int? Target { get; private set; }
    public long Bytes { get; private set; }
    public int Files => finished.Count;
    public double Speed => Samples.Count == 0 ? 0 : Samples[^1].BytesPerSecond;
    public string Label { get; private set; } = "";
    public event Action? Updated;
    /// <summary>A new song file appeared (name without extension), once per song.</summary>
    public event Action<string>? FileArrived;

    public DownloadMonitor() => timer.Tick += (_, _) => Tick();

    /// <summary>"Artist - Title" for a finished audio file, ignoring yt-dlp temp/fragment names.</summary>
    private static string? SongName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return null;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\.f\d+$")) return null; // yt-dlp format fragment
        return name.Trim();
    }

    public void Start(string label, IEnumerable<string> folders, ISynchronizeInvoke sync)
    {
        StopWatching();
        Samples.Clear(); sizes.Clear(); finished.Clear();
        Bytes = 0; lastTotal = 0; Target = null; Ended = null;
        Label = label; Started = DateTime.Now; Running = true;
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, SynchronizingObject = sync,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                watcher.Created += (_, e) => Track(e.FullPath);
                watcher.Changed += (_, e) => Track(e.FullPath);
                watcher.Renamed += (_, e) => Moved(e.OldFullPath, e.FullPath);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch { /* a folder we cannot watch just isn't graphed */ }
        }
        timer.Start();
        Updated?.Invoke();
    }

    public void Stop()
    {
        if (!Running) return;
        Tick();
        StopWatching();
        Running = false; Ended = DateTime.Now;
        Updated?.Invoke();
    }

    private void StopWatching()
    {
        timer.Stop();
        foreach (var w in watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        watchers.Clear();
    }

    /// <summary>Picks up totals the app logs, e.g. “… 37 to download.”</summary>
    public void NoteLog(string text)
    {
        if (!Running) return;
        var m = TargetPattern.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0) Target = n;
    }

    private void Track(string path)
    {
        var ext = Path.GetExtension(path);
        if (Ignored.Contains(ext) || Directory.Exists(path)) return;
        if (!sizes.ContainsKey(path))
        {
            // The same file name reappearing elsewhere is a move (e.g. MusicBee organising it), not new data.
            var name = Path.GetFileName(path);
            var moved = sizes.Keys.FirstOrDefault(k => Path.GetFileName(k).Equals(name, StringComparison.OrdinalIgnoreCase) && !File.Exists(k));
            if (moved is not null) { sizes[path] = sizes[moved]; sizes.Remove(moved); }
            else sizes[path] = 0;
        }
        if (Audio.Contains(ext) && SongName(path) is { Length: > 0 } song && finished.Add(song)) FileArrived?.Invoke(song);
    }

    private void Moved(string from, string to)
    {
        if (sizes.Remove(from, out var size)) sizes[to] = size;
        Track(to);
    }

    private void Tick()
    {
        long total = 0;
        foreach (var path in sizes.Keys.ToList())
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) sizes[path] = info.Length;
            }
            catch { }
            total += sizes[path];
        }
        var delta = Math.Max(0, total - lastTotal);
        lastTotal = total;
        Bytes += delta;
        Samples.Add(new Sample((DateTime.Now - Started).TotalSeconds, delta, Files));
        if (Samples.Count > 600) Samples.RemoveAt(0);
        Updated?.Invoke();
    }

    public void Dispose() { StopWatching(); timer.Dispose(); }
}

/// <summary>Live graph of the current/last operation: speed area, files line and headline numbers.</summary>
internal sealed class ActivityGraph : ThemedControl
{
    private DownloadMonitor? monitor;
    private string result = "";
    private Led resultState = Led.Idle;

    public DownloadMonitor? Monitor
    {
        get => monitor;
        set { if (monitor is not null) monitor.Updated -= Invalidate; monitor = value; if (monitor is not null) monitor.Updated += Invalidate; Invalidate(); }
    }

    public void SetResult(string text, Led state) { result = text; resultState = state; Invalidate(); }

    private static string Bytes(double b) => b >= 1 << 30 ? $"{b / (1 << 30):0.00} GB" : b >= 1 << 20 ? $"{b / (1 << 20):0.0} MB" : b >= 1024 ? $"{b / 1024:0} KB" : $"{b:0} B";

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var m = monitor;
        var elapsed = m is null || m.Samples.Count == 0 && !m.Running ? TimeSpan.Zero : (m.Ended ?? DateTime.Now) - m.Started;
        var stats = new (string Caption, string Value, Color Color)[]
        {
            ("SPEED", m is { Running: true } ? Bytes(m.Speed) + "/s" : "—", p.Accent),
            ("WRITTEN", m is null || m.Bytes == 0 ? "—" : Bytes(m.Bytes), p.Ink),
            ("FILES", m is null ? "—" : m.Target is { } t ? $"{m.Files} / {t}" : m.Files.ToString(), p.Accent2),
            ("ELAPSED", elapsed == TimeSpan.Zero ? "—" : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}", p.Ink),
        };
        var colW = Width / stats.Length;
        for (var i = 0; i < stats.Length; i++)
        {
            Ui.Text(g, stats[i].Caption, Fonts.Body(7.5f, FontStyle.Bold), new Rectangle(i * colW, 0, colW - 8, 16), p.Dim);
            Ui.Text(g, stats[i].Value, Fonts.Display(16f), new Rectangle(i * colW, 15, colW - 8, 30), stats[i].Color);
        }
        var top = 52;
        if (m?.Target is { } target && target > 0)
        {
            var frac = Math.Clamp(m.Files / (double)target, 0, 1);
            Ui.FillRound(g, p.Well, new RectangleF(0, top, Width - 1, 6), 3);
            if (frac > 0) Ui.FillGradient(g, p.Accent, p.Accent2, new RectangleF(0, top, Math.Max(6, (Width - 1) * (float)frac), 6), 3);
            top += 14;
        }

        var footer = result.Length > 0 ? 24 : 0;
        var plot = new Rectangle(0, top, Width - 1, Math.Max(20, Height - top - footer - 4));
        // soft plot area that blends with the card (easier on the eyes than a black box)
        Ui.FillRound(g, ColorX.Mix(p.Panel, p.Panel2, 0.5), plot, Theme.R(8), ColorX.Mix(p.Line, p.Panel, 0.4));
        var gridLines = Math.Clamp(plot.Height / 70, 2, 6);
        using (var gridPen = new Pen(ColorX.Mix(p.Line, p.Panel, 0.55)) { DashStyle = DashStyle.Dot })
            for (var i = 1; i < gridLines; i++) g.DrawLine(gridPen, plot.Left + 8, plot.Top + plot.Height * i / gridLines, plot.Right - 8, plot.Top + plot.Height * i / gridLines);

        var samples = m?.Samples ?? new List<DownloadMonitor.Sample>();
        if (samples.Count < 2)
        {
            var msg = m is { Running: true } ? "Waiting for files to appear…" : "Live download speed and progress appear here while SpotySync downloads.";
            Ui.Text(g, msg, Fonts.Body(9f), plot, p.Dim, TextFormatFlags.HorizontalCenter);
        }
        else
        {
            // zoomed out: about 4 px per second of history, so a bigger module shows a longer, calmer timeline
            var window = samples.TakeLast(Math.Clamp(plot.Width / 4, 60, 600)).ToList();
            var maxSpeed = Math.Max(256 * 1024, window.Max(s => s.BytesPerSecond));
            var maxFiles = Math.Max(1, window.Max(s => s.Files));
            var inner = Rectangle.Inflate(plot, -6, -8);
            PointF Pt(int i, double v, double max) => new(inner.Left + inner.Width * i / (float)Math.Max(1, window.Count - 1), inner.Bottom - (float)(inner.Height * Math.Clamp(v / max, 0, 1)));
            var line = window.Select((s, i) => Pt(i, s.BytesPerSecond, maxSpeed)).ToArray();
            using (var area = new GraphicsPath())
            {
                area.AddLine(inner.Left, inner.Bottom, line[0].X, line[0].Y);
                area.AddLines(line);
                area.AddLine(line[^1].X, line[^1].Y, line[^1].X, inner.Bottom);
                area.CloseFigure();
                using var fill = new LinearGradientBrush(inner, ColorX.Alpha(p.Accent, 60), ColorX.Alpha(p.Accent2, 8), LinearGradientMode.Vertical);
                g.FillPath(fill, area);
            }
            using (var pen = new Pen(ColorX.Mix(p.Accent, p.Panel, 0.15), 1.5f)) g.DrawLines(pen, line);
            var files = window.Select((s, i) => Pt(i, s.Files, maxFiles)).ToArray();
            using (var pen = new Pen(ColorX.Mix(p.Accent2, p.Panel, 0.3), 1.2f) { DashStyle = DashStyle.Dash }) g.DrawLines(pen, files);
            Ui.Text(g, Bytes(maxSpeed) + "/s", Fonts.Mono(7.5f), new Rectangle(plot.Left + 8, plot.Top + 4, 120, 14), p.Dim);
            Ui.Text(g, $"{maxFiles} files", Fonts.Mono(7.5f), new Rectangle(plot.Right - 128, plot.Top + 4, 120, 14), p.Accent2, TextFormatFlags.Right);
        }
        if (footer > 0)
        {
            var led = StatusPill.LedColor(resultState);
            using (var dot = new SolidBrush(led)) g.FillEllipse(dot, 2, Height - footer / 2 - 4, 8, 8);
            Ui.Text(g, result, Fonts.Body(9f), new Rectangle(16, Height - footer, Width - 16, footer), p.Muted);
        }
    }
}

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
