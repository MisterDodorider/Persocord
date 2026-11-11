using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// Keeps Persocord's recording work inside set limits, so it can never take
/// over the PC or make a game stutter:
///  - CPU and RAM are hard caps enforced by Windows itself (a job object):
///    FFmpeg simply can't use more. Past the RAM cap it can't allocate and
///    stops; the replay then restarts on its own.
///  - The job also closes FFmpeg if Persocord itself ever crashes, so no
///    orphaned recorder keeps running in the background.
///  - On the graphics card, FFmpeg runs at below-normal priority, so the
///    game always goes first when the card is busy.
///  - Video memory has no hard cap in Windows, so it's watched instead:
///    <see cref="VideoMemoryMb"/> reads what a process holds right now.
/// </summary>
public static class ResourceGuard
{
    private static readonly List<IntPtr> Jobs = new();

    /// <summary>Puts a freshly started FFmpeg under the limits in settings.</summary>
    public static void Contain(Process p, AppSettings s)
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;

            var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            ext.BasicLimitInformation.LimitFlags = JobMemory | KillOnJobClose;
            ext.JobMemoryLimit = (UIntPtr)((ulong)Math.Clamp(s.SafetyRamMb, 256, 8192) * 1024 * 1024);
            SetInformationJobObject(job, 9 /* extended limits */, ref ext, (uint)Marshal.SizeOf(ext));

            // CPU: by default a soft share. When the CPU is busy, the recorder
            // gets less than other programs (a game included); when it isn't,
            // nothing is held back. A hard cap is optional: Windows enforces it
            // by pausing the process in bursts, which drops frames and let
            // sound pile up (the 5-6 second delays and choppy replays).
            var cpu = s.SafetyCpuHardCap
                ? new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = 0x1 | 0x4,   // enable, hard cap
                    CpuRate = (uint)(Math.Clamp(s.SafetyCpuPercent, 5, 100) * 100),
                }
                : new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = 0x1 | 0x2,   // enable, weight-based (soft)
                    CpuRate = 3,                // weight 1-9; 5 is everyone else's
                };
            SetInformationJobObject(job, 15 /* CPU rate control */, ref cpu, (uint)Marshal.SizeOf(cpu));

            if (!AssignProcessToJobObject(job, p.Handle)) { CloseHandle(job); return; }
            lock (Jobs) Jobs.Add(job);
            // The job is let go of when FFmpeg ends. Watched through a handle of
            // its own: the Process object's "Exited" never came for one that was
            // killed and let go of at once (a cancel, a restart), and each of
            // those kept a job open until Persocord closed.
            var watch = OpenProcess(Synchronize, false, p.Id);
            if (watch != IntPtr.Zero)
            {
                var ended = new ProcessEnd(watch);
                ThreadPool.RegisterWaitForSingleObject(ended, (state, _) =>
                {
                    lock (Jobs) Jobs.Remove(job);
                    CloseHandle(job);
                    ((ProcessEnd)state!).Dispose();
                }, ended, Timeout.Infinite, executeOnlyOnce: true);
            }
            else
            {
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) => { lock (Jobs) Jobs.Remove(job); CloseHandle(job); };
            }

            Log.Write($"Limits for FFmpeg {p.Id}: CPU " +
                      (s.SafetyCpuHardCap ? $"hard cap {s.SafetyCpuPercent}%" : "soft (yields to games when busy, never paused)") +
                      $", RAM {s.SafetyRamMb} MB");
        }
        catch (Exception ex) { Log.Error("resource limits", ex); }
    }

    /// <summary>
    /// Recording's place in the graphics card's queue: normal, or behind the
    /// game (used only while the card's memory is nearly full).
    /// </summary>
    public static void SetGpuPriority(Process p, bool behindGames)
    {
        try { D3DKMTSetProcessSchedulingPriorityClass(p.Handle, behindGames ? 1 : 2); }
        catch (Exception ex) { Log.Error("graphics card priority", ex); }
    }

    /// <summary>
    /// Video memory in use on the busiest graphics card, by everything on the
    /// PC (the game included), in MB.
    /// </summary>
    public static double CardMemoryUsedMb()
    {
        lock (CardGate)
        {
            _card ??= new PdhCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            var values = _card.Read();
            return values.Count == 0 ? 0 : values.Max() / 1048576.0;
        }
    }

    /// <summary>Dedicated video memory a process holds right now, in MB (0 if unknown).</summary>
    public static double VideoMemoryMb(int pid)
    {
        lock (ProcessGate)
        {
            // One query kept per process being watched: opening a fresh one
            // each time made Windows list every program's graphics memory
            // all over again, which is what cost CPU.
            if (_processPid != pid)
            {
                _process?.Dispose();
                _process = new PdhCounter($@"\GPU Process Memory(pid_{pid}_*)\Dedicated Usage");
                _processPid = pid;
            }
            var values = _process!.Read();
            return values.Sum() / 1048576.0;
        }
    }

    private static readonly object CardGate = new(), ProcessGate = new();
    private static PdhCounter? _card, _process;
    private static int _processPid = -1;

    /// <summary>A performance counter kept open between reads (reopened if it stops working).</summary>
    private sealed class PdhCounter : IDisposable
    {
        private readonly string _path;
        private IntPtr _query, _counter;
        private int _reads;

        // Windows' graphics-card counters remember every program instance they
        // have ever listed for as long as the query stays open, so a query kept
        // open for days grew by about 20 MB an hour (seen in a 37-hour log:
        // Persocord at 1.2 GB with its own memory at 20 MB). Reopened every
        // 10 minutes (120 reads 5 s apart), that memory is let go each time.
        private const int ReadsPerQuery = 120;

        public PdhCounter(string path) => _path = path;

        public List<long> Read()
        {
            var values = new List<long>();
            try
            {
                if (_query != IntPtr.Zero && ++_reads >= ReadsPerQuery) Dispose();
                if (_query == IntPtr.Zero)
                {
                    if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return values; }
                    if (PdhAddEnglishCounter(_query, _path, IntPtr.Zero, out _counter) != 0) { Dispose(); return values; }
                }
                if (PdhCollectQueryData(_query) != 0) { Dispose(); return values; }
                uint size = 0, count = 0;
                PdhGetFormattedCounterArray(_counter, PdhFmtLarge, ref size, ref count, IntPtr.Zero);
                if (size == 0) return values;
                var buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArray(_counter, PdhFmtLarge, ref size, ref count, buf) != 0) return values;
                    int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                    for (int i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                        if (item.CStatus == 0) values.Add(item.LargeValue);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { Dispose(); }
            return values;
        }

        public void Dispose()
        {
            try { if (_query != IntPtr.Zero) PdhCloseQuery(_query); } catch { }
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _reads = 0;
        }
    }

    /// <summary>A process's own handle, to wait on its end.</summary>
    private sealed class ProcessEnd : WaitHandle
    {
        public ProcessEnd(IntPtr handle) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: true);
    }

    /* ---- Windows declarations -------------------------------------------- */

    private const uint Synchronize = 0x00100000;
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    private const uint JobMemory = 0x200;
    private const uint KillOnJobClose = 0x2000;
    private const uint PdhFmtLarge = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public uint CStatus;
        private uint _pad;
        public long LargeValue;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attr, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
