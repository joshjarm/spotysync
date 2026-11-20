namespace SpotySync;

public sealed class PreparedSync
{
    public SyncPlan Plan { get; set; } = new();
    public List<LocalTrack> Library { get; set; } = new();
    public List<PlaylistInfo> SpotifyPlaylists { get; set; } = new();
    public List<LocalPlaylist> LocalPlaylists { get; set; } = new();
    public LikedTrackCache Liked { get; set; } = new();
}

public sealed class SpotySyncService : IDisposable
{
    private readonly PortableSettings settings;
    private readonly Action<string> log;
    private readonly SpotifyAuth auth;
    private readonly SpotifyApi spotify;
    private readonly MusicBeeBridgeClient musicBee;
    private readonly M3uStore m3u;
    private readonly LibraryScanner scanner;
    private readonly Downloader downloader;
    private readonly SyncExclusionStore exclusions;

    public SpotySyncService(PortableSettings settings, Action<string>? log = null)
    {
        this.settings = settings;
        this.log = log ?? (_ => { });
        auth = new SpotifyAuth(settings, this.log);
        spotify = new SpotifyApi(settings, auth, this.log);
        musicBee = new MusicBeeBridgeClient(settings, this.log);
        m3u = new M3uStore(settings, this.log);
        scanner = new LibraryScanner(settings, this.log);
        downloader = new Downloader(settings, this.log);
        exclusions = new SyncExclusionStore(settings.SyncExclusionsPath);
    }

    public bool HasSavedLogin => auth.HasSavedLogin;
    /// <summary>Signs in; returns the redirect pattern Spotify accepted (worth saving as RedirectUri).</summary>
    public Task<string> ConnectSpotifyAsync(CancellationToken cancellationToken = default) => auth.LoginAsync(cancellationToken);
    public void DisconnectSpotify() => auth.Logout();

    public async Task<PreparedSync> PrepareAsync(bool forceSpotifyRefresh = false, bool forceLibraryRefresh = false, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        log("Reading Spotify profile…");
        var profile = await spotify.GetProfileAsync(cancellationToken);
        var liked = await spotify.GetLikedTracksAsync(profile.Id, forceSpotifyRefresh, cancellationToken);
        log("Reading Spotify playlists (unchanged snapshots come from the portable cache)…");
        var spotifyPlaylists = await spotify.GetPlaylistsAsync(profile.Id, forceSpotifyRefresh, cancellationToken);

        List<LocalTrack> library;
        List<LocalPlaylist> localPlaylists;
        if (settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
        {
            log("Reading MusicBee library and playlists through the local bridge…");
            var snapshot = await musicBee.GetSnapshotAsync(cancellationToken);
            library = snapshot.Tracks;
            localPlaylists = snapshot.Playlists;
        }
        else
        {
            library = await scanner.ScanAsync(forceLibraryRefresh, cancellationToken);
            localPlaylists = m3u.ReadPlaylists();
        }

        var state = JsonStore.Load(settings.StatePath, () => new SyncState());
        if (state.SpotifyUserId.Length > 0 && !state.SpotifyUserId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            log("Spotify account changed; ignoring the prior account's sync baselines.");
            state = new SyncState { SpotifyUserId = profile.Id };
        }
        var plan = new SyncPlanner().Build(profile.Id, spotifyPlaylists, localPlaylists, library, liked.Tracks, state, settings, exclusions.Load());
        log($"Preview ready: {plan.Playlists.Count} playlists, {plan.TotalChanges} changes, {plan.ConfidentMatches} local tracks matched from Spotify metadata, {plan.UnmatchedLocal} local-only/unmatched.");
        return new PreparedSync { Plan = plan, Library = library, SpotifyPlaylists = spotifyPlaylists, LocalPlaylists = localPlaylists, Liked = liked };
    }

    public SyncPlan RebuildPlan(PreparedSync prepared)
    {
        var state = JsonStore.Load(settings.StatePath, () => new SyncState());
        prepared.Plan = new SyncPlanner().Build(prepared.Plan.SpotifyUserId, prepared.SpotifyPlaylists,
            prepared.LocalPlaylists, prepared.Library, prepared.Liked.Tracks, state, settings, exclusions.Load());
        return prepared.Plan;
    }

    /// <summary>Compares a loaded library with the disk (safe to run on a worker thread; changes nothing).</summary>
    public Task<(List<LocalTrack> Added, List<LocalTrack> Removed)> CompareLibraryWithDiskAsync(IReadOnlyCollection<LocalTrack> library, CancellationToken cancellationToken = default)
        => scanner.ReconcileAsync(library, cancellationToken);

    /// <summary>Applies <see cref="CompareLibraryWithDiskAsync"/> results to a preview and optionally rebuilds its plan.</summary>
    public void ApplyLibraryChanges(PreparedSync prepared, IReadOnlyCollection<LocalTrack> added, IReadOnlyCollection<LocalTrack> removed, bool rebuildPlan)
    {
        if (added.Count == 0 && removed.Count == 0) return;
        var gone = new HashSet<string>(removed.Select(x => SyncPlanner.PathKey(x.Path)), StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        prepared.Library = prepared.Library.Where(track => !gone.Contains(SyncPlanner.PathKey(track.Path)))
            .Concat(added)
            .Where(track => present.Add(SyncPlanner.PathKey(track.Path)))
            .ToList();
        if (rebuildPlan) RebuildPlan(prepared);
    }

    public int AddSyncExclusions(IEnumerable<SyncExclusion> items) => exclusions.Add(items);
    public int RemoveSyncExclusions(IEnumerable<SyncExclusion> items) => exclusions.Remove(items);
    public IReadOnlyList<SyncExclusion> LoadSyncExclusions() => exclusions.Load();
    public string EnsureSyncExclusionsDocument()
    {
        exclusions.EnsureExists();
        return settings.SyncExclusionsPath;
    }

    public Task<SyncExecutionResult> ExecuteAsync(PreparedSync prepared, bool dryRun, CancellationToken cancellationToken = default)
    {
        var executor = new SyncExecutor(settings, spotify, settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both ? musicBee : null, log);
        return executor.ExecuteAsync(prepared.Plan, prepared.Library, dryRun, cancellationToken);
    }

    public async Task<BackupResult> BackupRecentLikesAsync(bool forceLikedRefresh = false, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var profile = await spotify.GetProfileAsync(cancellationToken);
        var liked = await spotify.GetLikedTracksAsync(profile.Id, forceLikedRefresh, cancellationToken);
        List<LocalTrack> library;
        MusicBeeSnapshot? musicBeeSnapshot = null;
        if (settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
        {
            musicBeeSnapshot = await musicBee.GetSnapshotAsync(cancellationToken);
            AdoptMusicFolderFromMusicBeeIfNeeded(musicBeeSnapshot.Tracks);
            // Backup means "present on disk", not merely "already indexed by
            // MusicBee". Seed the disk scan with MusicBee metadata so thousands of
            // existing files remain fast while unindexed files are still detected.
            var diskLibrary = await scanner.ScanAsync(false, musicBeeSnapshot.Tracks, cancellationToken);
            library = LibraryScanner.MergeExisting(musicBeeSnapshot.Tracks, diskLibrary);
            if (library.Count > diskLibrary.Count)
                log($"Included {library.Count - diskLibrary.Count} verified MusicBee file(s) outside the configured scan result.");
        }
        else library = await scanner.ScanAsync(false, cancellationToken);

        var initializedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (musicBeeSnapshot is not null && settings.ImportDownloadsIntoMusicBee)
        {
            var known = musicBeeSnapshot.Tracks.Select(x => SyncPlanner.PathKey(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var diskOnly = library.Where(x => !known.Contains(SyncPlanner.PathKey(x.Path))).Select(x => x.Path).ToList();
            if (diskOnly.Count > 0)
            {
                log($"MusicBee is missing {diskOnly.Count} audio file(s) found in the configured music folder; importing them now.");
                try
                {
                    var imported = await ImportIntoMusicBeeAsync(diskOnly, cancellationToken);
                    if (IsMusicBeeInitializationComplete(imported, diskOnly.Count))
                        initializedThisRun.UnionWith(diskOnly.Select(SyncPlanner.PathKey));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log("WARNING: MusicBee import/initialization failed; backup will continue: " + ex.Message); }
            }
        }

        var state = JsonStore.Load(settings.StatePath, () => new SyncState());
        if (state.SpotifyUserId.Length > 0 && !state.SpotifyUserId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) state = new SyncState();
        state.SpotifyUserId = profile.Id;
        state.PendingTemporaryDownloadPlaylistIds ??= new List<string>();
        await CleanupPendingDownloadPlaylistsAsync(profile.Id, state, cancellationToken);
        var cutoff = FindRecentDownloadCutoff(library, state);
        // Spotify returns Likes newest-first, and spot2yt preserves the submitted
        // playlist order when finalizing file timestamps. Keep newest first so a
        // date-sorted MusicBee library displays the batch in Spotify order.
        var recent = BackupWindow.RecentLikesNewestFirst(liked.Tracks, cutoff);
        var confidentMatches = new TrackMatcher(liked.Tracks).MatchLibrary(library)
            .Where(x => x.IsConfident && x.Spotify is not null).ToList();
        var localMatches = confidentMatches.Select(x => x.Spotify!.SpotifyId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedPaths = confidentMatches.GroupBy(x => x.Spotify!.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Local.Path, StringComparer.OrdinalIgnoreCase);
        var initializationRecoveryFiles = recent
            .Where(track => !state.LastSuccessfulBackupMarker.HasValue ||
                !track.AddedAt.HasValue || track.AddedAt > state.LastSuccessfulBackupMarker)
            .Select(track => matchedPaths.GetValueOrDefault(track.SpotifyId))
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>().Where(path => !initializedThisRun.Contains(SyncPlanner.PathKey(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = recent.Where(x => !localMatches.Contains(x.SpotifyId)).ToList();
        var result = new BackupResult { Candidates = candidates.Count, AlreadyLocal = recent.Count - candidates.Count };
        result.ResolvedMusicFolder = settings.ResolvePath(settings.MusicFolder);
        log($"Recent Likes backup: {recent.Count} tracks since {cutoff:u}; {result.AlreadyLocal} already local; {candidates.Count} to download.");
        if (candidates.Count == 0)
        {
            if (settings.ImportDownloadsIntoMusicBee && settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both && initializationRecoveryFiles.Count > 0)
            {
                log($"Found {initializationRecoveryFiles.Count} already-downloaded recent Like(s) from an unfinished backup; completing MusicBee initialization now.");
                try
                {
                    var recovered = await ImportIntoMusicBeeAsync(initializationRecoveryFiles, cancellationToken);
                    var complete = IsMusicBeeInitializationComplete(recovered, initializationRecoveryFiles.Count);
                    if (!complete)
                    {
                        log("WARNING: MusicBee initialization is still incomplete; audio files were kept and the backup marker remains unchanged for another safe retry.");
                        result.Success = true;
                        return result;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log("WARNING: MusicBee initialization is still incomplete; audio files were kept and will be retried without re-downloading: " + ex.Message);
                    result.Success = true;
                    return result;
                }
            }
            AdvanceBackupMarker(state, liked);
            result.Success = true;
            return result;
        }

        PlaylistInfo? temporary = null;
        try
        {
            var configured = settings.ResolvePath(settings.DownloaderPath);
            var isSpot2Yt = Path.GetFileName(configured).StartsWith("spot2yt", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<string> urls;
            if (isSpot2Yt && settings.CreateTemporaryDownloadPlaylist && candidates.Count > 1)
            {
                temporary = await spotify.CreateTemporaryPlaylistAsync(candidates, profile.Id, cancellationToken);
                state.PendingTemporaryDownloadPlaylistIds.Add(temporary.Id);
                state.PendingTemporaryDownloadPlaylistIds = state.PendingTemporaryDownloadPlaylistIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                JsonStore.SaveAtomic(settings.StatePath, state);
                urls = new[] { temporary.Url };
                log($"Created one temporary playlist for {candidates.Count} concurrent spot2yt downloads.");
            }
            else urls = candidates.Select(x => x.Url.Length > 0 ? x.Url : "https://open.spotify.com/track/" + x.SpotifyId).ToList();

            var download = await downloader.RunAsync(urls, candidates, cancellationToken);
            AdoptDownloaderOutputFolder(download.OutputFolder);
            result.ResolvedMusicFolder = settings.ResolvePath(settings.MusicFolder);
            result.ExitCode = download.ExitCode;
            // Verify against the configured download folder before MusicBee has an
            // opportunity to auto-organize/move a newly imported file.
            var verifiedLibrary = await scanner.ScanAsync(false, library, cancellationToken);
            var verification = ResolveVerifiedDownloads(candidates, verifiedLibrary, download.ChangedAudio);
            var verifiedIds = verification.Ids;
            var verifiedCandidates = candidates.Count(x => verifiedIds.Contains(x.SpotifyId));
            var downloadedFiles = download.ChangedAudio.Concat(verification.Files).Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var importFiles = downloadedFiles.Concat(initializationRecoveryFiles).Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Files = importFiles;
            result.NewAudioFiles = downloadedFiles.Count;
            var initializationComplete = true;
            // Import successful partial work even when another candidate failed.
            if (settings.ImportDownloadsIntoMusicBee && settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both && importFiles.Count > 0)
            {
                try
                {
                    var imported = await ImportIntoMusicBeeAsync(importFiles, cancellationToken);
                    initializationComplete = IsMusicBeeInitializationComplete(imported, importFiles.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    initializationComplete = false;
                    log("WARNING: downloaded files were kept, but MusicBee import/initialization failed: " + ex.Message);
                }
            }
            if (verifiedCandidates < candidates.Count)
            {
                result.Error = $"{download.DownloaderName} exit code {download.ExitCode}; reported errors {download.ReportedErrors}; " +
                    $"verified {verifiedCandidates}/{candidates.Count} requested track(s) in the music folder. " +
                    "Successful partial files were kept/imported, but the backup marker was not advanced.";
                log("Backup verification failed: " + result.Error);
                return result;
            }
            if (download.ExitCode != 0 || download.ReportedErrors > 0)
                log($"Downloader reported exit code {download.ExitCode} with {download.ReportedErrors} error line(s), but all {verifiedCandidates} requested tracks were verified on disk; continuing safely.");
            if (initializationComplete)
            {
                AdvanceBackupMarker(state, liked);
            }
            else log("WARNING: Audio backup is complete, but the durable marker was not advanced so MusicBee initialization can retry without downloading the files again.");
            result.Success = true;
            log(initializationComplete
                ? "Recent Likes backup completed and the durable marker was advanced."
                : "Recent Likes audio backup completed; MusicBee initialization remains pending.");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            log("Backup failed: " + ex.Message);
            return result;
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await spotify.RemovePlaylistFromLibraryAsync(temporary.Id, cleanup.Token);
                    state.PendingTemporaryDownloadPlaylistIds.RemoveAll(id => id.Equals(temporary.Id, StringComparison.OrdinalIgnoreCase));
                    JsonStore.SaveAtomic(settings.StatePath, state);
                    log("Removed the temporary Spotify download playlist.");
                }
                catch (Exception ex) { log("WARNING: temporary playlist cleanup failed: " + ex.Message); }
            }
        }
    }

    // Large requests (e.g. thousands of missing Likes) run in batches: one downloader
    // process per batch keeps the Windows command line short, and each batch is
    // imported into MusicBee before the next starts, so stopping loses nothing.
    private const int DownloadBatchSize = 50;
    private bool spotifyTrackLookupBlocked;

    public async Task<ManualDownloadResult> DownloadLinksAsync(IEnumerable<string> links, CancellationToken cancellationToken = default)
    {
        var urls = links.Select(x => CleanDownloadLink(x.Trim())).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0) return new ManualDownloadResult { Error = "Paste at least one Spotify, YouTube, or other supported media link." };
        var invalid = urls.FirstOrDefault(value =>
            !value.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) &&
            (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")));
        if (invalid is not null) return new ManualDownloadResult { Error = "This is not a supported link: " + invalid };
        log($"Download queue: {urls.Count} song(s).");
        if (urls.Count <= DownloadBatchSize) return await DownloadLinkBatchAsync(urls, cancellationToken);

        var batches = urls.Chunk(DownloadBatchSize).ToList();
        log($"Large download: {urls.Count} links in {batches.Count} batches of up to {DownloadBatchSize}. " +
            "Each batch is added to MusicBee before the next one starts, so you can cancel at any time and continue later.");
        var total = new ManualDownloadResult { Success = true };
        var troubledBatches = 0;
        for (var i = 0; i < batches.Count; i++)
        {
            await DownloadProcesses.WaitIfPausedAsync(cancellationToken); // a paused run starts no new batch
            cancellationToken.ThrowIfCancellationRequested();
            log($"Batch {i + 1} of {batches.Count} ({total.NewAudioFiles} new song(s) so far)…");
            var part = await DownloadLinkBatchAsync(batches[i].ToList(), cancellationToken);
            total.NewAudioFiles += part.NewAudioFiles;
            total.ImportedFiles += part.ImportedFiles;
            total.PrimedFiles += part.PrimedFiles;
            total.Files.AddRange(part.Files);
            if (part.ResolvedMusicFolder.Length > 0) total.ResolvedMusicFolder = part.ResolvedMusicFolder;
            if (!part.Success)
            {
                troubledBatches++;
                if (part.ExitCode != 0) total.ExitCode = part.ExitCode;
                log($"WARNING: batch {i + 1} of {batches.Count}: {part.Error} Continuing with the next batch.");
            }
        }
        if (troubledBatches > 0)
        {
            total.Success = false;
            total.Error = $"{troubledBatches} of {batches.Count} batch(es) had songs that could not be downloaded (see the Activity log). " +
                $"Kept {total.NewAudioFiles} new audio file(s).";
        }
        else log($"Large download complete: {total.NewAudioFiles} audio file(s), {total.ImportedFiles} verified in MusicBee.");
        return total;
    }

    /// <summary>Drops share-tracking query strings (?si=…&amp;pt=…) from Spotify links; spotDL can fail on them.</summary>
    private static string CleanDownloadLink(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.Query.Length > 0 || uri.Fragment.Length > 0))
            return uri.GetLeftPart(UriPartial.Path);
        return value;
    }

    private async Task<ManualDownloadResult> DownloadLinkBatchAsync(List<string> urls, CancellationToken cancellationToken)
    {
        var result = new ManualDownloadResult();
        try
        {
            var spotifyIds = urls.Select(url => SpotifyIdExtractor.FromText(url)).Where(id => id.Length > 0).ToList();
            var expectedTracks = new List<TrackInfo>();
            if (spotifyIds.Count > 0 && !spotifyTrackLookupBlocked)
            {
                try { expectedTracks = await spotify.GetTracksAsync(spotifyIds, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // spot2yt can still resolve the link itself, and its readable
                    // skipped-track line is sufficient for the YouTube fallback.
                    log("WARNING: Spotify metadata lookup failed; continuing with downloader metadata: " + ex.Message);
                    if (ex.Message.Contains("403")) spotifyTrackLookupBlocked = true; // don't repeat it for every batch
                }
            }
            List<LocalTrack> beforeLibrary = new();
            if (settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
            {
                var snapshot = await musicBee.GetSnapshotAsync(cancellationToken);
                AdoptMusicFolderFromMusicBeeIfNeeded(snapshot.Tracks);
                beforeLibrary = LibraryScanner.MergeExisting(snapshot.Tracks, await scanner.ScanAsync(false, snapshot.Tracks, cancellationToken));
            }
            else if (expectedTracks.Count > 0) beforeLibrary = await scanner.ScanAsync(false, cancellationToken);
            var beforeIds = expectedTracks.Count == 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) :
                new TrackMatcher(expectedTracks).MatchLibrary(beforeLibrary).Where(match => match.IsConfident && match.Spotify is not null)
                    .Select(match => match.Spotify!.SpotifyId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            log($"Manual download: {urls.Count} link(s) queued.");
            var download = await downloader.RunManualAsync(urls, expectedTracks, cancellationToken);
            AdoptDownloaderOutputFolder(download.OutputFolder);
            result.ResolvedMusicFolder = settings.ResolvePath(settings.MusicFolder);
            result.ExitCode = download.ExitCode;
            var afterLibrary = expectedTracks.Count > 0 ? await scanner.ScanAsync(false, beforeLibrary, cancellationToken) : new List<LocalTrack>();
            var verification = ResolveVerifiedDownloads(expectedTracks, afterLibrary, download.ChangedAudio);
            var newlyVerified = verification.Files.Where(path =>
            {
                var local = afterLibrary.FirstOrDefault(track => SyncPlanner.PathKey(track.Path) == SyncPlanner.PathKey(path));
                if (local is null) return true;
                var match = new TrackMatcher(expectedTracks).Match(local);
                return match.Spotify is null || !beforeIds.Contains(match.Spotify.SpotifyId);
            });
            var importFiles = download.ChangedAudio.Concat(newlyVerified).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Files = importFiles;
            result.NewAudioFiles = importFiles.Count;
            if (settings.ImportDownloadsIntoMusicBee && settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both && importFiles.Count > 0)
            {
                try
                {
                    var import = await ImportIntoMusicBeeAsync(importFiles, cancellationToken);
                    result.ImportedFiles = import.Imported;
                    result.PrimedFiles = import.Primed;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log("WARNING: downloaded files were kept, but MusicBee import/initialization failed: " + ex.Message); }
            }
            var allExpectedVerified = expectedTracks.Count > 0 && expectedTracks.All(track => verification.Ids.Contains(track.SpotifyId));
            if ((download.ExitCode != 0 || download.ReportedErrors > 0) && !allExpectedVerified)
            {
                result.Error = $"{download.DownloaderName} exit code {download.ExitCode}; reported errors {download.ReportedErrors}. " +
                    $"Kept {importFiles.Count} successful new/updated audio file(s).";
                return result;
            }
            if (download.ExitCode != 0 || download.ReportedErrors > 0)
                log("Downloader reported an error, but the requested Spotify track(s) were all verified in the music folder.");
            if (importFiles.Count == 0 && !allExpectedVerified)
            {
                result.Error = "The downloader completed but did not create or update an audio file. It may already exist; check the Activity log.";
                return result;
            }
            result.Success = true;
            log($"Manual download complete: {result.NewAudioFiles} audio file(s), {result.ImportedFiles} verified in MusicBee.");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            log("Manual download failed: " + ex.Message);
            return result;
        }
    }

    /// <summary>
    /// Re-downloads songs that SpotySync saved below Premium quality. Each file carries its Spotify
    /// link (spotDL's WOAS tag), so the exact song is fetched again; the new copy replaces the old
    /// file at the same path (MusicBee playlists keep working) only when it is better.
    /// </summary>
    public async Task<QualityUpgradeResult> UpgradeLowQualityAsync(CancellationToken cancellationToken = default)
    {
        const double PremiumKbps = 200;
        var result = new QualityUpgradeResult();
        try
        {
            var premium = await downloader.CheckPremiumAvailableAsync(cancellationToken);
            if (premium != true)
            {
                result.Error = "YouTube isn't offering Premium audio to SpotySync right now, so re-downloading would give the same quality. Nothing was changed.";
                return result;
            }

            var ffprobe = downloader.FfprobePath;
            var library = await scanner.ScanAsync(false, cancellationToken);
            // File size includes cover art, so it over-estimates the audio bitrate: a cheap first filter.
            var suspects = library.Where(track => track.SpotifyId.Length > 0 && track.DurationMs > 0 && File.Exists(track.Path) &&
                    track.Size * 8.0 / (track.DurationMs / 1000.0) / 1000 < PremiumKbps + 60)
                .GroupBy(track => track.SpotifyId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            log($"Quality upgrade: measuring {suspects.Count} possible low-quality song(s)…");
            var candidates = new List<(LocalTrack Track, double Kbps)>();
            foreach (var track in suspects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kbps = await Downloader.AudioKbpsAsync(ffprobe, track.Path, cancellationToken);
                if (kbps > 0 && kbps < PremiumKbps) candidates.Add((track, kbps));
            }
            result.Candidates = candidates.Count;
            if (candidates.Count == 0)
            {
                result.Success = true;
                log("Quality upgrade: every downloaded song is already Premium quality.");
                return result;
            }
            var batches = candidates.Chunk(DownloadBatchSize).ToList();
            log($"Quality upgrade: {candidates.Count} song(s) are below ~{PremiumKbps:0} kbps; re-downloading in {batches.Count} batch(es) of up to {DownloadBatchSize}. " +
                "Cancel at any time; finished songs stay upgraded.");
            var replacedForMusicBee = new List<string>();
            for (var i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log($"Quality upgrade batch {i + 1} of {batches.Count} ({result.Upgraded} upgraded so far)…");
                var batch = batches[i];
                DownloadRunResult download;
                try { download = await downloader.RunSpotifyReplaceAsync(batch.Select(item => "https://open.spotify.com/track/" + item.Track.SpotifyId).ToList(), cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log($"WARNING: quality upgrade batch {i + 1} failed; continuing: {ex.Message}");
                    result.Failed += batch.Length;
                    continue;
                }
                var changed = download.ChangedAudio.Where(File.Exists).ToList();
                var changedTracks = changed.Count == 0 ? new List<LocalTrack>() :
                    (await scanner.ScanAsync(false, library, cancellationToken))
                        .Where(track => changed.Any(path => SyncPlanner.PathKey(path) == SyncPlanner.PathKey(track.Path))).ToList();
                foreach (var (track, oldKbps) in batch)
                {
                    var fresh = changedTracks.FirstOrDefault(item => item.SpotifyId.Equals(track.SpotifyId, StringComparison.OrdinalIgnoreCase));
                    if (fresh is null) { result.Failed++; continue; }
                    var newKbps = await Downloader.AudioKbpsAsync(ffprobe, fresh.Path, cancellationToken);
                    var samePath = SyncPlanner.PathKey(fresh.Path) == SyncPlanner.PathKey(track.Path);
                    if (newKbps < PremiumKbps)
                    {
                        // No better: drop the duplicate copy (the original file was overwritten in place already if same path).
                        if (!samePath) { try { File.Delete(fresh.Path); } catch { } }
                        result.StillLow++;
                        continue;
                    }
                    if (!samePath)
                    {
                        // MusicBee may have organised the original elsewhere: put the better copy at the original path.
                        try { File.Move(fresh.Path, track.Path, overwrite: true); }
                        catch (Exception ex) { log($"WARNING: could not replace {track.Path}: {ex.Message}"); result.Failed++; continue; }
                    }
                    replacedForMusicBee.Add(track.Path);
                    result.Upgraded++;
                    log($"Upgraded {track.DisplayName}: {oldKbps:0} → {newKbps:0} kbps.");
                }
            }
            if (replacedForMusicBee.Count > 0 && settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
            {
                try { await ImportIntoMusicBeeAsync(replacedForMusicBee, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log("WARNING: songs were upgraded, but refreshing them in MusicBee failed: " + ex.Message); }
            }
            result.Success = result.Failed == 0 && result.StillLow == 0;
            if (!result.Success) result.Error = $"{result.StillLow} song(s) had no better version and {result.Failed} could not be re-downloaded (see the Activity log).";
            log($"Quality upgrade complete: {result.Upgraded} of {result.Candidates} upgraded.");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            log("Quality upgrade failed: " + ex.Message);
            return result;
        }
    }

    private static readonly HashSet<string> SoundtrackAudio =new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wav", ".wma", ".aac", ".aiff", ".wv" };
    private static readonly string[] SidecarExtensions = [".lrc", ".txt"];

    /// <summary>Folder name safe for Windows (no reserved characters, no trailing dots/spaces).</summary>
    public static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Trim().Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s{2,}", " ").Trim().TrimEnd('.', ' ');
        return clean;
    }

    public string SoundtrackTarget(string name) => Path.Combine(settings.ResolvePath(settings.SoundtrackFolder), SafeFolderName(name));

    /// <summary>
    /// Moves a saved soundtrack's songs back from the Songs folder (only files written at/after the save,
    /// so older copies such as Liked Songs stay put), then re-imports them into MusicBee at their new place.
    /// Returns how many files were moved back.
    /// </summary>
    public async Task<int> RepairSoundtrackAsync(string name, CancellationToken cancellationToken = default)
    {
        var folderName = SafeFolderName(name);
        var manifest = SoundtrackManifest.Load(settings, folderName) ?? throw new InvalidOperationException($"No song list is saved for “{folderName}”.");
        var target = SoundtrackTarget(folderName);
        var library = settings.ResolvePath(settings.MusicFolder);
        Directory.CreateDirectory(target);
        var moved = new List<string>();
        var notFound = 0;
        foreach (var file in manifest.MissingFrom(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stray = manifest.FindStray(file, library);
            if (stray is null) { notFound++; continue; }
            var destination = Path.Combine(target, file);
            try { File.Move(stray, destination); MoveSidecars(stray, destination); moved.Add(destination); }
            catch (IOException ex) { log($"WARNING: could not move {Path.GetFileName(stray)} back: {ex.Message}"); }
        }
        log($"Soundtrack Saver: moved {moved.Count} song(s) back into {target}" + (notFound > 0 ? $"; {notFound} could not be found in {library}." : "."));
        if (moved.Count > 0 && settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
        {
            try { await ImportIntoMusicBeeAsync(moved, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log("WARNING: the songs are back in the folder, but MusicBee could not be updated yet: " + ex.Message); }
        }
        return moved.Count;
    }

    /// <summary>
    /// Downloads a whole playlist/album/soundtrack, keeps it together in its own folder under the
    /// soundtrack root (e.g. Music\Blockheads), lets MusicBee import + initialize it, and moves back any
    /// file MusicBee's auto-organise relocates (for example into the Songs folder).
    /// </summary>
    public async Task<SoundtrackResult> SaveSoundtrackAsync(IReadOnlyList<string> links, string name, bool initializeInMusicBee, CancellationToken cancellationToken = default)
    {
        var result = new SoundtrackResult();
        var folderName = SafeFolderName(name);
        if (folderName.Length == 0) return new SoundtrackResult { Error = "Give the soundtrack a folder name, for example “Blockheads”." };
        var target = SoundtrackTarget(folderName);
        var musicFolder = settings.ResolvePath(settings.MusicFolder);
        if (PathsEqual(target, musicFolder) || PathsEqual(target, settings.ResolvePath(settings.SoundtrackFolder)))
            return new SoundtrackResult { Error = "The soundtrack folder must be a new subfolder, not the Songs/library folder itself." };
        var urls = links.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0) return new SoundtrackResult { Error = "Paste a Spotify, YouTube or other playlist/album link." };
        result.Folder = target;
        try
        {
            Directory.CreateDirectory(target);
            log($"Soundtrack Saver: downloading {urls.Count} link(s) for “{folderName}” → {target}");
            var startedUtc = DateTime.UtcNow;
            var download = await downloader.RunManualAsync(urls, null, cancellationToken);
            var fresh = download.ChangedAudio.Where(File.Exists).Where(f => SoundtrackAudio.Contains(Path.GetExtension(f)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (fresh.Count == 0)
            {
                result.Error = "The downloader finished but produced no new audio files" + (download.ExitCode != 0 ? $" (exit code {download.ExitCode})." : ". They may already exist in your library.");
                log("WARNING: " + result.Error);
                return result;
            }

            // Move everything into the soundtrack folder before MusicBee sees it.
            foreach (var file in fresh)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Files.Add(MoveInto(file, target));
            }
            log($"Soundtrack Saver: moved {result.Files.Count} file(s) into {target}.");
            // remember the songs so a later "Repair" can bring back any that end up in the Songs folder
            var manifest = SoundtrackManifest.Load(settings, folderName) ?? new SoundtrackManifest { Name = folderName, SavedUtc = startedUtc };
            if (manifest.SavedUtc == default || manifest.SavedUtc > startedUtc) manifest.SavedUtc = startedUtc;
            manifest.Files.AddRange(result.Files.Select(Path.GetFileName).OfType<string>());
            manifest.Save(settings);

            if (initializeInMusicBee && settings.LocalBackend is LocalBackendKind.MusicBee or LocalBackendKind.Both)
            {
                var import = await ImportIntoMusicBeeAsync(result.Files, cancellationToken);
                result.ImportedFiles = import.Imported; result.PrimedFiles = import.Primed;

                // Guard: MusicBee auto-organise may move freshly imported files (e.g. into Songs). Put them back.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    var missing = result.Files.Where(f => !File.Exists(f)).ToList();
                    if (missing.Count == 0) break;
                    var restored = new List<string>();
                    foreach (var expected in missing)
                    {
                        var found = FindRelocated(expected, musicFolder, settings.ResolvePath(settings.SoundtrackFolder));
                        if (found is null) continue;
                        // MusicBee deletes the soundtrack folder once it has emptied it, so recreate it first
                        try { Directory.CreateDirectory(Path.GetDirectoryName(expected)!); File.Move(found, expected); MoveSidecars(found, expected); restored.Add(expected); }
                        catch (IOException ex) { log("WARNING: could not move a relocated file back: " + ex.Message); }
                    }
                    result.Relocated += restored.Count;
                    if (restored.Count == 0) break;
                    log($"Soundtrack Saver: MusicBee moved {restored.Count} file(s) out of the soundtrack folder; moved them back and re-pointing MusicBee.");
                    await ImportIntoMusicBeeAsync(restored, cancellationToken);
                }
                result.Escaped = result.Files.Where(f => !File.Exists(f)).ToList();
                if (result.Escaped.Count > 0)
                    log($"WARNING: {result.Escaped.Count} file(s) keep being moved out of {target} by MusicBee. In MusicBee open Tools > Auto-Organise and exclude this folder (or turn off auto-organise for newly added files).");
            }

            result.Success = true;
            log($"Soundtrack Saver: “{folderName}” saved — {result.Files.Count} file(s)" +
                (result.ImportedFiles > 0 ? $", {result.ImportedFiles} in MusicBee, {result.PrimedFiles} initialized" : "") + ".");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            log("Soundtrack Saver failed: " + ex.Message);
            return result;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string MoveInto(string file, string folder)
    {
        var destination = Path.Combine(folder, Path.GetFileName(file));
        if (PathsEqual(file, destination)) return destination;
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length == new FileInfo(file).Length) { File.Delete(file); return destination; }
            var stem = Path.GetFileNameWithoutExtension(file); var ext = Path.GetExtension(file);
            for (var n = 2; File.Exists(destination); n++) destination = Path.Combine(folder, $"{stem} ({n}){ext}");
        }
        File.Move(file, destination);
        MoveSidecars(file, destination);
        return destination;
    }

    private static void MoveSidecars(string from, string to)
    {
        foreach (var ext in SidecarExtensions)
        {
            var side = Path.ChangeExtension(from, ext);
            var dest = Path.ChangeExtension(to, ext);
            try { if (File.Exists(side) && !File.Exists(dest)) File.Move(side, dest); } catch { }
        }
    }

    /// <summary>Finds a file MusicBee relocated (same file name somewhere under the library roots).</summary>
    private static string? FindRelocated(string expected, params string[] roots)
    {
        var name = Path.GetFileName(expected);
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var byName = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault(f => !PathsEqual(f, expected));
                if (byName is not null) return byName;
            }
            catch { }
        }
        return null;
    }

    private async Task<MusicBeeImportResult> ImportIntoMusicBeeAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        // MusicBee's folder monitor can rename a freshly downloaded file after
        // verification but before the bridge request. Preserve the requested
        // path so bridge v18 can resolve it to MusicBee's current library path.
        var paths = files.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count > 0)
            log(settings.PrimeDownloadsInMusicBee
                ? $"MusicBee final stage: importing and silently initializing {paths.Count} track(s) for one second each."
                : $"MusicBee final stage: importing {paths.Count} track(s).");
        MusicBeeImportResult imported;
        for (var attempt = 1; ; attempt++)
        {
            try { imported = await musicBee.ImportFilesAsync(paths, settings.PrimeDownloadsInMusicBee, cancellationToken); break; }
            catch (Exception ex) when (attempt < 3 && ex is InvalidOperationException or TimeoutException or HttpRequestException or ApiException)
            {
                // MusicBee's folder monitor sometimes holds the library for a moment right after a download
                // (e.g. while it drops the downloader's temporary .webm), which makes the API call fail.
                log($"MusicBee was busy; retrying the import in 3 seconds ({attempt}/2)…");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
        log($"MusicBee verified {imported.Imported}/{imported.Requested} imported file(s)" +
            (settings.PrimeDownloadsInMusicBee ? $"; silently initialized {imported.Primed}." : "."));
        if (imported.PrimeWarning.Length > 0) log("WARNING: " + imported.PrimeWarning);
        if (imported.Imported < imported.Requested)
            log($"WARNING: MusicBee did not expose {imported.Requested - imported.Imported} requested file(s) after the retry.");
        return imported;
    }

    private bool IsMusicBeeInitializationComplete(MusicBeeImportResult imported, int expected)
    {
        if (imported.Imported != expected) return false;
        if (!settings.PrimeDownloadsInMusicBee) return true;
        if (imported.Primed != expected) return false;
        return true;
    }

    private static (HashSet<string> Ids, List<string> Files) ResolveVerifiedDownloads(IReadOnlyList<TrackInfo> expected,
        IReadOnlyList<LocalTrack> library, IReadOnlyList<string> changedFiles)
    {
        var expectedIds = expected.Select(track => track.SpotifyId).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new TrackMatcher(expected).MatchLibrary(library)
            .Where(match => match.IsConfident && match.Spotify is not null && expectedIds.Contains(match.Spotify.SpotifyId)).ToList();
        var ids = matches.Select(match => match.Spotify!.SpotifyId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = matches.Select(match => match.Local.Path).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changedKeys = changedFiles.Select(SyncPlanner.PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedTracks = library.Where(track => changedKeys.Contains(SyncPlanner.PathKey(track.Path))).ToList();
        foreach (var track in expected.Where(track => !ids.Contains(track.SpotifyId)))
        {
            var local = changedTracks.FirstOrDefault(candidate => !files.Contains(candidate.Path, StringComparer.OrdinalIgnoreCase) &&
                TrackMatcher.IsLikelyDownloadedMatch(candidate, track));
            if (local is null) continue;
            ids.Add(track.SpotifyId);
            files.Add(local.Path);
        }
        return (ids, files);
    }

    private void AdoptMusicFolderFromMusicBeeIfNeeded(IEnumerable<LocalTrack> tracks)
    {
        var configured = settings.ResolvePath(settings.MusicFolder);
        if (LibraryScanner.ContainsAudio(configured)) return;
        var inferred = LibraryScanner.InferCommonLibraryRoot(tracks);
        if (string.IsNullOrWhiteSpace(inferred) || !LibraryScanner.ContainsAudio(inferred)) return;
        settings.MusicFolder = inferred;
        settings.Save();
        log($"Configured music folder had no audio. Auto-detected the MusicBee library folder instead: {inferred}");
    }

    private void AdoptDownloaderOutputFolder(string reportedFolder)
    {
        if (string.IsNullOrWhiteSpace(reportedFolder) || !Directory.Exists(reportedFolder)) return;
        var configured = settings.ResolvePath(settings.MusicFolder);
        var reported = Path.GetFullPath(reportedFolder);
        if (configured.Equals(reported, StringComparison.OrdinalIgnoreCase)) return;
        settings.MusicFolder = reported;
        settings.Save();
        log($"Updated Music folder to the location reported by the downloader: {reported}");
    }

    private async Task CleanupPendingDownloadPlaylistsAsync(string userId, SyncState state, CancellationToken cancellationToken)
    {
        var discovered = await spotify.GetTemporaryDownloadPlaylistIdsAsync(userId, cancellationToken);
        var pending = state.PendingTemporaryDownloadPlaylistIds.Concat(discovered)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pending.Count == 0) return;
        log($"Cleaning {pending.Count} temporary download playlist(s) left by an interrupted run.");
        foreach (var playlistId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await spotify.RemovePlaylistFromLibraryAsync(playlistId, cancellationToken);
                state.PendingTemporaryDownloadPlaylistIds.RemoveAll(id => id.Equals(playlistId, StringComparison.OrdinalIgnoreCase));
                JsonStore.SaveAtomic(settings.StatePath, state);
                log("Removed an interrupted run's temporary Spotify download playlist.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log("WARNING: could not clean an earlier temporary playlist yet: " + ex.Message);
            }
        }
    }

    private void AdvanceBackupMarker(SyncState state, LikedTrackCache liked)
    {
        var latest = liked.Tracks.Where(x => x.AddedAt.HasValue).OrderByDescending(x => x.AddedAt).FirstOrDefault();
        if (latest is not null)
        {
            state.LastSuccessfulBackupMarker = latest.AddedAt;
            state.LastSuccessfulBackupTrackId = latest.SpotifyId;
        }
        JsonStore.SaveAtomic(settings.StatePath, state);
    }

    private DateTimeOffset FindRecentDownloadCutoff(IEnumerable<LocalTrack> library, SyncState state)
    {
        var recentFiles = library
            .Select(track => (Track: track, Modified: TryGetModifiedTime(track.Path)))
            .Where(item => item.Modified.HasValue)
            .OrderByDescending(item => item.Modified)
            .Take(settings.DownloadAnchorFileCount)
            .ToList();
        var anchorTime = BackupWindow.OldestOfNewest(recentFiles.Select(item => item.Modified!.Value), settings.DownloadAnchorFileCount);
        if (anchorTime.HasValue)
        {
            var anchor = recentFiles[^1];
            var cutoff = anchorTime.Value.AddMinutes(-settings.BackupMarkerOverlapMinutes);
            log($"Recent Likes window anchored to Date modified on the oldest of the {settings.DownloadAnchorFileCount} newest audio files: " +
                $"{Path.GetFileName(anchor.Track.Path)} at {anchorTime.Value:u}.");
            return cutoff;
        }

        var fallback = state.LastSuccessfulBackupMarker?.AddMinutes(-settings.BackupMarkerOverlapMinutes)
            ?? DateTimeOffset.UtcNow.AddDays(-settings.FirstRunLikedLookbackDays);
        log($"Only {recentFiles.Count} local audio timestamp(s) were available; using the durable/fallback cutoff {fallback:u}.");
        return fallback;
    }

    private static DateTimeOffset? TryGetModifiedTime(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var modified = File.GetLastWriteTimeUtc(path);
            return modified.Year <= 1980 ? null : new DateTimeOffset(modified, TimeSpan.Zero);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        spotify.Dispose();
        auth.Dispose();
        musicBee.Dispose();
    }
}
