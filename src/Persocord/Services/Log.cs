using System;
using System.IO;

namespace Persocord.Services;

public static class Log
{
    private static readonly object Gate = new();
    private static int _written;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Paths.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                // The size is looked at every 50 lines, not after every one.
                if (++_written % 50 != 0) return;
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Length > 2 * 1024 * 1024)
                {
                    var text = File.ReadAllText(Paths.LogFile);
                    File.WriteAllText(Paths.LogFile, text[(text.Length / 2)..]);
                }
            }
        }
        catch { /* logging must never throw */ }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR {context}: {ex}");
}
