using System.Text.Json.Serialization;

namespace Persocord.Models;

/// <summary>Light or dark.</summary>
public enum ThemeMode { Dark, Light }

/// <summary>The app's background family. The first six are dark, the last three light.</summary>
public enum SurfaceStyle { Graphite, OledBlack, Slate, Midnight, Forest, Plum, Paper, Snow, Sand, CustomDark, CustomLight }

/// <summary>The colour of accent-coloured text (links, labels, tags).</summary>
public enum AccentTextMode
{
    /// <summary>The accent, made lighter or darker as needed to read on the background.</summary>
    Auto,
    /// <summary>Exactly the accent (may be hard to read with dark accents on dark backgrounds).</summary>
    Exact,
    /// <summary>A colour of your own.</summary>
    Custom,
}

/// <summary>How the frame around the recording area is drawn.</summary>
/// <summary>The frame around a recording: a clean thin line (default), brushed metal, or a solid line.</summary>
public enum FrameStyle { Clean, Metal, Solid }

/// <summary>Windows 11's see-through window materials.</summary>
public enum GlassKind
{
    /// <summary>Takes a soft tint from the desktop wallpaper; the most subtle.</summary>
    Mica,
    /// <summary>Frosted: blurs whatever is behind the window.</summary>
    Acrylic,
    /// <summary>A deeper wallpaper tint.</summary>
    MicaAlt,
}

/// <summary>The colour laid over a window's glass.</summary>
public enum GlassTint
{
    /// <summary>The background colour (smoky in dark mode).</summary>
    Surface,
    /// <summary>Frosted white, like liquid glass.</summary>
    Frost,
    /// <summary>The accent colour.</summary>
    Accent,
    /// <summary>Hardly any colour: mostly the blur.</summary>
    Clear,
}

/// <summary>Text colour over glass.</summary>
public enum GlassText { Auto, Light, Dark, Custom }

/// <summary>What fills a window's background.</summary>
public enum BackgroundFill { Solid, Gradient, Image }

/// <summary>How a background image covers a window.</summary>
public enum ImageFit { Fill, Fit, Stretch, Tile }

/// <summary>How round corners are.</summary>
public enum CornerStyle { Sharp, Subtle, Rounded, Round }

/// <summary>How much room things get.</summary>
public enum Density { Compact, Normal, Comfy }

/// <summary>How each clip in the list is drawn.</summary>
public enum CardStyle { Filled, Outlined, Glass, Flat }

/// <summary>Thumbnail size in the clip list.</summary>
public enum ThumbSize { Small, Medium, Large, Huge }

/// <summary>How the countdown before a recording looks.</summary>
public enum CountdownStyle { Badge, Big, Minimal }

/// <summary>Where the accent glow sits in a window.</summary>
public enum GlowCorner { TopLeft, TopRight, BottomLeft, BottomRight, Centre }

/// <summary>The player's controls: clean, straight over the video, or on a floating glass bar.</summary>
public enum PlayerStyle { Minimal, Glass }

/// <summary>The seek bar's colour: white (reads on any clip), the accent (made light enough to read), or one's own.</summary>
public enum SeekStyle { White, Accent, Custom }

/// <summary>One window's (or group of windows') look.</summary>
public class WindowLook
{
    /// <summary>Windows 11 glass behind the window. Off on older Windows.</summary>
    public bool Glass { get; set; } = false;
    public GlassKind GlassKind { get; set; } = GlassKind.Acrylic;
    /// <summary>The colour over the glass.</summary>
    public GlassTint Tint { get; set; } = GlassTint.Surface;
    /// <summary>How much of that colour stays over the glass, 0–100%.</summary>
    public int GlassTintPercent { get; set; } = 55;
    /// <summary>
    /// How see-through the window is, 0–100%: 0 is solid; higher lets more of
    /// what's behind show through a blur (lighter towards 100%, the clearest).
    /// </summary>
    public int SeeThroughPercent { get; set; } = 0;
    /// <summary>Older looks' "how solid", 30–100%; read once and turned into <see cref="SeeThroughPercent"/>.</summary>
    public int OpacityPercent { get; set; } = 100;
    /// <summary>The shared background (gradient or image) shows in this window.</summary>
    public bool ShowBackground { get; set; } = true;

    public WindowLook Copy() => (WindowLook)MemberwiseClone();
}

/// <summary>
/// Everything about how Persocord looks, kept apart from its settings: its
/// own file (looks.json), its own share code (LOOK1-), its own export and
/// reset. Sharing a look never touches someone's settings, and the other way
/// round.
/// </summary>
public class Looks
{
    public int Version { get; set; } = 7;

    /* ---- colours ------------------------------------------------------------ */
    public ThemeMode Mode { get; set; } = ThemeMode.Dark;
    public SurfaceStyle DarkSurface { get; set; } = SurfaceStyle.Graphite;
    public SurfaceStyle LightSurface { get; set; } = SurfaceStyle.Paper;
    /// <summary>Buttons, highlights, links and the timeline.</summary>
    public string Accent { get; set; } = "#D6B06A";
    /// <summary>Main buttons fade from the accent into a second colour.</summary>
    public bool AccentGradient { get; set; } = false;
    /// <summary>Accent-coloured text stays readable on any background (Auto), or is fixed.</summary>
    public AccentTextMode AccentText { get; set; } = AccentTextMode.Auto;
    public string AccentTextColor { get; set; } = "#E3C184";
    /// <summary>Your own dark and light background colours (the Custom choices).</summary>
    public string CustomDark { get; set; } = "#16191F";
    public string CustomLight { get; set; } = "#EEF0F3";
    public string Accent2 { get; set; } = "#E0708A";

    /* ---- text and shape ------------------------------------------------------- */
    /// <summary>Any installed font; empty is Persocord's own (Segoe UI Variable).</summary>
    public string Font { get; set; } = "";
    public int TextSizePercent { get; set; } = 100;
    /// <summary>Everything bigger or smaller, 80–150%.</summary>
    public int UiScalePercent { get; set; } = 100;
    public CornerStyle Corners { get; set; } = CornerStyle.Rounded;
    public Density Density { get; set; } = Density.Normal;
    /// <summary>Buttons get a glassy fill with a soft shine along the top.</summary>
    public bool GlassButtons { get; set; } = false;
    /// <summary>A thin edge around buttons.</summary>
    public bool ButtonOutlines { get; set; } = false;

    /* ---- backgrounds ---------------------------------------------------------- */
    public BackgroundFill Background { get; set; } = BackgroundFill.Solid;
    public string GradientFrom { get; set; } = "#1A2333";
    public string GradientTo { get; set; } = "#2A1A2E";
    public int GradientAngle { get; set; } = 135;
    /// <summary>A picture on this PC (never part of a share code).</summary>
    public string ImagePath { get; set; } = "";
    public ImageFit ImageFit { get; set; } = ImageFit.Fill;
    /// <summary>How much the background colour covers the image, 0–90%.</summary>
    public int ImageDimPercent { get; set; } = 55;
    public int ImageBlur { get; set; } = 0;
    /// <summary>A soft glow of the accent colour in one corner of each window.</summary>
    public bool AccentGlow { get; set; } = false;
    public GlowCorner GlowCorner { get; set; } = GlowCorner.TopLeft;
    public int GlowPercent { get; set; } = 30;
    /// <summary>
    /// Over glass or a background, how solid panels and cards stay, 20–100%
    /// (lower lets more show through).
    /// </summary>
    public int PanelOpacityPercent { get; set; } = 75;
    /// <summary>Over glass: thin bright edges on cards, buttons and lines, like cut glass.</summary>
    public bool GlassEdges { get; set; } = false;
    /// <summary>Over glass: a soft rainbow sheen, like light through a prism.</summary>
    public bool PrismSheen { get; set; } = false;
    public int PrismPercent { get; set; } = 10;
    /// <summary>Over glass: a fine grain, as real frosted glass has (it also stops gradients banding).</summary>
    public bool GlassGrain { get; set; } = false;
    /// <summary>Before 1.5, a switch for Persocord's own title bar over glass; it's always on now (kept so older looks still read).</summary>
    public bool GlassTitleBar { get; set; } = true;
    /// <summary>Text over glass: chosen to read on the glass (Auto), or fixed.</summary>
    public GlassText GlassText { get; set; } = GlassText.Auto;
    public string GlassTextColor { get; set; } = "#FFFFFF";

    /* ---- windows ------------------------------------------------------------- */
    public WindowLook ClipList { get; set; } = new();
    public WindowLook SettingsWindow { get; set; } = new();
    public WindowLook Editor { get; set; } = new();
    /// <summary>Dialogs and small windows.</summary>
    public WindowLook Popups { get; set; } = new();

    /* ---- clip list ------------------------------------------------------------ */
    public CardStyle Cards { get; set; } = CardStyle.Filled;
    public ThumbSize Thumbs { get; set; } = ThumbSize.Medium;
    public bool ShowDuration { get; set; } = true;
    public bool ShowSize { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowFolder { get; set; } = true;
    public bool ShowTags { get; set; } = true;
    /// <summary>Cards rise a little, with a shadow, under the pointer.</summary>
    public bool CardLift { get; set; } = false;
    /// <summary>A thin accent stripe down the left of the selected card.</summary>
    public bool SelectionStripe { get; set; } = false;

    /* ---- while recording ------------------------------------------------------ */
    public FrameStyle FrameStyle { get; set; } = FrameStyle.Clean;
    /// <summary>The frame's colour; for the metal style, the tint of the metal.</summary>
    public string FrameColor { get; set; } = "#FFFFFF";
    public string FrameCornerColor { get; set; } = "#FFFFFF";
    public bool FramePulse { get; set; } = true;
    public string ClickRingColor { get; set; } = "#FFFFFF";
    /// <summary>The control bar: how solid its background is, 30–100%.</summary>
    public int BarOpacityPercent { get; set; } = 100;
    public int BarScalePercent { get; set; } = 100;
    /// <summary>Older looks' "compact" bar; read once and turned into hiding Restart and Settings.</summary>
    public bool BarCompact { get; set; } = false;
    public bool BarShadow { get; set; } = true;
    public bool BarShowTrack { get; set; } = true;
    /// <summary>The control bar's own colour, and its Done button's (the text on it picks dark or light).</summary>
    public string BarColor { get; set; } = "#121418";
    public string BarDoneColor { get; set; } = "#F4F5F7";
    /// <summary>A word next to each icon on the control bar.</summary>
    public bool BarLabels { get; set; } = false;
    // Each piece of the control bar, on or off (Done is always there).
    public bool BarShowTimer { get; set; } = true;
    public bool BarShowPause { get; set; } = true;
    public bool BarShowPcAudio { get; set; } = true;
    public bool BarShowMic { get; set; } = true;
    public bool BarShowRestart { get; set; } = true;
    public bool BarShowSettings { get; set; } = true;
    public bool BarShowCancel { get; set; } = true;
    public bool BarShowUpload { get; set; } = true;
    public CountdownStyle Countdown { get; set; } = CountdownStyle.Badge;
    public int CountdownScalePercent { get; set; } = 100;
    public string CountdownColor { get; set; } = "#FFFFFF";
    /// <summary>The countdown tints the area slightly.</summary>
    public bool CountdownDim { get; set; } = true;
    /// <summary>How thick the frame around the recorded area is, 1–12 px.</summary>
    public int FrameThickness { get; set; } = 2;
    /// <summary>Persocord's own notifications, 75–150% of their normal size.</summary>
    public int ToastSizePercent { get; set; } = 100;

    /* ---- the logo -------------------------------------------------------------- */
    // Its blades, its accent blade and the shard behind them ("" follows the
    // look: the text colour, the accent and the background).
    public string LogoBlades { get; set; } = "";
    public string LogoAccent { get; set; } = "";
    public string LogoBack { get; set; } = "";

    /* ---- area picker ---------------------------------------------------------- */
    public string PickerColor { get; set; } = "#FFFFFF";
    public int PickerDimPercent { get; set; } = 15;
    public bool PickerGuides { get; set; } = true;
    public bool PickerReadout { get; set; } = true;
    public bool PickerHint { get; set; } = true;
    /// <summary>Rule-of-thirds lines inside the selection.</summary>
    public bool PickerThirds { get; set; } = false;

    /* ---- player ---------------------------------------------------------------- */
    public PlayerStyle PlayerStyle { get; set; } = PlayerStyle.Minimal;
    public SeekStyle SeekStyle { get; set; } = SeekStyle.White;
    /// <summary>The seek bar's own colour (with <see cref="SeekStyle.Custom"/>).</summary>
    public string SeekColor { get; set; } = "";
    /// <summary>The seek bar's thickness at rest; it grows a little under the mouse.</summary>
    public int SeekThickness { get; set; } = 3;
    /// <summary>How dark the player's glass controls and the shades along the top and bottom are, 0–100%.</summary>
    public int PlayerShadePercent { get; set; } = 85;
    /// <summary>Soft shades along the top and bottom of the video while the controls show.</summary>
    public bool PlayerEdgeShade { get; set; } = true;
    /// <summary>Seconds before the player's controls hide while playing.</summary>
    public double PlayerHideSeconds { get; set; } = 2.5;

    /* ---- editor ---------------------------------------------------------------- */
    public string TimelineVideoColor { get; set; } = "#2F6FA8";
    public string TimelineAudioColor { get; set; } = "#3E7D3A";
    public string PlayheadColor { get; set; } = "#FF4D5E";
    public string TimelineBackground { get; set; } = "#121418";

    public Looks Clone()
    {
        var c = (Looks)MemberwiseClone();
        c.ClipList = (ClipList ?? new()).Copy();
        c.SettingsWindow = (SettingsWindow ?? new()).Copy();
        c.Editor = (Editor ?? new()).Copy();
        c.Popups = (Popups ?? new()).Copy();
        return c;
    }

    /// <summary>The surface in use (dark or light, as chosen).</summary>
    [JsonIgnore]
    public SurfaceStyle Surface => Mode == ThemeMode.Light ? LightSurface : DarkSurface;

    public static bool IsLight(SurfaceStyle s) => s is SurfaceStyle.Paper or SurfaceStyle.Snow or SurfaceStyle.Sand or SurfaceStyle.CustomLight;
}
