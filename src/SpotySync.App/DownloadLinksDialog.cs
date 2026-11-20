using System.Text.RegularExpressions;

namespace SpotySyncApp;

/// <summary>Base for small themed dialogs: glow background, dark title bar, one card.</summary>
public class ThemedDialog : Form
{
    internal readonly Card Body = new() { Dock = DockStyle.Fill, Radius = 16 };

    public ThemedDialog()
    {
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        Font = Fonts.Body();
        BackColor = Theme.P.Bg; ForeColor = Theme.P.Ink;
        Padding = new Padding(14);
        Ui.StyleWindow(this);
        var host = new GlowHost { Dock = DockStyle.Fill, Padding = new Padding(0) };
        host.Controls.Add(Body);
        Controls.Add(host);
    }

    protected override void OnLoad(EventArgs e)
    {
        Ui.Restyle(this);
        base.OnLoad(e);
    }
}

public sealed class DownloadLinksDialog : ThemedDialog
{
    private readonly FieldBox input = new() { Multiline = true };
    private readonly Label count = Ui.Label("", "dim", Fonts.Body(9f), autoSize: false);

    public IReadOnlyList<string> Links { get; private set; } = Array.Empty<string>();

    public DownloadLinksDialog()
    {
        Text = "Download links";
        MinimumSize = new Size(640, 420);
        Size = new Size(760, 500);
        Body.Title = "Download Spotify or media links";
        Body.Eyebrow = "Manual download";
        Body.Glyph = Glyph.Link;
        Body.Inner = new Padding(20, 6, 20, 16);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var copy = Ui.Label("Paste one or several links. Spotify tracks/playlists use the configured downloader (spotDL by default); YouTube and other supported media links use the bundled yt-dlp.", "muted", Fonts.Body(9.5f), autoSize: false);
        copy.Dock = DockStyle.Fill;
        layout.Controls.Add(copy, 0, 0);
        input.Dock = DockStyle.Fill;
        input.Inner.Font = Fonts.Mono(9.75f);
        input.Placeholder = "https://open.spotify.com/playlist/...\r\nhttps://www.youtube.com/watch?v=...";
        input.TextChanged += (_, _) => UpdateCount();
        layout.Controls.Add(input, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var paste = RoundedButton.Make("Paste", ButtonKind.Ghost, Glyph.Copy, (_, _) => { if (Clipboard.ContainsText()) input.Text = Clipboard.GetText(); });
        var cancel = RoundedButton.Make("Cancel", ButtonKind.Secondary);
        var download = RoundedButton.Make("Start download", ButtonKind.Primary, Glyph.Download, (_, _) => AcceptLinks());
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        count.Dock = DockStyle.Fill; count.TextAlign = ContentAlignment.MiddleLeft;
        actions.Controls.Add(count, 0, 0); actions.Controls.Add(paste, 1, 0); actions.Controls.Add(cancel, 2, 0); actions.Controls.Add(download, 3, 0);
        layout.Controls.Add(actions, 0, 2);
        Body.Controls.Add(layout);
        AcceptButton = download;
        CancelButton = cancel;
        UpdateCount();
        Shown += (_, _) => input.Inner.Focus();
    }

    private static List<string> Parse(string text) =>
        Regex.Matches(text, @"(?:https?://|spotify:)\S+", RegexOptions.IgnoreCase)
            .Select(match => match.Value.Trim().TrimEnd(',', ';', ')', ']', '}', '.', '"', '\''))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void UpdateCount()
    {
        var n = Parse(input.Text).Count;
        count.Text = n == 0 ? "No links detected yet" : n == 1 ? "1 link ready" : $"{n} links ready";
    }

    private void AcceptLinks()
    {
        Links = Parse(input.Text);
        if (Links.Count == 0)
        {
            MessageBox.Show(this, "Paste at least one Spotify, YouTube, or other media link.", "No links found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
