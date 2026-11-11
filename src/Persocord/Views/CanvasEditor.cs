using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Handles on the preview for the selected clip's picture: drag it to move
/// (it snaps to the frame's centre and edges), drag a corner to resize,
/// the round handle above to turn it (Shift: 15° steps). Double-click (or
/// Crop in Properties) to crop it instead: drag its edges in. Clicking
/// another picture selects that one. Works in the frame's own pixels, so
/// what's set here is exactly what's saved.
/// </summary>
public sealed class CanvasEditor : FrameworkElement
{
    private const double HandleSize = 9, RotateGap = 26, SnapPx = 7;

    /// <summary>The frame's size (the saved video's).</summary>
    public double FrameW { get; set; } = 1920;
    public double FrameH { get; set; } = 1080;

    /// <summary>The pictures under the playhead, top first, with their own sizes.</summary>
    public Func<IReadOnlyList<(TClip Clip, MediaInfo Source)>>? Layers { get; set; }

    /// <summary>The selected clip (null: nothing to move).</summary>
    public TClip? Target { get; set; }

    /// <summary>Cropping instead of moving.</summary>
    public bool CropMode
    {
        get => _crop;
        set { _crop = value; CropModeChanged?.Invoke(); InvalidateVisual(); }
    }
    private bool _crop;

    /// <summary>A change is about to start (keep the state for undo).</summary>
    public event Action? Begin;
    /// <summary>The picture moved (place it again, nothing saved yet).</summary>
    public event Action? Changing;
    /// <summary>A change ended (save it).</summary>
    public event Action? Committed;
    /// <summary>A picture was clicked (null: empty frame).</summary>
    public event Action<TClip?>? Picked;
    public event Action? CropModeChanged;

    public CanvasEditor()
    {
        Focusable = false;
        ClipToBounds = true;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /* ---- the frame on screen -------------------------------------------------------- */

    private double K => Math.Min(ActualWidth / FrameW, ActualHeight / FrameH);
    private Point Off => new((ActualWidth - FrameW * K) / 2, (ActualHeight - FrameH * K) / 2);
    private Point ToScreen(double fx, double fy) => new(Off.X + fx * K, Off.Y + fy * K);
    private Point ToFrame(Point s) => new((s.X - Off.X) / K, (s.Y - Off.Y) / K);

    private (TClip Clip, MediaInfo Source)? Current()
    {
        if (Target == null || Layers == null) return null;
        foreach (var l in Layers()) if (l.Clip == Target) return l;
        return null;
    }

    private static Point Turn(double x, double y, double deg)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return new Point(x * c - y * s, x * s + y * c);
    }

    /// <summary>A corner of the visible (cropped) picture, in the frame: sx, sy are -1 or 1.</summary>
    private static Point Corner(LayerBox b, int sx, int sy)
    {
        var d = Turn(sx * b.CropWidth / 2, sy * b.CropHeight / 2, b.Rotation);
        return new Point(b.CropCentreX + d.X, b.CropCentreY + d.Y);
    }

    /// <summary>The middle of a side of the visible picture (side: 0 left, 1 top, 2 right, 3 bottom).</summary>
    private static Point SideMid(LayerBox b, int side)
    {
        var (x, y) = side switch { 0 => (-b.CropWidth / 2, 0.0), 1 => (0.0, -b.CropHeight / 2), 2 => (b.CropWidth / 2, 0.0), _ => (0.0, b.CropHeight / 2) };
        var d = Turn(x, y, b.Rotation);
        return new Point(b.CropCentreX + d.X, b.CropCentreY + d.Y);
    }

    private Point RotateKnob(LayerBox b)
    {
        var top = SideMid(b, 1);
        var dir = Turn(0, -1, b.Rotation);
        var s = ToScreen(top.X, top.Y);
        return new Point(s.X + dir.X * RotateGap, s.Y + dir.Y * RotateGap);
    }

    /* ---- drawing ---------------------------------------------------------------------- */

    private static readonly Pen Outline = Frozen(new Pen(Brushes.White, 1.5)), Shadow = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 3.5)),
        Guide = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0xC4)), 1) { DashStyle = DashStyles.Dash }),
        HandleEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), 1));
    private static readonly Brush HandleFill = Brushes.White, CropShade = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)));
    private static Pen Frozen(Pen p) { p.Freeze(); return p; }
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private bool _guideX, _guideY;

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent, but there, so clicks anywhere on the preview arrive.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        try { DrawHandles(dc); }
        catch (Exception ex) { if (!_drawFailed) { _drawFailed = true; Persocord.Services.Log.Error("preview handles", ex); } }
    }

    private bool _drawFailed;

    private void DrawHandles(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (Current() is not { } cur) return;
        var c = cur.Clip;
        var box = LayerBox.Of(c, FrameW, FrameH, cur.Source.Width, cur.Source.Height);

        if (_guideX) { var a = ToScreen(FrameW / 2, 0); dc.DrawLine(Guide, a, new Point(a.X, Off.Y + FrameH * K)); }
        if (_guideY) { var a = ToScreen(0, FrameH / 2); dc.DrawLine(Guide, a, new Point(Off.X + FrameW * K, a.Y)); }

        if (CropMode)
        {
            // The whole picture, faintly, with the kept part bright inside it.
            var whole = Poly(new[] { Whole(box, -1, -1), Whole(box, 1, -1), Whole(box, 1, 1), Whole(box, -1, 1) });
            var kept = Poly(new[] { Corner(box, -1, -1), Corner(box, 1, -1), Corner(box, 1, 1), Corner(box, -1, 1) });
            dc.DrawGeometry(CropShade, Shadow, new CombinedGeometry(GeometryCombineMode.Exclude, whole, kept));
            dc.DrawGeometry(null, Outline, kept);
            for (int side = 0; side < 4; side++)
            {
                var m = SideMid(box, side);
                var s = ToScreen(m.X, m.Y);
                bool across = side is 0 or 2;
                var r = new Rect(s.X - (across ? 3 : 10), s.Y - (across ? 10 : 3), across ? 6 : 20, across ? 20 : 6);
                dc.PushTransform(new RotateTransform(box.Rotation, s.X, s.Y));
                dc.DrawRoundedRectangle(HandleFill, HandleEdge, r, 2, 2);
                dc.Pop();
            }
            return;
        }

        var outline = Poly(new[] { Corner(box, -1, -1), Corner(box, 1, -1), Corner(box, 1, 1), Corner(box, -1, 1) });
        dc.DrawGeometry(null, Shadow, outline);
        dc.DrawGeometry(null, Outline, outline);
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            var p = Corner(box, sx, sy);
            var s = ToScreen(p.X, p.Y);
            dc.DrawRectangle(HandleFill, HandleEdge, new Rect(s.X - HandleSize / 2, s.Y - HandleSize / 2, HandleSize, HandleSize));
        }
        var topMid = SideMid(box, 1);
        var tm = ToScreen(topMid.X, topMid.Y);
        var knob = RotateKnob(box);
        dc.DrawLine(Outline, tm, knob);
        dc.DrawEllipse(HandleFill, HandleEdge, knob, 6, 6);
    }

    private static Point Whole(LayerBox b, int sx, int sy)
    {
        var d = Turn(sx * b.Width / 2, sy * b.Height / 2, b.Rotation);
        return new Point(b.CentreX + d.X, b.CentreY + d.Y);
    }

    private Geometry Poly(Point[] framePts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(ToScreen(framePts[0].X, framePts[0].Y), true, true);
            for (int i = 1; i < framePts.Length; i++) ctx.LineTo(ToScreen(framePts[i].X, framePts[i].Y), true, false);
        }
        g.Freeze();
        return g;
    }

    /* ---- mouse ------------------------------------------------------------------------- */

    private enum Op { None, Move, Scale, Rotate, CropL, CropT, CropR, CropB }
    private Op _op;
    private Point _start;
    private double _x0, _y0, _scale0, _rot0, _dist0, _angle0;
    private bool _begun;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        _op = Op.None;
        _begun = false;
        if (Current() is { } cur)
        {
            var box = LayerBox.Of(cur.Clip, FrameW, FrameH, cur.Source.Width, cur.Source.Height);
            _op = HandleAt(p, box);
            if (_op == Op.None && box.Contains(ToFrame(p).X, ToFrame(p).Y)) _op = Op.Move;
            if (_op != Op.None && e.ClickCount == 2 && _op == Op.Move) { CropMode = !CropMode; _op = Op.None; e.Handled = true; return; }
            if (_op != Op.None)
            {
                var c = cur.Clip;
                _start = p;
                _x0 = c.X; _y0 = c.Y; _scale0 = c.Scale; _rot0 = c.Rotation;
                var centre = ToScreen(box.CropCentreX, box.CropCentreY);
                _dist0 = Math.Max(1, (p - centre).Length);
                _angle0 = Math.Atan2(p.Y - centre.Y, p.X - centre.X);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }
        // Not on the selected picture: pick whichever picture is here (the top one).
        var f = ToFrame(p);
        TClip? hit = null;
        if (Layers != null)
            foreach (var (clip, src) in Layers())
                if (LayerBox.Of(clip, FrameW, FrameH, src.Width, src.Height).Contains(f.X, f.Y)) { hit = clip; break; }
        if (CropMode && hit != Target) CropMode = false;
        Picked?.Invoke(hit);
        // Picked and grabbed in one go: it can be dragged straight away.
        if (hit != null && Current() is { } now && now.Clip == hit && !CropMode)
        {
            _op = Op.Move;
            _start = p;
            _x0 = hit.X; _y0 = hit.Y; _scale0 = hit.Scale; _rot0 = hit.Rotation;
            CaptureMouse();
        }
        e.Handled = true;
        InvalidateVisual();
    }

    private Op HandleAt(Point p, LayerBox box)
    {
        bool Near(Point frame, double r = 9) { var s = ToScreen(frame.X, frame.Y); return (s - p).Length <= r; }
        if (CropMode)
        {
            if (Near(SideMid(box, 0), 12)) return Op.CropL;
            if (Near(SideMid(box, 1), 12)) return Op.CropT;
            if (Near(SideMid(box, 2), 12)) return Op.CropR;
            if (Near(SideMid(box, 3), 12)) return Op.CropB;
            return Op.None;
        }
        if ((RotateKnob(box) - p).Length <= 10) return Op.Rotate;
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            if (Near(Corner(box, sx, sy))) return Op.Scale;
        return Op.None;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_op == Op.None)
        {
            Cursor = Current() is { } cur ? CursorFor(p, LayerBox.Of(cur.Clip, FrameW, FrameH, cur.Source.Width, cur.Source.Height)) : null;
            return;
        }
        // Let go somewhere this never heard about: the change so far is kept, and it stops following the mouse.
        if (e.LeftButton != MouseButtonState.Pressed) { Finish(); return; }
        if (Current() is not { } now) return;
        if (!_begun && (p - _start).Length < 3) return;
        if (!_begun) { _begun = true; Begin?.Invoke(); }
        var c = now.Clip;
        var src = now.Source;
        _guideX = _guideY = false;
        switch (_op)
        {
            case Op.Move:
            {
                c.X = _x0 + (p.X - _start.X) / K / FrameW;
                c.Y = _y0 + (p.Y - _start.Y) / K / FrameH;
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) SnapPosition(c, src);
                break;
            }
            case Op.Scale:
            {
                var box = LayerBox.Of(c, FrameW, FrameH, src.Width, src.Height);
                var centre = ToScreen(box.CropCentreX, box.CropCentreY);
                double d = Math.Max(1, (p - centre).Length);
                double s = _scale0 * d / _dist0;
                // Close to filling the frame: it snaps there.
                if (Math.Abs(s - 1) < 0.03 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) s = 1;
                c.Scale = s;
                break;
            }
            case Op.Rotate:
            {
                var box = LayerBox.Of(c, FrameW, FrameH, src.Width, src.Height);
                var centre = ToScreen(box.CropCentreX, box.CropCentreY);
                double a = Math.Atan2(p.Y - centre.Y, p.X - centre.X);
                double deg = _rot0 + (a - _angle0) * 180 / Math.PI;
                deg = Math.IEEERemainder(deg, 360);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) deg = Math.Round(deg / 15) * 15;
                else foreach (var snap in new[] { -180.0, -90, 0, 90, 180 }) if (Math.Abs(deg - snap) < 3) deg = snap;
                c.Rotation = deg;
                break;
            }
            default: Crop(c, src, p); break;
        }
        c.Tidy();
        Changing?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Snaps the picture's centre to the frame's centre lines, and its edges to the frame's edges (when not turned).</summary>
    private void SnapPosition(TClip c, MediaInfo src)
    {
        double tol = SnapPx / K;   // frame pixels
        var box = LayerBox.Of(c, FrameW, FrameH, src.Width, src.Height);
        double dx = 0, dy = 0;
        if (Math.Abs(box.CropCentreX - FrameW / 2) < tol) { dx = FrameW / 2 - box.CropCentreX; _guideX = true; }
        if (Math.Abs(box.CropCentreY - FrameH / 2) < tol) { dy = FrameH / 2 - box.CropCentreY; _guideY = true; }
        if (Math.Abs(Math.IEEERemainder(c.Rotation, 90)) < 1e-3)
        {
            var (bw, bh) = box.Bounds();
            double l = box.CropCentreX - bw / 2, r = box.CropCentreX + bw / 2, t = box.CropCentreY - bh / 2, b = box.CropCentreY + bh / 2;
            if (!_guideX) { if (Math.Abs(l) < tol) dx = -l; else if (Math.Abs(r - FrameW) < tol) dx = FrameW - r; }
            if (!_guideY) { if (Math.Abs(t) < tol) dy = -t; else if (Math.Abs(b - FrameH) < tol) dy = FrameH - b; }
        }
        c.X += dx / FrameW;
        c.Y += dy / FrameH;
    }

    /// <summary>Drags one side of the crop to the mouse (measured along the picture, turned as it is).</summary>
    private void Crop(TClip c, MediaInfo src, Point p)
    {
        var box = LayerBox.Of(c, FrameW, FrameH, src.Width, src.Height);
        var f = ToFrame(p);
        var local = Turn(f.X - box.CentreX, f.Y - box.CentreY, -box.Rotation);
        double u = (local.X + box.Width / 2) / Math.Max(1, box.Width), v = (local.Y + box.Height / 2) / Math.Max(1, box.Height);
        switch (_op)
        {
            case Op.CropL: c.CropL = Math.Clamp(u, 0, 0.95 - c.CropR); break;
            case Op.CropR: c.CropR = Math.Clamp(1 - u, 0, 0.95 - c.CropL); break;
            case Op.CropT: c.CropT = Math.Clamp(v, 0, 0.95 - c.CropB); break;
            case Op.CropB: c.CropB = Math.Clamp(1 - v, 0, 0.95 - c.CropT); break;
        }
    }

    private Cursor? CursorFor(Point p, LayerBox box) => HandleAt(p, box) switch
    {
        Op.Rotate => Cursors.Hand,
        Op.Scale => Cursors.SizeNWSE,
        Op.CropL or Op.CropR => Cursors.SizeWE,
        Op.CropT or Op.CropB => Cursors.SizeNS,
        _ => box.Contains(ToFrame(p).X, ToFrame(p).Y) && !CropMode ? Cursors.SizeAll : null,
    };

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Finish();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        Finish();
        base.OnLostMouseCapture(e);
    }

    private void Finish()
    {
        var op = _op;
        _op = Op.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _guideX = _guideY = false;
        if (op != Op.None && _begun) { _begun = false; Committed?.Invoke(); }
        InvalidateVisual();
    }
}
