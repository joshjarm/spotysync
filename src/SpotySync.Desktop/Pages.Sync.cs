using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using SpotySync;

namespace SpotySyncDesktop;

public sealed partial class MainWindow
{
    private PlaylistPlan? selectedPlan;

    private Control BuildSyncPage()
    {
        var page = Ui.Column(16);
        var force = new CheckBox { Content = "Force full refresh (re-read everything from Spotify)", Foreground = Pal.Muted };
        page.Children.Add(Ui.Column(2, Ui.Title("Sync"),
            Ui.Muted("SpotySync compares your Spotify playlists with the M3U playlists in your music folder and shows every change first. Nothing changes until you press Apply.")));
        var apply = Action("Apply changes", ApplyAsync, primary: true);
        page.Children.Add(Ui.Row(10, Action("Build preview", () => BuildPreviewAsync(force.IsChecked == true)), apply, force));

        if (prepared is null)
        {
            apply.IsVisible = false;
            page.Children.Add(Ui.Card(Ui.Muted("No preview yet. Build one to see what would change.")));
            return page;
        }
        var plan = prepared.Plan;
        var state = planAppliedAt is not null ? $"Applied at {planAppliedAt:HH:mm}; build a new preview to sync again."
            : previewRestoredAt is not null ? $"From {previewRestoredAt:ddd d MMM HH:mm} (last session). Build a new preview before applying."
            : $"{plan.TotalChanges} change(s) ready to apply.";
        page.Children.Add(Ui.Card(Ui.Column(4,
            Ui.Text($"{plan.Playlists.Count} playlist(s) • {plan.TotalChanges} change(s) • {plan.ConfidentMatches:N0} songs matched to Spotify • {plan.UnmatchedLocal:N0} local-only songs (never removed)"),
            Ui.Muted(state))));

        var header = PlanRow("Playlist", "Status", "+ Spotify", "− Spotify", "+ Local", "− Local", Pal.Muted, FontWeight.SemiBold);
        var list = new ListBox
        {
            ItemsSource = plan.Playlists.OrderByDescending(p => p.IsBlocked).ThenByDescending(p => p.ChangeCount).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ItemTemplate = new FuncDataTemplate<PlaylistPlan>((p, _) => p is null ? new TextBlock() : PlanRow(
                p.Name, p.IsBlocked ? "Blocked" : p.ChangeCount == 0 ? "In sync" : p.HasBaseline ? "Ready" : "First run",
                Count(p.SpotifyAddIds.Count), Count(p.SpotifyRemoveIds.Count), Count(p.LocalAddFiles.Count), Count(p.LocalRemoveFiles.Count),
                p.IsBlocked ? Pal.Err : p.ChangeCount == 0 ? Pal.Muted : Pal.Ink, FontWeight.Normal)),
            Background = Brushes.Transparent, MaxHeight = 420
        };
        var details = new SelectableTextBlock { Foreground = Pal.Ink, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        list.SelectionChanged += (_, _) => { selectedPlan = list.SelectedItem as PlaylistPlan; details.Text = Describe(selectedPlan); };
        if (selectedPlan is not null) list.SelectedItem = plan.Playlists.FirstOrDefault(p => p.Name == selectedPlan.Name);
        page.Children.Add(Ui.Card(Ui.Column(4, header, list), 10));
        page.Children.Add(Ui.Card(Ui.Column(6, Ui.Heading("Details"), details)));
        if (details.Text is null or "") details.Text = "Pick a playlist to see the songs that would be added or removed.";
        return page;
    }

    private static string Count(int n) => n == 0 ? "·" : n.ToString("N0");

    private static Grid PlanRow(string name, string status, string spotifyAdd, string spotifyRemove, string localAdd, string localRemove, IBrush color, FontWeight weight)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,90,90,90,90"), Margin = new Thickness(6, 2) };
        var values = new[] { name, status, spotifyAdd, spotifyRemove, localAdd, localRemove };
        for (var i = 0; i < values.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = values[i], Foreground = i switch { 2 when values[i] != "·" && weight == FontWeight.Normal => Pal.SpotifyAdd, 4 when values[i] != "·" && weight == FontWeight.Normal => Pal.LocalAdd, (3 or 5) when values[i] != "·" && weight == FontWeight.Normal => Pal.Warn, _ => color },
                FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private string Describe(PlaylistPlan? plan)
    {
        if (plan is null || prepared is null) return "";
        var tracks = prepared.Liked.Tracks.Concat(prepared.SpotifyPlaylists.SelectMany(p => p.Tracks))
            .Where(t => t.SpotifyId.Length > 0).GroupBy(t => t.SpotifyId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string Song(string id) => tracks.TryGetValue(id, out var t) ? string.Join(", ", t.Artists) + " - " + t.Title : id;
        var lines = new List<string> { plan.Name };
        if (plan.IsBlocked) lines.Add("Blocked: " + plan.BlockReason);
        void Section(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            lines.Add("");
            lines.Add($"{title} ({list.Count})");
            lines.AddRange(list.Take(300).Select(x => "  • " + x));
            if (list.Count > 300) lines.Add($"  … and {list.Count - 300} more");
        }
        Section("Add on Spotify", plan.SpotifyAddIds.Select(Song));
        Section("Remove on Spotify", plan.SpotifyRemoveIds.Select(Song));
        Section("Add to the local playlist", plan.LocalAddFiles.Select(Path.GetFileNameWithoutExtension).OfType<string>());
        Section("Remove from the local playlist", plan.LocalRemoveFiles.Select(Path.GetFileNameWithoutExtension).OfType<string>());
        Section("Kept as they are (only on one side)", plan.PlatformSpecific);
        Section("Warnings", plan.Warnings);
        if (plan.ChangeCount == 0 && !plan.IsBlocked) { lines.Add(""); lines.Add("Nothing to change: this playlist is in sync."); }
        return string.Join("\n", lines);
    }

    private async Task ApplyAsync()
    {
        if (cancellation is not null || prepared is null) return;
        if (previewRestoredAt is not null || planAppliedAt is not null)
        {
            // never apply a plan from a previous session or twice: build a fresh one first
            if (!await BuildPreviewAsync(false) || prepared is null) return;
        }
        var plan = prepared.Plan;
        if (plan.TotalChanges == 0) { SetResult("Everything is in sync; nothing to apply."); return; }
        var removals = plan.Playlists.Sum(p => p.SpotifyRemoveIds.Count + p.LocalRemoveFiles.Count);
        var question = removals > 0
            ? $"This plan removes {removals} song(s) from playlists (backups are written first) and makes {plan.TotalChanges - removals} addition(s).\n\nApply this exact plan?"
            : $"Apply {plan.TotalChanges} addition(s) from this plan?";
        if (!await Confirm("Apply sync plan", question, removals > 0 ? "Apply with removals" : "Apply")) return;
        var target = prepared;
        await RunBusyAsync("Applying the sync plan", async token =>
        {
            var result = await Task.Run(() => service.ExecuteAsync(target, false, token));
            foreach (var error in result.Errors) AppendLog("ERROR: " + error);
            planAppliedAt = DateTime.Now;
            SavePreview();
            SetResult($"Applied: Spotify +{result.SpotifyAdded}/−{result.SpotifyRemoved}; local +{result.LocalAdded}/−{result.LocalRemoved}.", result.Errors.Count > 0 ? Pal.Warn : Pal.Ok);
        });
        Refresh();
    }

    /// <summary>A small yes/no window.</summary>
    private async Task<bool> Confirm(string title, string message, string yes)
    {
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, Background = Pal.Surface,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var answer = false;
        var ok = Ui.Button(yes, () => { answer = true; dialog.Close(); }, primary: true);
        var cancel = Ui.Button("Cancel", () => dialog.Close());
        var buttons = Ui.Row(10, cancel, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new Border { Padding = new Thickness(22), Child = Ui.Column(18, Ui.Heading(title), Ui.Text(message), buttons) };
        await dialog.ShowDialog(this);
        return answer;
    }
}
