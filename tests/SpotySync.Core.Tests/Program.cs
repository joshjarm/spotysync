using SpotySync;
using System.Reflection;

if (Environment.GetEnvironmentVariable("SPOTYSYNC_FAKE_SPOT2YT") == "1")
{
    RunFakeSpot2Yt();
    return;
}

if (Environment.GetEnvironmentVariable("SPOTYSYNC_FAKE_FFPROBE") == "1")
{
    RunFakeFfprobe(args);
    return;
}

if (args.Length == 3 && args[0].Equals("--fallback-smoke", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await RunFallbackSmoke(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0].Equals("--preview-root", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await RunReadOnlyPreview(args[1]);
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("normalization", TestNormalization),
    ("liked-only matching", TestMatching),
    ("reversed local title and artist matching", TestReversedTagMatching),
    ("ampersand artist matching", TestAmpersandArtistMatching),
    ("edition duration drift matching", TestEditionDurationDriftMatching),
    ("first-run is add-only", TestFirstRunSafety),
    ("three-way deletion", TestThreeWayDeletion),
    ("baseline is scoped to backend and direction", TestBaselineModeSafety),
    ("platform-specific protection", TestPlatformSpecificProtection),
    ("liked Spotify-only protection", TestLikedSpotifyOnlyProtection),
    ("new additions merge after baseline", TestNewAdditionsMerge),
    ("playlist order is preserved", TestPlaylistOrder),
    ("MusicBee folders match Spotify leaf names", TestMusicBeeFolderMatching),
    ("duplicate MusicBee leaf names are blocked", TestDuplicateLeafProtection),
    ("Spotify relinked IDs do not create duplicate additions", TestRelinkedSpotifyIds),
    ("playlist metadata finds local non-liked tracks", TestPlaylistMetadataMatching),
    ("platform-specific entries use readable names", TestPlatformSpecificNames),
    ("MusicBee-only tracks appear as protected", TestMusicBeeOnlyProtection),
    ("per-playlist exclusions suppress changes", TestSyncExclusions),
    ("exclusion text file round trip", TestExclusionFile),
    ("backup window uses oldest of newest five", TestBackupWindow),
    ("recent Likes download newest first", TestRecentLikesDownloadOrder),
    ("removal cap", TestRemovalCap),
    ("portable settings round trip", TestPortableSettings),
    ("appearance and soundtrack settings round trip", TestAppearanceSettings),
    ("library insights find unplaylisted, missing and duplicate songs", TestLibraryInsights),
    ("MusicBee filtered folders edit is safe and idempotent", TestMusicBeeFilteredFolders),
    ("soundtrack folder names are sanitized", TestSoundtrackFolderName),
    ("Spotify login tries common redirect addresses in order", TestRedirectCandidates),
    ("ignore rules can be removed again", TestExclusionRemoval),
    ("MusicBee auto-organise can be limited to the Songs folder", TestMusicBeeOrganiseLimit),
    ("soundtrack repair only moves the soundtrack's own new files", TestSoundtrackRepairSafety),
    ("MusicBee metadata seeds disk scan", TestScannerSeed),
    ("library scan survives MusicBee rename", TestScannerRenameRace),
    ("MusicBee library root detection and merge", TestLibraryRootAndMerge),
    ("custom downloader output folder detection", TestDownloaderOutputFolder),
    ("custom downloader skipped-track parsing", TestSkippedTrackParsing),
    ("default downloader is spotDL", TestDefaultDownloader),
    ("custom downloader interactive protocol", TestSpot2YtProtocol),
    ("YouTube fallback candidate safety", TestYouTubeFallbackScoring),
    ("download verification accepts title and duration", TestDownloadedTrackVerification),
    ("M3U write, removal, and backup", TestM3uSafety),
    ("downloads pause and resume their whole process tree", TestDownloadPause),
    ("loaded library catches up with new and deleted files", TestLibraryReconcile),
    ("console split and graph range settings round trip", TestLiveSettings),
    ("download queue skips songs already in the library", TestQueueSkipsOwnedSongs),
    ("download format choice maps to spotDL and yt-dlp", TestDownloadFormat)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures.Add($"{test.Name}: {ex.Message}"); Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
if (failures.Count > 0) Environment.Exit(1);
Console.WriteLine($"All {tests.Length} tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void RunFakeSpot2Yt()
{
    Console.WriteLine("Spot2YT Portable");
    Console.Write("Paste Spotify link (or type exit): ");
    var submitted = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(submitted)) Environment.Exit(11);
    Console.WriteLine("Starting download...");
    Console.WriteLine("[1/1] SKIPPED for now: Fake Artist - Fake Track");
    Console.Write("Fake Artist - Fake Track was skipped. Paste a YouTube link (or press Enter to leave skipped): ");
    if (!string.IsNullOrEmpty(Console.ReadLine())) Environment.Exit(12);
    Console.WriteLine("All done! 0/1 track(s) ready.");
    Console.Write("Paste Spotify link (or type exit): ");
    if (!string.Equals(Console.ReadLine(), "exit", StringComparison.OrdinalIgnoreCase)) Environment.Exit(13);
    Console.WriteLine("Goodbye.");
    Console.Write("Press Enter to continue...");
    Console.ReadLine();
}

static void RunFakeFfprobe(string[] arguments)
{
    var path = arguments.LastOrDefault() ?? string.Empty;
    if (Path.GetFileName(path).Equals("Metropolis.mp3", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "Metropolis - Accelio.mp3"));
    Console.Write("{\"format\":{\"duration\":\"180.0\",\"tags\":{\"title\":\"Metropolis\",\"artist\":\"Accelio\"}}}");
}

static void TestScannerRenameRace()
{
    var root = Path.Combine(Path.GetTempPath(), "SpotySync-rename-" + Guid.NewGuid().ToString("N"));
    var tools = Path.Combine(root, "Tools");
    var music = Path.Combine(root, "Music");
    Directory.CreateDirectory(tools);
    Directory.CreateDirectory(music);
    try
    {
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(source, Path.Combine(tools, Path.GetFileName(source)), true);
        File.Copy(Environment.ProcessPath!, Path.Combine(tools, "ffprobe.exe"), true);
        File.WriteAllBytes(Path.Combine(music, "Metropolis.mp3"), [0x49, 0x44, 0x33]);
        var settings = PortableSettings.Load(root);
        settings.MusicFolder = music;
        settings.FfprobePath = @"Tools\ffprobe.exe";
        var log = new List<string>();
        Environment.SetEnvironmentVariable("SPOTYSYNC_FAKE_FFPROBE", "1");
        var tracks = new LibraryScanner(settings, log.Add).ScanAsync().GetAwaiter().GetResult();
        Assert(tracks.Count == 1, "renamed track disappeared from refreshed scan");
        Assert(Path.GetFileName(tracks[0].Path).Equals("Metropolis - Accelio.mp3", StringComparison.OrdinalIgnoreCase),
            "refreshed scan retained the vanished pre-rename path");
        Assert(tracks[0].Title == "Metropolis" && tracks[0].Artist == "Accelio", "renamed track metadata was not inspected");
        Assert(log.Any(line => line.Contains("refreshing the library paths", StringComparison.OrdinalIgnoreCase)),
            "rename race did not trigger the stabilization scan");
    }
    finally
    {
        Environment.SetEnvironmentVariable("SPOTYSYNC_FAKE_FFPROBE", null);
        try { Directory.Delete(root, true); } catch { }
    }
}

static void TestDefaultDownloader()
{
    var root = Path.Combine(Path.GetTempPath(), "SpotySync-downloader-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "Tools"));
    try
    {
        Assert(PortableSettings.Load(root).DownloaderPath == @"Tools\spotdl.exe", "a fresh install did not default to spotDL");
        var ini = Path.Combine(root, "SpotySync.ini");
        File.WriteAllText(ini, File.ReadAllText(ini).Replace(@"Downloader=Tools\spotdl.exe", @"Downloader=Tools\spot2yt.exe"));
        Assert(PortableSettings.Load(root).DownloaderPath == @"Tools\spotdl.exe", "a missing spot2yt.exe did not fall back to spotDL");
        File.WriteAllText(Path.Combine(root, "Tools", "spot2yt.exe"), "");
        Assert(PortableSettings.Load(root).DownloaderPath == @"Tools\spot2yt.exe", "an installed spot2yt.exe was not kept");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static void TestSpot2YtProtocol()
{
    var root = Path.Combine(Path.GetTempPath(), "SpotySync-protocol-" + Guid.NewGuid().ToString("N"));
    var tools = Path.Combine(root, "Tools");
    var music = Path.Combine(root, "Music");
    Directory.CreateDirectory(tools);
    Directory.CreateDirectory(music);
    try
    {
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(source, Path.Combine(tools, Path.GetFileName(source)), true);
        File.Copy(Environment.ProcessPath!, Path.Combine(tools, "spot2yt.exe"), true);
        File.WriteAllText(Path.Combine(tools, "spotify_credentials.txt"),
            "SPOTIFY_CLIENT_ID=fake\nSPOTIFY_CLIENT_SECRET=fake\n");
        var settings = PortableSettings.Load(root);
        settings.DownloaderPath = @"Tools\spot2yt.exe";
        settings.MusicFolder = music;
        settings.AutoUpdateYtDlp = false;
        settings.AutoYouTubeFallback = false;
        var log = new List<string>();
        Environment.SetEnvironmentVariable("SPOTYSYNC_FAKE_SPOT2YT", "1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = new Downloader(settings, log.Add)
            .RunManualAsync(["https://open.spotify.com/track/fake"], cancellationToken: timeout.Token)
            .GetAwaiter().GetResult();
        Assert(result.ExitCode == 0, "fake custom downloader did not exit cleanly");
        Assert(result.SkippedTracks.SequenceEqual(["Fake Artist - Fake Track"]), "missing-source prompt was not answered or parsed");
        Assert(log.Count(line => line.Contains("Audio download stage complete", StringComparison.OrdinalIgnoreCase)) == 1,
            "audio-stage completion was not relabeled exactly once");
        Assert(!log.Any(line => line.Contains("Paste Spotify link", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("Paste a YouTube link", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("Press Enter", StringComparison.OrdinalIgnoreCase)),
            "interactive prompt leaked into the Activity log");
    }
    finally
    {
        Environment.SetEnvironmentVariable("SPOTYSYNC_FAKE_SPOT2YT", null);
        try { Directory.Delete(root, true); } catch { }
    }
}

static async Task<int> RunFallbackSmoke(string portableRoot, string musicFolder)
{
    try
    {
        var settings = PortableSettings.Load(portableRoot);
        settings.MusicFolder = musicFolder;
        settings.FfprobePath = @"Tools\ffprobe.exe";
        settings.AutoUpdateYtDlp = false;
        settings.AutoYouTubeFallback = true;
        settings.Save();
        var lines = new List<string>();
        var downloader = new Downloader(settings, message => { lines.Add(message); Console.WriteLine("LOG " + message); });
        var method = typeof(Downloader).GetMethod("RunYouTubeFallbackAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Fallback method was not found.");
        var track = new TrackInfo { SpotifyId = "7q3D75N5N9wdSYipTQHno2", Title = "Rivers", Artists = ["Major Axis"], DurationMs = 425_568 };
        var task = (Task<DownloadRunResult>)(method.Invoke(downloader, new object[] { track, CancellationToken.None })
            ?? throw new InvalidOperationException("Fallback method did not return a task."));
        var result = await task;
        Assert(result.ExitCode == 0 && result.ReportedErrors == 0, $"fallback returned {result.ExitCode}/{result.ReportedErrors}");
        Assert(result.ChangedAudio.Count == 1 && File.Exists(result.ChangedAudio[0]), "fallback did not publish exactly one finished audio file");
        Assert(Directory.EnumerateFiles(musicFolder).Count() == 1, "fallback published sidecars or duplicate intermediate files");
        Assert(lines.Count < 50, $"fallback console was noisy ({lines.Count} lines)");
        var library = await new LibraryScanner(settings).ScanAsync(true);
        var downloaded = library.Single();
        Assert(downloaded.Title.Equals("Rivers", StringComparison.OrdinalIgnoreCase), "published title tag is wrong: " + downloaded.Title);
        Assert(downloaded.Artist.Contains("Major Axis", StringComparison.OrdinalIgnoreCase), "published artist tag is wrong: " + downloaded.Artist);
        Assert(downloaded.DurationMs > 420_000, "published audio is truncated");
        Console.WriteLine($"PASS fallback smoke: {downloaded.DisplayName}, {downloaded.DurationMs}ms, {lines.Count} log lines");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex is TargetInvocationException invocation && invocation.InnerException is not null ? invocation.InnerException : ex);
        return 1;
    }
}

static async Task<int> RunReadOnlyPreview(string root)
{
    try
    {
        var settings = PortableSettings.Load(root);
        using var service = new SpotySyncService(settings, message => Console.WriteLine("LOG " + message));
        var prepared = await service.PrepareAsync(false, true);
        foreach (var plan in prepared.Plan.Playlists.Where(plan => plan.ChangeCount > 0 || plan.PlatformSpecific.Count > 0))
        {
            Console.WriteLine($"PLAN {plan.Name}: Spotify +{plan.SpotifyAddIds.Count}/-{plan.SpotifyRemoveIds.Count}; " +
                              $"local +{plan.LocalAddFiles.Count}/-{plan.LocalRemoveFiles.Count}; protected {plan.PlatformSpecific.Count}; ignored {plan.IgnoredByRule.Count}");
            foreach (var item in plan.ReviewItems.Where(item =>
                         item.Title.Contains("Safety Dance", StringComparison.OrdinalIgnoreCase) ||
                         item.Title.Contains("Them Changes", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"REVIEW {plan.Name}: {item.Kind} | {item.DisplayName}");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static (PortableSettings Settings, TrackInfo A, TrackInfo B, LocalTrack LA, LocalTrack LB) Fixture()
{
    var dir = Path.Combine(Path.GetTempPath(), "spotysync-tests");
    var settings = PortableSettings.Load(dir);
    settings.EnableRemovals = true;
    settings.Direction = SyncDirection.TwoWay;
    settings.MaxRemovalsPerPlaylist = 25;
    var a = new TrackInfo { SpotifyId = "AAAAAAAAAAAAAAAAAAAAAA", Uri = "spotify:track:AAAAAAAAAAAAAAAAAAAAAA", Title = "Song One", Artists = ["Artist"], Album = "Album", DurationMs = 200000 };
    var b = new TrackInfo { SpotifyId = "BBBBBBBBBBBBBBBBBBBBBB", Uri = "spotify:track:BBBBBBBBBBBBBBBBBBBBBB", Title = "Song Two", Artists = ["Artist"], Album = "Album", DurationMs = 210000 };
    var la = new LocalTrack { Path = Path.Combine(dir, "one.mp3"), Title = "Song One", Artist = "Artist", Album = "Album", DurationMs = 200100 };
    var lb = new LocalTrack { Path = Path.Combine(dir, "two.mp3"), Title = "Song Two", Artist = "Artist", Album = "Album", DurationMs = 209900 };
    return (settings, a, b, la, lb);
}

static void TestNormalization()
{
    Assert(TextNormalizer.Normalize("Beyoncé & Jay-Z") == "beyonce jay z", "diacritics or punctuation not normalized");
    Assert(TextNormalizer.Normalize("Track (2011 Remastered)", true) == "track", "version qualifier not normalized");
}

static void TestMatching()
{
    var f = Fixture();
    var match = new TrackMatcher([f.A]).Match(f.LA);
    Assert(match.IsConfident && match.Spotify?.SpotifyId == f.A.SpotifyId, "expected confident liked-library match");
}

static void TestReversedTagMatching()
{
    var remote = new TrackInfo { SpotifyId = "RRRRRRRRRRRRRRRRRRRRRR", Title = "Signal from the Stars", Artists = ["Major Axis"], DurationMs = 250000 };
    var local = new LocalTrack { Path = "signal.mp3", Title = "Major Axis", Artist = "Signal from the Stars", DurationMs = 250200 };
    var match = new TrackMatcher([remote]).Match(local);
    Assert(match.IsConfident && match.Spotify?.SpotifyId == remote.SpotifyId, "reversed local tags were not recognized");
    Assert(match.Reason.Contains("reversed", StringComparison.OrdinalIgnoreCase), "reversed-tag reason was not reported");
}

static void TestAmpersandArtistMatching()
{
    var remote = new TrackInfo { SpotifyId = "SSSSSSSSSSSSSSSSSSSSSS", Title = "Summer Breeze", Artists = ["Seals and Crofts"], DurationMs = 205000 };
    var local = new LocalTrack { Path = "summer.mp3", Title = "Summer Breeze", Artist = "Seals & Crofts", DurationMs = 205500 };
    Assert(new TrackMatcher([remote]).Match(local).IsConfident, "artist '&' and 'and' were treated as different artists");
}

static void TestEditionDurationDriftMatching()
{
    var remote = new TrackInfo { SpotifyId = "DDDDDDDDDDDDDDDDDDDDDD", Title = "Safety Dance", Artists = ["Men Without Hats"], Album = "Rhythm of Youth", DurationMs = 165000 };
    var local = new LocalTrack { Path = "safety.mp3", Title = "Safety Dance - Extended Edition", Artist = "Men Without Hats", Album = "First of the Year Edition", DurationMs = 285000 };
    Assert(new TrackMatcher([remote]).Match(local).IsConfident, "a unique exact recording name was rejected only because its edition duration differs");
}

static void TestFirstRunSafety()
{
    var f = Fixture();
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LB.Path] };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, f.LB], [f.A, f.B], new SyncState(), f.Settings).Playlists.Single();
    Assert(plan.SpotifyRemoveIds.Count == 0 && plan.LocalRemoveFiles.Count == 0, "first run attempted a removal");
    Assert(plan.SpotifyAddIds.Contains(f.B.SpotifyId) && plan.LocalAddFiles.Contains(f.LA.Path), "first run did not union additions");
}

static void TestThreeWayDeletion()
{
    var f = Fixture();
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LA.Path, f.LB.Path] };
    var state = new SyncState { Playlists = new(StringComparer.OrdinalIgnoreCase) { [TextNormalizer.PlaylistKey("Mix")] = new PlaylistBaseline { Name = "Mix", SpotifyIds = [f.A.SpotifyId, f.B.SpotifyId] } } };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, f.LB], [f.A, f.B], state, f.Settings).Playlists.Single();
    Assert(plan.LocalRemoveFiles.Contains(f.LB.Path), "Spotify-side deletion was not propagated locally");
    Assert(!plan.SpotifyAddIds.Contains(f.B.SpotifyId), "deleted track was incorrectly restored to Spotify");
}

static void TestBaselineModeSafety()
{
    var f = Fixture();
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LA.Path, f.LB.Path] };
    var state = new SyncState { Playlists = new(StringComparer.OrdinalIgnoreCase) { [TextNormalizer.PlaylistKey("Mix")] = new PlaylistBaseline { Name = "Mix", SyncMode = "MusicBee:TwoWay", SpotifyIds = [f.A.SpotifyId, f.B.SpotifyId] } } };
    f.Settings.LocalBackend = LocalBackendKind.M3U;
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, f.LB], [f.A, f.B], state, f.Settings).Playlists.Single();
    Assert(!plan.HasBaseline, "MusicBee baseline crossed into M3U mode");
    Assert(plan.LocalRemoveFiles.Count == 0 && plan.SpotifyRemoveIds.Count == 0, "backend change attempted a removal");
}

static void TestPlatformSpecificProtection()
{
    var f = Fixture();
    var odd = Path.Combine(Path.GetTempPath(), "local-only.mp3");
    var remoteOnly = new TrackInfo { SpotifyId = "CCCCCCCCCCCCCCCCCCCCCC", Title = "Not Liked", Artists = ["Else"] };
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A, remoteOnly] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LA.Path, odd] };
    var state = new SyncState { Playlists = new(StringComparer.OrdinalIgnoreCase) { [TextNormalizer.PlaylistKey("Mix")] = new PlaylistBaseline { Name = "Mix", SpotifyIds = [f.A.SpotifyId] } } };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, new LocalTrack { Path = odd, Title = "Bootleg", Artist = "Local" }], [f.A], state, f.Settings).Playlists.Single();
    Assert(!plan.LocalRemoveFiles.Contains(odd), "local-only file was removed");
    Assert(!plan.SpotifyRemoveIds.Contains(remoteOnly.SpotifyId), "non-liked Spotify-only track was removed");
}

static void TestRemovalCap()
{
    var f = Fixture();
    f.Settings.MaxRemovalsPerPlaylist = 0;
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LA.Path] };
    var state = new SyncState { Playlists = new(StringComparer.OrdinalIgnoreCase) { [TextNormalizer.PlaylistKey("Mix")] = new PlaylistBaseline { Name = "Mix", SpotifyIds = [f.A.SpotifyId] } } };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA], [f.A], state, f.Settings).Playlists.Single();
    Assert(plan.IsBlocked, "removal cap did not block the playlist");
}

static void TestLikedSpotifyOnlyProtection()
{
    var f = Fixture();
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A, f.B] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LA.Path] };
    var state = new SyncState { Playlists = new(StringComparer.OrdinalIgnoreCase) { [TextNormalizer.PlaylistKey("Mix")] = new PlaylistBaseline { Name = "Mix", SpotifyIds = [f.A.SpotifyId, f.B.SpotifyId] } } };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA], [f.A, f.B], state, f.Settings).Playlists.Single();
    Assert(!plan.SpotifyRemoveIds.Contains(f.B.SpotifyId), "Spotify-only track with no local file was removed");
    Assert(plan.PlatformSpecific.Any(x => x.Contains("Song Two")), "Spotify-only track was not reported as protected");
}

static void TestNewAdditionsMerge()
{
    var f = Fixture();
    var c = new TrackInfo { SpotifyId = "CCCCCCCCCCCCCCCCCCCCCC", Title = "Song Three", Artists = ["Artist"], Album = "Album" };
    var lc = new LocalTrack { Path = Path.Combine(Path.GetTempPath(), "three.mp3"), Title = "Song Three", Artist = "Artist", Album = "Album" };
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A, f.B] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LA.Path, lc.Path] };
    var state = new SyncState { Playlists = new(StringComparer.OrdinalIgnoreCase) { [TextNormalizer.PlaylistKey("Mix")] = new PlaylistBaseline { Name = "Mix", SpotifyIds = [f.A.SpotifyId] } } };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, f.LB, lc], [f.A, f.B, c], state, f.Settings).Playlists.Single();
    Assert(plan.SpotifyAddIds.Contains(c.SpotifyId), "new local addition did not flow to Spotify");
    Assert(plan.LocalAddFiles.Contains(f.LB.Path), "new Spotify addition did not flow locally");
}

static void TestPlaylistOrder()
{
    var f = Fixture();
    var c = new TrackInfo { SpotifyId = "CCCCCCCCCCCCCCCCCCCCCC", Title = "Song Three", Artists = ["Artist"], Album = "Album" };
    var lc = new LocalTrack { Path = Path.Combine(Path.GetTempPath(), "three.mp3"), Title = "Song Three", Artist = "Artist", Album = "Album" };
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.B, f.A] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LA.Path, lc.Path] };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, f.LB, lc], [f.A, f.B, c], new SyncState(), f.Settings).Playlists.Single();
    Assert(plan.DesiredSpotifyIds.SequenceEqual([f.B.SpotifyId, f.A.SpotifyId, c.SpotifyId]), "Spotify order or appended local order changed");
    Assert(plan.DesiredLocalFiles.SequenceEqual([f.LA.Path, lc.Path, f.LB.Path]), "local order or appended Spotify order changed");
}

static void TestMusicBeeFolderMatching()
{
    var f = Fixture();
    var remote = new PlaylistInfo { Id = "p", Name = "1st Playlist", IsOwned = true, CanWrite = true, Tracks = [f.A] };
    var local = new LocalPlaylist { Id = "musicbee-id", Name = @"Old Playlists\1st Playlist", Files = [f.LB.Path] };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, f.LB], [f.A, f.B], new SyncState(), f.Settings);
    Assert(plan.Playlists.Count == 1, "folder-qualified MusicBee playlist was treated as a separate playlist");
    Assert(plan.Playlists[0].Name == "1st Playlist" && plan.Playlists[0].LocalSourceName == @"Old Playlists\1st Playlist", "playlist display/source names were not retained correctly");
    Assert(plan.Playlists[0].SpotifyAddIds.Contains(f.B.SpotifyId) && plan.Playlists[0].LocalAddFiles.Contains(f.LA.Path), "merged folder playlist did not synchronize both sides");
}

static void TestDuplicateLeafProtection()
{
    var f = Fixture();
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A] };
    var locals = new[]
    {
        new LocalPlaylist { Id = "one", Name = @"Folder One\Mix", Files = [f.LA.Path] },
        new LocalPlaylist { Id = "two", Name = @"Folder Two\Mix", Files = [f.LB.Path] }
    };
    var plan = new SyncPlanner().Build("u", [remote], locals, [f.LA, f.LB], [f.A, f.B], new SyncState(), f.Settings).Playlists.Single();
    Assert(plan.IsBlocked && plan.BlockReason.Contains("multiple MusicBee playlists"), "ambiguous leaf-name collision was not blocked");
}

static void TestRelinkedSpotifyIds()
{
    var f = Fixture();
    var remoteTrack = new TrackInfo
    {
        SpotifyId = "51pshtuYkgUQnt5huMPbKL", Title = "The Spins", Artists = ["Mac Miller"],
        Album = "K.I.D.S.", DurationMs = 195873
    };
    var likedTrack = new TrackInfo
    {
        SpotifyId = "7BcOUi4FQ3wBLmNTW0yfCR", Title = "The Spins", Artists = ["Mac Miller", "Empire Of The Sun"],
        Album = "K.I.D.S.", DurationMs = 195873
    };
    var localTrack = new LocalTrack
    {
        Path = Path.Combine(Path.GetTempPath(), "The Spins.mp3"), Title = "The Spins", Artist = "Mac Miller",
        Album = "K.I.D.S.", DurationMs = 195873
    };
    var remote = new PlaylistInfo { Id = "p", Name = "1st Playlist", IsOwned = true, CanWrite = true, Tracks = [remoteTrack] };
    var local = new LocalPlaylist { Id = "l", Name = @"Old Playlists\1st Playlist", Files = [localTrack.Path] };
    var plan = new SyncPlanner().Build("u", [remote], [local], [localTrack], [likedTrack], new SyncState(), f.Settings).Playlists.Single();
    Assert(plan.ChangeCount == 0, "relinked Spotify IDs were treated as different songs");
    Assert(plan.PlatformSpecific.Count == 0, "relinked recording was incorrectly protected as platform-specific");
}

static void TestPlaylistMetadataMatching()
{
    var f = Fixture();
    var remoteTrack = new TrackInfo
    {
        SpotifyId = "1tNJrcVe6gwLEiZCtprs1u", Title = "Playlist Only Song", Artists = ["Example Artist"],
        Album = "Example Album", DurationMs = 201000
    };
    var localTrack = new LocalTrack
    {
        Path = Path.Combine(Path.GetTempPath(), "Playlist Only Song.mp3"), Title = "Playlist Only Song", Artist = "Example Artist",
        Album = "Example Album", DurationMs = 201000
    };
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [remoteTrack] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix" };
    var plan = new SyncPlanner().Build("u", [remote], [local], [localTrack], Array.Empty<TrackInfo>(), new SyncState(), f.Settings).Playlists.Single();
    Assert(plan.LocalAddFiles.SequenceEqual([localTrack.Path]), "Spotify playlist metadata did not find the existing local file");
    Assert(plan.PlatformSpecific.Count == 0, "existing non-liked local track was incorrectly labeled Spotify-only");
}

static void TestPlatformSpecificNames()
{
    var f = Fixture();
    var remoteTrack = new TrackInfo
    {
        SpotifyId = "1tNJrcVe6gwLEiZCtprs1u", Title = "Readable Song", Artists = ["Readable Artist"],
        Album = "Example Album", DurationMs = 201000
    };
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [remoteTrack] };
    var plan = new SyncPlanner().Build("u", [remote], Array.Empty<LocalPlaylist>(), Array.Empty<LocalTrack>(), Array.Empty<TrackInfo>(), new SyncState(), f.Settings).Playlists.Single();
    Assert(plan.PlatformSpecific.Single().Contains("Readable Artist - Readable Song"), "platform-specific entry omitted readable metadata");
    Assert(!plan.PlatformSpecific.Single().Contains(remoteTrack.SpotifyId), "platform-specific entry exposed a raw Spotify ID");
}

static void TestMusicBeeOnlyProtection()
{
    var f = Fixture();
    var localTrack = new LocalTrack
    {
        Path = Path.Combine(Path.GetTempPath(), "Them Changes.mp3"), Title = "Them Changes", Artist = "Ariana Grande",
        Album = "Unreleased", DurationMs = 210000
    };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [localTrack.Path] };
    var plan = new SyncPlanner().Build("u", Array.Empty<PlaylistInfo>(), [local], [localTrack], Array.Empty<TrackInfo>(), new SyncState(), f.Settings).Playlists.Single();
    Assert(plan.PlatformSpecific.Single() == "MusicBee-only/unmatched: Ariana Grande - Them Changes", "local-only track was missing from protected details");
    Assert(plan.ReviewItems.Any(item => item.Title == "Them Changes"), "local-only track was not available in review choices");
}

static void TestSyncExclusions()
{
    var f = Fixture();
    var remote = new PlaylistInfo { Id = "p", Name = "Mix", IsOwned = true, CanWrite = true, Tracks = [f.A] };
    var local = new LocalPlaylist { Id = "l", Name = "Mix", Files = [f.LB.Path] };
    var exclusions = new[]
    {
        new SyncExclusion { Playlist = "Mix", Artist = f.A.Artists[0], Title = f.A.Title },
        new SyncExclusion { Playlist = "Mix", Artist = f.B.Artists[0], Title = f.B.Title }
    };
    var plan = new SyncPlanner().Build("u", [remote], [local], [f.LA, f.LB], [f.A, f.B], new SyncState(), f.Settings, exclusions).Playlists.Single();
    Assert(plan.ChangeCount == 0, "saved exclusions did not suppress playlist additions");
    Assert(plan.DesiredSpotifyIds.SequenceEqual([f.A.SpotifyId]), "excluded local-only track was added to desired Spotify state");
    Assert(plan.DesiredLocalFiles.SequenceEqual([f.LB.Path]), "excluded Spotify-only track was added to desired local state");
    Assert(plan.IgnoredByRule.Count == 2, "suppressed review items were not counted");
}

static void TestExclusionFile()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-exclusions-" + Guid.NewGuid().ToString("N"));
    try
    {
        var path = Path.Combine(directory, "sync-exclusions.txt");
        var store = new SyncExclusionStore(path);
        var added = store.Add([
            new SyncExclusion { Playlist = "1st Playlist", Artist = "Men Without Hats", Title = "The Safety Dance (Extended Version)" },
            new SyncExclusion { Playlist = "1st Playlist", Artist = "Men Without Hats", Title = "The Safety Dance (Extended Version)" }
        ]);
        Assert(added == 1 && store.Load().Count == 1, "exclusion file did not deduplicate or reload");
        Assert(File.ReadAllText(path).Contains("Delete a line to undo") && File.ReadAllText(path).Contains('\t'), "exclusion file is not human-reviewable TSV");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestBackupWindow()
{
    var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    var times = Enumerable.Range(0, 9).Select(day => start.AddDays(day)).ToList();
    Assert(BackupWindow.OldestOfNewest(times, 5) == start.AddDays(4), "backup anchor was not the oldest of the newest five downloads");
    Assert(BackupWindow.OldestOfNewest(times.Take(4), 5) is null, "incomplete anchor window should use the fallback cutoff");
}

static void TestRecentLikesDownloadOrder()
{
    var cutoff = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    var oldest = new TrackInfo { SpotifyId = "AAAAAAAAAAAAAAAAAAAAAA", AddedAt = cutoff.AddDays(1) };
    var newest = new TrackInfo { SpotifyId = "BBBBBBBBBBBBBBBBBBBBBB", AddedAt = cutoff.AddDays(3) };
    var middle = new TrackInfo { SpotifyId = "CCCCCCCCCCCCCCCCCCCCCC", AddedAt = cutoff.AddDays(2) };
    var excluded = new TrackInfo { SpotifyId = "DDDDDDDDDDDDDDDDDDDDDD", AddedAt = cutoff.AddDays(-1) };
    var ordered = BackupWindow.RecentLikesNewestFirst([oldest, newest, excluded, middle], cutoff);
    Assert(ordered.Select(track => track.SpotifyId).SequenceEqual([newest.SpotifyId, middle.SpotifyId, oldest.SpotifyId]),
        "recent Likes were not submitted newest-to-oldest");
}

static void TestAppearanceSettings()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-ui-" + Guid.NewGuid().ToString("N"));
    try
    {
        var settings = PortableSettings.Load(directory);
        settings.UiTheme = "qd_ion_cyan"; settings.UiAccent = "#ff00aa"; settings.UiFont = "mono";
        settings.UiRoundedCorners = false; settings.UiCompactNavigation = true; settings.UiDashboardCards = "overview,-backup,sync";
        settings.SoundtrackFolder = @"E:\Libraries\Music";
        settings.Save();
        var loaded = PortableSettings.Load(directory);
        Assert(loaded.UiTheme == "qd_ion_cyan" && loaded.UiAccent == "#ff00aa" && loaded.UiFont == "mono", "appearance strings did not round-trip");
        Assert(!loaded.UiRoundedCorners && loaded.UiCompactNavigation, "appearance switches did not round-trip");
        Assert(loaded.UiDashboardCards == "overview,-backup,sync", "dashboard layout did not round-trip");
        Assert(loaded.SoundtrackFolder == @"E:\Libraries\Music", "soundtrack folder did not round-trip");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestLibraryInsights()
{
    TrackInfo T(string id, string artist, string title) => new() { SpotifyId = id, Artists = [artist], Title = title, DurationMs = 200_000 };
    LocalTrack L(string path, string artist, string title, string id = "") => new() { Path = path, Artist = artist, Title = title, DurationMs = 200_000, SpotifyId = id, Size = 5_000_000 };
    var a = T("aaaaaaaaaaaaaaaaaaaaa1", "Alpha", "One");
    var b = T("aaaaaaaaaaaaaaaaaaaaa2", "Beta", "Two");
    var c = T("aaaaaaaaaaaaaaaaaaaaa3", "Gamma", "Three");
    var la = L(@"C:\m\Alpha - One.mp3", "Alpha", "One", a.SpotifyId);
    var lb = L(@"C:\m\Beta - Two.mp3", "Beta", "Two", b.SpotifyId);
    var lx = L(@"C:\m\Local - Only.mp3", "Local", "Only");
    var lx2 = L(@"C:\m\dupes\Local - Only.mp3", "Local", "Only");
    var lsound = L(@"C:\m\Blockheads\Theme.mp3", "Composer", "Theme");
    var prepared = new PreparedSync
    {
        Library = [la, lb, lx, lx2, lsound],
        LocalPlaylists = [new LocalPlaylist { Name = "P", Files = [la.Path] }],
        SpotifyPlaylists = [new PlaylistInfo { Name = "S", Tracks = [b] }],
        Liked = new LikedTrackCache { Tracks = [a, b, c] },
        Plan = new SyncPlan()
    };
    var report = LibraryInsights.Build(prepared, [@"C:\m\Blockheads"]);
    Assert(report.Unplaylisted.Select(r => r.Path).OrderBy(x => x).SequenceEqual(new[] { lx2.Path, lx.Path }.OrderBy(x => x)), "unplaylisted songs were wrong");
    Assert(report.NoLocalPlaylist.Any(r => r.Path == lb.Path && r.Detail.Contains("Spotify playlist")), "Spotify-playlist-only song not reported as such");
    Assert(report.LikedMissingLocally.Count == 1 && report.LikedMissingLocally[0].SpotifyId == c.SpotifyId, "missing Liked song not found");
    Assert(report.LikedNotInPlaylist.Select(r => r.SpotifyId).OrderBy(x => x).SequenceEqual(new[] { a.SpotifyId, c.SpotifyId }), "Likes in no playlist were wrong");
    Assert(report.Duplicates.Count == 2, "duplicate files were not grouped");
    Assert(report.Unplaylisted.All(r => !r.Path.Contains("Blockheads")), "ignored soundtrack folder was not skipped");
}

static void TestMusicBeeFilteredFolders()
{
    var xml = string.Join("\r\n",
        "<?xml version=\"1.0\"?>",
        "<ApplicationSettings>",
        "<NavigatorShowFilteredFolders>true</NavigatorShowFilteredFolders>",
        "<NavigatorFilteredFolders>",
        @" <string>E:\Libraries\Music\Minecraft\</string>",
        @" <string>E:\Libraries\Music\RDR2\</string>",
        "</NavigatorFilteredFolders>",
        "<Other>x</Other>",
        "</ApplicationSettings>");
    var (updated, changed) = MusicBeeSettingsFile.AddFilteredFolder(xml, @"E:\Libraries\Music\Blockheads");
    Assert(changed, "folder was not added");
    var folders = MusicBeeSettingsFile.ReadFilteredFolders(updated);
    Assert(folders.SequenceEqual(new[] { @"E:\Libraries\Music\Blockheads\", @"E:\Libraries\Music\Minecraft\", @"E:\Libraries\Music\RDR2\" }), "folders were not kept sorted with a trailing slash");
    Assert(updated.Contains("<Other>x</Other>") && updated.Contains("\r\n <string>"), "surrounding settings or line endings were damaged");
    var (again, changedAgain) = MusicBeeSettingsFile.AddFilteredFolder(updated, @"e:\libraries\music\blockheads\");
    Assert(!changedAgain && again == updated, "adding the same folder twice changed the file");
    var (fromEmpty, _) = MusicBeeSettingsFile.AddFilteredFolder("<a><NavigatorFilteredFolders /></a>", @"C:\Music\X");
    Assert(MusicBeeSettingsFile.ReadFilteredFolders(fromEmpty).Single() == @"C:\Music\X\", "empty filter section was not filled");
    var threw = false;
    try { MusicBeeSettingsFile.AddFilteredFolder("<a></a>", @"C:\Music\X"); } catch (InvalidOperationException) { threw = true; }
    Assert(threw, "a settings file without the filter section must not be modified");
}

static void TestMusicBeeOrganiseLimit()
{
    var xml = string.Join("\r\n",
        "<ApplicationSettings>",
        "<OrganisationLibraryKeepOrganised>true</OrganisationLibraryKeepOrganised>",
        "<MusicOrganiseEnabled>true</MusicOrganiseEnabled>",
        @"<MusicFromFolder>E:\</MusicFromFolder>",
        @"<OrganisationLibraryMusicFolderPath>E:\Libraries\Music\Songs\</OrganisationLibraryMusicFolderPath>",
        "</ApplicationSettings>");
    Assert(MusicBeeLibrarySettings.OrganiseWouldMove(xml, @"E:\Libraries\Music\Straftat"), "organise from the drive root must be detected as moving soundtracks");
    Assert(!MusicBeeLibrarySettings.OrganiseWouldMove(xml, @"E:\Libraries\Music\Songs\Sub"), "the Songs folder itself is fine");
    var (limited, changed) = MusicBeeLibrarySettings.LimitOrganiseToLibraryFolder(xml);
    Assert(changed && limited.Contains(@"<MusicFromFolder>E:\Libraries\Music\Songs\</MusicFromFolder>"), "organise-from was not narrowed to Songs");
    Assert(!MusicBeeLibrarySettings.OrganiseWouldMove(limited, @"E:\Libraries\Music\Straftat"), "after narrowing, soundtracks must be left alone");
    Assert(!MusicBeeLibrarySettings.LimitOrganiseToLibraryFolder(limited).Changed, "narrowing twice must not change anything");
}

static void TestSoundtrackRepairSafety()
{
    var root = Path.Combine(Path.GetTempPath(), "spotysync-repair-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var liked = Path.Combine(root, "Midnight Journey - Major Axis.mp3");
        var copy = Path.Combine(root, "Midnight Journey - Major Axis_1.mp3");
        File.WriteAllText(liked, "old"); File.SetLastWriteTimeUtc(liked, DateTime.UtcNow.AddDays(-30));
        File.WriteAllText(copy, "new");
        var manifest = new SoundtrackManifest { Name = "Straftat", SavedUtc = DateTime.UtcNow.AddMinutes(-1), Files = ["Midnight Journey - Major Axis.mp3", "Unrelated - Song.mp3"] };
        Assert(manifest.FindStray("Midnight Journey - Major Axis.mp3", root) == copy, "repair must pick the soundtrack's own (new) copy, never the older Liked copy");
        Assert(manifest.FindStray("Unrelated - Song.mp3", root) is null, "nothing must be moved when no safe match exists");
        var older = new SoundtrackManifest { Name = "X", SavedUtc = DateTime.UtcNow.AddDays(1), Files = ["Midnight Journey - Major Axis.mp3"] };
        Assert(older.FindStray("Midnight Journey - Major Axis.mp3", root) is null, "files older than the save must never be moved");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static void TestExclusionRemoval()
{
    var path = Path.Combine(Path.GetTempPath(), "spotysync-excl-" + Guid.NewGuid().ToString("N") + ".txt");
    try
    {
        var store = new SyncExclusionStore(path);
        store.Add([new SyncExclusion { Playlist = "Chill", Artist = "A", Title = "One" }, new SyncExclusion { Playlist = "Chill", Artist = "B", Title = "Two" }]);
        var removed = store.Remove([new SyncExclusion { Playlist = "chill", Artist = "a", Title = "one" }]);
        var left = store.Load();
        Assert(removed == 1 && left.Count == 1 && left[0].Artist == "B", "removing a rule did not work (case-insensitive exact match)");
        Assert(store.Remove([new SyncExclusion { Playlist = "Other", Artist = "B", Title = "Two" }]) == 0, "a rule for another playlist must not be removed");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void TestRedirectCandidates()
{
    var c = SpotifyAuth.CallbackCandidates("http://127.0.0.1/callback");
    Assert(c.Select(x => x.Pattern).SequenceEqual(new[] { "http://127.0.0.1/callback", "http://127.0.0.1/", "http://127.0.0.1:9900/", "http://127.0.0.1:8888/callback" }),
        "unexpected candidates: " + string.Join(", ", c.Select(x => x.Pattern)));
    Assert(c.All(x => x.Prefix.EndsWith('/') && x.RedirectUri.StartsWith("http://127.0.0.1:")), "candidates need concrete loopback ports and listener prefixes");
    Assert(c[2].RedirectUri == "http://127.0.0.1:9900/" && c[3].RedirectUri == "http://127.0.0.1:8888/callback", "fixed-port forms must keep their port");
    var spotdl = SpotifyAuth.CallbackCandidates("http://127.0.0.1:9900/");
    Assert(spotdl[0].RedirectUri == "http://127.0.0.1:9900/" && spotdl.Count(x => x.Pattern.Contains(":9900")) == 1, "a configured fixed address goes first and is not repeated");
}

static void TestSoundtrackFolderName()
{
    Assert(SpotySyncService.SafeFolderName("  Red Dead: Redemption 2?  ") == "Red Dead Redemption 2", "invalid characters were not removed");
    Assert(SpotySyncService.SafeFolderName("Blockheads...") == "Blockheads", "trailing dots were not removed");
    Assert(SpotySyncService.SafeFolderName("   ") == "", "blank names must stay blank");
}

static void TestPortableSettings()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-settings-" + Guid.NewGuid().ToString("N"));
    try
    {
        var settings = PortableSettings.Load(directory);
        settings.SpotifyClientId = "test-client-id";
        settings.LocalBackend = LocalBackendKind.Both;
        settings.Direction = SyncDirection.SpotifyToLocal;
        settings.MaxRemovalsPerPlaylist = 7;
        settings.DownloadAnchorFileCount = 8;
        settings.AutoUpdateYtDlp = false;
        settings.YtDlpUpdateDays = 3;
        settings.PrimeDownloadsInMusicBee = false;
        settings.AutoYouTubeFallback = false;
        settings.Save();
        var loaded = PortableSettings.Load(directory);
        Assert(loaded.SpotifyClientId == "test-client-id", "Client ID did not round-trip");
        Assert(loaded.LocalBackend == LocalBackendKind.Both && loaded.Direction == SyncDirection.SpotifyToLocal, "enum settings did not round-trip");
        Assert(loaded.MaxRemovalsPerPlaylist == 7, "numeric setting did not round-trip");
        Assert(loaded.DownloadAnchorFileCount == 8, "download anchor setting did not round-trip");
        Assert(!loaded.AutoUpdateYtDlp && loaded.YtDlpUpdateDays == 3, "yt-dlp update settings did not round-trip");
        Assert(!loaded.PrimeDownloadsInMusicBee, "MusicBee priming setting did not round-trip");
        Assert(!loaded.AutoYouTubeFallback, "download fallback setting did not round-trip");
        Assert(loaded.TokenPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase), "portable data escaped its base folder");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestSkippedTrackParsing()
{
    var skipped = Downloader.ExtractSkippedTrackNames([
        "[1/1] SKIPPED for now: Major Axis - Rivers",
        "Major Axis - Rivers was skipped. Paste a YouTube link (or press Enter to leave skipped):",
        "Left skipped:",
        "  - Major Axis - Rivers"
    ]);
    Assert(skipped.SequenceEqual(["Major Axis - Rivers"]), "spot2yt skipped track was not parsed/deduplicated");
    var track = new TrackInfo { SpotifyId = "AAAAAAAAAAAAAAAAAAAAAA", Artists = ["Major Axis"], Title = "Rivers", DurationMs = 232000 };
    Assert(Downloader.ResolveSkippedTracks(skipped, [track]).Single().SpotifyId == track.SpotifyId, "skipped display name did not resolve to Spotify metadata");
}

static void TestYouTubeFallbackScoring()
{
    var track = new TrackInfo { Artists = ["Major Axis"], Title = "Rivers", DurationMs = 232000 };
    var safe = Downloader.ScoreYouTubeCandidate(track, "Major Axis - Rivers (Official Audio)", "Major Axis", 231);
    var cover = Downloader.ScoreYouTubeCandidate(track, "Major Axis - Rivers cover", "Random Channel", 232);
    var wrongArtist = Downloader.ScoreYouTubeCandidate(track, "Rivers", "Different Artist", 232);
    Assert(safe >= 100, "strong official YouTube candidate was rejected");
    Assert(cover < 100 && wrongArtist == 0, "unsafe fallback candidate was accepted");
}

static void TestDownloadedTrackVerification()
{
    var track = new TrackInfo { Artists = ["Lady A"], Title = "Bartender", DurationMs = 196000 };
    var local = new LocalTrack { Path = Path.Combine(Path.GetTempPath(), "Bartender.mp3"), Title = "Bartender", DurationMs = 198000 };
    Assert(TrackMatcher.IsLikelyDownloadedMatch(local, track), "title-only downloaded file was not corroborated by duration");
    local.DurationMs = 280000;
    Assert(!TrackMatcher.IsLikelyDownloadedMatch(local, track), "wrong-duration title-only file was accepted");
}

static void TestM3uSafety()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-m3u-" + Guid.NewGuid().ToString("N"));
    try
    {
        var settings = PortableSettings.Load(directory);
        settings.M3uFolder = "Playlists";
        settings.RelativeM3uPaths = true;
        var audio = Path.Combine(directory, "Music", "Artist - Song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
        File.WriteAllBytes(audio, [0]);
        var metadata = new Dictionary<string, LocalTrack>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(audio)] = new LocalTrack { Path = audio, Artist = "Artist", Title = "Song", DurationMs = 123000 }
        };
        var store = new M3uStore(settings);
        var add = new PlaylistPlan { Name = "Road Mix", DesiredLocalFiles = [audio], LocalAddFiles = [audio] };
        var added = store.Apply(add, metadata, false, 10, false);
        var playlistPath = Path.Combine(directory, "Playlists", "Road Mix.m3u8");
        Assert(added.Added == 1 && File.Exists(playlistPath), "M3U playlist was not created");
        Assert(File.ReadAllText(playlistPath).Contains(Path.Combine("..", "Music", "Artist - Song.mp3")), "M3U path was not portable/relative");

        var remove = new PlaylistPlan
        {
            Name = "Road Mix", LocalPlaylistId = playlistPath,
            ExpectedLocalDigest = PlaylistDigests.ForFiles([audio]),
            DesiredLocalFiles = [], LocalRemoveFiles = [audio]
        };
        var removed = store.Apply(remove, metadata, true, 10, false);
        Assert(removed.Removed == 1 && !File.ReadAllText(playlistPath).Contains("Artist - Song.mp3"), "M3U removal was not applied");
        Assert(Directory.EnumerateFiles(Path.Combine(directory, "Data", "Backups"), "Road Mix.m3u8", SearchOption.AllDirectories).Any(), "M3U safety backup was not written");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestScannerSeed()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-scan-" + Guid.NewGuid().ToString("N"));
    try
    {
        var music = Path.Combine(directory, "Music");
        Directory.CreateDirectory(music);
        var audio = Path.Combine(music, "opaque-name.mp3");
        File.WriteAllBytes(audio, [1, 2, 3]);
        var info = new FileInfo(audio);
        var settings = PortableSettings.Load(directory);
        settings.MusicFolder = music;
        settings.FfprobePath = "missing-ffprobe.exe";
        var seed = new LocalTrack
        {
            Path = audio, Artist = "Seed Artist", Title = "Seed Title",
            Size = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks
        };
        var scanned = new LibraryScanner(settings).ScanAsync(false, [seed]).GetAwaiter().GetResult();
        Assert(scanned.Count == 1 && scanned[0].Title == "Seed Title" && scanned[0].Artist == "Seed Artist", "MusicBee metadata seed was not reused");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestLibraryRootAndMerge()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-root-" + Guid.NewGuid().ToString("N"));
    try
    {
        var music = Path.Combine(directory, "Library", "Songs");
        var nested = Path.Combine(music, "Artist", "Album");
        Directory.CreateDirectory(nested);
        var first = Path.Combine(music, "first.mp3");
        var second = Path.Combine(nested, "second.mp3");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        var fromMusicBee = new[]
        {
            new LocalTrack { Path = first, Artist = "First Artist", Title = "First" },
            new LocalTrack { Path = second, Artist = "Second Artist", Title = "Second" }
        };
        var fromDisk = new[] { new LocalTrack { Path = first, Title = "first" } };
        var inferred = LibraryScanner.InferCommonLibraryRoot(fromMusicBee);
        var merged = LibraryScanner.MergeExisting(fromMusicBee, fromDisk);
        Assert(inferred == music, "MusicBee paths did not identify their shared library root");
        Assert(merged.Count == 2 && merged.Any(track => track.Title == "First"), "MusicBee and disk tracks were not merged without duplicates");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestDownloaderOutputFolder()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-output-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(directory);
        var parsed = Downloader.ExtractOutputFolder(["download | Output folder: " + directory, "Paste Spotify link"]);
        Assert(parsed == Path.GetFullPath(directory), "spot2yt output folder was not parsed");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestDownloadPause()
{
    var start = new System.Diagnostics.ProcessStartInfo("ping", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    using var process = DownloadProcesses.Start(start);
    try
    {
        _ = process.StandardOutput.ReadToEndAsync();
        Thread.Sleep(300);
        bool AllSuspended()
        {
            process.Refresh();
            return process.Threads.Cast<System.Diagnostics.ProcessThread>().All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == System.Diagnostics.ThreadWaitReason.Suspended);
        }
        Assert(DownloadProcesses.HasRunning, "the started downloader process was not tracked");
        DownloadProcesses.Pause();
        Thread.Sleep(150);
        Assert(DownloadProcesses.Paused && AllSuspended(), "Pause did not suspend the downloader process");
        var waiting = DownloadProcesses.WaitIfPausedAsync(CancellationToken.None);
        Assert(!waiting.IsCompleted, "a new download did not wait while paused");
        DownloadProcesses.Resume();
        Assert(waiting.Wait(2000), "Resume did not release a waiting download");
        Thread.Sleep(150);
        Assert(!DownloadProcesses.Paused && !AllSuspended(), "Resume did not resume the downloader process");
    }
    finally
    {
        DownloadProcesses.Reset();
        try { process.Kill(true); process.WaitForExit(3000); } catch { }
    }
}

static void TestLibraryReconcile()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-live-" + Guid.NewGuid().ToString("N"));
    try
    {
        var music = Path.Combine(directory, "Songs");
        Directory.CreateDirectory(music);
        var kept = Path.Combine(music, "Artist A - Kept.mp3");
        var deleted = Path.Combine(music, "Artist B - Deleted.mp3");
        var added = Path.Combine(music, "Artist C - New Song.opus");
        var partial = Path.Combine(music, "Artist D - Downloading.webm.part");
        File.WriteAllBytes(kept, [1, 2, 3]);
        File.WriteAllBytes(added, [4, 5, 6]);
        File.WriteAllBytes(partial, [7]);
        File.SetLastWriteTimeUtc(added, DateTime.UtcNow.AddMinutes(-1));
        var settings = PortableSettings.Load(directory);
        settings.MusicFolder = music;
        settings.FfprobePath = Path.Combine(directory, "missing-ffprobe.exe"); // file names only
        var library = new List<LocalTrack> { new() { Path = kept, Artist = "Artist A", Title = "Kept" }, new() { Path = deleted, Artist = "Artist B", Title = "Deleted" } };
        var (newTracks, gone) = new LibraryScanner(settings).ReconcileAsync(library).GetAwaiter().GetResult();
        Assert(newTracks.Count == 1 && newTracks[0].Path == added && newTracks[0].Title.Contains("New Song"), "a new song in the music folder was not picked up: " + string.Join(", ", newTracks.Select(t => t.Path)));
        Assert(gone.Count == 1 && gone[0].Path == deleted, "a deleted song was not dropped");
        Assert(LibraryScanner.IsPartialDownload(partial) && !LibraryScanner.IsPartialDownload(added), "partial downloads were not told apart from finished songs");
        var unplugged = new List<LocalTrack> { new() { Path = @"Q:\Not\Here\song.mp3" } };
        if (!Directory.Exists(@"Q:\"))
            Assert(new LibraryScanner(settings).ReconcileAsync(unplugged).GetAwaiter().GetResult().Removed.Count == 0, "songs on a missing drive were treated as deleted");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestLiveSettings()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-live-settings-" + Guid.NewGuid().ToString("N"));
    try
    {
        var settings = PortableSettings.Load(directory);
        settings.UiConsoleSplit = true; settings.UiActivityRange = 900;
        settings.UiDashboardCards = "~console:1:300:0:150,actions:2:320:1:160";
        settings.Save();
        var loaded = PortableSettings.Load(directory);
        Assert(loaded.UiConsoleSplit && loaded.UiActivityRange == 900, "console split / graph range did not round-trip");
        Assert(loaded.UiDashboardCards == "~console:1:300:0:150,actions:2:320:1:160", "dashboard positions did not round-trip");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestQueueSkipsOwnedSongs()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-queue-" + Guid.NewGuid().ToString("N"));
    try
    {
        var music = Path.Combine(directory, "Songs");
        Directory.CreateDirectory(music);
        File.WriteAllBytes(Path.Combine(music, "Artist A - Song A.mp3"), [1, 2, 3]);
        var settings = PortableSettings.Load(directory);
        settings.MusicFolder = music;
        settings.FfprobePath = Path.Combine(directory, "missing-ffprobe.exe");
        Directory.CreateDirectory(settings.DataDirectory);
        var owned = new TrackInfo { SpotifyId = "AAAAAAAAAAAAAAAAAAAAAA", Title = "Song A", Artists = ["Artist A"], DurationMs = 0 };
        var missing = new TrackInfo { SpotifyId = "BBBBBBBBBBBBBBBBBBBBBB", Title = "Song B", Artists = ["Artist B"] };
        var cache = new PlaylistTrackCache();
        cache.Playlists["PPPPPPPPPPPPPPPPPPPPPP"] = new PlaylistTrackCacheEntry { Tracks = [owned, missing] };
        JsonStore.SaveAtomic(settings.PlaylistCachePath, cache);
        using var service = new SpotySyncService(settings);
        var method = typeof(SpotySyncService).GetMethod("ExpandAndSkipOwnedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var queue = ((Task<List<string>>)method.Invoke(service, [new List<string> { "https://open.spotify.com/playlist/PPPPPPPPPPPPPPPPPPPPPP", "https://www.youtube.com/watch?v=x" }, CancellationToken.None])!).GetAwaiter().GetResult();
        Assert(queue.Count == 2 && queue[0] == "https://open.spotify.com/track/BBBBBBBBBBBBBBBBBBBBBB" && queue[1].Contains("youtube"),
            "a known playlist was not expanded to its missing songs only: " + string.Join(", ", queue));
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static void TestDownloadFormat()
{
    var directory = Path.Combine(Path.GetTempPath(), "spotysync-format-" + Guid.NewGuid().ToString("N"));
    try
    {
        var settings = PortableSettings.Load(directory);
        Assert(settings.DownloadFormat == "Opus", "Opus is not the default download format");
        settings.DownloadFormat = "MP3";
        settings.Save();
        Assert(PortableSettings.Load(directory).DownloadFormat == "MP3", "download format did not round-trip");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    Assert(Downloader.NormalizeFormat(" mp3 ") == "MP3" && Downloader.NormalizeFormat("wav") == "Opus" && Downloader.NormalizeFormat(null) == "Opus",
        "unknown or untidy format values are not cleaned up");
    Assert(string.Join(' ', Downloader.SpotDlFormatArguments("Opus")) == "--format opus --bitrate disable", "Opus no longer copies the source audio in spotDL");
    Assert(string.Join(' ', Downloader.SpotDlFormatArguments("MP3")) == "--format mp3 --bitrate 320k", "MP3 is not 320 kbps in spotDL");
    Assert(string.Join(' ', Downloader.SpotDlFormatArguments("M4A")) == "--format m4a --bitrate disable", "M4A is not copied in spotDL");
    Assert(string.Join(' ', Downloader.YtDlpFormatArguments("Opus")).EndsWith("--audio-format best"), "Opus no longer keeps the source audio in yt-dlp");
    Assert(string.Join(' ', Downloader.YtDlpFormatArguments("MP3")).Contains("--audio-format mp3 --audio-quality 320K"), "MP3 is not 320 kbps in yt-dlp");
    Assert(string.Join(' ', Downloader.YtDlpFormatArguments("M4A")).Contains("bestaudio[ext=m4a]"), "M4A does not prefer the AAC stream in yt-dlp");
}
