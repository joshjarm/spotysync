using System.Text.Json;
using SpotySync;

namespace SpotySyncApp;

/// <summary>
/// Keeps the last sync preview (and the Library analysis built from it) across restarts, in
/// Data\last-preview.json. A restored preview is for viewing: Apply needs a fresh build first, because the
/// library and playlists may have changed since.
/// </summary>
public sealed partial class MainForm
{
    private DateTime? previewRestoredAt;

    private sealed class PreviewSnapshot
    {
        public DateTime BuiltAt { get; set; }
        public DateTime? AppliedAt { get; set; }
        public bool LibraryAnalysed { get; set; }
        public PreparedSync? Prepared { get; set; }
    }

    private string PreviewSnapshotPath => Path.Combine(settings.DataDirectory, "last-preview.json");
    private DateTime previewBuiltAt = DateTime.Now;
    private static readonly JsonSerializerOptions SnapshotJson = new() { IncludeFields = true };

    /// <summary>Call after a new preview was built (or applied / the library analyzed). Writes in the background.</summary>
    private void SavePreviewSnapshot(bool newlyBuilt = false)
    {
        if (newlyBuilt) { previewBuiltAt = DateTime.Now; previewRestoredAt = null; }
        if (prepared is null) return;
        var snapshot = new PreviewSnapshot { BuiltAt = previewBuiltAt, AppliedAt = planAppliedAt, LibraryAnalysed = libraryReport is not null, Prepared = prepared };
        var path = PreviewSnapshotPath;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, snapshot, SnapshotJson);
                File.Move(temp, path, true);
            }
            catch { /* a missing snapshot only means the next launch starts empty */ }
        });
    }

    private void DeletePreviewSnapshot()
    {
        try { if (File.Exists(PreviewSnapshotPath)) File.Delete(PreviewSnapshotPath); } catch { }
    }

    /// <summary>Restores the last preview + library analysis at launch (view-only until rebuilt).</summary>
    private void RestorePreviewSnapshot()
    {
        if (prepared is not null) return;
        PreviewSnapshot? snapshot;
        try
        {
            if (!File.Exists(PreviewSnapshotPath)) return;
            using var stream = File.OpenRead(PreviewSnapshotPath);
            snapshot = JsonSerializer.Deserialize<PreviewSnapshot>(stream, SnapshotJson);
        }
        catch (Exception ex) { AppendLog("Could not restore the last sync preview: " + ex.Message); return; }
        if (snapshot?.Prepared is null) return;
        prepared = snapshot.Prepared;
        previewBuiltAt = snapshot.BuiltAt;
        previewRestoredAt = snapshot.BuiltAt;
        planAppliedAt = snapshot.AppliedAt;
        PopulatePlan(prepared.Plan);
        if (snapshot.LibraryAnalysed)
        {
            BuildLibraryReport();
            if (libraryReport is not null) { libraryReport.CreatedAt = snapshot.BuiltAt; UpdateLibraryState(); }
        }
        AppendLog($"Restored the sync preview from {snapshot.BuiltAt:ddd d MMM HH:mm}. Rebuild it (or Force full refresh) to apply changes.");
    }
}
