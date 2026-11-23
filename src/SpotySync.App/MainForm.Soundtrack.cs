using System.Diagnostics;
using SpotySync;

namespace SpotySyncApp;

public sealed partial class MainForm
{
    private readonly SoundtrackList soundtrackList = new();

    private Control BuildSoundtrackCard()
    {
        var body = new Panel { BackColor = Color.Transparent, Dock = DockStyle.Fill };
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 94, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); top.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var copy = Ui.Label("Download a whole game/film soundtrack or playlist into its own folder, initialize it in MusicBee and add it to your Folders tab.", "muted", Fonts.Body(9.5f), autoSize: false);
        copy.Dock = DockStyle.Fill;
        var save = RoundedButton.Make("Save soundtrack", ButtonKind.Primary, Glyph.Music, async (_, _) => await SaveSoundtrackAsync());
        save.Dock = DockStyle.Fill; save.AutoSize = false; save.Margin = new Padding(0, 4, 0, 4);
        busyButtons.Add(save);
        tips.SetToolTip(save, "Spotify, YouTube or other playlist/album links → Music\\<Name>\\ — kept out of the Songs folder.");
        top.Controls.Add(copy, 0, 0); top.Controls.Add(save, 0, 1);
        soundtrackList.Dock = DockStyle.Fill;
        soundtrackList.Clicked += async item =>
        {
            if (item.Missing > 0) await RepairSoundtrackAsync(item.Name);
            else if (Directory.Exists(item.Path)) OpenPath(item.Path);
        };
        body.Controls.Add(soundtrackList); body.Controls.Add(top);
        // Fit the description to its wrapped height; drop it when the module is too short, so the button and the
        // soundtrack list always fit (it used to cut the list off at "YOUR" in a narrow or short module).
        var fitting = false;
        void Fit()
        {
            if (fitting || body.Width <= 0) return;
            fitting = true;
            try
            {
                var textH = TextRenderer.MeasureText(copy.Text, copy.Font, new Size(Math.Max(40, body.Width), 0), TextFormatFlags.WordBreak).Height + 6;
                var showCopy = body.Height - (textH + 46) >= 64; // room left for the list header and a row
                copy.Visible = showCopy;
                top.RowStyles[0] = new RowStyle(SizeType.Absolute, showCopy ? textH : 0);
                var height = (showCopy ? textH : 0) + 46;
                if (top.Height != height) top.Height = height;
            }
            finally { fitting = false; }
        }
        body.Resize += (_, _) => Fit();
        Theme.Changed += Fit;
        return body;
    }

    private void RefreshSoundtrackList()
    {
        var pending = MusicBeeSettingsFile.ReadPending(settings);
        IReadOnlyList<string> registered = Array.Empty<string>();
        try
        {
            var path = MusicBeeSettingsFile.Locate(settings);
            if (path is not null) registered = MusicBeeSettingsFile.ReadFilteredFolders(File.ReadAllText(path));
        }
        catch { }
        var folders = SoundtrackFolders().ToList();
        try
        {
            // soundtracks with a saved song list whose folder MusicBee emptied/removed still need to be listed
            var manifests = Path.Combine(settings.DataDirectory, "soundtracks");
            if (Directory.Exists(manifests))
                foreach (var file in Directory.GetFiles(manifests, "*.txt"))
                {
                    var folder = service.SoundtrackTarget(Path.GetFileNameWithoutExtension(file));
                    if (!folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase))) folders.Add(folder);
                }
        }
        catch { }
        soundtrackList.Items = folders.Select(folder =>
        {
            int count;
            try { count = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Count(f => IsAudio(f)) : 0; } catch { count = 0; }
            var inMusicBee = registered.Any(r => r.TrimEnd('\\').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            var isPending = pending.Any(r => r.TrimEnd('\\').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            var missing = SoundtrackManifest.Load(settings, Path.GetFileName(folder))?.MissingFrom(folder).Count ?? 0;
            var status = missing > 0 ? $"{missing} in Songs - click to repair" : inMusicBee ? "In Folders tab" : isPending ? "Folders tab: pending" : "";
            return new SoundtrackItem(Path.GetFileName(folder), folder, count, status, missing);
        }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsAudio(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".m4a" or ".flac" or ".ogg" or ".opus" or ".wav" or ".wma" or ".aac" or ".aiff" or ".wv";

    private async Task SaveSoundtrackAsync()
    {
        if (cancellation is not null) return;
        using var dialog = new SoundtrackDialog(settings.ResolvePath(settings.SoundtrackFolder), SoundtrackFolders().Select(Path.GetFileName).OfType<string>().ToList());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!SaveSettingsFields(false)) return;
        var target = service.SoundtrackTarget(dialog.SoundtrackName);
        await PrepareMusicBeeForSoundtrackAsync(target, dialog.AddToFoldersTab);
        SoundtrackResult? result = null;
        await RunBusyAsync($"Saving soundtrack “{dialog.SoundtrackName}”", async token =>
        {
            var (links, soundtrackName, initialize) = (dialog.Links, dialog.SoundtrackName, dialog.InitializeInMusicBee); // read the dialog here, not on the worker
            result = await Task.Run(() => service.SaveSoundtrackAsync(links, soundtrackName, initialize, token));
            if (!result.Success) { SetResult("Soundtrack Saver: " + result.Error, Led.Warn); return; }
            SetResult($"Soundtrack “{dialog.SoundtrackName}” saved: {result.Files.Count} file(s) in {result.Folder}" +
                      (result.ImportedFiles > 0 ? $"; {result.PrimedFiles} initialized in MusicBee" : "") +
                      (result.Relocated > 0 ? $"; {result.Relocated} moved back after MusicBee relocated them" : "") + ".",
                result.Escaped.Count > 0 ? Led.Warn : Led.Ok);
        });
        if (result is { Success: true } && dialog.AddToFoldersTab) await RegisterFoldersInMusicBeeAsync([result.Folder], interactive: true);
        RefreshSoundtrackList();
    }

    private string OrganiseDeclinedPath => Path.Combine(settings.DataDirectory, "organise-limit-declined.txt");

    /// <summary>
    /// MusicBee's auto-organise ("keep organised" from E:\ into Songs) is what pulls saved soundtracks into
    /// the Songs folder. Offer once to narrow it to the Songs folder, and add the Folders-tab entry in the
    /// same MusicBee restart. Returns true when MusicBee can no longer move the soundtrack.
    /// </summary>
    private async Task<bool> PrepareMusicBeeForSoundtrackAsync(string target, bool addToFoldersTab)
    {
        if (settings.LocalBackend == LocalBackendKind.M3U) return true;
        var libraryPath = MusicBeeLibrarySettings.Locate(settings);
        string text;
        try { text = libraryPath is null ? "" : File.ReadAllText(libraryPath); } catch { text = ""; }
        var organiseMoves = libraryPath is not null && MusicBeeLibrarySettings.OrganiseWouldMove(text, target);
        if (!organiseMoves) return true;
        if (File.Exists(OrganiseDeclinedPath))
        {
            AppendLog("WARNING: MusicBee's auto-organise may move this soundtrack into Songs (you chose to keep that setting).");
            return false;
        }
        var answer = MessageBox.Show(this,
            "MusicBee is set to auto-organise every song on your drive into the Songs folder - that is what moves saved soundtracks into Songs.\r\n\r\n" +
            "Limit MusicBee's auto-organise to the Songs folder? New downloads in Songs are still organised exactly as now; soundtrack folders are left alone.\r\n\r\n" +
            "MusicBee closes for a moment (it reopens automatically) and a backup of its library settings is kept.",
            "Keep soundtracks in their own folder", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            try { Directory.CreateDirectory(settings.DataDirectory); File.WriteAllText(OrganiseDeclinedPath, "declined"); } catch { }
            return false;
        }
        var wasRunning = MusicBeeSettingsFile.IsMusicBeeRunning();
        if (wasRunning && !await CloseMusicBeeAsync()) { AppendLog("WARNING: MusicBee did not close, so its auto-organise setting was not changed."); return false; }
        var ok = false;
        try
        {
            ok = MusicBeeLibrarySettings.LimitOrganiseOnDisk(libraryPath!) || true;
            AppendLog("MusicBee auto-organise now only organises the Songs folder (backup: MusicBeeLibrarySettings.ini.spotysync-backup).");
            if (addToFoldersTab)
            {
                Directory.CreateDirectory(target);
                var settingsPath = MusicBeeSettingsFile.Locate(settings);
                if (settingsPath is not null && MusicBeeSettingsFile.RegisterFilteredFolders(settingsPath, [target]) > 0)
                    AppendLog($"MusicBee Folders tab: added {Path.GetFileName(target)} to your filtered folders.");
            }
        }
        catch (Exception ex) { AppendLog("WARNING: could not change MusicBee's settings: " + ex.Message); }
        if (wasRunning) ReopenMusicBee();
        return ok;
    }

    private async Task<bool> CloseMusicBeeAsync()
    {
        AppendLog("Closing MusicBee for a moment…");
        foreach (var process in Process.GetProcessesByName("MusicBee")) { try { process.CloseMainWindow(); } catch { } }
        for (var i = 0; i < 60 && MusicBeeSettingsFile.IsMusicBeeRunning(); i++) await Task.Delay(500);
        if (MusicBeeSettingsFile.IsMusicBeeRunning()) return false;
        await Task.Delay(800); // let MusicBee finish writing its settings
        return true;
    }

    private void ReopenMusicBee()
    {
        try
        {
            var exe = settings.ResolvePath(settings.MusicBeeExe);
            if (File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); AppendLog("Reopened MusicBee."); }
        }
        catch (Exception ex) { AppendLog("WARNING: could not reopen MusicBee: " + ex.Message); }
    }

    /// <summary>Moves a soundtrack's songs back from Songs (after making sure MusicBee won't move them again).</summary>
    private async Task RepairSoundtrackAsync(string name)
    {
        if (cancellation is not null) return;
        var target = service.SoundtrackTarget(name);
        if (!await PrepareMusicBeeForSoundtrackAsync(target, addToFoldersTab: true))
        {
            if (MessageBox.Show(this, "MusicBee's auto-organise is still set to move songs into Songs, so it may move these again. Repair anyway?",
                    "Repair soundtrack", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        await RunBusyAsync($"Repairing soundtrack “{name}”", async token =>
        {
            var moved = await Task.Run(() => service.RepairSoundtrackAsync(name, token));
            SetResult($"Soundtrack “{name}”: moved {moved} song(s) back into its folder.", moved > 0 ? Led.Ok : Led.Warn);
        });
        RefreshSoundtrackList();
    }

    /// <summary>Adds folders to MusicBee's Folders-tab filter. MusicBee must be closed for this; we offer to close and reopen it.</summary>
    private async Task RegisterFoldersInMusicBeeAsync(IReadOnlyList<string> folders, bool interactive)
    {
        var settingsPath = MusicBeeSettingsFile.Locate(settings);
        if (settingsPath is null)
        {
            AppendLog("WARNING: MusicBee3Settings.ini was not found, so the folder could not be added to the Folders tab. Add it manually: Folders tab > choose filtered folders.");
            return;
        }
        var all = MusicBeeSettingsFile.ReadPending(settings).Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wasRunning = MusicBeeSettingsFile.IsMusicBeeRunning();
        if (wasRunning)
        {
            if (!interactive) return;
            var names = string.Join(", ", folders.Select(f => Path.GetFileName(f.TrimEnd('\\'))));
            var answer = MessageBox.Show(this,
                $"To add “{names}” to MusicBee's Folders tab, MusicBee has to close briefly (it overwrites its settings when it exits).\r\n\r\n" +
                "Close MusicBee now, add the folder, and reopen it?\r\n\r\nChoose No to do it automatically the next time SpotySync sees MusicBee closed.",
                "Add to MusicBee Folders tab", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                MusicBeeSettingsFile.WritePending(settings, all);
                AppendLog("MusicBee Folders tab: queued " + names + " — it will be added when MusicBee is closed.");
                return;
            }
            AppendLog("Closing MusicBee to update its Folders tab…");
            foreach (var process in Process.GetProcessesByName("MusicBee")) { try { process.CloseMainWindow(); } catch { } }
            for (var i = 0; i < 60 && MusicBeeSettingsFile.IsMusicBeeRunning(); i++) await Task.Delay(500);
            if (MusicBeeSettingsFile.IsMusicBeeRunning())
            {
                MusicBeeSettingsFile.WritePending(settings, all);
                AppendLog("WARNING: MusicBee did not close (maybe a dialog is open). The folder is queued and will be added when MusicBee is closed.");
                return;
            }
            await Task.Delay(800); // let MusicBee finish writing its settings file
        }
        try
        {
            var added = MusicBeeSettingsFile.RegisterFilteredFolders(settingsPath, all);
            MusicBeeSettingsFile.WritePending(settings, Array.Empty<string>());
            AppendLog(added > 0
                ? $"MusicBee Folders tab: added {added} folder(s) to your filtered folders ({string.Join(", ", all.Select(f => Path.GetFileName(f.TrimEnd('\\'))))}). Backup: MusicBee3Settings.ini.spotysync-backup"
                : "MusicBee Folders tab already lists this folder.");
        }
        catch (Exception ex)
        {
            MusicBeeSettingsFile.WritePending(settings, all);
            AppendLog("WARNING: could not update MusicBee's Folders tab yet: " + ex.Message);
        }
        if (wasRunning)
        {
            try
            {
                var exe = settings.ResolvePath(settings.MusicBeeExe);
                if (File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); AppendLog("Reopened MusicBee."); }
            }
            catch (Exception ex) { AppendLog("WARNING: could not reopen MusicBee: " + ex.Message); }
        }
    }

    /// <summary>Applies queued Folders-tab registrations once MusicBee is closed (checked on status refresh).</summary>
    private async Task ApplyPendingMusicBeeFoldersAsync()
    {
        if (cancellation is not null) return;
        var pending = MusicBeeSettingsFile.ReadPending(settings);
        if (pending.Count == 0 || MusicBeeSettingsFile.IsMusicBeeRunning()) return;
        await RegisterFoldersInMusicBeeAsync(Array.Empty<string>(), interactive: false);
        RefreshSoundtrackList();
    }
}

internal sealed record SoundtrackItem(string Name, string Path, int Tracks, string Status, int Missing = 0);

/// <summary>Compact list of soundtrack folders (click to open).</summary>
internal sealed class SoundtrackList : ThemedControl
{
    private List<SoundtrackItem> items = new();
    private int hover = -1;
    public event Action<SoundtrackItem>? Clicked;
    public List<SoundtrackItem> Items { get => items; set { items = value; Invalidate(); } }
    private const int RowH = 28;

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        Ui.Text(g, "YOUR SOUNDTRACKS", Fonts.Mono(7.5f, FontStyle.Bold), new Rectangle(0, 6, Width, 18), p.Dim);
        if (items.Count == 0)
        {
            Ui.Text(g, "None yet — saved soundtracks appear here.", Fonts.Body(9f), new Rectangle(0, 28, Width, RowH), p.Muted);
            return;
        }
        for (var i = 0; i < items.Count; i++)
        {
            var y = 28 + i * RowH;
            if (y + RowH > Height) { Ui.Text(g, $"+{items.Count - i} more", Fonts.Body(8.5f), new Rectangle(0, y, Width, 18), p.Dim); break; }
            var item = items[i];
            if (i == hover) Ui.FillRound(g, p.Hover, new RectangleF(0, y + 1, Width, RowH - 2), Theme.R(6));
            Ui.DrawGlyph(g, Glyph.Folder, 9f, p.Accent, new Rectangle(4, y, 20, RowH));
            Ui.Text(g, item.Name, Fonts.Semibold(9.25f), new Rectangle(28, y, Width / 2, RowH), p.Ink);
            var right = $"{item.Tracks} tracks" + (item.Status.Length > 0 ? "  ·  " + item.Status : "");
            Ui.Text(g, right, Fonts.Body(8.5f), new Rectangle(Width / 2, y, Width / 2 - 8, RowH), item.Missing > 0 ? p.Accent : item.Status.Contains("pending") ? p.Warn : p.Muted, TextFormatFlags.Right);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = e.Y < 28 ? -1 : (e.Y - 28) / RowH;
        if (index >= items.Count) index = -1;
        if (index != hover) { hover = index; Cursor = index >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (hover >= 0 && hover < items.Count) Clicked?.Invoke(items[hover]);
    }
}

/// <summary>Soundtrack Saver input: links, folder name and MusicBee options.</summary>
public sealed class SoundtrackDialog : ThemedDialog
{
    private readonly FieldBox links = new() { Multiline = true };
    private readonly FieldBox name = new() { Placeholder = "e.g. Blockheads" };
    private readonly ToggleSwitch initialize = new() { Text = "Initialize in MusicBee (import + 1-second silent play)", Checked = true };
    private readonly ToggleSwitch foldersTab = new() { Text = "Add the folder to MusicBee's Folders tab (filtered folders)", Checked = true };
    private readonly Label target = Ui.Label("", "accent", Fonts.Mono(9f), autoSize: false);
    private readonly string root;
    private readonly IReadOnlyList<string> existing;

    public IReadOnlyList<string> Links { get; private set; } = Array.Empty<string>();
    public string SoundtrackName => SpotySyncService.SafeFolderName(name.Text);
    public bool InitializeInMusicBee => initialize.Checked;
    public bool AddToFoldersTab => foldersTab.Checked;

    public SoundtrackDialog(string soundtrackRoot, IReadOnlyList<string> existingFolders)
    {
        root = soundtrackRoot; existing = existingFolders;
        Text = "Soundtrack Saver";
        MinimumSize = new Size(680, 560);
        Size = new Size(780, 600);
        Body.Title = "Save a soundtrack or playlist";
        Body.Eyebrow = "Soundtrack Saver";
        Body.Glyph = Glyph.Music;
        Body.Inner = new Padding(20, 6, 20, 16);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var copy = Ui.Label("Paste a Spotify playlist/album link, a YouTube playlist, or any other supported link. Everything is downloaded, then kept together in its own folder instead of the Songs folder.", "muted", Fonts.Body(9.5f), autoSize: false);
        copy.Dock = DockStyle.Fill;
        layout.Controls.Add(copy, 0, 0);
        layout.Controls.Add(Caption("LINKS"), 0, 1);
        links.Dock = DockStyle.Fill; links.Inner.Font = Fonts.Mono(9.75f);
        links.Placeholder = "https://open.spotify.com/playlist/...\r\nhttps://www.youtube.com/playlist?list=...";
        layout.Controls.Add(links, 0, 2);
        layout.Controls.Add(Caption("FOLDER NAME"), 0, 3);
        name.Dock = DockStyle.Fill; name.Margin = new Padding(0, 2, 0, 2);
        name.TextChanged += (_, _) => UpdateTarget();
        layout.Controls.Add(name, 0, 4);
        target.Dock = DockStyle.Fill; target.TextAlign = ContentAlignment.MiddleLeft; target.AutoEllipsis = true;
        layout.Controls.Add(target, 0, 5);
        initialize.Dock = DockStyle.Fill; foldersTab.Dock = DockStyle.Fill;
        initialize.CheckedChanged += (_, _) => { };
        layout.Controls.Add(initialize, 0, 6);
        layout.Controls.Add(foldersTab, 0, 7);

        var actions = new Row(rightToLeft: true) { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        var save = RoundedButton.Make("Save soundtrack", ButtonKind.Primary, Glyph.Download, (_, _) => Accept());
        var cancel = RoundedButton.Make("Cancel", ButtonKind.Secondary);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var paste = RoundedButton.Make("Paste", ButtonKind.Ghost, Glyph.Copy, (_, _) => { if (Clipboard.ContainsText()) links.Text = Clipboard.GetText(); });
        foreach (var b in new[] { save, cancel, paste }) { b.Margin = new Padding(8, 0, 0, 0); actions.Controls.Add(b); }
        layout.Controls.Add(actions, 0, 8);
        Body.Controls.Add(layout);
        AcceptButton = null; // Enter adds new lines in the links box
        CancelButton = cancel;
        UpdateTarget();
        Shown += (_, _) => links.Inner.Focus();
    }

    private static Label Caption(string text)
    {
        var label = Ui.Label(text, "dim", Fonts.Mono(7.75f, FontStyle.Bold), autoSize: false);
        label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.BottomLeft;
        return label;
    }

    private void UpdateTarget()
    {
        var folder = SoundtrackName;
        if (folder.Length == 0) { target.Text = "→ " + Path.Combine(root, "<name>") + "\\"; return; }
        var exists = existing.Any(x => x.Equals(folder, StringComparison.OrdinalIgnoreCase));
        target.Text = "→ " + Path.Combine(root, folder) + "\\" + (exists ? "   (exists — new files are added to it)" : "");
    }

    private void Accept()
    {
        Links = System.Text.RegularExpressions.Regex.Matches(links.Text, @"(?:https?://|spotify:)\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Value.Trim().TrimEnd(',', ';', ')', ']', '}', '.', '"', '\''))
            .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Links.Count == 0) { MessageBox.Show(this, "Paste at least one playlist, album or track link.", "No links found", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (SoundtrackName.Length == 0) { MessageBox.Show(this, "Give the soundtrack a folder name, for example “Blockheads”.", "Folder name needed", MessageBoxButtons.OK, MessageBoxIcon.Information); name.Inner.Focus(); return; }
        if (SoundtrackName.Equals("Songs", StringComparison.OrdinalIgnoreCase) || SoundtrackName.Equals("Playlists", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "Pick a different name — that folder is used by your main library.", "Folder name", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}
