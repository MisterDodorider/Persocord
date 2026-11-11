using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>What kind of thing a file is, for the editor.</summary>
public enum MediaKind { Unsupported, Video, Image, Audio }

/// <summary>
/// Files brought into the editor (pasted, dropped or imported): copied into
/// Persocord's own media folder, so an edit never breaks when the original
/// is moved or deleted. A file already copied is reused, not copied again.
/// </summary>
public static class EditorMedia
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".jfif" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".gif", ".ts", ".wmv", ".flv" };
    private static readonly HashSet<string> Sounds = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".flac", ".wma" };

    public static string Folder
    {
        get { var p = Path.Combine(Paths.Root, "media"); Directory.CreateDirectory(p); return p; }
    }

    /// <summary>The kind of file, by its name.</summary>
    public static MediaKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        return Images.Contains(ext) ? MediaKind.Image : Videos.Contains(ext) ? MediaKind.Video
             : Sounds.Contains(ext) ? MediaKind.Audio : MediaKind.Unsupported;
    }

    /// <summary>For the Import box.</summary>
    public const string OpenFilter =
        "Pictures, videos and sounds|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.mp3;*.wav;*.ogg;*.opus;*.m4a;*.aac;*.flac|All files|*.*";

    /// <summary>Whether a file is already in Persocord (the media folder or the clip library), so it isn't copied.</summary>
    public static bool IsOurs(string path, string libraryFolder)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   (libraryFolder.Length > 0 && full.StartsWith(Path.GetFullPath(libraryFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// A copy of a file in the media folder (made in the background; big
    /// videos take a moment). The same file brought in again is reused.
    /// </summary>
    public static Task<string?> CopyInAsync(string source) => Task.Run(() =>
    {
        try
        {
            var fi = new FileInfo(source);
            if (!fi.Exists) return null;
            // Named after the file and a fingerprint of where it came from, its size and time.
            var stamp = Paths.StableHash($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
            var name = Safe(Path.GetFileNameWithoutExtension(source));
            var target = Path.Combine(Folder, $"{name}-{stamp}{fi.Extension.ToLowerInvariant()}");
            if (File.Exists(target) && new FileInfo(target).Length == fi.Length) return target;
            File.Copy(source, target + ".part", true);
            File.Move(target + ".part", target, true);
            return target;
        }
        catch (Exception ex) { Log.Error("bring file into the editor", ex); return null; }
    });

    /// <summary>Pictures every part of Persocord reads as they are (the preview, and saving).</summary>
    private static readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".jfif" };

    /// <summary>
    /// A picture as a PNG beside it in the media folder, when it's a kind
    /// Windows or FFmpeg can't always show as a still (WebP, TIFF, a one-frame
    /// GIF). Others are returned as they are.
    /// </summary>
    public static Task<string?> AsPlainPictureAsync(string path) => Task.Run(() =>
    {
        if (Plain.Contains(Path.GetExtension(path))) return path;
        try
        {
            var fi = new FileInfo(path);
            var stamp = Paths.StableHash($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
            var target = Path.Combine(Folder, $"{Safe(Path.GetFileNameWithoutExtension(path))}-{stamp}.png");
            if (File.Exists(target) && new FileInfo(target).Length > 0) return target;
            bool ok = TimelineRender.Run($"-hide_banner -loglevel error -i \"{path}\" -frames:v 1 -y \"{target}.part.png\"",
                                         System.Threading.CancellationToken.None, null, 0);
            if (!ok || !File.Exists(target + ".part.png")) return null;
            File.Move(target + ".part.png", target, true);
            return target;
        }
        catch (Exception ex) { Log.Error("picture for the editor", ex); return null; }
    });

    /// <summary>A new file name in the media folder for something pasted (a picture with no file).</summary>
    public static string NewPastedPath(string ext) =>
        Path.Combine(Folder, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{ext}");

    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        if (s.Length > 48) s = s[..48];
        return s.Length == 0 ? "media" : s;
    }
}
