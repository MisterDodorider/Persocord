using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Persocord.Models;

namespace Persocord.Views;

/// <summary>
/// Puts a look (see <see cref="Looks"/>) into the app's theme: colours, light
/// or dark, the accent, text, corners, spacing and the clip list's cards.
/// Everything in the theme is a live reference, so open windows repaint at
/// once. Each window's own glass, background and see-through level are
/// handled by <see cref="WindowLooks"/>.
/// </summary>
public static class ThemeApplier
{
    public record Palette(
        Color Bezel, Color Panel, Color Raised, Color Field, Color Line, Color LineStrong,
        Color Hover, Color Pressed, Color ButtonFill, Color CardHover, Color CardSelected,
        bool Light = false)
    {
        public Color Ink => Light ? C(0x1B1D21) : C(0xE8EAED);
        public Color Body => Light ? C(0x3A3F47) : C(0xC9CDD3);
        public Color Muted => Light ? C(0x5E6570) : C(0x8A909A);
        public Color Faint => Light ? C(0x8E949D) : C(0x5A6069);
        public Color Track => Light ? C(0xD3D7DD) : C(0x2A2F37);
        public Color Thumb => Light ? C(0xC3C8CF) : C(0x2E333B);
        public Color ThumbHover => Light ? C(0xADB3BC) : C(0x454B55);
        public Color ThumbPressed => Light ? C(0x969DA8) : C(0x5A616C);
        public Color CheckEdge => Light ? C(0xB4BAC3) : C(0x3A3F47);
        public Color CheckEdgeHover => Light ? C(0x8E949D) : C(0x565C66);
        public Color MenuEdge => Light ? C(0xD5D9DF) : C(0x30353D);
    }

    /// <summary>The palette for a look: one of the built-in backgrounds, or one made from your own colour.</summary>
    public static Palette For(Looks l, SurfaceStyle style) => style switch
    {
        SurfaceStyle.CustomDark => Derive(Parse(l.CustomDark, C(0x16191F))),
        SurfaceStyle.CustomLight => Derive(Parse(l.CustomLight, C(0xEEF0F3))),
        _ => For(style),
    };

    /// <summary>
    /// A whole palette from one colour: panels, menus, fields, lines and hover
    /// states in close steps around it. Light or dark text follows from how
    /// light the colour itself is, so any colour reads.
    /// </summary>
    public static Palette Derive(Color b)
    {
        if (Luminance(b) > 0.35)
            return new Palette(b, Mix(b, Colors.White, 0.6), Mix(b, Colors.White, 0.75), Mix(b, Colors.Black, 0.05),
                Mix(b, Colors.Black, 0.10), Mix(b, Colors.Black, 0.20), Mix(b, Colors.Black, 0.05), Mix(b, Colors.Black, 0.10),
                Mix(b, Colors.Black, 0.04), Mix(b, Colors.White, 0.35), Mix(b, Colors.White, 0.2), Light: true);
        return new Palette(b, Mix(b, Colors.White, 0.035), Mix(b, Colors.White, 0.07), Mix(b, Colors.Black, 0.3),
            Mix(b, Colors.White, 0.09), Mix(b, Colors.White, 0.15), Mix(b, Colors.White, 0.08), Mix(b, Colors.White, 0.11),
            Mix(b, Colors.White, 0.06), Mix(b, Colors.White, 0.05), Mix(b, Colors.White, 0.065));
    }

    public static Palette For(SurfaceStyle style) => style switch
    {
        SurfaceStyle.OledBlack => new(
            C(0x000000), C(0x0B0C0E), C(0x141619), C(0x000000), C(0x1B1D21), C(0x2A2D33),
            C(0x17191D), C(0x202328), C(0x131518), C(0x101215), C(0x131519)),
        SurfaceStyle.Slate => new(
            C(0x11151C), C(0x171C25), C(0x1E2430), C(0x0B0E13), C(0x252C38), C(0x333C4B),
            C(0x212835), C(0x29313F), C(0x1C222D), C(0x1A202A), C(0x1D2430)),
        SurfaceStyle.Midnight => new(
            C(0x0E1320), C(0x141B2B), C(0x1B2336), C(0x0A0E18), C(0x232C40), C(0x323D56),
            C(0x1F283D), C(0x283249), C(0x19213A), C(0x172034), C(0x1A2338)),
        SurfaceStyle.Forest => new(
            C(0x0F1512), C(0x151D19), C(0x1C2621), C(0x0A0F0C), C(0x23302A), C(0x31423A),
            C(0x1F2A24), C(0x27352D), C(0x1A241F), C(0x18221D), C(0x1B2620)),
        SurfaceStyle.Plum => new(
            C(0x16111A), C(0x1D1622), C(0x251C2B), C(0x100C13), C(0x2E2335), C(0x3F3148),
            C(0x281E2F), C(0x312639), C(0x221A28), C(0x201825), C(0x241B2A)),
        SurfaceStyle.Paper => new(
            C(0xF1F2F4), C(0xFFFFFF), C(0xFFFFFF), C(0xE7E9ED), C(0xDADDE2), C(0xC3C8CF),
            C(0xE6E8EC), C(0xDADDE2), C(0xE9EBEF), C(0xF7F8FA), C(0xEEF1F5), Light: true),
        SurfaceStyle.Snow => new(
            C(0xFAFBFC), C(0xFFFFFF), C(0xFFFFFF), C(0xEEF0F3), C(0xE3E6EA), C(0xCBD0D7),
            C(0xEDEFF2), C(0xE1E4E9), C(0xF0F2F5), C(0xF6F7F9), C(0xEFF2F6), Light: true),
        SurfaceStyle.Sand => new(
            C(0xF3EFE8), C(0xFBF8F3), C(0xFFFDF9), C(0xEAE4DA), C(0xDDD5C8), C(0xC8BDAC),
            C(0xECE5DA), C(0xE2D9CB), C(0xEEE8DE), C(0xF7F2EA), C(0xF1EADF), Light: true),
        _ => new(
            C(0x121417), C(0x191C20), C(0x20242A), C(0x0D0F12), C(0x262A31), C(0x363B44),
            C(0x23272E), C(0x2B3038), C(0x1E2126), C(0x1D2025), C(0x1F2228)),
    };

    public static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Gold", "#D6B06A"), ("Ember", "#E8845A"), ("Rose", "#E0708A"), ("Violet", "#A58BEA"),
        ("Ice", "#6FB4E8"), ("Mint", "#5CCB9E"), ("Lime", "#A9CF5E"), ("Silver", "#C3C9D2"),
        ("Crimson", "#E05260"), ("Cyan", "#4FC8D8"), ("Indigo", "#7C8CF0"), ("Peach", "#F0A782"),
    };

    /// <summary>What the app's windows use now.</summary>
    public static Palette Current { get; private set; } = For(SurfaceStyle.Graphite);

    /// <summary>
    /// For things drawn over the screen while recording (control bar, area
    /// picker, countdown, frame): always the dark style chosen, so they read
    /// over any game, light mode or not.
    /// </summary>
    public static Palette Overlay { get; private set; } = For(SurfaceStyle.Graphite);

    public static Color Accent { get; private set; } = C(0xD6B06A);

    /// <summary>Dark or light text, whichever reads on the accent.</summary>
    public static Color OnAccent => Luminance(Accent) > 0.45 ? C(0x141210) : C(0xF5F6F8);

    public static void Apply(ResourceDictionary theme, Looks l)
    {
        var p = For(l, l.Surface);
        var accent = Parse(l.Accent, C(0xD6B06A));
        Current = p;
        // Overlays stay dark whatever is chosen (a light "custom dark" colour falls back to Graphite).
        var overlay = For(l, l.DarkSurface);
        Overlay = overlay.Light ? For(SurfaceStyle.Graphite) : overlay;
        Accent = accent;

        Put(theme, "Bezel", p.Bezel);
        Put(theme, "Panel", p.Panel);
        Put(theme, "Raised", p.Raised);
        Put(theme, "Field", p.Field);
        Put(theme, "Line", p.Line);
        Put(theme, "LineStrong", p.LineStrong);
        Put(theme, "Hover", p.Hover);
        Put(theme, "Pressed", p.Pressed);
        Put(theme, "CardHover", p.CardHover);
        Put(theme, "CardSelected", p.CardSelected);
        Put(theme, "Ink", p.Ink);
        Put(theme, "Body", p.Body);
        Put(theme, "ButtonInk", p.Body);
        Put(theme, "Muted", p.Muted);
        Put(theme, "Faint", p.Faint);
        Put(theme, "Track", p.Track);
        Put(theme, "ScrollThumbFill", p.Thumb);
        Put(theme, "ScrollThumbHover", p.ThumbHover);
        Put(theme, "ScrollThumbPressed", p.ThumbPressed);
        Put(theme, "CheckEdge", p.CheckEdge);
        Put(theme, "CheckEdgeHover", p.CheckEdgeHover);
        Put(theme, "MenuEdge", p.MenuEdge);

        // The accent and its tints.
        Put(theme, "Brass", accent);
        Put(theme, "BrassHover", Mix(accent, Colors.White, 0.14));
        Put(theme, "BrassPressed", Mix(accent, Colors.Black, 0.12));
        Put(theme, "BrassSoft", WithAlpha(accent, 0x1F));
        Put(theme, "BrassSofter", WithAlpha(accent, 0x12));
        Put(theme, "BrassPress", WithAlpha(accent, 0x2E));
        Put(theme, "BrassLine", WithAlpha(accent, 0x33));
        Put(theme, "BrassLineHover", WithAlpha(accent, 0x66));
        Put(theme, "OnBrass", OnAccent);
        // Accent-coloured text: readable on this background whatever the accent.
        var accentText = l.AccentText switch
        {
            AccentTextMode.Exact => accent,
            AccentTextMode.Custom => Parse(l.AccentTextColor, accent),
            _ => Readable(accent, p.Panel, p.Bezel),
        };
        Put(theme, "AccentText", accentText);

        // Main buttons: the accent, or a fade into a second colour.
        if (l.AccentGradient)
        {
            var second = Parse(l.Accent2, accent);
            theme["PrimaryFill"] = Gradient(accent, second, 0);
            theme["PrimaryHover"] = Gradient(Mix(accent, Colors.White, 0.14), Mix(second, Colors.White, 0.14), 0);
            theme["PrimaryPressed"] = Gradient(Mix(accent, Colors.Black, 0.12), Mix(second, Colors.Black, 0.12), 0);
        }
        else
        {
            Put(theme, "PrimaryFill", accent);
            Put(theme, "PrimaryHover", Mix(accent, Colors.White, 0.14));
            Put(theme, "PrimaryPressed", Mix(accent, Colors.Black, 0.12));
        }

        // Plain buttons: solid, or glassy with a soft shine along the top.
        if (l.GlassButtons)
        {
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            var top = p.Light ? Mix(p.ButtonFill, Colors.White, 0.7) : Mix(p.ButtonFill, Colors.White, 0.10);
            sheen.GradientStops.Add(new GradientStop(WithAlpha(top, 0xD8), 0));
            sheen.GradientStops.Add(new GradientStop(WithAlpha(p.ButtonFill, 0xB0), 0.55));
            sheen.GradientStops.Add(new GradientStop(WithAlpha(p.ButtonFill, 0xC8), 1));
            sheen.Freeze();
            theme["ButtonFill"] = sheen;
            theme["ButtonFillColor"] = p.ButtonFill;
            Put(theme, "ButtonEdge", p.Light ? WithAlpha(Colors.Black, 0x18) : WithAlpha(Colors.White, 0x1C));
            theme["ButtonEdgeThickness"] = new Thickness(1);
        }
        else
        {
            Put(theme, "ButtonFill", p.ButtonFill);
            Put(theme, "ButtonEdge", l.ButtonOutlines ? p.LineStrong : Colors.Transparent);
            theme["ButtonEdgeThickness"] = new Thickness(l.ButtonOutlines ? 1 : 0);
        }
        if (l.ButtonOutlines && l.GlassButtons) Put(theme, "ButtonEdge", p.LineStrong);

        // Text.
        var sans = string.IsNullOrWhiteSpace(l.Font) ? "Segoe UI Variable Text, Segoe UI" : l.Font + ", Segoe UI";
        var display = string.IsNullOrWhiteSpace(l.Font) ? "Segoe UI Variable Display, Segoe UI" : l.Font + ", Segoe UI";
        theme["Sans"] = new FontFamily(sans);
        theme["Display"] = new FontFamily(display);
        double k = l.TextSizePercent / 100.0;
        theme["FontBody"] = Math.Round(13 * k, 1);
        theme["FontSmall"] = Math.Round(12 * k, 1);
        theme["FontHead"] = Math.Round(15 * k, 1);
        theme["FontTitle"] = Math.Round(20 * k, 1);

        // Corners.
        var (control, small, menu, card, thumb, pill) = l.Corners switch
        {
            CornerStyle.Sharp => (0.0, 0.0, 0.0, 0.0, 0.0, 2.0),
            CornerStyle.Subtle => (3.0, 3.0, 5.0, 5.0, 4.0, 6.0),
            CornerStyle.Round => (11.0, 7.0, 14.0, 18.0, 12.0, 16.0),
            _ => (6.0, 5.0, 9.0, 10.0, 7.0, 11.0),
        };
        theme["RadiusControl"] = new CornerRadius(control);
        theme["RadiusSmall"] = new CornerRadius(small);
        theme["RadiusMenu"] = new CornerRadius(menu);
        theme["RadiusCard"] = new CornerRadius(card);
        theme["RadiusThumb"] = new CornerRadius(thumb);
        theme["RadiusPill"] = new CornerRadius(pill);

        // Spacing.
        (theme["ButtonPadding"], theme["CardMargin"], theme["CardPadding"]) = l.Density switch
        {
            Density.Compact => (new Thickness(10, 4, 10, 4), new Thickness(6, 0, 6, 4), new Thickness(8, 6, 10, 6)),
            Density.Comfy => (new Thickness(16, 9, 16, 9), new Thickness(6, 0, 6, 12), new Thickness(14, 14, 16, 14)),
            _ => (new Thickness(13, 7, 13, 7), new Thickness(6, 0, 6, 8), new Thickness(10, 10, 12, 10)),
        };
        theme["SliderHeight"] = 4.0;

        ApplyClipList(theme, l, p);
    }

    /// <summary>The clip list's cards: fill, edge, size, what they show, hover.</summary>
    private static void ApplyClipList(ResourceDictionary theme, Looks l, Palette p)
    {
        var (w, h) = l.Thumbs switch
        {
            ThumbSize.Small => (104.0, 58.0),
            ThumbSize.Large => (176.0, 99.0),
            ThumbSize.Huge => (224.0, 126.0),
            _ => (136.0, 76.0),
        };
        theme["ThumbWidth"] = w;
        theme["ThumbHeight"] = h;

        // Cards over glass or a background let it through a little (as panels do).
        switch (l.Cards)
        {
            case CardStyle.Outlined:
                Put(theme, "CardFill", Colors.Transparent);
                Put(theme, "CardHoverFill", WithAlpha(p.Hover, 0x80));
                Put(theme, "CardSelectedFill", WithAlpha(p.CardSelected, 0xA0));
                Put(theme, "CardEdge", p.LineStrong);
                break;
            case CardStyle.Glass:
                Put(theme, "CardFill", WithAlpha(p.Panel, 0x99));
                Put(theme, "CardHoverFill", WithAlpha(p.CardHover, 0xB8));
                Put(theme, "CardSelectedFill", WithAlpha(p.CardSelected, 0xC8));
                Put(theme, "CardEdge", p.Light ? WithAlpha(Colors.Black, 0x14) : WithAlpha(Colors.White, 0x18));
                break;
            case CardStyle.Flat:
                Put(theme, "CardFill", Colors.Transparent);
                Put(theme, "CardHoverFill", WithAlpha(p.Hover, 0x80));
                Put(theme, "CardSelectedFill", WithAlpha(p.CardSelected, 0xC0));
                Put(theme, "CardEdge", Colors.Transparent);
                break;
            default:
                Put(theme, "CardFill", p.Panel);
                Put(theme, "CardHoverFill", p.CardHover);
                Put(theme, "CardSelectedFill", p.CardSelected);
                Put(theme, "CardEdge", Colors.Transparent);
                break;
        }
        theme["CardEdgeThickness"] = new Thickness(1);
        Put(theme, "CardSheen", Colors.Transparent);   // a highlight across the top of each card over glass (WindowLooks)

        Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        theme["ShowDuration"] = V(l.ShowDuration);
        theme["ShowSize"] = V(l.ShowSize);
        theme["ShowDate"] = V(l.ShowDate);
        theme["ShowFolder"] = V(l.ShowFolder);
        theme["ShowTags"] = V(l.ShowTags);
        theme["ShowStripe"] = V(l.SelectionStripe);

        if (l.CardLift)
        {
            var lift = new TranslateTransform(0, -2);
            lift.Freeze();
            theme["CardHoverTransform"] = lift;
            var shadow = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = p.Light ? 0.18 : 0.45, Color = Colors.Black, Direction = 270 };
            shadow.Freeze();
            theme["CardHoverEffect"] = shadow;
        }
        else
        {
            theme["CardHoverTransform"] = Transform.Identity;
            var none = new DropShadowEffect { Opacity = 0, BlurRadius = 0, ShadowDepth = 0 };
            none.Freeze();
            theme["CardHoverEffect"] = none;
        }
    }

    /// <summary>
    /// Applies a look while Persocord is running: the theme's colours are live
    /// references, so every open window repaints; each window's glass,
    /// background and title bar are redone too.
    /// </summary>
    /// <summary>
    /// The always-dark overlay colours (text, panels, lines, tick boxes) into a
    /// window's or control's own resources: for controls drawn over video or a
    /// game, which have to read whatever the theme is.
    /// </summary>
    public static void PutOverlay(ResourceDictionary r)
    {
        var p = Overlay;
        Put(r, "Panel", p.Panel);
        Put(r, "Raised", p.Raised);
        Put(r, "Field", p.Field);
        Put(r, "Line", p.Line);
        Put(r, "LineStrong", p.LineStrong);
        Put(r, "Hover", p.Hover);
        Put(r, "Pressed", p.Pressed);
        Put(r, "Ink", p.Ink);
        Put(r, "Body", p.Body);
        Put(r, "ButtonInk", p.Body);
        Put(r, "Muted", p.Muted);
        Put(r, "Faint", p.Faint);
        Put(r, "Track", p.Track);
        Put(r, "CheckEdge", p.CheckEdge);
        Put(r, "CheckEdgeHover", p.CheckEdgeHover);
        Put(r, "MenuEdge", p.MenuEdge);
        Put(r, "AccentText", Readable(Accent, p.Panel, p.Bezel));
    }

    public static void ApplyLive(Looks l)
    {
        var app = Application.Current;
        if (app == null || app.Resources.MergedDictionaries.Count == 0) return;
        Apply(app.Resources.MergedDictionaries[0], l);
        LogoArt.Refresh(l);
        foreach (Window w in app.Windows) WindowLooks.Apply(w);
    }

    /// <summary>Sets both the colour and its brush, as the theme defines them in pairs.</summary>
    private static void Put(ResourceDictionary d, string key, Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        d[key] = brush;
        d[key + "Color"] = c;
    }

    private static LinearGradientBrush Gradient(Color a, Color b, double angle)
    {
        var g = new LinearGradientBrush(a, b, angle);
        g.Freeze();
        return g;
    }

    public static Color Parse(string? hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return (Color)ColorConverter.ConvertFromString(hex.Trim().StartsWith("#") ? hex.Trim() : "#" + hex.Trim());
        }
        catch { return fallback; }
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>WCAG contrast between two colours (1 to 21).</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a) + 0.05, lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    /// <summary>
    /// The colour, lightened or darkened only as much as needed to read as
    /// text (4.5:1) on both backgrounds. A gold on black stays gold; a navy on
    /// black becomes a light blue; a pale yellow on white becomes ochre.
    /// </summary>
    public static Color Readable(Color c, Color bg1, Color bg2)
    {
        bool ok(Color x) => Contrast(x, bg1) >= 4.5 && Contrast(x, bg2) >= 4.5;
        if (ok(c)) return c;
        var towards = Luminance(bg1) > 0.35 ? Colors.Black : Colors.White;
        for (double t = 0.05; t <= 1.0; t += 0.05)
        {
            var x = Mix(c, towards, t);
            if (ok(x)) return x;
        }
        return towards == Colors.White ? Color.FromRgb(0xF4, 0xF5, 0xF7) : Color.FromRgb(0x14, 0x16, 0x1A);
    }

    public static double Luminance(Color c)
    {
        static double Ch(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
