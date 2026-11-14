using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Persocord.Models;
using Timer = System.Threading.Timer;

namespace Persocord.Services;

public record RecordRequest(Rectangle Region, bool AsGif);

public record RecordResult(
    string FilePath, int Width, int Height, long DurationMs, int Fps, bool Ok, string? Error);

/// <summary>
/// Runs a recording session.
///
/// Video comes from FFmpeg, one process per unpaused stretch. Audio comes
/// from <see cref="AudioEngine"/>, which runs for the whole session and is
/// encoded by a second FFmpeg alongside. The two are joined when recording
/// stops.
///
/// Nothing the user presses waits on FFmpeg. Pause closes the audio gate and
/// tells FFmpeg to stop, then returns; FFmpeg finishes writing its file in
/// the background. Resume starts the next process and returns at once. A
/// fresh FFmpeg needs a few hundred milliseconds before its first frame, so
/// audio stays gated until FFmpeg reports that frame, which keeps sound and
/// picture aligned in every piece.
/// </summary>
public class RecorderService
{
    private sealed class Segment
    {
        public Process Proc = null!;
        public string Path = "";
        public DateTime Started;
        public bool UsesProbe;
        public bool UsesGpu;
        public volatile bool EndRequested;
        public volatile bool FirstFrameSeen;
        public long LiveTicks;   // Stopwatch time of the first frame
        public long EndTicks;    // Stopwatch time capture was told to stop
    }

    /// <summary>How long each piece really lasted, by the clock, for retiming at the end.</summary>
    private readonly Dictionary<string, double> _wallSeconds = new();
    private volatile bool _limitFired;

    // What this machine has refused so far, remembered while Persocord runs,
    // so each recording starts with the best route that is known to work.
    private static bool _gpuRouteBroken;
    private static bool _probeOffOnGpu;
    private readonly HashSet<string> _gpuPieces = new();

    private readonly SettingsService _settings;
    private readonly object _sync = new();

    private Segment? _current;
    private readonly List<string> _segments = new();
    private readonly List<Task> _finishing = new();
    /// <summary>Every FFmpeg this recording started, let go of (their handles freed) when it ends.</summary>
    private readonly List<Process> _procs = new();

    private AudioTracks? _audio;
    private DateTime _audioStart;
    private VideoCodec _codec = VideoCodec.H264;
    private (List<(int Stream, string Name)> Tracks, List<int> Mix) _audioLayout = (new(), new());
    private Process? _audioProc;
    private string _audioPath = "";
    private long _pendingSuppress;

    private string _sessionId = "";
    private Stopwatch _clock = new();
    private Timer? _ticker;
    private Rectangle _region;
    private Rectangle _capture;
    private int _outW, _outH, _fps;
    private bool _wantGif;
    private DxgiTarget? _dda;
    // The chip Persocord sees the screen on, and the chips tried when FFmpeg couldn't find it there.
    private uint _ddaSeenVendor;
    private List<uint>? _chipCands;
    private bool _chipTrying;
    private CaptureBackend _backend;
    private EncoderKind _encoder;
    private bool _probeBroken;
    /// <summary>How far this recording has stepped back from the chosen encoder after it refused to start (0 = not at all).</summary>
    private int _encoderStep;
    private bool _plainEncoder;
    private bool _wantSystem, _wantMic;
    private bool _sysMuted, _micMuted;
    private readonly StringBuilder _stderr = new();
    private string _lastFailure = "";

    public bool IsRecording { get; private set; }
    public bool IsPaused { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;
    public Rectangle CurrentRegion => _region;
    public int SegmentCount { get { lock (_sync) return _segments.Count; } }

    public bool SystemAvailable => _wantSystem;
    /// <summary>The mic button works in any video recording; the mic opens when pressed.</summary>
    public bool MicAvailable => !_wantGif && _audio != null;

    public bool SystemMuted
    {
        get => _sysMuted;
        set { _sysMuted = value; if (_audio != null) _audio.SystemMuted = value; }
    }

    public bool MicMuted
    {
        get => _micMuted;
        set { _micMuted = value; _audio?.SetMicLive(!value); }
    }

    public event Action<TimeSpan>? Tick;
    public event Action<string>? Failed;        // something went wrong but recording goes on
    public event Action<string>? Died;          // recording cannot continue
    public event Action? LimitReached;          // hit the automatic stop
    public event Action<bool>? PauseStateChanged;

    /// <summary>
    /// The first frame of a stretch has actually been captured. True for the
    /// very start of a recording, false after a resume. This, not the click,
    /// is the moment the clip begins.
    /// </summary>
    public event Action<bool>? CaptureLive;

    public RecorderService(SettingsService settings) => _settings = settings;

    /* ------------------------------------------------------------------ */
    /* start                                                               */
    /* ------------------------------------------------------------------ */

    public async Task<bool> StartAsync(RecordRequest req)
    {
        if (IsRecording) return false;

        // Area recordings may follow the replay's quality.
        var s = _settings.Current.ForRecording();
        _wantGif = req.AsGif;
        _region = Normalise(req.Region);
        _fps = _wantGif ? s.GifFps : ClampFps(s.Fps, _region);

        _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        lock (_stderr) _stderr.Clear();
        lock (_sync) { _segments.Clear(); _finishing.Clear(); _gpuPieces.Clear(); }
        _probeBroken = false;
        _encoderStep = 0;
        _plainEncoder = false;
        _limitFired = false;
        lock (_sync) _wallSeconds.Clear();
        _clock = new Stopwatch();

        // Both are cached after the first run, so this does not wait on FFmpeg.
        _backend = await ResolveBackendAsync(s);
        // Recordings have their own format; the encoder must support it.
        (_encoder, _codec) = await EncoderDetector.ResolveAsync(s.Encoder, s.RecordCodec);

        _dda = null;
        if (_backend == CaptureBackend.Ddagrab)
        {
            _dda = DxgiOutputs.Locate(_region);
            _ddaSeenVendor = _dda?.VendorId ?? 0;
            _chipCands = null;
            _chipTrying = false;
            // Through the chip that worked before, if FFmpeg sees the screen on another one.
            if (_dda != null) _dda = CaptureChip.Apply(_dda);
            if (_dda == null)
            {
                Log.Write("No DXGI output covers the selection; recording with GDI");
                _backend = CaptureBackend.Gdigrab;
            }
        }

        _capture = _dda != null ? LocalTo(_dda.Desktop, _region) : _region;
        (_outW, _outH) = TargetSize(_capture, s);

        // PC audio always runs (muted when not wanted), so the control bar can switch it on.
        _wantSystem = !_wantGif;
        // The mic opens now only if it's meant to be on from the start; the
        // mic button can switch it on (and off) at any point regardless.
        _wantMic = !_wantGif && s.Audio is AudioMode.MicOnly or AudioMode.Both;
        _sysMuted = s.Audio is not (AudioMode.SystemOnly or AudioMode.Both);
        _micMuted = s.Audio is not (AudioMode.MicOnly or AudioMode.Both);

        // The sound track always runs for video, so the mic button can bring
        // the microphone in mid-recording even if nothing was on at the start.
        if (!_wantGif) await StartAudioAsync(s);

        if (!StartSegment())
        {
            StopAudioHard();
            return false;
        }

        IsRecording = true;
        IsPaused = false;
        // The clock starts when the first frame lands, so the timer shows the
        // clip's real length rather than including FFmpeg's warm-up.
        _ticker?.Dispose();
        _ticker = new Timer(_ => OnTick(), null, 250, 250);

        if (s.RememberLastRegion)
        {
            // Onto the saved settings themselves: following the replay's quality,
            // "s" is a copy, and the area was set on the copy and never kept.
            _settings.Current.LastRegion = new[] { _region.X, _region.Y, _region.Width, _region.Height };
            _settings.Save();
        }
        return true;
    }

    /// <summary>
    /// Updates the timer and enforces the length limit by the real clock.
    /// FFmpeg's own limit counts frames, which runs long whenever frames are
    /// missed, so it is only kept as a backstop.
    /// </summary>
    private void OnTick()
    {
        // On a timer's thread, where an escaped error would end Persocord.
        try
        {
            var elapsed = _clock.Elapsed;
            Tick?.Invoke(elapsed);

            int max = _settings.Current.MaxLengthSeconds;
            if (max > 0 && IsRecording && !IsPaused && !_limitFired && elapsed.TotalSeconds >= max)
            {
                _limitFired = true;
                LimitReached?.Invoke();
            }
        }
        catch (Exception ex) { Log.Error("recording tick", ex); }
    }

    private async Task StartAudioAsync(AppSettings s)
    {
        try
        {
            // Separate tracks (PC audio, Discord, microphone), so any can be
            // muted later in the editor. The mic track is always there so the
            // mic button works mid-recording; its device opens only while on.
            _audio = await Task.Run(() => AudioTracks.CreateAsync(s,
                pc: _wantSystem, apps: _wantSystem && s.AppTracks, mic: true,
                micOn: _wantMic && !_micMuted, pcMuted: _sysMuted));
            _audioStart = DateTime.Now;
            if (_pendingSuppress > 0) _audio.SuppressUntil(_pendingSuppress);

            // The mic through the AI noise suppression, the rest as they are.
            int mic = _audio.MicIndex;
            string denoise = MicDenoise.Filter(s);
            bool clean = mic >= 0 && denoise.Length > 0;
            var maps = (clean ? $"-filter_complex \"[{mic}:a]{denoise}[mico]\" " : "") +
                       string.Concat(Enumerable.Range(0, _audio.Tracks.Count).Select(i => clean && i == mic ? "-map \"[mico]\" " : $"-map {i}:a "));
            _audioPath = Path.Combine(Paths.Temp, $"aud_{_sessionId}.mka");
            _audioProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Paths.FFmpeg,
                    Arguments =
                        $"-hide_banner -loglevel warning {_audio.InputArgs()}{maps}" +
                        $"-c:a aac -b:a {Math.Clamp(s.AudioBitrateKbps, 64, 320)}k -f matroska -y \"{_audioPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
            };
            _audioProc.ErrorDataReceived += (_, e) => AppendStderr(e.Data);
            _audioProc.Start();
            ChildProcess.KeepAwake(_audioProc);
            ResourceGuard.Contain(_audioProc, s);
            _audioProc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Log.Error("audio start", ex);
            StopAudioHard();
            Failed?.Invoke("Sound could not be recorded this time; the video is unaffected.");
        }
    }

    /// <summary>
    /// Replaces desktop sound with silence for a moment. The engine ignores
    /// this when it is already excluding Persocord's own sounds at the source.
    /// </summary>
    public void SuppressOwnSounds(TimeSpan duration)
    {
        long until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        _pendingSuppress = until;
        _audio?.SuppressUntil(until);
    }

    /* ------------------------------------------------------------------ */
    /* pause, resume, restart                                              */
    /* ------------------------------------------------------------------ */

    /// <summary>Instant. FFmpeg winds down in the background.</summary>
    public bool Pause()
    {
        Segment? seg;
        lock (_sync)
        {
            if (!IsRecording || IsPaused) return false;
            IsPaused = true;
            seg = _current;
            _current = null;
        }

        _clock.Stop();
        _audio?.CloseGate();
        if (seg != null) EndSegment(seg);

        PauseStateChanged?.Invoke(true);
        return true;
    }

    /// <summary>Instant for the user; capture picks up as soon as FFmpeg is ready.</summary>
    public bool Resume()
    {
        lock (_sync)
        {
            if (!IsRecording || !IsPaused) return false;
            IsPaused = false;
        }

        PauseStateChanged?.Invoke(false);

        if (!StartSegment())
        {
            lock (_sync) IsPaused = true;
            PauseStateChanged?.Invoke(true);
            Failed?.Invoke("Could not resume. Stop to keep what you have so far.");
            return false;
        }
        return true;
    }

    public bool TogglePause() => IsPaused ? Resume() : Pause();

    /// <summary>Discards the take and starts again on the same area.</summary>
    public async Task<bool> RestartAsync()
    {
        Segment? seg;
        lock (_sync)
        {
            if (!IsRecording) return false;
            seg = _current;
            _current = null;
        }

        _ticker?.Dispose();
        _ticker = null;
        _audio?.CloseGate();

        if (seg != null) { seg.EndRequested = true; Kill(seg.Proc); }
        await WaitForFinishingAsync();
        DeleteSegments();

        StopAudioHard();
        TryDelete(_audioPath);

        _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        lock (_stderr) _stderr.Clear();
        _clock = new Stopwatch();
        _limitFired = false;
        lock (_sync) _wallSeconds.Clear();

        if (!_wantGif) await StartAudioAsync(_settings.Current);

        lock (_sync) IsPaused = false;
        if (!StartSegment())
        {
            IsRecording = false;
            StopAudioHard();
            return false;
        }

        _ticker?.Dispose();
        _ticker = new Timer(_ => OnTick(), null, 250, 250);
        PauseStateChanged?.Invoke(false);
        Log.Write("Restarted");
        return true;
    }

    /* ------------------------------------------------------------------ */
    /* stop and cancel                                                     */
    /* ------------------------------------------------------------------ */

    public async Task<RecordResult> StopAsync()
    {
        Segment? seg;
        lock (_sync)
        {
            if (!IsRecording)
                return new RecordResult("", 0, 0, 0, 0, false, "Not recording.");
            IsRecording = false;
            IsPaused = false;
            seg = _current;
            _current = null;
        }

        _clock.Stop();
        _ticker?.Dispose();
        _ticker = null;
        _audio?.CloseGate();

        if (seg != null) EndSegment(seg);

        long duration = (long)_clock.Elapsed.TotalMilliseconds;

        await WaitForFinishingAsync();
        var audioFile = await FinishAudioAsync();

        List<string> usable;
        lock (_sync)
            usable = _segments.FindAll(p => File.Exists(p) && new FileInfo(p).Length > 1024);

        bool gpuFroze;
        lock (_sync) gpuFroze = _segments.Exists(p => _gpuPieces.Contains(p) && !usable.Contains(p));
        if (gpuFroze && !_gpuRouteBroken)
        {
            _gpuRouteBroken = true;
            Log.Write("A GPU-route piece came out empty; later recordings use the CPU route");
        }

        if (usable.Count == 0)
        {
            var errText = LastStderr(60);
            Log.Write("no usable segments:\n" + (errText.Length > 0 ? errText : _lastFailure));
            var why = gpuFroze
                ? "The recording came out empty. Persocord has switched to its other " +
                  "capture route for the rest of this session; please record again.\n\n" +
                  "Full details: " + Paths.LogFile
                : "The recording came out empty.\n\n" + Problems() +
                  "\n\nFull details: " + Paths.LogFile;
            DeleteSegments();
            TryDelete(audioFile);
            return new RecordResult("", _outW, _outH, duration, _fps, false, why);
        }

        string assembled = await AssembleAsync(usable, _wantGif ? null : audioFile);
        TryDelete(audioFile);

        if (string.IsNullOrEmpty(assembled))
        {
            DeleteSegments();
            return new RecordResult("", _outW, _outH, duration, _fps, false,
                "Could not put the recording together.\n\n" + Problems() + "\n\nFull details: " + Paths.LogFile);
        }

        string final = assembled;
        if (_wantGif)
        {
            final = await ToGifAsync(assembled);
            TryDelete(assembled);
            if (string.IsNullOrEmpty(final))
            {
                DeleteSegments();
                return new RecordResult("", _outW, _outH, duration, _fps, false,
                    "Could not build the GIF.\n\n" + Problems());
            }
        }

        DeleteSegments();
        return new RecordResult(final, _outW, _outH, duration, _fps, true, null);
    }

    public async Task CancelAsync()
    {
        Segment? seg;
        lock (_sync)
        {
            if (!IsRecording) return;
            IsRecording = false;
            IsPaused = false;
            seg = _current;
            _current = null;
        }

        _clock.Stop();
        _ticker?.Dispose();
        _ticker = null;

        if (seg != null) { seg.EndRequested = true; Kill(seg.Proc); }
        StopAudioHard();
        await WaitForFinishingAsync();
        DeleteSegments();
        TryDelete(_audioPath);
    }

    /* ------------------------------------------------------------------ */
    /* segments                                                            */
    /* ------------------------------------------------------------------ */

    private bool StartSegment()
    {
        var s = _settings.Current.ForRecording();
        bool gpu = GpuRouteAvailable();
        bool probe = gpu ? !_probeOffOnGpu && !_probeBroken : !_probeBroken;

        string path;
        lock (_sync) path = Path.Combine(Paths.Temp, $"seg_{_sessionId}_{_segments.Count + 1:00}.mp4");

        int remaining = s.MaxLengthSeconds <= 0
            ? 86400
            : Math.Max(1, s.MaxLengthSeconds - (int)_clock.Elapsed.TotalSeconds) + 15;

        var seg = new Segment { Path = path, UsesProbe = probe, UsesGpu = gpu, Started = DateTime.UtcNow };
        var args = BuildArgs(s, path, remaining, probe, gpu);
        Log.Write($"ffmpeg (segment) {args}");

        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Paths.FFmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            p.ErrorDataReceived += (_, e) => OnStderr(seg, e.Data);
            p.Exited += (_, _) => OnSegmentExited(seg);
            seg.Proc = p;

            if (!p.Start()) { p.Dispose(); throw new InvalidOperationException("FFmpeg did not start."); }
            lock (_sync) _procs.Add(p);
            ChildProcess.KeepAwake(p);
            ResourceGuard.Contain(p, _settings.Current);
            p.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Log.Error("ffmpeg start", ex);
            Failed?.Invoke(Paths.FFmpegFound
                ? "FFmpeg is there but would not start. The log has the details."
                : "ffmpeg.exe is missing. Put it next to Persocord.exe:\n" +
                  Path.GetDirectoryName(Environment.ProcessPath));
            return false;
        }

        lock (_sync)
        {
            _segments.Add(path);
            if (gpu) _gpuPieces.Add(path);
            _current = seg;
        }

        // If the frame report never arrives, go live on a timer rather than
        // leaving the clock stopped and the sound off.
        int wait = probe ? 2500 : 300;
        _ = Task.Delay(wait).ContinueWith(_ =>
        {
            if (!seg.FirstFrameSeen)
            {
                if (probe) Log.Write("First-frame report did not arrive; going live on a timer");
                OnFirstFrame(seg);
            }
        });
        return true;
    }

    /// <summary>Asks FFmpeg to finish, then lets it write its file in the background.</summary>
    private void EndSegment(Segment seg)
    {
        seg.EndTicks = Stopwatch.GetTimestamp();
        seg.EndRequested = true;
        NoteWallTime(seg);
        try
        {
            if (!seg.Proc.HasExited)
            {
                // 'q' is how FFmpeg is told to stop cleanly. It stops pulling
                // frames at once; only the file tidy-up happens afterwards.
                seg.Proc.StandardInput.Write('q');
                seg.Proc.StandardInput.Flush();
            }
        }
        catch (Exception ex) { Log.Error("ffmpeg quit", ex); }

        var wait = Task.Run(() =>
        {
            try
            {
                if (!seg.Proc.WaitForExit(8000))
                {
                    Log.Write("ffmpeg ignored quit, killing");
                    Kill(seg.Proc);
                }
            }
            catch { }
        });
        lock (_sync) _finishing.Add(wait);
    }

    private void OnStderr(Segment seg, string? line)
    {
        if (line == null) return;

        // showinfo reports each captured frame. The first report marks the
        // moment picture starts, so sound, the clock and the cue start with it.
        if (line.Contains("showinfo"))
        {
            if (seg.UsesProbe && !seg.FirstFrameSeen && line.Contains(" n:")) OnFirstFrame(seg);
            return;   // one line per frame; not worth keeping
        }

        AppendStderr(line);
    }

    private void OnFirstFrame(Segment seg)
    {
        lock (_sync)
        {
            if (seg.FirstFrameSeen) return;
            seg.FirstFrameSeen = true;
            if (_current != seg || seg.EndRequested || IsPaused) return;
            seg.LiveTicks = Stopwatch.GetTimestamp();
            _clock.Start();
        }

        if (_chipTrying && _dda != null)
        {
            _chipTrying = false;
            CaptureChip.Learn(_dda.DeviceName, _dda.VendorId, _ddaSeenVendor);
            Log.Write($"Recording: capturing through the {CaptureChip.Name(_dda.VendorId)} chip works; remembered.");
        }

        _audio?.OpenGate();
        bool first;
        lock (_sync) first = _segments.Count <= 1;
        CaptureLive?.Invoke(first);
    }

    /// <summary>
    /// Starts the capture again after a failed start, a moment later: Windows
    /// can take a beat to let go of the screen the last attempt held, and an
    /// instant retry failed on that alone ("Generic error in an external library").
    /// </summary>
    private bool RetrySoon()
    {
        Thread.Sleep(400);
        return !IsPaused && IsRecording && StartSegment();
    }

    private void OnSegmentExited(Segment seg)
    {
        if (seg.EndRequested || _current != seg) return;
        // "Exited" can arrive before FFmpeg's last words have been read, which
        // is why failures were logged with no reason. Wait (briefly) for them.
        try { Task.Run(() => seg.Proc.WaitForExit()).Wait(3000); } catch { }

        int code;
        try { code = seg.Proc.ExitCode; } catch { code = -1; }
        double age = (DateTime.UtcNow - seg.Started).TotalSeconds;

        // A clean exit that nobody asked for means FFmpeg's backstop limit ran out.
        if (code == 0)
        {
            seg.EndTicks = Stopwatch.GetTimestamp();
            NoteWallTime(seg);
            if (!_limitFired) { _limitFired = true; LimitReached?.Invoke(); }
            return;
        }

        var tail = Problems();
        var lastErr = LastStderr(60);
        _lastFailure = lastErr;
        Log.Write($"ffmpeg segment exited (code {code}) after {age:0.0}s:\n" + lastErr);
        // A retry starts with a clean slate: its own reasons are logged, not
        // this attempt's again (which hid why the later attempts failed).
        lock (_stderr) _stderr.Clear();

        // FFmpeg couldn't find the screen on that chip (laptops with two
        // chips): the other chips are tried before anything else changes.
        if (age < 4 && _backend == CaptureBackend.Ddagrab && _dda != null && SegmentCount <= 1 &&
            CaptureChip.OutputMissing(lastErr))
        {
            _chipCands ??= CaptureChip.Candidates(_ddaSeenVendor);
            int at = _chipCands.IndexOf(_dda.VendorId);
            if (at >= 0 && at + 1 < _chipCands.Count)
            {
                uint next = _chipCands[at + 1];
                Log.Write($"Recording: FFmpeg couldn't find the screen through the {CaptureChip.Name(_dda.VendorId)} chip; " +
                          $"trying the {CaptureChip.Name(next)} chip");
                CaptureChip.Forget(_dda.DeviceName);
                _dda = _dda with { VendorId = next, Adapter = 0 };
                _chipTrying = true;
                lock (_sync) { _segments.Remove(seg.Path); _gpuPieces.Remove(seg.Path); }
                TryDelete(seg.Path);
                if (RetrySoon()) return;
            }
        }

        // The GPU route adds two things a machine might refuse: the frame
        // report on GPU frames, then the route itself. Step down one at a time.
        if (age < 4 && seg.UsesGpu && SegmentCount <= 1)
        {
            if (seg.UsesProbe && !_probeOffOnGpu)
            {
                _probeOffOnGpu = true;
                Log.Write("GPU route refused the frame report; retrying without it:\n" + tail);
            }
            else
            {
                _gpuRouteBroken = true;
                Log.Write("GPU route refused; using the CPU conversion route:\n" + tail);
            }
            lock (_sync) { _segments.Remove(seg.Path); _gpuPieces.Remove(seg.Path); }
            TryDelete(seg.Path);
            if (RetrySoon()) return;
        }

        // Fast capture assumed but refused: remember that, and use GDI.
        if (age < 4 && _backend == CaptureBackend.Ddagrab &&
            _settings.Current.Backend == CaptureBackend.Auto && SegmentCount <= 1)
        {
            Log.Write("Fast GPU capture failed to start; switching to GDI:\n" + tail);
            EncoderDetector.RecordDdagrab(false);
            _backend = CaptureBackend.Gdigrab;
            _dda = null;
            _capture = _region;
            (_outW, _outH) = TargetSize(_capture, _settings.Current.ForRecording());
            lock (_sync) _segments.Remove(seg.Path);
            TryDelete(seg.Path);
            if (RetrySoon()) return;
        }

        // The first-frame report adds one small filter. If an unusual FFmpeg
        // build rejects them, carry on without, timing audio from launch.
        if (seg.UsesProbe && age < 4 && !_probeBroken)
        {
            _probeBroken = true;
            Log.Write("Capture failed with the first-frame probe; retrying without it:\n" + tail);
            lock (_sync) _segments.Remove(seg.Path);
            TryDelete(seg.Path);
            if (RetrySoon()) return;
        }

        // The encoder itself refused (its format at this size, a setting the
        // driver won't take, the card busy): step back to what always works,
        // H.264 with plain settings on the same card, then the CPU.
        if (age < 4 && SegmentCount <= 1 && _encoderStep < 2)
        {
            _encoderStep++;
            var was = $"{EncoderDetector.FFmpegName(_encoder, _codec)}";
            if (_encoderStep == 1 && _encoder != EncoderKind.X264)
            {
                _codec = VideoCodec.H264;
                _plainEncoder = true;
            }
            else
            {
                _encoderStep = 2;
                _encoder = EncoderKind.X264;
                _codec = VideoCodec.H264;
                _plainEncoder = true;
            }
            Log.Write($"Recording: {was} wouldn't start; trying {EncoderDetector.FFmpegName(_encoder, _codec)} instead:\n" + tail);
            lock (_sync) { _segments.Remove(seg.Path); _gpuPieces.Remove(seg.Path); }
            TryDelete(seg.Path);
            if (RetrySoon()) return;
        }

        Died?.Invoke(age < 4
            ? "Recording failed to start.\n\n" + tail
            : "Recording stopped unexpectedly.\n\n" + tail);
    }

    private void NoteWallTime(Segment seg)
    {
        if (seg.LiveTicks <= 0 || seg.EndTicks <= seg.LiveTicks) return;
        double seconds = (seg.EndTicks - seg.LiveTicks) / (double)Stopwatch.Frequency;
        lock (_sync) _wallSeconds[seg.Path] = seconds;
    }

    /// <summary>
    /// How to make one piece of video match the real time it covered.
    ///
    /// Pressing pause stops the sound at once, but FFmpeg only notices the
    /// request a moment later and keeps capturing until it does, so a piece
    /// usually comes out a little long, with the extra at the very end. That
    /// tail is cut off (a copy, instant). Speeding the whole piece up to fit,
    /// as before, spread the error across it: the picture ran ahead of the
    /// sound and caught back up at each pause.
    ///
    /// Only a piece that came out short, because capture fell behind, is
    /// slowed to fit.
    /// </summary>
    private async Task<(double stretch, double? cutAt)> FitFor(string piece)
    {
        double wall;
        lock (_sync) if (!_wallSeconds.TryGetValue(piece, out wall)) return (1.0, null);

        double video = await MediaTools.DurationSecondsAsync(piece);
        if (wall < 0.2 || video < 0.2) return (1.0, null);

        double diff = video - wall;
        Log.Write($"Timing {Path.GetFileName(piece)}: clock {wall:0.000}s, video {video:0.000}s, " +
                  (diff > 0 ? $"trimming {diff:0.000}s captured after the pause" : $"short by {-diff:0.000}s"));

        if (Math.Abs(diff) < 0.02) return (1.0, null);          // within a frame or so
        if (diff > 0) return (1.0, wall);                        // extra tail: cut it

        double f = wall / video;
        if (f > 1.5) return (1.0, null);                         // implausible; leave it alone
        return (f, null);
    }

    private static string Num(double v) => v.ToString("0.000###", System.Globalization.CultureInfo.InvariantCulture);

    private static string SettsFor(double f)
    {
        var k = f.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        return $"-bsf:v \"setts=pts=PTS*{k}:dts=DTS*{k}:duration=DURATION*{k}\" ";
    }

    private async Task WaitForFinishingAsync()
    {
        Task[] pending;
        lock (_sync) pending = _finishing.ToArray();
        try { await Task.WhenAll(pending); } catch { }
        lock (_sync) _finishing.Clear();
    }

    private void DeleteSegments()
    {
        List<string> all;
        List<Process> procs;
        lock (_sync)
        {
            all = new List<string>(_segments); _segments.Clear();
            procs = new List<Process>(_procs); _procs.Clear();
        }
        // The pieces' FFmpegs have all finished by now; their handles go too
        // (they used to wait for .NET to collect them, one set per pause).
        foreach (var proc in procs) { Kill(proc); try { proc.Dispose(); } catch { } }
        foreach (var p in all) TryDelete(p);
    }

    /* ------------------------------------------------------------------ */
    /* audio lifetime                                                      */
    /* ------------------------------------------------------------------ */

    private async Task<string?> FinishAudioAsync()
    {
        var engine = _audio;
        var proc = _audioProc;
        _audio = null;
        _audioProc = null;
        _pendingSuppress = 0;
        if (engine == null) return null;
        // Which tracks this recording kept, and their names (apps named
        // after what they carried while it ran).
        _audioLayout = engine.Layout(_audioStart);

        // Never waits forever on a sound device that stopped answering
        // (a headset switched off mid-recording, say): the video is kept.
        var finishing = Task.Run(() => engine.Finish());
        if (await Task.WhenAny(finishing, Task.Delay(15000)) != finishing)
            Log.Write("Recording sound: the sound devices didn't finish in time; keeping what was captured");

        if (proc != null)
        {
            bool exited = await Task.Run(() => proc.WaitForExit(10000));
            if (!exited) Kill(proc);
            try { proc.Dispose(); } catch { }
        }

        return File.Exists(_audioPath) && new FileInfo(_audioPath).Length > 256 ? _audioPath : null;
    }

    private void StopAudioHard()
    {
        try { _audio?.Dispose(); } catch { }
        if (_audioProc != null) { Kill(_audioProc); try { _audioProc.Dispose(); } catch { } }
        _audio = null;
        _audioProc = null;
        _pendingSuppress = 0;
    }

    /* ------------------------------------------------------------------ */
    /* assembly                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// Joins the video pieces and lays the audio track alongside, in one pass
    /// that copies rather than re-encodes. That same pass moves the index to
    /// the front of the file so Discord can start playing before the whole
    /// clip has downloaded.
    /// </summary>
    private async Task<string> AssembleAsync(List<string> pieces, string? audio)
    {
        var outPath = Path.Combine(Paths.Temp, $"rec_{_sessionId}.mp4");
        string? listFile = null;

        var retimed = new List<string>();
        try
        {
            string input;
            string retime = "";
            if (pieces.Count == 1)
            {
                input = $"-i \"{pieces[0]}\"";
                var (f, cut) = await FitFor(pieces[0]);
                if (cut is double t) retime = $"-t {Num(t)} ";
                else if (f != 1.0) retime = SettsFor(f);
            }
            else
            {
                // Each piece is retimed on its own, since each may have fallen
                // behind by a different amount. A copy, so it is quick.
                var list = new List<string>();
                foreach (var piece in pieces)
                {
                    var (f, cut) = await FitFor(piece);
                    if (f == 1.0 && cut == null) { list.Add(piece); continue; }

                    var fixedPath = Path.ChangeExtension(piece, null) + "_t.mp4";
                    var how = cut is double t ? $"-t {Num(t)} " : SettsFor(f);
                    var fix = $"-hide_banner -loglevel warning -i \"{piece}\" -map 0:v:0 -c copy " +
                              how + $"-y \"{fixedPath}\"";
                    if (await RunAsync(fix, 60000) && Usable(fixedPath))
                    {
                        retimed.Add(fixedPath);
                        list.Add(fixedPath);
                    }
                    else list.Add(piece);
                }

                listFile = Path.Combine(Paths.Temp, $"join_{_sessionId}.txt");
                var sb = new StringBuilder();
                foreach (var p in list)
                    sb.AppendLine($"file '{p.Replace("\\", "/").Replace("'", "'\\''")}'");
                await File.WriteAllTextAsync(listFile, sb.ToString());
                input = $"-f concat -safe 0 -i \"{listFile}\"";
            }

            // The sound's cushion is trimmed off its start: it comes out that
            // much after it was heard, and this lines it up with the picture.
            string audioIn = audio != null
                ? $" -ss {(CaptureRing.CushionMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} -i \"{audio}\""
                : "";
            // The tracks go in as a mix first (what players and Discord
            // play), then each one on its own for the editor.
            var mux = audio != null
                ? TrackMux.Build(1, _audioLayout.Tracks, _audioLayout.Mix, _settings.Current.AudioBitrateKbps)
                : new TrackMux.Parts("", "", "", "");
            string map = $"{mux.Filter}-map 0:v:0 {mux.Maps}";
            // HEVC gets the tag Apple devices and most players look for.
            string tracks = mux.Codecs + mux.Meta + (_codec == VideoCodec.Hevc ? "-tag:v hvc1 " : "");

            var args = $"-hide_banner -loglevel warning {input}{audioIn} {map} " +
                       $"-c copy {tracks}{retime}-movflags +faststart -y \"{outPath}\"";
            if (await RunAsync(args, 120000) && Usable(outPath)) return outPath;

            // If retiming was the problem, the untouched copy still beats nothing.
            if (retime.Length > 0)
            {
                Log.Write("assembly with retiming failed; trying without");
                args = $"-hide_banner -loglevel warning {input}{audioIn} {map} " +
                       $"-c copy {tracks}-movflags +faststart -y \"{outPath}\"";
                if (await RunAsync(args, 120000) && Usable(outPath)) return outPath;
            }

            // A copy can still be refused if a piece ended oddly. Re-encoding
            // the video is slower, but it beats losing the recording.
            Log.Write("assembly by copy failed, re-encoding the video");
            var s = _settings.Current;
            var fallback = $"-hide_banner -loglevel warning {input}{audioIn} {map} " +
                           $"-c:v {EncoderDetector.FFmpegName(_encoder, _codec)} -b:v {AutoCap(_outW, _outH, _fps) / 2}k " +
                           $"-pix_fmt {PixelFormat} -c:a copy {tracks}-movflags +faststart -y \"{outPath}\"";
            if (await RunAsync(fallback, 600000) && Usable(outPath)) return outPath;

            // Last resort: the video alone, if it was the audio that broke.
            if (audio != null)
            {
                Log.Write("assembly with audio failed; saving the video alone");
                var videoOnly = $"-hide_banner -loglevel warning {input} -map 0:v:0 " +
                                $"-c copy -movflags +faststart -y \"{outPath}\"";
                if (await RunAsync(videoOnly, 120000) && Usable(outPath)) return outPath;
            }
            return "";
        }
        catch (Exception ex)
        {
            Log.Error("assemble", ex);
            return "";
        }
        finally
        {
            TryDelete(listFile);
            foreach (var r in retimed) TryDelete(r);
        }
    }

    private async Task<string> ToGifAsync(string source)
    {
        var s = _settings.Current;
        var outPath = Path.Combine(Paths.Temp, $"rec_{_sessionId}.gif");
        int gifW = s.GifWidth > 0 ? s.GifWidth : _outW;

        // One palette built from the whole clip, rather than the default
        // 216-colour web palette, which mangles screen content badly.
        var args =
            $"-hide_banner -loglevel warning -t {s.GifMaxSeconds} -i \"{source}\" " +
            $"-filter_complex \"fps={s.GifFps},scale={gifW}:-2:flags=lanczos,split[p1][p2];" +
            $"[p1]palettegen=stats_mode=diff[pal];" +
            $"[p2][pal]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle\" " +
            $"-loop 0 -y \"{outPath}\"";

        return await RunAsync(args, 180000) && Usable(outPath) ? outPath : "";
    }

    /* ------------------------------------------------------------------ */
    /* argument building                                                   */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// The all-GPU route: captured frames go straight into NVENC, which also
    /// converts their colours, so the CPU does no video work. It needs an
    /// NVIDIA card driving the captured monitor, and no resizing, which
    /// NVENC cannot do on this route.
    /// </summary>
    private bool GpuRouteAvailable() =>
        !_gpuRouteBroken &&
        _backend == CaptureBackend.Ddagrab && _dda != null && _dda.IsNvidia &&
        _encoder == EncoderKind.Nvenc &&
        _capture.Width == _outW && _capture.Height == _outH;

    /// <summary>
    /// The picture's format going into the encoder. Graphics card encoders get
    /// NV12, the layout they work in: newer NVIDIA cards (RTX 50) refuse the
    /// planar kind outright ("CreateInputBuffer failed"), which stopped every
    /// area recording from starting. The CPU encoder gets plain 4:2:0.
    /// </summary>
    private string PixelFormat => _encoder == EncoderKind.X264 ? "yuv420p" : "nv12";

    private string BuildArgs(AppSettings s, string outPath, int limitSeconds, bool probe, bool gpu)
    {
        var a = new StringBuilder();
        // The probe line is printed at info level, so it needs that verbosity.
        a.Append(probe ? "-hide_banner -loglevel info -nostats " : "-hide_banner -loglevel warning -nostats ");

        bool ddagrab = _backend == CaptureBackend.Ddagrab && _dda != null;

        if (ddagrab)
        {
            // FFmpeg captures from the first GPU unless told otherwise, and a
            // monitor on a second GPU is invisible from there (or black). The
            // chip is named by its maker: laptops with two chips can number
            // them differently for FFmpeg than for Persocord.
            a.Append(ReplayService.DdaDevice(_dda!.Adapter, _dda.VendorId));
        }

        if (!ddagrab)
        {
            a.Append($"-f gdigrab -framerate {_fps} ")
             .Append($"-offset_x {_region.X} -offset_y {_region.Y} ")
             .Append($"-video_size {_region.Width}x{_region.Height} ")
             .Append($"-draw_mouse {(s.ShowCursor ? 1 : 0)} ")
             .Append("-i desktop ");
        }

        string chain;
        string swScale =
            $"scale={_outW}:{_outH}:flags=bicubic:out_color_matrix=bt709:out_range=tv,format={PixelFormat}";

        if (ddagrab)
        {
            // Grabbed faster than the video's rate, then exactly _fps kept
            // (see CaptureChip.GrabRate). The first-frame report comes before
            // that step, so the clock and sound start the moment picture does.
            string grab =
                $"ddagrab=output_idx={_dda!.Output}:framerate={CaptureChip.GrabRate(_fps)}" +
                $":video_size={_capture.Width}x{_capture.Height}" +
                $":offset_x={_capture.X}:offset_y={_capture.Y}" +
                $":draw_mouse={(s.ShowCursor ? 1 : 0)}" +
                (probe ? ",showinfo=checksum=0" : "") +
                $",fps={_fps}";

            // On the GPU route frames never leave the graphics card. Otherwise
            // they come off it once and are converted here.
            chain = gpu ? grab : grab + ",hwdownload,format=bgra," + swScale;
        }
        else
        {
            chain = "[0:v]" + swScale;
        }

        // showinfo passes frames straight through, reporting each one. Only
        // the first report is used. (A side branch that stopped after one
        // frame was tidier, but it crashes FFmpeg 8's filter scheduler.)
        string graph = probe && !ddagrab ? $"{chain},showinfo=checksum=0[v]" : $"{chain}[v]";

        a.Append($"-filter_complex \"{graph}\" -map \"[v]\" -an ");

        /* ---- encoder ---- */
        // AV1's quality scale runs higher (0-63) than H.264/HEVC's (0-51).
        int cq = Math.Clamp(s.QualityCq, 10, 40) + (_codec == VideoCodec.Av1 ? 8 : 0);
        int cap = AutoCap(_outW, _outH, _fps);
        bool h264 = _codec == VideoCodec.H264;
        int br = s.VideoBitrateKbps;
        bool fixedRate = br > 0;

        a.Append($"-c:v {EncoderDetector.FFmpegName(_encoder, _codec)} ");
        switch (_encoder)
        {
            case EncoderKind.Nvenc:
                // Tuned for picture quality. The low-latency tune used before
                // exists for live streaming and gives up quality to save a
                // fraction of a second nobody here needs.
                a.Append(_plainEncoder ? "-preset p4 -rc vbr " : $"-preset {s.NvencPreset} -tune hq -rc vbr ");
                a.Append(fixedRate
                    ? $"-b:v {br}k -maxrate {br * 3 / 2}k -bufsize {br * 2}k "
                    : $"-cq {cq} -b:v 0 -maxrate {cap}k -bufsize {cap * 2}k ");
                // On the GPU route the encoder works on capture's own frames,
                // and capture has only a small supply. Look-ahead and B-frames
                // make the encoder hold many at once; when capture ran dry it
                // froze silently. Without them it holds one or two. Files come
                // out a little larger for the same quality, nothing worse.
                a.Append(gpu || _plainEncoder
                    ? "-rc-lookahead 0 -bf 0 -spatial-aq 1 "
                    : "-rc-lookahead 20 -spatial-aq 1 -bf 2 ");
                if (h264) a.Append("-profile:v high ");
                break;

            case EncoderKind.Qsv:
                a.Append("-preset medium ");
                a.Append(fixedRate ? $"-b:v {br}k -maxrate {br * 3 / 2}k " : $"-global_quality {cq} ");
                break;

            case EncoderKind.Amf:
                a.Append("-quality quality ");
                a.Append(fixedRate
                    ? $"-rc vbr_peak -b:v {br}k -maxrate {br * 3 / 2}k "
                    : $"-rc cqp -qp_i {cq} -qp_p {cq + 2} " + (_codec == VideoCodec.Av1 ? "" : $"-qp_b {cq + 4} "));
                break;

            default:
                // CPU: x264, x265 or SVT-AV1, set for real-time.
                a.Append(_codec switch
                {
                    VideoCodec.Av1 => "-preset 10 ",
                    VideoCodec.Hevc => "-preset faster ",
                    _ => "-preset faster -profile:v high ",
                });
                a.Append(fixedRate
                    ? $"-b:v {br}k -maxrate {br * 3 / 2}k -bufsize {br * 2}k "
                    : _codec == VideoCodec.Av1 ? $"-crf {cq} " : $"-crf {cq} -maxrate {cap}k -bufsize {cap * 2}k ");
                break;
        }

        // A keyframe every two seconds keeps the joins between pieces clean.
        a.Append($"-g {_fps * 2} ");

        // Screen content is sRGB, which shares BT.709's primaries. The
        // conversion above uses BT.709 too, and tagging it lets players show
        // the colours as captured instead of guessing.
        // When NVENC converts the colours itself it writes its own matching tag.
        a.Append("-color_primaries bt709 -color_trc bt709 ");
        if (!gpu) a.Append("-colorspace bt709 -color_range tv ");

        a.Append($"-t {limitSeconds} -y \"{outPath}\"");
        return a.ToString();
    }

    /// <summary>
    /// Ceiling for quality-based encoding. The encoder normally stays well
    /// under it; it exists so a burst of fast motion cannot produce an
    /// absurdly large file. Screen content compresses far better than camera
    /// footage, which is why these look modest.
    /// </summary>
    private static int AutoCap(int w, int h, int fps)
    {
        long pixels = (long)w * h;
        double baseRate = pixels switch
        {
            <= 1280 * 720  => 12000,
            <= 1920 * 1080 => 25000,
            <= 2560 * 1440 => 40000,
            _              => 60000,
        };
        double fpsScale = fps <= 30 ? 0.7 : fps <= 60 ? 1.0 : fps <= 120 ? 1.5 : 2.0;
        return (int)(baseRate * fpsScale);
    }

    /* ------------------------------------------------------------------ */
    /* geometry                                                            */
    /* ------------------------------------------------------------------ */

    private static Rectangle Normalise(Rectangle r)
    {
        // yuv420p needs even dimensions, and most encoders quietly misbehave
        // on odd ones.
        int w = Math.Max(16, r.Width - (r.Width % 2));
        int h = Math.Max(16, r.Height - (r.Height % 2));
        return new Rectangle(r.X, r.Y, w, h);
    }

    private static (int w, int h) TargetSize(Rectangle region, AppSettings s)
    {
        int longEdge = s.Resolution switch
        {
            ResolutionMode.Fixed720p  => 1280,
            ResolutionMode.Fixed1080p => 1920,
            ResolutionMode.Fixed1440p => 2560,
            _ => s.NativeMaxLongEdge > 0 ? s.NativeMaxLongEdge : 0,
        };

        int w = region.Width, h = region.Height;

        // Only ever scale down. Upscaling a small region wastes bitrate and
        // makes text look worse, not better.
        if (longEdge > 0 && Math.Max(w, h) > longEdge)
        {
            double f = longEdge / (double)Math.Max(w, h);
            w = (int)Math.Round(w * f);
            h = (int)Math.Round(h * f);
        }

        w -= w % 2; h -= h % 2;
        return (Math.Max(16, w), Math.Max(16, h));
    }

    /// <summary>
    /// The selection relative to its monitor's top-left corner, which is how
    /// ddagrab wants the offset. Clamped to the monitor, since ddagrab cannot
    /// read past its edges; a selection straddling two monitors keeps the
    /// part on the one holding its centre.
    /// </summary>
    private static Rectangle LocalTo(Rectangle monitor, Rectangle region)
    {
        int x = Math.Max(0, region.X - monitor.X);
        int y = Math.Max(0, region.Y - monitor.Y);
        int w = Math.Min(region.Width, monitor.Width - x);
        int h = Math.Min(region.Height, monitor.Height - y);
        w -= w % 2; h -= h % 2;
        return new Rectangle(x, y, Math.Max(16, w), Math.Max(16, h));
    }

    private static int ClampFps(int requested, Rectangle region)
    {
        // Desktop duplication cannot produce frames faster than the display
        // refreshes, so a 240 setting on a 144 Hz panel just duplicates frames
        // and inflates the file.
        int refresh = 60;
        try
        {
            var centre = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
            foreach (var sc in Screen.AllScreens)
                if (sc.Bounds.Contains(centre)) { refresh = MonitorRefresh(sc); break; }
        }
        catch { }

        return Math.Clamp(Math.Min(requested, Math.Max(refresh, 30)), 5, 480);
    }

    private static int MonitorRefresh(Screen sc)
    {
        try
        {
            var dm = new Native.DEVMODE();
            dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DEVMODE));
            if (Native.EnumDisplaySettings(sc.DeviceName, -1, ref dm))
                return dm.dmDisplayFrequency > 0 ? dm.dmDisplayFrequency : 60;
        }
        catch { }
        return 60;
    }

    private static Task<CaptureBackend> ResolveBackendAsync(AppSettings s)
    {
        if (s.Backend != CaptureBackend.Auto) return Task.FromResult(s.Backend);

        // Never wait on the check here. Fast capture works on nearly every
        // machine, so assume it until proven otherwise; a failed start falls
        // back to GDI on its own.
        var known = EncoderDetector.KnownDdagrab();
        return Task.FromResult(known == false ? CaptureBackend.Gdigrab : CaptureBackend.Ddagrab);
    }

    /* ------------------------------------------------------------------ */
    /* helpers                                                             */
    /* ------------------------------------------------------------------ */

    private async Task<bool> RunAsync(string args, int timeoutMs)
    {
        try
        {
            Log.Write($"ffmpeg {args}");
            var psi = new ProcessStartInfo
            {
                FileName = Paths.FFmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;

            p.ErrorDataReceived += (_, e) => AppendStderr(e.Data);
            p.BeginErrorReadLine();

            if (!await Task.Run(() => p.WaitForExit(timeoutMs)))
            {
                Kill(p);
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error("ffmpeg run", ex);
            return false;
        }
    }

    private void AppendStderr(string? line)
    {
        if (line == null) return;
        lock (_stderr)
        {
            _stderr.AppendLine(line);
            if (_stderr.Length > 40000) _stderr.Remove(0, 20000);
        }
    }

    private string LastStderr(int lines)
    {
        lock (_stderr)
        {
            var all = _stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var take = Math.Min(lines, all.Length);
            return take == 0 ? "" : string.Join("\n", all[^take..]).Trim();
        }
    }

    /// <summary>
    /// The lines worth showing a person: FFmpeg's complaints, without the
    /// stream listings it prints at startup.
    /// </summary>
    private string Problems()
    {
        string[] all;
        lock (_stderr) all = _stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var hits = new List<string>();
        foreach (var raw in all)
        {
            var l = raw.Trim();
            var low = l.ToLowerInvariant();
            if (low.Contains("error") || low.Contains("fail") || low.Contains("invalid") ||
                low.Contains("cannot") || low.Contains("unable") || low.Contains("not supported") ||
                low.Contains("no such"))
                hits.Add(l);
        }
        if (hits.Count == 0) return "FFmpeg reported no error; it may have stopped responding.";
        return string.Join("\n", hits.GetRange(Math.Max(0, hits.Count - 3), Math.Min(3, hits.Count)));
    }

    private static bool Usable(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 1024;

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>
/// Windows 11 puts background programs without a window into "efficiency
/// mode", slowing them on purpose. FFmpeg is exactly that kind of program, and
/// slowed down it drops frames. This opts each FFmpeg that Persocord starts
/// out of it and nudges its priority up.
/// </summary>
internal static class ChildProcess
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint ThrottleExecutionSpeed = 0x1;
    private const uint ThrottleIgnoreTimerResolution = 0x4;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass,
        ref PowerThrottlingState info, int size);

    public static void KeepAwake(Process p)
    {
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        try
        {
            var state = new PowerThrottlingState
            {
                Version = 1,
                ControlMask = ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution,
                StateMask = 0,   // both switched off
            };
            SetProcessInformation(p.Handle, ProcessPowerThrottling, ref state,
                System.Runtime.InteropServices.Marshal.SizeOf<PowerThrottlingState>());
        }
        catch { /* older Windows: nothing to switch off */ }
    }
}
