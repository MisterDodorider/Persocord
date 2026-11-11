using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Still pictures for the editor's preview, loaded once and kept (eight at
/// most, the oldest let go first, and all of them when the editor closes).
/// Read into memory and the file closed at once, so nothing holds it open;
/// big pictures are loaded no bigger than 1920 pixels across, plenty for a
/// preview (a 4K picture held at full size took 33 MB).
/// </summary>
public static class Pictures
{
    private const int Keep = 8, MaxWidth = 1920;
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new();

    public static BitmapSource? Get(string path)
    {
        var key = TimelineRender.Identity(path);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        BitmapSource? img = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var probe = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int width = probe.Frames.Count > 0 ? probe.Frames[0].PixelWidth : 0;
            fs.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = fs;
            if (width > MaxWidth) bi.DecodePixelWidth = MaxWidth;
            bi.EndInit();
            bi.Freeze();
            img = bi;
        }
        catch (Exception ex) { Log.Error("editor picture", ex); }
        Cache[key] = img;
        Order.Remove(key);
        Order.AddLast(key);
        while (Order.Count > Keep) { Cache.Remove(Order.First!.Value); Order.RemoveFirst(); }
        return img;
    }

    private static readonly Dictionary<string, BitmapSource?> Small = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A small copy (80 pixels high) for the timeline's clips: drawn on every
    /// redraw, so kept apart from the preview's (a timeline full of pictures
    /// never pushes the preview's out, or loads big ones to draw small).
    /// </summary>
    public static BitmapSource? Thumb(string path)
    {
        if (Small.TryGetValue(path, out var t)) return t;
        BitmapSource? img = null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelHeight = 80;
            bi.UriSource = new System.Uri(path);
            bi.EndInit();
            bi.Freeze();
            img = bi;
        }
        catch (Exception ex) { Log.Error("editor picture (small)", ex); }
        if (Small.Count > 128) Small.Clear();
        Small[path] = img;
        return img;
    }

    /// <summary>Lets every picture go (the editor closed).</summary>
    public static void Clear() { Cache.Clear(); Order.Clear(); Small.Clear(); }
}
