using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Persocord.Services;

/// <summary>
/// The editor's sound: one mixer for the whole timeline, feeding one output.
/// Every audio track is decoded once to plain samples (48 kHz stereo, 16-bit,
/// lined up with its file's own time), and for each slice the output asks
/// for, the mixer adds up exactly the right samples of every heard clip, with
/// the overall volume × track volume × fade applied per sample. Seeking just
/// moves the read position, so there are no restarts, no cuts and no drift:
/// every track is sample-accurate with every other, wherever you click. It
/// works from a snapshot of the timeline, so editing while it plays is safe.
/// </summary>
public sealed class TimelineAudio : IDisposable
{
    public const int Rate = 48000, Channels = 2;
    private const int BytesPerFrame = Channels * 2;

    private sealed record Piece(long Start, long End, long SrcStart, string Pcm, TClip Clip, float Gain, double Speed);

    private readonly Func<string, int, string?> _pcmOf;
    private readonly object _lock = new();
    private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);
    private List<Piece> _pieces = new();
    private long _pos;                 // timeline position, in sample frames
    private long _totalRead;           // frames handed to the output since it opened (sound or silence)
    private long _startAt, _startPos;  // where the current play began: output frame, timeline frame
    private short[] _scratch = new short[0];
    private bool _disposed;

    /// <summary>Whether it's producing sound (otherwise silence, and the position stays put).</summary>
    public volatile bool Playing;

    /// <param name="pcmOf">The decoded samples for a file's audio stream, or null if not ready yet.</param>
    public TimelineAudio(Func<string, int, string?> pcmOf) => _pcmOf = pcmOf;

    /// <summary>Takes a fresh snapshot of what's heard (after any edit or volume change).</summary>
    public void Update(EditTimeline tl)
    {
        var heard = new HashSet<int>(tl.HeardAudio());
        var list = new List<Piece>();
        foreach (var c in tl.Clips)
        {
            if (!heard.Contains(c.Track) || c.Length <= 0) continue;
            var pcm = _pcmOf(c.File, Math.Max(0, c.Stream));
            if (pcm == null) continue;
            long start = Frames(c.Start);
            long end = start + Frames(c.Length);
            list.Add(new Piece(start, end, Frames(c.In), pcm, c.Copy(), (float)(tl.Volume * tl.Tracks[c.Track].Volume * c.Volume), c.Speed));
        }
        lock (_lock) _pieces = list;
    }

    public static long Frames(double seconds) => (long)Math.Round(seconds * Rate);

    /// <summary>The playhead (seconds), as far as the mixer has read.</summary>
    public double Position { get { lock (_lock) return _pos / (double)Rate; } }

    public void Seek(double seconds)
    {
        lock (_lock) _pos = Math.Max(0, Frames(seconds));
    }

    /// <summary>
    /// Plays from a timeline time. The output keeps running throughout (it's
    /// never stopped and restarted, which made Windows' output replay stale
    /// buffers); the mixer notes which output frame the new sound starts on.
    /// </summary>
    public void Start(double seconds)
    {
        lock (_lock)
        {
            _pos = _startPos = Math.Max(0, Frames(seconds));
            _startAt = _totalRead;
            Playing = true;
        }
    }

    /// <summary>Quiet from the next slice on (the output carries on with silence).</summary>
    public void Stop() => Playing = false;

    /// <summary>
    /// The timeline time being heard right now, from how many frames the
    /// output has actually played: exact, whatever its buffering.
    /// </summary>
    public double Heard(long playedFrames)
    {
        lock (_lock)
        {
            long heard = _startPos + Math.Max(0, playedFrames - _startAt);
            return Math.Min(heard, _pos) / (double)Rate;
        }
    }

    /// <summary>Fills an output buffer (interleaved stereo floats) and moves on by that much.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        int frames = count / Channels;
        lock (_lock)
        {
            _totalRead += frames;
            if (!Playing || _disposed) return count;
            long start = _pos, end = start + frames;
            foreach (var p in _pieces)
            {
                long a = Math.Max(start, p.Start), b = Math.Min(end, p.End);
                if (b <= a) continue;
                int n = (int)(b - a);
                int outFrame = (int)(a - start);
                bool fades = p.Clip.FadeIn > 0 || p.Clip.FadeOut > 0;
                if (Math.Abs(p.Speed - 1) > 0.001)
                {
                    // Sped up or slowed down: read the stretch of the file it covers and
                    // step through it at that speed (the pitch follows, in the preview only;
                    // the saved video keeps the pitch).
                    double s0 = p.SrcStart + (a - p.Start) * p.Speed;
                    long first = (long)Math.Floor(s0);
                    int span = (int)Math.Ceiling(n * p.Speed) + 2;
                    ReadPcm(p.Pcm, first, span);
                    for (int i = 0; i < n; i++)
                    {
                        double pos = s0 + i * p.Speed - first;
                        int j = (int)pos;
                        if (j + 1 >= span) break;
                        float f = (float)(pos - j);
                        float g = p.Gain;
                        if (fades) g *= (float)EditTimeline.FadeAt(p.Clip, (a + i) / (double)Rate);
                        int o = offset + (outFrame + i) * Channels;
                        float l = _scratch[j * 2] + (_scratch[j * 2 + 2] - _scratch[j * 2]) * f;
                        float r = _scratch[j * 2 + 1] + (_scratch[j * 2 + 3] - _scratch[j * 2 + 1]) * f;
                        buffer[o] += l / 32768f * g;
                        buffer[o + 1] += r / 32768f * g;
                    }
                    continue;
                }
                int got = ReadPcm(p.Pcm, p.SrcStart + (a - p.Start), n);
                for (int i = 0; i < got; i++)
                {
                    float g = p.Gain;
                    if (fades) g *= (float)EditTimeline.FadeAt(p.Clip, (a + i) / (double)Rate);
                    int o = offset + (outFrame + i) * Channels;
                    buffer[o] += _scratch[i * 2] / 32768f * g;
                    buffer[o + 1] += _scratch[i * 2 + 1] / 32768f * g;
                }
            }
            _pos = end;
        }
        for (int i = offset; i < offset + count; i++)
            buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
        return count;
    }

    /// <summary>Reads frames from a decoded file into the scratch buffer; past its end is silence.</summary>
    private int ReadPcm(string path, long frame, int frames)
    {
        if (_scratch.Length < frames * Channels) _scratch = new short[frames * Channels];
        Array.Clear(_scratch, 0, frames * Channels);
        if (frame < 0) return frames;
        try
        {
            if (!_files.TryGetValue(path, out var fs))
                _files[path] = fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            long at = frame * BytesPerFrame;
            if (at >= fs.Length) return frames;
            fs.Position = at;
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_scratch.AsSpan(0, frames * Channels));
            int read = 0;
            while (read < bytes.Length)
            {
                int r = fs.Read(bytes.Slice(read));
                if (r <= 0) break;
                read += r;
            }
        }
        catch { }
        return frames;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Playing = false;
            foreach (var f in _files.Values) try { f.Dispose(); } catch { }
            _files.Clear();
            _pieces = new();
        }
    }
}
