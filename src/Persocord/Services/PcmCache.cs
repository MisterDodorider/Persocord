using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// Each clip's audio tracks decoded once to plain samples (48 kHz stereo,
/// 16-bit) for the editor's sound engine. Sample 0 is the file's own time 0
/// (a track that starts a little late is padded), so every track lines up
/// exactly with the picture. Kept while the clip is unchanged.
/// </summary>
public static class PcmCache
{
    private static readonly Dictionary<string, Task> Jobs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CancellationTokenSource> Stops = new(StringComparer.OrdinalIgnoreCase);

    static PcmCache()
    {
        // Leftovers (from a crash) are cleared after two days: they're big.
        try
        {
            foreach (var f in Directory.EnumerateFiles(Paths.Temp, "pcm_*"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)) try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Removes a file's decoded tracks (when the editor closes).</summary>
    public static void Forget(string file, IEnumerable<int> streams)
    {
        foreach (var s in streams.Distinct())
            try { File.Delete(PathFor(file, s)); } catch { }
    }

    public static string PathFor(string file, int stream)
    {
        // The file as it is now (time and size), so a clip saved over keeps nothing of the old one.
        return Path.Combine(Paths.Temp, $"pcm_{Paths.StableHash(TimelineRender.Identity(file).ToLowerInvariant())}_{stream}.raw");
    }

    /// <summary>The decoded samples, if they're ready.</summary>
    public static string? Ready(string file, int stream)
    {
        var p = PathFor(file, stream);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Marks a clip's decoded sound as used (kept longest by the temp cleanup).</summary>
    public static void Touch(string file, IEnumerable<int> streams)
    {
        foreach (var s in streams.Distinct()) TempJanitor.Touch(PathFor(file, s));
    }

    /// <summary>Decodes whichever of these streams aren't ready yet (one FFmpeg run per file).</summary>
    public static Task EnsureAsync(string file, IEnumerable<int> streams)
    {
        var missing = streams.Distinct().Where(s => Ready(file, s) == null).ToList();
        if (missing.Count == 0) return Task.CompletedTask;
        lock (Jobs)
        {
            Jobs.TryGetValue(file, out var before);
            if (!Stops.TryGetValue(file, out var stop) || stop.IsCancellationRequested) Stops[file] = stop = new CancellationTokenSource();
            var token = stop.Token;
            var job = Task.Run(async () =>
            {
                if (before != null) try { await before; } catch { }
                if (token.IsCancellationRequested) return;
                Decode(file, missing.Where(s => Ready(file, s) == null).ToList(), token);
            });
            Jobs[file] = job;
            // Finished jobs aren't kept (one entry per clip ever opened used to stay for good).
            _ = job.ContinueWith(_ =>
            {
                lock (Jobs)
                {
                    if (!Jobs.TryGetValue(file, out var current) || current != job) return;
                    Jobs.Remove(file);
                    if (Stops.Remove(file, out var cts)) cts.Dispose();
                }
            }, TaskScheduler.Default);
            return job;
        }
    }

    /// <summary>
    /// Stops decoding a file (before it's replaced: the decoder has it open,
    /// and Windows won't swap a file something is reading). Waits up to a few
    /// seconds for it to let go. Anything half done is thrown away.
    /// </summary>
    public static async Task StopAsync(string file)
    {
        Task? job;
        lock (Jobs)
        {
            if (Stops.TryGetValue(file, out var stop)) try { stop.Cancel(); } catch { }
            Jobs.TryGetValue(file, out job);
        }
        if (job != null) await Task.WhenAny(job, Task.Delay(5000));
    }

    private static void Decode(string file, List<int> streams, CancellationToken token)
    {
        if (streams.Count == 0) return;
        var outs = string.Concat(streams.Select(s =>
            $"-map 0:a:{s} -af \"aresample=48000:async=1:first_pts=0\" -f s16le -ac 2 -ar 48000 -y \"{PathFor(file, s)}.part\" "));
        var ok = TimelineRender.Run($"-hide_banner -loglevel error -i \"{file}\" {outs}-progress pipe:1", token, null, 0);
        foreach (var s in streams)
        {
            var p = PathFor(file, s);
            try
            {
                if (ok && File.Exists(p + ".part")) File.Move(p + ".part", p, true);
                else if (File.Exists(p + ".part")) File.Delete(p + ".part");
            }
            catch (Exception ex) { Log.Error("editor sound", ex); }
        }
        if (!ok) Log.Write($"Editor sound: couldn't decode the sound of {Path.GetFileName(file)}.");
    }
}
