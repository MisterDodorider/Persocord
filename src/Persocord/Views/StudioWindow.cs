using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Persocord.Models;
using Persocord.Services;
using IOPath = System.IO.Path;

namespace Persocord.Views;

/// <summary>
/// The clip editor. Media on the left (pictures, videos and sounds brought
/// in, and your other clips), the preview in the middle, the selected clip's
/// properties on the right, and the timeline below: video tracks (V2, V1…)
/// over audio tracks (A1, A2…, one per recorded sound source). Paste (Ctrl+V)
/// or drop pictures, videos and sounds anywhere; drag pictures on the
/// preview to move, resize, turn or crop them. V selects, C cuts, N switches
/// snapping, I / O set what gets saved, Delete removes (leaving a gap, or
/// closing it with Ripple on), Shift+Delete does the other. Each clip keeps
/// its edit, so reopening the editor carries on where it was left.
/// </summary>
public class StudioWindow : Window
{
    private readonly AppController _app;
    private readonly ClipRecord _clip;
    private readonly string _path;
    private MediaInfo? _info;
    private EditTimeline? _tl;

    private readonly TimelineView _view = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Grid _stage = new() { ClipToBounds = true, AllowDrop = true };
    /// <summary>The frame: a canvas the size of the saved video, shown scaled to fit.</summary>
    private readonly Canvas _board = new() { Background = Brushes.Black, ClipToBounds = true, Width = 1920, Height = 1080 };
    private readonly Viewbox _boardBox = new() { Stretch = Stretch.Uniform };
    private readonly CanvasEditor _handles = new() { AllowDrop = true };
    private readonly TextBlock _preparing = new()
    {
        Text = "Opening…", Foreground = Brushes.White, FontSize = 13, Padding = new Thickness(12, 6, 12, 6),
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 14, 0, 0),
        IsHitTestVisible = false,
    };
    private readonly Canvas _cropLayer = new() { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
    private readonly Rectangle _cropRect = new() { StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) };

    private readonly Button _select = new(), _cut = new(), _snap = new(), _ripple = new(), _undo = new(), _redo = new();
    private readonly Button _play = new() { Content = "▶", Width = 44 };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _marks = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), FontSize = 12 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Width = 180, Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 10, 0) };
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _saveCopy = new() { Content = "Save as copy", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _replace = new() { Content = "Replace original" };
    private readonly CheckBox _exactCuts = new()
    {
        Content = "Exact cuts", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        ToolTip = "Off: trims and cuts save in seconds, each part starting on the nearest keyframe (up to 2 s earlier).\n" +
                  "On: every cut lands on the exact frame, but saving re-encodes the clip (much slower).",
    };
    /// <summary>How the edit is saved: as it is, small enough for Discord, as a vertical short, or with every sound track separate.</summary>
    private readonly ComboBox _preset = new()
    {
        MinWidth = 170, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        ToolTip = "How it's saved.\nDiscord 10 / 50 MB: made small enough to post (it shrinks only if it has to).\n" +
                  "Vertical Shorts: a 9:16 frame (a blurred copy fills the sides, unless you chose a frame shape yourself).\n" +
                  "Separate audio tracks: each sound track (PC audio, mic, Discord…) saved on its own, without the Mix, for other video editors.",
    };
    private enum SavePreset { Original, Discord10, Discord50, Shorts, Separate }
    private readonly ComboBox _aspect = new() { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ComboBox _frameBack = new() { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _framing;
    private readonly Button _fit = new() { Content = "⤢  Fit", MinWidth = 72, FontSize = 14, Margin = new Thickness(18, 0, 6, 0), ToolTip = "The whole timeline in view  (\\)" };
    private readonly Button _zoomOut = new() { Content = "−", MinWidth = 44, FontSize = 20, FontWeight = FontWeights.SemiBold, Padding = new Thickness(12, 0, 12, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Zoom out  (-)" };
    private readonly Button _zoomIn = new() { Content = "+", MinWidth = 44, FontSize = 20, FontWeight = FontWeights.SemiBold, Padding = new Thickness(12, 0, 12, 3), ToolTip = "Zoom in  (+)" };

    private readonly CheckBox _cropOn = new() { Content = "Crop the saved video (drag on the picture)", Margin = new Thickness(0, 0, 0, 6) };
    private readonly ComboBox _speed = new() { Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 200, Value = 100 };
    private readonly TextBlock _volumeText = new() { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };

    private MediaBin? _bin;
    private readonly PropertiesPanel _props = new();

    // Live (instant) by default; "Render exact" plays a precise render until the next edit.
    private LivePlayer? _live;
    private bool _exact;
    private readonly Button _renderExact = new() { Content = "Render exact", Margin = new Thickness(18, 0, 0, 0), ToolTip = "Render precisely what will be saved, then play it" };
    /// <summary>For each file: the copy the live preview plays (the file itself until a small copy is ready).</summary>
    private readonly Dictionary<string, string> _proxyPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Size and length of every file used (the clips' own, and their preview copies).</summary>
    private readonly Dictionary<string, MediaInfo> _proxyInfo = new(StringComparer.OrdinalIgnoreCase);
    private FramePlayer? _player;
    private double _previewFrom;
    private string? _previewFile;
    private int _renderNo;
    private CancellationTokenSource? _render, _saving;
    /// <summary>Background work (working out the Unknown track): stopped when the editor closes.</summary>
    private readonly CancellationTokenSource _background = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _syncing, _closingAfterSave, _closed, _rippleOn;

    public StudioWindow(AppController app, ClipRecord clip)
    {
        _app = app;
        _clip = clip;
        _path = app.Library.PathOf(clip);
        Title = $"Edit · {clip.Title}";
        // As big as suits the editor, but never bigger than the screen it opens on.
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(1000, area.Width);
        MinHeight = Math.Min(640, area.Height);
        Width = Math.Max(MinWidth, Math.Min(1440, area.Width - 40));
        Height = Math.Max(MinHeight, Math.Min(900, area.Height - 40));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        Content = BuildLayout();
        Wire();
        Loaded += async (_, _) => await OpenAsync();
        _openEditors++;
        Closed += (_, _) => Shutdown();
    }

    /* ---- layout ------------------------------------------------------------------ */

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16, 12, 16, 10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                       // toolbar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 220 }); // media · preview · properties
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                       // splitter
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(370), MinHeight = 200 });                 // transport and timeline
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                       // status

        /* ---- toolbar: tools in one pill, then history and adding, saving on the right ---- */
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        void Left(UIElement e) { DockPanel.SetDock(e, Dock.Left); bar.Children.Add(e); }
        void Right(UIElement e) { DockPanel.SetDock(e, Dock.Right); bar.Children.Add(e); }

        Glyph(_select, "", "Select", "Select, move, trim, fade  (V)");
        Glyph(_cut, "", "Cut", "Click footage to cut it  (C)");
        Glyph(_snap, "", "Snap", "Snap to edges and the playhead  (N)");
        Glyph(_ripple, "", "Ripple", "On: Delete closes the gap it leaves (Shift+Delete leaves it)");
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _select, _cut, _snap, _ripple })
        {
            b.SetResourceReference(StyleProperty, "Ghost");
            b.Margin = new Thickness(0, 0, 2, 0);
            tools.Children.Add(b);
        }
        var toolPill = Pill(tools);

        Glyph(_undo, "", null, "Undo  (Ctrl+Z)");
        Glyph(_redo, "", null, "Redo  (Ctrl+Shift+Z)");
        _undo.SetResourceReference(StyleProperty, "Ghost");
        _redo.SetResourceReference(StyleProperty, "Ghost");
        var history = Pill(new StackPanel { Orientation = Orientation.Horizontal, Children = { _undo, _redo } });
        history.Margin = new Thickness(10, 0, 0, 0);

        var import = new Button { ToolTip = "Bring in pictures, videos or sounds  (or paste with Ctrl+V, or drop files)" };
        Glyph(import, "", "Import", null);
        import.Margin = new Thickness(10, 0, 6, 0);
        import.Click += async (_, _) => await ImportAsync(-2, null);
        var addTrack = new Button { ToolTip = "Another video track on top" };
        Glyph(addTrack, "", "Track", null);
        addTrack.SetResourceReference(StyleProperty, "Ghost");
        addTrack.Click += (_, _) => { if (_tl == null) return; _tl.Remember(); _tl.AddVideoTrack(); _tl.Renumber(); OnEdited(); };
        var reset = new Button { ToolTip = "Undo every change to this clip", Margin = new Thickness(6, 0, 0, 0) };
        Glyph(reset, "", "Reset", null);
        reset.SetResourceReference(StyleProperty, "Ghost");
        reset.Click += (_, _) =>
        {
            if (_tl == null || _info == null) return;
            if (Dialog.Show("Reset every change to this clip? (Ctrl+Z brings them back.)", "Persocord",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _tl.ResetTo(EditTimeline.From(_path, _clip.Title, _info));
            _view.ClearSelection();
            OnEdited();
        };
        var text = new Button { ToolTip = "Add text at the playhead  (T)", Margin = new Thickness(0, 0, 6, 0) };
        Glyph(text, "\uE8D2", "Text", null);
        text.SetResourceReference(StyleProperty, "Ghost");
        text.Click += (_, _) => AddText(-2, _view.Playhead);
        var silence = new Button { ToolTip = "Cut out the quiet parts (asks first)", Margin = new Thickness(6, 0, 0, 0) };
        Glyph(silence, "\uE74F", "Silences", null);
        silence.SetResourceReference(StyleProperty, "Ghost");
        silence.Click += async (_, _) => await CutSilencesAsync();
        Glyph(addTrack, "\uECC8", null, "Another video track on top");
        Glyph(reset, "\uE72C", null, "Undo every change to this clip");
        var keys = new Button { ToolTip = "Every editor key  (?)", Margin = new Thickness(6, 0, 0, 0) };
        Glyph(keys, "", null, null);
        keys.SetResourceReference(StyleProperty, "Ghost");
        keys.Click += (_, _) => ShowKeys();
        Left(toolPill); Left(history); Left(import); Left(text); Left(addTrack); Left(silence); Left(reset); Left(keys);

        _replace.SetResourceReference(StyleProperty, "Primary");
        Glyph(_replace, "", "Replace original", null);
        Right(_replace); Right(_saveCopy); Right(_exactCuts); Right(_preset);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        /* ---- media · preview · properties ---- */
        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250), MinWidth = 170 });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 240 });

        _bin = new MediaBin(
            () => _app.Library.Items.Where(c => c.Id != _clip.Id && File.Exists(_app.Library.PathOf(c))).ToList(),
            c => _app.Library.PathOf(c), c => _app.Library.ThumbPathOf(c));
        Grid.SetColumn(_bin, 0);
        middle.Children.Add(_bin);
        middle.Children.Add(Splitter(1, vertical: true));

        // Around the frame: a little lighter than black, so the frame's edges show.
        _stage.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0F));
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        _boardBox.Child = _board;
        _stage.Children.Add(_boardBox);
        _preview.Visibility = Visibility.Collapsed;
        _stage.Children.Add(_preview);
        _stage.Children.Add(_handles);
        _stage.Children.Add(_cropLayer);
        _stage.Children.Add(_preparing);
        _cropLayer.Children.Add(_cropRect);
        _cropRect.SetResourceReference(Shape.StrokeProperty, "Brass");
        var stageBorder = new Border { ClipToBounds = true, Child = _stage, BorderThickness = new Thickness(1) };
        stageBorder.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        stageBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
        // The picture's corners follow the card's (a clip, not just a border).
        stageBorder.SizeChanged += (_, e) =>
        {
            var r = stageBorder.CornerRadius.TopLeft;
            _stage.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2)), r, r);
        };
        Grid.SetColumn(stageBorder, 2);
        middle.Children.Add(stageBorder);
        middle.Children.Add(Splitter(3, vertical: true));

        _props.SetProject(BuildProjectProperties());
        Grid.SetColumn(_props, 4);
        middle.Children.Add(_props);
        Grid.SetRow(middle, 1);
        root.Children.Add(middle);

        /* ---- transport: time on the left, a round play button in the middle, zoom on the right ---- */
        var transport = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var times = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _time.Margin = new Thickness(0);
        _time.FontSize = 14;
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
        times.Children.Add(_time);
        times.Children.Add(_marks);
        _marks.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        transport.Children.Add(times);

        var start = new Button { ToolTip = "To the start  (Home)" };
        Glyph(start, "", null, null);
        start.SetResourceReference(StyleProperty, "Ghost");
        start.Click += (_, _) => Step(-_view.Playhead);
        var back = new Button { ToolTip = "Back a frame  (←)" };
        Glyph(back, "", null, null);
        back.SetResourceReference(StyleProperty, "Ghost");
        back.Click += (_, _) => Step(-1.0 / 30);
        var fwd = new Button { ToolTip = "Forward a frame  (→)" };
        Glyph(fwd, "", null, null);
        fwd.SetResourceReference(StyleProperty, "Ghost");
        fwd.Click += (_, _) => Step(1.0 / 30);
        var end = new Button { ToolTip = "To the end  (End)" };
        Glyph(end, "", null, null);
        end.SetResourceReference(StyleProperty, "Ghost");
        end.Click += (_, _) => Step((_tl?.Duration ?? 0) - _view.Playhead);
        _play.SetResourceReference(StyleProperty, "Primary");
        _play.Width = _play.Height = 46;
        _play.Padding = new Thickness(0);
        _play.FontSize = 16;
        _play.Margin = new Thickness(10, 0, 10, 0);
        _play.Resources["RadiusControl"] = new CornerRadius(23);   // round
        _play.ToolTip = "Play or pause  (Space)";
        var playRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new UIElement[] { start, back, _play, fwd, end }) playRow.Children.Add(b);
        Grid.SetColumn(playRow, 1);
        transport.Children.Add(playRow);

        var zoom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _renderExact.SetResourceReference(StyleProperty, "Ghost");
        _renderExact.Margin = new Thickness(0, 0, 10, 0);
        Glyph(_zoomOut, "", null, "Zoom out  (-)");
        Glyph(_zoomIn, "", null, "Zoom in  (+)");
        Glyph(_fit, "", "Fit", "The whole timeline in view  (\\)");
        foreach (var b in new[] { _zoomOut, _zoomIn, _fit })
        {
            b.SetResourceReference(StyleProperty, "Ghost");
            b.FontWeight = FontWeights.Normal;
            b.MinWidth = 0;
            b.Margin = new Thickness(0, 0, 2, 0);
        }
        zoom.Children.Add(_renderExact);
        zoom.Children.Add(Pill(new StackPanel { Orientation = Orientation.Horizontal, Children = { _zoomOut, _zoomIn, _fit } }));
        Grid.SetColumn(zoom, 2);
        transport.Children.Add(zoom);

        // Drag the line above the transport to give the timeline more (or less) room.
        var split = Splitter(0, vertical: false);
        Grid.SetRow(split, 2);
        root.Children.Add(split);

        /* ---- transport, then the timeline in a rounded card ---- */
        var tlBorder = new Border { ClipToBounds = true, BorderThickness = new Thickness(1) };
        tlBorder.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        tlBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
        tlBorder.Child = new ScrollViewer { Content = _view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var bottom = new DockPanel();
        DockPanel.SetDock(transport, Dock.Top);
        bottom.Children.Add(transport);
        bottom.Children.Add(tlBorder);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        /* ---- status ---- */
        var status = new DockPanel { Margin = new Thickness(2, 10, 0, 0) };
        DockPanel.SetDock(_cancel, Dock.Right);
        DockPanel.SetDock(_progress, Dock.Right);
        status.Children.Add(_cancel);
        status.Children.Add(_progress);
        status.Children.Add(_status);
        _status.FontSize = 12;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _status.Text = Hint();
        Grid.SetRow(status, 4);
        root.Children.Add(status);
        return root;
    }

    /// <summary>A thin bar to drag, between two parts of the window.</summary>
    private static GridSplitter Splitter(int column, bool vertical)
    {
        var s = new GridSplitter
        {
            Background = Brushes.Transparent, ShowsPreview = false, Focusable = false,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };
        if (vertical) { s.Width = 10; s.HorizontalAlignment = HorizontalAlignment.Stretch; s.VerticalAlignment = VerticalAlignment.Stretch; s.ResizeDirection = GridResizeDirection.Columns; Grid.SetColumn(s, column); }
        else { s.Height = 8; s.HorizontalAlignment = HorizontalAlignment.Stretch; s.ResizeDirection = GridResizeDirection.Rows; s.Cursor = Cursors.SizeNS; }
        if (vertical) s.Cursor = Cursors.SizeWE;
        return s;
    }

    /// <summary>The whole edit's settings (shown in Properties when nothing is selected).</summary>
    private UIElement BuildProjectProperties()
    {
        var panel = new StackPanel();
        var head = new TextBlock { Text = "This edit", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 2) };
        var hint = new TextBlock { Text = "Select a clip on the timeline (or click a picture on the preview) to change it.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(head);
        panel.Children.Add(hint);
        foreach (var a in EditTimeline.Aspects)
            _aspect.Items.Add(a switch { "" => "The clip's own", "16:9" => "16:9  (wide)", "9:16" => "9:16  (Shorts, TikTok)", "1:1" => "1:1  (square)", "4:5" => "4:5  (tall post)", _ => a });
        _frameBack.Items.Add("Black");
        _frameBack.Items.Add("Blurred copy of the clip");
        var fillAll = new Button { Content = "Fill the frame with every clip", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        fillAll.SetResourceReference(StyleProperty, "Ghost");
        fillAll.Click += (_, _) => FillFrameWithAll();
        panel.Children.Add(Card("Frame", _aspect, new TextBlock { Text = "Around clips that don't fill it", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) }, _frameBack, fillAll,
            Note("A 9:16 frame from a wide clip: the clip fits inside, and the background fills the rest.")));
        foreach (var sp in Speeds) _speed.Items.Add(sp == 1 ? "Normal" : $"{sp}×");
        panel.Children.Add(Card("Speed", _speed, Note("Applies when saving.")));
        var vol = new DockPanel();
        DockPanel.SetDock(_volumeText, Dock.Right);
        vol.Children.Add(_volumeText);
        vol.Children.Add(_volume);
        panel.Children.Add(Card("Volume", vol, Note("Every track together.")));
        var resetCrop = new Button { Content = "Reset crop", HorizontalAlignment = HorizontalAlignment.Left };
        resetCrop.SetResourceReference(StyleProperty, "Ghost");
        resetCrop.Click += (_, _) => { if (_tl == null) return; _tl.Remember(); _tl.Crop = null; ShowCrop(); OnEdited(); };
        panel.Children.Add(Card("Saved video", _cropOn, resetCrop, Note("Cuts the edges off the whole saved video (applies when saving). To crop one clip, select it.")));
        return panel;
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>A button with an icon (Windows' own icon font) and, optionally, a label.</summary>
    private static void Glyph(Button b, string glyph, string? label, string? tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        if (label != null) row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        b.Content = row;
        if (label == null) b.Padding = new Thickness(10, 7, 10, 7);
        if (tip != null) b.ToolTip = tip;
    }

    /// <summary>A soft rounded pill holding a group of buttons.</summary>
    private static Border Pill(UIElement content)
    {
        var p = new Border { Padding = new Thickness(3), Child = content, BorderThickness = new Thickness(1) };
        p.SetResourceReference(Border.BackgroundProperty, "Panel");
        p.SetResourceReference(Border.BorderBrushProperty, "Line");
        p.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        return p;
    }

    /// <summary>A side-panel card: a small heading and its controls.</summary>
    private static Border Card(string title, params UIElement[] children)
    {
        var body = new StackPanel();
        var head = Head(title);
        head.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(head);
        foreach (var c in children) body.Children.Add(c);
        var card = new Border { Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 0, 0, 10), Child = body, BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        return card;
    }

    private static TextBlock Head(string text)
    {
        var t = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        return t;
    }

    private void Wire()
    {
        _select.Click += (_, _) => SetTool(EditTool.Select);
        _cut.Click += (_, _) => ToggleCut();
        _snap.Click += (_, _) => ToggleSnap();
        _ripple.Click += (_, _) => SetRipple(!_rippleOn);
        _undo.Click += (_, _) => { if (_tl?.Undo() == true) OnEdited(); };
        _redo.Click += (_, _) => { if (_tl?.Redo() == true) OnEdited(); };
        _play.Click += (_, _) => TogglePlay();
        _renderExact.Click += async (_, _) => await RenderPreviewAsync();
        _saveCopy.Click += async (_, _) => await SaveAsync(false);
        _replace.Click += async (_, _) => await SaveAsync(true);
        _cancel.Click += (_, _) =>
        {
            if (_saving == null) return;
            _saving.Cancel();
            _cancel.IsEnabled = false;
            _status.Text = "Cancelling…";
        };
        _fit.Click += (_, _) => _view.FitAll();
        _zoomIn.Click += (_, _) => _view.Zoom(1.5);
        _zoomOut.Click += (_, _) => _view.Zoom(1 / 1.5);
        foreach (var name in new[] { "Original quality", "Discord · 10 MB", "Discord · 50 MB", "Vertical Shorts (9:16)", "Separate audio tracks" })
            _preset.Items.Add(name);
        // "Separate audio tracks" is remembered (as before); the others are for this time.
        _preset.SelectedIndex = _app.Settings.Current.EditSeparateTracks ? (int)SavePreset.Separate : (int)SavePreset.Original;
        _preset.SelectionChanged += (_, _) =>
        {
            var s = _app.Settings.Current;
            bool separate = _preset.SelectedIndex == (int)SavePreset.Separate;
            if (s.EditSeparateTracks == separate) return;
            s.EditSeparateTracks = separate;
            _app.Settings.Save(s);
        };
        _aspect.SelectionChanged += (_, _) =>
        {
            if (_framing || _tl == null || _aspect.SelectedIndex < 0) return;
            var a = EditTimeline.Aspects[_aspect.SelectedIndex];
            if (a == _tl.Aspect) return;
            _tl.Remember();
            _tl.Aspect = a;
            _tl.Crop = null;   // the old crop was of the old frame
            // A new shape looks best with the clip's own picture behind it.
            if (a.Length > 0 && _tl.Background == FrameBackground.Black) _tl.Background = FrameBackground.Blur;
            OnEdited();
        };
        _frameBack.SelectionChanged += (_, _) =>
        {
            if (_framing || _tl == null || _frameBack.SelectedIndex < 0) return;
            var b = (FrameBackground)_frameBack.SelectedIndex;
            if (b == _tl.Background) return;
            _tl.Remember();
            _tl.Background = b;
            OnEdited();
        };
        _exactCuts.IsChecked = _app.Settings.Current.EditExactCuts;
        _exactCuts.Click += (_, _) =>
        {
            var s = _app.Settings.Current;
            s.EditExactCuts = _exactCuts.IsChecked == true;
            _app.Settings.Save(s);
        };
        // The encoder checks a save needs, done now rather than when you press Save.
        _ = Task.Run(async () =>
        {
            try { await ClipEditor.BestEncoderAsync(); await EncoderDetector.AvailableForAsync(VideoCodec.H264); }
            catch { }
        });

        _view.Edited += OnEdited;
        _view.Status += s => _status.Text = s;
        _view.InfoOf = InfoOf;
        _view.TextRequested += (track, time) => AddText(track, time);
        _view.FreezeRequested += async clip => await FreezeAsync(clip);
        _view.ClipVolumeChanging += () => { if (_exact) LeaveExact(); _live?.ApplyVolumes(); };
        _view.SelectionChanged += OnSelection;
        _view.RenameRequested += RenameTrack;
        _view.Dropped += async (data, track, time) => await DropAsync(data, track, time);
        _view.PasteRequested += async (track, time) => await PasteAsync(track, time);
        _view.ImportRequested += async (track, time) => await ImportAsync(track, time);
        _view.CleanUpRequested += async () => await CleanUpAsync();
        _view.VolumeClicked += track =>
        {
            if (_tl == null) return;
            var tr = _tl.Tracks[track];
            var typed = AskPercent(tr.Shown.Length > 0 ? $"{tr.Name} · {tr.Shown}" : tr.Name, tr.Volume * 100);
            if (typed == null) return;
            _tl.Remember();
            tr.Volume = Math.Clamp(typed.Value / 100, 0, 2);
            _view.Refresh();
            VolumeDone(track);
        };
        _view.VolumeChanging += _ =>
        {
            if (_exact) LeaveExact();
            _live?.ApplyVolumes();
        };
        _view.VolumeCommitted += VolumeDone;
        _view.PlayheadMoved += t =>
        {
            if (!_syncing) { if (_exact) SeekPreview(t); else _live?.Seek(t); }
            UpdateTime();
            _handles.InvalidateVisual();
        };

        _speed.SelectionChanged += (_, _) =>
        {
            if (_tl == null || _speed.SelectedIndex < 0 || Math.Abs(_tl.Speed - Speeds[_speed.SelectedIndex]) < 1e-6) return;
            _tl.Remember();
            _tl.Speed = Speeds[_speed.SelectedIndex];
            SaveProjectSoon();
        };
        _volume.ValueChanged += (_, e) => _volumeText.Text = $"{(int)e.NewValue}%";
        _volume.PreviewMouseUp += (_, _) => Dispatcher.BeginInvoke(new Action(CommitVolume));
        _volume.LostMouseCapture += (_, _) => CommitVolume();

        _cropOn.Checked += (_, _) => { _cropLayer.Visibility = Visibility.Visible; ShowCrop(); UpdateHandles(); };
        _cropOn.Unchecked += (_, _) => { _cropLayer.Visibility = Visibility.Collapsed; UpdateHandles(); };
        _cropLayer.MouseLeftButtonDown += CropDown;
        _cropLayer.MouseMove += CropMove;
        _cropLayer.MouseLeftButtonUp += CropUp;
        _stage.SizeChanged += (_, _) => ShowCrop();

        // The picture's handles on the preview.
        _handles.Layers = LayersAtPlayhead;
        _handles.Picked += clip =>
        {
            if (_tl == null) return;
            if (clip == null) { if (_view.Selected.Count > 0) _view.ClearSelection(); return; }
            _view.Select(Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? new[] { clip } : _tl.Group(clip));
        };
        _handles.Begin += () => { _tl?.Remember(); if (_exact) LeaveExact(); };
        _handles.Changing += () => { _live?.Relayout(); _props.ReadValues(); };
        _handles.Committed += PlacementDone;
        _handles.CropModeChanged += () =>
        {
            if (_props.CropOnPictureOn == _handles.CropMode) return;
            _props.CropOnPictureOn = _handles.CropMode;
            if (_tl != null) _props.Show(_tl, _tl.Clips.Where(c => _view.Selected.Contains(c.Id)));
        };

        // Properties.
        _props.Begin += () => { _tl?.Remember(); if (_exact) LeaveExact(); };
        _props.Placing += () => { _live?.Relayout(); _handles.InvalidateVisual(); };
        _props.Sounding += () => _live?.ApplyVolumes();
        _props.Committed += PlacementDone;
        _props.CropOnPicture += on => { _handles.CropMode = on; };
        _props.Timing += () => { _live?.Refresh(); _view.Refresh(); UpdateTime(); _handles.InvalidateVisual(); };
        _props.TextChanged += clip => { EnsureText(clip); _live?.Relayout(); _view.Refresh(); _handles.InvalidateVisual(); };
        _props.CoverScale = CoverScale;

        // Dropped files: on the preview, at the playhead; on the media list, just listed.
        _stage.DragOver += (_, e) => { e.Effects = Droppable(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _stage.Drop += async (_, e) => { e.Handled = true; await DropAsync(e.Data, -2, _view.Playhead); };
        _bin!.Use += async (path, title) => await AddFilesAsync(new[] { (path, title) }, -2, _view.Playhead, copy: false);
        _bin.ImportRequested += async () => await ImportAsync(-2, null, placeOnTimeline: false);
        _bin.FilesDropped += async files => await AddFilesAsync(files.Select(f => (f, IOPath.GetFileNameWithoutExtension(f))), -2, null, copy: true, place: false);
        _bin.Changed += SaveProjectSoon;

        _debounce.Tick += (_, _) => { _debounce.Stop(); SaveProject(); };
        _tick.Tick += (_, _) => OnTick();
        PreviewKeyDown += OnKey;
    }

    private static bool Droppable(IDataObject d) => d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(MediaBin.Format);

    private void CommitVolume()
    {
        if (_tl == null || Math.Abs(_tl.Volume * 100 - _volume.Value) < 0.5) return;
        _tl.Remember();
        _tl.Volume = _volume.Value / 100;
        OnEdited();
    }

    /// <summary>A picture's placement (or a clip's fades or volume) was changed and let go: kept, undoable, saved.</summary>
    private void PlacementDone()
    {
        _live?.Relayout();
        _live?.ApplyVolumes();
        _view.Refresh();
        _undo.IsEnabled = _tl?.CanUndo == true;
        _redo.IsEnabled = _tl?.CanRedo == true;
        _handles.InvalidateVisual();
        SaveProjectSoon();
    }

    /// <summary>The pictures under the playhead, top first (for clicking and handles on the preview).</summary>
    private IReadOnlyList<(TClip Clip, MediaInfo Source)> LayersAtPlayhead()
    {
        var list = new List<(TClip, MediaInfo)>();
        if (_tl == null || _exact) return list;
        double t = _view.Playhead;
        for (int i = 0; i < _tl.Tracks.Count; i++)
        {
            var tr = _tl.Tracks[i];
            if (tr.Type != TrackType.Video || !tr.Enabled || tr.Locked) continue;
            var clip = _tl.Clips.FirstOrDefault(c => c.Track == i && c.Start <= t + 1e-6 && t < c.End);
            if (clip != null && _proxyInfo.TryGetValue(clip.File, out var info) && info.Width > 0) list.Add((clip, info));
        }
        return list;
    }

    /// <summary>The selection changed: Properties and the preview's handles follow it.</summary>
    private void OnSelection()
    {
        if (_tl == null) return;
        var chosen = _tl.Clips.Where(c => _view.Selected.Contains(c.Id)).ToList();
        _props.CropOnPictureOn = false;
        _handles.CropMode = false;
        _props.Show(_tl, chosen);
        UpdateHandles();
    }

    private void UpdateHandles()
    {
        if (_tl == null) return;
        var chosen = _tl.Clips.Where(c => _view.Selected.Contains(c.Id)).ToList();
        _handles.Target = chosen.FirstOrDefault(c => c.Track < _tl.Tracks.Count && _tl.Tracks[c.Track].Type == TrackType.Video);
        _handles.Visibility = _exact || _cropOn.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        _handles.InvalidateVisual();
    }

    /* ---- opening ------------------------------------------------------------------ */

    // What the live preview is still getting ready, shown over the picture
    // (null: nothing, so the banner is hidden). The exact render has its own text.
    private string? _liveBusy = "Opening…";
    private void SetLiveBusy(string? text)
    {
        _liveBusy = text;
        // While an exact render is being made or shown, its own text stays.
        if (_exact || _render is { IsCancellationRequested: false }) return;
        ShowLiveBusy();
    }
    private void ShowLiveBusy()
    {
        _preparing.Text = _liveBusy ?? "";
        _preparing.Visibility = _liveBusy == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task OpenAsync()
    {
        _status.Text = "Opening…";
        _info = await ClipEditor.ProbeAsync(_path);
        if (_closed) return;   // closed while it was opening: nothing more starts
        if (_info == null) { _status.Text = "This clip couldn't be opened."; SetLiveBusy(null); return; }
        _proxyInfo[_path] = _info;
        // An AV1 clip: the decoder that can start anywhere in it is found now (once), before anything needs it.
        await VideoDecoders.PrepareAsync(_path, _info);
        if (_closed) return;
        // The frame is the clip's own size: the saved video's.
        ApplyFrame(new EditTimeline());

        // The edit kept with this clip, if the clip hasn't changed since.
        var kept = await LoadProjectAsync();
        // Closed meanwhile: the edit on disk stays exactly as it was (nothing is saved over it).
        if (_closed) return;
        _tl = kept ?? EditTimeline.From(_path, _clip.Title, _info);
        _tl.SourceLength[_path] = _info.Duration;
        ApplyFrame(_tl);

        _view.Attach(_tl);
        _speed.SelectedIndex = Math.Max(0, Array.IndexOf(Speeds, _tl.Speed));
        _volume.Value = _tl.Volume * 100;
        _volumeText.Text = $"{(int)_volume.Value}%";
        SetTool(EditTool.Select);
        SetSnap(true);
        SetRipple(false);
        _props.Show(_tl, Array.Empty<TClip>());
        UpdateTime();
        _undo.IsEnabled = _tl.CanUndo;
        _redo.IsEnabled = _tl.CanRedo;
        _status.Text = _goneNote ?? Hint();
        _tick.Start();
        await StartLiveAsync();
    }

    /// <summary>Said once when the edit opens, if some of its files had gone.</summary>
    private string? _goneNote;

    /// <summary>Every file's size and length (the pictures' too: where they sit depends on it).</summary>
    private async Task EnsureInfoAsync()
    {
        if (_tl == null) return;
        foreach (var f in _tl.Clips.Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !_proxyInfo.ContainsKey(f)).ToList())
        {
            var info = await ClipEditor.ProbeAsync(f);
            if (info == null) continue;
            _proxyInfo[f] = info;
            await VideoDecoders.PrepareAsync(f, info);
        }
    }

    /// <summary>The small copies the live preview plays from (made once per clip, on the graphics card).</summary>
    private async Task EnsureCopiesAsync()
    {
        if (_tl == null) return;
        await EnsureInfoAsync();
        var files = _tl.Clips.Where(c => _tl.Tracks[c.Track].Type == TrackType.Video && !c.IsImage)
                       .Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase)
                       .Where(f => !_proxyPaths.ContainsKey(f)).ToList();
        if (files.Count == 0) return;
        // The original plays at once; a small copy (lighter to play, especially
        // with several layers) is made in the background and switched to.
        foreach (var f in files)
        {
            if (!_proxyInfo.TryGetValue(f, out var info)) continue;
            _proxyPaths[f] = f;
            // Small enough already (a GIF, a phone clip): played as it is.
            if (info.Height <= 600 && info.Width <= 1100) continue;
            _ = SwitchToCopyAsync(f);
        }
    }

    private async Task SwitchToCopyAsync(string file)
    {
        var copy = await TimelineRender.ProxyAsync(file);
        if (copy == null || _closed) return;
        var info = await ClipEditor.ProbeAsync(copy);
        if (info == null) return;
        _proxyInfo[copy] = info;
        _proxyPaths[file] = copy;
        _live?.Refresh();
    }

    private async Task StartLiveAsync()
    {
        if (_tl == null) return;
        await EnsureCopiesAsync();
        if (_closed) return;
        _live?.Dispose();
        _live = new LivePlayer(_board, _tl,
            f => _proxyPaths.TryGetValue(f, out var p) ? p : null,
            p => _proxyInfo.TryGetValue(p, out var i) ? i : null,
            PcmCache.Ready);
        _live.Moved += () =>
        {
            _syncing = true;
            _view.SetPlayhead(_live.Position, true);
            _syncing = false;
            UpdateTime();
        };
        _live.Ended += UpdatePlay;
        _live.Seek(_view.Playhead);
        SetLiveBusy(null);
        UpdateHandles();
        _ = DecodeSoundAsync();
    }

    /// <summary>Each clip's sound decoded once for the sound engine; heard as soon as it's ready.</summary>
    private async Task DecodeSoundAsync()
    {
        if (_tl == null) return;
        var groups = _tl.Clips.Where(c => _tl.Tracks[c.Track].Type == TrackType.Audio)
                              .GroupBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToList();
        // Only said when something isn't ready yet (the first time a clip is opened).
        int todo = groups.Count(g => g.Any(c => PcmCache.Ready(g.Key, Math.Max(0, c.Stream)) == null)), done = 0;
        try
        {
            foreach (var g in groups)
            {
                bool waiting = g.Any(c => PcmCache.Ready(g.Key, Math.Max(0, c.Stream)) == null);
                if (waiting) SetLiveBusy(todo > 1 ? $"Getting the sound ready… ({++done} of {todo})" : "Getting the sound ready…");
                await PcmCache.EnsureAsync(g.Key, g.Select(c => Math.Max(0, c.Stream)));
                if (_closed) return;
                _live?.ApplyVolumes();
                _view.Refresh();   // its waveform can be drawn now
            }
        }
        finally { if (!_closed && todo > 0) SetLiveBusy(null); }
        await FindUnknownSoundAsync();
    }

    /* ---- sound no track carries ------------------------------------------------ */

    private string UnknownWav => IOPath.Combine(Paths.Root, "edits", _clip.Id + "-unknown.wav");
    private string UnknownNote => IOPath.Combine(Paths.Root, "edits", _clip.Id + "-unknown.txt");
    private const string UnknownName = "Unknown (not separated)";

    /// <summary>
    /// Sound that's in All PC audio but on no app track (an app Windows wouldn't
    /// let Persocord record on its own) becomes an "Unknown" track: All PC audio
    /// minus every captured app track. Worked out once per clip, in the
    /// background, and added only when something real is left over.
    /// </summary>
    private async Task FindUnknownSoundAsync()
    {
        if (_tl == null || _info == null || _closed) return;
        if (_tl.Tracks.Any(t => t.Source.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))) return;
        var streams = _info.SeparateTracks;
        var pc = streams.Where(s => EditTimeline.IsAllPcName(s.Name)).Select(s => (int?)s.Stream).FirstOrDefault();
        var apps = streams.Where(s => EditTimeline.IsAppName(s.Name)).Select(s => s.Stream).ToList();
        if (pc == null || apps.Count == 0) return;
        // All PC audio deleted from this edit (or never there): nothing to work out.
        if (!_tl.Tracks.Any(t => t.Type == TrackType.Audio && EditTimeline.IsAllPcName(t.Source))) return;
        // Every other track is matched and taken out, the microphone too: when
        // it's played back through the PC (headset sidetone, "listen to this
        // device"), All PC audio holds a copy of it.
        var others = streams.Where(s => s.Stream != pc.Value && !EditTimeline.IsAllPcName(s.Name)).Select(s => s.Stream).ToList();

        // The method's version is part of the stamp, so older results are worked out again.
        string stamp = "0";
        try { stamp = $"v{Residual.Version}-{File.GetLastWriteTimeUtc(_path).Ticks}"; } catch { }
        bool significant;
        var note = File.Exists(UnknownNote) ? (await File.ReadAllTextAsync(UnknownNote)).Split('|') : Array.Empty<string>();
        if (note.Length >= 2 && note[0] == stamp && (note[1] == "0" || File.Exists(UnknownWav)))
            significant = note[1] == "1";
        else
        {
            await PcmCache.EnsureAsync(_path, others.Append(pc.Value));
            var pcPcm = PcmCache.Ready(_path, pc.Value);
            var parts = others.Select(a => PcmCache.Ready(_path, a)).ToList();
            if (_closed || pcPcm == null || parts.Any(p => p == null)) return;
            Residual.Result r;
            try { r = await Task.Run(() => Residual.Compute(pcPcm, parts!, UnknownWav, _background.Token)); }
            catch (OperationCanceledException) { TryDelete(UnknownWav); return; }
            catch (Exception ex) { Log.Error("unknown sound", ex); return; }
            significant = r.Significant;
            Log.Write($"Editor sound: {r.EnergyShare:P1} of All PC audio isn't on any other track" +
                      (significant ? " — added as an Unknown track" : " (just traces; no Unknown track)"));
            if (!significant) TryDelete(UnknownWav);
            try { await File.WriteAllTextAsync(UnknownNote, $"{stamp}|{(significant ? 1 : 0)}|{r.EnergyShare:0.000}"); } catch { }
        }
        if (!significant || _closed || _tl == null || !File.Exists(UnknownWav)) return;
        AddUnknownTrack();
    }

    /// <summary>The Unknown track, cut and placed exactly like All PC audio (which follows the picture).</summary>
    private void AddUnknownTrack()
    {
        if (_tl == null) return;
        int pcTrack = _tl.Tracks.FindIndex(t => t.Type == TrackType.Audio && EditTimeline.IsAllPcName(t.Source));
        if (pcTrack < 0) return;
        bool untouched = !_tl.CanUndo;
        int track = _tl.AddAudioTrack(UnknownName);
        _tl.SourceLength[UnknownWav] = _info?.Duration ?? 0;
        foreach (var c in _tl.Clips.Where(c => c.Track == pcTrack).ToList())
        {
            var u = c.Copy();
            u.Id = Guid.NewGuid().ToString("N");
            u.Track = track;
            u.File = UnknownWav;
            u.Stream = 0;
            u.Label = "Unknown";
            _tl.Clips.Add(u);
        }
        // Heard alongside the app tracks (together they make up All PC audio).
        // Left as it was if the tracks have already been changed by hand.
        var pcT = _tl.Tracks[pcTrack];
        var unk = _tl.Tracks[track];
        if (untouched)
        {
            pcT.Enabled = false;
            foreach (var t in _tl.Tracks.Where(t => t.Type == TrackType.Audio && EditTimeline.IsAppName(t.Source))) t.Enabled = true;
        }
        else unk.Enabled = !pcT.Enabled;
        _status.Text = "Found sound that isn't on any app track: it's on the Unknown track.";
        _ = PcmCache.EnsureAsync(UnknownWav, new[] { 0 }).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { _live?.ApplyVolumes(); _view.Refresh(); })));
        OnEdited();
    }

    /// <summary>Back to the live preview (any edit does this).</summary>
    private void LeaveExact()
    {
        _render?.Cancel();
        if (!_exact) { ShowLiveBusy(); return; }
        _exact = false;
        _player?.Dispose();
        _player = null;
        _preview.Source = null;
        _preview.Visibility = Visibility.Collapsed;
        _boardBox.Visibility = Visibility.Visible;
        ShowLiveBusy();
        _live?.Seek(_view.Playhead);
        UpdatePlay();
        UpdateHandles();
    }

    private string ProjectPath => IOPath.Combine(Paths.Root, "edits", _clip.Id + ".json");

    private async Task<EditTimeline?> LoadProjectAsync()
    {
        try
        {
            if (!File.Exists(ProjectPath)) return null;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(ProjectPath));
            long stamp = doc.RootElement.GetProperty("Stamp").GetInt64();
            if (stamp != File.GetLastWriteTimeUtc(_path).Ticks) return null;   // the clip itself changed
            var tl = EditTimeline.FromJson(doc.RootElement.GetProperty("EditTimeline").GetString() ?? "");
            if (tl == null) return null;
            List<string>? Read(string name) => doc.RootElement.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : null;
            tl.RestoreHistory(Read("Undo"), Read("Redo"));
            // Text is drawn again if its picture has gone (it lives with the temporary files).
            foreach (var c in tl.Clips.Where(c => c.IsText)) EnsureText(c);
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in tl.Clips.Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                // The clip being edited itself must be there; anything else that's gone
                // (another clip used in this edit and since deleted, say) is left out.
                var info = File.Exists(file) ? await ClipEditor.ProbeAsync(file) : null;
                if (info == null)
                {
                    if (string.Equals(file, _path, StringComparison.OrdinalIgnoreCase)) return null;
                    gone.Add(file);
                    continue;
                }
                tl.SourceLength[file] = info.Duration;
                _proxyInfo[file] = info;
                await VideoDecoders.PrepareAsync(file, info);
            }
            if (gone.Count > 0)
            {
                // Before, one missing file threw the whole edit away (and the fresh
                // one was then saved over it). Now only its clips go, and the edit
                // as it was is kept beside it, just in case.
                try { File.Copy(ProjectPath, ProjectPath + $".before-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
                int removed = tl.Clips.RemoveAll(c => gone.Contains(c.File));
                Log.Write($"Editor: {gone.Count} file(s) this edit used are gone; {removed} clip(s) of them left out");
                _goneNote = $"{removed} clip{(removed == 1 ? "" : "s")} in this edit used {(gone.Count == 1 ? "a file that's" : "files that are")} gone, so {(removed == 1 ? "it was" : "they were")} left out.";
            }
            foreach (var c in tl.Clips) c.Tidy();
            // What was brought into this edit before.
            if (doc.RootElement.TryGetProperty("Bin", out var bin) && bin.ValueKind == JsonValueKind.Array)
                foreach (var e in bin.EnumerateArray().Reverse())
                {
                    var p = e.TryGetProperty("Path", out var pp) ? pp.GetString() : null;
                    var t = e.TryGetProperty("Title", out var tt) ? tt.GetString() : null;
                    if (p != null && File.Exists(p)) _bin?.Add(p, t ?? IOPath.GetFileNameWithoutExtension(p), _proxyInfo.TryGetValue(p, out var bi) ? bi : null);
                }
            return tl;
        }
        catch (Exception ex) { Log.Error("open saved edit", ex); return null; }
    }

    private void SaveProject()
    {
        if (_tl == null || _closingAfterSave) return;
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(ProjectPath)!);
            var (undo, redo) = _tl.History();
            var json = JsonSerializer.Serialize(new
            {
                Stamp = File.GetLastWriteTimeUtc(_path).Ticks,
                EditTimeline = _tl.ToJson(),
                Undo = undo,
                Redo = redo,
                Bin = (_bin?.Added ?? Enumerable.Empty<(string Path, string Title)>()).Select(b => new { b.Path, b.Title }).ToList(),
            });
            // Written beside it, then swapped in: a crash mid-write can't lose the edit.
            File.WriteAllText(ProjectPath + ".part", json);
            File.Move(ProjectPath + ".part", ProjectPath, true);
        }
        catch (Exception ex) { Log.Error("keep edit", ex); }
    }

    private void SaveProjectSoon() { _debounce.Stop(); _debounce.Start(); }

    /* ---- editing --------------------------------------------------------------------- */

    private void OnEdited()
    {
        if (_tl != null)
        {
            EnsureAllText();
            ApplyFrame(_tl);
        }
        _view.Refresh();
        if (_tl != null)
        {
            _speed.SelectedIndex = Math.Max(0, Array.IndexOf(Speeds, _tl.Speed));
            _volume.Value = _tl.Volume * 100;
        }
        ShowCrop();
        UpdateTime();
        _undo.IsEnabled = _tl?.CanUndo == true;
        _redo.IsEnabled = _tl?.CanRedo == true;
        LeaveExact();
        if (_tl != null) _live?.SetTimeline(_tl);   // undo/redo swaps the lists inside
        // Undo can swap the clips for copies: Properties and the handles follow.
        OnSelection();
        SaveProjectSoon();
    }

    private void SetTool(EditTool tool)
    {
        _view.Tool = tool;
        Mark(_select, tool == EditTool.Select);
        Mark(_cut, tool == EditTool.Cut);
    }

    private void ToggleSnap() => SetSnap(!_view.Snap);

    /// <summary>Cut again goes back to Select.</summary>
    private void ToggleCut() => SetTool(_view.Tool == EditTool.Cut ? EditTool.Select : EditTool.Cut);

    private void SetSnap(bool on)
    {
        _view.Snap = on;
        Mark(_snap, on);
    }

    private void SetRipple(bool on)
    {
        _rippleOn = on;
        Mark(_ripple, on);
    }

    /// <summary>The tool in use: a soft accent fill and accent text; the others stay quiet.</summary>
    private static void Mark(Button b, bool on)
    {
        if (on)
        {
            b.SetResourceReference(Control.BackgroundProperty, "BrassSoft");
            b.SetResourceReference(Control.ForegroundProperty, "AccentText");
        }
        else
        {
            b.ClearValue(Control.BackgroundProperty);
            b.ClearValue(Control.ForegroundProperty);
        }
    }

    private void SetMark(bool isIn)
    {
        if (_tl == null) return;
        _tl.Remember();
        double t = _view.Playhead;
        if (isIn)
        {
            _tl.MarkIn = t;
            if (_tl.MarkOut <= t + EditTimeline.Eps) _tl.MarkOut = _tl.Duration;
        }
        else
        {
            _tl.MarkOut = t;
            if (_tl.MarkIn >= t - EditTimeline.Eps) _tl.MarkIn = 0;
        }
        OnEdited();
    }

    private void ClearMarks()
    {
        if (_tl == null || _tl.MarkOut <= _tl.MarkIn) return;
        _tl.Remember();
        _tl.MarkIn = _tl.MarkOut = 0;
        OnEdited();
    }

    private void RenameTrack(int track)
    {
        if (_tl == null || track < 0 || track >= _tl.Tracks.Count) return;
        var tr = _tl.Tracks[track];
        var name = RenameDialog.Ask(this, tr.Shown);
        if (name == null) return;
        name = name.Trim();
        // Back to what it holds if cleared (or named that).
        var title = name == tr.Source ? "" : name;
        if (title == tr.Title) return;
        _tl.Remember();
        tr.Title = title;
        OnEdited();
    }

    /* ---- bringing things in ----------------------------------------------------------- */

    /// <summary>Import…: pick files, copied into Persocord and (unless only listing) placed at the time given (or the playhead).</summary>
    private async Task ImportAsync(int track, double? at, bool placeOnTimeline = true)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Bring in pictures, videos or sounds", Filter = EditorMedia.OpenFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        await AddFilesAsync(dlg.FileNames.Select(f => (f, IOPath.GetFileNameWithoutExtension(f))), track, at ?? _view.Playhead, copy: true, place: placeOnTimeline);
    }

    /// <summary>Something dropped: files from outside (copied in), or entries from the media list (used as they are).</summary>
    private async Task DropAsync(IDataObject data, int track, double time)
    {
        try
        {
            if (data.GetData(MediaBin.Format) is string entry)
            {
                var parts = entry.Split('\t');
                await AddFilesAsync(new[] { (parts[0], parts.Length > 1 ? parts[1] : IOPath.GetFileNameWithoutExtension(parts[0])) }, track, time, copy: false);
            }
            else if (data.GetData(DataFormats.FileDrop) is string[] files)
                await AddFilesAsync(files.Select(f => (f, IOPath.GetFileNameWithoutExtension(f))), track, time, copy: true);
        }
        catch (Exception ex) { Log.Error("drop into editor", ex); }
    }

    /// <summary>Ctrl+V: a picture (as a new file) or files that were copied.</summary>
    private async Task PasteAsync(int track = -2, double? at = null)
    {
        if (_tl == null) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                await AddFilesAsync(files.Select(f => (f, IOPath.GetFileNameWithoutExtension(f))), track, at ?? _view.Playhead, copy: true);
                return;
            }
            BitmapSource? picture = null;
            // Browsers, Discord and the snipping tool offer a PNG (with its see-through parts); otherwise the plain picture.
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                var dec = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (dec.Frames.Count > 0) picture = dec.Frames[0];
            }
            if (picture == null && Clipboard.ContainsImage()) picture = Clipboard.GetImage();
            if (picture == null) { _status.Text = "Nothing to paste: copy a picture or a file first."; return; }
            var file = EditorMedia.NewPastedPath(".png");
            using (var fs = File.Create(file))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(picture));
                enc.Save(fs);
            }
            await AddFilesAsync(new[] { (file, "Pasted picture") }, track, at ?? _view.Playhead, copy: false);
        }
        catch (Exception ex)
        {
            Log.Error("paste into editor", ex);
            _status.Text = "That couldn't be pasted (another program may be using the clipboard). Try again.";
        }
    }

    /// <summary>
    /// Brings files into the edit: each is copied into Persocord (unless it's
    /// already there), listed under Added, and, when placing, laid on the
    /// timeline one after another from the time given. Pictures and videos go
    /// on the track asked for (covering what's there), or the highest video
    /// track with room, or a new one on top; a video's sound and sound files
    /// go on a sound track.
    /// </summary>
    private async Task AddFilesAsync(IEnumerable<(string Path, string Title)> files, int track, double? at, bool copy, bool place = true)
    {
        if (_tl == null) return;
        var list = files.ToList();
        var placed = new List<TClip>();
        double time = Math.Max(0, at ?? _view.Playhead);
        bool remembered = false;
        int skipped = 0, n = 0;
        foreach (var (src, title) in list)
        {
            var kind = EditorMedia.KindOf(src);
            if (kind == MediaKind.Unsupported || !File.Exists(src)) { skipped++; continue; }
            n++;
            if (list.Count > 1) _status.Text = $"Bringing in {n} of {list.Count}…";
            else _status.Text = $"Bringing in {title}…";
            string? file = src;
            if (copy && !EditorMedia.IsOurs(src, _app.Library.Folder)) file = await EditorMedia.CopyInAsync(src);
            if (file == null || _closed || _tl == null) { skipped++; continue; }
            var info = await ClipEditor.ProbeAsync(file);
            if (info == null) { skipped++; continue; }
            _proxyInfo[file] = info;
            await VideoDecoders.PrepareAsync(file, info);
            // A GIF with one frame is a picture.
            bool image = kind == MediaKind.Image || (kind == MediaKind.Video && info.Duration < 0.1 && info.Width > 0);
            if (image)
            {
                // WebP, TIFF and one-frame GIFs become a PNG, which everything shows the same.
                var plain = await EditorMedia.AsPlainPictureAsync(file);
                if (plain == null || _closed || _tl == null) { skipped++; continue; }
                if (!string.Equals(plain, file, StringComparison.OrdinalIgnoreCase))
                {
                    file = plain;
                    info = await ClipEditor.ProbeAsync(file) ?? info;
                    _proxyInfo[file] = info;
                }
            }
            if (copy || !EditorMedia.IsOurs(file, _app.Library.Folder) || file.StartsWith(EditorMedia.Folder, StringComparison.OrdinalIgnoreCase))
                _bin?.Add(file, title, info);
            if (!place) continue;
            if (!remembered) { _tl.Remember(); remembered = true; }
            var clips = PlaceFile(file, title, info, image, kind, track, time);
            placed.AddRange(clips);
            if (clips.Count > 0) time = clips.Max(c => c.End);
        }
        if (_closed || _tl == null) return;
        if (placed.Count == 0)
        {
            if (remembered) _tl.Forget();
            _status.Text = skipped > 0 ? (list.Count == 1 ? "That file can't be used here (pictures, videos and sounds can)." : $"{skipped} file(s) couldn't be used.") : Hint();
            return;
        }
        _tl.Renumber();
        OnEdited();
        _view.Select(placed);
        _status.Text = placed.Any(c => c.IsImage)
            ? "Added. Drag the picture on the preview to move it, its corners to resize, the round handle to turn it."
            : skipped > 0 ? $"Added ({skipped} file(s) couldn't be used)." : "Added.";
        await EnsureCopiesAsync();
        if (_closed) return;
        _live?.Refresh();
        _ = DecodeSoundAsync();
    }

    /// <summary>One file on the timeline (with its sound, if it has any). Returns the clips made.</summary>
    private List<TClip> PlaceFile(string file, string title, MediaInfo info, bool image, MediaKind kind, int track, double at)
    {
        var tl = _tl!;
        var made = new List<TClip>();
        double length = image ? 5 : Math.Max(EditTimeline.MinLength, info.Duration);
        if (!image) tl.SourceLength[file] = info.Duration;
        bool picture = kind != MediaKind.Audio && info.Width > 0;
        string? link = picture && !image && info.HasAudio ? Guid.NewGuid().ToString("N") : null;

        bool Free(int i) =>
            !tl.Tracks[i].Locked && tl.Clips.All(c => c.Track != i || c.End <= at + EditTimeline.Eps || c.Start >= at + length - EditTimeline.Eps);

        if (picture)
        {
            int v;
            if (track == -1) { tl.AddVideoTrack(); v = 0; }
            else if (track >= 0 && track < tl.Tracks.Count && tl.Tracks[track].Type == TrackType.Video && !tl.Tracks[track].Locked) v = track;
            else
            {
                // The highest video track with room here; a new one on top if none has.
                v = -1;
                for (int i = 0; i < tl.Tracks.Count && tl.Tracks[i].Type == TrackType.Video; i++)
                    if (Free(i)) { v = i; break; }
                if (v < 0) { tl.AddVideoTrack(); v = 0; }
            }
            var clip = new TClip
            {
                Track = v, File = file, Label = title, Start = at, In = 0, Out = length, Link = link,
                Kind = image ? ClipKind.Image : ClipKind.Media,
            };
            tl.Clips.Add(clip);
            made.Add(clip);
        }
        // A Persocord clip (or one saved with separate tracks): each of its sound
        // tracks goes on this edit's track of the same name (Deadlock with
        // Deadlock, mic with mic…); a new track only when there's none with room.
        var named = info.SeparateTracks.Count > 0 ? info.SeparateTracks.ToList()
                  : info.AudioTracks.Count > 1 ? info.AudioTracks.Select((n, i) => (Stream: i, Name: n)).ToList()
                  : new List<(int Stream, string Name)>();
        if (named.Count > 0 && !image)
        {
            bool hasApps = named.Any(n => EditTimeline.IsAppName(n.Name));
            foreach (var (stream, name) in named)
            {
                bool Same(TTrack t) => t.Type == TrackType.Audio &&
                    (string.Equals(t.Source, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Shown, name, StringComparison.OrdinalIgnoreCase));
                int a = -1;
                for (int i = 0; i < tl.Tracks.Count; i++)
                    if (Same(tl.Tracks[i]) && Free(i)) { a = i; break; }
                if (a < 0)
                {
                    // Heard or not like a track of that name already is; otherwise as a fresh
                    // clip would be (All PC audio stays off when there are app tracks).
                    var like = tl.Tracks.FirstOrDefault(Same);
                    a = tl.AddAudioTrack(name);
                    tl.Tracks[a].Enabled = like?.Enabled ??
                        !(EditTimeline.IsAllPcName(name) && hasApps && !name.Contains("(incl.", StringComparison.OrdinalIgnoreCase));
                }
                var sound = new TClip { Track = a, File = file, Stream = stream, Label = name, Start = at, In = 0, Out = length, Link = link };
                tl.Clips.Add(sound);
                made.Add(sound);
            }
        }
        else if (info.HasAudio && !image)
        {
            int a;
            if (!picture && track >= 0 && track < tl.Tracks.Count && tl.Tracks[track].Type == TrackType.Audio && !tl.Tracks[track].Locked) a = track;
            else if (!picture)
            {
                a = -1;
                for (int i = 0; i < tl.Tracks.Count; i++)
                    if (tl.Tracks[i].Type == TrackType.Audio && Free(i) && tl.Clips.Any(c => c.Track == i) == false) { a = i; break; }
                if (a < 0) a = tl.AddAudioTrack(title);
            }
            else a = tl.AddAudioTrack(title);
            var sound = new TClip { Track = a, File = file, Stream = 0, Label = title, Start = at, In = 0, Out = length, Link = link };
            tl.Clips.Add(sound);
            made.Add(sound);
        }
        // Whatever was there is covered.
        tl.Cover(made);
        return made;
    }

    /* ---- the frame -------------------------------------------------------------------- */

    /// <summary>The saved video's size (the clip's own, or the shape chosen).</summary>
    private (int W, int H) Frame => _tl != null && _info != null ? _tl.FrameSize(_info.Width, _info.Height) : ((int)_board.Width, (int)_board.Height);

    /// <summary>The clip's details with the frame's size, for the render.</summary>
    private MediaInfo FrameInfo(EditTimeline tl)
    {
        var (w, h) = tl.FrameSize(_info!.Width, _info.Height);
        return _info with { Width = w, Height = h };
    }

    /// <summary>The preview's frame and the Properties' frame settings follow the edit's.</summary>
    private void ApplyFrame(EditTimeline tl)
    {
        if (_info == null) return;
        var (w, h) = tl.FrameSize(_info.Width, _info.Height);
        if (Math.Abs(_board.Width - w) > 0.5 || Math.Abs(_board.Height - h) > 0.5)
        {
            _board.Width = w;
            _board.Height = h;
            _handles.FrameW = w;
            _handles.FrameH = h;
            ShowCrop();
            _handles.InvalidateVisual();
        }
        _framing = true;
        _aspect.SelectedIndex = Math.Max(0, Array.IndexOf(EditTimeline.Aspects, tl.Aspect));
        _frameBack.SelectedIndex = (int)tl.Background;
        _framing = false;
    }

    /// <summary>How big a clip must be to cover the whole frame (its Size for "Fill").</summary>
    private double CoverScale(TClip c)
    {
        var (w, h) = Frame;
        if (InfoOf(c.File) is not { Width: > 0, Height: > 0 } src) return 1;
        double fit = Math.Min(w / (double)src.Width, h / (double)src.Height), cover = Math.Max(w / (double)src.Width, h / (double)src.Height);
        return cover / fit;
    }

    /// <summary>Every picture (not text) fills the frame, centred: no bars anywhere.</summary>
    private void FillFrameWithAll()
    {
        if (_tl == null) return;
        var pictures = _tl.Clips.Where(c => _tl.Tracks[c.Track].Type == TrackType.Video && !c.IsText && !_tl.Tracks[c.Track].Locked).ToList();
        if (pictures.Count == 0) return;
        _tl.Remember();
        foreach (var c in pictures) { c.X = c.Y = c.Rotation = 0; c.Scale = CoverScale(c); c.Tidy(); }
        OnEdited();
        _status.Text = "Every clip fills the frame now (some of each picture's edges are cut off to do it). Ctrl+Z undoes it.";
    }

    /* ---- text ------------------------------------------------------------------------------ */

    /// <summary>Makes sure a text clip's picture is drawn for how it looks now.</summary>
    private void EnsureText(TClip c)
    {
        if (!c.IsText) return;
        c.Text ??= new TextStyle();
        if (TextArt.Get(c.Text) is not { } art) return;
        c.File = art.Path;
        c.Label = "Text";
        _proxyInfo[art.Path] = new MediaInfo(0, art.Width, art.Height, 30, false, "png");
    }

    private void EnsureAllText()
    {
        if (_tl == null) return;
        foreach (var c in _tl.Clips.Where(c => c.IsText && (!File.Exists(c.File) || !_proxyInfo.ContainsKey(c.File)))) EnsureText(c);
    }

    /// <summary>A text clip at a time (4 seconds, in the lower part of the frame), on the track asked for or the highest with room.</summary>
    private void AddText(int track, double at)
    {
        if (_tl == null) return;
        var tl = _tl;
        const double length = 4;
        _tl.Remember();
        bool Free(int i) => !tl.Tracks[i].Locked && tl.Clips.All(c => c.Track != i || c.End <= at + EditTimeline.Eps || c.Start >= at + length - EditTimeline.Eps);
        int v = -1;
        if (track == -1) { tl.AddVideoTrack(); v = 0; }
        else if (track >= 0 && track < tl.Tracks.Count && tl.Tracks[track].Type == TrackType.Video && !tl.Tracks[track].Locked) v = track;
        else
        {
            for (int i = 0; i < tl.Tracks.Count && tl.Tracks[i].Type == TrackType.Video; i++)
                if (Free(i)) { v = i; break; }
            if (v < 0) { tl.AddVideoTrack(); v = 0; }
        }
        var clip = new TClip { Track = v, Kind = ClipKind.Text, Text = new TextStyle(), Label = "Text", Start = Math.Max(0, at), In = 0, Out = length, Y = 0.3 };
        EnsureText(clip);
        if (!File.Exists(clip.File)) { _tl.Forget(); _status.Text = "The text couldn't be drawn. The log has the details."; return; }
        tl.Clips.Add(clip);
        tl.Cover(new[] { clip });
        tl.Renumber();
        OnEdited();
        _view.Select(new[] { clip });
        _status.Text = "Type your text in Properties. Drag it on the preview to move it; its corners resize it.";
        Dispatcher.BeginInvoke(new Action(_props.FocusText), DispatcherPriority.Input);
    }

    /* ---- freeze frame --------------------------------------------------------------------- */

    /// <summary>
    /// The picture at the playhead held for two seconds: everything after moves
    /// along to make room. The still is taken from the clip itself, full size.
    /// </summary>
    private async Task FreezeAsync(TClip clip)
    {
        if (_tl == null) return;
        double t = _view.Playhead;
        if (t < clip.Start || t >= clip.End) { _status.Text = "Move the playhead over the clip first."; return; }
        _status.Text = "Taking the still…";
        double src = clip.SrcAt(t);
        var info = InfoOf(clip.File);
        var png = EditorMedia.NewPastedPath(".png").Replace("pasted-", "freeze-");
        var args = $"-hide_banner -loglevel error {VideoDecoders.ArgsFor(info?.VideoCodec)}-ss {src.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} -i \"{clip.File}\" -map 0:v:0 -frames:v 1 -y \"{png}\"";
        bool ok = await Task.Run(() => TimelineRender.Run(args, CancellationToken.None, null, 0));
        if (!ok || !File.Exists(png) || _closed || _tl == null) { _status.Text = "The still couldn't be taken. The log has the details."; return; }
        var pinfo = await ClipEditor.ProbeAsync(png);
        if (pinfo == null) return;
        _proxyInfo[png] = pinfo;
        _tl.Remember();
        var still = _tl.InsertHold(clip, t, png, 2);
        if (still == null) { _tl.Forget(); _status.Text = "That clip's track is locked."; return; }
        OnEdited();
        _view.Select(new[] { still });
        _status.Text = "Frozen for 2 seconds (everything after moved along). Drag its end to hold it longer.";
    }

    /* ---- cutting out silences ----------------------------------------------------------- */

    /// <summary>
    /// Finds where every heard sound track is quiet together, shows how much
    /// that is (with how quiet counts and how long, to adjust), and removes
    /// those stretches from every track if you say so. A little sound is
    /// kept either side, so speech isn't clipped. Undoable.
    /// </summary>
    private async Task CutSilencesAsync()
    {
        if (_tl == null) return;
        var tl = _tl;
        var heard = new HashSet<int>(tl.HeardAudio());
        var clips = tl.Clips.Where(c => heard.Contains(c.Track)).ToList();
        if (clips.Count == 0) { _status.Text = "There's no sound to look at."; return; }
        _status.Text = "Looking at the sound…";
        foreach (var g in clips.GroupBy(c => c.File, StringComparer.OrdinalIgnoreCase))
            await PcmCache.EnsureAsync(g.Key, g.Select(c => Math.Max(0, c.Stream)));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var peaks = new Dictionary<TClip, Peaks>();
        while (true)
        {
            foreach (var c in clips.Where(c => !peaks.ContainsKey(c)))
                if (Waveforms.For(c.File, Math.Max(0, c.Stream)) is { } p) peaks[c] = p;
            if (peaks.Count == clips.Count || DateTime.UtcNow > deadline || _closed) break;
            await Task.Delay(300);
        }
        if (_closed || _tl != tl) return;
        if (peaks.Count < clips.Count) { _status.Text = "The sound isn't ready yet. Try again in a moment."; return; }

        // How loud the edit is every 10 ms (the loudest heard track at that moment).
        double step = 1.0 / Peaks.PerSecond, total = tl.Duration;
        int n = (int)Math.Ceiling(total / step);
        float[] loud = new float[n];
        foreach (var (c, p) in peaks)
        {
            double gain = c.Volume * tl.Tracks[c.Track].Volume * tl.Volume;
            int a = Math.Max(0, (int)(c.Start / step)), b = Math.Min(n, (int)Math.Ceiling(c.End / step));
            for (int i = a; i < b; i++)
            {
                double t = i * step;
                double v = p.Max(c.SrcAt(t), c.SrcAt(t + step), 0) * gain;
                if (v > loud[i]) loud[i] = (float)v;
            }
        }
        List<(double From, double To)> Find(double db, double minLen, double keep)
        {
            float limit = (float)Math.Pow(10, db / 20);
            var found = new List<(double, double)>();
            int runStart = -1;
            for (int i = 0; i <= n; i++)
            {
                bool quiet = i < n && loud[i] < limit;
                if (quiet && runStart < 0) runStart = i;
                if (!quiet && runStart >= 0)
                {
                    double from = runStart * step, to = i * step;
                    // The very start and end aren't padded on their outer side.
                    double a = runStart == 0 ? 0 : from + keep, b = i >= n ? total : to - keep;
                    if (to - from >= minLen && b - a >= 0.1) found.Add((a, b));
                    runStart = -1;
                }
            }
            return found;
        }

        // Ask first: how quiet, how long, and what that removes.
        double db = -42, minLen = 0.8, keep = 0.2;
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.SemiBold };
        var quietBox = new Slider { Minimum = -60, Maximum = -20, Value = db, Width = 220 };
        var lenBox = new Slider { Minimum = 0.3, Maximum = 3, Value = minLen, Width = 220 };
        var keepBox = new Slider { Minimum = 0.05, Maximum = 0.6, Value = keep, Width = 220 };
        var quietText = new TextBlock { Width = 70, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var lenText = new TextBlock { Width = 70, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var keepText = new TextBlock { Width = 70, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        List<(double From, double To)> ranges = new();
        void Update()
        {
            db = quietBox.Value; minLen = lenBox.Value; keep = keepBox.Value;
            quietText.Text = $"{db:0} dB";
            lenText.Text = $"{minLen:0.0} s";
            keepText.Text = $"{keep:0.00} s";
            ranges = Find(db, minLen, keep);
            double sum = ranges.Sum(r => r.To - r.From);
            summary.Text = ranges.Count == 0 ? "Nothing that quiet for that long."
                : $"Removes {ranges.Count} quiet part{(ranges.Count == 1 ? "" : "s")}, {TimelineView.Clock(sum, false)} in all: the edit goes from {TimelineView.Clock(total, false)} to {TimelineView.Clock(total - sum, false)}.";
        }
        quietBox.ValueChanged += (_, _) => Update();
        lenBox.ValueChanged += (_, _) => Update();
        keepBox.ValueChanged += (_, _) => Update();
        UIElement Row(string label, Slider sl, TextBlock val)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            r.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center });
            r.Children.Add(sl);
            r.Children.Add(val);
            return r;
        }
        var ok = new Button { Content = "Cut them out", MinWidth = 110, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Cut out the quiet parts", FontSize = 15, FontWeight = FontWeights.SemiBold });
        var what = new TextBlock { Text = "Where every sound track you hear is quiet at once. Picture and sound are cut together, so nothing goes out of sync.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), FontSize = 12 };
        what.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(what);
        panel.Children.Add(Row("Quieter than", quietBox, quietText));
        panel.Children.Add(Row("For at least", lenBox, lenText));
        panel.Children.Add(Row("Keep either side", keepBox, keepText));
        panel.Children.Add(summary);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = "Cut out silences", Owner = this, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
        };
        w.SetResourceReference(BackgroundProperty, "Bezel");
        w.SetResourceReference(ForegroundProperty, "Ink");
        ok.Click += (_, _) => { if (ranges.Count > 0) w.DialogResult = true; };
        Update();
        _status.Text = Hint();
        if (w.ShowDialog() != true || ranges.Count == 0 || _tl != tl) return;
        tl.Remember();
        foreach (var r in ranges.OrderByDescending(r => r.From)) tl.RemoveRange(r.From, r.To);
        _view.ClearSelection();
        OnEdited();
        _view.SetPlayhead(Math.Min(_view.Playhead, tl.Duration), true);
        _status.Text = $"Cut out {ranges.Count} quiet part{(ranges.Count == 1 ? "" : "s")}. Ctrl+Z puts them back.";
    }

    /* ---- tidying tracks ------------------------------------------------------------------ */

    /// <summary>
    /// "Clean up tracks": removes empty tracks, and sound tracks that are
    /// silent from start to end, after saying which. Undoable.
    /// </summary>
    private async Task CleanUpAsync()
    {
        if (_tl == null) return;
        _status.Text = "Checking which tracks are empty or silent…";
        // Silent is known from each sound's waveform (ready once its sound is decoded).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var found = new List<(int Track, string Why)>();
        while (true)
        {
            found.Clear();
            bool waiting = false;
            for (int i = 0; i < _tl.Tracks.Count; i++)
            {
                var tr = _tl.Tracks[i];
                if (tr.Locked) continue;
                var clips = _tl.Clips.Where(c => c.Track == i).ToList();
                if (clips.Count == 0) { found.Add((i, "empty")); continue; }
                if (tr.Type != TrackType.Audio) continue;
                bool silent = true;
                foreach (var c in clips)
                {
                    var s = Waveforms.Silent(c.File, Math.Max(0, c.Stream));
                    if (s == null) { waiting = true; silent = false; }
                    else if (s == false) { silent = false; break; }
                }
                if (silent) found.Add((i, "silent"));
            }
            if (!waiting || DateTime.UtcNow > deadline || _closed) break;
            await Task.Delay(400);
            if (_tl == null) return;
        }
        if (_closed || _tl == null) return;
        // At least one video track stays.
        int videos = _tl.Tracks.Count(t => t.Type == TrackType.Video);
        if (found.Count(f => _tl.Tracks[f.Track].Type == TrackType.Video) >= videos)
            found.Remove(found.Last(f => _tl.Tracks[f.Track].Type == TrackType.Video));
        if (found.Count == 0) { _status.Text = "Nothing to clean up: every track has something on it."; return; }
        var names = string.Join("\n", found.Select(f =>
        {
            var tr = _tl.Tracks[f.Track];
            return $"  {tr.Name}{(tr.Shown.Length > 0 ? " · " + tr.Shown : "")}  ({f.Why})";
        }));
        if (Dialog.Show(this, $"Remove {(found.Count == 1 ? "this track" : $"these {found.Count} tracks")}?\n\n{names}\n\n(Ctrl+Z brings them back.)", "Clean up tracks",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { _status.Text = Hint(); return; }
        _tl.Remember();
        foreach (var f in found.OrderByDescending(f => f.Track)) _tl.DeleteTrack(f.Track);
        _view.ClearSelection();
        OnEdited();
        _status.Text = $"Removed {found.Count} track{(found.Count == 1 ? "" : "s")}.  Ctrl+Z brings {(found.Count == 1 ? "it" : "them")} back.";
    }

    /// <summary>A track's volume was set: heard at once, kept for undo and with the clip. No restart, unless boosted past 100%.</summary>
    private void VolumeDone(int track)
    {
        if (_tl == null) return;
        if (_exact) LeaveExact();
        _live?.ApplyVolumes();
        _undo.IsEnabled = _tl.CanUndo;
        _redo.IsEnabled = _tl.CanRedo;
        SaveProjectSoon();
    }

    /// <summary>A small box to type a track's volume (0-200%).</summary>
    private double? AskPercent(string what, double current)
    {
        var box = new TextBox { Text = Math.Round(current).ToString(), MinWidth = 90, Padding = new Thickness(6, 4, 6, 4) };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(10, 0, 0, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        row.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"Volume for {what} (0 to 200%)" });
        panel.Children.Add(row);
        var w = new Window
        {
            Title = "Track volume", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
        };
        w.SetResourceReference(BackgroundProperty, "Bezel");
        w.SetResourceReference(ForegroundProperty, "Ink");
        double? result = null;
        ok.Click += (_, _) =>
        {
            var text = box.Text.Trim().TrimEnd('%').Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ||
                double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out v))
            { result = Math.Clamp(v, 0, 200); w.DialogResult = true; }
        };
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    /// <summary>Every editor key, in a small window ("?").</summary>
    private void ShowKeys()
    {
        var st = _app.Settings.Current;
        string K(string k) => string.IsNullOrWhiteSpace(k) ? "—" : k;
        var rows = new (string Keys, string What)[]
        {
            ("Space", "Play or pause"),
            ("← / →", "Back or forward a frame (Shift: a second)"),
            ("Home / End", "To the start or the end"),
            (K(st.EditorKeySplit), "Split at the playhead"),
            ($"{K(st.EditorKeyCutLeft)} / {K(st.EditorKeyCutRight)}", "Remove everything before / after the playhead"),
            ("Delete", _rippleOn ? "Delete and close the gap (Ripple is on)" : "Delete, leaving a gap"),
            ("Shift+Delete", _rippleOn ? "Delete, leaving a gap" : "Delete and close the gap"),
            ("Ctrl+V", "Paste a picture or files"),
            ("Ctrl+D", "Duplicate the selection"),
            ("T", "Add text at the playhead"),
            ("M", "Add a marker at the playhead (or remove the one there)"),
            ("Ctrl+← / Ctrl+→", "Jump to the previous / next marker"),
            ("Ctrl+G / Ctrl+Shift+G", "Group / ungroup"),
            ("Ctrl+Z / Ctrl+Shift+Z", "Undo / redo"),
            ("V / C", "Select tool / cut tool"),
            ("N", "Snapping on or off"),
            ("I / O", "Mark where saving starts / ends (Esc clears)"),
            ("+ / − / \\", "Zoom in / out / fit"),
            ("Alt", "Work on one piece (not its linked picture or sound); on the preview, no snapping"),
            ("Ctrl+scroll", "Zoom the timeline"),
            ("Double-click a picture", "Crop it on the preview"),
            ("Shift (turning)", "Turn in 15° steps"),
            ("?", "This list"),
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].Keys, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 18, 3) };
            k.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
            var w = new TextBlock { Text = rows[i].What, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i); Grid.SetRow(w, i); Grid.SetColumn(w, 1);
            grid.Children.Add(k); grid.Children.Add(w);
        }
        var win = new Window
        {
            Title = "Editor keys", Owner = this, Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid,
        };
        win.SetResourceReference(BackgroundProperty, "Bezel");
        win.SetResourceReference(ForegroundProperty, "Ink");
        win.PreviewKeyDown += (_, e) => { if (e.Key is Key.Escape or Key.OemQuestion) win.Close(); };
        win.ShowDialog();
    }

    /* ---- crop of the whole saved video -------------------------------------------- */

    private Rect PictureRect()
    {
        if (_info == null) return Rect.Empty;
        var (fw, fh) = Frame;
        double sw = _stage.ActualWidth, sh = _stage.ActualHeight, a = (double)fw / Math.Max(1, fh);
        double iw = Math.Min(sw, sh * a), ih = iw / a;
        return new Rect((sw - iw) / 2, (sh - ih) / 2, iw, ih);
    }

    private Point? _cropStart;

    private void CropDown(object sender, MouseButtonEventArgs e)
    {
        _cropStart = e.GetPosition(_cropLayer);
        _cropLayer.CaptureMouse();
    }

    private void CropMove(object sender, MouseEventArgs e)
    {
        if (_cropStart is not { } s) return;
        var p = e.GetPosition(_cropLayer);
        var r = new Rect(s, p);
        r.Intersect(PictureRect());
        if (r.IsEmpty) return;
        Canvas.SetLeft(_cropRect, r.X);
        Canvas.SetTop(_cropRect, r.Y);
        _cropRect.Width = r.Width;
        _cropRect.Height = r.Height;
    }

    private void CropUp(object sender, MouseButtonEventArgs e)
    {
        _cropLayer.ReleaseMouseCapture();
        if (_cropStart == null || _tl == null || _info == null) return;
        _cropStart = null;
        var pic = PictureRect();
        double k = Frame.W / pic.Width;
        int x = (int)Math.Round((Canvas.GetLeft(_cropRect) - pic.X) * k), y = (int)Math.Round((Canvas.GetTop(_cropRect) - pic.Y) * k);
        int w = (int)Math.Round(_cropRect.Width * k), h = (int)Math.Round(_cropRect.Height * k);
        if (w < 16 || h < 16) return;
        _tl.Remember();
        _tl.Crop = new CropBox(Math.Max(0, x), Math.Max(0, y), Math.Min(w, Frame.W), Math.Min(h, Frame.H));
        _status.Text = $"Crop: {w} × {h} (applies when saving)";
        _undo.IsEnabled = true;
        SaveProjectSoon();
    }

    private void ShowCrop()
    {
        if (_info == null || _tl == null) return;
        var pic = PictureRect();
        if (pic.IsEmpty) return;
        var c = _tl.Crop ?? new CropBox(0, 0, Frame.W, Frame.H);
        double k = pic.Width / Frame.W;
        Canvas.SetLeft(_cropRect, pic.X + c.X * k);
        Canvas.SetTop(_cropRect, pic.Y + c.Y * k);
        _cropRect.Width = c.Width * k;
        _cropRect.Height = c.Height * k;
    }

    /* ---- preview ------------------------------------------------------------------- */

    private const double ChunkSeconds = 10;
    private readonly HashSet<string> _chunks = new(StringComparer.OrdinalIgnoreCase);

    private MediaInfo? InfoOf(string file) => _proxyInfo.TryGetValue(file, out var i) ? i : null;

    /// <summary>
    /// The preview: exactly what will be saved (fades and layers included),
    /// small, from low-resolution copies of the clips. It's made in 10-second
    /// chunks, each named after everything inside it, so an edit only redoes
    /// the chunks it touched (and undo is instant); several are made at once,
    /// then joined by a quick copy.
    /// </summary>
    private async Task RenderPreviewAsync()
    {
        if (_tl == null || _info == null) return;
        _render?.Cancel();
        var cts = _render = new CancellationTokenSource();
        _live?.Pause();
        UpdatePlay();
        _preparing.Text = "Rendering exact preview…";
        _preparing.Visibility = Visibility.Visible;

        EnsureAllText();
        await EnsureInfoAsync();
        var proxies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Only pictures play from the small copies; sound is read from the clips themselves.
        foreach (var f in _tl.Clips.Where(c => _tl.Tracks[c.Track].Type == TrackType.Video && !c.IsImage)
                             .Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            proxies[f] = await TimelineRender.ProxyAsync(f) ?? f;
        if (cts.IsCancellationRequested) return;

        var (from, to) = _tl.Range;
        if (to - from < EditTimeline.MinLength) { _preparing.Text = "Nothing on the timeline"; return; }
        string FileFor(string f) => proxies.TryGetValue(f, out var px) ? px : f;
        var encoder = await Task.Run(() => TimelineRender.PreviewEncoder(21)) + "-g 30 ";

        var parts = new List<string>();
        var todo = new List<(string File, string Args)>();
        for (double a = from; a < to - 0.01; a += ChunkSeconds)
        {
            double b = Math.Min(to, a + ChunkSeconds);
            var file = IOPath.Combine(Paths.Temp, $"pchunk_{_clip.Id}_{ChunkKey(a, b, FileFor)}.mkv");
            parts.Add(file);
            if (!File.Exists(file))
                todo.Add((file, TimelineRender.BuildArgs(_tl, FrameInfo(_tl), file + ".part.mkv", true, encoder, FileFor,
                                                         (a, b), "-c:a pcm_s16le ", InfoOf)));
        }

        int done = parts.Count - todo.Count;
        bool failed = false;
        if (todo.Count > 0) _preparing.Text = $"Rendering exact preview… {done} of {parts.Count}";
        using (var gate = new SemaphoreSlim(3))
        {
            var jobs = todo.Select(async job =>
            {
                await gate.WaitAsync();
                try
                {
                    if (cts.IsCancellationRequested) return;
                    bool ok = await Task.Run(() => TimelineRender.Run(job.Args, cts.Token, null, 0));
                    if (ok)
                    {
                        try { File.Move(job.File + ".part.mkv", job.File, true); _chunks.Add(job.File); }
                        catch { failed = true; }
                    }
                    else
                    {
                        TryDelete(job.File + ".part.mkv");
                        if (!cts.IsCancellationRequested) failed = true;
                    }
                    if (!cts.IsCancellationRequested) _preparing.Text = $"Rendering exact preview… {++done} of {parts.Count}";
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(jobs);
        }
        if (cts.IsCancellationRequested) return;
        if (failed) { _preparing.Text = "The preview couldn't be made (the log says why)"; return; }

        // Joined by copying (the chunks' sound is uncompressed, so the joins are seamless).
        var list = IOPath.Combine(Paths.Temp, $"pchunks_{_clip.Id}.txt");
        await File.WriteAllLinesAsync(list, parts.Select(f => $"file '{f.Replace("'", "'\\''")}'"));
        var output = IOPath.Combine(Paths.Temp, $"preview_{_clip.Id}_{++_renderNo}.mkv");
        bool joined = await Task.Run(() => TimelineRender.Run(
            $"-hide_banner -loglevel error -f concat -safe 0 -i \"{list}\" -c copy -progress pipe:1 -y \"{output}\"",
            cts.Token, null, 0));
        if (cts.IsCancellationRequested) { TryDelete(output); return; }
        if (!joined) { _preparing.Text = "The preview couldn't be made (the log says why)"; return; }

        bool wasPlaying = _player?.IsPlaying == true;
        _player?.Dispose();
        TryDelete(_previewFile);
        _previewFile = output;
        _previewFrom = from;
        // As the chunks are made: 960 pixels along the frame's longer side.
        double ps = 960.0 / Math.Max(1, Math.Max(Frame.W, Frame.H));
        int pw = (int)Math.Round(Frame.W * ps), ph = (int)Math.Round(Frame.H * ps);
        pw -= pw % 2; ph -= ph % 2;
        var info = new MediaInfo(to - from, pw, ph, Math.Min(30, _info.Fps > 1 ? _info.Fps : 30), TimelineRender.HeardAudio(_tl).Count > 0, "h264");
        var player = new FramePlayer(output, info, _preview, pw, ph);
        player.Ended += () => { if (_player == player) UpdatePlay(); };
        _player = player;
        _exact = true;
        _boardBox.Visibility = Visibility.Hidden;
        _preview.Visibility = Visibility.Visible;
        UpdateHandles();
        player.Seek(Math.Clamp(_view.Playhead - from, 0, to - from));
        if (wasPlaying) player.Play();
        UpdatePlay();
        _preparing.Visibility = Visibility.Collapsed;
        _status.Text = "Showing the exact render. Any edit goes back to the live preview.";
    }

    /// <summary>A name for a chunk made of everything that affects it, so an unchanged chunk is reused.</summary>
    private string ChunkKey(double a, double b, Func<string, string> fileFor)
    {
        var sb = new System.Text.StringBuilder(FormattableString.Invariant($"v3|{a:F3}|{b:F3}|{_tl!.Volume:F3}|{Frame.W}x{Frame.H}|{_tl.Background}|"));
        foreach (var t in _tl.Tracks) sb.Append(t.Type).Append(t.Enabled ? '1' : '0').Append(t.Solo ? 'S' : '-').Append(FormattableString.Invariant($"{t.Volume:F3}")).Append(';');
        foreach (var c in _tl.Clips.Where(c => c.Start < b && c.End > a).OrderBy(c => c.Track).ThenBy(c => c.Start))
            sb.Append(FormattableString.Invariant(
                    $"{TimelineRender.Identity(fileFor(c.File))}|{c.Stream}|{c.Track}|{c.Kind}|{c.Start:F4}|{c.In:F4}|{c.Out:F4}|{c.FadeIn:F4}|{c.FadeOut:F4}|"))
              .Append(FormattableString.Invariant(
                    $"{c.X:F5}|{c.Y:F5}|{c.Scale:F5}|{c.Rotation:F3}|{c.Opacity:F4}|{c.CropL:F4}|{c.CropT:F4}|{c.CropR:F4}|{c.CropB:F4}|{c.Volume:F3}|{c.Speed:F4};"));
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }

    private void SeekPreview(double t)
    {
        if (_player == null) return;
        _player.Seek(Math.Max(0, t - _previewFrom));
    }

    private void TogglePlay()
    {
        if (!_exact)
        {
            if (_live == null) return;
            if (_live.IsPlaying) _live.Pause();
            else { _live.Seek(_view.Playhead); _live.Play(); }
            UpdatePlay();
            return;
        }
        if (_player == null || _tl == null) return;
        if (_player.IsPlaying) _player.Pause();
        else
        {
            var (from, to) = _tl.Range;
            if (_view.Playhead < from || _view.Playhead >= to - 0.05) { _view.SetPlayhead(from, false); SeekPreview(from); }
            _player.Play();
        }
        UpdatePlay();
    }

    private void UpdatePlay() =>
        _play.Content = new TextBlock
        {
            Text = (_exact ? _player?.IsPlaying : _live?.IsPlaying) == true ? "" : "",
            FontFamily = IconFont, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

    private void OnTick()
    {
        if (!_exact || _player?.IsPlaying != true) return;
        _syncing = true;
        _view.SetPlayhead(_previewFrom + _player.Position, true);
        _syncing = false;
    }

    private void UpdateTime()
    {
        if (_tl == null) return;
        _time.Text = $"{TimelineView.Clock(_view.Playhead)} / {TimelineView.Clock(_tl.Duration)}";
        var (from, to) = _tl.Range;
        _marks.Text = _tl.MarkOut > _tl.MarkIn + EditTimeline.Eps
            ? $"Saving {TimelineView.Clock(from)} – {TimelineView.Clock(to)}  (Esc clears)"
            : "Saving everything  (I / O to mark a part)";
    }

    /* ---- keys ------------------------------------------------------------------------ */

    /// <summary>The key reminder under the timeline, with the editor keys as they're set.</summary>
    private string Hint()
    {
        var st = _app.Settings.Current;
        string K(string k) => string.IsNullOrWhiteSpace(k) ? "—" : k;
        return $"Click anywhere on the timeline to move the playhead · {K(st.EditorKeySplit)} split · {K(st.EditorKeyCutLeft)} / {K(st.EditorKeyCutRight)} delete left / right · " +
               "Ctrl+V paste a picture · drop files anywhere · right-click for more · ? every key";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || _tl == null) return;
        var m = Keyboard.Modifiers;
        bool ctrl = m.HasFlag(ModifierKeys.Control), shift = m.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // The editor keys (Settings → Hotkeys → Editor) come first.
        var st = _app.Settings.Current;
        if (HotkeyService.Matches(st.EditorKeySplit, key, m))
        {
            if (!_view.SplitAtPlayhead()) _status.Text = "Nothing to split at the playhead.";
            e.Handled = true;
            return;
        }
        if (HotkeyService.Matches(st.EditorKeyCutLeft, key, m) || HotkeyService.Matches(st.EditorKeyCutRight, key, m))
        {
            bool left = HotkeyService.Matches(st.EditorKeyCutLeft, key, m);
            _player?.Pause();
            _live?.Pause();
            UpdatePlay();
            bool ok = left ? _view.CutLeftOfPlayhead() : _view.CutRightOfPlayhead();
            if (!ok) _status.Text = _view.Selected.Count > 0
                ? "The playhead isn't over the selected clip."
                : left ? "Move the playhead past the start first." : "Nothing after the playhead.";
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.V when ctrl: _ = PasteAsync(); break;
            case Key.D when ctrl: if (!_view.DuplicateSelection()) _status.Text = "Select something to duplicate first."; break;
            case Key.T when !ctrl: AddText(-2, _view.Playhead); break;
            case Key.M when !ctrl: _view.ToggleMarker(); break;
            case Key.Left when ctrl: if (!_view.JumpMarker(false)) _status.Text = "No marker before the playhead (M adds one)."; break;
            case Key.Right when ctrl: if (!_view.JumpMarker(true)) _status.Text = "No marker after the playhead (M adds one)."; break;
            case Key.V when !ctrl: SetTool(EditTool.Select); break;
            case Key.C when !ctrl: ToggleCut(); break;
            case Key.G when ctrl: _view.GroupSelection(!shift); break;
            case Key.N: ToggleSnap(); break;
            case Key.I when !ctrl: SetMark(true); break;
            case Key.O when !ctrl: SetMark(false); break;
            case Key.OemQuestion when shift: case Key.F1: ShowKeys(); break;
            case Key.Escape:
                if (_handles.CropMode) _handles.CropMode = false;
                else if (_view.Selected.Count > 0 || _view.SelectedGap != null) _view.ClearSelection(); else ClearMarks();
                break;
            case Key.Enter when _handles.CropMode: _handles.CropMode = false; break;
            case Key.Delete: case Key.Back: _view.DeleteSelection(shift != _rippleOn); break;
            case Key.Z when ctrl && shift: if (_tl.Redo()) OnEdited(); break;
            case Key.Z when ctrl: if (_tl.Undo()) OnEdited(); break;
            case Key.Y when ctrl: if (_tl.Redo()) OnEdited(); break;
            case Key.Left: Step(shift ? -1 : -1.0 / 30); break;
            case Key.Right: Step(shift ? 1 : 1.0 / 30); break;
            case Key.OemPlus: case Key.Add: _view.Zoom(1.5); break;
            case Key.OemMinus: case Key.Subtract: _view.Zoom(1 / 1.5); break;
            case Key.Oem5: case Key.OemBackslash: _view.FitAll(); break;
            case Key.Home: Step(-_view.Playhead); break;
            case Key.End: Step(_tl.Duration - _view.Playhead); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Step(double dt)
    {
        _player?.Pause();
        _live?.Pause();
        UpdatePlay();
        _view.SetPlayhead(Math.Clamp(_view.Playhead + dt, 0, _tl?.Duration ?? 0), true);
    }

    /* ---- saving ------------------------------------------------------------------------ */

    private async Task SaveAsync(bool replace)
    {
        if (_tl == null || _info == null || _saving != null) return;
        var (from, to) = _tl.Range;
        if (to - from < EditTimeline.MinLength) { _status.Text = "There's nothing on the timeline to save."; return; }
        if (replace && Dialog.Show("Replace the original clip with this edit?", "Persocord",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _player?.Pause();
        _live?.Pause();
        UpdatePlay();
        _saving = new CancellationTokenSource();
        _saveCopy.IsEnabled = _replace.IsEnabled = false;
        _cancel.IsEnabled = true;
        _progress.Value = 0;
        _progress.Visibility = _cancel.Visibility = Visibility.Visible;
        _status.Text = "Getting ready…";
        // Preview copies being made in the background compete with the save
        // (and hold the original open): stopped, and made again later if needed.
        TimelineRender.StopBackgroundCopies();
        var output = IOPath.Combine(Paths.Temp, $"edit_{Guid.NewGuid():N}.mp4");
        var preset = (SavePreset)Math.Max(0, _preset.SelectedIndex);
        // Vertical Shorts: a 9:16 frame for this save only (a blurred copy filling the sides),
        // unless a frame shape was chosen for the edit already.
        var tl = _tl;
        if (preset == SavePreset.Shorts && _tl.Aspect.Length == 0)
        {
            tl = EditTimeline.FromJson(_tl.ToJson()) ?? _tl;
            foreach (var kv in _tl.SourceLength) tl.SourceLength[kv.Key] = kv.Value;
            tl.Aspect = "9:16";
            tl.Background = FrameBackground.Blur;
            tl.Crop = null;
        }
        var (frameW, frameH) = tl.FrameSize(_info.Width, _info.Height);
        try
        {
            EnsureAllText();
            await EnsureInfoAsync();
            var hw = await ClipEditor.BestEncoderAsync();
            int q = _app.Settings.Current.QualityCq;
            bool exactCuts = _exactCuts.IsChecked == true;
            var progress = new Progress<double>(v => _progress.Value = v);
            _status.Text = "Saving…";

            // Only the original clip, trimmed and cut: the quick route (instant
            // unless Exact cuts is on). Anything more is rendered in full.
            var plan = TimelineRender.AsSimplePlan(tl, _path, _info);
            bool ok;
            if (plan != null)
            {
                bool instant;
                (ok, instant) = await ClipEditor.ExportAsync(_path, _info, plan, output, hw, q, progress, _saving.Token, exactCuts);
                if (ok && instant && !exactCuts) Log.Write("Edit saved instantly (cuts on keyframes)");
                // Each sound track keeps its name: every one as in the clip,
                // or with tracks left out, a new Mix then the kept ones.
                if (ok && !plan.Mute)
                {
                    var names = plan.MutedTracks.Count == 0
                        ? _info.AudioTracks.ToList()
                        : new[] { "Mix" }.Concat(_info.SeparateTracks.Where(t => !plan.MutedTracks.Contains(t.Stream)).Select(t => t.Name)).ToList();
                    await ClipEditor.NameTracksAsync(output, names);
                }
            }
            else
            {
                double pixels = (tl.Crop is { } cr ? (double)cr.Width * cr.Height : (double)frameW * frameH) / Math.Max(1.0, (double)_info.Width * _info.Height);
                // The allowance covers every clip used, each by how much of it is in the edit
                // (a still picture counts as the clip itself would).
                var parts = new List<(MediaInfo, double)>();
                foreach (var (file, secs) in TimelineRender.UsedVideoSeconds(tl))
                {
                    var fi = string.Equals(file, _path, StringComparison.OrdinalIgnoreCase) || EditorMedia.KindOf(file) == MediaKind.Image
                        ? _info : InfoOf(file) ?? await ClipEditor.ProbeAsync(file);
                    if (fi != null) parts.Add((fi, secs));
                }
                int kbps = ClipEditor.WeightedKbps(parts);
                var basis = kbps > 0 ? _info with { Kbps = kbps } : _info;
                var encoder = await ClipEditor.SaveEncoderAsync(hw, q, basis, pixels, _tl.Speed);
                var args = TimelineRender.BuildArgs(tl, FrameInfo(tl), output, false, encoder, f => f, null, null, InfoOf);
                double seconds = (to - from) / Math.Max(0.01, _tl.Speed);
                var token = _saving.Token;
                ok = await Task.Run(() => TimelineRender.Run(args, token, progress, seconds));
            }
            if (_saving.IsCancellationRequested) { _status.Text = "Saving was cancelled."; TryDelete(output); return; }
            if (!ok) { _status.Text = "It couldn't be saved. The log has the details."; TryDelete(output); return; }
            // Each sound track on its own, without the Mix (for other video editors).
            if (preset == SavePreset.Separate && await ClipEditor.DropMixAsync(output))
                Log.Write("Edit saved with separate sound tracks (no Mix)");
            // Small enough for Discord: made smaller only if it has to be.
            if (preset is SavePreset.Discord10 or SavePreset.Discord50)
            {
                long target = (preset == SavePreset.Discord10 ? 10 : 50) * 1048576L;
                long size = new FileInfo(output).Length;
                if (size > target)
                {
                    _status.Text = $"Making it fit {target / 1048576} MB…";
                    _progress.Value = 0;
                    var small = IOPath.Combine(Paths.Temp, $"edit_{Guid.NewGuid():N}.mp4");
                    var hwSmall = _app.Settings.Current.ShrinkOnGpu ? await ClipEditor.BestEncoderAsync() : EncoderKind.X264;
                    bool shrunk = await MediaTools.ShrinkToFitAsync(output, small, target, _app.Settings.Current.AudioBitrateKbps, hwSmall, 1080, keepAllTracks: false);
                    if (_saving.IsCancellationRequested) { TryDelete(small); TryDelete(output); _status.Text = "Saving was cancelled."; return; }
                    if (!shrunk) { TryDelete(small); TryDelete(output); _status.Text = $"It couldn't be made to fit {target / 1048576} MB. The log has the details."; return; }
                    TryDelete(output);
                    output = small;
                }
            }

            if (replace)
            {
                // Nothing here may still be reading the original while it's swapped:
                // the preview, and the sound still being decoded from it (on a long
                // clip that can take a while, and held the file so the swap failed).
                _status.Text = "Replacing the original…";
                _live?.Dispose();
                _live = null;
                _background.Cancel();
                await PcmCache.StopAsync(_path);
            }
            var sizes = plan ?? new EditPlan
            {
                In = 0, Out = to - from, Speed = tl.Speed,
                Crop = tl.Crop is { } c ? new System.Drawing.Rectangle(c.X, c.Y, c.Width, c.Height) : null,
            };
            // The saved video's size: the clip's own on the quick route, the frame's otherwise.
            int savedW = plan != null ? _info.Width : frameW, savedH = plan != null ? _info.Height : frameH;
            if (!await _app.ApplyEditAsync(_clip, output, replace, savedW, savedH, sizes))
            {
                // The original couldn't be swapped (another program has it open):
                // the finished edit isn't thrown away, it's kept as a copy.
                if (replace && File.Exists(output) && await _app.ApplyEditAsync(_clip, output, false, savedW, savedH, sizes))
                    _status.Text = "The original is open in another program, so the edit was saved as a copy next to it.";
                else
                {
                    _status.Text = "It was made, but couldn't be filed. The log has the details.";
                    TryDelete(output);
                }
                if (replace && !_closed) await StartLiveAsync();
                return;
            }
            if (replace)
            {
                // The original is now the edit: nothing left to carry on from.
                _closingAfterSave = true;
                TryDelete(ProjectPath);
                Close();
                return;
            }
            _status.Text = "Saved as a copy.";
        }
        catch (Exception ex)
        {
            Log.Error("save edit", ex);
            _status.Text = "It couldn't be saved. The log has the details.";
        }
        finally
        {
            _saving?.Dispose();
            _saving = null;
            _saveCopy.IsEnabled = _replace.IsEnabled = true;
            _progress.Visibility = _cancel.Visibility = Visibility.Collapsed;
        }
    }

    private void Shutdown()
    {
        _tick.Stop();
        _debounce.Stop();
        _render?.Cancel();
        _saving?.Cancel();
        _background.Cancel();
        SaveProject();
        // Nothing may play once the editor is closed (a preview copy finishing
        // later used to bring the sound back).
        _closed = true;
        _live?.Dispose();
        _live = null;
        // The decoded sound is kept so reopening this clip is instant; the temp
        // cleanup holds working copies to 3 GB (oldest go first).
        if (_tl != null)
            foreach (var g in _tl.Clips.GroupBy(c => c.File, StringComparer.OrdinalIgnoreCase))
                PcmCache.Touch(g.Key, g.Select(c => Math.Max(0, c.Stream)));
        _ = TempJanitor.RunAsync();
        _player?.Dispose();
        TryDelete(_previewFile);
        foreach (var f in _chunks) TryDelete(f);
        // The pictures and filmstrips kept in memory go with the last editor
        // (another one still open keeps using them).
        if (--_openEditors <= 0)
        {
            _openEditors = 0;
            Pictures.Clear();
            Filmstrips.Clear();
            Waveforms.Clear();
            // What the editor used is handed back to Windows a moment after it
            // closes (once its background work has stopped), rather than kept
            // by Persocord until it next restarts.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (_openEditors > 0) return;
                IdleMemory.Release("the editor closed");
            });
        }
    }

    private static int _openEditors;

    /// <summary>How many editors are open now.</summary>
    public static int OpenCount => _openEditors;
}
