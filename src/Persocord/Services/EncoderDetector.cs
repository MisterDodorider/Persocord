using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// Works out which encoder this machine can actually use.
/// Listing an encoder is not proof it works, so each candidate gets a
/// one-frame trial encode. Results are cached next to the settings file.
/// </summary>
public static class EncoderDetector
{
    private static readonly string CacheFile =
        Path.Combine(Paths.Root, "encoders.txt");

    private static List<EncoderKind>? _cache;
    private static string? _cacheFor;

    /// <summary>
    /// Identifies the FFmpeg build a cached result belongs to. A result from a
    /// different FFmpeg, or from a time when FFmpeg was missing and every test
    /// failed, says nothing about what works now.
    /// </summary>
    /// <summary>
    /// What the cached test results depend on: FFmpeg itself, and the graphics
    /// drivers' encoder libraries, so updating a driver means a fresh test
    /// (a failure remembered from an old driver would otherwise stick forever).
    /// </summary>
    private static string Fingerprint()
    {
        try
        {
            var fi = new FileInfo(Paths.FFmpeg);
            if (!fi.Exists) return "missing";
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var parts = new List<string> { $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" };
            foreach (var dll in new[] { "nvEncodeAPI64.dll", "amfrt64.dll", "libmfxhw64.dll", "libvpl.dll" })
            {
                var d = new FileInfo(Path.Combine(sys, dll));
                parts.Add(d.Exists ? $"{d.Length}:{d.LastWriteTimeUtc.Ticks}" : "-");
            }
            return string.Join("|", parts);
        }
        catch { return "missing"; }
    }

    /// <summary>Why each encoder failed its test (FFmpeg's own words), for the log and tooltips.</summary>
    public static Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<List<EncoderKind>> AvailableAsync(bool forceRescan = false)
    {
        var fp = Fingerprint();
        if (!forceRescan && _cache != null && _cacheFor == fp) return _cache;

        // One check at a time: at startup the replay and the warm-up both asked
        // at once, and each ran every trial encode of its own.
        await _availLock.WaitAsync();
        try
        {
            if (!forceRescan && _cache != null && _cacheFor == fp) return _cache;
            return await ScanAsync(fp, forceRescan);
        }
        finally { _availLock.Release(); }
    }

    private static readonly SemaphoreSlim _availLock = new(1, 1);

    private static async Task<List<EncoderKind>> ScanAsync(string fp, bool forceRescan)
    {
        if (!forceRescan && File.Exists(CacheFile))
        {
            try
            {
                var lines = File.ReadAllLines(CacheFile);
                if (lines.Length > 1 && lines[0] == "ffmpeg " + fp)
                {
                    var saved = new List<EncoderKind>();
                    foreach (var line in lines[1..])
                        if (Enum.TryParse<EncoderKind>(line.Trim(), out var k)) saved.Add(k);
                    if (saved.Count > 0) { _cache = saved; _cacheFor = fp; return saved; }
                }
            }
            catch { /* rescan */ }
        }

        var found = new List<EncoderKind>();
        foreach (var (kind, name) in new[]
                 {
                     (EncoderKind.Nvenc, "h264_nvenc"),
                     (EncoderKind.Qsv,   "h264_qsv"),
                     (EncoderKind.Amf,   "h264_amf"),
                     (EncoderKind.X264,  "libx264"),
                 })
        {
            if (await TrialEncodeAsync(name)) found.Add(kind);
        }

        bool anyWorked = found.Count > 0;
        if (!anyWorked) found.Add(EncoderKind.X264); // assume and hope, but do not remember it

        _cache = found;
        _cacheFor = fp;

        // Only a real result is worth keeping. Caching "nothing works" from a
        // run where FFmpeg was absent would pin the app to the slow CPU
        // encoder long after FFmpeg turned up.
        if (anyWorked && fp != "missing")
        {
            try
            {
                var lines = new List<string> { "ffmpeg " + fp };
                lines.AddRange(found.ConvertAll(k => k.ToString()));
                File.WriteAllLines(CacheFile, lines);
            }
            catch (Exception ex) { Log.Error("encoder cache", ex); }
        }

        Log.Write("Encoders available: " + string.Join(", ", found));
        return found;
    }

    /// <summary>Best working encoder, honouring the user's choice when possible.</summary>
    public static async Task<EncoderKind> ResolveAsync(EncoderKind preferred)
    {
        var have = await AvailableAsync();
        if (preferred != EncoderKind.Auto && have.Contains(preferred)) return preferred;

        foreach (var k in new[] { EncoderKind.Nvenc, EncoderKind.Qsv, EncoderKind.Amf, EncoderKind.X264 })
            if (have.Contains(k)) return k;

        return EncoderKind.X264;
    }

    /* ---- per video format ------------------------------------------------ */

    private static Dictionary<VideoCodec, List<EncoderKind>>? _byCodec;
    private static readonly SemaphoreSlim _scanLock = new(1, 1);
    private static string CodecCacheFile => Path.Combine(Paths.Root, "encoders-by-format.txt");

    /// <summary>
    /// Which encoders work for one video format on this PC, each proven with
    /// a real test encode (cached until FFmpeg or the graphics driver changes).
    /// </summary>
    public static async Task<List<EncoderKind>> AvailableForAsync(VideoCodec codec, bool forceRescan = false)
    {
        await _scanLock.WaitAsync();
        try
        {
            var fp = Fingerprint();
            if (!forceRescan && _byCodec == null && File.Exists(CodecCacheFile))
            {
                try
                {
                    var lines = File.ReadAllLines(CodecCacheFile);
                    if (lines.Length > 1 && lines[0] == "ffmpeg " + fp)
                    {
                        var map = new Dictionary<VideoCodec, List<EncoderKind>>();
                        foreach (var line in lines[1..])
                        {
                            var parts = line.Split('=');
                            if (parts.Length == 2 && Enum.TryParse<VideoCodec>(parts[0], out var c))
                                map[c] = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(x => Enum.TryParse<EncoderKind>(x, out var k) ? k : EncoderKind.Auto)
                                    .Where(k => k != EncoderKind.Auto).ToList();
                        }
                        if (map.Count == 3) _byCodec = map;
                    }
                }
                catch { }
            }

            if (_byCodec == null || forceRescan)
            {
                var map = new Dictionary<VideoCodec, List<EncoderKind>>();
                foreach (var c in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
                {
                    var list = new List<EncoderKind>();
                    foreach (var k in new[] { EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv, EncoderKind.X264 })
                        if (await TrialEncodeAsync(FFmpegName(k, c))) list.Add(k);
                    map[c] = list;
                }
                _byCodec = map;
                Log.Write("Encoders by format: " + string.Join("; ",
                    map.Select(kv => $"{kv.Key}: {(kv.Value.Count == 0 ? "none" : string.Join(", ", kv.Value))}")));
                try
                {
                    File.WriteAllLines(CodecCacheFile, new[] { "ffmpeg " + fp }
                        .Concat(map.Select(kv => $"{kv.Key}={string.Join(",", kv.Value)}")));
                }
                catch (Exception ex) { Log.Error("encoder cache", ex); }
            }
            return _byCodec.TryGetValue(codec, out var have) ? have : new List<EncoderKind>();
        }
        finally { _scanLock.Release(); }
    }

    /// <summary>
    /// The chosen encoder if it works for this format, else the best that
    /// does. When nothing on this PC can make the format at all, falls back
    /// to H.264 (which always works) rather than an encoder that isn't there.
    /// </summary>
    public static async Task<(EncoderKind Kind, VideoCodec Codec)> ResolveAsync(EncoderKind preferred, VideoCodec codec)
    {
        var have = await AvailableForAsync(codec);
        if (preferred != EncoderKind.Auto && have.Contains(preferred)) return (preferred, codec);
        foreach (var k in new[] { EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv, EncoderKind.X264 })
            if (have.Contains(k)) return (k, codec);

        if (codec != VideoCodec.H264)
        {
            Log.Write($"No encoder on this PC can make {CodecName(codec)}; using H.264 instead");
            return await ResolveAsync(preferred, VideoCodec.H264);
        }
        return (EncoderKind.X264, VideoCodec.H264);
    }

    public static string FFmpegName(EncoderKind k, VideoCodec c) => (k, c) switch
    {
        (EncoderKind.Nvenc, VideoCodec.Hevc) => "hevc_nvenc",
        (EncoderKind.Nvenc, VideoCodec.Av1)  => "av1_nvenc",
        (EncoderKind.Amf,   VideoCodec.Hevc) => "hevc_amf",
        (EncoderKind.Amf,   VideoCodec.Av1)  => "av1_amf",
        (EncoderKind.Qsv,   VideoCodec.Hevc) => "hevc_qsv",
        (EncoderKind.Qsv,   VideoCodec.Av1)  => "av1_qsv",
        (EncoderKind.X264,  VideoCodec.Hevc) => "libx265",
        (EncoderKind.X264,  VideoCodec.Av1)  => "libsvtav1",
        _ => FFmpegName(k),
    };

    public static string CodecName(VideoCodec c) => c switch
    {
        VideoCodec.Hevc => "HEVC (H.265)",
        VideoCodec.Av1 => "AV1",
        _ => "H.264",
    };

    public static string FFmpegName(EncoderKind k) => k switch
    {
        EncoderKind.Nvenc => "h264_nvenc",
        EncoderKind.Qsv   => "h264_qsv",
        EncoderKind.Amf   => "h264_amf",
        _                 => "libx264",
    };

    public static string Friendly(EncoderKind k) => k switch
    {
        EncoderKind.Nvenc => "NVIDIA NVENC",
        EncoderKind.Qsv   => "Intel Quick Sync",
        EncoderKind.Amf   => "AMD AMF",
        EncoderKind.X264  => "CPU (x264)",
        _                 => "Automatic",
    };

    private static void Fail(string encoder, string why)
    {
        lock (Failures) Failures[encoder] = why;
        if (!encoder.StartsWith("lib")) Log.Write($"Encoder test failed for {encoder}: {why}");
    }

    private static async Task<bool> TrialEncodeAsync(string encoder)
    {
        try
        {
            var r = await FFmpegRun.ExecAsync(
                $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=320x240:d=0.1 -c:v {encoder} -frames:v 1 -f null -", 15000);
            if (r is not { } done)
            {
                Fail(encoder, "the test didn't finish within 15 seconds");
                return false;
            }
            if (done.Exit == 0) { lock (Failures) Failures.Remove(encoder); return true; }

            // Keep FFmpeg's reason: it's the only way to tell a missing
            // driver feature from an old driver or a card without the encoder.
            var why = string.Join(" ", done.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                              .Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));
            Fail(encoder, why.Length > 0 ? why : $"exit code {done.Exit}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"trial encode {encoder}", ex);
            return false;
        }
    }

    private static bool? _dda;
    private static string? _ddaFor;
    private static string DdaFile => Path.Combine(Paths.Root, "capture.cache");

    /// <summary>
    /// The saved answer to "does fast GPU capture work here", if there is one
    /// for this FFmpeg build. Never runs FFmpeg, so it is safe on the hot path.
    /// </summary>
    public static bool? KnownDdagrab()
    {
        var fp = Fingerprint();
        if (_dda.HasValue && _ddaFor == fp) return _dda;
        try
        {
            if (File.Exists(DdaFile))
            {
                var lines = File.ReadAllLines(DdaFile);
                if (lines.Length == 2 && lines[0] == "ffmpeg2 " + fp && bool.TryParse(lines[1], out var ok))
                {
                    _dda = ok;
                    _ddaFor = fp;
                    return ok;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Remembers the answer across launches.</summary>
    public static void RecordDdagrab(bool ok)
    {
        var fp = Fingerprint();
        _dda = ok;
        _ddaFor = fp;
        if (fp == "missing") return;
        try { File.WriteAllLines(DdaFile, new[] { "ffmpeg2 " + fp, ok.ToString() }); }
        catch (Exception ex) { Log.Error("capture cache", ex); }
    }

    /// <summary>
    /// Tests fast GPU capture, once per FFmpeg build ever. It used to run on
    /// every launch, and with Persocord opening straight into the area
    /// picker, the first recording waited on it.
    /// </summary>
    public static async Task<bool> DdagrabAsync()
    {
        var known = KnownDdagrab();
        if (known.HasValue) return known.Value;

        var ok = await TryDdagrabAsync();
        RecordDdagrab(ok);
        Log.Write("Fast GPU capture " + (ok ? "available" : "unavailable, using GDI"));
        return ok;
    }

    /// <summary>Runs the one-time checks in the background so the first recording does not wait.</summary>
    public static Task WarmAsync() => Task.WhenAll(AvailableAsync(), DdagrabAsync());

    private static async Task<bool> TryDdagrabAsync()
    {
        try
        {
            // Test against a monitor that exists, on the chip it hangs off,
            // rather than assuming the first chip has one. On laptops with two
            // chips FFmpeg can see the screen on the other chip, so each is tried
            // (the one that works is remembered for capturing that screen).
            var first = DxgiOutputs.All().Find(_ => true);
            if (first == null) return await TryDdagrabOnAsync("", 0);
            foreach (var chip in CaptureChip.Candidates(first.VendorId))
            {
                // Named exactly as the replay and recordings name it.
                string device = ReplayService.DdaDevice(chip == first.VendorId ? first.Adapter : 0, chip);
                if (await TryDdagrabOnAsync(device, first.Output))
                {
                    CaptureChip.Learn(first.DeviceName, chip, first.VendorId);
                    return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    private static async Task<bool> TryDdagrabOnAsync(string device, int output)
    {
        try
        {
            var r = await FFmpegRun.ExecAsync(
                $"-hide_banner -loglevel error {device}-filter_complex " +
                $"ddagrab=output_idx={output}:framerate=10,hwdownload,format=bgra -frames:v 2 -f null -", 8000);
            return r is { Exit: 0 };
        }
        catch { return false; }
    }
}
