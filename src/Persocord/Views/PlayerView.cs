using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Persocord.Models;
using Persocord.Services;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Persocord.Views;

/// <summary>
/// Persocord's player, shown inside the clip list window (so a screen share
/// of that window keeps showing it). VLC's engine decodes on the graphics
/// card: full quality and frame rate, AV1 and HEVC included.
///
/// Keys: Space play/pause, ← → 10 s, Shift+← → 2 s, , . one frame,
/// ↑ ↓ volume, F or double-click fullscreen, Esc leaves fullscreen or goes back.
/// </summary>
public class PlayerView : UserControl
{
    /* ---- VLC's engine, loaded once (in the background at startup) ------------ */

    private static LibVLC? _vlc;
    private static readonly object EngineLock = new();

    public static bool EnsureEngine()
    {
        lock (EngineLock)
        {
            if (_vlc != null) return true;
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var dir = new[]
                    {
                        Path.Combine(baseDir, "libvlc", "win-x64"),
                        Path.Combine(baseDir, "libvlc", "x64"),
                        baseDir,
                    }
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "libvlc.dll")));
                if (dir == null)
                {
                    Log.Write($"Player: libvlc.dll not found under {baseDir} (looked in libvlc\\win-x64 and libvlc\\x64)");
                    return false;
                }
                Core.Initialize(dir);
                _vlc = new LibVLC(EngineArgs);
                _engineBorn = DateTime.UtcNow;
                _usesAtBirth = Health.VlcOpenedTotal;
                Log.Write("Player: VLC engine loaded from " + dir);
                StartFreshening();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("player engine", ex);
                return false;
            }
        }
    }

    // Only the normal Windows video outputs: when VLC had nowhere to
    // draw, it could pick a novelty one (colour ASCII art) and
    // open a window of its own for each clip.
    // A little more read ahead (default 300 ms): sound restarts smoothly after a seek.
    private static readonly string[] EngineArgs =
        { "--no-video-title-show", "--no-osd", "--vout=direct3d11,direct3d9,glwin32,wingdi", "--file-caching=600" };

    /// <summary>VLC's engine, once loaded (null before).</summary>
    public static LibVLC? Engine => _vlc;

    /* ---- a fresh engine after long use ---------------------------------------------- */

    // Every hover preview and clip opened is a VLC player made on the one
    // engine; after a day of that, VLC's own bookkeeping makes seeking slower.
    // So after hours of use (or hundreds of players), at a moment when nothing
    // plays, the engine is swapped for a fresh one. It's loaded off to the side
    // first, and swapped on the window's thread, where every player is made,
    // so nothing can be using the old one when it goes.
    private static DateTime _engineBorn;
    private static long _usesAtBirth;
    private static System.Windows.Threading.DispatcherTimer? _freshen;
    private static int _freshening;

    private static void StartFreshening() => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_freshen != null) return;
        _freshen = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(20) };
        _freshen.Tick += async (_, _) => await FreshenAsync();
        _freshen.Start();
    }));

    private static async Task FreshenAsync()
    {
        if (_vlc == null || Health.VlcPlayers != 0) return;
        bool worn = DateTime.UtcNow - _engineBorn > TimeSpan.FromHours(8) || Health.VlcOpenedTotal - _usesAtBirth > 600;
        if (!worn || System.Threading.Interlocked.Exchange(ref _freshening, 1) == 1) return;
        try
        {
            var fresh = await Task.Run(() =>
            {
                try { return new LibVLC(EngineArgs); }
                catch (Exception ex) { Log.Error("player engine renew", ex); return null; }
            });
            if (fresh == null) return;
            // Back on the window's thread: only swapped if nothing started meanwhile.
            if (Health.VlcPlayers != 0) { _ = Task.Run(() => { try { fresh.Dispose(); } catch { } }); return; }
            LibVLC? old;
            lock (EngineLock)
            {
                old = _vlc;
                _vlc = fresh;
                _engineBorn = DateTime.UtcNow;
                _usesAtBirth = Health.VlcOpenedTotal;
            }
            Log.Write("Player: VLC's engine renewed after long use");
            _ = Task.Run(async () => { await Task.Delay(5000); try { old?.Dispose(); } catch { } });
        }
        finally { System.Threading.Interlocked.Exchange(ref _freshening, 0); }
    }

    /// <summary>Loads the engine off the UI thread, so the first open is instant.</summary>
    public static Task<bool> WarmUpAsync() => Warm.Value;

    // One load, shared by everyone who needs it. Getting it is instant: the
    // lock the loading itself holds (for seconds, the first time) is never
    // waited on here, which is what froze the window when a clip was opened
    // or previewed while VLC was still loading.
    private static readonly Lazy<Task<bool>> Warm =
        new(() => Task.Run(EnsureEngine), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /* ---- state ------------------------------------------------------------------ */

    private readonly AppController _app;
    private readonly List<ClipRecord> _clips;
    private readonly Action _back;
    private int _index;

    private readonly VideoView _view = new();
    private VlcPlayer? _mp;
    private VlcMedia? _media;
    private Window? _window;

    private readonly Grid _overlay = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    // Soft shades along the top and bottom, so the controls read over any clip.
    private readonly Border _topShade = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, Height = 120 };
    private readonly Border _bottomShade = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, Height = 255 };
    private readonly Grid _top = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 12, 14, 0) };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _edit = new() { ToolTip = "Open this clip in the editor  ( E )" };
    private readonly Grid _bar = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _play = new();
    // The seek bar: a real slider (for the value and the mouse), drawn as a thin line.
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, Opacity = 0 };
    private readonly Border _seekHost = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        Padding = new Thickness(0, 7, 0, 7),
        Cursor = Cursors.Hand,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Grid _seekLine = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _seekRest = new();
    private readonly Border _seekAhead = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly Border _seekFill = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly Canvas _knobLayer = new() { IsHitTestVisible = false, ClipToBounds = false };
    private readonly System.Windows.Shapes.Ellipse _knob = new() { Width = 13, Height = 13, Visibility = Visibility.Collapsed };
    private bool _seekHot;
    // The time under the mouse, in a small bubble above the seek bar.
    private readonly Canvas _tipLayer = new() { IsHitTestVisible = false, ClipToBounds = false };
    private readonly Border _tip = new() { Padding = new Thickness(8, 3, 8, 3), Visibility = Visibility.Collapsed };
    private readonly TextBlock _tipText = new() { FontSize = 11.5 };
    // The time: beside the seek bar (glass), or next to the volume (minimal).
    private readonly TextBlock _timeNow = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinWidth = 44, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _timeTotal = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinWidth = 44 };
    private readonly TextBlock _timeInline = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _speedBtn = new() { ToolTip = "Speed" };
    private readonly Popup _speedPopup = new() { StaysOpen = false, Placement = PlacementMode.Top, AllowsTransparency = true, VerticalOffset = -6 };
    private readonly StackPanel _speedList = new() { Margin = new Thickness(6) };
    private int _speedIndex = 3;
    private readonly Button _soundBtn = new() { Visibility = Visibility.Collapsed, ToolTip = "Choose which sound tracks play" };
    private readonly Popup _soundPopup = new() { StaysOpen = false, Placement = PlacementMode.Top, AllowsTransparency = true, VerticalOffset = -6 };
    private readonly StackPanel _soundList = new() { Margin = new Thickness(14, 12, 14, 12), MinWidth = 190 };
    private readonly TextBlock _soundNote = new() { FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _busy = new() { Text = "Mixing sound…", FontSize = 12.5, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new()
    {
        // Up to 200%: past 100 the sound is boosted, shown in red.
        Minimum = 0, Maximum = 200, Width = 112, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true,
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),   // clickable in the see-through window
    };
    private readonly Button _volumeBtn = new() { ToolTip = "Mute  ( M )" };
    private double _volumeBefore = 80;
    private readonly Button _fullBtn = new() { ToolTip = "Fullscreen  ( F )" };
    private readonly Button _prev = new() { Width = 44, Height = 44, ToolTip = "Previous clip  ( Page Up )" };
    private readonly Button _next = new() { Width = 44, Height = 44, ToolTip = "Next clip  ( Page Down )" };
    // The pieces drawn behind controls: glass in the glass style, nothing (or a dark panel) in the minimal one.
    private readonly List<Surface> _surfaces = new();

    // Shows the controls for a moment after a key press, and hides the pointer when it rests on the video.
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.5) };
    // A moment's grace before the controls hide when the pointer leaves them.
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _inBottom, _inTop, _bottomShown = true, _topShown = true;
    private DateTime _flashUntil = DateTime.MinValue;
    private bool _dragging, _fullscreen, _tracksFilled, _closed;
    private List<string>? _fileTrackNames;
    private long _length;
    private DateTime _lastDragSeek;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;

    // Sound tracks: stream order in the file, their names, and VLC's ids.
    private readonly List<(int Stream, string Name, int VlcId, CheckBox Box)> _trackRows = new();
    private int _mixVlcId = -1;
    private int _mixStamp;

    // What's loaded: the clip itself, or a copy with the chosen tracks mixed.
    private string _originalPath = "";
    /// <summary>The clip file being played.</summary>
    public string OriginalPath => _originalPath;
    private string _currentPath = "";
    private string? _mixFile;
    // Applied once the reloaded file starts playing.
    private long? _pendingTime;
    private bool _pendingPause;
    private int? _pendingTrack;
    private bool _reloading;
    // Hides "Loading sound…" a moment after playback resumes (and never leaves it up for long).
    private readonly System.Windows.Threading.DispatcherTimer _busyGuard = new() { Interval = TimeSpan.FromSeconds(8) };

    // The watchdog: playing, but the time hasn't moved for 1.5 s (VLC's sound
    // stuck after a seek). Playback is restarted from that spot, so a stuck
    // state fixes itself instead of looping.
    private readonly System.Windows.Threading.DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private long _lastTimeMs = -1;
    private DateTime _lastTimeAt = DateTime.UtcNow;
    private DateTime _lastRescue = DateTime.MinValue;

    private void Watch()
    {
        if (_mp == null || _dragging || _reloading || !_mp.IsPlaying) { _lastTimeAt = DateTime.UtcNow; return; }
        if ((DateTime.UtcNow - _lastTimeAt).TotalSeconds < 1.5) return;
        if (_length > 0 && _lastTimeMs > _length - 800) return;          // just reaching the end
        if ((DateTime.UtcNow - _lastRescue).TotalSeconds < 5) return;     // one rescue at a time
        _lastRescue = DateTime.UtcNow;
        _lastTimeAt = DateTime.UtcNow;
        Log.Write($"Player: playback stalled at {_lastTimeMs / 1000.0:0.00}s; restarting it there.");
        int? track = _currentPath == _originalPath && _mp.AudioTrack >= 0 ? _mp.AudioTrack : null;
        Reload(_currentPath, track);
    }

    /// <summary>"Loading sound…" for a moment, while VLC settles on the new sound.</summary>
    private void FlashBusy()
    {
        _busy.Text = "Loading sound…";
        _busy.Visibility = Visibility.Visible;
        _busyGuard.Stop();
        _busyGuard.Interval = TimeSpan.FromMilliseconds(600);
        _busyGuard.Start();
    }

    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

    public PlayerView(AppController app, IReadOnlyList<ClipRecord> clips, int index, Action back)
    {
        _app = app;
        _clips = clips.ToList();
        _back = back;
        _index = Math.Clamp(index, 0, Math.Max(0, _clips.Count - 1));
        Background = Brushes.Black;
        Focusable = true;

        BuildControls();
        _view.Content = _overlay;
        Content = _view;

        _hide.Tick += (_, _) => HideControls();
        ApplyLook();
        // Live, while Customization is open.
        _lookChanged = () => Dispatcher.BeginInvoke(new Action(() => { if (!_closed) ApplyLook(); }));
        LookStore.Changed += _lookChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _window = Window.GetWindow(this);
        if (_window != null) _window.PreviewKeyDown += OnKey;
        // The controls sit in VLC's overlay window, which can hold the keyboard.
        var w = Window.GetWindow(_overlay);
        if (w != null && w != _window) w.PreviewKeyDown += OnKey;
        Focus();

        if (_closed) return;
        _mp = new VlcPlayer(_vlc!) { EnableHardwareDecoding = true };
        Health.VlcOpened();
        _view.MediaPlayer = _mp;
        _mp.Volume = Math.Clamp(_app.Settings.Current.PlayerVolume, 0, 200);
        _volume.Value = _mp.Volume;
        ShowVolume();

        _watch.Tick += (_, _) => Watch();
        _watch.Start();
        _busyGuard.Tick += (_, _) =>
        {
            _busyGuard.Stop();
            _busyGuard.Interval = TimeSpan.FromSeconds(8);
            if (!_reloading) _busy.Visibility = Visibility.Collapsed;
            else { _reloading = false; _busy.Visibility = Visibility.Collapsed; }
        };
        _mp.TimeChanged += (_, ev) => Dispatcher.InvokeAsync(() => OnTime(ev.Time));
        _mp.LengthChanged += (_, ev) => Dispatcher.InvokeAsync(() => { _length = ev.Length; _seek.Maximum = Math.Max(1, ev.Length); OnTime(_mp?.Time ?? 0); });
        _mp.Playing += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            SetPlay(PauseGlyph);
            if (_mp == null) return;
            // Muted only when every sound track is unticked; this also undoes a
            // mute left behind by an earlier version (VLC's mute is shared by
            // all of Persocord's sound).
            _mp.Mute = _allOff;
            if (_pendingTime is long t) { _pendingTime = null; _mp.Time = t; }
            if (_pendingTrack is int track) { _pendingTrack = null; _mp.SetAudioTrack(track); }
            _mp.SetRate((float)Speeds[_speedIndex]);
            if (_pendingPause) { _pendingPause = false; _mp.SetPause(true); }
            if (_currentPath == _originalPath) FillTracks();
            if (_reloading) { _reloading = false; FlashBusy(); }
            ArmHide();
        });
        _mp.Paused += (_, _) => Dispatcher.InvokeAsync(() => { SetPlay(PlayGlyph); ShowControls(); });
        _mp.EndReached += (_, _) => Dispatcher.InvokeAsync(() => { SetPlay(ReplayGlyph); ShowControls(); });

        Load(_index);
    }

    /// <summary>Stops playback and hands the window back to the clip list.</summary>
    /// <summary>Paused (when the editor opens, so nothing plays behind it).</summary>
    public void Pause()
    {
        try { if (_mp is { IsPlaying: true }) _mp.SetPause(true); } catch { }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        LookStore.Changed -= _lookChanged;
        StopHold();
        _hide.Stop();
        _watch.Stop();
        _settle.Stop();
        _busyGuard.Stop();
        if (_fullscreen) ToggleFullscreen();
        if (_window != null) _window.PreviewKeyDown -= OnKey;

        var s = _app.Settings.Current;
        s.PlayerVolume = (int)Math.Round(_volume.Value);
        _app.Settings.Save(s);

        var mp = _mp;
        var media = _media;
        var mix = _mixFile;
        _mixFile = null;
        _mp = null;
        _media = null;
        _view.MediaPlayer = null;
        _soundPopup.IsOpen = false;
        _speedPopup.IsOpen = false;
        // VLC's controls live in an overlay window of their own; it goes too.
        _view.Content = null;
        try { _view.Dispose(); } catch { }
        // VLC stops cleanly off the UI thread.
        if (mp != null) Health.VlcClosing();
        Task.Run(() =>
        {
            try { mp?.Stop(); mp?.Dispose(); media?.Dispose(); }
            catch (Exception ex) { Log.Error("player close", ex); }
            if (mp != null) Health.VlcClosed();
            if (mix != null) { try { File.Delete(mix); } catch { } }
        });
        _back();
    }

    private void Load(int index)
    {
        if (_clips.Count == 0 || _mp == null) return;
        _seekedPaused = false;
        _index = Math.Clamp(index, 0, _clips.Count - 1);
        var clip = _clips[_index];
        var path = _app.Library.PathOf(clip);
        _title.Text = clip.Title;
        var about = new List<string>();
        if (_clips.Count > 1) about.Add($"{_index + 1} of {_clips.Count}");
        if (!string.IsNullOrWhiteSpace(clip.App)) about.Add(clip.App);
        about.Add(clip.Created.ToString("d MMM yyyy, HH:mm"));
        _subtitle.Text = string.Join("  ·  ", about);

        var old = _media;
        _media = new VlcMedia(_vlc!, new Uri(path));
        _originalPath = _currentPath = path;
        _pendingTime = null; _pendingTrack = null; _pendingPause = false;
        DropMixFile();
        _tracksFilled = false;
        // The tracks' names straight from the file (VLC doesn't always pass
        // them on, which is when they showed as "Track 1, Track 2…").
        _fileTrackNames = null;
        var probing = path;
        _ = Task.Run(async () =>
        {
            var names = (await ClipEditor.ProbeAsync(probing))?.AudioTracks?.ToList();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_originalPath != probing || names == null) return;
                _fileTrackNames = names;
                // Already listed with VLC's names: listed again with the file's.
                if (_tracksFilled && _currentPath == _originalPath &&
                    _trackRows.Any(r => r.Name.StartsWith("Track ", StringComparison.Ordinal)))
                { _tracksFilled = false; FillTracks(); }
            });
        });
        _trackRows.Clear();
        _mixVlcId = -1;
        _soundBtn.Visibility = Visibility.Collapsed;
        SetSpeed(Array.IndexOf(Speeds, 1.0));   // speed isn't remembered
        _mp.Mute = false;
        Expect("opening a clip");
        _mp.Play(_media);
        old?.Dispose();

        _prev.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = _index < _clips.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /* ---- playback ---------------------------------------------------------------- */

    private void TogglePlay()
    {
        if (_mp == null) return;
        if (_mp.State == VLCState.Ended) { Load(_index); return; }
        bool resume = !_mp.IsPlaying;
        _mp.SetPause(!resume);
        // VLC's sound picks up badly (stutters, then races to catch up) after a
        // seek made while paused: a seek right as it resumes starts the sound
        // fresh, exactly where the picture is.
        if (resume) Expect(_seekedPaused ? "resume after a seek" : "resume");
        if (resume && _seekedPaused)
        {
            _seekedPaused = false;
            long at = _mp.Time;
            if (at >= 0) _mp.Time = at;
        }
    }

    /// <summary>The position was changed while paused (see <see cref="TogglePlay"/>).</summary>
    private bool _seekedPaused;

    private void NoteSeek()
    {
        if (_mp != null && !_mp.IsPlaying) _seekedPaused = true;
    }

    private void SeekBy(long ms)
    {
        if (_mp == null) return;
        if (_mp.State == VLCState.Ended) { Load(_index); return; }
        NoteSeek();
        _mp.Time = Math.Clamp(_mp.Time + ms, 0, Math.Max(0, _length - 50));
        ShowControls();
    }

    private void StepFrame(int direction)
    {
        if (_mp == null) return;
        if (_mp.IsPlaying) _mp.SetPause(true);
        _seekedPaused = true;
        if (direction > 0) _mp.NextFrame();
        else
        {
            // VLC only steps forward; one frame back is a seek by a frame's length.
            double fps = _mp.Fps > 1 ? _mp.Fps : 60;
            _mp.Time = Math.Max(0, _mp.Time - (long)Math.Round(1000 / fps));
        }
        ShowControls();
    }

    private long _lastSent = -1;
    private bool _allOff;

    private void SendSeek(long ms)
    {
        if (_mp == null) return;
        _lastSent = ms;
        NoteSeek();
        if (_mp.IsPlaying) Expect("seek", ms);
        _mp.Time = ms;
    }

    /// <summary>
    /// The mouse let go: one last seek only if it moved since the last one
    /// (a click no longer seeks twice). The sound is never muted for a seek:
    /// VLC's mute is shared by all of Persocord's sound and could get stuck.
    /// </summary>
    private void FinishSeek()
    {
        if (_mp == null) return;
        long target = (long)_seek.Value;
        if (_lastSent < 0 || Math.Abs(target - _lastSent) > 40) SendSeek(target);
    }

    // How long VLC takes to answer a press (open, seek, resume): slow ones
    // (over a third of a second) go in the log, so a report shows where waits come from.
    private string? _waitingFor;
    private long _waitStart, _waitTarget = -1;

    private void Expect(string what, long target = -1)
    {
        _waitingFor = what;
        _waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _waitTarget = target;
    }

    private void OnTime(long ms)
    {
        if (_waitingFor != null && (_waitTarget < 0 || Math.Abs(ms - _waitTarget) < 1500))
        {
            double took = System.Diagnostics.Stopwatch.GetElapsedTime(_waitStart).TotalMilliseconds;
            if (took > 350) Log.Write($"Player: {_waitingFor} took {took:0} ms");
            _waitingFor = null;
        }
        _lastTimeMs = ms;
        _lastTimeAt = DateTime.UtcNow;
        if (!_dragging) _seek.Value = Math.Min(ms, _seek.Maximum);
        ShowTime(ms);
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /* ---- sound tracks: tick any combination ------------------------------------- */

    /// <summary>
    /// One tick box per track. "All PC audio" and the per-app tracks exclude
    /// each other (together, the apps would play twice); the mic is separate.
    /// Starts on the usual mix.
    /// </summary>
    private void FillTracks()
    {
        if (_tracksFilled || _mp == null) return;
        var list = _mp.AudioTrackDescription.Where(t => t.Id >= 0).ToList();
        if (list.Count == 0) return;
        _tracksFilled = true;
        _soundList.Children.Clear();
        _trackRows.Clear();
        var head = new TextBlock { Text = "SOUND TRACKS", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _soundList.Children.Add(head);

        for (int i = 0; i < list.Count; i++)
        {
            // The file's own name for the track first (its tracks come in the
            // same order); VLC's otherwise, like "Track 2 - [Discord]".
            string? name = _fileTrackNames != null && _fileTrackNames.Count == list.Count &&
                           !_fileTrackNames[i].StartsWith("Track ", StringComparison.Ordinal)
                ? _fileTrackNames[i] : null;
            if (name == null)
            {
                name = list[i].Name ?? $"Track {i + 1}";
                int a = name.IndexOf('['), b = name.LastIndexOf(']');
                if (a >= 0 && b > a) name = name[(a + 1)..b];
            }
            if (name == "Mix") { _mixVlcId = list[i].Id; continue; }
            var box = new CheckBox { Content = name, Margin = new Thickness(0, 4, 0, 4) };
            box.SetResourceReference(Control.ForegroundProperty, "Ink");
            _trackRows.Add((i, name, list[i].Id, box));
            _soundList.Children.Add(box);
        }
        if (_trackRows.Count < 2) { _soundBtn.Visibility = Visibility.Collapsed; return; }
        _soundList.Children.Add(_soundNote);

        // The usual mix: all PC audio plus the mic (or everything, if unnamed).
        bool anyKnown = _trackRows.Any(r => IsAllPc(r.Name) || IsMic(r.Name));
        foreach (var r in _trackRows)
            r.Box.IsChecked = !anyKnown || IsAllPc(r.Name) || IsMic(r.Name);
        foreach (var r in _trackRows)
        {
            var row = r;
            row.Box.Checked += (_, _) => OnTick(row, true);
            row.Box.Unchecked += (_, _) => OnTick(row, false);
        }
        _soundBtn.Visibility = Visibility.Visible;
        _soundNote.Text = "";
    }

    // "All PC audio (incl. Dragon's Dogma 2)" too: an app that couldn't get its own track is named there.
    private static bool IsAllPc(string n) => n.StartsWith("All PC audio", StringComparison.OrdinalIgnoreCase) || n.Equals("PC audio", StringComparison.OrdinalIgnoreCase);
    private static bool IsMic(string n) => n.StartsWith("Microphone", StringComparison.OrdinalIgnoreCase) || n.Equals("Mic", StringComparison.OrdinalIgnoreCase);

    private bool _ticking;

    private void OnTick((int Stream, string Name, int VlcId, CheckBox Box) row, bool on)
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            if (on)
            {
                // All PC audio already holds every app: never both at once.
                if (IsAllPc(row.Name))
                    foreach (var r in _trackRows.Where(r => !IsAllPc(r.Name) && !IsMic(r.Name))) r.Box.IsChecked = false;
                else if (!IsMic(row.Name))
                    foreach (var r in _trackRows.Where(r => IsAllPc(r.Name))) r.Box.IsChecked = false;
            }
        }
        finally { _ticking = false; }
        _ = ApplyTicksAsync();
    }

    private async Task ApplyTicksAsync()
    {
        if (_mp == null) return;
        var on = _trackRows.Where(r => r.Box.IsChecked == true).ToList();
        int stamp = ++_mixStamp;

        if (on.Count == 0) { _allOff = true; _mp.Mute = true; _soundNote.Text = "Muted"; return; }
        _allOff = false;
        _mp.Mute = false;
        _soundNote.Text = "";

        // The usual mix, or a single track: the clip itself, switched instantly.
        bool usual = _mixVlcId >= 0 && on.Count == 2 && on.Any(r => IsAllPc(r.Name)) && on.Any(r => IsMic(r.Name));
        if (usual || on.Count == 1)
        {
            int id = usual ? _mixVlcId : on[0].VlcId;
            if (_currentPath == _originalPath) { _mp.SetAudioTrack(id); FlashBusy(); }
            else Reload(_originalPath, id);
            DropMixFile();
            return;
        }

        // Any other combination: one complete file with those tracks mixed
        // and the picture copied as it is (about a second for five minutes),
        // then playback carries on from the same moment. A single file keeps
        // VLC in step; adding a second sound file to a playing clip made it
        // stall and jump back and forth.
        var clipPath = _originalPath;
        var file = Path.Combine(Paths.Temp, $"player_mix_{Guid.NewGuid():N}.mkv");
        // Each track lined up with the file's own time before mixing (otherwise
        // AAC's start-up padding put tracks up to 21 ms apart).
        var align = string.Concat(on.Select((r, i) => $"[0:a:{r.Stream}]aresample=48000:async=1:min_hard_comp=0.001:first_pts=0[s{i}];"));
        var inputs = string.Concat(on.Select((r, i) => $"[s{i}]"));
        var args = $"-hide_banner -loglevel error -i \"{clipPath}\" " +
                   $"-filter_complex \"{align}{inputs}amix=inputs={on.Count}:normalize=0:dropout_transition=0[m]\" " +
                   // AAC (small 21 ms blocks): uncompressed sound in MKV is stored in
                   // ~1-second blocks, which VLC seeks and resyncs badly (choppy sound).
                   $"-map 0:v:0 -map \"[m]\" -c:v copy -c:a aac -b:a 256k -y \"{file}\"";
        _busy.Text = "Mixing sound…";
        _busy.Visibility = Visibility.Visible;
        // Work you're waiting on goes first.
        bool ok = await FFmpegRun.RunAsync(args, 10 * 60_000, ProcessPriorityClass.High, label: "player mix") && File.Exists(file);
        // A newer choice (or leaving the clip) wins.
        if (stamp != _mixStamp || _mp == null || _closed || clipPath != _originalPath)
        {
            try { File.Delete(file); } catch { }
            if (stamp == _mixStamp) _busy.Visibility = Visibility.Collapsed;
            return;
        }
        _busy.Visibility = Visibility.Collapsed;
        if (!ok) { _soundNote.Text = "Couldn't mix those tracks"; try { File.Delete(file); } catch { } return; }

        var previous = _mixFile;
        _mixFile = file;
        Reload(file, null);
        // The old mix is let go once VLC has moved on from it.
        if (previous != null) _ = Task.Run(async () => { await Task.Delay(3000); try { File.Delete(previous); } catch { } });
    }

    /// <summary>
    /// Plays another file in place of the current one, from the same moment,
    /// keeping pause and speed (and choosing a track, if given).
    /// </summary>
    private void Reload(string path, int? track)
    {
        if (_mp == null || _vlc == null) return;
        _pendingTime = _mp.Time > 0 ? _mp.Time : null;
        _pendingPause = !_mp.IsPlaying && _mp.State != VLCState.Ended;
        _pendingTrack = track;
        // Loading the clip again with the new sound: say so until it's playing.
        _reloading = true;
        _busy.Text = "Loading sound…";
        _busy.Visibility = Visibility.Visible;
        _busyGuard.Stop();
        _busyGuard.Interval = TimeSpan.FromSeconds(8);
        _busyGuard.Start();
        var old = _media;
        _media = new VlcMedia(_vlc, new Uri(path));
        _currentPath = path;
        _mp.Play(_media);
        old?.Dispose();
    }

    /// <summary>Deletes the current mixed file (once VLC isn't reading it).</summary>
    private void DropMixFile()
    {
        var f = _mixFile;
        _mixFile = null;
        if (f != null) _ = Task.Run(async () => { await Task.Delay(3000); try { File.Delete(f); } catch { } });
    }

    /* ---- look (Customization → Player and editor) ------------------------------------ */

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private const string PlayGlyph = "", PauseGlyph = "", ReplayGlyph = "";
    private const string FullGlyph = "", WindowGlyph = "";

    private Grid _speedPanel = null!, _soundPanel = null!;
    private readonly Action _lookChanged;
    private bool _glassStyle;
    private string _playGlyph = PauseGlyph;

    /// <summary>
    /// Something drawn behind controls. In the minimal style a bar or chip has
    /// nothing behind it (the shades along the edges carry it), and a pop-up
    /// panel is plain dark; in the glass style both are floating glass.
    /// </summary>
    private sealed class Surface
    {
        public Grid Root = null!;
        public Border Back = null!, Sheen = null!, Body = null!;
        public Thickness GlassPadding, PlainPadding;
        public bool Panel;
    }

    /// <summary>
    /// The player's look: dark whatever the theme (it sits over video and has to
    /// read on any clip), minimal or glass, with the seek bar's colour and
    /// thickness, how dark the shades are, and the corners and font from
    /// Customization. Applies live.
    /// </summary>
    private void ApplyLook()
    {
        var look = LookStore.Current;
        _glassStyle = look.PlayerStyle == PlayerStyle.Glass;
        _hide.Interval = TimeSpan.FromSeconds(Math.Max(0.5, look.PlayerHideSeconds));
        // Everything over the video, the two pop-up panels included, takes the dark colours.
        foreach (var r in new[] { _overlay.Resources, _speedPanel.Resources, _soundPanel.Resources })
            OverVideo(r);

        // The surfaces behind the controls.
        var glassFill = GlassFill();
        foreach (var s in _surfaces)
        {
            s.Body.Padding = _glassStyle ? s.GlassPadding : s.PlainPadding;
            s.Sheen.Visibility = _glassStyle ? Visibility.Visible : Visibility.Collapsed;
            if (s.Panel)
            {
                s.Back.Background = _glassStyle ? glassFill : PanelFill;
                s.Back.BorderBrush = _glassStyle ? GlassEdge : PanelEdge;
            }
            else
            {
                s.Back.Background = glassFill;
                s.Back.Visibility = _glassStyle ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        _bar.Margin = _glassStyle ? new Thickness(20, 0, 20, 16) : new Thickness(10, 0, 10, 4);
        _bar.MaxWidth = _glassStyle ? 1040 : double.PositiveInfinity;
        _timeNow.Visibility = _timeTotal.Visibility = _glassStyle ? Visibility.Visible : Visibility.Collapsed;
        _timeInline.Visibility = _glassStyle ? Visibility.Collapsed : Visibility.Visible;

        // Play: a round accent button on glass; a plain white icon in the minimal style.
        if (_glassStyle)
        {
            _play.SetResourceReference(StyleProperty, "Primary");
            _play.ClearValue(ForegroundProperty);
            _play.Width = _play.Height = 42;
            _play.Resources["RadiusControl"] = new CornerRadius(21);
            _edit.SetResourceReference(StyleProperty, "Primary");
        }
        else
        {
            _play.SetResourceReference(StyleProperty, "Ghost");
            _play.SetResourceReference(ForegroundProperty, "Ink");
            _play.Width = _play.Height = 40;
            _play.Resources["RadiusControl"] = new CornerRadius(20);
            _edit.ClearValue(StyleProperty);
        }
        _play.Margin = new Thickness(6, 0, 6, 0);
        SetPlay(_playGlyph);
        foreach (var b in new[] { _prev, _next })
        {
            b.Background = _glassStyle ? glassFill : Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0x0A, 0x0B, 0x0D)));
            b.BorderThickness = new Thickness(_glassStyle ? 1 : 0);
        }

        // The seek bar.
        var line = look.SeekStyle switch
        {
            SeekStyle.Accent => ThemeApplier.Readable(ThemeApplier.Accent, Color.FromRgb(0x10, 0x12, 0x15), Colors.Black),
            SeekStyle.Custom => ThemeApplier.Parse(look.SeekColor, Colors.White),
            _ => Color.FromRgb(0xF4, 0xF5, 0xF7),
        };
        _seekFill.Background = Frozen(new SolidColorBrush(line));
        _knob.Fill = _seekFill.Background;
        _seekRest.Background = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        _seekAhead.Background = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        SizeSeek();
        // The volume slider: thin and white, like the seek bar (red past 100%).
        _volume.Resources["SliderHeight"] = 3.0;
        PaintVolume();

        double shade = look.PlayerEdgeShade ? Math.Clamp(look.PlayerShadePercent, 0, 100) / 100.0 : 0;
        _topShade.Background = Shade(shade * 0.85, fromTop: true);
        // Deeper at the bottom, so the seek bar and buttons stand out on any clip.
        _bottomShade.Background = Shade(shade * 1.5, fromTop: false);
    }

    /// <summary>Dark colours for controls over video: light text, and fills that are veils of white.</summary>
    private static void OverVideo(ResourceDictionary r)
    {
        ThemeApplier.PutOverlay(r);
        Put(r, "ButtonFill", Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        Put(r, "Hover", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        Put(r, "Pressed", Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF));
        Put(r, "Line", Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Put(r, "Track", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        Put(r, "Ink", Color.FromRgb(0xF4, 0xF5, 0xF7));
        Put(r, "ButtonInk", Color.FromRgb(0xE6, 0xE8, 0xEB));
        Put(r, "ButtonEdge", Colors.Transparent);
        r["ButtonEdgeThickness"] = new Thickness(0);
    }

    private static void Put(ResourceDictionary r, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        r[key] = b;
    }

    /// <summary>Smoky glass: dark, a touch lighter at the top, as see-through as the look says.</summary>
    private static Brush GlassFill()
    {
        var o = ThemeApplier.Overlay;
        double s = 0.35 + 0.65 * Math.Clamp(LookStore.Current.PlayerShadePercent, 0, 100) / 100.0;
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(ThemeApplier.WithAlpha(ThemeApplier.Mix(o.Raised, Colors.White, 0.05), (byte)Math.Round(0xC4 * s)), 0));
        g.GradientStops.Add(new GradientStop(ThemeApplier.WithAlpha(o.Bezel, (byte)Math.Round(0xE0 * s)), 1));
        g.Freeze();
        return g;
    }

    /// <summary>A plain dark panel (the minimal style's pop-ups).</summary>
    private static readonly Brush PanelFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x16, 0x1A)));
    private static readonly Brush PanelEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));

    /// <summary>A thin bright edge, catching the light top-left.</summary>
    private static readonly Brush GlassEdge = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0),
        new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.5),
        new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 1),
    }, new Point(0, 0), new Point(1, 1)));

    /// <summary>A soft highlight across the top of the glass.</summary>
    private static readonly Brush GlassSheen = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0),
        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55),
    }, new Point(0, 0), new Point(0, 1)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Brush Shade(double strength, bool fromTop)
    {
        byte a = (byte)Math.Round(Math.Clamp(strength * 0xB0, 0, 0xE6));
        var g = new LinearGradientBrush { StartPoint = new Point(0, fromTop ? 0 : 1), EndPoint = new Point(0, fromTop ? 1 : 0) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(a, 0x03, 0x04, 0x06), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(a * 0.45), 0x03, 0x04, 0x06), 0.4));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x03, 0x04, 0x06), 1));
        g.Freeze();
        return g;
    }

    /// <param name="panel">A pop-up panel: always has a background (dark, or glass).</param>
    private Grid MakeSurface(UIElement content, Thickness glassPadding, Thickness plainPadding, bool panel = false)
    {
        var s = new Surface { GlassPadding = glassPadding, PlainPadding = plainPadding, Panel = panel };
        s.Back = new Border
        {
            BorderThickness = new Thickness(1), BorderBrush = GlassEdge,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        s.Back.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        s.Sheen = new Border { IsHitTestVisible = false, Background = GlassSheen, Margin = new Thickness(1) };
        s.Sheen.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        s.Body = new Border { Child = content };
        s.Root = new Grid();
        s.Root.Children.Add(s.Back);
        s.Root.Children.Add(s.Sheen);
        s.Root.Children.Add(s.Body);
        _surfaces.Add(s);
        return s.Root;
    }

    /// <summary>A quiet icon button (Windows' own icon font), optionally with a label.</summary>
    private Button IconButton(Button b, string glyph, double size = 15, string? label = null)
    {
        b.SetResourceReference(StyleProperty, "Ghost");
        b.SetResourceReference(ForegroundProperty, "Ink");
        b.MinWidth = 0;
        b.Padding = label == null ? new Thickness(9, 7, 9, 7) : new Thickness(10, 6, 12, 6);
        b.VerticalAlignment = VerticalAlignment.Center;
        SetIcon(b, glyph, size, label);
        return b;
    }

    /// <summary>A small text button (the skips: −10s, −2s, +2s, +10s).</summary>
    private Button TextButton(string text, string tip, Action click)
    {
        var b = new Button
        {
            Content = text, ToolTip = tip, MinWidth = 0, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(7, 7, 7, 7), VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(StyleProperty, "Ghost");
        b.SetResourceReference(ForegroundProperty, "Body");
        b.Click += (_, _) => click();
        return b;
    }

    private static void SetIcon(Button b, string glyph, double size = 15, string? label = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center });
        if (label != null) row.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        b.Content = row;
    }

    private void SetPlay(string glyph)
    {
        _playGlyph = glyph;
        _play.Content = new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = _glassStyle ? 16 : 19,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            // Windows' play triangle sits a hair left of centre in a circle.
            Margin = glyph == PlayGlyph ? new Thickness(2, 0, 0, 0) : new Thickness(0),
        };
    }

    /* ---- controls ---------------------------------------------------------------- */

    private void BuildControls()
    {
        /* ---- top: back, the clip's name, and Edit ---- */
        var backBtn = IconButton(new Button { ToolTip = "Back to the clips  (Esc)" }, "", 14);
        backBtn.Click += (_, _) => Close();
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "Display");
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0) };
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        var chipRow = new DockPanel();
        DockPanel.SetDock(backBtn, Dock.Left);
        chipRow.Children.Add(backBtn);
        chipRow.Children.Add(titles);
        var chip = MakeSurface(chipRow, new Thickness(5), new Thickness(0));
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        chip.MaxWidth = 640;

        SetIcon(_edit, "", 13, "Edit");
        _edit.Padding = new Thickness(12, 7, 14, 7);
        _edit.VerticalAlignment = VerticalAlignment.Top;
        _edit.Margin = new Thickness(12, 4, 0, 0);
        _edit.Click += (_, _) => OpenEditor();

        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_edit, 1);
        _top.Children.Add(chip);
        _top.Children.Add(_edit);

        /* ---- previous and next clip: round buttons at the sides ---- */
        SetIcon(_prev, "", 16);
        SetIcon(_next, "", 16);
        foreach (var b in new[] { _prev, _next })
        {
            b.SetResourceReference(StyleProperty, "Ghost");
            b.SetResourceReference(ForegroundProperty, "Ink");
            b.Resources["RadiusControl"] = new CornerRadius(22);
            b.Padding = new Thickness(0);
            b.BorderBrush = GlassEdge;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Opacity = 0;
        }
        _prev.HorizontalAlignment = HorizontalAlignment.Left;
        _prev.Margin = new Thickness(18, 0, 0, 0);
        _next.HorizontalAlignment = HorizontalAlignment.Right;
        _next.Margin = new Thickness(0, 0, 18, 0);
        _prev.Click += (_, _) => Load(_index - 1);
        _next.Click += (_, _) => Load(_index + 1);

        /* ---- the seek bar: a thin line that grows a little under the mouse ---- */
        // While dragging: the picture follows at most four times a second, and
        // lands exactly where it's let go. Seeking on every mouse move made
        // VLC jump back and forth.
        // Press anywhere on the bar and drag: it follows the mouse (not only
        // when the little handle is grabbed).
        _seekHost.Child = _seek;
        void ScrubTo(MouseEventArgs e)
        {
            double w = Math.Max(1, _seek.ActualWidth);
            _seek.Value = Math.Clamp(e.GetPosition(_seek).X / w, 0, 1) * _seek.Maximum;
        }
        // The controls sit in a see-through window, where Windows lets clicks
        // on fully transparent pixels fall through to the video: only the
        // visible handle could be grabbed. This strip is invisible but not
        // fully transparent, so the whole bar (and a little around it) takes
        // clicks and drags.
        _seekHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            _lastDragSeek = DateTime.MinValue;
            _lastSent = -1;
            _seekHost.CaptureMouse();
            ScrubTo(e);
            SizeSeek();
            e.Handled = true;
        };
        _seekHost.PreviewMouseMove += (_, e) =>
        {
            if (_dragging && _seekHost.IsMouseCaptured) ScrubTo(e);
            PlaceTip(e.GetPosition(_seek).X);
        };
        _seekHost.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            _seekHost.ReleaseMouseCapture();
            FinishSeek();
            SizeSeek();
            e.Handled = true;
        };
        _seekHost.LostMouseCapture += (_, _) =>
        {
            if (!_seekHost.IsMouseOver) { _tip.Visibility = Visibility.Collapsed; _seekHot = false; _seekAhead.Width = 0; }
            if (_dragging) { _dragging = false; FinishSeek(); }
            SizeSeek();
        };
        _seekHost.MouseEnter += (_, e) => { _seekHot = true; SizeSeek(); PlaceTip(e.GetPosition(_seek).X); };
        _seekHost.MouseLeave += (_, _) =>
        {
            if (_seekHost.IsMouseCaptured) return;
            _seekHot = false;
            _tip.Visibility = Visibility.Collapsed;
            _seekAhead.Width = 0;
            SizeSeek();
        };
        _seek.ValueChanged += (_, e) =>
        {
            DrawSeek();
            if (!_dragging || _mp == null) return;
            ShowTime((long)e.NewValue);
            // At most four seeks a second while dragging: each one restarts
            // VLC's sound, and back-to-back restarts could leave it stuck.
            if ((DateTime.UtcNow - _lastDragSeek).TotalMilliseconds < 250) return;
            _lastDragSeek = DateTime.UtcNow;
            SendSeek((long)e.NewValue);
        };
        _seek.SizeChanged += (_, _) => DrawSeek();
        _seekLine.Children.Add(_seekRest);
        _seekLine.Children.Add(_seekAhead);
        _seekLine.Children.Add(_seekFill);
        _knobLayer.Children.Add(_knob);

        // The time under the mouse, in a bubble above the bar.
        _tipText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _tipText.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
        _tip.Child = _tipText;
        _tip.CornerRadius = new CornerRadius(6);
        _tip.Background = Frozen(new SolidColorBrush(Color.FromArgb(0xEC, 0x12, 0x14, 0x18)));
        _tip.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
        _tip.BorderThickness = new Thickness(1);
        _tipLayer.Children.Add(_tip);

        foreach (var t in new[] { _timeNow, _timeTotal, _timeInline }) t.SetResourceReference(TextBlock.FontFamilyProperty, "Mono");
        _timeNow.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _timeTotal.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _timeInline.SetResourceReference(TextBlock.ForegroundProperty, "Body");
        var seekRow = new Grid();
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var seekCell = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        seekCell.Children.Add(_seekLine);
        seekCell.Children.Add(_knobLayer);
        seekCell.Children.Add(_seekHost);
        seekCell.Children.Add(_tipLayer);
        Grid.SetColumn(seekCell, 1);
        Grid.SetColumn(_timeTotal, 2);
        seekRow.Children.Add(_timeNow);
        seekRow.Children.Add(seekCell);
        seekRow.Children.Add(_timeTotal);

        /* ---- the controls row: sound and time on the left, playback in the middle, the rest on the right ---- */
        IconButton(_volumeBtn, "", 15);
        _volumeBtn.Click += (_, _) => ToggleMute();
        _volume.ValueChanged += (_, e) =>
        {
            if (_mp != null) _mp.Volume = (int)Math.Round(e.NewValue);
            ShowVolume();
        };
        _volume.Loaded += (_, _) => { _volumeKnob = null; PaintVolume(); };
        _volume.Margin = new Thickness(2, 0, 0, 0);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(_volumeBtn);
        left.Children.Add(_volume);
        left.Children.Add(_timeInline);

        // Skip 10 s and 2 s (as ← → and Shift + ← →), and one frame at a time.
        var back10 = TextButton("−10s", "Back 10 seconds  ( ← )", () => SeekBy(-10000));
        var back2 = TextButton("−2s", "Back 2 seconds  ( Shift + ← )", () => SeekBy(-2000));
        var fwd2 = TextButton("+2s", "Forward 2 seconds  ( Shift + → )", () => SeekBy(2000));
        var fwd10 = TextButton("+10s", "Forward 10 seconds  ( → )", () => SeekBy(10000));
        var back = IconButton(new Button { ToolTip = "Back one frame  ( , )" }, "", 13);
        var fwd = IconButton(new Button { ToolTip = "Forward one frame  ( . )" }, "", 13);
        // A click is one frame; held down, the frames run on, faster the longer it's held.
        back.PreviewMouseLeftButtonDown += (_, e) => { StartHold(-1, back); e.Handled = true; };
        fwd.PreviewMouseLeftButtonDown += (_, e) => { StartHold(1, fwd); e.Handled = true; };
        _play.Padding = new Thickness(0);
        _play.ToolTip = "Play or pause  ( Space )";
        _play.Click += (_, _) => TogglePlay();
        var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var el in new UIElement[] { back10, back2, back, _play, fwd, fwd2, fwd10 }) middle.Children.Add(el);
        Grid.SetColumn(middle, 1);

        // Sound tracks: a tick box each, in a small panel above the button.
        IconButton(_soundBtn, "", 14, "Tracks");
        _soundNote.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _soundPanel = MakeSurface(_soundList, new Thickness(0), new Thickness(0), panel: true);
        _soundPanel.Margin = new Thickness(12);
        _soundPopup.Child = _soundPanel;
        _soundPopup.PlacementTarget = _soundBtn;
        _soundPopup.Closed += (_, _) => ArmHide();
        _soundBtn.Click += (_, _) => { _soundPopup.IsOpen = !_soundPopup.IsOpen; ArmHide(); };

        // Speed: a short list above the button.
        _speedBtn.SetResourceReference(StyleProperty, "Ghost");
        _speedBtn.SetResourceReference(ForegroundProperty, "Ink");
        _speedBtn.MinWidth = 44;
        _speedBtn.Padding = new Thickness(9, 7, 9, 7);
        _speedBtn.FontSize = 12.5;
        _speedBtn.FontWeight = FontWeights.SemiBold;
        _speedBtn.VerticalAlignment = VerticalAlignment.Center;
        _speedPanel = MakeSurface(_speedList, new Thickness(0), new Thickness(0), panel: true);
        _speedPanel.Margin = new Thickness(12);
        _speedPopup.Child = _speedPanel;
        _speedPopup.PlacementTarget = _speedBtn;
        _speedPopup.Closed += (_, _) => ArmHide();
        _speedBtn.Click += (_, _) => { _speedPopup.IsOpen = !_speedPopup.IsOpen; ArmHide(); };
        SetSpeed(_speedIndex);

        IconButton(_fullBtn, FullGlyph, 14);
        _fullBtn.Click += (_, _) => ToggleFullscreen();

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_soundBtn);
        right.Children.Add(_speedBtn);
        right.Children.Add(_fullBtn);
        Grid.SetColumn(right, 2);

        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.Children.Add(left);
        controls.Children.Add(middle);
        controls.Children.Add(right);

        var stack = new StackPanel();
        stack.Children.Add(seekRow);
        stack.Children.Add(controls);
        _bar.Children.Add(MakeSurface(stack, new Thickness(14, 6, 10, 8), new Thickness(0, 0, 0, 2)));

        /* ---- "Mixing sound…": a small chip at the top ---- */
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brass");
        _busy.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var busyRow = new StackPanel { Orientation = Orientation.Horizontal };
        busyRow.Children.Add(dot);
        busyRow.Children.Add(_busy);
        var busyChip = MakeSurface(busyRow, new Thickness(14, 8, 16, 8), new Thickness(14, 8, 16, 8), panel: true);
        busyChip.HorizontalAlignment = HorizontalAlignment.Center;
        busyChip.VerticalAlignment = VerticalAlignment.Top;
        busyChip.Margin = new Thickness(0, 76, 0, 0);
        busyChip.IsHitTestVisible = false;
        busyChip.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = _busy });

        _overlay.Children.Add(_topShade);
        _overlay.Children.Add(_bottomShade);
        _overlay.Children.Add(_prev);
        _overlay.Children.Add(_next);
        _overlay.Children.Add(_top);
        _overlay.Children.Add(_bar);
        _overlay.Children.Add(busyChip);

        _settle.Tick += (_, _) => { _settle.Stop(); UpdateControls(); };
        _overlay.MouseMove += (_, e) => OnPointer(e.GetPosition(_overlay));
        _overlay.MouseLeave += (_, _) =>
        {
            _prev.Opacity = 0;
            _next.Opacity = 0;
            if (_inBottom || _inTop) { _inBottom = _inTop = false; _settle.Stop(); _settle.Start(); }
        };
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != _overlay) return;
            if (e.ClickCount == 2) ToggleFullscreen(); else TogglePlay();
        };
    }

    private void OpenEditor()
    {
        if (_clips.Count == 0) return;
        var clip = _clips[_index];
        Close();
        _app.OpenEditor(clip);
    }

    /* ---- the seek bar's drawing ---------------------------------------------------- */

    /// <summary>Thin at rest; a little thicker, with a handle, under the mouse or while dragging.</summary>
    private void SizeSeek()
    {
        double t = Math.Clamp(LookStore.Current.SeekThickness, 2, 10) + (_seekHot || _dragging ? 2 : 0);
        foreach (var b in new[] { _seekRest, _seekAhead, _seekFill })
        {
            b.Height = t;
            b.CornerRadius = new CornerRadius(t / 2);
        }
        _knob.Visibility = _seekHot || _dragging ? Visibility.Visible : Visibility.Collapsed;
        DrawSeek();
    }

    private void DrawSeek()
    {
        double w = _seek.ActualWidth;
        if (w <= 0) return;
        double f = _seek.Maximum > 0 ? Math.Clamp(_seek.Value / _seek.Maximum, 0, 1) : 0;
        _seekFill.Width = f * w;
        Canvas.SetLeft(_knob, f * w - _knob.Width / 2);
        Canvas.SetTop(_knob, (_knobLayer.ActualHeight - _knob.Height) / 2);
    }

    /// <summary>The time under the mouse, above the seek bar, and a lighter stretch up to it.</summary>
    private void PlaceTip(double x)
    {
        double w = Math.Max(1, _seek.ActualWidth);
        double f = Math.Clamp(x / w, 0, 1);
        _seekAhead.Width = f * w;
        if (_length <= 0) { _tip.Visibility = Visibility.Collapsed; return; }
        _tipText.Text = Fmt((long)(f * _length));
        _tip.Visibility = Visibility.Visible;
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = _tip.DesiredSize.Width;
        Canvas.SetLeft(_tip, Math.Clamp(f * w - tw / 2, -tw / 2, w - tw / 2));
        Canvas.SetTop(_tip, -24);
    }

    private void ShowTime(long ms)
    {
        _timeNow.Text = Fmt(ms);
        _timeTotal.Text = Fmt(_length);
        _timeInline.Text = $"{Fmt(ms)} / {Fmt(_length)}";
    }

    private void SetSpeed(int index)
    {
        _speedIndex = Math.Clamp(index, 0, Speeds.Length - 1);
        _speedBtn.Content = SpeedName(Speeds[_speedIndex]);
        _mp?.SetRate((float)Speeds[_speedIndex]);
        _speedList.Children.Clear();
        for (int i = Speeds.Length - 1; i >= 0; i--)
        {
            var b = new Button { Content = SpeedName(Speeds[i]), MinWidth = 96, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(12, 6, 12, 6) };
            b.SetResourceReference(StyleProperty, "Ghost");
            if (i == _speedIndex)
            {
                b.SetResourceReference(BackgroundProperty, "Hover");
                b.SetResourceReference(ForegroundProperty, "Ink");
                b.FontWeight = FontWeights.SemiBold;
            }
            else b.SetResourceReference(ForegroundProperty, "Body");
            int k = i;
            b.Click += (_, _) => { _speedPopup.IsOpen = false; SetSpeed(k); };
            _speedList.Children.Add(b);
        }
    }

    private static string SpeedName(double s) => s == 1.0 ? "1×" : $"{s:0.##}×";

    private void ToggleMute()
    {
        if (_volume.Value > 0) { _volumeBefore = _volume.Value; _volume.Value = 0; }
        else _volume.Value = _volumeBefore > 0 ? _volumeBefore : 60;
        ShowControls();
    }

    private static readonly Color VolumeWhite = Color.FromRgb(0xF4, 0xF5, 0xF7);
    private static readonly Color VolumeHot = Color.FromRgb(0xFF, 0x3B, 0x30);
    private System.Windows.Shapes.Ellipse? _volumeKnob;

    /// <summary>
    /// White up to 100%. Past it the line runs into red, redder the louder,
    /// and the part of the track past 100% is always faintly red, so the
    /// boost zone shows before it's used.
    /// </summary>
    private void PaintVolume()
    {
        double v = Math.Clamp(_volume.Value, 0, 200);
        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        var hot = Mix(VolumeWhite, VolumeHot, Math.Clamp((v - 100) / 100.0, 0, 1) * 0.6 + (v > 100 ? 0.4 : 0));

        Brush fill;
        if (v <= 100) fill = new SolidColorBrush(VolumeWhite);
        else
        {
            // The filled part runs 0..v: white to the 100% mark, then into red.
            double edge = 100 / v;
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            g.GradientStops.Add(new GradientStop(VolumeWhite, 0));
            g.GradientStops.Add(new GradientStop(VolumeWhite, edge));
            g.GradientStops.Add(new GradientStop(Mix(VolumeWhite, VolumeHot, 0.45), edge + (1 - edge) * 0.35));
            g.GradientStops.Add(new GradientStop(hot, 1));
            fill = g;
        }
        fill.Freeze();
        _volume.Resources["Brass"] = fill;

        // The rest of the track runs v..200: faint white, then faint red past 100%.
        var faint = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        var faintHot = Color.FromArgb(0x55, VolumeHot.R, VolumeHot.G, VolumeHot.B);
        Brush rest;
        if (v >= 100) rest = new SolidColorBrush(faintHot);
        else
        {
            double at = (100 - v) / (200 - v);
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            g.GradientStops.Add(new GradientStop(faint, 0));
            g.GradientStops.Add(new GradientStop(faint, at));
            g.GradientStops.Add(new GradientStop(faintHot, Math.Min(1, at + 0.02)));
            g.GradientStops.Add(new GradientStop(faintHot, 1));
            rest = g;
        }
        rest.Freeze();
        _volume.Resources["Track"] = rest;

        // The handle's ring follows: white, or the red it has reached.
        if (_volumeKnob == null && _volume.Template?.FindName("PART_Track", _volume) is Track track && track.Thumb is { } thumb)
        {
            thumb.ApplyTemplate();
            _volumeKnob = thumb.Template?.FindName("knob", thumb) as System.Windows.Shapes.Ellipse;
        }
        if (_volumeKnob != null)
        {
            var ring = new SolidColorBrush(v > 100 ? hot : VolumeWhite);
            ring.Freeze();
            _volumeKnob.Stroke = ring;
        }
    }

    private void ShowVolume()
    {
        double v = _volume.Value;
        PaintVolume();
        _volume.ToolTip = v > 100 ? $"Volume {v:0}% (boosted)  ( ↑ ↓ )" : $"Volume {v:0}%  ( ↑ ↓ )";
        SetIcon(_volumeBtn, v <= 0 ? "" : v < 34 ? "" : v < 67 ? "" : "", 15);
        _volumeBtn.ToolTip = v <= 0 ? "Unmute  ( M )" : "Mute  ( M )";
    }

    /* ---- holding a frame button (or , and .) --------------------------------------- */

    // Frames run on while held: one frame at once, a short pause (so a tap is
    // exactly one frame), then 1 frame a second, rising smoothly to 20 a
    // second over about a second and a half.
    private readonly DispatcherTimer _frameHold = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private int _holdDir;
    private Button? _holdButton;
    private Key? _holdKey;
    private DateTime _holdStart, _holdLast;
    private double _holdPhase;

    private void StartHold(int direction, Button? button, Key? key = null)
    {
        if (_holdDir == direction && _frameHold.IsEnabled) return;
        _holdDir = direction;
        _holdButton = button;
        _holdKey = key;
        StepFrame(direction);
        _holdStart = _holdLast = DateTime.UtcNow;
        _holdPhase = 0;
        if (!_holdTickHooked)
        {
            _frameHold.Tick += (_, _) => HoldTick();
            _holdTickHooked = true;
        }
        _frameHold.Start();
    }

    private bool _holdTickHooked;

    private void StopHold()
    {
        _holdDir = 0;
        _holdButton = null;
        _holdKey = null;
        _frameHold.Stop();
    }

    private void HoldTick()
    {
        // Let go? (Checked directly, so a release outside the window still stops it.)
        bool held = _holdKey is Key k ? Keyboard.IsKeyDown(k) : Mouse.LeftButton == MouseButtonState.Pressed;
        if (!held || _holdDir == 0 || _mp == null || _closed) { StopHold(); return; }
        var now = DateTime.UtcNow;
        double since = (now - _holdStart).TotalSeconds;
        double dt = (now - _holdLast).TotalSeconds;
        _holdLast = now;
        if (since < 0.3) return;
        // Off the button (mouse still down): wait, without stepping.
        if (_holdButton != null && !_holdButton.IsMouseOver) return;
        double ramp = Math.Clamp((since - 0.3) / 1.5, 0, 1);
        ramp = ramp * ramp * (3 - 2 * ramp);
        _holdPhase += (1 + 19 * ramp) * dt;
        if (_holdPhase < 1) return;
        _holdPhase = Math.Min(_holdPhase - 1, 1);
        StepFrame(_holdDir);
    }

    /* ---- showing and hiding the controls ---------------------------------------- */
    //
    // While a clip plays, the controls show only while the pointer is over
    // them (the bottom strip for the seek bar and buttons, the top strip for
    // Back, the name and Edit), and for a moment after a key press. On the
    // video itself they stay out of the way. Paused or ended, they stay up.

    private void OnPointer(Point at)
    {
        double h = _overlay.ActualHeight;
        bool bottom = at.Y >= h - (_bar.ActualHeight + _bar.Margin.Bottom + 40);
        bool top = at.Y <= _top.ActualHeight + _top.Margin.Top + 30;
        if (bottom != _inBottom || top != _inTop)
        {
            bool entering = bottom || top;
            _inBottom = bottom;
            _inTop = top;
            _settle.Stop();
            if (entering) UpdateControls(); else _settle.Start();
        }
        _overlay.Cursor = null;
        _hide.Stop();
        _hide.Start();
        _prev.Opacity = at.X < 150 && !bottom && !top ? 1 : 0;
        _next.Opacity = at.X > _overlay.ActualWidth - 150 && !bottom && !top ? 1 : 0;
    }

    private void Fade(UIElement e, double to) =>
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 240))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

    private void UpdateControls()
    {
        bool playing = _mp?.IsPlaying == true;
        bool popups = _soundPopup.IsOpen || _speedPopup.IsOpen;
        bool bottom = !playing || _inBottom || _dragging || popups || DateTime.UtcNow < _flashUntil;
        bool top = !playing || _inTop;
        if (bottom != _bottomShown)
        {
            _bottomShown = bottom;
            Fade(_bar, bottom ? 1 : 0);
            Fade(_bottomShade, bottom ? 1 : 0);
        }
        if (top != _topShown)
        {
            _topShown = top;
            Fade(_top, top ? 1 : 0);
            Fade(_topShade, top ? 1 : 0);
        }
    }

    /// <summary>After a key press or a click on a control: the bottom controls show for a moment.</summary>
    private void ShowControls()
    {
        _flashUntil = DateTime.UtcNow + _hide.Interval;
        _overlay.Cursor = null;
        UpdateControls();
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>Playback started, stopped or a pop-up closed: show or hide to match.</summary>
    private void ArmHide()
    {
        UpdateControls();
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>The moment's up: hide what isn't hovered, and the pointer if it rests on the video.</summary>
    private void HideControls()
    {
        _hide.Stop();
        UpdateControls();
        if (_mp?.IsPlaying == true && !_inBottom && !_inTop && !_dragging && !_soundPopup.IsOpen && !_speedPopup.IsOpen)
        {
            _prev.Opacity = 0;
            _next.Opacity = 0;
            _overlay.Cursor = Cursors.None;
        }
    }

    /// <summary>The clip list window itself fills the screen, so a share of it keeps working.</summary>
    private void ToggleFullscreen()
    {
        if (_window == null) return;
        if (!_fullscreen)
        {
            _restoreState = _window.WindowState;
            _restoreStyle = _window.WindowStyle;
            _window.WindowStyle = WindowStyle.None;
            _window.WindowState = WindowState.Normal;   // needed for None + Maximized to cover the taskbar
            _window.WindowState = WindowState.Maximized;
        }
        else
        {
            _window.WindowStyle = _restoreStyle;
            _window.WindowState = _restoreState;
        }
        _fullscreen = !_fullscreen;
        SetIcon(_fullBtn, _fullscreen ? WindowGlyph : FullGlyph, 14);
        _fullBtn.ToolTip = _fullscreen ? "Leave fullscreen  ( F )" : "Fullscreen  ( F )";
        // A glass window's own title bar steps aside in full screen and comes back after.
        WindowLooks.Apply(_window);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_closed || e.OriginalSource is TextBox) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekBy(shift ? -2000 : -10000); break;
            case Key.Right: SeekBy(shift ? 2000 : 10000); break;
            // Held, Windows repeats the key: the hold below runs at its own pace instead.
            case Key.OemComma: if (!e.IsRepeat) StartHold(-1, null, Key.OemComma); break;
            case Key.OemPeriod: if (!e.IsRepeat) StartHold(1, null, Key.OemPeriod); break;
            case Key.Up: _volume.Value = Math.Min(200, _volume.Value + 5); ShowControls(); break;
            case Key.Down: _volume.Value = Math.Max(0, _volume.Value - 5); ShowControls(); break;
            case Key.F: case Key.F11: ToggleFullscreen(); break;
            case Key.M: ToggleMute(); break;
            case Key.E: OpenEditor(); break;
            case Key.PageUp: Load(_index - 1); break;
            case Key.PageDown: Load(_index + 1); break;
            case Key.Escape: if (_fullscreen) ToggleFullscreen(); else Close(); break;
            default: return;
        }
        e.Handled = true;
    }
}
