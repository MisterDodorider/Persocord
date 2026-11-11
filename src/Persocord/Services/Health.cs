using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Persocord.Services;

/// <summary>
/// How much Persocord is holding: memory, handles, threads, its FFmpegs and
/// video players. Written to the log every 15 minutes and shown in a
/// diagnostic report, so a slowdown after hours of running shows what grew.
/// Only counts: nothing about clips, files or the screen.
/// </summary>
public static class Health
{
    // Players and decoders made and finished, counted where they're made.
    private static int _vlcOpen, _vlcStopping, _decoders;
    private static long _vlcEver;

    /// <summary>VLC players alive right now (playing, paused or still stopping).</summary>
    public static int VlcPlayers => Volatile.Read(ref _vlcOpen);
    /// <summary>VLC players made since Persocord started (hover previews included).</summary>
    public static long VlcOpenedTotal => Interlocked.Read(ref _vlcEver);

    public static void VlcOpened() { Interlocked.Increment(ref _vlcOpen); Interlocked.Increment(ref _vlcEver); }
    public static void VlcClosing() => Interlocked.Increment(ref _vlcStopping);
    public static void VlcClosed() { Interlocked.Decrement(ref _vlcOpen); Interlocked.Decrement(ref _vlcStopping); }
    public static void DecoderStarted() => Interlocked.Increment(ref _decoders);
    public static void DecoderStopped() => Interlocked.Decrement(ref _decoders);

    private static Timer? _timer;
    private static readonly DateTime Started = DateTime.Now;

    public static void Start()
    {
        _timer ??= new Timer(_ =>
        {
            try
            {
                // Windows objects (sound sessions, devices) are freed only when
                // .NET collects their wrappers, and with Persocord's own memory
                // this small it hardly ever did: their memory piled up over days.
                // A collection every 15 minutes takes a few milliseconds.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Log.Write("Health: " + Snapshot());
            }
            catch { }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15));
        // Once a minute: a sudden jump in memory is logged as it happens, with
        // what was open, so a spike can be traced (one of 7.8 GB with the
        // editor open showed up only in a 15-minute line, with nothing to say why).
        _jumpWatch ??= new Timer(_ => WatchJumps(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(IdleMemory.Start));
    }

    private static Timer? _jumpWatch;
    private static long _lastPrivate;

    private static void WatchJumps()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            long now = me.PrivateMemorySize64;
            long before = Interlocked.Exchange(ref _lastPrivate, now);
            if (before > 0 && now - before > 1L << 30)
                Log.Write($"Health: memory jumped {before / 1048576} → {now / 1048576} MB in a minute · open: {OpenWindows()} · " + Snapshot());
        }
        catch { }
    }

    /// <summary>The kinds of Persocord window open right now (for the log).</summary>
    internal static string OpenWindows()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app == null) return "?";
            return app.Dispatcher.Invoke(() =>
            {
                var names = new List<string>();
                foreach (System.Windows.Window w in app.Windows)
                    if (w.IsVisible) names.Add(w.GetType().Name);
                return names.Count == 0 ? "none" : string.Join(", ", names);
            }, System.Windows.Threading.DispatcherPriority.Background, CancellationToken.None, TimeSpan.FromSeconds(2));
        }
        catch { return "?"; }
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);

    public static string Snapshot()
    {
        using var me = Process.GetCurrentProcess();
        me.Refresh();
        int gdi = 0, user = 0;
        try { gdi = GetGuiResources(me.Handle, 0); user = GetGuiResources(me.Handle, 1); } catch { }
        int ffmpegs = 0;
        try
        {
            foreach (var p in Process.GetProcessesByName("ffmpeg"))
                using (p)
                    try { if (string.Equals(p.MainModule?.FileName, Paths.FFmpeg, StringComparison.OrdinalIgnoreCase)) ffmpegs++; }
                    catch { }
        }
        catch { }
        var up = DateTime.Now - Started;
        return $"up {(int)up.TotalHours}h{up.Minutes:00}m · memory {me.WorkingSet64 / 1048576} MB (private {me.PrivateMemorySize64 / 1048576} MB, .NET {GC.GetTotalMemory(false) / 1048576} MB) · " +
               $"handles {me.HandleCount} · threads {me.Threads.Count} · GDI {gdi} · USER {user} · " +
               $"FFmpegs {ffmpegs} · VLC players {Volatile.Read(ref _vlcOpen)} ({Volatile.Read(ref _vlcStopping)} stopping, {VlcOpenedTotal} made) · preview decoders {Volatile.Read(ref _decoders)}";
    }
}

/// <summary>
/// While Persocord sits in the tray with no window open, the memory its
/// windows used (the library, the editor, the player) is handed back to
/// Windows: .NET's memory is compacted, the C runtime's freed blocks are
/// released, and the pages Persocord isn't using right now are let go, so a
/// game has that RAM. What's still in use (the replay's sound) simply stays.
/// Done once each time it goes idle, two minutes after the last window
/// closes; reopening a window afterwards may take a moment longer, once.
/// </summary>
internal static class IdleMemory
{
    private static System.Windows.Threading.DispatcherTimer? _timer;
    private static DateTime? _idleSince;
    private static bool _done;
    private static int _busy;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private static void Check()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            foreach (System.Windows.Window w in app.Windows)
                if (w.IsVisible) { _idleSince = null; _done = false; return; }
            _idleSince ??= DateTime.UtcNow;
            if (_done || DateTime.UtcNow - _idleSince < TimeSpan.FromMinutes(2)) return;
            _done = true;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { Trim(); }
                catch (Exception ex) { Log.Error("idle memory", ex); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }
        catch { }
    }

    private static void Trim()
    {
        using var me = Process.GetCurrentProcess();
        long before = me.WorkingSet64;
        Compact();
        try { K32EmptyWorkingSet(me.Handle); } catch { }
        me.Refresh();
        Log.Write($"Idle: no window open, memory handed back to Windows ({before / 1048576} → {me.WorkingSet64 / 1048576} MB in use)");
    }

    /// <summary>
    /// .NET's memory compacted and the freed part given back to Windows (it
    /// otherwise keeps freed memory for reuse, so a big job left Persocord
    /// looking big until it restarted), and the main heap's free blocks released.
    /// </summary>
    private static void Compact()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        // Only the process's main heap: it's always safe to compact from any thread.
        try { HeapCompact(GetProcessHeap(), 0); } catch { }
    }

    /// <summary>After a big job (the editor closing): its memory goes back to Windows now, not at the next restart.</summary>
    public static void Release(string why)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var me = Process.GetCurrentProcess();
            long before = me.PrivateMemorySize64;
            Compact();
            me.Refresh();
            Log.Write($"Memory: {why}; {before / 1048576} → {me.PrivateMemorySize64 / 1048576} MB kept");
        }
        catch (Exception ex) { Log.Error("release memory", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll")]
    private static extern UIntPtr HeapCompact(IntPtr heap, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool K32EmptyWorkingSet(IntPtr process);
}
