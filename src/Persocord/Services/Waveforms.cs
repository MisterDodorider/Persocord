using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// How loud each moment of a sound track is, for drawing its waveform on the
/// timeline. Worked out once from the editor's decoded sound (PcmCache), in
/// the background, and kept beside it (pcm_….peaks), so reopening a clip
/// draws at once. Stored at 100 steps a second, plus coarser copies (each 4×
/// coarser) so a zoomed-out timeline draws as quickly as a zoomed-in one.
/// </summary>
public sealed class Peaks
{
    public const int PerSecond = 100;
    /// <summary>Level 0 is 100 a second; each level after is 4× coarser. Values 0-255 (loudest sample in the step).</summary>
    public readonly List<byte[]> Levels = new();

    public Peaks(byte[] fine)
    {
        Levels.Add(fine);
        var cur = fine;
        while (cur.Length > 64)
        {
            var next = new byte[(cur.Length + 3) / 4];
            for (int i = 0; i < next.Length; i++)
            {
                byte m = 0;
                for (int j = i * 4; j < Math.Min(cur.Length, i * 4 + 4); j++) if (cur[j] > m) m = cur[j];
                next[i] = m;
            }
            Levels.Add(next);
            cur = next;
        }
    }

    /// <summary>The loudest moment (0-1) between two times of the file, at a level suiting that span.</summary>
    public double Max(double from, double to, int level)
    {
        level = Math.Clamp(level, 0, Levels.Count - 1);
        var arr = Levels[level];
        double step = Math.Pow(4, level) / PerSecond;
        int a = (int)Math.Floor(from / step), b = (int)Math.Ceiling(to / step);
        a = Math.Max(0, a); b = Math.Min(arr.Length, Math.Max(b, a + 1));
        byte m = 0;
        for (int i = a; i < b; i++) if (arr[i] > m) m = arr[i];
        return m / 255.0;
    }

    /// <summary>The level whose steps are about one per pixel, for a zoom of so many pixels a second.</summary>
    public int LevelFor(double pixelsPerSecond)
    {
        double perPixel = PerSecond / Math.Max(1e-6, pixelsPerSecond);
        int level = 0;
        while (perPixel >= 4 && level < Levels.Count - 1) { perPixel /= 4; level++; }
        return level;
    }
}

public static class Waveforms
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Peaks?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> LastLook = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new();
    private const int Keep = 24;

    /// <summary>Forgets every waveform (the last editor closed; they're kept on disk and quick to reload).</summary>
    public static void Clear()
    {
        lock (Gate) { Cache.Clear(); LastLook.Clear(); Order.Clear(); }
    }

    /// <summary>A waveform finished: the timeline redraws.</summary>
    public static event Action? Ready;

    /// <summary>The waveform of a file's audio stream, or null while it isn't ready (it's started if it can be).</summary>
    public static Peaks? For(string file, int stream)
    {
        string key = file + "|" + stream;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var p)) return p;   // ready, or being worked out (null)
            // Not decoded yet: looked at again at most once a second (it's a disk check).
            if (LastLook.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(1)) return null;
            LastLook[key] = DateTime.UtcNow;
        }
        var pcm = PcmCache.Ready(file, stream);
        if (pcm == null) return null;
        lock (Gate)
        {
            if (Cache.ContainsKey(key)) return Cache[key];
            Cache[key] = null;   // being worked out
            LastLook.Remove(key);
            Order.AddLast(key);
            while (Order.Count > Keep) { Cache.Remove(Order.First!.Value); Order.RemoveFirst(); }
        }
        _ = Task.Run(() =>
        {
            Peaks? made = null;
            try { made = Build(pcm); }
            catch (Exception ex) { Log.Error("waveform", ex); }
            lock (Gate)
            {
                if (made == null) { Cache.Remove(key); Order.Remove(key); }
                else if (Cache.ContainsKey(key)) Cache[key] = made;
            }
            if (made != null) Ready?.Invoke();
        });
        return null;
    }

    private static Peaks Build(string pcm)
    {
        var peaksFile = pcm + ".peaks";
        try
        {
            if (File.Exists(peaksFile) && File.GetLastWriteTimeUtc(peaksFile) >= File.GetLastWriteTimeUtc(pcm))
            {
                var kept = File.ReadAllBytes(peaksFile);
                if (kept.Length > 0) { TempJanitor.Touch(peaksFile); return new Peaks(kept); }
            }
        }
        catch { }

        // 48 kHz stereo 16-bit: 480 frames per step.
        const int frames = 48000 / Peaks.PerSecond, bytesPerStep = frames * 4;
        long length = new FileInfo(pcm).Length;
        var fine = new byte[(int)Math.Min(int.MaxValue, (length + bytesPerStep - 1) / bytesPerStep)];
        using (var fs = new FileStream(pcm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
        {
            var buf = new byte[bytesPerStep * 256];
            int step = 0, inStep = 0, max = 0;
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i + 1 < n; i += 2)
                {
                    int v = Math.Abs((int)(short)(buf[i] | (buf[i + 1] << 8)));   // as an int: the loudest negative sample has no positive short
                    if (v > max) max = v;
                    inStep += 2;
                    if (inStep >= bytesPerStep)
                    {
                        if (step < fine.Length) fine[step++] = (byte)Math.Min(255, max >> 7);
                        inStep = 0; max = 0;
                    }
                }
            }
            if (inStep > 0 && step < fine.Length) fine[step] = (byte)Math.Min(255, max >> 7);
        }
        try { File.WriteAllBytes(peaksFile, fine); } catch { }
        return new Peaks(fine);
    }

    /// <summary>
    /// Whether a file's audio stream is silent throughout (nothing above
    /// about -50 dB), from its waveform. Null while that isn't known yet.
    /// </summary>
    public static bool? Silent(string file, int stream)
    {
        var p = For(file, stream);
        if (p == null) return null;
        var top = p.Levels[^1];
        foreach (var b in top) if (b > 0) return false;
        return true;
    }
}
