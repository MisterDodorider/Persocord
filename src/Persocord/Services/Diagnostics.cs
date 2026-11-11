using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// One report with everything needed to see what's going on: what this PC
/// has (FFmpeg, encoders, screens, graphics cards, sound devices), whether
/// the things Persocord relies on work right now (app-sound capture, the
/// player's engine), the settings that matter, and the log lines worth
/// reading. Built to be pasted straight into a chat.
/// </summary>
public static class Diagnostics
{
    public static async Task<string> BuildAsync(AppController app)
    {
        var sb = new StringBuilder();
        void H(string title) => sb.AppendLine().AppendLine($"== {title} ==");
        void L(string line) => sb.AppendLine(line);
        async Task Try(string what, Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex) { L($"{what}: couldn't check ({ex.Message})"); }
        }

        L($"Persocord diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"Persocord {Paths.AppVersion}, .NET {Environment.Version}");
        L($"Windows: {WindowsVersion()}");

        H("Persocord right now");
        await Try("Health", () => { L(Health.Snapshot()); return Task.CompletedTask; });
        if (app.Refresh is { } refresh) L("Fresh start: " + refresh.Describe());
        L($"Updates: {Updates.Status}" + (Updates.Available is { } up ? $"; {up.Label} is out" : "") +
          $"; Update {(app.Settings.Current.UpdateInstallsItself ? "installs it" : "opens the page")}");

        H("FFmpeg");
        await Try("FFmpeg", async () =>
        {
            L($"Path: {Paths.FFmpeg}");
            L($"Version: {await FirstLineAsync(Paths.FFmpeg, "-hide_banner -version")}");
        });

        H("Encoders");
        await Try("Encoders", async () =>
        {
            foreach (var c in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
            {
                var have = await EncoderDetector.AvailableForAsync(c);
                L($"{EncoderDetector.CodecName(c)}: {(have.Count == 0 ? "none" : string.Join(", ", have.Select(k => k == EncoderKind.X264 ? $"CPU ({EncoderDetector.FFmpegName(k, c)})" : EncoderDetector.Friendly(k))))}");
            }
            List<KeyValuePair<string, string>> fails;
            lock (EncoderDetector.Failures) fails = EncoderDetector.Failures.Where(f => !f.Key.StartsWith("lib")).ToList();
            foreach (var f in fails) L($"  {f.Key} failed: {Short(f.Value, 220)}");
        });

        H("Screens and graphics cards");
        await Try("Screens", () =>
        {
            foreach (var t in DxgiOutputs.All())
                L($"{t.DeviceName}: {t.Desktop.Width}x{t.Desktop.Height} at {t.Desktop.X},{t.Desktop.Y} — card {t.Adapter} " +
                  $"({Vendor(t.VendorId)}, {t.VideoMemoryBytes / 1073741824.0:0.0} GB)" +
                  (CaptureChip.Learned(t.DeviceName) is { } chip ? $", captured through {CaptureChip.Name(chip)}" : ""));
            L("Graphics chips: " + string.Join(", ", DxgiOutputs.AdapterVendors().Select(CaptureChip.Name)));
            var used = ResourceGuard.CardMemoryUsedMb();
            if (used > 0) L($"Video memory in use now (busiest card): {used / 1024:0.0} GB");
            return Task.CompletedTask;
        });

        H("Sound");
        await Try("Sound devices", () =>
        {
            using var en = new MMDeviceEnumerator();
            foreach (var (flow, label) in new[] { (DataFlow.Render, "Output"), (DataFlow.Capture, "Input") })
            {
                string? def = null;
                try { using var d = en.GetDefaultAudioEndpoint(flow, Role.Multimedia); def = d.ID; } catch { }
                foreach (var dev in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    string fmt = "";
                    try { var f = dev.AudioClient.MixFormat; fmt = $" — {f.SampleRate} Hz, {f.Channels} ch"; } catch { }
                    L($"{label}: {dev.FriendlyName}{(dev.ID == def ? " (default)" : "")}{fmt}");
                    dev.Dispose();
                }
            }
            return Task.CompletedTask;
        });
        await Try("App sound capture", async () =>
        {
            // The per-app capture every separate track relies on.
            var cap = await Task.Run(() => ProcessLoopbackCapture.ExcludingAsync(Environment.ProcessId, TimeSpan.FromSeconds(3)));
            cap.Dispose();
            L("App-by-app sound capture: works");
        });

        H("Player");
        L(await Views.PlayerView.WarmUpAsync() ? "VLC engine: loaded" : "VLC engine: missing (clips open in Windows' player)");

        H("Replay");
        var s = app.Settings.Current;
        L($"Running: {(app.Replay.IsRunning ? "yes" : app.ReplayWaiting ? "waiting for a game" : "no")}" +
          (s.ReplayGamesOnly ? $" (only while a game runs; {s.MyGames.Count} added by hand)" : "") +
          (app.Replay.CaptureVendor is { } cv ? $", capturing through the {CaptureChip.Name(cv)} chip" : ""));
        L($"Length {s.ReplaySeconds}s, {EncoderDetector.CodecName(s.ReplayCodec)}, encoder {s.ReplayEncoder}, " +
          $"{(s.ReplayHeight == 0 ? "screen size" : s.ReplayHeight + "p")} @ {s.ReplayFps} fps, " +
          (s.ReplayUseBitrate ? $"{s.ReplayBitrateKbps / 1000.0:0.#} Mbps" : $"quality {s.ReplayQuality}"));
        L($"Sound: PC {(s.ReplayTrackPc ? "on" : "off")}, mic {(s.ReplayTrackMic ? "on" : "off")}, " +
          $"app tracks {(s.AppTracks ? $"up to {s.MaxAppTracks}" : "off")}, sync {s.ReplaySoundSyncMs} ms");
        L($"Administrator apps: {(s.ElevatedAppTracks ? "own tracks through the helper" : "All PC audio only")}" +
          $"{(AppAudio.SelfElevated ? " (Persocord itself runs as administrator: not needed)" : "")}, " +
          $"helper {(ElevatedAudio.Running ? "running" : ElevatedAudio.Declined ? "declined this session" : "not started")}");
        L($"Game now: {app.Games.Current?.Name ?? "none detected"}");
        L($"State: {app.Replay.StateReport()}");
        L($"FFmpeg on the screen's chip (power saving): {(s.CaptureOnScreenGpu ? "on" : "off")}; " +
          $"Persocord runs as administrator: {(AppAudio.SelfElevated ? "yes" : "no")}");
        L($"Windows graphics preference: FFmpeg {GpuPreference(Paths.FFmpeg)}, Persocord {GpuPreference(Environment.ProcessPath ?? "")}");
        if (app.Replay.LastVideoArgs is { } va) L("Last picture command: ffmpeg " + Short(va, 900));

        H("Capture test (runs FFmpeg briefly on each chip and route)");
        await Try("Capture test", () => CaptureTestAsync(s, L));

        H("Recording and limits");
        var rec = s.ForRecording();
        L($"Recordings: {EncoderDetector.CodecName(rec.RecordCodec)}, encoder {rec.Encoder}, {rec.Fps} fps, {rec.Resolution}" +
          (s.RecordLikeReplay ? " (same as the replay)" : ""));
        L($"CPU: {(s.SafetyCpuHardCap ? $"strict cap {s.SafetyCpuPercent}%" : "soft")}{(s.ReplayLight ? ", lighter replay" : "")}, {Environment.ProcessorCount} threads, RAM cap {s.SafetyRamMb} MB, " +
          $"video memory {s.SafetyVramMb} MB, step back at {s.SafetyVramFullPercent}% card memory: {(s.SafetyGpuAdaptive ? "on" : "off")}");

        H("Clips");
        await Try("Clips", () =>
        {
            var disk = app.Library.Disk();
            L($"Folder: {app.Library.Folder}");
            L($"{app.Library.Items.Count} clips, {disk.Used / 1073741824.0:0.00} GB used");
            return Task.CompletedTask;
        });

        H("Windows' crash reports for Persocord (last 7 days)");
        var crashes = await CrashWatch.ReportsAsync(DateTime.UtcNow.AddDays(-7));
        if (crashes.Count == 0) L("None.");
        foreach (var c in crashes.Take(5)) L(c);

        H("Recent log lines that matter");
        foreach (var line in RelevantLog(140)) L(line);

        return sb.ToString();
    }

    /// <summary>Builds the report, saves it as a file and copies it. Returns the file path.</summary>
    public static async Task<string> SaveAndCopyAsync(AppController app)
    {
        var text = await BuildAsync(app);
        var dir = Path.Combine(Paths.Root, "diagnostics");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, text);
        await ShareService.CopyTextAsync(text);
        Log.Write("Diagnostics report saved: " + path);
        return path;
    }

    /* ---- capture test ---------------------------------------------------------- */

    /// <summary>
    /// Tries, for a few frames each, what the replay is made of: grabbing the
    /// screen through every graphics chip, resizing on the card, and handing
    /// frames to each encoder by each route. Shows which part fails, and how.
    /// </summary>
    private static async Task CaptureTestAsync(AppSettings s, Action<string> L)
    {
        var all = DxgiOutputs.All();
        var screen = all.FirstOrDefault(t => t.Desktop.X == 0 && t.Desktop.Y == 0) ?? all.FirstOrDefault();
        if (screen == null) { L("No screen found through DirectX."); return; }
        int outH = Math.Min(720, screen.Desktop.Height) & ~1;
        int outW = (int)Math.Round(screen.Desktop.Width * (double)outH / screen.Desktop.Height) & ~1;
        string grab = $"ddagrab=output_idx={screen.Output}:framerate=30";

        uint? works = null;
        var learned = CaptureChip.Learned(screen.DeviceName);
        foreach (var chip in CaptureChip.Candidates(screen.VendorId))
        {
            string dev = ReplayService.DdaDevice(chip == screen.VendorId ? screen.Adapter : 0, chip);
            var (ok, why) = await TryFFmpegAsync($"{dev}-filter_complex \"{grab},hwdownload,format=bgra\" -frames:v 5 -f null -");
            L($"Screen through the {CaptureChip.Name(chip)} chip: {(ok ? "works" : "fails — " + why)}" +
              (learned == chip ? " (remembered)" : chip == screen.VendorId && learned == null ? " (Persocord's view)" : ""));
            if (ok && (works == null || learned == chip)) works = chip;
        }
        if (works is not { } use) { L("No chip could grab the screen, so the routes weren't tested."); return; }

        string device = ReplayService.DdaDevice(use == screen.VendorId ? screen.Adapter : 0, use);
        var (scaleOk, scaleWhy) = await TryFFmpegAsync(
            $"{device}-filter_complex \"{grab},scale_d3d11=width={outW}:height={outH}:format=nv12,hwdownload,format=nv12\" -frames:v 5 -f null -");
        L($"Resizing on the {CaptureChip.Name(use)} chip: {(scaleOk ? "works" : "fails — " + scaleWhy)}");

        var have = await EncoderDetector.AvailableForAsync(s.ReplayCodec);
        foreach (var enc in have.Where(k => k != EncoderKind.X264))
        {
            string name = EncoderDetector.FFmpegName(enc, s.ReplayCodec);
            var routes = new (string Name, string Chain)[]
            {
                // With the colour fix-up the real replay does after resizing (NVIDIA's
                // AV1 encoder refuses the card's frames without it, so testing
                // without it reported a failure the replay never has).
                ("resized on the card", $"{grab},scale_d3d11=width={outW}:height={outH}:format=nv12,hwdownload,format=nv12," +
                                        "setparams=range=pc,scale=in_range=pc:out_range=tv,format=nv12," +
                                        "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709"),
                ("full size on the card", grab + ReplayService.QsvMap(enc)),
                ("through memory", $"{grab},hwdownload,format=bgra,scale={outW}:{outH},format=nv12"),
                // The planar layout area recordings used before 1.5.50 (RTX 50 cards refuse it).
                ("through memory, planar", $"{grab},hwdownload,format=bgra,scale={outW}:{outH},format=yuv420p"),
            };
            var results = new List<string>();
            foreach (var (routeName, chain) in routes)
            {
                // Colour stated as the replay states it.
                var (ok, why) = await TryFFmpegAsync($"{device}-filter_complex \"{chain}\" -c:v {name} -b:v 4M " +
                    "-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 -frames:v 20 -f null -");
                results.Add($"{routeName} {(ok ? "works" : "fails — " + why)}");
            }
            L($"{EncoderDetector.Friendly(enc)} ({name}): " + string.Join("; ", results));
        }
    }

    /// <summary>Runs FFmpeg for a moment: whether it finished cleanly, and its first error line if not.</summary>
    private static Task<(bool Ok, string Why)> TryFFmpegAsync(string args) => FFmpegProbe.TryAsync(args);

    /// <summary>Windows' per-app graphics choice (Settings → Display → Graphics) for a program.</summary>
    private static string GpuPreference(string exe)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            var v = k?.GetValue(exe) as string;
            if (string.IsNullOrEmpty(v)) return "Windows decides";
            return v.Contains("GpuPreference=1") ? "power saving" : v.Contains("GpuPreference=2") ? "high performance"
                 : v.Contains("GpuPreference=0") ? "Windows decides" : v;
        }
        catch { return "unknown"; }
    }

    /* ---- helpers --------------------------------------------------------------- */

    /// <summary>
    /// The log lines worth reading: replay starts, saves, health and checks,
    /// encoder results, failures and errors. Long FFmpeg command lines and
    /// stack traces are shortened; the rest is left out.
    /// </summary>
    private static IEnumerable<string> RelevantLog(int max)
    {
        string[] lines;
        try
        {
            using var fs = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            lines = r.ReadToEnd().Split('\n');
        }
        catch { return new[] { "(the log couldn't be read)" }; }

        string[] keys =
        {
            "Replay on", "Replay save", "Replay health", "Saved replay check", "Replay stopped", "Replay video stopped", "Replay sound stopped", "Replay:", "Editor sound", "Player: ", "Sound track for", "No sound has come through", "joins Other apps", "runs as administrator", "Administrator sound helper", "through the administrator helper", "administrator sound",
            "Replay video started", "Replay trimmed", "Encoders", "Encoder test failed", "ERROR", "FATAL", "Refresh:", "Game detected", "Game closed", "Game added", "failed", "closed unexpectedly", "Windows crash report", "Idle:", "Memory:", "Compress", "shrink",
            "Limits for FFmpeg", "Video memory", "No sound has come through", "Sound is waiting", "started (", "exited",
            "Health: ", "timeline render", "Replace:", "Edit saved", "save edit", "keep edit", "open saved edit", "Editor:", "Recording:", "Recording sound", "Audiodg", "administrator sound", "ffmpeg (segment)", "no usable segments", "Capture failed", "GPU route", "Fast GPU capture", "First-frame report",
        };
        // An entry's own lines follow it without a time (FFmpeg's reasons,
        // say): they come along, a few at most, so failures show why.
        var picked = new List<string>();
        bool taking = false;
        int extra = 0;
        foreach (var raw in lines)
        {
            var l = raw.TrimEnd('\r');
            bool stamped = l.Length > 20 && char.IsDigit(l[0]);
            if (stamped)
            {
                taking = keys.Any(k => l.Contains(k, StringComparison.Ordinal));
                extra = 0;
                if (taking) picked.Add(Short(l, 400));
            }
            else if (taking && l.Trim().Length > 0 && extra++ < 8)
                picked.Add("      " + Short(l.Trim(), 300));
        }
        // The minute-by-minute health lines are alike: only the newest few
        // stay, so they never crowd out the lines that explain a problem.
        int health = 0, usage = 0;
        var kept = new List<string>();
        for (int i = picked.Count - 1; i >= 0; i--)
        {
            if (picked[i].Contains("Replay health", StringComparison.Ordinal) && ++health > 6) continue;
            // Persocord's own use over time (every 15 minutes): enough to see what grew.
            if (picked[i].Contains("Health: ", StringComparison.Ordinal) && ++usage > 16) continue;
            kept.Add(picked[i]);
        }
        kept.Reverse();
        return kept.TakeLast(max);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    private static string Vendor(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        _ => $"vendor 0x{id:X4}",
    };

    private static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var display = k?.GetValue("DisplayVersion") as string ?? "";
            var build = k?.GetValue("CurrentBuild") as string ?? "";
            var ubr = k?.GetValue("UBR")?.ToString() ?? "";
            // Windows 11 still reports "Windows 10" as its product name.
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {display} (build {build}.{ubr})";
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    private static async Task<string> FirstLineAsync(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return "(couldn't start)";
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text.Split('\n').FirstOrDefault()?.Trim() ?? "";
    }
}
