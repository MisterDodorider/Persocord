using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Persocord's logo, drawn here rather than loaded, so it can take any
/// colours: the blades, the one accent blade, and the shard behind them.
/// By default it follows the look (text colour, accent, background); each
/// part can be set in Customization → Logo.
///
/// Used for the tray icon, every window's icon (title bar and taskbar while
/// open), the title bars Persocord draws itself, About and the setup, and
/// Persocord's own shortcuts: the taskbar pin, Start menu and desktop (see
/// <see cref="ShortcutIcons"/>). The .exe's own icon keeps the original colours.
/// </summary>
public static class LogoArt
{
    public readonly record struct Colours(Color Blades, Color Accent, Color Back);

    /// <summary>The logo as it was designed: cream blades, a coral one, on a near-black shard.</summary>
    public static readonly Colours Original = new(C(0xFFFDF8), C(0xEC6456), C(0x1B1C21));

    /* ---- the shape (from the logo's SVG, 256 × 256 box centred on 0,0) ---- */

    private const string Shard = "M76.09,16.17 L33.40,66.78 L-34.28,74.00 L-73.72,17.99 L-58.38,-45.15 L-1.22,-81.68 L72.48,-59.59 Z";

    private static readonly (double Angle, string Blade, string Fold)[] Blades =
    {
        (0.00, "M16.80,0.00 C44.86,-14.47 27.93,-75.11 36.22,-81.34 C46.60,-83.10 66.80,-65.41 62.20,-56.00 C40.24,-37.79 31.43,5.59 16.80,0.00 Z",
               "M16.80,0.00 C31.43,5.59 40.24,-37.79 42.67,-45.44 C33.05,-17.50 25.13,-1.85 16.80,0.00 Z"),
        (57.43, "M15.58,0.00 C39.21,-12.65 24.02,-64.59 31.15,-69.95 C40.07,-71.47 57.45,-56.26 53.49,-48.16 C34.61,-32.50 29.14,5.18 15.58,0.00 Z",
                "M15.58,0.00 C29.14,5.18 34.61,-32.50 36.69,-39.07 C28.42,-15.05 23.31,-1.71 15.58,0.00 Z"),
        (98.86, "M17.46,0.00 C48.08,-15.51 30.17,-81.12 39.11,-87.85 C50.32,-89.75 72.14,-70.65 67.18,-60.48 C43.46,-40.81 32.66,5.81 17.46,0.00 Z",
                "M17.46,0.00 C32.66,5.81 43.46,-40.81 46.08,-49.07 C35.69,-18.90 26.12,-1.92 17.46,0.00 Z"),
        (164.29, "M14.26,0.00 C33.49,-10.81 20.11,-54.08 26.08,-58.57 C33.55,-59.83 48.09,-47.10 44.78,-40.32 C28.97,-27.21 26.67,4.74 14.26,0.00 Z",
                 "M14.26,0.00 C26.67,4.74 28.97,-27.21 30.72,-32.71 C23.80,-12.60 21.33,-1.57 14.26,0.00 Z"),
        (205.71, "M16.80,0.00 C44.86,-14.47 27.93,-75.11 36.22,-81.34 C46.60,-83.10 66.80,-65.41 62.20,-56.00 C40.24,-37.79 31.43,5.59 16.80,0.00 Z",
                 "M16.80,0.00 C31.43,5.59 40.24,-37.79 42.67,-45.44 C33.05,-17.50 25.13,-1.85 16.80,0.00 Z"),
        (251.14, "M16.29,0.00 C42.45,-13.69 26.26,-70.60 34.04,-76.46 C43.80,-78.11 62.79,-61.49 58.47,-52.64 C37.83,-35.52 30.47,5.42 16.29,0.00 Z",
                 "M16.29,0.00 C30.47,5.42 37.83,-35.52 40.11,-42.71 C31.07,-16.45 24.37,-1.79 16.29,0.00 Z"),
        (311.57, "M17.78,0.00 C49.68,-16.03 31.29,-84.12 40.56,-91.10 C52.19,-93.07 74.81,-73.26 69.66,-62.73 C45.07,-42.33 33.26,5.91 17.78,0.00 Z",
                 "M17.78,0.00 C33.26,5.91 45.07,-42.33 47.79,-50.89 C37.02,-19.60 26.60,-1.95 17.78,0.00 Z"),
    };

    private const int AccentBlade = 3;

    /* ---- colours ---------------------------------------------------------------- */

    /// <summary>The colours a look gives the logo: its own picks, or the look's text, accent and background.</summary>
    public static Colours For(Looks l)
    {
        var p = ThemeApplier.For(l, l.Surface);
        var accent = ThemeApplier.Parse(l.Accent, C(0xD6B06A));
        return new Colours(
            Pick(l.LogoBlades, p.Ink),
            Pick(l.LogoAccent, accent),
            Pick(l.LogoBack, p.Bezel));
    }

    private static Color Pick(string? hex, Color auto) =>
        string.IsNullOrWhiteSpace(hex) ? auto : ThemeApplier.Parse(hex, auto);

    private static Colours? _now;

    /// <summary>The colours in use now.</summary>
    public static Colours Now => _now ??= For(LookStore.Current);

    /// <summary>Raised (on the UI thread) when the logo's colours change.</summary>
    public static event Action? Changed;

    /// <summary>Called when the look changes: redraws the logo everywhere if its colours did.</summary>
    public static void Refresh(Looks l)
    {
        SyncShortcuts();
        var c = For(l);
        if (_now == c) return;
        _now = c;
        _image = null;
        _icon = null;
        Changed?.Invoke();
    }

    /* ---- drawing ------------------------------------------------------------------ */

    /// <summary>The logo in a 256 × 256 box.</summary>
    public static DrawingGroup Art(Colours c)
    {
        var back = Solid(c.Back);
        var shardPen = new Pen(back, 9) { LineJoin = PenLineJoin.Round };
        var cutPen = new Pen(back, 5) { LineJoin = PenLineJoin.Round };
        shardPen.Freeze();
        cutPen.Freeze();

        var (bladeA, bladeB, bladeFold) = Shades(c.Blades, Original.Blades, C(0xFFFDF8), C(0xEBE6DE), C(0xBDB6AE), 0.08, 0.0);
        var (accA, accB, accFold) = Shades(c.Accent, Original.Accent, C(0xFF8172), C(0xD8473A), C(0x9E3127), 0.09, 0.13);
        var paper = Diagonal(bladeA, bladeB);
        var coral = Diagonal(accA, accB);
        var paperFold = Solid(bladeFold, 0.55);
        var coralFold = Solid(accFold, 0.55);

        var root = new DrawingGroup();
        var t = new TransformGroup();
        // The SVG's "scale(1.3425) translate(1.88,11.12)", then centred in the box.
        t.Children.Add(new TranslateTransform(1.88, 11.12));
        t.Children.Add(new ScaleTransform(1.3425, 1.3425));
        t.Children.Add(new TranslateTransform(128, 128));
        root.Transform = t;

        root.Children.Add(new GeometryDrawing(back, shardPen, Geometry.Parse(Shard)));

        var swirl = new DrawingGroup { Transform = new ScaleTransform(0.98, 0.98) };
        // First every blade's cut outline (in the shard's colour), so the blades read apart…
        foreach (var b in Blades)
            swirl.Children.Add(Turned(b.Angle, new GeometryDrawing(back, cutPen, Geometry.Parse(b.Blade))));
        // …then the blades themselves, each with its fold.
        for (int i = 0; i < Blades.Length; i++)
        {
            bool accent = i == AccentBlade;
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(accent ? coral : paper, null, Geometry.Parse(Blades[i].Blade)));
            g.Children.Add(new GeometryDrawing(accent ? coralFold : paperFold, null, Geometry.Parse(Blades[i].Fold)));
            swirl.Children.Add(Turned(Blades[i].Angle, g));
        }
        root.Children.Add(swirl);
        root.Freeze();
        return root;
    }

    /// <summary>
    /// A colour's light and dark ends and its fold shadow. The designed colours
    /// keep their exact artwork; any other is shaded the same way.
    /// </summary>
    private static (Color A, Color B, Color Fold) Shades(Color c, Color designed, Color a, Color b, Color fold, double darken, double lighten)
    {
        if (c == designed) return (a, b, fold);
        bool dark = Luminance(c) < 0.3;
        return (
            lighten > 0 ? ThemeApplier.Mix(c, Colors.White, lighten) : c,
            ThemeApplier.Mix(c, Colors.Black, darken),
            dark ? ThemeApplier.Mix(c, Colors.White, 0.28) : ThemeApplier.Mix(c, Colors.Black, 0.3));
    }

    private static Drawing Turned(double angle, Drawing d) =>
        new DrawingGroup { Children = { d }, Transform = new RotateTransform(angle) };

    private static Brush Diagonal(Color a, Color b)
    {
        var g = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
    }

    private static Brush Solid(Color c, double opacity = 1)
    {
        var s = new SolidColorBrush(c) { Opacity = opacity };
        s.Freeze();
        return s;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /* ---- images ------------------------------------------------------------------- */

    private static ImageSource? _image;

    /// <summary>The logo as it is now, sharp at any size (for About, the setup, title bars).</summary>
    public static ImageSource Image => _image ??= ImageOf(Now);

    public static ImageSource ImageOf(Colours c)
    {
        var img = new DrawingImage(Art(c));
        img.Freeze();
        return img;
    }

    /// <summary>The logo at an exact size in pixels.</summary>
    public static BitmapSource Render(Colours c, int px)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0));
            dc.DrawDrawing(Art(c));
        }
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    private static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
    private static BitmapFrame? _icon;

    /// <summary>The logo as a Windows icon with every size, for windows' title bars and the taskbar.</summary>
    public static BitmapFrame? WindowIcon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                var ms = new MemoryStream(IcoBytes(Now));
                var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                _icon = frame;
            }
            catch (Exception ex) { Log.Error("logo icon", ex); }
            return _icon;
        }
    }

    /// <summary>
    /// An .ico file's bytes. The small sizes are plain bitmaps (what every part
    /// of Windows reads best, Explorer and the taskbar included), the big one a PNG.
    /// </summary>
    public static byte[] IcoBytes(Colours c)
    {
        var images = new List<byte[]>();
        foreach (var size in IconSizes)
        {
            if (size <= 64) { images.Add(Dib(c, size)); continue; }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(c, size)));
            using var png = new MemoryStream();
            enc.Save(png);
            images.Add(png.ToArray());
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int s = IconSizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The logo's pixels (BGRA, top row first, straight alpha as icons want).</summary>
    private static byte[] StraightPixels(Colours c, int px)
    {
        var pixels = new byte[px * px * 4];
        Render(c, px).CopyPixels(pixels, px * 4, 0);
        // Rendered premultiplied; icons want straight alpha.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        return pixels;
    }

    /// <summary>One icon size as a bitmap entry: header, the pixels bottom row first, then an empty mask.</summary>
    private static byte[] Dib(Colours c, int px)
    {
        var pixels = StraightPixels(c, px);
        int maskRow = (px + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(px); w.Write(px * 2);          // height counts the colour and mask halves
        w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(px * px * 4 + maskRow * px);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = px - 1; y >= 0; y--) w.Write(pixels, y * px * 4, px * 4);
        w.Write(new byte[maskRow * px]);
        w.Flush();
        return ms.ToArray();
    }

    /* ---- shortcuts (the taskbar pin, Start menu, desktop) ---------------------- */

    private static string? _shortcutKey;
    private static System.Threading.Tasks.Task _shortcutJob = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>
    /// Persocord's shortcuts get the saved logo colours (Windows shows a pinned
    /// app with its shortcut's icon). Only once per saved change, in the
    /// background; trying colours in Customization never touches them.
    /// </summary>
    public static void SyncShortcuts()
    {
        try
        {
            var c = For(LookStore.Saved);
            string key = $"{ThemeApplier.Hex(c.Blades)}{ThemeApplier.Hex(c.Accent)}{ThemeApplier.Hex(c.Back)}";
            if (key == _shortcutKey) return;
            _shortcutKey = key;
            // The designed colours are the .exe's own icon: shortcuts go back to it.
            byte[]? ico = c == Original ? null : IcoBytes(c);
            // One after another, so a quick second change always ends up the one shown.
            _shortcutJob = _shortcutJob.ContinueWith(_ =>
            {
                try { ShortcutIcons.Apply(ico, key); }
                catch (Exception ex) { Log.Error("shortcut logo", ex); }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        catch (Exception ex) { Log.Error("shortcut logo", ex); }
    }

    /// <summary>The logo for the tray, drawn at exactly the size Windows shows it.</summary>
    public static System.Drawing.Icon? TrayIcon()
    {
        try
        {
            int px = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
            var pixels = StraightPixels(Now, px);
            using var bmp = new System.Drawing.Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, px, px), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                for (int y = 0; y < px; y++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, y * px * 4, data.Scan0 + y * data.Stride, px * 4);
            }
            finally { bmp.UnlockBits(data); }
            var handle = bmp.GetHicon();
            try
            {
                using var temp = System.Drawing.Icon.FromHandle(handle);
                return (System.Drawing.Icon)temp.Clone();
            }
            finally { DestroyIcon(handle); }
        }
        catch (Exception ex) { Log.Error("tray logo", ex); return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Gives a window the logo as its icon (title bar, taskbar, Alt+Tab).</summary>
    public static void ApplyTo(Window? w)
    {
        if (w == null) return;
        try { if (WindowIcon is { } icon && !ReferenceEquals(w.Icon, icon)) w.Icon = icon; }
        catch (Exception ex) { Log.Error("window logo", ex); }
    }
}
