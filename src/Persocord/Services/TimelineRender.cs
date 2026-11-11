using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// Turns a timeline into one FFmpeg command. The picture: a black canvas with
/// each video track laid over it bottom to top (V1, then V2…), every clip at
/// its time; fades change transparency, so a fade over nothing goes to black
/// and a fade over another clip is a crossfade. The sound: each audio track's
/// clips in place, plus a fresh Mix of the enabled tracks.
/// </summary>
public static class TimelineRender
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("0.######", Inv);

    /// <summary>The audio tracks that are heard (enabled, and soloed if any are).</summary>
    public static List<int> HeardAudio(EditTimeline tl) => tl.HeardAudio();

    /* ---- the quick route ----------------------------------------------------- */

    /// <summary>
    /// A timeline that is only the original clip, trimmed and cut, on one
    /// video track with no gaps, fades or overlays: saved by the fast route
    /// (straight on the graphics card). Null when the full render is needed.
    /// </summary>
    public static EditPlan? AsSimplePlan(EditTimeline tl, string primary, MediaInfo info)
    {
        var (from, to) = tl.Range;
        if (to - from < EditTimeline.MinLength) return null;
        // Pictures, anything moved, resized, cropped or see-through, and clips
        // with their own volume need the full render.
        if (tl.Clips.Any(c => c.Track < tl.Tracks.Count && tl.Tracks[c.Track].Enabled &&
                              (c.IsImage || (tl.Tracks[c.Track].Type == TrackType.Video && c.Placed) || Math.Abs(c.Volume - 1) > 0.001 ||
                               Math.Abs(c.Speed - 1) > 0.001)))
            return null;
        // A frame of another shape, or a blurred background, is drawn anew.
        if (tl.Aspect.Length > 0 || tl.Background != FrameBackground.Black) return null;
        var videoTracks = Enumerable.Range(0, tl.Tracks.Count).Where(i => tl.Tracks[i].Type == TrackType.Video).ToList();
        var used = videoTracks.Where(i => tl.Tracks[i].Enabled && tl.Clips.Any(c => c.Track == i)).ToList();
        if (used.Count != 1) return null;

        var pieces = Pieces(tl, used[0], from, to);
        if (pieces.Count == 0 || pieces.Any(p => !string.Equals(p.Clip.File, primary, StringComparison.OrdinalIgnoreCase))) return null;
        if (pieces.Any(p => p.Clip.FadeIn > 0 || p.Clip.FadeOut > 0)) return null;
        // Back to back from the start, in the file's own order.
        if (Math.Abs(pieces[0].At) > 0.01) return null;
        for (int i = 1; i < pieces.Count; i++)
        {
            if (Math.Abs(pieces[i].At - (pieces[i - 1].At + pieces[i - 1].Length)) > 0.01) return null;
            if (pieces[i].SrcIn < pieces[i - 1].SrcOut - 0.01) return null;
        }
        if (Math.Abs(pieces[^1].At + pieces[^1].Length - (to - from)) > 0.01) return null;

        // Every heard sound track must follow the picture exactly (at its recorded loudness).
        var heard = HeardAudio(tl);
        // Several tracks and no Mix (saved with "Separate audio tracks"): the
        // quick route can only keep them all, as they are.
        int audioCount = tl.Tracks.Count(t => t.Type == TrackType.Audio);
        if (info.SeparateTracks.Count == 0 && info.AudioTracks.Count > 1 &&
            (heard.Count != audioCount || Math.Abs(tl.Volume - 1) > 0.001)) return null;
        if (heard.Any(a => Math.Abs(tl.Tracks[a].Volume - 1) > 0.001)) return null;

        // The editor's Unknown track isn't in the file: it's All PC audio minus
        // the app tracks. Heard with every app track (and All PC audio off), the
        // result is simply All PC audio, which the file has.
        var audioTracks = Enumerable.Range(0, tl.Tracks.Count).Where(i => tl.Tracks[i].Type == TrackType.Audio).ToList();
        int unknown = audioTracks.FirstOrDefault(i => IsUnknown(tl.Tracks[i].Source), -1);
        int pcTrack = audioTracks.FirstOrDefault(i => EditTimeline.IsAllPcName(tl.Tracks[i].Source), -1);
        var appTracks = audioTracks.Where(i => i != unknown && EditTimeline.IsAppName(tl.Tracks[i].Source)).ToList();
        var micTracks = audioTracks.Where(i => tl.Tracks[i].Source.Equals("Microphone", StringComparison.OrdinalIgnoreCase)).ToList();
        if (unknown >= 0 && heard.Contains(unknown))
        {
            if (pcTrack < 0 || heard.Contains(pcTrack) || !appTracks.All(heard.Contains)) return null;
            heard = heard.Where(a => a != unknown && !appTracks.Contains(a)).Append(pcTrack).ToList();
        }
        else heard.Remove(unknown);

        foreach (var a in heard)
        {
            var sp = Pieces(tl, a, from, to);
            if (sp.Count != pieces.Count) return null;
            for (int i = 0; i < sp.Count; i++)
                if (Math.Abs(sp[i].At - pieces[i].At) > 0.01 || Math.Abs(sp[i].SrcIn - pieces[i].SrcIn) > 0.01 ||
                    Math.Abs(sp[i].Length - pieces[i].Length) > 0.01 || sp[i].Clip.FadeIn > 0 || sp[i].Clip.FadeOut > 0 ||
                    !string.Equals(sp[i].Clip.File, primary, StringComparison.OrdinalIgnoreCase))
                    return null;
        }

        var plan = new EditPlan
        {
            In = pieces[0].SrcIn,
            Out = pieces[^1].SrcOut,
            Speed = tl.Speed,
            Volume = tl.Volume,
            Crop = tl.Crop is { } c ? new System.Drawing.Rectangle(c.X, c.Y, c.Width, c.Height) : null,
        };
        for (int i = 1; i < pieces.Count; i++)
            if (pieces[i].SrcIn > pieces[i - 1].SrcOut + 0.01)
                plan.Cuts.Add(new TimeRange(pieces[i - 1].SrcOut, pieces[i].SrcIn));

        // Sound tracks switched off. Hearing exactly what the main mix holds
        // (All PC audio, or every app track in its place, plus the mic) changes
        // nothing: every track is kept as recorded.
        bool asRecorded = micTracks.All(heard.Contains) &&
                          (heard.Contains(pcTrack) ? heard.All(a => a == pcTrack || micTracks.Contains(a))
                                                   : appTracks.Count > 0 && appTracks.All(heard.Contains) && unknown < 0 &&
                                                     heard.All(a => appTracks.Contains(a) || micTracks.Contains(a)));
        if (info.SeparateTracks.Count > 0)
        {
            if (!asRecorded)
                foreach (var a in audioTracks.Where(a => a != unknown && !heard.Contains(a)))
                    foreach (var s in tl.Clips.Where(c => c.Track == a).Select(c => c.Stream).Distinct())
                        plan.MutedTracks.Add(s);
            if (heard.Count == 0) plan.Mute = true;
        }
        else if (audioTracks.Count > 0 && heard.Count == 0) plan.Mute = true;
        return plan;
    }

    /// <summary>The editor's Unknown track (sound no other track carries), worked out from the others.</summary>
    public static bool IsUnknown(string source) => source.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);

    /* ---- the full render ------------------------------------------------------- */

    /// <summary>A clip cut to a range. Off: how far into the clip it starts (fades are measured on the whole clip).</summary>
    /// <summary>Len: how long it lasts on the timeline; SrcLength: how much of its file that is (they differ when sped up or slowed down).</summary>
    private sealed record Piece(TClip Clip, double At, double SrcIn, double SrcOut, double Off, double Len)
    {
        public double Length => Len;
        public double SrcLength => SrcOut - SrcIn;
        public bool FadesIn => Clip.FadeIn > 0 && Off < Clip.FadeIn;
        public bool FadesOut => Clip.FadeOut > 0 && Off + Length > Clip.Length - Clip.FadeOut;
    }

    /// <summary>How many seconds of each file's picture end up in the saved range (enabled video tracks).</summary>
    public static Dictionary<string, double> UsedVideoSeconds(EditTimeline tl)
    {
        var (from, to) = tl.Range;
        var used = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int t = 0; t < tl.Tracks.Count; t++)
        {
            if (tl.Tracks[t].Type != TrackType.Video || !tl.Tracks[t].Enabled) continue;
            foreach (var p in Pieces(tl, t, from, to))
                used[p.Clip.File] = (used.TryGetValue(p.Clip.File, out var s) ? s : 0) + p.Length;
        }
        return used;
    }

    /// <summary>A track's clips inside the range, cut to it, placed from the range's start.</summary>
    private static List<Piece> Pieces(EditTimeline tl, int track, double from, double to)
    {
        var list = new List<Piece>();
        foreach (var c in tl.OnTrack(track))
        {
            double s = Math.Max(c.Start, from), e = Math.Min(c.End, to);
            if (e - s < 0.01) continue;
            list.Add(new Piece(c, s - from, c.SrcAt(s), c.SrcAt(e), s - c.Start, e - s));
        }
        return list;
    }

    /// <summary>
    /// The FFmpeg arguments for a timeline. Preview: small and quick, from the
    /// low-resolution copies, with the Mix only. Otherwise: full size, every
    /// sound track, crop and speed applied.
    /// </summary>
    /// <param name="infoOf">Each clip's own size (for clips that are moved, resized, cropped or pictures).</param>
    public static string BuildArgs(EditTimeline tl, MediaInfo canvas, string output, bool preview,
                                   string encoderArgs, Func<string, string> fileFor,
                                   (double From, double To)? range = null, string? audioArgs = null,
                                   Func<string, MediaInfo?>? infoOf = null)
    {
        var (from, to) = range ?? tl.Range;
        double dur = Math.Max(EditTimeline.MinLength, to - from);
        int w = canvas.Width - canvas.Width % 2, h = canvas.Height - canvas.Height % 2;
        if (preview)
        {
            // Small: 960 pixels along its longer side (a tall frame stays tall).
            double s = 960.0 / Math.Max(1, Math.Max(w, h));
            w = (int)Math.Round(w * s); h = (int)Math.Round(h * s);
            w -= w % 2; h -= h % 2;
        }
        double fps = preview ? Math.Min(30, canvas.Fps > 1 ? canvas.Fps : 30) : (canvas.Fps > 1 ? canvas.Fps : 60);

        var inputs = new StringBuilder();
        var g = new List<string>();
        int n = 0;
        string Input(TClip c, Piece p)
        {
            if (c.IsImage && tl.Tracks[c.Track].Type == TrackType.Video)
            {
                // A still picture: repeated for as long as it's on the timeline.
                inputs.Append($"-loop 1 -framerate {F(fps)} -t {F(p.Length + 0.25)} -i \"{c.File}\" ");
                return (n++).ToString(Inv);
            }
            bool picture = tl.Tracks[c.Track].Type == TrackType.Video;
            string file = picture ? fileFor(c.File) : c.File;
            inputs.Append(preview ? "" : "-hwaccel auto ")
                  // AV1 is decoded by a decoder that can start in the middle of any AV1 file.
                  .Append(picture ? VideoDecoders.ArgsFor(infoOf?.Invoke(file)?.VideoCodec ?? infoOf?.Invoke(c.File)?.VideoCodec) : "")
                  // A little extra is read and trimmed exactly in the filter: compressed
                  // sound otherwise stops at the last whole frame, ~20 ms short.
                  .Append($"-ss {F(p.SrcIn)} -t {F(p.SrcLength + 0.25 * p.Clip.Speed)} -i \"{file}\" ");
            return (n++).ToString(Inv);
        }

        // ---- picture
        var videoBottomUp = Enumerable.Range(0, tl.Tracks.Count)
            .Where(i => tl.Tracks[i].Type == TrackType.Video && tl.Tracks[i].Enabled).Reverse().ToList();
        var used = videoBottomUp.Where(t => Pieces(tl, t, from, to).Count > 0).ToList();
        // Pictures, or clips placed anywhere but filling the frame, are laid
        // over the frame as layers, even on a single track (and so is
        // everything when the frame has a blurred background).
        bool blur = tl.Background == FrameBackground.Blur;
        bool plain = !blur && used.All(t => Pieces(tl, t, from, to).All(p => !p.Clip.IsImage && !p.Clip.Placed));
        string current;
        if (used.Count <= 1 && plain)
        {
            // One video track (cuts, gaps, fades to black): the pieces and the
            // black gaps are joined end to end. About 40% quicker than laying
            // each piece over a full-length canvas, which only layers need.
            var parts = new List<string>();
            int gaps = 0;
            double at = 0;
            void Gap(double len)
            {
                if (len < 0.5 / fps) return;
                g.Add($"color=c=black:s={w}x{h}:r={F(fps)}:d={F(len)},format=yuv420p,setsar=1[gap{gaps}]");
                parts.Add($"[gap{gaps++}]");
            }
            foreach (var p in used.Count == 0 ? new List<Piece>() : Pieces(tl, used[0], from, to))
            {
                Gap(p.At - at);
                var k = Input(p.Clip, p);
                var chain = new List<string>
                {
                    Pts(p),
                    $"trim=start={F(p.Off)}:end={F(p.Off + p.Length)}",
                    $"scale={w}:{h}:force_original_aspect_ratio=decrease",
                    $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black",
                    "setsar=1",
                    $"fps={F(fps)}",
                    "format=yuv420p",
                };
                if (p.FadesIn) chain.Add($"fade=t=in:st=0:d={F(p.Clip.FadeIn)}");
                if (p.FadesOut) chain.Add($"fade=t=out:st={F(p.Clip.Length - p.Clip.FadeOut)}:d={F(p.Clip.FadeOut)}");
                chain.Add("setpts=PTS-STARTPTS");
                g.Add($"[{k}:v:0]{string.Join(",", chain)}[v{k}]");
                parts.Add($"[v{k}]");
                at = p.At + p.Length;
            }
            Gap(dur - at);
            if (parts.Count == 0) Gap(dur);
            g.Add(parts.Count == 1 ? $"{parts[0]}null[joined]" : $"{string.Concat(parts)}concat=n={parts.Count}:v=1:a=0[joined]");
            current = "joined";
        }
        else
        {
            // Layers: each piece laid over the ones below, fades as transparency
            // (to black over nothing, a crossfade over another layer).
            g.Add($"color=c=black:s={w}x{h}:r={F(fps)}:d={F(dur)},format=yuva420p[base0]");
            current = "base0";
            int layer = 0;
            if (blur && used.Count > 0)
            {
                // Behind everything: the bottom track's own picture, filling the
                // frame, blurred and dimmed (made small, blurred, then grown: as
                // smooth as a big blur, at a fraction of the work).
                int bw = Math.Max(16, w / 8 - w / 8 % 2), bh = Math.Max(16, h / 8 - h / 8 % 2);
                foreach (var p in Pieces(tl, used[0], from, to))
                {
                    var k = Input(p.Clip, p);
                    var chain = new List<string>
                    {
                        Pts(p),
                        $"trim=start={F(p.Off)}:end={F(p.Off + p.Length)}",
                        $"scale={bw}:{bh}:force_original_aspect_ratio=increase",
                        $"crop={bw}:{bh}",
                        "boxblur=6:2",
                        $"scale={w}:{h}:flags=bilinear",
                        "setsar=1",
                        $"fps={F(fps)}",
                        "format=yuva420p",
                        "lutyuv=y=val*0.6",
                    };
                    if (p.FadesIn) chain.Add($"fade=t=in:st=0:d={F(p.Clip.FadeIn)}:alpha=1");
                    if (p.FadesOut) chain.Add($"fade=t=out:st={F(p.Clip.Length - p.Clip.FadeOut)}:d={F(p.Clip.FadeOut)}:alpha=1");
                    chain.Add($"setpts=PTS-{F(p.Off)}/TB+{F(p.At)}/TB");
                    g.Add($"[{k}:v:0]{string.Join(",", chain)}[bg{k}]");
                    var next = $"base{++layer}";
                    g.Add($"[{current}][bg{k}]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=auto[{next}]");
                    current = next;
                }
            }
            foreach (var t in videoBottomUp)
                foreach (var p in Pieces(tl, t, from, to))
                {
                    var c = p.Clip;
                    var src = infoOf?.Invoke(c.File);
                    bool placed = (c.Placed || c.IsImage) && src is { Width: > 0, Height: > 0 };
                    var k = Input(c, p);
                    // True timestamps (not reset to the first frame): what was at the
                    // piece's start in the file is exactly at its start here.
                    var chain = new List<string>
                    {
                        Pts(p),
                        $"trim=start={F(p.Off)}:end={F(p.Off + p.Length)}",
                    };
                    string at = "0:0";
                    if (placed)
                    {
                        // Where it sits on the frame, exactly as the live preview shows it (LayerBox).
                        var box = LayerBox.Of(c, w, h, src!.Width, src.Height);
                        int cw = Even(box.CropWidth), ch = Even(box.CropHeight);
                        bool turned = Math.Abs(c.Rotation) > 1e-3;
                        if (c.Cropped)
                            chain.Add($"crop=w=iw*{F(1 - c.CropL - c.CropR)}:h=ih*{F(1 - c.CropT - c.CropB)}:x=iw*{F(c.CropL)}:y=ih*{F(c.CropT)}");
                        chain.Add($"scale={cw}:{ch}");
                        chain.Add("setsar=1");
                        chain.Add($"fps={F(fps)}");
                        chain.Add("format=rgba");
                        if (p.FadesIn) chain.Add($"fade=t=in:st=0:d={F(c.FadeIn)}:alpha=1");
                        if (p.FadesOut) chain.Add($"fade=t=out:st={F(c.Length - c.FadeOut)}:d={F(c.FadeOut)}:alpha=1");
                        if (c.Opacity < 0.9999) chain.Add($"colorchannelmixer=aa={F(Math.Clamp(c.Opacity, 0, 1))}");
                        double ow = cw, oh = ch;
                        if (turned)
                        {
                            var (bw, bh) = new LayerBox(0, 0, 0, 0, cw, ch, 0, 0, c.Rotation).Bounds();
                            ow = Even(Math.Ceiling(bw)); oh = Even(Math.Ceiling(bh));
                            chain.Add($"rotate={F(c.Rotation * Math.PI / 180)}:ow={F(ow)}:oh={F(oh)}:c=none:bilinear=1");
                        }
                        at = $"{F(Math.Round(box.CropCentreX - ow / 2))}:{F(Math.Round(box.CropCentreY - oh / 2))}";
                    }
                    else
                    {
                        chain.Add($"scale={w}:{h}:force_original_aspect_ratio=decrease");
                        chain.Add($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black@0");
                        chain.Add($"fps={F(fps)}");
                        chain.Add("format=yuva420p");
                        if (p.FadesIn) chain.Add($"fade=t=in:st=0:d={F(c.FadeIn)}:alpha=1");
                        if (p.FadesOut) chain.Add($"fade=t=out:st={F(c.Length - c.FadeOut)}:d={F(c.FadeOut)}:alpha=1");
                    }
                    chain.Add($"setpts=PTS-{F(p.Off)}/TB+{F(p.At)}/TB");
                    g.Add($"[{k}:v:0]{string.Join(",", chain)}[v{k}]");
                    var next = $"base{++layer}";
                    g.Add($"[{current}][v{k}]overlay=x={at.Split(':')[0]}:y={at.Split(':')[1]}:eof_action=pass:repeatlast=0:format=auto[{next}]");
                    current = next;
                }
        }
        var tail = new List<string>();
        if (!preview && tl.Crop is { } cr)
            tail.Add($"crop={cr.Width - cr.Width % 2}:{cr.Height - cr.Height % 2}:{cr.X}:{cr.Y}");
        if (!preview && Math.Abs(tl.Speed - 1) > 0.001) tail.Add($"setpts=PTS/{F(tl.Speed)}");
        // Graphics card encoders get NV12, their own layout (newer NVIDIA cards
        // refuse the planar kind); the CPU encoder plain 4:2:0.
        tail.Add($"format={PixelFormatFor(encoderArgs)}");
        g.Add($"[{current}]{string.Join(",", tail)}[vout]");

        // ---- sound
        var heard = HeardAudio(tl);
        var tempo = new List<string>();
        if (!preview)
        {
            double sp = tl.Speed;
            while (sp > 2.0 + 1e-9) { tempo.Add("atempo=2.0"); sp /= 2.0; }
            while (sp < 0.5 - 1e-9) { tempo.Add("atempo=0.5"); sp /= 0.5; }
            if (Math.Abs(sp - 1) > 0.001) tempo.Add($"atempo={F(sp)}");
        }
        if (Math.Abs(tl.Volume - 1) > 0.001) tempo.Add($"volume={F(tl.Volume)}");

        var trackOuts = new List<(string Label, string Name)>();
        foreach (var t in heard)
        {
            var parts = new List<string>();
            foreach (var p in Pieces(tl, t, from, to))
            {
                var k = Input(p.Clip, p);
                // True timestamps: resetting to the first decoded frame put sound
                // that starts at a file's beginning 21 ms early (AAC's start-up
                // padding sits before zero). Trimmed exactly by time, and padded
                // with silence if a track starts a little late, so it lines up
                // with the picture exactly (as the editor's live sound does).
                var chain = new List<string>();
                if (Math.Abs(p.Clip.Speed - 1) > 0.001)
                {
                    // Sped up or slowed down, keeping its pitch: then on the timeline's clock.
                    chain.Add("asetpts=PTS-STARTPTS");
                    chain.AddRange(Tempo(p.Clip.Speed));
                    chain.Add("asetpts=N/SR/TB");
                }
                chain.Add($"asetpts=PTS+{F(p.Off)}/TB");
                chain.Add($"atrim=start={F(p.Off)}:end={F(p.Off + p.Length)}");
                if (p.FadesIn) chain.Add($"afade=t=in:st=0:d={F(p.Clip.FadeIn)}");
                if (p.FadesOut) chain.Add($"afade=t=out:st={F(p.Clip.Length - p.Clip.FadeOut)}:d={F(p.Clip.FadeOut)}");
                if (Math.Abs(p.Clip.Volume - 1) > 0.001) chain.Add($"volume={F(p.Clip.Volume)}");
                chain.Add($"asetpts=PTS-{F(p.Off)}/TB");
                chain.Add("aresample=48000:async=1:min_hard_comp=0.001:first_pts=0");
                chain.Add("aformat=channel_layouts=stereo");
                chain.Add($"adelay={(long)Math.Round(p.At * 1000)}:all=1");
                // The delay inserts silence but doesn't move the sound's timestamps;
                // rebuilt from the samples, or the trim that follows cuts the end off.
                chain.Add("asetpts=N/SR/TB");
                g.Add($"[{k}:a:{Math.Max(0, p.Clip.Stream)}]{string.Join(",", chain)}[a{k}]");
                parts.Add($"[a{k}]");
            }
            string label = $"t{t}";
            string body = parts.Count == 0
                ? $"anullsrc=r=48000:cl=stereo,atrim=0:{F(dur)}"
                : (parts.Count == 1 ? $"{parts[0]}anull" : $"{string.Concat(parts)}amix=inputs={parts.Count}:normalize=0:dropout_transition=0") +
                  $",apad,atrim=0:{F(dur)}";
            double trackVolume = tl.Tracks[t].Volume;
            if (Math.Abs(trackVolume - 1) > 0.001) body += $",volume={F(trackVolume)}";
            g.Add($"{body}[{label}]");
            var name = tl.Tracks[t].Source.Length > 0 ? tl.Tracks[t].Source : tl.Tracks[t].Name;
            trackOuts.Add((label, name));
        }

        var args = new StringBuilder("-hide_banner -loglevel error -nostats ");
        args.Append(inputs);
        var maps = new StringBuilder("-map \"[vout]\" ");
        var meta = new StringBuilder();
        if (trackOuts.Count > 0)
        {
            // Each track goes to the Mix, and (when saving) on its own too.
            var mixIns = new StringBuilder();
            for (int i = 0; i < trackOuts.Count; i++)
            {
                var l = trackOuts[i].Label;
                if (preview) { mixIns.Append($"[{l}]"); continue; }
                g.Add($"[{l}]asplit=2[{l}m][{l}o]");
                mixIns.Append($"[{l}m]");
            }
            string mixBody = trackOuts.Count == 1 ? $"{mixIns}anull" : $"{mixIns}amix=inputs={trackOuts.Count}:normalize=0:dropout_transition=0";
            g.Add($"{mixBody}{(tempo.Count > 0 ? "," + string.Join(",", tempo) : "")}[mix]");
            maps.Append("-map \"[mix]\" ");
            meta.Append("-metadata:s:a:0 title=Mix -metadata:s:a:0 handler_name=Mix -disposition:a:0 default ");
            if (!preview)
                for (int i = 0; i < trackOuts.Count; i++)
                {
                    var l = trackOuts[i].Label;
                    var outLabel = $"{l}f";
                    g.Add($"[{l}o]{(tempo.Count > 0 ? string.Join(",", tempo) : "anull")}[{outLabel}]");
                    maps.Append($"-map \"[{outLabel}]\" ");
                    var nm = trackOuts[i].Name.Replace("\"", "'");
                    meta.Append($"-metadata:s:a:{i + 1} title=\"{nm}\" -metadata:s:a:{i + 1} handler_name=\"{nm}\" -disposition:a:{i + 1} 0 ");
                }
        }

        args.Append($"-filter_complex \"{string.Join(";", g)}\" ");
        args.Append(maps);
        args.Append(encoderArgs);
        if (trackOuts.Count > 0) args.Append(audioArgs ?? (preview ? "-c:a aac -b:a 128k " : "-c:a aac -b:a 160k -b:a:0 192k ")).Append(meta);
        else args.Append("-an ");
        args.Append($"-t {F(preview ? dur : dur / Math.Max(0.01, tl.Speed))} ");
        if (output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) args.Append("-movflags +faststart ");
        args.Append($"-progress pipe:1 -y \"{output}\"");
        return args.ToString();
    }

    /// <summary>A piece's frames on the timeline's clock, from where it starts in its clip (sped up or slowed down as set).</summary>
    private static string Pts(Piece p) =>
        Math.Abs(p.Clip.Speed - 1) > 0.001 && !p.Clip.IsImage
            ? $"setpts=PTS/{F(p.Clip.Speed)}+{F(p.Off)}/TB"
            : $"setpts=PTS+{F(p.Off)}/TB";

    /// <summary>atempo steps for a speed (each step can only do 0.5× to 2×).</summary>
    private static List<string> Tempo(double speed)
    {
        var list = new List<string>();
        double sp = speed;
        while (sp > 2.0 + 1e-9) { list.Add("atempo=2.0"); sp /= 2.0; }
        while (sp < 0.5 - 1e-9) { list.Add("atempo=0.5"); sp /= 0.5; }
        if (Math.Abs(sp - 1) > 0.001) list.Add($"atempo={F(sp)}");
        return list;
    }

    /// <summary>A size rounded to an even number of pixels (at least 2), as video encoders need.</summary>
    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /* ---- low-resolution copies for the preview ---------------------------- */

    private static readonly Dictionary<string, Task<string?>> Proxies = new(StringComparer.OrdinalIgnoreCase);
    private static CancellationTokenSource _proxyStop = new();

    /// <summary>
    /// Stops any preview copies being made (before a save: they compete with it
    /// for the PC, and hold the original open). They're made again when needed.
    /// </summary>
    public static void StopBackgroundCopies()
    {
        var old = Interlocked.Exchange(ref _proxyStop, new CancellationTokenSource());
        try { old.Cancel(); } catch { }
    }

    /// <summary>
    /// A small copy of a file (540p, 30 fps, every sound track), made once and
    /// kept while Persocord runs, so preview renders are quick.
    /// </summary>
    public static Task<string?> ProxyAsync(string file)
    {
        // Remembered by the file as it is now (its time and size), not just its
        // name: a clip trimmed and saved over its original, or a new clip that
        // took a deleted one's name, otherwise showed the old picture.
        string key = Identity(file);
        lock (Proxies)
        {
            if (Proxies.TryGetValue(key, out var t) && !(t.IsCompleted && t.Result == null)) return t;
            // Older versions of the same path aren't needed any more.
            foreach (var old in Proxies.Keys.Where(k => k.StartsWith(file + "|", StringComparison.OrdinalIgnoreCase) && k != key).ToList())
                Proxies.Remove(old);
            return Proxies[key] = Task.Run(() => MakeProxy(file));
        }
    }

    /// <summary>A file as it is right now: its path, when it was last written and its size.</summary>
    public static string Identity(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            return $"{file}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        }
        catch { return file + "|0|0"; }
    }

    private static string? MakeProxy(string file)
    {
        try
        {
            // Picture only: the sound is always read from the clip itself (the
            // live preview decodes it once, the exact preview reads it directly),
            // so it isn't re-encoded into every copy for nothing.
            var name = $"proxy540v_{Paths.StableHash(Identity(file).ToLowerInvariant())}.mp4";
            var output = Path.Combine(Paths.Temp, name);
            if (File.Exists(output) && new FileInfo(output).Length > 0) { TempJanitor.Touch(output); return output; }
            // On an NVIDIA card the whole copy stays on it: decoded, shrunk and
            // encoded there, never passing through the CPU (several times
            // quicker). Otherwise decoded on the card and finished on the CPU.
            var hw = ClipEditor.BestEncoderAsync().GetAwaiter().GetResult();
            var info = ClipEditor.ProbeAsync(file).GetAwaiter().GetResult();
            var stop = _proxyStop.Token;
            bool ok = false;
            if (hw == EncoderKind.Nvenc && info != null && info.Height > 0)
            {
                int pw = (int)Math.Round(540.0 * info.Width / info.Height);
                pw -= pw % 2;
                var gpu = $"-hide_banner -loglevel error -hwaccel d3d11va -hwaccel_output_format d3d11 -i \"{file}\" -map 0:v:0 -an " +
                          $"-vf \"scale_d3d11=width={pw}:height=540:format=nv12,fps=30\" " +
                          "-c:v h264_nvenc -preset p1 -tune hq -rc vbr -cq 20 -b:v 0 -bf 0 -g 15 " +
                          $"-y \"{output}.part.mp4\"";
                ok = Run(gpu, stop, null, 0, background: true);
                if (stop.IsCancellationRequested) return null;
                if (!ok) Log.Write("Preview copy on the graphics card didn't work; using the CPU route.");
            }
            if (!ok)
            {
                var enc = PreviewEncoder(20);
                var args = $"-hide_banner -loglevel error -hwaccel auto -i \"{file}\" -map 0:v:0 -an " +
                           $"-vf \"scale=-2:540:flags=bicubic,fps=30,format={PixelFormatFor(enc)}\" " + enc + "-g 15 " +
                           $"-y \"{output}.part.mp4\"";
                ok = Run(args, stop, null, 0, background: true);
            }
            if (!ok || stop.IsCancellationRequested) return null;
            File.Move(output + ".part.mp4", output, true);
            return output;
        }
        catch (Exception ex) { Log.Error("preview copy", ex); return null; }
    }

    /// <summary>The picture format an encoder takes best: NV12 for a graphics card's, 4:2:0 planar for the CPU's.</summary>
    public static string PixelFormatFor(string encoderArgs) =>
        encoderArgs.Contains("_nvenc") || encoderArgs.Contains("_amf") || encoderArgs.Contains("_qsv") ? "nv12" : "yuv420p";

    /// <summary>
    /// The encoder for preview work: the graphics card's when there is one
    /// (quicker, and it leaves the CPU free), otherwise the CPU's fastest.
    /// </summary>
    public static string PreviewEncoder(int quality)
    {
        var hw = ClipEditor.BestEncoderAsync().GetAwaiter().GetResult();
        return hw == EncoderKind.X264
            ? $"-c:v libx264 -preset ultrafast -crf {quality} -pix_fmt yuv420p "
            : ClipEditor.EncoderArgs(hw, quality);
    }

    /// <summary>Runs FFmpeg (high priority: this is waited on), reporting progress 0-1.</summary>
    public static bool Run(string args, CancellationToken ct, IProgress<double>? progress, double seconds) =>
        Run(args, ct, progress, seconds, background: false);

    /// <param name="background">Work nobody is waiting on: runs below normal priority, so it never slows a game or a save.</param>
    public static bool Run(string args, CancellationToken ct, IProgress<double>? progress, double seconds, bool background)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = Paths.FFmpeg, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return false;
            try { p.PriorityClass = background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.AboveNormal; } catch { }
            var err = p.StandardError.ReadToEndAsync();
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
                if (progress != null && seconds > 0 && line.StartsWith("out_time_us=") &&
                    long.TryParse(line.AsSpan(12), out var us))
                    progress.Report(Math.Clamp(us / 1e6 / seconds, 0, 1));
            p.WaitForExit();
            if (p.ExitCode != 0 && !ct.IsCancellationRequested) Log.Write("timeline render: " + err.Result.Trim());
            return p.ExitCode == 0 && !ct.IsCancellationRequested;
        }
        catch (Exception ex) { Log.Error("timeline render", ex); return false; }
    }
}
