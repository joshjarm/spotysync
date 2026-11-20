using System.Diagnostics;
using System.Text.Json;

namespace SpotySync;

public sealed class LibraryScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wav", ".wma", ".aac", ".aiff", ".wv" };
    private readonly PortableSettings settings;
    private readonly Action<string> log;

    public LibraryScanner(PortableSettings settings, Action<string>? log = null)
    {
        this.settings = settings;
        this.log = log ?? (_ => { });
    }

    public async Task<List<LocalTrack>> ScanAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        => await ScanAsync(forceRefresh, null, cancellationToken);

    public async Task<List<LocalTrack>> ScanAsync(bool forceRefresh, IEnumerable<LocalTrack>? seedMetadata, CancellationToken cancellationToken = default)
        => await ScanCoreAsync(forceRefresh, seedMetadata, true, cancellationToken);

    private async Task<List<LocalTrack>> ScanCoreAsync(bool forceRefresh, IEnumerable<LocalTrack>? seedMetadata,
        bool retryIfLibraryMoves, CancellationToken cancellationToken)
    {
        var folder = settings.ResolvePath(settings.MusicFolder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Music folder was not found: " + folder);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => Extensions.Contains(Path.GetExtension(path))).ToList();
        var cached = forceRefresh ? new List<LocalTrack>() : JsonStore.Load(settings.LibraryCachePath, () => new List<LocalTrack>());
        if (!forceRefresh && seedMetadata is not null)
        {
            // A MusicBee snapshot is a fast metadata seed for large libraries. Files
            // absent from MusicBee are still discovered and inspected from disk.
            cached = seedMetadata.Concat(cached)
                .GroupBy(x => SyncPlanner.PathKey(x.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
        }
        var byPath = cached.ToDictionary(x => SyncPlanner.PathKey(x.Path), StringComparer.OrdinalIgnoreCase);
        var results = new LocalTrack?[files.Count];
        var missing = new List<(int Index, string Path, long Size, long ModifiedUtcTicks)>();
        var vanished = 0;
        for (var i = 0; i < files.Count; i++)
        {
            try
            {
                var info = new FileInfo(files[i]);
                info.Refresh();
                if (!info.Exists) { vanished++; continue; }
                var size = info.Length;
                var modifiedUtcTicks = info.LastWriteTimeUtc.Ticks;
                if (byPath.TryGetValue(SyncPlanner.PathKey(files[i]), out var old) && old.Size == size && old.ModifiedUtcTicks == modifiedUtcTicks)
                    results[i] = old;
                else
                    missing.Add((i, files[i], size, modifiedUtcTicks));
            }
            catch (IOException) { vanished++; }
            catch (UnauthorizedAccessException) { vanished++; }
        }
        log($"Local library at {folder}: {files.Count} audio files, {results.Count(track => track is not null)} metadata cache hits, {missing.Count} to inspect.");
        var ffprobe = settings.ResolvePath(settings.FfprobePath);
        using var gate = new SemaphoreSlim(4, 4);
        var processed = 0;
        await Task.WhenAll(missing.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var track = File.Exists(ffprobe)
                    ? await ProbeAsync(ffprobe, item.Path, item.Size, item.ModifiedUtcTicks, cancellationToken)
                    : FromFilename(item.Path, item.Size, item.ModifiedUtcTicks);
                if (File.Exists(item.Path)) results[item.Index] = track;
                else Interlocked.Increment(ref vanished);
                var count = Interlocked.Increment(ref processed);
                if (count % 250 == 0) log($"Local metadata scan: {count}/{missing.Count}…");
            }
            catch (IOException) { Interlocked.Increment(ref vanished); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref vanished); }
            finally { gate.Release(); }
        }));
        var final = results.Where(x => x is not null).Cast<LocalTrack>().ToList();
        List<string> currentFiles;
        try
        {
            currentFiles = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => Extensions.Contains(Path.GetExtension(path))).ToList();
        }
        catch { currentFiles = files; }
        var pathsChanged = vanished > 0 || !new HashSet<string>(files, StringComparer.OrdinalIgnoreCase).SetEquals(currentFiles);
        if (pathsChanged && retryIfLibraryMoves)
        {
            log("MusicBee renamed or moved new audio while SpotySync was reading it; refreshing the library paths once.");
            await Task.Delay(250, cancellationToken);
            return await ScanCoreAsync(false, final, false, cancellationToken);
        }
        if (pathsChanged)
            log("WARNING: audio files continued moving during the refreshed scan; skipped transient paths and continued with the stable files.");
        JsonStore.SaveAtomic(settings.LibraryCachePath, final);
        return final;
    }

    /// <summary>
    /// Brings an already-loaded library up to date with the disk without a full rescan: audio files in the music
    /// folder that are not in <paramref name="library"/> are inspected and returned as added, and tracks whose file
    /// no longer exists are returned as removed. Files still being written (or renamed away meanwhile) are skipped
    /// and simply picked up next time.
    /// </summary>
    public async Task<(List<LocalTrack> Added, List<LocalTrack> Removed)> ReconcileAsync(IReadOnlyCollection<LocalTrack> library, CancellationToken cancellationToken = default)
    {
        var folder = settings.ResolvePath(settings.MusicFolder);
        var known = new HashSet<string>(library.Select(x => SyncPlanner.PathKey(x.Path)), StringComparer.OrdinalIgnoreCase);
        // a song only counts as removed when its drive is there (an unplugged drive must not "delete" songs)
        var removed = library.Where(track => track.Path.Length > 0 && !File.Exists(track.Path) && DriveAvailable(track.Path)).ToList();
        var added = new List<LocalTrack>();
        if (!Directory.Exists(folder)) return (added, removed);
        List<string> fresh;
        try
        {
            fresh = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => Extensions.Contains(Path.GetExtension(path)) && !IsPartialDownload(path) && !known.Contains(SyncPlanner.PathKey(path)))
                .ToList();
        }
        catch (IOException) { return (added, removed); }
        catch (UnauthorizedAccessException) { return (added, removed); }
        var ffprobe = settings.ResolvePath(settings.FfprobePath);
        foreach (var path in fresh)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                // a file written in the last two seconds may still be growing (ffmpeg, tag writes)
                if (!info.Exists || info.Length == 0 || DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(2)) continue;
                var track = File.Exists(ffprobe)
                    ? await ProbeAsync(ffprobe, path, info.Length, info.LastWriteTimeUtc.Ticks, cancellationToken)
                    : FromFilename(path, info.Length, info.LastWriteTimeUtc.Ticks);
                if (File.Exists(path)) added.Add(track);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (added, removed);
    }

    private static bool DriveAvailable(string path)
    {
        try { var root = Path.GetPathRoot(Path.GetFullPath(path)); return !string.IsNullOrEmpty(root) && Directory.Exists(root); }
        catch { return false; }
    }

    /// <summary>yt-dlp / spotDL temporary names (still downloading or about to be converted).</summary>
    public static bool IsPartialDownload(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".temp.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
               System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"\.f\d+$");
    }

    public static bool IsAudioFile(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool ContainsAudio(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(path => Extensions.Contains(Path.GetExtension(path)));
        }
        catch { return false; }
    }

    public static List<LocalTrack> MergeExisting(params IEnumerable<LocalTrack>[] sources)
    {
        return sources.SelectMany(source => source)
            .Where(track => !string.IsNullOrWhiteSpace(track.Path) && File.Exists(track.Path))
            .GroupBy(track => SyncPlanner.PathKey(track.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(track => track.Title.Length + track.Artist.Length).First())
            .ToList();
    }

    public static string? InferCommonLibraryRoot(IEnumerable<LocalTrack> tracks)
    {
        var directories = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Path) && File.Exists(track.Path))
            .Select(track => Path.GetDirectoryName(Path.GetFullPath(track.Path)))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (directories.Count == 0) return null;
        var roots = directories.Select(Path.GetPathRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count != 1 || string.IsNullOrWhiteSpace(roots[0])) return null;

        var common = directories[0];
        foreach (var directory in directories.Skip(1))
        {
            while (!IsSameOrChild(directory, common))
            {
                common = Path.GetDirectoryName(common) ?? roots[0]!;
                if (common.Equals(roots[0], StringComparison.OrdinalIgnoreCase)) return null;
            }
        }
        return common.Equals(roots[0], StringComparison.OrdinalIgnoreCase) ? null : common;
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<LocalTrack> ProbeAsync(string ffprobe, string path, long size, long modifiedUtcTicks, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration:format_tags", "-of", "json", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return FromFilename(path, size, modifiedUtcTicks);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return FromFilename(path, size, modifiedUtcTicks);
        try
        {
            using var doc = JsonDocument.Parse(output);
            var format = doc.RootElement.GetProperty("format");
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (format.TryGetProperty("tags", out var tagElement))
                foreach (var tag in tagElement.EnumerateObject()) tags[tag.Name] = tag.Value.ToString();
            var fallback = FromFilename(path, size, modifiedUtcTicks);
            var comment = string.Join(" ", tags.Where(x => x.Key.Contains("comment", StringComparison.OrdinalIgnoreCase) || x.Key.Contains("url", StringComparison.OrdinalIgnoreCase) || x.Value.Contains("spotify", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value));
            var durationText = format.TryGetProperty("duration", out var durationValue) ? durationValue.ToString() : "";
            var duration = double.TryParse(durationText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? (int)Math.Round(seconds * 1000) : 0;
            return new LocalTrack
            {
                Path = path, Title = Get(tags, "title", fallback.Title), Artist = Get(tags, "artist", fallback.Artist),
                Album = Get(tags, "album", ""), Comment = comment, DurationMs = duration,
                SpotifyId = SpotifyIdExtractor.FromText(comment, path), Size = size, ModifiedUtcTicks = modifiedUtcTicks
            };
        }
        catch { return FromFilename(path, size, modifiedUtcTicks); }
    }

    private static string Get(Dictionary<string, string> tags, string key, string fallback) => tags.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    private static LocalTrack FromFilename(string path, long size, long modifiedUtcTicks)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var split = name.IndexOf(" - ", StringComparison.Ordinal);
        return new LocalTrack
        {
            Path = path, Artist = split > 0 ? name[..split].Trim() : "", Title = split > 0 ? name[(split + 3)..].Trim() : name,
            SpotifyId = SpotifyIdExtractor.FromText(path), Size = size, ModifiedUtcTicks = modifiedUtcTicks
        };
    }
}
