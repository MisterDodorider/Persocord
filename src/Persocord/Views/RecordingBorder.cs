using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Persocord.Models;
using Persocord.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;
using WRect = System.Windows.Shapes.Rectangle;

namespace Persocord.Views;

/// <summary>
/// The frame around the area being recorded: a thin line, corner marks with
/// a slow heartbeat, and a timer above.
///
/// It is four slim windows along the edges rather than one window the size
/// of the area. Click-through overlays have to be redrawn by the CPU as a
/// whole image every frame they change, so a heartbeat on one area-sized
/// window would repaint millions of pixels many times a second while a game
/// is running. Four strips a few pixels wide cost next to nothing.
///
/// Everything sits in a band just outside the captured area, placed to the
/// pixel, so none of it is ever recorded.
/// </summary>
public sealed class RecordingBorder
{
    private const int PillRoomPx = 28;
    private const int CornerPx = 4;
    private const int CornerLenPx = 20;

    private readonly int _t;        // line thickness
    private readonly int _c;        // corner thickness
    private readonly int _m;        // band width outside the area
    private readonly int _fw, _fh;  // frame size in pixels
    private readonly FrameStyle _style;
    private readonly Color _tint;
    private readonly Color _cornerTint;
    private readonly bool _pulse;

    private readonly List<(Window win, Canvas canvas, Rectangle strip)> _strips = new();
    private Window? _pillWindow;
    private readonly TextBlock _timer = new();
    private readonly TextBlock _pausedMark = new();
    private readonly Ellipse _liveDot = new();
    private AnimationClock? _beat;

    private static readonly Color Warn = Color.FromRgb(0xE0, 0x59, 0x4D);
    private static readonly Color Muted = Color.FromRgb(0x8A, 0x90, 0x9A);
    private static readonly Color Ink = Color.FromRgb(0xF4, 0xF5, 0xF7);

    private enum State { Recording, Paused, Warned }
    private State _state = State.Recording;

    public RecordingBorder(Rectangle region, AppSettings s)
    {
        _t = Math.Clamp(LookStore.Current.FrameThickness, 1, 12);
        _c = Math.Max(CornerPx, _t);
        _m = _c + 1;
        _fw = region.Width + _m * 2;
        _fh = region.Height + _m * 2;
        var look = LookStore.Current;
        _style = look.FrameStyle;
        _tint = ThemeApplier.Parse(look.FrameColor, Color.FromRgb(0xA7, 0xAD, 0xB6));
        _cornerTint = ThemeApplier.Parse(look.FrameCornerColor, Color.FromRgb(0xE4, 0xE7, 0xEB));
        _pulse = look.FramePulse;

        int fx = region.X - _m, fy = region.Y - _m;

        // Top and bottom run the full width; the sides fill in between.
        AddStrip(new Rectangle(0, 0, _fw, _m), fx, fy);
        AddStrip(new Rectangle(0, _fh - _m, _fw, _m), fx, fy);
        AddStrip(new Rectangle(0, _m, _m, _fh - _m * 2), fx, fy);
        AddStrip(new Rectangle(_fw - _m, _m, _m, _fh - _m * 2), fx, fy);

        if (HasRoomAbove(region, _m)) BuildPill(fx, fy);
    }

    /* ---- windows --------------------------------------------------------- */

    private static Window Overlay()
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            IsHitTestVisible = false,
            Topmost = true,
        };
        w.SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(w).Handle);
        return w;
    }

    private void AddStrip(Rectangle strip, int fx, int fy)
    {
        if (strip.Width <= 0 || strip.Height <= 0) return;
        var win = Overlay();
        var canvas = new Canvas();
        win.Content = canvas;
        PixelPlacement.Pin(win, new Rectangle(fx + strip.X, fy + strip.Y, strip.Width, strip.Height));
        win.Loaded += (_, _) => Draw(win, canvas, strip);
        win.DpiChanged += (_, _) => Draw(win, canvas, strip);
        _strips.Add((win, canvas, strip));
    }

    private void BuildPill(int fx, int fy)
    {
        var win = Overlay();
        var pal = ThemeApplier.Overlay;

        _liveDot.Width = 7;
        _liveDot.Height = 7;
        _liveDot.Fill = new SolidColorBrush(Warn);
        _liveDot.Margin = new Thickness(0, 0, 7, 0);
        _liveDot.VerticalAlignment = VerticalAlignment.Center;

        _timer.Text = "0:00";
        _timer.Foreground = new SolidColorBrush(Ink);
        _timer.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _timer.FontWeight = FontWeights.SemiBold;
        _timer.FontSize = 12;
        System.Windows.Documents.Typography.SetNumeralAlignment(_timer, FontNumeralAlignment.Tabular);

        _pausedMark.Text = "paused";
        _pausedMark.Foreground = new SolidColorBrush(Muted);
        _pausedMark.FontSize = 11.5;
        _pausedMark.Margin = new Thickness(8, 0, 0, 0);
        _pausedMark.VerticalAlignment = VerticalAlignment.Center;
        _pausedMark.Visibility = Visibility.Collapsed;

        // A small dark chip, like the player's and the area picker's.
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x12, 0x14, 0x18)),
            BorderThickness = new Thickness(1),
            BorderBrush = _style == FrameStyle.Metal
                ? MetalBrush(_tint, 0, 0, 150, 26, 0.35)
                : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 9, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _liveDot, _timer, _pausedMark } },
        };
        win.Content = new Grid { Children = { pill } };
        PixelPlacement.Pin(win, new Rectangle(fx, fy - PillRoomPx, 160, PillRoomPx - 2));
        _pillWindow = win;
    }

    public void Show()
    {
        foreach (var (win, _, _) in _strips) win.Show();
        _pillWindow?.Show();
        Beat(_state == State.Recording && _pulse);
    }

    public void Close()
    {
        Beat(false);
        foreach (var (win, _, _) in _strips) { try { win.Close(); } catch { } }
        try { _pillWindow?.Close(); } catch { }
    }

    /* ---- drawing --------------------------------------------------------- */

    /// <summary>
    /// Draws whatever parts of the line and corner marks fall inside one
    /// strip. Pieces are worked out in frame pixels, so the four strips join
    /// seamlessly, and the metal sheen is mapped across the whole frame.
    /// </summary>
    private void Draw(Window win, Canvas canvas, Rectangle strip)
    {
        canvas.Children.Clear();
        double s = PixelPlacement.Scale(win);

        // Positions in this strip's own scaled units, and where the frame
        // starts relative to it (for continuous gradients).
        double ox = strip.X / s, oy = strip.Y / s, fw = _fw / s, fh = _fh / s;

        Brush line, corner;
        switch (_state)
        {
            case State.Paused:
                line = Solid(Muted, 0.5);
                corner = Solid(Muted, 0.9);
                break;
            case State.Warned:
                line = Solid(Warn, 0.8);
                corner = Solid(Warn, 1);
                break;
            default:
                if (_style == FrameStyle.Metal)
                {
                    line = MetalBrush(_tint, ox, oy, fw, fh, 0);
                    line.Opacity = 0.92;
                    corner = MetalBrush(_cornerTint, ox, oy, fw, fh, 0.15);
                }
                else if (_style == FrameStyle.Clean)
                {
                    // A soft line and crisp corners: the area reads clearly without a heavy box.
                    line = Solid(_tint, 0.6);
                    corner = Solid(_cornerTint, 1);
                }
                else
                {
                    line = Solid(_tint, 0.55);
                    corner = Solid(_cornerTint, 1);
                }
                break;
        }

        // One shared clock, so every corner beats together.
        if (_beat != null && _state == State.Recording) corner.ApplyAnimationClock(Brush.OpacityProperty, _beat);

        if (_style == FrameStyle.Clean)
        {
            // Clean: a crisp 1px line hugging the area with a hairline of shadow
            // outside it (so it reads on bright and dark screens alike), and
            // short, slim corner marks, also edged in shadow.
            var shade = Solid(Colors.Black, 0.45);
            foreach (var piece in CleanCorners(_m - 4, 1, 1)) Place(canvas, piece, strip, s, shade);
            foreach (var piece in Ring(_m - 2, 1)) Place(canvas, piece, strip, s, shade);
            foreach (var piece in Ring(_m - 1, 1)) Place(canvas, piece, strip, s, line);
            foreach (var piece in CleanCorners(_m - 3, 3, 0)) Place(canvas, piece, strip, s, corner);
            return;
        }
        foreach (var piece in LinePieces()) Place(canvas, piece, strip, s, line);
        foreach (var piece in CornerPieces()) Place(canvas, piece, strip, s, corner);
    }

    /// <summary>A ring <paramref name="inset"/> pixels in from the frame's outer edge.</summary>
    private IEnumerable<Rectangle> Ring(int inset, int thick)
    {
        if (inset < 0) yield break;
        yield return new Rectangle(inset, inset, _fw - inset * 2, thick);
        yield return new Rectangle(inset, _fh - inset - thick, _fw - inset * 2, thick);
        yield return new Rectangle(inset, inset, thick, _fh - inset * 2);
        yield return new Rectangle(_fw - inset - thick, inset, thick, _fh - inset * 2);
    }

    /// <summary>Short L marks at the four corners, <paramref name="inset"/> in from the outer edge.</summary>
    private IEnumerable<Rectangle> CleanCorners(int inset, int thick, int grow)
    {
        if (inset < 0) yield break;
        int len = Math.Min(18, Math.Min(_fw, _fh) / 3) + grow * 2;
        int r = _fw - inset, b = _fh - inset;
        yield return new Rectangle(inset, inset, len, thick);
        yield return new Rectangle(inset, inset, thick, len);
        yield return new Rectangle(r - len, inset, len, thick);
        yield return new Rectangle(r - thick, inset, thick, len);
        yield return new Rectangle(inset, b - thick, len, thick);
        yield return new Rectangle(inset, b - len, thick, len);
        yield return new Rectangle(r - len, b - thick, len, thick);
        yield return new Rectangle(r - thick, b - len, thick, len);
    }

    private IEnumerable<Rectangle> LinePieces()
    {
        yield return new Rectangle(0, 0, _fw, _t);
        yield return new Rectangle(0, _fh - _t, _fw, _t);
        yield return new Rectangle(0, 0, _t, _fh);
        yield return new Rectangle(_fw - _t, 0, _t, _fh);
    }

    private IEnumerable<Rectangle> CornerPieces()
    {
        int L = Math.Min(CornerLenPx, Math.Min(_fw, _fh) / 3);
        yield return new Rectangle(0, 0, L, _c);
        yield return new Rectangle(0, 0, _c, L);
        yield return new Rectangle(_fw - L, 0, L, _c);
        yield return new Rectangle(_fw - _c, 0, _c, L);
        yield return new Rectangle(0, _fh - _c, L, _c);
        yield return new Rectangle(0, _fh - L, _c, L);
        yield return new Rectangle(_fw - L, _fh - _c, L, _c);
        yield return new Rectangle(_fw - _c, _fh - L, _c, L);
    }

    private static void Place(Canvas canvas, Rectangle piece, Rectangle strip, double s, Brush fill)
    {
        var hit = Rectangle.Intersect(piece, strip);
        if (hit.Width <= 0 || hit.Height <= 0) return;
        var r = new WRect { Width = hit.Width / s, Height = hit.Height / s, Fill = fill, SnapsToDevicePixels = true };
        Canvas.SetLeft(r, (hit.X - strip.X) / s);
        Canvas.SetTop(r, (hit.Y - strip.Y) / s);
        canvas.Children.Add(r);
    }

    /// <summary>
    /// Brushed metal from a tint: bands of lighter and darker tint across the
    /// frame's diagonal, like light running along a machined edge.
    /// </summary>
    private static LinearGradientBrush MetalBrush(Color tint, double ox, double oy, double w, double h, double lift)
    {
        Color L(double k) => ThemeApplier.Mix(tint, Colors.White, Math.Min(1, k + lift));
        Color D(double k) => ThemeApplier.Mix(tint, Colors.Black, Math.Max(0, k - lift));

        var b = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(-ox, -oy),
            EndPoint = new Point(w - ox, h - oy),
        };
        b.GradientStops.Add(new GradientStop(L(0.45), 0.00));
        b.GradientStops.Add(new GradientStop(D(0.30), 0.14));
        b.GradientStops.Add(new GradientStop(L(0.60), 0.28));
        b.GradientStops.Add(new GradientStop(D(0.20), 0.43));
        b.GradientStops.Add(new GradientStop(L(0.35), 0.57));
        b.GradientStops.Add(new GradientStop(D(0.35), 0.72));
        b.GradientStops.Add(new GradientStop(L(0.55), 0.86));
        b.GradientStops.Add(new GradientStop(D(0.15), 1.00));
        return b;
    }

    private static SolidColorBrush Solid(Color c, double opacity) => new(c) { Opacity = opacity };

    /* ---- heartbeat ------------------------------------------------------- */

    private void Beat(bool on)
    {
        _beat?.Controller?.Stop();
        _beat = null;
        _liveDot.BeginAnimation(UIElement.OpacityProperty, null);
        _liveDot.Opacity = 1;

        if (on)
        {
            var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            // A slow sine needs few frames to look smooth; capping them keeps
            // the overlay's cost negligible while a game is running.
            Timeline.SetDesiredFrameRate(pulse, 30);
            _beat = pulse.CreateClock();
            _liveDot.ApplyAnimationClock(UIElement.OpacityProperty, _beat);
        }

        foreach (var (win, canvas, strip) in _strips)
            if (win.IsLoaded) Draw(win, canvas, strip);
    }

    /* ---- state ----------------------------------------------------------- */

    /// <summary>Grey and still while paused, so it never looks like it is recording.</summary>
    public void SetPaused(bool paused)
    {
        _state = paused ? State.Paused : (_warnedOnce ? State.Warned : State.Recording);
        _liveDot.Fill = new SolidColorBrush(paused ? Muted : Warn);
        _timer.Foreground = new SolidColorBrush(paused ? Muted : (_warnedOnce ? Warn : Ink));
        _pausedMark.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        Beat(_state == State.Recording && _pulse);
    }

    private bool _warnedOnce;

    public void Update(TimeSpan elapsed, int warnAtSeconds, bool warnEnabled)
    {
        if (_state == State.Paused) return;

        var t = (int)elapsed.TotalSeconds;
        _timer.Text = $"{t / 60}:{t % 60:00}";

        if (warnEnabled && !_warnedOnce && warnAtSeconds > 0 && t >= warnAtSeconds)
        {
            _warnedOnce = true;
            _state = State.Warned;
            _timer.Foreground = new SolidColorBrush(Warn);
            Beat(false);
        }
    }

    private static bool HasRoomAbove(Rectangle region, int pad)
    {
        foreach (var sc in Screen.AllScreens)
            if (sc.Bounds.Contains(region.X + region.Width / 2, region.Y + region.Height / 2))
                return region.Y - pad - PillRoomPx >= sc.Bounds.Top;
        return false;
    }
}
