using System.Text;
using System.Text.RegularExpressions;

namespace SpotySyncApp;

internal enum LogLevel { Info, Success, Warn, Error, Header, Detail }

internal sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Text { get; set; } = "";
    public LogLevel Level { get; init; }
    public string Tag { get; init; } = "";
    public string Key { get; init; } = "";
    public int Repeat { get; set; } = 1;
    /// <summary>Shown in the simple view (key steps, progress, results, problems).</summary>
    public bool Milestone { get; set; }
    /// <summary>Plain-English wording for the simple view (empty = use Text).</summary>
    public string Simple { get; set; } = "";
    public bool IsDetail => Level == LogLevel.Detail;
    public bool IsIssue => Level is LogLevel.Warn or LogLevel.Error;
}

/// <summary>Turns raw service log lines into tidy, categorised console entries.</summary>
internal static class LogClassifier
{
    private static readonly Regex DownloaderPrefix = new(@"^(download|spot2yt|spotdl|yt-dlp|direct|recovery|media)\s*\|\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SuccessWords = new(@"\b(complete|completed|finished|ready|connected|installed|saved|verified|applied|recovered)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Progress-style lines that should update in place instead of scrolling the console.
    private static readonly (string Prefix, string Key)[] Collapsing =
    [
        ("Liked Songs cache: read page", "liked-pages"),
        ("Local metadata scan:", "meta-scan"),
        ("Spotify playlist cache: refreshed", "playlist-cache"),
        ("Wrote M3U playlist", "m3u-write"),
        ("Submitting to spot2yt:", "submit"),
    ];

    private static readonly Regex Progress = new(@"^progress \| (.+) \| (\d{1,3})$", RegexOptions.Compiled);
    private static readonly Regex Num = new(@"\d+", RegexOptions.Compiled);

    public static LogEntry Classify(string message)
    {
        var text = message.Trim();
        var progress = Progress.Match(text);
        if (progress.Success)
        {
            var name = progress.Groups[1].Value.Trim();
            var pct = int.Parse(progress.Groups[2].Value);
            var line = pct >= 100 ? $"Downloaded {name}" : $"Downloading {name}  {pct}%";
            return new LogEntry { Text = line, Simple = line, Level = pct >= 100 ? LogLevel.Success : LogLevel.Info, Tag = "DOWNLOAD", Key = "progress:" + name, Milestone = true };
        }
        var error = text.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || text.Contains(" | ERROR", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains(" failed:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("BLOCKED ", StringComparison.Ordinal);
        // a song that simply has no download source is a warning, not an error (it is retried next backup)
        if (text.StartsWith("Backup verification failed:", StringComparison.OrdinalIgnoreCase) && text.Contains("verified", StringComparison.OrdinalIgnoreCase)) error = false;
        var warn = !error && (text.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) || text.Contains(" | WARNING", StringComparison.OrdinalIgnoreCase) ||
                              text.StartsWith("Backup verification failed:", StringComparison.OrdinalIgnoreCase));
        var downloader = DownloaderPrefix.Match(text);
        var detail = downloader.Success && !error && !warn && !text.Contains("stage complete", StringComparison.OrdinalIgnoreCase);
        var display = text;
        if (downloader.Success && !error && !warn) display = text[downloader.Length..].Trim();
        else if (error && text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) display = text[6..].Trim();
        else if (warn && text.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase)) display = text[8..].Trim();
        var level = error ? LogLevel.Error : warn ? LogLevel.Warn : detail ? LogLevel.Detail
            : SuccessWords.IsMatch(text) && !text.EndsWith('…') && !text.EndsWith("...") ? LogLevel.Success : LogLevel.Info;
        var key = Collapsing.FirstOrDefault(c => text.StartsWith(c.Prefix, StringComparison.OrdinalIgnoreCase)).Key ?? "";
        var (milestone, simple, success) = Simplify(display, error || warn);
        if (success && level is LogLevel.Info or LogLevel.Detail) level = LogLevel.Success;
        return new LogEntry { Text = display, Level = level, Tag = TagFor(text, downloader.Success), Key = key, Milestone = milestone, Simple = simple };
    }

    /// <summary>Decides what the simple view shows and how it is worded.</summary>
    private static (bool Milestone, string Simple, bool Success) Simplify(string t, bool issue)
    {
        bool Starts(string s) => t.StartsWith(s, StringComparison.OrdinalIgnoreCase);
        int[] N() => Num.Matches(t).Select(m => int.Parse(m.Value)).ToArray();
        if (Starts("Backup verification failed:") || Regex.IsMatch(t, @"exit code \d+; reported errors \d+; verified"))
        {
            var m = Regex.Match(t, @"verified (\d+)/(\d+)");
            var missing = m.Success ? int.Parse(m.Groups[2].Value) - int.Parse(m.Groups[1].Value) : 1;
            return (true, $"{missing} song{(missing == 1 ? "" : "s")} couldn't be downloaded (no source found) - SpotySync will try again next backup", false);
        }
        var noSource = Regex.Match(t, @"no matching public audio source was found for (.+?); it remains skipped", RegexOptions.IgnoreCase);
        if (noSource.Success) return (true, $"Not found on YouTube or SoundCloud: {noSource.Groups[1].Value} (skipped - use Download links with a YouTube link to add it)", false);
        if (t.Contains("MusicBee import/initialization failed", StringComparison.OrdinalIgnoreCase))
            return (true, "Couldn't add to MusicBee right now - the songs are saved and will be added on the next backup", false);
        if (issue) return (true, t.Length > 160 ? t[..157] + "…" : t, false);
        if (Starts("MusicBee was busy")) return (true, "MusicBee was busy - retrying…", false);
        if (Regex.IsMatch(t, @"left \d+ track\(s\) without a YouTube Music source")) { var n = N(); return (true, $"Looking for {n.FirstOrDefault()} song(s) on YouTube and SoundCloud…", false); }
        if (Starts("Recent Likes backup:"))
        {
            var m = Regex.Match(t, @"(\d+) to download");
            var n = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            return (true, n == 0 ? "No new Liked Songs to download" : $"{n} new Liked Song{(n == 1 ? "" : "s")} to download", false);
        }
        if (Starts("Found ") && t.Contains("track(s)")) { var n = N(); return (true, $"Found {n.FirstOrDefault()} song(s) - finding sources…", false); }
        if (Starts("Downloading ") && t.Contains("track(s)")) { var n = N(); return (true, $"Downloading {n.FirstOrDefault()} song(s)…", false); }
        if (Starts("Audio download stage complete")) { var n = N(); return (true, n.Length >= 2 ? $"Downloaded {n[0]}/{n[1]} songs" : "Downloads finished", true); }
        if (Starts("Downloaded: ")) return (true, "✓ " + t["Downloaded: ".Length..], true);
        if (Starts("MusicBee final stage")) return (true, "Adding to MusicBee…", false);
        if (Starts("MusicBee verified")) { var n = N(); return (true, n.Length >= 2 ? $"MusicBee: {n[0]}/{n[1]} added" : "Added to MusicBee", true); }
        if (Starts("Preview ready:")) { var n = N(); return (true, n.Length >= 2 ? $"Preview ready: {n[1]} change(s) across {n[0]} playlists" : "Preview ready", true); }
        if (Starts("Applying '"))
        {
            var n = N();
            if (n.All(x => x == 0)) return (false, "", false);
            var name = t.Split('\'').ElementAtOrDefault(1) ?? "playlist";
            return (true, $"Updated {name}: Spotify +{n.ElementAtOrDefault(0)}/−{n.ElementAtOrDefault(1)}, local +{n.ElementAtOrDefault(2)}/−{n.ElementAtOrDefault(3)}", true);
        }
        if (Starts("Reading Spotify profile")) return (true, "Reading Spotify…", false);
        if (Starts("Reading MusicBee library")) return (true, "Reading MusicBee…", false);
        if (Starts("Soundtrack Saver:")) return (true, t["Soundtrack Saver:".Length..].Trim(), t.Contains("saved"));
        if (Starts("Manual download complete") || Starts("Spotify connected") || Starts("Spotify is connected") || Starts("Library insights:") ||
            Starts("Never-sync list updated") || Starts("Reused your existing") || Starts("Remembered the redirect") ||
            Starts("Updated the MusicBee plugin") || Starts("Installed the MusicBee plugin") || Starts("MusicBee plugin is") ||
            Starts("Reopened MusicBee") || Starts("Opening Spotify sign-in") || Starts("MusicBee Folders tab") || Starts("Saved ") || Starts("Exported "))
            return (true, t, false);
        return (false, "", false);
    }

    private static string TagFor(string text, bool downloader)
    {
        bool Has(string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (downloader || Has("spot2yt") || Has("yt-dlp") || Has("downloader") || Has("public-source") || Has("download")) return "DOWNLOAD";
        if (Has("MusicBee")) return "MUSICBEE";
        if (Has("M3U")) return "M3U";
        if (Has("Preview") || Has("Applying") || Has("BLOCKED") || Has("baseline") || Has("sync")) return "SYNC";
        if (Has("Backup") || Has("Recent Likes") || Has("Liked")) return "BACKUP";
        if (Has("Spotify")) return "SPOTIFY";
        if (Has("library") || Has("metadata") || Has("audio file")) return "LIBRARY";
        if (Has("Settings") || Has("plugin")) return "APP";
        return "";
    }
}

/// <summary>Builds coloured RTF for a set of entries (fast enough for thousands of lines).</summary>
internal static class LogRtf
{
    public static string Build(IEnumerable<LogEntry> entries, bool timestamps, float fontSize, bool simple = false)
    {
        var p = Theme.P;
        var colors = new[] { p.Ink, p.Muted, p.Dim, p.Accent, p.Ok, p.Warn, p.Err, p.LocalAdd, p.Protected, p.SpotifyAdd, ColorX.Mix(p.Ok, p.Ink, 0.45) };
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern ").Append(Fonts.Mono(fontSize).Name).Append(";}}{\\colortbl ;");
        foreach (var c in colors) sb.Append($"\\red{c.R}\\green{c.G}\\blue{c.B};");
        sb.Append('}').Append($"\\f0\\fs{(int)Math.Round(fontSize * 2)} ");
        var any = false;
        foreach (var entry in entries)
        {
            if (any) sb.Append(entry.Level == LogLevel.Header ? "\\par\\par " : "\\par ");
            any = true;
            if (simple) SimpleLine(sb, entry); else Line(sb, entry, timestamps);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void SimpleLine(StringBuilder sb, LogEntry e)
    {
        if (e.Level == LogLevel.Header) { sb.Append("\\cf4\\b ").Append(Escape(e.Text)).Append("\\b0 "); return; }
        var text = e.Simple.Length > 0 ? e.Simple : e.Text;
        if (e.Tag == "DONE") text = e.Level == LogLevel.Success ? "Done" + text[(text.IndexOf(" finished", StringComparison.Ordinal) is var i and >= 0 ? i + 9 : text.Length)..].TrimEnd('.') : text;
        var color = e.Level switch { LogLevel.Error => 7, LogLevel.Warn => 6, LogLevel.Success => 5, _ => 1 };
        var marker = e.Level switch { LogLevel.Error => "✕ ", LogLevel.Warn => "▲ ", _ => "  " };
        if (text.StartsWith("✓ ")) { marker = "✓ "; text = text[2..]; }
        sb.Append($"\\cf{color} ").Append(Escape(marker + text));
    }

    // colour indices: 1 ink, 2 muted, 3 dim, 4 accent, 5 ok, 6 warn, 7 err, 8 blue, 9 purple, 10 green, 11 soft-ok
    private static void Line(StringBuilder sb, LogEntry e, bool timestamps)
    {
        if (e.Level == LogLevel.Header)
        {
            sb.Append("\\cf3 ").Append(timestamps ? e.Time.ToString("HH:mm:ss") + "  " : "");
            sb.Append("\\cf4\\b ").Append(Escape("━━  " + e.Text + "  ")).Append("\\b0\\cf3 ").Append(Escape(new string('━', Math.Max(4, 56 - e.Text.Length))));
            return;
        }
        if (timestamps) sb.Append("\\cf3 ").Append(e.Time.ToString("HH:mm:ss")).Append("  ");
        var tagColor = e.Tag switch { "SPOTIFY" => 10, "MUSICBEE" => 6, "DOWNLOAD" => 8, "SYNC" => 4, "BACKUP" => 9, "M3U" => 8, _ => 3 };
        var marker = e.Level switch { LogLevel.Error => "✕", LogLevel.Warn => "▲", LogLevel.Success => "✓", LogLevel.Detail => "·", _ => "›" };
        var markerColor = e.Level switch { LogLevel.Error => 7, LogLevel.Warn => 6, LogLevel.Success => 5, _ => 3 };
        sb.Append($"\\cf{markerColor} ").Append(Escape(marker)).Append(' ');
        sb.Append($"\\cf{tagColor} ").Append(Escape((e.Tag.Length > 0 ? e.Tag : "").PadRight(9)));
        var textColor = e.Level switch { LogLevel.Error => 7, LogLevel.Warn => 6, LogLevel.Detail => 2, LogLevel.Success => 11, _ => 1 };
        if (e.Level == LogLevel.Error) sb.Append("\\b ");
        sb.Append($"\\cf{textColor} ").Append(Escape(e.Text));
        if (e.Level == LogLevel.Error) sb.Append("\\b0 ");
        if (e.Repeat > 1) sb.Append("\\cf3 ").Append(Escape($"  ×{e.Repeat}"));
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\r': break;
                case '\n': sb.Append(@"\line "); break;
                default:
                    if (ch > 127) sb.Append("\\u").Append((int)(short)ch).Append('?');
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>The Console page: filters, search, collapsing progress lines and per-run summaries.</summary>
internal sealed class ConsoleView : Panel
{
    private const int MaxEntries = 6000;
    private readonly List<LogEntry> entries = new();
    private readonly RichTextBox box = new();
    private readonly FieldBox search = new() { LeadingGlyph = Glyph.Search, ClearButton = true, Placeholder = "Search the console   (Ctrl+F)", Width = 260 };
    private readonly Chip allChip = new() { Text = "All", Selected = true };
    private readonly Chip warnChip = new() { Text = "Warnings", Glyph = Glyph.Warning };
    private readonly Chip errorChip = new() { Text = "Errors", Glyph = Glyph.Error };
    private readonly ToggleSwitch detailSwitch = new() { Text = "Downloader detail" };
    private readonly ToggleSwitch simpleSwitch = new() { Text = "Simple" };
    public bool Simple => simpleSwitch.Checked;
    public event Action<bool>? SimpleChanged;
    private readonly ToggleSwitch timeSwitch = new() { Text = "Times" };
    private readonly ToggleSwitch followSwitch = new() { Text = "Follow", Checked = true };
    private readonly Card summary = new() { Raised = true, Radius = 12, Inner = new Padding(18, 10, 14, 10) };
    private readonly Label summaryText = Ui.Label("", "ink", Fonts.Semibold(10f), autoSize: false);
    private readonly Label summaryIssues = Ui.Label("", "muted", Fonts.Body(9f), autoSize: false);
    private readonly RoundedButton summaryShow = RoundedButton.Make("Show issues", ButtonKind.Secondary, Glyph.Filter);
    private readonly RoundedButton summaryClose = RoundedButton.Make("", ButtonKind.Ghost, Glyph.Close);
    private readonly Card logCard = new() { Radius = 14, Inner = new Padding(4, 4, 2, 4) };
    private readonly Label hiddenNote = Ui.Label("", "dim", Fonts.Body(8.75f));
    private string levelFilter = "all";
    private int lastLineStart = -1;
    private LogEntry? lastVisible;
    private int hiddenDetail;
    private RunScope? run;
    private readonly System.Windows.Forms.Timer searchDebounce = new() { Interval = 180 };

    public event Action<LogEntry>? Appended;
    public event Action? CountsChanged;
    /// <summary>The log was cleared or trimmed (followers should re-read <see cref="Entries"/>).</summary>
    public event Action? Reset;
    public event Action<bool, bool>? PreferencesChanged; // detail, timestamps
    public string LogDirectory { get; set; } = "";
    public int Warnings { get; private set; }
    public int Errors { get; private set; }
    public int UnseenIssues { get; set; }

    private sealed class RunScope
    {
        public string Label = "";
        public DateTime Started;
        public int Warnings, Errors;
        public readonly List<string> Issues = new();
    }

    public ConsoleView(bool showDetail, bool timestamps, bool simple = true)
    {
        simpleSwitch.Checked = simple;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        detailSwitch.Checked = showDetail;
        timeSwitch.Checked = timestamps;

        var toolbar = new WrapRow();
        var left = new Row { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        allChip.Click += (_, _) => SetLevel("all");
        warnChip.Click += (_, _) => SetLevel("warn");
        errorChip.Click += (_, _) => SetLevel("error");
        search.Margin = new Padding(8, 0, 12, 0);
        search.Height = 32;
        foreach (var c in new Control[] { allChip, warnChip, errorChip, search }) left.Controls.Add(c);
        foreach (var s in new[] { simpleSwitch, detailSwitch })
        {
            s.Width = s.GetPreferredSize(Size.Empty).Width; s.Margin = new Padding(6, 3, 6, 0);
            left.Controls.Add(s);
        }
        var right = new Row(rightToLeft: true) { Dock = DockStyle.Right, Width = 300 };
        var openLogs = RoundedButton.Make("Logs", ButtonKind.Ghost, Glyph.Folder, (_, _) => OpenLogs());
        var clear = RoundedButton.Make("Clear", ButtonKind.Ghost, Glyph.Clear, (_, _) => Clear());
        var copy = RoundedButton.Make("Copy", ButtonKind.Ghost, Glyph.Copy, (_, _) => { if (box.TextLength > 0) Clipboard.SetText(box.Text); });
        foreach (var b in new[] { openLogs, clear, copy }) { b.Margin = new Padding(4, 2, 0, 0); right.Controls.Add(b); }
        toolbar.Absorb(left, right);

        summary.Dock = DockStyle.Top; summary.Height = 66; summary.Visible = false;
        var summaryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent };
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); summaryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        summaryText.Dock = DockStyle.Fill; summaryText.TextAlign = ContentAlignment.BottomLeft; summaryText.AutoEllipsis = true;
        summaryIssues.Dock = DockStyle.Fill; summaryIssues.TextAlign = ContentAlignment.TopLeft; summaryIssues.AutoEllipsis = true;
        summaryShow.Anchor = AnchorStyles.Right; summaryClose.Anchor = AnchorStyles.Right; summaryClose.MinimumSize = new Size(34, 34);
        summaryShow.Click += (_, _) => SetLevel(Errors > 0 && run is null ? "error" : "warn");
        summaryClose.Click += (_, _) => { summary.Visible = false; };
        summaryLayout.Controls.Add(summaryText, 0, 0); summaryLayout.Controls.Add(summaryIssues, 0, 1);
        summaryLayout.Controls.Add(summaryShow, 1, 0); summaryLayout.SetRowSpan(summaryShow, 2);
        summaryLayout.Controls.Add(summaryClose, 2, 0); summaryLayout.SetRowSpan(summaryClose, 2);
        summary.Controls.Add(summaryLayout);

        var spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
        box.Dock = DockStyle.Fill; box.ReadOnly = true; box.BorderStyle = BorderStyle.None; box.DetectUrls = false;
        box.WordWrap = true; box.HideSelection = false; box.ScrollBars = RichTextBoxScrollBars.Vertical;
        Ui.Role(box, "well");
        Ui.DarkScrollbars(box);
        var menu = Ui.Menu();
        menu.Items.Add("Copy selection", null, (_, _) => { if (box.SelectionLength > 0) Clipboard.SetText(box.SelectedText); });
        menu.Items.Add("Copy everything shown", null, (_, _) => { if (box.TextLength > 0) Clipboard.SetText(box.Text); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Clear view", null, (_, _) => Clear());
        menu.Items.Add("Open logs folder", null, (_, _) => OpenLogs());
        box.ContextMenuStrip = menu;
        box.KeyDown += (_, e) => CopyKeys(box, e);
        var hiddenBar = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = Color.Transparent };
        hiddenNote.Dock = DockStyle.Fill; hiddenNote.AutoSize = false; hiddenNote.TextAlign = ContentAlignment.MiddleRight; hiddenNote.Padding = new Padding(0, 0, 10, 0);
        hiddenBar.Controls.Add(hiddenNote);
        logCard.Controls.Add(box); logCard.Controls.Add(hiddenBar);
        logCard.Dock = DockStyle.Fill;
        Controls.Add(logCard); Controls.Add(spacer); Controls.Add(summary); Controls.Add(toolbar);

        searchDebounce.Tick += (_, _) => { searchDebounce.Stop(); Rerender(); };
        search.TextChanged += (_, _) => { searchDebounce.Stop(); searchDebounce.Start(); };
        simpleSwitch.CheckedChanged += (_, _) => { detailSwitch.Enabled = !simpleSwitch.Checked; Rerender(); SimpleChanged?.Invoke(simpleSwitch.Checked); };
        detailSwitch.Enabled = !simple;
        detailSwitch.CheckedChanged += (_, _) => { Rerender(); PreferencesChanged?.Invoke(detailSwitch.Checked, timeSwitch.Checked); };
        timeSwitch.CheckedChanged += (_, _) => { Rerender(); PreferencesChanged?.Invoke(detailSwitch.Checked, timeSwitch.Checked); };
        followSwitch.CheckedChanged += (_, _) => { if (followSwitch.Checked) ScrollToEnd(); };
        Theme.Changed += OnTheme;
        OnTheme();
    }

    private void OnTheme()
    {
        box.BackColor = ColorX.Mix(Theme.P.Panel, Theme.P.Well, 0.6);
        box.ForeColor = Theme.P.Ink;
        Rerender();
    }

    /// <summary>Ctrl+C copies the selection (or everything shown); Ctrl+A selects all.</summary>
    internal static void CopyKeys(RichTextBox target, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            var text = target.SelectionLength > 0 ? target.SelectedText : target.Text;
            if (text.Length > 0) { try { Clipboard.SetText(text); } catch { } }
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.A) { target.SelectAll(); e.Handled = e.SuppressKeyPress = true; }
    }

    public void FocusSearch() { search.Inner.Focus(); search.Inner.SelectAll(); }

    public void SetDetail(bool detail) => detailSwitch.Checked = detail;
    public void SetTimestamps(bool on) => timeSwitch.Checked = on;
    public void SetSimple(bool on) => simpleSwitch.Checked = on;

    /// <summary>Recent entries as the dashboard Console module should show them (follows the Simple switch).</summary>
    public List<LogEntry> RecentForFeed(int count) =>
        entries.Where(e => Simple ? e.Milestone || e.IsIssue || e.Tag == "DONE" || e.Level == LogLevel.Header : !e.IsDetail).TakeLast(count).ToList();

    public IReadOnlyList<LogEntry> Entries => entries;

    // ---------------------------------------------------------------- operations
    public void BeginRun(string label)
    {
        run = new RunScope { Label = label, Started = DateTime.Now };
        Add(new LogEntry { Text = label, Level = LogLevel.Header });
    }

    /// <summary>Closes the current run and returns (warnings, errors).</summary>
    public (int Warnings, int Errors, IReadOnlyList<string> Issues) EndRun(string outcome)
    {
        if (run is null) return (0, 0, Array.Empty<string>());
        var r = run; run = null;
        var elapsed = DateTime.Now - r.Started;
        var duration = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{Math.Max(0, elapsed.TotalSeconds):0.0}s";
        LogLevel level; string text;
        switch (outcome)
        {
            case "cancelled": level = LogLevel.Warn; text = $"{r.Label} cancelled after {duration}."; break;
            case "failed": level = LogLevel.Error; text = $"{r.Label} stopped after {duration}."; break;
            default:
                level = r.Errors > 0 ? LogLevel.Error : r.Warnings > 0 ? LogLevel.Warn : LogLevel.Success;
                text = r.Errors + r.Warnings == 0 ? $"{r.Label} finished in {duration}."
                    : $"{r.Label} finished in {duration} with {Plural(r.Errors, "error")} and {Plural(r.Warnings, "warning")}.";
                break;
        }
        Add(new LogEntry { Text = text, Level = level, Tag = "DONE" }, countIssue: false);
        ShowSummary(text, level, r.Issues);
        return (r.Warnings, r.Errors, r.Issues);
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private void ShowSummary(string text, LogLevel level, IReadOnlyList<string> issues)
    {
        summaryText.Text = text;
        summaryText.ForeColor = level switch { LogLevel.Error => Theme.P.Err, LogLevel.Warn => Theme.P.Warn, _ => ColorX.Mix(Theme.P.Ok, Theme.P.Ink, 0.3) };
        summary.AccentEdge = level switch { LogLevel.Error => Theme.P.Err, LogLevel.Warn => Theme.P.Warn, _ => Theme.P.Ok };
        summary.Glyph = level switch { LogLevel.Error => Glyph.Error, LogLevel.Warn => Glyph.Warning, _ => Glyph.Check };
        summaryIssues.Text = issues.Count == 0 ? "Everything went smoothly — no warnings or errors."
            : string.Join("   •   ", issues.Take(3)) + (issues.Count > 3 ? $"   (+{issues.Count - 3} more)" : "");
        summaryShow.Visible = issues.Count > 0;
        summary.Visible = true;
        summary.Invalidate();
    }

    // ---------------------------------------------------------------- appending
    public LogEntry Append(string message)
    {
        var entry = LogClassifier.Classify(message);
        Add(entry);
        return entry;
    }

    private void Add(LogEntry entry, bool countIssue = true)
    {
        if (countIssue && entry.IsIssue)
        {
            if (entry.Level == LogLevel.Error) Errors++; else Warnings++;
            UnseenIssues++;
            if (run is not null)
            {
                if (entry.Level == LogLevel.Error) run.Errors++; else run.Warnings++;
                if (!run.Issues.Contains(entry.Text)) run.Issues.Add(entry.Text);
            }
            warnChip.Count = Warnings > 0 ? Warnings : null;
            errorChip.Count = Errors > 0 ? Errors : null;
            CountsChanged?.Invoke();
        }

        // Collapse repeating progress lines (page 1, page 2, ...) into one updating line.
        var last = entries.Count > 0 ? entries[^1] : null;
        if (entry.Key.Length > 0 && last is not null && last.Key == entry.Key)
        {
            last.Text = entry.Text; last.Repeat++;
            if (ReferenceEquals(lastVisible, last) && lastLineStart >= 0 && lastLineStart <= box.TextLength) ReplaceLastLine(last);
            Appended?.Invoke(last);
            return;
        }

        entries.Add(entry);
        if (entries.Count > MaxEntries) { entries.RemoveRange(0, 1000); Rerender(); Reset?.Invoke(); return; }
        if (Passes(entry)) AppendLine(entry);
        else if (entry.IsDetail) { hiddenDetail++; UpdateHiddenNote(); }
        Appended?.Invoke(entry);
    }

    private bool Passes(LogEntry e)
    {
        var query = search.Text.Trim();
        if (e.Level == LogLevel.Header) return levelFilter == "all" && query.Length == 0;
        if (levelFilter == "warn" && !e.IsIssue) return false;
        if (levelFilter == "error" && e.Level != LogLevel.Error) return false;
        if (Simple && query.Length == 0 && !(e.Milestone || e.IsIssue || e.Tag == "DONE")) return false;
        if (e.IsDetail && !detailSwitch.Checked && query.Length == 0 && !Simple) return false;
        if (query.Length > 0)
            return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
                e.Text.Contains(word, StringComparison.OrdinalIgnoreCase) || e.Tag.Contains(word, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private void AppendLine(LogEntry entry)
    {
        var (selStart, selLength) = (box.SelectionStart, box.SelectionLength);
        try { AppendLineCore(entry); }
        finally { if (selLength > 0) box.Select(selStart, selLength); }
    }

    private void AppendLineCore(LogEntry entry)
    {
        if (box.TextLength > 0)
        {
            box.Select(box.TextLength, 0);
            box.SelectedText = entry.Level == LogLevel.Header ? "\n\n" : "\n";
        }
        lastLineStart = box.TextLength;
        box.Select(box.TextLength, 0);
        box.SelectedRtf = LogRtf.Build([entry], timeSwitch.Checked, 9.25f, Simple);
        lastVisible = entry;
        if (followSwitch.Checked && box.SelectionLength == 0 && selectionHold == 0) ScrollToEnd();
    }

    private int selectionHold;

    private void ReplaceLastLine(LogEntry entry)
    {
        var (selStart, selLength) = (box.SelectionStart, box.SelectionLength);
        selectionHold = selLength;
        try { ReplaceLastLineCore(entry); }
        finally { if (selLength > 0 && selStart + selLength <= box.TextLength) box.Select(selStart, selLength); selectionHold = 0; }
    }

    private void ReplaceLastLineCore(LogEntry entry)
    {
        box.Select(lastLineStart, box.TextLength - lastLineStart);
        box.SelectedRtf = LogRtf.Build([entry], timeSwitch.Checked, 9.25f, Simple);
        if (followSwitch.Checked && selectionHold == 0) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        box.Select(box.TextLength, 0);
        box.ScrollToCaret();
    }

    public void Rerender()
    {
        var visible = entries.Where(Passes).ToList();
        hiddenDetail = detailSwitch.Checked ? 0 : entries.Count(e => e.IsDetail);
        box.SuspendLayout();
        box.Clear();
        lastVisible = null; lastLineStart = -1;
        if (visible.Count > 0)
        {
            box.Rtf = LogRtf.Build(visible, timeSwitch.Checked, 9.25f, Simple);
            lastVisible = visible[^1];
            var text = box.Text;
            lastLineStart = text.LastIndexOf('\n') + 1;
        }
        box.ResumeLayout();
        UpdateHiddenNote();
        ScrollToEnd();
    }

    private void UpdateHiddenNote()
    {
        var q = search.Text.Trim();
        hiddenNote.Text = q.Length > 0 ? $"Filtered by “{q}”"
            : Simple && entries.Count > 0 ? "Simple view - technical lines are hidden (turn off Simple to see everything; the log files always have it all)"
            : hiddenDetail > 0 ? $"{hiddenDetail:N0} downloader detail line(s) hidden — turn on Downloader detail to see them"
            : entries.Count == 0 ? "Nothing logged yet. Every run also writes a full log file to the Logs folder." : "";
    }

    private void SetLevel(string level)
    {
        levelFilter = level;
        allChip.Selected = level == "all"; warnChip.Selected = level == "warn"; errorChip.Selected = level == "error";
        Rerender();
    }

    public void ShowIssues() => SetLevel(Errors > 0 ? "error" : "warn");

    public void Clear()
    {
        entries.Clear(); Warnings = Errors = 0; UnseenIssues = 0; hiddenDetail = 0;
        warnChip.Count = null; errorChip.Count = null; summary.Visible = false;
        CountsChanged?.Invoke();
        Rerender();
        Reset?.Invoke();
    }

    /// <summary>Adds a control (e.g. a view toggle) to the right end of the console toolbar.</summary>
    public void AddToolbarControl(Control control)
    {
        var toolbar = Controls.OfType<WrapRow>().FirstOrDefault();
        if (toolbar is null) return;
        control.Margin = new Padding(0, 0, 8, 8);
        toolbar.Controls.Add(control);
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogDirectory) { UseShellExecute = true });
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= OnTheme; searchDebounce.Dispose(); }
        base.Dispose(disposing);
    }
}
