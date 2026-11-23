using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpotySync;

/// <summary>
/// The downloader processes (spotDL / yt-dlp / spot2yt and the ffmpeg, Deno … they start) that are running right now.
/// Pause suspends every one of them including their child processes, so a long download can be paused and resumed
/// without cancelling; nothing is lost because the processes simply stop being scheduled. New downloader processes
/// wait while paused (<see cref="WaitIfPausedAsync"/>).
/// </summary>
public static class DownloadProcesses
{
    private static readonly object Gate = new();
    private static readonly HashSet<Process> Running = new();
    private static readonly HashSet<int> Suspended = new();
    private static TaskCompletionSource resumed = Completed();
    private static bool paused;

    /// <summary>Raised (on any thread) when Paused or HasRunning changes.</summary>
    public static event Action? StateChanged;

    public static bool Paused { get { lock (Gate) return paused; } }

    public static bool HasRunning
    {
        get { lock (Gate) { Running.RemoveWhere(HasExited); return Running.Count > 0; } }
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    /// <summary>Starts a downloader process and tracks it (suspended at once if a pause is active).</summary>
    public static Process Start(ProcessStartInfo start)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + Path.GetFileName(start.FileName) + ".");
        Register(process);
        return process;
    }

    public static void Register(Process process)
    {
        bool suspendNow;
        lock (Gate)
        {
            Running.Add(process);
            suspendNow = paused;
        }
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Unregister(process);
        }
        catch { }
        if (suspendNow) SuspendTree(process.Id);
        StateChanged?.Invoke();
    }

    private static void Unregister(Process process)
    {
        lock (Gate) Running.Remove(process);
        StateChanged?.Invoke();
    }

    /// <summary>Completes immediately unless downloads are paused; then waits for Resume (or cancellation).</summary>
    public static Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (Gate) wait = paused ? resumed.Task : Task.CompletedTask;
        return wait.IsCompleted ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
    }

    public static void Pause()
    {
        List<Process> targets;
        lock (Gate)
        {
            if (paused) return;
            paused = true;
            resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Running.RemoveWhere(HasExited);
            targets = Running.ToList();
        }
        foreach (var process in targets) SuspendTree(process.Id);
        StateChanged?.Invoke();
    }

    public static void Resume()
    {
        TaskCompletionSource toRelease;
        List<int> pids;
        lock (Gate)
        {
            if (!paused) return;
            paused = false;
            toRelease = resumed;
            pids = Suspended.ToList();
            Suspended.Clear();
        }
        foreach (var pid in pids) ResumeOne(pid);
        toRelease.TrySetResult();
        StateChanged?.Invoke();
    }

    /// <summary>Clears a pause (end of a run or cancel), resuming anything still suspended.</summary>
    public static void Reset() => Resume();

    // ------------------------------------------------------------------ process tree suspend/resume
    private static void SuspendTree(int rootPid)
    {
        foreach (var pid in TreeOf(rootPid))
        {
            lock (Gate) if (!Suspended.Add(pid)) continue;
            if (!OperatingSystem.IsWindows()) { try { _ = kill(pid, SIGSTOP); } catch { } continue; }
            var handle = OpenProcess(ProcessSuspendResume, false, pid);
            if (handle == IntPtr.Zero) continue;
            try { NtSuspendProcess(handle); } finally { CloseHandle(handle); }
        }
    }

    private static void ResumeOne(int pid)
    {
        if (!OperatingSystem.IsWindows()) { try { _ = kill(pid, SIGCONT); } catch { } return; }
        var handle = OpenProcess(ProcessSuspendResume, false, pid);
        if (handle == IntPtr.Zero) return;
        try { NtResumeProcess(handle); } finally { CloseHandle(handle); }
    }

    /// <summary>The process and all of its descendants (from a Toolhelp snapshot).</summary>
    private static List<int> TreeOf(int rootPid)
    {
        var children = new Dictionary<int, List<int>>();
        if (!OperatingSystem.IsWindows()) return Collect(rootPid, LinuxChildren());
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return new List<int> { rootPid };
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    var pid = (int)entry.th32ProcessID; var parent = (int)entry.th32ParentProcessID;
                    if (pid == parent) continue;
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                    list.Add(pid);
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }
        return Collect(rootPid, children);
    }

    /// <summary>Parent → children from /proc/&lt;pid&gt;/stat (field 4 is the parent id).</summary>
    private static Dictionary<int, List<int>> LinuxChildren()
    {
        var children = new Dictionary<int, List<int>>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                string stat;
                try { stat = File.ReadAllText(Path.Combine(dir, "stat")); } catch { continue; }
                var close = stat.LastIndexOf(')'); // the command name may contain spaces and brackets
                if (close < 0) continue;
                var fields = stat[(close + 2)..].Split(' ');
                if (fields.Length < 2 || !int.TryParse(fields[1], out var parent) || parent == pid) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(pid);
            }
        }
        catch { }
        return children;
    }

    private static List<int> Collect(int rootPid, Dictionary<int, List<int>> children)
    {
        var result = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0 && result.Count < 256)
        {
            var pid = queue.Dequeue();
            if (result.Contains(pid)) continue;
            result.Add(pid);
            if (children.TryGetValue(pid, out var kids)) foreach (var kid in kids) queue.Enqueue(kid);
        }
        return result;
    }

    private const uint ProcessSuspendResume = 0x0800;
    private const int SIGCONT = 18, SIGSTOP = 19; // Linux x86-64 / arm64
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr handle);
}
