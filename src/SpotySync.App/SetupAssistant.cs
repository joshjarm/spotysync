using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SpotySyncApp;

/// <summary>
/// One-click Spotify setup.
/// 1) If a working SpotySync setup already exists on this PC, one button reuses it (keys, login, folders, history).
/// 2) Otherwise it uses the user's normal browser (already signed in): opens the right Spotify pages, offers
///    one-click copies of the app form values, and picks the Client ID and secret up from the clipboard,
///    then continues automatically. The user creates/opens the app and accepts Spotify's terms themselves.
/// </summary>
public sealed class SetupAssistant : ThemedDialog
{
    private static readonly Regex Hex32 = new(@"\b[0-9a-f]{32}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdInUrl = new(@"developer\.spotify\.com/dashboard/([0-9a-f]{32})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string DashboardUrl = "https://developer.spotify.com/dashboard";
    private const string AppName = "SpotySync";
    private const string AppDescription = "Personal Spotify / MusicBee playlist sync and Liked Songs backup.";
    private readonly FieldBox idBox = new() { Placeholder = "Client ID - copy it on the Spotify page" };
    private readonly FieldBox secretBox = new() { Placeholder = "Client secret - click “View client secret”, then copy it", Password = true };
    private readonly StatusPill idPill = new() { Text = "Client ID: waiting" };
    private readonly StatusPill secretPill = new() { Text = "Client secret: waiting" };
    private readonly Label status = Ui.Label("", "muted", Fonts.Body(9.25f), autoSize: false);
    private readonly RoundedButton finish = RoundedButton.Make("Save & connect", ButtonKind.Primary, Glyph.Check);
    private readonly System.Windows.Forms.Timer clipboardTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer autoContinue = new() { Interval = 1500 };
    private readonly bool offerReuse;
    private string lastClipboard = "";

    /// <summary>Open the browser and watch the clipboard when shown (off for headless UI tests).</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Set when the user picked an existing, working SpotySync setup to reuse.</summary>
    public ExistingSetup? Adopted { get; private set; }

    public string ClientId => idBox.Text.Trim().ToLowerInvariant();
    public string ClientSecret => secretBox.Text.Trim().ToLowerInvariant();

    public SetupAssistant(string redirectUri, string currentClientId = "", IReadOnlyList<ExistingSetup>? existing = null)
    {
        Text = "Spotify setup";
        MinimumSize = new Size(760, 640);
        Size = new Size(840, 760);
        Body.Title = "Connect SpotySync to Spotify";
        Body.Eyebrow = "One-click setup";
        Body.Glyph = Glyph.Link;
        Body.Inner = new Padding(20, 8, 20, 16);

        var stack = new VStack { Dock = DockStyle.Fill, Gap = 10, AutoScroll = true };
        var best = existing?.FirstOrDefault();
        offerReuse = best is not null;
        if (best is not null)
        {
            var reuse = new Card { Raised = true, Radius = 12, AccentEdge = Theme.P.Accent, Inner = new Padding(18, 12, 14, 12), Height = 96 };
            var what = new List<string> { "Spotify app" };
            if (best.HasLogin) what.Add("login");
            if (best.ClientSecret.Length > 0) what.Add("downloader key");
            what.Add("folders and sync history");
            var text = Ui.Label($"Found your working SpotySync setup in {best.Folder}.\r\nReuse its {string.Join(", ", what)} - nothing to do on the Spotify website.",
                "ink", Fonts.Body(9.75f), autoSize: false);
            text.Dock = DockStyle.Fill; text.TextAlign = ContentAlignment.MiddleLeft;
            var use = RoundedButton.Make("Use my existing setup", ButtonKind.Primary, Glyph.Check, (_, _) =>
            {
                Adopted = best;
                idBox.Text = best.ClientId;
                secretBox.Text = best.ClientSecret;
                DialogResult = DialogResult.OK;
                Close();
            });
            use.AutoSize = false; use.Size = use.GetPreferredSize(Size.Empty);
            var buttonHost = new Panel { Dock = DockStyle.Right, Width = use.Width + 8 };
            use.Location = new Point(8, (72 - use.Height) / 2);
            buttonHost.Controls.Add(use);
            reuse.Controls.Add(text); reuse.Controls.Add(buttonHost);
            stack.Controls.Add(reuse);
            var or = Ui.Label("- or set up a different Spotify app -", "dim", Fonts.Body(9f), autoSize: false);
            or.Height = 24; or.TextAlign = ContentAlignment.MiddleCenter;
            stack.Controls.Add(or);
        }

        stack.Controls.Add(Step("1", "Open your Spotify developer dashboard",
            "It opens in your normal browser, where you're already signed in.",
            RoundedButton.Make("Open dashboard", ButtonKind.Secondary, Glyph.Open, (_, _) => Open(DashboardUrl))));
        stack.Controls.Add(Step("2", "Open your app - or create one",
            "New app: paste these values, tick “Web API”, accept Spotify's terms, Save. Existing app (e.g. one made for SpotDL): " +
            "Edit settings and add the Redirect URI below, or Spotify will say “redirect_uri: Not matching configuration”.",
            RoundedButton.Make("Create app", ButtonKind.Secondary, Glyph.Add, (_, _) => Open(DashboardUrl + "/create"))));
        stack.Controls.Add(CopyRow("App name", AppName));
        stack.Controls.Add(CopyRow("Description", AppDescription));
        stack.Controls.Add(CopyRow("Redirect URI", redirectUri));
        stack.Controls.Add(Step("3", "Copy the Client ID, then the Client secret",
            "On the app's page: copy the Client ID, click “View client secret” and copy it. SpotySync picks both up automatically.", null));
        var pills = new Row { Height = 34 };
        idPill.Margin = new Padding(0, 0, 8, 0); secretPill.Margin = new Padding(0);
        pills.Controls.Add(idPill); pills.Controls.Add(secretPill);
        stack.Controls.Add(pills);
        idBox.Height = secretBox.Height = 34;
        stack.Controls.Add(idBox);
        stack.Controls.Add(secretBox);
        status.Height = 40;
        stack.Controls.Add(status);

        var footer = new Row(rightToLeft: true) { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 12, 0, 0) };
        var cancel = RoundedButton.Make("Cancel", ButtonKind.Secondary);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        finish.Enabled = false;
        finish.Click += (_, _) => Accept();
        finish.Margin = new Padding(8, 0, 0, 0); cancel.Margin = new Padding(8, 0, 0, 0);
        footer.Controls.Add(finish); footer.Controls.Add(cancel);
        Body.Controls.Add(stack); Body.Controls.Add(footer);
        CancelButton = cancel;

        if (Hex32.IsMatch(currentClientId)) idBox.Text = currentClientId;
        idBox.TextChanged += (_, _) => UpdateState();
        secretBox.TextChanged += (_, _) => UpdateState();
        clipboardTimer.Tick += (_, _) => WatchClipboard();
        autoContinue.Tick += (_, _) => { autoContinue.Stop(); if (finish.Enabled) Accept(); };
        Shown += (_, _) =>
        {
            UpdateState();
            if (!AutoStart) return;
            try { lastClipboard = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; } catch { }
            clipboardTimer.Start();
            if (!offerReuse) Open(DashboardUrl); // with a reusable setup, let the user choose first
        };
        FormClosed += (_, _) => { clipboardTimer.Stop(); autoContinue.Stop(); };
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static Control Step(string number, string title, string text, RoundedButton? action)
    {
        var row = new StepRow(number, title, text) { Height = text.Length > 140 ? 92 : 66 };
        if (action is not null) { action.AutoSize = false; action.Size = action.GetPreferredSize(Size.Empty); row.Controls.Add(action); }
        return row;
    }

    private static Control CopyRow(string label, string value)
    {
        var field = new FieldBox { Text = value };
        field.Inner.ReadOnly = true;
        var copy = RoundedButton.Make("Copy", ButtonKind.Ghost, Glyph.Copy);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(value); copy.Text = "Copied"; } catch { }
        };
        var caption = Ui.Label(label, "dim", Fonts.Body(8.75f), autoSize: false);
        caption.Width = 110; caption.TextAlign = ContentAlignment.MiddleLeft;
        var strip = new FieldStrip(field, copy) { Height = 36 };
        var host = new Panel { Height = 36, Margin = new Padding(34, 0, 0, 0) };
        caption.Dock = DockStyle.Left; strip.Dock = DockStyle.Fill;
        host.Controls.Add(strip); host.Controls.Add(caption);
        return host;
    }

    private void WatchClipboard()
    {
        string text;
        try { if (!Clipboard.ContainsText()) return; text = Clipboard.GetText().Trim(); }
        catch { return; } // clipboard busy
        if (text == lastClipboard) return;
        lastClipboard = text;
        var fromUrl = IdInUrl.Match(text);
        if (fromUrl.Success) { idBox.Text = fromUrl.Groups[1].Value; return; }
        if (text.Length > 40) return;
        var match = Hex32.Match(text);
        if (!match.Success) return;
        var value = match.Value.ToLowerInvariant();
        if (ClientId.Length == 0 || ClientId == value) idBox.Text = value;
        else secretBox.Text = value;
    }

    private void UpdateState()
    {
        var hasId = Hex32.IsMatch(ClientId);
        var hasSecret = Hex32.IsMatch(ClientSecret) && ClientSecret != ClientId;
        idPill.Text = hasId ? "Client ID: found" : "Client ID: waiting";
        idPill.State = hasId ? Led.Ok : Led.Busy;
        secretPill.Text = hasSecret ? "Client secret: found" : "Client secret: waiting";
        secretPill.State = hasSecret ? Led.Ok : hasId ? Led.Busy : Led.Idle;
        finish.Enabled = hasId && hasSecret;
        status.Text = hasId && hasSecret ? "Both keys found - saving, updating the MusicBee plugin and connecting Spotify..."
            : hasId ? "Got the Client ID. Now click “View client secret” on the Spotify page and copy it."
            : "Waiting for you to copy the Client ID (copying the app page's address works too).";
        if (finish.Enabled && Visible && Adopted is null) autoContinue.Start();
    }

    private void Accept()
    {
        if (!finish.Enabled) return;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { clipboardTimer.Dispose(); autoContinue.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Numbered step heading with an optional action button on the right.</summary>
internal sealed class StepRow : ThemedControl
{
    private readonly string number, title, text;

    public StepRow(string number, string title, string text) { this.number = number; this.title = title; this.text = text; }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        foreach (Control c in Controls) c.Location = new Point(Width - c.Width, 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P; var g = e.Graphics;
        var circle = new RectangleF(0, 8, 26, 26);
        Ui.FillRound(g, p.Accent, circle, 13);
        Ui.Text(g, number, Fonts.Semibold(9.5f), Rectangle.Round(circle), p.AccentInk, TextFormatFlags.HorizontalCenter);
        var right = Controls.Count > 0 ? Controls[0].Width + 14 : 0;
        Ui.Text(g, title, Fonts.Semibold(10f), new Rectangle(36, 6, Width - 36 - right, 22), p.Ink);
        TextRenderer.DrawText(g, text, Fonts.Body(9f), new Rectangle(36, 28, Width - 36 - right, Height - 28), p.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}
