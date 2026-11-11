using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Persocord.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using FontFamily = System.Windows.Media.FontFamily;
using ColorConverter = System.Windows.Media.ColorConverter;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace Persocord.Views;

/// <summary>
/// One transparent window stretched over the whole virtual desktop. Drag to
/// pick a region; the dimmed area outside the selection is drawn with an
/// even-odd geometry so the selected part stays perfectly clear.
/// </summary>
public class RegionSelector : Window
{
    private readonly Canvas _canvas = new();
    private readonly Path _dimmer = new();
    private readonly System.Windows.Shapes.Rectangle _outline = new();
    private readonly System.Windows.Shapes.Rectangle _outlineShade = new();
    private readonly Border _readout = new();
    private readonly TextBlock _readoutText = new();
    private readonly Path _corners = new();
    private readonly System.Windows.Shapes.Rectangle _guideH = new();
    private readonly System.Windows.Shapes.Rectangle _guideV = new();
    /// <summary>Rule-of-thirds lines inside the selection (Customization → Area picker).</summary>
    private readonly Path _thirds = new();
    private readonly Persocord.Models.Looks _look = LookStore.Current;
    private Color _dimColour, _dimDeep;
    private readonly int _dimPercent;

    private Point _start;
    private bool _dragging;
    private Rect? _hole;
    private Border? _hintBox;
    private readonly Rectangle _virtualBounds;
    private readonly Rectangle? _preset;
    private readonly string _accent;

    public Rectangle? Result { get; private set; }

    /// <summary>True when the picker was left with Tab to go to the clip list.</summary>
    public bool OpenClipsRequested { get; private set; }

    public RegionSelector(string accentHex, Rectangle? lastRegion = null, int dimPercent = 15)
    {
        _accent = accentHex;
        _preset = lastRegion;
        _dimPercent = Math.Clamp(dimPercent, 0, 90);

        _virtualBounds = VirtualDesktop();

        // See-through the fast way: rather than a "layered" window, which the
        // CPU has to redraw as one screen-sized image on every mouse move
        // (capping the guides far below a high-refresh monitor), the window
        // is drawn by the GPU and Windows' compositor blends it. That keeps
        // up with a 240 Hz display.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            GpuTransparency(hwnd);
            StayInBackground(hwnd);
        };
        // Never the active window: games and videos behind keep running
        // (many pause or minimise when they lose focus).
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;

        // Covers every monitor, placed in physical pixels. The mouse position
        // WPF reports inside this window converts back to screen pixels with
        // one factor across the whole window, which is what makes the
        // selection land exactly where it was drawn.
        PixelPlacement.Pin(this, _virtualBounds);

        Content = _canvas;
        SizeChanged += (_, _) => { DrawDim(_hole); PlaceHint(); };
        Build();

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;

        // Tab normally moves keyboard focus, so it has to be caught before
        // WPF acts on it.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Tab) return;
            e.Handled = true;
            OpenClipsRequested = true;
            Result = null;
            DialogResult = false;
            Close();
        };
        // A quick fade in rather than the screen snapping dark. (The content
        // fades; a GPU-drawn window can't fade as a whole.)
        _canvas.Opacity = 0;
        Loaded += (_, _) =>
        {
            HookKeys();
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Timeline.SetDesiredFrameRate(fade, 240);
            _canvas.BeginAnimation(OpacityProperty, fade);
            PlaceGuides(Mouse.GetPosition(_canvas));
        };
    }

    // The overlay look shared with the player: thin light lines, dark soft chips.
    private static readonly Color ChipFill = Color.FromArgb(0xE8, 0x12, 0x14, 0x18);
    private static readonly Color ChipEdge = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
    private static readonly Color Ink = Color.FromRgb(0xF4, 0xF5, 0xF7);
    private static readonly Color Soft = Color.FromRgb(0xA4, 0xAA, 0xB2);
    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");

    private static Border Chip(UIElement child, Thickness padding, double radius) => new()
    {
        Background = new SolidColorBrush(ChipFill),
        BorderBrush = new SolidColorBrush(ChipEdge),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding,
        Child = child,
        IsHitTestVisible = false,
    };

    private void Build()
    {
        var line = ParseColour(_accent);

        // A neutral dim, at the chosen strength.
        // While an area is being drawn, the rest of the screen dims a little
        // more, like a spotlight on what's chosen.
        byte alpha = (byte)(_dimPercent * 255 / 100);
        _dimColour = Color.FromArgb(alpha, 0x06, 0x07, 0x09);
        _dimDeep = Color.FromArgb((byte)Math.Min(235, alpha + 38), 0x06, 0x07, 0x09);
        _dimmer.Fill = new SolidColorBrush(_dimColour);
        _dimmer.IsHitTestVisible = false;
        _canvas.Children.Add(_dimmer);

        // Faint cross-hair guides that follow the pointer until a drag starts.
        // They fade out away from the pointer, so they guide without slicing the screen in four.
        foreach (var g in new[] { _guideH, _guideV })
        {
            g.IsHitTestVisible = false;
            _canvas.Children.Add(g);
        }
        _guideH.Height = 1;
        _guideV.Width = 1;
        if (!_look.PickerGuides) _guideH.Visibility = _guideV.Visibility = Visibility.Collapsed;

        _thirds.Stroke = new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF));
        _thirds.StrokeThickness = 1;
        _thirds.IsHitTestVisible = false;
        _thirds.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(_thirds);

        // A hairline of shadow just outside the edge, so the line reads on bright pictures too.
        _outlineShade.Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0));
        _outlineShade.StrokeThickness = 1;
        _outlineShade.Visibility = Visibility.Collapsed;
        _outlineShade.IsHitTestVisible = false;
        _canvas.Children.Add(_outlineShade);

        _outline.Stroke = new SolidColorBrush(Color.FromArgb(0xD8, line.R, line.G, line.B));
        _outline.StrokeThickness = 1;
        _outline.Visibility = Visibility.Collapsed;
        _outline.IsHitTestVisible = false;
        _canvas.Children.Add(_outline);

        // Short rounded corner marks: the selection reads as a frame, not a box.
        _corners.Stroke = new SolidColorBrush(line);
        _corners.StrokeThickness = 2.5;
        _corners.StrokeStartLineCap = PenLineCap.Round;
        _corners.StrokeEndLineCap = PenLineCap.Round;
        _corners.StrokeLineJoin = PenLineJoin.Round;
        _corners.Visibility = Visibility.Collapsed;
        _corners.IsHitTestVisible = false;
        _corners.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.45,
        };
        _canvas.Children.Add(_corners);

        _readoutText.Foreground = new SolidColorBrush(Ink);
        _readoutText.FontFamily = Ui;
        _readoutText.FontSize = 12;
        _readoutText.FontWeight = FontWeights.SemiBold;
        Typography.SetNumeralAlignment(_readoutText, FontNumeralAlignment.Tabular);

        _readout.Background = new SolidColorBrush(ChipFill);
        _readout.BorderBrush = new SolidColorBrush(ChipEdge);
        _readout.BorderThickness = new Thickness(1);
        _readout.CornerRadius = new CornerRadius(8);
        _readout.Padding = new Thickness(9, 4, 9, 5);
        _readout.Child = _readoutText;
        _readout.Visibility = Visibility.Collapsed;
        _readout.IsHitTestVisible = false;
        _canvas.Children.Add(_readout);

        // The hint: a small dark chip with key caps.
        var hintRow = new StackPanel { Orientation = Orientation.Horizontal };
        void Say(string text) => hintRow.Children.Add(new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(Soft), FontFamily = Ui, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        void Cap(string cap) => hintRow.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(12, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = cap, Foreground = new SolidColorBrush(Ink), FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = Ui },
        });

        hintRow.Children.Add(new TextBlock
        {
            Text = "Drag to select an area", Foreground = new SolidColorBrush(Ink), FontFamily = Ui, FontSize = 12.5,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        if (_preset.HasValue) { Cap("Space"); Say("last area"); }
        Cap("Tab"); Say("clips");
        Cap("Esc"); Say("cancel");

        var hintBox = _hintBox = Chip(hintRow, new Thickness(14, 8, 13, 8), 10);
        hintBox.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35,
        };
        if (!_look.PickerHint) hintBox.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(hintBox);

        // Centre the hint on whichever monitor the pointer is on, not on the
        // middle of a three-monitor desktop where nobody is looking.
        Loaded += (_, _) => { PlaceHint(); DrawDim(null); };
    }

    private static Brush GuideFade(Point from, Point to)
    {
        var b = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = from, EndPoint = to };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.5));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        b.Freeze();
        return b;
    }

    private void PlaceGuides(Point p)
    {
        const double reach = 1100;   // long, fading out only far from the pointer
        _guideH.Width = ActualWidth;
        _guideV.Height = ActualHeight;
        _guideH.Fill = GuideFade(new Point(p.X - reach, 0), new Point(p.X + reach, 0));
        _guideV.Fill = GuideFade(new Point(0, p.Y - reach), new Point(0, p.Y + reach));
        Canvas.SetLeft(_guideH, 0);
        Canvas.SetTop(_guideH, Math.Round(p.Y));
        Canvas.SetLeft(_guideV, Math.Round(p.X));
        Canvas.SetTop(_guideV, 0);
    }

    private void DrawCorners(Rect r)
    {
        double len = Math.Min(14, Math.Min(r.Width, r.Height) / 3);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            void L(Point a, Point m, Point b)
            {
                c.BeginFigure(a, false, false);
                c.LineTo(m, true, true);
                c.LineTo(b, true, true);
            }
            L(new(r.Left, r.Top + len), new(r.Left, r.Top), new(r.Left + len, r.Top));
            L(new(r.Right - len, r.Top), new(r.Right, r.Top), new(r.Right, r.Top + len));
            L(new(r.Right, r.Bottom - len), new(r.Right, r.Bottom), new(r.Right - len, r.Bottom));
            L(new(r.Left + len, r.Bottom), new(r.Left, r.Bottom), new(r.Left, r.Bottom - len));
        }
        g.Freeze();
        _corners.Data = g;
    }

    /// <summary>Common shapes, named when the selection matches one closely.</summary>
    private static string? ShapeName(double w, double h)
    {
        if (w < 1 || h < 1) return null;
        double r = w / h;
        (string name, double ratio)[] known = { ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:3", 4.0 / 3), ("1:1", 1), ("21:9", 21.0 / 9), ("3:2", 1.5) };
        foreach (var (name, ratio) in known)
            if (Math.Abs(r - ratio) / ratio < 0.012) return name;
        return null;
    }

    /* ------------------------------------------------------------------ */

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _dragging = true;
        _outline.Visibility = Visibility.Visible;
        _outlineShade.Visibility = Visibility.Visible;
        _corners.Visibility = Visibility.Visible;
        _readout.Visibility = _look.PickerReadout ? Visibility.Visible : Visibility.Collapsed;
        _thirds.Visibility = _look.PickerThirds ? Visibility.Visible : Visibility.Collapsed;
        _guideH.Visibility = _guideV.Visibility = Visibility.Collapsed;
        _hintBox?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        _dimmer.Fill = new SolidColorBrush(_dimColour);
        _dimmer.Fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_dimDeep, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var now = e.GetPosition(_canvas);
        if (!_dragging) { PlaceGuides(now); return; }
        var r = FromPoints(_start, now);

        Canvas.SetLeft(_outline, r.X);
        Canvas.SetTop(_outline, r.Y);
        _outline.Width = r.Width;
        _outline.Height = r.Height;
        Canvas.SetLeft(_outlineShade, r.X - 1);
        Canvas.SetTop(_outlineShade, r.Y - 1);
        _outlineShade.Width = r.Width + 2;
        _outlineShade.Height = r.Height + 2;

        DrawDim(r);
        DrawCorners(r);
        if (_look.PickerThirds)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                for (int i = 1; i <= 2; i++)
                {
                    double x = r.X + r.Width * i / 3, y = r.Y + r.Height * i / 3;
                    c.BeginFigure(new Point(x, r.Y), false, false); c.LineTo(new Point(x, r.Bottom), true, false);
                    c.BeginFigure(new Point(r.X, y), false, false); c.LineTo(new Point(r.Right, y), true, false);
                }
            }
            g.Freeze();
            _thirds.Data = g;
        }

        // Real pixels, not the scaled units the window works in.
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Round(r.Width * dpi.DpiScaleX), ph = (int)Math.Round(r.Height * dpi.DpiScaleY);
        _readoutText.Inlines.Clear();
        _readoutText.Inlines.Add(new Run($"{pw} × {ph}"));
        if (ShapeName(pw, ph) is string shape)
            _readoutText.Inlines.Add(new Run("   " + shape)
            {
                Foreground = new SolidColorBrush(Soft), FontWeight = FontWeights.Normal,
            });
        _readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = _readout.DesiredSize;

        // Centred just below the selection; inside it if the selection hugs
        // the bottom of the screen.
        double rx = r.X + (r.Width - sz.Width) / 2;
        double ry = r.Bottom + 10;
        if (ry + sz.Height > ActualHeight - 4) ry = r.Bottom - sz.Height - 10;
        rx = Math.Clamp(rx, 4, Math.Max(4, ActualWidth - sz.Width - 4));
        Canvas.SetLeft(_readout, rx);
        Canvas.SetTop(_readout, ry);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(_canvas);
        var r = FromPoints(_start, end);

        if (r.Width < 16 || r.Height < 16)
        {
            // Treat a stray click as "I changed my mind", not a 3-pixel clip.
            Result = null;
            DialogResult = false;
            Close();
            return;
        }

        Result = ToScreen(r);
        DialogResult = true;
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Space && _preset.HasValue)
        {
            Result = _preset;
            DialogResult = true;
            Close();
        }
    }

    /* ------------------------------------------------------------------ */

    /// <summary>Centres the hint on whichever monitor the pointer is on.</summary>
    private void PlaceHint()
    {
        if (_hintBox == null) return;
        _hintBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = _hintBox.DesiredSize;
        var active = ActiveScreenBounds();
        double s = PixelPlacement.Scale(this);

        Canvas.SetLeft(_hintBox, (active.X - _virtualBounds.X + active.Width / 2.0) / s - sz.Width / 2);
        // Near the bottom of the screen, out of the way of where an area usually starts.
        Canvas.SetTop(_hintBox, (active.Y - _virtualBounds.Y + active.Height * 0.86) / s - sz.Height / 2);
    }

    private void DrawDim(Rect? hole)
    {
        _hole = hole;
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (hole == null || hole.Value.Width < 1)
        {
            _dimmer.Data = full;
            return;
        }
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(full);
        group.Children.Add(new RectangleGeometry(hole.Value));
        _dimmer.Data = group;
    }

    private static Rect FromPoints(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>
    /// WPF works in device-independent units. Capture works in real pixels.
    /// On a scaled display those differ, and skipping this conversion is why
    /// naive region capture grabs the wrong area on high-DPI setups.
    /// </summary>
    private Rectangle ToScreen(Rect r)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;

        return new Rectangle(
            _virtualBounds.X + (int)Math.Round(r.X * sx),
            _virtualBounds.Y + (int)Math.Round(r.Y * sy),
            (int)Math.Round(r.Width * sx),
            (int)Math.Round(r.Height * sy));
    }

    private static Rectangle VirtualDesktop()
    {
        var all = Screen.AllScreens.Select(s => s.Bounds).ToArray();
        int x = all.Min(b => b.Left);
        int y = all.Min(b => b.Top);
        int r = all.Max(b => b.Right);
        int bm = all.Max(b => b.Bottom);
        return new Rectangle(x, y, r - x, bm - y);
    }

    private static Rectangle ActiveScreenBounds()
    {
        try
        {
            Native.GetCursorPos(out var p);
            foreach (var s in Screen.AllScreens)
                if (s.Bounds.Contains(p.X, p.Y)) return s.Bounds;
        }
        catch { }
        return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>
    /// Lets the compositor blend this window: a clear background plus the
    /// "glass" frame stretched over the whole window.
    /// </summary>
    /* ---- staying out of the way ------------------------------------------ */

    /// <summary>
    /// Not activated by clicks, and marked as a hair see-through (99.6%, not
    /// visible), so Windows and browsers know nothing behind it is hidden:
    /// covered browser windows otherwise pause their videos.
    /// </summary>
    private static void StayInBackground(IntPtr hwnd)
    {
        try
        {
            const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x00080000;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, 254, 0x2 /* LWA_ALPHA */);
        }
        catch (Exception ex) { Log.Error("picker background", ex); }
    }

    // Without focus, the picker's keys (Esc, Space, Tab) are caught directly
    // while it's open, and kept from the app behind (Esc won't also open a
    // game's menu). Every other key passes through untouched.
    private IntPtr _hook;
    private LowLevelKeyboardProc? _hookProc;

    private void HookKeys()
    {
        _hookProc = (code, wParam, lParam) =>
        {
            if (code >= 0 && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104))   // key down
            {
                int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                Key? key = vk switch { 0x1B => Key.Escape, 0x20 => Key.Space, 0x09 => Key.Tab, _ => null };
                if (key is Key k && (k != Key.Space || _preset.HasValue))
                {
                    Dispatcher.BeginInvoke(new Action(() => OnHookedKey(k)));
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        };
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, GetModuleHandle(null), 0);
        Closed += (_, _) => { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } };
    }

    private void OnHookedKey(Key k)
    {
        if (!IsVisible) return;
        if (k == Key.Tab)
        {
            OpenClipsRequested = true;
            Result = null;
            DialogResult = false;
            Close();
        }
        else if (k == Key.Escape)
        {
            Result = null;
            DialogResult = false;
            Close();
        }
        else if (k == Key.Space && _preset.HasValue)
        {
            Result = _preset;
            DialogResult = true;
            Close();
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint threadId);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    private static void GpuTransparency(IntPtr hwnd)
    {
        try
        {
            var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
        catch (Exception ex) { Log.Error("picker transparency", ex); }
    }

    private static Color ParseColour(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.White; }
    }
}
