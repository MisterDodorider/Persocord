using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// The one way Persocord runs FFmpeg for a job it waits on: started with no
/// window, everything it says read as it goes (so it can never stall on a full
/// pipe), and stopped if it runs past its time. (Each part of Persocord used
/// to have a copy of this of its own.)
/// </summary>
public static class FFmpegRun
{
    /// <summary>
    /// Runs FFmpeg and waits for it: its exit code and everything it printed
    /// (its errors, or, for "-i file", what it found in the file). Null when it
    /// couldn't start or ran past <paramref name="timeoutMs"/> (then it's stopped).
    /// </summary>
    public static async Task<(int Exit, string Text)?> ExecAsync(string args, int timeoutMs,
        ProcessPriorityClass? priority = null, string? workDir = null)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = Paths.FFmpeg,
            Arguments = args,
            WorkingDirectory = workDir ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });
        if (p == null) return null;
        if (priority is { } pr) try { p.PriorityClass = pr; } catch { }
        var err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        using var limit = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            // Stopped, and given a moment to let go of its files (what it was
            // working on may be moved or deleted straight after).
            try { p.Kill(true); p.WaitForExit(1000); } catch { }
            return null;
        }
        return (p.ExitCode, await err);
    }

    /// <summary>Runs FFmpeg; true if it finished cleanly. When it doesn't, what it said goes in the log after <paramref name="label"/>.</summary>
    public static async Task<bool> RunAsync(string args, int timeoutMs, ProcessPriorityClass? priority = null,
        string? workDir = null, string label = "ffmpeg")
    {
        try
        {
            var r = await ExecAsync(args, timeoutMs, priority, workDir);
            if (r is not { } done) return false;
            if (done.Exit != 0) Log.Write($"{label}: " + done.Text.Trim());
            return done.Exit == 0;
        }
        catch (Exception ex) { Log.Error(label, ex); return false; }
    }

    /// <summary>
    /// What FFmpeg finds in a file (length, size, streams and their names), as
    /// it prints it. Null if it couldn't be read; a file that never answers (a
    /// network drive gone to sleep) is given up on after 30 seconds.
    /// </summary>
    public static async Task<string?> InfoAsync(string path)
    {
        try { return (await ExecAsync($"-hide_banner -i \"{path}\"", 30000))?.Text; }
        catch (Exception ex) { Log.Error("ffmpeg probe", ex); return null; }
    }
}

/// <summary>Runs FFmpeg for a moment, to see whether something works on this PC.</summary>
public static class FFmpegProbe
{
    /// <summary>Whether it finished cleanly, and its first error line if not.</summary>
    public static async Task<(bool Ok, string Why)> TryAsync(string args)
    {
        try
        {
            var r = await FFmpegRun.ExecAsync("-hide_banner -loglevel error " + args, 12000);
            if (r == null) return (false, "timed out");
            var (exit, text) = r.Value;
            if (exit == 0) return (true, "");
            var first = text.Trim().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? $"exit {exit}";
            return (false, first.Length <= 160 ? first : first[..160] + " …");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
