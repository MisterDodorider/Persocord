using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// How Persocord looks, kept in its own file (looks.json), apart from the
/// settings. It has its own share code, export, import and reset, so sharing
/// a look never changes anyone's settings, and sharing settings never changes
/// a look.
///
/// <see cref="Current"/> is what's on screen: the saved look, or a preview
/// while Customization is open (every change shows live; Cancel goes back).
/// </summary>
public static class LookStore
{
    public const string CodePrefix = "LOOK1-";

    private static string FilePath => Path.Combine(Paths.Root, "looks.json");
    private static string BackupFile => FilePath + ".backup";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Never in a share code: a picture on this PC.</summary>
    private static readonly HashSet<string> Personal = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(Looks.Version),
        nameof(Looks.ImagePath),
    };

    /// <summary>The look as saved.</summary>
    public static Looks Saved { get; private set; } = new();

    /// <summary>The look on screen (a preview while it's being changed).</summary>
    public static Looks Current { get; private set; } = new();

    /// <summary>The look on screen changed: windows repaint.</summary>
    public static event Action? Changed;

    /* ---- loading and saving -------------------------------------------------- */

    /// <summary>Reads looks.json; the first time, takes the colours already chosen in settings.</summary>
    public static void Load()
    {
        var l = Read(FilePath) ?? Read(BackupFile);
        if (l == null)
        {
            l = FromOldSettings();
            Write(l);
            Log.Write("Looks: created looks.json" + (l.Accent != new Looks().Accent ? " from the colours in settings" : ""));
        }
        Validate(l);
        if (l.Version < 2)
        {
            // 1.4: the area picker no longer darkens the screen heavily by
            // default. A strength someone picked stays.
            if (l.PickerDimPercent == 55) l.PickerDimPercent = 15;
            l.Version = 2;
            Write(l);
        }
        if (l.Version < 3)
        {
            // 1.4: the glass grain was more distracting than frosted; off unless chosen again.
            l.GlassGrain = false;
            l.Version = 3;
            Write(l);
        }
        if (l.Version < 4)
        {
            // 1.4: the seek bar is white and thinner by default; a colour someone picked stays.
            l.SeekStyle = string.IsNullOrWhiteSpace(l.SeekColor) ? SeekStyle.White : SeekStyle.Custom;
            if (l.SeekThickness == 4) l.SeekThickness = 3;
            l.Version = 4;
            Write(l);
        }
        if (l.Version < 5)
        {
            // 1.4.1: the recording overlays went clean and white, like the player.
            // Colours someone picked themselves stay.
            bool Was(string v, params string[] olds) => olds.Any(o => string.Equals(v, o, StringComparison.OrdinalIgnoreCase));
            if (l.FrameStyle == FrameStyle.Metal && Was(l.FrameColor, "#A7ADB6")) l.FrameStyle = FrameStyle.Clean;
            if (Was(l.FrameColor, "#A7ADB6")) l.FrameColor = "#FFFFFF";
            if (Was(l.FrameCornerColor, "#E4E7EB")) l.FrameCornerColor = "#FFFFFF";
            if (Was(l.PickerColor, "#D6B06A", l.Accent)) l.PickerColor = "#FFFFFF";
            if (Was(l.CountdownColor, "#D6B06A", l.Accent)) l.CountdownColor = "#FFFFFF";
            if (Was(l.ClickRingColor, "#D6B06A", l.Accent)) l.ClickRingColor = "#FFFFFF";
            l.Version = 5;
            Write(l);
        }
        if (l.Version < 6)
        {
            // 1.4: "compact" became a switch per button.
            if (l.BarCompact) { l.BarShowRestart = false; l.BarShowSettings = false; l.BarCompact = false; }
            l.Version = 6;
            Write(l);
        }
        if (l.Version < 7)
        {
            // 1.5: the frame's thickness and the notifications' size moved
            // here from settings, as part of the look.
            CarryFromSettings(l);
            l.Version = 7;
            Write(l);
        }
        Saved = Sanitise(l);
        Current = Saved.Clone();
    }

    /// <summary>Shows a look without saving it (Customization's live preview).</summary>
    public static void Preview(Looks l)
    {
        Current = Sanitise(l.Clone());
        Changed?.Invoke();
    }

    /// <summary>Back to the saved look (Customization closed without saving).</summary>
    public static void Revert()
    {
        if (Same(Current, Saved)) return;
        Current = Saved.Clone();
        Changed?.Invoke();
    }

    public static void Save(Looks l)
    {
        Saved = Sanitise(l.Clone());
        Current = Saved.Clone();
        Write(Saved);
        Changed?.Invoke();
    }

    public static void ResetToDefaults() => Save(new Looks());

    public static void Export(string path)
    {
        // The picture's path is never read back from a file (and holds the Windows user name).
        var copy = Saved.Clone();
        copy.ImagePath = "";
        File.WriteAllText(path, JsonSerializer.Serialize(copy, Opts));
    }

    /// <summary>Reads a look file. Null if fine, otherwise a plain reason.</summary>
    public static string? Import(string path, out Looks? looks)
    {
        looks = null;
        // A look file is a few kilobytes; anything far bigger isn't one.
        try { if (new FileInfo(path).Length > 256 * 1024) return "That file isn't a Persocord look (or it's damaged)."; } catch { }
        looks = Read(path);
        if (looks == null) return "That file isn't a Persocord look (or it's damaged).";
        Validate(looks);
        looks = Sanitise(looks);
        looks.ImagePath = "";   // a file from elsewhere doesn't pick a picture on this PC
        return null;
    }

    /* ---- share codes ---------------------------------------------------------- */

    public static string ExportCode(Looks l) => DiffCode.Export(l, new Looks(), CodePrefix, Personal);

    /// <summary>Applies a look code onto <paramref name="target"/>; throws with a plain reason if it's bad.</summary>
    /// <remarks>
    /// Safe whatever a code holds. A code is only data (never run), read only
    /// into a look (the one place it can write), and only into the look's
    /// known values: anything else is ignored. Each value must be the right
    /// kind (a colour must be a colour, a choice one of the real choices,
    /// numbers in range), or it falls back to the original look's. Size and
    /// nesting are capped. Settings, folders, devices and files are out of
    /// reach: the look holds none of them, and its picture path is never read
    /// from a code.
    /// </remarks>
    public static int ImportCode(string code, Looks target)
    {
        if ((code ?? "").Contains("PERSO1-", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That's a settings code. Import it under Settings → About → Your settings.");
        var obj = DiffCode.Decode(code ?? "", CodePrefix, "a Persocord look code");

        // Read into a fresh look of its own, checked, and only then copied over.
        var incoming = new Looks();
        int applied = DiffCode.Apply(obj, incoming, Personal);
        Validate(incoming);
        Sanitise(incoming);
        var keepPicture = target.ImagePath;
        foreach (var prop in typeof(Looks).GetProperties())
            if (prop.CanWrite && prop.CanRead) prop.SetValue(target, prop.GetValue(incoming));
        target.ImagePath = keepPicture;
        target.ClipList = incoming.ClipList.Copy();
        target.SettingsWindow = incoming.SettingsWindow.Copy();
        target.Editor = incoming.Editor.Copy();
        target.Popups = incoming.Popups.Copy();
        return applied;
    }

    private static readonly System.Text.RegularExpressions.Regex HexColour = new("^#[0-9A-Fa-f]{6}$");

    /// <summary>
    /// Every value must be a real one: colours are #RRGGBB, choices are ones
    /// that exist, the font name is just a name. Anything else goes back to
    /// the original look's value.
    /// </summary>
    public static void Validate(Looks l)
    {
        var std = new Looks();
        foreach (var prop in typeof(Looks).GetProperties())
        {
            if (!prop.CanWrite || !prop.CanRead) continue;
            var v = prop.GetValue(l);
            if (prop.PropertyType.IsEnum)
            {
                if (v == null || !Enum.IsDefined(prop.PropertyType, v)) prop.SetValue(l, prop.GetValue(std));
            }
            else if (prop.PropertyType == typeof(string))
            {
                var s = v as string ?? "";
                bool ok = prop.Name switch
                {
                    nameof(Looks.ImagePath) => true,                       // never comes from a code
                    nameof(Looks.Font) => s.Length <= 64 && s.All(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '\'' or '&' or '.'),
                    nameof(Looks.SeekColor) or nameof(Looks.LogoBlades) or nameof(Looks.LogoAccent) or nameof(Looks.LogoBack)
                        => s.Length == 0 || HexColour.IsMatch(s),
                    _ => HexColour.IsMatch(s),
                };
                if (!ok) prop.SetValue(l, prop.GetValue(std));
            }
            else if (prop.PropertyType == typeof(double) && v is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                prop.SetValue(l, prop.GetValue(std));
        }
        foreach (var w in new[] { l.ClipList, l.SettingsWindow, l.Editor, l.Popups })
        {
            if (w == null) continue;
            if (!Enum.IsDefined(w.GlassKind)) w.GlassKind = GlassKind.Acrylic;
            if (!Enum.IsDefined(w.Tint)) w.Tint = GlassTint.Surface;
        }
    }

    /* ---- files ------------------------------------------------------------------ */

    private static Looks? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Looks>(File.ReadAllText(path), Opts);
        }
        catch (Exception ex)
        {
            Log.Error("read looks " + Path.GetFileName(path), ex);
            return null;
        }
    }

    private static void Write(Looks l)
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupFile, true);
            var temp = FilePath + ".part";
            File.WriteAllText(temp, JsonSerializer.Serialize(l, Opts));
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("save looks", ex); }
    }

    private static bool Same(Looks a, Looks b) =>
        JsonSerializer.Serialize(a, Opts) == JsonSerializer.Serialize(b, Opts);

    /// <summary>Keeps every number in range and every window's look present (old or hand-edited files).</summary>
    public static Looks Sanitise(Looks l)
    {
        l.ClipList ??= new();
        l.SettingsWindow ??= new();
        l.Editor ??= new();
        l.Popups ??= new();
        foreach (var w in new[] { l.ClipList, l.SettingsWindow, l.Editor, l.Popups })
        {
            w.GlassTintPercent = Math.Clamp(w.GlassTintPercent, 0, 100);
            // An older look's "how solid" becomes how see-through (it went 100% solid → 30%).
            w.OpacityPercent = Math.Clamp(w.OpacityPercent, 30, 100);
            if (w.OpacityPercent < 100)
            {
                w.SeeThroughPercent = Math.Max(w.SeeThroughPercent, (int)Math.Round((100 - w.OpacityPercent) * 100 / 70.0));
                w.OpacityPercent = 100;
            }
            w.SeeThroughPercent = Math.Clamp(w.SeeThroughPercent, 0, 100);
        }
        if (Looks.IsLight(l.DarkSurface)) l.DarkSurface = SurfaceStyle.Graphite;
        if (!Looks.IsLight(l.LightSurface)) l.LightSurface = SurfaceStyle.Paper;
        l.TextSizePercent = Math.Clamp(l.TextSizePercent, 80, 140);
        l.UiScalePercent = Math.Clamp(l.UiScalePercent, 80, 150);
        l.GradientAngle = ((l.GradientAngle % 360) + 360) % 360;
        l.ImageDimPercent = Math.Clamp(l.ImageDimPercent, 0, 90);
        l.ImageBlur = Math.Clamp(l.ImageBlur, 0, 40);
        l.GlowPercent = Math.Clamp(l.GlowPercent, 5, 80);
        l.PanelOpacityPercent = Math.Clamp(l.PanelOpacityPercent, 10, 100);
        // An older look (or code) with the "compact" control bar: it hid Restart and Settings.
        if (l.BarCompact) { l.BarShowRestart = false; l.BarShowSettings = false; l.BarCompact = false; }
        l.PrismPercent = Math.Clamp(l.PrismPercent, 3, 30);
        l.BarOpacityPercent = Math.Clamp(l.BarOpacityPercent, 30, 100);
        l.BarScalePercent = Math.Clamp(l.BarScalePercent, 70, 160);
        l.CountdownScalePercent = Math.Clamp(l.CountdownScalePercent, 50, 250);
        l.FrameThickness = Math.Clamp(l.FrameThickness, 1, 12);
        l.ToastSizePercent = Math.Clamp(l.ToastSizePercent, 75, 150);
        l.PickerDimPercent = Math.Clamp(l.PickerDimPercent, 0, 90);
        l.SeekThickness = Math.Clamp(l.SeekThickness, 2, 10);
        l.PlayerShadePercent = Math.Clamp(l.PlayerShadePercent, 0, 100);
        l.PlayerHideSeconds = Math.Clamp(l.PlayerHideSeconds, 1, 10);
        l.Font ??= "";
        l.ImagePath ??= "";
        l.SeekColor ??= "";
        l.LogoBlades ??= "";
        l.LogoAccent ??= "";
        l.LogoBack ??= "";
        return l;
    }

    /// <summary>
    /// The first run with looks.json: the colours chosen before (they lived in
    /// settings.json) carry over, so nothing changes on screen.
    /// </summary>
    private static Looks FromOldSettings()
    {
        var l = new Looks();
        foreach (var file in new[] { Paths.SettingsFile, Paths.SettingsFile + ".backup" })
        {
            try
            {
                if (!File.Exists(file)) continue;
                var o = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (o == null) continue;
                string? S(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;
                int? I(string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var x) ? x : null;
                bool? B(string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : null;
                if (S("UiAccent") is { } accent) l.Accent = accent;
                if (S("Surface") is { } surface && Enum.TryParse<SurfaceStyle>(surface, out var st) && !Looks.IsLight(st)) l.DarkSurface = st;
                if (S("BorderColor") is { } frame)
                    l.FrameColor = frame.Equals("#B4483C", StringComparison.OrdinalIgnoreCase) ? "#A7ADB6" : frame;
                if (S("FrameStyle") is { } fs && Enum.TryParse<FrameStyle>(fs, out var fst)) l.FrameStyle = fst;
                if (S("FrameCornerColor") is { } corner) l.FrameCornerColor = corner;
                if (B("FramePulse") is { } pulse) l.FramePulse = pulse;
                if (S("ClickHighlightColor") is { } ring) l.ClickRingColor = ring;
                if (S("AccentColor") is { } picker) { l.PickerColor = picker; l.CountdownColor = picker; }
                if (I("PickerDimPercent") is { } dim) l.PickerDimPercent = dim == 55 ? 15 : dim;   // the old heavy default becomes light
                break;
            }
            catch (Exception ex) { Log.Error("looks from settings", ex); }
        }
        CarryFromSettings(l);
        return Sanitise(l);
    }

    /// <summary>The look's parts that used to be settings (1.5): frame thickness, notification size.</summary>
    private static void CarryFromSettings(Looks l)
    {
        foreach (var file in new[] { Paths.SettingsFile, Paths.SettingsFile + ".backup" })
        {
            try
            {
                if (!File.Exists(file)) continue;
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject o) continue;
                int? I(string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var x) ? x : null;
                if (I("BorderThickness") is { } t) l.FrameThickness = Math.Clamp(t, 1, 12);
                if (I("ToastSizePercent") is { } z) l.ToastSizePercent = Math.Clamp(z, 75, 150);
                return;
            }
            catch (Exception ex) { Log.Error("looks from settings", ex); }
        }
    }
}
