using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace Persocord.Services;

/// <summary>
/// Draws an expanding ring wherever the mouse is clicked.
///
/// FFmpeg has no notion of clicks, so the ring is drawn on a transparent
/// click-through window over the capture area and simply gets recorded along
/// with everything else.
///
/// The mouse hook reports physical pixels while WPF draws in scaled units.
/// Converting with this window's own scale is what keeps the ring under the
/// pointer on a scaled display; treating the two as the same is what put it
/// off to one side.
/// </summary>
public class ClickHighlightOverlay : IDisposable
{
    private Window? _window;
    private Canvas? _canvas;
    private IntPtr _hook = IntPtr.Zero;
    private Native.HookProc? _proc;   // kept alive; the GC would otherwise collect it
    private Dispatcher? _dispatcher;
    private Rectangle _bounds;

    private Color _colour = Color.FromRgb(0xD6, 0xB0, 0x6A);
    private int _sizePx = 42;

    public void Start(Rectangle bounds, string hexColour, int size)
    {
        try { _colour = (Color)ColorConverter.ConvertFromString(hexColour); } catch { }
        _sizePx = Math.Clamp(size, 12, 200);
        _bounds = bounds;

        _canvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            IsHitTestVisible = false,
            Content = _canvas,
        };

        PixelPlacement.Pin(_window, bounds);
        _window.SourceInitialized += (_, _) =>
            Native.MakeClickThrough(new WindowInteropHelper(_window).Handle);

        _window.Show();
        _dispatcher = _window.Dispatcher;

        _proc = HookProc;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("click hook failed to install");
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
            {
                try
                {
                    var data = System.Runtime.InteropServices.Marshal
                        .PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    _dispatcher?.BeginInvoke(DispatcherPriority.Render,
                        new Action(() => Ping(data.pt.X, data.pt.Y)));
                }
                catch { }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Ping(int screenX, int screenY)
    {
        if (_window == null || _canvas == null) return;

        // Ignore clicks well outside the recorded area.
        if (screenX < _bounds.Left - _sizePx || screenX > _bounds.Right + _sizePx ||
            screenY < _bounds.Top - _sizePx || screenY > _bounds.Bottom + _sizePx) return;

        double scale = PixelPlacement.Scale(_window);
        double x = (screenX - _bounds.X) / scale;
        double y = (screenY - _bounds.Y) / scale;
        double size = _sizePx / scale;

        var ring = new Ellipse
        {
            Width = size, Height = size,
            Stroke = new SolidColorBrush(_colour),
            StrokeThickness = 2.5 / scale,
            Fill = new SolidColorBrush(Color.FromArgb(38, _colour.R, _colour.G, _colour.B)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false,
        };
        var grow = new ScaleTransform(0.35, 0.35);
        ring.RenderTransform = grow;

        Canvas.SetLeft(ring, x - size / 2.0);
        Canvas.SetTop(ring, y - size / 2.0);
        _canvas.Children.Add(ring);

        var dur = TimeSpan.FromMilliseconds(420);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var expand = new DoubleAnimation(0.35, 1.0, dur) { EasingFunction = ease };
        var fade = new DoubleAnimation(0.95, 0.0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => _canvas?.Children.Remove(ring);

        grow.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
        ring.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            try { Native.UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
        _proc = null;
        try { _window?.Close(); } catch { }
        _window = null;
        _canvas = null;
    }

    public void Dispose() => Stop();
}
