using System.Diagnostics;
using System.Text.Json;

namespace SpotySync;

public sealed class DownloadRunResult
{
    public int ExitCode { get; set; }
    public int ReportedErrors { get; set; }
    public string DownloaderName { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public List<string> ChangedAudio { get; set; } = new();
    public List<string> SkippedTracks { get; set; } = new();
    public List<string> RecoveredFallbackTracks { get; set; } = new();
    /// <summary>Song names spotDL said it couldn't find on YouTube Music.</summary>
    public List<string> NotFound { get; set; } = new();
    public bool Succeeded => ExitCode == 0 && ReportedErrors == 0 && ChangedAudio.Count > 0;
}

public sealed class Downloader
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wav", ".wma", ".aac", ".aiff", ".wv" };
    private const string AccountSleepMin = "4", AccountSleepMax = "10";

    // Download pace comes from the speed dial (DownloadSpeed). If YouTube or Spotify start limiting, it halves for the
    // rest of the download and the log says so - that is how the real limit shows up.
    private static DownloadSpeed.Level Pace => DownloadSpeed.Current;

    /// <summary>Download file types (setting DownloadFormat). Opus is the default.</summary>
    public static readonly string[] FormatNames = ["Opus", "MP3", "M4A"];

    /// <summary>The setting value cleaned up: Opus, MP3 or M4A. Anything unknown means Opus.</summary>
    public static string NormalizeFormat(string? format) =>
        FormatNames.FirstOrDefault(n => n.Equals(format?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Opus";

    /// <summary>spotDL arguments for the chosen file type.</summary>
    public static string[] SpotDlFormatArguments(string? format) => NormalizeFormat(format) switch
    {
        // spotDL's own default re-encodes to 128 kbps MP3, so MP3 asks for 320 kbps.
        "MP3" => ["--format", "mp3", "--bitrate", "320k"],
        // spotDL picks YouTube's AAC stream for M4A; with the bitrate option off it is copied unchanged.
        "M4A" => ["--format", "m4a", "--bitrate", "disable"],
        // Opus with the bitrate option off copies YouTube Music's best stream unchanged.
        _ => ["--format", "opus", "--bitrate", "disable"]
    };

    /// <summary>yt-dlp arguments (stream choice + audio extraction) for the chosen file type.</summary>
    public static string[] YtDlpFormatArguments(string? format) => NormalizeFormat(format) switch
    {
        "MP3" => ["--format", "bestaudio/best", "--extract-audio", "--audio-format", "mp3", "--audio-quality", "320K"],
        // Prefer the AAC (m4a) stream so it is copied instead of converted.
        "M4A" => ["--format", "bestaudio[ext=m4a]/bestaudio/best", "--extract-audio", "--audio-format", "m4a"],
        // Keep the source's best audio stream bit-for-bit (normally YouTube's Opus format 251).
        _ => ["--format", "bestaudio/best", "--extract-audio", "--audio-format", "best"]
    };

    private static readonly System.Text.RegularExpressions.Regex RateLimited = new(
        @"HTTP Error 429|429 Client Error|Too Many Requests|rate.?limit|Sign in to confirm you.re not a bot|confirm you.re not a bot",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Steps the pace down after a run whose output shows rate limiting.</summary>
    private void BackOffIfLimited(IEnumerable<string> output)
    {
        var hit = output.FirstOrDefault(line => RateLimited.IsMatch(line));
        if (hit is null) return;
        var before = Pace;
        if (!DownloadSpeed.StepDownForLimit())
        {
            log("WARNING: YouTube/Spotify is limiting downloads even at the slowest speed. Take a break (an hour or more) before downloading again.");
            return;
        }
        log($"WARNING: hit a rate limit at {before.Label} ({hit.Trim()}). Slowed down to {Pace.Label}. " +
            "Turn the speed dial up again later if you like; it is saved only when you change it yourself.");
    }
    private readonly PortableSettings settings;
    private readonly Action<string> log;

    public Downloader(PortableSettings settings, Action<string>? log = null)
    {
        this.settings = settings;
        this.log = log ?? (_ => { });
        DownloadSpeed.Load(settings);
    }

    public async Task<DownloadRunResult> RunAsync(IReadOnlyList<string> urls, IReadOnlyList<TrackInfo>? expectedTracks = null, CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0) return new DownloadRunResult();
        var executable = settings.ResolvePath(settings.DownloaderPath);
        if (!File.Exists(executable)) throw new FileNotFoundException("Downloader was not found. Set Downloader in SpotySync.ini to spot2yt.exe or spotdl.exe.", executable);
        if (IsSpot2Yt(executable) && !CustomDownloaderIsConfigured(executable))
            throw new InvalidOperationException("The bundled downloader needs your Spotify Client ID and Client secret. Enter both once in Settings and click Save settings.");
        await UpdateYtDlpIfNeededAsync(Path.GetDirectoryName(executable)!, cancellationToken);
        var primary = IsSpot2Yt(executable)
            ? await RunSpot2YtAsync(executable, urls, expectedTracks ?? Array.Empty<TrackInfo>(), cancellationToken)
            : await RunOnceAsync(executable, urls, cancellationToken, songData: expectedTracks);
        if (!IsSpot2Yt(executable) && primary.ExitCode != 0 && primary.ChangedAudio.Count == 0)
        {
            // spotDL sometimes crashes while reading Spotify (e.g. "KeyError: 'ownerV2'")
            // and the same link works moments later, so try once more before giving up.
            log($"{primary.DownloaderName} stopped before downloading anything; retrying once in 15 seconds.");
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            primary = await RunOnceAsync(executable, urls, cancellationToken, songData: expectedTracks);
        }
        var custom =Path.Combine(settings.BaseDirectory, "Tools", Platform.Exe("spot2yt"));
        if (!IsSpot2Yt(executable) && !primary.Succeeded && File.Exists(custom) && CustomDownloaderIsConfigured(custom))
        {
            log($"{primary.DownloaderName} produced no verified audio or reported errors; retrying with bundled spot2yt.");
            var fallback = await RunSpot2YtAsync(custom, urls, expectedTracks ?? Array.Empty<TrackInfo>(), cancellationToken);
            fallback.ChangedAudio = primary.ChangedAudio.Concat(fallback.ChangedAudio)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            primary = fallback;
        }
        if (IsSpot2Yt(executable) || IsSpot2Yt(primary.DownloaderName))
            primary = await RecoverSkippedTracksAsync(primary, expectedTracks ?? Array.Empty<TrackInfo>(), cancellationToken);
        return primary;
    }

    /// <summary>Re-downloads Spotify tracks even when a file with the same name exists (quality upgrades).</summary>
    public async Task<DownloadRunResult> RunSpotifyReplaceAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0) return new DownloadRunResult();
        var executable = settings.ResolvePath(settings.DownloaderPath);
        if (!File.Exists(executable)) throw new FileNotFoundException("Downloader was not found.", executable);
        if (IsSpot2Yt(executable)) throw new InvalidOperationException("Quality upgrades need spotDL as the downloader (Downloader=Tools\\spotdl.exe).");
        await UpdateYtDlpIfNeededAsync(Path.GetDirectoryName(executable)!, cancellationToken);
        return await RunOnceAsync(executable, urls, cancellationToken, "force");
    }

    public string FfprobePath =>
        Path.Combine(Path.GetDirectoryName(settings.ResolvePath(settings.DownloaderPath)) ?? settings.BaseDirectory, Platform.Exe("ffprobe"));

    public async Task<DownloadRunResult> RunManualAsync(IReadOnlyList<string> urls, IReadOnlyList<TrackInfo>? expectedTracks = null, CancellationToken cancellationToken = default)
    {
        var cleaned = urls.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleaned.Count == 0) return new DownloadRunResult();
        var spotifyUrls = cleaned.Where(IsSpotifyLink).ToList();
        var mediaUrls = cleaned.Where(x => !IsSpotifyLink(x)).ToList();
        var results = new List<DownloadRunResult>();
        if (spotifyUrls.Count > 0)
        {
            var batch = await RunAsync(spotifyUrls, expectedTracks, cancellationToken);
            if (spotifyUrls.Count > 1 && batch.ExitCode != 0 && batch.ChangedAudio.Count == 0)
            {
                // One unreadable link (deleted/private playlist, spotDL parse error) aborts a whole
                // batch before anything downloads. Retry link by link so only the bad one is lost.
                log($"The batch of {spotifyUrls.Count} links failed; retrying them one at a time so a single bad link cannot block the rest.");
                var singles = new List<DownloadRunResult>();
                var failed = new List<string>();
                for (var i = 0; i < spotifyUrls.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = spotifyUrls[i];
                    log($"Link {i + 1} of {spotifyUrls.Count}: {url}");
                    var one = await RunAsync(new[] { url }, expectedTracks, cancellationToken);
                    singles.Add(one);
                    if (one.ExitCode != 0 && one.ChangedAudio.Count == 0 && CachedPlaylistTrackUrls(url) is { Count: > 0 } tracks)
                    {
                        // spotDL can't read some playlists (KeyError: 'ownerV2' in its playlist parser). SpotySync
                        // already knows the playlist's songs from its Spotify cache, so download those directly.
                        log($"spotDL couldn't read this playlist (a known spotDL bug); downloading its {tracks.Count} song(s) from SpotySync's copy of the playlist instead.");
                        log($"Found {tracks.Count} songs in playlist {url}");
                        var parts = new List<DownloadRunResult> { one };
                        foreach (var chunk in tracks.Chunk(50))
                        {
                            await DownloadProcesses.WaitIfPausedAsync(cancellationToken);
                            parts.Add(await RunAsync(chunk, expectedTracks, cancellationToken));
                        }
                        one = Merge(parts);
                        one.ExitCode = parts.Skip(1).All(p => p.ExitCode == 0) ? 0 : 1;
                        singles[^1] = one;
                    }
                    if (one.ExitCode != 0 && one.ChangedAudio.Count == 0) failed.Add(url);
                }
                foreach (var url in failed)
                    log($"ERROR: Could not download {url}. The link may be deleted, private or unreadable by the downloader; the other links were still processed.");
                batch = Merge(singles);
                if (failed.Count == 0) batch.ExitCode = 0; // the batch failure was transient
            }
            results.Add(batch);
        }
        if (mediaUrls.Count > 0) results.Add(await RunYtDlpAsync(mediaUrls, cancellationToken));
        return Merge(results);
    }

    /// <summary>Track links of a Spotify playlist from SpotySync's own playlist cache (empty when it isn't cached).</summary>
    internal List<string> CachedPlaylistTrackUrls(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url, @"playlist[/:]([A-Za-z0-9]{22})");
        if (!match.Success) return new List<string>();
        try
        {
            var cache = JsonStore.Load(settings.PlaylistCachePath, () => new PlaylistTrackCache());
            return cache.Playlists.TryGetValue(match.Groups[1].Value, out var entry)
                ? entry.Tracks.Where(t => !t.IsLocal && t.SpotifyId.Length > 0).Select(t => "https://open.spotify.com/track/" + t.SpotifyId).Distinct().ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    private async Task<DownloadRunResult> RunYtDlpAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        var toolDirectory = Path.Combine(settings.BaseDirectory, "Tools");
        var executable = Path.Combine(toolDirectory, Platform.Exe("yt-dlp"));
        if (!File.Exists(executable)) throw new FileNotFoundException("The bundled yt-dlp.exe was not found in Tools.", executable);
        await UpdateYtDlpIfNeededAsync(toolDirectory, cancellationToken);
        // A signed-in browser (YouTube Premium) unlocks the ~256 kbps streams, so use it first.
        var browser = ConfiguredCookieBrowser(toolDirectory);
        var primary = await RunYtDlpOnceAsync(executable, urls, browser, null, false, null, cancellationToken);
        if (!primary.Succeeded && browser is not null)
        {
            log($"Direct media download with {BrowserName(browser)} cookies produced no audio; retrying once without them.");
            var retry = await RunYtDlpOnceAsync(executable, urls, null, null, false, null, cancellationToken);
            retry.ChangedAudio = primary.ChangedAudio.Concat(retry.ChangedAudio).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return retry;
        }
        return primary;
    }

    private async Task<DownloadRunResult> RunYtDlpOnceAsync(string executable, IReadOnlyList<string> urls, string? cookieBrowser,
        string? outputTemplate, bool noPlaylist, string? downloadSection, CancellationToken cancellationToken)
    {
        var musicFolder = settings.ResolvePath(settings.MusicFolder);
        Directory.CreateDirectory(musicFolder);
        var workingOutputFolder = string.IsNullOrWhiteSpace(outputTemplate)
            ? musicFolder
            : Path.GetDirectoryName(outputTemplate) ?? musicFolder;
        Directory.CreateDirectory(workingOutputFolder);
        var before = Snapshot(workingOutputFolder);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = musicFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["PATH"] = Path.GetDirectoryName(executable)! + Path.PathSeparator +
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        foreach (var argument in YtDlpFormatArguments(settings.DownloadFormat)) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--embed-metadata");
        start.ArgumentList.Add("--embed-thumbnail");
        start.ArgumentList.Add("--windows-filenames");
        start.ArgumentList.Add("--no-overwrites");
        start.ArgumentList.Add("--ignore-errors");
        start.ArgumentList.Add("--newline"); // one progress line per update, so it can be read live
        if (noPlaylist) start.ArgumentList.Add("--no-playlist");
        if (!string.IsNullOrWhiteSpace(downloadSection))
        {
            start.ArgumentList.Add("--download-sections");
            start.ArgumentList.Add(downloadSection);
        }
        if (cookieBrowser is not null)
        {
            start.ArgumentList.Add("--cookies-from-browser");
            start.ArgumentList.Add(cookieBrowser);
            // pauses between signed-in downloads follow the Download speed setting
            if (Pace.PauseMax > 0)
            {
                start.ArgumentList.Add("--sleep-interval"); start.ArgumentList.Add(Pace.PauseMin.ToString());
                start.ArgumentList.Add("--max-sleep-interval"); start.ArgumentList.Add(Pace.PauseMax.ToString());
            }
        }
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputTemplate ?? Path.Combine(musicFolder, "%(title)s - %(uploader)s.%(ext)s"));
        foreach (var url in urls) start.ArgumentList.Add(url);

        log("Starting direct media download with yt-dlp" + (cookieBrowser is null ? "." : $" and {cookieBrowser} cookies."));
        await DownloadProcesses.WaitIfPausedAsync(cancellationToken);
        using var process = DownloadProcesses.Start(start);
        var stdout = PumpAsync(process.StandardOutput, "download", cancellationToken);
        var stderr = PumpAsync(process.StandardError, "download", cancellationToken);
        List<string> output;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            output = (await Task.WhenAll(stdout, stderr)).SelectMany(x => x).ToList();
            await Task.Delay(500, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            throw;
        }
        var after = Snapshot(workingOutputFolder);
        return new DownloadRunResult
        {
            ExitCode = process.ExitCode,
            ReportedErrors = output.Count(IsDownloaderError),
            DownloaderName = Platform.Exe("yt-dlp"),
            OutputFolder = musicFolder,
            ChangedAudio = after.Where(x => !before.TryGetValue(x.Key, out var old) || old != x.Value).Select(x => x.Key).ToList()
        };
    }

    private async Task<DownloadRunResult> RunSpot2YtAsync(string executable, IReadOnlyList<string> urls,
        IReadOnlyList<TrackInfo> expectedTracks, CancellationToken cancellationToken)
    {
        // A queued second Spotify URL can be mistaken for the answer to spot2yt's
        // missing-source prompt. One process per submitted URL keeps the protocol
        // deterministic. Normal backups still use one temporary playlist URL.
        if (urls.Count <= 1)
        {
            return await RunOnceAsync(executable, urls, cancellationToken);
        }

        var results = new List<DownloadRunResult>();
        foreach (var url in urls)
        {
            try
            {
                var spotifyId = SpotifyIdExtractor.FromText(url);
                var expected = expectedTracks.Where(track => spotifyId.Length == 0 || track.SpotifyId.Equals(spotifyId, StringComparison.OrdinalIgnoreCase)).ToList();
                results.Add(await RunOnceAsync(executable, new[] { url }, cancellationToken));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log("ERROR: one submitted download failed; continuing with the remaining links: " + ex.Message);
                results.Add(new DownloadRunResult { ExitCode = 1, ReportedErrors = 1, DownloaderName = Path.GetFileName(executable) });
            }
        }
        return Merge(results);
    }

    private async Task<DownloadRunResult> RunOnceAsync(string executable, IReadOnlyList<string> urls, CancellationToken cancellationToken,
        string overwriteMode = "skip", IReadOnlyList<TrackInfo>? songData = null)
    {
        var musicFolder = settings.ResolvePath(settings.MusicFolder);
        Directory.CreateDirectory(musicFolder);
        var before = Snapshot(musicFolder);
        var startedUtc = DateTime.UtcNow;
        var isSpot2Yt = IsSpot2Yt(executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = isSpot2Yt ? Path.GetDirectoryName(executable)! : musicFolder,
            RedirectStandardInput = isSpot2Yt,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Keep the toolchain genuinely portable: spotDL can discover the ffmpeg,
        // ffprobe, Deno and yt-dlp executables shipped in its own Tools folder.
        var toolDirectory = Path.GetDirectoryName(executable)!;
        start.Environment["PATH"] = toolDirectory + Path.PathSeparator +
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        if (isSpot2Yt) start.Environment["PYTHONUNBUFFERED"] = "1";
        // Song titles with symbols (e.g. "ʕ̡̢̡ʘ̅͟͜͡ʘ̲̅ʔ") crashed spotDL's console output under
        // the legacy Windows code page; UTF-8 output keeps the run going.
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        // spotDL's console library wraps lines at 80 characters when it isn't writing to a window, which split song
        // names over several log lines (and broke reading them). A wide console keeps each message on one line.
        start.Environment["COLUMNS"] = "1000";
        notFoundThisRun.Clear();
        string? premiumBrowser = null;
        string? songFile = null;
        var directUrls = urls;
        if (!isSpot2Yt)
        {
            premiumBrowser = ConfiguredCookieBrowser(toolDirectory);
            // Songs SpotySync already knows go to spotDL as a song file, so spotDL never asks Spotify about them
            // (its shared Spotify key is so rate-limited that every lookup took ~13 s).
            (songFile, directUrls) = await WriteSongFileAsync(urls, songData, cancellationToken);
        }
        if (premiumBrowser is not null)
        {
            await CheckPremiumAudioAsync(toolDirectory, premiumBrowser, cancellationToken);
            await WarmUpPoTokenHelperAsync(toolDirectory, cancellationToken); // keep it warm for this batch
        }

        var output = new List<string>();
        var rawExitCode = 0;
        try
        {
            while (true)
            {
                var pace = Pace;
                var speedVersion = DownloadSpeed.Version;
                if (!isSpot2Yt)
                {
                    start.ArgumentList.Clear();
                    start.ArgumentList.Add("download");
                    if (songFile is not null) start.ArgumentList.Add(songFile);
                    foreach (var url in directUrls) start.ArgumentList.Add(url);
                    start.ArgumentList.Add("--output");
                    start.ArgumentList.Add(Path.Combine(musicFolder, "{artists} - {title}.{output-ext}"));
                    start.ArgumentList.Add("--overwrite");
                    start.ArgumentList.Add(overwriteMode);
                    foreach (var argument in SpotDlFormatArguments(settings.DownloadFormat)) start.ArgumentList.Add(argument);
                    // No lyrics: Genius answers 429 and Musixmatch 403 to spotDL's shared keys, so every song waited
                    // ~20 s for lyrics it never got.
                    start.ArgumentList.Add("--lyrics");
                    start.ArgumentList.Add("--threads");
                    start.ArgumentList.Add(pace.Threads.ToString());
                    var ytDlpArgs = new List<string>();
                    if (premiumBrowser is not null)
                    {
                        // Signed in (YouTube Premium): spotDL's built-in yt-dlp then sees the ~256 kbps
                        // streams (needs the bgutil PO-token plugin).
                        log($"Using the signed-in {BrowserName(premiumBrowser)} YouTube session for higher-quality audio ({pace.Label}" +
                            (pace.PauseMax > 0 ? ")." : ", no pauses)."));
                        ytDlpArgs.Add("--cookies-from-browser " + premiumBrowser);
                        ytDlpArgs.AddRange(PoTokenHelperArgs(toolDirectory).Select(argument => argument.StartsWith("--") ? argument : "\"" + argument + "\""));
                    }
                    else log($"Downloading {pace.Label}.");
                    if (pace.PauseMax > 0) ytDlpArgs.Add($"--sleep-interval {pace.PauseMin} --max-sleep-interval {pace.PauseMax}");
                    if (ytDlpArgs.Count > 0)
                    {
                        start.ArgumentList.Add("--yt-dlp-args");
                        start.ArgumentList.Add(string.Join(' ', ytDlpArgs));
                    }
                    start.ArgumentList.Add("--print-errors");
                }

                await DownloadProcesses.WaitIfPausedAsync(cancellationToken);
                Interlocked.Exchange(ref errorStreak, 0);
                lastFailedVideo = null;
                using var process = DownloadProcesses.Start(start);
                var stdout = isSpot2Yt
                    ? PumpSpot2YtAsync(process.StandardOutput, process.StandardInput, cancellationToken)
                    : PumpAsync(process.StandardOutput, "download", cancellationToken);
                var stderr = PumpAsync(process.StandardError, "download", cancellationToken);
                var restarted = false;
                Task<string?>? rateCheck = null;
                string? rateLimitReason = null;
                try
                {
                    if (isSpot2Yt)
                    {
                        foreach (var url in urls)
                        {
                            log("Submitting to spot2yt: " + url);
                            await process.StandardInput.WriteLineAsync(url.AsMemory(), cancellationToken);
                            await process.StandardInput.FlushAsync(cancellationToken);
                        }
                        await process.WaitForExitAsync(cancellationToken);
                    }
                    else
                    {
                        // A speed change while spotDL runs restarts it at the new speed. Finished songs are kept and
                        // skipped at once (they come from the song file, so there is nothing to look up).
                        var exited = process.WaitForExitAsync(cancellationToken);
                        while (!exited.IsCompleted)
                        {
                            await Task.WhenAny(exited, Task.Delay(400, cancellationToken));
                            cancellationToken.ThrowIfCancellationRequested();
                            // a run of failed songs: ask YouTube once why (spotDL hides the reason)
                            if (rateCheck is null && Volatile.Read(ref errorStreak) >= RateLimitStreak && lastFailedVideo is { } probeUrl)
                                rateCheck = RateLimitReasonAsync(toolDirectory, premiumBrowser, probeUrl, cancellationToken);
                            if (rateCheck is { IsCompleted: true })
                            {
                                rateLimitReason = await rateCheck;
                                rateCheck = null;
                                if (rateLimitReason is not null)
                                {
                                    restarted = true;
                                    StopProcess(process);
                                    break;
                                }
                                Interlocked.Exchange(ref errorStreak, 0); // something else failed them; keep going
                            }
                            if (exited.IsCompleted || DownloadSpeed.Version == speedVersion) continue;
                            await Task.Delay(1500, cancellationToken); // let a quick run of clicks settle
                            if (exited.IsCompleted) break;
                            log($"Download speed changed to {Pace.Label}; restarting this batch at the new speed (finished songs are kept).");
                            restarted = true;
                            StopProcess(process);
                            break;
                        }
                        await exited.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
                    }
                    output.AddRange((await Task.WhenAll(stdout, stderr)).SelectMany(lines => lines));
                    await Task.Delay(500, cancellationToken);
                    // the batch also ends quickly when YouTube refuses every song
                    if (!isSpot2Yt && !restarted && Volatile.Read(ref errorStreak) >= RateLimitStreak && lastFailedVideo is { } endUrl)
                    {
                        rateLimitReason = await (rateCheck ?? RateLimitReasonAsync(toolDirectory, premiumBrowser, endUrl, cancellationToken));
                        restarted = rateLimitReason is not null;
                    }
                }
                catch (OperationCanceledException)
                {
                    StopProcess(process);
                    throw;
                }
                if (rateLimitReason is not null)
                {
                    ForgetNotFoundSince(streakStartedUtc);
                    // the refused songs are retried, so their errors don't count against the batch
                    output.RemoveAll(line => line.Contains("AudioProviderError", StringComparison.Ordinal));
                    await WaitOutRateLimitAsync(toolDirectory, premiumBrowser, lastFailedVideo!, rateLimitReason, cancellationToken);
                    continue;
                }
                if (restarted) continue;
                rawExitCode = process.ExitCode;
                break;
            }
        }
        finally
        {
            if (songFile is not null) try { File.Delete(songFile); } catch { }
        }
        var after = Snapshot(musicFolder);
        var changed = after.Where(x => !before.TryGetValue(x.Key, out var old) || old != x.Value).Select(x => x.Key).ToList();
        var reportedOutputFolder = isSpot2Yt ? ExtractOutputFolder(output) : null;
        if (!string.IsNullOrWhiteSpace(reportedOutputFolder) &&
            !Path.GetFullPath(reportedOutputFolder).Equals(Path.GetFullPath(musicFolder), StringComparison.OrdinalIgnoreCase))
        {
            log($"WARNING: spot2yt reported a different output folder: {reportedOutputFolder}. SpotySync will use that folder for verification.");
            changed.AddRange(FilesChangedSince(reportedOutputFolder, startedUtc.AddSeconds(-2)));
            changed = changed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (premiumBrowser is not null) await ReportAudioQualityAsync(toolDirectory, premiumBrowser, changed, cancellationToken);
        BackOffIfLimited(output);
        var reportedErrors = output.Count(IsDownloaderError);
        // spotDL exits 1 when only its closing progress display crashed on a song name: that is not a failed run
        var exitCode = rawExitCode != 0 && reportedErrors == 0 && output.Contains(CosmeticExitMarker) ? 0 : rawExitCode;
        var skippedTracks = ExtractSkippedTrackNames(output);
        if (rawExitCode == 0 && reportedErrors > 0)
            log($"WARNING: {Path.GetFileName(executable)} returned exit code 0 but reported {reportedErrors} download error line(s).");
        return new DownloadRunResult
        {
            ExitCode = exitCode, ReportedErrors = reportedErrors,
            DownloaderName = Path.GetFileName(executable),
            OutputFolder = reportedOutputFolder ?? musicFolder,
            ChangedAudio = changed,
            SkippedTracks = skippedTracks,
            NotFound = notFoundThisRun.Keys.ToList()
        };
    }

    // songs spotDL couldn't find during the current run (each is logged once, even when the batch restarts), with when
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> notFoundThisRun = new(StringComparer.OrdinalIgnoreCase);

    // YouTube rate limits: spotDL only says "YT-DLP download error", so a run of those in a row is checked with yt-dlp
    private const int RateLimitStreak = 12;
    private static readonly TimeSpan RateLimitRecheck = TimeSpan.FromMinutes(10);
    private int errorStreak;
    private DateTime streakStartedUtc;
    private volatile string? lastFailedVideo;
    private static readonly System.Text.RegularExpressions.Regex VideoLink = new(
        @"https?://(?:music|www)\.youtube\.com/watch\?v=[\w-]{11}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Asks yt-dlp about one failed song; returns YouTube's rate-limit message, or null when it isn't one.</summary>
    private async Task<string?> RateLimitReasonAsync(string toolDirectory, string? browser, string url, CancellationToken cancellationToken)
    {
        var probe = await ProbeFormatsAsync(toolDirectory, browser, url, cancellationToken);
        return probe.Diagnostics.FirstOrDefault(line => line.StartsWith("ERROR", StringComparison.Ordinal) && RateLimited.IsMatch(line));
    }

    /// <summary>YouTube's message without yt-dlp's prefix and advice, e.g. "Your account has been rate-limited by YouTube for up to an hour."</summary>
    public static string RateLimitMessage(string line)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(line, @"^ERROR:\s*(?:\[[^\]]+\]\s*)?(?:[\w-]{11}:\s*)?", "");
        var limited = text.IndexOf("Your account", StringComparison.OrdinalIgnoreCase);
        if (limited > 0) text = text[limited..];
        var advice = text.IndexOf(" It is recommended", StringComparison.OrdinalIgnoreCase);
        if (advice > 0) text = text[..advice];
        return text.Length > 200 ? text[..200] + "…" : text.Trim();
    }

    /// <summary>"Not on YouTube Music" results that came in while YouTube was refusing songs aren't trusted; the retry checks them again.</summary>
    private void ForgetNotFoundSince(DateTime sinceUtc)
    {
        var doubtful = notFoundThisRun.Where(p => p.Value >= sinceUtc.AddMinutes(-2)).Select(p => p.Key).ToList();
        foreach (var name in doubtful) notFoundThisRun.TryRemove(name, out _);
        if (doubtful.Count > 0)
            log($"{doubtful.Count} song(s) that looked like they weren't on YouTube Music during the limit will be searched again.");
    }

    /// <summary>
    /// Waits out a YouTube rate limit: slows down, checks every 10 minutes, and returns once YouTube serves songs again.
    /// The batch then restarts; finished songs are skipped and the failed ones are tried again.
    /// </summary>
    private async Task WaitOutRateLimitAsync(string toolDirectory, string? browser, string url, string reason, CancellationToken cancellationToken)
    {
        var before = Pace;
        var slowed = DownloadSpeed.StepDownForLimit();
        log($"YouTube rate limit: {RateLimitMessage(reason)} Downloading waits and continues by itself once YouTube allows it again; " +
            "the songs that failed are retried" + (slowed ? $". Slowed down from {before.Label} to {Pace.Label}." : "."));
        var started = DateTime.Now;
        while (true)
        {
            log($"YouTube rate limit: checking again at {DateTime.Now.Add(RateLimitRecheck):HH:mm}" +
                (DateTime.Now - started >= RateLimitRecheck ? $" (waiting {(DateTime.Now - started).TotalMinutes:0} min so far)" : "") +
                ". Press Stop to end the download instead.");
            await Task.Delay(RateLimitRecheck, cancellationToken);
            await DownloadProcesses.WaitIfPausedAsync(cancellationToken);
            if (await RateLimitReasonAsync(toolDirectory, browser, url, cancellationToken) is null) break;
        }
        log($"YouTube rate limit lifted after {(DateTime.Now - started).TotalMinutes:0} min; continuing at {Pace.Label}.");
    }

    private static readonly System.Text.RegularExpressions.Regex SpotifyTrackLink = new(
        @"(?:open\.spotify\.com/(?:intl-[A-Za-z-]+/)?track/|spotify:track:)([A-Za-z0-9]{22})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static bool coverLookupUnavailable;

    private string SongFileFolder => Path.Combine(settings.DataDirectory, "spotdl-queue");
    private string CoverCachePath => Path.Combine(settings.DataDirectory, "cover-cache.json");

    /// <summary>
    /// Writes a spotDL song file (.spotdl) for the Spotify track links SpotySync already has details for. spotDL reads
    /// those songs straight from the file and never asks Spotify about them. Links without details are returned so
    /// spotDL looks them up itself as before. A song without a saved cover still goes in the file: spotDL then uses
    /// the YouTube Music cover (checked with spotDL 4.6).
    /// </summary>
    internal async Task<(string? File, IReadOnlyList<string> Others)> WriteSongFileAsync(IReadOnlyList<string> urls,
        IReadOnlyList<TrackInfo>? songData, CancellationToken cancellationToken)
    {
        if (songData is null || songData.Count == 0) return (null, urls);
        var byId = songData.Where(t => t.SpotifyId.Length > 0 && t.Title.Length > 0 && t.Artists.Count > 0 && !t.IsLocal)
            .GroupBy(t => t.SpotifyId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var songs = new List<TrackInfo>();
        var rest = new List<string>();
        foreach (var url in urls)
        {
            var match = SpotifyTrackLink.Match(url);
            if (match.Success && byId.TryGetValue(match.Groups[1].Value, out var track) && songs.All(s => s.SpotifyId != track.SpotifyId)) songs.Add(track);
            else rest.Add(url);
        }
        if (songs.Count == 0) return (null, urls);
        await FillMissingCoversAsync(songs, cancellationToken);
        // songs YouTube Music didn't have, for which the user pasted a YouTube link: spotDL downloads that link
        var links = NotFoundSongs.Load(settings).Songs.Values.Where(s => s.ManualUrl.Length > 0)
            .ToDictionary(s => s.SpotifyId, s => s.ManualUrl, StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(SongFileFolder);
        foreach (var old in Directory.GetFiles(SongFileFolder, "*.spotdl"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) try { File.Delete(old); } catch { }
        var file = Path.Combine(SongFileFolder, "queue-" + Guid.NewGuid().ToString("N") + ".spotdl");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(songs.Select(s => SpotDlSong(s, links.GetValueOrDefault(s.SpotifyId)))), cancellationToken);
        var linked = songs.Count(s => links.ContainsKey(s.SpotifyId));
        if (linked > 0) log($"{linked} song(s) download from the YouTube link you added for them.");
        log($"{songs.Count} song(s) go to spotDL with SpotySync's own song details, so it doesn't look them up on Spotify" +
            (rest.Count > 0 ? $"; spotDL looks up the other {rest.Count} link(s) itself." : "."));
        return (file, rest);
    }

    /// <summary>One song in spotDL 4's save-file format (every field its Song type needs, so it never re-reads Spotify).</summary>
    public static Dictionary<string, object?> SpotDlSong(TrackInfo track) => SpotDlSong(track, null);

    /// <summary>As above; with <paramref name="downloadUrl"/> spotDL skips its search and downloads that link.</summary>
    public static Dictionary<string, object?> SpotDlSong(TrackInfo track, string? downloadUrl)
    {
        var date = track.ReleaseDate.Trim();
        var year = date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : 0;
        var trackNumber = track.TrackNumber > 0 ? track.TrackNumber : 1;
        var discNumber = track.DiscNumber > 0 ? track.DiscNumber : 1;
        return new Dictionary<string, object?>
        {
            ["name"] = track.Title,
            ["artists"] = track.Artists,
            ["artist"] = track.Artists[0],
            ["genres"] = Array.Empty<string>(),
            ["disc_number"] = discNumber,
            ["disc_count"] = discNumber,
            ["album_name"] = track.Album.Length > 0 ? track.Album : track.Title,
            ["album_artist"] = track.AlbumArtist.Length > 0 ? track.AlbumArtist : track.Artists[0],
            ["duration"] = track.DurationMs / 1000.0,
            ["year"] = year,
            ["date"] = date,
            ["track_number"] = trackNumber,
            ["tracks_count"] = Math.Max(trackNumber, track.AlbumTrackCount),
            ["song_id"] = track.SpotifyId,
            ["explicit"] = track.Explicit,
            ["publisher"] = "",
            ["url"] = "https://open.spotify.com/track/" + track.SpotifyId,
            ["isrc"] = track.Isrc.Length > 0 ? track.Isrc : null,
            ["cover_url"] = track.CoverUrl.Length > 0 ? track.CoverUrl : null,
            ["copyright_text"] = null,
            ["download_url"] = string.IsNullOrWhiteSpace(downloadUrl) ? null : downloadUrl.Trim(),
            ["lyrics"] = null,
            ["popularity"] = null,
            // spotDL 4.6 re-reads the song from Spotify whenever album_id is empty (and drops it if that fails); only
            // --fetch-albums uses the id, so any non-empty value keeps it from asking Spotify.
            ["album_id"] = track.SpotifyId,
            ["list_name"] = null,
            ["list_url"] = null,
            ["list_position"] = null,
            ["list_length"] = null,
            ["artist_id"] = null,
            ["album_type"] = null
        };
    }

    /// <summary>
    /// Songs read from Spotify before 1.5.6 have no cover picture saved. Spotify's public embed page gives it without
    /// using the Spotify app's request allowance; results are remembered in Data\cover-cache.json.
    /// </summary>
    private async Task FillMissingCoversAsync(List<TrackInfo> songs, CancellationToken cancellationToken)
    {
        var missing = songs.Where(t => t.CoverUrl.Length == 0).ToList();
        if (missing.Count == 0) return;
        var cache = JsonStore.Load(CoverCachePath, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        cache = new Dictionary<string, string>(cache, StringComparer.OrdinalIgnoreCase);
        foreach (var track in missing)
            if (cache.TryGetValue(track.SpotifyId, out var known)) track.CoverUrl = known;
        missing = missing.Where(t => t.CoverUrl.Length == 0).ToList();
        if (missing.Count == 0 || coverLookupUnavailable) return;

        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = 0;
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (track, token) =>
        {
            if (coverLookupUnavailable) return;
            try
            {
                var link = Uri.EscapeDataString("https://open.spotify.com/track/" + track.SpotifyId);
                using var response = await CoverHttp.GetAsync("https://open.spotify.com/oembed?url=" + link, token);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException(((int)response.StatusCode).ToString());
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var thumbnail = doc.RootElement.TryGetProperty("thumbnail_url", out var value) ? value.GetString() ?? "" : "";
                // the embed gives the 300 px picture; the same image id with this prefix is the 640 px one
                if (thumbnail.Length > 0) found[track.SpotifyId] = thumbnail.Replace("ab67616d00001e02", "ab67616d0000b273");
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref failures) >= 8 && found.IsEmpty) coverLookupUnavailable = true;
            }
        });
        foreach (var track in missing)
            if (found.TryGetValue(track.SpotifyId, out var url)) { track.CoverUrl = url; cache[track.SpotifyId] = url; }
        if (!found.IsEmpty) try { JsonStore.SaveAtomic(CoverCachePath, cache); } catch { }
        if (coverLookupUnavailable)
            log("Couldn't reach Spotify's public page for album covers; songs without a saved cover get the YouTube Music cover instead.");
    }

    private async Task<DownloadRunResult> RecoverSkippedTracksAsync(DownloadRunResult primary,
        IReadOnlyList<TrackInfo> expectedTracks, CancellationToken cancellationToken)
    {
        if (!settings.AutoYouTubeFallback || primary.SkippedTracks.Count == 0) return primary;
        var tracks = ResolveSkippedTracks(primary.SkippedTracks, expectedTracks);
        if (tracks.Count == 0) return primary;

        log($"spot2yt left {tracks.Count} track(s) without a YouTube Music source; trying automatic public-source recovery.");
        var unresolved = new List<string>(primary.SkippedTracks);
        var fallbackErrors = 0;
        foreach (var track in tracks)
        {
            try
            {
                var fallback = await RunYouTubeFallbackAsync(track, cancellationToken);
                primary.ChangedAudio.AddRange(fallback.ChangedAudio);
                if (fallback.ExitCode == 0 && fallback.ReportedErrors == 0)
                {
                    primary.RecoveredFallbackTracks.Add(track.DisplayName);
                    unresolved.RemoveAll(value => SkippedNameMatches(value, track));
                }
                else fallbackErrors += Math.Max(1, fallback.ReportedErrors);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                fallbackErrors++;
                log($"ERROR: public-source recovery failed for {track.DisplayName}; continuing: {ex.Message}");
            }
        }
        primary.ChangedAudio = primary.ChangedAudio.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        primary.SkippedTracks = unresolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        primary.ReportedErrors += fallbackErrors;
        if (primary.SkippedTracks.Count == 0 && fallbackErrors == 0 && primary.ReportedErrors == 0)
            primary.ExitCode = 0;
        return primary;
    }

    private async Task<DownloadRunResult> RunYouTubeFallbackAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        var toolDirectory = Path.Combine(settings.BaseDirectory, "Tools");
        var executable = Path.Combine(toolDirectory, Platform.Exe("yt-dlp"));
        if (!File.Exists(executable))
            return new DownloadRunResult { ExitCode = 2, ReportedErrors = 1, DownloaderName = Platform.Exe("yt-dlp"), SkippedTracks = new() { track.DisplayName } };
        await UpdateYtDlpIfNeededAsync(toolDirectory, cancellationToken);

        var candidate = await FindYouTubeCandidateAsync(executable, track, cancellationToken);
        if (candidate is null)
        {
            log($"WARNING: no matching public audio source was found for {track.DisplayName}; it remains skipped.");
            return new DownloadRunResult { ExitCode = 2, ReportedErrors = 1, DownloaderName = Platform.Exe("yt-dlp"), SkippedTracks = new() { track.DisplayName } };
        }

        log($"Public-source recovery selected: {candidate.Title} ({candidate.Uploader}).");
        var fileName = SanitizeFileName($"{track.Artists.FirstOrDefault()} - {track.Title}".Trim(' ', '-'));
        var stagingRoot = Path.Combine(settings.DataDirectory, "DownloadStaging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            // Keep every intermediate stream, thumbnail and conversion outside the
            // watched MusicBee library. Otherwise MusicBee can auto-organize an
            // unfinished file out from under ffmpeg and leave truncated/unknown
            // duplicates behind.
            var outputTemplate = Path.Combine(stagingRoot, fileName + ".%(ext)s");
            var browser = ConfiguredCookieBrowser(toolDirectory);
            var result = await RunYtDlpOnceAsync(executable, new[] { candidate.Url }, browser, outputTemplate, true, candidate.DownloadSection, cancellationToken);
            if (result.ChangedAudio.Count == 0 && browser is not null)
            {
                log($"Public-source recovery with {BrowserName(browser)} cookies produced no audio; retrying once without them.");
                var retry = await RunYtDlpOnceAsync(executable, new[] { candidate.Url }, null, outputTemplate, true, candidate.DownloadSection, cancellationToken);
                retry.ChangedAudio = result.ChangedAudio.Concat(retry.ChangedAudio).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                result = retry;
            }
            if (result.ExitCode == 0 && result.ReportedErrors == 0)
            {
                var ready = new List<string>();
                foreach (var file in result.ChangedAudio.Where(File.Exists))
                    if (await ApplySpotifyMetadataAsync(file, track, cancellationToken)) ready.Add(file);
                if (ready.Count == 0)
                {
                    result.ReportedErrors++;
                    log($"ERROR: recovered audio for {track.DisplayName} could not be tagged, so it was not published to MusicBee.");
                }
                else
                {
                    result.ChangedAudio = PublishStagedAudio(ready, fileName);
                    result.OutputFolder = settings.ResolvePath(settings.MusicFolder);
                    log($"Recovered {track.DisplayName} and published one finished, tagged audio file.");
                }
            }
            else result.ChangedAudio.Clear();
            return result;
        }
        finally { TryDeleteDirectory(stagingRoot); }
    }

    private List<string> PublishStagedAudio(IReadOnlyList<string> stagedFiles, string fileName)
    {
        var musicFolder = settings.ResolvePath(settings.MusicFolder);
        Directory.CreateDirectory(musicFolder);
        // A successful yt-dlp extraction should leave one audio file (in the source's
        // own format). Publish only one recording if a provider leaves extras.
        var staged = stagedFiles.OrderByDescending(path => new FileInfo(path).Length).First();
        var extension = Path.GetExtension(staged);
        var destination = Path.Combine(musicFolder, fileName + extension);
        var transfer = Path.Combine(musicFolder, ".spotysync-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(staged, transfer, true);
            File.Move(transfer, destination, true);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(staged));
            return new List<string> { destination };
        }
        finally
        {
            try { if (File.Exists(transfer)) File.Delete(transfer); } catch { }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private async Task<YouTubeCandidate?> FindYouTubeCandidateAsync(string executable, TrackInfo track, CancellationToken cancellationToken)
    {
        var query = string.Join(" ", track.Artists.Take(2).Append(track.Title).Append("official audio"));
        var entries = await SearchMediaAsync(executable, "ytsearch", query, 12, cancellationToken);
        var direct = entries.Select(item => item with
        {
            Score = ScoreYouTubeCandidate(track, item.Title, item.Uploader, item.DurationSeconds)
        }).Where(item => item.Url.Length > 0 && item.Score >= 100).OrderByDescending(item => item.Score).FirstOrDefault();
        if (direct is not null) return direct;

        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            var albumQuery = string.Join(" ", track.Artists.Take(2).Append(track.Album).Append("full album"));
            var albumEntries = await SearchMediaAsync(executable, "ytsearch", albumQuery, 15, cancellationToken);
            var wantedAlbum = TextNormalizer.Normalize(track.Album, true);
            var wantedArtists = track.Artists.Select(artist => TextNormalizer.Normalize(artist)).Where(value => value.Length > 0).ToList();
            var possibleAlbums = albumEntries.Where(item =>
            {
                var source = TextNormalizer.Normalize(item.Title + " " + item.Uploader, true);
                return source.Contains(wantedAlbum, StringComparison.Ordinal) && wantedArtists.Any(artist => source.Contains(artist, StringComparison.Ordinal));
            }).Take(3).ToList();
            foreach (var album in possibleAlbums)
            {
                var chapter = await FindMatchingChapterAsync(executable, album, track, cancellationToken);
                if (chapter is not null) return chapter;
            }
        }

        // SoundCloud often has legitimate independent releases that never reached
        // YouTube Music (the Major Axis catalog is one real example). yt-dlp's
        // search extractor gives us normal metadata/duration scoring before any
        // audio is downloaded.
        var soundCloudQuery = string.Join(" ", track.Artists.Take(2).Append(track.Title));
        var soundCloud = await SearchMediaAsync(executable, "scsearch", soundCloudQuery, 15, cancellationToken);
        return soundCloud.Select(item => item with
        {
            Score = ScoreYouTubeCandidate(track, item.Title, item.Uploader, item.DurationSeconds)
        }).Where(item => item.Url.Length > 0 && item.Score >= 100).OrderByDescending(item => item.Score).FirstOrDefault();
    }

    private async Task<List<YouTubeCandidate>> SearchMediaAsync(string executable, string searchPrefix, string query, int count, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("--flat-playlist");
        start.ArgumentList.Add("--dump-single-json");
        start.ArgumentList.Add("--no-warnings");
        start.ArgumentList.Add(searchPrefix + count + ":" + query);
        using var process = DownloadProcesses.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { StopProcess(process); throw; }
        var errorText = await stderr;
        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(errorText)) log($"WARNING: {searchPrefix} public-source search failed; continuing: " + errorText.Replace('\r', ' ').Replace('\n', ' ').Trim());
            return new List<YouTubeCandidate>();
        }
        try
        {
            using var document = JsonDocument.Parse(await stdout);
            if (!document.RootElement.TryGetProperty("entries", out var entries)) return new List<YouTubeCandidate>();
            return entries.EnumerateArray().Select(item =>
            {
                var id = JsonString(item, "id");
                var url = JsonString(item, "webpage_url");
                if (url.Length == 0) url = JsonString(item, "url");
                if (url.Length == 0 && id.Length > 0 && searchPrefix.Equals("ytsearch", StringComparison.OrdinalIgnoreCase))
                    url = "https://www.youtube.com/watch?v=" + id;
                var title = JsonString(item, "title");
                var uploader = JsonString(item, "uploader");
                if (uploader.Length == 0) uploader = JsonString(item, "channel");
                var duration = item.TryGetProperty("duration", out var durationValue) && durationValue.ValueKind == JsonValueKind.Number && durationValue.TryGetDouble(out var seconds) ? (int)Math.Round(seconds) : 0;
                return new YouTubeCandidate(url, title, uploader, duration, 0, null);
            }).Where(item => item.Url.Length > 0).ToList();
        }
        catch (JsonException ex)
        {
            log("WARNING: public-source search returned unreadable metadata; continuing: " + ex.Message);
            return new List<YouTubeCandidate>();
        }
    }

    private async Task<YouTubeCandidate?> FindMatchingChapterAsync(string executable, YouTubeCandidate album,
        TrackInfo track, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "--dump-single-json", "--skip-download", "--no-warnings", album.Url }) start.ArgumentList.Add(argument);
        using var process = DownloadProcesses.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { StopProcess(process); throw; }
        _ = await stderr;
        if (process.ExitCode != 0) return null;
        try
        {
            using var document = JsonDocument.Parse(await stdout);
            if (!document.RootElement.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array) return null;
            var wantedTitle = TextNormalizer.Normalize(track.Title, true);
            foreach (var chapter in chapters.EnumerateArray())
            {
                var chapterTitle = JsonString(chapter, "title");
                var normalized = TextNormalizer.Normalize(chapterTitle, true);
                if (!(normalized == wantedTitle || normalized.Contains(wantedTitle, StringComparison.Ordinal) || wantedTitle.Contains(normalized, StringComparison.Ordinal))) continue;
                if (!chapter.TryGetProperty("start_time", out var startValue) || startValue.ValueKind != JsonValueKind.Number || !startValue.TryGetDouble(out var startSeconds) ||
                    !chapter.TryGetProperty("end_time", out var endValue) || endValue.ValueKind != JsonValueKind.Number || !endValue.TryGetDouble(out var endSeconds) || endSeconds <= startSeconds) continue;
                var duration = (int)Math.Round(endSeconds - startSeconds);
                if (track.DurationMs > 0 && Math.Abs(track.DurationMs / 1000 - duration) > 15) continue;
                var section = "*" + FormatSectionTime(startSeconds) + "-" + FormatSectionTime(endSeconds);
                return album with { DurationSeconds = duration, Score = 190, DownloadSection = section };
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string FormatSectionTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.fff") : time.ToString(@"m\:ss\.fff");
    }

    public static int ScoreYouTubeCandidate(TrackInfo track, string candidateTitle, string uploader, int durationSeconds)
    {
        var wantedTitle = TextNormalizer.Normalize(track.Title, true);
        var title = TextNormalizer.Normalize(candidateTitle, true);
        var source = title + " " + TextNormalizer.Normalize(uploader);
        if (wantedTitle.Length == 0 || !source.Contains(wantedTitle, StringComparison.Ordinal)) return 0;
        var artistMatch = track.Artists.Select(artist => TextNormalizer.Normalize(artist)).Where(value => value.Length > 0)
            .Any(artist => source.Contains(artist, StringComparison.Ordinal));
        if (!artistMatch) return 0;

        var score = title == wantedTitle ? 90 : 70;
        score += 45;
        if (track.DurationMs > 0 && durationSeconds > 0)
        {
            var difference = Math.Abs(track.DurationMs / 1000 - durationSeconds);
            if (difference <= 5) score += 35;
            else if (difference <= 12) score += 20;
            else if (difference <= 25) score += 5;
            else if (difference > 45) return 0;
            else score -= 25;
        }
        var wanted = TextNormalizer.Normalize(track.Title + " " + string.Join(" ", track.Artists));
        foreach (var risky in new[] { "cover", "karaoke", "nightcore", "slowed", "sped up", "remix", "live" })
            if (source.Contains(risky, StringComparison.Ordinal) && !wanted.Contains(risky, StringComparison.Ordinal)) score -= 55;
        if (source.Contains("official audio", StringComparison.Ordinal) || source.Contains(" topic ", StringComparison.Ordinal)) score += 10;
        return score;
    }

    private async Task<bool> ApplySpotifyMetadataAsync(string file, TrackInfo track, CancellationToken cancellationToken)
    {
        var ffmpeg = Path.Combine(settings.BaseDirectory, "Tools", Platform.Exe("ffmpeg"));
        if (!File.Exists(ffmpeg)) return false;
        var temporary = Path.Combine(Path.GetDirectoryName(file)!, ".spotysync-tags-" + Guid.NewGuid().ToString("N") + Path.GetExtension(file));
        var originalTimestamp = File.GetLastWriteTimeUtc(file);
        try
        {
            // Audio is always stream-copied (never re-encoded). The first pass keeps
            // embedded cover art; Ogg/Opus cannot hold ffmpeg's picture stream, so a
            // failure retries with the audio stream only.
            var error = await RunFfmpegTagAsync(ffmpeg, file, temporary, track, "0", cancellationToken);
            if (error is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                error = await RunFfmpegTagAsync(ffmpeg, file, temporary, track, "0:a", cancellationToken);
            }
            if (error is not null) throw new InvalidOperationException(error);
            File.Move(temporary, file, true);
            File.SetLastWriteTimeUtc(file, originalTimestamp);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log("WARNING: downloaded fallback audio was kept, but Spotify title/artist tags could not be applied: " + ex.Message);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return false;
        }
    }

    private static async Task<string?> RunFfmpegTagAsync(string ffmpeg, string input, string output, TrackInfo track, string map, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var artist = string.Join("; ", track.Artists);
        foreach (var value in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-map", map, "-c", "copy",
                     // Container-level tags (MP3/M4A) and stream-level tags (Ogg/Opus keep theirs on the audio stream).
                     "-metadata", "title=" + track.Title, "-metadata", "artist=" + artist, "-metadata", "album=" + track.Album,
                     "-metadata:s:a:0", "title=" + track.Title, "-metadata:s:a:0", "artist=" + artist, "-metadata:s:a:0", "album=" + track.Album,
                     output }) start.ArgumentList.Add(value);
        using var process = DownloadProcesses.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdout;
        var error = (await stderr).Trim();
        return process.ExitCode == 0 ? null : (error.Length > 0 ? error : $"ffmpeg exited with code {process.ExitCode}.");
    }

    private static readonly System.Text.RegularExpressions.Regex ProgressLine =
        new(@"^\[download\]\s+(\d{1,3}(?:\.\d+)?)%", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<List<string>> PumpAsync(StreamReader reader, string prefix, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var current = ""; var lastStep = -1;
        // spotDL's console library sometimes can't print song names with non-Latin letters (Windows code page) and
        // dumps a Python "Logging error" block per song, then a final traceback when it closes its progress display.
        // Nothing actually fails, so those blocks are reduced to the one useful fact (which song was skipped).
        var inLoggingError = false; var pendingSkip = false; var afterExcepthook = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            if (!string.IsNullOrWhiteSpace(line))
            {
                line = line.TrimEnd();
                var bare = line.Trim();
                if (afterExcepthook) continue;
                if (bare == "--- Logging error ---") { inLoggingError = true; pendingSkip = false; continue; }
                if (inLoggingError)
                {
                    if (bare.StartsWith("Message: 'Skipping %s", StringComparison.Ordinal)) pendingSkip = true;
                    if (bare.StartsWith("Arguments:", StringComparison.Ordinal))
                    {
                        inLoggingError = false;
                        var name = System.Text.RegularExpressions.Regex.Match(bare, @"^Arguments: \('(.*?)', ");
                        if (pendingSkip && name.Success)
                        {
                            string song;
                            try { song = System.Text.RegularExpressions.Regex.Unescape(name.Groups[1].Value); } catch { song = name.Groups[1].Value; }
                            var skip = $"Skipping {song} (file already exists)";
                            lines.Add(skip);
                            log(prefix + " | " + skip);
                        }
                    }
                    continue;
                }
                if (bare == "Error in sys.excepthook:")
                {
                    // spotDL's progress display crashed on a song name while closing: nothing failed, so nothing to show
                    afterExcepthook = true;
                    lines.Add(CosmeticExitMarker);
                    continue;
                }
                if (bare.Contains("YouTube Music returned no usable results", StringComparison.OrdinalIgnoreCase))
                {
                    // one empty search; spotDL still tries other searches, and its final verdict is the LookupError line
                    lines.Add(line);
                    continue;
                }
                if (NotFoundSongs.NameFromDownloaderLine(bare) is { } missing)
                {
                    lines.Add(line);
                    if (notFoundThisRun.TryAdd(missing, DateTime.UtcNow)) log(prefix + " | Not on YouTube Music: " + missing);
                    continue;
                }
                if (bare.StartsWith("AudioProviderError:", StringComparison.Ordinal))
                {
                    var video = VideoLink.Match(bare);
                    if (video.Success) lastFailedVideo = video.Value;
                    if (Interlocked.Increment(ref errorStreak) == 1) streakStartedUtc = DateTime.UtcNow;
                }
                else if (bare.StartsWith("Downloaded \"", StringComparison.Ordinal)) Interlocked.Exchange(ref errorStreak, 0);
                var progress = ProgressLine.Match(line.TrimStart());
                if (progress.Success)
                {
                    // live progress is reported in 10% steps and kept out of the stored output
                    var step = (int)(double.Parse(progress.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / 10) * 10;
                    if (current.Length > 0 && step != lastStep) { lastStep = step; log($"progress | {current} | {step}"); }
                    continue;
                }
                var destination = line.IndexOf("[download] Destination:", StringComparison.OrdinalIgnoreCase);
                if (destination >= 0)
                {
                    current = Path.GetFileNameWithoutExtension(line[(destination + "[download] Destination:".Length)..].Trim().Trim('"'));
                    lastStep = -1;
                }
                lines.Add(line);
                if (ShouldDisplayDownloaderLine(line)) log(prefix + " | " + CondenseDownloaderLine(line));
            }
        return lines;
    }

    private async Task<List<string>> PumpSpot2YtAsync(StreamReader reader, StreamWriter input, CancellationToken cancellationToken)
    {
        const string sourcePrompt = "Paste a YouTube link (or press Enter to leave skipped):";
        const string mainPrompt = "Paste Spotify link (or type exit):";
        const string continuePrompt = "Press Enter to continue...";
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        var buffer = new char[256];
        var downloadStageComplete = false;
        var answeredSourcePrompt = false;
        var answeredContinuePrompt = false;
        var exitSent = false;
        var mainPromptsSeen = 0;
        var countedCurrentMainPrompt = false;

        async Task RespondAsync(string response)
        {
            if (!input.BaseStream.CanWrite) return;
            await input.WriteLineAsync(response.AsMemory(), cancellationToken);
            await input.FlushAsync(cancellationToken);
        }

        void FinishLine()
        {
            var raw = current.ToString().TrimEnd('\r');
            current.Clear();
            answeredSourcePrompt = false;
            answeredContinuePrompt = false;
            countedCurrentMainPrompt = false;
            if (string.IsNullOrWhiteSpace(raw)) return;
            lines.Add(raw);
            var display = SanitizeSpot2YtLine(raw);
            if (raw.Contains("All done!", StringComparison.OrdinalIgnoreCase))
            {
                downloadStageComplete = true;
                var detail = raw[(raw.IndexOf("All done!", StringComparison.OrdinalIgnoreCase) + "All done!".Length)..].Trim();
                log("download | Audio download stage complete" + (detail.Length > 0 ? ": " + detail : ".") +
                    " MusicBee verification, lyrics, and initialization are next.");
            }
            else if (display.Length > 0 && ShouldDisplayDownloaderLine(display))
                log("download | " + CondenseDownloaderLine(display));
        }

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    FinishLine();
                    continue;
                }
                current.Append(character);
                var pending = current.ToString();
                if (!answeredSourcePrompt && pending.Contains(sourcePrompt, StringComparison.OrdinalIgnoreCase))
                {
                    answeredSourcePrompt = true;
                    await RespondAsync("");
                }
                else if (!countedCurrentMainPrompt && pending.Contains(mainPrompt, StringComparison.OrdinalIgnoreCase))
                {
                    countedCurrentMainPrompt = true;
                    mainPromptsSeen++;
                    if (!exitSent && (downloadStageComplete || mainPromptsSeen > 1))
                    {
                        exitSent = true;
                        await RespondAsync("exit");
                    }
                }
                else if (!answeredContinuePrompt && pending.Contains(continuePrompt, StringComparison.OrdinalIgnoreCase))
                {
                    answeredContinuePrompt = true;
                    await RespondAsync("");
                    try { input.Close(); } catch { }
                }
            }
        }
        if (current.Length > 0) FinishLine();
        return lines;
    }

    private static string SanitizeSpot2YtLine(string line)
    {
        foreach (var prompt in new[] { "Paste Spotify link (or type exit):", "Press Enter to continue..." })
            line = line.Replace(prompt, "", StringComparison.OrdinalIgnoreCase);
        var sourcePrompt = line.IndexOf(" was skipped. Paste a YouTube link (or press Enter to leave skipped):", StringComparison.OrdinalIgnoreCase);
        if (sourcePrompt >= 0)
        {
            var prefix = line[..sourcePrompt].Trim();
            line = prefix.Length > 0 ? prefix + " was skipped; automatic recovery will be tried." : "";
        }
        return line.Trim();
    }

    private static bool ShouldDisplayDownloaderLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Length > trimmed.Length && (trimmed.StartsWith("It is strongly", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Run \"yt-dlp", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("To suppress", StringComparison.OrdinalIgnoreCase))) return false;
        if (trimmed.StartsWith("Deleting original file", StringComparison.OrdinalIgnoreCase)) return false;
        if (!trimmed.StartsWith("[", StringComparison.Ordinal)) return true; // spot2yt's readable status protocol
        return trimmed.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("[download] Download completed", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("[ExtractAudio] Destination:", StringComparison.OrdinalIgnoreCase);
    }

    private static string CondenseDownloaderLine(string line)
    {
        var marker = line.IndexOf("Destination:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return line;
        var prefix = line[..(marker + "Destination:".Length)];
        var path = line[(marker + "Destination:".Length)..].Trim().Trim('"');
        return prefix + " " + Path.GetFileName(path);
    }

    private async Task UpdateYtDlpIfNeededAsync(string toolDirectory, CancellationToken cancellationToken)
    {
        if (!settings.AutoUpdateYtDlp) return;
        var executable = Path.Combine(toolDirectory, Platform.Exe("yt-dlp"));
        if (!File.Exists(executable)) return;
        var marker = Path.Combine(toolDirectory, ".ytdlp_last_update_check");
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(settings.YtDlpUpdateDays)) return;
            log("Checking yt-dlp for updates...");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = toolDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add("-U");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start yt-dlp updater.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var message = string.Join(" ", new[] { await stdout, await stderr }.Where(x => !string.IsNullOrWhiteSpace(x)))
                .Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (process.ExitCode == 0)
            {
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
                log("yt-dlp update check complete" + (message.Length > 0 ? ": " + message : "."));
            }
            else log($"WARNING: yt-dlp update check failed with exit code {process.ExitCode}: {message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log("WARNING: yt-dlp auto-update failed; continuing with the installed copy: " + ex.Message);
        }
    }

    private static bool IsSpot2Yt(string executable) =>
        Path.GetFileName(executable).StartsWith("spot2yt", StringComparison.OrdinalIgnoreCase);

    private static bool IsSpotifyLink(string value) =>
        value.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase);

    // ---- Premium audio diagnostics -------------------------------------------------
    // YouTube only offers the ~256 kbps streams (formats 774 Opus / 141 AAC) to a signed-in
    // Premium session with a valid PO token. When that silently fails, spotDL quietly takes
    // the ~128 kbps stream, so check up front and verify the files afterwards.
    private const string PremiumProbeUrl = "https://music.youtube.com/watch?v=Rgrt_8mXrK8";
    private DateTime premiumCheckedUtc = DateTime.MinValue;

    private bool? lastPremiumResult;

    /// <summary>True/false once the check ran with a configured browser session; null when it could not tell.</summary>
    public async Task<bool?> CheckPremiumAvailableAsync(CancellationToken cancellationToken = default)
    {
        var executable = settings.ResolvePath(settings.DownloaderPath);
        var toolDirectory = Path.GetDirectoryName(executable) ?? Path.Combine(settings.BaseDirectory, "Tools");
        var browser = ConfiguredCookieBrowser(toolDirectory);
        if (browser is null)
        {
            log("WARNING: no signed-in browser is set in Tools\\browser_options.txt, so Premium audio is unavailable.");
            return false;
        }
        return await CheckPremiumAudioAsync(toolDirectory, browser, cancellationToken, force: true);
    }

    private async Task<bool?> CheckPremiumAudioAsync(string toolDirectory, string browser, CancellationToken cancellationToken, bool force = false)
    {
        if (!force && DateTime.UtcNow - premiumCheckedUtc < TimeSpan.FromMinutes(30)) return lastPremiumResult;
        premiumCheckedUtc = DateTime.UtcNow;
        lastPremiumResult = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await WarmUpPoTokenHelperAsync(toolDirectory, cancellationToken);
            var probe = await ProbeFormatsAsync(toolDirectory, browser, PremiumProbeUrl, cancellationToken);
            if (probe.Premium == true)
            {
                log($"Premium audio check passed: YouTube is offering the ~256 kbps streams to your {BrowserName(browser)} session.");
                return lastPremiumResult = true;
            }
            foreach (var line in probe.Diagnostics.Take(8)) log("Premium check | " + line);
            if (attempt == 1) log("Premium audio check didn't get the ~256 kbps streams; warming up the token helper and trying once more…");
        }
        // Still failing: capture yt-dlp's own debug view of the token helper for the log.
        var detail = await ProbeFormatsAsync(toolDirectory, browser, PremiumProbeUrl, cancellationToken, verbose: true);
        if (detail.Premium == true)
        {
            log($"Premium audio check passed on the detailed retry: YouTube is offering the ~256 kbps streams to your {BrowserName(browser)} session.");
            return lastPremiumResult = true;
        }
        foreach (var line in detail.Diagnostics.Take(40)) log("Premium debug | " + (line.Length > 400 ? line[..400] + "…" : line));
        log($"WARNING: Premium audio check FAILED: YouTube is not offering the ~256 kbps streams to your {BrowserName(browser)} session right now, " +
            "so songs will download at ~128 kbps. Make sure you are signed in to YouTube in that browser, then cancel and try again.");
        return lastPremiumResult = false;
    }

    private sealed record FormatProbe(bool? Premium, bool TokenGenerated, List<string> Diagnostics);

    /// <summary>
    /// Lists a YouTube video's formats with the signed-in browser session. Premium=true when the ~256 kbps
    /// streams (774 Opus / 141 AAC) are offered; false when they are missing even though a PO token was
    /// generated (the video genuinely has no 256k version); null when the probe could not tell.
    /// </summary>
    /// <summary>
    /// Tell yt-dlp exactly where Deno and the bgutil token helper are, instead of relying on its own
    /// lookups. Forward slashes: spotDL splits --yt-dlp-args shell-style, which would eat backslashes.
    /// </summary>
    private static List<string> PoTokenHelperArgs(string toolDirectory)
    {
        var arguments = new List<string>();
        var deno = Path.Combine(toolDirectory, Platform.Exe("deno"));
        var serverHome = PoTokenServerHome(toolDirectory);
        if (File.Exists(deno)) { arguments.Add("--js-runtimes"); arguments.Add("deno:" + deno.Replace('\\', '/')); }
        if (Directory.Exists(serverHome)) { arguments.Add("--extractor-args"); arguments.Add("youtubepot-bgutilscript:server_home=" + serverHome.Replace('\\', '/')); }
        return arguments;
    }

    private async Task<FormatProbe> ProbeFormatsAsync(string toolDirectory, string? browser, string url, CancellationToken cancellationToken, bool verbose = false)
    {
        var ytDlp = Path.Combine(toolDirectory, Platform.Exe("yt-dlp"));
        if (!File.Exists(ytDlp)) return new FormatProbe(null, false, new List<string> { "yt-dlp.exe is missing from Tools." });
        var start = new ProcessStartInfo(ytDlp)
        {
            WorkingDirectory = toolDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.Environment["PATH"] = toolDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        if (verbose) start.ArgumentList.Add("-v");
        var cookies = browser is null ? Array.Empty<string>() : new[] { "--cookies-from-browser", browser };
        foreach (var argument in new[] { "-F" }.Concat(cookies).Concat(PoTokenHelperArgs(toolDirectory)).Append(url)) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start yt-dlp.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                StopProcess(process);
                if (cancellationToken.IsCancellationRequested) throw;
                return new FormatProbe(null, false, new List<string> { "yt-dlp format check timed out." });
            }
            var lines = ((await stdout) + "\n" + (await stderr)).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
            var premium = lines.Any(line => line.StartsWith("774 ") || line.StartsWith("141 "));
            var token = lines.Any(line => line.Contains("Generating a gvs PO Token", StringComparison.OrdinalIgnoreCase)) &&
                        !lines.Any(line => line.Contains("PO Token which was not provided", StringComparison.OrdinalIgnoreCase));
            var listed = lines.Any(line => line.StartsWith("251 ") || line.StartsWith("140 "));
            var diagnostics = lines.Where(line => !line.Contains("4416") &&
                (line.Contains("[pot") || line.StartsWith("WARNING") || line.StartsWith("ERROR") ||
                 (verbose && (line.Contains("[jsc") || line.Contains("Plugin") || line.Contains("PO Token") ||
                              line.Contains("deno", StringComparison.OrdinalIgnoreCase) || line.Contains("exe versions") ||
                              line.Contains("Python") || line.Contains("Command-line config"))))).ToList();
            if (!token && !premium) diagnostics.Add("No PO token was generated, so YouTube hid the ~256 kbps streams.");
            return new FormatProbe(premium ? true : listed && token ? false : null, token, diagnostics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FormatProbe(null, false, new List<string> { "yt-dlp format check failed: " + ex.Message });
        }
    }

    /// <summary>
    /// The bgutil PO-token helper gives Deno only 5 s to start and 15 s to load its script. A cold start
    /// (first run in a while, Defender scanning, low memory) misses those limits and yt-dlp then turns
    /// the helper off for the whole run, so every song silently drops to ~128 kbps. Running the same
    /// commands first with generous time limits leaves them warm for the real run.
    /// </summary>
    private async Task WarmUpPoTokenHelperAsync(string toolDirectory, CancellationToken cancellationToken)
    {
        var deno = Path.Combine(toolDirectory, Platform.Exe("deno"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await EnsureBundledPoTokenServerAsync(toolDirectory, cancellationToken);
        var serverHome = PoTokenServerHome(toolDirectory);
        var script = Path.Combine(serverHome, "src", "generate_once.ts");
        if (!File.Exists(deno) || !File.Exists(script) || !Directory.Exists(Path.Combine(serverHome, "node_modules")))
        {
            log($"WARNING: the Premium token helper is not installed ({(File.Exists(deno) ? serverHome : deno)} is missing or not set up), so downloads will be ~128 kbps.");
            return;
        }
        var portablePlugin = Path.Combine(toolDirectory, "yt-dlp-plugins", "bgutil-ytdlp-pot-provider.zip");
        if (!File.Exists(portablePlugin))
            log($"WARNING: the Premium token plugin is missing from {Path.GetDirectoryName(portablePlugin)}; " +
                "unless it is installed in %APPDATA%\\yt-dlp\\plugins, downloads will be ~128 kbps.");
        var nodeModules = Path.Combine(serverHome, "node_modules");
        var xdgCache =Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheDir = !string.IsNullOrEmpty(xdgCache)
            ? Path.Combine(xdgCache, "bgutil-ytdlp-pot-provider")
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? profile, ".cache", "bgutil-ytdlp-pot-provider");
        var watch = Stopwatch.StartNew();
        var versionOk = await RunHelperAsync(deno, toolDirectory, new[] { "--version" }, TimeSpan.FromMinutes(2), cancellationToken);
        var scriptOk = versionOk && await RunHelperAsync(deno, toolDirectory, new[]
        {
            "run", "--allow-env", "--allow-net", $"--allow-ffi={nodeModules}", $"--allow-write={cacheDir}",
            $"--allow-read={cacheDir},{nodeModules}", script, "--version"
        }, TimeSpan.FromMinutes(3), cancellationToken);
        var seconds = watch.Elapsed.TotalSeconds;
        if (!scriptOk) log($"WARNING: the Premium token helper failed to start (took {seconds:0.0}s); downloads may fall back to ~128 kbps.");
        else if (seconds > 4) log($"Warmed up the Premium token helper in {seconds:0.0}s (a cold start this slow used to make downloads fall back to ~128 kbps).");
    }

    private static async Task<bool> RunHelperAsync(string executable, string workingDirectory, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                StopProcess(process);
                if (cancellationToken.IsCancellationRequested) throw;
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private async Task ReportAudioQualityAsync(string toolDirectory, string browser, IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var ffprobe = Path.Combine(toolDirectory, Platform.Exe("ffprobe"));
        if (!File.Exists(ffprobe)) return;
        var premium = 0;
        var standard = new List<(string File, double Kbps)>();
        foreach (var file in files.Where(File.Exists).Where(path => AudioExtensions.Contains(Path.GetExtension(path))).Take(500))
        {
            var kbps = await AudioKbpsAsync(ffprobe, file, cancellationToken);
            if (kbps <= 0) continue;
            if (kbps >= 200) premium++; else standard.Add((file, kbps));
        }
        var total = premium + standard.Count;
        if (total == 0) return;
        if (standard.Count == 0) { log($"Quality check: all {total} new song(s) are Premium quality (~256 kbps)."); return; }

        // For each standard-quality song, ask YouTube whether a 256k version exists: that tells
        // "this song isn't offered in 256k" apart from "it was offered but the download missed it".
        // At most a few songs, side by side and time-limited: this runs after every batch, and checking 15 songs one by
        // one held up the next batch for minutes.
        const int MaxProbes = 3;
        int noPremiumVersion = 0, missed = 0, unknown = 0;
        var missedNames = new List<string>();
        var probes = await Task.WhenAll(standard.Take(MaxProbes).Select(async item =>
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var source = await SourceUrlAsync(ffprobe, item.File, limit.Token);
                return (item.File, item.Kbps, Premium: source.Length == 0 ? null : (await ProbeFormatsAsync(toolDirectory, browser, source, limit.Token))?.Premium);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (item.File, item.Kbps, Premium: (bool?)null); }
        }));
        foreach (var (file, kbps, isPremium) in probes)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            switch (isPremium)
            {
                case true: missed++; missedNames.Add(name); log($"Quality check | {name}: {kbps:0} kbps, but YouTube DOES offer a 256k version (missed)."); break;
                case false: noPremiumVersion++; log($"Quality check | {name}: {kbps:0} kbps; YouTube has no 256k version of this song."); break;
                default: unknown++; log($"Quality check | {name}: {kbps:0} kbps; couldn't tell whether a 256k version exists."); break;
            }
        }
        var unchecked_ = Math.Max(0, standard.Count - MaxProbes);
        log($"{(missed > 0 ? "WARNING: " : "")}Quality check: {premium} of {total} new song(s) are Premium quality (~256 kbps). " +
            $"Of the {standard.Count} at ~128 kbps: {noPremiumVersion} have no 256k version on YouTube, {missed} missed an available 256k version" +
            (unknown > 0 ? $", {unknown} unknown" : "") + (unchecked_ > 0 ? $", {unchecked_} not checked" : "") + "." +
            (missed > 0 ? " Library › Upgrade quality will re-download them." : ""));
    }

    /// <summary>The YouTube URL spotDL stores in the comment tag.</summary>
    private static async Task<string> SourceUrlAsync(string ffprobe, string file, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffprobe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format_tags", "-of", "default=nw=1:nk=1", file }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return "";
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(value => value.StartsWith("https://music.youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                                         value.StartsWith("https://www.youtube.com/", StringComparison.OrdinalIgnoreCase)) ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }

    /// <summary>Audio-only bitrate (cover art excluded) from the sum of the audio packet sizes.</summary>
    public static async Task<double> AudioKbpsAsync(string ffprobe, string file, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffprobe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "packet=size:format=duration", "-of", "csv=p=0", file })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return 0;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            long bytes = 0;
            double duration = 0;
            foreach (var line in (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Contains('.') && double.TryParse(line.TrimEnd(','), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) duration = seconds;
                else if (long.TryParse(line.TrimEnd(','), out var size)) bytes += size;
            }
            return duration > 0 ? bytes * 8 / duration / 1000 : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return 0; }
    }

    private static string? ConfiguredCookieBrowser(string toolDirectory)
    {
        var path = Path.Combine(toolDirectory, "browser_options.txt");
        var supported = new[] { "FIREFOX", "CHROME", "EDGE", "BRAVE", "OPERA", "VIVALDI", "CHROMIUM" };
        var values = !File.Exists(path) ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var chosen = supported.FirstOrDefault(browser => bool.TryParse(values.GetValueOrDefault(browser), out var enabled) && enabled);
        if (chosen is not null) return CookieSource(chosen.ToLowerInvariant());
        // No browser picked (fresh install, or AUTO=true): use the Windows default browser.
        var auto = !File.Exists(path) || (bool.TryParse(values.GetValueOrDefault("AUTO"), out var autoOn) && autoOn);
        return auto ? CookieSource(DefaultBrowser()) : null;
    }

    /// <summary>On Linux a Flatpak Firefox (Bazzite's default) needs its sandboxed profile folder named for yt-dlp.</summary>
    private static string? CookieSource(string? browser) => browser == "firefox" ? Platform.FirefoxCookieSource() : browser;

    /// <summary>"firefox:/home/…/firefox" is shown as just "firefox".</summary>
    private static string BrowserName(string? browser) => (browser ?? "").Split(':')[0];

    /// <summary>The user's default web browser, as a yt-dlp --cookies-from-browser name.</summary>
    private static string? DefaultBrowser()
    {
        if (!OperatingSystem.IsWindows()) return Platform.LinuxDefaultBrowser();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = (key?.GetValue("ProgId") as string ?? "").ToLowerInvariant();
            if (progId.Contains("firefox")) return "firefox";
            if (progId.Contains("msedge")) return "edge";
            if (progId.Contains("brave")) return "brave";
            if (progId.Contains("opera")) return "opera";
            if (progId.Contains("vivaldi")) return "vivaldi";
            if (progId.Contains("chromium")) return "chromium";
            if (progId.Contains("chrome")) return "chrome";
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// Where the bgutil PO-token helper lives: the copy bundled in Tools\bgutil-server once it is set up,
    /// otherwise a manual install at %USERPROFILE%\bgutil-ytdlp-pot-provider\server.
    /// </summary>
    private static string PoTokenServerHome(string toolDirectory)
    {
        var bundled = Path.Combine(toolDirectory, "bgutil-server");
        if (File.Exists(Path.Combine(bundled, "node_modules", BundledServerReadyMarker))) return bundled;
        var manual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bgutil-ytdlp-pot-provider", "server");
        return Directory.Exists(Path.Combine(manual, "node_modules")) || !Directory.Exists(bundled) ? manual : bundled;
    }

    private const string BundledServerReadyMarker = ".spotysync-ready";
    private static readonly SemaphoreSlim bundledServerSetupGate = new(1, 1);

    /// <summary>
    /// The release ships the token helper's source only; its npm packages are installed once, on first
    /// use, with the bundled Deno (about a minute, needs internet). No setup is needed from the user.
    /// </summary>
    private async Task EnsureBundledPoTokenServerAsync(string toolDirectory, CancellationToken cancellationToken)
    {
        var bundled = Path.Combine(toolDirectory, "bgutil-server");
        var deno = Path.Combine(toolDirectory, Platform.Exe("deno"));
        var marker = Path.Combine(bundled, "node_modules", BundledServerReadyMarker);
        if (!File.Exists(deno) || !File.Exists(Path.Combine(bundled, "src", "generate_once.ts")) || File.Exists(marker)) return;
        await bundledServerSetupGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(marker)) return;
            log("Setting up the Premium audio token helper (one time only, about a minute)…");
            var ok = await RunHelperAsync(deno, bundled, new[] { "install", "--allow-scripts=npm:canvas", "--frozen" }, TimeSpan.FromMinutes(10), cancellationToken);
            if (ok && Directory.Exists(Path.Combine(bundled, "node_modules")))
            {
                await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("o"), cancellationToken);
                log("Premium audio token helper is set up.");
            }
            else log("WARNING: could not set up the Premium audio token helper (check your internet connection). " +
                     "Downloads will be ~128 kbps; SpotySync will try again next time.");
        }
        finally { bundledServerSetupGate.Release(); }
    }

    private static DownloadRunResult Merge(IReadOnlyList<DownloadRunResult> results)
    {
        if (results.Count == 0) return new DownloadRunResult();
        return new DownloadRunResult
        {
            ExitCode = results.Any(x => x.ExitCode != 0) ? results.First(x => x.ExitCode != 0).ExitCode : 0,
            ReportedErrors = results.Sum(x => x.ReportedErrors),
            DownloaderName = string.Join(" + ", results.Select(x => x.DownloaderName).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)),
            OutputFolder = results.Select(x => x.OutputFolder).LastOrDefault(x => x.Length > 0) ?? "",
            ChangedAudio = results.SelectMany(x => x.ChangedAudio).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SkippedTracks = results.SelectMany(x => x.SkippedTracks).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RecoveredFallbackTracks = results.SelectMany(x => x.RecoveredFallbackTracks).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            NotFound = results.SelectMany(x => x.NotFound).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public static List<string> ExtractSkippedTrackNames(IEnumerable<string> output)
    {
        var result = new List<string>();
        var inSkippedList = false;
        foreach (var raw in output)
        {
            var line = raw.Trim();
            if (line.Contains("Left skipped:", StringComparison.OrdinalIgnoreCase)) { inSkippedList = true; continue; }
            var marker = line.IndexOf("SKIPPED for now:", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) result.Add(line[(marker + "SKIPPED for now:".Length)..].Trim());
            marker = line.IndexOf(" was skipped. Paste a YouTube link", StringComparison.OrdinalIgnoreCase);
            if (marker > 0) result.Add(line[..marker].Trim());
            if (inSkippedList && line.StartsWith("- ", StringComparison.Ordinal)) result.Add(line[2..].Trim());
            else if (inSkippedList && line.Length > 0 && !line.StartsWith("download |", StringComparison.OrdinalIgnoreCase)) inSkippedList = false;
        }
        return result.Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<TrackInfo> ResolveSkippedTracks(IEnumerable<string> skippedNames, IReadOnlyList<TrackInfo> expectedTracks)
    {
        var result = new List<TrackInfo>();
        foreach (var skipped in skippedNames.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var match = expectedTracks.FirstOrDefault(track => SkippedNameMatches(skipped, track));
            if (match is not null) { result.Add(match); continue; }
            var separator = skipped.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0 && separator + 3 < skipped.Length)
                result.Add(new TrackInfo { Artists = new() { skipped[..separator].Trim() }, Title = skipped[(separator + 3)..].Trim() });
        }
        return result.GroupBy(track => TextNormalizer.Normalize(track.DisplayName)).Select(group => group.First()).ToList();
    }

    private static bool SkippedNameMatches(string skipped, TrackInfo track)
    {
        var value = TextNormalizer.Normalize(skipped, true);
        var title = TextNormalizer.Normalize(track.Title, true);
        if (title.Length == 0 || !value.Contains(title, StringComparison.Ordinal)) return false;
        return track.Artists.Count == 0 || track.Artists.Select(artist => TextNormalizer.Normalize(artist))
            .Where(artist => artist.Length > 0).Any(artist => value.Contains(artist, StringComparison.Ordinal));
    }

    private static string JsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        value = value.Trim().TrimEnd('.');
        return value.Length == 0 ? "SpotySync download" : value;
    }

    public static string? ExtractOutputFolder(IEnumerable<string> output)
    {
        foreach (var line in output)
        {
            foreach (var prefix in new[] { "Output folder:", "Download folder:" })
            {
                var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                var candidate = line[(index + prefix.Length)..].Trim().Trim('"');
                try { if (Directory.Exists(candidate)) return Path.GetFullPath(candidate); }
                catch { }
            }
        }
        return null;
    }

    private static IEnumerable<string> FilesChangedSince(string folder, DateTime thresholdUtc)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
                .Where(path => File.GetLastWriteTimeUtc(path) >= thresholdUtc || File.GetCreationTimeUtc(path) >= thresholdUtc)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
            process.WaitForExit(5000);
        }
        catch { }
    }

    private static bool CustomDownloaderIsConfigured(string executable)
    {
        var credentials = Path.Combine(Path.GetDirectoryName(executable)!, "spotify_credentials.txt");
        if (!File.Exists(credentials)) return false;
        var values = File.ReadAllLines(credentials)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return values.GetValueOrDefault("SPOTIFY_CLIENT_ID")?.Length > 0 &&
               values.GetValueOrDefault("SPOTIFY_CLIENT_SECRET")?.Length > 0;
    }

    private const string CosmeticExitMarker = "SPOTYSYNC: cosmetic downloader exit";

    private static bool IsDownloaderError(string line) =>
        line.Contains("AudioProviderError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("YT-DLP download error", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, (long Size, long Ticks)> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .ToDictionary(x => x.FullName, x => (x.Length, x.LastWriteTimeUtc.Ticks), StringComparer.OrdinalIgnoreCase);

    private sealed record YouTubeCandidate(string Url, string Title, string Uploader, int DurationSeconds, int Score, string? DownloadSection);
}
