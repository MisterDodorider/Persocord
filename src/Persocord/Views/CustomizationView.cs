using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// How Persocord looks. Every change shows at once, across the whole app;
/// Save keeps it, Cancel (or closing Settings) goes back. The look is its own
/// thing (<see cref="LookStore"/>): its code, file and reset never touch a
/// setting, and settings never touch it.
/// </summary>
public class CustomizationView : StackPanel
{
    private readonly Window _owner;
    private Looks _l;
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly HashSet<string> _open = new() { "Colours" };

    private static readonly string[] Bright =
        { "#E0594D", "#D6B06A", "#E8845A", "#E0708A", "#A58BEA", "#6FB4E8", "#5CCB9E", "#E8EAED" };

    private static readonly (string, string?)[] MetalPresets =
    {
        ("#A7ADB6", "Steel"), ("#8E949E", "Titanium"), ("#5E646E", "Gunmetal"), ("#D9DDE2", "Chrome"),
        ("#B8875A", "Bronze"), ("#D6B06A", "Gold"), ("#C9897A", "Rose gold"), ("#E0594D", "Red"),
    };

    private static readonly (string, string?)[] CornerPresets =
    {
        ("#E4E7EB", "Chrome"), ("#D6B06A", "Gold"), ("#E0594D", "Red"), ("#E8845A", "Ember"),
        ("#E0708A", "Rose"), ("#A58BEA", "Violet"), ("#6FB4E8", "Ice"), ("#5CCB9E", "Mint"),
    };

    private static readonly (string, string?)[] GradientPresets =
    {
        ("#1A2333", "Night"), ("#2A1A2E", "Dusk"), ("#0F2A2A", "Lagoon"), ("#2E1F14", "Ember"),
        ("#1B2B1B", "Moss"), ("#2B1630", "Grape"), ("#331A1A", "Wine"), ("#0E1726", "Deep sea"),
        ("#E9E4F5", "Lilac"), ("#E2F0EC", "Mint cream"), ("#F6E7DA", "Peach"), ("#E3ECF7", "Sky"),
    };

    private static readonly (string, string?)[] TimelineBackPresets =
    {
        ("#121418", "Graphite"), ("#000000", "Black"), ("#0E1320", "Midnight"), ("#16111A", "Plum"),
        ("#0F1512", "Forest"), ("#1C1C1C", "Charcoal"),
    };

    public CustomizationView(Window owner)
    {
        _owner = owner;
        _l = LookStore.Saved.Clone();
        _debounce.Tick += (_, _) => { _debounce.Stop(); LookStore.Preview(_l); };
        // Closing Settings without saving puts the saved look back.
        owner.Closed += (_, _) => { _debounce.Stop(); LookStore.Revert(); };
        Build();
    }

    /// <summary>Settings → Save: keeps the look shown now.</summary>
    public void Commit()
    {
        _debounce.Stop();
        LookStore.Save(_l);
        _note.Text = "Look saved.";
    }

    /// <summary>Shows a change everywhere (slider drags are bundled up).</summary>
    private void Changed(bool rebuild = false)
    {
        _debounce.Stop();
        _debounce.Start();
        if (rebuild) Build();
    }

    /* ---- the page ---------------------------------------------------------------- */

    private void Build()
    {
        // Remember which folds are open, so rebuilding doesn't close them.
        foreach (var e in Children.OfType<Expander>())
            if (e.Header is string h) { if (e.IsExpanded) _open.Add(h); else _open.Remove(h); }
        Children.Clear();

        _note.Text = "Changes show straight away. Save keeps them; Cancel puts the saved look back.";
        _note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Children.Add(_note);

        BuildColours();
        BuildLogo();
        BuildGlass();
        BuildBackground();
        BuildTextAndShape();
        BuildClipList();
        BuildRecording();
        BuildPicker();
        BuildPlayerEditor();
        BuildShare();
    }

    private void BuildColours()
    {
        var p = Fold("Colours");
        p.Children.Add(Label("Mode"));
        p.Children.Add(Choices(new[] { ("Dark", "Easy on the eyes"), ("Light", "Bright and clean") },
            _l.Mode == ThemeMode.Light ? 1 : 0, i => { _l.Mode = i == 1 ? ThemeMode.Light : ThemeMode.Dark; Changed(true); }));

        p.Children.Add(Label(_l.Mode == ThemeMode.Light ? "Light background" : "Dark background"));
        var styles = new WrapPanel();
        var list = _l.Mode == ThemeMode.Light
            ? new[]
            {
                (SurfaceStyle.Paper, "Paper", "Soft grey-white"), (SurfaceStyle.Snow, "Snow", "Bright white"), (SurfaceStyle.Sand, "Sand", "Warm cream"),
                (SurfaceStyle.CustomLight, "Custom", "Your own colour"),
            }
            : new[]
            {
                (SurfaceStyle.Graphite, "Graphite", "Soft near-black"), (SurfaceStyle.OledBlack, "OLED black", "True black"),
                (SurfaceStyle.Slate, "Slate", "Cool blue-grey"), (SurfaceStyle.Midnight, "Midnight", "Deep navy"),
                (SurfaceStyle.Forest, "Forest", "Dark green"), (SurfaceStyle.Plum, "Plum", "Dark purple"),
                (SurfaceStyle.CustomDark, "Custom", "Your own colour"),
            };
        foreach (var (style, name, blurb) in list) styles.Children.Add(StyleCard(style, name, blurb));
        p.Children.Add(styles);
        // Your own background colour: panels, menus and lines are made from it.
        if (_l.Surface == SurfaceStyle.CustomDark)
        {
            p.Children.Add(Hint("Pick any colour; panels, menus and lines are made from it, and text turns light or dark to read on it."));
            p.Children.Add(ColourPicker(new (string, string?)[]
            {
                ("#16191F", "Ink"), ("#1E1B2E", "Dusk"), ("#102027", "Deep teal"), ("#1F1416", "Oxblood"),
                ("#14201A", "Pine"), ("#22201C", "Espresso"), ("#0B0D18", "Abyss"), ("#262626", "Charcoal"),
            }, () => _l.CustomDark, v => { _l.CustomDark = v; Changed(true); }));
        }
        else if (_l.Surface == SurfaceStyle.CustomLight)
        {
            p.Children.Add(Hint("Pick any colour; panels, menus and lines are made from it, and text turns light or dark to read on it."));
            p.Children.Add(ColourPicker(new (string, string?)[]
            {
                ("#EEF0F3", "Mist"), ("#F3EEF8", "Lavender"), ("#EAF4F1", "Mint"), ("#F8EFE7", "Peach"),
                ("#EAF1FA", "Sky"), ("#F5F2E8", "Linen"), ("#F1F1F1", "Silver"), ("#FBF7EE", "Ivory"),
            }, () => _l.CustomLight, v => { _l.CustomLight = v; Changed(true); }));
        }

        p.Children.Add(Label("Accent"));
        p.Children.Add(ColourPicker(ThemeApplier.AccentPresets.Select(a => (a.Hex, (string?)a.Name)).ToArray(),
            () => _l.Accent, v => { _l.Accent = v; Changed(); }));

        var adv = Advanced(p, "Colours");
        adv.Children.Add(Label("Accent-coloured text (links, labels, tags)"));
        adv.Children.Add(Combo(new[] { "Automatic: always readable on the background", "Exactly the accent", "My own colour" }, (int)_l.AccentText,
            i => { _l.AccentText = (AccentTextMode)i; Changed(true); }, width: 320));
        if (_l.AccentText == AccentTextMode.Exact)
            adv.Children.Add(Hint("A dark accent on a dark background (or light on light) can be hard to read this way."));
        if (_l.AccentText == AccentTextMode.Custom)
            adv.Children.Add(ColourPicker(Presets(new[] { "#FFFFFF", "#E3C184", "#8FD3FF", "#9EF0C8", "#FFB3C7", "#1B1D21" }),
                () => _l.AccentTextColor, v => { _l.AccentTextColor = v; Changed(); }));
        adv.Children.Add(Toggle("Main buttons fade into a second colour", () => _l.AccentGradient, v => { _l.AccentGradient = v; Changed(true); }));
        if (_l.AccentGradient)
            adv.Children.Add(Indent(ColourPicker(ThemeApplier.AccentPresets.Select(a => (a.Hex, (string?)a.Name)).ToArray(),
                () => _l.Accent2, v => { _l.Accent2 = v; Changed(); })));
        if (_l.Mode == ThemeMode.Light)
            adv.Children.Add(Hint("While recording, the control bar, area picker and countdown stay dark so they read over any game."));
    }

    /// <summary>The logo's three colours: each follows the look unless set here.</summary>
    private void BuildLogo()
    {
        var p = Fold("Logo");
        p.Children.Add(Hint("Shown in the tray, on the taskbar (pinned too), in the Start menu and desktop shortcuts, title bars, About and the setup. " +
                            "Shortcuts change when you save. The program file itself keeps the original colours."));

        // A live preview, with the two quick choices beside it.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        var preview = new Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 16, 0), Source = LogoArt.ImageOf(LogoArt.For(_l)) };
        row.Children.Add(preview);
        var quick = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var match = new Button { Content = "Match my look", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6),
                                 ToolTip = "Blades in the text colour, one in the accent, on the background colour." };
        match.Click += (_, _) => { _l.LogoBlades = _l.LogoAccent = _l.LogoBack = ""; Changed(true); };
        var original = new Button { Content = "Original colours", HorizontalAlignment = HorizontalAlignment.Left,
                                    ToolTip = "Cream blades and a coral one on a near-black shard, as the logo was designed." };
        original.Click += (_, _) =>
        {
            _l.LogoBlades = ThemeApplier.Hex(LogoArt.Original.Blades);
            _l.LogoAccent = ThemeApplier.Hex(LogoArt.Original.Accent);
            _l.LogoBack = ThemeApplier.Hex(LogoArt.Original.Back);
            Changed(true);
        };
        quick.Children.Add(match);
        quick.Children.Add(original);
        row.Children.Add(quick);
        p.Children.Add(row);

        var auto = LogoArt.For(new Looks { Mode = _l.Mode, DarkSurface = _l.DarkSurface, LightSurface = _l.LightSurface,
                                           CustomDark = _l.CustomDark, CustomLight = _l.CustomLight, Accent = _l.Accent });
        void Part(string title, string follows, Color lookColour, Func<string> get, Action<string> set, string[] presets)
        {
            p.Children.Add(Label(title));
            p.Children.Add(Toggle(follows, () => string.IsNullOrEmpty(get()), on =>
            {
                // Switching it off starts from the colour it has now.
                set(on ? "" : ThemeApplier.Hex(lookColour));
                Changed(true);
            }));
            if (!string.IsNullOrEmpty(get()))
                p.Children.Add(Indent(ColourPicker(Presets(presets), get, v =>
                {
                    set(v);
                    preview.Source = LogoArt.ImageOf(LogoArt.For(_l));
                    Changed();
                })));
        }
        Part("The white blades", "Use the text colour", auto.Blades, () => _l.LogoBlades, v => _l.LogoBlades = v,
            new[] { "#FFFDF8", "#E8EAED", "#F2E9D8", "#DCEBFF", "#FFE6E1", "#1B1D21" });
        Part("The accent blade", "Use the accent", auto.Accent, () => _l.LogoAccent, v => _l.LogoAccent = v,
            new[] { "#EC6456", _l.Accent, "#E0594D", "#E8845A", "#A58BEA", "#6FB4E8", "#5CCB9E" });
        Part("The background shard", "Use the background colour", auto.Back, () => _l.LogoBack, v => _l.LogoBack = v,
            new[] { "#1B1C21", "#000000", "#16191F", "#0E1320", "#2B1E33", "#F4F5F7" });
    }

    private WindowLook[] AllWindows => new[] { _l.ClipList, _l.SettingsWindow, _l.Editor, _l.Popups };

    private void BuildGlass()
    {
        var p = Fold("Glass and see-through");
        if (!WindowLooks.GlassSupported)
            p.Children.Add(Hint("Glass needs Windows 11 (22H2 or later); on this PC only see-through (with a blur) applies."));

        var preset = new Button
        {
            Content = "✨  Liquid glass", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 8),
            ToolTip = "Frosted, milky glass on every window, with bright edges, a faint prism sheen, see-through cards and glass buttons. Fine-tune it after.",
        };
        preset.SetResourceReference(StyleProperty, "Primary");
        preset.Click += (_, _) =>
        {
            foreach (var w in AllWindows)
            {
                w.Glass = true;
                w.GlassKind = GlassKind.Acrylic;
                w.Tint = GlassTint.Frost;
                w.GlassTintPercent = 35;
            }
            _l.PanelOpacityPercent = 40;
            _l.GlassEdges = true;
            _l.GlassGrain = false;
            _l.GlassTitleBar = true;
            _l.PrismSheen = true;
            _l.PrismPercent = 7;
            _l.GlassText = GlassText.Auto;
            _l.GlassButtons = true;
            _l.Cards = CardStyle.Glass;
            _l.Corners = CornerStyle.Round;
            Changed(true);
            _note.Text = "Liquid glass on. Fine-tune it here; Save changes keeps it.";
        };
        p.Children.Add(preset);

        // The simple controls change every window together; Advanced sets them one by one.
        var all = AllWindows;
        p.Children.Add(Toggle("Glass on every window", () => all.Any(w => w.Glass), v => { foreach (var w in all) w.Glass = v; Changed(true); }));
        if (all.Any(w => w.Glass))
        {
            p.Children.Add(Label("Style"));
            p.Children.Add(Choices(new[] { ("Liquid", "Milky frosted glass"), ("Smoky", "Your background colour"), ("Tinted", "Your accent colour"), ("Clear", "Just the blur") },
                all[0].Tint switch { GlassTint.Frost => 0, GlassTint.Accent => 2, GlassTint.Clear => 3, _ => 1 },
                i => { foreach (var w in all) w.Tint = i switch { 0 => GlassTint.Frost, 2 => GlassTint.Accent, 3 => GlassTint.Clear, _ => GlassTint.Surface }; Changed(true); }, width: 128));
            p.Children.Add(SliderRow("Strength", 0, 100, () => all[0].GlassTintPercent,
                v => { foreach (var w in all) w.GlassTintPercent = (int)v; Changed(); }, "{0}%", tip: "How much colour sits on the glass."));
        }
        // Shown as how see-through (0% solid), kept as how solid (100% solid).
        p.Children.Add(SliderRow("See-through", 0, 100, () => all[0].SeeThroughPercent,
            v => { foreach (var w in all) w.SeeThroughPercent = (int)v; Changed(); }, "{0}%", step: 5,
            tip: "0% is solid (or your glass as it is). Higher shows more of what's behind, through a blur: up to 35% a deep frosted blur, from 40% a light one, and 100% the clearest. The clip list goes back to 0% while a clip plays."));
        if (!WindowLooks.BlurSupported)
            p.Children.Add(Hint("See-through needs Windows 10 (April 2018 update) or later."));

        var adv = Advanced(p, "Glass and see-through");
        foreach (var (title, get) in new (string, Func<WindowLook>)[]
        {
            ("Clip list", () => _l.ClipList), ("Settings", () => _l.SettingsWindow),
            ("Editor", () => _l.Editor), ("Dialogs and small windows", () => _l.Popups),
        })
        {
            var w = get();
            adv.Children.Add(Label(title));
            var box = new StackPanel { Margin = new Thickness(12, 0, 0, 4) };
            box.Children.Add(Toggle("Glass", () => w.Glass, v => { w.Glass = v; Changed(true); }));
            if (w.Glass)
            {
                box.Children.Add(Indent(Combo(new[] { "Acrylic (frosted blur)", "Mica (wallpaper tint)", "Mica alt (deeper tint)" },
                    w.GlassKind switch { GlassKind.Mica => 1, GlassKind.MicaAlt => 2, _ => 0 },
                    i => { w.GlassKind = i switch { 1 => GlassKind.Mica, 2 => GlassKind.MicaAlt, _ => GlassKind.Acrylic }; Changed(); })));
                box.Children.Add(Indent(Combo(new[] { "Smoky (background colour)", "Liquid (milky frosted)", "Tinted (accent colour)", "Clear (just the blur)" },
                    (int)w.Tint, i => { w.Tint = (GlassTint)i; Changed(); })));
                box.Children.Add(Indent(SliderRow("Strength", 0, 100, () => w.GlassTintPercent, v => { w.GlassTintPercent = (int)v; Changed(); }, "{0}%")));
            }
            box.Children.Add(SliderRow("See-through", 0, 100, () => w.SeeThroughPercent, v => { w.SeeThroughPercent = (int)v; Changed(); }, "{0}%", step: 5, tip: "0% is solid; 100% the clearest."));
            box.Children.Add(Toggle("Show the background (Background section)", () => w.ShowBackground, v => { w.ShowBackground = v; Changed(); }));
            adv.Children.Add(box);
        }
        adv.Children.Add(Label("Material"));
        adv.Children.Add(SliderRow("Panels and cards solid", 10, 100, () => _l.PanelOpacityPercent, v => { _l.PanelOpacityPercent = (int)v; Changed(); }, "{0}%",
            tip: "Lower lets more of the glass or picture through panels, cards and buttons."));
        adv.Children.Add(Toggle("Bright cut-glass edges and a highlight on cards", () => _l.GlassEdges, v => { _l.GlassEdges = v; Changed(); }));
        adv.Children.Add(Toggle("Fine grain, as real frosted glass has", () => _l.GlassGrain, v => { _l.GlassGrain = v; Changed(); }));
        adv.Children.Add(Toggle("Prism sheen (a soft rainbow across the glass)", () => _l.PrismSheen, v => { _l.PrismSheen = v; Changed(true); }));
        if (_l.PrismSheen)
            adv.Children.Add(Indent(SliderRow("Strength", 3, 30, () => _l.PrismPercent, v => { _l.PrismPercent = (int)v; Changed(); }, "{0}%")));
        adv.Children.Add(Label("Text over glass"));
        adv.Children.Add(Combo(new[] { "Automatic (reads on the glass)", "Always light", "Always dark", "My own colour" }, (int)_l.GlassText,
            i => { _l.GlassText = (GlassText)i; Changed(true); }));
        if (_l.GlassText == GlassText.Custom)
            adv.Children.Add(ColourPicker(Presets(new[] { "#FFFFFF", "#F2E9D8", "#DCEBFF", "#14161A", "#2B2140", "#0E2A24" }),
                () => _l.GlassTextColor, v => { _l.GlassTextColor = v; Changed(); }));
    }

    private void BuildBackground()
    {
        var p = Fold("Background");
        p.Children.Add(Choices(new[] { ("Plain", "The background colour"), ("Gradient", "Two colours blended"), ("Picture", "An image of yours") },
            (int)_l.Background, i => { _l.Background = (BackgroundFill)i; Changed(true); }));
        var adv = new StackPanel();   // goes under "Advanced", after the main options

        if (_l.Background == BackgroundFill.Gradient)
        {
            p.Children.Add(Label("From"));
            p.Children.Add(ColourPicker(GradientPresets, () => _l.GradientFrom, v => { _l.GradientFrom = v; Changed(); }));
            p.Children.Add(Label("To"));
            p.Children.Add(ColourPicker(GradientPresets, () => _l.GradientTo, v => { _l.GradientTo = v; Changed(); }));
            adv.Children.Add(SliderRow("Angle", 0, 345, () => _l.GradientAngle, v => { _l.GradientAngle = (int)v; Changed(); }, "{0}°", step: 15));
        }
        else if (_l.Background == BackgroundFill.Image)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            var pick = new Button { Content = "Choose a picture…" };
            var name = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), MaxWidth = 340,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = string.IsNullOrEmpty(_l.ImagePath) ? "No picture yet" : System.IO.Path.GetFileName(_l.ImagePath),
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            pick.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
                if (dlg.ShowDialog(_owner) != true) return;
                _l.ImagePath = dlg.FileName;
                Changed(true);
            };
            row.Children.Add(pick);
            row.Children.Add(name);
            p.Children.Add(row);
            p.Children.Add(SliderRow("Dim", 0, 90, () => _l.ImageDimPercent, v => { _l.ImageDimPercent = (int)v; Changed(); }, "{0}%",
                tip: "Covers the picture with the background colour, so text stays readable."));
            adv.Children.Add(Label("Fit"));
            adv.Children.Add(Combo(new[] { "Fill the window (crop)", "Fit inside", "Stretch", "Tile" }, (int)_l.ImageFit,
                i => { _l.ImageFit = (ImageFit)i; Changed(); }));
            adv.Children.Add(SliderRow("Blur", 0, 40, () => _l.ImageBlur, v => { _l.ImageBlur = (int)v; Changed(); }, "{0}"));
            adv.Children.Add(Hint("Share codes leave the picture out: a friend picks their own."));
        }

        adv.Children.Add(Toggle("Accent glow in a corner", () => _l.AccentGlow, v => { _l.AccentGlow = v; Changed(true); }));
        if (_l.AccentGlow)
        {
            adv.Children.Add(Indent(Combo(new[] { "Top left", "Top right", "Bottom left", "Bottom right", "Centre" }, (int)_l.GlowCorner,
                i => { _l.GlowCorner = (GlowCorner)i; Changed(); })));
            adv.Children.Add(Indent(SliderRow("Strength", 5, 80, () => _l.GlowPercent, v => { _l.GlowPercent = (int)v; Changed(); }, "{0}%")));
        }
        Advanced(p, "Background").Children.Add(adv);
    }

    private void BuildTextAndShape()
    {
        var p = Fold("Text and shape");
        p.Children.Add(Label("Font"));
        var fonts = new List<string> { "Persocord default (Segoe UI Variable)" };
        fonts.AddRange(Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        int at = string.IsNullOrWhiteSpace(_l.Font) ? 0 : Math.Max(0, fonts.FindIndex(f => f.Equals(_l.Font, StringComparison.OrdinalIgnoreCase)));
        p.Children.Add(Combo(fonts.ToArray(), at, i => { _l.Font = i <= 0 ? "" : fonts[i]; Changed(); }, width: 300));
        p.Children.Add(Label("Corners"));
        p.Children.Add(Choices(new[] { ("Sharp", "Square edges"), ("Subtle", "Slightly rounded"), ("Rounded", "Persocord's own"), ("Round", "Soft and bubbly") },
            (int)_l.Corners, i => { _l.Corners = (CornerStyle)i; Changed(true); }));
        p.Children.Add(Label("Spacing"));
        p.Children.Add(Choices(new[] { ("Compact", "More on screen"), ("Normal", "Balanced"), ("Comfy", "More room") },
            (int)_l.Density, i => { _l.Density = (Density)i; Changed(true); }));

        var adv = Advanced(p, "Text and shape");
        adv.Children.Add(SliderRow("Text size", 80, 140, () => _l.TextSizePercent, v => { _l.TextSizePercent = (int)v; Changed(); }, "{0}%", step: 5, onRelease: true));
        adv.Children.Add(SliderRow("Everything (UI scale)", 80, 150, () => _l.UiScalePercent, v => { _l.UiScalePercent = (int)v; Changed(); }, "{0}%", step: 5, onRelease: true));
        adv.Children.Add(Toggle("Glass buttons (see-through, with a soft shine)", () => _l.GlassButtons, v => { _l.GlassButtons = v; Changed(); }));
        adv.Children.Add(Toggle("Outline buttons", () => _l.ButtonOutlines, v => { _l.ButtonOutlines = v; Changed(); }));
        adv.Children.Add(SliderRow("Notification size", 75, 150, () => _l.ToastSizePercent, v => { _l.ToastSizePercent = (int)v; Changed(); }, "{0}%", step: 5,
            tip: "Persocord's own notifications (Settings → General)."));
    }

    private void BuildClipList()
    {
        var p = Fold("Clip list");
        p.Children.Add(Label("Cards"));
        p.Children.Add(Choices(new[] { ("Filled", "Solid cards"), ("Outlined", "Just an edge"), ("Glass", "See-through"), ("Flat", "No card at all") },
            (int)_l.Cards, i => { _l.Cards = (CardStyle)i; Changed(true); }));
        p.Children.Add(Label("Thumbnails"));
        p.Children.Add(Choices(new[] { ("Small", ""), ("Medium", ""), ("Large", ""), ("Huge", "") },
            (int)_l.Thumbs, i => { _l.Thumbs = (ThumbSize)i; Changed(true); }, width: 96));

        var adv = Advanced(p, "Clip list");
        adv.Children.Add(Label("Show"));
        var wrap = new WrapPanel();
        foreach (var (text, get, set) in new (string, Func<bool>, Action<bool>)[]
        {
            ("Length on the thumbnail", () => _l.ShowDuration, v => _l.ShowDuration = v),
            ("Size", () => _l.ShowSize, v => _l.ShowSize = v),
            ("Date", () => _l.ShowDate, v => _l.ShowDate = v),
            ("Folder", () => _l.ShowFolder, v => _l.ShowFolder = v),
            ("Tags", () => _l.ShowTags, v => _l.ShowTags = v),
        })
        {
            var t = Toggle(text, get, v => { set(v); Changed(); });
            t.Margin = new Thickness(0, 6, 18, 0);
            wrap.Children.Add(t);
        }
        adv.Children.Add(wrap);
        adv.Children.Add(Toggle("Cards rise under the pointer", () => _l.CardLift, v => { _l.CardLift = v; Changed(); }));
        adv.Children.Add(Toggle("Accent stripe on the selected clip", () => _l.SelectionStripe, v => { _l.SelectionStripe = v; Changed(); }));
    }

    private void BuildRecording()
    {
        var p = Fold("While recording");
        p.Children.Add(Hint("These show from the next recording."));
        p.Children.Add(Label("Frame around the area"));
        p.Children.Add(Choices(new[] { ("Clean", "A faint line with crisp corners"), ("Brushed metal", "Light runs along it like a machined edge"), ("Solid", "A plain, stronger line") },
            (int)_l.FrameStyle, i => { _l.FrameStyle = (FrameStyle)i; Changed(true); }, width: 170));
        p.Children.Add(ColourPicker(_l.FrameStyle == FrameStyle.Metal ? MetalPresets : Presets(Bright),
            () => _l.FrameColor, v => { _l.FrameColor = v; Changed(); }));
        p.Children.Add(SliderRow("Thickness", 1, 12, () => _l.FrameThickness, v => { _l.FrameThickness = (int)v; Changed(); }, "{0} px"));

        p.Children.Add(Label("Control bar"));
        p.Children.Add(Toggle("Words next to the icons", () => _l.BarLabels, v => { _l.BarLabels = v; Changed(); }));
        p.Children.Add(Label("Bar colour"));
        p.Children.Add(ColourPicker(Presets(new[] { "#121418", "#1C1F26", "#0A0B0D", "#2A2E36", "#1E2A3A", "#2B1E33", "#F4F5F7" }),
            () => _l.BarColor, v => { _l.BarColor = v; Changed(); }));
        p.Children.Add(Label("Done button"));
        p.Children.Add(ColourPicker(Presets(new[] { "#F4F5F7", _l.Accent, "#4ADE80", "#60A5FA", "#F0A33E", "#F472B6" }),
            () => _l.BarDoneColor, v => { _l.BarDoneColor = v; Changed(); }));

        var adv = Advanced(p, "While recording");
        adv.Children.Add(Label("Corner marks"));
        adv.Children.Add(ColourPicker(CornerPresets, () => _l.FrameCornerColor, v => { _l.FrameCornerColor = v; Changed(); }));
        adv.Children.Add(Toggle("Heartbeat: corners and the live dot pulse slowly", () => _l.FramePulse, v => { _l.FramePulse = v; Changed(); }));
        adv.Children.Add(Label("Click rings"));
        adv.Children.Add(ColourPicker(Presets(Bright), () => _l.ClickRingColor, v => { _l.ClickRingColor = v; Changed(); }));
        adv.Children.Add(Label("Control bar: what it shows"));
        adv.Children.Add(Toggle("Timer", () => _l.BarShowTimer, v => { _l.BarShowTimer = v; Changed(); }));
        adv.Children.Add(Toggle("Pause", () => _l.BarShowPause, v => { _l.BarShowPause = v; Changed(); }));
        adv.Children.Add(Toggle("PC audio on/off", () => _l.BarShowPcAudio, v => { _l.BarShowPcAudio = v; Changed(); }));
        adv.Children.Add(Toggle("Mic on/off", () => _l.BarShowMic, v => { _l.BarShowMic = v; Changed(); }));
        adv.Children.Add(Toggle("Restart", () => _l.BarShowRestart, v => { _l.BarShowRestart = v; Changed(); }));
        adv.Children.Add(Toggle("Settings", () => _l.BarShowSettings, v => { _l.BarShowSettings = v; Changed(); }));
        adv.Children.Add(Toggle("Cancel", () => _l.BarShowCancel, v => { _l.BarShowCancel = v; Changed(); }));
        adv.Children.Add(Toggle("Upload (save and get a link)", () => _l.BarShowUpload, v => { _l.BarShowUpload = v; Changed(); }));
        adv.Children.Add(Hint("Done is always there. The pause, stop and cancel hotkeys work either way."));
        adv.Children.Add(Label("Control bar: look"));
        adv.Children.Add(SliderRow("Solid", 30, 100, () => _l.BarOpacityPercent, v => { _l.BarOpacityPercent = (int)v; Changed(); }, "{0}%"));
        adv.Children.Add(SliderRow("Size", 70, 160, () => _l.BarScalePercent, v => { _l.BarScalePercent = (int)v; Changed(); }, "{0}%", step: 5));
        adv.Children.Add(Toggle("Shadow", () => _l.BarShadow, v => { _l.BarShadow = v; Changed(); }));
        adv.Children.Add(Toggle("Progress line next to the timer", () => _l.BarShowTrack, v => { _l.BarShowTrack = v; Changed(); }));
        adv.Children.Add(Label("Countdown (when it's on, in Settings → Recording)"));
        adv.Children.Add(Choices(new[] { ("Ring", "A ring that runs down with the number"), ("Big", "A large number"), ("Minimal", "A small pill at the top") },
            (int)_l.Countdown, i => { _l.Countdown = (CountdownStyle)i; Changed(true); }));
        adv.Children.Add(SliderRow("Size", 50, 250, () => _l.CountdownScalePercent, v => { _l.CountdownScalePercent = (int)v; Changed(); }, "{0}%", step: 10));
        adv.Children.Add(ColourPicker(Presets(Bright), () => _l.CountdownColor, v => { _l.CountdownColor = v; Changed(); }));
        adv.Children.Add(Toggle("Tint the area during the countdown", () => _l.CountdownDim, v => { _l.CountdownDim = v; Changed(); }));
    }

    private void BuildPicker()
    {
        var p = Fold("Area picker");
        p.Children.Add(Label("Highlight"));
        p.Children.Add(ColourPicker(Presets(Bright), () => _l.PickerColor, v => { _l.PickerColor = v; Changed(); }));
        p.Children.Add(SliderRow("Darken the rest of the screen", 0, 90, () => _l.PickerDimPercent, v => { _l.PickerDimPercent = (int)v; Changed(); }, "{0}%", step: 5));
        var adv = Advanced(p, "Area picker");
        adv.Children.Add(Toggle("Crosshair lines following the pointer", () => _l.PickerGuides, v => { _l.PickerGuides = v; Changed(); }));
        adv.Children.Add(Toggle("Size readout while dragging", () => _l.PickerReadout, v => { _l.PickerReadout = v; Changed(); }));
        adv.Children.Add(Toggle("Key hints", () => _l.PickerHint, v => { _l.PickerHint = v; Changed(); }));
        adv.Children.Add(Toggle("Rule-of-thirds lines inside the area", () => _l.PickerThirds, v => { _l.PickerThirds = v; Changed(); }));
    }

    private void BuildPlayerEditor()
    {
        var p = Fold("Player and editor");
        p.Children.Add(Label("Player controls"));
        p.Children.Add(Choices(new[] { ("Minimal", "Clean, straight over the video"), ("Glass", "A floating glass bar") },
            (int)_l.PlayerStyle, i => { _l.PlayerStyle = (PlayerStyle)i; Changed(true); }));
        p.Children.Add(Label("Seek bar"));
        p.Children.Add(Choices(new[] { ("White", "Reads on any clip"), ("Accent", "Your accent, kept light"), ("My own", "Pick a colour") },
            (int)_l.SeekStyle, i =>
            {
                _l.SeekStyle = (SeekStyle)i;
                if (_l.SeekStyle == SeekStyle.Custom && string.IsNullOrWhiteSpace(_l.SeekColor)) _l.SeekColor = "#FFFFFF";
                Changed(true);
            }, width: 128));
        if (_l.SeekStyle == SeekStyle.Custom)
            p.Children.Add(Indent(ColourPicker(Presets(Bright), () => _l.SeekColor, v => { _l.SeekColor = v; Changed(); })));

        var adv = Advanced(p, "Player and editor");
        adv.Children.Add(SliderRow("Seek bar thickness", 2, 10, () => _l.SeekThickness, v => { _l.SeekThickness = (int)v; Changed(); }, "{0} px"));
        adv.Children.Add(SliderRow("Shade behind the controls", 0, 100, () => _l.PlayerShadePercent, v => { _l.PlayerShadePercent = (int)v; Changed(); }, "{0}%", step: 5,
            tip: "How dark the soft shades along the top and bottom are (and the glass bar, in the glass style)."));
        adv.Children.Add(Toggle("Soft shade along the top and bottom of the video", () => _l.PlayerEdgeShade, v => { _l.PlayerEdgeShade = v; Changed(); }));
        adv.Children.Add(SliderRow("Controls stay up after a key", 1, 10, () => _l.PlayerHideSeconds, v => { _l.PlayerHideSeconds = v; Changed(); }, "{0:0.#} s", step: 0.5,
            tip: "While a clip plays, the controls show when the mouse is over them, and for this long after a key (and the mouse pointer hides after this long on the video)."));
        adv.Children.Add(Label("Editor timeline: background"));
        adv.Children.Add(ColourPicker(TimelineBackPresets, () => _l.TimelineBackground, v => { _l.TimelineBackground = v; Changed(); }));
        adv.Children.Add(Label("Video clips"));
        adv.Children.Add(ColourPicker(new (string, string?)[] { ("#2F6FA8", "Blue"), ("#6A4FB0", "Purple"), ("#B0563C", "Rust"), ("#2E8C85", "Teal"), ("#7A7F88", "Grey"), ("#A8862F", "Gold") },
            () => _l.TimelineVideoColor, v => { _l.TimelineVideoColor = v; Changed(); }));
        adv.Children.Add(Label("Sound clips"));
        adv.Children.Add(ColourPicker(new (string, string?)[] { ("#3E7D3A", "Green"), ("#2E8C85", "Teal"), ("#8C6A2E", "Amber"), ("#7D3A5E", "Berry"), ("#3A5C7D", "Steel"), ("#7A7F88", "Grey") },
            () => _l.TimelineAudioColor, v => { _l.TimelineAudioColor = v; Changed(); }));
        adv.Children.Add(Label("Playhead"));
        adv.Children.Add(ColourPicker(Presets(Bright), () => _l.PlayheadColor, v => { _l.PlayheadColor = v; Changed(); }));
    }

    private void BuildShare()
    {
        var p = Fold("Share, save and reset");
        p.Children.Add(Hint("A look code holds only how Persocord looks, never your settings (those have their own code under About). " +
                            "Your background picture stays on this PC."));
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var copy = new Button { Content = "Copy my look code", Margin = new Thickness(0, 0, 8, 8) };
        copy.SetResourceReference(StyleProperty, "Primary");
        copy.Click += async (_, _) =>
        {
            var code = LookStore.ExportCode(_l);
            bool ok = await ShareService.CopyTextAsync(code);
            _note.Text = ok ? $"Look code copied ({code.Length} characters). Friends paste it in Import a look code."
                            : "Couldn't reach the clipboard. Try again.";
        };
        var import = new Button { Content = "Import a look code…", Margin = new Thickness(0, 0, 8, 8) };
        import.Click += (_, _) => ImportCode();
        var save = new Button { Content = "Save to a file…", Margin = new Thickness(0, 0, 8, 8) };
        save.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"Persocord look {DateTime.Now:yyyy-MM-dd}.json", Filter = "Persocord look (*.json)|*.json" };
            if (dlg.ShowDialog(_owner) != true) return;
            try
            {
                LookStore.Save(_l);
                LookStore.Export(dlg.FileName);
                _note.Text = "Saved to " + dlg.FileName;
            }
            catch (Exception ex) { Log.Error("export look", ex); _note.Text = "The file couldn't be written there."; }
        };
        var load = new Button { Content = "Load a file…", Margin = new Thickness(0, 0, 8, 8) };
        load.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Persocord look (*.json)|*.json|All files|*.*" };
            if (dlg.ShowDialog(_owner) != true) return;
            var err = LookStore.Import(dlg.FileName, out var looks);
            if (err != null || looks == null) { _note.Text = err ?? "That file couldn't be read."; return; }
            if (string.IsNullOrEmpty(looks.ImagePath)) looks.ImagePath = _l.ImagePath;
            _l = looks;
            Changed(true);
            _note.Text = "Look loaded. Save changes to keep it.";
        };
        var reset = new Button { Content = "Reset the look…", Margin = new Thickness(0, 0, 8, 8) };
        reset.SetResourceReference(StyleProperty, "Danger");
        reset.Click += (_, _) =>
        {
            if (Dialog.Show(_owner, "Put every colour and style back to how Persocord ships? (Settings aren't touched.)",
                    "Reset the look", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _l = new Looks();
            Changed(true);
            _note.Text = "Back to the original look. Save changes to keep it.";
        };
        foreach (var b in new[] { copy, import, save, load, reset }) row.Children.Add(b);
        p.Children.Add(row);
    }

    private void ImportCode()
    {
        var code = Dialog.AskCode(_owner, "Import a look code",
            "Paste the look code a friend sent you (it starts with LOOK1-). Your settings stay as they are.");
        if (code == null) return;

        // A code holds only what differs from the original look, so it's
        // applied over the original look (keeping your own picture).
        var incoming = new Looks { ImagePath = _l.ImagePath };
        int count;
        try { count = LookStore.ImportCode(code, incoming); }
        catch (FormatException ex) { Dialog.Show(_owner, ex.Message, "Import a look", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _l = LookStore.Sanitise(incoming);
        Changed(true);
        _note.Text = count == 0
            ? "That code is Persocord's original look. It's showing now; save changes to keep it."
            : "Look imported and showing. Save changes to keep it.";
    }

    /* ---- building blocks ----------------------------------------------------------------- */

    private StackPanel Fold(string title)
    {
        var body = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
        var e = new Expander { Header = title, Content = body, IsExpanded = _open.Contains(title) };
        e.SetResourceReference(StyleProperty, "Fold");
        e.Expanded += (_, _) => _open.Add(title);
        e.Collapsed += (_, _) => _open.Remove(title);
        Children.Add(e);
        return body;
    }

    /// <summary>
    /// The very specific options of a section, folded away under "Advanced"
    /// (remembered open or closed while Settings is open).
    /// </summary>
    private StackPanel Advanced(StackPanel section, string title)
    {
        var key = title + "·advanced";
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var e = new Expander { Header = "Advanced", Content = body, IsExpanded = _open.Contains(key), Margin = new Thickness(0, 12, 0, 0) };
        e.SetResourceReference(StyleProperty, "Fold");
        e.Expanded += (_, _) => _open.Add(key);
        e.Collapsed += (_, _) => _open.Remove(key);
        section.Children.Add(e);
        return body;
    }

    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        t.SetResourceReference(StyleProperty, "Meta");
        return t;
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6), FontWeight = FontWeights.SemiBold };
        return t;
    }

    private static FrameworkElement Indent(FrameworkElement e)
    {
        e.Margin = new Thickness(26, e.Margin.Top, e.Margin.Right, e.Margin.Bottom);
        return e;
    }

    private static (string, string?)[] Presets(string[] hexes) => hexes.Select(h => (h, (string?)null)).ToArray();

    private static CheckBox Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = get(), Margin = new Thickness(0, 6, 0, 0) };
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        return c;
    }

    private static ComboBox Combo(string[] items, int selected, Action<int> pick, double width = 240)
    {
        var c = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) pick(c.SelectedIndex); };
        return c;
    }

    /// <summary>A labelled slider with its value. <paramref name="onRelease"/>: applies only when let go (for things that move the slider itself, like scale).</summary>
    private static FrameworkElement SliderRow(string label, double min, double max, Func<double> get, Action<double> set,
                                              string format, double step = 1, string? tip = null, bool onRelease = false)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, ToolTip = tip };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Body");
        var s = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        var value = new TextBlock { Text = string.Format(format, s.Value), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        bool dragging = false;
        s.AddHandler(SliderDrag.StartedEvent, new RoutedEventHandler((_, _) => dragging = true));
        s.AddHandler(SliderDrag.EndedEvent, new RoutedEventHandler((_, _) => { dragging = false; if (onRelease) set(s.Value); }));
        s.ValueChanged += (_, _) =>
        {
            value.Text = string.Format(format, s.Value);
            if (!onRelease || !dragging) set(s.Value);
        };
        Grid.SetColumn(s, 1);
        Grid.SetColumn(value, 2);
        g.Children.Add(name);
        g.Children.Add(s);
        g.Children.Add(value);
        return g;
    }

    /// <summary>A row of selectable cards (one is picked).</summary>
    private FrameworkElement Choices((string Title, string Detail)[] options, int selected, Action<int> pick, double width = 150)
    {
        var wrap = new WrapPanel();
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = options[i].Title, FontWeight = FontWeights.SemiBold });
            if (options[i].Detail.Length > 0)
            {
                var d = new TextBlock { Text = options[i].Detail, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                d.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                inner.Children.Add(d);
            }
            var card = new Border
            {
                Width = width, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 9, 12, 9),
                BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Child = inner,
            };
            card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
            card.SetResourceReference(Border.BackgroundProperty, "Panel");
            if (i == selected) card.SetResourceReference(Border.BorderBrushProperty, "Brass");
            else card.BorderBrush = Brushes.Transparent;
            card.MouseLeftButtonUp += (_, _) => { if (index != selected) pick(index); };
            wrap.Children.Add(card);
        }
        return wrap;
    }

    private FrameworkElement StyleCard(SurfaceStyle style, string name, string blurb)
    {
        var p = ThemeApplier.For(_l, style);
        bool on = _l.Surface == style;
        var swatch = new Border
        {
            Height = 44, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(p.Bezel),
            BorderBrush = new SolidColorBrush(p.Line), BorderThickness = new Thickness(1),
            Child = new Border
            {
                Margin = new Thickness(8), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(p.Panel),
                Child = new TextBlock { Text = "Aa", Foreground = new SolidColorBrush(p.Ink), FontSize = 12, Margin = new Thickness(6, 3, 0, 0) },
            },
        };
        var inner = new StackPanel();
        inner.Children.Add(swatch);
        inner.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var b = new TextBlock { Text = blurb, FontSize = 11.5 };
        b.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        inner.Children.Add(b);
        var card = new Border
        {
            Width = 140, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(10),
            BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Child = inner,
        };
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        if (on) card.SetResourceReference(Border.BorderBrushProperty, "Brass");
        else card.BorderBrush = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (Looks.IsLight(style)) _l.LightSurface = style; else _l.DarkSurface = style;
            Changed(true);
        };
        return card;
    }

    /// <summary>A row of round swatches plus a box for any colour.</summary>
    private static FrameworkElement ColourPicker((string hex, string? name)[] presets, Func<string> get, Action<string> set)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        var hexBox = new TextBox { Width = 92, Margin = new Thickness(6, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        var swatches = new Border[presets.Length];

        void Mark()
        {
            var current = (get() ?? "").Trim().ToUpperInvariant();
            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].hex.ToUpperInvariant() == current) swatches[i].SetResourceReference(Border.BorderBrushProperty, "Ink");
                else swatches[i].BorderBrush = Brushes.Transparent;
            }
        }

        for (int i = 0; i < presets.Length; i++)
        {
            var (hex, name) = presets[i];
            var sw = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Padding = new Thickness(3),
                ToolTip = name ?? hex,
                Child = new Ellipse { Fill = new SolidColorBrush(ThemeApplier.Parse(hex, Colors.Gray)) },
            };
            sw.MouseLeftButtonUp += (_, _) => { set(hex); hexBox.Text = hex; Mark(); };
            swatches[i] = sw;
            row.Children.Add(sw);
        }

        hexBox.Text = get();
        hexBox.ToolTip = "Any colour, like #FF8800";
        hexBox.LostFocus += (_, _) =>
        {
            var c = ThemeApplier.Parse(hexBox.Text, Colors.Transparent);
            if (c == Colors.Transparent) { hexBox.Text = get(); return; }
            set(ThemeApplier.Hex(c));
            hexBox.Text = get();
            Mark();
        };
        hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        // A complete colour counts as soon as it's typed, not only on leaving the box.
        hexBox.TextChanged += (_, _) =>
        {
            var t = hexBox.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, "^#?[0-9A-Fa-f]{6}$")) return;
            var hex = ThemeApplier.Hex(ThemeApplier.Parse(t, Colors.Gray));
            if (string.Equals(hex, get(), StringComparison.OrdinalIgnoreCase)) return;
            set(hex);
            Mark();
        };
        row.Children.Add(hexBox);
        Mark();
        return row;
    }
}
