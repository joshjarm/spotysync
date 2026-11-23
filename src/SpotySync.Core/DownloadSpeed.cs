namespace SpotySync;

/// <summary>
/// The download speed dial, shared by the downloader and the window. It runs from slowest (one song at a time with
/// pauses) to fastest (32 songs at a time). A change made while spotDL is running restarts the current batch at
/// the new speed; songs that already finished are kept and skipped instantly.
/// </summary>
public static class DownloadSpeed
{
    public readonly record struct Level(int Threads, int PauseMin, int PauseMax)
    {
        public string Label => Threads == 1
            ? (PauseMax > 0 ? $"1 song at a time, {PauseMin}-{PauseMax} s pauses" : "1 song at a time")
            : $"{Threads} songs at a time";
        public string Short => PauseMax > 0 ? $"1× +{PauseMax}s" : $"{Threads}×";
    }

    public static readonly Level[] Levels =
    [
        new(1, 5, 10), new(1, 2, 5), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0), new(4, 0, 0), new(6, 0, 0),
        new(8, 0, 0), new(10, 0, 0), new(12, 0, 0), new(16, 0, 0), new(20, 0, 0), new(24, 0, 0), new(32, 0, 0)
    ];

    /// <summary>12 songs at a time: fast from the start; turn it down if YouTube starts limiting.</summary>
    public const int DefaultThreads = 12;

    private static int index = IndexFor(DefaultThreads, 0);
    private static readonly object Gate = new();

    public static int Index { get { lock (Gate) return index; } }
    public static Level Current { get { lock (Gate) return Levels[index]; } }

    /// <summary>Goes up by one on every change; a running batch restarts when it sees a new value.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised after every change. The flag is true when the person changed it (save it), false when SpotySync slowed down by itself.</summary>
    public static event Action<bool>? Changed;

    public static void Set(int newIndex, bool byUser)
    {
        newIndex = Math.Clamp(newIndex, 0, Levels.Length - 1);
        lock (Gate)
        {
            if (newIndex == index) return;
            index = newIndex;
            Version++;
        }
        Changed?.Invoke(byUser);
    }

    /// <summary>Applies the saved setting without counting it as a live change.</summary>
    public static void Load(PortableSettings settings)
    {
        lock (Gate) index = IndexFor(settings.DownloadThreads, settings.DownloadPauseSeconds);
    }

    /// <summary>Writes the current dial position into the settings (the caller saves them).</summary>
    public static void Store(PortableSettings settings)
    {
        var level = Current;
        settings.DownloadThreads = level.Threads;
        settings.DownloadPauseSeconds = level.PauseMax;
    }

    /// <summary>The dial position closest to a song count and pause length.</summary>
    public static int IndexFor(int threads, int pauseSeconds)
    {
        threads = Math.Clamp(threads, 1, Levels[^1].Threads);
        if (threads == 1)
            return pauseSeconds >= 8 ? 0 : pauseSeconds >= 2 ? 1 : 2;
        var best = 3;
        for (var i = 3; i < Levels.Length; i++)
            if (Math.Abs(Levels[i].Threads - threads) < Math.Abs(Levels[best].Threads - threads)) best = i;
        return best;
    }

    /// <summary>Halves the song count (used when YouTube or Spotify start limiting). False when already at the slowest.</summary>
    public static bool StepDownForLimit()
    {
        var level = Current;
        if (Index == 0) return false;
        var target = level.Threads > 2 ? IndexFor(level.Threads / 2, 0) : Index - 1;
        Set(Math.Min(target, Index - 1), byUser: false);
        return true;
    }
}
