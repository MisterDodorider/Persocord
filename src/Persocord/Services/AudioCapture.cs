using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>Audio device listing for the settings window.</summary>
public static class AudioSource
{
    public record Device(string Id, string Name, bool IsDefault);

    public static List<Device> ListMics() => List(DataFlow.Capture, Role.Communications);
    public static List<Device> ListOutputs() => List(DataFlow.Render, Role.Multimedia);

    private static List<Device> List(DataFlow flow, Role role)
    {
        var result = new List<Device>();
        try
        {
            using var mm = new MMDeviceEnumerator();
            string defaultId = "";
            try { defaultId = mm.GetDefaultAudioEndpoint(flow, role).ID; } catch { }
            foreach (var d in mm.EnumerateAudioEndPoints(flow, DeviceState.Active))
                result.Add(new Device(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        catch (Exception ex) { Log.Error("device list", ex); }
        return result;
    }
}

/// <summary>
/// Captures desktop sound and microphone, mixes them, and streams the result
/// to an encoder as one steady stereo track.
///
/// Doing this in-process rather than handing raw device output to FFmpeg
/// fixes three things at once. Windows sends no data at all while nothing is
/// playing, so a naive pipe silently deletes every quiet moment; here a
/// wall-clock pump writes silence through those gaps. Devices report their
/// samples in formats that have to be read correctly — floating point hidden
/// behind an "extensible" label is the one that was being misread. And
/// because Persocord decides exactly when samples start flowing, audio can
/// begin at the precise moment the first video frame is captured.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int Rate = 48000;
    public const int Channels = 2;

    private readonly MixingSampleProvider _mixer =
        new(WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels)) { ReadFully = true };

    // Sound on its way to FFmpeg: pooled buffers (a new one every 10 ms per
    // track was steady garbage), each handed back once written.
    private readonly BlockingCollection<(byte[] Buf, int Len)> _out = new(new ConcurrentQueue<(byte[], int)>());
    private readonly NamedPipeServerStream _pipe;
    private readonly Thread _pump;
    private readonly Thread _writer;
    private readonly long _t0 = Stopwatch.GetTimestamp();
    private long _pulled;
    private volatile bool _running = true;
    private volatile bool _gate;
    private long _suppressUntil;

    private Channel? _sys;
    private Channel? _mic;
    private bool _sysMuted;
    private bool _micMuted;

    public string PipePath { get; }

    /// <summary>
    /// True once desktop sound is being captured with Persocord's own process
    /// left out, so cue sounds cannot end up in a recording.
    /// </summary>
    public bool ExcludesOwnSounds { get; private set; }

    /// <summary>Windows refused the microphone (its privacy setting). Raised once per launch.</summary>
    public static event Action? MicBlocked;
    private static bool _saidMicBlocked;

    public AudioEngine(AppSettings s, bool wantSystem, bool wantMic, bool systemMuted, bool micMuted)
    {
        _sysMuted = systemMuted;
        _micMuted = micMuted;
        _settings = s;

        var name = $"persocord_audio_{Guid.NewGuid():N}";
        PipePath = $@"\\.\pipe\{name}";
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 20);

        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "Persocord audio pump", Priority = ThreadPriority.AboveNormal };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Persocord audio writer" };
        _pump.Start();
        _writer.Start();

        // Opening devices takes a moment; the mixer produces silence until they
        // arrive, and the wall-clock pump keeps the track in step regardless.
        _ = Task.Run(() => AttachAsync(s, wantSystem, wantMic));
    }

    /* ---- control -------------------------------------------------------- */

    /// <summary>Start sending audio. Called when the first video frame lands.</summary>
    public void OpenGate() => _gate = true;

    /// <summary>Stop sending audio. Called the instant recording pauses or stops.</summary>
    public void CloseGate() => _gate = false;

    /// <summary>
    /// Writes silence in place of desktop sound until the given moment. Used
    /// while a cue plays, when Windows could not exclude our own sounds.
    /// </summary>
    public void SuppressUntil(long stopwatchTicks) =>
        Interlocked.Exchange(ref _suppressUntil, stopwatchTicks);

    public bool SystemMuted
    {
        get => _sysMuted;
        set { _sysMuted = value; _sys?.Apply(value); _own?.Apply(value); }
    }

    /// <summary>Persocord's own sound (its player and editor), part of desktop sound; see AttachAsync.</summary>
    private Channel? _own;

    public bool MicMuted
    {
        get => _micMuted;
        set { _micMuted = value; _mic?.Apply(value); }
    }

    private AppSettings? _settings;
    private int _micOpening;

    /* ---- apps' sound ------------------------------------------------------- */

    private bool _procMuted;
    private readonly SemaphoreSlim _procLock = new(1, 1);

    public bool ProcessMuted
    {
        get => _procMuted;
        set
        {
            _procMuted = value;
            lock (_includes) foreach (var ch in _includes.Values) ch.Apply(value);
        }
    }

    /// <summary>Whether any app captured on this track has delivered real sound.</summary>
    public bool AppSoundHeard { get { lock (_includes) return _includes.Values.Any(c => c.HeardSound); } }

    /// <summary>How many app captures are running on this track.</summary>
    public int AppCaptures { get { lock (_includes) return _includes.Count; } }

    /* ---- several apps mixed into this one track ---------------------------- */

    private readonly Dictionary<int, Channel> _includes = new();

    /// <summary>
    /// Makes this track carry exactly these apps (each with anything it
    /// launched), mixed together: one app for an app's own track, several for
    /// "Other apps". Captures are added and removed while running.
    /// </summary>
    public async Task SetAppsAsync(IReadOnlyCollection<int> pids)
    {
        await _procLock.WaitAsync();
        try
        {
            List<int> gone;
            lock (_includes) gone = _includes.Keys.Where(k => !pids.Contains(k)).ToList();
            foreach (var pid in gone)
            {
                Channel ch;
                lock (_includes) { ch = _includes[pid]; _includes.Remove(pid); }
                _mixer.RemoveMixerInput(ch.Output);
                ch.Stop();
            }
            foreach (var pid in pids)
            {
                lock (_includes) if (_includes.ContainsKey(pid)) continue;
                if (!_running) break;
                try
                {
                    var capture = await ProcessLoopbackCapture.IncludingAsync(pid, TimeSpan.FromSeconds(3));
                    if (!_running) { capture.Dispose(); break; }
                    var ch = Attach(capture, 1f, _procMuted, $"app {pid}");
                    if (ch != null) lock (_includes) _includes[pid] = ch;
                }
                catch (Exception ex) { Log.Error($"app sound {pid}", ex); }
            }
        }
        finally { _procLock.Release(); }
    }

    /// <summary>
    /// Records one app that runs as administrator through the administrator
    /// helper (see <see cref="ElevatedAudio"/>). False when that can't be done:
    /// the normal capture is used instead (it hears nothing from such an app,
    /// so the editor's Unknown track covers its sound).
    /// </summary>
    /// <param name="stillWanted">False once the track has moved on (the app closed while Windows was asking).</param>
    public async Task<bool> SetElevatedAppAsync(int pid, string name, Func<bool> stillWanted)
    {
        // Asking Windows can take a while (the person has to answer), so this
        // waits without holding the lock; a newer change wins.
        var piped = await ElevatedAudio.CaptureAsync(pid, name);
        await _procLock.WaitAsync();
        try
        {
            if (!stillWanted()) { piped?.Dispose(); return true; }
            List<int> gone;
            lock (_includes) gone = _includes.Keys.Where(k => k != pid).ToList();
            foreach (var old in gone)
            {
                Channel ch;
                lock (_includes) { ch = _includes[old]; _includes.Remove(old); }
                _mixer.RemoveMixerInput(ch.Output);
                ch.Stop();
            }
            if (!_running) { piped?.Dispose(); return false; }
            lock (_includes)
                if (_includes.TryGetValue(pid, out var existing))
                {
                    _includes.Remove(pid);
                    _mixer.RemoveMixerInput(existing.Output);
                    existing.Stop();
                }
            if (piped != null)
            {
                var ch = Attach(piped, 1f, _procMuted, $"app {pid} (administrator)");
                if (ch != null) { lock (_includes) _includes[pid] = ch; return true; }
            }
            try
            {
                var capture = await ProcessLoopbackCapture.IncludingAsync(pid, TimeSpan.FromSeconds(3));
                if (!_running) { capture.Dispose(); return false; }
                var ch = Attach(capture, 1f, _procMuted, $"app {pid}");
                if (ch != null) lock (_includes) _includes[pid] = ch;
            }
            catch (Exception ex) { Log.Error($"app sound {pid}", ex); }
            return false;
        }
        finally { _procLock.Release(); }
    }

    /// <summary>
    /// The mic button: on opens the microphone (if it isn't already) and
    /// lets it through; off closes the device, so Windows' microphone
    /// indicator only shows while the mic is really on.
    /// </summary>
    public void SetMicLive(bool on)
    {
        _micMuted = !on;
        if (on)
        {
            if (_mic != null) { _mic.Apply(false); return; }
            if (_settings != null && Interlocked.Exchange(ref _micOpening, 1) == 0)
                _ = Task.Run(() =>
                {
                    try { OpenMic(_settings); }
                    finally { Interlocked.Exchange(ref _micOpening, 0); }
                });
        }
        else
        {
            var mic = _mic;
            _mic = null;
            if (mic != null)
            {
                _mixer.RemoveMixerInput(mic.Output);
                mic.Stop();
            }
        }
    }

    private void OpenMic(AppSettings s)
    {
        try
        {
            using var mm = new MMDeviceEnumerator();
            var dev = string.IsNullOrEmpty(s.MicDeviceId)
                ? mm.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : mm.GetDevice(s.MicDeviceId);
            var capture = new WasapiCapture(dev) { ShareMode = AudioClientShareMode.Shared };
            if (!_running || _micMuted) { capture.Dispose(); return; }   // turned off again meanwhile
            _mic = Attach(capture, s.MicGainPercent / 100f, _micMuted, "microphone", s.MicNoiseGate, s.MicLimiter, s.MicGateDb, s.MicLimiterDb);
        }
        catch (Exception ex) { Log.Error("microphone", ex); }
    }

    /* ---- devices -------------------------------------------------------- */

    private async Task AttachAsync(AppSettings s, bool wantSystem, bool wantMic)
    {
        if (wantSystem)
        {
            IWaveIn? capture = null;

            // Capturing "everything except this process" keeps cue sounds out
            // of recordings without any timing tricks. It needs a recent
            // Windows build and only applies to the default output, so a
            // specific device choice uses the classic route.
            if (string.IsNullOrEmpty(s.SystemDeviceId))
            {
                try
                {
                    var pc = await ProcessLoopbackCapture.ExcludingAsync(
                        Environment.ProcessId, TimeSpan.FromSeconds(3));
                    // Starting is where an unsupported Windows build refuses,
                    // so do it here, inside the fallback.
                    try { pc.StartRecording(); }
                    catch { pc.Dispose(); throw; }
                    capture = pc;
                    ExcludesOwnSounds = true;
                    Log.Write("Desktop sound: capturing all apps except Persocord");
                }
                catch (Exception ex)
                {
                    Log.Write("Process-excluding capture unavailable (" + ex.Message + "); using classic loopback");
                }
            }

            if (capture == null)
            {
                try
                {
                    using var mm = new MMDeviceEnumerator();
                    var dev = string.IsNullOrEmpty(s.SystemDeviceId)
                        ? mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : mm.GetDevice(s.SystemDeviceId);
                    capture = new WasapiLoopbackCapture(dev);
                }
                catch (Exception ex) { Log.Error("desktop sound", ex); }
            }

            if (capture != null && _running)
                _sys = Attach(capture, s.SystemGainPercent / 100f, _sysMuted, "desktop");
            else capture?.Dispose();   // stopped while the device was opening: it never keeps running

            // Leaving Persocord out kept its cues out, but also its player and
            // editor: a clip watched in Persocord was silent in replays. Its own
            // sound is taken separately and added back, silenced only while a
            // cue plays, so it's heard like any other app's and cues still never are.
            if (ExcludesOwnSounds && _sys != null && _running)
            {
                try
                {
                    var own = await ProcessLoopbackCapture.IncludingAsync(Environment.ProcessId, TimeSpan.FromSeconds(3));
                    if (!_running) own.Dispose();
                    else _own = Attach(own, s.SystemGainPercent / 100f, _sysMuted, "Persocord's own sound (cues left out)",
                                       quiet: () => CuePlayer.Sounding);
                    // Stopped just as it was added: it doesn't run on unseen.
                    if (!_running && Interlocked.Exchange(ref _own, null) is { } late) late.Stop();
                }
                catch (Exception ex) { Log.Write("Persocord's own sound can't be added to desktop sound (" + ex.Message + "); its player stays out of recordings."); }
            }
        }

        if (wantMic && !_micMuted && _mic == null) OpenMic(s);
    }

    /// <param name="quiet">While this says so, the capture's sound is replaced by silence (Persocord's own sound during a cue).</param>
    private Channel? Attach(IWaveIn capture, float gain, bool muted, string label, bool gate = false, bool limit = false,
                            int gateDb = -38, int limitDb = -8, Func<bool>? quiet = null)
    {
        try
        {
            var fmt = capture.WaveFormat;
            int outCh = fmt.Channels >= 2 ? 2 : 1;
            var ring = new CaptureRing(fmt.SampleRate, outCh);

            Channel? self = null;
            // Reused for every buffer: each capture delivers on one thread of its
            // own, and the ring copies what it's given before this returns.
            float[] samples = Array.Empty<float>(), frame = Array.Empty<float>();
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                int n = Pcm.ToFloat(e.Buffer, e.BytesRecorded, fmt, outCh, ref samples, ref frame);
                if (quiet != null && quiet()) Array.Clear(samples, 0, n);
                ring.Write(samples, n);
                // Notes the first real sound, so a capture that only ever
                // delivers silence can be told apart and left out.
                if (self != null && !self.HeardSound)
                    for (int i = 0; i < n; i++)
                        if (samples[i] > 0.0005f || samples[i] < -0.0005f) { self.HeardSound = true; break; }
            };

            ISampleProvider chain = ring;
            if (fmt.SampleRate != Rate) chain = new WdlResamplingSampleProvider(chain, Rate);
            if (outCh == 1) chain = new MonoToStereoSampleProvider(chain);
            // The volume comes before the sensitivity line and the loudness
            // limit, so they act on the level the meter in Settings shows (it
            // shows the mic with its volume). At 100% nothing changes.
            if (Math.Abs(gain - 1f) > 0.001f && (gate || limit))
            {
                chain = new VolumeSampleProvider(chain) { Volume = gain };
                gain = 1f;
            }
            if (gate) chain = new NoiseGate(chain, gateDb);
            if (limit) chain = new Limiter(chain, limitDb);

            var volume = new VolumeSampleProvider(chain);
            var channel = new Channel(capture, volume, gain);
            self = channel;
            channel.Apply(muted);

            _mixer.AddMixerInput(volume);
            capture.StartRecording();

            Log.Write($"Audio {label}: {fmt.SampleRate} Hz, {fmt.Channels} ch, " +
                      $"{fmt.BitsPerSample}-bit {fmt.Encoding}");
            // Stopped while this device was opening (a quick cancel): it goes
            // at once, rather than recording on unseen until Persocord closes.
            if (!_running)
            {
                _mixer.RemoveMixerInput(volume);
                channel.Stop();
                return null;
            }
            return channel;
        }
        catch (Exception ex)
        {
            Log.Error("attach " + label, ex);
            // Windows' privacy setting blocks desktop apps from the mic: said
            // once, with where to change it, instead of failing silently.
            if (ex is UnauthorizedAccessException && label.Contains("mic", StringComparison.OrdinalIgnoreCase) && !_saidMicBlocked)
            {
                _saidMicBlocked = true;
                MicBlocked?.Invoke();
            }
            try { capture.Dispose(); } catch { }
            return null;
        }
    }

    /* ---- pump and writer ------------------------------------------------ */

    private void PumpLoop()
    {
        var buf = new float[Rate * Channels / 10];
        long freq = Stopwatch.Frequency;

        while (_running)
        {
            long due = (Stopwatch.GetTimestamp() - _t0) * Rate / freq;
            long frames = due - _pulled;
            // Woken every 10 ms and moved in 10 ms steps or more: a few
            // milliseconds made hundreds of tiny wake-ups and buffers a second
            // for every track (the sound's cushion is far longer than this).
            if (frames < Rate / 100) { Thread.Sleep(10); continue; }
            if (frames > Rate / 10) frames = Rate / 10;

            int count = (int)frames * Channels;
            try { _mixer.Read(buf, 0, count); }
            catch (Exception ex) { Log.Error("audio mix", ex); Array.Clear(buf, 0, count); }
            _pulled += frames;
            _level.Process(buf, count);

            if (!_gate) continue;

            // During a cue, when our own sounds could not be excluded at the
            // source, silence takes their place. The sample count still
            // advances, so audio stays in step with video.
            bool silence = !ExcludesOwnSounds &&
                           Stopwatch.GetTimestamp() < Interlocked.Read(ref _suppressUntil);

            // FFmpeg stuck and not reading at all: past a minute queued, sound is
            // let go rather than piling up in memory for as long as it stays stuck.
            if (Interlocked.Read(ref _queued) > (long)Rate * Channels * 2 * 60)
            {
                if (!_warnedStuck) { _warnedStuck = true; Log.Write("Sound: FFmpeg stopped reading (over a minute queued); newer sound is dropped until it catches up."); }
                continue;
            }

            int len = count * 2;
            var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
            for (int i = 0; i < count; i++)
            {
                float v = silence ? 0f : buf[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                short sv = (short)(v * 32767f);
                bytes[i * 2] = (byte)sv;
                bytes[i * 2 + 1] = (byte)(sv >> 8);
            }
            // (Closed under it by a cancel: the pump just stops, it never throws on its own thread.)
            try { _out.Add((bytes, len)); }
            catch (InvalidOperationException) { System.Buffers.ArrayPool<byte>.Shared.Return(bytes); break; }

            // Sound piling up means FFmpeg isn't keeping up (a CPU limit set
            // too tight, say). Worth a line in the log, once.
            // (Not during warm-up: sound always queues while FFmpeg starts.)
            if (Interlocked.Add(ref _queued, len) > Rate * Channels * 2 * 3 && !_warnedBacklog &&
                _connectedTicks != 0 && Stopwatch.GetElapsedTime(_connectedTicks).TotalSeconds > 10)
            {
                _warnedBacklog = true;
                Log.Write("Sound is waiting on FFmpeg (over 3 seconds queued): it's running slower than real time. " +
                          "Raising the CPU limit in Settings → Replay → Safety limits may help.");
            }
        }
    }

    // Recorded sound came out quiet next to other apps' clips: everything is
    // saved 30% louder, with a limiter so loud moments never distort.
    private readonly Loudness _level = new(1.3f, Rate, Channels);

    private long _queued;

    /// <summary>Sound written but not yet taken by FFmpeg, in seconds (for the health line).</summary>
    public double QueuedSeconds => Interlocked.Read(ref _queued) / (double)(Rate * Channels * 2);

    private long _connectedTicks;
    private bool _warnedBacklog, _warnedStuck;

    private void WriteLoop()
    {
        try { _pipe.WaitForConnection(); _connectedTicks = Stopwatch.GetTimestamp(); }
        catch { return; }

        try
        {
            foreach (var (chunk, len) in _out.GetConsumingEnumerable())
            {
                try { _pipe.Write(chunk, 0, len); }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(chunk); }
                Interlocked.Add(ref _queued, -len);
            }
        }
        catch (IOException) { /* the encoder went away */ }
        catch (ObjectDisposedException) { }
        finally
        {
            try { _pipe.Flush(); } catch { }
            try { _pipe.Dispose(); } catch { }
        }
    }

    /// <summary>Stops capture and sends everything buffered, then closes the stream.</summary>
    public void Finish()
    {
        _gate = false;
        _running = false;
        try { _pump.Join(1000); } catch { }
        _out.CompleteAdding();
        try { _writer.Join(5000); } catch { }
        StopDevices();
    }

    /// <summary>Abandons everything, for cancel and restart.</summary>
    public void Dispose()
    {
        _gate = false;
        _running = false;
        try { _out.CompleteAdding(); } catch { }
        try { _pipe.Dispose(); } catch { }
        StopDevices();
    }

    private void StopDevices()
    {
        _sys?.Stop();
        _own?.Stop();
        _mic?.Stop();
        lock (_includes) { foreach (var ch in _includes.Values) ch.Stop(); _includes.Clear(); }
        _sys = null;
        _own = null;
        _mic = null;
    }

    /* ---- pieces --------------------------------------------------------- */

    private sealed class Channel
    {
        private readonly IWaveIn _capture;
        private readonly VolumeSampleProvider _volume;
        private readonly float _gain;

        public ISampleProvider Output => _volume;

        /// <summary>Has any real (non-silent) sound come through?</summary>
        public volatile bool HeardSound;

        public Channel(IWaveIn capture, VolumeSampleProvider volume, float gain)
        {
            _capture = capture;
            _volume = volume;
            _gain = gain;
        }

        /// <summary>Muting zeroes the level rather than closing the device, so the stream never breaks.</summary>
        public void Apply(bool muted) => _volume.Volume = muted ? 0f : _gain;

        public void Stop()
        {
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
        }
    }
}

/// <summary>
/// Samples arriving from a device, handed out on a steady clock: a jitter
/// buffer. Windows delivers sound in bursts (more so while a game loads the
/// PC), so it keeps a small cushion (CushionMs) before playing, and refills
/// it after any hiccup rather than crackling. Bursts up to a quarter second
/// are absorbed instead of thrown away, and slow clock drift is corrected by
/// playing a touch faster or slower, smoothly (skipping samples, as before
/// 1.5.50, was heard as static). (It used to
/// allow only 80 ms: late sound became silence, then the burst that followed
/// was cut, which broke every track up.) A quiet device simply gives silence.
/// </summary>
internal sealed class CaptureRing : ISampleProvider
{
    /// <summary>The cushion: sound comes out this much later than it went in (compensated when saving).</summary>
    public const int CushionMs = 120;

    // How far from the cushion the level may drift before it's eased back,
    // and the most the playback rate is nudged to do it (0.4%: about 7
    // cents, not heard, where skipping samples was heard as static).
    private const int DeadbandMs = 25;
    private const double MaxNudge = 0.004;

    private readonly float[] _buf;
    private readonly int _channels, _rate;
    private readonly int _target, _high, _deadband;
    private readonly float[] _hist;               // the frame just before the read position
    private readonly object _lock = new();
    private int _read;
    private int _count;
    private double _frac;                          // how far between two frames the read position is
    private bool _primed;

    public WaveFormat WaveFormat { get; }

    public CaptureRing(int rate, int channels)
    {
        _channels = channels;
        _rate = rate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        _buf = new float[rate * channels * 2];
        _hist = new float[channels];
        int Ms(int ms) => rate * ms / 1000 * channels;
        _target = Ms(CushionMs);
        _deadband = Ms(DeadbandMs);
        _high = Ms(Math.Max(250, CushionMs * 4));
    }

    public void Write(float[] src) => Write(src, src.Length);

    /// <summary>The first <paramref name="length"/> samples of a buffer.</summary>
    public void Write(float[] src, int length)
    {
        lock (_lock)
        {
            int n = Math.Min(length, src.Length);
            int start = 0;
            if (n > _buf.Length) { start = n - _buf.Length; n = _buf.Length; }

            int overflow = _count + n - _buf.Length;
            if (overflow > 0)
            {
                overflow += (_channels - overflow % _channels) % _channels;
                _read = (_read + overflow) % _buf.Length;
                _count -= overflow;
            }

            int write = (_read + _count) % _buf.Length;
            for (int i = 0; i < n; i++)
                _buf[(write + i) % _buf.Length] = src[start + i];
            _count += n;
        }
    }

    private float At(int frame, int c) => _buf[(_read + frame * _channels + c) % _buf.Length];

    private void Advance()
    {
        for (int c = 0; c < _channels; c++) _hist[c] = _buf[(_read + c) % _buf.Length];
        _read = (_read + _channels) % _buf.Length;
        _count -= _channels;
    }

    public int Read(float[] dst, int offset, int count)
    {
        int asked = count;
        count -= count % _channels;
        if (asked > count) Array.Clear(dst, offset + count, asked - count);
        lock (_lock)
        {
            // Filling the cushion (at the start, or after running dry).
            if (!_primed)
            {
                if (_count >= _target) { _primed = true; _frac = 0; Array.Clear(_hist); }
                else { Array.Clear(dst, offset, count); return asked; }
            }

            if (_count > _high)
            {
                // A long stall then a flood: back to the cushion in one step.
                int drop = _count - _target;
                drop -= drop % _channels;
                _read = (_read + drop) % _buf.Length;
                _count -= drop;
            }

            // The device's clock never quite matches: the level drifts. It's
            // eased back by playing a touch faster or slower (smoothly
            // interpolated), never by dropping samples, which clicked
            // hundreds of times a second and sounded like static.
            int off = _count - _target;
            double step = 1;
            if (Math.Abs(off) > _deadband)
                step = 1 + Math.Clamp(off / (double)(_rate * _channels) * 0.05, -MaxNudge, MaxNudge);
            else if (_frac > 0)
                step = 1 + 0.002;                  // finish the step in progress, then exact again

            int frames = count / _channels, done = 0;
            if (step == 1 && _frac == 0)
            {
                // The usual case: an exact copy.
                int n = Math.Min(frames, _count / _channels);
                for (int i = 0; i < n * _channels; i++) dst[offset + i] = _buf[(_read + i) % _buf.Length];
                if (n > 0)
                {
                    for (int c = 0; c < _channels; c++) _hist[c] = dst[offset + (n - 1) * _channels + c];
                    _read = (_read + n * _channels) % _buf.Length;
                    _count -= n * _channels;
                }
                done = n;
            }
            else
            {
                // Four-point Hermite between frames: smooth, keeps the treble.
                while (done < frames && _count >= 3 * _channels)
                {
                    float t = (float)_frac;
                    for (int c = 0; c < _channels; c++)
                    {
                        float y0 = _hist[c], y1 = At(0, c), y2 = At(1, c), y3 = At(2, c);
                        float c1 = 0.5f * (y2 - y0);
                        float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
                        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
                        dst[offset + done * _channels + c] = ((c3 * t + c2) * t + c1) * t + y1;
                    }
                    done++;
                    _frac += step;
                    while (_frac >= 1) { Advance(); _frac -= 1; }
                    // Back on a whole frame: exact copies from here.
                    if (Math.Abs(off) <= _deadband && _frac < 0.0025) { _frac = 0; step = 1; }
                }
            }

            if (done < frames)
            {
                // Ran dry: silence for the rest, and the cushion refills.
                Array.Clear(dst, offset + done * _channels, (frames - done) * _channels);
                _primed = false;
            }
        }
        return asked;
    }
}

/// <summary>Turns raw device buffers into float samples, folding surround down to stereo.</summary>
internal static class Pcm
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static float[] ToFloat(byte[] buf, int bytes, WaveFormat fmt, int outCh)
    {
        float[] output = Array.Empty<float>(), frame = Array.Empty<float>();
        int n = ToFloat(buf, bytes, fmt, outCh, ref output, ref frame);
        return output.Length == n ? output : output[..n];
    }

    /// <summary>
    /// The same, into buffers kept by the caller and reused (grown when
    /// needed): a capture delivers a buffer every 10 ms, and a new array for
    /// each one was garbage a hundred times a second per sound track.
    /// Returns how many samples were written.
    /// </summary>
    public static int ToFloat(byte[] buf, int bytes, WaveFormat fmt, int outCh, ref float[] output, ref float[] frame)
    {
        // Devices usually label float audio as "extensible" with the real
        // format tucked inside. Reading that as integers is what garbled the
        // sound before.
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);

        int inCh = Math.Max(1, fmt.Channels);
        int bps = Math.Max(1, fmt.BitsPerSample / 8);
        int frames = bytes / (bps * inCh);

        if (output.Length < frames * outCh) output = new float[frames * outCh];
        // Downmix reads the frame's length, so it's exactly the device's channels.
        if (frame.Length != inCh) frame = new float[inCh];
        int pos = 0;

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < inCh; c++, pos += bps)
                frame[c] = Sample(buf, pos, bps, isFloat);
            Downmix(frame, inCh, output, f * outCh, outCh);
        }
        return frames * outCh;
    }

    private static float Sample(byte[] b, int i, int bps, bool isFloat) => (isFloat, bps) switch
    {
        (true, 4) => BitConverter.ToSingle(b, i),
        (true, 8) => (float)BitConverter.ToDouble(b, i),
        (false, 2) => BitConverter.ToInt16(b, i) / 32768f,
        (false, 3) => ((b[i] << 8 | b[i + 1] << 16 | b[i + 2] << 24) >> 8) / 8388608f,
        (false, 4) => BitConverter.ToInt32(b, i) / 2147483648f,
        (false, 1) => (b[i] - 128) / 128f,
        _ => 0f,
    };

    /// <summary>
    /// Folds surround layouts into stereo without the level loss a generic
    /// downmix applies. Channel order follows the Windows standard layouts.
    /// </summary>
    private static void Downmix(float[] s, int inCh, float[] o, int at, int outCh)
    {
        if (outCh == 1) { o[at] = s[0]; return; }

        const float k = 0.7071f;
        float l = s[0], r = s.Length > 1 ? s[1] : s[0];

        switch (inCh)
        {
            case 1:
            case 2: break;
            case 3: l += k * s[2]; r += k * s[2]; break;                            // L R C
            case 4: l += k * s[2]; r += k * s[3]; break;                            // quad
            case 5: l += k * (s[2] + s[3]); r += k * (s[2] + s[4]); break;          // L R C BL BR
            case 6: l += k * (s[2] + s[4]); r += k * (s[2] + s[5]); break;          // 5.1
            case 7: l += k * (s[2] + s[5]) + 0.5f * s[4];
                    r += k * (s[2] + s[6]) + 0.5f * s[4]; break;                    // 6.1
            default: l += k * (s[2] + s[4] + s[6]); r += k * (s[2] + s[5] + s[7]); break; // 7.1
        }

        o[at] = l;
        o[at + 1] = r;
    }
}


/// <summary>
/// Quiets the microphone between words: below a level (static, hiss, fans)
/// it fades out; when you speak it opens at once and stays open a moment
/// after, so words aren't clipped and it never sounds choppy.
/// </summary>
internal sealed class NoiseGate : NAudio.Wave.ISampleProvider
{
    private readonly NAudio.Wave.ISampleProvider _src;
    private readonly float OpenAt;    // the sensitivity (default about -38 dBFS)
    private readonly float CloseAt;   // 6 dB below it
    private float _env, _gain;
    private int _hold;
    private readonly int _holdSamples, _channels;
    private readonly float _attack, _release, _envFall;

    public NoiseGate(NAudio.Wave.ISampleProvider src, int openDb = -38)
    {
        _src = src;
        OpenAt = (float)Math.Pow(10, Math.Clamp(openDb, -80, -10) / 20.0);
        CloseAt = OpenAt / 2;
        int rate = src.WaveFormat.SampleRate;
        _channels = src.WaveFormat.Channels;
        _holdSamples = rate / 4;                       // 250 ms
        _attack = 1f / (rate * 0.005f);                // opens in 5 ms
        _release = 1f / (rate * 0.120f);               // closes over 120 ms
        _envFall = (float)Math.Exp(-1.0 / (rate * 0.050));
    }

    public NAudio.Wave.WaveFormat WaveFormat => _src.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        for (int i = offset; i < offset + n; i += _channels)
        {
            float peak = 0;
            for (int c = 0; c < _channels && i + c < offset + n; c++) peak = Math.Max(peak, Math.Abs(buffer[i + c]));
            _env = Math.Max(peak, _env * _envFall);

            if (_env >= OpenAt) _hold = _holdSamples;
            else if (_hold > 0) _hold--;
            bool open = _hold > 0 || _env >= CloseAt && _gain > 0.5f;

            _gain = open ? Math.Min(1f, _gain + _attack) : Math.Max(0f, _gain - _release);
            for (int c = 0; c < _channels && i + c < offset + n; c++) buffer[i + c] *= _gain;
        }
        return n;
    }
}


/// <summary>
/// Tames a loud microphone: peaks above about -8 dBFS are turned down at
/// once and let back up smoothly, so shouting stays clear without clipping.
/// Normal speech passes untouched.
/// </summary>
/// <summary>
/// A fixed boost with a peak limiter after it: quiet and normal sound just
/// gets louder; a peak that would pass the ceiling is turned down at once and
/// eased back up over a quarter second, so nothing clips.
/// </summary>
internal sealed class Loudness
{
    private readonly float _boost, _ceiling, _release, _envFall;
    private readonly int _channels;
    private float _env, _gain = 1f;

    public Loudness(float boost, int rate, int channels, float ceilingDb = -0.5f)
    {
        _boost = boost;
        _channels = channels;
        _ceiling = (float)Math.Pow(10, ceilingDb / 20.0);
        _release = 1f / (rate * 0.25f);
        _envFall = (float)Math.Exp(-1.0 / (rate * 0.010));
    }

    public void Process(float[] buf, int count)
    {
        for (int i = 0; i + _channels <= count; i += _channels)
        {
            float peak = 0;
            for (int c = 0; c < _channels; c++) peak = Math.Max(peak, Math.Abs(buf[i + c] * _boost));
            _env = Math.Max(peak, _env * _envFall);
            float target = _env > _ceiling ? _ceiling / _env : 1f;
            _gain = target < _gain ? target : Math.Min(target, _gain + _release);
            float g = _boost * _gain;
            for (int c = 0; c < _channels; c++) buf[i + c] *= g;
        }
    }
}

internal sealed class Limiter : NAudio.Wave.ISampleProvider
{
    private readonly NAudio.Wave.ISampleProvider _src;
    private readonly float Ceiling;
    private readonly float _release, _envFall;
    private readonly int _channels;
    private float _env, _gain = 1f;

    public Limiter(NAudio.Wave.ISampleProvider src, int ceilingDb = -8)
    {
        _src = src;
        Ceiling = (float)Math.Pow(10, Math.Clamp(ceilingDb, -30, 0) / 20.0);
        int rate = src.WaveFormat.SampleRate;
        _channels = src.WaveFormat.Channels;
        _release = 1f / (rate * 0.25f);                   // back up over 250 ms
        _envFall = (float)Math.Exp(-1.0 / (rate * 0.010));
    }

    public NAudio.Wave.WaveFormat WaveFormat => _src.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        for (int i = offset; i < offset + n; i += _channels)
        {
            float peak = 0;
            for (int c = 0; c < _channels && i + c < offset + n; c++) peak = Math.Max(peak, Math.Abs(buffer[i + c]));
            _env = Math.Max(peak, _env * _envFall);
            float target = _env > Ceiling ? Ceiling / _env : 1f;
            _gain = target < _gain ? target : Math.Min(target, _gain + _release);
            for (int c = 0; c < _channels && i + c < offset + n; c++) buffer[i + c] *= _gain;
        }
        return n;
    }
}
