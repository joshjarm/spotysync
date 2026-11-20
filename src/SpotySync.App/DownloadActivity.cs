using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// Live model of the current (or last) download run: real transfer speed measured from the downloader's growing
/// temporary files (spotDL's %USERPROFILE%\.spotdl\temp, yt-dlp ".part" files), songs finished / failed / skipped
/// against the queue, the song being downloaded and size estimates. Purely observational: it never touches files.
/// Any number of <see cref="ActivityGraph"/> views can show the same monitor.
/// </summary>
internal sealed class DownloadMonitor : IDisposable
{
    /// <summary>One point per second of active (unpaused) time.</summary>
    public readonly record struct Sample(double Seconds, double Instant, double Smooth, int Done, int Failed);
    public readonly record struct SongEvent(double Seconds, string Name, bool Failed);

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".lrc", ".txt", ".json", ".jpg", ".jpeg", ".png", ".webp", ".m3u", ".m3u8", ".ini", ".log", ".spotdl" };
    private static readonly Regex[] TargetPatterns =
    [
        new(@"Download queue: (\d+)", RegexOptions.Compiled),
        new(@"(\d+) to download", RegexOptions.Compiled),
        new(@"Quality upgrade: (\d+) song", RegexOptions.Compiled),
        new(@"downloading (\d+) link\(s\) for", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];
    private static readonly Regex Batch = new(@"^Batch (\d+) of (\d+)", RegexOptions.Compiled);
    private static readonly Regex SpotDlDownloaded = new("^Downloaded \"(.+?)\":", RegexOptions.Compiled);
    private static readonly Regex Failure = new(@"(LookupError: No results found|AudioProviderError|ERROR: \[youtube\])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Skip = new(@"^Skipping .+\((file already exists|duplicate)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly List<string> tempFolders = new();
    private readonly Dictionary<string, long> active = new(StringComparer.OrdinalIgnoreCase);   // growing download files → last size
    private readonly HashSet<string> partials = new(StringComparer.OrdinalIgnoreCase);           // .part files seen in watched folders
    private readonly Dictionary<string, long> finals = new(StringComparer.OrdinalIgnoreCase);    // finished audio → size
    private readonly HashSet<string> finishedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(double T, long Bytes)> window = new();
    private readonly List<string> queue = new();                                                 // Spotify IDs in the order the downloader read them
    private double lastTick, secondBytes, nextSampleAt;
    private DateTime? pausedSince;
    private TimeSpan pausedTotal;
    private string lastGrowing = "";
    private bool tempActivity;

    public List<Sample> Samples { get; } = new();
    public List<SongEvent> Events { get; } = new();
    public DateTime Started { get; private set; }
    public DateTime? Ended { get; private set; }
    public bool Running { get; private set; }
    public bool Paused => pausedSince is not null;
    public string Label { get; private set; } = "";
    public int? Target { get; private set; }
    public int Looked { get; private set; }
    public int Done => finishedNames.Count;
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int Processed => Done + Failed + Skipped;
    public string BatchText { get; private set; } = "";
    public string Phase { get; private set; } = "";
    public long Downloaded { get; private set; }
    public long Written => finals.Values.Sum();
    /// <summary>Speed now (bytes/s, averaged over the last 10 s so gaps between songs don't read as 0).</summary>
    public double Speed { get; private set; }
    public double Peak { get; private set; }
    public string LastFinished { get; private set; } = "";
    public string Result { get; private set; } = "";
    public Led ResultState { get; private set; } = Led.Idle;
    /// <summary>Spotify track lookup for names and durations (set by the main window).</summary>
    public Func<string, TrackInfo?>? Lookup { get; set; }
    public event Action? Updated;
    /// <summary>A new song file appeared (name without extension), once per song.</summary>
    public event Action<string>? FileArrived;

    public DownloadMonitor() => timer.Tick += (_, _) => Tick();

    /// <summary>Active (unpaused) time of the run.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            if (Started == default) return TimeSpan.Zero;
            var end = Ended ?? DateTime.Now;
            var paused = pausedTotal + (pausedSince is { } p ? end - p : TimeSpan.Zero);
            return end - Started - paused < TimeSpan.Zero ? TimeSpan.Zero : end - Started - paused;
        }
    }

    public double AverageSpeed => Elapsed.TotalSeconds < 1 ? 0 : Downloaded / Elapsed.TotalSeconds;

    public void SetResult(string text, Led state) { Result = text; ResultState = state; Updated?.Invoke(); }

    public void Start(string label, string musicFolder, IEnumerable<string> extraFolders, ISynchronizeInvoke sync)
    {
        StopWatching();
        Samples.Clear(); Events.Clear(); active.Clear(); partials.Clear(); finals.Clear(); finishedNames.Clear(); window.Clear(); queue.Clear(); tempFolders.Clear();
        Downloaded = 0; Speed = Peak = 0; Target = null; Looked = Failed = Skipped = 0; BatchText = ""; LastFinished = ""; Phase = "Starting…";
        Ended = null; pausedSince = null; pausedTotal = TimeSpan.Zero; secondBytes = 0; nextSampleAt = 1; lastTick = 0; lastGrowing = ""; tempActivity = false;
        Label = label; Started = DateTime.Now; Running = true;
        // spotDL downloads into ~/.spotdl/temp (yt-dlp ".part" files that grow as data arrives), then converts into the music folder
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        tempFolders.Add(Path.Combine(home, ".spotdl", "temp"));
        foreach (var folder in new[] { musicFolder }.Concat(extraFolders).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, SynchronizingObject = sync,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                watcher.Created += (_, e) => Track(e.FullPath);
                // a change to a file that existed before the run (MusicBee writing tags…) is not a new song
                watcher.Changed += (_, e) => { if (LibraryScanner.IsPartialDownload(e.FullPath)) partials.Add(e.FullPath); };
                watcher.Renamed += (_, e) => Moved(e.OldFullPath, e.FullPath);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch { /* a folder we cannot watch just isn't counted */ }
        }
        timer.Start();
        Updated?.Invoke();
    }

    public void Stop()
    {
        if (!Running) return;
        if (pausedSince is not null) SetPaused(false);
        Tick();
        StopWatching();
        Running = false; Ended = DateTime.Now; Phase = "Finished";
        Speed = 0;
        Updated?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (!Running || paused == Paused) return;
        if (paused) pausedSince = DateTime.Now;
        else if (pausedSince is { } since) { pausedTotal += DateTime.Now - since; pausedSince = null; lastTick = Elapsed.TotalSeconds; }
        Updated?.Invoke();
    }

    private void StopWatching()
    {
        timer.Stop();
        foreach (var w in watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        watchers.Clear();
    }

    // ---------------------------------------------------------------- log lines
    /// <summary>Reads queue sizes, lookups, finished/failed/skipped songs and phases from the run's log.</summary>
    public void NoteLog(string text)
    {
        if (!Running) return;
        var t = text.Trim();
        foreach (var pattern in TargetPatterns)
        {
            var m = pattern.Match(t);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0) { Target = Math.Max(n, Target ?? 0); break; }
        }
        var batch = Batch.Match(t);
        if (batch.Success) BatchText = $"batch {batch.Groups[1].Value} of {batch.Groups[2].Value}";
        if (t.Contains("Processing query:", StringComparison.OrdinalIgnoreCase))
        {
            Looked++;
            var id = SpotifyIdExtractor.FromText(t);
            if (id.Length > 0) queue.Add(id);
            if (Processed == 0) Phase = "Looking up songs";
        }
        else if (SpotDlDownloaded.Match(t) is { Success: true } done) LastFinished = done.Groups[1].Value;
        else if (Failure.IsMatch(t) && !t.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            Failed++;
            Events.Add(new SongEvent(Elapsed.TotalSeconds, CurrentName, true));
        }
        else if (Skip.IsMatch(t)) Skipped++;
        if (t.StartsWith("MusicBee final stage", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Importing", StringComparison.OrdinalIgnoreCase)) Phase = "Adding to MusicBee";
        else if (t.Contains("Quality check", StringComparison.OrdinalIgnoreCase)) Phase = "Checking audio quality";
    }

    // ---------------------------------------------------------------- files
    private void Track(string path)
    {
        var ext = Path.GetExtension(path);
        if (Ignored.Contains(ext)) return;
        if (LibraryScanner.IsPartialDownload(path)) { partials.Add(path); return; }
        if (!LibraryScanner.IsAudioFile(path)) return;
        var name = Path.GetFileNameWithoutExtension(path).Trim();
        if (name.Length == 0) return;
        if (!finals.ContainsKey(path))
        {
            // the same name elsewhere is a move (e.g. MusicBee organising it), not a new song
            var moved = finals.Keys.FirstOrDefault(k => Path.GetFileName(k).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) && !File.Exists(k));
            if (moved is not null) { finals[path] = finals[moved]; finals.Remove(moved); }
            else finals[path] = 0;
        }
        if (finishedNames.Add(name))
        {
            LastFinished = name;
            Phase = "Downloading";
            Events.Add(new SongEvent(Elapsed.TotalSeconds, name, false));
            FileArrived?.Invoke(name);
        }
    }

    private void Moved(string from, string to)
    {
        if (finals.Remove(from, out var size)) finals[to] = size;
        partials.Remove(from);
        Track(to);
    }

    private void Tick()
    {
        if (Paused || !Running) { Updated?.Invoke(); return; }
        var now = Elapsed.TotalSeconds;
        var dt = Math.Max(0.05, now - lastTick);
        lastTick = now;
        long bytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Measure(string file)
        {
            if (Ignored.Contains(Path.GetExtension(file))) return;
            long size;
            try { var info = new FileInfo(file); if (!info.Exists) return; size = info.Length; } catch { return; }
            seen.Add(file);
            var grew = active.TryGetValue(file, out var last) ? size - last : size;
            active[file] = size;
            if (grew > 0) { bytes += grew; lastGrowing = file; tempActivity = true; }
        }
        foreach (var folder in tempFolders)
        {
            try { if (Directory.Exists(folder)) foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) Measure(file); }
            catch { }
        }
        foreach (var file in partials.ToList()) { if (File.Exists(file)) Measure(file); else partials.Remove(file); }
        foreach (var gone in active.Keys.Where(k => !seen.Contains(k)).ToList()) active.Remove(gone);
        foreach (var file in finals.Keys.ToList())
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                var grew = info.Length - finals[file];
                finals[file] = info.Length;
                // no temp-file activity (e.g. another downloader writing straight to the folder): count the output instead
                if (!tempActivity && grew > 0) bytes += grew;
            }
            catch { }
        }
        Downloaded += bytes;
        secondBytes += bytes;
        window.Enqueue((now, bytes));
        while (window.Count > 0 && window.Peek().T < now - 10) window.Dequeue();
        var span = Math.Min(10, Math.Max(1, now));
        Speed = window.Sum(w => w.Bytes) / span;
        if (bytes > 0 && Phase is "Starting…" or "Looking up songs") Phase = "Downloading";
        while (now >= nextSampleAt)
        {
            var smooth = Samples.Count < 2 ? Speed : TrailingAverage(15, secondBytes);
            Samples.Add(new Sample(nextSampleAt, secondBytes, smooth, Done, Failed));
            Peak = Math.Max(Peak, smooth);
            secondBytes = 0;
            nextSampleAt += 1;
            if (Samples.Count > 6 * 3600) Samples.RemoveAt(0);
        }
        _ = dt;
        Updated?.Invoke();
    }

    private double TrailingAverage(int seconds, double current)
    {
        var take = Math.Min(seconds - 1, Samples.Count);
        double sum = current;
        for (var i = Samples.Count - take; i < Samples.Count; i++) sum += Samples[i].Instant;
        return sum / (take + 1);
    }

    // ---------------------------------------------------------------- derived
    private TrackInfo? QueueTrack(int index) => index >= 0 && index < queue.Count ? Lookup?.Invoke(queue[index]) : null;

    /// <summary>The song being downloaded: the next one in the downloader's queue (spotDL works through it in order).</summary>
    public string CurrentName
    {
        get
        {
            if (!Running) return "";
            var track = QueueTrack(Processed);
            if (track is not null) return TrackText(track);
            return Processed < queue.Count ? "Spotify track " + queue[Processed] : "";
        }
    }

    public static string TrackText(TrackInfo track) =>
        track.Artists.Count == 0 ? track.Title : $"{track.Title} — {string.Join(", ", track.Artists)}";

    /// <summary>Bytes of the file being downloaded right now.</summary>
    public long CurrentBytes => lastGrowing.Length > 0 && active.TryGetValue(lastGrowing, out var size) ? size : 0;

    private double AverageSongBytes => Done > 0 && Written > 0 ? Written / (double)Done : 0;

    /// <summary>Bytes per millisecond of audio seen so far (≈160 kbps until songs finish).</summary>
    private double BytesPerMs
    {
        get
        {
            double duration = 0;
            for (var i = 0; i < Math.Min(Done, queue.Count); i++) duration += QueueTrack(i)?.DurationMs ?? 0;
            return duration > 60_000 && Written > 0 ? Written / duration : 20.0;
        }
    }

    public long? CurrentExpected
    {
        get
        {
            var track = QueueTrack(Processed);
            if (track is { DurationMs: > 0 }) return (long)(track.DurationMs * BytesPerMs);
            return AverageSongBytes > 0 ? (long)AverageSongBytes : null;
        }
    }

    /// <summary>Estimated size of the whole run (downloaded so far + what is left), or null while unknown.</summary>
    public long? ExpectedTotal
    {
        get
        {
            if (Target is not { } target || target <= 0) return null;
            var remaining = Math.Max(0, target - Processed);
            if (remaining == 0) return Downloaded;
            var ratio = BytesPerMs;
            var fallback = AverageSongBytes > 0 ? AverageSongBytes : 5.5 * 1024 * 1024;
            double left = 0;
            for (var i = Processed; i < Processed + remaining; i++)
            {
                var track = QueueTrack(i);
                left += track is { DurationMs: > 0 } ? track.DurationMs * ratio : fallback;
            }
            return (long)Math.Max(Downloaded, Downloaded + left - CurrentBytes);
        }
    }

    /// <summary>Time left, from the average time per finished song (includes searching and pauses between songs).</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (Target is not { } target || Processed < 2 || target <= Processed) return null;
            var perSong = Elapsed.TotalSeconds / Processed;
            return TimeSpan.FromSeconds(perSong * (target - Processed));
        }
    }

    public void Dispose() { StopWatching(); timer.Dispose(); }
}

/// <summary>
/// The download activity view: headline numbers, queue and current-song progress, and a live speed graph
/// (15-second average so it doesn't collapse to zero between songs) with song markers, axes, time ranges and a
/// hover read-out. Several instances can show one <see cref="DownloadMonitor"/> (dashboard, Console split, full screen).
/// </summary>
internal sealed class ActivityGraph : ThemedControl
{
    private DownloadMonitor? monitor;
    private int range; // seconds shown, 0 = whole run
    private Point? hoverPoint;
    private Rectangle plotRect;
    private readonly List<(Rectangle Bounds, int Seconds)> rangeChips = new();
    private static readonly (string Label, int Seconds)[] Ranges = [("1m", 60), ("5m", 300), ("15m", 900), ("1h", 3600), ("All", 0)];

    /// <summary>Bigger type and spacing (full-screen window).</summary>
    public bool Large { get; set; }
    public event Action? FullScreenRequested;
    public event Action<int>? RangeChanged;

    public ActivityGraph() { Cursor = Cursors.Default; }

    public DownloadMonitor? Monitor
    {
        get => monitor;
        set { if (monitor is not null) monitor.Updated -= OnUpdated; monitor = value; if (monitor is not null) monitor.Updated += OnUpdated; Invalidate(); }
    }

    public int Range { get => range; set { range = value; Invalidate(); } }

    private void OnUpdated() { if (!IsDisposed && Visible) Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && monitor is not null) monitor.Updated -= OnUpdated;
        base.Dispose(disposing);
    }

    internal static string Bytes(double b) =>
        b >= 1L << 30 ? $"{b / (1L << 30):0.00} GB" : b >= 1 << 20 ? $"{b / (1 << 20):0.0} MB" : b >= 1024 ? $"{b / 1024:0} KB" : $"{b:0} B";

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private static string Approx(TimeSpan t) =>
        t.TotalHours >= 1 ? $"≈ {(int)t.TotalHours} h {t.Minutes} min left" : t.TotalMinutes >= 1 ? $"≈ {Math.Ceiling(t.TotalMinutes)} min left" : "under a minute left";

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var inPlot = plotRect.Contains(e.Location);
        Cursor = rangeChips.Any(c => c.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        var next = inPlot ? e.Location : (Point?)null;
        if (next != hoverPoint) { hoverPoint = next; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverPoint = null; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (bounds, seconds) in rangeChips)
            if (bounds.Contains(e.Location)) { range = seconds; RangeChanged?.Invoke(seconds); Invalidate(); return; }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (plotRect.Contains(e.Location)) FullScreenRequested?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var m = monitor;
        var scale = Large ? Math.Clamp(Width / 1150f, 1.25f, 2.2f) : 1f; // full screen grows with the monitor
        var hasRun = m is not null && m.Started != default;
        var y = 0;

        // ---- headline tiles
        var target = m?.Target;
        var expected = m?.ExpectedTotal;
        var tiles = new (string Caption, string Value, string Sub, Color Color)[]
        {
            ("SPEED", m is null || !hasRun ? "—" : m.Paused ? "Paused" : m.Running ? Bytes(m.Speed) + "/s" : Bytes(m.AverageSpeed) + "/s",
                m is null || !hasRun ? "" : m.Running ? $"avg {Bytes(m.AverageSpeed)}/s · peak {Bytes(m.Peak)}/s" : "average of the last run", p.Accent),
            ("DOWNLOADED", m is null || m.Downloaded == 0 ? "—" : Bytes(m.Downloaded),
                expected is { } total && m!.Running ? $"of ≈ {Bytes(total)}" : m is { Written: > 0 } ? $"{Bytes(m.Written)} saved" : "", p.Ink),
            ("SONGS", m is null || !hasRun ? "—" : target is { } t ? $"{Math.Min(m.Processed, t)} / {t}" : m.Done.ToString(),
                m is null ? "" : string.Join(" · ", new[] { m.Done > 0 && target is not null ? $"{m.Done} done" : "", m.Failed > 0 ? $"{m.Failed} failed" : "", m.Skipped > 0 ? $"{m.Skipped} skipped" : "" }.Where(s => s.Length > 0)), p.Accent2),
            ("TIME", m is null || !hasRun ? "—" : Clock(m.Elapsed),
                m is { Running: true, Remaining: { } left } ? Approx(left) : m is { Running: false } && hasRun ? "finished " + m.Ended?.ToString("HH:mm") : "", p.Ink),
        };
        var perRow = Width < 640 ? 2 : 4;
        var tileW = Width / perRow;
        var tileH = (int)(50 * scale);
        for (var i = 0; i < tiles.Length; i++)
        {
            var x = i % perRow * tileW; var ty = y + i / perRow * (tileH + 4);
            Ui.Text(g, tiles[i].Caption, Fonts.Body(7.5f * scale, FontStyle.Bold), new Rectangle(x, ty, tileW - 8, (int)(15 * scale)), p.Dim);
            Ui.Text(g, tiles[i].Value, Fonts.Display(16f * scale), new Rectangle(x, ty + (int)(13 * scale), tileW - 8, (int)(26 * scale)), tiles[i].Color);
            if (tiles[i].Sub.Length > 0)
                Ui.Text(g, tiles[i].Sub, Fonts.Body(7.75f * scale), new Rectangle(x, ty + (int)(37 * scale), tileW - 8, (int)(14 * scale)), p.Muted);
        }
        y += (tiles.Length / perRow) * (tileH + 4) + 4;

        // ---- queue + current song
        var footer = m is { Result.Length: > 0 } ? (int)(22 * scale) : 0;
        var compact = Height - y - footer < 200;
        if (hasRun && m!.Running && Height - y - footer > 150)
        {
            var phase = m.Paused ? "Paused" : m.Phase;
            if (m.BatchText.Length > 0) phase += "  ·  " + m.BatchText;
            if (m.Phase == "Looking up songs" && target is { } tl) phase += $"  {m.Looked} / {tl}";
            var pct = target is { } tp && tp > 0 ? Math.Clamp(m.Processed / (double)tp, 0, 1) : (double?)null;
            Ui.Text(g, phase, Fonts.Semibold(8.75f * scale), new Rectangle(0, y, Width - 70, (int)(18 * scale)), m.Paused ? p.Warn : p.Ink);
            if (pct is { } pv) Ui.Text(g, $"{pv:P0}", Fonts.Semibold(8.75f * scale), new Rectangle(Width - 70, y, 70, (int)(18 * scale)), p.Accent2, TextFormatFlags.Right);
            y += (int)(20 * scale);
            var bar = new RectangleF(0, y, Width - 1, 7 * scale);
            Ui.FillRound(g, p.Well, bar, bar.Height / 2);
            if (target is { } tb && tb > 0)
            {
                var doneW = bar.Width * Math.Clamp((m.Done + m.Skipped) / (float)tb, 0, 1);
                var failW = bar.Width * Math.Clamp(m.Failed / (float)tb, 0, 1);
                var curFrac = m.CurrentExpected is { } ce && ce > 0 ? Math.Clamp(m.CurrentBytes / (float)ce, 0, 1) : 0;
                var curW = bar.Width / tb * curFrac;
                if (doneW > 0) Ui.FillGradient(g, p.Accent, p.Accent2, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, doneW), bar.Height), bar.Height / 2);
                if (failW > 0) Ui.FillRound(g, p.Err, new RectangleF(bar.X + doneW, bar.Y, Math.Max(2, failW), bar.Height), 2);
                if (curW > 1) Ui.FillRound(g, ColorX.Alpha(p.Accent, 110), new RectangleF(bar.X + doneW + failW, bar.Y, curW, bar.Height), 2);
            }
            y += (int)(11 * scale);
            if (!compact)
            {
                var name = m.CurrentName;
                var now = name.Length > 0 ? "Now  " + name : m.LastFinished.Length > 0 ? "Last  " + m.LastFinished : "Waiting for the downloader…";
                var size = m.CurrentBytes > 0 ? Bytes(m.CurrentBytes) + (m.CurrentExpected is { } ex ? " / ≈ " + Bytes(Math.Max(ex, m.CurrentBytes)) : "") : "";
                var sizeW = size.Length > 0 ? TextRenderer.MeasureText(size, Fonts.Mono(8f * scale)).Width + 8 : 0;
                Ui.Text(g, now, Fonts.Body(8.75f * scale), new Rectangle(0, y, Width - sizeW - 6, (int)(18 * scale)), p.Muted);
                if (size.Length > 0) Ui.Text(g, size, Fonts.Mono(8f * scale), new Rectangle(Width - sizeW, y, sizeW, (int)(18 * scale)), p.Dim, TextFormatFlags.Right);
                y += (int)(22 * scale);
            }
            else y += 4;
        }

        // ---- plot
        plotRect = new Rectangle(0, y, Width - 1, Math.Max(40, Height - y - footer - 4));
        var plot = plotRect;
        Ui.FillRound(g, ColorX.Mix(p.Panel, p.Panel2, 0.5), plot, Theme.R(8), ColorX.Mix(p.Line, p.Panel, 0.4));
        DrawRangeChips(g, plot, scale);
        var all = m?.Samples ?? new List<DownloadMonitor.Sample>();
        if (all.Count < 2)
        {
            var msg = m is { Running: true } ? (m.Paused ? "Paused — Resume to continue the download." : "Waiting for the downloader to start transferring…")
                : "Download speed, progress and every finished song show up here live while SpotySync downloads.";
            Ui.Text(g, msg, Fonts.Body(9f * scale), plot, p.Dim, TextFormatFlags.HorizontalCenter);
        }
        else DrawSeries(g, plot, m!, all, scale);

        if (footer > 0)
        {
            var led = StatusPill.LedColor(m!.ResultState);
            using (var dot = new SolidBrush(led)) g.FillEllipse(dot, 2, Height - footer / 2 - 4, 8, 8);
            Ui.Text(g, m.Result, Fonts.Body(8.75f * scale), new Rectangle(16, Height - footer, Width - 16, footer), p.Muted);
        }
    }

    private void DrawRangeChips(Graphics g, Rectangle plot, float scale)
    {
        var p = Theme.P;
        rangeChips.Clear();
        var font = Fonts.Body(7.75f * scale, FontStyle.Bold);
        var x = plot.Right - 8;
        foreach (var (label, seconds) in Ranges.Reverse())
        {
            var w = TextRenderer.MeasureText(label, font).Width + 10;
            x -= w;
            var r = new Rectangle(x, plot.Top + 6, w, (int)(17 * scale));
            var on = seconds == range;
            if (on) Ui.FillRound(g, ColorX.Mix(p.Panel, p.Accent, 0.22), r, r.Height / 2f);
            Ui.Text(g, label, font, r, on ? p.Accent : p.Dim, TextFormatFlags.HorizontalCenter);
            rangeChips.Add((r, seconds));
            x -= 3;
        }
    }

    private void DrawSeries(Graphics g, Rectangle plot, DownloadMonitor m, List<DownloadMonitor.Sample> all, float scale)
    {
        var p = Theme.P;
        var end = all[^1].Seconds;
        var start = range > 0 ? Math.Max(0, end - range) : 0;
        var shown = all.Where(s => s.Seconds >= start).ToList();
        if (shown.Count < 2) shown = all.TakeLast(2).ToList();
        start = shown[0].Seconds;
        var spanSec = Math.Max(1, end - start);
        var axisW = (int)(78 * scale);
        var inner = new Rectangle(plot.Left + axisW, plot.Top + (int)(30 * scale), plot.Width - axisW - 12, plot.Height - (int)(30 * scale) - (int)(22 * scale));
        if (inner.Width < 20 || inner.Height < 20) return;
        // bucket to about 2 px per point
        var buckets = Math.Max(2, Math.Min(shown.Count, inner.Width / 2));
        var smooth = new double[buckets]; var instant = new double[buckets]; var secs = new double[buckets];
        for (var b = 0; b < buckets; b++)
        {
            var from = b * shown.Count / buckets; var to = Math.Max(from + 1, (b + 1) * shown.Count / buckets);
            double s = 0, i = 0;
            for (var k = from; k < to; k++) { s += shown[k].Smooth; i = Math.Max(i, shown[k].Instant); }
            smooth[b] = s / (to - from); instant[b] = i; secs[b] = shown[to - 1].Seconds;
        }
        var max = NiceCeiling(Math.Max(256 * 1024, Math.Max(smooth.Max() * 1.15, m.AverageSpeed * 1.3)));
        float X(double sec) => inner.Left + (float)((sec - start) / spanSec * inner.Width);
        float Y(double v) => inner.Bottom - (float)(Math.Clamp(v / max, 0, 1) * inner.Height);

        // grid + y axis
        using (var gridPen = new Pen(ColorX.Mix(p.Line, p.Panel, 0.5)) { DashStyle = DashStyle.Dot })
        {
            for (var k = 0; k <= 4; k++)
            {
                var v = max * k / 4; var gy = Y(v);
                if (k > 0) g.DrawLine(gridPen, inner.Left, gy, inner.Right, gy);
                Ui.Text(g, k == 0 ? "0" : Bytes(v) + "/s", Fonts.Mono(7f * scale), new Rectangle(plot.Left + 2, (int)(gy - 8 * scale), axisW - 8, (int)(16 * scale)), p.Dim, TextFormatFlags.Right);
            }
        }
        // x axis (elapsed time)
        var ticks = Math.Clamp(inner.Width / (int)(110 * scale), 2, 8);
        for (var k = 0; k <= ticks; k++)
        {
            var sec = start + spanSec * k / ticks;
            var tx = (int)X(sec);
            var label = Clock(TimeSpan.FromSeconds(sec));
            var flags = k == 0 ? TextFormatFlags.Left : k == ticks ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
            var rx = k == 0 ? tx : k == ticks ? tx - 70 : tx - 35;
            Ui.Text(g, label, Fonts.Mono(7f * scale), new Rectangle(rx, inner.Bottom + 4, 70, 16), p.Dim, flags);
        }

        // instant bursts (faint bars), then the smooth area + line
        using (var burst = new SolidBrush(ColorX.Alpha(p.Accent2, 20)))
        {
            var bw = Math.Max(1f, inner.Width / (float)buckets - 1);
            for (var b = 0; b < buckets; b++)
                if (instant[b] > 0) { var top = Y(instant[b]); g.FillRectangle(burst, X(secs[b]) - bw / 2, top, bw, inner.Bottom - top); }
        }
        var line = Enumerable.Range(0, buckets).Select(b => new PointF(X(secs[b]), Y(smooth[b]))).ToArray();
        using (var area = new GraphicsPath())
        {
            area.AddLine(line[0].X, inner.Bottom, line[0].X, line[0].Y);
            area.AddCurve(line, 0.35f);
            area.AddLine(line[^1].X, line[^1].Y, line[^1].X, inner.Bottom);
            area.CloseFigure();
            using var fill = new LinearGradientBrush(new Rectangle(inner.X, inner.Y - 1, inner.Width, inner.Height + 2), ColorX.Alpha(p.Accent, 95), ColorX.Alpha(p.Accent, 6), LinearGradientMode.Vertical);
            g.FillPath(fill, area);
        }
        using (var pen = new Pen(p.Accent, 2f * scale) { LineJoin = LineJoin.Round }) g.DrawCurve(pen, line, 0.35f);
        // run average
        if (m.AverageSpeed > 0)
        {
            var ay = Y(m.AverageSpeed);
            using var avg = new Pen(ColorX.Alpha(p.Ink, 90), 1f) { DashStyle = DashStyle.Dash };
            g.DrawLine(avg, inner.Left, ay, inner.Right, ay);
            Ui.Text(g, "avg " + Bytes(m.AverageSpeed) + "/s", Fonts.Mono(7f * scale), new Rectangle(inner.Left + 6, (int)ay - 15, 140, 14), p.Muted);
        }
        // finished (accent) and failed (red) song markers along the bottom
        foreach (var ev in m.Events)
        {
            if (ev.Seconds < start) continue;
            var ex = X(Math.Min(ev.Seconds, end));
            using var marker = new SolidBrush(ev.Failed ? p.Err : p.Accent2);
            g.FillEllipse(marker, ex - 2.5f * scale, inner.Bottom - 6 * scale, 5 * scale, 5 * scale);
        }
        // live dot at the head of the line
        if (m.Running && !m.Paused)
        {
            var head = line[^1];
            using var glow = new SolidBrush(ColorX.Alpha(p.Accent, 70));
            g.FillEllipse(glow, head.X - 6, head.Y - 6, 12, 12);
            using var dot = new SolidBrush(p.Accent);
            g.FillEllipse(dot, head.X - 3, head.Y - 3, 6, 6);
        }
        if (m.Paused)
        {
            var banner = new Rectangle(inner.Left + inner.Width / 2 - 60, inner.Top + 6, 120, (int)(24 * scale));
            Ui.FillRound(g, ColorX.Mix(p.Panel, p.Warn, 0.25), banner, banner.Height / 2f);
            Ui.Text(g, "❚❚  Paused", Fonts.Semibold(9f * scale), banner, p.Warn, TextFormatFlags.HorizontalCenter);
        }

        // hover read-out
        if (hoverPoint is { } hp && hp.X >= inner.Left && hp.X <= inner.Right)
        {
            var sec = start + (hp.X - inner.Left) / (double)inner.Width * spanSec;
            var sample = shown.MinBy(s => Math.Abs(s.Seconds - sec));
            var sx = X(sample.Seconds);
            using (var cross = new Pen(ColorX.Alpha(p.Ink, 80)) { DashStyle = DashStyle.Dot }) g.DrawLine(cross, sx, inner.Top, sx, inner.Bottom);
            using (var dot = new SolidBrush(p.Accent)) g.FillEllipse(dot, sx - 3.5f, Y(sample.Smooth) - 3.5f, 7, 7);
            var lines = new[]
            {
                Clock(TimeSpan.FromSeconds(sample.Seconds)),
                $"{Bytes(sample.Smooth)}/s average",
                $"{Bytes(sample.Instant)} in that second",
                $"{sample.Done} song(s) done" + (sample.Failed > 0 ? $", {sample.Failed} failed" : "")
            };
            var font = Fonts.Body(8f * scale);
            var w = lines.Max(l => TextRenderer.MeasureText(l, font).Width) + 16;
            var h = lines.Length * (int)(16 * scale) + 10;
            var bx = sx + 12 + w > inner.Right ? (int)sx - 12 - w : (int)sx + 12;
            var box = new Rectangle(bx, inner.Top + 4, w, h);
            Ui.FillRound(g, ColorX.Alpha(p.Panel2, 240), box, Theme.R(6), p.Border);
            for (var k = 0; k < lines.Length; k++)
                Ui.Text(g, lines[k], k == 0 ? Fonts.Semibold(8f * scale) : font, new Rectangle(box.X + 8, box.Y + 5 + k * (int)(16 * scale), w - 12, (int)(16 * scale)), k == 0 ? p.Ink : p.Muted);
        }
    }

    private static double NiceCeiling(double v)
    {
        var exp = Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(1, v))));
        foreach (var f in new[] { 0.5, 0.625, 0.75, 0.875, 1.0 }) if (exp * f >= v) return exp * f;
        return exp;
    }
}

/// <summary>Full-screen download activity window (F11 / Esc), with pause and cancel.</summary>
internal sealed class ActivityWindow : Form
{
    private readonly ActivityGraph graph = new() { Dock = DockStyle.Fill, Large = true };
    private readonly RoundedButton pause = RoundedButton.Make("Pause", ButtonKind.Secondary, Glyph.Pause);
    private readonly RoundedButton cancel = RoundedButton.Make("Cancel", ButtonKind.Secondary, Glyph.Stop);
    private readonly RoundedButton exit = RoundedButton.Make("Exit full screen", ButtonKind.Ghost, Glyph.Collapse);
    private readonly Func<bool> canPause;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 400 };
    private FormWindowState restoreState;
    private Rectangle restoreBounds;

    public ActivityWindow(DownloadMonitor monitor, int range, Action togglePause, Action cancelRun, Func<bool> canPause, Action<int> rangeChanged)
    {
        this.canPause = canPause;
        Text = "SpotySync — Download activity";
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        MinimumSize = new Size(640, 420);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Ui.StyleWindow(this);
        graph.Monitor = monitor; graph.Range = range;
        graph.RangeChanged += rangeChanged;
        graph.FullScreenRequested += ToggleFullScreen;
        var top = new Row(rightToLeft: true) { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 4, 0, 6) };
        exit.Click += (_, _) => Close();
        pause.Click += (_, _) => { togglePause(); UpdateButtons(); };
        cancel.Click += (_, _) => cancelRun();
        foreach (var b in new[] { exit, cancel, pause }) { b.Margin = new Padding(8, 0, 0, 0); top.Controls.Add(b); }
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(36, 18, 36, 24) };
        host.Controls.Add(graph); host.Controls.Add(top);
        Controls.Add(host);
        refresh.Tick += (_, _) => UpdateButtons();
        refresh.Start();
        ApplyTheme();
        Theme.Changed += ApplyTheme;
        FormClosed += (_, _) => { Theme.Changed -= ApplyTheme; refresh.Dispose(); graph.Monitor = null; };
        UpdateButtons();
    }

    private void ApplyTheme()
    {
        BackColor = Theme.P.Bg;
        Ui.StyleWindow(this);
        Ui.Restyle(this);
        Invalidate(true);
    }

    private void UpdateButtons()
    {
        var running = graph.Monitor?.Running == true;
        pause.Enabled = canPause() || DownloadProcesses.Paused;
        pause.Text = DownloadProcesses.Paused ? "Resume" : "Pause";
        pause.Glyph = DownloadProcesses.Paused ? Glyph.Play : Glyph.Pause;
        cancel.Enabled = running;
    }

    /// <summary>Opens on the screen of <paramref name="owner"/>, borderless over the whole monitor.</summary>
    public void ShowFullScreen(Form owner)
    {
        var screen = Screen.FromControl(owner).Bounds;
        Bounds = Rectangle.Inflate(screen, -80, -60);
        Show(owner);
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = restoreState;
            if (restoreState == FormWindowState.Normal) Bounds = restoreBounds;
            exit.Text = "Close";
        }
        else
        {
            restoreState = WindowState; restoreBounds = Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            exit.Text = "Exit full screen";
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11) { ToggleFullScreen(); return true; }
        if (keyData == Keys.Escape) { Close(); return true; }
        if (keyData == Keys.Space) { pause.PerformClick(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
