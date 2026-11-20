namespace SpotySyncApp;

/// <summary>
/// The complete log, unfiltered (every line including downloader detail, with time and tag), following a
/// <see cref="ConsoleView"/> live. Used by the dashboard Console module. New lines are appended in place; it only
/// scrolls along while you are at the bottom and nothing is selected, so you can read back while a run goes on.
/// </summary>
internal sealed class LogFeed : SmoothRichText
{
    private const float FontSize = 8.75f;
    private const int MaxLines = 6000;
    private readonly ConsoleView source;
    private LogEntry? last;
    private int lastStart = -1, lines;
    private bool stale = true;

    public LogFeed(ConsoleView source)
    {
        this.source = source;
        ReadOnly = true; BorderStyle = BorderStyle.None; ScrollBars = RichTextBoxScrollBars.Vertical;
        WordWrap = true; DetectUrls = false; HideSelection = false;
        Ui.DarkScrollbars(this);
        source.Appended += OnAppended;
        source.Reset += MarkStale;
        Theme.Changed += MarkStale;
        HandleCreated += (_, _) => MarkStale();
        KeyDown += (_, e) => ConsoleView.CopyKeys(this, e);
        var menu = Ui.Menu();
        menu.Items.Add("Copy selection", null, (_, _) => { if (SelectionLength > 0) Clipboard.SetText(SelectedText); });
        menu.Items.Add("Copy everything", null, (_, _) => { if (TextLength > 0) Clipboard.SetText(Text); });
        ContextMenuStrip = menu;
    }

    private void MarkStale()
    {
        stale = true;
        if (IsHandleCreated && Visible) Rebuild();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && stale) Rebuild();
    }

    private bool AtBottom => SelectionLength == 0 && ScrollY >= MaxScroll - 24;

    public void Rebuild()
    {
        if (!IsHandleCreated) return;
        stale = false;
        BackColor = Theme.P.Panel;
        var all = source.Entries;
        BeginUpdate();
        try
        {
            Rtf = all.Count == 0
                ? LogRtf.Build([new LogEntry { Text = "Nothing has run yet in this session. Everything SpotySync does shows up here in full.", Level = LogLevel.Info, Tag = "APP" }], false, FontSize)
                : LogRtf.Build(all, true, FontSize);
            last = all.Count > 0 ? all[^1] : null;
            lines = all.Count;
            lastStart = Text.LastIndexOf('\n') + 1;
            Select(TextLength, 0);
            ScrollToCaret();
        }
        finally { EndUpdate(); }
    }

    private void OnAppended(LogEntry entry)
    {
        if (!IsHandleCreated || stale) return;
        if (!Visible) { stale = true; return; } // catch up in one go when shown again
        var follow = AtBottom;
        var (selStart, selLength) = (SelectionStart, SelectionLength);
        var scroll = ScrollY;
        BeginUpdate();
        try
        {
            if (ReferenceEquals(entry, last) && lastStart >= 0 && lastStart <= TextLength)
            {
                // a collapsing progress line (page 1, page 2 …) updates in place
                Select(lastStart, TextLength - lastStart);
                SelectedRtf = LogRtf.Build([entry], true, FontSize);
            }
            else
            {
                if (last is null && lines == 0) { Clear(); }
                else
                {
                    Select(TextLength, 0);
                    SelectedText = entry.Level == LogLevel.Header ? "\n\n" : "\n";
                }
                lastStart = TextLength;
                Select(TextLength, 0);
                SelectedRtf = LogRtf.Build([entry], true, FontSize);
                last = entry;
                if (++lines > MaxLines) { stale = true; Rebuild(); return; }
            }
            if (follow) { Select(TextLength, 0); ScrollToCaret(); }
            else
            {
                if (selLength > 0 && selStart + selLength <= TextLength) Select(selStart, selLength);
                else Select(selStart, 0);
                ScrollY = scroll;
            }
        }
        finally { EndUpdate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Appended -= OnAppended;
            source.Reset -= MarkStale;
            Theme.Changed -= MarkStale;
        }
        base.Dispose(disposing);
    }
}
