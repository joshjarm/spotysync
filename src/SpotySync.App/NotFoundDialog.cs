using System.Diagnostics;
using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// Songs spotDL couldn't find on YouTube Music. Downloads leave them out for a while; here a YouTube link can be added
/// for any of them (spotDL then downloads that link and still tags the file from Spotify), or they can be tried again.
/// </summary>
public sealed class NotFoundDialog : ThemedDialog
{
    private readonly PortableSettings settings;
    private readonly Panel list = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
    private readonly Label count = Ui.Label("", "dim", Fonts.Body(9f), autoSize: false);
    private readonly Dictionary<string, FieldBox> linkFields = new(StringComparer.OrdinalIgnoreCase);
    private List<NotFoundSong> songs = new();

    /// <summary>Spotify links to download when the dialog closes with OK.</summary>
    public IReadOnlyList<string> Links { get; private set; } = Array.Empty<string>();

    public NotFoundDialog(PortableSettings settings)
    {
        this.settings = settings;
        Text = "Not on YouTube Music";
        MinimumSize = new Size(760, 460);
        Size = new Size(980, 620);
        Body.Title = "Not on YouTube Music";
        Body.Eyebrow = "Songs spotDL couldn't find";
        Body.Glyph = Glyph.Music;
        Body.Inner = new Padding(20, 6, 20, 16);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var copy = Ui.Label("spotDL searched YouTube Music for these songs and found nothing that matched, so downloads leave them out for " +
            $"{NotFoundSongs.RetryAfter.TotalDays:0} days instead of searching again every time. If you find one yourself (Search opens YouTube Music), " +
            "paste its link: it then downloads from that link, tagged with the Spotify details.", "muted", Fonts.Body(9.5f), autoSize: false);
        copy.Dock = DockStyle.Fill;
        layout.Controls.Add(copy, 0, 0);
        Ui.DarkScrollbars(list);
        layout.Controls.Add(list, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var close = RoundedButton.Make("Close", ButtonKind.Secondary, "", (_, _) => { SaveLinks(); DialogResult = DialogResult.Cancel; Close(); });
        var retry = RoundedButton.Make("Search all again", ButtonKind.Secondary, Glyph.Refresh, (_, _) => RetryAll());
        var download = RoundedButton.Make("Download linked songs", ButtonKind.Primary, Glyph.Download, (_, _) => DownloadLinked());
        count.Dock = DockStyle.Fill; count.TextAlign = ContentAlignment.MiddleLeft;
        actions.Controls.Add(count, 0, 0); actions.Controls.Add(close, 1, 0); actions.Controls.Add(retry, 2, 0); actions.Controls.Add(download, 3, 0);
        layout.Controls.Add(actions, 0, 2);
        Body.Controls.Add(layout);
        CancelButton = close;
        Fill();
    }

    private void Fill()
    {
        songs = NotFoundSongs.Load(settings).Songs.Values.OrderBy(s => s.Artists, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        list.SuspendLayout();
        list.Controls.Clear();
        linkFields.Clear();
        if (songs.Count == 0)
        {
            var none = Ui.Label("Nothing here: every song spotDL looked for was found.", "muted", Fonts.Body(10f), autoSize: false);
            none.Dock = DockStyle.Top; none.Height = 40;
            list.Controls.Add(none);
        }
        // docked to the top in reverse, so the first song is at the top
        foreach (var song in Enumerable.Reverse(songs)) list.Controls.Add(BuildRow(song));
        list.ResumeLayout();
        UpdateCount();
    }

    private Control BuildRow(NotFoundSong song)
    {
        var title = Ui.Label(song.DisplayName, "ink", Fonts.Body(9.75f), autoSize: false);
        title.AutoEllipsis = true;
        var detail = Ui.Label(Detail(song), "dim", Fonts.Body(8.5f), autoSize: false);
        detail.AutoEllipsis = true;
        var link = new FieldBox { Placeholder = "Paste a YouTube link for this song", ClearButton = true, Text = song.ManualUrl };
        link.TextChanged += (_, _) => UpdateCount();
        linkFields[song.SpotifyId] = link;
        var search = RoundedButton.Make("Search", ButtonKind.Ghost, Glyph.Search, (_, _) => OpenSearch(song));
        var forget = RoundedButton.Make("Forget", ButtonKind.Ghost, Glyph.Delete, (_, _) =>
        {
            SaveLinks();
            NotFoundSongs.Forget(settings, [song.SpotifyId]);
            Fill();
        });
        new ToolTip().SetToolTip(forget, "Remove it from this list. The next download searches for it again.");
        return new SongRow(title, detail, link, search, forget);
    }

    /// <summary>One song: names on the left, the link box in the middle, the buttons on the right, all centred.</summary>
    private sealed class SongRow : Panel
    {
        private readonly Label title, detail;
        private readonly FieldBox link;
        private readonly Control search, forget;

        public SongRow(Label title, Label detail, FieldBox link, Control search, Control forget)
        {
            (this.title, this.detail, this.link, this.search, this.forget) = (title, detail, link, search, forget);
            Dock = DockStyle.Top; Height = 56; BackColor = Color.Transparent;
            Controls.AddRange([title, detail, link, search, forget]);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var w = ClientSize.Width - 8;
            var right = w;
            foreach (var button in new[] { forget, search })
            {
                var size = button.GetPreferredSize(Size.Empty);
                var width = Math.Max(size.Width, button.Width);
                right -= width;
                button.SetBounds(right, (Height - button.Height) / 2, width, button.Height);
                right -= 6;
            }
            var namesWidth = (int)(w * 0.42);
            title.SetBounds(0, Height / 2 - 21, namesWidth - 12, 22);
            detail.SetBounds(0, Height / 2 + 1, namesWidth - 12, 18);
            var linkHeight = 34;
            link.SetBounds(namesWidth, (Height - linkHeight) / 2, Math.Max(80, right - 6 - namesWidth), linkHeight);
        }
    }

    private static string Detail(NotFoundSong song)
    {
        var parts = new List<string>();
        if (song.Album.Length > 0 && !song.Album.Equals(song.Title, StringComparison.OrdinalIgnoreCase)) parts.Add(song.Album);
        if (song.DurationMs > 0) parts.Add(TimeSpan.FromMilliseconds(song.DurationMs).ToString(@"m\:ss"));
        parts.Add(song.Tries == 1 ? "searched once" : $"searched {song.Tries} times");
        parts.Add("last " + song.LastTried.LocalDateTime.ToString("d MMM"));
        return string.Join("  ·  ", parts);
    }

    private static void OpenSearch(NotFoundSong song)
    {
        try { Process.Start(new ProcessStartInfo("https://music.youtube.com/search?q=" + Uri.EscapeDataString(song.DisplayName)) { UseShellExecute = true }); }
        catch { }
    }

    private static bool IsLink(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private void UpdateCount()
    {
        var linked = linkFields.Values.Count(f => IsLink(f.Text));
        count.Text = songs.Count == 0 ? "" : $"{songs.Count} song(s)" + (linked > 0 ? $" · {linked} with a link" : "");
    }

    /// <summary>Saves the typed links; an empty or invalid field clears the song's link.</summary>
    private void SaveLinks()
    {
        var typed = linkFields.ToDictionary(p => p.Key, p => IsLink(p.Value.Text) ? p.Value.Text.Trim() : "", StringComparer.OrdinalIgnoreCase);
        if (songs.All(s => typed.GetValueOrDefault(s.SpotifyId, s.ManualUrl) == s.ManualUrl)) return;
        NotFoundSongs.Update(settings, l =>
        {
            foreach (var (id, url) in typed)
                if (l.Songs.TryGetValue(id, out var song)) song.ManualUrl = url;
        });
    }

    private void DownloadLinked()
    {
        var bad = linkFields.Where(p => p.Value.Text.Trim().Length > 0 && !IsLink(p.Value.Text)).Select(p => p.Value).FirstOrDefault();
        if (bad is not null)
        {
            MessageBox.Show(this, "One of the links isn't a web address. Paste the full link, e.g. https://music.youtube.com/watch?v=…", "Check the link", MessageBoxButtons.OK, MessageBoxIcon.Information);
            bad.Inner.Focus();
            return;
        }
        SaveLinks();
        var linked = songs.Where(s => linkFields.TryGetValue(s.SpotifyId, out var f) && IsLink(f.Text)).Select(s => s.SpotifyUrl).ToList();
        if (linked.Count == 0)
        {
            MessageBox.Show(this, "Paste a YouTube link next to a song first (Search opens YouTube Music to look for it).", "No links yet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Links = linked;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RetryAll()
    {
        if (songs.Count == 0) return;
        SaveLinks();
        // clearing the last search date lets the download include them again
        NotFoundSongs.Update(settings, l => { foreach (var song in l.Songs.Values) song.LastTried = DateTimeOffset.MinValue; });
        Links = songs.Select(s => s.SpotifyUrl).ToList();
        DialogResult = DialogResult.OK;
        Close();
    }
}
