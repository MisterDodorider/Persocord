using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// Notices when Persocord ended without closing (a crash the log couldn't
/// see: a native crash leaves no line behind), and finds what Windows
/// recorded about it: the faulting part and error code, or .NET's own
/// report. Both go in the log and the diagnostics report, so the next report
/// after a crash says why it happened. Reads Windows' event log only for
/// Persocord's own entries; nothing is sent anywhere.
/// </summary>
public static class CrashWatch
{
    private static string Marker => Path.Combine(Paths.Root, "running.txt");

    /// <summary>At start: checks how the last run ended, then marks this one as running.</summary>
    public static void Started()
    {
        try
        {
            if (File.Exists(Marker))
            {
                var parts = File.ReadAllText(Marker).Trim().Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && pid != Environment.ProcessId &&
                    DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since) &&
                    !StillRunning(pid))
                {
                    Log.Write($"Persocord closed unexpectedly last time (it had been running since {since.ToLocalTime():yyyy-MM-dd HH:mm:ss}). " +
                              "What Windows recorded follows, if anything.");
                    // A little after starting, out of the way.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15));
                        var found = await ReportsAsync(since);
                        if (found.Count == 0) Log.Write("Windows crash report: none found for Persocord (a forced close leaves none).");
                        foreach (var r in found) Log.Write("Windows crash report: " + r);
                    });
                }
            }
            File.WriteAllText(Marker, $"{Environment.ProcessId}|{DateTime.UtcNow:o}");
        }
        catch (Exception ex) { Log.Error("crash watch", ex); }
    }

    /// <summary>A clean exit: this run is no longer marked as running.</summary>
    public static void Exited()
    {
        try
        {
            // Only this run's mark (a restart's new copy may already have written its own).
            if (File.Exists(Marker) && File.ReadAllText(Marker).StartsWith(Environment.ProcessId + "|", StringComparison.Ordinal))
                File.Delete(Marker);
        }
        catch { }
    }

    private static bool StillRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals("Persocord", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// What Windows recorded about Persocord ending badly since a moment:
    /// one line per crash (newest first), from its Application Error and .NET
    /// Runtime entries.
    /// </summary>
    public static async Task<List<string>> ReportsAsync(DateTime sinceUtc)
    {
        var result = new List<string>();
        try
        {
            string query = "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime']) and " +
                           $"TimeCreated[@SystemTime>='{sinceUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.000Z}']]]";
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                Arguments = $"qe Application /q:\"{query}\" /f:text /rd:true /c:40",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (p == null) return result;
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(10000)) != read) { try { p.Kill(); } catch { } return result; }
            result.AddRange(Parse(await read));
        }
        catch (Exception ex) { Log.Error("windows crash reports", ex); }
        return result;
    }

    /// <summary>Picks Persocord's entries out of wevtutil's text, each as one line.</summary>
    internal static IEnumerable<string> Parse(string text)
    {
        var blocks = System.Text.RegularExpressions.Regex.Split(text, @"(?m)^Event\[\d+\]:\s*$");
        foreach (var block in blocks)
        {
            if (block.IndexOf("Persocord", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            string Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";
            int d = lines.FindIndex(l => l.StartsWith("Description:", StringComparison.OrdinalIgnoreCase));
            var body = d < 0 ? lines : lines.Skip(d + 1).ToList();
            var first = d >= 0 && lines[d].Length > "Description:".Length ? lines[d]["Description:".Length..].Trim() : "";
            var said = string.Join(" · ", new[] { first }.Concat(body).Where(l => l.Length > 0).Take(30));
            if (said.Length > 1800) said = said[..1800] + " …";
            yield return $"{Field("Date")} {Field("Source")}: {said}";
        }
    }
}
