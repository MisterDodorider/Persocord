using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>Small pictures along a video, for its clips on the timeline: one per keyframe (at most 600).</summary>
public sealed class Filmstrip
{
    public double[] Times = Array.Empty<double>();
    public BitmapSource[] Frames = Array.Empty<BitmapSource>();
    public int Width, Height;

    /// <summary>The picture nearest a moment of the file.</summary>
    public BitmapSource? At(double t)
    {
        if (Frames.Length == 0) return null;
        int i = Array.BinarySearch(Times, t);
        if (i < 0) i = Math.Max(0, ~i - 1);
        return Frames[Math.Min(i, Frames.Length - 1)];
    }
}

/// <summary>
/// The timeline's filmstrips, made in the background from each video's
/// keyframes only (quick: nothing in between is decoded), kept while
/// Persocord runs (a dozen videos at most).
/// </summary>
public static class Filmstrips
{
    public const int Height = 40;
    private const int MaxFrames = 600, Keep = 12;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Filmstrip?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new();

    /// <summary>A filmstrip finished: the timeline redraws.</summary>
    public static event Action? Ready;

    /// <summary>Lets every filmstrip go (the editor closed).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            // Ones still being made are left to finish into nothing.
            foreach (var k in Cache.Where(kv => kv.Value != null).Select(kv => kv.Key).ToList()) { Cache.Remove(k); Order.Remove(k); }
        }
    }

    /// <summary>The filmstrip of a video, or null while it's being made (started if it can be).</summary>
    public static Filmstrip? For(string file, MediaInfo? info)
    {
        if (info == null || info.Width <= 0 || info.Height <= 0 || info.Duration <= 0) return null;
        string key = TimelineRender.Identity(file);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var f)) return f;
            Cache[key] = null;
            Order.AddLast(key);
            while (Order.Count > Keep) { Cache.Remove(Order.First!.Value); Order.RemoveFirst(); }
        }
        _ = Task.Run(async () =>
        {
            Filmstrip? made = null;
            try { made = await MakeAsync(file, info); }
            catch (Exception ex) { Log.Error("filmstrip", ex); }
            lock (Gate) { if (Cache.ContainsKey(key)) Cache[key] = made; }
            if (made != null) Ready?.Invoke();
        });
        return null;
    }

    private static async Task<Filmstrip?> MakeAsync(string file, MediaInfo info)
    {
        var keys = await ClipEditor.KeyframesAsync(file);
        if (keys.Count == 0) keys.Add(0);
        int every = (int)Math.Ceiling(keys.Count / (double)MaxFrames);
        var times = keys.Where((_, i) => i % every == 0).ToArray();
        int h = Height, w = Math.Max(8, (int)Math.Round(h * (double)info.Width / info.Height) & ~1);
        string pick = every > 1 ? $"select='not(mod(n\\,{every}))'," : "";
        var psi = new ProcessStartInfo
        {
            FileName = Paths.FFmpeg,
            Arguments = $"-hide_banner -loglevel error -nostdin {VideoDecoders.ArgsFor(info.VideoCodec)}-skip_frame nokey -i \"{file}\" -map 0:v:0 -an -sn -dn " +
                        $"-vf \"{pick}scale={w}:{h}:flags=fast_bilinear\" -fps_mode passthrough -f rawvideo -pix_fmt bgra pipe:1",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _ = p.StandardError.ReadToEndAsync();
        var frames = new List<BitmapSource>();
        var stream = p.StandardOutput.BaseStream;
        int size = w * h * 4;
        var buf = new byte[size];
        while (frames.Count < times.Length + 2)
        {
            int read = 0;
            while (read < size)
            {
                int n = await stream.ReadAsync(buf.AsMemory(read, size - read));
                if (n == 0) break;
                read += n;
            }
            if (read < size) break;
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
            bmp.Freeze();
            frames.Add(bmp);
        }
        try { if (!p.HasExited) p.Kill(); } catch { }
        if (frames.Count == 0) return null;
        // One picture per (chosen) keyframe; if the counts differ, they're spread across the video.
        var at = frames.Count == times.Length ? times
            : Enumerable.Range(0, frames.Count).Select(i => info.Duration * i / frames.Count).ToArray();
        return new Filmstrip { Times = at, Frames = frames.ToArray(), Width = w, Height = h };
    }
}
