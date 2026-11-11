using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Persocord.Models;
using Persocord.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Drawing.Rectangle;

namespace Persocord.Views;

/// <summary>A number counting down in the middle of the area (in a ring that runs down each second), then gone.</summary>
public static class CountdownOverlay
{
    public static async Task RunAsync(Rectangle region, int seconds, string accentHex)
    {
        if (seconds <= 0) return;

        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(accentHex); }
        catch { accent = Colors.White; }

        var pal = ThemeApplier.Overlay;
        var look = LookStore.Current;
        double k = look.CountdownScalePercent / 100.0;
        // The overlay look shared with the player: a dark chip, white type, a thin ring.
        var chip = Color.FromArgb(0xE0, 0x12, 0x14, 0x18);
        var edge = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
        var text = new TextBlock
        {
            FontSize = 46,
            FontWeight = FontWeights.Light,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Margin = new Thickness(0, 0, 0, 5),
        };

        var badge = new Grid
        {
            Width = 112,
            Height = 112,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 24, ShadowDepth = 2, Opacity = 0.4,
            },
        };
        // Ring: a dark disc, and a thin ring that runs down over each second.
        // Big: just a large number. Minimal: a small pill near the top of the area.
        System.Windows.Shapes.Ellipse? ring = null;
        double ringDash = 0;
        if (look.Countdown == CountdownStyle.Big)
        {
            badge.Width = badge.Height = double.NaN;
            text.FontSize = 120;
            text.FontWeight = FontWeights.SemiBold;
            text.Foreground = new SolidColorBrush(accent);
        }
        else if (look.Countdown == CountdownStyle.Minimal)
        {
            badge.Width = 60;
            badge.Height = 36;
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 24, 0, 0);
            text.FontSize = 19;
            text.FontWeight = FontWeights.SemiBold;
            text.Margin = new Thickness(0, 0, 0, 2);
            badge.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(chip),
                BorderBrush = new SolidColorBrush(edge), BorderThickness = new Thickness(1),
            });
        }
        else
        {
            const double size = 112, thick = 2.5;
            badge.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Fill = new SolidColorBrush(chip), Stroke = new SolidColorBrush(edge), StrokeThickness = 1,
            });
            // The track the ring runs along, faint.
            badge.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), StrokeThickness = thick, Margin = new Thickness(7),
            });
            // Dashes are measured in stroke widths: one dash the length of the whole ring.
            ringDash = Math.PI * (size - 14 - thick) / thick;
            ring = new System.Windows.Shapes.Ellipse
            {
                Stroke = new SolidColorBrush(accent), StrokeThickness = thick, Margin = new Thickness(7),
                StrokeDashArray = new DoubleCollection { ringDash, ringDash },
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-90),   // from the top
            };
            badge.Children.Add(ring);
        }
        badge.Children.Add(text);
        if (Math.Abs(k - 1) > 0.01) badge.LayoutTransform = new ScaleTransform(k, k);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = look.CountdownDim
                ? new SolidColorBrush(Color.FromArgb(40, pal.Bezel.R, pal.Bezel.G, pal.Bezel.B))
                : Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            IsHitTestVisible = false,
            Content = new Grid { Children = { badge } },
        };

        PixelPlacement.Pin(window, region);
        window.SourceInitialized += (_, _) =>
            Native.MakeClickThrough(new WindowInteropHelper(window).Handle);
        window.Show();

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        for (int i = seconds; i > 0; i--)
        {
            text.Text = i.ToString();

            // Each number settles in: a touch larger and faint, easing to rest.
            var grow = new System.Windows.Media.Animation.DoubleAnimation(1.25, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
            var scale = (ScaleTransform)text.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            text.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            // The ring empties over the second.
            ring?.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, ringDash, TimeSpan.FromMilliseconds(950)));

            await Task.Delay(1000);
        }

        window.Close();
        // A moment for the overlay to really leave the screen, so it does not
        // appear in the first captured frame.
        await Task.Delay(120);
    }
}
