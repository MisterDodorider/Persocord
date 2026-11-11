using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace Persocord.Services;

/// <summary>
/// Puts overlay windows at exact physical-pixel positions.
///
/// WPF positions windows in scaled units, while capture, the mouse hook and
/// monitor bounds all speak physical pixels. At 100% scaling on one monitor
/// the two agree and nothing looks wrong. At 125% or 150%, or across monitors
/// with different scaling, they diverge and overlays land in the wrong place.
/// So overlays are positioned with the Win32 call that takes real pixels, and
/// anything drawn inside them converts using that window's own scale.
/// </summary>
public static class PixelPlacement
{
    /// <summary>Pins a window to a rectangle in physical pixels, size included.</summary>
    public static void Pin(Window window, Rectangle bounds)
    {
        window.Topmost = true;
        Keep(window, () => Apply(window, bounds.X, bounds.Y, bounds.Width, bounds.Height, false));
    }

    /// <summary>Moves a window to a physical-pixel position, leaving its size alone.</summary>
    public static void MoveTo(Window window, int x, int y) =>
        Apply(window, x, y, 0, 0, true);

    /// <summary>
    /// Runs the placement once the window has a handle, and again whenever its
    /// DPI changes. Crossing onto a monitor with different scaling makes WPF
    /// resize the window to match, which would undo an exact placement.
    /// </summary>
    public static void Keep(Window window, Action place)
    {
        window.SourceInitialized += (_, _) =>
        {
            place();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == Native.WM_DPICHANGED)
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, place);
                return IntPtr.Zero;
            });
        };
        window.Loaded += (_, _) => place();
    }

    private static void Apply(Window window, int x, int y, int w, int h, bool keepSize)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        uint flags = Native.SWP_NOACTIVATE | (keepSize ? Native.SWP_NOSIZE : 0);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, flags);
    }

    /// <summary>Scaled units to physical pixels, for this window's monitor.</summary>
    public static double Scale(Visual v)
    {
        try { return VisualTreeHelper.GetDpi(v).DpiScaleX; }
        catch { return 1.0; }
    }

    /// <summary>Where the window actually sits, in physical pixels.</summary>
    public static Rectangle ScreenRect(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r))
            return Rectangle.Empty;
        return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}
