namespace SpotySync;

public sealed class SyncExecutor
{
    private readonly PortableSettings settings;
    private readonly SpotifyApi spotify;
    private readonly MusicBeeBridgeClient? musicBee;
    private readonly M3uStore m3u;
    private readonly Action<string> log;

    public SyncExecutor(PortableSettings settings, SpotifyApi spotify, MusicBeeBridgeClient? musicBee, Action<string>? log = null)
    {
        this.settings = settings;
        this.spotify = spotify;
        this.musicBee = musicBee;
        this.log = log ?? (_ => { });
        m3u = new M3uStore(settings, log);
    }

    public async Task<SyncExecutionResult> ExecuteAsync(SyncPlan plan, IReadOnlyList<LocalTrack> library, bool dryRun, CancellationToken cancellationToken = default)
    {
        var result = new SyncExecutionResult { Success = true };
        var state = JsonStore.Load(settings.StatePath, () => new SyncState());
        if (state.SpotifyUserId.Length > 0 && !state.SpotifyUserId.Equals(plan.SpotifyUserId, StringComparison.OrdinalIgnoreCase))
            state = new SyncState { SpotifyUserId = plan.SpotifyUserId };
        state.SpotifyUserId = plan.SpotifyUserId;
        var metadata = library.ToDictionary(x => SyncPlanner.PathKey(x.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var playlist in plan.Playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (playlist.IsBlocked)
            {
                result.Success = false;
                result.Errors.Add($"{playlist.Name}: {playlist.BlockReason}");
                log($"BLOCKED {playlist.Name}: {playlist.BlockReason}");
                continue;
            }
            try
            {
                log($"{(dryRun ? "Preview" : "Applying")} '{playlist.Name}': Spotify +{playlist.SpotifyAddIds.Count}/-{playlist.SpotifyRemoveIds.Count}, local +{playlist.LocalAddFiles.Count}/-{playlist.LocalRemoveFiles.Count}.");
                if (!dryRun && settings.Direction != SyncDirection.SpotifyToLocal)
                {
                    await spotify.ApplyPlaylistChangesAsync(playlist, plan.SpotifyUserId, cancellationToken);
                    result.SpotifyAdded += playlist.SpotifyAddIds.Count;
                    result.SpotifyRemoved += playlist.SpotifyRemoveIds.Count;
                }

                if (settings.Direction != SyncDirection.LocalToSpotify)
                {
                    var allowRemovals = settings.EnableRemovals && playlist.HasBaseline;
                    if (settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
                    {
                        if (musicBee is null) throw new InvalidOperationException("MusicBee backend is selected but the bridge client is unavailable.");
                        var changes = await musicBee.ApplyPlaylistAsync(playlist, allowRemovals, settings.MaxRemovalsPerPlaylist, dryRun, cancellationToken);
                        result.LocalAdded += changes.Added;
                        result.LocalRemoved += changes.Removed;
                    }
                    if (settings.LocalBackend is LocalBackendKind.M3U or LocalBackendKind.Both)
                    {
                        var changes = m3u.Apply(playlist, metadata, allowRemovals, settings.MaxRemovalsPerPlaylist, dryRun);
                        if (settings.LocalBackend == LocalBackendKind.M3U)
                        {
                            result.LocalAdded += changes.Added;
                            result.LocalRemoved += changes.Removed;
                        }
                    }
                }
                if (!dryRun)
                {
                    state.Playlists[TextNormalizer.PlaylistKey(playlist.Name)] = new PlaylistBaseline
                    {
                        Name = playlist.Name,
                        SyncMode = $"{settings.LocalBackend}:{settings.Direction}",
                        SpotifyIds = playlist.DesiredSpotifyIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    JsonStore.SaveAtomic(settings.StatePath, state);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"{playlist.Name}: {ex.Message}");
                log($"ERROR {playlist.Name}: {ex.Message}");
            }
        }
        return result;
    }
}
