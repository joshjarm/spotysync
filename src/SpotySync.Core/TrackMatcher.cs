using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SpotySync;

public static partial class TextNormalizer
{
    [GeneratedRegex(@"\b(feat(?:uring)?|ft)\.?\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingSuffix();

    [GeneratedRegex(@"[\(\[][^\)\]]*(remaster(?:ed)?|version|radio edit|album edit|mono|stereo|explicit|clean)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex VersionQualifier();

    [GeneratedRegex(@"\s*[-â€“â€”]\s*[^-]*(remaster(?:ed)?|version|edit(?:ion)?|radio edit|album edit|mono|stereo|explicit|clean)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Normalize(string? value, bool stripVersionQualifiers = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var source = value.Replace('&', ' ').Replace("’", "'").Replace("‘", "'");
        source = FeaturingSuffix().Replace(source, "");
        if (stripVersionQualifiers)
        {
            source = VersionQualifier().Replace(source, "");
            source = VersionSuffix().Replace(source, "");
        }
        source = source.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(source.Length);
        foreach (var c in source)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    public static string PlaylistKey(string? name) => Normalize(name ?? "");

    public static string MusicBeePlaylistLeaf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var normalized = name.Replace('/', '\\').Trim().TrimEnd('\\');
        var separator = normalized.LastIndexOf('\\');
        return (separator >= 0 ? normalized[(separator + 1)..] : normalized).Trim();
    }

    public static string StableKey(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static bool WildcardMatch(string text, string patterns, bool emptyMatches)
    {
        var pieces = (patterns ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length == 0) return emptyMatches;
        return pieces.Any(pattern => Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase));
    }
}

public static partial class SpotifyIdExtractor
{
    [GeneratedRegex(@"(?:spotify:track:|open\.spotify\.com/track/)([A-Za-z0-9]{22})", RegexOptions.IgnoreCase)]
    private static partial Regex TrackUri();

    public static string FromText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var match = TrackUri().Match(value);
            if (match.Success) return match.Groups[1].Value;
        }
        return "";
    }
}

public sealed class TrackMatcher
{
    private readonly Dictionary<string, TrackInfo> byId;
    private readonly Dictionary<string, List<TrackInfo>> byTitle;

    public TrackMatcher(IEnumerable<TrackInfo> spotifyLibrary)
    {
        var tracks = spotifyLibrary.Where(x => !string.IsNullOrWhiteSpace(x.SpotifyId)).ToList();
        byId = tracks.GroupBy(x => x.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        byTitle = tracks.GroupBy(x => TextNormalizer.Normalize(x.Title, true))
            .Where(x => x.Key.Length > 0)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
    }

    public IReadOnlyList<TrackMatch> MatchLibrary(IEnumerable<LocalTrack> localTracks)
    {
        return localTracks.Select(Match).ToList();
    }

    public TrackMatch Match(LocalTrack local)
    {
        var embedded = !string.IsNullOrWhiteSpace(local.SpotifyId)
            ? local.SpotifyId
            : SpotifyIdExtractor.FromText(local.Comment, local.Path);
        if (embedded.Length > 0 && byId.TryGetValue(embedded, out var direct))
        {
            local.SpotifyId = direct.SpotifyId;
            return new TrackMatch { Local = local, Spotify = direct, Score = 1000, Reason = "embedded Spotify track ID", IsConfident = true };
        }

        var normal = MatchMetadata(local);
        if (normal.IsConfident) return normal;

        // Some downloaders write "title" into Artist and "artist" into Title.
        // Recognize that exact inversion instead of showing the same recording as
        // both MusicBee-only and Spotify-only. The original local tags are kept.
        if (!string.IsNullOrWhiteSpace(local.Title) && !string.IsNullOrWhiteSpace(local.Artist))
        {
            var swappedLocal = new LocalTrack
            {
                Path = local.Path, Title = local.Artist, Artist = local.Title, Album = local.Album,
                Comment = local.Comment, DurationMs = local.DurationMs, Size = local.Size,
                ModifiedUtcTicks = local.ModifiedUtcTicks, SpotifyId = local.SpotifyId
            };
            var swapped = MatchMetadata(swappedLocal);
            if (swapped.IsConfident && swapped.Spotify is not null)
            {
                local.SpotifyId = swapped.Spotify.SpotifyId;
                return new TrackMatch
                {
                    Local = local, Spotify = swapped.Spotify, Score = swapped.Score,
                    Reason = "Spotify metadata match (local title/artist tags are reversed)", IsConfident = true
                };
            }
        }
        return normal;
    }

    private TrackMatch MatchMetadata(LocalTrack local)
    {
        var title = TextNormalizer.Normalize(local.Title, true);
        if (title.Length == 0 || !byTitle.TryGetValue(title, out var candidates))
            return new TrackMatch { Local = local, Reason = "title not present in cached Spotify metadata", Score = 0 };

        var ranked = candidates.Select(track => (Track: track, Score: Score(local, track)))
            .OrderByDescending(x => x.Score)
            .ToList();
        var best = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1].Score : int.MinValue;
        var exactArtist = ArtistMatches(local.Artist, best.Track.Artists);
        // Spotify sometimes relinks the same recording to a new catalog ID. Tied
        // candidates are safe when their title, primary artist and duration identify
        // the same recording; they must not turn a valid local match into ambiguity.
        var hasDifferentCloseCandidate = ranked.Skip(1)
            .Where(candidate => best.Score - candidate.Score < 8)
            .Any(candidate => !SameRecording(best.Track, candidate.Track));
        var confident = best.Score >= 90 && exactArtist && !hasDifferentCloseCandidate;
        if (confident) local.SpotifyId = best.Track.SpotifyId;
        var reason = confident
            ? $"Spotify metadata match ({best.Score})"
            : hasDifferentCloseCandidate
                ? $"ambiguous Spotify metadata match ({best.Score}/{runnerUp})"
                : $"match confidence too low ({best.Score})";
        return new TrackMatch { Local = local, Spotify = best.Track, Score = best.Score, Reason = reason, IsConfident = confident };
    }

    public static bool SameRecording(TrackInfo left, TrackInfo right)
    {
        var leftKey = RecordingGroupKey(left);
        if (leftKey.Length == 0 || !leftKey.Equals(RecordingGroupKey(right), StringComparison.Ordinal)) return false;
        return left.DurationMs <= 0 || right.DurationMs <= 0 || Math.Abs(left.DurationMs - right.DurationMs) <= 2_500;
    }

    public static string RecordingGroupKey(TrackInfo track)
    {
        var title = TextNormalizer.Normalize(track.Title, true);
        var primaryArtist = NormalizeArtist(track.Artists.FirstOrDefault());
        return title.Length == 0 || primaryArtist.Length == 0 ? "" : title + "\u001f" + primaryArtist;
    }

    public static bool IsLikelyDownloadedMatch(LocalTrack local, TrackInfo spotify)
    {
        var localTitle = NormalizeDownloadedTitle(local);
        var spotifyTitle = TextNormalizer.Normalize(spotify.Title, true);
        if (localTitle.Length == 0 || spotifyTitle.Length == 0 ||
            !(localTitle == spotifyTitle || localTitle.Contains(spotifyTitle, StringComparison.Ordinal) || spotifyTitle.Contains(localTitle, StringComparison.Ordinal)))
            return false;

        var artistMatches = ArtistMatches(local.Artist, spotify.Artists) ||
            spotify.Artists.Select(NormalizeArtist).Where(value => value.Length > 0)
                .Any(artist => NormalizeArtist(Path.GetFileNameWithoutExtension(local.Path)).Contains(artist, StringComparison.Ordinal));
        var durationMatches = local.DurationMs > 0 && spotify.DurationMs > 0 && Math.Abs(local.DurationMs - spotify.DurationMs) <= 10_000;
        // A title-only file such as "Bartender.mp3" is acceptable only when its
        // measured duration corroborates the Spotify recording.
        return artistMatches || durationMatches;
    }

    private static string NormalizeDownloadedTitle(LocalTrack local)
    {
        var tagged = TextNormalizer.Normalize(local.Title, true);
        if (tagged.Length > 0) return tagged;
        return TextNormalizer.Normalize(Path.GetFileNameWithoutExtension(local.Path), true);
    }

    private static int Score(LocalTrack local, TrackInfo spotify)
    {
        var score = 0;
        var localTitle = TextNormalizer.Normalize(local.Title, true);
        var spotifyTitle = TextNormalizer.Normalize(spotify.Title, true);
        if (localTitle == spotifyTitle) score += 60;
        else if (localTitle.Contains(spotifyTitle, StringComparison.Ordinal) || spotifyTitle.Contains(localTitle, StringComparison.Ordinal)) score += 40;
        else return 0;

        if (ArtistMatches(local.Artist, spotify.Artists)) score += 35;
        else return score;

        var localAlbum = TextNormalizer.Normalize(local.Album, true);
        var spotifyAlbum = TextNormalizer.Normalize(spotify.Album, true);
        if (localAlbum.Length > 0 && localAlbum == spotifyAlbum) score += 10;
        if (local.DurationMs > 0 && spotify.DurationMs > 0)
        {
            var difference = Math.Abs(local.DurationMs - spotify.DurationMs);
            if (difference <= 2_500) score += 15;
            else if (difference <= 6_000) score += 6;
            else score -= 5;
        }
        return score;
    }

    private static bool ArtistMatches(string localArtist, IEnumerable<string> spotifyArtists)
    {
        var local = NormalizeArtist(localArtist);
        if (local.Length == 0) return false;
        foreach (var artist in spotifyArtists)
        {
            var remote = NormalizeArtist(artist);
            if (remote.Length == 0) continue;
            if (local == remote || local.Contains(remote, StringComparison.Ordinal) || remote.Contains(local, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string NormalizeArtist(string? value)
    {
        var normalized = TextNormalizer.Normalize(value);
        if (normalized.Length == 0) return "";
        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !token.Equals("and", StringComparison.Ordinal)));
    }
}
