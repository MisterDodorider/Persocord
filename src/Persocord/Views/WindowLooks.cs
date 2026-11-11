using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Each window's own look, from the Customization page: Windows 11 glass,
/// the shared background (gradient or picture, dimmed, blurred, with an
/// accent glow), how see-through the whole window is, the UI scale, the
/// title bar, and text that stays readable over glass. Applied as every
/// window loads and again whenever the look changes.
///
/// A window is drawn in layers under its own content:
///   picture or gradient → accent glow → tint over the glass → fine grain →
///   prism sheen → [own title bar + the window's content] → bright rim.
///
/// What makes glass read as glass (and not as a washed-out window), and what
/// each layer is for:
///  - the blur itself is Windows' (Acrylic or Mica), and runs right up to the
///    top edge: Persocord draws its own title bar over it instead of Windows'
///    solid one, with its own minimise, maximise and close buttons (Windows'
///    own ones would sit under the glass layers, out of sight);
///  - a light or dark material to match the tint (frosted glass uses Windows'
///    light material, so it's milky rather than grey);
///  - a thin veil of colour, not a solid one, on panels and cards;
///  - bright, thin edges, strongest along the top-left where light would catch
///    them, and a soft highlight across the top of each card;
///  - a fine grain, as real frosted glass has (it also keeps soft gradients
///    from banding);
///  - text that picks dark or light by itself to read on the glass.
/// </summary>
public static class WindowLooks
{
    /// <summary>Glass (Mica and Acrylic) needs Windows 11 22H2 or later.</summary>
    public static bool GlassSupported { get; } = Environment.OSVersion.Version.Build >= 22621;

    /// <summary>See-through with a blur works from Windows 10 (April 2018) on.</summary>
    public static bool BlurSupported { get; } = Environment.OSVersion.Version.Build >= 17134;

    /// <summary>What's behind the window's content.</summary>
    private enum Material { Solid, Acrylic, Mica, MicaAlt, Blur, OldAcrylic }

    /// <summary>From here up, see-through uses the lighter blur (more of what's behind shows).</summary>
    private const int LightBlurFrom = 40;

    private const string HostTag = "Persocord.LookHost";
    private const double TitleHeight = 34;

    public static void Apply(Window? w)
    {
        if (w == null) return;
        LogoArt.ApplyTo(w);
        try
        {
            var l = LookStore.Current;
            var wl = For(w, l);
            var hwnd = new WindowInteropHelper(w).Handle;

            // Overlays (the area picker, the frame, click rings, the countdown,
            // the control bar…) are left exactly as they are. Restyling them
            // gave the full-screen area picker a solid background, which
            // blacked out the whole screen.
            if (wl == null) return;
            int see = IsShowingVideo(w) ? 0 : Math.Clamp(wl.SeeThroughPercent, 0, 100);
            if (w.AllowsTransparency)
            {
                // Dialog cards draw their own see-through edges: no blur there, just fainter.
                w.Opacity = 1 - see / 100.0 * 0.45;
                return;
            }
            if (hwnd == IntPtr.Zero) return;

            var material = MaterialFor(wl, see);
            bool glass = material != Material.Solid;
            // (wl is known to be there from here on: overlays returned above.)
            bool art = wl.ShowBackground && l.Background != BackgroundFill.Solid &&
                       (l.Background == BackgroundFill.Gradient || File.Exists(l.ImagePath));
            bool glow = wl.ShowBackground && l.AccentGlow;
            bool lightMaterial = glass && LightMaterial(wl);
            // Persocord's own title bar on its main windows, always, glass or solid.
            bool ownTitle = w is LibraryWindow or SettingsWindow or StudioWindow or SetupWindow;

            TitleBar(hwnd, glass, glass ? lightMaterial : ThemeApplier.Current.Light);
            Backdrop(w, hwnd, material);
            Chrome(w, hwnd, ownTitle);
            Layers(w, l, wl, glass, art, glow, ownTitle, see, material is Material.Blur or Material.OldAcrylic);
            Text(w, l, wl, glass, lightMaterial);
            // The old way of making a window see-through (fading the whole
            // window) did nothing on most PCs with glass; it's undone here for
            // windows that still have it.
            Opacity(hwnd, 100);
        }
        catch (Exception ex) { Log.Error("window look", ex); }
    }

    /// <summary>Which of the four window looks a window follows (null: none, e.g. overlays).</summary>
    private static WindowLook? For(Window w, Looks l) => w switch
    {
        LibraryWindow => l.ClipList,
        SettingsWindow => l.SettingsWindow,
        StudioWindow => l.Editor,
        RecordingBar or RegionSelector or Toast => null,
        // Anything borderless that isn't a dialog card is an overlay.
        _ when w.WindowStyle == WindowStyle.None && w is not Dialog => null,
        _ when w.Title == "" && !w.ShowInTaskbar && w.AllowsTransparency => null,
        _ => l.Popups,
    };

    /// <summary>
    /// Glass, if it's on, and see-through: 0% is the glass as chosen (or a
    /// solid window); higher swaps Mica (which only shows the wallpaper) for a
    /// blur of what's really behind, and from 40% up for Windows' lighter blur,
    /// so more and more shows through. The colour over it thins out as it goes
    /// (see <see cref="Layers"/>).
    /// </summary>
    private static Material MaterialFor(WindowLook wl, int see)
    {
        if (see <= 0)
        {
            if (!wl.Glass || !GlassSupported) return Material.Solid;
            return wl.GlassKind switch { GlassKind.Mica => Material.Mica, GlassKind.MicaAlt => Material.MicaAlt, _ => Material.Acrylic };
        }
        if (!BlurSupported) return Material.Solid;
        if (see >= LightBlurFrom) return Material.Blur;
        return GlassSupported ? Material.Acrylic : Material.OldAcrylic;
    }

    /// <summary>Glass that's light (milky) rather than dark (smoky).</summary>
    private static bool LightMaterial(WindowLook wl) => wl.Tint switch
    {
        GlassTint.Frost => true,
        GlassTint.Accent => ThemeApplier.Luminance(ThemeApplier.Accent) > 0.45,
        _ => ThemeApplier.Current.Light,
    };

    /* ---- the clip player hides see-through (video can't be drawn into it) ---- */

    // Weak: a window closed while showing video is never held on to.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Video = new();
    private static readonly object Showing = new();

    /// <summary>
    /// While a window shows video, it isn't see-through (its glass, if on,
    /// stays): the video player is kept away from Windows' blur-behind.
    /// </summary>
    public static void SetShowingVideo(Window w, bool showing)
    {
        if (showing) Video.AddOrUpdate(w, Showing); else Video.Remove(w);
        Apply(w);
    }

    private static bool IsShowingVideo(Window w) => Video.TryGetValue(w, out _);

    /* ---- Windows' title bar and frame --------------------------------------------- */

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    private const int UseImmersiveDarkMode = 20, BorderColour = 34, CaptionColour = 35, TextColour = 36, SystemBackdrop = 38;
    /// <summary>Paint nothing: lets the glass show (DEFAULT would paint Windows' accent colour, if it's set for title bars).</summary>
    private const int NoColour = unchecked((int)0xFFFFFFFE);

    private static int Bgr(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <param name="light">Light title bar and glass material (dark buttons on it), or dark.</param>
    private static void TitleBar(IntPtr hwnd, bool glass, bool light)
    {
        var p = ThemeApplier.Current;
        int dark = light ? 0 : 1;
        // Over glass the title bar takes the glass too; otherwise it's the window's colour.
        int caption = glass ? NoColour : Bgr(p.Bezel);
        int border = glass ? NoColour : Bgr(p.Line);
        int text = Bgr(p.Ink);
        DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, CaptionColour, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, BorderColour, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, TextColour, ref text, sizeof(int));
    }

    /// <summary>
    /// Persocord's own title bar over glass: the window's content (and the
    /// glass) runs to the top edge. Persocord draws minimise, maximise and
    /// close itself; dragging, snapping and double-click to maximise all work.
    /// </summary>
    private static void Chrome(Window w, IntPtr hwnd, bool on)
    {
        var current = WindowChrome.GetWindowChrome(w);
        // Windows draws its own minimise, maximise and close wherever the glass
        // reaches the top edge; over see-through glass they showed through as
        // a second, ghostly set behind Persocord's. Without a window menu,
        // Windows draws none (and Persocord's buttons do the job).
        WindowsButtons(w, hwnd, show: !on);
        if (!on)
        {
            if (current != null) w.ClearValue(WindowChrome.WindowChromeProperty);
            return;
        }
        // Full screen (the clip player) has no title bar to drag by.
        double caption = w.WindowStyle == WindowStyle.None ? 0 : TitleHeight;
        if (current != null && Math.Abs(current.CaptionHeight - caption) < 0.5 && !current.UseAeroCaptionButtons) return;
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = caption,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,   // ours are drawn in the title row
            CornerRadius = new CornerRadius(0),
        });
    }

    private const long WindowMenu = 0x00080000;   // WS_SYSMENU
    private const int StyleIndex = -16, StyleChanging = 0x007C;

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleChange { public uint Old, New; }

    private static readonly DependencyProperty HideButtonsProperty =
        DependencyProperty.RegisterAttached("HideButtons", typeof(bool), typeof(WindowLooks), new PropertyMetadata(false));
    private static readonly HashSet<IntPtr> Watched = new();

    /// <summary>
    /// Windows' own caption buttons (with the window menu) on or off. While
    /// off, any later style change WPF makes (full screen and back, for one)
    /// keeps them off, and Alt+F4 still closes the window.
    /// </summary>
    private static void WindowsButtons(Window w, IntPtr hwnd, bool show)
    {
        // Only ever puts back what was taken away (some windows have no window menu of their own).
        if (show && !(bool)w.GetValue(HideButtonsProperty)) return;
        w.SetValue(HideButtonsProperty, !show);
        if (!show && Watched.Add(hwnd))
        {
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == StyleChanging && (int)wp.ToInt64() == StyleIndex && (bool)w.GetValue(HideButtonsProperty))
                {
                    var change = Marshal.PtrToStructure<StyleChange>(lp);
                    change.New &= ~(uint)WindowMenu;
                    Marshal.StructureToPtr(change, lp, false);
                }
                return IntPtr.Zero;
            });
            w.PreviewKeyDown += (_, e) =>
            {
                if ((bool)w.GetValue(HideButtonsProperty) && e.Key == System.Windows.Input.Key.System && e.SystemKey == System.Windows.Input.Key.F4)
                {
                    e.Handled = true;
                    SystemCommands.CloseWindow(w);
                }
            };
            w.Closed += (_, _) => Watched.Remove(hwnd);
        }
        long style = GetWindowLongPtr(hwnd, StyleIndex).ToInt64();
        long want = show ? style | WindowMenu : style & ~WindowMenu;
        if (want == style) return;
        SetWindowLongPtr(hwnd, StyleIndex, new IntPtr(want));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, Refresh);
    }

    /* ---- glass ------------------------------------------------------------------ */

    private static void Backdrop(Window w, IntPtr hwnd, Material material)
    {
        var source = HwndSource.FromHwnd(hwnd);
        if (material != Material.Solid)
        {
            // WPF paints a solid colour behind everything unless told not to.
            if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref all);
            int type = material switch { Material.Mica => 2, Material.MicaAlt => 4, Material.Acrylic => 3, _ => 1 };
            if (GlassSupported) DwmSetWindowAttribute(hwnd, SystemBackdrop, ref type, sizeof(int));
            // The lighter blur (and acrylic before Windows 11 22H2) is Windows' older
            // blur-behind; it's switched off whenever the newer glass is used.
            Accent(hwnd, material switch { Material.Blur => AccentBlur, Material.OldAcrylic => AccentAcrylic, _ => AccentOff });
            w.Background = Brushes.Transparent;
        }
        else
        {
            if (!IsGlassOn(w)) { w.SetResourceReference(Control.BackgroundProperty, "Bezel"); return; }
            Accent(hwnd, AccentOff);
            int none = 1;
            if (GlassSupported) DwmSetWindowAttribute(hwnd, SystemBackdrop, ref none, sizeof(int));
            var zero = new Margins();
            DwmExtendFrameIntoClientArea(hwnd, ref zero);
            if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = ThemeApplier.Current.Bezel;
            // Back to the theme's background. (Clearing it instead dropped the
            // window's own setting, leaving Windows' default white.)
            w.SetResourceReference(Control.BackgroundProperty, "Bezel");
        }
        SetGlassOn(w, material != Material.Solid);
    }

    /* ---- Windows' blur-behind (the lighter blur) ---------------------------------- */

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State; public int Flags; public uint Colour; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);

    private const int AccentOff = 0, AccentBlur = 3, AccentAcrylic = 4, AccentPolicyAttribute = 19;

    private static readonly DependencyProperty AccentProperty =
        DependencyProperty.RegisterAttached("Accent", typeof(int), typeof(WindowLooks), new PropertyMetadata(AccentOff));

    private static void Accent(IntPtr hwnd, int state)
    {
        if (!BlurSupported) return;
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is not Window w) return;
        if ((int)w.GetValue(AccentProperty) == state && state == AccentOff) return;
        var policy = new AccentPolicy
        {
            State = state,
            // Acrylic needs a colour; ours is laid over it, so it's barely there.
            Colour = state == AccentAcrylic ? 0x10000000u | (uint)Bgr(ThemeApplier.Current.Bezel) : 0,
        };
        int size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new CompositionData { Attribute = AccentPolicyAttribute, Data = ptr, Size = size };
            SetWindowCompositionAttribute(hwnd, ref data);
            w.SetValue(AccentProperty, state);
        }
        catch (Exception ex) { Log.Error("window blur", ex); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static readonly DependencyProperty GlassOnProperty =
        DependencyProperty.RegisterAttached("GlassOn", typeof(bool), typeof(WindowLooks), new PropertyMetadata(false));
    private static bool IsGlassOn(Window w) => (bool)w.GetValue(GlassOnProperty);
    private static void SetGlassOn(Window w, bool on) => w.SetValue(GlassOnProperty, on);

    /* ---- see-through ------------------------------------------------------------- */

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    private const int ExStyle = -20;
    private const long Layered = 0x00080000;
    private const uint UseAlpha = 0x2;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    // No move, no size, no z-order, no activation; re-read the frame.
    private const uint Refresh = 0x0002 | 0x0001 | 0x0004 | 0x0010 | 0x0020;
    // Invalidate, erase, all children, now.
    private const uint RedrawAll = 0x0001 | 0x0004 | 0x0080 | 0x0100;

    /// <summary>
    /// How solid the whole window is (100: solid; lower lets the windows and
    /// desktop behind show through everything, text included). Windows only
    /// takes a style change like this once the frame is refreshed and the
    /// window redrawn, which is what made it only work some of the time.
    /// </summary>
    private static void Opacity(IntPtr hwnd, int percent)
    {
        long ex = GetWindowLongPtr(hwnd, ExStyle).ToInt64();
        bool was = (ex & Layered) != 0;
        if (percent >= 100)
        {
            if (!was) return;
            SetWindowLongPtr(hwnd, ExStyle, new IntPtr(ex & ~Layered));
        }
        else
        {
            if (!was) SetWindowLongPtr(hwnd, ExStyle, new IntPtr(ex | Layered));
            SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(Math.Clamp(percent, 30, 100) * 2.55), UseAlpha);
            if (was) return;
        }
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, Refresh);
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RedrawAll);
    }

    /* ---- readable text over glass -------------------------------------------------- */

    private static readonly string[] TextKeys = { "Ink", "Body", "ButtonInk", "Muted", "Faint", "AccentText" };

    /// <summary>
    /// Over glass, text is set for the glass, not the theme: dark on milky
    /// glass, light on smoky glass (or fixed, if chosen under Advanced).
    /// </summary>
    private static void Text(Window w, Looks l, WindowLook? wl, bool glass, bool lightMaterial)
    {
        var r = w.Resources;
        foreach (var k in TextKeys) r.Remove(k);
        if (!glass || wl == null) return;

        Color ink, body, muted, faint;
        bool dark = l.GlassText switch
        {
            GlassText.Dark => true,
            GlassText.Light => false,
            GlassText.Custom => ThemeApplier.Luminance(ThemeApplier.Parse(l.GlassTextColor, Colors.White)) < 0.4,
            _ => lightMaterial,
        };
        if (l.GlassText == GlassText.Custom)
        {
            ink = ThemeApplier.Parse(l.GlassTextColor, Colors.White);
            var away = dark ? Colors.White : Colors.Black;
            body = ThemeApplier.Mix(ink, away, 0.12);
            muted = ThemeApplier.Mix(ink, away, 0.32);
            faint = ThemeApplier.Mix(ink, away, 0.5);
        }
        else if (dark)
            (ink, body, muted, faint) = (C(0x14161A), C(0x2A2E35), C(0x4A515C), C(0x737A85));
        else
            (ink, body, muted, faint) = (C(0xF6F7F9), C(0xE1E4E8), C(0xB4BAC3), C(0x8C929B));

        Local(r, "Ink", ink);
        Local(r, "Body", body);
        Local(r, "ButtonInk", body);
        Local(r, "Muted", muted);
        Local(r, "Faint", faint);
        // Accent-coloured text, readable on this glass too.
        if (l.AccentText == AccentTextMode.Auto)
        {
            var glassBg = dark ? C(0xF2F3F5) : C(0x1A1C20);
            Local(r, "AccentText", ThemeApplier.Readable(ThemeApplier.Accent, glassBg, glassBg));
        }
    }

    private static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /* ---- background layers, panels, scale -------------------------------------------- */

    private sealed class LookHost
    {
        public Grid Root = null!;
        public Rectangle Art = null!, Glow = null!, Tint = null!, Grain = null!, Prism = null!;
        public Border Title = null!, Rim = null!;
        public Border Minimise = null!, Maximise = null!, Close = null!;
        public TextBlock MaximiseGlyph = null!;
        public FrameworkElement Content = null!;
        public Grid Frame = null!;
    }

    private static readonly DependencyProperty HostProperty =
        DependencyProperty.RegisterAttached("Host", typeof(LookHost), typeof(WindowLooks), new PropertyMetadata(null));

    private static void Layers(Window w, Looks l, WindowLook wl, bool glass, bool art, bool glow, bool ownTitle, int see, bool plainBlur)
    {
        // See-through thins the colour over the glass, and the panels, as it goes up:
        // at 100% only a whisper of colour is left, so what's behind shows clearly.
        double thin = 1 - 0.85 * see / 100.0;
        double panels = 1 - 0.55 * see / 100.0;
        var p = ThemeApplier.Current;
        var h = Host(w);
        if (h == null) return;

        // UI scale (the background stays the size of the window).
        double s = l.UiScalePercent / 100.0;
        h.Content.LayoutTransform = Math.Abs(s - 1) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);

        // Persocord's own title bar, and the room a maximised window needs.
        bool title = ownTitle && w.WindowStyle != WindowStyle.None;
        h.Title.Visibility = title ? Visibility.Visible : Visibility.Collapsed;
        bool max = w.WindowState == WindowState.Maximized;
        h.MaximiseGlyph.Text = max ? "\uE923" : "\uE922";
        h.Maximise.ToolTip = max ? "Restore down" : "Maximise";
        h.Minimise.Visibility = w.ResizeMode == ResizeMode.NoResize ? Visibility.Collapsed : Visibility.Visible;
        h.Maximise.Visibility = w.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize ? Visibility.Collapsed : Visibility.Visible;
        h.Root.Margin = ownTitle && w.WindowState == WindowState.Maximized && w.WindowStyle != WindowStyle.None
            ? new Thickness(8) : new Thickness(0);

        // The picture or gradient.
        h.Art.Fill = null;
        h.Art.Effect = null;
        if (art)
        {
            if (l.Background == BackgroundFill.Gradient)
                h.Art.Fill = new LinearGradientBrush(ThemeApplier.Parse(l.GradientFrom, p.Bezel), ThemeApplier.Parse(l.GradientTo, p.Panel), l.GradientAngle);
            else if (Picture(l.ImagePath) is { } img)
            {
                var brush = new ImageBrush(img)
                {
                    Stretch = l.ImageFit switch { ImageFit.Fit => Stretch.Uniform, ImageFit.Stretch => Stretch.Fill, ImageFit.Tile => Stretch.None, _ => Stretch.UniformToFill },
                };
                if (l.ImageFit == ImageFit.Tile)
                {
                    brush.TileMode = TileMode.Tile;
                    brush.ViewportUnits = BrushMappingMode.Absolute;
                    brush.Viewport = new Rect(0, 0, img.PixelWidth, img.PixelHeight);
                }
                brush.Freeze();
                h.Art.Fill = brush;
                if (l.ImageBlur > 0) h.Art.Effect = new BlurEffect { Radius = l.ImageBlur, KernelType = KernelType.Gaussian };
            }
        }
        // Blur reaches past the edges; a little overscan hides the soft rim.
        h.Art.Margin = h.Art.Effect != null ? new Thickness(-l.ImageBlur) : new Thickness(0);

        // The accent glow.
        h.Glow.Fill = null;
        if (glow)
        {
            var accent = ThemeApplier.Accent;
            var centre = l.GlowCorner switch
            {
                GlowCorner.TopRight => new Point(1, 0),
                GlowCorner.BottomLeft => new Point(0, 1),
                GlowCorner.BottomRight => new Point(1, 1),
                GlowCorner.Centre => new Point(0.5, 0.5),
                _ => new Point(0, 0),
            };
            var g = new RadialGradientBrush { Center = centre, GradientOrigin = centre, RadiusX = 0.8, RadiusY = 0.9 };
            g.GradientStops.Add(new GradientStop(ThemeApplier.WithAlpha(accent, (byte)(l.GlowPercent * 2.55)), 0));
            g.GradientStops.Add(new GradientStop(ThemeApplier.WithAlpha(accent, 0), 1));
            g.Freeze();
            h.Glow.Fill = g;
        }

        // The colour over the glass, or the dim over a picture.
        var tintBase = glass ? wl.Tint switch
        {
            GlassTint.Frost => Colors.White,
            GlassTint.Accent => ThemeApplier.Accent,
            _ => p.Bezel,
        } : p.Bezel;
        int tintPercent = glass
            ? (int)Math.Round(thin * wl.Tint switch
            {
                GlassTint.Clear => Math.Min(wl.GlassTintPercent, 10),
                GlassTint.Frost => wl.GlassTintPercent * 40 / 100,      // over the light material, a little goes a long way
                GlassTint.Accent => wl.GlassTintPercent * 60 / 100,
                _ => wl.GlassTintPercent,
            })
            : art ? (l.Background == BackgroundFill.Image ? l.ImageDimPercent : 0) : 100;
        // Windows' plain blur has no colour of its own (Acrylic and Mica do): a
        // little always stays over it, so text reads whatever is behind.
        if (glass && plainBlur) tintPercent = Math.Max(tintPercent, LightMaterial(wl) ? 30 : 18);
        bool layered = glass || art;
        if (layered)
        {
            // A gentle top-to-bottom fall-off reads as light on a surface, where a flat tint looks like a filter.
            var t = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            byte top = (byte)Math.Round(Math.Clamp(tintPercent * 0.85, 0, 100) * 2.55);
            byte bottom = (byte)Math.Round(Math.Clamp(tintPercent * 1.1, 0, 100) * 2.55);
            t.GradientStops.Add(new GradientStop(ThemeApplier.WithAlpha(tintBase, top), 0));
            t.GradientStops.Add(new GradientStop(ThemeApplier.WithAlpha(tintBase, bottom), 1));
            t.Freeze();
            h.Tint.Fill = t;
        }
        else h.Tint.Fill = null;

        // A fine grain over the glass.
        h.Grain.Fill = glass && l.GlassGrain ? Grain : null;

        // A soft rainbow sheen across the glass, like light through a prism.
        h.Prism.Fill = null;
        if (glass && l.PrismSheen)
        {
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0.15), EndPoint = new Point(1, 0.85) };
            byte a2 = (byte)Math.Round(l.PrismPercent * 2.55);
            void Stop(uint rgb, double at, bool on) => rainbow.GradientStops.Add(new GradientStop(
                Color.FromArgb(on ? a2 : (byte)0, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), at));
            Stop(0xFF7AB6, 0.00, false); Stop(0xFF7AB6, 0.18, true); Stop(0xFFD36E, 0.26, true); Stop(0x8CF5B5, 0.34, true);
            Stop(0x7FC8FF, 0.42, true); Stop(0xB79CFF, 0.50, true); Stop(0xB79CFF, 0.58, false);
            Stop(0x7FC8FF, 0.70, false); Stop(0x7FC8FF, 0.78, true); Stop(0xFF9AD5, 0.86, true); Stop(0xFF9AD5, 0.94, false);
            rainbow.Freeze();
            h.Prism.Fill = rainbow;
        }

        // A bright rim around the window, strongest top-left.
        bool edges = glass && l.GlassEdges;
        h.Rim.BorderBrush = edges ? Edge(0x70, 0x18, 0x36) : null;
        h.Rim.BorderThickness = new Thickness(edges ? 1 : 0);

        // Over glass or a picture: the window's own colour goes clear, and
        // panels, cards and buttons are thin veils of colour.
        var r = w.Resources;
        string[] keys = { "Bezel", "Panel", "Field", "Hover", "Pressed", "ButtonFill", "CardFill", "CardHoverFill", "CardSelectedFill",
                          "CardEdge", "CardSheen", "ButtonEdge", "ButtonEdgeThickness", "Line", "MenuEdge" };
        foreach (var k in keys) r.Remove(k);
        if (layered)
        {
            byte a = (byte)Math.Round(l.PanelOpacityPercent * 2.55 * panels);
            bool milky = glass && LightMaterial(wl);
            // On milky glass panels are white veils; otherwise the background colours, see-through.
            Color Veil(Color c, int extra) => milky
                ? ThemeApplier.WithAlpha(Colors.White, (byte)Math.Min(255, a * 45 / 100 + extra))
                : ThemeApplier.WithAlpha(c, (byte)Math.Min(255, a + extra));
            Local(r, "Bezel", Colors.Transparent);
            Local(r, "Panel", Veil(p.Panel, 0));
            Local(r, "Field", milky ? ThemeApplier.WithAlpha(Colors.White, (byte)Math.Min(255, a * 60 / 100 + 20)) : Veil(p.Field, 30));
            Local(r, "Hover", milky ? ThemeApplier.WithAlpha(Colors.White, (byte)Math.Min(255, a * 70 / 100 + 30)) : Veil(p.Hover, 20));
            Local(r, "Pressed", milky ? ThemeApplier.WithAlpha(Colors.White, (byte)Math.Min(255, a * 80 / 100 + 45)) : Veil(p.Pressed, 30));
            if (!l.GlassButtons) Local(r, "ButtonFill", Veil(p.ButtonFill, 10));
            if (l.Cards is CardStyle.Filled or CardStyle.Glass)
            {
                Local(r, "CardFill", Veil(p.Panel, 0));
                Local(r, "CardHoverFill", Veil(p.CardHover, 24));
                Local(r, "CardSelectedFill", Veil(p.CardSelected, 38));
            }
            if (milky)
            {
                // Lines on milky glass are soft dark hairlines, not light ones.
                Local(r, "Line", Color.FromArgb(0x1E, 0x10, 0x14, 0x1A));
                Local(r, "MenuEdge", Color.FromArgb(0x26, 0x10, 0x14, 0x1A));
            }
            if (!glass) w.Background = new SolidColorBrush(p.Bezel);

            // Cut-glass edges and a soft highlight across the top of each card.
            if (edges)
            {
                r["CardEdge"] = Edge(0x8C, 0x1C, 0x40);
                r["ButtonEdge"] = Edge(0x7A, 0x18, 0x34);
                r["ButtonEdgeThickness"] = new Thickness(1);
                var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF), 0));
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45));
                sheen.Freeze();
                r["CardSheen"] = sheen;
                if (!milky) Local(r, "Line", Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
            }
        }
    }

    /// <summary>A thin white edge: brightest at the top-left, dimmest mid-way, catching light again at the bottom-right.</summary>
    private static Brush Edge(byte bright, byte dim, byte back)
    {
        var e = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        e.GradientStops.Add(new GradientStop(Color.FromArgb(bright, 0xFF, 0xFF, 0xFF), 0));
        e.GradientStops.Add(new GradientStop(Color.FromArgb(dim, 0xFF, 0xFF, 0xFF), 0.5));
        e.GradientStops.Add(new GradientStop(Color.FromArgb(back, 0xFF, 0xFF, 0xFF), 1));
        e.Freeze();
        return e;
    }

    private static void Local(ResourceDictionary r, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        r[key] = b;
    }

    /// <summary>
    /// Wraps the window's content (once) in the layers. A window that later
    /// replaces its content simply loses the layers until the next look change.
    /// </summary>
    private static LookHost? Host(Window w)
    {
        if (w.GetValue(HostProperty) is LookHost known && ReferenceEquals(w.Content, known.Root)) return known;
        if (w.Content is not FrameworkElement content) return null;

        w.Content = null;
        var h = new LookHost { Content = content };
        h.Art = new Rectangle { IsHitTestVisible = false, ClipToBounds = true };
        h.Glow = new Rectangle { IsHitTestVisible = false };
        h.Tint = new Rectangle { IsHitTestVisible = false };
        h.Grain = new Rectangle { IsHitTestVisible = false };
        h.Prism = new Rectangle { IsHitTestVisible = false };
        h.Rim = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(8) };

        // Persocord's own title bar: the icon and the window's name, over the glass.
        var titleRow = new DockPanel { Height = TitleHeight, LastChildFill = true };
        {
            // The logo in the look's colours (redrawn when they change).
            var img = new Image { Source = LogoArt.Image, Width = 16, Height = 16, Margin = new Thickness(12, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(img, Dock.Left);
            titleRow.Children.Add(img);
            void Redraw() => img.Source = LogoArt.Image;
            LogoArt.Changed += Redraw;
            w.Closed += (_, _) => LogoArt.Changed -= Redraw;
        }
        // Minimise, maximise and close, on the right.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        h.Minimise = CaptionButton("\uE921", "Minimise", false, () => SystemCommands.MinimizeWindow(w), out _);
        h.Maximise = CaptionButton("\uE922", "Maximise", false, () =>
        {
            if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
            else SystemCommands.MaximizeWindow(w);
        }, out h.MaximiseGlyph);
        h.Close = CaptionButton("\uE8BB", "Close", true, () => SystemCommands.CloseWindow(w), out _);
        buttons.Children.Add(h.Minimise);
        buttons.Children.Add(h.Maximise);
        buttons.Children.Add(h.Close);
        titleRow.Children.Add(buttons);

        var name = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = w });
        name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        titleRow.Children.Add(name);
        h.Title = new Border { Child = titleRow, Visibility = Visibility.Collapsed, Background = Brushes.Transparent };

        h.Frame = new Grid();
        h.Frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        h.Frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(h.Title, 0);
        Grid.SetRow(content, 1);
        h.Frame.Children.Add(h.Title);
        h.Frame.Children.Add(content);

        h.Root = new Grid { Tag = HostTag, ClipToBounds = true };
        foreach (var layer in new UIElement[] { h.Art, h.Glow, h.Tint, h.Grain, h.Prism, h.Frame, h.Rim })
            h.Root.Children.Add(layer);
        w.Content = h.Root;
        w.SetValue(HostProperty, h);

        // A maximised window with its own title bar needs a little room at the edges.
        w.StateChanged += (_, _) => Apply(w);
        return h;
    }

    private static readonly FontFamily CaptionFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush CloseRed = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly Brush CloseRedPressed = Frozen(Color.FromRgb(0xA8, 0x25, 0x18));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>
    /// One of the title bar's buttons, drawn like Windows 11's own: a soft
    /// highlight on hover (red for close), clicked on release, and reachable
    /// through the title bar's drag area.
    /// </summary>
    private static Border CaptionButton(string glyph, string tip, bool close, Action click, out TextBlock icon)
    {
        var text = new TextBlock
        {
            Text = glyph, FontFamily = CaptionFont, FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var b = new Border { Width = 46, Height = TitleHeight, Background = Brushes.Transparent, Child = text, ToolTip = tip, Focusable = false };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);

        void Idle()
        {
            b.Background = Brushes.Transparent;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        }
        void Lit(bool pressed)
        {
            if (close)
            {
                b.Background = pressed ? CloseRedPressed : CloseRed;
                text.Foreground = Brushes.White;
            }
            else b.SetResourceReference(Border.BackgroundProperty, pressed ? "Pressed" : "Hover");
        }
        b.MouseEnter += (_, _) => Lit(b.IsMouseCaptured);
        b.MouseLeave += (_, _) => { if (!b.IsMouseCaptured) Idle(); };
        b.MouseLeftButtonDown += (_, e) => { b.CaptureMouse(); Lit(true); e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            var at = e.GetPosition(b);
            bool inside = b.IsMouseCaptured && at.X >= 0 && at.Y >= 0 && at.X < b.ActualWidth && at.Y < b.ActualHeight;
            b.ReleaseMouseCapture();
            Idle();   // the window moves, shrinks or closes under the pointer
            if (inside) click();
            e.Handled = true;
        };
        b.LostMouseCapture += (_, _) => { if (!b.IsMouseOver) Idle(); };
        icon = text;
        return b;
    }

    /* ---- the grain, the icon and the picture, made once ------------------------------ */

    private static Brush? _grain;

    /// <summary>A fine, barely-there noise, tiled.</summary>
    private static Brush Grain
    {
        get
        {
            if (_grain != null) return _grain;
            const int size = 128;
            var px = new byte[size * size * 4];
            var rnd = new Random(7);
            for (int i = 0; i < size * size; i++)
            {
                byte v = rnd.Next(2) == 0 ? (byte)0 : (byte)255;
                byte a = (byte)rnd.Next(0, 6);    // at most 2% here and there: felt, not seen
                px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = (byte)(v * a / 255);   // premultiplied
                px[i * 4 + 3] = a;
            }
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, px, size * 4);
            bmp.Freeze();
            var b = new ImageBrush(bmp) { TileMode = TileMode.Tile, Stretch = Stretch.None, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size) };
            b.Freeze();
            return _grain = b;
        }
    }

    private static string? _picturePath;
    private static BitmapSource? _picture;

    private static BitmapSource? Picture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path == _picturePath) return _picture;
        _picturePath = path;
        _picture = null;
        try
        {
            // Only big pictures are shrunk (to 2560 across: plenty for a window); a
            // smaller one was blown up to that size, using many times the memory.
            int width = 0;
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            {
                var probe = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (probe.Frames.Count > 0) width = probe.Frames[0].PixelWidth;
            }
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;   // the file isn't kept open
            img.UriSource = new Uri(path);
            if (width > 2560) img.DecodePixelWidth = 2560;
            img.EndInit();
            img.Freeze();
            _picture = img;
        }
        catch (Exception ex) { Log.Error("background picture", ex); }
        return _picture;
    }
}
