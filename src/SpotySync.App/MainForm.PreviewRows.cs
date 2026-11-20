using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// Sync preview details as rows (same look as the Library): changes (ticked — untick to never sync), excluded
/// tracks (unticked — tick to sync again), protected tracks with their platform, and tracks already in sync (grey,
/// at the bottom). Ticking/unticking edits the per-playlist never-sync list and rebuilds the plan in place.
/// </summary>
public sealed partial class MainForm
{
    private enum DetailSection { Change, Excluded, Protected, InSync }
    private sealed record DetailInfo(DetailSection Section, string Change);

    private readonly InsightTable detailTable = new() { Dock = DockStyle.Fill };
    private readonly Label detailSummary = Ui.Label("", "muted", Fonts.Body(9.25f), autoSize: false);
    private readonly InsightTable dashDetailTable = new() { Dock = DockStyle.Fill };
    private readonly Label dashDetailSummary = Ui.Label("", "muted", Fonts.Body(8.75f), autoSize: false);
    private readonly Dictionary<InsightRow, DetailInfo> detailInfo = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<InsightTable, string> detailPlaylistOf = new();

    /// <summary>The track-change rows for the Sync preview page (also used, smaller, by the dashboard module).</summary>
    private Control BuildDetailRows() => BuildDetailRows(detailTable, detailSummary);

    private Control BuildDetailRows(InsightTable table, Label summary)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        summary.Dock = DockStyle.Top; summary.Height = 26; summary.AutoEllipsis = true; summary.TextAlign = ContentAlignment.MiddleLeft;
        table.Columns.Add(new("Change", 34, r => Info(r)?.Change ?? "", RowColor: (r, p) => Info(r)?.Section switch
        {
            DetailSection.Protected => p.Protected,
            DetailSection.Excluded or DetailSection.InSync => p.Dim,
            _ => r.Detail.StartsWith('−') ? p.Removal : r.Detail.StartsWith("Spotify") ? p.SpotifyAdd : p.LocalAdd
        }));
        table.Columns.Add(new("Artist", 70, r => r.Artist));
        table.Columns.Add(new("Title", 100, r => r.Title));
        table.Columns.Add(new("Details", 90, r => r.Album, RowColor: (r, p) => Info(r)?.Section == DetailSection.Protected ? ColorX.Mix(p.Protected, p.Ink, 0.3) : p.Muted));
        table.Checked = r => Info(r)?.Section switch { DetailSection.Change => true, DetailSection.Excluded => false, _ => null };
        table.Dimmed = r => Info(r)?.Section is DetailSection.Excluded or DetailSection.InSync;
        table.CheckToggled += rows => ToggleDetailRows(rows, detailPlaylistOf.GetValueOrDefault(table, ""));
        panel.Controls.Add(table); panel.Controls.Add(summary);
        return panel;
    }

    private void FillDetailRows(PlaylistPlan? item) => FillDetailRows(detailTable, detailSummary, item);

    private DetailInfo? Info(InsightRow row) => detailInfo.GetValueOrDefault(row);

    private static (string Artist, string Title) SplitName(string name)
    {
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? (name[..dash].Trim(), name[(dash + 3)..].Trim()) : ("", name.Trim());
    }

    private void FillDetailRows(InsightTable table, Label summary, PlaylistPlan? item)
    {
        foreach (var old in table.Items) detailInfo.Remove(old);
        detailPlaylistOf[table] = item?.Name ?? "";
        var detailTable = table; var detailSummary = summary; // the names below refer to the target table
        if (item is null)
        {
            detailSummary.Text = prepared is null ? "" : "Select a playlist above to see its track-level changes.";
            detailSummary.ForeColor = Theme.P.Muted;
            detailTable.Items = Array.Empty<InsightRow>();
            return;
        }
        var rows = new List<InsightRow>();
        void Add(DetailSection section, string change, string artist, string title, string detail)
        {
            var row = new InsightRow { Artist = artist, Title = title, Album = detail, Detail = change };
            detailInfo[row] = new DetailInfo(section, change);
            rows.Add(row);
        }
        void Spotify(string id, string change)
        {
            if (previewSpotifyTracks.TryGetValue(id, out var t)) Add(DetailSection.Change, change, string.Join(", ", t.Artists), t.Title, change.StartsWith('−') ? "Removed from the Spotify playlist" : "Added to the Spotify playlist");
            else Add(DetailSection.Change, change, "", "Unknown Spotify track", id);
        }
        void Local(string path, string change)
        {
            var name = previewLocalTracks.TryGetValue(SafePathKey(path), out var t) && t.Title.Length > 0 ? (t.Artist, t.Title) : SplitName(Path.GetFileNameWithoutExtension(path));
            Add(DetailSection.Change, change, name.Item1, name.Item2, Path.GetFileName(path));
        }
        foreach (var id in item.SpotifyAddIds) Spotify(id, "Spotify +");
        foreach (var path in item.LocalAddFiles) Local(path, "Local +");
        foreach (var id in item.SpotifyRemoveIds) Spotify(id, "−  Spotify");
        foreach (var path in item.LocalRemoveFiles) Local(path, "−  Local");
        foreach (var ignored in item.IgnoredByRule)
        {
            var name = ignored;
            foreach (var suffix in new[] { " → Spotify", " → local", " removal" })
                if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
            var (artist, title) = SplitName(name);
            Add(DetailSection.Excluded, "Never sync", artist, title, "Excluded — tick to sync it again");
        }
        foreach (var entry in item.PlatformSpecific)
        {
            var colon = entry.IndexOf(": ", StringComparison.Ordinal);
            var kind = colon > 0 ? entry[..colon] : "";
            var (artist, title) = SplitName(colon > 0 ? entry[(colon + 2)..] : entry);
            var where = kind switch
            {
                "Spotify-only/no local file" => "Only on Spotify — no local file",
                "MusicBee-only/unmatched" => "Only in MusicBee — no Spotify match",
                "Local-only/not liked" => "Only in MusicBee — not in your Spotify Likes",
                _ => kind
            };
            Add(DetailSection.Protected, "Protected", artist, title, where);
        }
        var changed = item.SpotifyAddIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in item.DesiredSpotifyIds.Where(id => !changed.Contains(id)))
            if (previewSpotifyTracks.TryGetValue(id, out var t)) Add(DetailSection.InSync, "In sync", string.Join(", ", t.Artists), t.Title, "Already on both sides");

        var p = Theme.P;
        var parts = new List<string> { item.Name, item.HasBaseline ? "deletion baseline ready" : "first run — add-only",
            $"Spotify +{item.SpotifyAddIds.Count}/−{item.SpotifyRemoveIds.Count}", $"Local +{item.LocalAddFiles.Count}/−{item.LocalRemoveFiles.Count}" };
        if (item.IgnoredByRule.Count > 0) parts.Add($"{item.IgnoredByRule.Count} excluded");
        if (planAppliedAt is { } applied) parts.Add($"applied {applied:HH:mm}");
        if (item.IsBlocked) parts.Add("BLOCKED: " + item.BlockReason);
        parts.AddRange(item.Warnings.Select(w => "⚠ " + w));
        detailSummary.Text = string.Join("   ·   ", parts);
        detailSummary.ForeColor = item.IsBlocked ? p.Err : item.Warnings.Count > 0 ? p.Warn : p.Muted;
        detailTable.Items = rows;
    }

    private void ToggleDetailRows(List<InsightRow> rows, string detailPlaylist)
    {
        if (prepared is null || detailPlaylist.Length == 0) return;
        var exclude = rows.Where(r => Info(r)?.Section == DetailSection.Change)
            .Select(r => new SyncExclusion { Playlist = detailPlaylist, Artist = r.Artist.Split(',')[0].Trim(), Title = r.Title }).ToList();
        var include = new List<SyncExclusion>();
        var rules = service.LoadSyncExclusions();
        foreach (var r in rows.Where(r => Info(r)?.Section == DetailSection.Excluded))
            include.AddRange(rules.Where(rule => rule.Matches(detailPlaylist, r.Artist.Length > 0 ? r.Artist : null, r.Title)));
        var added = exclude.Count > 0 ? service.AddSyncExclusions(exclude) : 0;
        var removed = include.Count > 0 ? service.RemoveSyncExclusions(include) : 0;
        if (added + removed == 0) return;
        AppendLog($"Never-sync list updated for '{detailPlaylist}': {added} track(s) excluded, {removed} track(s) syncing again.");
        service.RebuildPlan(prepared);
        PopulatePlan(prepared.Plan, detailPlaylist);
        SavePreviewSnapshot();
    }
}
