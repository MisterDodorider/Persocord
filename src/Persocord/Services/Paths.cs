using System;
using System.IO;

namespace Persocord.Services;

public static class Paths
{
    public static string Root { get; } = Init();

    /// <summary>Persocord's version as shown to people (e.g. 1.0.01).</summary>
    public static string AppVersion { get; } =
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is { Length: > 0 } a ? ((System.Reflection.AssemblyInformationalVersionAttribute)a[0]).InformationalVersion : null)
        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "";

    private static string Init()
    {
        var p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Persocord");
        Directory.CreateDirectory(p);
        return p;
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile      => Path.Combine(Root, "persocord.log");

    public static string Clips
    {
        get { var p = Path.Combine(Root, "clips"); Directory.CreateDirectory(p); return p; }
    }

    /// <summary>Clip thumbnails (absolute paths are stored with each clip).</summary>
    public static string Thumbs
    {
        get
        {
            var d = System.IO.Path.Combine(Root, "thumbs");
            System.IO.Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>
    /// A short fingerprint of some text that stays the same from run to run
    /// (string.GetHashCode changes every time the app starts).
    /// </summary>
    public static string StableHash(string text)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    public static string Temp
    {
        get { var p = Path.Combine(Root, "temp"); Directory.CreateDirectory(p); return p; }
    }

    /// <summary>
    /// ffmpeg.exe beside the app is the expected home. A copy in the settings
    /// folder also works, which lets someone fix a missing FFmpeg by dropping
    /// the file in without rebuilding. PATH is the last resort.
    /// </summary>
    public static string FFmpeg
    {
        get
        {
            foreach (var p in FFmpegCandidates)
                if (File.Exists(p)) return p;
            return "ffmpeg";
        }
    }

    public static string[] FFmpegCandidates
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            return new[]
            {
                Path.Combine(exeDir, "ffmpeg.exe"),
                Path.Combine(exeDir, "Assets", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(Root, "ffmpeg.exe"),
            };
        }
    }

    public static bool FFmpegFound => Array.Exists(FFmpegCandidates, File.Exists);
}
