using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Persocord.Models;
using Persocord.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace Persocord.Views;

/// <summary>
/// The control bar that appears while recording: elapsed time, a progress
/// track, pause, per-source audio toggles, restart, settings, cancel and done.
///
/// Its one hard constraint is that it must sit outside the captured area,
/// because anything inside gets recorded. Placement tries below, above, then
/// either side, then a second monitor, and only overlaps as a last resort on
/// a fullscreen single-monitor capture, where there is nowhere else to be.
/// </summary>
public class RecordingBar : Window
{
    // The overlay look shared with the player and the area picker: a dark
    // chip, white icons, thin white lines; red only for "recording".
    // Its colours come from Customization (bar and Done button); icons and
    // text pick dark or light by themselves to read on them.
    private static Color Shell => ThemeApplier.Parse(LookStore.Current.BarColor, Color.FromRgb(0x12, 0x14, 0x18));
    private static bool LightBar => ThemeApplier.Luminance(Shell) > 0.45;
    private static Color Line  => LightBar ? Color.FromArgb(0x24, 0x00, 0x00, 0x00) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
    private static Color Ink   => LightBar ? Color.FromRgb(0x14, 0x16, 0x1A) : Color.FromRgb(0xF4, 0xF5, 0xF7);
    private static Color Mute  => LightBar ? Color.FromRgb(0x6A, 0x70, 0x7A) : Color.FromRgb(0x8A, 0x90, 0x9A);
    private static readonly Color Brick = Color.FromRgb(0xF0, 0x4A, 0x3E);
    private static readonly Color ChipFill = Colors.Transparent;
    private static Color ChipHover => LightBar ? Color.FromArgb(0x14, 0x00, 0x00, 0x00) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
    private static Color DoneFill => ThemeApplier.Parse(LookStore.Current.BarDoneColor, Color.FromRgb(0xF4, 0xF5, 0xF7));
    private static Color DoneInk => ThemeApplier.Luminance(DoneFill) > 0.45 ? Color.FromRgb(0x12, 0x14, 0x18) : Color.FromRgb(0xF4, 0xF5, 0xF7);
    private readonly bool _labels = LookStore.Current.BarLabels;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");

    private const string PauseGlyph = "\uE769", ResumeGlyph = "\uE768";
    private const string PcOnGlyph = "\uE767", PcOffGlyph = "\uE74F";
    private const string MicOnGlyph = "\uE720", MicOffGlyph = "\uEC54";

    private readonly Ellipse _dot = new();
    private readonly TextBlock _time = new();
    private readonly System.Windows.Shapes.Rectangle _trackFill = new();
    private readonly Border _track;
    private readonly Button _pause;
    private readonly Button _pcAudio;
    private readonly Button _mic;
    private readonly TextBlock _note = new();

    private readonly int _softLimit;
    private bool _paused;
    private bool _pcOn, _micOn;

    public event Action? PauseClicked;
    public event Action? RestartClicked;
    public event Action? SettingsClicked;
    public event Action? CancelClicked;
    public event Action? DoneClicked;
    public event Action? DoneUploadClicked;
    public event Action<bool>? PcAudioToggled;   // true = on
    public event Action<bool>? MicToggled;
    public event Action<int, int>? Moved;        // x, y after a drag

    public RecordingBar(
        Rectangle region, int softLimitSeconds,
        bool pcAvailable, bool micAvailable,
        bool pcOn, bool micOn,
        int[] savedPosition)
    {
        _softLimit = Math.Max(1, softLimitSeconds);
        _pcOn = pcOn;
        _micOn = micOn;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        /* ---- left: status dot, timer, progress track ------------------- */

        // The live dot breathes slowly while recording.
        _dot.Width = 8; _dot.Height = 8;
        _dot.Fill = new SolidColorBrush(Brick);
        _dot.VerticalAlignment = VerticalAlignment.Center;
        _dot.Margin = new Thickness(4, 0, 9, 0);
        Breathe(true);

        _time.Text = "0:00";
        _time.Foreground = new SolidColorBrush(Ink);
        _time.FontFamily = Ui;
        _time.FontWeight = FontWeights.SemiBold;
        _time.FontSize = 13.5;
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.MinWidth = 38;
        System.Windows.Documents.Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);

        _trackFill.Fill = new SolidColorBrush(Ink);
        _trackFill.HorizontalAlignment = HorizontalAlignment.Left;
        _trackFill.Width = 0;
        _trackFill.RadiusX = 1; _trackFill.RadiusY = 1;

        _track = new Border
        {
            Width = 72, Height = 2,
            Background = new SolidColorBrush(ThemeApplier.WithAlpha(Ink, 0x30)),
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            Child = _trackFill,
            ToolTip = $"Fills at {_softLimit} seconds. Recording carries on past it.",
        };

        /* ---- buttons: icons, quiet until hovered ------------------------ */

        string? L(string word) => _labels ? word : null;

        _pause = Chip(PauseGlyph, L("Pause"), () => PauseClicked?.Invoke(), "Pause  (resumes without a gap)");

        _pcAudio = Chip(PcOnGlyph, L("PC audio"), () =>
        {
            _pcOn = !_pcOn;
            PaintToggle(_pcAudio!, _pcOn, PcOnGlyph, PcOffGlyph, L("PC audio"));
            PcAudioToggled?.Invoke(_pcOn);
        }, "PC audio: click to mute or unmute");

        _mic = Chip(MicOnGlyph, L("Mic"), () =>
        {
            _micOn = !_micOn;
            PaintToggle(_mic!, _micOn, MicOnGlyph, MicOffGlyph, L("Mic"));
            MicToggled?.Invoke(_micOn);
        }, "Microphone: click to mute or unmute");

        PaintToggle(_pcAudio, _pcOn, PcOnGlyph, PcOffGlyph, L("PC audio"));
        PaintToggle(_mic, _micOn, MicOnGlyph, MicOffGlyph, L("Mic"));

        if (!pcAvailable) Disable(_pcAudio, "No desktop sound in this recording");
        if (!micAvailable) Disable(_mic, "No microphone in this recording");

        var restart = Chip("\uE72C", L("Restart"), () => RestartClicked?.Invoke(),
            "Restart: throw this take away and start again on the same area");
        var settings = Chip("\uE713", L("Settings"), () => SettingsClicked?.Invoke(), "Settings");
        var cancel = Chip("\uE711", L("Cancel"), () => CancelClicked?.Invoke(), "Cancel: stop and keep nothing");
        var done = Chip("\uE73E", "Done", () => DoneClicked?.Invoke(), "Stop, save and copy the clip");
        var doneUpload = Chip("\uE898", L("Upload"), () => DoneUploadClicked?.Invoke(),
            "Stop, save, and upload with your default link settings; the link is copied when ready");

        // Cancel turns red only under the pointer.
        cancel.Template = ChipTemplate(Color.FromArgb(0x30, Brick.R, Brick.G, Brick.B), Brick);

        // Done is the one filled button (white unless changed in Customization).
        var doneFill = DoneFill;
        done.Foreground = new SolidColorBrush(DoneInk);
        done.Background = new SolidColorBrush(doneFill);
        done.FontWeight = FontWeights.SemiBold;
        done.Padding = new Thickness(10, 5, 12, 5);
        done.Template = ChipTemplate(ThemeApplier.Mix(doneFill, ThemeApplier.Luminance(doneFill) > 0.45 ? Colors.White : Colors.Black, 0.12), DoneInk);
        done.Margin = new Thickness(6, 0, 0, 0);
        doneUpload.Margin = new Thickness(2, 0, 0, 0);

        _note.Foreground = new SolidColorBrush(Mute);
        _note.FontSize = 11;
        _note.FontFamily = Ui;
        _note.VerticalAlignment = VerticalAlignment.Center;
        _note.Margin = new Thickness(10, 0, 0, 0);
        _note.Visibility = Visibility.Collapsed;

        // The look (Customization → While recording): which pieces show, labels,
        // colours, see-through, size, shadow. Groups are split by thin lines.
        var look = LookStore.Current;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var groups = new UIElement?[][]
        {
            new UIElement?[] { _dot, look.BarShowTimer ? _time : null, look.BarShowTimer && look.BarShowTrack ? _track : null },
            new UIElement?[] { look.BarShowPause ? _pause : null, look.BarShowPcAudio ? _pcAudio : null, look.BarShowMic ? _mic : null },
            new UIElement?[] { look.BarShowRestart ? restart : null, look.BarShowSettings ? settings : null },
            new UIElement?[] { look.BarShowCancel ? cancel : null, done, look.BarShowUpload ? doneUpload : null },
        };
        bool first = true;
        foreach (var g in groups)
        {
            var parts = g.Where(e => e != null).ToList();
            if (parts.Count == 0) continue;
            if (!first) row.Children.Add(Divider());
            first = false;
            foreach (var e in parts) row.Children.Add(e!);
        }
        row.Children.Add(_note);

        var shell = new Border
        {
            Background = new SolidColorBrush(ThemeApplier.WithAlpha(Shell, (byte)Math.Round(Math.Min(look.BarOpacityPercent * 2.55, 0xEC)))),
            BorderBrush = new SolidColorBrush(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(look.Corners switch
            {
                CornerStyle.Sharp => 2, CornerStyle.Subtle => 7, CornerStyle.Round => 22, _ => 12,
            }),
            Padding = new Thickness(8, 5, 6, 5),
            Child = row,
            Effect = look.BarShadow
                ? new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 3, Opacity = 0.4 }
                : null,
            LayoutTransform = look.BarScalePercent == 100 ? Transform.Identity
                : new ScaleTransform(look.BarScalePercent / 100.0, look.BarScalePercent / 100.0),
        };

        Content = shell;

        // Dragging anywhere on the background moves the bar; buttons swallow
        // their own clicks so this never fires by accident.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }

                // Remember the spot in physical pixels, the same unit used to
                // put it back next time.
                var r = PixelPlacement.ScreenRect(this);
                if (!r.IsEmpty)
                {
                    _placed = new System.Drawing.Point(r.X, r.Y);
                    Moved?.Invoke(r.X, r.Y);
                }
            }
        };

        // Hidden until placed, so it does not flash up at a default position
        // on the wrong monitor first.
        Opacity = 0;
        Loaded += (_, _) => Place(region, savedPosition);

        // Moving onto a monitor with different scaling makes WPF resize the
        // bar; this keeps its top-left corner where it was put.
        PixelPlacement.Keep(this, () =>
        {
            if (_placed is { } p) PixelPlacement.MoveTo(this, p.X, p.Y);
        });
    }

    private System.Drawing.Point? _placed;

    private void PutAt(int x, int y)
    {
        _placed = new System.Drawing.Point(x, y);
        PixelPlacement.MoveTo(this, x, y);
    }

    /* ------------------------------------------------------------------ */
    /* placement                                                           */
    /* ------------------------------------------------------------------ */

    private void Place(Rectangle region, int[] saved)
    {
        UpdateLayout();

        // All placement maths happens in physical pixels, the same unit the
        // capture area is in. The bar's own size comes from WPF in scaled
        // units, so it is converted first.
        double scale = PixelPlacement.Scale(this);
        int w = (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : 620) * scale);
        int h = (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : 44) * scale);
        int gap = (int)Math.Round(12 * scale);

        // A position the user dragged to last time wins, as long as it is
        // still on a screen and still clear of the capture area.
        if (saved.Length == 2)
        {
            var candidate = new Rect(saved[0], saved[1], w, h);
            if (OnAScreen(candidate) && !Overlaps(candidate, region))
            {
                PutAt(saved[0], saved[1]);
                Opacity = 1;
                return;
            }
        }

        var screen = ScreenFor(region);
        double cx = region.X + (region.Width - w) / 2.0;

        var options = new[]
        {
            new Rect(cx, region.Bottom + gap, w, h),           // below
            new Rect(cx, region.Y - h - gap, w, h),            // above
            new Rect(region.Right + gap, region.Y, w, h),      // right
            new Rect(region.X - w - gap, region.Y, w, h),      // left
        };

        foreach (var o in options)
        {
            if (Contains(screen, o) && !Overlaps(o, region))
            {
                PutAt((int)o.X, (int)o.Y);
                Opacity = 1;
                return;
            }
        }

        // Nothing fits beside the selection on this screen. Another monitor
        // is the next best home.
        foreach (var other in Screen.AllScreens)
        {
            if (other.Bounds == screen) continue;
            var b = other.Bounds;
            var o = new Rect(b.X + (b.Width - w) / 2.0, b.Bottom - h - 60 * scale, w, h);
            if (!Overlaps(o, region))
            {
                PutAt((int)o.X, (int)o.Y);
                Opacity = 1;
                return;
            }
        }

        // Last resort: a fullscreen capture on a single monitor. The bar has
        // to sit over the recorded area, so say so rather than letting it turn
        // up in the clip unannounced.
        PutAt(region.X + (region.Width - w) / 2, region.Bottom - h - (int)(24 * scale));
        _note.Text = "in shot — drag me off";
        _note.Visibility = Visibility.Visible;
        Opacity = 0.9;
    }

    private static Rectangle ScreenFor(Rectangle region)
    {
        var centre = new System.Drawing.Point(
            region.X + region.Width / 2, region.Y + region.Height / 2);
        foreach (var s in Screen.AllScreens)
            if (s.Bounds.Contains(centre)) return s.Bounds;
        return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    private static bool Contains(Rectangle screen, Rect r) =>
        r.X >= screen.X && r.Y >= screen.Y &&
        r.X + r.Width <= screen.Right && r.Y + r.Height <= screen.Bottom;

    private static bool Overlaps(Rect r, Rectangle region) =>
        r.X < region.Right && r.X + r.Width > region.X &&
        r.Y < region.Bottom && r.Y + r.Height > region.Y;

    private static bool OnAScreen(Rect r) =>
        Screen.AllScreens.Any(s => s.Bounds.Contains((int)r.X + 20, (int)r.Y + 10));

    /* ------------------------------------------------------------------ */
    /* state                                                               */
    /* ------------------------------------------------------------------ */

    public void Update(TimeSpan elapsed)
    {
        if (_paused) return;

        int t = (int)elapsed.TotalSeconds;
        _time.Text = $"{t / 60}:{t % 60:00}";

        double frac = Math.Min(1.0, elapsed.TotalSeconds / _softLimit);
        _trackFill.Width = _track.Width * frac;

        if (frac >= 1.0)
        {
            _trackFill.Fill = new SolidColorBrush(Brick);
            _track.ToolTip = "Past your usual length. Still recording.";
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        SetGlyph(_pause, paused ? ResumeGlyph : PauseGlyph, _labels ? (paused ? "Resume" : "Pause") : null);
        _pause.ToolTip = paused ? "Resume" : "Pause  (resumes without a gap)";
        _dot.Fill = new SolidColorBrush(paused ? Mute : Brick);
        Breathe(!paused);
        _time.Foreground = new SolidColorBrush(paused ? Mute : Ink);

        if (paused && _note.Visibility != Visibility.Visible)
        {
            _note.Text = "paused";
            _note.Visibility = Visibility.Visible;
        }
        else if (!paused && _note.Text == "paused")
        {
            _note.Visibility = Visibility.Collapsed;
        }
    }

    public void ResetTimer()
    {
        _time.Text = "0:00";
        _trackFill.Width = 0;
        _trackFill.Fill = new SolidColorBrush(Ink);
        _track.ToolTip = $"Fills at {_softLimit} seconds. Recording carries on past it.";
    }

    public void SetBusy(string message)
    {
        _note.Text = message;
        _note.Visibility = Visibility.Visible;
        foreach (var b in FindButtons()) b.IsEnabled = false;
    }

    private System.Collections.Generic.IEnumerable<Button> FindButtons()
    {
        if (Content is Border { Child: StackPanel row })
            foreach (var c in row.Children)
                if (c is Button b) yield return b;
    }

    /* ------------------------------------------------------------------ */
    /* pieces                                                              */
    /* ------------------------------------------------------------------ */

    /// <summary>An icon button (Windows' own icons), optionally with a label after the icon.</summary>
    private Button Chip(string glyph, string? label, Action onClick, string tip)
    {
        var b = new Button
        {
            ToolTip = tip,
            Foreground = new SolidColorBrush(Ink),
            Background = new SolidColorBrush(ChipFill),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = label == null ? new Thickness(8, 6, 8, 6) : new Thickness(10, 5, 12, 5),
            Margin = new Thickness(1, 0, 1, 0),
            MinWidth = 32,
            FontSize = 12.5,
            FontFamily = Ui,
            Cursor = Cursors.Hand,
            Focusable = false,
            Template = ChipTemplate(),
        };
        SetGlyph(b, glyph, label);
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private static void SetGlyph(Button b, string glyph, string? label = null)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        if (label == null) { b.Content = icon; return; }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        icon.FontSize = 12;
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        b.Content = row;
    }

    private static ControlTemplate ChipTemplate(Color? hoverFill = null, Color? hoverText = null)
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(ContentPresenter.MarginProperty,
            new System.Windows.Data.Binding("Padding")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.AppendChild(content);

        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };

        // A soft veil of white under the pointer.
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverFill ?? ChipHover), "bd"));
        if (hoverText != null) hover.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(hoverText.Value)));
        t.Triggers.Add(hover);

        var press = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(OpacityProperty, 0.8));
        t.Triggers.Add(press);

        var off = new Trigger { Property = IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(OpacityProperty, 0.35));
        off.Setters.Add(new Setter(CursorProperty, Cursors.Arrow));
        t.Triggers.Add(off);

        return t;
    }

    /// <summary>On: a white icon. Off: the crossed-out icon, greyed.</summary>
    private static void PaintToggle(Button b, bool on, string onGlyph, string offGlyph, string? label)
    {
        SetGlyph(b, on ? onGlyph : offGlyph, label);
        b.Foreground = new SolidColorBrush(on ? Ink : Mute);
    }

    /// <summary>
    /// The breathing stops with the bar: an endless animation left running on
    /// a closed window kept it (and its drawing) alive until Persocord closed.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _dot.BeginAnimation(OpacityProperty, null);
        base.OnClosed(e);
    }

    /// <summary>The live dot's slow breathing (off while paused).</summary>
    private void Breathe(bool on)
    {
        _dot.BeginAnimation(OpacityProperty, null);
        _dot.Opacity = 1;
        if (!on || !LookStore.Current.FramePulse) return;
        var pulse = new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(1100))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(pulse, 30);
        _dot.BeginAnimation(OpacityProperty, pulse);
    }

    private static void Disable(Button b, string why)
    {
        b.IsEnabled = false;
        b.ToolTip = why;
    }

    private static Border Divider() => new()
    {
        Width = 1, Height = 16,
        Background = new SolidColorBrush(Line),
        Margin = new Thickness(6, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
