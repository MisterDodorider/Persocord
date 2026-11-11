using System;
using System.Collections.Generic;
using System.Linq;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// What the replay costs the PC with a given set of choices: video memory,
/// the graphics card's video encoder, CPU, memory and disk.
///
/// Estimates, worked out the way a run picks its encoder and route, so every
/// setting moves the figures it really moves (resolution: video memory, the
/// encoder and disk; bitrate: only disk; app tracks: CPU, memory and disk).
/// Video memory is scaled by what this PC was measured using; the rest are
/// typical figures, and the Settings page shows the real ones while it runs.
/// </summary>
public static class ReplayCost
{
    /// <summary>The choices that change what the replay costs.</summary>
    public record Input(
        DxgiTarget? Screen, int Height, int Fps, int Seconds,
        VideoCodec Codec, EncoderKind Chosen, IReadOnlyCollection<EncoderKind> Have,
        bool FixedRate, int BitrateKbps, int Quality,
        bool Pc, bool Mic, bool AppTracks, int MaxApps, int Denoise, bool Light)
    {
        public static Input From(AppSettings s, DxgiTarget? screen, IReadOnlyCollection<EncoderKind> have) => new(
            screen, s.ReplayHeight, s.ReplayFps, s.ReplaySeconds, s.ReplayCodec, s.ReplayEncoder, have,
            s.ReplayUseBitrate, s.ReplayBitrateKbps, s.ReplayQuality,
            s.ReplayTrackPc, s.ReplayTrackMic, s.AppTracks, s.MaxAppTracks, s.MicSuppression, s.ReplayLight);
    }

    public record Result(
        int VramMb, double CpuPercent, double EncoderPercent, int RamMb,
        double VideoKbps, double SoundKbps, int LiveTracks,
        double LoopMb, double ReplayMb, double ClipMbPerMinute,
        EncoderKind Encoder, bool Copied, bool CpuEncoder, int OutHeight);

    /// <summary>Replay sound streams are encoded at this rate each.</summary>
    public const int TrackKbps = 160;

    /// <summary>Typical number of apps making sound at once, for the estimate.</summary>
    public const int TypicalApps = 2;

    public static Result Estimate(Input i)
    {
        int threads = Math.Max(1, Environment.ProcessorCount);
        int w = i.Screen?.Desktop.Width ?? 1920, h = i.Screen?.Desktop.Height ?? 1080;
        var g = ReplayService.GuessVideoMemory(i.Screen, i.Height, i.Codec, i.Chosen, i.Have);
        int outH = Math.Max(2, g.OutH);
        double screenMp = w * (double)h / 1e6;
        double outMp = screenMp * (outH / (double)h) * (outH / (double)h);
        int fps = Math.Clamp(i.Fps, 1, 240);
        bool x264 = g.Encoder == EncoderKind.X264;
        bool copied = g.Copied || x264;
        bool scaled = !g.FullSize && !copied;

        // ---- sound: every track is captured and encoded all the time ----
        bool apps = i.AppTracks && i.Pc && !i.Light;
        int appSlots = apps ? Math.Clamp(i.MaxApps, 1, 10) + 1 : 0;      // + "Other apps"
        int tracks = (i.Pc ? 1 : 0) + (i.Mic ? 1 : 0) + appSlots;
        int streams = tracks + (tracks >= 2 ? 1 : 0);                      // + the mix
        bool denoise = i.Mic && i.Denoise > 0;

        // ---- CPU, in % of one thread, then of the whole CPU ----
        double core = 0.5;                                                 // Persocord itself
        core += 1.5 + 0.03 * fps;                                          // the picture's FFmpeg
        if (scaled) core += outMp * fps * 0.06;                            // small frames brought over
        if (copied) core += screenMp * fps * 0.18 + outMp * fps * 0.08;   // whole frames through memory
        if (x264) core += outMp * fps * 1.6;                               // encoded on the CPU
        if (g.Encoder == EncoderKind.Qsv) core += 2;
        core += tracks * 0.4;                                              // sound capture
        core += streams * (i.Light ? 0.8 : 2.0);                           // sound encoding
        if (tracks >= 2) core += 0.5;                                      // mixing
        if (denoise) core += 4;                                            // AI noise suppression
        double cpu = Math.Min(100, core / threads);

        // ---- the graphics card's video encoder (not the part games use) ----
        double codecF = i.Codec switch { VideoCodec.Hevc => 1.25, VideoCodec.Av1 => 1.15, _ => 1.0 };
        double chipF = g.Encoder switch { EncoderKind.Qsv => 1.6, EncoderKind.Amf => 1.4, _ => 1.0 };
        double enc = x264 ? 0 : Math.Min(100, outMp * fps * 0.16 * codecF * chipF);

        // ---- memory (the FFmpegs, MB) ----
        double ram = 45 + 25;                                              // both FFmpegs' base
        if (scaled) ram += outMp * 1.5 * 8;
        if (copied) ram += screenMp * 4 * 6 + outMp * 1.5 * 6;
        if (x264) ram += 60 + outMp * 1.5 * 45;
        ram += streams * 6 + tracks * 3 + (denoise ? 8 : 0);

        // ---- disk ----
        double video = VideoKbps(outH, fps, i.FixedRate, i.BitrateKbps, i.Quality, i.Codec);
        double sound = streams * TrackKbps;
        // A saved clip keeps the tracks that had sound: the mix, PC, mic and the apps heard.
        int kept = (i.Pc ? 1 : 0) + (i.Mic ? 1 : 0) + (apps ? Math.Min(TypicalApps, appSlots) : 0);
        double savedSound = (kept + (kept >= 2 ? 1 : 0)) * TrackKbps;
        int seconds = Math.Max(1, i.Seconds);

        return new Result(
            g.Mb, cpu, enc, (int)Math.Round(ram / 5) * 5,
            video, sound, tracks,
            (video + sound) * (seconds + 6) / 8 / 1000,
            (video + savedSound) * seconds / 8 / 1000,
            (video + savedSound) * 60 / 8 / 1000,
            g.Encoder, g.Copied, x264, outH);
    }

    /// <summary>
    /// The picture's bitrate. A fixed bitrate is what it says; a quality
    /// level follows the action, so this is a typical game: level 21 in
    /// H.264 lands near these rates, HEVC and AV1 need less for the same look.
    /// </summary>
    public static double VideoKbps(int height, int fps, bool fixedRate, int bitrateKbps, int quality, VideoCodec codec)
    {
        if (fixedRate && bitrateKbps > 0) return bitrateKbps;
        if (quality <= 0) quality = 21;
        double codecFactor = codec switch { VideoCodec.Hevc => 0.7, VideoCodec.Av1 => 0.6, _ => 1.0 };
        return height * (16.0 / 9) * height * fps * 0.07 / 1000 * Math.Pow(1.12, 21 - quality) * codecFactor;
    }

    /* ---- wording ------------------------------------------------------------ */

    public static string Cpu(double pct) => pct < 0.95 ? "<1%" : pct < 10 ? $"{pct:0.#}%" : $"{pct:0}%";

    public static string Mb(double mb) => mb >= 1000 ? $"{mb / 1000:0.0} GB" : $"{mb:0} MB";

    /// <summary>
    /// How an option differs from what's picked now, naming only what it
    /// changes ("+120 MB video memory · +4% encoder"), or "" when it's the same.
    /// </summary>
    public static string Delta(Result now, Result other, int most = 3)
    {
        var parts = new List<(double Weight, string Text)>();
        void Add(double d, double min, double weight, Func<double, string> say)
        {
            if (Math.Abs(d) >= min) parts.Add((Math.Abs(d) * weight, (d > 0 ? "+" : "−") + say(Math.Abs(d))));
        }
        Add(other.VramMb - now.VramMb, 10, 1, d => $"{d:0} MB VRAM");
        Add(other.CpuPercent - now.CpuPercent, 0.3, 40, d => $"{(d < 10 ? d.ToString("0.#") : d.ToString("0"))}% CPU");
        Add(other.EncoderPercent - now.EncoderPercent, 1, 6, d => $"{d:0}% encoder");
        Add(other.RamMb - now.RamMb, 10, 0.8, d => $"{d:0} MB RAM");
        Add(other.ReplayMb - now.ReplayMb, Math.Max(2, now.ReplayMb * 0.03), 0.5, d => $"{Mb(d)} a replay");
        return string.Join(" · ", parts.OrderByDescending(p => p.Weight).Take(most).Select(p => p.Text));
    }

    /// <summary>One line for the whole thing.</summary>
    public static string Summary(Result r) =>
        $"VRAM ≈ {r.VramMb} MB · CPU ≈ {Cpu(r.CpuPercent)} · " +
        (r.CpuEncoder ? "" : $"encoder ≈ {r.EncoderPercent:0}% · ") +
        $"RAM ≈ {r.RamMb} MB · each replay ≈ {Mb(r.ReplayMb)}";
}
