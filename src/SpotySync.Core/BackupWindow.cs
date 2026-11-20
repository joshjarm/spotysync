namespace SpotySync;

public static class BackupWindow
{
    public static DateTimeOffset? OldestOfNewest(IEnumerable<DateTimeOffset> downloadTimes, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var newest = downloadTimes.OrderByDescending(time => time).Take(count).ToList();
        return newest.Count == count ? newest[^1] : null;
    }

    public static List<TrackInfo> RecentLikesNewestFirst(IEnumerable<TrackInfo> tracks, DateTimeOffset cutoff)
    {
        return tracks
            .Where(track => track.AddedAt.HasValue && track.AddedAt.Value > cutoff)
            .OrderByDescending(track => track.AddedAt!.Value)
            .ToList();
    }
}
