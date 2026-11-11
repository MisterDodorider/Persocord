using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>Thumbnails and size-targeted re-encoding, both via FFmpeg.</summary>
public static class MediaTools
{
    // Work you're waiting on goes first.
    private static Task<bool> RunAsync(string args, int timeoutMs) =>
        FFmpegRun.RunAsync(args, timeoutMs, ProcessPriorityClass.High);

    /// <summary>Grabs a frame for the library list. Returns the file name, or null.</summary>
    public static async Task<string?> MakeThumbnailAsync(string videoPath, string folder, string id)
    {
        try
        {
            // In Persocord's own thumbnails folder, not next to the clips, so
            // clips deleted in Explorer don't leave pictures behind.
            var outPath = Path.Combine(Paths.Thumbs, $"{id}.jpg");
            var name = outPath;

            // A frame from a second in is far more representative than frame
            // zero, which on screen recordings is often a blank window.
            var args =
                $"-hide_banner -loglevel error -hwaccel auto -ss 1 -i \"{videoPath}\" " +
                $"-frames:v 1 -vf scale=320:-2 -strict unofficial -q:v 5 -y \"{outPath}\"";

            if (!await RunAsync(args, 15000) || !File.Exists(outPath))
            {
                // Clips shorter than a second have no frame at that offset.
                args = $"-hide_banner -loglevel error -i \"{videoPath}\" " +
                       $"-frames:v 1 -vf scale=320:-2 -strict unofficial -q:v 5 -y \"{outPath}\"";
                if (!await RunAsync(args, 15000) || !File.Exists(outPath)) return null;
            }

            return name;
        }
        catch (Exception ex) { Log.Error("thumbnail", ex); return null; }
    }

    /// <summary>
    /// Re-encodes to land under a byte budget.
    ///
    /// Bitrate is derived from the target size and the clip's length rather
    /// than guessed, because that is the only relationship that actually
    /// governs the output size. A margin covers container overhead, which a
    /// bare calculation always underestimates.
    /// </summary>
    public static async Task<bool> ShrinkToFitAsync(
        string source, string destination, long targetBytes,
        int audioKbps, EncoderKind hw, int maxHeight = 1080, bool keepAllTracks = true)
    {
        try
        {
            double seconds = await DurationSecondsAsync(source);
            if (seconds <= 0.2) return false;

            // Constant bitrate spends exactly this, so only a small margin
            // (for the container) is kept back: it lands just under the size
            // asked for, instead of well below it.
            long budget = (long)(targetBytes * 0.97);
            int totalKbps = (int)(budget * 8 / seconds / 1000);
            // Sound gets a share of the budget, not a fixed amount: with nine
            // tracks at full rates, a 60-second clip squeezed to 10 MB spent
            // three quarters of the file on sound and left the picture at 480p.
            // The main mix (what players play) gets about 12%; the separate
            // tracks, when kept, shrink with the budget. A link's copy keeps
            // only the mix, the only track a viewer hears.
            int tracks = await AudioStreamCountAsync(source);
            int mixKbps = Math.Clamp((int)(totalKbps * 0.12), totalKbps < 500 ? 32 : 64, Math.Max(64, audioKbps));
            int extraKbps = Math.Clamp((int)(totalKbps * 0.08 / Math.Max(1, tracks - 1)), 32, Math.Max(32, Math.Min(96, audioKbps)));
            if (!keepAllTracks && tracks > 1) tracks = 1;
            // The separate tracks are kept only while the picture can still be
            // decent beside them. A long clip with many tracks squeezed small
            // gave each track its minimum, those minimums alone came to more than
            // the whole budget, and a 10-minute clip "compressed to 10 MB" came
            // out at 30 MB. Then only the mix (what players play) is kept.
            if (tracks > 1 && totalKbps - (mixKbps + (tracks - 1) * extraKbps) < Math.Max(250, totalKbps / 2))
            {
                Log.Write($"Compress: {tracks - 1} separate sound tracks left out so the size fits (the main mix is kept)");
                tracks = 1;
            }
            int audioTotal = tracks == 0 ? 0 : mixKbps + (tracks - 1) * extraKbps;
            int videoKbps = totalKbps - audioTotal;
            Log.Write($"Compress: {seconds:0.#} s to {targetBytes / 1048576.0:0.#} MB: {totalKbps} kb/s in all; sound {audioTotal} kb/s ({tracks} tracks), picture {videoKbps} kb/s");

            // Plenty of bitrate: keep the full picture, even above 1080p.
            if (videoKbps >= 6000) maxHeight = Math.Max(maxHeight, 2160);

            // Little bitrate: a smaller, cleaner picture looks better than a
            // big one starved of detail (1080p was kept down to 120 kb/s).
            if (videoKbps < 300)
            {
                videoKbps = Math.Max(60, videoKbps);
                maxHeight = Math.Min(maxHeight, videoKbps < 150 ? 360 : 480);
            }

            var plan = new Plan(seconds, targetBytes, mixKbps, extraKbps, tracks, audioTotal, maxHeight);
            if (hw is EncoderKind.Nvenc or EncoderKind.Amf or EncoderKind.Qsv)
            {
                // Decoding, resizing and encoding all on the graphics card.
                // Several times faster than the CPU route; at very tight
                // sizes slightly softer, which is why the CPU stays available.
                if (await EncodeToFitAsync(source, destination, videoKbps, plan, hw))
                {
                    if (new FileInfo(destination).Length <= targetBytes) return true;
                    // Still over after its corrections: the CPU's encoder keeps to a
                    // bitrate exactly. Its copy is used if it comes out smaller.
                    Log.Write($"Compress: the graphics card's copy is {new FileInfo(destination).Length / 1048576.0:0.#} MB, over the size; trying the CPU");
                    var cpu = Path.Combine(Paths.Temp, $"compress_{Guid.NewGuid():N}.mp4");
                    try
                    {
                        if (await EncodeToFitAsync(source, cpu, videoKbps, plan, EncoderKind.X264) &&
                            new FileInfo(cpu).Length < new FileInfo(destination).Length)
                            File.Move(cpu, destination, true);
                    }
                    finally { try { File.Delete(cpu); } catch { } }
                    return true;
                }
                Log.Write("GPU shrink failed; using the CPU");
            }

            return await EncodeToFitAsync(source, destination, videoKbps, plan, EncoderKind.X264);
        }
        catch (Exception ex) { Log.Error("shrink", ex); return false; }
    }

    /// <summary>What a compress aims for, worked out once from the clip and the size.</summary>
    private sealed record Plan(double Seconds, long TargetBytes, int MixKbps, int ExtraKbps, int Tracks, int AudioKbps, int MaxHeight);

    private static async Task<bool> EncodeToFitAsync(string source, string destination, int videoKbps, Plan plan, EncoderKind hw)
    {
        int tracks = plan.Tracks, maxHeight = plan.MaxHeight;
        // The picture and the sound tracks kept (the mix first), each named as before.
        var maps = "-map 0:v:0 " + (tracks == 1 ? "-map 0:a:0 " : tracks > 1 ? "-map 0:a " : "");
        var sound = tracks == 0 ? "-an " :
            "-c:a aac " + string.Concat(Enumerable.Range(0, tracks).Select(i =>
                $"-b:a:{i} {(i == 0 ? plan.MixKbps : plan.ExtraKbps)}k "));
        // The graphics card's own encoder where there is one: NVIDIA does
        // everything on the card; AMD and Intel decode and resize on the CPU
        // and encode on the card, still far quicker than all-CPU.
        // Constant bitrate on every encoder, padded so still scenes don't
        // come in under it: variable bitrate saved bits on easy screen footage
        // and left files far smaller than asked. Fast settings throughout,
        // decoding on the graphics card too.
        string Args(int kbps, string destination) => hw switch
        {
            EncoderKind.Nvenc =>
              $"-hide_banner -loglevel error -hwaccel cuda -hwaccel_output_format cuda -i \"{source}\" {maps}" +
              $"-vf \"scale_cuda=-2:'min({maxHeight},ih)'\" " +
              $"-c:v h264_nvenc -preset p2 -tune hq -rc cbr -cbr_padding 1 -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps}k " +
              $"-bf 0 -rc-lookahead 0 {sound}-movflags +faststart -y \"{destination}\"",
            EncoderKind.Amf =>
              $"-hide_banner -loglevel error -hwaccel auto -i \"{source}\" {maps}-vf \"scale=-2:'min({maxHeight},ih)',format=nv12\" " +
              $"-c:v h264_amf -quality speed -rc cbr -filler_data 1 -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps}k " +
              $"{sound}-movflags +faststart -y \"{destination}\"",
            EncoderKind.Qsv =>
              $"-hide_banner -loglevel error -hwaccel auto -i \"{source}\" {maps}-vf \"scale=-2:'min({maxHeight},ih)',format=nv12\" " +
              $"-c:v h264_qsv -preset veryfast -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps}k " +
              $"{sound}-movflags +faststart -y \"{destination}\"",
            _ =>
              $"-hide_banner -loglevel error -i \"{source}\" {maps}-vf \"scale=-2:'min({maxHeight},ih)'\" " +
              $"-c:v libx264 -preset veryfast -b:v {kbps}k -minrate {kbps}k -maxrate {kbps}k -bufsize {kbps}k " +
              $"-x264-params nal-hrd=cbr:force-cfr=1 -pix_fmt yuv420p {sound}-movflags +faststart -y \"{destination}\"",
        };

        // Encoded, then measured: over the size, the picture's share is scaled
        // by how far over it came (the sound's share is about fixed) and it's
        // done again, up to three times in all. (It used to correct once, and
        // not at all at low bitrates, then call the result the size asked for.)
        // Later passes go to a spare file, so a pass that fails never spoils
        // the copy already made.
        string Spare() => Path.Combine(Paths.Temp, $"compress_{Guid.NewGuid():N}.mp4");
        int kbps = videoKbps;
        for (int pass = 0; pass < 3; pass++)
        {
            string output = pass == 0 ? destination : Spare();
            try
            {
                if (!await RunAsync(Args(kbps, output), 600000) || !File.Exists(output))
                    return pass > 0 && File.Exists(destination);
                if (pass > 0)
                {
                    if (new FileInfo(output).Length < new FileInfo(destination).Length) File.Move(output, destination, true);
                    else break;   // no smaller: nothing more to gain
                }
            }
            finally { if (pass > 0) try { File.Delete(output); } catch { } }

            long actual = new FileInfo(destination).Length;
            if (actual <= plan.TargetBytes) break;
            double soundBytes = plan.AudioKbps * 1000.0 / 8 * plan.Seconds;
            double pictureNow = Math.Max(1, actual - soundBytes);
            double pictureRoom = plan.TargetBytes * 0.96 - soundBytes;
            if (pictureRoom <= 0 || kbps <= 40) break;
            int next = Math.Max(40, (int)(kbps * pictureRoom / pictureNow));
            if (next >= kbps) break;
            Log.Write($"Compress: {actual / 1048576.0:0.#} MB is over {plan.TargetBytes / 1048576.0:0.#} MB; picture {kbps} → {next} kb/s");
            kbps = next;
        }

        return File.Exists(destination) && new FileInfo(destination).Length > 1024;
    }

    public static async Task<double> DurationSecondsAsync(string path)
    {
        try
        {
            // ffprobe ships alongside ffmpeg, but read the duration from
            // ffmpeg itself so only one binary has to be present.
            var text = await FFmpegRun.InfoAsync(path);
            if (text == null) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"Duration:\s*(\d+):(\d\d):(\d\d\.\d+)");
            if (!m.Success) return 0;

            return int.Parse(m.Groups[1].Value) * 3600
                 + int.Parse(m.Groups[2].Value) * 60
                 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { Log.Error("duration", ex); return 0; }
    }

    /// <summary>How many sound tracks a file has (1 if it couldn't be read).</summary>
    public static async Task<int> AudioStreamCountAsync(string path)
    {
        var text = await FFmpegRun.InfoAsync(path);
        return text == null ? 1 : System.Text.RegularExpressions.Regex.Matches(text, @"Stream #0:\d+[^\n]*: Audio:").Count;
    }
}
