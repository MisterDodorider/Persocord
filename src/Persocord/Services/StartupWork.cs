using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// The things Persocord gets ready right after it starts (the video player's
/// engine, the graphics card checks, new clips' details). Until they're done,
/// previews can be slow to appear, so the clip list shows a bar saying so.
/// </summary>
public static class StartupWork
{
    private static readonly object Gate = new();
    private static readonly List<(string Name, Task Task)> Items = new();
    private static readonly Stopwatch Since = new();

    /// <summary>Shown for at most this long, whatever is still going.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(2);

    public static void Track(string name, Task task)
    {
        lock (Gate)
        {
            if (!Since.IsRunning) Since.Start();
            Items.Add((name, task));
        }
    }

    public static TimeSpan Age => Since.Elapsed;

    /// <summary>How many are done, out of how many, and the first one still going (null: all done).</summary>
    public static (int Done, int Total, string? Now) State()
    {
        lock (Gate)
        {
            int done = Items.Count(i => i.Task.IsCompleted);
            var now = Items.FirstOrDefault(i => !i.Task.IsCompleted).Name;
            return (done, Items.Count, now);
        }
    }
}
