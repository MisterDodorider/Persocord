using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// Keeps Persocord's temp folder from growing forever. Leftovers (finished
/// conversions, joins, mixes, previews) go after two days. The editor's
/// working copies (small preview copies and decoded sound) are kept so
/// reopening a clip is instant, up to 3 GB and two weeks, oldest first.
/// Only files directly in the folder are touched (never the replay's own
/// folder), and anything still open, like a recording in progress, is left.
/// </summary>
public static class TempJanitor
{
    private const long WorkingBudget = 3L * 1024 * 1024 * 1024;

    private static bool IsWorkingCopy(string name) =>
        name.StartsWith("proxy", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase);

    public static Task RunAsync() => Task.Run(Run);

    public static void Run()
    {
        try
        {
            var files = new DirectoryInfo(Paths.Temp).GetFiles();
            var now = DateTime.UtcNow;
            long freed = 0;
            int removed = 0;
            void Drop(FileInfo f)
            {
                try { long len = f.Length; f.Delete(); freed += len; removed++; } catch { /* in use */ }
            }

            foreach (var f in files.Where(f => !IsWorkingCopy(f.Name) && now - f.LastWriteTimeUtc > TimeSpan.FromDays(2)))
                Drop(f);

            var working = files.Where(f => IsWorkingCopy(f.Name) && f.Exists)
                               .OrderByDescending(Used).ToList();
            long total = 0;
            foreach (var f in working)
            {
                if (now - Used(f) > TimeSpan.FromDays(14) || total + f.Length > WorkingBudget) Drop(f);
                else total += f.Length;
            }
            if (removed > 0) Log.Write($"Temp folder: removed {removed} old files ({freed / 1048576.0:0} MB).");
        }
        catch (Exception ex) { Log.Error("temp cleanup", ex); }
        CleanEditorMedia();
    }

    /// <summary>
    /// Pictures, videos and sounds brought into the editor are kept while any
    /// kept edit uses them (or could, through undo), and go a month after
    /// the last one stops. If any edit can't be read, nothing is removed.
    /// </summary>
    private static void CleanEditorMedia()
    {
        try
        {
            var media = Path.Combine(Paths.Root, "media");
            var edits = Path.Combine(Paths.Root, "edits");
            if (!Directory.Exists(media)) return;
            var used = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void FromState(string? json)
            {
                if (string.IsNullOrEmpty(json)) return;
                using var st = System.Text.Json.JsonDocument.Parse(json);
                if (st.RootElement.TryGetProperty("Clips", out var clips) && clips.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var c in clips.EnumerateArray())
                        if (c.TryGetProperty("File", out var f) && f.GetString() is { Length: > 0 } file) used.Add(Path.GetFullPath(file));
            }
            if (Directory.Exists(edits))
                foreach (var project in Directory.EnumerateFiles(edits, "*.json"))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(project));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("EditTimeline", out var tl)) FromState(tl.GetString());
                    foreach (var list in new[] { "Undo", "Redo" })
                        if (root.TryGetProperty(list, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var st in arr.EnumerateArray()) FromState(st.GetString());
                    if (root.TryGetProperty("Bin", out var bin) && bin.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var e in bin.EnumerateArray())
                            if (e.TryGetProperty("Path", out var p) && p.GetString() is { Length: > 0 } path) used.Add(Path.GetFullPath(path));
                }
            int removed = 0;
            foreach (var f in new DirectoryInfo(media).GetFiles())
            {
                if (used.Contains(f.FullName) || DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromDays(30)) continue;
                try { f.Delete(); removed++; } catch { }
            }
            if (removed > 0) Log.Write($"Editor media: removed {removed} file(s) no edit uses any more.");
        }
        catch (Exception ex) { Log.Error("editor media cleanup (nothing removed)", ex); }
    }

    /// <summary>When a working copy was last used (written, or marked as used when opened).</summary>
    private static DateTime Used(FileInfo f)
    {
        try { f.Refresh(); } catch { }
        return f.LastWriteTimeUtc > f.LastAccessTimeUtc ? f.LastWriteTimeUtc : f.LastAccessTimeUtc;
    }

    /// <summary>Marks a working copy as just used, so it's the last to go.</summary>
    public static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
    }
}
