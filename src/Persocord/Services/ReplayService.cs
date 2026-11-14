using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// The replay: the screen is always being recorded into a short loop on
/// disk, and the hotkey keeps the last few seconds.
///
/// Built to cost as little as possible:
///  - the whole screen is captured and encoded on the graphics card (the
///    same Desktop Duplication + hardware encoder route as normal recording,
///    and no hooking into games, so nothing for anti-cheat to object to);
///  - FFmpeg writes two-second pieces in a loop, overwriting the oldest, so
///    disk use stays fixed and memory stays flat;
///  - saving just joins the last pieces without re-encoding, so it's
///    instant; only the combined sound track is encoded, which takes a
///    moment even for minutes of video.
/// </summary>
public sealed class ReplayService : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int SegmentSeconds = 2;

    private readonly SettingsService _settings;
    // Each run of the replay records into a folder of its own inside this one,
    // so a run that's ending (or tidying up after itself) can never touch the
    // pieces of the run that replaced it.
    private readonly string _root = Path.Combine(Paths.Temp, "replay");
    /// <summary>
    /// This launch's own prefix for run folders. Run numbers start over at
    /// each launch, and a folder the last launch couldn't clear on its way
    /// out ("run00001") was reused, old pieces and all: the sound loop then
    /// kept hours-old sound and threw away the new, so saves had sound from
    /// long ago. Each launch's folders now have names of their own.
    /// </summary>
    private readonly string _launch = DateTime.Now.ToString("MMddHHmmss");
    private string _dir = Path.Combine(Paths.Temp, "replay");
    private int _run;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Process? _proc;
    private AudioTracks? _tracks;
    private int _keep;
    private VideoCodec _codec;
    private bool _warnedFormat;
    private bool _warnedVram;
    private bool _behindGames;

    // The sound FFmpeg, and what lines its pieces up with the video's:
    // the video's first frame (real time, µs), and each FFmpeg's own zero.
    private Process? _audioProc;
    private int _lastVideoExit;
    // The sound recorder failed while the picture was fine; noise suppression is dropped if it was the cause.
    private bool _soundBroke, _noDenoise;
    private bool _triedScreenGpuSwitch;
    private bool _checkingSwitch;
    private bool _everRan;

    // Which graphics chip FFmpeg captures through (see CaptureChip): the ones
    // being tried after FFmpeg couldn't find the screen, and where that's at.
    private List<uint> _chipCands = new();
    private int _chipTry = -1;
    private DateTime _chipCycleEnded = DateTime.MinValue;
    private bool _captureMissing;
    private uint _lastSeenVendor, _lastChip;
    private DxgiTarget? _lastSeen;
    private DateTime _lastProbe = DateTime.MinValue;

    /// <summary>
    /// The capture keeps failing: rather than guessing one change at a time,
    /// every combination is tried for a moment (FFmpeg on Windows' chip or the
    /// power-saving one, through each graphics chip) and the first that really
    /// grabs the screen is kept. Nothing changes if none works (the PC locked,
    /// say) or if what's set already works. Returns true when it changed something.
    /// </summary>
    private async Task<bool> FindWorkingCaptureAsync()
    {
        _lastProbe = DateTime.UtcNow;
        var seen = _lastSeen;
        if (seen == null) return false;
        var st = _settings.Current;
        bool was = st.CaptureOnScreenGpu;
        uint wasChip = _lastChip;
        string grab = $"ddagrab=output_idx={seen.Output}:framerate=30,hwdownload,format=bgra";
        foreach (bool powerSaving in new[] { was, !was })
        {
            HybridGpu.SetPowerSaving(powerSaving);
            foreach (var chip in CaptureChip.Candidates(seen.VendorId))
            {
                string dev = DdaDevice(chip == seen.VendorId ? seen.Adapter : 0, chip);
                var (ok, _) = await FFmpegProbe.TryAsync($"{dev}-filter_complex \"{grab}\" -frames:v 5 -f null -");
                if (!ok) continue;
                if (powerSaving == was && chip == wasChip) return false;   // what's set works: it was a passing problem
                st.CaptureOnScreenGpu = powerSaving;
                _settings.Save(st);
                CaptureChip.Learn(seen.DeviceName, chip, seen.VendorId);
                _chipTry = -1;
                _chipCands = new();
                _triedScreenGpuSwitch = true;
                _checkingSwitch = false;
                _plainEncoder.Clear();
                _routeFor.Clear();
                Log.Write($"Replay: the capture kept failing; checked every way to grab the screen. Works with FFmpeg on " +
                          $"{(powerSaving ? "the power-saving chip" : "Windows' choice of chip")}, through the {CaptureChip.Name(chip)} chip: using that.");
                return true;
            }
        }
        HybridGpu.SetPowerSaving(was);
        Log.Write("Replay: checked every way to grab the screen; none works right now (the PC locked or a game switching modes?). Waiting.");
        return false;
    }
    private string _lastScreenName = "";

    /// <summary>The chip the replay captures through right now (vendor id; 0 = FFmpeg's own pick).</summary>
    public uint? CaptureVendor { get; private set; }

    /// <summary>Moves the capture on to the next graphics chip. False once every chip has been tried.</summary>
    private bool NextChip()
    {
        if (_chipTry < 0)
        {
            if ((DateTime.UtcNow - _chipCycleEnded).TotalSeconds < 120) return false;
            CaptureChip.Forget(_lastScreenName);
            _chipCands = CaptureChip.Candidates(_lastSeenVendor);
            _chipTry = Math.Max(0, _chipCands.IndexOf(_lastChip));
        }
        _chipTry++;
        if (_chipTry >= _chipCands.Count)
        {
            _chipTry = -1;
            _chipCands = new();
            _chipCycleEnded = DateTime.UtcNow;
            Log.Write("Replay: FFmpeg couldn't find the screen through any graphics chip.");
            return false;
        }
        uint next = _chipCands[_chipTry];
        Log.Write($"Replay: FFmpeg couldn't find the screen through the {CaptureChip.Name(_lastChip)} chip " +
                  "(on laptops with two chips Windows can show it on a different chip to each app); " +
                  $"trying the {CaptureChip.Name(next)} chip.");
        _plainEncoder.Clear();
        _routeFor.Clear();
        return true;
    }

    /// <summary>Waits 30 seconds before trying again, telling the person once.</summary>
    private async Task PauseAndRetryAsync(CancellationToken ct, string? why = null, int seconds = 30)
    {
        Log.Write($"Replay paused: {why ?? "it couldn't restart (the graphics card may be too busy)"}. Trying again in {seconds} seconds.");
        SetTrouble(why ?? "the graphics card seems too busy right now");
        try { await Task.Delay(seconds * 1000, ct); } catch { }
        CheckTroubleNotice();
    }

    /* ---- when it isn't capturing ------------------------------------------- */

    // Short blips (Windows starting up, a game switching display modes) pass
    // quietly; the clip list and the tray show them. Only a problem that lasts
    // gets a notification, one per problem (and no more than one every ten
    // minutes), and its end gets one too, so nobody is left guessing.
    private const int TroubleNoticeAfterSeconds = 60;
    private readonly object _troubleLock = new();
    private DateTime? _troubleSince;
    private bool _troubleNotified;
    private DateTime _lastTroubleNotice = DateTime.MinValue;

    /// <summary>Why the replay isn't capturing right now (null while it is, or while it's off).</summary>
    public string? Trouble { get; private set; }

    /// <summary>A notification worth showing: title, message.</summary>
    public event Action<string, string>? Notice;

    private void SetTrouble(string why)
    {
        bool changed;
        lock (_troubleLock)
        {
            changed = Trouble != why;
            Trouble = why;
            _troubleSince ??= DateTime.UtcNow;
        }
        if (changed) StateChanged?.Invoke();
        CheckTroubleNotice();
    }

    private void CheckTroubleNotice()
    {
        string? why;
        lock (_troubleLock)
        {
            why = Trouble;
            if (why == null || _troubleNotified || _troubleSince is not DateTime since) return;
            if ((DateTime.UtcNow - since).TotalSeconds < TroubleNoticeAfterSeconds) return;
            if ((DateTime.UtcNow - _lastTroubleNotice).TotalMinutes < 10) return;
            _troubleNotified = true;
            _lastTroubleNotice = DateTime.UtcNow;
        }
        Log.Write("Replay: told the user it isn't capturing: " + why);
        Notice?.Invoke("Replay isn't capturing",
            $"{char.ToUpper(why[0])}{why[1..]}. It stays on and keeps trying; you'll get a note when it's back.");
    }

    /// <summary>Capturing again: said so if the problem was announced.</summary>
    private void ClearTrouble(bool quietly = false)
    {
        bool told, had;
        lock (_troubleLock)
        {
            had = Trouble != null;
            told = _troubleNotified;
            Trouble = null;
            _troubleSince = null;
            _troubleNotified = false;
        }
        if (!had) return;
        StateChanged?.Invoke();
        if (told && !quietly)
        {
            Log.Write("Replay: capturing again");
            Notice?.Invoke("Replay is back", "It's capturing your screen again.");
        }
    }
    private DateTime? _lastSaveAt;

    /// <summary>How many seconds the latest save kept.</summary>
    public int LastSaveSeconds { get; private set; }
    private long? _vfirstUs;
    private double? _p0v, _p0a;
    // What starts on this PC, per encoder and format (H.264 on a chip can take
    // the screen's frames directly where HEVC on the same chip can't).
    private readonly Dictionary<(EncoderKind, VideoCodec), Route> _routeFor = new();
    private readonly HashSet<(EncoderKind, VideoCodec)> _plainEncoder = new();
    private (EncoderKind, VideoCodec) LastKey => (_lastEncoder, _codec);

    /// <summary>The key a working route is remembered under across launches.</summary>
    private string MemoryKey(EncoderKind e, VideoCodec c) => $"{_lastScreenName}|{_lastChip:x4}|{e}|{c}";
    private bool _lastTuned;
    private EncoderKind _lastEncoder;
    private Route _lastRoute;
    private string _ext = "ts";
    private volatile bool _saving;

    public bool IsRunning => _cts != null;

    /// <summary>The picture FFmpeg's last command line, for diagnostics.</summary>
    public string? LastVideoArgs { get; private set; }

    /// <summary>Where the replay's start-up stands, in one line, for diagnostics.</summary>
    public string StateReport() =>
        $"route {RouteName(_lastRoute)}, encoder {_lastEncoder}{(_plainEncoder.Contains(LastKey) ? " (plain settings)" : "")}, " +
        $"last stop: exit {_lastVideoExit}{(_failKind != FailKind.Unknown ? $" ({_failKind.ToString().ToLowerInvariant()} problem)" : "")}, " +
        $"quick restarts in a row: {_quickStreak}, held {SecondsHeld} s, stretch running {(IsRunning ? (DateTime.UtcNow - _runStarted).TotalSeconds : 0):0} s, chip being tried: {(_chipTry >= 0 && _chipTry < _chipCands.Count ? CaptureChip.Name(_chipCands[_chipTry]) : "none")}, " +
        $"trouble: {Trouble ?? "none"}";

    /// <summary>The replay's FFmpegs right now (picture, sound), for measuring what they use.</summary>
    public int[] ProcessIds()
    {
        var ids = new List<int>();
        foreach (var p in new[] { _proc, _audioProc })
            try { if (p != null && !p.HasExited) ids.Add(p.Id); } catch { }
        return ids.ToArray();
    }

    /// <summary>On, off, or gave up after repeated failures.</summary>
    public event Action? StateChanged;
    public event Action<string>? Failed;

    public ReplayService(SettingsService settings) => _settings = settings;

    public void Start()
    {
        if (_cts != null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        // The previous session's loop must be completely finished first: two
        // loops running side by side (a quick restart, e.g. after changing a
        // setting) wrote into the same pieces and saved nonsense.
        var prior = _loop;
        _loop = Task.Run(async () =>
        {
            try { await prior; } catch { }
            await LoopAsync(cts.Token);
        });
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Off at once: the FFmpegs close (both together) and the pieces are
    /// cleared in the background, never holding up the window. A start right
    /// after waits for that to finish first.
    /// </summary>
    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts == null) return;
        cts.Cancel();
        var procs = new[] { _proc, _audioProc };
        var tracks = _tracks;
        _proc = null;
        _audioProc = null;
        _tracks = null;
        lock (_past) _past.Clear();
        _keepSound = false;
        ClearTrouble(quietly: true);
        VideoMemoryNowMb = VideoMemoryPeakMb = -1;
        _planKey = "";
        var previous = _stopping;
        // One after another: a quick off-on-off never lets two sessions overlap.
        _stopping = Task.Run(async () =>
        {
            try { await previous; } catch { }
            Task.WaitAll(procs.Select(p => Task.Run(() => Quit(p))).ToArray());
            try { tracks?.Dispose(); } catch { }
            await CleanOldRunsAsync(keep: null);
        });
        StateChanged?.Invoke();
    }

    private Task _stopping = Task.CompletedTask;
    private Task _loop = Task.CompletedTask;

    /// <summary>Picks up changed settings.</summary>
    public void Restart()
    {
        if (!IsRunning) return;
        Stop();
        _lastSaveAt = null;   // a new loop: nothing saved from it yet
        Start();
    }

    /// <summary>
    /// A new longest save length, while running: the loop simply keeps more
    /// (or fewer) pieces, so what's already recorded stays. (AV1's loop is
    /// trimmed by Persocord too now, so it never needs a restart for this.)
    /// </summary>
    public bool SetLongest(int seconds)
    {
        int keep = KeepFor(seconds);
        _keep = keep;
        Log.Write($"Replay: now keeps up to {seconds}s (no restart needed)");
        return true;
    }

    private static int KeepFor(int seconds) => (int)Math.Ceiling(Math.Clamp(seconds, 5, 3600) / (double)SegmentSeconds) + 4;

    /// <summary>
    /// Deletes the folders of runs that have ended (all but <paramref name="keep"/>),
    /// once no save is reading from them.
    /// </summary>
    private async Task CleanOldRunsAsync(string? keep)
    {
        bool locked = false;
        try
        {
            locked = await _saveLock.WaitAsync(TimeSpan.FromMinutes(3));
            if (!Directory.Exists(_root)) return;
            foreach (var d in Directory.EnumerateDirectories(_root))
            {
                if (keep != null && string.Equals(d, keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(d, _dir, StringComparison.OrdinalIgnoreCase) && IsRunning) continue;
                if (string.Equals(d, _soundDir, StringComparison.OrdinalIgnoreCase) && IsRunning) continue;
                bool kept;
                lock (_past) kept = _past.Any(r => string.Equals(r.Dir, d, StringComparison.OrdinalIgnoreCase));
                if (kept) continue;
                try { Directory.Delete(d, true); } catch { }
            }
            // Pieces from before runs had folders of their own.
            foreach (var f in Directory.EnumerateFiles(_root)) try { File.Delete(f); } catch { }
        }
        catch (Exception ex) { Log.Error("replay cleanup", ex); }
        finally { if (locked) _saveLock.Release(); }
    }

    /* ---- running ---------------------------------------------------------- */

    private async Task LoopAsync(CancellationToken ct)
    {
        // The last session finishes closing before this one begins.
        try { await _stopping; } catch { }
        int quickFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            var began = DateTime.UtcNow;
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { Log.Error("replay", ex); }
            if (ct.IsCancellationRequested) break;

            // The screen was taken over (a game going full screen): straight
            // back on, not counted as a failure. (Unless it keeps happening
            // many times a minute: then the usual handling below takes over.)
            if (_screenChanged)
            {
                _screenChanged = false;
                _screenChanges.Enqueue(DateTime.UtcNow);
                while (_screenChanges.Count > 0 && (DateTime.UtcNow - _screenChanges.Peek()).TotalSeconds > 60) _screenChanges.Dequeue();
                if (_screenChanges.Count <= 10)
                {
                    quickFailures = 0;
                    try { await Task.Delay(300, ct); } catch { break; }
                    continue;
                }
            }

            bool quick = (DateTime.UtcNow - began).TotalSeconds < 15 || _stalled;
            if (_stalled) { _failKind = FailKind.Encoder; _stalled = false; }
            _quickStreak = quick ? _quickStreak + 1 : 0;

            // Only the sound recorder failed, with the mic's noise suppression on:
            // carry on without it rather than blaming the picture.
            if (quick && _soundBroke && !_noDenoise && MicDenoise.Filter(_settings.Current).Length > 0)
            {
                _noDenoise = true;
                Log.Write("Replay: the sound recorder didn't start with the mic's noise suppression; carrying on without it.");
                quickFailures = 0;
                try { await Task.Delay(1000, ct); } catch { break; }
                continue;
            }

            // A switch of graphics chip is only kept if it works: if the very
            // next start fails too, it's undone at once (so a one-off hiccup
            // on a desktop can never leave FFmpeg on the wrong chip).
            if (_checkingSwitch)
            {
                _checkingSwitch = false;
                if (quick)
                {
                    var st2 = _settings.Current;
                    st2.CaptureOnScreenGpu = !st2.CaptureOnScreenGpu;
                    HybridGpu.SetPowerSaving(st2.CaptureOnScreenGpu);
                    _settings.Save(st2);
                    _plainEncoder.Clear();
                    _routeFor.Clear();
                    Log.Write("Replay: switching graphics chip didn't help; switched back.");
                    try { await Task.Delay(1500, ct); } catch { break; }
                    continue;
                }
                Log.Write("Replay: the graphics chip switch works; kept.");
            }

            // The capture keeps failing (three quick failures in a row): every
            // combination of chip and graphics preference is checked once, and
            // one that works is kept. (One guess at a time could settle on a
            // combination that never works: FFmpeg on the power-saving chip of
            // a desktop whose screen is on the graphics card.)
            if (quick && _quickStreak >= 3 && (_captureMissing || _lastVideoExit == ExternalError) &&
                (DateTime.UtcNow - _lastProbe).TotalMinutes >= 2 && await FindWorkingCaptureAsync())
            {
                quickFailures = 0;
                try { await Task.Delay(500, ct); } catch { break; }
                continue;
            }

            // FFmpeg couldn't find the screen on the chip it was pointed at:
            // the other chips are tried first (the one that works is remembered).
            if (quick && _captureMissing && NextChip())
            {
                quickFailures = 0;
                try { await Task.Delay(1000, ct); } catch { break; }
                continue;
            }

            // The screen capture refused ("error in an external library")
            // on a PC with two graphics chips: the screen is wired to the
            // other one. FFmpeg moves to it, once, and it's remembered.
            if (quick && _lastVideoExit == ExternalError && _failKind is FailKind.Capture or FailKind.Unknown &&
                !_triedScreenGpuSwitch && await HasTwoChipsAsync())
            {
                _triedScreenGpuSwitch = true;
                _checkingSwitch = true;
                var st = _settings.Current;
                bool on = !st.CaptureOnScreenGpu;
                st.CaptureOnScreenGpu = on;
                HybridGpu.SetPowerSaving(on);
                _settings.Save(st);
                _plainEncoder.Clear();
                _routeFor.Clear();
                Log.Write(on
                    ? "Replay: the screen capture was refused. This PC has two graphics chips and the screen is wired to the integrated one, so FFmpeg now runs there and records with its encoder."
                    : "Replay: capturing through the integrated chip didn't work either; back to Windows' choice.");
                quickFailures = 0;
                try { await Task.Delay(1500, ct); } catch { break; }
                continue;
            }
            // The screen couldn't be captured at all just now (Windows still
            // starting up, the PC locked, a game switching display modes):
            // that says nothing about the encoder or route, so nothing is
            // stepped down. It waits a little longer each time and tries again.
            // (Several in a row, though, and it may be the encoder after all:
            // then the usual stepping down below gets its turn, every few tries.)
            if (!quick) _cutStreak = 0;
            // (Only when it's the capture that failed: a resize or encoder
            // failure is stepped down at once, below, rather than waited out.)
            // A capture refusal (access denied while Windows shows a secure
            // screen, say) is only ever waited out; an unexplained one gets the
            // usual stepping down every few tries.
            if (quick && (_failKind == FailKind.Capture || (_lastVideoExit == ExternalError && _failKind == FailKind.Unknown)) &&
                (++_cutStreak % 6 != 0 || _failKind == FailKind.Capture))
            {
                int wait = Math.Min(30, 2 * _cutStreak);
                if (_cutStreak == 1 || _cutStreak % 10 == 0)
                    Log.Write($"Replay: the screen can't be captured right now (Windows starting, the PC locked, or a game switching modes); trying again in {wait} s.");
                SetTrouble("the screen can't be captured right now (the PC locked, Windows starting, or a game switching display modes)");
                try { await Task.Delay(wait * 1000, ct); } catch { break; }
                CheckTroubleNotice();
                continue;
            }
            if (quick && _lastTuned && _failKind != FailKind.Filter && _plainEncoder.Add(LastKey))
            {
                Log.Write($"Replay: the tuned {_lastEncoder} settings didn't start; using the plain ones");
                quickFailures++;
                try { await Task.Delay(3000, ct); } catch { break; }
                continue;
            }
            // A route that won't start moves this encoder on to the next one
            // (resize on the card → full size on the card → CPU), and the one
            // that works is kept for the rest of the session.
            if (quick && _lastRoute == Route.GpuScale)
            {
                _routeFor[LastKey] = Route.GpuFull;
                Log.Write($"Replay: the {RouteName(Route.GpuScale)} route didn't start with {_lastEncoder}; trying {RouteName(Route.GpuFull)}");
            }
            else if (quick && _lastRoute == Route.GpuFull && _everRan && _failKind is not (FailKind.Encoder or FailKind.Filter))
            {
                // It worked earlier, so the setup is fine: most likely the
                // graphics card is too busy right now (a heavy game on an older
                // laptop). Paused, and tried again every 30 seconds.
                await PauseAndRetryAsync(ct);
                quickFailures = 0;
                continue;
            }
            else if (quick && _lastRoute == Route.GpuFull && _lastEncoder != EncoderKind.X264 &&
                     !(_routeFor.TryGetValue(LastKey, out var was) && was == Route.Cpu))
            {
                // Still on the graphics card's encoder, with the frames copied
                // over to it through memory (some setups won't hand them across directly).
                _routeFor[LastKey] = Route.Cpu;
                Log.Write($"Replay: the {RouteName(Route.GpuFull)} route didn't start with {_lastEncoder}; trying with {RouteName(Route.Cpu)}");
            }
            else if (quick && (_lastRoute == Route.GpuFull || (_lastRoute == Route.Cpu && _lastEncoder != EncoderKind.X264)))
            {
                // Never down to the CPU for a graphics-card encoder: that's
                // what made replays choppy. Stop and say why instead.
                // It stays on: paused a minute, then every route is tried
                // afresh (a driver update or a busy card often clears it).
                Log.Write("Replay: neither graphics-card route starts right now (it never falls back to the CPU). See the lines above.");
                await PauseAndRetryAsync(ct, "the graphics card's encoder wouldn't start (Settings → Replay → Quality can pick another)", 60);
                _routeFor.Remove(LastKey);
                _plainEncoder.Remove(LastKey);
                RouteMemory.Forget(MemoryKey(_lastEncoder, _codec));
                quickFailures = 0;
                continue;
            }
            if (!quick) _everRan = true;
            quickFailures = quick ? quickFailures + 1 : 0;
            if (quickFailures >= 5 && _everRan)
            {
                await PauseAndRetryAsync(ct);
                quickFailures = 0;
                continue;
            }
            if (quickFailures >= 5)
            {
                // Never switched off by itself: someone who turned it on wants
                // it on. Paused a minute, then tried again.
                Log.Write("Replay keeps failing to start. See the lines above.");
                await PauseAndRetryAsync(ct, "it keeps failing to start (the log says why)", 60);
                quickFailures = 0;
                continue;
            }
            if (quick) SetTrouble("it's having trouble starting (the log says why)");
            try { await Task.Delay(3000, ct); } catch { break; }
            CheckTroubleNotice();
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // A save in progress finishes first: it reads this run's state.
        try { await _saveLock.WaitAsync(ct); } catch (OperationCanceledException) { return; }
        _saveLock.Release();
        var s = _settings.Current;
        var screen = PickScreen(s);
        if (screen == null) { Log.Write("Replay: no screen found"); await Task.Delay(5000, ct); return; }
        // The chip FFmpeg captures through: one being tried, one that worked
        // before, or the one Persocord sees the screen on.
        _lastScreenName = screen.DeviceName;
        _lastSeenVendor = screen.VendorId;
        _lastSeen = screen;
        if (_chipTry >= 0 && _chipTry < _chipCands.Count)
        {
            uint v = _chipCands[_chipTry];
            if (v != screen.VendorId) screen = screen with { VendorId = v, Adapter = 0 };
        }
        else screen = CaptureChip.Apply(screen);
        _lastChip = screen.VendorId;
        _captureMissing = false;
        _failKind = FailKind.Unknown;
        uint chipNow = screen.VendorId;
        string screenNow = screen.DeviceName;

        // The replay's own format and encoder (separate from recordings').
        var (encoder, codec) = await EncoderDetector.ResolveAsync(s.ReplayEncoder, s.ReplayCodec);
        if (s.CaptureOnScreenGpu)
        {
            // FFmpeg runs on the chip that drives the screen, so it encodes
            // there too: frames never have to cross to the other card.
            var here = await EncoderDetector.AvailableForAsync(codec);
            var h264 = await EncoderDetector.AvailableForAsync(VideoCodec.H264);
            if (here.Contains(EncoderKind.Qsv)) encoder = EncoderKind.Qsv;
            else if (h264.Contains(EncoderKind.Qsv)) { encoder = EncoderKind.Qsv; codec = VideoCodec.H264; }
            else if (here.Contains(EncoderKind.Amf)) encoder = EncoderKind.Amf;
        }
        // Laptops: the screen is often wired to the built-in chip (Intel or
        // AMD) while the encoder picked is the separate card's. A graphics-card
        // encoder can only take frames from its own chip, which is why the
        // replay wouldn't start on such laptops. On "Automatic", the screen's
        // own chip encodes (no copying at all); with an encoder chosen by
        // hand, the frames are copied over to it through memory.
        bool crossChip = encoder != EncoderKind.X264 && screen.VendorId != 0 && !MadeBy(encoder, screen.VendorId);
        if (crossChip && s.ReplayEncoder == EncoderKind.Auto && OwnEncoder(screen.VendorId) is { } own)
        {
            var here = await EncoderDetector.AvailableForAsync(codec);
            var h264 = await EncoderDetector.AvailableForAsync(VideoCodec.H264);
            if (here.Contains(own)) { encoder = own; crossChip = false; }
            else if (h264.Contains(own)) { encoder = own; codec = VideoCodec.H264; crossChip = false; }
        }
        if (crossChip && !_saidCrossChip)
        {
            _saidCrossChip = true;
            Log.Write($"Replay: the screen is on the {ChipName(screen.VendorId)} chip and {encoder} is on another; frames are copied across to it");
        }
        if (codec != s.ReplayCodec && !_warnedFormat)
        {
            // Say so once, rather than failing over and over.
            _warnedFormat = true;
            Failed?.Invoke($"{EncoderDetector.CodecName(s.ReplayCodec)} isn't available on this PC's encoders, so the replay " +
                           $"is using {EncoderDetector.CodecName(codec)}. Settings → Replay → Quality shows what works here.");
        }
        // Graphics-card encoders take the screen's frames directly, at full
        // size: the encoder converts the colours itself, correctly. (Resizing
        // on the card first came out with the wrong colour range: dark, with
        // crushed blacks and harsh contrast.)
        bool wantSmaller = s.ReplayHeight > 0 && s.ReplayHeight < screen.Desktop.Height;
        // What worked here before (this screen, chip, encoder and format) is
        // where it starts, rather than failing through the same steps every launch.
        if (!_routeFor.ContainsKey((encoder, codec)) && RouteMemory.Get(MemoryKey(encoder, codec)) is { } mem)
        {
            _routeFor[(encoder, codec)] = (Route)mem.Route;
            if (mem.Plain) _plainEncoder.Add((encoder, codec));
        }
        Route? known = _routeFor.TryGetValue((encoder, codec), out var r0) ? r0 : null;
        var route = encoder == EncoderKind.X264 || crossChip || known == Route.Cpu ? Route.Cpu
                  : wantSmaller && known != Route.GpuFull ? Route.GpuScale
                  : Route.GpuFull;
        bool tuned = !_plainEncoder.Contains((encoder, codec));
        _lastTuned = tuned;
        bool gpu = route != Route.Cpu;
        _codec = codec;
        _lastEncoder = encoder;
        _lastRoute = route;
        string memKey = MemoryKey(encoder, codec);

        int w = screen.Desktop.Width, h = screen.Desktop.Height;
        // The "full size" route skips resizing altogether.
        int outH = route != Route.GpuFull && s.ReplayHeight > 0 && s.ReplayHeight < h ? s.ReplayHeight : h;
        int outW = Math.Max(2, (int)Math.Round(w * (double)outH / h) & ~1);
        outH &= ~1;
        bool scaled = outW != w || outH != h;
        int fps = Math.Clamp(s.ReplayFps, 15, 240);

        // Sound: all PC audio, a track per app playing sound, and the mic.
        // After the screen was taken over (only the picture stopped), the
        // sound recorder is still going: it carries straight on, every track
        // in place, and the picture picks up beside it.
        bool reuseSound = false;
        try { reuseSound = _keepSound && _audioProc is { HasExited: false } && _tracks != null && !ct.IsCancellationRequested; } catch { }
        _keepSound = false;
        long gateUs;
        if (reuseSound)
        {
            gateUs = _gateUs;
            Log.Write("Replay: the sound carries on; only the picture restarts");
        }
        else
        {
            // A new sound session: what came before can't be joined to it.
            if (_audioProc != null) { Quit(_audioProc); _audioProc = null; }
            lock (_past) _past.Clear();
            _tracks?.Dispose();
            _tracks = await AudioTracks.CreateAsync(s,
                pc: s.ReplayTrackPc, apps: s.ReplayTrackPc && s.AppTracks && !s.ReplayLight, mic: s.ReplayTrackMic,
                micOn: s.ReplayTrackMic, pcMuted: false);
            // Sound starts now; the moment is noted, and so is the moment the
            // video's first frame arrives (below). Saves line the two up from
            // those, so the video's warm-up never shows as an offset.
            _tracks.OpenGate();
            gateUs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        }
        int trackCount = _tracks!.Tracks.Count;   // set above, or kept from the running sound session

        // The loop on disk: numbered pieces, oldest deleted as new ones come.
        // (Reusing a fixed set of names made the order unreliable: a save
        // could stitch together pieces from minutes apart.) Each run has a
        // folder of its own; the ones before it are cleared away.
        if (ct.IsCancellationRequested) { KillCurrent(); return; }
        // Earlier stretches only join this one if they look the same (a game that
        // changes the screen's size starts a fresh replay).
        _sig = $"{codec}|{outW}x{outH}|{fps}";
        lock (_past) _past.RemoveAll(r => r.Sig != _sig);
        int run = ++_run;
        var dir = Path.Combine(_root, $"run{_launch}-{run:D4}");
        // Never anything left in it from before (belt and braces).
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        _dir = dir;
        // A new sound session records its pieces into this first stretch's folder, for the whole session.
        if (!reuseSound) _soundDir = dir;
        var soundDir = _soundDir;
        _ = CleanOldRunsAsync(keep: dir);
        // Enough for the longest save setup.
        _keep = KeepFor(s.ReplayLongest());

        // Picture and sound are recorded by two separate FFmpegs. In one,
        // FFmpeg keeps its streams in step with each other: changing the
        // video's timestamps to line it up with the sound starved the
        // capture (choppy, mostly repeated frames), and leaving them made the
        // sound run seconds ahead, so saves went silent at the end.
        //
        // The video is captured exactly as it always was. Its timestamps are
        // untouched: the pass-through below only prints the real time of the
        // first frame (RTCTIME, on the same clock as the sound's start), once.
        // Grabbed faster than the video's rate, then exactly fps kept (see
        // CaptureChip.GrabRate): frames no longer come out shown twice. The
        // first frame's real time is printed before that step, at the moment
        // it was captured, and it stays the video's first frame.
        string grab = $"ddagrab=output_idx={screen.Output}:framerate={CaptureChip.GrabRate(fps)}:draw_mouse={(s.ReplayCursor ? 1 : 0)}," +
                      "setpts='PTS+0*if(eq(N\\,0)\\,print(RTCTIME)\\,0)'" +
                      $",fps={fps}";
        _gateUs = gateUs;
        _fps = fps;
        _vfirstUs = null;
        _p0v = null;
        if (!reuseSound) _p0a = null;
        _lastHealth = DateTime.UtcNow;
        _pFrame = 0;
        _runStarted = DateTime.UtcNow;
        string chain = route switch
        {
            // Resized and turned into the encoder's own format by the card's
            // video processor (the part that plays videos, not the 3D engine
            // games use), without the frame ever leaving the graphics card.
            // Resized by the card's video processor, labelled with what it
            // really produces: full-range colour on the BT.601 scale. (It was
            // labelled limited-range BT.709 before, which made replays dark
            // and harsh.) The alternative labelling is a setting, in case a
            // driver's resizer differs.
            // The graphics card captures and resizes. Its resizer gives
            // full-range colour, which Windows' player understands but VLC,
            // Discord and phones often don't (harsh, dark). So the small,
            // already-resized image is brought over once and its range set
            // to the standard one (black 16, white 235): one light pass,
            // well under 1% of the CPU at 1080p60, then into the encoder.
            Route.GpuScale => grab + $",scale_d3d11=width={outW}:height={outH}:format=nv12,hwdownload,format=nv12," +
                              (s.ReplayScaleFullRange
                                  ? "setparams=range=pc,scale=in_range=pc:out_range=tv,format=nv12," +
                                    "setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709"
                                  : "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709"),
            // The screen's frames straight into the encoder, at full size.
            Route.GpuFull => grab + QsvMap(encoder),
            // Last resort: through the CPU.
            _ => grab + $",hwdownload,format=bgra,scale={outW}:{outH}:flags=bilinear:out_color_matrix=bt709:out_range=tv,format=nv12",
        };

        // The main sound (all PC audio + mic) is mixed as it records, so a
        // save is a plain copy with no sound to encode, however long it is.
        // The mic goes through the AI noise suppression first (into the mix and its own track).
        var parts = new List<string>();
        int mic = _tracks.MicIndex;
        string denoise = _noDenoise ? "" : MicDenoise.Filter(s);
        string micIn = mic >= 0 ? $"[{mic}:a]" : "", micMap = mic >= 0 ? $"-map {mic}:a " : "";
        string mixFilter = "", mixMap = "";
        var mixIn = _tracks.Tracks.Select((t, i) => (t, i))
            .Where(x => x.t.Kind is TrackKind.Pc or TrackKind.Mic).Select(x => x.i).ToList();
        if (mixIn.Count == 0) mixIn = Enumerable.Range(0, trackCount).ToList();
        if (mic >= 0 && denoise.Length > 0)
        {
            bool inMix = trackCount >= 2 && mixIn.Contains(mic);
            parts.Add(inMix ? $"[{mic}:a]{denoise},asplit=2[micm][mico]" : $"[{mic}:a]{denoise}[mico]");
            if (inMix) micIn = "[micm]";
            micMap = "-map \"[mico]\" ";
        }
        if (trackCount >= 2)
        {
            string In(int i) => i == mic ? micIn : $"[{i}:a]";
            mixFilter = mixIn.Count == 1
                ? $"{In(mixIn[0])}anull[mix]"
                : $"{string.Concat(mixIn.Select(In))}amix=inputs={mixIn.Count}:normalize=0:dropout_transition=0[mix]";
            mixMap = "-map \"[mix]\" ";
            parts.Add(mixFilter);
        }
        mixFilter = string.Join(";", parts);
        var maps = string.Concat(Enumerable.Range(0, trackCount).Select(i => i == mic ? micMap : $"-map {i}:a "));

        // The sound FFmpeg: every track (the mix first) into its own pieces,
        // MPEG-TS whatever the video format. Each sound frame is written the
        // moment it's ready, so a save has sound right up to the end.
        string? audioArgs = trackCount == 0 ? null :
            $"-hide_banner -loglevel warning {_tracks.InputArgs()}" +
            (mixFilter.Length > 0 ? $"-filter_complex \"{mixFilter}\" " : "") +
            // Lighter on the CPU: FFmpeg's quicker AAC search (about half the
            // CPU per track). Otherwise its default, most careful one.
            $"{mixMap}{maps}-c:a aac {(s.ReplayLight ? "-aac_coder fast " : "")}-b:a 160k " +
            $"-f segment -segment_time {SegmentSeconds} -segment_format mpegts " +
            "-segment_format_options flush_packets=1:pes_payload_size=0 -reset_timestamps 0 " +
            "-y aud%08d.ts";

        // Pieces: MPEG-TS for H.264 and HEVC (joins byte for byte, even the
        // piece still being written). TS can't carry AV1, so AV1 goes in
        // Matroska pieces, flushed often so the newest one is readable too.
        // AV1 can't go in MPEG-TS, so it uses fragmented MP4 pieces: all of
        // them share one header, and each keeps its real timestamps, so they
        // too join byte for byte. (Joining Matroska pieces through a list
        // stretched a replay's timeline, which showed as gaps and drifting
        // sound.)
        bool fmp4 = codec == VideoCodec.Av1;
        _ext = fmp4 ? "m4s" : "ts";
        // The playlist lists up to the longest a replay can be; Persocord
        // deletes pieces past the length wanted (as it does for TS), so the
        // length can change without restarting. A piece or the playlist that
        // Windows holds for a moment (an antivirus scan, say) is skipped
        // rather than stopping the whole capture.
        string output = fmp4
            ? $"-f hls -hls_segment_type fmp4 -hls_time {SegmentSeconds} -hls_list_size {KeepFor(3600)} -ignore_io_errors 1 " +
              "-hls_flags independent_segments -hls_fmp4_init_filename init.mp4 " +
              // FFmpeg runs inside the replay folder (see WorkingDirectory),
              // so the shared header lands next to the pieces. On Windows it
              // was written elsewhere, and every AV1 save failed to find it.
              "-hls_segment_filename seg%08d.m4s -y live.m3u8"
            : $"-f segment -segment_time {SegmentSeconds} -segment_format mpegts -reset_timestamps 0 " +
              $"-y \"{Path.Combine(dir, "seg%08d.ts")}\"";

        var args =
            // Progress on stdout every 2 s: how the replay is keeping up.
            // Info level, for the first frame's printed time.
            "-hide_banner -loglevel info -nostats -stats_period 2 -progress pipe:1 " +
            // FFmpeg captures from the first graphics chip unless told otherwise,
            // and a screen wired to another chip is invisible from there (or
            // black). The chip is named by its maker, not its number: on a
            // laptop with two chips Windows can number them differently for
            // FFmpeg than for Persocord.
            DdaDevice(screen.Adapter, screen.VendorId) +
            $"-filter_complex \"{chain}[v]\" -map \"[v]\" " +
            EncoderArgs(encoder, codec, s, outW, outH, fps, tuned) +
            // Stated outright: NVIDIA's AV1 encoder refuses frames whose
            // colour format is left unspecified. They match the frames.
            (route == Route.GpuScale && s.ReplayScaleFullRange
                ? "-color_range tv -colorspace smpte170m -color_primaries bt709 -color_trc bt709 "
                : "-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 ") +
            // A keyframe every two seconds, forced: the loop can only split at
            // keyframes, and some encoders ignore -g on its own.
            $"-g {fps * SegmentSeconds} -force_key_frames \"expr:gte(t,n_forced*{SegmentSeconds})\" " +
            output;

        Log.Write($"Replay on: {screen.DeviceName} {w}x{h} → {outW}x{outH} @ {fps}, " +
                  $"{EncoderDetector.CodecName(codec)} with {EncoderDetector.FFmpegName(encoder, codec)} via {RouteName(route)}" +
                  (encoder == EncoderKind.Nvenc ? (tuned ? ", tuned" : ", plain settings") : "") + ", " +
                  $"{s.ReplaySeconds}s, {trackCount} sound tracks{(s.AppTracks ? $" (up to {s.MaxAppTracks} apps)" : "")}");
        Log.Write("ffmpeg (video) " + args);
        LastVideoArgs = args;

        // Sound first: it's already flowing from the gate (or, after a screen change, still going).
        Process? aproc = reuseSound ? _audioProc : null;
        if (!reuseSound) _soundErrors = new StringBuilder();
        var aerrors = _soundErrors;
        if (audioArgs != null && !reuseSound)
        {
            Log.Write("ffmpeg (sound) " + audioArgs);
            aproc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Paths.FFmpeg,
                    Arguments = audioArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    WorkingDirectory = soundDir,
                },
            };
            aproc.ErrorDataReceived += (_, e) => { if (e.Data != null && aerrors.Length < 4000) aerrors.AppendLine(e.Data); };
            // Switched off (or restarted) while this run was getting ready: it never starts.
            if (ct.IsCancellationRequested) { aproc.Dispose(); KillCurrent(); return; }
            aproc.Start();
            _audioProc = aproc;
            try { aproc.PriorityClass = ProcessPriorityClass.Normal; } catch { }
            ChildProcess.KeepAwake(aproc);
            ResourceGuard.Contain(aproc, s);
            if (s.ReplayLight) try { aproc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            aproc.BeginErrorReadLine();
        }

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Paths.FFmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = dir,
            },
        };
        // Info level is chatty (a line per piece), so only the latest lines
        // are kept for when something goes wrong.
        var errors = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (_vfirstUs == null && run == _run)
            {
                var m = System.Text.RegularExpressions.Regex.Match(e.Data, @"\[Eval @ [^\]]+\] (\d{13,})");
                if (m.Success && long.TryParse(m.Groups[1].Value, out var us))
                {
                    _vfirstUs = us;
                    CaptureVendor = chipNow;
                    RouteMemory.Set(memKey, (int)route, !tuned);
                    if (_chipTry >= 0)
                    {
                        // Frames are coming: this chip sees the screen. Kept for next time.
                        CaptureChip.Learn(screenNow, chipNow, _lastSeenVendor);
                        Log.Write($"Replay: capturing through the {CaptureChip.Name(chipNow)} chip works; remembered.");
                        _chipTry = -1;
                        _chipCands = new();
                    }
                    Log.Write($"Replay video's first frame came {(us - _gateUs) / 1e6:0.000}s after its sound started");
                    return;
                }
            }
            // The loop's own note for every piece it writes ("Opening 'seg….m4s'
            // for writing", twice every 2 s) isn't a reason for anything, and it
            // pushed the real one (why the capture stopped) out of the log.
            if (e.Data.Contains("for writing", StringComparison.Ordinal) && e.Data.Contains("Opening '", StringComparison.Ordinal)) return;
            lock (errors)
            {
                errors.AppendLine(e.Data);
                if (errors.Length > 8000) errors.Remove(0, errors.Length - 4000);
            }
        };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnProgress(e.Data); };
        if (ct.IsCancellationRequested) { proc.Dispose(); KillCurrent(); return; }
        proc.Start();
        _proc = proc;
        // Normal priority: below normal let a busy game starve the capture,
        // which showed up as dropped frames. The work itself is on the GPU.
        try { proc.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        ChildProcess.KeepAwake(proc);
        // Hard limits on CPU and RAM, and the game first on the graphics card.
        ResourceGuard.Contain(proc, s);
        // Lighter on the CPU: behind every other program (the encoding itself
        // is on the graphics card; this is the copying and sound around it).
        if (s.ReplayLight) try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        // Delete pieces older than the replay needs.
        bool stallChecked = false;
        using var prune = new Timer(_ =>
        {
          // Nothing may escape a timer's thread: it would end Persocord.
          try
          {
            if (run != _run) return;
            Prune();
            // Frames are being encoded, yet the loop never gets past its first
            // piece: the encoder isn't making the keyframes pieces are cut at,
            // so nothing could ever be saved. Said, and handled as a failed start.
            double age = (DateTime.UtcNow - _runStarted).TotalSeconds;
            if (!stallChecked && age > 20 && _pFrame > fps * 15)
            {
                stallChecked = true;
                int made = Pieces(dir, _ext).Count;
                if (made <= 1)
                {
                    Log.Write($"Replay: {_pFrame} frames encoded in {age:0} s but the loop is still on its first piece " +
                              $"({EncoderDetector.FFmpegName(encoder, codec)} isn't making keyframes here); trying another way.");
                    _stalled = true;
                    try { proc.Kill(); } catch { }
                    return;
                }
            }
            // Pieces are being written: it's capturing (again).
            if (Trouble != null && HeldSeconds(CurrentRun()) > 0) ClearTrouble();
          }
          catch (Exception ex) { Log.Error("replay prune", ex); }
        }, null, 4000, 4000);

        // Each FFmpeg's own zero: the timestamp of its very first piece,
        // read once that piece is complete (when the second one appears).
        _ = Task.Run(async () =>
        {
            try
            {
                // Only ever for this run: a measurement finishing after a
                // restart must never land on the next run (its sound would
                // be lined up against the wrong zero).
                for (int i = 0; i < 100 && !ct.IsCancellationRequested && run == _run && (_p0v == null || (aproc != null && _p0a == null)); i++)
                {
                    await Task.Delay(500, ct);
                    if (_p0v == null)
                    {
                        string? vin = _ext == "m4s"
                            ? (FinishedFmp4(dir).FirstOrDefault() is string f0 && Path.GetFileName(f0) == "seg00000000.m4s"
                                ? $"-i \"concat:{Path.Combine(dir, "init.mp4")}|{f0}\"" : null)
                            : File.Exists(Path.Combine(dir, "seg00000001.ts")) ? $"-i \"{Path.Combine(dir, "seg00000000.ts")}\"" : null;
                        if (vin != null && await FirstPacketAsync(vin, "v") is { } v && run == _run) _p0v = v.Pts;
                    }
                    if (aproc != null && _p0a == null && File.Exists(Path.Combine(soundDir, "aud00000001.ts")) &&
                        await FirstPacketAsync($"-i \"{Path.Combine(soundDir, "aud00000000.ts")}\"", "a") is { } a && run == _run)
                        _p0a = a.Pts;
                }
            }
            catch { }
        }, ct);

        // Video memory has no hard cap in Windows, so it's watched: over the
        // limit on two checks in a row, the replay restarts (freeing it all).
        int over = 0, ticks = 0;
        _behindGames = false;   // a new recorder starts at normal priority
        _planKey = PlanKey(screen.DeviceName, outH, codec, encoder, route == Route.Cpu);
        VideoMemoryNowMb = VideoMemoryPeakMb = -1;
        double rawGuess = RawVideoMemoryMb(w, h, outH, codec, encoder, route == Route.Cpu, tuned);
        using var vram = new Timer(_ =>
        {
            try
            {
                if (proc.HasExited) return;
                var st = _settings.Current;

                // Normal priority on the graphics card, unless its memory is
                // nearly full: then recording steps behind the game until it
                // isn't (with a little room, so it doesn't flip back and forth).
                if (st.SafetyGpuAdaptive && screen.VideoMemoryBytes > 0)
                {
                    double totalMb = screen.VideoMemoryBytes / 1048576.0;
                    double usedMb = ResourceGuard.CardMemoryUsedMb();
                    double full = Math.Clamp(st.SafetyVramFullPercent, 50, 100) / 100.0;
                    if (!_behindGames && usedMb >= totalMb * full)
                    {
                        _behindGames = true;
                        ResourceGuard.SetGpuPriority(proc, true);
                        Log.Write($"Video memory {usedMb / 1024:0.0} of {totalMb / 1024:0.0} GB: recording steps behind the game");
                    }
                    else if (_behindGames && usedMb < totalMb * (full - 0.05))
                    {
                        _behindGames = false;
                        ResourceGuard.SetGpuPriority(proc, false);
                        Log.Write($"Video memory back to {usedMb / 1024:0.0} GB: recording at normal priority again");
                    }
                }

                double mb = ResourceGuard.VideoMemoryMb(proc.Id);
                if (run == _run && mb > 0)
                {
                    VideoMemoryNowMb = mb;
                    VideoMemoryPeakMb = Math.Max(VideoMemoryPeakMb, mb);
                    // Once it has settled (the encoder's queue full), what it
                    // really uses tunes the estimate in Settings to this PC.
                    if (++ticks >= 4 && route != Route.Cpu && rawGuess > 0)
                        LearnFit(VideoMemoryPeakMb / rawGuess, ticks % 12 == 4);
                }
                int cap = Math.Clamp(_settings.Current.SafetyVramMb, 128, 8192);
                over = mb > cap ? over + 1 : 0;
                if (over >= 2)
                {
                    Log.Write($"Replay was using {mb:0} MB of video memory (limit {cap} MB); restarting it");
                    if (!_warnedVram)
                    {
                        _warnedVram = true;
                        Failed?.Invoke($"The replay used more video memory than its {cap} MB limit, so it restarted to free it. " +
                                       "The limit can be changed in Settings → Replay → Safety limits.");
                    }
                    try { proc.Kill(); } catch { }
                }
            }
            catch { }
        }, null, 10000, 5000);

        // Either FFmpeg stopping ends this run; both restart together.
        try
        {
            var waits = new List<Task> { proc.WaitForExitAsync(ct) };
            if (aproc != null) waits.Add(aproc.WaitForExitAsync(ct));
            await Task.WhenAny(waits);
        }
        catch (OperationCanceledException) { }
        try { _lastVideoExit = proc.HasExited ? proc.ExitCode : 0; } catch { _lastVideoExit = 0; }
        try { _soundBroke = aproc != null && aproc.HasExited && aproc.ExitCode != 0 && !proc.HasExited; } catch { _soundBroke = false; }

        if (!ct.IsCancellationRequested)
        {
            string why;
            lock (errors) why = errors.ToString().Trim();
            _captureMissing = proc.HasExited && CaptureChip.OutputMissing(why);
            _failKind = proc.HasExited ? Classify(why) : FailKind.Unknown;
            // Whichever stopped first says why (both, if both stopped).
            if (aproc != null && aproc.HasExited)
                Log.Write($"Replay sound stopped (exit {SafeExit(aproc)}): {aerrors.ToString().Trim()}");
            if (proc.HasExited || aproc == null || !aproc.HasExited)
                Log.Write($"Replay video stopped (exit {SafeExit(proc)}" +
                          (_failKind != FailKind.Unknown ? $", {_failKind.ToString().ToLowerInvariant()} problem" : "") + $"): {why}");
        }
        // Windows cuts the screen capture off when a game takes the screen
        // over (going full screen, or being tabbed back into). That isn't a
        // failure: the picture carries straight on in a new stretch while the
        // sound keeps recording untouched, and this stretch is kept so saves
        // still reach back past that moment, every track included.
        _screenChanged = false;
        bool soundAlive = false;
        try { soundAlive = aproc != null && !aproc.HasExited; } catch { }
        if (!ct.IsCancellationRequested && _lastVideoExit == ExternalError && soundAlive &&
            (DateTime.UtcNow - _runStarted).TotalSeconds >= 3 && HeldSeconds(CurrentRun()) > 0)
        {
            _screenChanged = true;
            _keepSound = true;
            var ended = CurrentRun();
            ended.Ended = DateTime.UtcNow;
            lock (_past)
            {
                _past.Add(ended);
                // Only as far back as the longest save could reach.
                double reach = _settings.Current.ReplayLongest() + 30;
                _past.RemoveAll(r => (DateTime.UtcNow - r.Ended).TotalSeconds > reach);
                while (_past.Count > 12) _past.RemoveAt(0);
            }
            string why2;
            lock (errors) why2 = errors.ToString();
            Log.Write(WriteFailed(why2)
                ? "Replay: a piece couldn't be written for a moment (Windows or an antivirus held the file); " +
                  "the picture carries on in a new stretch, the sound never stopped."
                : "Replay: the screen capture was cut off (a game going full screen, or being tabbed into); " +
                  "the picture carries on in a new stretch, the sound never stopped.");
            Quit(proc);
            if (_proc == proc) _proc = null;
            return;
        }
        // Only this run's own FFmpegs (a newer run may already be going).
        if (_proc == proc && _audioProc == aproc) KillCurrent();
        else { Quit(proc); if (!reuseSound || _audioProc != aproc) Quit(aproc); }
    }

    private bool _screenChanged;
    // The last run encoded frames but never finished a piece (no keyframes).
    private volatile bool _stalled;
    // Starts in a row that ended within seconds (the replay restarting rather than recording).
    private int _quickStreak;

    /// <summary>Which part of the picture's FFmpeg failed: the screen capture, the resize/handover on the card, or the encoder.</summary>
    private enum FailKind { Unknown, Capture, Filter, Encoder }
    private FailKind _failKind;

    /// <summary>FFmpeg couldn't write a piece (not just its usual "Opening … for writing" line).</summary>
    private static bool WriteFailed(string errors) =>
        errors.Split('\n').Any(l => l.Contains("for writing", StringComparison.OrdinalIgnoreCase) &&
            (l.Contains("fail", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
             l.Contains("denied", StringComparison.OrdinalIgnoreCase) || l.Contains("could not", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Reads FFmpeg's last words for which part gave up first.</summary>
    private static FailKind Classify(string errors)
    {
        static bool Bad(string l) =>
            l.Contains("fail", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("unsupported", StringComparison.OrdinalIgnoreCase) || l.Contains("Impossible", StringComparison.Ordinal);
        var lines = errors.Split('\n').Where(Bad).ToList();
        if (lines.Any(l => l.Contains("ddagrab", StringComparison.OrdinalIgnoreCase))) return FailKind.Capture;
        if (lines.Any(l => l.Contains("scale_d3d11", StringComparison.OrdinalIgnoreCase) || l.Contains("hwmap", StringComparison.OrdinalIgnoreCase) ||
                           l.Contains("hwdownload", StringComparison.OrdinalIgnoreCase) || l.Contains("Parsed_scale", StringComparison.OrdinalIgnoreCase) ||
                           l.Contains("auto_scale", StringComparison.OrdinalIgnoreCase) || l.Contains("convert between the formats", StringComparison.OrdinalIgnoreCase)))
            return FailKind.Filter;
        if (lines.Any(l => l.Contains("encoder", StringComparison.OrdinalIgnoreCase) || l.Contains("[enc:", StringComparison.Ordinal) ||
                           l.Contains("nvenc", StringComparison.OrdinalIgnoreCase) || l.Contains("_qsv", StringComparison.OrdinalIgnoreCase) ||
                           l.Contains("_amf", StringComparison.OrdinalIgnoreCase)))
            return FailKind.Encoder;
        return FailKind.Unknown;
    }
    private int _cutStreak;
    private StringBuilder _soundErrors = new();
    private readonly Queue<DateTime> _screenChanges = new();

    /// <summary>How frames get from the screen to the encoder, best first.</summary>
    private enum Route { GpuScale, GpuFull, Cpu }

    private static string RouteName(Route r) => r switch
    {
        Route.GpuScale => "graphics card (resized there, standard colour range)",
        Route.GpuFull => "graphics card (full size)",
        _ => "frames copied through memory",
    };

    private bool _saidCrossChip;

    /// <summary>FFmpeg's capture device on the chip that drives the screen.</summary>
    internal static string DdaDevice(int adapter, uint vendor) =>
        vendor != 0 ? $"-init_hw_device d3d11va=dda:,vendor_id=0x{vendor:x4} -filter_hw_device dda "
        : adapter != 0 ? $"-init_hw_device d3d11va=dda:{adapter} -filter_hw_device dda "
        : "";

    /// <summary>Whether an encoder belongs to the chip with this vendor id.</summary>
    private static bool MadeBy(EncoderKind e, uint vendor) => e switch
    {
        EncoderKind.Nvenc => vendor == 0x10DE,
        EncoderKind.Qsv => vendor == 0x8086,
        EncoderKind.Amf => vendor == 0x1002 || vendor == 0x1022,
        _ => true,
    };

    /// <summary>The encoder built into a chip, by its vendor id.</summary>
    private static EncoderKind? OwnEncoder(uint vendor) => vendor switch
    {
        0x10DE => EncoderKind.Nvenc,
        0x8086 => EncoderKind.Qsv,
        0x1002 or 0x1022 => EncoderKind.Amf,
        _ => null,
    };

    /* ---- video memory: what the replay uses, and a guess before it runs ---- */

    /// <summary>Video memory the replay was using at the last check (MB); -1 when it isn't running.</summary>
    public double VideoMemoryNowMb { get; private set; } = -1;

    /// <summary>The most it has used since it (re)started (MB); -1 when it isn't running.</summary>
    public double VideoMemoryPeakMb { get; private set; } = -1;

    /// <summary>Which screen, size, format, encoder and route the running replay uses.</summary>
    public string RunningPlan => _planKey;
    private string _planKey = "";

    internal static string PlanKey(string device, int outH, VideoCodec codec, EncoderKind encoder, bool copied) =>
        $"{device}|{outH}|{codec}|{encoder}|{(copied ? "copy" : "card")}";

    public readonly record struct VramGuess(int Mb, EncoderKind Encoder, VideoCodec Codec, int OutH, bool FullSize, bool Copied, string Key);

    /// <summary>
    /// What the replay would use in video memory with these choices, worked
    /// out the way a run picks its encoder and route. Rough by nature:
    /// drivers differ, so it is scaled by what this PC was measured using.
    /// </summary>
    public static VramGuess GuessVideoMemory(DxgiTarget? screen, int height, VideoCodec codec, EncoderKind chosen,
                                             IReadOnlyCollection<EncoderKind> have)
    {
        int w = screen?.Desktop.Width ?? 1920, h = screen?.Desktop.Height ?? 1080;
        uint vendor = screen != null ? CaptureChip.Apply(screen).VendorId : 0;
        var encoder = chosen != EncoderKind.Auto && have.Contains(chosen) ? chosen
            : new[] { EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv }.FirstOrDefault(have.Contains, EncoderKind.X264);
        bool copied = encoder != EncoderKind.X264 && vendor != 0 && !MadeBy(encoder, vendor);
        if (copied && chosen == EncoderKind.Auto && OwnEncoder(vendor) is { } own && have.Contains(own))
        {
            encoder = own;
            copied = false;
        }
        bool cpu = copied || encoder == EncoderKind.X264;
        bool smaller = height > 0 && height < h;
        int outH = (smaller ? height : h) & ~1;
        double mb = RawVideoMemoryMb(w, h, outH, codec, encoder, cpu, true) * Fit;
        return new VramGuess((int)Math.Round(mb / 5) * 5, encoder, codec, outH, !smaller && !cpu, copied,
                             PlanKey(screen?.DeviceName ?? "", outH, codec, encoder, cpu));
    }

    /// <summary>
    /// The model behind the guess. Video memory goes on frames, so it follows
    /// the number of pixels: the screen's copy Windows hands over, the frames
    /// the encoder keeps to compare against, and the queue it looks ahead
    /// through. Kept at full size, that queue is the screen's own frames (4
    /// bytes a pixel); resized on the card, it is small frames (1.5 bytes a
    /// pixel). Frame rate and replay length hardly matter.
    /// </summary>
    internal static double RawVideoMemoryMb(int screenW, int screenH, int outH, VideoCodec codec, EncoderKind encoder,
                                            bool cpuRoute, bool tuned)
    {
        const double MB = 1048576.0;
        double screenPx = (double)Math.Max(1, screenW) * Math.Max(1, screenH);
        bool smaller = outH > 0 && outH < screenH;
        double scale = smaller ? (double)outH / screenH : 1;
        double outPx = screenPx * scale * scale;
        // The screen's copy and a few frames on their way out of it.
        double capture = screenPx * 4 * 4 / MB;
        // Frames copied out to the CPU (or across to another chip) leave
        // only the capture on this card.
        if (cpuRoute) return 45 + capture + screenPx * 4 * 2 / MB;

        double context = encoder switch { EncoderKind.Nvenc => 150, EncoderKind.Amf => 120, _ => 90 };
        int queue = encoder switch { EncoderKind.Nvenc => tuned ? 28 : 8, EncoderKind.Amf => 10, _ => 12 };
        double codecF = codec switch { VideoCodec.Hevc => 1.2, VideoCodec.Av1 => 1.35, _ => 1.0 };
        double refs = outPx * 1.5 * 6 * codecF / MB;
        double queued = smaller ? outPx * 1.5 * (queue + 4) / MB : screenPx * 4 * queue / MB;
        return context + capture + refs + queued;
    }

    private static double _fit = LoadFit();
    private static readonly object _fitLock = new();
    private static string FitFile => Path.Combine(Paths.Root, "vram-fit.txt");

    /// <summary>How this PC compares with the model (1 = as the model says).</summary>
    private static double Fit { get { lock (_fitLock) return _fit; } }

    private static double LoadFit()
    {
        try
        {
            if (File.Exists(FitFile) &&
                double.TryParse(File.ReadAllText(FitFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                return Math.Clamp(f, 0.5, 2.0);
        }
        catch { }
        return 1.0;
    }

    private static void LearnFit(double ratio, bool save)
    {
        if (double.IsNaN(ratio) || ratio <= 0) return;
        double f;
        lock (_fitLock)
        {
            _fit = Math.Clamp(_fit * 0.7 + Math.Clamp(ratio, 0.5, 2.0) * 0.3, 0.5, 2.0);
            f = _fit;
        }
        if (!save) return;
        try { File.WriteAllText(FitFile, f.ToString("0.###", CultureInfo.InvariantCulture)); } catch { }
    }

    private static string ChipName(uint vendor) => vendor switch
    {
        0x10DE => "NVIDIA", 0x8086 => "Intel", 0x1002 or 0x1022 => "AMD", _ => "other",
    };

    /// <summary>Intel's encoder takes frames through its own layer.</summary>
    internal static string QsvMap(EncoderKind e) => e == EncoderKind.Qsv ? ",hwmap=derive_device=qsv,format=qsv" : "";

    private static string EncoderArgs(EncoderKind e, VideoCodec c, AppSettings s, int w, int h, int fps, bool tuned = false)
    {
        // AV1's quality scale runs higher (0-63) than H.264/HEVC's (0-51).
        int q = Math.Clamp(s.ReplayQuality, 10, 40) + (c == VideoCodec.Av1 ? 8 : 0);
        int br = s.ReplayUseBitrate ? Math.Max(500, s.ReplayBitrateKbps) : 0;
        // A ceiling for quality mode, so fast motion can't balloon the loop.
        int cap = Math.Clamp((int)(w * (long)h * fps * 0.12 / 1000), 4000, 80000);
        string name = EncoderDetector.FFmpegName(e, c);

        return e switch
        {
            // Tuned for picture per MB: the encoder chip has headroom, so a
            // better preset, two-pass frames, a short look-ahead, adaptive
            // quantisation and B-frames cost games next to nothing. (It puts
            // the recording about a third of a second behind the screen,
            // which a replay never notices.) Plain settings if tuned won't start.
            EncoderKind.Nvenc => $"-c:v {name} " +
                (tuned
                    ? "-preset p5 -tune hq -multipass qres -rc-lookahead 20 -spatial-aq 1 -temporal-aq 1 -aq-strength 8 -bf 3 -b_ref_mode middle "
                    : "-preset p4 -bf 0 -rc-lookahead 0 ") +
                (br > 0 ? $"-rc vbr -b:v {br}k -maxrate {br * 3 / 2}k -bufsize {br * 2}k "
                        : $"-rc vbr -cq {q} -b:v 0 -maxrate {cap}k -bufsize {cap * 2}k "),
            // forced_idr makes AMD honour the forced keyframes (H.264/HEVC).
            EncoderKind.Amf => $"-c:v {name} -usage transcoding -quality speed " +
                (c == VideoCodec.Av1 ? "" : "-bf 0 -forced_idr 1 ") +
                (br > 0 ? $"-rc vbr_peak -b:v {br}k -maxrate {br * 3 / 2}k "
                        : $"-rc cqp -qp_i {q} -qp_p {q + 2} "),
            // Forced keyframes made IDR (Intel otherwise may make plain I
            // frames, which the loop can't be cut at); plain: no B-frames.
            EncoderKind.Qsv => $"-c:v {name} -preset veryfast -forced_idr 1 " + (tuned ? "" : "-bf 0 ") +
                (br > 0 ? $"-b:v {br}k -maxrate {br * 3 / 2}k " : $"-global_quality {q} "),
            _ => c switch
            {
                VideoCodec.Av1 => $"-c:v libsvtav1 -preset 10 -pix_fmt yuv420p " +
                    (br > 0 ? $"-b:v {br}k " : $"-crf {q} "),
                VideoCodec.Hevc => $"-c:v libx265 -preset veryfast -pix_fmt yuv420p " +
                    (br > 0 ? $"-b:v {br}k -maxrate {br * 3 / 2}k -bufsize {br * 2}k " : $"-crf {q} "),
                _ => "-c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p " +
                    (br > 0 ? $"-b:v {br}k -maxrate {br * 3 / 2}k -bufsize {br * 2}k "
                            : $"-crf {q} -maxrate {cap}k -bufsize {cap * 2}k "),
            },
        };
    }

    /// <summary>
    /// The chosen screen: by the monitor's own ID first (Windows' DISPLAY
    /// numbers can swap), then where it sits, then its Windows name. The main
    /// screen only if it isn't connected right now.
    /// </summary>
    private static DxgiTarget? PickScreen(AppSettings s)
    {
        var all = DxgiOutputs.All();
        bool chosen = s.ReplayMonitorId.Length > 0 || s.ReplayMonitorRect.Length > 0 || s.ReplayMonitor.Length > 0;
        var pick =
            (s.ReplayMonitorId.Length > 0 ? all.FirstOrDefault(t => MonitorIdentity.Of(t.DeviceName).Id == s.ReplayMonitorId) : null)
            ?? (s.ReplayMonitorRect.Length > 0 ? all.FirstOrDefault(t => MonitorIdentity.RectOf(t.Desktop) == s.ReplayMonitorRect) : null)
            ?? (s.ReplayMonitor.Length > 0 ? all.FirstOrDefault(t => string.Equals(t.DeviceName, s.ReplayMonitor, StringComparison.OrdinalIgnoreCase)) : null);
        if (pick == null && chosen)
            Log.Write("Replay: the chosen screen isn't connected right now; recording the main screen until it is.");
        return pick
            ?? all.FirstOrDefault(t => t.Desktop.X == 0 && t.Desktop.Y == 0)
            ?? all.FirstOrDefault();
    }

    /* ---- how the replay is doing ------------------------------------------- */

    private long _gateUs;
    private int _fps;
    private DateTime _lastHealth;
    private long _pFrame, _pDrop, _pDup;
    private string _pSpeed = "";

    /// <summary>One line of FFmpeg's progress report (key=value, a block every 2 s).</summary>
    private void OnProgress(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line[..eq].Trim();
        var val = line[(eq + 1)..].Trim();
        switch (key)
        {
            case "frame": long.TryParse(val, out _pFrame); break;
            case "drop_frames": long.TryParse(val, out _pDrop); break;
            case "dup_frames": long.TryParse(val, out _pDup); break;
            case "speed": _pSpeed = val; break;
            case "progress":
                if (DateTime.UtcNow - _lastHealth > TimeSpan.FromMinutes(1))
                {
                    _lastHealth = DateTime.UtcNow;
                    // How far behind the sound is, at worst: if it creeps up over
                    // hours, the sound encoder is falling behind (the cause of
                    // late sound in a clip, if it happens).
                    double behind = 0;
                    try { if (_tracks is { } tr) behind = tr.Tracks.Max(t => t.Engine.QueuedSeconds); } catch { }
                    Log.Write($"Replay health: speed {_pSpeed}, {_pFrame} frames, {_pDrop} dropped, {_pDup} duplicated, " +
                              $"{HeldSeconds(CurrentRun())} s held in this stretch, sound waiting {behind:0.0} s");
                }
                break;
        }
    }

    /// <summary>
    /// The first packet of a stream ("v" or "a") in an input, with the
    /// timestamps it has in the pieces themselves (seconds): decode and
    /// display time. Null if it can't be read.
    /// </summary>
    private static async Task<(double Dts, double Pts)?> FirstPacketAsync(string input, string kind, string? workDir = null)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = Paths.FFmpeg,
                Arguments = $"-hide_banner -loglevel error -copyts {input} -map 0:{kind}:0 -frames:{kind} 1 -c copy -f framecrc -",
                WorkingDirectory = workDir ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return null;
            _ = p.StandardError.ReadToEndAsync();
            // One packet is read in a moment; a piece Windows is holding never stalls this for long.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string text;
            try
            {
                text = await p.StandardOutput.ReadToEndAsync(limit.Token);
                await p.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } return null; }
            var tb = System.Text.RegularExpressions.Regex.Match(text, @"#tb 0: (\d+)/(\d+)");
            var line = text.Split('\n').FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (!tb.Success || line == null) return null;
            var parts = line.Split(',');
            if (parts.Length < 3 || !long.TryParse(parts[1].Trim(), out var dts) || !long.TryParse(parts[2].Trim(), out var pts))
                return null;
            double unit = double.Parse(tb.Groups[1].Value, Inv) / double.Parse(tb.Groups[2].Value, Inv);
            return (dts * unit, pts * unit);
        }
        catch (Exception ex) { Log.Error("first packet", ex); return null; }
    }

    /// <summary>Measures a saved replay's real frame rate, for the log.</summary>
    private static async Task CheckSavedAsync(string path, int target)
    {
        try
        {
            var info = await ClipEditor.ProbeAsync(path);
            // A check for the log only: long clips aren't read through again.
            if (info == null || info.Duration <= 0 || info.Duration > 120) return;
            // A copy pass, done in well under a second; never held up longer than 3.
            var r = await FFmpegRun.ExecAsync($"-hide_banner -i \"{path}\" -map 0:v:0 -c copy -f null -", 3000);
            if (r is not { } done) return;
            var err = done.Text;
            var m = System.Text.RegularExpressions.Regex.Matches(err, @"frame=\s*(\d+)");
            if (m.Count == 0) return;
            double frames = double.Parse(m[^1].Groups[1].Value, Inv);
            double fps = frames / info.Duration;
            Log.Write($"Saved replay check: {fps:0.0} fps over {info.Duration:0.0}s (target {target})" +
                      (fps < target * 0.8 ? " — frames are missing" : ""));
        }
        catch (Exception ex) { Log.Error("replay check", ex); }
    }

    private const int ExternalError = -542398533;   // FFmpeg's "error in an external library"

    /// <summary>An integrated chip (Intel) next to a separate card (NVIDIA or AMD).</summary>
    private static async Task<bool> HasTwoChipsAsync()
    {
        var h264 = await EncoderDetector.AvailableForAsync(VideoCodec.H264);
        return h264.Contains(EncoderKind.Qsv) && (h264.Contains(EncoderKind.Nvenc) || h264.Contains(EncoderKind.Amf));
    }

    /// <summary>The loop's pieces, oldest first (their numbers only ever go up).</summary>
    private List<string> Pieces(string? dir = null, string? ext = null)
    {
        try
        {
            return Directory.EnumerateFiles(dir ?? _dir, "seg*." + (ext ?? _ext))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Finished fMP4 pieces, oldest first, as the playlist lists them.</summary>
    private List<string> FinishedFmp4(string? dir = null)
    {
        dir ??= _dir;
        try
        {
            var list = Path.Combine(dir, "live.m3u8");
            if (!File.Exists(list)) return new();
            string text;
            using (var fs = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) text = r.ReadToEnd();
            // Only the newest few are checked on disk: older entries in the
            // playlist are pieces already trimmed away.
            var names = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith('#') && l.EndsWith(".m4s"))
                .ToList();
            int look = Math.Max(_keep, KeepFor(_settings.Current.ReplayLongest())) + 4;
            return names.Skip(Math.Max(0, names.Count - look))
                .Select(l => Path.Combine(dir, Path.GetFileName(l)))
                .Where(File.Exists)
                .ToList();
        }
        catch { return new(); }
    }

    private void Prune()
    {
        if (_saving) return;   // never pull pieces out from under a save
        // Video pieces past the length wanted, oldest first (fMP4 by its
        // playlist; its shared header, init.mp4, is never one of them).
        var all = _ext == "m4s" ? FinishedFmp4() : Pieces();
        for (int i = 0; i < all.Count - _keep; i++)
            try { File.Delete(all[i]); } catch { }
        var sound = SoundPieces(_soundDir.Length > 0 ? _soundDir : null);
        for (int i = 0; i < sound.Count - _keep; i++)
            try { File.Delete(sound[i]); } catch { }
    }

    /// <summary>The sound's pieces, oldest first.</summary>
    private List<string> SoundPieces(string? dir = null)
    {
        try
        {
            return Directory.EnumerateFiles(dir ?? _dir, "aud*.ts")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }
        catch { return new(); }
    }

    private void KillCurrent()
    {
        foreach (var p in new[] { _proc, _audioProc })
            Quit(p);
        _proc = null;
        _audioProc = null;
        var t = _tracks;
        _tracks = null;
        t?.Dispose();
    }

    private static void Quit(Process? p)
    {
        if (p != null)
        {
            try
            {
                // "q" lets FFmpeg close the current piece cleanly; kill if it lingers.
                if (!p.HasExited) { p.StandardInput.Write("q"); p.StandardInput.Flush(); }
                if (!p.WaitForExit(2000)) p.Kill();
            }
            catch { try { p.Kill(); } catch { } }
            try { p.Dispose(); } catch { }
        }
    }

    private static string SafeExit(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    /* ---- runs a save can draw on ------------------------------------------- */

    /// <summary>
    /// One stretch of continuous picture: its folder, format, and what places
    /// it in real time. When a game takes the screen over (going full screen,
    /// or tabbing back in), Windows cuts the screen capture off and the
    /// picture starts a new stretch. The sound keeps recording straight
    /// through (one folder for the whole session), so a save can reach back
    /// across that moment with every sound track intact; the picture just
    /// holds for the second the capture was cut off.
    /// </summary>
    private sealed class RunInfo
    {
        public string Dir = "", SoundDir = "", Ext = "ts", Sig = "";
        public VideoCodec Codec;
        public int Fps;
        public long GateUs;
        public long? VfirstUs;
        public double? P0v, P0a;
        public AudioTracks? Tracks;
        public int TrackCount;
        public DateTime Started, Ended;
    }

    // Earlier stretches of this sound session, oldest first (same picture format as now).
    private readonly List<RunInfo> _past = new();
    private string _sig = "";
    private DateTime _runStarted;
    private string _soundDir = "";
    // The last stretch ended because the screen was taken over: the sound carries on into the next.
    private bool _keepSound;

    private RunInfo CurrentRun() => new()
    {
        Dir = _dir, SoundDir = _soundDir.Length > 0 ? _soundDir : _dir, Ext = _ext, Sig = _sig, Codec = _codec, Fps = _fps,
        GateUs = _gateUs, VfirstUs = _vfirstUs, P0v = _p0v, P0a = _p0a,
        Tracks = _tracks, TrackCount = _tracks?.Tracks.Count ?? 0, Started = _runStarted,
    };

    private List<string> PiecesOf(RunInfo r) => r.Ext == "m4s" ? FinishedFmp4(r.Dir) : Pieces(r.Dir, r.Ext);

    private int HeldSeconds(RunInfo r) => PiecesOf(r).Count * SegmentSeconds;

    /// <summary>Why the latest save didn't work (null when it did).</summary>
    public string? LastError { get; private set; }

    private const string SaveFailed = "Saving the replay didn't work. Settings → About → Create diagnostics report shows why.";

    /* ---- saving ------------------------------------------------------------- */

    /// <summary>
    /// How long a save made now would be: the key's length, shorter after
    /// "Start over after saving", and never more than the loop holds (after a
    /// restart it only has what came since).
    /// </summary>
    public int PlannedSeconds(int? lengthOverride = null)
    {
        var st = _settings.Current;
        int seconds = Math.Clamp(lengthOverride ?? st.ReplaySeconds, 5, 3600);
        // Start over after saving: the next save only has what came after.
        // Only the main save hotkey starts over; the extra save setups always
        // take their full length.
        if (lengthOverride == null && st.ReplayStartOverAfterSave && _lastSaveAt is DateTime last)
        {
            double since = (DateTime.UtcNow - last).TotalSeconds;
            if (since < seconds) seconds = Math.Max(3, (int)Math.Ceiling(since));
        }
        int held = HeldSeconds(CurrentRun());
        lock (_past) held += _past.Sum(HeldSeconds);
        if (held > 0) seconds = Math.Min(seconds, held);
        return seconds;
    }

    /// <summary>Seconds of picture held right now (0 while off or just starting).</summary>
    public int SecondsHeld
    {
        get
        {
            if (!IsRunning) return 0;
            try
            {
                int held = HeldSeconds(CurrentRun());
                lock (_past) held += _past.Sum(HeldSeconds);
                return held;
            }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Keeps the last <see cref="AppSettings.ReplaySeconds"/> seconds as an
    /// MP4 in the temp folder, and returns its path (null if it couldn't; <see cref="LastError"/> says why).
    /// </summary>
    public async Task<string?> SaveAsync(int? lengthOverride = null)
    {
        LastError = null;
        if (!IsRunning) { LastError = "The replay is off."; return null; }
        // A save already going: this one waits its turn rather than being dropped.
        if (!await _saveLock.WaitAsync(TimeSpan.FromMinutes(3))) { LastError = "Another save took too long; try again."; return null; }
        _saving = true;
        try
        {
            if (!IsRunning) { LastError = "The replay is off."; return null; }
            var now = DateTime.UtcNow;
            int seconds = PlannedSeconds(lengthOverride);
            LastSaveSeconds = seconds;

            // Newest first: this stretch, then (if it doesn't reach back far
            // enough) the ones before the last screen changes.
            var live = CurrentRun();
            var runs = new List<RunInfo> { live };
            lock (_past) runs.AddRange(Enumerable.Reverse(_past).Where(r => r.SoundDir == live.SoundDir && r.Sig == live.Sig));
            // Just (re)started, after a settings change say: a second or two of
            // picture isn't a replay, and saving it raced the encoder's start.
            if (runs.Count == 1 && HeldSeconds(live) < 2 * SegmentSeconds)
            {
                Log.Write($"Replay save refused: {HeldSeconds(live)} s held in {live.Dir} ({Pieces(live.Dir, live.Ext).Count} pieces), " +
                          $"this stretch running {(DateTime.UtcNow - _runStarted).TotalSeconds:0} s, {_pFrame} frames; {StateReport()}");
                LastError = Trouble is { } why
                    ? $"The replay isn't capturing right now: {why}. It keeps trying by itself."
                    : _quickStreak >= 2
                        ? "The replay keeps restarting instead of recording, so there's nothing to save yet. It keeps trying other ways by itself; " +
                          "if this lasts, send a diagnostics report (Settings → About)."
                        : "The replay has only just started (it restarts when its settings change). Give it a few seconds, then save again.";
                return null;
            }
            string? output = null;
            if (runs.Count > 1 && seconds > HeldSeconds(live) + SegmentSeconds)
            {
                output = await SaveAcrossAsync(runs, seconds);
                if (output == null) Log.Write("Replay save: joining across the screen change didn't work; saving the latest stretch only");
            }
            output ??= await SaveRunAsync(live, seconds);
            if (output == null)
            {
                LastError ??= SaveFailed;
                return null;
            }
            var saved = await TrimToLengthAsync(output, seconds);
            // Finished before the file is handed on: this check reading it while
            // it was being moved into the clips folder made that move fail
            // ("Replay not saved: the file couldn't be moved").
            await CheckSavedAsync(saved, live.Fps);
            // "Start over after saving" counts from here, and only once a save has worked.
            if (lengthOverride == null) _lastSaveAt = now;
            return saved;
        }
        catch (Exception ex)
        {
            Log.Error("save replay", ex);
            LastError = SaveFailed;
            return null;
        }
        finally
        {
            _saving = false;
            _saveLock.Release();
        }
    }

    /// <summary>
    /// One input made of many pieces that join byte for byte, named relative
    /// to the folder FFmpeg runs in, which keeps the command short. With more
    /// pieces than a command line can hold (Windows' limit is about 32,000
    /// characters: a ten-minute replay went past it), they're joined into one
    /// file first.
    /// </summary>
    private static string PiecesInput(string workDir, string filesDir, IEnumerable<string> files, string tag, List<string> made)
    {
        string prefix = string.Equals(Path.GetFullPath(workDir), Path.GetFullPath(filesDir), StringComparison.OrdinalIgnoreCase)
            ? "" : Path.GetRelativePath(workDir, filesDir) + Path.DirectorySeparatorChar;
        var names = files.Select(f => Path.GetFileName(f)!).ToList();
        string inline = "concat:" + string.Join("|", names.Select(n => prefix + n));
        if (inline.Length < 12000) return $"-i \"{inline}\"";
        var joined = Path.Combine(filesDir, $"joined_{tag}_{Guid.NewGuid().ToString("N")[..6]}{Path.GetExtension(names[^1])}");
        using (var output = new FileStream(joined, FileMode.Create, FileAccess.Write, FileShare.Read))
            foreach (var n in names)
            {
                using var input = new FileStream(Path.Combine(filesDir, n), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                input.CopyTo(output);
            }
        made.Add(joined);
        return $"-i \"{prefix}{Path.GetFileName(joined)}\"";
    }

    /// <summary>The video pieces a save takes from one stretch (the live one waits for its freshest fMP4 piece).</summary>
    private async Task<List<string>> TakePiecesAsync(RunInfo run, bool live, int seconds)
    {
        List<string> all;
        if (run.Ext == "m4s" && live)
        {
            // An fMP4 piece can only be read once it's finished, so wait
            // for the one being written now (at most a couple of seconds):
            // the freshest moment is usually the one that matters.
            var before = FinishedFmp4(run.Dir);
            var until = DateTime.UtcNow.AddSeconds(SegmentSeconds + 1);
            all = before;
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(100);
                all = FinishedFmp4(run.Dir);
                if (all.Count > 0 && (before.Count == 0 || all[^1] != before[^1])) break;
            }
        }
        else all = PiecesOf(run);
        // Just enough whole pieces for the length, plus the one being
        // written now (the freshest seconds): no trimming pass needed.
        int take = (int)Math.Ceiling(seconds / (double)SegmentSeconds) + 1;
        return all.Skip(Math.Max(0, all.Count - take)).ToList();
    }

    private static IEnumerable<string> WithHeader(RunInfo run, List<string> pieces) =>
        run.Ext == "m4s" ? new[] { Path.Combine(run.Dir, "init.mp4") }.Concat(pieces) : pieces;

    /// <summary>
    /// The real time (s) of a stretch's first frame in <paramref name="vf"/>, from
    /// what was measured as it recorded (estimated where it couldn't be).
    /// </summary>
    private static double WallStart(RunInfo run, (double Dts, double Pts) vf, bool hasSound)
    {
        long? vfirst = run.VfirstUs;
        double? p0v = run.P0v, p0a = run.P0a;
        if (vfirst == null || p0v == null || (hasSound && p0a == null))
        {
            var missing = new List<string>();
            if (vfirst == null)
            {
                missing.Add("the video's first frame");
                var seg0 = Directory.EnumerateFiles(run.Dir, "seg00000000.*").FirstOrDefault();
                vfirst = seg0 != null ? (File.GetCreationTimeUtc(seg0) - DateTime.UnixEpoch).Ticks / 10 : run.GateUs + 500_000;
            }
            if (p0v == null) missing.Add("the video's zero");
            if (hasSound && p0a == null) missing.Add("the sound's zero");
            p0v ??= p0a ?? 0;
            Log.Write("Replay save: estimated " + string.Join(", ", missing) + " (sound may be slightly off)");
        }
        return vfirst.Value / 1e6 + (vf.Pts - p0v.Value);
    }

    /// <summary>The last <paramref name="seconds"/> of the live stretch, as an MP4 (null if it couldn't).</summary>
    private async Task<string?> SaveRunAsync(RunInfo run, int seconds)
    {
        var made = new List<string>();
        try
        {
            var pieces = await TakePiecesAsync(run, live: true, seconds);
            if (pieces.Count == 0) { LastError = "Nothing to save yet: the replay has only just started."; return null; }
            Log.Write($"Replay save: {pieces.Count} pieces, {Path.GetFileName(pieces[0])} to {Path.GetFileName(pieces[^1])}");
            // Both kinds join byte for byte: TS pieces as they are, fMP4 pieces behind their shared header.
            string input = PiecesInput(run.Dir, run.Dir, WithHeader(run, pieces), "v", made);
            return await MuxAsync(run, input, run.Dir, seconds, made, wallStartOf: null, pictureSeconds: pieces.Count * SegmentSeconds);
        }
        finally { foreach (var f in made) try { File.Delete(f); } catch { } }
    }

    /// <summary>
    /// A save reaching back past screen changes: each stretch's picture,
    /// placed at its real time (the picture holds over the moment the capture
    /// was cut off), then the session's sound, every track, lined up over the lot.
    /// </summary>
    private async Task<string?> SaveAcrossAsync(List<RunInfo> newestFirst, int seconds)
    {
        var made = new List<string>();
        try
        {
            // Pieces from each stretch, newest first, until the length is covered.
            var take = new List<(RunInfo Run, List<string> Pieces)>();
            int need = seconds;
            for (int i = 0; i < newestFirst.Count && need > 0; i++)
            {
                var run = newestFirst[i];
                var pieces = await TakePiecesAsync(run, live: i == 0, need);
                if (pieces.Count == 0) { if (i == 0) continue; break; }
                take.Insert(0, (run, pieces));
                need -= pieces.Count * SegmentSeconds;
            }
            if (take.Count < 2) return null;

            // Each stretch's picture on its own, and where it starts in real time.
            var parts = new List<(string File, double Wall)>();
            foreach (var (run, pieces) in take)
            {
                string input = PiecesInput(run.Dir, run.Dir, WithHeader(run, pieces), "v", made);
                if (await FirstPacketAsync(input, "v", run.Dir) is not { } vf) return null;
                double wall = WallStart(run, vf, hasSound: true);
                var part = Path.Combine(Paths.Temp, $"replay_part_{Guid.NewGuid():N}.mp4");
                made.Add(part);
                var args = $"-hide_banner -loglevel warning {input} -map 0:v:0 -c copy " +
                           (run.Codec == VideoCodec.Hevc ? "-tag:v hvc1 " : "") + $"-y \"{part}\"";
                if (!await RunAsync(args, 120000, run.Dir) || !File.Exists(part)) return null;
                parts.Add((part, wall));
            }

            // Joined, each part lasting until the next one began, so the gap
            // shows as the picture holding still rather than time jumping.
            var list = Path.Combine(Paths.Temp, $"replay_join_{Guid.NewGuid():N}.txt");
            made.Add(list);
            var lines = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                lines.Add("file '" + parts[i].File.Replace("'", "'\\''") + "'");
                if (i < parts.Count - 1)
                    lines.Add("duration " + Math.Max(0.1, parts[i + 1].Wall - parts[i].Wall).ToString("0.######", Inv));
            }
            File.WriteAllLines(list, lines);
            var picture = Path.Combine(Paths.Temp, $"replay_picture_{Guid.NewGuid():N}.mp4");
            made.Add(picture);
            if (!await RunAsync($"-hide_banner -loglevel warning -f concat -safe 0 -i \"{list}\" -map 0:v:0 -c copy -y \"{picture}\"", 120000) ||
                !File.Exists(picture))
                return null;
            Log.Write($"Replay save: picture joined from {parts.Count} stretches (the screen was taken over in between)");

            // The sound of the whole session, lined up with the first stretch's first frame.
            var live = newestFirst[0];
            // The joined picture runs from the first stretch's start to the end of the last.
            double span = Math.Max(0, parts[^1].Wall - parts[0].Wall) + take[^1].Pieces.Count * SegmentSeconds;
            return await MuxAsync(live, $"-i \"{picture}\"", live.Dir, seconds, made, wallStartOf: parts[0].Wall, pictureSeconds: span);
        }
        catch (Exception ex) { Log.Error("replay save across", ex); return null; }
        finally { foreach (var f in made) try { File.Delete(f); } catch { } }
    }

    /// <summary>
    /// The picture input plus the session's sound, every track that carried
    /// sound, lined up on real time, into one MP4. <paramref name="wallStartOf"/>:
    /// the picture's first frame in real time, if already known (otherwise measured from <paramref name="run"/>).
    /// </summary>
    private async Task<string?> MuxAsync(RunInfo run, string input, string workDir, int seconds, List<string> made, double? wallStartOf,
                                         double pictureSeconds)
    {
        var st = _settings.Current;
        // Positive sync: the sound comes earlier (it's captured a moment late). Negative: later.
        double sync = Math.Clamp(st.ReplaySoundSyncMs, -10000, 10000) / 1000.0;

        // Picture and sound come from separate FFmpegs, each with its own
        // zero, so they're placed by real time: the video's first frame
        // and the sound's start are both known on the same clock.
        string videoIn = $"-itsoffset 1 {input}";
        string audioIn = "";
        string cut = "";
        var vFirst = await FirstPacketAsync(input, "v", workDir);
        // Just after starting, the zeros may not be known yet.
        for (int i = 0; i < 30 && (_vfirstUs == null || _p0v == null || _p0a == null); i++) await Task.Delay(100);
        if (run.Tracks == _tracks) { run.VfirstUs ??= _vfirstUs; run.P0v ??= _p0v; run.P0a ??= _p0a; }
        var soundAll = SoundPieces(run.SoundDir);
        if (vFirst is { } vf && soundAll.Count > 0)
        {
            double wallStart = wallStartOf ?? WallStart(run, vf, hasSound: true);
            double? knownP0a = run.P0a;
            // The sound pieces from just before the first frame on.
            double gate = run.GateUs / 1e6;
            int from = Math.Max(0, (int)Math.Floor((wallStart - gate) / SegmentSeconds) - 1);
            // Only as many as the picture needs (a few spare): never pieces from
            // some other time that happen to sit in the folder.
            int need = (int)Math.Ceiling(seconds / (double)SegmentSeconds) + 6;
            var sound = soundAll.Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f)[3..], out var n) && n >= from)
                                .Take(need).ToList();
            if (sound.Count > 0)
            {
                string soundInput = PiecesInput(workDir, run.SoundDir, sound, "a", made);
                var aFirst = await FirstPacketAsync(soundInput, "a", workDir);
                if (aFirst is { } af)
                {
                    // The sound's zero, if it wasn't measured: each sound piece
                    // is cut every 2 s from the sound's start, so its number
                    // says where it begins. (Guessing from the video's zero
                    // instead could put the sound minutes off.)
                    int firstNumber = int.TryParse(Path.GetFileNameWithoutExtension(sound[0])[3..], out var fn) ? fn : 0;
                    double p0a = knownP0a ?? af.Pts - firstNumber * SegmentSeconds;
                    if (knownP0a == null) Log.Write($"Replay save: the sound's zero worked out from its piece numbers (piece {firstNumber})");
                    // Where the sound starts relative to the picture, on real time.
                    // (Less the sound's cushion: it comes out that much after it was heard.)
                    // Sound pieces are MPEG-TS, whose clock rolls over every ~26.5 hours
                    // (2^33 ticks at 90 kHz), and it doesn't start at zero, so the
                    // rollover can come at any time after the sound starts. The real
                    // time since the sound's start is known to within seconds (the
                    // picture's start less the sound's), so the timestamps are put
                    // back on the right side of however many rollovers there were.
                    // (1.5.60 only handled a rollover in the first half day, which
                    // missed most of them: sound up to 2 s off in long sessions.)
                    const double Rollover = 8589934592.0 / 90000;
                    double sinceZero = af.Pts - p0a;
                    double expected = wallStart - gate;
                    sinceZero += Math.Round((expected - sinceZero) / Rollover) * Rollover;
                    double align = (gate + sinceZero) - wallStart - CaptureRing.CushionMs / 1000.0;
                    // A sound start far from the picture's means something was
                    // measured wrong: lined up by their ends instead (both run
                    // up to now), which is never far off.
                    if (Math.Abs(align) > 20)
                    {
                        double end = pictureSeconds - sound.Count * SegmentSeconds;
                        Log.Write($"Replay save: the sound's timing looked wrong ({align:+0.0;-0.0}s); lined up by the end instead ({end:+0.0;-0.0}s)");
                        align = end;
                    }
                    audioIn = $"-itsoffset {(1 + align - sync).ToString("0.####", Inv)} {soundInput}";
                    // Both cut together just before the first frame's decode
                    // time, so it's kept (with B-frames that sits just before
                    // its display time) and everything starts together.
                    cut = $"-ss {(1 + (vf.Dts - vf.Pts) - 0.01).ToString("0.####", Inv)} ";
                    Log.Write($"Replay save: {sound.Count} sound pieces, sound {align:+0.000;-0.000}s from the first frame, sync {sync:+0.00;-0.00}s");
                }
            }
        }
        if (audioIn.Length == 0)
        {
            // Nothing to line up with (no sound, or too soon): picture only.
            Log.Write("Replay save: picture only — " + (soundAll.Count == 0
                ? "no sound pieces exist (the sound recorder isn't running; see \"Replay sound stopped\")"
                : vFirst == null ? "the video's first frame couldn't be read" : "the sound pieces couldn't be read"));
            videoIn = input;
        }

        // Sound: the main mix (made while recording), then each track that
        // actually carried sound during this stretch, named after its app.
        int trackCount = audioIn.Length == 0 ? 0 : run.TrackCount;
        var layout = run.Tracks?.Layout(DateTime.Now.AddSeconds(-seconds - SegmentSeconds))
                     ?? (new List<(int Stream, string Name)>(), new List<int>());
        var maps = new StringBuilder("-map 0:v:0 ");
        var meta = new StringBuilder();
        if (trackCount >= 2)
        {
            maps.Append("-map 1:a:0 ");
            meta.Append(Title(0, "Mix")).Append("-disposition:a:0 default ");
            int n = 1;
            foreach (var (stream, name) in layout.Tracks)
            {
                maps.Append($"-map 1:a:{stream + 1} ");
                meta.Append(Title(n, name)).Append($"-disposition:a:{n} 0 ");
                n++;
            }
        }
        else if (trackCount == 1)
        {
            maps.Append("-map 1:a:0 ");
            // Always named: the track it is, or what the replay records.
            meta.Append(Title(0, layout.Tracks.Count == 1 ? layout.Tracks[0].Name
                : st.ReplayTrackPc && !st.ReplayTrackMic ? "All PC audio"
                : !st.ReplayTrackPc && st.ReplayTrackMic ? "Microphone" : "Mix"));
        }

        // Unique: two saves in the same second (the main key, then an extra one)
        // used to write the same file while the first was still being filed.
        var output = Path.Combine(Paths.Temp, $"replay_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.mp4");
        // Never longer than the picture: sound runs a moment past it, and
        // trimming that off afterwards meant copying the whole clip again.
        string upTo = audioIn.Length > 0 && pictureSeconds > 0 ? $"-t {(pictureSeconds + 0.5).ToString("0.###", Inv)} " : "";
        var args = $"-hide_banner -loglevel warning {videoIn} {audioIn} {maps}{cut}{upTo}-c copy " +
                   (run.Codec == VideoCodec.Hevc ? "-tag:v hvc1 " : "") +
                   $"{meta}-movflags +faststart -y \"{output}\"";

        if (!(await RunAsync(args, 120000, workDir) && File.Exists(output) && new FileInfo(output).Length > 10_000))
        {
            // Picture and main sound only, in case the tracks were the problem.
            var simple = audioIn.Length > 0
                ? $"-hide_banner -loglevel warning {videoIn} {audioIn} -map 0:v:0 -map 1:a:0? {cut}-c copy {Title(0, trackCount >= 2 ? "Mix" : layout.Tracks.FirstOrDefault().Name ?? "Mix")}-movflags +faststart -y \"{output}\""
                : $"-hide_banner -loglevel warning {input} -map 0:v:0 -c copy -movflags +faststart -y \"{output}\"";
            if (!await RunAsync(simple, 120000, workDir) || !File.Exists(output)) { LastError = SaveFailed; return null; }
        }
        return output;
    }

    private static string Title(int stream, string? name) => TrackMux.Title(stream, name);

    /// <summary>
    /// A safety net: if a replay came out longer than asked (a piece that
    /// grew too long), keep only its last <paramref name="seconds"/>.
    /// </summary>
    private static async Task<string> TrimToLengthAsync(string path, int seconds)
    {
        var info = await ClipEditor.ProbeAsync(path);
        if (info == null || info.Duration <= seconds + 4) return path;

        var trimmed = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_t.mp4");
        // Keyframes fall every 2 s from the start, so cutting on one of them
        // (after the input, so sound and picture are cut at the same point)
        // keeps both starting together.
        double from = Math.Max(0, Math.Floor((info.Duration - seconds) / SegmentSeconds) * SegmentSeconds);
        var copy = $"-hide_banner -loglevel warning -i \"{path}\" -ss {from.ToString("0.###", Inv)} -map 0 -c copy " +
                   $"-movflags +faststart -y \"{trimmed}\"";
        if (await RunAsync(copy, 60000) && File.Exists(trimmed))
        {
            var t = await ClipEditor.ProbeAsync(trimmed);
            if (t != null && t.Duration <= seconds + 4)
            {
                Log.Write($"Replay trimmed from {info.Duration:0.#}s to {t.Duration:0.#}s");
                try { File.Delete(path); } catch { }
                return trimmed;
            }
        }
        var exact = $"-hide_banner -loglevel warning -ss {from.ToString("0.###", Inv)} -i \"{path}\" -map 0 " +
                    $"-c:v libx264 -preset veryfast -crf 20 -c:a copy -movflags +faststart -y \"{trimmed}\"";
        if (await RunAsync(exact, 300000) && File.Exists(trimmed))
        {
            Log.Write($"Replay re-cut from {info.Duration:0.#}s to {seconds}s");
            try { File.Delete(path); } catch { }
            return trimmed;
        }
        return path;
    }


    // Saving copies a lot of data quickly: it gives way to the game.
    private static Task<bool> RunAsync(string args, int timeoutMs, string? workDir = null) =>
        FFmpegRun.RunAsync(args, timeoutMs, ProcessPriorityClass.BelowNormal, workDir, "ffmpeg (replay save)");

    public void Dispose() => Stop();
}

/// <summary>
/// The way of recording that worked, per screen, chip, encoder and format,
/// kept across launches so the replay starts the right way at once.
/// </summary>
internal static class RouteMemory
{
    private static readonly object Gate = new();
    private static Dictionary<string, (int Route, bool Plain)>? _map;
    private static string File_ => Path.Combine(Paths.Root, "replay-routes.txt");

    public static (int Route, bool Plain)? Get(string key)
    {
        lock (Gate) return Load().TryGetValue(key, out var v) ? v : null;
    }

    public static void Set(string key, int route, bool plain)
    {
        lock (Gate)
        {
            var m = Load();
            if (m.TryGetValue(key, out var was) && was == (route, plain)) return;
            m[key] = (route, plain);
            Save(m);
        }
    }

    public static void Forget(string key)
    {
        lock (Gate) { var m = Load(); if (m.Remove(key)) Save(m); }
    }

    private static Dictionary<string, (int, bool)> Load()
    {
        if (_map != null) return _map;
        _map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(File_))
                foreach (var line in File.ReadAllLines(File_))
                {
                    int eq = line.LastIndexOf('=');
                    if (eq <= 0) continue;
                    var parts = line[(eq + 1)..].Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var r) && bool.TryParse(parts[1], out var p))
                        _map[line[..eq]] = (r, p);
                }
        }
        catch { }
        return _map;
    }

    private static void Save(Dictionary<string, (int, bool)> m)
    {
        try { File.WriteAllLines(File_, m.Select(kv => $"{kv.Key}={kv.Value.Item1},{kv.Value.Item2}")); }
        catch (Exception ex) { Log.Error("replay routes", ex); }
    }
}
