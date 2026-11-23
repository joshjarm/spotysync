using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpotySync;

namespace SpotySyncDesktop;

public sealed partial class MainWindow
{
    private Control BuildHomePage()
    {
        var page = Ui.Column(18);
        page.Children.Add(Ui.Column(2,
            Ui.Title("Home"),
            Ui.Muted("Back up your Spotify Likes as music files, keep playlists in step, and fill the gaps in your library.")));

        // Spotify connection
        var connected = service.HasSavedLogin;
        var spotify = Ui.Row(14,
            new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = connected ? Pal.Ok : Pal.Warn, VerticalAlignment = VerticalAlignment.Center },
            Ui.Text(connected ? "Spotify is connected." : "Spotify isn't connected yet."));
        if (!connected) spotify.Children.Add(Action("Connect Spotify", ConnectAsync, primary: true));
        page.Children.Add(Ui.Card(spotify));

        // main actions
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ActionCard("Run everything", "Downloads new Likes, builds a sync preview, then asks before changing any playlist.", "Run everything", RunAllAsync, true));
        actions.Children.Add(ActionCard("Back up new Likes", "Downloads the songs you liked on Spotify recently.", "Back up Likes", BackupAsync));
        actions.Children.Add(ActionCard("Sync playlists", "Compares your Spotify playlists with the M3U playlists in your music folder.", "Build preview", PreviewAsync));
        var missing = report?.LikedMissingLocally.Count ?? 0;
        actions.Children.Add(ActionCard("Missing Liked songs",
            report is null ? "Build a preview first; then SpotySync lists every Liked song you don't have yet." : $"{missing:N0} Liked song(s) aren't in your music folder yet.",
            missing > 0 ? $"Download {missing:N0}" : "Open Library", missing > 0 ? DownloadMissingLikedAsync : () => { ShowPage("Library"); return Task.CompletedTask; }));
        page.Children.Add(actions);

        // library facts
        if (prepared is not null)
        {
            var facts = Ui.Column(6,
                Ui.Heading("Your library"),
                Ui.Text($"{prepared.Library.Count:N0} song files • {prepared.Liked.Tracks.Count:N0} Liked songs on Spotify • {prepared.SpotifyPlaylists.Count} Spotify playlists • {prepared.LocalPlaylists.Count} local playlists"),
                Ui.Muted(previewRestoredAt is not null ? $"From the sync preview of {previewRestoredAt:ddd d MMM HH:mm}." : $"Sync preview built {previewBuiltAt:HH:mm}."));
            if (report is not null)
                facts.Children.Add(Ui.Muted($"{report.Unplaylisted.Count:N0} song(s) in no playlist • {report.LikedNotInPlaylist.Count:N0} Like(s) in no playlist • {report.Duplicates.Count:N0} duplicate file(s)"));
            page.Children.Add(Ui.Card(facts));
        }

        // Strawberry
        var playlists = settings.ResolvePath(settings.M3uFolder);
        page.Children.Add(Ui.Card(Ui.Column(8,
            Ui.Heading("Strawberry"),
            Ui.Muted($"Songs download to {settings.ResolvePath(settings.MusicFolder)} and synced playlists are saved as M3U files in {playlists}."),
            Ui.Muted("In Strawberry: add your music folder under Settings → Collection. To load a playlist, drag its .m3u8 file into Strawberry or use Playlist → Load playlist. Strawberry keeps its own copy, so load it again after a sync changes it."),
            Ui.Row(8, Ui.Button("Open playlists folder", () => { Directory.CreateDirectory(playlists); Ui.Open(playlists); }),
                      Ui.Button("Open music folder", () => Ui.Open(settings.ResolvePath(settings.MusicFolder)))))));
        return page;
    }

    private Control ActionCard(string title, string text, string button, Func<Task> action, bool primary = false)
    {
        var card = Ui.Card(Ui.Column(10, Ui.Heading(title), Ui.Muted(text), Action(button, action, primary)));
        card.Width = 300;
        card.Margin = new Thickness(0, 0, 14, 14);
        return card;
    }

    // ------------------------------------------------------------------ actions
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(settings.SpotifyClientId))
        {
            SetResult("Add your Spotify Client ID in Settings first.", Pal.Warn);
            ShowPage("Settings");
            return;
        }
        await RunBusyAsync("Connecting Spotify", async token =>
        {
            var accepted = await Task.Run(() => service.ConnectSpotifyAsync(token));
            if (!accepted.Equals(settings.RedirectUri, StringComparison.OrdinalIgnoreCase))
            {
                settings.RedirectUri = accepted; // remember the address Spotify accepts so the next sign-in goes straight through
                try { settings.Save(); } catch { }
            }
            AppendLog("Spotify connected successfully.");
            SetResult("Spotify connected.");
        });
        Refresh();
    }

    private async Task BackupAsync()
    {
        await RunBusyAsync("Backing up recent Liked Songs", async token =>
        {
            var result = await Task.Run(() => service.BackupRecentLikesAsync(false, token));
            if (!result.Success) { AppendLog("WARNING: " + result.Error); SetResult($"Backup partly done: {result.NewAudioFiles} new song(s). See Console.", Pal.Warn); return; }
            SetResult($"Backup complete: {result.NewAudioFiles} new song(s); {result.AlreadyLocal} recent Like(s) were already here.");
        });
    }

    private async Task PreviewAsync() => await BuildPreviewAsync(false);

    private async Task<bool> BuildPreviewAsync(bool forceRefresh)
    {
        var built = false;
        await RunBusyAsync("Building a sync preview", async token =>
        {
            var fresh = await Task.Run(() => service.PrepareAsync(forceRefresh, forceRefresh, token));
            var freshReport = await Task.Run(() => LibraryInsights.Build(fresh));
            prepared = fresh; report = freshReport;
            previewBuiltAt = DateTime.Now; previewRestoredAt = null; planAppliedAt = null;
            SavePreview();
            built = true;
            SetResult($"Preview: {fresh.Plan.TotalChanges} change(s) across {fresh.Plan.Playlists.Count} playlist(s); {fresh.Plan.ConfidentMatches:N0} songs matched to Spotify.");
        });
        if (built) ShowPage("Sync");
        return built;
    }

    private async Task RunAllAsync()
    {
        var backedUp = false;
        await RunBusyAsync("Backing up new Likes", async token =>
        {
            var backup = await Task.Run(() => service.BackupRecentLikesAsync(false, token));
            if (!backup.Success) AppendLog("WARNING: the backup was partial; the playlist preview still continues. " + backup.Error);
            backedUp = true;
        });
        if (backedUp && await BuildPreviewAsync(false)) await ApplyAsync();
    }

    private async Task DownloadMissingLikedAsync()
    {
        var links = report?.LikedMissingLocally.Where(r => r.SpotifyUrl.Length > 0).Select(r => r.SpotifyUrl).ToList() ?? new();
        if (links.Count == 0) return;
        await DownloadLinksAsync($"Downloading {links.Count} missing Liked song(s)", links);
    }

    private async Task DownloadLinksAsync(string label, IReadOnlyList<string> links)
    {
        await RunBusyAsync(label, async token =>
        {
            var result = await Task.Run(() => service.DownloadLinksAsync(links, token));
            if (!result.Success) { AppendLog("WARNING: " + result.Error); SetResult($"Download partly done: {result.NewAudioFiles} new song(s). See Console.", Pal.Warn); return; }
            SetResult($"Download complete: {result.NewAudioFiles} new song(s)" + (result.NotFound > 0 ? $", {result.NotFound} not on YouTube Music." : "."));
        });
    }
}
