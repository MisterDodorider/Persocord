using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Plays a timeline as it is, live: every edit shows at once. The sound is
/// one mixer for the whole timeline (TimelineAudio) through one output, and
/// that output's own position is the clock: sample-accurate on every track,
/// wherever you click. Each video track is a silent picture layer that
/// follows the clock, placed on the frame (a canvas the size of the saved
/// video) exactly as it will be saved: moved, resized, turned, cropped and
/// see-through as set (LayerBox). Fades are opacity; layers under one that
/// covers the whole frame aren't decoded. Still pictures are shown as they
/// are, with nothing to decode.
/// </summary>
public sealed class LivePlayer : IDisposable
{
    private sealed class Slot
    {
        public FramePlayer? Player;
        public string? File;
        public Image? Image;
        /// <summary>The placement last applied (so an unchanged one isn't redone 66 times a second).</summary>
        public string? Placed;
    }

    private readonly Canvas _layers;
    // The frame's size: read from the canvas each time (a new frame shape resizes it).
    private double _frameW => Math.Max(2, _layers.Width);
    private double _frameH => Math.Max(2, _layers.Height);
    /// <summary>The blurred background (a blurred, dimmed copy of the bottom picture, filling the frame).</summary>
    private readonly Image _blur = new() { Stretch = Stretch.UniformToFill, Opacity = 0.6, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Func<string, string?> _proxyOf;
    private readonly Func<string, MediaInfo?> _infoOf;
    private readonly Dictionary<int, Slot> _video = new();
    private readonly HashSet<Slot> _active = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(15) };
    // At least as long as the sound in flight to the speakers (150 ms): by the time
    // the new sound starts, only silence is ahead of it, so nothing stale is heard.
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(170) };
    private readonly TimelineAudio _sound;
    private readonly Stopwatch _clock = new();   // only if there's no sound output
    private IWavePlayer? _out;
    private EditTimeline _tl;
    private double _anchor;                      // timeline time when the output last started
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public double Position
    {
        get => _pos;
        private set { _pos = value; _posTicks = Stopwatch.GetTimestamp(); }
    }
    private double _pos;
    private long _posTicks;
    private volatile bool _flowing;      // the sound is running, so the position moves on between ticks

    /// <summary>
    /// The timeline moment heard right now, from any thread: the last position
    /// plus the time since, while the sound runs (the picture layers follow it).
    /// </summary>
    public double Now()
    {
        double p = _pos;
        long at = _posTicks;
        return _flowing ? p + Math.Min(0.1, (Stopwatch.GetTimestamp() - at) / (double)Stopwatch.Frequency) : p;
    }
    public event Action? Ended;
    public event Action? Moved;

    /// <param name="layers">The frame: a canvas the size of the saved video (shown scaled to fit).</param>
    /// <param name="infoOf">Size and length of a file (the clips' own, and their preview copies).</param>
    public LivePlayer(Canvas layers, EditTimeline tl, Func<string, string?> proxyOf, Func<string, MediaInfo?> infoOf,
                      Func<string, int, string?> pcmOf)
    {
        _layers = layers;
        _blur.Effect = new System.Windows.Media.Effects.BlurEffect { RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
        Panel.SetZIndex(_blur, -1);
        _layers.Children.Add(_blur);
        _tl = tl;
        _proxyOf = proxyOf;
        _infoOf = infoOf;
        _sound = new TimelineAudio(pcmOf);
        _sound.Update(tl);
        _tick.Tick += (_, _) => OnTick();
        _settle.Tick += (_, _) => { _settle.Stop(); if (IsPlaying && !_disposed) StartOutput(); };

    }

    public void SetTimeline(EditTimeline tl) { if (_disposed) return; _tl = tl; _sound.Update(tl); Sync(true); }

    /// <summary>After an edit: heard and seen at once.</summary>
    public void Refresh() { if (_disposed) return; _sound.Update(_tl); Sync(true); }

    /// <summary>Only loudness changed (or a clip's sound became ready): the mixer picks it up mid-play.</summary>
    public void ApplyVolumes() { if (_disposed) return; _sound.Update(_tl); }

    /// <summary>Only where pictures sit changed (dragged on the preview, or set in Properties): placed again, nothing restarts.</summary>
    public void Relayout() { if (_disposed) return; Sync(false); }

    /* ---- transport ------------------------------------------------------------ */

    public void Play()
    {
        if (_disposed) return;
        var (from, to) = _tl.Range;
        if (Position < from || Position >= to - 0.05) Position = from;
        IsPlaying = true;
        StartOutput();
        _tick.Start();
    }

    /// <summary>The sound (and the clock) start from the playhead; pictures follow.</summary>
    private void StartOutput()
    {
        Log.Write($"Editor sound: playing from {Position:0.00}s");
        _anchor = Position;
        _sound.Start(Position);
        Sync(true);
        foreach (var s in _active) if (s.Player is { IsPlaying: false } p) p.Play();
        StartDevice();
        _clock.Restart();
        Position = Position;
        _flowing = true;
    }

    /// <summary>
    /// A fresh Windows output for this play, fed by its own thread through
    /// NAudio's standard buffer (as the clip player and Render exact do). It
    /// starts with its first sound already queued, so "played since it
    /// started" is exactly how far the timeline has been heard. Closed on
    /// pause: a paused editor has no output at all, so nothing can repeat.
    /// </summary>
    private void StartDevice()
    {
        StopDevice();
        try
        {
            var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(TimelineAudio.Rate, TimelineAudio.Channels))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = false,
                ReadFully = true,
            };
            var scratch = new Scratch();
            Feed(buffer, 0.2, scratch);   // the first sound, queued before it starts
            var output = SoundOut.Open(buffer, 60);
            var stop = new System.Threading.CancellationTokenSource();
            var feeder = new System.Threading.Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (buffer.BufferedDuration.TotalSeconds < 0.25) Feed(buffer, 0.05, scratch);
                    else System.Threading.Thread.Sleep(10);
                }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.AboveNormal, Name = "Editor sound" };
            _out = output;
            _feedStop = stop;
            feeder.Start();
            output.Play();
        }
        catch (Exception ex)
        {
            Log.Error("editor sound output", ex);
            StopDevice();
        }
    }

    /// <summary>Mixes the next slice of the timeline into the output's buffer.</summary>
    /// <summary>
    /// Buffers for one output's feeding, reused slice after slice (new ones every
    /// slice made garbage 20 times a second). Each output has its own, so a
    /// feeder still finishing as the next play starts can't touch the new one's.
    /// </summary>
    private sealed class Scratch { public float[] Samples = Array.Empty<float>(); public byte[] Bytes = Array.Empty<byte>(); }

    private void Feed(BufferedWaveProvider buffer, double seconds, Scratch s)
    {
        int frames = (int)(TimelineAudio.Rate * seconds);
        int count = frames * TimelineAudio.Channels;
        if (s.Samples.Length < count) { s.Samples = new float[count]; s.Bytes = new byte[count * 4]; }
        _sound.Read(s.Samples, 0, count);
        Buffer.BlockCopy(s.Samples, 0, s.Bytes, 0, count * 4);
        buffer.AddSamples(s.Bytes, 0, count * 4);
    }

    private void StopDevice()
    {
        try { _feedStop?.Cancel(); } catch { }
        _feedStop = null;
        var output = _out;
        _out = null;
        try { output?.Stop(); output?.Dispose(); } catch { }
    }

    private System.Threading.CancellationTokenSource? _feedStop;

    public void Pause()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        _tick.Stop();
        _settle.Stop();
        StopOutput();
        foreach (var s in _video.Values) s.Player?.Pause();
        Sync(true);
    }

    private void StopOutput()
    {
        _flowing = false;
        _sound.Stop();
        StopDevice();
        _clock.Stop();
    }

    /// <summary>
    /// A click on the timeline. While playing, the sound hushes and resumes
    /// from there once the mouse settles, so dragging never stutters.
    /// </summary>
    public void Seek(double t)
    {
        if (_disposed) return;
        Position = Math.Max(0, t);
        if (IsPlaying)
        {
            StopOutput();
            _settle.Stop();
            _settle.Start();
            Sync(false);
            return;
        }
        Sync(true);
    }

    private bool _tickFailed;

    private void OnTick()
    {
        // Runs 66 times a second: a problem is logged once and playback stops, never repeated.
        try { Tick(); }
        catch (Exception ex)
        {
            if (!_tickFailed) { _tickFailed = true; Log.Error("editor playback", ex); }
            try { Pause(); Ended?.Invoke(); } catch { }
        }
    }

    private void Tick()
    {
        if (_disposed || _settle.IsEnabled) return;   // waiting for the mouse to settle
        // The heard position: where this play started, plus what the output has played since.
        var output = _out;
        if (output != null && output.PlaybackState == PlaybackState.Playing)
            Position = _anchor + SoundOut.PlayedSeconds(output);
        else if (_clock.IsRunning)
            Position = _anchor + _clock.Elapsed.TotalSeconds;
        var (_, to) = _tl.Range;
        if (Position >= to)
        {
            Position = to;
            Pause();
            Ended?.Invoke();
            return;
        }
        Sync(false);
        Moved?.Invoke();
    }

    /* ---- pictures ------------------------------------------------------------- */

    /// <summary>Points every picture layer at its clip and moment. Hard: re-seek even if close.</summary>
    private void Sync(bool hard)
    {
        if (_disposed) return;
        double t = Position;
        _active.Clear();
        bool covered = false;
        var wanted = new HashSet<int>();
        for (int i = 0; i < _tl.Tracks.Count && !covered; i++)
        {
            var tr = _tl.Tracks[i];
            if (tr.Type != TrackType.Video || !tr.Enabled) continue;
            var clip = _tl.Clips.FirstOrDefault(c => c.Track == i && c.Start <= t && t < c.End);
            if (clip == null) continue;
            var src = _infoOf(clip.File);
            if (src == null || src.Width <= 0 || src.Height <= 0) continue;
            double fade = EditTimeline.FadeAt(clip, t);
            var slot = Get(i);
            var box = LayerBox.Of(clip, _frameW, _frameH, src.Width, src.Height);
            if (clip.IsImage) ShowPicture(slot, clip);
            else Point(slot, clip, t, hard);
            Place(slot, clip, box, src);
            _active.Add(slot);
            slot.Image!.Opacity = fade * clip.Opacity;
            Panel.SetZIndex(slot.Image, 1000 - i);
            wanted.Add(i);
            if (fade >= 0.999 && clip.Opacity >= 0.999 && box.Covers(_frameW, _frameH)) covered = true;
        }
        foreach (var (_, slot) in _video.Where(kv => !wanted.Contains(kv.Key)).ToList()) Park(slot);

        // Blurred background: the bottom picture showing now, filling the frame behind everything.
        if (_tl.Background == FrameBackground.Blur && wanted.Count > 0 && _video[wanted.Max()].Image is { Source: { } bottom })
        {
            if (!ReferenceEquals(_blur.Source, bottom)) _blur.Source = bottom;
            _blur.Width = _frameW;
            _blur.Height = _frameH;
            ((System.Windows.Media.Effects.BlurEffect)_blur.Effect).Radius = Math.Max(8, _frameH * 0.045);
            _blur.Visibility = Visibility.Visible;
        }
        else if (_blur.Visibility != Visibility.Collapsed) { _blur.Visibility = Visibility.Collapsed; _blur.Source = null; }
    }

    private Slot Get(int track)
    {
        if (_video.TryGetValue(track, out var slot)) return slot;
        slot = new Slot { Image = new Image { Stretch = Stretch.Fill, RenderTransformOrigin = new System.Windows.Point(0.5, 0.5) } };
        RenderOptions.SetBitmapScalingMode(slot.Image, BitmapScalingMode.HighQuality);
        _layers.Children.Add(slot.Image);
        _video[track] = slot;
        return slot;
    }

    /// <summary>Puts a layer where its clip sits on the frame (size, place, crop, turn).</summary>
    private static void Place(Slot slot, TClip clip, LayerBox box, MediaInfo src)
    {
        var img = slot.Image!;
        string key = FormattableString.Invariant(
            $"{box.Width:F2}|{box.Height:F2}|{box.CentreX:F2}|{box.CentreY:F2}|{clip.CropL:F4}|{clip.CropT:F4}|{clip.CropR:F4}|{clip.CropB:F4}|{clip.Rotation:F3}");
        if (key == slot.Placed) return;
        slot.Placed = key;
        img.Width = Math.Max(1, box.Width);
        img.Height = Math.Max(1, box.Height);
        Canvas.SetLeft(img, box.CentreX - box.Width / 2);
        Canvas.SetTop(img, box.CentreY - box.Height / 2);
        if (clip.Cropped)
        {
            var g = new RectangleGeometry(new Rect(clip.CropL * box.Width, clip.CropT * box.Height,
                                                   Math.Max(1, box.CropWidth), Math.Max(1, box.CropHeight)));
            g.Freeze();
            img.Clip = g;
        }
        else img.Clip = null;
        if (Math.Abs(clip.Rotation) > 1e-3)
        {
            var r = new RotateTransform(clip.Rotation);
            r.Freeze();
            img.RenderTransform = r;
        }
        else img.RenderTransform = Transform.Identity;
    }

    /// <summary>A still picture on a layer (no decoding: the picture itself, loaded once).</summary>
    private static void ShowPicture(Slot slot, TClip clip)
    {
        if (slot.Player != null)
        {
            slot.Player.Dispose();
            slot.Player = null;
        }
        if (string.Equals(slot.File, clip.File, StringComparison.OrdinalIgnoreCase) && slot.Image!.Source != null) return;
        slot.File = clip.File;
        slot.Image!.Source = Pictures.Get(clip.File);
    }

    private void Point(Slot slot, TClip clip, double t, bool hard)
    {
        var file = _proxyOf(clip.File) ?? clip.File;
        var info = _infoOf(file);
        if (info == null) { Park(slot); return; }
        if (slot.Player == null && string.Equals(slot.File, file, StringComparison.OrdinalIgnoreCase)) slot.File = null;   // was a picture
        double src = clip.SrcAt(t);
        bool running = IsPlaying && !_settle.IsEnabled;
        double clipIn = clip.In, clipStart = clip.Start, speed = clip.Speed;

        if (slot.Player == null || !string.Equals(slot.File, file, StringComparison.OrdinalIgnoreCase))
        {
            slot.Player?.Dispose();
            slot.Player = new FramePlayer(file, info, slot.Image!, 960, 540) { Muted = true };
            slot.File = file;
            slot.Player.Follow = () => clipIn + (Now() - clipStart) * speed;
            slot.Player.Seek(src);
            if (running) slot.Player.Play();
            return;
        }
        // The picture follows the sound's clock, for whichever clip is here now.
        slot.Player.Follow = () => clipIn + (Now() - clipStart) * speed;
        if (slot.Player.Restarting) { }
        else if (running)
        {
            // Playing: a layer catches up by itself (late frames are skipped), so
            // it's only restarted when it's ahead, far behind, or an edit moved
            // what's under the playhead. Restarting it whenever it trailed by a
            // quarter second (the time a decoder takes to start) restarted it
            // over and over: the picture stood still while the sound played.
            double lag = src - slot.Player.Position;
            bool settled = (DateTime.UtcNow - slot.Player.StartedAt).TotalSeconds > 1.5;
            if (!slot.Player.IsPlaying) { if (Math.Abs(lag) > 0.1) slot.Player.Seek(src); slot.Player.Play(); }
            else if (lag < -0.3 || (settled && lag > 2.5) || (hard && settled && Math.Abs(lag) > 0.5)) slot.Player.Seek(src);
        }
        // Paused: re-seeked only when the picture would really change, so an
        // edit that leaves the frame under the playhead alone costs nothing.
        else if (Math.Abs(slot.Player.Position - src) > (hard ? 0.02 : 0.25)) slot.Player.Seek(src);
    }

    private static void Park(Slot slot)
    {
        if (slot.Player?.IsPlaying == true) slot.Player.Pause();
        if (slot.Image != null) slot.Image.Opacity = 0;
    }

    /// <summary>Off for good: nothing can start playing again afterwards.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsPlaying = false;
        _tick.Stop();
        _settle.Stop();
        StopOutput();
        _sound.Dispose();
        foreach (var s in _video.Values)
        {
            s.Player?.Dispose();
            if (s.Image != null) _layers.Children.Remove(s.Image);
        }
        _video.Clear();
        _active.Clear();
        _layers.Children.Remove(_blur);
        _blur.Source = null;
    }
}
