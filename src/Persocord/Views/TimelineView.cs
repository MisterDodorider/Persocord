using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Persocord.Services;

namespace Persocord.Views;

public enum EditTool { Select, Cut }

/// <summary>
/// The editor's timeline: video tracks (V2, V1…) over audio tracks (A1, A2…),
/// a ruler, the playhead and the in/out marks. Select tool: click, drag to
/// move (onto other clips too: the one underneath is covered there), drag
/// edges to trim, drag a clip's top corners for fades, click a gap to select
/// it. A click anywhere that isn't a clip moves the playhead. Cut tool: click
/// to cut. Alt works on one piece instead of linked video and sound together.
/// Each track's header has its switches (shown/heard, solo, lock, delete);
/// drag a header up or down to reorder, double-click it to rename, and
/// right-click anything for what can be done with it.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    private const double Header = 196, Ruler = 26, NewStrip = 14, VideoRow = 50, AudioRow = 44, Split = 8, Edge = 6, SnapPx = 8,
        BelowRows = 34, Btn = 20;

    public EditTimeline? Tl { get; private set; }
    public EditTool Tool { get; set; } = EditTool.Select;
    public bool Snap { get; set; } = true;
    public double Playhead { get; private set; }

    /// <summary>Pixels per second, and the time at the left edge.</summary>
    private double _pps = 40, _scroll;

    public readonly HashSet<string> Selected = new();
    public (int Track, double From, double To)? SelectedGap { get; private set; }

    /// <summary>Something was edited (after a drag ends, or at once for clicks).</summary>
    public event Action? Edited;
    public event Action<double>? PlayheadMoved;
    public event Action? SelectionChanged;
    /// <summary>A track's volume was clicked (not dragged): ask for a value.</summary>
    public event Action<int>? VolumeClicked;
    /// <summary>A track's volume is being dragged: only loudness changes (nothing restarts).</summary>
    public event Action<int>? VolumeChanging;
    /// <summary>A volume drag ended: the change is kept (and undoable).</summary>
    public event Action<int>? VolumeCommitted;
    /// <summary>A track's name was double-clicked (or Rename chosen).</summary>
    public event Action<int>? RenameRequested;
    /// <summary>Something was dropped (files, or media from the editor's lists) on a track (-1: a new one) at a time.</summary>
    public event Action<IDataObject, int, double>? Dropped;
    /// <summary>Paste or Import was chosen from a track's menu: there, at that time.</summary>
    public event Action<int, double>? PasteRequested;
    public event Action<int, double>? ImportRequested;
    /// <summary>"Clean up tracks" was chosen.</summary>
    public event Action? CleanUpRequested;
    /// <summary>Something worth telling (shown under the timeline).</summary>
    public event Action<string>? Status;
    /// <summary>"Add text here" was chosen: on that track (-2: wherever suits) at that time.</summary>
    public event Action<int, double>? TextRequested;
    /// <summary>"Freeze frame here" was chosen for a clip (at the playhead).</summary>
    public event Action<TClip>? FreezeRequested;
    /// <summary>A sound clip's volume line is being dragged (heard at once; kept when let go).</summary>
    public event Action? ClipVolumeChanging;

    /// <summary>Each file's size, length and kind (for filmstrips).</summary>
    public Func<string, MediaInfo?>? InfoOf { get; set; }

    public TimelineView()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 160;
        AllowDrop = true;
        _edgeTimer.Tick += (_, _) => EdgeScroll();
        // A new look (Customization → Editor) repaints the timeline, as does a waveform becoming ready.
        Action repaint = () => Dispatcher.BeginInvoke(new Action(Redraw));
        // Loaded can come more than once (moved between windows): never subscribed twice.
        Loaded += (_, _) =>
        {
            LookStore.Changed -= repaint; Waveforms.Ready -= repaint; Filmstrips.Ready -= repaint;
            LookStore.Changed += repaint; Waveforms.Ready += repaint; Filmstrips.Ready += repaint;
        };
        Unloaded += (_, _) => { LookStore.Changed -= repaint; Waveforms.Ready -= repaint; Filmstrips.Ready -= repaint; };
        SizeChanged += (_, _) => { ClampView(); Redraw(); };
    }

    public void Attach(EditTimeline tl)
    {
        Tl = tl;
        Selected.Clear();
        SelectedGap = null;
        FitAll();
    }

    /// <summary>The whole timeline in view.</summary>
    public void FitAll()
    {
        if (Tl == null) return;
        _pps = FitPps();
        _scroll = 0;
        Redraw();
    }

    /// <summary>Zooms in (factor above 1) or out around the playhead, never further out than the whole timeline.</summary>
    public void Zoom(double factor)
    {
        if (Tl == null) return;
        double anchorX = Math.Clamp(X(Playhead), Header, Math.Max(Header, ActualWidth));
        _pps = Math.Clamp(_pps * factor, FitPps(), 2000);
        _scroll = Playhead - (anchorX - Header) / _pps;
        ClampView();
        Redraw();
    }

    /// <summary>Pixels per second that show the whole timeline.</summary>
    private double FitPps()
    {
        double w = Math.Max(200, ActualWidth - Header - 24);
        return Math.Clamp(w / Math.Max(1, Tl?.Duration ?? 1), 0.05, 2000);
    }

    /// <summary>Seconds visible across the timeline.</summary>
    private double ViewSeconds => Math.Max(0.1, (ActualWidth - Header) / _pps);

    /// <summary>
    /// Keeps the view on the timeline: never before the start, and never so far
    /// right that the timeline's end leaves the screen (moving a clip past the
    /// end may go a little further, to make room).
    /// </summary>
    private void ClampView(double extra = 0)
    {
        if (Tl == null) return;
        _pps = Math.Clamp(_pps, Math.Min(FitPps(), 2000), 2000);
        double max = Math.Max(0, Tl.Duration + extra - ViewSeconds * 0.85);
        _scroll = Math.Clamp(_scroll, 0, max);
    }

    public void SetPlayhead(double t, bool announce) => SetPlayhead(t, announce, follow: true);

    private void SetPlayhead(double t, bool announce, bool follow)
    {
        if (!double.IsFinite(t)) t = 0;
        Playhead = Math.Clamp(t, 0, Math.Max(0, Tl?.Duration ?? t));
        double before = _scroll;
        // Keep it in view while playing (dragging scrolls gently at the edges instead).
        if (follow)
        {
            double x = X(Playhead);
            if (x > ActualWidth - 30) _scroll = Playhead - ViewSeconds * 0.2;
            else if (x < Header) _scroll = Playhead - ViewSeconds * 0.1;
            ClampView();
        }
        if (_scroll != before) Redraw();   // the view moved: everything is redrawn
        else InvalidateVisual();           // only the playhead line moves
        if (announce) PlayheadMoved?.Invoke(Playhead);
    }

    /* ---- scrolling at the edges while dragging ------------------------------------- */

    private readonly System.Windows.Threading.DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Point _lastMouse;

    /// <summary>Dragging past either edge scrolls, faster the further past (up to about a screen a second).</summary>
    private void EdgeScroll()
    {
        if (Tl == null || _drag is Drag.None or Drag.Volume or Drag.TrackMove || Mouse.LeftButton != MouseButtonState.Pressed) { _edgeTimer.Stop(); return; }
        double over = _lastMouse.X > ActualWidth - 16 ? _lastMouse.X - (ActualWidth - 16)
                    : _lastMouse.X < Header ? _lastMouse.X - Header : 0;
        if (over == 0) { _edgeTimer.Stop(); return; }
        double speed = Math.Clamp(Math.Abs(over) / 80.0, 0.1, 1.0) * ViewSeconds;   // seconds of timeline per second
        double before = _scroll;
        _scroll += Math.Sign(over) * speed * 0.016;
        ClampView(_drag == Drag.Scrub ? 0 : ViewSeconds * 0.5);
        if (_scroll == before) return;   // at the start or end: nothing more to show
        DragTo(_lastMouse);
        Redraw();
    }

    /* ---- geometry ---------------------------------------------------------------- */

    private double X(double t) => Header + (t - _scroll) * _pps;
    private double T(double x) => _scroll + (x - Header) / _pps;

    private List<(int Track, double Y, double H)> Rows()
    {
        var rows = new List<(int, double, double)>();
        if (Tl == null) return rows;
        double y = Ruler + NewStrip;
        bool sawAudio = false;
        for (int i = 0; i < Tl.Tracks.Count; i++)
        {
            bool audio = Tl.Tracks[i].Type == TrackType.Audio;
            if (audio && !sawAudio) { y += Split; sawAudio = true; }
            double h = audio ? AudioRow : VideoRow;
            rows.Add((i, y, h));
            y += h;
        }
        return rows;
    }

    private double RowsBottom()
    {
        var rows = Rows();
        return rows.Count == 0 ? Ruler + NewStrip : rows[^1].Y + rows[^1].H;
    }

    protected override Size MeasureOverride(Size available)
    {
        double h = RowsBottom() + BelowRows;
        return new Size(double.IsInfinity(available.Width) ? 800 : available.Width, Math.Max(MinHeight, h));
    }

    private (int Track, double Y, double H)? RowAt(double y)
    {
        foreach (var r in Rows())
            if (y >= r.Y && y < r.Y + r.H) return r;
        return null;
    }

    /* ---- the header's switches ------------------------------------------------------ */

    private enum Part { None, Toggle, Solo, Lock, Delete, Volume, Name }

    private static Rect ToggleBox(double y, double h) => new(8, y + h / 2 - Btn / 2, Btn, Btn);
    private static Rect DeleteBox(double y) => new(Header - 8 - Btn, y + 4, Btn, Btn);
    private static Rect LockBox(double y) => new(Header - 8 - 2 * Btn - 2, y + 4, Btn, Btn);
    private static Rect SoloBox(double y) => new(Header - 8 - 3 * Btn - 4, y + 4, Btn, Btn);
    /// <summary>Where an audio track's volume sits in its header.</summary>
    private static Rect VolumeBox(double rowY, double rowH) => new(Header - 54, rowY + rowH - 20, 46, 17);

    private (int Track, Part Part)? HeaderHit(Point p)
    {
        if (Tl == null || p.X >= Header || RowAt(p.Y) is not { } r) return null;
        var tr = Tl.Tracks[r.Track];
        if (ToggleBox(r.Y, r.H).Contains(p)) return (r.Track, Part.Toggle);
        if (DeleteBox(r.Y).Contains(p)) return (r.Track, Part.Delete);
        if (LockBox(r.Y).Contains(p)) return (r.Track, Part.Lock);
        if (tr.Type == TrackType.Audio && SoloBox(r.Y).Contains(p)) return (r.Track, Part.Solo);
        if (tr.Type == TrackType.Audio && VolumeBox(r.Y, r.H).Contains(p)) return (r.Track, Part.Volume);
        return (r.Track, Part.Name);
    }

    /* ---- drawing ------------------------------------------------------------------ */

    private static Brush B(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    private static readonly Brush
        Line = B("#2A2E36"), Muted = B("#8A919C"), Ink = B("#E6E8EC"), Faint = B("#5A606A"),
        Disabled = B("#3A3D44"), GapSel = B("#33FFFFFF"), FadeShade = B("#66000000"), Outside = B("#88000000"),
        Covered = B("#B0101216"), Wave = B("#8CFFFFFF"), HoverBtn = B("#22FFFFFF"), DropGhost = B("#44FFFFFF"),
        Warn = B("#E0594D"), MarkerBrush = B("#FFC83D"), LabelBand = MakeBand();
    private static Brush MakeBand()
    {
        var g = new LinearGradientBrush(Color.FromArgb(0xC0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90);
        g.Freeze();
        return g;
    }
    private static readonly Pen VolumePen = Frozen(new Pen(B("#E0FFFFFF"), 1.2)), MarkerPen = Frozen(new Pen(B("#66FFC83D"), 1));
    private static readonly Pen LinePen = Frozen(new Pen(Line, 1)), SelPen = Frozen(new Pen(B("#F2F4F8"), 2)), HandlePen = Frozen(new Pen(B("#FFFFFF"), 1)),
        DropPen = Frozen(new Pen(B("#F2F4F8"), 2) { DashStyle = DashStyles.Dash });

    // Colours from Customization → Editor (background, video, sound, playhead).
    private static Brush Bg = B("#121418"), RowA = B("#181B20"), RowB = B("#1B1E24"), HeaderBg = B("#101216"),
        VideoClip = B("#2F6FA8"), ImageClip = B("#4A6FA0"), AudioClip = B("#3E7D3A"), PlayheadBrush = B("#FF4D5E"),
        VideoDot = B("#2F6FA8"), AudioDot = B("#3E7D3A");
    private static Pen PlayPen = new(PlayheadBrush, 1.5);
    private static readonly Pen ClipEdge = Frozen(new Pen(B("#24FFFFFF"), 1));
    private static Pen Frozen(Pen p) { p.Freeze(); return p; }
    private static Persocord.Models.Looks? _coloursFor;

    private static void RefreshColours()
    {
        var l = LookStore.Current;
        if (ReferenceEquals(l, _coloursFor)) return;
        _coloursFor = l;
        Brush S(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        var bg = ThemeApplier.Parse(l.TimelineBackground, Color.FromRgb(0x12, 0x14, 0x18));
        Bg = S(bg);
        RowA = S(ThemeApplier.Mix(bg, Colors.White, 0.025));
        RowB = S(ThemeApplier.Mix(bg, Colors.White, 0.04));
        HeaderBg = S(ThemeApplier.Mix(bg, Colors.Black, 0.12));
        // Clips catch a little light along the top, like the rest of the app's surfaces.
        Brush Lit(Color c)
        {
            var g = new LinearGradientBrush(ThemeApplier.Mix(c, Colors.White, 0.16), ThemeApplier.Mix(c, Colors.Black, 0.06), 90);
            g.Freeze();
            return g;
        }
        var video = ThemeApplier.Parse(l.TimelineVideoColor, Color.FromRgb(0x2F, 0x6F, 0xA8));
        var audio = ThemeApplier.Parse(l.TimelineAudioColor, Color.FromRgb(0x3E, 0x7D, 0x3A));
        VideoClip = Lit(video);
        // Pictures are a lighter shade of the video colour, so they stand out.
        ImageClip = Lit(ThemeApplier.Mix(video, Colors.White, 0.18));
        AudioClip = Lit(audio);
        VideoDot = S(video);
        AudioDot = S(audio);
        PlayheadBrush = S(ThemeApplier.Parse(l.PlayheadColor, Color.FromRgb(0xFF, 0x4D, 0x5E)));
        var pen = new Pen(PlayheadBrush, 1.5);
        pen.Freeze();
        PlayPen = pen;
    }
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Icons = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private FormattedText Text(string s, double size, Brush brush, Typeface? face = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // The timeline is drawn once into a cached picture; moving the playhead
    // (66 times a second while playing) only draws the playhead line over it.
    private DrawingGroup? _static;
    private bool _dirty = true, _drawFailed;
    private Size _drawnSize;

    /// <summary>Everything changed: redrawn from scratch.</summary>
    private void Redraw() { _dirty = true; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var before = _coloursFor;
        RefreshColours();
        if (!ReferenceEquals(before, _coloursFor)) _dirty = true;
        var size = new Size(ActualWidth, ActualHeight);
        if (_dirty || _static == null || size != _drawnSize)
        {
            _static = new DrawingGroup();
            using (var sdc = _static.Open())
            {
                // A drawing problem shows an empty timeline (and is logged once), never a stream of errors.
                try { RenderStatic(sdc); }
                catch (Exception ex) { if (!_drawFailed) { _drawFailed = true; Log.Error("timeline drawing", ex); } }
            }
            _static.Freeze();
            _dirty = false;
            _drawnSize = size;
        }
        dc.DrawDrawing(_static);
        if (Tl == null) return;

        // Where a track being dragged would go.
        if (_drag == Drag.TrackMove && _trackTo != _trackFrom && Rows().FirstOrDefault(r => r.Track == _trackTo) is { H: > 0 } tr)
        {
            double y = _trackTo < _trackFrom ? tr.Y : tr.Y + tr.H;
            dc.DrawRectangle(PlayheadBrush, null, new Rect(0, y - 1.5, ActualWidth, 3));
        }

        dc.PushClip(new RectangleGeometry(new Rect(Header, 0, Math.Max(0, ActualWidth - Header), ActualHeight)));
        // Something being dragged in from outside: where it would land.
        if (_dropAt is { } drop)
        {
            double x = X(drop.Time);
            var row = Rows().FirstOrDefault(r => r.Track == drop.Track);
            var box = drop.Track < 0 || row.H == 0
                ? new Rect(x, Ruler + 1, 120, NewStrip - 2)
                : new Rect(x, row.Y + 3, 120, row.H - 6);
            dc.DrawRoundedRectangle(DropGhost, DropPen, box, 5, 5);
        }
        double px = X(Playhead);
        dc.DrawLine(PlayPen, new Point(px, 0), new Point(px, ActualHeight));
        dc.DrawGeometry(PlayheadBrush, null, Tri(px - 6, 0, px + 6, 10, true, pointDown: true));
        dc.Pop();
    }

    private void RenderStatic(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (Tl == null) return;
        var rows = Rows();
        bool dragging = _drag == Drag.Move && _changed;

        // A strip above the pictures: drop a clip there for a new track on top.
        dc.DrawRectangle(HeaderBg, null, new Rect(0, Ruler, Header, NewStrip));
        if (dragging && _kinds.Values.Any(k => k == TrackType.Video))
        {
            var hint = Text("Drop here for a new track", 9.5, Muted);
            dc.DrawText(hint, new Point(Header + 8, Ruler + (NewStrip - hint.Height) / 2));
        }

        // Rows and their headers.
        foreach (var (track, y, rh) in rows)
        {
            var tr = Tl.Tracks[track];
            bool audio = tr.Type == TrackType.Audio;
            dc.DrawRectangle(track % 2 == 0 ? RowA : RowB, null, new Rect(Header, y, w - Header, rh));
            dc.DrawRectangle(HeaderBg, null, new Rect(0, y, Header, rh));
            dc.DrawRectangle(audio ? AudioDot : VideoDot, null, new Rect(0, y + 4, 3, rh - 8));
            dc.DrawLine(LinePen, new Point(0, y + rh), new Point(w, y + rh));
            bool hot = _hover == track;

            // Shown / heard.
            var tb = ToggleBox(y, rh);
            if (hot) dc.DrawRoundedRectangle(HoverBtn, null, tb, 4, 4);
            Glyph(dc, audio ? (tr.Enabled ? "" : "") : (tr.Enabled ? "" : ""), tb, tr.Enabled ? Ink : Warn, 13);

            // Number, then what's on it.
            var name = Text(tr.Name, 12.5, tr.Enabled ? Ink : Muted);
            name.SetFontWeight(FontWeights.SemiBold);
            dc.DrawText(name, new Point(34, y + 5));
            if (tr.Shown.Length > 0)
            {
                var src = Text(tr.Shown, 10.5, Muted);
                src.MaxTextWidth = Math.Max(10, (audio ? Header - 58 : Header - 8) - 34);
                src.MaxLineCount = 1;
                src.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(src, new Point(34, y + rh - 20));
            }

            // Solo (sound), lock, delete: bright when on (or the mouse is over the track).
            if (audio)
            {
                var sb = SoloBox(y);
                if (tr.Solo) dc.DrawRoundedRectangle(PlayheadBrush, null, sb, 4, 4);
                var s = Text("S", 11, tr.Solo ? Brushes.White : hot ? Ink : Faint);
                s.SetFontWeight(FontWeights.Bold);
                dc.DrawText(s, new Point(sb.X + (sb.Width - s.Width) / 2, sb.Y + (sb.Height - s.Height) / 2));
            }
            Glyph(dc, tr.Locked ? "" : "", LockBox(y), tr.Locked ? Ink : hot ? Muted : Faint, 12);
            Glyph(dc, "", DeleteBox(y), hot ? Muted : Faint, 12);

            if (audio)
            {
                var vol = Text($"{Math.Round(tr.Volume * 100)}%", 10.5, tr.Enabled ? Ink : Muted);
                var box = VolumeBox(y, rh);
                dc.DrawRoundedRectangle(RowB, LinePen, box, 3, 3);
                dc.DrawText(vol, new Point(box.Right - vol.Width - 5, box.Top + (box.Height - vol.Height) / 2));
            }
            if (tr.Locked)
                dc.DrawRectangle(Outside, null, new Rect(Header, y, w - Header, rh));
        }
        dc.DrawLine(LinePen, new Point(Header, 0), new Point(Header, h));

        dc.PushClip(new RectangleGeometry(new Rect(Header, 0, Math.Max(0, w - Header), h)));

        // A selected gap.
        if (SelectedGap is { } gap)
            foreach (var (track, y, rh) in rows.Where(r => r.Track == gap.Track))
                dc.DrawRectangle(GapSel, null, new Rect(X(gap.From), y + 3, (gap.To - gap.From) * _pps, rh - 6));

        // Clips: the ones being moved last, on top of what they'd cover.
        var rowOf = rows.ToDictionary(r => r.Track);
        var moving = dragging ? new HashSet<TClip>(_moving) : new HashSet<TClip>();
        foreach (var c in Tl.Clips.Where(c => !moving.Contains(c))) DrawClip(dc, c, rowOf, w, false);
        foreach (var c in moving)
        {
            // What it would cover is darkened underneath.
            if (rowOf.TryGetValue(c.Track, out var mr))
                foreach (var o in Tl.Clips.Where(o => !moving.Contains(o) && o.Track == c.Track && o.Start < c.End && o.End > c.Start))
                {
                    double a = X(Math.Max(o.Start, c.Start)), b = X(Math.Min(o.End, c.End));
                    dc.DrawRectangle(Covered, null, new Rect(a, mr.Y + 3, Math.Max(0, b - a), mr.H - 6));
                }
        }
        foreach (var c in moving) DrawClip(dc, c, rowOf, w, true);

        // Ruler, with the in/out marks.
        dc.DrawRectangle(HeaderBg, null, new Rect(Header, 0, w - Header, Ruler));
        double step = TickStep();
        double first = Math.Floor(_scroll / step) * step;
        for (double t = first; X(t) < w; t += step)
        {
            if (t < 0) continue;
            double x = X(t);
            dc.DrawLine(LinePen, new Point(x, Ruler - 8), new Point(x, Ruler));
            dc.DrawText(Text(Clock(t, step < 1), 10, Muted), new Point(x + 3, 4));
        }
        foreach (var m in Tl.Markers)
        {
            double x = X(m);
            if (x < Header - 6 || x > w + 6) continue;
            dc.DrawLine(MarkerPen, new Point(x, Ruler), new Point(x, h));
            var flag = new StreamGeometry();
            using (var ctx = flag.Open())
            {
                ctx.BeginFigure(new Point(x - 5, Ruler - 12), true, true);
                ctx.LineTo(new Point(x + 5, Ruler - 12), false, false);
                ctx.LineTo(new Point(x + 5, Ruler - 5), false, false);
                ctx.LineTo(new Point(x, Ruler), false, false);
                ctx.LineTo(new Point(x - 5, Ruler - 5), false, false);
            }
            flag.Freeze();
            dc.DrawGeometry(MarkerBrush, null, flag);
        }
        if (Tl.MarkOut > Tl.MarkIn + EditTimeline.Eps)
        {
            dc.DrawRectangle(Outside, null, new Rect(Header, Ruler, Math.Max(0, X(Tl.MarkIn) - Header), h - Ruler));
            dc.DrawRectangle(Outside, null, new Rect(X(Tl.MarkOut), Ruler, Math.Max(0, w - X(Tl.MarkOut)), h - Ruler));
            dc.DrawRectangle(PlayheadBrush, null, new Rect(X(Tl.MarkIn), Ruler - 4, Math.Max(0, (Tl.MarkOut - Tl.MarkIn) * _pps), 4));
        }
        dc.DrawLine(LinePen, new Point(Header, Ruler), new Point(w, Ruler));

        dc.Pop();
    }

    private void DrawClip(DrawingContext dc, TClip c, Dictionary<int, (int Track, double Y, double H)> rowOf, double w, bool lifted)
    {
        if (!rowOf.TryGetValue(c.Track, out var row)) return;
        double x0 = X(c.Start), x1 = X(c.End);
        if (x1 < Header || x0 > w) return;
        var tr = Tl!.Tracks[c.Track];
        bool audio = tr.Type == TrackType.Audio;
        var rect = new Rect(x0, row.Y + 3, Math.Max(2, x1 - x0), row.H - 6);
        var fill = !tr.Enabled ? Disabled : audio ? AudioClip : c.IsImage ? ImageClip : VideoClip;
        dc.PushOpacity(tr.Enabled ? (lifted ? 0.92 : 1) : 0.55);
        dc.DrawRoundedRectangle(fill, null, rect, 6, 6);

        if (audio && rect.Width > 6) DrawWave(dc, c, rect, w);
        if (!audio && rect.Width > 6) DrawFilm(dc, c, rect, w);
        dc.DrawRoundedRectangle(null, Selected.Contains(c.Id) ? SelPen : ClipEdge, rect, 6, 6);
        if (audio && rect.Width > 20)
        {
            // The clip's own volume, as a line to drag: the middle is 100%, the top 200%.
            double vy = VolumeY(c, rect);
            dc.DrawLine(VolumePen, new Point(Math.Max(rect.Left, Header) + 2, vy), new Point(Math.Min(rect.Right, w) - 2, vy));
            if (_drag == Drag.ClipVolume && _grab == c)
            {
                var t = Text($"{Math.Round(c.Volume * 100)}%", 10.5, Ink);
                dc.DrawText(t, new Point(Math.Max(rect.Left, Header) + 6, Math.Max(rect.Top + 2, vy - t.Height - 1)));
            }
        }

        // Fades: shaded corners, with a handle at each top corner.
        if (c.FadeIn > 0)
            dc.DrawGeometry(FadeShade, null, Tri(rect.Left, rect.Top, rect.Left + c.FadeIn * _pps, rect.Bottom, true));
        if (c.FadeOut > 0)
            dc.DrawGeometry(FadeShade, null, Tri(rect.Right, rect.Top, rect.Right - c.FadeOut * _pps, rect.Bottom, false));
        if (rect.Width > 30)
        {
            dc.DrawRectangle(Brushes.White, HandlePen, new Rect(rect.Left + c.FadeIn * _pps - 3, rect.Top, 6, 6));
            dc.DrawRectangle(Brushes.White, HandlePen, new Rect(rect.Right - c.FadeOut * _pps - 3, rect.Top, 6, 6));
        }
        if (rect.Width > 40)
        {
            // A small mark for what it is, then its name (and anything changed about it).
            string tag = c.IsText ? "\uE8D2" : c.IsImage ? "" : audio ? "" : "";
            double lx = Math.Max(rect.Left, Header) + 6;
            if (tag.Length > 0)
            {
                var g = Text(tag, 10.5, Ink, Icons);
                dc.DrawText(g, new Point(lx, rect.Top + 5));
                lx += g.Width + 5;
            }
            string extra = (Math.Abs(c.Speed - 1) > 0.001 ? $"  ·  {c.Speed:0.##}×" : "") +
                           (c.Placed && !audio ? "  ·  placed" : "") + (Math.Abs(c.Volume - 1) > 0.005 ? $"  ·  {Math.Round(c.Volume * 100)}%" : "");
            string name = c.IsText && c.Text != null ? c.Text.Text.Replace('\r', ' ').Replace('\n', ' ') : c.Label;
            var label = Text(name + extra, 11.5, Ink);
            label.MaxTextWidth = Math.Max(10, rect.Right - lx - 6);
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(lx, rect.Top + 4));
        }
        dc.Pop();
    }

    /// <summary>Where a sound clip's volume line sits.</summary>
    private static double VolumeY(TClip c, Rect rect)
    {
        double top = rect.Top + 4, bottom = rect.Bottom - 4;
        return bottom - Math.Clamp(c.Volume / 2, 0, 1) * (bottom - top);
    }

    /// <summary>A video clip's pictures along it (its filmstrip, once made), or a picture's own picture.</summary>
    private void DrawFilm(DrawingContext dc, TClip c, Rect rect, double w)
    {
        double a = Math.Max(rect.Left, Header), b = Math.Min(rect.Right, w);
        if (b - a < 4) return;
        dc.PushClip(new RectangleGeometry(rect, 6, 6));
        dc.PushOpacity(0.85);
        if (c.IsImage)
        {
            if (Pictures.Thumb(c.File) is { } pic && pic.PixelHeight > 0)
            {
                double tw = Math.Max(8, rect.Height * pic.PixelWidth / pic.PixelHeight);
                for (double x = rect.Left + Math.Floor((a - rect.Left) / tw) * tw; x < b; x += tw)
                    dc.DrawImage(pic, new Rect(x, rect.Top, tw, rect.Height));
            }
        }
        else if (Filmstrips.For(c.File, InfoOf?.Invoke(c.File)) is { Frames.Length: > 0 } film)
        {
            double tw = Math.Max(8, rect.Height * film.Width / film.Height);
            for (double x = rect.Left + Math.Floor((a - rect.Left) / tw) * tw; x < b; x += tw)
                if (film.At(c.SrcAt(T(x + tw / 2))) is { } frame)
                    dc.DrawImage(frame, new Rect(x, rect.Top, tw, rect.Height));
        }
        dc.Pop();
        // A shade along the top, so the name reads over any picture.
        dc.DrawRectangle(LabelBand, null, new Rect(rect.Left, rect.Top, rect.Width, Math.Min(22, rect.Height)));
        dc.Pop();
    }

    /// <summary>A sound clip's waveform (once its sound is decoded), louder or quieter with its volume and fades.</summary>
    private void DrawWave(DrawingContext dc, TClip c, Rect rect, double w)
    {
        var peaks = Waveforms.For(c.File, Math.Max(0, c.Stream));
        if (peaks == null) return;
        int level = peaks.LevelFor(_pps);
        double top = rect.Top + 18, bottom = rect.Bottom - 3;
        if (bottom - top < 6) { top = rect.Top + 3; }
        double mid = (top + bottom) / 2, half = (bottom - top) / 2;
        double a = Math.Max(rect.Left + 1, Header), b = Math.Min(rect.Right - 1, w);
        if (b - a < 2) return;
        double gain = c.Volume * (Tl!.Tracks[c.Track].Volume);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            int cols = (int)Math.Ceiling((b - a) / 2);   // a point every 2 pixels
            var ys = new double[cols + 1];
            for (int i = 0; i <= cols; i++)
            {
                double x = Math.Min(b, a + i * 2);
                double t0 = T(x - 1), t1 = T(x + 1);
                double s0 = c.SrcAt(t0), s1 = c.SrcAt(t1);
                double v = peaks.Max(Math.Max(0, s0), Math.Max(0, s1), level);
                double fade = EditTimeline.FadeAt(c, Math.Clamp(T(x), c.Start, c.End));
                // Square root: quiet sound still shows (as ears hear it).
                ys[i] = Math.Min(1, Math.Sqrt(Math.Min(1, v * gain)) * fade) * half;
            }
            ctx.BeginFigure(new Point(a, mid - ys[0]), true, true);
            for (int i = 1; i <= cols; i++) ctx.LineTo(new Point(Math.Min(b, a + i * 2), mid - ys[i]), false, false);
            for (int i = cols; i >= 0; i--) ctx.LineTo(new Point(Math.Min(b, a + i * 2), mid + Math.Max(0.5, ys[i])), false, false);
        }
        g.Freeze();
        dc.DrawGeometry(Wave, null, g);
    }

    private void Glyph(DrawingContext dc, string glyph, Rect box, Brush brush, double size)
    {
        var t = Text(glyph, size, brush, Icons);
        dc.DrawText(t, new Point(box.X + (box.Width - t.Width) / 2, box.Y + (box.Height - t.Height) / 2));
    }

    private static Geometry Tri(double xCorner, double top, double xAlong, double bottom, bool leftSide, bool pointDown = false)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (pointDown)
            {
                ctx.BeginFigure(new Point(xCorner, top), true, true);
                ctx.LineTo(new Point(xAlong, top), false, false);
                ctx.LineTo(new Point((xCorner + xAlong) / 2, bottom), false, false);
            }
            else
            {
                // The fade's shaded triangle: the corner, along the top, down the edge.
                ctx.BeginFigure(new Point(xCorner, top), true, true);
                ctx.LineTo(new Point(xAlong, top), false, false);
                ctx.LineTo(new Point(xCorner, bottom), false, false);
            }
        }
        g.Freeze();
        return g;
    }

    private double TickStep()
    {
        foreach (var s in new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
            if (s * _pps >= 70) return s;
        return 1200;
    }

    public static string Clock(double t, bool fine = true)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(t) ? t : 0));
        return fine ? $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}" : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    /* ---- snapping ------------------------------------------------------------------ */

    private double SnapTime(double t, IEnumerable<string>? ignore = null)
    {
        if (!Snap || Tl == null) return t;
        var skip = ignore == null ? new HashSet<string>() : new HashSet<string>(ignore);
        var targets = Tl.Clips.Where(c => !skip.Contains(c.Id)).SelectMany(c => new[] { c.Start, c.End })
                         .Append(Playhead).Append(0);
        if (Tl.MarkOut > Tl.MarkIn) targets = targets.Append(Tl.MarkIn).Append(Tl.MarkOut);
        targets = targets.Concat(Tl.Markers);
        double best = t, bestDist = SnapPx / _pps;
        foreach (var s in targets)
            if (Math.Abs(s - t) < bestDist) { best = s; bestDist = Math.Abs(s - t); }
        return best;
    }

    /* ---- mouse ---------------------------------------------------------------------- */

    private enum Drag { None, Scrub, Move, TrimLeft, TrimRight, FadeIn, FadeOut, Volume, TrackMove, ClipVolume }
    private int _volTrack;
    private double _volStartX, _volStart;
    private Drag _drag;
    private TClip? _grab;
    private List<TClip> _moving = new();
    private Dictionary<string, (int Track, double Start)> _orig = new();
    private Dictionary<string, TrackType> _kinds = new();
    private double _grabT, _grabX, _grabY;
    private List<(TClip Clip, int Track, double Start, double In, double Out)>? _snapshot;
    private bool _remembered, _changed;
    private int _trackFrom, _trackTo;
    private TTrack? _madeVideo, _madeAudio;   // tracks made by this drag (gone again if left empty)
    private int _hover = -1;

    private static bool Alt => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

    private List<TClip> Targets(TClip c) => Alt ? new List<TClip> { c } : Tl!.Group(c).ToList();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (Tl == null) return;
        var p = e.GetPosition(this);
        _remembered = _changed = false;
        if (p.X < Header)
        {
            HeaderDown(p, e.ClickCount);
            return;
        }
        double t = Math.Max(0, T(p.X));

        var row = p.Y >= Ruler ? RowAt(p.Y) : null;
        if (row == null)
        {
            // The ruler, the strip above the tracks, the space between or below
            // them: the playhead goes there (drag on to scrub).
            if (p.Y >= Ruler && (Selected.Count > 0 || SelectedGap != null)) ClearSelection();
            StartScrub(t);
            return;
        }
        var (track, rowY, _) = row.Value;
        var clip = Tl.ClipAt(track, t);

        if (Tool == EditTool.Cut)
        {
            if (clip == null) { SetPlayhead(SnapTime(Math.Min(t, Tl.Duration)), true, follow: false); return; }
            if (Tl.Tracks[track].Locked) { Status?.Invoke($"{Tl.Tracks[track].Name} is locked."); return; }
            double at = SnapTime(t);
            Tl.Remember();
            if (Tl.Split(Targets(clip), at)) { Edited?.Invoke(); Redraw(); }
            else Tl.Forget();
            SetPlayhead(at, true, follow: false);
            return;
        }

        if (clip == null)
        {
            // A click on empty track: the playhead goes there (drag on to scrub),
            // and the gap there is selected, if it's between clips.
            Selected.Clear();
            SelectedGap = Tl.GapAt(track, t) is { } g ? (track, g.From, g.To) : null;
            SelectionChanged?.Invoke();
            Redraw();
            StartScrub(t);
            return;
        }

        // Fade handles at the top corners, then the edges, then the body.
        double top = rowY + 3;
        double fadeInX = X(clip.Start + clip.FadeIn), fadeOutX = X(clip.End - clip.FadeOut);
        bool nearTop = p.Y <= top + 10;
        if (nearTop && Math.Abs(p.X - fadeInX) <= 7) _drag = Drag.FadeIn;
        else if (nearTop && Math.Abs(p.X - fadeOutX) <= 7) _drag = Drag.FadeOut;
        else if (Math.Abs(p.X - X(clip.Start)) <= Edge) _drag = Drag.TrimLeft;
        else if (Math.Abs(p.X - X(clip.End)) <= Edge) _drag = Drag.TrimRight;
        else if (Tl.Tracks[track].Type == TrackType.Audio && Math.Abs(p.Y - VolumeY(clip, new Rect(X(clip.Start), rowY + 3, 1, row.Value.H - 6))) <= 3) _drag = Drag.ClipVolume;
        else _drag = Drag.Move;

        _grab = clip;
        SelectedGap = null;
        var group = Targets(clip);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _drag == Drag.Move)
        {
            bool on = !Selected.Contains(clip.Id);
            foreach (var c in group) { if (on) Selected.Add(c.Id); else Selected.Remove(c.Id); }
        }
        else if (!Selected.Contains(clip.Id))
        {
            Selected.Clear();
            foreach (var c in group) Selected.Add(c.Id);
        }
        // Clips on locked tracks stay where they are.
        _moving = (_drag == Drag.Move ? Tl.Clips.Where(c => Selected.Contains(c.Id)) : _drag == Drag.ClipVolume ? new List<TClip> { clip } : group)
                  .Where(c => !Tl.Tracks[c.Track].Locked).ToList();
        _volStart = clip.Volume;
        _orig = _moving.ToDictionary(c => c.Id, c => (c.Track, c.Start));
        _kinds = _moving.ToDictionary(c => c.Id, c => Tl.Tracks[c.Track].Type);
        _madeVideo = _madeAudio = null;
        _grabT = t;
        _grabX = p.X;
        _grabY = p.Y;
        // Trimming the start works from how everything was when the drag began,
        // so footage brought back (and clips pushed along) can be taken back.
        _snapshot = _drag == Drag.TrimLeft ? Tl.Clips.Select(c => (c, c.Track, c.Start, c.In, c.Out)).ToList() : null;
        if (Tl.Tracks[clip.Track].Locked) { Status?.Invoke($"{Tl.Tracks[clip.Track].Name} is locked: unlock it to change its clips."); _drag = Drag.None; }
        SelectionChanged?.Invoke();
        if (_drag != Drag.None) CaptureMouse();
        Redraw();
    }

    private void StartScrub(double t)
    {
        _drag = Drag.Scrub;
        SetPlayhead(SnapTime(Math.Min(t, Tl!.Duration)), true, follow: false);
        CaptureMouse();
    }

    private void HeaderDown(Point p, int clicks)
    {
        if (Tl == null || HeaderHit(p) is not { } hit) return;
        var tr = Tl.Tracks[hit.Track];
        switch (hit.Part)
        {
            case Part.Toggle:
                ChangeTrack(() =>
                {
                    tr.Enabled = !tr.Enabled;
                    // All PC audio already holds every app: never both at once.
                    if (tr.Enabled && tr.Type == TrackType.Audio)
                    {
                        bool pc = EditTimeline.IsAllPcName(tr.Source), app = EditTimeline.IsAppName(tr.Source);
                        foreach (var other in Tl.Tracks.Where(o => o != tr && o.Type == TrackType.Audio))
                            if ((pc && EditTimeline.IsAppName(other.Source)) || (app && EditTimeline.IsAllPcName(other.Source)))
                                other.Enabled = false;
                    }
                });
                break;
            case Part.Solo: ChangeTrack(() => tr.Solo = !tr.Solo); break;
            case Part.Lock:
                ChangeTrack(() => tr.Locked = !tr.Locked);
                if (tr.Locked) { Selected.RemoveWhere(id => Tl.Clips.Any(c => c.Id == id && c.Track == hit.Track)); SelectionChanged?.Invoke(); }
                break;
            case Part.Delete: DeleteTrack(hit.Track); break;
            case Part.Volume:
                _drag = Drag.Volume;
                _volTrack = hit.Track;
                _volStartX = p.X;
                _volStart = tr.Volume;
                CaptureMouse();
                break;
            case Part.Name:
                if (clicks >= 2) { RenameRequested?.Invoke(hit.Track); return; }
                // Dragged up or down: the track moves among its kind.
                _drag = Drag.TrackMove;
                _trackFrom = _trackTo = hit.Track;
                _grabY = p.Y;
                CaptureMouse();
                break;
        }
    }

    /// <summary>A track switch: undoable, heard and seen at once.</summary>
    private void ChangeTrack(Action change)
    {
        if (Tl == null) return;
        Tl.Remember();
        change();
        Edited?.Invoke();
        Redraw();
    }

    public void DeleteTrack(int track)
    {
        if (Tl == null || track < 0 || track >= Tl.Tracks.Count) return;
        var tr = Tl.Tracks[track];
        if (tr.Locked) { Status?.Invoke($"{tr.Name} is locked: unlock it to delete it."); return; }
        int clips = Tl.Clips.Count(c => c.Track == track);
        string what = tr.Shown.Length > 0 ? $"{tr.Name} ({tr.Shown})" : tr.Name;
        Tl.Remember();
        Tl.DeleteTrack(track);
        Selected.RemoveWhere(id => Tl.Clips.All(c => c.Id != id));
        SelectedGap = null;
        _hover = -1;
        SelectionChanged?.Invoke();
        Edited?.Invoke();
        InvalidateMeasure();
        Redraw();
        Status?.Invoke($"Deleted {what}{(clips > 0 ? $" and its {clips} clip{(clips == 1 ? "" : "s")}" : "")}.  Ctrl+Z brings it back.");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Tl == null) return;
        var p = e.GetPosition(this);
        _lastMouse = p;
        if (_drag == Drag.None)
        {
            int hover = p.X < Header && RowAt(p.Y) is { } hr ? hr.Track : -1;
            if (hover != _hover) { _hover = hover; Redraw(); }
            Cursor = CursorAt(p, Math.Max(0, T(p.X)));
            return;
        }
        // The button was let go somewhere this never heard about: the drag is over.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();   // ends it (lost capture)
            EndDrag();                                    // or, if it wasn't captured, here
            return;
        }
        DragTo(p);
        if (_drag is not (Drag.Volume or Drag.TrackMove) && (p.X > ActualWidth - 16 || p.X < Header) && !_edgeTimer.IsEnabled) _edgeTimer.Start();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != -1 && _drag == Drag.None) { _hover = -1; Redraw(); }
    }

    /// <summary>Carries on a drag with the mouse at <paramref name="p"/> (held to what's on screen).</summary>
    private void DragTo(Point p)
    {
        if (Tl == null) return;
        // Past an edge, the drag acts at the edge; the view scrolls to bring more in.
        double t = Math.Max(0, T(Math.Clamp(p.X, Header, Math.Max(Header, ActualWidth - 16))));
        if (_drag == Drag.Scrub) { SetPlayhead(SnapTime(Math.Min(t, Tl.Duration)), true, follow: false); return; }
        if (_drag == Drag.Volume)
        {
            double dx = p.X - _volStartX;
            if (!_changed && Math.Abs(dx) < 3) return;
            if (!_remembered) { Tl.Remember(); _remembered = true; }
            // Half a percent a pixel (a tenth with Shift, for fine steps).
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.001 : 0.005;
            Tl.Tracks[_volTrack].Volume = Math.Round(Math.Clamp(_volStart + dx * step, 0, 2), 2);
            _changed = true;
            Redraw();
            VolumeChanging?.Invoke(_volTrack);
            return;
        }
        if (_drag == Drag.TrackMove)
        {
            if (!_changed && Math.Abs(p.Y - _grabY) < 5) return;
            _changed = true;
            var kind = Tl.Tracks[_trackFrom].Type;
            var same = Rows().Where(r => Tl.Tracks[r.Track].Type == kind).ToList();
            if (same.Count == 0) return;
            var hit = same.FirstOrDefault(r => p.Y >= r.Y && p.Y < r.Y + r.H);
            _trackTo = hit.H > 0 ? hit.Track : p.Y < same[0].Y ? same[0].Track : same[^1].Track;
            InvalidateVisual();
            return;
        }
        if (_grab == null) return;
        // A click that wobbles a pixel or two isn't a drag.
        if (!_changed && Math.Abs(p.X - _grabX) < 4 && Math.Abs(p.Y - _grabY) < 4) return;

        if (!_remembered) { Tl.Remember(); _remembered = true; }
        var ids = _moving.Select(c => c.Id).ToList();
        switch (_drag)
        {
            case Drag.Move:
            {
                if (!_orig.ContainsKey(_grab.Id)) break;   // the grabbed clip is on a locked track
                double dt = t - _grabT;
                // Snap whichever edge of the grabbed clip is closest to something.
                var (_, gStart) = _orig[_grab.Id];
                double s = gStart + dt, e2 = s + _grab.Length;
                double ss = SnapTime(s, ids), se = SnapTime(e2, ids);
                if (Math.Abs(ss - s) > EditTimeline.Eps && (Math.Abs(ss - s) <= Math.Abs(se - e2) || Math.Abs(se - e2) < EditTimeline.Eps)) dt += ss - s;
                else if (Math.Abs(se - e2) > EditTimeline.Eps) dt += se - e2;

                // Up or down: onto another track of the same kind, or a new one
                // (the strip above the pictures, or below the last sound track).
                var type = _kinds[_grab.Id];
                int target = -1;
                if (type == TrackType.Video && p.Y >= Ruler && p.Y < Ruler + NewStrip) target = EnsureNewVideoTrack();
                else if (type == TrackType.Audio && p.Y >= RowsBottom() && p.Y < RowsBottom() + BelowRows + 200) target = EnsureNewAudioTrack();
                else if (RowAt(p.Y) is { } r && Tl.Tracks[r.Track].Type == type) target = r.Track;
                int trackDelta = target >= 0 ? target - _orig[_grab.Id].Track : 0;
                // With the mouse off any track of its kind, it keeps to the track it's on.
                if (target < 0) trackDelta = _grab.Track - _orig[_grab.Id].Track;
                var placed = _moving.Select(c =>
                {
                    var (ot, os) = _orig[c.Id];
                    int nt = _kinds[c.Id] == type ? ot + trackDelta : ot;
                    return (c, nt, Math.Max(0, os + dt));
                }).ToList();
                if (Tl.CanPlace(placed, _kinds))
                    foreach (var (moved, nt, ns) in placed) { moved.Track = nt; moved.Start = ns; }
                break;
            }
            case Drag.TrimLeft:
            {
                if (_snapshot == null) { Tl.Trim(_moving, true, SnapTime(t, ids)); break; }
                // Back to how it was, then trimmed to where the mouse is now.
                foreach (var (c, tr, st, i, o) in _snapshot) { c.Track = tr; c.Start = st; c.In = i; c.Out = o; }
                if (!Tl.Clips.Contains(_grab)) break;
                // Measured from where the drag began, so moving past 0:00 (or past
                // the clip before) still counts.
                double want = _orig[_grab.Id].Start + (p.X - _grabX) / _pps;
                if (want >= 0) want = SnapTime(want, ids);
                double limit = _grab.Start - Tl.RoomBefore(_grab);
                if (want >= limit - EditTimeline.Eps) Tl.Trim(_moving, true, want);
                else
                {
                    // Out of room on the left: the start comes back where it is,
                    // and everything after moves right to make room.
                    Tl.Trim(_moving, true, limit);
                    Tl.ExtendStart(_moving, limit - want);
                }
                break;
            }
            case Drag.TrimRight:
                Tl.Trim(_moving, false, SnapTime(t, ids));
                break;
            case Drag.FadeIn:
                Tl.SetFade(_moving, true, t - _grab.Start);
                break;
            case Drag.FadeOut:
                Tl.SetFade(_moving, false, _grab.End - t);
                break;
            case Drag.ClipVolume:
            {
                if (RowAt(_grabY) is not { } vr) break;
                double span = vr.H - 6 - 8;
                // Up is louder: the whole height is 0-200% (Shift: fine steps).
                double dv = -(p.Y - _grabY) / Math.Max(1, span) * 2 * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.2 : 1);
                foreach (var c in _moving) c.Volume = Math.Round(Math.Clamp(_volStart + dv, 0, 2), 2);
                ClipVolumeChanging?.Invoke();
                break;
            }
        }
        _changed = true;
        Redraw();
    }

    /// <summary>A video track on top, made once per drag (its index is 0; everything else moves down one).</summary>
    private int EnsureNewVideoTrack()
    {
        if (_madeVideo != null) return Tl!.Tracks.IndexOf(_madeVideo);
        Tl!.AddVideoTrack();
        _madeVideo = Tl.Tracks[0];
        Tl.Renumber();
        foreach (var k in _orig.Keys.ToList()) _orig[k] = (_orig[k].Track + 1, _orig[k].Start);
        if (_snapshot != null) _snapshot = _snapshot.Select(s => (s.Clip, s.Track + 1, s.Start, s.In, s.Out)).ToList();
        InvalidateMeasure();
        return 0;
    }

    private int EnsureNewAudioTrack()
    {
        if (_madeAudio != null) return Tl!.Tracks.IndexOf(_madeAudio);
        int i = Tl!.AddAudioTrack("");
        _madeAudio = Tl.Tracks[i];
        InvalidateMeasure();
        return i;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;   // before letting go of the mouse (letting go is "lost capture", which ends drags too)
        _edgeTimer.Stop();
        ReleaseMouseCapture();
        if (drag == Drag.Volume)
        {
            if (_changed) VolumeCommitted?.Invoke(_volTrack);
            else VolumeClicked?.Invoke(_volTrack);
            return;
        }
        if (drag == Drag.TrackMove)
        {
            if (_changed && _trackTo != _trackFrom && Tl != null)
            {
                Tl.Remember();
                Tl.MoveTrack(_trackFrom, _trackTo);
                Edited?.Invoke();
            }
            Redraw();
            return;
        }
        bool edited = _changed && drag != Drag.Scrub;
        if (edited && drag == Drag.Move && Tl != null)
        {
            // Landed: whatever it's over is covered there.
            Tl.Cover(_moving);
            DropUnusedTracks();
        }
        else if (Tl != null && (_madeVideo != null || _madeAudio != null)) DropUnusedTracks();
        // A click on a clip (no drag): it's selected, and the playhead goes there too.
        if (!_changed && drag == Drag.Move && Tl != null)
            SetPlayhead(SnapTime(Math.Min(Math.Max(0, T(e.GetPosition(this).X)), Tl.Duration)), true, follow: false);
        _grab = null;
        _snapshot = null;
        if (edited) Edited?.Invoke();
        InvalidateMeasure();
        Redraw();
    }

    /// <summary>Tracks a drag made but left empty go again.</summary>
    private void DropUnusedTracks()
    {
        if (Tl == null) return;
        foreach (var made in new[] { _madeVideo, _madeAudio })
        {
            if (made == null) continue;
            int i = Tl.Tracks.IndexOf(made);
            if (i >= 0 && Tl.Clips.All(c => c.Track != i)) Tl.DeleteTrack(i);
        }
        _madeVideo = _madeAudio = null;
        Tl.Renumber();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        // Captured elsewhere mid-drag (a window popping up): the drag ends where it is.
        EndDrag();
        base.OnLostMouseCapture(e);
    }

    /// <summary>
    /// A drag that didn't end with the button being let go here ends where it
    /// is: what it changed is kept. Every kind of drag ends, so nothing carries
    /// on following the mouse afterwards (a click on a clip, then a window
    /// popping up, left the clip moving with the mouse with no button held).
    /// </summary>
    private void EndDrag()
    {
        if (_drag == Drag.None) return;
        var was = _drag;
        _drag = Drag.None;
        _edgeTimer.Stop();
        bool edited = _changed && Tl != null && was is not (Drag.Scrub or Drag.TrackMove or Drag.Volume);
        if (Tl != null)
        {
            if (edited && was == Drag.Move) { Tl.Cover(_moving); DropUnusedTracks(); }
            else if (_madeVideo != null || _madeAudio != null) DropUnusedTracks();
        }
        _grab = null;
        _snapshot = null;
        if (was == Drag.Volume && _changed) VolumeCommitted?.Invoke(_volTrack);
        if (edited) Edited?.Invoke();
        InvalidateMeasure();
        Redraw();
    }

    private Cursor CursorAt(Point p, double t)
    {
        if (p.X < Header)
            return HeaderHit(p) is { } h ? h.Part switch
            {
                Part.Volume => Cursors.SizeWE,
                Part.Name => Cursors.SizeNS,
                _ => Cursors.Hand,
            } : Cursors.Arrow;
        if (p.Y < Ruler) return Cursors.Hand;
        if (RowAt(p.Y) is not { } row || Tl!.ClipAt(row.Track, t) is not { } c) return Cursors.Hand;
        if (Tl.Tracks[row.Track].Locked) return Cursors.No;
        if (Tool == EditTool.Cut) return Cursors.IBeam;
        if (Tl.Tracks[row.Track].Type == TrackType.Audio && Math.Abs(p.Y - VolumeY(c, new Rect(0, row.Y + 3, 1, row.H - 6))) <= 3 &&
            Math.Abs(p.X - X(c.Start)) > Edge && Math.Abs(p.X - X(c.End)) > Edge) return Cursors.SizeNS;
        bool nearTop = p.Y <= row.Y + 13;
        if (nearTop && (Math.Abs(p.X - X(c.Start + c.FadeIn)) <= 7 || Math.Abs(p.X - X(c.End - c.FadeOut)) <= 7)) return Cursors.Cross;
        if (Math.Abs(p.X - X(c.Start)) <= Edge || Math.Abs(p.X - X(c.End)) <= Edge) return Cursors.SizeWE;
        return Cursors.SizeAll;
    }

    /// <summary>Ctrl/Alt + scroll zooms around the mouse; scrolling alone moves along; Shift+scroll scrolls up and down.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Shift)) return;   // the scroll bar beside the timeline takes it
        if (m.HasFlag(ModifierKeys.Control) || m.HasFlag(ModifierKeys.Alt))
        {
            double x = e.GetPosition(this).X;
            double at = T(Math.Max(Header, x));
            _pps = Math.Clamp(_pps * (e.Delta > 0 ? 1.25 : 0.8), FitPps(), 2000);
            _scroll = at - (Math.Max(Header, x) - Header) / _pps;
        }
        else
            _scroll -= e.Delta / 120.0 * 60 / _pps;
        ClampView();
        e.Handled = true;
        Redraw();
    }

    /* ---- dropping files and media ------------------------------------------------ */

    private (int Track, double Time)? _dropAt;

    private static bool Droppable(IDataObject d) =>
        d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(MediaBin.Format);

    private (int Track, double Time) DropPoint(Point p)
    {
        double t = Math.Max(0, SnapTime(T(Math.Max(Header, p.X))));
        if (p.Y >= Ruler && p.Y < Ruler + NewStrip) return (-1, t);
        return (RowAt(p.Y)?.Track ?? -2, t);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        if (Tl == null || !Droppable(e.Data)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Copy;
        _dropAt = DropPoint(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        _dropAt = null;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        _dropAt = null;
        InvalidateVisual();
        if (Tl == null || !Droppable(e.Data)) return;
        var (track, time) = DropPoint(e.GetPosition(this));
        e.Handled = true;
        Dropped?.Invoke(e.Data, track, time);
    }

    /* ---- right-click menus --------------------------------------------------------- */

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (Tl == null) return;
        var p = e.GetPosition(this);
        var menu = new ContextMenu();
        void Item(string text, string keys, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, InputGestureText = keys, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        void Sep() { if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator()); }

        double t = Math.Max(0, T(p.X));
        if (p.X < Header)
        {
            if (RowAt(p.Y) is not { } hr) { TrackAdders(Item); Sep(); Item("Clean up tracks…", "", () => CleanUpRequested?.Invoke()); }
            else
            {
                int i = hr.Track;
                var tr = Tl.Tracks[i];
                bool audio = tr.Type == TrackType.Audio;
                Item("Rename…", "", () => RenameRequested?.Invoke(i));
                Item(audio ? (tr.Enabled ? "Mute" : "Unmute") : (tr.Enabled ? "Hide" : "Show"), "", () => ChangeTrack(() => tr.Enabled = !tr.Enabled));
                if (audio) Item(tr.Solo ? "Stop solo" : "Solo", "", () => ChangeTrack(() => tr.Solo = !tr.Solo));
                Item(tr.Locked ? "Unlock" : "Lock", "", () => ChangeTrack(() => tr.Locked = !tr.Locked));
                Sep();
                bool upOk = i > 0 && Tl.Tracks[i - 1].Type == tr.Type, downOk = i < Tl.Tracks.Count - 1 && Tl.Tracks[i + 1].Type == tr.Type;
                Item("Move up", "", () => ChangeTrack(() => Tl.MoveTrack(i, i - 1)), upOk);
                Item("Move down", "", () => ChangeTrack(() => Tl.MoveTrack(i, i + 1)), downOk);
                Item("Select everything on it", "", () => SelectTrack(i));
                Sep();
                TrackAdders(Item);
                Item("Clean up tracks…", "", () => CleanUpRequested?.Invoke());
                Sep();
                Item("Delete track", "", () => DeleteTrack(i));
            }
        }
        else if (p.Y < Ruler)
        {
            Item("Mark in here", "I", () => SetMarkAt(true, t));
            Item("Mark out here", "O", () => SetMarkAt(false, t));
            Item("Clear marks", "Esc", () => { if (Tl.MarkOut > Tl.MarkIn) ChangeTrack(() => Tl.MarkIn = Tl.MarkOut = 0); }, Tl.MarkOut > Tl.MarkIn);
            Sep();
            Item("Add a marker here", "M", () => ChangeTrack(() => { if (!Tl.Markers.Any(m => Math.Abs(m - t) < 0.01)) { Tl.Markers.Add(t); Tl.Markers.Sort(); } }));
            Item("Remove every marker", "", () => ChangeTrack(() => Tl.Markers.Clear()), Tl.Markers.Count > 0);
        }
        else if (RowAt(p.Y) is { } row && Tl.ClipAt(row.Track, t) is { } clip)
        {
            if (!Selected.Contains(clip.Id))
            {
                Selected.Clear();
                foreach (var c in Targets(clip)) Selected.Add(c.Id);
                SelectedGap = null;
                SelectionChanged?.Invoke();
                Redraw();
            }
            bool video = Tl.Tracks[clip.Track].Type == TrackType.Video;
            Item("Split at the playhead", "Ctrl+B", () => SplitAtPlayhead(), clip.Start < Playhead && Playhead < clip.End);
            Item("Duplicate", "Ctrl+D", () => DuplicateSelection());
            Sep();
            if (video && !clip.IsImage)
            {
                bool under = clip.Start <= Playhead && Playhead < clip.End;
                Item("Punch in here (zoom)", "", () => PunchIn(clip), under);
                Item("Freeze frame here", "", () => FreezeRequested?.Invoke(clip), under);
            }
            if (video && Tl.Clips.Any(o => o != clip && o.Track == clip.Track && Math.Abs(o.End - clip.Start) < 0.02))
                Item("Crossfade from the clip before", "", () => CrossfadeInto(clip));
            Sep();
            if (video && clip.Placed)
                Item("Fill the frame again", "", () => Apply(cs => { foreach (var c in cs.Where(c => Tl.Tracks[c.Track].Type == TrackType.Video)) c.ResetPlacement(); }));
            Item("Unlink picture and sound", "", () => Apply(cs => Tl.Unlink(cs)), clip.Link != null);
            Item("Link selected", "", () => Apply(cs => Tl.LinkTogether(cs)), Selected.Count > 1);
            Item("Group", "Ctrl+G", () => GroupSelection(true), Selected.Count > 1);
            Item("Ungroup", "Ctrl+Shift+G", () => GroupSelection(false), clip.GroupId != null);
            Sep();
            Item("Delete", "Delete", () => DeleteSelection(false));
            Item("Delete and close the gap", "Shift+Delete", () => DeleteSelection(true));
        }
        else
        {
            var r = RowAt(p.Y);
            int track = r?.Track ?? -2;
            double at = SnapTime(Math.Min(t, Tl.Duration));
            Item("Move the playhead here", "", () => SetPlayhead(at, true, follow: false));
            Item("Paste here", "Ctrl+V", () => PasteRequested?.Invoke(track, at));
            Item("Add a file here…", "", () => ImportRequested?.Invoke(track, at));
            Item("Add text here", "T", () => TextRequested?.Invoke(track, at));
            if (r is { } rr && Tl.GapAt(rr.Track, t) is { } gap)
            {
                Sep();
                Item("Close this gap", "", () =>
                {
                    Tl.Remember();
                    if (Tl.CloseGap(rr.Track, gap.From, gap.To)) { Edited?.Invoke(); Redraw(); }
                    else { Tl.Forget(); Status?.Invoke("That gap can't close: linked clips on other tracks are in the way."); }
                });
            }
            Sep();
            TrackAdders(Item);
            if (r is { } r2) { Sep(); Item("Delete this track", "", () => DeleteTrack(r2.Track)); }
        }
        if (menu.Items.Count == 0) return;
        if (menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void TrackAdders(Action<string, string, Action, bool> item)
    {
        item("Add a video track", "", () => ChangeTrack(() => { Tl!.AddVideoTrack(); Tl.Renumber(); InvalidateMeasure(); }), true);
        item("Add a sound track", "", () => ChangeTrack(() => { Tl!.AddAudioTrack(""); Tl.Renumber(); InvalidateMeasure(); }), true);
    }

    private void SetMarkAt(bool isIn, double t)
    {
        if (Tl == null) return;
        t = Math.Clamp(t, 0, Tl.Duration);
        ChangeTrack(() =>
        {
            if (isIn) { Tl.MarkIn = t; if (Tl.MarkOut <= t + EditTimeline.Eps) Tl.MarkOut = Tl.Duration; }
            else { Tl.MarkOut = t; if (Tl.MarkIn >= t - EditTimeline.Eps) Tl.MarkIn = 0; }
        });
    }

    private void SelectTrack(int track)
    {
        if (Tl == null) return;
        Selected.Clear();
        foreach (var c in Tl.Clips.Where(c => c.Track == track)) Selected.Add(c.Id);
        SelectedGap = null;
        SelectionChanged?.Invoke();
        Redraw();
    }

    private void Apply(Action<List<TClip>> change)
    {
        if (Tl == null) return;
        var clips = Tl.Clips.Where(c => Selected.Contains(c.Id)).ToList();
        if (clips.Count == 0) return;
        Tl.Remember();
        change(clips);
        Edited?.Invoke();
        Redraw();
    }

    /// <summary>Ctrl+G groups the selected clips (they move, cut and delete together); Ctrl+Shift+G ungroups.</summary>
    public void GroupSelection(bool group) => Apply(cs => { if (group) Tl!.GroupTogether(cs); else Tl!.Ungroup(cs); });

    /* ---- commands from the window ------------------------------------------------ */

    public void ClearSelection()
    {
        Selected.Clear();
        SelectedGap = null;
        SelectionChanged?.Invoke();
        Redraw();
    }

    /// <summary>Selects these clips (from a click on the picture).</summary>
    public void Select(IEnumerable<TClip> clips)
    {
        Selected.Clear();
        foreach (var c in clips) Selected.Add(c.Id);
        SelectedGap = null;
        SelectionChanged?.Invoke();
        Redraw();
    }

    /// <summary>Delete: the selected clips (leaving a gap, or closing it with ripple), or a selected gap.</summary>
    public bool DeleteSelection(bool ripple)
    {
        if (Tl == null) return false;
        if (SelectedGap is { } g)
        {
            Tl.Remember();
            bool ok = Tl.CloseGap(g.Track, g.From, g.To);
            SelectedGap = null;
            if (ok) Edited?.Invoke(); else { Tl.Forget(); Status?.Invoke("That gap can't close: linked clips on other tracks are in the way."); }
            Redraw();
            return ok;
        }
        var clips = Tl.Clips.Where(c => Selected.Contains(c.Id)).ToList();
        if (clips.Count == 0) return false;
        Tl.Remember();
        Tl.Delete(clips, ripple);
        // Nothing left before what remains: it all slides back to 0:00.
        double moved = Tl.CloseLeadingGap();
        if (moved > 0) SetPlayhead(Math.Max(0, Playhead - moved), true);
        Selected.Clear();
        SelectionChanged?.Invoke();
        Edited?.Invoke();
        InvalidateMeasure();
        Redraw();
        return true;
    }

    /// <summary>
    /// Ctrl+D: a copy of the selected clips straight after them (on the same
    /// tracks, linked among themselves as the originals are), covering
    /// whatever is there. The copies become the selection.
    /// </summary>
    public bool DuplicateSelection()
    {
        if (Tl == null) return false;
        var clips = Tl.Clips.Where(c => Selected.Contains(c.Id) && !Tl.Tracks[c.Track].Locked).ToList();
        if (clips.Count == 0) return false;
        double from = clips.Min(c => c.Start), to = clips.Max(c => c.End), shift = to - from;
        Tl.Remember();
        var links = new Dictionary<string, string>();
        var groups = new Dictionary<string, string>();
        var copies = new List<TClip>();
        foreach (var c in clips)
        {
            var d = c.Copy();
            d.Id = Guid.NewGuid().ToString("N");
            d.Start += shift;
            if (c.Link != null) { if (!links.TryGetValue(c.Link, out var l)) links[c.Link] = l = Guid.NewGuid().ToString("N"); d.Link = l; }
            if (c.GroupId != null) { if (!groups.TryGetValue(c.GroupId, out var gid)) groups[c.GroupId] = gid = Guid.NewGuid().ToString("N"); d.GroupId = gid; }
            copies.Add(d);
        }
        Tl.Clips.AddRange(copies);
        Tl.Cover(copies);
        Select(copies);
        Edited?.Invoke();
        return true;
    }

    /// <summary>The selected clips, or null when nothing is selected (then quick cuts work on everything).</summary>
    private List<TClip>? Chosen()
    {
        var list = Tl?.Clips.Where(c => Selected.Contains(c.Id)).ToList();
        return list is { Count: > 0 } ? list : null;
    }

    /// <summary>Ctrl+B: split at the playhead (the selected clips, or everything under it).</summary>
    public bool SplitAtPlayhead()
    {
        if (Tl == null) return false;
        Tl.Remember();
        if (!Tl.SplitAt(Chosen(), Playhead)) { Tl.Forget(); return false; }
        Edited?.Invoke();
        Redraw();
        return true;
    }

    /// <summary>Q: everything before the playhead goes, and what's left slides back.</summary>
    public bool CutLeftOfPlayhead()
    {
        if (Tl == null) return false;
        Tl.Remember();
        var at = Tl.CutLeft(Chosen(), Playhead);
        if (at == null) { Tl.Forget(); return false; }
        Edited?.Invoke();
        InvalidateMeasure();
        SetPlayhead(at.Value, true);
        Redraw();
        return true;
    }

    /// <summary>E: everything after the playhead goes.</summary>
    public bool CutRightOfPlayhead()
    {
        if (Tl == null) return false;
        Tl.Remember();
        if (!Tl.CutRight(Chosen(), Playhead)) { Tl.Forget(); return false; }
        Edited?.Invoke();
        InvalidateMeasure();
        SetPlayhead(Playhead, true);
        Redraw();
        return true;
    }

    /// <summary>
    /// A punch-in: the clip (with its sound) is cut at the playhead and two
    /// seconds later, and the middle is shown 35% closer. Drag it on the
    /// preview to zoom in on a spot.
    /// </summary>
    public void PunchIn(TClip clip)
    {
        if (Tl == null || Tl.Tracks[clip.Track].Locked) return;
        double a = Playhead, b = Math.Min(clip.End, Playhead + 2);
        if (b - a < 0.2 || a < clip.Start) { Status?.Invoke("Move the playhead over the clip (at least a moment before its end) first."); return; }
        Tl.Remember();
        Tl.Split(Tl.Group(clip).ToList(), a);
        var middle = Tl.Clips.FirstOrDefault(c => c.Track == clip.Track && Math.Abs(c.Start - a) < 1e-6) ?? clip;
        Tl.Split(Tl.Group(middle).ToList(), b);
        middle.Scale = Math.Min(20, middle.Scale * 1.35);
        Select(new[] { middle });
        Edited?.Invoke();
        Redraw();
        Status?.Invoke("Punched in. Drag the picture on the preview to zoom in on a spot, or change Size in Properties.");
    }

    /// <summary>The clip fades in over the one before it (half a second; drag its fade handle to change).</summary>
    public void CrossfadeInto(TClip clip)
    {
        if (Tl == null) return;
        Tl.Remember();
        double d = Tl.Crossfade(clip, 0.5);
        if (d <= 0) { Tl.Forget(); Status?.Invoke("No room for a crossfade: the clip before has no more footage after its end."); return; }
        // Its sound comes in as gently.
        foreach (var s in Tl.Clips.Where(o => o.Link != null && o.Link == clip.Link && o != clip)) { s.FadeIn = Math.Max(s.FadeIn, Math.Min(d, s.Length)); EditTimeline.ClampFades(s); }
        InvalidateMeasure();
        Edited?.Invoke();
        Redraw();
        Status?.Invoke($"Crossfade of {d:0.0#} s. Drag the clip's top-left corner to make it longer or shorter.");
    }

    /// <summary>M: a marker at the playhead (or the one there goes).</summary>
    public void ToggleMarker()
    {
        if (Tl == null) return;
        Tl.Remember();
        bool added = Tl.ToggleMarker(Playhead, 6 / _pps);
        Edited?.Invoke();
        Redraw();
        Status?.Invoke(added ? "Marker added. Ctrl+← / Ctrl+→ jump between markers." : "Marker removed.");
    }

    /// <summary>The playhead to the next (or previous) marker. False when there's none that way.</summary>
    public bool JumpMarker(bool next)
    {
        if (Tl == null) return false;
        double? m = next ? Tl.Markers.Where(x => x > Playhead + 0.01).Cast<double?>().FirstOrDefault()
                         : Tl.Markers.Where(x => x < Playhead - 0.01).Cast<double?>().LastOrDefault();
        if (m == null) return false;
        SetPlayhead(m.Value, true);
        return true;
    }

    public void Refresh()
    {
        Selected.RemoveWhere(id => Tl == null || Tl.Clips.All(c => c.Id != id));
        if (SelectedGap is { } g && (Tl == null || g.Track >= Tl.Tracks.Count)) SelectedGap = null;
        InvalidateMeasure();
        Redraw();
    }
}
