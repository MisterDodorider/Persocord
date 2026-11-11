using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Persocord.Services;

/// <summary>
/// Plays short cue sounds with no noticeable delay.
///
/// Opening an audio device takes long enough to hear, so one output stays
/// open playing silence and cues are mixed into it. Sounds are decoded once
/// and kept in memory. Persocord's own sound is recorded except while a cue
/// plays (see <see cref="Sounding"/>), which keeps cues out of recordings.
/// </summary>
public sealed class CuePlayer : IDisposable
{
    public static CuePlayer Shared { get; } = new();

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly object _lock = new();
    private readonly Dictionary<string, float[]?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IWavePlayer? _out;
    private MixingSampleProvider? _mix;
    private Timer? _idle;

    /// <summary>Plays a cue. Returns how long it lasts, or zero if it could not play.</summary>
    public TimeSpan Play(string sound, float volume)
    {
        var path = Resolve(sound);
        if (path == null) return TimeSpan.Zero;

        var data = Load(path);
        if (data == null || data.Length == 0) return TimeSpan.Zero;

        try
        {
            lock (_lock)
            {
                EnsureOutput();
                _mix!.AddMixerInput(new Clip(data, Math.Clamp(volume, 0f, 1f)));
                KeepAlive();
            }
        }
        catch (Exception ex)
        {
            Log.Error("cue play", ex);
            Shutdown();
            return TimeSpan.Zero;
        }

        var length = TimeSpan.FromSeconds(data.Length / (double)Format.Channels / Format.SampleRate);
        // Until it has finished (with room for the output's own delay), what
        // Persocord itself plays is left out of recordings.
        long until = Stopwatch.GetTimestamp() + (long)((length.TotalSeconds + 0.35) * Stopwatch.Frequency);
        long was;
        do { was = Interlocked.Read(ref _quietUntil); if (was >= until) break; }
        while (Interlocked.CompareExchange(ref _quietUntil, until, was) != was);
        return length;
    }

    private static long _quietUntil;

    /// <summary>
    /// Whether a cue may be sounding right now. Persocord's own sound (its
    /// player, the editor) is recorded like any app's, except while this is true,
    /// so cues never end up in a recording or replay.
    /// </summary>
    public static bool Sounding => Stopwatch.GetTimestamp() < Interlocked.Read(ref _quietUntil);

    /// <summary>Opens the output ahead of time, so the first cue is as quick as the rest.</summary>
    public void Warm()
    {
        try { lock (_lock) { EnsureOutput(); KeepAlive(); } }
        catch (Exception ex) { Log.Error("cue warm", ex); }
    }

    private void EnsureOutput()
    {
        if (_out != null) return;
        _mix = new MixingSampleProvider(Format) { ReadFully = true };
        _out = SoundOut.Open(_mix, 40);
        _out.Play();
    }

    private void KeepAlive()
    {
        _idle?.Dispose();
        _idle = new Timer(_ => Shutdown(), null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
    }

    private void Shutdown()
    {
        lock (_lock)
        {
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;
            _mix = null;
        }
    }

    public void Dispose()
    {
        _idle?.Dispose();
        Shutdown();
    }

    /* ---- sounds on disk ------------------------------------------------- */

    public static string MediaFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>The sounds that ship with Windows, by file name.</summary>
    public static List<string> WindowsSounds()
    {
        try
        {
            return Directory.EnumerateFiles(MediaFolder, "*.wav")
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>A bare name means a Windows sound; anything else is a full path.</summary>
    public static string? Resolve(string sound)
    {
        if (string.IsNullOrWhiteSpace(sound)) return null;
        var path = Path.IsPathRooted(sound) ? sound : Path.Combine(MediaFolder, sound);
        return File.Exists(path) ? path : null;
    }

    private float[]? Load(string path)
    {
        lock (_lock)
            if (_cache.TryGetValue(path, out var hit)) return hit;

        float[]? data = null;
        try
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider p = reader;
            if (p.WaveFormat.Channels > 2) p = new StereoFromMulti(p);
            if (p.WaveFormat.SampleRate != Format.SampleRate) p = new WdlResamplingSampleProvider(p, Format.SampleRate);
            if (p.WaveFormat.Channels == 1) p = new MonoToStereoSampleProvider(p);

            var all = new List<float>();
            var buf = new float[Format.SampleRate * Format.Channels];
            int n;
            // Cues are short; anything past five seconds is cut rather than
            // left droning over the start of a recording.
            while ((n = p.Read(buf, 0, buf.Length)) > 0 && all.Count < Format.SampleRate * Format.Channels * 5)
                all.AddRange(buf.Take(n));
            data = all.ToArray();
        }
        catch (Exception ex) { Log.Error("cue load " + path, ex); }

        lock (_lock) _cache[path] = data;
        return data;
    }

    /* ---- providers ------------------------------------------------------- */

    /// <summary>One cue in flight. Ends itself, so the mixer drops it when done.</summary>
    private sealed class Clip : ISampleProvider
    {
        private readonly float[] _data;
        private readonly float _volume;
        private int _pos;

        public Clip(float[] data, float volume) { _data = data; _volume = volume; }

        public WaveFormat WaveFormat => Format;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            for (int i = 0; i < n; i++) buffer[offset + i] = _data[_pos + i] * _volume;
            _pos += n;
            return n;
        }
    }

    /// <summary>Keeps the front pair of a multichannel file.</summary>
    private sealed class StereoFromMulti : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _ch;
        private float[] _buf = Array.Empty<float>();

        public StereoFromMulti(ISampleProvider src)
        {
            _src = src;
            _ch = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            if (_buf.Length < frames * _ch) _buf = new float[frames * _ch];
            int read = _src.Read(_buf, 0, frames * _ch) / _ch;
            for (int f = 0; f < read; f++)
            {
                buffer[offset + f * 2] = _buf[f * _ch];
                buffer[offset + f * 2 + 1] = _buf[f * _ch + 1];
            }
            return read * 2;
        }
    }
}
