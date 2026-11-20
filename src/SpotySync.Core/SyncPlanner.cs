namespace SpotySync;

public sealed class SyncPlanner
{
    public SyncPlan Build(
        string spotifyUserId,
        IEnumerable<PlaylistInfo> spotifyPlaylists,
        IEnumerable<LocalPlaylist> localPlaylists,
        IEnumerable<LocalTrack> localLibrary,
        IEnumerable<TrackInfo> likedTracks,
        SyncState state,
        PortableSettings settings,
        IReadOnlyList<SyncExclusion>? exclusions = null)
    {
        var spotifyPlaylistList = spotifyPlaylists.ToList();
        var likedList = likedTracks.Where(x => x.SpotifyId.Length > 0).ToList();
        var liked = likedList
            .ToDictionary(x => x.SpotifyId, x => x, StringComparer.OrdinalIgnoreCase);
        // Matching against playlist metadata as well as Liked Songs lets us detect
        // Spotify tracks that already exist locally even when they are not Liked.
        // Liked Songs remains the gate for new local-to-Spotify additions below.
        var spotifyCatalog = likedList
            .Concat(spotifyPlaylistList.SelectMany(x => x.Tracks))
            .Where(x => x.SpotifyId.Length > 0)
            .GroupBy(x => x.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        var catalogById = spotifyCatalog.ToDictionary(x => x.SpotifyId, StringComparer.OrdinalIgnoreCase);
        var matcher = new TrackMatcher(spotifyCatalog);
        var matches = matcher.MatchLibrary(localLibrary);
        var matchByPath = matches.ToDictionary(x => PathKey(x.Local.Path), StringComparer.OrdinalIgnoreCase);
        var confidentMatches = matches.Where(x => x.IsConfident && x.Spotify is not null).ToList();
        var localBySpotifyId = confidentMatches
            .GroupBy(x => x.Spotify!.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Local, StringComparer.OrdinalIgnoreCase);
        var matchesByRecording = confidentMatches
            .GroupBy(x => TrackMatcher.RecordingGroupKey(x.Spotify!))
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var likedByRecording = liked.Values
            .GroupBy(TrackMatcher.RecordingGroupKey)
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        var remoteGroups = spotifyPlaylistList
            .Where(x => ShouldInclude(x.Name, settings))
            .Where(x => !settings.OwnedPlaylistsOnly || x.IsOwned || x.Collaborative)
            .GroupBy(x => TextNormalizer.PlaylistKey(x.Name))
            .ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.IsOwned).ToList(), StringComparer.OrdinalIgnoreCase);
        var localGroups = localPlaylists
            .Where(x => ShouldIncludeLocal(x.Name, settings))
            .GroupBy(x => TextNormalizer.PlaylistKey(TextNormalizer.MusicBeePlaylistLeaf(x.Name)))
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new SyncPlan { SpotifyUserId = spotifyUserId, Matches = matches.ToList() };
        foreach (var key in remoteGroups.Keys.Union(localGroups.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            remoteGroups.TryGetValue(key, out var remoteCandidates);
            localGroups.TryGetValue(key, out var localCandidates);
            var remote = remoteCandidates?.FirstOrDefault();
            var local = localCandidates?.FirstOrDefault();
            var plan = BuildPlaylist(key, remote, local, matchByPath, localBySpotifyId, matchesByRecording,
                liked, likedByRecording, catalogById, state, settings, exclusions ?? Array.Empty<SyncExclusion>());
            if ((remoteCandidates?.Count ?? 0) > 1)
            {
                plan.IsBlocked = true;
                plan.BlockReason = $"multiple Spotify playlists have the same name '{plan.Name}'";
            }
            if ((localCandidates?.Count ?? 0) > 1)
            {
                plan.IsBlocked = true;
                plan.BlockReason = $"multiple MusicBee playlists share the leaf name '{plan.Name}': " +
                    string.Join(", ", localCandidates!.Select(x => x.Name));
            }
            result.Playlists.Add(plan);
        }
        return result;
    }

    private static PlaylistPlan BuildPlaylist(
        string key,
        PlaylistInfo? remote,
        LocalPlaylist? local,
        Dictionary<string, TrackMatch> matchByPath,
        IReadOnlyDictionary<string, LocalTrack> matchedLocalBySpotifyId,
        IReadOnlyDictionary<string, List<TrackMatch>> matchesByRecording,
        Dictionary<string, TrackInfo> liked,
        IReadOnlyDictionary<string, List<TrackInfo>> likedByRecording,
        Dictionary<string, TrackInfo> catalogById,
        SyncState state,
        PortableSettings settings,
        IReadOnlyList<SyncExclusion> exclusions)
    {
        var name = remote?.Name ?? TextNormalizer.MusicBeePlaylistLeaf(local?.Name) ?? key;
        var plan = new PlaylistPlan
        {
            Name = name,
            SpotifySourceName = remote?.Name ?? "",
            LocalSourceName = local?.Name ?? "",
            SpotifyPlaylistId = remote?.Id ?? "",
            LocalPlaylistId = local?.Id ?? "",
            ExpectedSpotifySnapshot = remote?.SnapshotId ?? "",
            ExpectedLocalDigest = PlaylistDigests.ForFiles(local?.Files ?? new())
        };

        var remoteTracks = (remote?.Tracks ?? new()).Where(x => x.SpotifyId.Length > 0).ToList();
        var remoteById = remoteTracks.GroupBy(x => x.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var remoteByRecording = remoteTracks
            .GroupBy(TrackMatcher.RecordingGroupKey)
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var localBySpotifyId = new Dictionary<string, LocalTrack>(matchedLocalBySpotifyId, StringComparer.OrdinalIgnoreCase);
        foreach (var remoteTrack in remoteTracks)
        {
            if (localBySpotifyId.ContainsKey(remoteTrack.SpotifyId)) continue;
            var recordingKey = TrackMatcher.RecordingGroupKey(remoteTrack);
            var equivalent = matchesByRecording.GetValueOrDefault(recordingKey)?
                .FirstOrDefault(match => TrackMatcher.SameRecording(match.Spotify!, remoteTrack));
            if (equivalent is not null) localBySpotifyId[remoteTrack.SpotifyId] = equivalent.Local;
        }
        var likedEquivalentIds = liked.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var remoteTrack in remoteTracks)
        {
            if (likedEquivalentIds.Contains(remoteTrack.SpotifyId)) continue;
            var recordingKey = TrackMatcher.RecordingGroupKey(remoteTrack);
            if (likedByRecording.GetValueOrDefault(recordingKey)?.Any(likedTrack => TrackMatcher.SameRecording(likedTrack, remoteTrack)) == true)
                likedEquivalentIds.Add(remoteTrack.SpotifyId);
        }

        var spotifyOrder = remoteTracks
            .Select(x => x.SpotifyId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var spotifyIds = spotifyOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var playlistExclusions = exclusions
            .Where(exclusion => TextNormalizer.PlaylistKey(exclusion.Playlist).Equals(key, StringComparison.Ordinal))
            .ToList();
        bool SpotifyExcluded(string id)
        {
            var track = remoteById.GetValueOrDefault(id) ?? catalogById.GetValueOrDefault(id);
            return track is not null && playlistExclusions.Any(exclusion => exclusion.Matches(name, track));
        }
        bool LocalExcluded(string path) =>
            matchByPath.TryGetValue(PathKey(path), out var match) &&
            playlistExclusions.Any(exclusion => exclusion.Matches(name, match.Local));
        var localMatchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localMatchedOrder = new List<string>();
        var localSpecificFiles = new List<string>();
        foreach (var file in local?.Files ?? new())
        {
            if (matchByPath.TryGetValue(PathKey(file), out var match) && match.IsConfident && match.Spotify is not null)
            {
                var matchedId = ResolveExistingRemoteId(match.Spotify, remoteById, remoteByRecording);
                if (localMatchedIds.Add(matchedId)) localMatchedOrder.Add(matchedId);
            }
            else
                localSpecificFiles.Add(file);
        }

        var protectedRemote = spotifyIds.Where(id =>
                !localBySpotifyId.ContainsKey(id) ||
                settings.LikedSongsOnlyForSpotify && !likedEquivalentIds.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var syncMode = $"{settings.LocalBackend}:{settings.Direction}";
        var hasBaseline = state.Playlists.TryGetValue(key, out var baseline) &&
            baseline.SyncMode.Equals(syncMode, StringComparison.OrdinalIgnoreCase);
        plan.HasBaseline = hasBaseline;
        var baseIds = (baseline?.SpotifyIds ?? new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var union = spotifyIds.Union(localMatchedIds, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> desired;

        if (!settings.EnableRemovals || !hasBaseline)
        {
            desired = union;
            if (settings.EnableRemovals && !hasBaseline)
                plan.Warnings.Add($"No deletion baseline exists for {syncMode}; this run is add-only and will establish one.");
        }
        else if (settings.Direction == SyncDirection.SpotifyToLocal)
        {
            desired = spotifyIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else if (settings.Direction == SyncDirection.LocalToSpotify)
        {
            desired = localMatchedIds.Union(protectedRemote, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in union.Union(baseIds, StringComparer.OrdinalIgnoreCase))
            {
                var keep = baseIds.Contains(id)
                    ? spotifyIds.Contains(id) && localMatchedIds.Contains(id)
                    : spotifyIds.Contains(id) || localMatchedIds.Contains(id);
                if (keep) desired.Add(id);
            }
            desired.UnionWith(protectedRemote);
        }

        // A local track may only be projected to Spotify when it is in the cached Liked Songs library.
        var allowedSpotifyDesired = desired.Where(id => spotifyIds.Contains(id) || !settings.LikedSongsOnlyForSpotify || likedEquivalentIds.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var blocked in desired.Except(allowedSpotifyDesired, StringComparer.OrdinalIgnoreCase))
        {
            var localTrack = localBySpotifyId.GetValueOrDefault(blocked);
            if (localTrack is not null && playlistExclusions.Any(exclusion => exclusion.Matches(name, localTrack)))
                plan.IgnoredByRule.Add(localTrack.DisplayName);
            else
            {
                plan.PlatformSpecific.Add($"Local-only/not liked: {localTrack?.DisplayName ?? blocked}");
                if (localTrack is not null) AddReviewItem(plan, "Protected • MusicBee-only/not Liked", localTrack);
            }
        }

        foreach (var file in localSpecificFiles)
        {
            var localTrack = matchByPath.GetValueOrDefault(PathKey(file))?.Local;
            var displayName = localTrack?.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = Path.GetFileNameWithoutExtension(file);
            if (localTrack is not null && playlistExclusions.Any(exclusion => exclusion.Matches(name, localTrack)))
                plan.IgnoredByRule.Add(displayName);
            else
            {
                plan.PlatformSpecific.Add("MusicBee-only/unmatched: " + displayName);
                if (localTrack is not null) AddReviewItem(plan, "Protected • MusicBee-only/unmatched", localTrack);
            }
        }

        // Preserve each platform's existing order. Surviving local entries stay in
        // place, Spotify-only additions follow Spotify order, and local additions
        // sent to Spotify follow local order.
        var desiredLocalFiles = new List<string>();
        foreach (var file in local?.Files ?? new())
        {
            if (matchByPath.TryGetValue(PathKey(file), out var match) && match.IsConfident && match.Spotify is not null)
            {
                var matchedId = ResolveExistingRemoteId(match.Spotify, remoteById, remoteByRecording);
                if (desired.Contains(matchedId)) desiredLocalFiles.Add(file);
            }
            else desiredLocalFiles.Add(file);
        }
        foreach (var id in spotifyOrder)
        {
            if (desired.Contains(id) && localBySpotifyId.TryGetValue(id, out var track) &&
                !desiredLocalFiles.Contains(track.Path, StringComparer.OrdinalIgnoreCase))
                desiredLocalFiles.Add(track.Path);
        }
        foreach (var id in desired.Where(x => !localBySpotifyId.ContainsKey(x)))
        {
            var track = remoteById.GetValueOrDefault(id) ?? catalogById.GetValueOrDefault(id);
            var displayName = track?.DisplayName;
            displayName = string.IsNullOrWhiteSpace(displayName) ? "Unknown Spotify track" : displayName;
            if (SpotifyExcluded(id)) plan.IgnoredByRule.Add(displayName);
            else
            {
                plan.PlatformSpecific.Add("Spotify-only/no local file: " + displayName);
                if (track is not null) AddReviewItem(plan, "Protected • Spotify-only/no local file", track);
            }
        }

        var currentFiles = (local?.Files ?? new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unfilteredDesiredSpotify = spotifyOrder.Concat(localMatchedOrder)
            .Where(allowedSpotifyDesired.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unfilteredDesiredLocal = desiredLocalFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        plan.DesiredSpotifyIds = unfilteredDesiredSpotify
            .Concat(spotifyOrder.Where(SpotifyExcluded))
            .Where(id => !SpotifyExcluded(id) || spotifyIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        plan.DesiredLocalFiles = unfilteredDesiredLocal
            .Concat(currentFiles.Where(LocalExcluded))
            .Where(path => !LocalExcluded(path) || currentFiles.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var id in unfilteredDesiredSpotify.Where(id => !spotifyIds.Contains(id) && SpotifyExcluded(id)))
            plan.IgnoredByRule.Add((catalogById.GetValueOrDefault(id)?.DisplayName ?? "Spotify track") + " → Spotify");
        foreach (var id in spotifyOrder.Where(id => !unfilteredDesiredSpotify.Contains(id, StringComparer.OrdinalIgnoreCase) && SpotifyExcluded(id)))
            plan.IgnoredByRule.Add((remoteById.GetValueOrDefault(id)?.DisplayName ?? "Spotify track") + " removal");
        foreach (var path in unfilteredDesiredLocal.Where(path => !currentFiles.Contains(path) && LocalExcluded(path)))
            plan.IgnoredByRule.Add((matchByPath.GetValueOrDefault(PathKey(path))?.Local.DisplayName ?? Path.GetFileNameWithoutExtension(path)) + " → local");
        foreach (var path in currentFiles.Where(path => !unfilteredDesiredLocal.Contains(path, StringComparer.OrdinalIgnoreCase) && LocalExcluded(path)))
            plan.IgnoredByRule.Add((matchByPath.GetValueOrDefault(PathKey(path))?.Local.DisplayName ?? Path.GetFileNameWithoutExtension(path)) + " removal");
        plan.IgnoredByRule = plan.IgnoredByRule.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (settings.Direction != SyncDirection.SpotifyToLocal)
        {
            plan.SpotifyAddIds = plan.DesiredSpotifyIds.Where(id => !spotifyIds.Contains(id)).ToList();
            var desiredSpotifySet = plan.DesiredSpotifyIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            plan.SpotifyRemoveIds = spotifyOrder.Where(id => !desiredSpotifySet.Contains(id))
                .Where(id => !protectedRemote.Contains(id)).ToList();
        }
        if (settings.Direction != SyncDirection.LocalToSpotify)
        {
            plan.LocalAddFiles = plan.DesiredLocalFiles.Except(currentFiles, StringComparer.OrdinalIgnoreCase).ToList();
            plan.LocalRemoveFiles = currentFiles.Except(plan.DesiredLocalFiles, StringComparer.OrdinalIgnoreCase)
                .Where(path => !localSpecificFiles.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        var removalCount = Math.Max(plan.SpotifyRemoveIds.Count, plan.LocalRemoveFiles.Count);
        if (removalCount > settings.MaxRemovalsPerPlaylist)
        {
            plan.IsBlocked = true;
            plan.BlockReason = $"removal safety cap exceeded ({removalCount} > {settings.MaxRemovalsPerPlaylist})";
        }
        if (remote is { CanReadItems: false })
        {
            plan.IsBlocked = true;
            plan.BlockReason = "Spotify did not expose this playlist's items (usually followed/read-only in Development Mode)";
        }
        if ((plan.SpotifyAddIds.Count > 0 || plan.SpotifyRemoveIds.Count > 0) && remote is { CanWrite: false })
        {
            plan.IsBlocked = true;
            plan.BlockReason = "Spotify playlist is read-only";
        }
        foreach (var id in plan.SpotifyAddIds)
            if ((remoteById.GetValueOrDefault(id) ?? catalogById.GetValueOrDefault(id)) is { } track)
                AddReviewItem(plan, "Add to Spotify", track);
        foreach (var id in plan.SpotifyRemoveIds)
            if ((remoteById.GetValueOrDefault(id) ?? catalogById.GetValueOrDefault(id)) is { } track)
                AddReviewItem(plan, "Remove from Spotify", track);
        foreach (var path in plan.LocalAddFiles)
            if (matchByPath.GetValueOrDefault(PathKey(path))?.Local is { } track)
                AddReviewItem(plan, "Add to MusicBee", track);
        foreach (var path in plan.LocalRemoveFiles)
            if (matchByPath.GetValueOrDefault(PathKey(path))?.Local is { } track)
                AddReviewItem(plan, "Remove from MusicBee", track);
        return plan;
    }

    private static void AddReviewItem(PlaylistPlan plan, string kind, TrackInfo track) =>
        AddReviewItem(plan, kind, track.Artists.FirstOrDefault() ?? "", track.Title);

    private static void AddReviewItem(PlaylistPlan plan, string kind, LocalTrack track) =>
        AddReviewItem(plan, kind, track.Artist, track.Title);

    private static void AddReviewItem(PlaylistPlan plan, string kind, string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return;
        if (plan.ReviewItems.Any(item => item.Kind == kind &&
            TextNormalizer.Normalize(item.Artist) == TextNormalizer.Normalize(artist) &&
            TextNormalizer.Normalize(item.Title, true) == TextNormalizer.Normalize(title, true))) return;
        plan.ReviewItems.Add(new TrackReviewItem { Kind = kind, Artist = artist.Trim(), Title = title.Trim() });
    }

    private static string ResolveExistingRemoteId(
        TrackInfo track,
        IReadOnlyDictionary<string, TrackInfo> remoteById,
        IReadOnlyDictionary<string, List<TrackInfo>> remoteByRecording)
    {
        if (remoteById.TryGetValue(track.SpotifyId, out var exact)) return exact.SpotifyId;
        var recordingKey = TrackMatcher.RecordingGroupKey(track);
        return remoteByRecording.GetValueOrDefault(recordingKey)?
            .FirstOrDefault(candidate => TrackMatcher.SameRecording(track, candidate))?.SpotifyId ?? track.SpotifyId;
    }

    private static bool ShouldInclude(string name, PortableSettings settings) =>
        TextNormalizer.WildcardMatch(name, settings.PlaylistInclude, true) &&
        !TextNormalizer.WildcardMatch(name, settings.PlaylistExclude, false);

    private static bool ShouldIncludeLocal(string name, PortableSettings settings)
    {
        var leaf = TextNormalizer.MusicBeePlaylistLeaf(name);
        var included = TextNormalizer.WildcardMatch(name, settings.PlaylistInclude, true) ||
                       TextNormalizer.WildcardMatch(leaf, settings.PlaylistInclude, true);
        var excluded = TextNormalizer.WildcardMatch(name, settings.PlaylistExclude, false) ||
                       TextNormalizer.WildcardMatch(leaf, settings.PlaylistExclude, false);
        return included && !excluded;
    }

    internal static string PathKey(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim(); }
    }
}

public static class PlaylistDigests
{
    public static string ForFiles(IEnumerable<string> files) => TextNormalizer.StableKey(string.Join("\n", files.Select(x =>
    {
        try { return Path.GetFullPath(x).ToLowerInvariant(); }
        catch { return x.Trim().ToLowerInvariant(); }
    })));
}
