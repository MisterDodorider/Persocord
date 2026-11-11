using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// The editor's preview, drawn with FFmpeg rather than Windows' own video
/// playback. Windows' player depends on the system's media components,
/// which are missing on "N" editions of Windows and can be broken by codec
/// packs or drivers; that is what left some PCs with a black preview.
/// FFmpeg ships with Persocord, so this works the same on every PC.
///
/// Paused, it decodes the exact frame asked for (the newest request wins
/// while scrubbing). Playing, it streams scaled frames from one FFmpeg and
/// sound from another, paced against a clock.
/// </summary>
public sealed class FramePlayer : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _path;
    private readonly MediaInfo _info;
    private readonly int _w, _h;
    private readonly double _fps;
    private readonly WriteableBitmap _bitmap;
    private readonly Dispatcher _ui;
    private readonly int _frameBytes;

    private double _position;
    private double _seekTarget = -1;
    private int _seekBusy;

    private CancellationTokenSource? _play;
    private Process? _videoProc, _audioProc;
    private IWavePlayer? _out;
    private int _framePending;
    private byte[] _latest = Array.Empty<byte>();
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public double Speed { get; set; } = 1.0;
    /// <summary>
    /// Loudness (0-2), applied to this player's own samples, so each player is
    /// separate and a change is instant and glitch-free. (Windows' output
    /// volume would change all of Persocord's sound at once, and stutters
    /// when changed often.)
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            var g = _gain;
            if (g != null) g.Volume = (float)Math.Clamp(value, 0, 4);
        }
    }
    private double _volume = 1.0;
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _gain;

    /// <summary>Sound only: no picture is decoded (the editor's sound tracks).</summary>
    public bool AudioOnly { get; set; }

    /// <summary>Waiting a moment after a seek before playing on (so scrubbing doesn't stutter).</summary>
    public bool Restarting { get; private set; }
    private System.Windows.Threading.DispatcherTimer? _restart;
    public bool Muted { get; set; }

    /// <summary>
    /// For files with separate tracks: which ones to hear (stream numbers),
    /// mixed together. Null plays the file's first track, the normal mix.
    /// </summary>
    public IReadOnlyList<int>? MixTracks { get; set; }

    /// <summary>
    /// The moment of this file that should be on screen now (the editor's
    /// sound is the clock). When set, frames that come too late are skipped
    /// so the picture catches up, instead of trailing the sound by however
    /// long the decoder took to start.
    /// </summary>
    public Func<double>? Follow { get; set; }

    /// <summary>When playback last (re)started, for not restarting it again straight away.</summary>
    public DateTime StartedAt { get; private set; }

    /// <summary>Playback reached the end of the clip.</summary>
    public event Action? Ended;

    public double Position => _position;

    public FramePlayer(string path, MediaInfo info, Image target, int maxWidth = 960, int maxHeight = 540)
    {
        _path = path;
        _info = info;
        _ui = target.Dispatcher;

        // Preview size: plenty for editing, light enough to stream smoothly.
        double s = Math.Min(1.0, Math.Min((double)maxWidth / Math.Max(1, info.Width), (double)maxHeight / Math.Max(1, info.Height)));
        _w = Math.Max(2, (int)(info.Width * s) & ~1);
        _h = Math.Max(2, (int)(info.Height * s) & ~1);
        _fps = Math.Clamp(info.Fps > 0 ? info.Fps : 30, 10, 60);
        _frameBytes = _w * _h * 4;

        _bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
        target.Source = _bitmap;
    }

    private static string F(double v) => v.ToString("0.###", Inv);

    /// <summary>
    /// Graphics-card decoding only for big videos (1440p AV1 is heavy for a
    /// CPU). Small ones, like the editor's 540p preview copies, start quicker
    /// on the CPU: setting up the card costs more than decoding them.
    /// </summary>
    private string Hw => (_info.Height > 720 ? "-hwaccel auto " : "-threads 2 ") + VideoDecoders.ArgsFor(_info.VideoCodec);

    /* ---- paused: one exact frame ---------------------------------------- */

    public void Seek(double t)
    {
        t = Math.Clamp(t, 0, Math.Max(0, _info.Duration - 0.001));
        _position = t;

        if (IsPlaying)
        {
            // Clicks and drags while playing restart the streams once the mouse
            // settles (120 ms), not on every movement, which tore the sound up.
            StopStreams();
            Restarting = true;
            _restart ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(120),
                System.Windows.Threading.DispatcherPriority.Normal, (_, _) =>
                {
                    _restart!.Stop();
                    Restarting = false;
                    if (IsPlaying) StartPlaying(_position);
                }, _ui);
            _restart.Stop();
            _restart.Start();
            return;
        }
        if (AudioOnly) return;

        // Scrubbing sends many requests; only the newest one matters.
        Interlocked.Exchange(ref _seekTarget, t);
        if (Interlocked.Exchange(ref _seekBusy, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed)
                {
                    double want = Interlocked.Exchange(ref _seekTarget, -1);
                    if (want < 0) break;
                    var frame = await GrabFrameAsync(want);
                    if (frame == null) continue;
                    if (!IsPlaying && !_disposed) Show(frame);
                    else Frames.Return(frame);
                }
            }
            finally { Interlocked.Exchange(ref _seekBusy, 0); }
            // A request that arrived as the loop was finishing.
            if (Interlocked.CompareExchange(ref _seekTarget, -1, -1) >= 0 && !_disposed) Seek(_position);
        });
    }

    private bool _slowSeek;
    private bool _loggedFailure;

    private async Task<byte[]?> GrabFrameAsync(double t)
    {
        // Seeking before opening is quick; if a file's timestamps make it
        // come back empty, decode from the start and seek after instead.
        var frame = _slowSeek ? null : await GrabFrameAsync(t, fast: true);
        if (frame == null)
        {
            frame = await GrabFrameAsync(t, fast: false);
            if (frame != null && !_slowSeek) { _slowSeek = true; Log.Write("preview: using slow seeking for this file"); }
        }
        return frame;
    }

    private async Task<byte[]?> GrabFrameAsync(double t, bool fast)
    {
        try
        {
            // Decoded on the graphics card where it can be (AV1 at 1440p is
            // heavy for a CPU); FFmpeg falls back to the CPU by itself.
            string seek = fast ? $"{Hw}-ss {F(t)} -i \"{_path}\"" : $"{Hw}-i \"{_path}\" -ss {F(t)}";
            var psi = new ProcessStartInfo
            {
                FileName = Paths.FFmpeg,
                Arguments = $"-hide_banner -loglevel error {seek} -map 0:v:0 -an -sn -dn -frames:v 1 " +
                            $"-vf scale={_w}:{_h}:flags=bilinear -f rawvideo -pix_fmt bgra pipe:1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var err = p.StandardError.ReadToEndAsync();

            // A frame's buffer is borrowed and handed back once drawn: a new
            // 2 MB one for every step of a scrub was a steady stream of the
            // biggest kind of garbage.
            var buf = Frames.Rent(_frameBytes);
            int read = 0;
            var stream = p.StandardOutput.BaseStream;
            try
            {
                while (read < _frameBytes)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(read, _frameBytes - read));
                    if (n == 0) break;
                    read += n;
                }
                await p.WaitForExitAsync();
            }
            catch { Frames.Return(buf); throw; }
            if (read == _frameBytes) return buf;
            Frames.Return(buf);
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Log.Write($"preview frame empty ({(fast ? "fast" : "slow")} seek at {t:0.##}s): {(await err).Trim()}");
            }
            return null;
        }
        catch (Exception ex) { Log.Error("preview frame", ex); return null; }
    }

    /* ---- playing: paced frames and sound ---------------------------------- */

    public void Play()
    {
        if (IsPlaying) return;
        IsPlaying = true;
        StartPlaying(_position);
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        StopStreams();
    }

    private void StartPlaying(double from)
    {
        StopStreams();
        _position = from;
        StartedAt = DateTime.UtcNow;
        var cts = new CancellationTokenSource();
        _play = cts;

        double speed = Math.Clamp(Speed, 0.25, 4);
        var clock = new Stopwatch();

        // Sound first: its output buffer takes a moment to fill, and the
        // picture waits that moment too so they start together.
        // When the sound becomes audible after a (re)start: its cushion fills in a
        // few tens of ms, then the output's 200 ms. The picture waits the same.
        const double audioLead = 0.28;
        if (_info.HasAudio && !Muted) StartAudio(from, speed, cts.Token);

        if (AudioOnly)
        {
            // No picture: the clock alone moves the position and spots the end.
            clock.Start();
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    _position = from + Played(clock, speed, audioLead);
                    if (_position >= _info.Duration) break;
                    try { await Task.Delay(20, cts.Token); } catch { break; }
                }
                if (!cts.IsCancellationRequested)
                    _ = _ui.InvokeAsync(() => { IsPlaying = false; StopStreams(); Ended?.Invoke(); });
            });
            return;
        }

        _videoProc = Start($"-hide_banner -loglevel error " +
                           Hw +
                           (_slowSeek ? $"-i \"{_path}\" -ss {F(from)} " : $"-ss {F(from)} -i \"{_path}\" ") +
                           "-map 0:v:0 -an -sn -dn " +
                           $"-vf scale={_w}:{_h}:flags=bilinear,fps={F(_fps)} -f rawvideo -pix_fmt bgra pipe:1");
        if (_videoProc == null) { IsPlaying = false; return; }
        Health.DecoderStarted();
        var proc = _videoProc;

        _ = Task.Run(() =>
        {
            var stream = proc.StandardOutput.BaseStream;
            var buf = new byte[_frameBytes];
            long index = 0;
            long lastShown = 0;
            clock.Start();
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = stream.Read(buf, read, buf.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < buf.Length) break;   // end of the clip

                    if (Follow is { } follow)
                    {
                        // Paced by the editor's sound: a frame already past is
                        // skipped (one still shows every 100 ms, so the picture
                        // keeps moving while it catches up); an early one waits.
                        double at = from + index / _fps;
                        if (at < follow() - 1.5 / _fps && Stopwatch.GetElapsedTime(lastShown).TotalSeconds < 0.1)
                        {
                            _position = at;
                            index++;
                            continue;
                        }
                        while (!cts.IsCancellationRequested)
                        {
                            double wait = at - follow();
                            if (wait <= 0.002) break;
                            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 0.04)));
                        }
                    }
                    else
                    {
                        // Hold each frame until the sound reaches its moment (the sound's
                        // own played position is the clock, so they can't drift apart).
                        double due = index / _fps;
                        while (!cts.IsCancellationRequested)
                        {
                            double wait = (due - Played(clock, speed, audioLead)) / speed;
                            if (wait <= 0.002) break;
                            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 0.04)));
                        }
                    }
                    if (cts.IsCancellationRequested) break;
                    lastShown = Stopwatch.GetTimestamp();

                    _position = from + index / _fps;
                    index++;

                    // Frames arriving faster than the screen can take them
                    // are skipped rather than queued. One buffer is reused for
                    // the frame on its way to the screen (copying into a new one
                    // each frame made 60 MB of garbage a second per layer): it's
                    // only written while the screen isn't reading it.
                    if (Interlocked.Exchange(ref _framePending, 1) == 0)
                    {
                        if (_latest.Length != buf.Length) _latest = new byte[buf.Length];
                        Buffer.BlockCopy(buf, 0, _latest, 0, buf.Length);
                        _ui.InvokeAsync(() =>
                        {
                            try { if (IsPlaying) Blit(_latest); }
                            finally { Interlocked.Exchange(ref _framePending, 0); }
                        });
                    }
                }
            }
            catch (Exception ex) { if (!cts.IsCancellationRequested) Log.Error("preview playback", ex); }

            if (!cts.IsCancellationRequested)
                _ui.InvokeAsync(() =>
                {
                    IsPlaying = false;
                    StopStreams();
                    Ended?.Invoke();
                });
        });
    }

    private void StartAudio(double from, double speed, CancellationToken ct)
    {
        try
        {
            var filters = new System.Collections.Generic.List<string>();
            double s = speed;
            while (s > 2.0 + 1e-9) { filters.Add("atempo=2.0"); s /= 2.0; }
            while (s < 0.5 - 1e-9) { filters.Add("atempo=0.5"); s /= 0.5; }
            if (Math.Abs(s - 1) > 0.001) filters.Add($"atempo={F(s)}");

            string chain = filters.Count > 0 ? string.Join(",", filters) : "anull";
            string audio;
            if (MixTracks != null)
            {
                if (MixTracks.Count == 0) return;   // every track switched off
                var ins = string.Concat(MixTracks.Select(i => $"[0:a:{i}]"));
                audio = MixTracks.Count == 1
                    ? $"-filter_complex \"[0:a:{MixTracks[0]}]{chain}[o]\" -map \"[o]\" "
                    : $"-filter_complex \"{ins}amix=inputs={MixTracks.Count}:normalize=0:dropout_transition=0,{chain}[o]\" -map \"[o]\" ";
            }
            else audio = $"-map 0:a:0 -af {chain} ";

            _audioProc = Start($"-hide_banner -loglevel error -ss {F(from)} -i \"{_path}\" -vn {audio}" +
                               "-f f32le -ac 2 -ar 48000 pipe:1");
            if (_audioProc == null) return;
            Health.DecoderStarted();
            try { _audioProc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            var proc = _audioProc;

            var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
            };
            // Roomier than before (100 ms in 2 buffers ran dry when FFmpeg was a beat late).
            _gain = new NAudio.Wave.SampleProviders.VolumeSampleProvider(buffer.ToSampleProvider())
            {
                Volume = (float)Math.Clamp(Volume, 0, 4),
            };
            _out = SoundOut.Open(_gain, 100);

            _ = Task.Run(() =>
            {
                var chunk = new byte[48000 * 2 * 4 / 20];   // 50 ms
                var stream = proc.StandardOutput.BaseStream;
                bool started = false;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (buffer.BufferedDuration.TotalSeconds > 1.0) { Thread.Sleep(20); continue; }
                        int n = stream.Read(chunk, 0, chunk.Length);
                        if (n <= 0) break;
                        buffer.AddSamples(chunk, 0, n);
                        if (!started && buffer.BufferedDuration.TotalSeconds > 0.25)
                        {
                            started = true;
                            _out?.Play();
                        }
                    }
                }
                catch (Exception ex) { if (!ct.IsCancellationRequested) Log.Error("preview sound", ex); }
            }, ct);
        }
        catch (Exception ex) { Log.Error("preview sound start", ex); }
    }

    /// <summary>
    /// How much of the clip has been heard since playback started (seconds of
    /// the clip): the sound output's own position when there's sound, the
    /// stopwatch otherwise (or if the sound hasn't started within a second).
    /// </summary>
    private double Played(Stopwatch clock, double speed, double lead)
    {
        bool sound = _info.HasAudio && !Muted;
        var o = _out;
        if (sound && o != null)
        {
            try
            {
                if (o.PlaybackState == PlaybackState.Playing)
                    return SoundOut.PlayedSeconds(o) * speed;
            }
            catch { }
        }
        if (sound && clock.Elapsed.TotalSeconds < 1.0) return 0;   // the sound is about to start
        return Math.Max(0, clock.Elapsed.TotalSeconds - (sound ? lead : 0)) * speed;
    }

    private static Process? Start(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = Paths.FFmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p != null) _ = p.StandardError.ReadToEndAsync();
            return p;
        }
        catch (Exception ex) { Log.Error("preview ffmpeg", ex); return null; }
    }

    private void StopStreams()
    {
        try { _play?.Cancel(); } catch { }
        _play = null;
        foreach (var p in new[] { _videoProc, _audioProc })
        {
            if (p != null) Health.DecoderStopped();
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            try { p?.Dispose(); } catch { }
        }
        _videoProc = _audioProc = null;
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _out = null;
    }

    /* ---- drawing ---------------------------------------------------------- */

    /// <summary>Draws a borrowed frame, then hands its buffer back.</summary>
    private void Show(byte[] frame) => _ui.InvokeAsync(() =>
    {
        try { Blit(frame); }
        finally { Frames.Return(frame); }
    });

    private static System.Buffers.ArrayPool<byte> Frames => System.Buffers.ArrayPool<byte>.Shared;

    private void Blit(byte[] frame)
    {
        // (A borrowed buffer can be longer than a frame: only the frame is read.)
        if (_disposed || frame.Length < _frameBytes) return;
        _bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), frame, _w * 4, 0);
    }

    public void Dispose()
    {
        _disposed = true;
        IsPlaying = false;
        _restart?.Stop();
        StopStreams();
    }
}
