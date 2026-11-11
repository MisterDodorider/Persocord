using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Persocord.Services;

public enum TrackType { Video, Audio }

/// <summary>What a clip shows: footage (a video, GIF or sound file), a still picture or text (both any length).</summary>
public enum ClipKind { Media, Image, Text }

/// <summary>How a text clip looks. Sizes are in pixels on a 1080-high frame (they grow with bigger frames).</summary>
public sealed class TextStyle
{
    public string Text { get; set; } = "Your text";
    public string Font { get; set; } = "Segoe UI";
    public double Size { get; set; } = 96;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public string Colour { get; set; } = "#FFFFFF";
    public double Outline { get; set; }
    public string OutlineColour { get; set; } = "#000000";
    public bool Shadow { get; set; } = true;
    public bool Box { get; set; }
    public string BoxColour { get; set; } = "#000000";
    public double BoxOpacity { get; set; } = 0.6;
    /// <summary>Left, Center or Right (for text on several lines).</summary>
    public string Align { get; set; } = "Center";

    public TextStyle Clone() => (TextStyle)MemberwiseClone();

    /// <summary>Everything that changes the picture, as one line (to name its picture by).</summary>
    public string Key() => System.FormattableString.Invariant(
        $"{Text}|{Font}|{Size:F2}|{Bold}|{Italic}|{Colour}|{Outline:F2}|{OutlineColour}|{Shadow}|{Box}|{BoxColour}|{BoxOpacity:F3}|{Align}");
}

/// <summary>What fills the frame around clips that don't cover it.</summary>
public enum FrameBackground { Black, Blur }

/// <summary>One row of the timeline: V1, V2… or A1, A2… (top to bottom as shown).</summary>
public sealed class TTrack
{
    public TrackType Type { get; set; }
    public string Name { get; set; } = "";
    /// <summary>What the track holds, e.g. "Discord" (audio) or "" (video).</summary>
    public string Source { get; set; } = "";
    /// <summary>Off: greyed out, hidden (video) or muted (audio), left out of the result.</summary>
    public bool Enabled { get; set; } = true;
    public bool Locked { get; set; }
    public bool Solo { get; set; }
    /// <summary>Loudness of an audio track (0-2; 1 is as recorded).</summary>
    public double Volume { get; set; } = 1.0;
    /// <summary>A name given by hand ("" shows what the track holds).</summary>
    public string Title { get; set; } = "";

    /// <summary>What the header shows under the track's number.</summary>
    [JsonIgnore] public string Shown => Title.Length > 0 ? Title : Source;
}

/// <summary>A piece of footage or sound placed on a track.</summary>
public sealed class TClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Track { get; set; }
    public string File { get; set; } = "";
    /// <summary>Audio stream in the file; -1 for the picture.</summary>
    public int Stream { get; set; } = -1;
    public string Label { get; set; } = "";
    /// <summary>Where it sits on the timeline (seconds).</summary>
    public double Start { get; set; }
    /// <summary>The part of the file used (seconds).</summary>
    public double In { get; set; }
    public double Out { get; set; }
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    /// <summary>Clips sharing a link (a clip's own picture and sound) move, cut and trim together.</summary>
    public string? Link { get; set; }
    /// <summary>Clips grouped by hand (Ctrl+G) also move, cut and delete together.</summary>
    public string? GroupId { get; set; }

    public ClipKind Kind { get; set; } = ClipKind.Media;

    // Where the picture sits (video tracks). Untouched, it fills the frame as
    // before: centred, as large as fits. X and Y move its centre, as a share
    // of the frame's width and height; Scale is its size against that fit;
    // Rotation is in degrees, clockwise; Opacity 0-1.
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1.0;

    // A crop of this clip alone: the share of its picture cut off each side
    // (0-0.95). The rest stays exactly where it was.
    public double CropL { get; set; }
    public double CropT { get; set; }
    public double CropR { get; set; }
    public double CropB { get; set; }

    /// <summary>This clip's own loudness (0-2), on top of its track's.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>This clip's own speed (2 = twice as fast, so half as long on the timeline).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>A text clip's words and look (null for anything else).</summary>
    public TextStyle? Text { get; set; }

    /// <summary>How long it lasts on the timeline (its part of the file, sped up or slowed down).</summary>
    public double Length => (Out - In) / Speed;
    public double End => Start + Length;
    public TClip Copy()
    {
        var c = (TClip)MemberwiseClone();
        c.Text = Text?.Clone();
        return c;
    }

    /// <summary>The moment of its file shown at a timeline time.</summary>
    public double SrcAt(double t) => In + (t - Start) * Speed;

    /// <summary>A still picture or text: no length of its own, nothing to decode.</summary>
    [JsonIgnore] public bool IsImage => Kind is ClipKind.Image or ClipKind.Text;
    [JsonIgnore] public bool IsText => Kind == ClipKind.Text;
    [JsonIgnore] public bool Cropped => CropL > 1e-4 || CropT > 1e-4 || CropR > 1e-4 || CropB > 1e-4;
    /// <summary>Moved, resized, turned, see-through or cropped: not simply filling the frame.</summary>
    [JsonIgnore] public bool Placed =>
        Math.Abs(X) > 1e-4 || Math.Abs(Y) > 1e-4 || Math.Abs(Scale - 1) > 1e-4 || Math.Abs(Rotation) > 1e-3 || Opacity < 0.9999 || Cropped;

    /// <summary>Back to filling the frame (position, size, turn, see-through and crop).</summary>
    public void ResetPlacement() { X = Y = Rotation = 0; Scale = Opacity = 1; CropL = CropT = CropR = CropB = 0; }

    /// <summary>Keeps the placement sensible: a visible size, a crop that leaves some picture.</summary>
    public void Tidy()
    {
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, 0.02, 20);
        Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 1, 0, 1);
        X = Math.Clamp(double.IsFinite(X) ? X : 0, -5, 5);
        Y = Math.Clamp(double.IsFinite(Y) ? Y : 0, -5, 5);
        Rotation = double.IsFinite(Rotation) ? Math.IEEERemainder(Rotation, 360) : 0;
        CropL = Math.Clamp(double.IsFinite(CropL) ? CropL : 0, 0, 0.95);
        CropR = Math.Clamp(double.IsFinite(CropR) ? CropR : 0, 0, 0.95 - CropL);
        CropT = Math.Clamp(double.IsFinite(CropT) ? CropT : 0, 0, 0.95);
        CropB = Math.Clamp(double.IsFinite(CropB) ? CropB : 0, 0, 0.95 - CropT);
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 1, 0, 2);
        Speed = IsImage ? 1 : Math.Clamp(double.IsFinite(Speed) && Speed > 0 ? Speed : 1, 0.1, 10);
    }
}

/// <summary>
/// Where a clip's picture lands in the frame, in the frame's pixels. One
/// calculation for the live preview and the saved video, so they match.
/// Width, Height and the centre are the whole (uncropped) picture's; the
/// crop values are the part left after cropping (its centre after turning).
/// </summary>
public readonly record struct LayerBox(
    double Width, double Height, double CentreX, double CentreY,
    double CropWidth, double CropHeight, double CropCentreX, double CropCentreY,
    double Rotation)
{
    /// <summary>Text pictures are drawn twice as big as their size on a 1080-high frame (for sharp edges).</summary>
    public const double TextOversample = 2;

    public static LayerBox Of(TClip c, double frameW, double frameH, double srcW, double srcH)
    {
        srcW = Math.Max(1, srcW); srcH = Math.Max(1, srcH);
        // Text keeps its own size (grown with the frame); everything else starts as large as fits.
        double fit = (c.IsText ? frameH / 1080.0 / TextOversample : Math.Min(frameW / srcW, frameH / srcH)) * c.Scale;
        double w = srcW * fit, h = srcH * fit;
        double cx = frameW / 2 + c.X * frameW, cy = frameH / 2 + c.Y * frameH;
        double cw = w * (1 - c.CropL - c.CropR), ch = h * (1 - c.CropT - c.CropB);
        // The kept part's centre, measured from the whole picture's, turned with it.
        double dx = (c.CropL - c.CropR) / 2 * w, dy = (c.CropT - c.CropB) / 2 * h;
        double a = c.Rotation * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        return new LayerBox(w, h, cx, cy, cw, ch, cx + dx * cos - dy * sin, cy + dx * sin + dy * cos, c.Rotation);
    }

    /// <summary>The size of the box the turned, cropped picture needs.</summary>
    public (double W, double H) Bounds()
    {
        double a = Rotation * Math.PI / 180, cos = Math.Abs(Math.Cos(a)), sin = Math.Abs(Math.Sin(a));
        return (CropWidth * cos + CropHeight * sin, CropWidth * sin + CropHeight * cos);
    }

    /// <summary>Whether a point of the frame is on the visible (cropped, turned) picture.</summary>
    public bool Contains(double x, double y)
    {
        double a = -Rotation * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double px = x - CropCentreX, py = y - CropCentreY;
        double lx = px * cos - py * sin, ly = px * sin + py * cos;
        return Math.Abs(lx) <= CropWidth / 2 && Math.Abs(ly) <= CropHeight / 2;
    }

    /// <summary>Covers the whole frame, unturned and solid: nothing under it can be seen.</summary>
    public bool Covers(double frameW, double frameH) =>
        Math.Abs(Math.IEEERemainder(Rotation, 360)) < 1e-3 &&
        CropCentreX - CropWidth / 2 <= 0.5 && CropCentreX + CropWidth / 2 >= frameW - 0.5 &&
        CropCentreY - CropHeight / 2 <= 0.5 && CropCentreY + CropHeight / 2 >= frameH - 0.5;
}

/// <summary>A crop, in the source's pixels.</summary>
public sealed record CropBox(int X, int Y, int Width, int Height);

public sealed class EditTimeline
{
    public const double Eps = 1e-4;
    public const double MinLength = 0.05;

    public List<TTrack> Tracks { get; set; } = new();
    public List<TClip> Clips { get; set; } = new();
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public CropBox? Crop { get; set; }
    /// <summary>In and out marks (I / O): what gets saved. Out ≤ In means everything.</summary>
    public double MarkIn { get; set; }
    public double MarkOut { get; set; }
    /// <summary>Highlight markers (M), in timeline seconds.</summary>
    public List<double> Markers { get; set; } = new();
    /// <summary>The frame's shape: "" (the clip's own), "16:9", "9:16", "1:1" or "4:5".</summary>
    public string Aspect { get; set; } = "";
    public FrameBackground Background { get; set; } = FrameBackground.Black;

    public static readonly string[] Aspects = { "", "16:9", "9:16", "1:1", "4:5" };

    /// <summary>
    /// The saved video's size: the clip's own, or the shape chosen with the
    /// clip's shorter side kept (1920×1080 becomes 1080×1920 for 9:16).
    /// </summary>
    public (int W, int H) FrameSize(int srcW, int srcH)
    {
        srcW = Math.Max(2, srcW); srcH = Math.Max(2, srcH);
        var parts = Aspect.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) || a <= 0 || b <= 0)
            return (srcW - srcW % 2, srcH - srcH % 2);
        double ratio = a / b, shortSide = Math.Min(srcW, srcH);
        double w = ratio >= 1 ? shortSide * ratio : shortSide, h = ratio >= 1 ? shortSide : shortSide / ratio;
        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
        return (Even(w), Even(h));
    }

    /// <summary>Length of each source file (not saved with undo).</summary>
    public Dictionary<string, double> SourceLength { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;
    public void Touch() => Changed?.Invoke();

    public double Duration => Clips.Count == 0 ? 0 : Clips.Max(c => c.End);

    /// <summary>The saved range: the marks if set, otherwise everything.</summary>
    public (double From, double To) Range =>
        MarkOut > MarkIn + Eps ? (Math.Max(0, MarkIn), Math.Min(Duration, MarkOut)) : (0, Duration);

    /* ---- building ------------------------------------------------------------- */

    /// <summary>
    /// A clip laid out as V2 (empty), V1 (the picture), then one audio track
    /// per sound source, all linked.
    /// </summary>
    public static EditTimeline From(string file, string label, MediaInfo info)
    {
        var tl = new EditTimeline();
        tl.SourceLength[file] = info.Duration;
        tl.Tracks.Add(new TTrack { Type = TrackType.Video, Name = "V2" });
        tl.Tracks.Add(new TTrack { Type = TrackType.Video, Name = "V1" });
        string link = Guid.NewGuid().ToString("N");
        tl.Clips.Add(new TClip { Track = 1, File = file, Label = label, In = 0, Out = info.Duration, Link = link });

        // A file saved with "Separate audio tracks" has no Mix: each of its
        // tracks is still a track of its own here (not just the first one).
        var sounds = info.SeparateTracks.Count > 0
            ? info.SeparateTracks.ToList()
            : info.AudioTracks.Count > 1 ? info.AudioTracks.Select((n, i) => (Stream: i, Name: n)).ToList()
            : info.HasAudio ? new List<(int Stream, string Name)> { (0, "Sound") } : new();
        // "All PC audio" holds every app's sound, so it's heard either instead of
        // the app tracks or not at all: by default the app tracks, unless an app
        // couldn't be recorded on its own (its sound is only in All PC audio,
        // labelled "incl.") or there are no app tracks.
        bool hasPc = sounds.Any(s => IsAllPcName(s.Name));
        bool pcOn = !sounds.Any(s => IsAppName(s.Name)) || sounds.Any(s => IsAllPcName(s.Name) && s.Name.Contains("(incl."));
        int n = 1;
        foreach (var (stream, name) in sounds)
        {
            bool on = !hasPc || (IsAllPcName(name) ? pcOn : !IsAppName(name) || !pcOn);
            tl.Tracks.Add(new TTrack { Type = TrackType.Audio, Name = $"A{n++}", Source = name, Enabled = on });
            tl.Clips.Add(new TClip
            {
                Track = tl.Tracks.Count - 1, File = file, Stream = stream, Label = name,
                In = 0, Out = info.Duration, Link = link,
            });
        }
        return tl;
    }

    /// <summary>The track holding everything the PC played (its name may name an app it alone carries).</summary>
    public static bool IsAllPcName(string n) => n.StartsWith("All PC audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>A per-app track (a game, Discord, Other apps…): part of All PC audio.</summary>
    public static bool IsAppName(string n) =>
        !IsAllPcName(n) && !n.Equals("Microphone", StringComparison.OrdinalIgnoreCase) &&
        !n.Equals("PC audio", StringComparison.OrdinalIgnoreCase) && !n.Equals("Sound", StringComparison.OrdinalIgnoreCase);

    /// <summary>Another video track, on top.</summary>
    public void AddVideoTrack()
    {
        int count = Tracks.Count(t => t.Type == TrackType.Video);
        Tracks.Insert(0, new TTrack { Type = TrackType.Video, Name = $"V{count + 1}" });
        foreach (var c in Clips) c.Track++;
    }

    public int AddAudioTrack(string source)
    {
        int count = Tracks.Count(t => t.Type == TrackType.Audio);
        Tracks.Add(new TTrack { Type = TrackType.Audio, Name = $"A{count + 1}", Source = source });
        return Tracks.Count - 1;
    }

    /// <summary>How far into its file a clip can reach: the file's length, or (a still picture) as long as you like.</summary>
    public double SourceEnd(TClip c) =>
        c.IsImage ? 24 * 3600 : SourceLength.TryGetValue(c.File, out var l) && l > 0 ? l : c.Out;

    /// <summary>Names the tracks by where they are: V1 at the bottom of the pictures, A1 at the top of the sound.</summary>
    public void Renumber()
    {
        int videos = Tracks.Count(t => t.Type == TrackType.Video), v = videos, a = 1;
        foreach (var t in Tracks)
            t.Name = t.Type == TrackType.Video ? $"V{v--}" : $"A{a++}";
    }

    /// <summary>Removes a track and everything on it.</summary>
    public void DeleteTrack(int track)
    {
        if (track < 0 || track >= Tracks.Count) return;
        Clips.RemoveAll(c => c.Track == track);
        Tracks.RemoveAt(track);
        foreach (var c in Clips) if (c.Track > track) c.Track--;
        Renumber();
    }

    /// <summary>Moves a track to another place among its kind (pictures stay above sound).</summary>
    public bool MoveTrack(int from, int to)
    {
        if (from < 0 || from >= Tracks.Count || to < 0 || to >= Tracks.Count || from == to) return false;
        if (Tracks[from].Type != Tracks[to].Type) return false;
        var order = Enumerable.Range(0, Tracks.Count).ToList();
        order.RemoveAt(from);
        order.Insert(to, from);
        // order[newIndex] = oldIndex
        var newOf = new int[Tracks.Count];
        for (int i = 0; i < order.Count; i++) newOf[order[i]] = i;
        Tracks = order.Select(i => Tracks[i]).ToList();
        foreach (var c in Clips) c.Track = newOf[c.Track];
        Renumber();
        return true;
    }

    /// <summary>
    /// Could these clips go here? Each must start at 0:00 or later, on an
    /// unlocked track of its own kind, clear of the others being placed. Clips
    /// already there don't stop it: they're covered (see Cover).
    /// </summary>
    public bool CanPlace(IEnumerable<(TClip Clip, int Track, double Start)> placed, IReadOnlyDictionary<string, TrackType>? kinds = null)
    {
        var list = placed.ToList();
        foreach (var (clip, track, start) in list)
        {
            if (start < -Eps || track < 0 || track >= Tracks.Count || Tracks[track].Locked) return false;
            var kind = kinds != null && kinds.TryGetValue(clip.Id, out var k) ? k
                     : clip.Track >= 0 && clip.Track < Tracks.Count ? Tracks[clip.Track].Type : Tracks[track].Type;
            if (Tracks[track].Type != kind) return false;
            double end = start + clip.Length;
            foreach (var (other, t2, s2) in list)
                if (other != clip && t2 == track && start < s2 + other.Length - Eps && end > s2 + Eps)
                    return false;
        }
        return true;
    }

    /// <summary>
    /// The clips just placed cover whatever was under them on their tracks:
    /// those are cut back where they overlap (split in two if a placed clip
    /// lands in their middle, removed if it hides them completely).
    /// </summary>
    public void Cover(IEnumerable<TClip> placed)
    {
        var top = placed.ToList();
        var mine = new HashSet<TClip>(top);
        foreach (var p in top)
        {
            double s = p.Start, e = p.End;
            foreach (var o in Clips.Where(o => !mine.Contains(o) && o.Track == p.Track && o.Start < e - Eps && o.End > s + Eps).ToList())
            {
                if (Tracks[o.Track].Locked) continue;
                bool startsBefore = o.Start < s - Eps, endsAfter = o.End > e + Eps;
                if (!startsBefore && !endsAfter) { Clips.Remove(o); continue; }
                if (startsBefore && endsAfter)
                {
                    // Landed in the middle: what's left after it is a clip of its own.
                    var right = o.Copy();
                    right.Id = Guid.NewGuid().ToString("N");
                    right.In = o.SrcAt(e);
                    right.Start = e;
                    right.FadeIn = 0;
                    right.Link = null;
                    right.GroupId = null;
                    o.Out = o.SrcAt(s);
                    o.FadeOut = 0;
                    ClampFades(o);
                    ClampFades(right);
                    Clips.Add(right);
                    continue;
                }
                if (startsBefore) { o.Out = o.SrcAt(s); ClampFades(o); }
                else
                {
                    o.In = o.SrcAt(e);
                    o.Start = e;
                    ClampFades(o);
                }
            }
        }
    }

    /* ---- asking ----------------------------------------------------------------- */

    /// <summary>Everything that goes with a clip: linked to it or grouped with it (and so on, through either).</summary>
    public IEnumerable<TClip> Group(TClip c)
    {
        var found = new HashSet<TClip> { c };
        var queue = new Queue<TClip>();
        queue.Enqueue(c);
        while (queue.Count > 0)
        {
            var x = queue.Dequeue();
            foreach (var o in Clips)
                if (!found.Contains(o) && ((x.Link != null && o.Link == x.Link) || (x.GroupId != null && o.GroupId == x.GroupId)))
                {
                    found.Add(o);
                    queue.Enqueue(o);
                }
        }
        return found.ToArray();
    }

    public void Unlink(IEnumerable<TClip> clips) { foreach (var c in clips) c.Link = null; }

    public void LinkTogether(IEnumerable<TClip> clips)
    {
        var id = Guid.NewGuid().ToString("N");
        foreach (var c in clips) c.Link = id;
    }

    public void GroupTogether(IEnumerable<TClip> clips)
    {
        var id = Guid.NewGuid().ToString("N");
        foreach (var c in clips) c.GroupId = id;
    }

    public void Ungroup(IEnumerable<TClip> clips) { foreach (var c in clips) c.GroupId = null; }

    /// <summary>Back to how the clip was (undoable).</summary>
    public void ResetTo(EditTimeline fresh)
    {
        Remember();
        Tracks = fresh.Tracks; Clips = fresh.Clips; Speed = fresh.Speed; Volume = fresh.Volume; Crop = fresh.Crop;
        MarkIn = fresh.MarkIn; MarkOut = fresh.MarkOut; Markers = fresh.Markers; Aspect = fresh.Aspect; Background = fresh.Background;
        Touch();
    }

    public IEnumerable<TClip> OnTrack(int track) => Clips.Where(c => c.Track == track).OrderBy(c => c.Start);

    public TClip? ClipAt(int track, double t) =>
        Clips.FirstOrDefault(c => c.Track == track && t >= c.Start - Eps && t <= c.End + Eps);

    /// <summary>The empty stretch on a track around a time (between clips), if any.</summary>
    public (double From, double To)? GapAt(int track, double t)
    {
        if (ClipAt(track, t) != null) return null;
        var before = OnTrack(track).Where(c => c.End <= t + Eps).Select(c => c.End).DefaultIfEmpty(0).Max();
        var after = OnTrack(track).Where(c => c.Start >= t - Eps).Select(c => c.Start).DefaultIfEmpty(double.NaN).Min();
        if (double.IsNaN(after) || after - before < Eps) return null;   // nothing after: not a gap
        return (before, after);
    }

    /// <summary>Would these clips fit here, clear of every other clip on their tracks?</summary>
    public bool Fits(IEnumerable<(TClip Clip, int Track, double Start)> placed)
    {
        var list = placed.ToList();
        var moving = new HashSet<string>(list.Select(p => p.Clip.Id));
        foreach (var (clip, track, start) in list)
        {
            if (start < -Eps || track < 0 || track >= Tracks.Count) return false;
            if (Tracks[track].Type != Tracks[clip.Track].Type) return false;
            double end = start + clip.Length;
            foreach (var o in Clips)
                if (!moving.Contains(o.Id) && o.Track == track && start < o.End - Eps && end > o.Start + Eps)
                    return false;
            foreach (var (other, t2, s2) in list)
                if (other != clip && t2 == track && start < s2 + other.Length - Eps && end > s2 + Eps)
                    return false;
        }
        return true;
    }

    /// <summary>The audio tracks that are heard (enabled, and soloed if any are).</summary>
    public List<int> HeardAudio()
    {
        var audio = Enumerable.Range(0, Tracks.Count).Where(i => Tracks[i].Type == TrackType.Audio).ToList();
        bool solo = audio.Any(i => Tracks[i].Solo && Tracks[i].Enabled);
        return audio.Where(i => Tracks[i].Enabled && (!solo || Tracks[i].Solo)).ToList();
    }

    /// <summary>A clip's fade level (0-1) at a timeline time.</summary>
    public static double FadeAt(TClip c, double t)
    {
        double f = 1;
        if (c.FadeIn > 0 && t < c.Start + c.FadeIn) f = Math.Min(f, (t - c.Start) / c.FadeIn);
        if (c.FadeOut > 0 && t > c.End - c.FadeOut) f = Math.Min(f, (c.End - t) / c.FadeOut);
        return Math.Clamp(f, 0, 1);
    }

    /* ---- editing ---------------------------------------------------------------- */

    /// <summary>Cuts clips in two at a time (each keeps its links with its own half).</summary>
    public bool Split(IEnumerable<TClip> clips, double t)
    {
        var newLinks = new Dictionary<string, string>();
        var newGroups = new Dictionary<string, string>();
        bool any = false;
        foreach (var c in clips.ToList())
        {
            if (t <= c.Start + MinLength || t >= c.End - MinLength) continue;
            var right = c.Copy();
            right.Id = Guid.NewGuid().ToString("N");
            right.In = c.SrcAt(t);
            right.Start = t;
            right.FadeIn = 0;
            if (c.Link != null)
            {
                if (!newLinks.TryGetValue(c.Link, out var nl)) newLinks[c.Link] = nl = Guid.NewGuid().ToString("N");
                right.Link = nl;
            }
            if (c.GroupId != null)
            {
                if (!newGroups.TryGetValue(c.GroupId, out var ng)) newGroups[c.GroupId] = ng = Guid.NewGuid().ToString("N");
                right.GroupId = ng;
            }
            c.Out = right.In;
            c.FadeOut = 0;
            ClampFades(c);
            ClampFades(right);
            Clips.Add(right);
            any = true;
        }
        return any;
    }

    /// <summary>Removes clips. Ripple: everything after on those tracks slides left to close the space.</summary>
    public void Delete(IEnumerable<TClip> clips, bool ripple)
    {
        var gone = clips.Where(c => !Tracks[c.Track].Locked).ToList();
        foreach (var c in gone) Clips.Remove(c);
        if (!ripple) return;
        foreach (var byTrack in gone.GroupBy(c => c.Track))
            foreach (var r in byTrack.OrderByDescending(c => c.Start))
                foreach (var o in Clips.Where(o => o.Track == r.Track && o.Start >= r.End - Eps))
                    o.Start -= r.Length;
    }

    /// <summary>
    /// When nothing is left at the start of the timeline (on any track), everything
    /// slides back so the first clip starts at 0:00. Returns how far it moved.
    /// </summary>
    public double CloseLeadingGap()
    {
        if (Clips.Count == 0) return 0;
        double first = Clips.Min(c => c.Start);
        if (first <= Eps) return 0;
        foreach (var c in Clips) c.Start = Math.Max(0, c.Start - first);
        Markers = Markers.Select(m => m - first).Where(m => m >= 0).ToList();
        if (MarkOut > MarkIn + Eps) { MarkIn = Math.Max(0, MarkIn - first); MarkOut = Math.Max(0, MarkOut - first); }
        return first;
    }

    /// <summary>Closes an empty stretch: that track's later clips (and those linked to them) slide left.</summary>
    public bool CloseGap(int track, double from, double to)
    {
        double len = to - from;
        if (len < Eps) return false;
        var movers = Clips.Where(c => c.Track == track && c.Start >= to - Eps)
                          .SelectMany(Group).Distinct().ToList();
        // Linked clips on other tracks must have room to slide too.
        if (!Fits(movers.Select(m => (m, m.Track, m.Start - len)))) return false;
        foreach (var m in movers) m.Start -= len;
        return true;
    }

    /// <summary>Trims a clip's start (left) or end (right) to a new timeline time; linked clips follow.</summary>
    public void Trim(IEnumerable<TClip> clips, bool left, double t)
    {
        foreach (var c in clips.Where(c => !Tracks[c.Track].Locked))
        {
            double srcLen = SourceEnd(c);
            if (left)
            {
                double dt = t - c.Start;
                dt = Math.Max(dt, c.IsImage ? -c.Start : -c.In / c.Speed); // not before the file starts (a picture has no start)
                dt = Math.Min(dt, c.Length - MinLength);          // keep some of it
                double prevEnd = Clips.Where(o => o != c && o.Track == c.Track && o.End <= c.Start + Eps)
                                      .Select(o => o.End).DefaultIfEmpty(0).Max();
                dt = Math.Max(dt, prevEnd - c.Start);             // not over the clip before
                if (c.IsImage) c.Out -= dt; else c.In += dt * c.Speed;   // a picture just gets longer or shorter
                c.Start += dt;
            }
            else
            {
                double dt = t - c.End;
                dt = Math.Min(dt, (srcLen - c.Out) / c.Speed);    // not past the file's end
                dt = Math.Max(dt, -(c.Length - MinLength));
                double nextStart = Clips.Where(o => o != c && o.Track == c.Track && o.Start >= c.End - Eps)
                                        .Select(o => o.Start).DefaultIfEmpty(double.MaxValue).Min();
                dt = Math.Min(dt, nextStart - c.End);             // not over the clip after
                c.Out += dt * c.Speed;
            }
            ClampFades(c);
        }
    }

    /* ---- quick cuts at the playhead (Ctrl+B, Q, E) ----------------------------------- */

    private IEnumerable<TClip> Unlocked => Clips.Where(c => !Tracks[c.Track].Locked);

    /// <summary>The clips a quick cut works on: the given ones (with what goes with them), or everything.</summary>
    private List<TClip> Scope(IEnumerable<TClip>? chosen) =>
        chosen == null ? Unlocked.ToList()
                       : chosen.SelectMany(Group).Distinct().Where(c => !Tracks[c.Track].Locked).ToList();

    /// <summary>Clips under a time (not just touching it at an edge).</summary>
    private static bool Spans(TClip c, double t) => t > c.Start + MinLength && t < c.End - MinLength;

    /// <summary>Splits at a time: the chosen clips, or everything under it.</summary>
    public bool SplitAt(IEnumerable<TClip>? chosen, double t) => Split(Scope(chosen).Where(c => Spans(c, t)), t);

    /// <summary>
    /// Removes everything before a time and slides what's left back. With
    /// nothing chosen: every track, from 0:00, and the rest starts at 0:00.
    /// With clips chosen: the part of them before the time, and the clips after
    /// them on those tracks slide back to close the space. Returns where the
    /// kept part now starts (for the playhead), or null if nothing changed.
    /// </summary>
    public double? CutLeft(IEnumerable<TClip>? chosen, double t)
    {
        if (chosen == null)
        {
            if (t <= Eps) return null;
            var all = Unlocked.ToList();
            if (all.Count == 0) return null;
            foreach (var c in all.Where(c => c.End <= t + Eps).ToList()) Clips.Remove(c);
            foreach (var c in Unlocked.Where(c => c.Start < t - Eps).ToList())
            {
                c.In = c.SrcAt(t);
                c.Start = t;
                ClampFades(c);
            }
            foreach (var c in Unlocked) c.Start = Math.Max(0, c.Start - t);
            if (MarkOut > MarkIn + Eps) { MarkIn = Math.Max(0, MarkIn - t); MarkOut = Math.Max(0, MarkOut - t); }
            return 0;
        }
        var hit = Scope(chosen).Where(c => Spans(c, t)).ToList();
        if (hit.Count == 0) return null;
        double? at = null;
        foreach (var c in hit)
        {
            double cut = t - c.Start, oldEnd = c.End;
            c.In = c.SrcAt(t);
            ClampFades(c);
            foreach (var o in Clips.Where(o => o != c && o.Track == c.Track && o.Start >= oldEnd - Eps)) o.Start -= cut;
            at = at == null ? c.Start : Math.Min(at.Value, c.Start);
        }
        return at;
    }

    /// <summary>
    /// Removes everything after a time. With nothing chosen: every track, to
    /// the end. With clips chosen: the part of them after the time, and the
    /// clips after them on those tracks slide back to close the space.
    /// </summary>
    public bool CutRight(IEnumerable<TClip>? chosen, double t)
    {
        if (chosen == null)
        {
            var all = Unlocked.ToList();
            if (all.Count == 0 || t >= Duration - Eps) return false;
            foreach (var c in all.Where(c => c.Start >= t - Eps).ToList()) Clips.Remove(c);
            foreach (var c in Unlocked.Where(c => c.End > t + Eps).ToList())
            {
                c.Out = c.SrcAt(t);
                ClampFades(c);
            }
            return true;
        }
        var hit = Scope(chosen).Where(c => Spans(c, t)).ToList();
        if (hit.Count == 0) return false;
        foreach (var c in hit)
        {
            double cut = c.End - t, oldEnd = c.End;
            c.Out = c.SrcAt(t);
            ClampFades(c);
            foreach (var o in Clips.Where(o => o != c && o.Track == c.Track && o.Start >= oldEnd - Eps)) o.Start -= cut;
        }
        return true;
    }

    /// <summary>
    /// Brings back footage cut from the start of clips that can't move further
    /// left (at 0:00, or against the clip before): each grows at its start by up
    /// to <paramref name="seconds"/> (as much as was cut), staying where it is,
    /// and everything after it on those tracks moves right to make room.
    /// Returns how much was brought back.
    /// </summary>
    public double ExtendStart(IEnumerable<TClip> clips, double seconds)
    {
        var list = clips.Where(c => !Tracks[c.Track].Locked).ToList();
        if (list.Count == 0 || seconds <= Eps) return 0;
        // Linked picture and sound grow together, so by the same amount.
        double e = Math.Min(seconds, list.Min(c => c.In / c.Speed));
        if (e <= Eps) return 0;
        var growing = new HashSet<TClip>(list);
        // Clips after them on their tracks move right (with whatever goes with them).
        var pushed = new HashSet<TClip>();
        foreach (var c in list)
            foreach (var o in Clips.Where(o => !growing.Contains(o) && o.Track == c.Track && o.Start >= c.Start - Eps))
                foreach (var g in Group(o))
                    if (!growing.Contains(g) && !Tracks[g.Track].Locked) pushed.Add(g);
        foreach (var c in list) { c.In -= e * c.Speed; ClampFades(c); }
        foreach (var o in pushed) o.Start += e;
        return e;
    }

    /// <summary>How far a clip's start can move left before it meets 0:00 or the clip before it.</summary>
    public double RoomBefore(TClip c) =>
        c.Start - Clips.Where(o => o != c && o.Track == c.Track && o.End <= c.Start + Eps).Select(o => o.End).DefaultIfEmpty(0).Max();

    public void SetFade(IEnumerable<TClip> clips, bool fadeIn, double seconds)
    {
        foreach (var c in clips)
        {
            if (fadeIn) c.FadeIn = Math.Max(0, seconds);
            else c.FadeOut = Math.Max(0, seconds);
            ClampFades(c);
        }
    }

    public static void ClampFades(TClip c)
    {
        c.FadeIn = Math.Clamp(c.FadeIn, 0, c.Length);
        c.FadeOut = Math.Clamp(c.FadeOut, 0, c.Length - c.FadeIn);
    }

    /* ---- part 2: speed, crossfades, removing stretches, markers ------------------- */

    /// <summary>
    /// Sets clips' own speed. Each keeps its start and gets longer or shorter;
    /// what comes after it on its track moves along by as much.
    /// </summary>
    public void SetSpeed(IEnumerable<TClip> clips, double speed)
    {
        speed = Math.Clamp(speed, 0.1, 10);
        foreach (var c in clips.Where(c => !c.IsImage && !Tracks[c.Track].Locked).OrderByDescending(c => c.Start).ToList())
        {
            double oldEnd = c.End;
            c.Speed = speed;
            double delta = c.End - oldEnd;
            if (Math.Abs(delta) < Eps) continue;
            foreach (var o in Clips.Where(o => o != c && o.Track == c.Track && o.Start >= oldEnd - Eps))
                o.Start = Math.Max(0, o.Start + delta);
            ClampFades(c);
        }
    }

    /// <summary>A new video track straight above another (its index; everything from there moves down one).</summary>
    public int InsertVideoTrackAbove(int track)
    {
        int at = Math.Clamp(track, 0, Tracks.Count);
        Tracks.Insert(at, new TTrack { Type = TrackType.Video });
        foreach (var c in Clips) if (c.Track >= at) c.Track++;
        Renumber();
        return at;
    }

    /// <summary>
    /// Crossfades a picture into the one before it on its track: the earlier
    /// one carries on underneath for <paramref name="seconds"/> (as far as its
    /// footage allows), and this one moves to a track just above and fades in
    /// over it. Nothing moves in time. Returns the length used (0: not possible).
    /// </summary>
    public double Crossfade(TClip b, double seconds)
    {
        if (Tracks[b.Track].Type != TrackType.Video || Tracks[b.Track].Locked) return 0;
        var a = Clips.Where(o => o != b && o.Track == b.Track && Math.Abs(o.End - b.Start) < 0.02).OrderByDescending(o => o.End).FirstOrDefault();
        if (a == null) return 0;
        // How far the earlier clip can carry on: its footage, and this clip's length.
        double room = Math.Min((SourceEnd(a) - a.Out) / a.Speed, b.Length - MinLength);
        double d = Math.Min(seconds, room);
        if (d < 0.04) return 0;
        // A track above with room for this clip; a new one straight above if none has.
        int above = -1;
        for (int i = b.Track - 1; i >= 0 && Tracks[i].Type == TrackType.Video; i--)
            if (!Tracks[i].Locked && Clips.All(o => o.Track != i || o.End <= b.Start + Eps || o.Start >= b.End - Eps)) { above = i; break; }
        if (above < 0) { above = InsertVideoTrackAbove(b.Track); }
        a = Clips.First(o => o.Id == a.Id);
        b.Track = above;
        a.Out += d * a.Speed;
        b.FadeIn = Math.Max(b.FadeIn, d);
        ClampFades(a);
        ClampFades(b);
        return d;
    }

    /// <summary>
    /// Removes a stretch of time from every unlocked track (what's in it goes,
    /// everything after moves back), so picture and sound stay together.
    /// </summary>
    public bool RemoveRange(double from, double to)
    {
        if (to - from < MinLength) return false;
        var unlocked = Clips.Where(c => !Tracks[c.Track].Locked).ToList();
        Split(unlocked.Where(c => c.Start < from - Eps && c.End > from + Eps), from);
        Split(Clips.Where(c => !Tracks[c.Track].Locked && c.Start < to - Eps && c.End > to + Eps), to);
        Clips.RemoveAll(c => !Tracks[c.Track].Locked && c.Start >= from - Eps && c.End <= to + Eps);
        foreach (var c in Clips.Where(c => !Tracks[c.Track].Locked && c.Start >= to - Eps)) c.Start = Math.Max(0, c.Start - (to - from));
        Markers = Markers.Where(m => m < from || m > to).Select(m => m >= to ? m - (to - from) : m).ToList();
        if (MarkOut > MarkIn + Eps)
        {
            double Shift(double m) => m <= from ? m : m >= to ? m - (to - from) : from;
            MarkIn = Shift(MarkIn); MarkOut = Shift(MarkOut);
        }
        return true;
    }

    /// <summary>
    /// Inserts a still of the picture at <paramref name="t"/> for some seconds:
    /// everything at and after that moment moves along to make room (on every
    /// unlocked track, so sound stays with picture). The still goes on the
    /// clip's own track. Returns the new clip.
    /// </summary>
    public TClip? InsertHold(TClip under, double t, string pictureFile, double seconds)
    {
        if (Tracks[under.Track].Locked || t < under.Start - Eps || t > under.End + Eps) return null;
        Split(Clips.Where(c => !Tracks[c.Track].Locked && c.Start < t - Eps && c.End > t + Eps).ToList(), t);
        foreach (var c in Clips.Where(c => !Tracks[c.Track].Locked && c.Start >= t - Eps)) c.Start += seconds;
        Markers = Markers.Select(m => m >= t ? m + seconds : m).ToList();
        var still = new TClip
        {
            Track = under.Track, File = pictureFile, Kind = ClipKind.Image, Label = "Freeze frame", Start = t, In = 0, Out = seconds,
            X = under.X, Y = under.Y, Scale = under.Scale, Rotation = under.Rotation, Opacity = under.Opacity,
            CropL = under.CropL, CropT = under.CropT, CropR = under.CropR, CropB = under.CropB,
        };
        Clips.Add(still);
        return still;
    }

    /// <summary>Adds a marker at a time, or removes one already within <paramref name="near"/> seconds. True when added.</summary>
    public bool ToggleMarker(double t, double near)
    {
        var hit = Markers.Where(m => Math.Abs(m - t) <= near).OrderBy(m => Math.Abs(m - t)).Cast<double?>().FirstOrDefault();
        if (hit != null) { Markers.Remove(hit.Value); return false; }
        Markers.Add(t);
        Markers.Sort();
        return true;
    }

    /* ---- undo / redo -------------------------------------------------------- */

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    private sealed class State
    {
        public List<TTrack> Tracks { get; set; } = new();
        public List<TClip> Clips { get; set; } = new();
        public double Speed { get; set; }
        public double Volume { get; set; }
        public CropBox? Crop { get; set; }
        public double MarkIn { get; set; }
        public double MarkOut { get; set; }
        public List<double> Markers { get; set; } = new();
        public string Aspect { get; set; } = "";
        public FrameBackground Background { get; set; }
    }

    private string Save() => JsonSerializer.Serialize(new State
    {
        Tracks = Tracks, Clips = Clips, Speed = Speed, Volume = Volume, Crop = Crop, MarkIn = MarkIn, MarkOut = MarkOut,
        Markers = Markers, Aspect = Aspect, Background = Background,
    });

    private void Load(string json)
    {
        var s = JsonSerializer.Deserialize<State>(json)!;
        Tracks = s.Tracks ?? new(); Clips = s.Clips ?? new(); Speed = s.Speed; Volume = s.Volume; Crop = s.Crop;
        MarkIn = s.MarkIn; MarkOut = s.MarkOut;
        Markers = s.Markers ?? new(); Aspect = s.Aspect ?? ""; Background = s.Background;
        Repair();
    }

    /// <summary>
    /// Whatever was read back (an edit kept with a clip, an undo step) is made
    /// safe to use: clips on tracks that aren't there, or with no length or
    /// broken numbers, are dropped; everything else is kept within bounds.
    /// Something odd in a saved edit can then never stop the editor drawing.
    /// </summary>
    private void Repair()
    {
        Tracks.RemoveAll(t => t == null);
        foreach (var t in Tracks)
        {
            t.Name ??= ""; t.Source ??= ""; t.Title ??= "";
            t.Volume = double.IsFinite(t.Volume) ? Math.Clamp(t.Volume, 0, 2) : 1;
        }
        Clips.RemoveAll(c => c == null || c.Track < 0 || c.Track >= Tracks.Count ||
                             !double.IsFinite(c.Start) || !double.IsFinite(c.In) || !double.IsFinite(c.Out) || c.Out - c.In <= 1e-6);
        foreach (var c in Clips)
        {
            c.File ??= ""; c.Label ??= "";
            if (c.IsText) c.Text ??= new TextStyle();
            c.Tidy();
            c.Start = Math.Max(0, c.Start);
            ClampFades(c);
        }
        Markers.RemoveAll(m => !double.IsFinite(m) || m < 0);
        if (!double.IsFinite(Speed) || Speed <= 0) Speed = 1;
        if (!double.IsFinite(Volume)) Volume = 1;
        if (!double.IsFinite(MarkIn) || !double.IsFinite(MarkOut)) MarkIn = MarkOut = 0;
        Aspect = Aspects.Contains(Aspect) ? Aspect : "";
    }

    /// <summary>The whole edit as text, to keep it with the clip.</summary>
    public string ToJson() => Save();

    public static EditTimeline? FromJson(string json)
    {
        try
        {
            var tl = new EditTimeline();
            tl.Load(json);
            return tl.Tracks.Count > 0 ? tl : null;
        }
        catch { return null; }
    }

    /// <summary>Call before a change, so it can be undone.</summary>
    public void Remember()
    {
        _undo.Push(Save());
        _redo.Clear();
        if (_undo.Count > 200)
        {
            var keep = _undo.Take(150).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
    }

    /// <summary>The undo and redo history (newest first), kept with the clip so undo works after reopening.</summary>
    public (List<string> Undo, List<string> Redo) History() => (_undo.Take(60).ToList(), _redo.Take(60).ToList());

    public void RestoreHistory(IEnumerable<string>? undo, IEnumerable<string>? redo)
    {
        _undo.Clear();
        _redo.Clear();
        if (undo != null) foreach (var u in undo.Reverse()) _undo.Push(u);
        if (redo != null) foreach (var r in redo.Reverse()) _redo.Push(r);
    }

    /// <summary>Takes back the last Remember (nothing changed after all).</summary>
    public void Forget() { if (_undo.Count > 0) _undo.Pop(); }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Save());
        Load(_undo.Pop());
        Touch();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Save());
        Load(_redo.Pop());
        Touch();
        return true;
    }
}
