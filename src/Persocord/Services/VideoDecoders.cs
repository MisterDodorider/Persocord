using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// Which AV1 decoder FFmpeg should use when it jumps into the middle of a clip.
///
/// AV1 saved as MP4 by a graphics card keeps the picture's settings (its
/// "sequence header") once, at the front of the file, not with every
/// keyframe. FFmpeg's builds for Windows often pick libaom to decode AV1, and
/// libaom only reads those settings when they come with the frames: jumping
/// into such a clip (to save an edit of an edit, say) found "no sequence
/// header" and nothing could be decoded. FFmpeg's own AV1 decoder (on the
/// graphics card) and dav1d read them from the front of the file.
///
/// So the first time an AV1 clip is worked on, each is tried on that very clip,
/// a few frames from its middle, and the first that decodes is used from then on.
/// </summary>
public static class VideoDecoders
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _decided, _hw, _dav1d;

    /// <summary>
    /// Input options for decoding a file of this codec. <paramref name="cudaPath"/>:
    /// the command sets its own graphics card decoding (NVIDIA's), so only the
    /// decoder is named.
    /// </summary>
    public static string ArgsFor(string? codec, bool cudaPath = false)
    {
        if (!_decided || !string.Equals(codec, "av1", StringComparison.OrdinalIgnoreCase)) return "";
        if (_hw) return cudaPath ? "-c:v av1 " : "-hwaccel auto -c:v av1 ";
        if (_dav1d) return "-c:v libdav1d ";
        return "";
    }

    /// <summary>Decides, once, using a real AV1 clip (does nothing for other kinds of clip).</summary>
    public static async Task PrepareAsync(string file, MediaInfo? info)
    {
        if (_decided || info == null || !string.Equals(info.VideoCodec, "av1", StringComparison.OrdinalIgnoreCase)) return;
        await Gate.WaitAsync();
        try
        {
            if (_decided) return;
            string at = (info.Duration > 6 ? info.Duration / 2 : 0).ToString("0.###", CultureInfo.InvariantCulture);
            string Test(string dec) => $"-xerror {dec}-ss {at} -i \"{file}\" -map 0:v:0 -frames:v 3 -f null -";
            var (hw, whyHw) = await FFmpegProbe.TryAsync(Test("-hwaccel auto -c:v av1 "));
            bool dav1d = false;
            string whyDav1d = "";
            if (!hw) (dav1d, whyDav1d) = await FFmpegProbe.TryAsync(Test("-c:v libdav1d "));
            _hw = hw;
            _dav1d = dav1d;
            _decided = true;
            Log.Write("Editor: AV1 decoding " + (hw ? "on the graphics card (FFmpeg's own decoder)"
                : dav1d ? "with dav1d (the graphics card's didn't work: " + whyHw + ")"
                : "left to FFmpeg (graphics card: " + whyHw + "; dav1d: " + whyDav1d + ")"));
        }
        catch (Exception ex) { Log.Error("AV1 decoder check", ex); }
        finally { Gate.Release(); }
    }
}
