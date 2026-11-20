using System.Reflection;
using SpotySync;
using SpotySyncApp;

// WinForms needs an STA thread (drag & drop, clipboard, dialogs). Top-level Main is MTA, so run on one.
// Errors must never pop up a dialog on the user's desktop: they are thrown and printed instead.
if (args.Length >= 1 && args[0] == "--watch")
    return Watcher.Run(args.Length > 1 ? int.Parse(args[1]) : 90, args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "watch"));
var exitCode = 0;
var sta = new Thread(() =>
{
    try { RunSmoke(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
});
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();
return exitCode;

static void RunSmoke(string[] args)
{
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "ui-captures"));
Directory.CreateDirectory(output);
var settingsDirectory = Path.Combine(output, "portable-data");
Directory.CreateDirectory(settingsDirectory);
var settings = PortableSettings.Load(settingsDirectory);
settings.SpotifyClientId = "ui-smoke-client";
settings.MusicFolder = @"E:\Libraries\Music\Songs";
settings.SoundtrackFolder = Path.Combine(output, "soundtracks");
settings.Save();

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
using var form = new MainForm(settings) { StartupPrompts = false, LiveLibrary = false, Size = new Size(1600, 920), FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
var prepared = MockPrepared();
SetField(form, "prepared", prepared);
Invoke(form, "PopulatePlan", prepared.Plan);
form.Show();
Application.DoEvents();

Capture(form, 0, Path.Combine(output, "dashboard.png"));
Capture(form, 1, Path.Combine(output, "preview.png"));
Invoke(form, "BuildLibraryReport", false);
Capture(form, 2, Path.Combine(output, "library.png"));
Capture(form, 3, Path.Combine(output, "console.png"));
Capture(form, 4, Path.Combine(output, "settings.png"));
ValidateSettingsSearch(form, output);
ValidateSettingsScrolling(form);
MeasurePaint(form);
ValidateDashboardLayout(form);
ValidatePreviewInteractions(form);
ValidatePreviewSnapshot(form);
ValidateLiveFeatures(form, output);
if (Environment.GetEnvironmentVariable("SPOTYSYNC_FLICKER_PROBE") == "1")
{
    var brightFrames = FlickerProbe.Run(form, (PortableSettings)GetField(form, "settings"), output);
    form.Close();
    if (brightFrames > 0) throw new InvalidOperationException($"{brightFrames} bright frame(s) in the content area or bottom bar.");
    return;
}
if (Environment.GetEnvironmentVariable("SPOTYSYNC_REAL_INPUT") == "1") { RealInput.Run(form, settings); form.Location = new Point(-30000, -30000); return; }
ValidateThemes(form, output);
foreach (var size in new[] { new Size(1340, 870), new Size(1100, 720) })
{
    form.Size = size;
    Application.DoEvents(); LayoutAll(form);
    foreach (var page in new[] { 0, 1, 2, 3, 4 })
    {
        Invoke(form, "SelectPage", page);
        Application.DoEvents(); LayoutAll(form); Application.DoEvents();
        using var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(shot, form.ClientRectangle);
        shot.Save(Path.Combine(output, $"size{size.Width}-page{page}.png"), System.Drawing.Imaging.ImageFormat.Png);
        if (page == 4) ValidateSettingsFit(form, $"{size.Width}px");
    }
}
form.Size = new Size(1600, 920);
CaptureExclusionDialog(Path.Combine(output, "exclusions.png"));
CaptureDownloadDialog(Path.Combine(output, "download-links.png"));
var foundSetups = ExistingSetup.Find(settings);
Console.WriteLine("existing setups: " + (foundSetups.Count == 0 ? "none" : string.Join("; ", foundSetups.Select(f => $"{f.Folder} (login={f.HasLogin}, secret={f.ClientSecret.Length > 0})"))));
CaptureDialog(new SetupAssistant("http://127.0.0.1/callback", "", foundSetups) { AutoStart = false, Size = new Size(840, 760) }, Path.Combine(output, "setup.png"));
CaptureDialog(new SoundtrackDialog(settings.SoundtrackFolder, ["Blockheads"]) { Size = new Size(780, 600) }, Path.Combine(output, "soundtrack.png"));
Console.WriteLine(output);
form.Close();
}

static void Capture(MainForm form, int page, string path)
{
    Invoke(form, "SelectPage", page);
    if (page == 1) Invoke(form, "ShowSelectedDetails");
    if (page == 2)
    {
        var report = (LibraryReport?)GetField(form, "libraryReport") ?? throw new InvalidOperationException("Library report was not built.");
        if (report.NoLocalPlaylist.Count == 0 || report.Unplaylisted.Count != 0) throw new InvalidOperationException("Library tools misclassified the mock library.");
        Invoke(form, "ShowInsight", InsightKind.NoLocalPlaylist);
    }
    if (page == 3)
    {
        var log = (Delegate)Delegate.CreateDelegate(typeof(Action<string>), form, form.GetType().GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic)!);
        var console = GetField(form, "console");
        var startCount = ((System.Collections.ICollection)console.GetType().GetProperty("Entries")!.GetValue(console)!).Count;
        console.GetType().GetMethod("BeginRun")!.Invoke(console, ["Backing up recent Liked Songs"]);
        foreach (var line in new[] { "Reading Spotify profile…", "Liked Songs cache: read page 1 (50 tracks this refresh)…", "Liked Songs cache: read page 2 (100 tracks this refresh)…",
                     "Liked Songs cache: read page 3 (150 tracks this refresh)…", "Liked Songs index ready: 4213 tracks using 3 API call(s). Incremental refresh.",
                     "download | [download] Destination: E:/Music/a.m4a", "download | Audio download stage complete: 3 tracks.",
                     "WARNING: no matching public audio source was found for Example - Track; it remains skipped.", "MusicBee verified 3/3 imported file(s)" })
            log.DynamicInvoke(line);
        console.GetType().GetMethod("EndRun")!.Invoke(console, ["ok"]);
        var entries = (System.Collections.ICollection)console.GetType().GetProperty("Entries")!.GetValue(console)!;
        // simple view wording
        var classifier = typeof(MainForm).Assembly.GetType("SpotySyncApp.LogClassifier")!.GetMethod("Classify")!;
        string SimpleOf(string line) { var e = classifier.Invoke(null, [line])!; return (string)e.GetType().GetProperty("Simple")!.GetValue(e)!; }
        bool MilestoneOf(string line) { var e = classifier.Invoke(null, [line])!; return (bool)e.GetType().GetProperty("Milestone")!.GetValue(e)!; }
        if (SimpleOf("progress | The Midnight - Sunset | 40") != "Downloading The Midnight - Sunset  40%") throw new InvalidOperationException("progress wording: " + SimpleOf("progress | The Midnight - Sunset | 40"));
        if (SimpleOf("Recent Likes backup: 8 tracks since 2026-10-25 12:42:57Z; 1 already local; 7 to download.") != "7 new Liked Songs to download") throw new InvalidOperationException("backup wording");
        if (MilestoneOf("Spotify playlist cache: refreshed Chill (11 tracks).") || MilestoneOf("download | [download] Destination: a.m4a")) throw new InvalidOperationException("technical lines must stay out of the simple view");
        if (!MilestoneOf("Downloaded: Angel Admin - Dusqk   3/7")) throw new InvalidOperationException("downloaded songs must show in the simple view");
        var missing = classifier.Invoke(null, ["Backup verification failed: spot2yt.exe exit code 0; reported errors 1; verified 0/1 requested track(s) in the music folder."])!;
        if (missing.GetType().GetProperty("Level")!.GetValue(missing)!.ToString() != "Warn" || !SimpleOf("Backup verification failed: spot2yt.exe exit code 0; reported errors 1; verified 0/1 requested track(s).").StartsWith("1 song couldn't be downloaded"))
            throw new InvalidOperationException("an unavailable song must be a single, plain warning");
        // selecting text in the console must survive new lines arriving
        var box = (RichTextBox)console.GetType().GetField("box", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(console)!;
        box.Select(0, 6);
        log.DynamicInvoke("Reading Spotify profile…");
        if (box.SelectionLength != 6) throw new InvalidOperationException("a new console line cleared the user's selection");
        if (entries.Count - startCount != 10) throw new InvalidOperationException($"Console did not collapse progress lines (expected 9 entries, got {entries.Count - startCount}).");
    }
    Application.DoEvents();
    LayoutAll(form);
    Application.DoEvents();
    if (page == 1) ValidatePreview(form);
    if (page == 0)
    {
        foreach (var text in new[] { "Back up recent Liked Songs", "Download Spotify / YouTube links...", "Build sync preview", "Apply plan", "Run everything", "Save soundtrack", "Build preview" })
            if (!Descendants(form).Any(control => control.Text == text && control.Visible))
                throw new InvalidOperationException("Dashboard action is missing: " + text);
    }
    using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
    form.DrawToBitmap(bitmap, form.ClientRectangle);
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
}

static void ValidateSettingsSearch(MainForm form, string output)
{
    Invoke(form, "SelectPage", 4);
    var view = GetField(form, "settingsView");
    view.GetType().GetMethod("Find")!.Invoke(view, ["removal"]);
    Application.DoEvents(); LayoutAll(form); Application.DoEvents();
    var rows = Descendants(form).Where(c => c.GetType().Name == "SettingRow").ToList();
    var shown = rows.Count(r => !(bool)r.GetType().GetProperty("Filtered")!.GetValue(r)!);
    if (rows.Count < 40 || shown == 0 || shown > 8) throw new InvalidOperationException($"Settings search is not filtering (rows {rows.Count}, shown {shown}).");
    using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
    {
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(Path.Combine(output, "settings-search.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
    view.GetType().GetMethod("Find")!.Invoke(view, ["zzzz-no-such-setting"]);
    if (rows.Any(r => !(bool)r.GetType().GetProperty("Filtered")!.GetValue(r)!)) throw new InvalidOperationException("Nonsense search still shows rows.");
    view.GetType().GetMethod("Find")!.Invoke(view, [""]);
    if (rows.Any(r => (bool)r.GetType().GetProperty("Filtered")!.GetValue(r)!)) throw new InvalidOperationException("Clearing search did not restore every row.");
}

static void ValidateSettingsFit(MainForm form, string where)
{
    // every Settings section must be tall enough for its rows (nothing cut off)
    foreach (var group in Descendants(form).Where(c => c.GetType().Name == "SettingGroup" && c.Visible))
    {
        var rows = (Control)group.GetType().GetProperty("Rows")!.GetValue(group)!;
        var inner = (Padding)group.GetType().GetProperty("Inner")!.GetValue(group)!;
        if (rows.Bottom > group.Height - inner.Bottom + 2)
            throw new InvalidOperationException($"Settings section '{group.GetType().GetProperty("Title")!.GetValue(group)}' is cut off at {where} (rows end {rows.Bottom}, section {group.Height}).");
    }
}

static void ValidateSettingsScrolling(MainForm form)
{
    ValidateSettingsFit(form, "1600px");
    Invoke(form, "SelectPage", 4);
    Application.DoEvents(); LayoutAll(form); Application.DoEvents();
    var view = GetField(form, "settingsView");
    var groups = (System.Collections.IList)view.GetType().GetProperty("Groups")!.GetValue(view)!;
    int Offset() => (int)view.GetType().GetProperty("ScrollOffset")!.GetValue(view)!;
    var last = groups[groups.Count - 1]!;
    var rail = (Control)last.GetType().GetProperty("RailItem")!.GetValue(last)!;
    typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rail, [EventArgs.Empty]);
    WaitFor(() => Offset() > 0 && Settled(Offset));
    var afterRail = Offset();
    if (afterRail <= 0) throw new InvalidOperationException("Clicking a Settings category did not scroll to it.");
    var first = groups[0]!;
    var firstRail = (Control)first.GetType().GetProperty("RailItem")!.GetValue(first)!;
    typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(firstRail, [EventArgs.Empty]);
    WaitFor(() => Offset() == 0);
    if (Offset() != 0) throw new InvalidOperationException("Clicking the first Settings category did not scroll back to the top.");
    var scroll = (Control)view.GetType().GetField("scroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    SendMessage(scroll.Handle, 0x020A, (IntPtr)((-120 * 3) << 16), IntPtr.Zero);
    WaitFor(() => Offset() > 0 && Settled(Offset));
    if (Offset() <= 0) throw new InvalidOperationException("Mouse wheel does not scroll the Settings page.");
    for (var i = 0; i < 40; i++) SendMessage(scroll.Handle, 0x020A, (IntPtr)((-120 * 5) << 16), IntPtr.Zero);
    var lastRail = (Control)groups[groups.Count - 1]!.GetType().GetProperty("RailItem")!.GetValue(groups[groups.Count - 1])!;
    bool LastSelected() => (bool)lastRail.GetType().GetProperty("Selected")!.GetValue(lastRail)!;
    WaitFor(() => LastSelected() && Settled(Offset), 4000); // smooth scrolling may still be gliding
    if (!LastSelected())
        throw new InvalidOperationException("Scrolling to the bottom of Settings did not highlight the last section.");
    Console.WriteLine($"settings scrolling ok (rail → {afterRail}px, wheel → {Offset()}px)");
}

static void ValidateDashboardLayout(MainForm form)
{
    Invoke(form, "SelectPage", 0);
    Application.DoEvents(); LayoutAll(form);
    var settings = (PortableSettings)GetField(form, "settings");
    var activity = Descendants(form).First(c => c.Name == "card-activity");
    var before = activity.Width;
    if (!settings.UiDashboardCards.Equals("") || !((System.Collections.IList)GetField(form, "dashCards")).Cast<object>().Any())
        throw new InvalidOperationException("Default layout expected");
    var library = Descendants(form).First(c => c.Name == "card-library");
    if (library.Visible) throw new InvalidOperationException("Library tools should be hidden in the default layout.");
    Invoke(form, "SetDashCardSize", "activity", (int?)2, (int?)440);
    WaitFor(() => activity.Height == 440 && activity.Width > before, 2000); // modules glide into place
    if (activity.Width <= before || activity.Height != 440) throw new InvalidOperationException($"Resizing a module did not change its size ({activity.Width}x{activity.Height}).");
    if (!settings.UiDashboardCards.Contains("activity:2:440")) throw new InvalidOperationException("Module size was not saved: " + settings.UiDashboardCards);
    Invoke(form, "MoveDashCard", "soundtrack", -1);
    if (!settings.UiDashboardCards.StartsWith("soundtrack:")) throw new InvalidOperationException("Moving a module did not reorder it: " + settings.UiDashboardCards);
    settings.UiDashboardCards = "";
    Invoke(form, "ApplyDashboardLayout");
    Application.DoEvents(); LayoutAll(form);

    // Simulated mouse gesture through the real code path: press on the grip, drag, release.
    var module = ((System.Collections.IList)GetField(form, "dashCards")).Cast<object>().First(m => (string)m.GetType().GetProperty("Id")!.GetValue(m)! == "actions");
    var card = (Control)module.GetType().GetProperty("Card")!.GetValue(module)!;
    var start = card.PointToScreen(new Point(card.Width - 10, card.Height - 10));
    var cursor = start; var down = true;
    SetField(form, "gestureCursor", (Func<Point>)(() => cursor));
    SetField(form, "gestureButtonDown", (Func<bool>)(() => down));
    var begin = form.GetType().GetMethod("BeginCardGesture", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var tick = form.GetType().GetMethod("GestureTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    begin.Invoke(form, [module, new MouseEventArgs(MouseButtons.Left, 1, card.Width - 10, card.Height - 10, 0)]);
    cursor = new Point(start.X + 40, start.Y + 120); tick.Invoke(form, null);
    down = false; tick.Invoke(form, null);
    WaitFor(() => card.Height >= 400, 2000); // no forced layout: the app itself must re-lay out the grid
    if (!settings.UiDashboardCards.Contains("actions:") || card.Height < 400)
        throw new InvalidOperationException($"Grip resize did not apply (height {card.Height}, layout '{settings.UiDashboardCards}').");

    // Drag the Library module onto the first slot by its title.
    var lib = ((System.Collections.IList)GetField(form, "dashCards")).Cast<object>().First(m => (string)m.GetType().GetProperty("Id")!.GetValue(m)! == "preview");
    var libCard = (Control)lib.GetType().GetProperty("Card")!.GetValue(lib)!;
    cursor = libCard.PointToScreen(new Point(60, 20)); down = true;
    begin.Invoke(form, [lib, new MouseEventArgs(MouseButtons.Left, 1, 60, 20, 0)]);
    cursor = card.PointToScreen(new Point(20, card.Height / 2)); tick.Invoke(form, null); tick.Invoke(form, null);
    down = false; tick.Invoke(form, null);
    if (!settings.UiDashboardCards.StartsWith("preview:"))
        throw new InvalidOperationException("Dragging a module by its title did not move it: " + settings.UiDashboardCards);
    SetField(form, "gestureCursor", (Func<Point>)(() => Cursor.Position));
    SetField(form, "gestureButtonDown", (Func<bool>)(() => (Control.MouseButtons & MouseButtons.Left) != 0));
    settings.UiDashboardCards = "";
    Invoke(form, "ApplyDashboardLayout");
    Console.WriteLine("dashboard resize/move ok (menu, grip resize and title drag)");
}

static void WaitFor(Func<bool> condition, int timeoutMs = 2000)
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    while (!condition() && watch.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); Thread.Sleep(5); }
}

static bool Settled(Func<int> value)
{
    var a = value();
    for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(8); }
    return value() == a;
}

static void MeasurePaint(MainForm form)
{
    foreach (var page in new[] { 0, 1, 4 })
    {
        Invoke(form, "SelectPage", page);
        Application.DoEvents(); LayoutAll(form); form.Refresh();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) form.Refresh();
        var full = watch.Elapsed.TotalMilliseconds / 10;
        var nav = Descendants(form).First(c => c.GetType().Name == "NavItem");
        watch.Restart();
        for (var i = 0; i < 50; i++) { nav.Invalidate(); nav.Update(); }
        var hover = watch.Elapsed.TotalMilliseconds / 50;
        Console.WriteLine($"page {page}: full repaint {full:0.0} ms, hover repaint {hover:0.00} ms");
        if (hover > 8) throw new InvalidOperationException($"Hover repaint too slow on page {page}: {hover:0.0} ms");
    }
}

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

static void ValidateThemes(MainForm form, string output)
{
    var settings = (PortableSettings)GetField(form, "settings");
    foreach (var id in new[] { "duo_crimson_cobalt", "duo_synth_sunset", "qd_ion_cyan", "spotysync" })
    {
        settings.UiTheme = id;
        Invoke(form, "ReapplyTheme");
        Invoke(form, "SelectPage", 0);
        Application.DoEvents(); LayoutAll(form); Application.DoEvents();
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(Path.Combine(output, $"theme-{id}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
}

static void CaptureDialog(Form dialog, string path)
{
    using (dialog)
    {
        dialog.FormBorderStyle = FormBorderStyle.None; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-30000, -30000);
        dialog.Show(); Application.DoEvents(); LayoutAll(dialog);
        using var bitmap = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height);
        dialog.DrawToBitmap(bitmap, dialog.ClientRectangle);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        dialog.Close();
    }
}

static void CaptureDownloadDialog(string path)
{
    using var dialog = new DownloadLinksDialog
    {
        Size = new Size(720, 460), FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000)
    };
    dialog.Show();
    Application.DoEvents();
    LayoutAll(dialog);
    using var bitmap = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height);
    dialog.DrawToBitmap(bitmap, dialog.ClientRectangle);
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    dialog.Close();
}

static void ValidatePreview(MainForm form)
{
    foreach (var text in new[] { "Track-level changes  •  drag the divider to resize", "Expand details", "Copy details", "Cancel", "Apply plan", "Rebuild preview" })
    {
        var control = Descendants(form).FirstOrDefault(candidate => candidate.Text == text && candidate.Visible);
        if (control is null || !control.Visible || control.Parent is null ||
            control.Bounds.Top + control.Bounds.Height / 2 >= control.Parent.ClientSize.Height)
            throw new InvalidOperationException($"Visible UI text is clipped: {text}");
    }

    Invoke(form, "SelectPlanRow", "1st Playlist"); // rows are ordered by changes, so pick the playlist explicitly
    var details = (RichTextBox)GetField(form, "details");
    if (!details.Text.Contains("The Midnight - Track 1", StringComparison.Ordinal) ||
        details.Text.Contains("Unknown Spotify track", StringComparison.Ordinal))
        throw new InvalidOperationException("Preview did not resolve Spotify IDs to readable track names.");

    var grid = (DataGridView)GetField(form, "planGrid");
    if (grid.AllowUserToResizeColumns || !grid.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[0].Value?.ToString() == "1st Playlist"))
        throw new InvalidOperationException("Playlist columns are resizable or MusicBee folder names were not condensed.");
}

// Dashboard → playlist navigation, the in-module overview, smooth grid scrolling, the animated
// expand/collapse and the "applied" state of a kept preview.
static void ValidatePreviewInteractions(MainForm form)
{
    var details = (RichTextBox)GetField(form, "details");
    Invoke(form, "SelectPage", 1);
    Invoke(form, "SelectPlanRow", "1st Playlist");
    Invoke(form, "SelectPage", 0);
    Invoke(form, "SelectPage", 1);
    Invoke(form, "SelectPlanRow", "Playlist 7");
    Application.DoEvents();
    if (!details.Text.StartsWith("Playlist 7", StringComparison.Ordinal))
        throw new InvalidOperationException("Opening a playlist showed the previously selected playlist's details.");
    var search = (Control)GetField(form, "previewSearch");
    search.Text = "1st";
    Invoke(form, "SelectPlanRow", "Playlist 3");
    if (!details.Text.StartsWith("Playlist 3", StringComparison.Ordinal) || search.Text.Length != 0)
        throw new InvalidOperationException("A playlist hidden by the preview filter could not be opened.");

    // smooth grid: full-height grid inside a scrolling host; the wheel moves it by pixels
    var grid = (DataGridView)GetField(form, "planGrid");
    var host = (ScrollableControl)grid.Parent!;
    LayoutAll(form); Application.DoEvents();
    if (grid.Height < grid.Rows.GetRowsHeight(DataGridViewElementStates.Visible))
        throw new InvalidOperationException("The preview grid is not laid out at full height inside its scroll host.");
    if (grid.Height > host.ClientSize.Height)
    {
        host.AutoScrollPosition = Point.Empty;
        host.GetType().GetMethod("Wheel")!.Invoke(host, [-120]);
        Pump(() => false, 0.6);
        if (host.AutoScrollPosition.Y == 0) throw new InvalidOperationException("Mouse wheel did not scroll the preview grid.");
        host.AutoScrollPosition = Point.Empty;
    }

    // expand details / show playlist list (animated)
    var split = (SplitContainer)GetField(form, "previewSplit");
    Invoke(form, "ToggleDetails");
    Pump(() => GetField(form, "splitAnimation") is null, 2);
    if (!split.Panel1Collapsed) throw new InvalidOperationException("Expand details did not collapse the playlist list.");
    Invoke(form, "ToggleDetails");
    Pump(() => GetField(form, "splitAnimation") is null, 2);
    if (split.Panel1Collapsed || split.SplitterDistance < 140 || split.Panel1MinSize != 150)
        throw new InvalidOperationException($"Show playlist list did not restore the list (distance {split.SplitterDistance}).");

    // dashboard module overview of one playlist
    Invoke(form, "SelectPage", 0);
    Invoke(form, "ShowDashPlan", "Playlist 2", false);
    var overviewSummary = (Label)GetField(form, "dashDetailSummary");
    var overviewRows = (IReadOnlyList<InsightRow>)GetField(form, "dashDetailTable").GetType().GetProperty("Items")!.GetValue(GetField(form, "dashDetailTable"))!;
    if (!((Control)GetField(form, "dashPlanDetailView")).Visible || !overviewSummary.Text.StartsWith("Playlist 2", StringComparison.Ordinal) ||
        !overviewRows.Any(r => r.Detail == "Local +"))
        throw new InvalidOperationException("The dashboard Sync preview module did not show the playlist overview.");
    Invoke(form, "ShowDashPlanList", false);
    if (!((Control)GetField(form, "dashPlanListView")).Visible) throw new InvalidOperationException("The module did not return to the playlist list.");

    // an applied plan stays visible but can't be applied twice
    SetField(form, "planAppliedAt", DateTime.Now);
    Invoke(form, "UpdatePreviewState");
    var apply = (Button)GetField(form, "previewApplyButton");
    if (apply.Enabled || apply.Text != "Applied" || GetField(form, "prepared") is null)
        throw new InvalidOperationException("An applied preview was discarded or could be applied again.");
    SetField(form, "planAppliedAt", null!);
    Invoke(form, "UpdatePreviewState");
    if (!apply.Enabled || apply.Text != "Apply plan") throw new InvalidOperationException("Apply button did not come back for a fresh plan.");
    Invoke(form, "SelectPlanRow", "1st Playlist");
}

static void ValidatePreviewSnapshot(MainForm form)
{
    var settings = (PortableSettings)GetField(form, "settings");
    var path = Path.Combine(settings.DataDirectory, "last-preview.json");
    if (File.Exists(path)) File.Delete(path);
    var original = (PreparedSync)GetField(form, "prepared");
    Invoke(form, "SavePreviewSnapshot", false);
    WaitFor(() => File.Exists(path), 5000);
    if (!File.Exists(path)) throw new InvalidOperationException("The sync preview was not saved for the next launch.");
    SetField(form, "prepared", null!);
    Invoke(form, "RestorePreviewSnapshot");
    var restored = GetField(form, "prepared") as PreparedSync ?? throw new InvalidOperationException("The saved sync preview was not restored.");
    if (restored.Plan.Playlists.Count != original.Plan.Playlists.Count || restored.Library.Count != original.Library.Count)
        throw new InvalidOperationException("The restored sync preview differs from the saved one.");
    var apply = (Control)GetField(form, "applyButton");
    if (apply.Enabled) throw new InvalidOperationException("A preview restored from a previous session must not be applyable.");
    Console.WriteLine($"preview snapshot ok ({restored.Plan.Playlists.Count} playlists, {new FileInfo(path).Length / 1024} KB)");
    // back to a fresh, applyable preview for the remaining checks
    SetField(form, "previewRestoredAt", null!);
    Invoke(form, "UpdatePreviewState");
}

static void Pump(Func<bool> done, double seconds)
{
    var until = DateTime.Now.AddSeconds(seconds);
    while (DateTime.Now < until && !done()) { Application.DoEvents(); Thread.Sleep(5); }
    Application.DoEvents();
}

static IEnumerable<Control> Descendants(Control root)
{
    foreach (Control child in root.Controls)
    {
        yield return child;
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

static void CaptureExclusionDialog(string path)
{
    using var dialog = new ExclusionReviewDialog("1st Playlist",
    [
        new TrackReviewItem { Kind = "Add to Spotify", Artist = "Men Without Hats", Title = "The Safety Dance (Extended Version)" },
        new TrackReviewItem { Kind = "Protected • MusicBee-only/unmatched", Artist = "Ariana Grande", Title = "Them Changes" },
        new TrackReviewItem { Kind = "Protected • Spotify-only/no local file", Artist = "Example Artist", Title = "Example Track" }
    ])
    {
        Size = new Size(820, 620), FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000)
    };
    dialog.Show();
    Application.DoEvents();
    LayoutAll(dialog);
    using var bitmap = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height);
    dialog.DrawToBitmap(bitmap, dialog.ClientRectangle);
    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    dialog.Close();
}

static void LayoutAll(Control control)
{
    control.CreateControl();
    control.PerformLayout();
    foreach (Control child in control.Controls) LayoutAll(child);
}

static void SetField(object target, string name, object value) =>
    target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

static object GetField(object target, string name) =>
    target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

static object? Invoke(object target, string name, params object[] arguments) =>
    target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).First(m => m.Name == name && m.GetParameters().Length == arguments.Length).Invoke(target, arguments);

static PreparedSync MockPrepared()
{
    var tracks = Enumerable.Range(1, 80).Select(index => new TrackInfo
    {
        SpotifyId = index.ToString("D22"), Title = "Track " + index,
        Artists = [index % 3 == 0 ? "Glass Animals" : index % 3 == 1 ? "The Midnight" : "CHVRCHES"],
        Album = "Sample Album", DurationMs = 180000 + index * 100
    }).ToList();
    var locals = tracks.Select((track, index) => new LocalTrack
    {
        Path = $@"E:\Libraries\Music\Songs\{track.Artists[0]} - {track.Title}.mp3",
        Artist = track.Artists[0], Title = track.Title, Album = track.Album, DurationMs = track.DurationMs,
        SpotifyId = track.SpotifyId
    }).ToList();
    var plans = new List<PlaylistPlan>();
    for (var index = 0; index < 18; index++)
    {
        var plan = new PlaylistPlan
        {
            Name = index == 0 ? "1st Playlist" : "Playlist " + (index + 1),
            LocalSourceName = index == 0 ? @"Old Playlists\1st Playlist" : "Playlist " + (index + 1),
            SpotifySourceName = index == 0 ? "1st Playlist" : "Playlist " + (index + 1),
            HasBaseline = index > 4
        };
        if (index % 5 == 0) plan.SpotifyAddIds.AddRange(tracks.Skip(index).Take(9).Select(x => x.SpotifyId));
        if (index % 5 == 1) plan.LocalAddFiles.AddRange(locals.Skip(index).Take(7).Select(x => x.Path));
        if (index % 5 == 2) { plan.SpotifyRemoveIds.Add(tracks[index].SpotifyId); plan.LocalRemoveFiles.Add(locals[index + 1].Path); }
        if (index % 5 == 3) plan.PlatformSpecific.AddRange(tracks.Skip(index).Take(4).Select(x => "Spotify-only/no local file: " + x.DisplayName));
        if (index == 14) { plan.IsBlocked = true; plan.BlockReason = "example safety cap exceeded"; }
        if (index == 0) plan.ReviewItems.Add(new TrackReviewItem { Kind = "Add to Spotify", Artist = "The Midnight", Title = "Track 1" });
        plans.Add(plan);
    }
    return new PreparedSync
    {
        Plan = new SyncPlan { SpotifyUserId = "ui-smoke", Playlists = plans },
        Library = locals,
        Liked = new LikedTrackCache { SpotifyUserId = "ui-smoke", Tracks = tracks },
        SpotifyPlaylists = [new PlaylistInfo { Id = "sample", Name = "1st Playlist", Tracks = tracks }]
    };
}

// Dashboard drag into a column / minimize, the full dashboard console, song names in the status bar, Library
// selection, the Console split view, the live download graph and its full-screen window.
static void ValidateLiveFeatures(MainForm form, string output)
{
    var settings = (PortableSettings)GetField(form, "settings");
    var modules = ((System.Collections.IList)GetField(form, "dashCards")).Cast<object>().ToList();
    object Module(string id) => modules.First(m => (string)m.GetType().GetProperty("Id")!.GetValue(m)! == id);
    Control CardOf(string id) => (Control)Module(id).GetType().GetProperty("Card")!.GetValue(Module(id))!;
    var grid = (Control)GetField(form, "dashGrid");

    // 1. three modules side by side: drag the middle one under the left one
    settings.UiDashboardCards = "actions:1:320:0:160,soundtrack:1:320:1:160,console:1:320:2:160,activity:3:320:0:600,-preview:1:480,-library:2:420";
    Invoke(form, "SelectPage", 0);
    Invoke(form, "ApplyDashboardLayout");
    Application.DoEvents(); LayoutAll(form);
    var left = CardOf("actions"); var middle = CardOf("soundtrack");
    WaitFor(() => middle.Left > left.Right && middle.Top == left.Top, 2000);
    if (!(middle.Left > left.Right && middle.Top == left.Top)) throw new InvalidOperationException($"Setup: modules are not side by side ({left.Bounds} / {middle.Bounds}).");
    var cursor = middle.PointToScreen(new Point(60, 20)); var down = true;
    SetField(form, "gestureCursor", (Func<Point>)(() => cursor));
    SetField(form, "gestureButtonDown", (Func<bool>)(() => down));
    var begin = form.GetType().GetMethod("BeginCardGesture", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var tick = form.GetType().GetMethod("GestureTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    begin.Invoke(form, [Module("soundtrack"), new MouseEventArgs(MouseButtons.Left, 1, 60, 20, 0)]);
    cursor = grid.PointToScreen(new Point(left.Left + 60, left.Bottom + 40 + 20));
    tick.Invoke(form, null); tick.Invoke(form, null); tick.Invoke(form, null);
    down = false; tick.Invoke(form, null);
    WaitFor(() => middle.Left == left.Left && middle.Top > left.Bottom, 2000);
    if (middle.Left != left.Left || middle.Top <= left.Bottom)
        throw new InvalidOperationException($"Dragging a module under another did not put it there ({middle.Bounds} vs {left.Bounds}); layout '{settings.UiDashboardCards}'.");
    if (!settings.UiDashboardCards.Contains("soundtrack:1:320:0:")) throw new InvalidOperationException("The new column was not saved: " + settings.UiDashboardCards);
    SetField(form, "gestureCursor", (Func<Point>)(() => Cursor.Position));
    SetField(form, "gestureButtonDown", (Func<bool>)(() => (Control.MouseButtons & MouseButtons.Left) != 0));
    using (var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(shot, form.ClientRectangle); shot.Save(Path.Combine(output, "dashboard-dragged.png"), System.Drawing.Imaging.ImageFormat.Png); }

    // 2. minimize / restore
    var consoleCard = CardOf("console");
    var minimizedHeight = (int)consoleCard.GetType().GetProperty("MinimizedHeight")!.GetValue(consoleCard)!;
    Invoke(form, "ToggleDashCardMinimized", "console");
    WaitFor(() => consoleCard.Height == minimizedHeight, 2000);
    if (consoleCard.Height != minimizedHeight || !settings.UiDashboardCards.Contains("~console")) throw new InvalidOperationException($"Minimize did not collapse the module ({consoleCard.Height}, '{settings.UiDashboardCards}').");
    Invoke(form, "ToggleDashCardMinimized", "console");
    WaitFor(() => consoleCard.Height == 320, 2000);
    if (consoleCard.Height != 320) throw new InvalidOperationException("Restore did not bring the module back.");

    // 3. the dashboard console shows everything, including downloader detail lines
    var console = GetField(form, "console");
    var id = 1.ToString("D22");
    var entry = console.GetType().GetMethod("Append")!.Invoke(console, ["download | Processing query: https://open.spotify.com/track/" + id])!;
    Application.DoEvents();
    var feed = (RichTextBox)GetField(form, "dashFeed");
    if (!feed.Text.Contains("Processing query: https://open.spotify.com/track/" + id)) throw new InvalidOperationException("The dashboard console left out a downloader detail line.");

    // 4. status bar: title, artist(s), then the link
    var line = (string)Invoke(form, "StatusLine", entry)!;
    if (!line.Contains("Track 1 — The Midnight  ·  https://open.spotify.com/track/" + id)) throw new InvalidOperationException("Status line lacks the song name before the link: " + line);

    // 5. Library: nothing ticked until you tick; Select all ⇄ Deselect
    Invoke(form, "ShowInsight", InsightKind.NoLocalPlaylist);
    int Ticked() => ((System.Collections.ICollection)Invoke(form, "TickedInsights")!).Count;
    var selectAll = (Button)GetField(form, "librarySelectAll");
    if (Ticked() != 0 || selectAll.Text != "Select all") throw new InvalidOperationException("Library rows were ticked by default.");
    Invoke(form, "ToggleSelectAll");
    if (Ticked() == 0 || !selectAll.Text.StartsWith("Deselect")) throw new InvalidOperationException("Select all did not tick the rows / turn into Deselect.");
    Invoke(form, "ToggleSelectAll");
    if (Ticked() != 0 || selectAll.Text != "Select all") throw new InvalidOperationException("Deselect did not clear the ticks.");

    // 6. Console page split with the download graph
    Invoke(form, "SelectPage", 3);
    Invoke(form, "SetConsoleSplit", true);
    Application.DoEvents(); LayoutAll(form);
    var split = (SplitContainer)GetField(form, "consoleSplit");
    if (split.Panel2Collapsed || ((Control)GetField(form, "consoleGraph")).Width < 200) throw new InvalidOperationException("The Console split view did not show the graph.");

    // 7. a simulated download run drives the graph (a growing .part file + a finished song)
    var monitor = GetField(form, "downloadMonitor");
    var music = Path.Combine(output, "graph-music");
    if (Directory.Exists(music)) Directory.Delete(music, true);
    Directory.CreateDirectory(music);
    monitor.GetType().GetMethod("Start")!.Invoke(monitor, ["Downloading test songs", music, Array.Empty<string>(), form]);
    var note = monitor.GetType().GetMethod("NoteLog")!;
    note.Invoke(monitor, ["Download queue: 3 song(s)."]);
    for (var i = 1; i <= 3; i++) note.Invoke(monitor, ["Processing query: https://open.spotify.com/track/" + i.ToString("D22")]);
    var part = Path.Combine(music, "abc.webm.part");
    using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
    {
        var chunk = new byte[256 * 1024];
        for (var k = 0; k < 8; k++) { stream.Write(chunk); stream.Flush(); var until = DateTime.Now.AddMilliseconds(260); while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); } }
    }
    File.WriteAllBytes(Path.Combine(music, "The Midnight - Track 1.opus"), new byte[3 * 1024 * 1024]);
    var settleUntil = DateTime.Now.AddSeconds(2.5);
    while (DateTime.Now < settleUntil) { Application.DoEvents(); Thread.Sleep(20); }
    T Prop<T>(string name) => (T)monitor.GetType().GetProperty(name)!.GetValue(monitor)!;
    if (Prop<long>("Downloaded") < 1024 * 1024) throw new InvalidOperationException($"The graph did not measure the growing download ({Prop<long>("Downloaded")} bytes).");
    if (Prop<int>("Done") != 1 || Prop<int?>("Target") != 3) throw new InvalidOperationException($"Song counts are wrong (done {Prop<int>("Done")}, queue {Prop<int?>("Target")}).");
    if (!Prop<string>("CurrentName").Contains("Track 2")) throw new InvalidOperationException("The current song is wrong: " + Prop<string>("CurrentName"));
    using (var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(shot, form.ClientRectangle); shot.Save(Path.Combine(output, "console-split-live.png"), System.Drawing.Imaging.ImageFormat.Png); }
    Invoke(form, "SetConsoleSplit", false);
    Invoke(form, "SelectPage", 0);
    Application.DoEvents(); LayoutAll(form);
    using (var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(shot, form.ClientRectangle); shot.Save(Path.Combine(output, "dashboard-live.png"), System.Drawing.Imaging.ImageFormat.Png); }

    // full-screen window (opened off-screen with the test form) and closed again
    Invoke(form, "OpenActivityFullScreen");
    var window = (Form)GetField(form, "activityWindow");
    Application.DoEvents(); LayoutAll(window); Application.DoEvents();
    using (var shot = new Bitmap(Math.Max(1, window.ClientSize.Width), Math.Max(1, window.ClientSize.Height))) { window.DrawToBitmap(shot, window.ClientRectangle); shot.Save(Path.Combine(output, "activity-fullscreen.png"), System.Drawing.Imaging.ImageFormat.Png); }
    window.Close();
    monitor.GetType().GetMethod("Stop")!.Invoke(monitor, null);
    try { Directory.Delete(music, true); } catch { }

    settings.UiDashboardCards = "";
    Invoke(form, "ApplyDashboardLayout");
    Console.WriteLine("live features ok (drag into column, minimize, full console, status names, library select, console split, live graph, full screen)");
}
