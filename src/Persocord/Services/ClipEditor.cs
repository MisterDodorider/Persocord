using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>What FFmpeg says about a clip.</summary>
public record MediaInfo(double Duration, int Width, int Height, double Fps, bool HasAudio, string VideoCodec)
{
    /// <summary>Names of the audio tracks in order ("Mix", "PC audio", "Discord", …).</summary>
    public IReadOnlyList<string> AudioTracks { get; init; } = Array.Empty<string>();

    /// <summary>The whole file's bitrate in kb/s (picture and sound), 0 if unknown.</summary>
    public int Kbps { get; init; }

    /// <summary>
    /// A Persocord file with separate tracks: a mix first, then each source.
    /// Returns the source tracks' stream numbers and names.
    /// </summary>
    public IReadOnlyList<(int Stream, string Name)> SeparateTracks
    {
        get
        {
            if (AudioTracks.Count < 3 || AudioTracks[0] != "Mix") return Array.Empty<(int, string)>();
            // Every source, "All PC audio" included. It used to be hidden when
            // app tracks existed (it holds every app already), which lost any
            // app that couldn't be recorded on its own track: its sound is only
            // in All PC audio. The editor now offers it, ticked or not by default
            // (EditTimeline.From), and never both it and the app tracks at once.
            return AudioTracks.Select((n, i) => (i, n)).Skip(1).ToList();
        }
    }

}

/// <summary>A stretch of the source, in seconds.</summary>
public record TimeRange(double Start, double End)
{
    public double Length => Math.Max(0, End - Start);
}

/// <summary>Everything the editor can change about a clip.</summary>
public class EditPlan
{
    public double In { get; set; }
    public double Out { get; set; }

    /// <summary>Sections removed from the middle.</summary>
    public List<TimeRange> Cuts { get; } = new();

    /// <summary>Crop in source pixels, or null for the whole picture.</summary>
    public System.Drawing.Rectangle? Crop { get; set; }

    public bool Mute { get; set; }

    /// <summary>1.0 is unchanged; up to 2.0 (200%).</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>1.0 is unchanged; 0.25 to 4.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>What survives: the In–Out span minus every cut, in order.</summary>
    public List<TimeRange> Kept()
    {
        var kept = new List<TimeRange>();
        double at = In;
        foreach (var c in Cuts.Where(c => c.End > In && c.Start < Out).OrderBy(c => c.Start))
        {
            double s = Math.Max(c.Start, In), e = Math.Min(c.End, Out);
            if (s > at + 0.001) kept.Add(new TimeRange(at, s));
            at = Math.Max(at, e);
        }
        if (Out > at + 0.001) kept.Add(new TimeRange(at, Out));
        return kept;
    }

    public double ResultSeconds => Kept().Sum(r => r.Length) / Speed;

    /// <summary>Only cutting in time; nothing that needs every frame redrawn.</summary>
    public bool TimeOnly =>
        Crop == null && !Mute && Math.Abs(Volume - 1) < 0.001 && Math.Abs(Speed - 1) < 0.001 &&
        MutedTracks.Count == 0;

    /// <summary>Separate tracks switched off (their stream numbers in the source).</summary>
    public HashSet<int> MutedTracks { get; } = new();

    /// <summary>The same edit, minus the track choices (once they've been applied).</summary>
    public EditPlan WithoutTrackChoices()
    {
        var p = new EditPlan { In = In, Out = Out, Crop = Crop, Mute = Mute, Volume = Volume, Speed = Speed };
        p.Cuts.AddRange(Cuts);
        return p;
    }
}

/// <summary>
/// Applies edits with FFmpeg. When an edit only removes time and every piece
/// starts on a keyframe, the video is copied as-is: instant and lossless.
/// Anything else is re-encoded frame-exact, on the graphics card when there
/// is one.
/// </summary>
public static class ClipEditor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("0.######", Inv);

    /* ---- reading clips -------------------------------------------------- */

    public static async Task<MediaInfo?> ProbeAsync(string path)
    {
        var text = await FFmpegRun.InfoAsync(path);
        if (text == null) return null;

        var dur = Regex.Match(text, @"Duration:\s*(\d+):(\d\d):(\d\d\.\d+)");
        double duration = dur.Success
            ? int.Parse(dur.Groups[1].Value) * 3600 + int.Parse(dur.Groups[2].Value) * 60 +
              double.Parse(dur.Groups[3].Value, Inv)
            : 0;

        int w = 0, h = 0;
        double fps = 0;
        string codec = "";
        var video = Regex.Match(text, @"Stream #\d+:\d+[^:]*: Video: (\w+)[^\n]*?(\d{2,5})x(\d{2,5})");
        if (video.Success)
        {
            codec = video.Groups[1].Value;
            w = int.Parse(video.Groups[2].Value);
            h = int.Parse(video.Groups[3].Value);
        }
        var rate = Regex.Match(text, @"([\d.]+) fps");
        if (rate.Success) double.TryParse(rate.Groups[1].Value, NumberStyles.Float, Inv, out fps);

        bool audio = Regex.IsMatch(text, @"Stream #\d+:\d+[^:]*: Audio:");

        // Each audio stream's name, from its title (or handler name).
        var tracks = new List<string>();
        foreach (var block in Regex.Split(text, @"(?=\n\s*Stream #)"))
        {
            if (!Regex.IsMatch(block, @"^\s*Stream #\d+:\d+[^:]*: Audio:")) continue;
            var t = Regex.Match(block, @"\n\s*title\s*:\s*([^\r\n]+)");
            var hn = Regex.Match(block, @"\n\s*handler_name\s*:\s*([^\r\n]+)");
            string name = t.Success ? t.Groups[1].Value.Trim()
                : hn.Success && !hn.Groups[1].Value.Contains("Handler") ? hn.Groups[1].Value.Trim()
                : $"Track {tracks.Count + 1}";
            tracks.Add(name);
        }

        var br = Regex.Match(text, @"Duration:[^\n]*?bitrate:\s*(\d+)\s*kb/s");
        int kbps = br.Success ? int.Parse(br.Groups[1].Value, Inv) : 0;
        if (kbps <= 0 && duration > 0)
            try { kbps = (int)(new FileInfo(path).Length * 8 / duration / 1000); } catch { }

        return new MediaInfo(duration, w, h, fps > 0 ? fps : 60, audio, codec) { AudioTracks = tracks, Kbps = kbps };
    }

    /// <summary>
    /// Where the keyframes are. Decoding only keyframes is quick, since
    /// Persocord's recordings have one every two seconds.
    /// </summary>
    public static async Task<List<double>> KeyframesAsync(string path, CancellationToken ct = default)
    {
        // Read from the file's packet list (copied, never decoded): seconds for
        // a long clip. Decoding every keyframe instead could take minutes on a
        // PC that can't decode the clip's format on its graphics card, with no
        // progress and no way to cancel.
        var list = new List<double>();
        var text = await RunForStdoutAsync(
            $"-hide_banner -loglevel error -nostdin -i \"{path}\" -map 0:v:0 -c copy -f framecrc -", ct);
        if (text == null) return list;
        long num = 1, den = 1;
        long? first = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#tb 0:"))
            {
                var tb = line[6..].Trim().Split('/');
                if (tb.Length == 2) { long.TryParse(tb[0], out num); long.TryParse(tb[1], out den); }
                continue;
            }
            if (line.Length == 0 || line[0] == '#') continue;
            // stream, dts, pts, duration, size, checksum[, F=flags when not a plain keyframe]
            var f = line.Split(',');
            if (f.Length < 6 || !long.TryParse(f[2].Trim(), out var pts)) continue;
            first = first == null ? pts : Math.Min(first.Value, pts);
            bool key = true;
            if (f.Length > 6)
            {
                var fl = f[6].Trim();
                key = fl.StartsWith("F=0x") && int.TryParse(fl[4..], NumberStyles.HexNumber, Inv, out var bits) && (bits & 1) != 0;
            }
            if (key) list.Add(pts);
        }
        if (den <= 0 || num <= 0) return new List<double>();
        long start = first ?? 0;
        return list.Select(p => (p - start) * (double)num / den).OrderBy(x => x).ToList();
    }

    private static async Task<string?> RunForStdoutAsync(string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = Paths.FFmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return null;
            _ = p.StandardError.ReadToEndAsync();
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(CancellationToken.None);
            return ct.IsCancellationRequested ? null : text;
        }
        catch (Exception ex) { Log.Error("ffmpeg packet list", ex); return null; }
    }

    /* ---- exporting an edit ---------------------------------------------- */

    /// <summary>
    /// Writes the edited clip to <paramref name="output"/>. Returns true and
    /// reports whether it could be done as an instant copy.
    /// </summary>
    /// <param name="exact">False: trims and cuts are saved instantly (copied, not re-encoded), each
    /// part starting at the keyframe at or just before where it was cut (up to 2 s earlier).</param>
    public static async Task<(bool ok, bool instant)> ExportAsync(
        string source, MediaInfo info, EditPlan plan, string output,
        EncoderKind hw, int quality, IProgress<double> progress, CancellationToken ct, bool exact = true)
    {
        var kept = plan.Kept();
        if (kept.Count == 0) return (false, false);

        if (!exact && plan.Crop == null && Math.Abs(plan.Speed - 1) < 0.001)
        {
            if (await InstantExportAsync(source, info, plan, kept, output, progress, ct)) return (true, true);
            if (ct.IsCancellationRequested) return (false, false);
            Log.Write("Instant save not used; re-encoding instead");
        }

        double pixelRatio = plan.Crop is { } cr && info.Width > 0 && info.Height > 0
            ? (double)cr.Width * cr.Height / ((double)info.Width * info.Height) : 1;
        string encoder = await SaveEncoderAsync(hw, quality, info, pixelRatio, plan.Speed);

        // Only trims and cuts: straight on the graphics card, with any
        // unticked tracks left out in the same pass (no separate re-mix).
        bool plain = plan.Crop == null && Math.Abs(plan.Speed - 1) < 0.001 && Math.Abs(plan.Volume - 1) < 0.001 && !plan.Mute;
        if (plain && hw != EncoderKind.X264 && !(plan.TimeOnly && await StartsOnKeyframesAsync(source, info, kept, ct)))
        {
            var keep = plan.MutedTracks.Count > 0 && info.SeparateTracks.Count > 0
                ? info.SeparateTracks.Where(t => !plan.MutedTracks.Contains(t.Stream)).ToList()
                : null;
            if (await EncodeRangesOnGpuAsync(source, info, kept, output, hw, encoder, keep, progress, ct))
                return (true, false);
            if (ct.IsCancellationRequested) return (false, false);
            Log.Write("Graphics-card edit failed; using the standard route");
        }

        // Tracks switched off (standard route): rebuild the mix from the
        // ones kept first (sound only, the video is copied), then edit that.
        if (plan.MutedTracks.Count > 0 && info.SeparateTracks.Count > 0)
        {
            var keep = info.SeparateTracks.Where(t => !plan.MutedTracks.Contains(t.Stream)).ToList();
            var premix = Path.Combine(Paths.Temp, $"premix_{Guid.NewGuid():N}.mkv");
            string audio = keep.Count switch
            {
                0 => "",
                1 => $"-map 0:a:{keep[0].Stream} -c:a aac -b:a 192k ",
                _ => $"-filter_complex \"{string.Concat(keep.Select(k => $"[0:a:{k.Stream}]"))}" +
                     $"amix=inputs={keep.Count}:normalize=0:dropout_transition=0[m]\" -map \"[m]\" -c:a aac -b:a 192k ",
            };
            var mixArgs = $"-hide_banner -loglevel warning -i \"{source}\" -map 0:v:0 {audio}-c:v copy -y \"{premix}\"";
            try
            {
                if (await RunWithProgressAsync(mixArgs, info.Duration, null, ct) && File.Exists(premix))
                {
                    var premixInfo = info with { HasAudio = keep.Count > 0, AudioTracks = Array.Empty<string>() };
                    return await ExportAsync(premix, premixInfo, plan.WithoutTrackChoices(), output, hw, quality, progress, ct);
                }
            }
            finally { TryDelete(premix); }
            Log.Write("track mix failed; saving with all tracks");
        }

        if (plan.TimeOnly && await StartsOnKeyframesAsync(source, info, kept, ct))
        {
            if (await CopyRangesAsync(source, kept, output, ct))
            {
                progress.Report(1);
                return (true, true);
            }
            Log.Write("Instant edit failed; re-encoding instead");
        }

        var args = BuildEncodeArgs(source, info, plan, kept, output, hw, encoder);
        bool ok = await RunWithProgressAsync(args, plan.ResultSeconds, progress, ct);

        // A GPU that turns the job down still leaves the CPU.
        if (!ok && hw != EncoderKind.X264 && !ct.IsCancellationRequested)
        {
            Log.Write("GPU edit failed; retrying on the CPU");
            args = BuildEncodeArgs(source, info, plan, kept, output, EncoderKind.X264,
                                   await SaveEncoderAsync(EncoderKind.X264, quality, info, pixelRatio, plan.Speed));
            ok = await RunWithProgressAsync(args, plan.ResultSeconds, progress, ct);
        }
        return (ok && Usable(output), false);
    }

    private static async Task<bool> StartsOnKeyframesAsync(string source, MediaInfo info, List<TimeRange> kept, CancellationToken ct)
    {
        // A piece starting at the very beginning needs no keyframe check.
        if (kept.All(r => r.Start < 0.001)) return true;

        var keys = await KeyframesAsync(source, ct);
        double slack = 0.5 / Math.Max(1, info.Fps);
        return kept.All(r => r.Start < 0.001 || keys.Any(k => Math.Abs(k - r.Start) <= slack));
    }

    private static async Task<bool> CopyRangesAsync(string source, List<TimeRange> kept, string output, CancellationToken ct)
    {
        var parts = new List<string>();
        string? list = null;
        try
        {
            string stem = Path.Combine(Paths.Temp, "edit_" + Guid.NewGuid().ToString("N")[..8]);
            for (int i = 0; i < kept.Count; i++)
            {
                var part = kept.Count == 1 ? output : $"{stem}_{i}.mp4";
                var r = kept[i];
                var args = $"-hide_banner -loglevel error -ss {F(r.Start)} -i \"{source}\" -t {F(r.Length)} " +
                           "-map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero " +
                           (kept.Count == 1 ? "-movflags +faststart " : "") + $"-y \"{part}\"";
                if (!await RunAsync(args, ct) || !Usable(part)) return false;
                if (kept.Count > 1) parts.Add(part);
            }

            if (kept.Count == 1) return true;

            list = stem + ".txt";
            var sb = new StringBuilder();
            foreach (var p in parts) sb.AppendLine($"file '{p.Replace("\\", "/").Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(list, sb.ToString(), ct);

            var join = $"-hide_banner -loglevel error -f concat -safe 0 -i \"{list}\" -map 0 -c copy " +
                       $"-movflags +faststart -y \"{output}\"";
            return await RunAsync(join, ct) && Usable(output);
        }
        finally
        {
            foreach (var p in parts) TryDelete(p);
            TryDelete(list);
        }
    }

    /// <summary>
    /// Trims and cuts saved in seconds, as LosslessCut does: the picture is
    /// copied, not re-encoded, so each part starts on the keyframe at or just
    /// before its cut (recordings have one every 2 seconds). The sound is
    /// copied too, or, with tracks switched off or the volume changed, only
    /// the main mix is made again (a light job); the kept tracks stay as they
    /// were. Picture quality and size stay exactly the original's.
    /// </summary>
    private static async Task<bool> InstantExportAsync(
        string source, MediaInfo info, EditPlan plan, List<TimeRange> kept, string output,
        IProgress<double> progress, CancellationToken ct)
    {
        var keys = await KeyframesAsync(source, ct);
        if (ct.IsCancellationRequested || keys.Count == 0) return false;

        // Each part from its keyframe; parts that now overlap become one.
        double half = 0.5 / Math.Max(1, info.Fps);
        var parts = new List<TimeRange>();
        double removedUpTo = 0;   // where the last kept part ended (0: the clip's start)
        foreach (var r in kept)
        {
            double k = r.Start < half ? 0 : keys.LastOrDefault(t => t <= r.Start + half, 0);
            // Starting on the keyframe brings back part of what was cut. Fine for
            // a long cut; but a cut shorter than the gap between keyframes (2 s)
            // came back whole, and the edit seemed not to save at all (a short
            // clip, say). If more than half a cut would come back, it's saved
            // exactly instead (re-encoded, still quick).
            double gap = r.Start - removedUpTo;
            if (gap > half && r.Start - k > gap * 0.5)
            {
                Log.Write($"Instant save: starting on keyframes would undo a {gap:0.00} s cut; saving exactly instead");
                return false;
            }
            removedUpTo = r.End;
            if (parts.Count > 0 && k <= parts[^1].End + half)
                parts[^1] = new TimeRange(parts[^1].Start, Math.Max(parts[^1].End, r.End));
            else parts.Add(new TimeRange(k, r.End));
        }
        double shift = parts.Sum(p => p.Length) - kept.Sum(r => r.Length);
        Log.Write($"Instant save: {parts.Count} part(s), {shift:0.00} s added by starting on keyframes");

        // Sound: as recorded, or a new main mix (the kept tracks follow, as recorded).
        bool volume = Math.Abs(plan.Volume - 1) > 0.001;
        string sound;
        if (plan.Mute || !info.HasAudio) sound = "-map 0:v:0 -an -c:v copy ";
        else if (plan.MutedTracks.Count == 0 && !volume) sound = "-map 0:v:0 -map 0:a? -c copy ";
        else
        {
            var keep = info.SeparateTracks.Where(t => !plan.MutedTracks.Contains(t.Stream)).ToList();
            string vol = volume ? $",volume={plan.Volume.ToString("0.###", Inv)}" : "";
            if (info.SeparateTracks.Count == 0)
                sound = $"-filter_complex \"[0:a:0]anull{vol}[m]\" -map 0:v:0 -map \"[m]\" -c:v copy -c:a aac -b:a 192k ";
            else if (keep.Count == 0) sound = "-map 0:v:0 -an -c:v copy ";
            else
            {
                // Only the volume changed: the recorded mix, louder or quieter. Tracks
                // switched off: a new mix of the ones kept.
                var sources = plan.MutedTracks.Count == 0 ? new List<int> { -1 } : keep.Select(k => k.Stream).ToList();
                var ins = string.Concat(sources.Select(st => st < 0 ? "[0:a:0]" : $"[0:a:{st}]"));
                var mix = sources.Count == 1 ? $"{ins}anull{vol}[m]" : $"{ins}amix=inputs={sources.Count}:normalize=0:dropout_transition=0{vol}[m]";
                var sb = new StringBuilder($"-filter_complex \"{mix}\" -map 0:v:0 -map \"[m]\" ");
                // Unticked tracks are left out; with only the volume changed, every track stays.
                foreach (var k in keep) sb.Append($"-map 0:a:{k.Stream} ");
                sb.Append("-c:v copy -c:a copy -c:a:0 aac -b:a:0 192k ");
                sound = sb.ToString();
            }
        }

        double total = Math.Max(0.001, parts.Sum(r => r.Length));
        var files = new List<string>();
        string? list = null;
        string stem = Path.Combine(Paths.Temp, "edit_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            double done = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var r = parts[i];
                var part = parts.Count == 1 ? output : $"{stem}_{i}.mp4";
                // A hair past the keyframe, so rounding can't land on the one before.
                double from = r.Start < half ? 0 : r.Start + Math.Min(half, 0.001);
                var args = $"-hide_banner -loglevel error -nostdin -ss {F(from)} -i \"{source}\" -t {F(r.End - from)} " +
                           sound + "-avoid_negative_ts make_zero " +
                           (parts.Count == 1 ? "-movflags +faststart " : "") + $"-y \"{part}\"";
                if (!await RunAsync(args, ct) || !Usable(part)) return false;
                if (parts.Count > 1) files.Add(part);
                done += r.Length;
                progress.Report(done / total * 0.9);
            }
            if (parts.Count > 1)
            {
                list = stem + ".txt";
                var sb = new StringBuilder();
                foreach (var p in files) sb.AppendLine($"file '{p.Replace("\\", "/").Replace("'", "'\\''")}'");
                await File.WriteAllTextAsync(list, sb.ToString(), ct);
                var join = $"-hide_banner -loglevel error -nostdin -f concat -safe 0 -i \"{list}\" -map 0 -c copy -movflags +faststart -y \"{output}\"";
                if (!await RunAsync(join, ct) || !Usable(output)) return false;
            }
            progress.Report(1);
            return true;
        }
        finally
        {
            foreach (var p in files) TryDelete(p);
            TryDelete(list);
        }
    }

    /// <summary>
    /// Trims and cuts only: each kept part is decoded and encoded on the
    /// graphics card (NVIDIA keeps frames on the card the whole way), starting
    /// right at the part instead of decoding the clip from the beginning.
    /// Parts are then joined without re-encoding. Every sound track is kept.
    /// </summary>
    private static async Task<bool> EncodeRangesOnGpuAsync(
        string source, MediaInfo info, List<TimeRange> kept, string output,
        EncoderKind hw, string encoder, IReadOnlyList<(int Stream, string Name)>? keep,
        IProgress<double> progress, CancellationToken ct)
    {
        string decode = hw == EncoderKind.Nvenc ? "-hwaccel cuda -hwaccel_output_format cuda " + VideoDecoders.ArgsFor(info.VideoCodec, cudaPath: true)
                                                : "-hwaccel auto " + VideoDecoders.ArgsFor(info.VideoCodec);

        // Sound: every track as it is; or, with tracks unticked, a new Mix of
        // the kept ones (the default) followed by each kept track by name.
        string sound;
        if (!info.HasAudio) sound = "-map 0:v:0 ";
        // The mix at 192 kb/s, the separate tracks at 160 (what they were recorded at).
        else if (keep == null) sound = "-map 0:v:0 -map 0:a? -c:a aac -b:a 160k -b:a:0 192k ";
        else if (keep.Count == 0) sound = "-map 0:v:0 -an ";
        else
        {
            var ins = string.Concat(keep.Select(k => $"[0:a:{k.Stream}]"));
            var mix = keep.Count == 1 ? $"{ins}anull[m]" : $"{ins}amix=inputs={keep.Count}:normalize=0:dropout_transition=0[m]";
            var sb = new StringBuilder($"-filter_complex \"{mix}\" -map 0:v:0 -map \"[m]\" ");
            foreach (var k in keep) sb.Append($"-map 0:a:{k.Stream} ");
            sb.Append("-c:a aac -b:a 160k -b:a:0 192k -metadata:s:a:0 title=Mix -metadata:s:a:0 handler_name=Mix -disposition:a:0 default ");
            for (int i = 0; i < keep.Count; i++)
            {
                var n = keep[i].Name.Replace("\"", "'");
                sb.Append($"-metadata:s:a:{i + 1} title=\"{n}\" -metadata:s:a:{i + 1} handler_name=\"{n}\" -disposition:a:{i + 1} 0 ");
            }
            sound = sb.ToString();
        }
        double total = Math.Max(0.001, kept.Sum(r => r.Length));
        var parts = new List<string>();
        string? list = null;
        string stem = Path.Combine(Paths.Temp, "edit_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            double done = 0;
            for (int i = 0; i < kept.Count; i++)
            {
                var r = kept[i];
                var part = kept.Count == 1 ? output : $"{stem}_{i}.mp4";
                double before = done;
                var partProgress = new Progress<double>(p => progress.Report((before + p * r.Length) / total));
                var args = $"-hide_banner -loglevel error -nostats {decode}-ss {F(r.Start)} -t {F(r.Length)} -i \"{source}\" " +
                           sound + encoder +
                           (kept.Count == 1 ? "-movflags +faststart " : "") +
                           $"-progress pipe:1 -y \"{part}\"";
                if (!await RunWithProgressAsync(args, r.Length, partProgress, ct) || !Usable(part)) return false;
                if (kept.Count > 1) parts.Add(part);
                done += r.Length;
            }
            if (kept.Count == 1) { progress.Report(1); return true; }

            list = stem + ".txt";
            var sb = new StringBuilder();
            foreach (var p in parts) sb.AppendLine($"file '{p.Replace("\\", "/").Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(list, sb.ToString(), ct);
            var join = $"-hide_banner -loglevel error -f concat -safe 0 -i \"{list}\" -map 0 -c copy -movflags +faststart -y \"{output}\"";
            bool ok = await RunAsync(join, ct) && Usable(output);
            if (ok) progress.Report(1);
            return ok;
        }
        finally
        {
            foreach (var p in parts) TryDelete(p);
            TryDelete(list);
        }
    }

    /// <summary>
    /// Crop, speed or volume changes. Each kept part is still read from its
    /// own start (not decoded from the beginning of the clip), and every
    /// sound track is kept, with its name.
    /// </summary>
    private static string BuildEncodeArgs(
        string source, MediaInfo info, EditPlan plan, List<TimeRange> kept,
        string output, EncoderKind hw, string encoder)
    {
        bool audio = info.HasAudio && !plan.Mute;
        int tracks = audio ? Math.Max(1, info.AudioTracks.Count) : 0;
        var g = new StringBuilder();

        // One input per kept part, each starting right at it.
        var inputs = new StringBuilder();
        foreach (var r in kept)
            inputs.Append($"-hwaccel auto {VideoDecoders.ArgsFor(info.VideoCodec)}-ss {F(r.Start)} -t {F(r.Length)} -i \"{source}\" ");

        var joinInputs = new StringBuilder();
        for (int i = 0; i < kept.Count; i++)
        {
            joinInputs.Append($"[{i}:v:0]");
            for (int k = 0; k < tracks; k++) joinInputs.Append($"[{i}:a:{k}]");
        }
        g.Append($"{joinInputs}concat=n={kept.Count}:v=1:a={tracks}[vc]");
        for (int k = 0; k < tracks; k++) g.Append($"[ac{k}]");
        g.Append(';');

        // Picture: crop, then speed. NV12 goes to graphics-card encoders as is.
        var v = new List<string>();
        if (plan.Crop is { } c)
        {
            int cw = Even(c.Width), ch = Even(c.Height);
            v.Add($"crop={cw}:{ch}:{Math.Max(0, c.X)}:{Math.Max(0, c.Y)}");
        }
        if (Math.Abs(plan.Speed - 1) > 0.001) v.Add($"setpts=PTS/{F(plan.Speed)}");
        v.Add(hw == EncoderKind.X264 ? "format=yuv420p" : "format=nv12");
        g.Append($"[vc]{string.Join(",", v)}[vo]");

        // Sound: speed without changing pitch, then level, on every track.
        var a = new List<string>();
        double s = plan.Speed;
        while (s > 2.0 + 1e-9) { a.Add("atempo=2.0"); s /= 2.0; }
        while (s < 0.5 - 1e-9) { a.Add("atempo=0.5"); s /= 0.5; }
        if (Math.Abs(s - 1) > 0.001) a.Add($"atempo={F(s)}");
        if (Math.Abs(plan.Volume - 1) > 0.001) a.Add($"volume={F(plan.Volume)}");
        if (a.Count == 0) a.Add("anull");
        for (int k = 0; k < tracks; k++) g.Append($";[ac{k}]{string.Join(",", a)}[ao{k}]");

        var args = new StringBuilder();
        args.Append("-hide_banner -loglevel error -nostats ").Append(inputs);
        args.Append($"-filter_complex \"{g}\" -map \"[vo]\" ");
        if (tracks == 0) args.Append("-an ");
        else
        {
            args.Append(string.Concat(Enumerable.Range(0, tracks).Select(k => $"-map \"[ao{k}]\" ")));
            args.Append("-c:a aac ");
            for (int k = 0; k < tracks; k++)
            {
                args.Append($"-b:a:{k} {(k == 0 ? 192 : 128)}k ");
                if (k < info.AudioTracks.Count)
                {
                    var name = info.AudioTracks[k].Replace("\"", "'");
                    args.Append($"-metadata:s:a:{k} title=\"{name}\" -metadata:s:a:{k} handler_name=\"{name}\" ");
                }
                args.Append($"-disposition:a:{k} {(k == 0 ? "default" : "0")} ");
            }
        }
        args.Append(encoder);
        args.Append($"-movflags +faststart -progress pipe:1 -y \"{output}\"");
        return args.ToString();
    }

    /// <summary>
    /// The quickest settings each graphics-card encoder has: NVIDIA's p1 runs
    /// several times faster than its quality presets, and at the quality level
    /// used here the difference is hard to see.
    /// </summary>
    private static string FastEncoder(EncoderKind hw, int q) => hw switch
    {
        EncoderKind.Nvenc => $"-c:v h264_nvenc -preset p1 -tune hq -rc vbr -cq {q} -b:v 0 -bf 0 -rc-lookahead 0 -profile:v high ",
        EncoderKind.Amf => $"-c:v h264_amf -quality speed -rc cqp -qp_i {q} -qp_p {q + 2} ",
        EncoderKind.Qsv => $"-c:v h264_qsv -preset veryfast -global_quality {q} ",
        _ => VideoEncoder(hw, q),
    };

    /// <summary>
    /// Writes each sound track's name into a saved file (Mix, Discord,
    /// Microphone…). Joining pieces loses them, and players then show
    /// "Track 1, 2, 3". A copy, not a re-encode. Only when the number of
    /// tracks matches, so a name never lands on the wrong track.
    /// </summary>
    /// <summary>
    /// Takes the Mix out of a file with separate tracks, so each source (PC
    /// audio, mic, Discord…) is in it on its own: a video editor otherwise
    /// plays the Mix and the sources together. A plain copy: no quality lost.
    /// </summary>
    public static async Task<bool> DropMixAsync(string file)
    {
        try
        {
            var info = await ProbeAsync(file);
            if (info == null || info.AudioTracks.Count < 2 || info.AudioTracks[0] != "Mix") return false;
            var temp = file + ".tracks.mp4";
            var maps = new StringBuilder("-map 0:v? ");
            var meta = new StringBuilder();
            for (int i = 1; i < info.AudioTracks.Count; i++)
            {
                int o = i - 1;
                var n = info.AudioTracks[i].Replace("\"", "'");
                maps.Append($"-map 0:a:{i} ");
                meta.Append($"-metadata:s:a:{o} title=\"{n}\" -metadata:s:a:{o} handler_name=\"{n}\" -disposition:a:{o} {(o == 0 ? "default" : "0")} ");
            }
            var args = $"-hide_banner -loglevel error -i \"{file}\" {maps}-c copy {meta}-movflags +faststart -y \"{temp}\"";
            if (await RunAsync(args, CancellationToken.None) && Usable(temp)) { File.Move(temp, file, true); return true; }
            TryDelete(temp);
        }
        catch (Exception ex) { Log.Error("separate tracks", ex); }
        return false;
    }

    public static async Task NameTracksAsync(string file, IReadOnlyList<string> names)
    {
        try
        {
            if (names.Count == 0 || await MediaTools.AudioStreamCountAsync(file) != names.Count) return;
            var temp = file + ".named.mp4";
            var meta = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[i].Replace("\"", "'");
                meta.Append($"-metadata:s:a:{i} title=\"{n}\" -metadata:s:a:{i} handler_name=\"{n}\" -disposition:a:{i} {(i == 0 ? "default" : "0")} ");
            }
            var args = $"-hide_banner -loglevel error -i \"{file}\" -map 0 -c copy {meta}-movflags +faststart -y \"{temp}\"";
            if (await RunAsync(args, CancellationToken.None) && Usable(temp)) File.Move(temp, file, true);
            else TryDelete(temp);
        }
        catch (Exception ex) { Log.Error("name tracks", ex); }
    }

    /// <summary>
    /// Settings for saving an edit, matched to the clip it came from: the same
    /// codec (AV1 stays AV1) when this PC's encoder can make it, and a bitrate
    /// ceiling taken from the original's own bitrate. Before, edits were saved
    /// at unlimited bitrate with the fastest (least efficient) settings, always
    /// as H.264: a 47-second cut of a 13 Mbps AV1 clip came out at ~68 Mbps,
    /// almost as big as the 5-minute original. Constant quality still decides
    /// the picture; the ceiling only stops it outgrowing the source.
    /// </summary>
    /// <summary>
    /// The bitrate allowance for a result made from several clips: each clip's
    /// own rate, weighted by how much of it is used. Two clips joined get room
    /// for both (20 MB + 30 MB of footage allows about 50 MB), and each part is
    /// held to roughly its own quality, not the first clip's.
    /// </summary>
    public static int WeightedKbps(IEnumerable<(MediaInfo Info, double Seconds)> parts)
    {
        double bits = 0, secs = 0;
        foreach (var (info, s) in parts)
        {
            if (s <= 0) continue;
            double kbps = info.Kbps > 0 ? info.Kbps : 20000;
            bits += kbps * s;
            secs += s;
        }
        return secs > 0 ? (int)(bits / secs) : 0;
    }

    /// <param name="pixelRatio">Share of the picture kept (a crop makes less to encode).</param>
    /// <param name="speed">Playback speed (faster packs more frames into each second).</param>
    public static async Task<string> SaveEncoderAsync(EncoderKind hw, int q, MediaInfo source,
                                                      double pixelRatio = 1, double speed = 1)
    {
        var from = source.VideoCodec.ToLowerInvariant() switch
        {
            "av1" => VideoCodec.Av1,
            "hevc" or "h265" => VideoCodec.Hevc,
            _ => VideoCodec.H264,
        };
        var codec = VideoCodec.H264;
        if (hw != EncoderKind.X264 && from != VideoCodec.H264)
        {
            try { if ((await EncoderDetector.AvailableForAsync(from)).Contains(hw)) codec = from; }
            catch { }
        }

        // H.264 needs more bits than AV1 or HEVC for the same picture.
        double factor = (from, codec) switch
        {
            (VideoCodec.Av1, VideoCodec.H264) => 1.8,
            (VideoCodec.Hevc, VideoCodec.H264) => 1.4,
            _ => 1.15,   // the same codec: a little headroom, as re-encoding loses some
        };
        double srcKbps = source.Kbps > 0 ? source.Kbps : 20000;
        int cap = (int)Math.Max(2000, srcKbps * factor * Math.Clamp(pixelRatio, 0.05, 1) * Math.Max(1, speed));
        int target = (int)(cap / 1.15);
        string limits = $"-maxrate {cap}k -bufsize {cap * 2}k ";
        string name = EncoderDetector.FFmpegName(hw == EncoderKind.X264 ? EncoderKind.X264 : hw, codec);
        Log.Write($"Edit save: {name}, source {source.VideoCodec} at {srcKbps:0} kb/s, ceiling {cap} kb/s");

        return hw switch
        {
            EncoderKind.Nvenc => $"-c:v {name} -preset p4 -tune hq -rc vbr -cq {q} -b:v 0 {limits}" +
                                 "-rc-lookahead 16 -spatial-aq 1 " +
                                 (codec == VideoCodec.H264 ? "-bf 2 -profile:v high " : codec == VideoCodec.Hevc ? "-bf 2 -profile:v main " : ""),
            EncoderKind.Amf => $"-c:v {name} -quality balanced -rc vbr_peak -b:v {target}k {limits}" +
                               (codec == VideoCodec.H264 ? "-profile:v high " : ""),
            EncoderKind.Qsv => $"-c:v {name} -preset medium -b:v {target}k {limits}",
            _ => $"-c:v libx264 -preset veryfast -crf {q} {limits}-profile:v high ",
        };
    }

    /// <summary>Encoder settings for the timeline renderer (quick ones for work being waited on).</summary>
    public static string EncoderArgs(EncoderKind hw, int quality) => FastEncoder(hw, quality);

    /// <summary>The best video encoder on this PC: NVIDIA, AMD, Intel, then the CPU.</summary>
    public static async Task<EncoderKind> BestEncoderAsync()
    {
        var have = await EncoderDetector.AvailableAsync();
        foreach (var k in new[] { EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv })
            if (have.Contains(k)) return k;
        return EncoderKind.X264;
    }

    private static string VideoEncoder(EncoderKind hw, int q) => hw switch
    {
        // Quick settings: you're waiting for this one.
        EncoderKind.Nvenc => $"-c:v h264_nvenc -preset p3 -tune hq -rc vbr -cq {q} -b:v 0 -bf 2 -rc-lookahead 8 -spatial-aq 1 -profile:v high ",
        EncoderKind.Amf => $"-c:v h264_amf -quality quality -rc cqp -qp_i {q} -qp_p {q + 2} -qp_b {q + 4} ",
        EncoderKind.Qsv => $"-c:v h264_qsv -preset medium -global_quality {q} ",
        _ => $"-c:v libx264 -preset veryfast -crf {q} -profile:v high ",
    };

    /* ---- joining clips --------------------------------------------------- */

    /// <summary>
    /// Joins clips end to end. Clips recorded with the same settings are
    /// copied together instantly; mixed sizes or frame rates are scaled to
    /// match the first clip and re-encoded.
    /// </summary>
    public static async Task<bool> JoinAsync(
        IReadOnlyList<string> sources, string output, EncoderKind hw, int quality,
        IProgress<double> progress, CancellationToken ct)
    {
        var infos = new List<MediaInfo>();
        foreach (var src in sources)
        {
            var info = await ProbeAsync(src);
            if (info == null || info.Width == 0) return false;
            infos.Add(info);
        }

        var first = infos[0];
        bool alike = infos.All(i =>
            i.Width == first.Width && i.Height == first.Height &&
            Math.Abs(i.Fps - first.Fps) < 0.5 && i.VideoCodec == first.VideoCodec &&
            i.HasAudio == first.HasAudio);

        if (alike)
        {
            string list = Path.Combine(Paths.Temp, "join_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            try
            {
                var sb = new StringBuilder();
                foreach (var p in sources) sb.AppendLine($"file '{p.Replace("\\", "/").Replace("'", "'\\''")}'");
                await File.WriteAllTextAsync(list, sb.ToString(), ct);
                var copy = $"-hide_banner -loglevel error -f concat -safe 0 -i \"{list}\" -map 0 -c copy " +
                           $"-movflags +faststart -y \"{output}\"";
                if (await RunAsync(copy, ct) && Usable(output)) { progress.Report(1); return true; }
                Log.Write("Instant join failed; re-encoding instead");
            }
            finally { TryDelete(list); }
        }

        bool anyAudio = infos.Any(i => i.HasAudio);
        int w = Even(first.Width), h = Even(first.Height);
        string fps = F(first.Fps);

        var inputs = new StringBuilder();
        var g = new StringBuilder();
        var join = new StringBuilder();
        for (int i = 0; i < sources.Count; i++)
        {
            inputs.Append($"-i \"{sources[i]}\" ");
            g.Append($"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease," +
                     $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format={(hw == EncoderKind.X264 ? "yuv420p" : "nv12")}[v{i}];");
            join.Append($"[v{i}]");
            if (anyAudio)
            {
                // A clip without sound gets silence of its own length, so
                // the sound of the next clip does not slide under it.
                g.Append(infos[i].HasAudio
                    ? $"[{i}:a]aresample=48000,aformat=channel_layouts=stereo[a{i}];"
                    : $"anullsrc=r=48000:cl=stereo,atrim=duration={F(infos[i].Duration)}[a{i}];");
                join.Append($"[a{i}]");
            }
        }
        g.Append($"{join}concat=n={sources.Count}:v=1:a={(anyAudio ? 1 : 0)}[vo]{(anyAudio ? "[ao]" : "")}");

        double total = infos.Sum(i => i.Duration);
        // The allowance covers every clip joined, each by its own length.
        var joined = first with { Kbps = WeightedKbps(infos.Select(i => (i, i.Duration))) };
        string encHw = await SaveEncoderAsync(hw, quality, joined);
        string encCpu = hw == EncoderKind.X264 ? encHw : await SaveEncoderAsync(EncoderKind.X264, quality, joined);
        string Args(EncoderKind nv) =>
            $"-hide_banner -loglevel error -nostats {inputs}-filter_complex \"{g}\" -map \"[vo]\" " +
            (anyAudio ? "-map \"[ao]\" -c:a aac -b:a 192k " : "-an ") +
            (nv == hw ? encHw : encCpu) + $"-movflags +faststart -progress pipe:1 -y \"{output}\"";

        if (await RunWithProgressAsync(Args(hw), total, progress, ct) && Usable(output)) return true;
        if (hw != EncoderKind.X264 && !ct.IsCancellationRequested)
            return await RunWithProgressAsync(Args(EncoderKind.X264), total, progress, ct) && Usable(output);
        return false;
    }

    /* ---- running FFmpeg -------------------------------------------------- */

    private static Task<bool> RunAsync(string args, CancellationToken ct) =>
        RunWithProgressAsync(args, 0, null, ct);

    /// <summary>Runs FFmpeg, reporting progress from its -progress output when asked.</summary>
    private static async Task<bool> RunWithProgressAsync(
        string args, double expectedSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        Log.Write("ffmpeg (edit) " + args);
        Process? p = null;
        try
        {
            p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Paths.FFmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            var errors = new StringBuilder();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null || progress == null || expectedSeconds <= 0) return;
                if (e.Data.StartsWith("out_time_us=") &&
                    long.TryParse(e.Data.AsSpan(12), out var us) && us > 0)
                    progress.Report(Math.Clamp(us / 1_000_000.0 / expectedSeconds, 0, 1));
            };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };

            p.Start();
            // Work you're waiting on goes first.
            try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
            ChildProcess.KeepAwake(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                await p.WaitForExitAsync(CancellationToken.None);

            if (p.ExitCode != 0 && !ct.IsCancellationRequested)
                lock (errors) Log.Write("ffmpeg (edit) failed:\n" + errors);
            return p.ExitCode == 0 && !ct.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            Log.Error("ffmpeg (edit)", ex);
            return false;
        }
        finally { p?.Dispose(); }
    }

    private static int Even(int v) => Math.Max(2, v - (v % 2));

    private static bool Usable(string path) => File.Exists(path) && new FileInfo(path).Length > 1024;

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }
}
