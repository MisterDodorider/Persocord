using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// The editor's Properties, on the right: for the selected clip, where its
/// picture sits (position, size, turn, see-through, quick layouts), its crop,
/// its own volume and its fades; with nothing selected, the whole edit's
/// settings. Every change shows at once and is one undo step.
/// </summary>
public sealed class PropertiesPanel : ScrollViewer
{
    private readonly StackPanel _body = new();
    private readonly List<Action> _readers = new();
    private EditTimeline? _tl;
    private List<TClip> _clips = new();
    private UIElement? _project;

    /// <summary>A change is about to start: keep the state for undo.</summary>
    public event Action? Begin;
    /// <summary>Picture placement changed (show it, nothing saved yet).</summary>
    public event Action? Placing;
    /// <summary>Sound changed (volume): heard at once.</summary>
    public event Action? Sounding;
    /// <summary>A change ended (save it, refresh the timeline).</summary>
    public event Action? Committed;
    /// <summary>"Crop on the picture" was switched.</summary>
    public event Action<bool>? CropOnPicture;
    /// <summary>A clip's length changed (its speed): the timeline and the preview follow at once.</summary>
    public event Action? Timing;
    /// <summary>A text clip's words or look changed: its picture is drawn again.</summary>
    public event Action<TClip>? TextChanged;

    private TextBox? _textBox;

    /// <summary>Puts the cursor in the text box (a text clip just added), with its words selected.</summary>
    public void FocusText()
    {
        if (_textBox == null) return;
        _textBox.Focus();
        _textBox.SelectAll();
    }

    public bool CropOnPictureOn { get; set; }

    /// <summary>The Size at which a clip covers the whole frame (for "Fill").</summary>
    public Func<TClip, double>? CoverScale { get; set; }

    public PropertiesPanel()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _body;
    }

    /// <summary>The settings shown when nothing is selected (built by the window).</summary>
    public void SetProject(UIElement project) => _project = project;

    /// <summary>Shows the selection's properties (or the edit's, with nothing selected).</summary>
    public void Show(EditTimeline? tl, IEnumerable<TClip> selected)
    {
        _tl = tl;
        _clips = selected.ToList();
        _readers.Clear();
        _body.Children.Clear();
        _textBox = null;
        if (tl == null) return;

        var video = _clips.FirstOrDefault(c => c.Track < tl.Tracks.Count && tl.Tracks[c.Track].Type == TrackType.Video);
        var sounds = _clips.Where(c => c.Track < tl.Tracks.Count && tl.Tracks[c.Track].Type == TrackType.Audio).ToList();
        if (_clips.Count == 0)
        {
            if (_project != null) _body.Children.Add(_project);
            return;
        }

        var first = video ?? _clips[0];
        var title = new TextBlock { Text = first.Label.Length > 0 ? first.Label : "Clip", FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, 2) };
        var what = new TextBlock
        {
            Text = (first.IsText ? "Text" : first.IsImage ? "Picture" : video != null ? "Video" : "Sound") + $"  ·  {TimelineView.Clock(first.Length)}" +
                   (_clips.Count > 1 ? $"  ·  {_clips.Count} pieces selected" : ""),
            FontSize = 11.5, Margin = new Thickness(2, 0, 0, 10),
        };
        what.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _body.Children.Add(title);
        _body.Children.Add(what);

        if (video is { IsText: true, Text: not null }) _body.Children.Add(TextCard(video));

        if (video != null)
        {
            var c = video;
            var place = new StackPanel();
            place.Children.Add(Number("Position X", -200, 200, 1, "%", () => c.X * 100, v => c.X = v / 100, Placing));
            place.Children.Add(Number("Position Y", -200, 200, 1, "%", () => c.Y * 100, v => c.Y = v / 100, Placing));
            place.Children.Add(Number("Size", 2, 400, 1, "%", () => c.Scale * 100, v => c.Scale = v / 100, Placing));
            place.Children.Add(Number("Turn", -180, 180, 1, "°", () => c.Rotation, v => c.Rotation = v, Placing));
            place.Children.Add(Number("Opacity", 0, 100, 1, "%", () => c.Opacity * 100, v => c.Opacity = v / 100, Placing));
            if (!c.IsText) place.Children.Add(Presets(c));
            _body.Children.Add(Card(c.IsText ? "Placement" : "Picture", place, () => { c.X = c.Y = c.Rotation = 0; c.Scale = c.Opacity = 1; }));
        }

        if (video is { IsText: false })
        {
            var c = video;
            var crop = new StackPanel();
            var onPic = new CheckBox { Content = "Crop on the picture", IsChecked = CropOnPictureOn, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Drag the picture's edges on the preview (or double-click the picture)" };
            onPic.Click += (_, _) => CropOnPicture?.Invoke(onPic.IsChecked == true);
            crop.Children.Add(onPic);
            crop.Children.Add(Number("Left", 0, 95, 0.5, "%", () => c.CropL * 100, v => c.CropL = v / 100, Placing));
            crop.Children.Add(Number("Top", 0, 95, 0.5, "%", () => c.CropT * 100, v => c.CropT = v / 100, Placing));
            crop.Children.Add(Number("Right", 0, 95, 0.5, "%", () => c.CropR * 100, v => c.CropR = v / 100, Placing));
            crop.Children.Add(Number("Bottom", 0, 95, 0.5, "%", () => c.CropB * 100, v => c.CropB = v / 100, Placing));
            _body.Children.Add(Card("Crop", crop, () => { c.CropL = c.CropT = c.CropR = c.CropB = 0; }));
        }

        if (sounds.Count > 0)
        {
            var s0 = sounds[0];
            var vol = Number("Volume", 0, 200, 1, "%", () => s0.Volume * 100, v => { foreach (var s in sounds) s.Volume = v / 100; }, Sounding);
            var note = Note(sounds.Count > 1 ? $"For its {sounds.Count} sound pieces, on top of each track's own volume." : "On top of the track's own volume.");
            _body.Children.Add(Card("Sound", new StackPanel { Children = { vol, note } }, () => { foreach (var s in sounds) s.Volume = 1; }));
        }

        // Speed: the selected footage (its picture and its sound together), not pictures or text.
        var timed = _clips.Where(x => !x.IsImage).ToList();
        if (timed.Count > 0)
        {
            var t0 = timed[0];
            var sp = Number("Speed", 0.25, 4, 0.05, "×", () => t0.Speed, v => _tl?.SetSpeed(timed, v), Timing, decimals: 2);
            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var v in new[] { 0.5, 1, 1.5, 2, 4 })
            {
                var b = new Button { Content = v == 1 ? "Normal" : $"{v}×", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4), FontSize = 11.5 };
                b.SetResourceReference(StyleProperty, "Ghost");
                double speed = v;
                b.Click += (_, _) => { Begin?.Invoke(); _tl?.SetSpeed(timed, speed); Timing?.Invoke(); ReadValues(); Committed?.Invoke(); };
                chips.Children.Add(b);
            }
            var note = Note("Sound keeps its pitch when saved. What comes after it on its track moves along.");
            _body.Children.Add(Card("Speed", new StackPanel { Children = { sp, chips, note } }, () => _tl?.SetSpeed(timed, 1)));
        }

        var fades = new StackPanel();
        double maxLen = _clips.Min(x => x.Length);
        fades.Children.Add(Number("Fade in", 0, Math.Max(0.1, Math.Min(10, maxLen)), 0.05, "s",
            () => first.FadeIn, v => { foreach (var x in _clips) { x.FadeIn = v; EditTimeline.ClampFades(x); } }, Placing, decimals: 2));
        fades.Children.Add(Number("Fade out", 0, Math.Max(0.1, Math.Min(10, maxLen)), 0.05, "s",
            () => first.FadeOut, v => { foreach (var x in _clips) { x.FadeOut = v; EditTimeline.ClampFades(x); } }, Placing, decimals: 2));
        _body.Children.Add(Card("Fades", fades, () => { foreach (var x in _clips) x.FadeIn = x.FadeOut = 0; }));
    }

    /// <summary>The numbers again (after the picture was dragged on the preview), without rebuilding anything.</summary>
    public void ReadValues() { foreach (var r in _readers) r(); }

    /* ---- building blocks ---------------------------------------------------------- */

    private Border Card(string title, UIElement content, Action reset)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var t = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var r = new Button { Content = "Reset", FontSize = 11, Padding = new Thickness(8, 2, 8, 2), ToolTip = "Back to how it was" };
        r.SetResourceReference(StyleProperty, "Ghost");
        r.Click += (_, _) =>
        {
            Begin?.Invoke();
            reset();
            foreach (var c in _clips) c.Tidy();
            Placing?.Invoke();
            Sounding?.Invoke();
            Timing?.Invoke();
            ReadValues();
            Committed?.Invoke();
        };
        DockPanel.SetDock(r, Dock.Right);
        head.Children.Add(r);
        head.Children.Add(t);
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(content);
        var card = new Border { Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 0, 10), Child = body, BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        return card;
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        return t;
    }

    /// <summary>
    /// A labelled slider with a box to type the exact value. Dragging the
    /// slider (or typing, or the arrow keys in the box) changes the clip at
    /// once; the whole drag is one undo step.
    /// </summary>
    private FrameworkElement Number(string label, double min, double max, double step, string unit,
                                    Func<double> get, Action<double> set, Action? changed, int decimals = 0, Action? onChange = null)
    {
        var name = new TextBlock { Text = label, Width = 74, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        var box = new TextBox { Width = 58, Padding = new Thickness(4, 2, 4, 2), TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
        var u = new TextBlock { Text = unit, Width = 14, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        u.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        string fmt = decimals == 0 ? "0" : "0." + new string('0', decimals);
        bool loading = false, inGesture = false;

        void Read()
        {
            loading = true;
            double v = get();
            slider.Value = Math.Clamp(v, min, max);
            if (!box.IsKeyboardFocusWithin) box.Text = v.ToString(fmt, CultureInfo.CurrentCulture);
            loading = false;
        }
        void Apply(double v)
        {
            set(v);
            foreach (var c in _clips) c.Tidy();
            changed?.Invoke();
            onChange?.Invoke();
        }
        void StartGesture() { if (!inGesture) { inGesture = true; Begin?.Invoke(); } }
        void EndGesture() { if (inGesture) { inGesture = false; Committed?.Invoke(); Read(); } }

        // A drag on the slider (Persocord's sliders follow the mouse from wherever they're pressed) is one undo step.
        bool dragging = false;
        slider.AddHandler(SliderDrag.StartedEvent, new RoutedEventHandler((_, _) => { dragging = true; StartGesture(); }));
        slider.AddHandler(SliderDrag.EndedEvent, new RoutedEventHandler((_, _) => { dragging = false; EndGesture(); }));
        slider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            StartGesture();
            Apply(Math.Round(e.NewValue / step) * step);
            if (!box.IsKeyboardFocusWithin) box.Text = get().ToString(fmt, CultureInfo.CurrentCulture);
            if (!dragging) EndGesture();   // an arrow key: done at once
        };

        void Typed()
        {
            var text = box.Text.Trim().TrimEnd('%', '°', 's').Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { Read(); return; }
            v = Math.Clamp(v, min, max);
            if (Math.Abs(v - get()) < 1e-9) { Read(); return; }
            StartGesture();
            Apply(v);
            EndGesture();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Typed(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Read(); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down)
            {
                double v = Math.Clamp(get() + (e.Key == Key.Up ? step : -step) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1), min, max);
                StartGesture(); Apply(v); EndGesture();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) => Typed();

        _readers.Add(Read);
        Read();
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(u, Dock.Right);
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(name);
        row.Children.Add(u);
        row.Children.Add(box);
        slider.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(slider);
        return row;
    }

    /* ---- text ---------------------------------------------------------------- */

    private static readonly string[] Swatch = { "#FFFFFF", "#000000", "#FFD43B", "#FF4D5E", "#FF8A3D", "#5BC0FF", "#4CD97B", "#C77DFF" };
    private static List<string>? _fonts;

    /// <summary>A text clip's words and look: every change is drawn at once, each one undoable.</summary>
    private Border TextCard(TClip c)
    {
        var st = c.Text!;
        var body = new StackPanel();
        void Changed() => TextChanged?.Invoke(c);
        void Change(Action set) { Begin?.Invoke(); set(); Changed(); Committed?.Invoke(); }

        // The words: typing is one undo step, from the first key to leaving the box.
        var box = new TextBox
        {
            Text = st.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8),
        };
        bool typing = false;
        box.TextChanged += (_, _) =>
        {
            if (box.Text == st.Text) return;
            if (!typing) { typing = true; Begin?.Invoke(); }
            st.Text = box.Text;
            Changed();
        };
        box.LostKeyboardFocus += (_, _) => { if (typing) { typing = false; Committed?.Invoke(); } };
        _textBox = box;
        body.Children.Add(box);

        // Font, then bold, italic and how lines line up.
        _fonts ??= Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.Contains('#')).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        var font = new ComboBox { ItemsSource = _fonts, SelectedItem = _fonts.FirstOrDefault(f => string.Equals(f, st.Font, StringComparison.OrdinalIgnoreCase)), Margin = new Thickness(0, 0, 0, 6) };
        VirtualizingPanel.SetIsVirtualizing(font, true);
        font.SelectionChanged += (_, _) => { if (font.SelectedItem is string f && f != st.Font) Change(() => st.Font = f); };
        body.Children.Add(font);
        var styleRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        Toggle(styleRow, "B", "Bold", () => st.Bold, v => st.Bold = v, FontWeights.Bold);
        Toggle(styleRow, "I", "Italic", () => st.Italic, v => st.Italic = v, FontWeights.Normal, italic: true);
        foreach (var (label, value) in new[] { ("Left", "Left"), ("Centre", "Center"), ("Right", "Right") })
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4), FontSize = 11.5 };
            b.SetResourceReference(StyleProperty, "Ghost");
            if (st.Align == value) { b.SetResourceReference(BackgroundProperty, "BrassSoft"); b.SetResourceReference(ForegroundProperty, "AccentText"); }
            b.Click += (_, _) => { if (st.Align != value) Change(() => st.Align = value); Show(_tl, _clips); };
            styleRow.Children.Add(b);
        }
        body.Children.Add(styleRow);

        body.Children.Add(Number("Size", 8, 400, 1, "px", () => st.Size, v => st.Size = v, null, onChange: Changed));
        body.Children.Add(Label("Colour"));
        body.Children.Add(Colours(() => st.Colour, v => Change(() => st.Colour = v)));
        body.Children.Add(Number("Outline", 0, 30, 0.5, "px", () => st.Outline, v => st.Outline = v, null, decimals: 1, onChange: Changed));
        body.Children.Add(Colours(() => st.OutlineColour, v => Change(() => st.OutlineColour = v)));
        var shadow = new CheckBox { Content = "Shadow", IsChecked = st.Shadow, Margin = new Thickness(0, 8, 0, 4) };
        shadow.Click += (_, _) => Change(() => st.Shadow = shadow.IsChecked == true);
        body.Children.Add(shadow);
        var back = new CheckBox { Content = "Background box", IsChecked = st.Box, Margin = new Thickness(0, 4, 0, 4) };
        back.Click += (_, _) => Change(() => st.Box = back.IsChecked == true);
        body.Children.Add(back);
        body.Children.Add(Colours(() => st.BoxColour, v => Change(() => { st.BoxColour = v; st.Box = true; })));
        body.Children.Add(Number("Box opacity", 0, 100, 1, "%", () => st.BoxOpacity * 100, v => st.BoxOpacity = v / 100, null, onChange: Changed));

        return Card("Text", body, () =>
        {
            var fresh = new TextStyle { Text = st.Text };
            c.Text = fresh;
            st = fresh;
            Changed();
            Dispatcher.BeginInvoke(new Action(() => Show(_tl, _clips)));
        });
    }

    private void Toggle(Panel row, string text, string tip, Func<bool> get, Action<bool> set, FontWeight weight, bool italic = false)
    {
        var b = new Button { Content = text, Width = 30, Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 4, 4), FontWeight = weight, ToolTip = tip };
        if (italic) b.FontStyle = FontStyles.Italic;
        b.SetResourceReference(StyleProperty, "Ghost");
        void Look()
        {
            if (get()) { b.SetResourceReference(BackgroundProperty, "BrassSoft"); b.SetResourceReference(ForegroundProperty, "AccentText"); }
            else { b.ClearValue(BackgroundProperty); b.ClearValue(ForegroundProperty); }
        }
        Look();
        b.Click += (_, _) =>
        {
            Begin?.Invoke();
            set(!get());
            if (_clips.FirstOrDefault(x => x.IsText) is { } t) TextChanged?.Invoke(t);
            Committed?.Invoke();
            Look();
        };
        row.Children.Add(b);
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 6, 0, 4) };
        return t;
    }

    /// <summary>A row of colours to click, and a box for any colour (#RRGGBB).</summary>
    private UIElement Colours(Func<string> get, Action<string> set)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var hex = new TextBox { Width = 72, Padding = new Thickness(4, 1, 4, 1), Text = get(), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        foreach (var c in Swatch)
        {
            var colour = ThemeApplier.Parse(c, Colors.White);
            var sw = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(colour), BorderThickness = new Thickness(1), ToolTip = c,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
            };
            string pick = c;
            sw.MouseLeftButtonUp += (_, _) => { set(pick); hex.Text = pick; };
            row.Children.Add(sw);
        }
        void Typed()
        {
            var t = hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7 && int.TryParse(t[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            { if (!string.Equals(t, get(), StringComparison.OrdinalIgnoreCase)) set(t.ToUpperInvariant()); }
            else hex.Text = get();
        }
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Typed(); e.Handled = true; } };
        hex.LostKeyboardFocus += (_, _) => Typed();
        row.Children.Add(hex);
        return row;
    }

    /// <summary>Quick layouts: fill the frame, a small picture in a corner, or one half.</summary>
    private UIElement Presets(TClip c)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void P(string text, string tip, Action set)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4), FontSize = 11.5, ToolTip = tip };
            b.SetResourceReference(StyleProperty, "Ghost");
            b.Click += (_, _) =>
            {
                Begin?.Invoke();
                set();
                c.Tidy();
                Placing?.Invoke();
                ReadValues();
                Committed?.Invoke();
            };
            wrap.Children.Add(b);
        }
        // A corner picture: a third of the frame, a small margin from the edges.
        void Corner(double sx, double sy) { c.Scale = 1.0 / 3; c.Rotation = 0; c.X = sx * (0.5 - 1.0 / 6 - 0.03); c.Y = sy * (0.5 - 1.0 / 6 - 0.04); }
        P("Fit", "As large as fits in the frame, centred (as it was)", () => { c.X = c.Y = c.Rotation = 0; c.Scale = 1; });
        P("Fill", "Covers the whole frame, centred (its edges may be cut off)", () => { c.X = c.Y = c.Rotation = 0; c.Scale = CoverScale?.Invoke(c) ?? 1; });
        P("↖", "Small, top left", () => Corner(-1, -1));
        P("↗", "Small, top right", () => Corner(1, -1));
        P("↙", "Small, bottom left", () => Corner(-1, 1));
        P("↘", "Small, bottom right", () => Corner(1, 1));
        P("Left half", "In the left half of the frame", () => { c.Scale = 0.5; c.Rotation = 0; c.X = -0.25; c.Y = 0; });
        P("Right half", "In the right half of the frame", () => { c.Scale = 0.5; c.Rotation = 0; c.X = 0.25; c.Y = 0; });
        return wrap;
    }
}
