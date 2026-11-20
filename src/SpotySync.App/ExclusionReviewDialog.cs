using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// Never-sync editor. For one playlist it lists its saved ignore rules (ticked) and the tracks this preview
/// would change (unticked): tick to ignore, untick to un-ignore. Without a playlist it manages every saved
/// rule. Rules live only in this copy's Data\sync-exclusions.txt.
/// </summary>
public sealed class ExclusionReviewDialog : ThemedDialog
{
    private sealed class Entry
    {
        public string Label = "";
        public SyncExclusion Rule = new();
        public bool WasIgnored;
        public bool Checked;
    }

    private readonly CheckedListBox choices = new();
    private readonly FieldBox search = new() { LeadingGlyph = Glyph.Search, ClearButton = true, Placeholder = "Filter tracks…", Height = 34 };
    private readonly Label summary = Ui.Label("", "dim", Fonts.Body(9f), autoSize: false);
    private readonly List<Entry> entries = new();
    private readonly List<Entry> shown = new();
    private bool filling;

    /// <summary>New rules to save (ticked tracks that weren't ignored before).</summary>
    public IReadOnlyList<SyncExclusion> SelectedExclusions { get; private set; } = Array.Empty<SyncExclusion>();
    /// <summary>Existing rules the user unticked.</summary>
    public IReadOnlyList<SyncExclusion> RemovedExclusions { get; private set; } = Array.Empty<SyncExclusion>();

    public ExclusionReviewDialog(string? playlist, IReadOnlyList<TrackReviewItem> items, IReadOnlyList<SyncExclusion>? existing = null)
    {
        Text = playlist is null ? "Ignored tracks" : "Never sync • " + playlist;
        MinimumSize = new Size(640, 480);
        Size = new Size(860, 660);
        Body.Title = playlist ?? "All ignored tracks";
        Body.Eyebrow = "Never sync";
        Body.Glyph = Glyph.Filter;
        Body.Inner = new Padding(20, 6, 20, 16);

        var rules = (existing ?? Array.Empty<SyncExclusion>())
            .Where(r => playlist is null || TextNormalizer.PlaylistKey(r.Playlist) == TextNormalizer.PlaylistKey(playlist)).ToList();
        foreach (var rule in rules.OrderBy(r => r.Playlist, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase))
            entries.Add(new Entry
            {
                Label = (playlist is null ? rule.Playlist + "   •   " : "Ignored   •   ") + $"{rule.Artist} - {rule.Title}",
                Rule = rule, WasIgnored = true, Checked = true
            });
        foreach (var item in items)
        {
            if (playlist is null) break;
            if (rules.Any(r => r.Matches(playlist, item.Artist, item.Title))) continue;
            entries.Add(new Entry
            {
                Label = $"{item.Kind}   •   {item.DisplayName}",
                Rule = new SyncExclusion { Playlist = playlist, Artist = item.Artist, Title = item.Title }
            });
        }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Color.Transparent };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        var copy = Ui.Label(playlist is null
                ? "Untick a track to sync it again. Rules are stored in this copy of SpotySync only."
                : "Ticked tracks are never added or removed in this playlist. Untick a saved rule to sync that track again.",
            "muted", Fonts.Body(9.5f), autoSize: false);
        copy.Dock = DockStyle.Fill;
        root.Controls.Add(copy, 0, 0);
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 0, 12, 6);
        summary.Dock = DockStyle.Fill; summary.TextAlign = ContentAlignment.MiddleRight;
        searchRow.Controls.Add(search, 0, 0); searchRow.Controls.Add(summary, 1, 0);
        root.Controls.Add(searchRow, 0, 1);

        choices.Dock = DockStyle.Fill;
        choices.BackColor = ColorX.Mix(Theme.P.Panel, Theme.P.Well, 0.6);
        choices.ForeColor = Theme.P.Ink;
        choices.BorderStyle = BorderStyle.None;
        choices.CheckOnClick = true;
        choices.IntegralHeight = false;
        choices.HorizontalScrollbar = true;
        choices.Font = Fonts.Body(9.75f);
        Ui.DarkScrollbars(choices);
        choices.ItemCheck += (_, e) =>
        {
            if (filling || e.Index < 0 || e.Index >= shown.Count) return;
            shown[e.Index].Checked = e.NewValue == CheckState.Checked;
            BeginInvoke(UpdateSummary);
        };
        var listCard = new Card { Dock = DockStyle.Fill, Raised = true, Radius = 10, Inner = new Padding(10, 8, 6, 8) };
        listCard.Controls.Add(choices);
        root.Controls.Add(listCard, 0, 2);

        var actions = new Row(rightToLeft: true) { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        var save = RoundedButton.Make("Save", ButtonKind.Primary, Glyph.Save, (_, _) => SaveSelection());
        var cancel = RoundedButton.Make("Cancel", ButtonKind.Secondary);
        cancel.DialogResult = DialogResult.Cancel;
        foreach (var b in new[] { save, cancel }) { b.Margin = new Padding(8, 0, 0, 0); actions.Controls.Add(b); }
        root.Controls.Add(actions, 0, 3);
        Body.Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
        search.TextChanged += (_, _) => Fill();
        Fill();
    }

    private void Fill()
    {
        filling = true;
        var words = search.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        shown.Clear();
        shown.AddRange(entries.Where(e => words.All(w => e.Label.Contains(w, StringComparison.OrdinalIgnoreCase))));
        choices.BeginUpdate();
        choices.Items.Clear();
        foreach (var e in shown) choices.Items.Add(e.Label, e.Checked);
        choices.EndUpdate();
        filling = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var ignored = entries.Count(e => e.Checked);
        var added = entries.Count(e => e.Checked && !e.WasIgnored);
        var removed = entries.Count(e => !e.Checked && e.WasIgnored);
        summary.Text = $"{ignored} ignored" + (added + removed > 0 ? $"  ·  +{added} / −{removed} unsaved" : "");
    }

    private void SaveSelection()
    {
        SelectedExclusions = entries.Where(e => e.Checked && !e.WasIgnored).Select(e => e.Rule).ToList();
        RemovedExclusions = entries.Where(e => !e.Checked && e.WasIgnored).Select(e => e.Rule).ToList();
        DialogResult = DialogResult.OK;
        Close();
    }
}
