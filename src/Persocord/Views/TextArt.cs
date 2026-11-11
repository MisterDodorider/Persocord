using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Text clips are drawn once as a see-through picture (PNG), and that picture
/// is what the preview shows and what's saved, so they always match, with
/// any font on the PC. Drawn at twice its size on a 1080-high frame, for
/// clean edges. Kept in the temp folder by what it looks like, and drawn
/// again whenever it's missing.
/// </summary>
public static class TextArt
{
    /// <summary>The picture for this text (made if needed), and its size in pixels. Null if it couldn't be drawn.</summary>
    public static (string Path, int Width, int Height)? Get(TextStyle style)
    {
        var path = System.IO.Path.Combine(Paths.Temp, $"proxytext_{Paths.StableHash("v2|" + style.Key())}.png");
        try
        {
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (dec.Frames.Count > 0) { TempJanitor.Touch(path); return (path, dec.Frames[0].PixelWidth, dec.Frames[0].PixelHeight); }
            }
            var bmp = Draw(style);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path + ".part"))
                enc.Save(fs);
            File.Move(path + ".part", path, true);
            return (path, bmp.PixelWidth, bmp.PixelHeight);
        }
        catch (Exception ex) { Log.Error("text picture", ex); return null; }
    }

    private static Color Parse(string hex, Color fallback) => ThemeApplier.Parse(hex, fallback);

    private static BitmapSource Draw(TextStyle s)
    {
        double k = LayerBox.TextOversample;
        double size = Math.Clamp(s.Size, 4, 1000) * k;
        var family = new FontFamily(string.IsNullOrWhiteSpace(s.Font) ? "Segoe UI" : s.Font);
        var face = new Typeface(family, s.Italic ? FontStyles.Italic : FontStyles.Normal, s.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = string.IsNullOrEmpty(s.Text) ? " " : s.Text.Replace("\r\n", "\n");
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White, 1.0);
        // Centred and right-aligned lines line up within the widest line. Without a
        // width to line up in, WPF centres them on the starting point itself, so
        // half the text (all of it, right-aligned) fell outside the picture.
        double width = Math.Max(1, ft.WidthIncludingTrailingWhitespace);
        ft.MaxTextWidth = width + 1;
        ft.TextAlignment = s.Align switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center };
        double outline = Math.Clamp(s.Outline, 0, 60) * k;
        double pad = s.Box ? size * 0.3 : 0;
        double shadow = s.Shadow ? Math.Max(2, size * 0.05) : 0;
        double margin = outline + shadow + pad + 4;
        var geo = ft.BuildGeometry(new Point(0, 0));
        var raw = geo.Bounds.IsEmpty ? new Rect(0, 0, 1, size) : geo.Bounds;
        // Whatever the letters do (a slanted italic, a swash), all of them land inside the picture.
        double dx = margin - Math.Min(0, raw.Left), dy = margin - Math.Min(0, raw.Top);
        geo = geo.Clone();
        geo.Transform = new TranslateTransform(dx, dy);
        var bounds = geo.Bounds;
        var layout = new Rect(dx, dy, width + 1, Math.Max(1, ft.Height));
        // The box hugs the lines' layout (so letters with and without descenders get the same box).
        var box = Rect.Union(layout, bounds);
        box.Inflate(pad, pad * 0.6);
        double w = Math.Ceiling(Math.Max(box.Right, bounds.Right + outline) + outline + shadow + 4);
        double h = Math.Ceiling(Math.Max(box.Bottom, bounds.Bottom + outline) + outline + shadow + 4);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (s.Box)
            {
                var c = Parse(s.BoxColour, Colors.Black);
                c.A = (byte)Math.Round(Math.Clamp(s.BoxOpacity, 0, 1) * 255);
                dc.DrawRoundedRectangle(new SolidColorBrush(c), null, box, size * 0.18, size * 0.18);
            }
            if (s.Shadow)
            {
                dc.PushTransform(new TranslateTransform(shadow, shadow));
                var sh = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0));
                dc.DrawGeometry(sh, outline > 0 ? new Pen(sh, outline * 2) { LineJoin = PenLineJoin.Round } : null, geo);
                dc.Pop();
            }
            if (outline > 0)
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Parse(s.OutlineColour, Colors.Black)), outline * 2) { LineJoin = PenLineJoin.Round }, geo);
            dc.DrawGeometry(new SolidColorBrush(Parse(s.Colour, Colors.White)), null, geo);
        }
        var rtb = new RenderTargetBitmap((int)Math.Clamp(w, 2, 8000), (int)Math.Clamp(h, 2, 8000), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
