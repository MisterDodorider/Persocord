using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// Getting a finished clip into Discord.
///
/// Copying the file itself (rather than its path as text) is what makes
/// Ctrl+V in a Discord message box attach the video. Windows calls this a
/// file drop list, and it is the same payload Explorer puts on the clipboard
/// when you copy a file.
/// </summary>
public static class ShareService
{
    /// <summary>Puts the file itself on the clipboard, so Ctrl+V attaches it.</summary>
    public static Task<bool> CopyFileAsync(string path)
    {
        if (!File.Exists(path)) return Task.FromResult(false);
        var files = new StringCollection { path };
        return Clip(() => Clipboard.SetFileDropList(files), "file");
    }

    /// <summary>Puts text on the clipboard, such as a share link.</summary>
    public static Task<bool> CopyTextAsync(string text) =>
        Clip(() => Clipboard.SetText(text), "text");

    /// <summary>
    /// Runs a clipboard write on its own thread. Windows makes each write wait
    /// on every app watching the clipboard (clipboard history, Discord, cloud
    /// clipboard sync), which froze Persocord's window for a moment when done
    /// on the UI thread. Clipboard managers also hold it open briefly, so a
    /// refused write is retried rather than reported.
    /// </summary>
    private static Task<bool> Clip(Action write, string what)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            // Clipboard managers, cloud clipboard sync and Discord can each
            // hold the clipboard for a moment; about 2.5 seconds of patient
            // retries rides out all of them.
            int lastError = 0;
            for (int i = 0; i < 25; i++)
            {
                try { write(); done.TrySetResult(true); return; }
                catch (ExternalException ex) { lastError = ex.ErrorCode; Thread.Sleep(i < 5 ? 40 : 110); }
                catch (Exception ex) { Log.Error("clipboard " + what, ex); break; }
            }
            Log.Write($"clipboard refused the {what} (0x{lastError:X8})");
            done.TrySetResult(false);
        }) { IsBackground = true, Name = "Persocord clipboard" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""))?.Dispose();
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception ex) { Log.Error("show in explorer", ex); }
    }

    public static void Play(string path)
    {
        try
        {
            // Only ever a video or a GIF: a list entry pointing at anything else
            // (a hand-edited or copied-in library.json) is never opened, let alone run.
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".mp4" or ".gif" or ".mkv" or ".mov" or ".webm" or ".m4v" or ".avi")) return;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log.Error("play", ex); }
    }
}
