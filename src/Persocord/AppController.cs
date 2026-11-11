using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Persocord.Models;
using Persocord.Services;
using Persocord.Views;

namespace Persocord;

/// <summary>
/// The glue: hotkeys and the control bar in, files in the library out.
/// </summary>
public class AppController
{
    public SettingsService Settings { get; } = new();
    public LibraryService Library { get; }
    public RecorderService Recorder { get; }
    public GameDetector Games { get; } = new();
    public ReplayService Replay { get; }

    /// <summary>The replay switched on or off (for the tray and Settings).</summary>
    public event Action? ReplayStateChanged;

    private readonly HotkeyService _hotkeys = new();
    private ClickHighlightOverlay? _clicks;
    private RecordingBorder? _border;
    private RecordingBar? _bar;
    private LibraryWindow? _libraryWindow;
    private SettingsWindow? _settingsWindow;
    private bool _busy;
    private bool _picking;
    private bool _diskWarned;

    public event Action<string>? StatusChanged;
    public event Action<string, string>? Notify;   // title, message
    /// <summary>A notification about one clip; clicking it can take you to that clip.</summary>
    public event Action<string, string, string>? NotifyClip;   // title, message, clip id

    /// <summary>
    /// Clicking a clip's notification: shows it in the clip list, opens it
    /// in the editor, or plays it, as chosen in Settings.
    /// </summary>
    public void OpenFromNotification(string clipId)
    {
        var clip = Library.Items.FirstOrDefault(c => c.Id == clipId);
        if (clip == null && _pendingClips.Contains(clipId))
        {
            // Clicked while it's still being filed: try again in a moment.
            _ = Task.Delay(300).ContinueWith(_ =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => OpenFromNotification(clipId))));
            return;
        }
        if (clip == null) { ShowLibrary(); return; }
        switch (Settings.Current.ToastClickAction)
        {
            case ToastClick.ShowInList:
                ShowLibrary();
                _libraryWindow?.Reveal(clip);
                break;
            case ToastClick.OpenEditor:
                OpenEditor(clip);
                break;
            case ToastClick.Play:
                ShareService.Play(Library.PathOf(clip));
                break;
        }
    }

    /// <summary>A clip's upload progress text changed (null when it finished).</summary>
    public event Action<string, string?>? LinkProgress;

    private readonly System.Collections.Generic.Dictionary<string, string> _uploadStatus = new();

    /// <summary>What an in-flight upload is doing, for the clip list.</summary>
    public string? UploadStatusOf(string clipId) =>
        _uploadStatus.TryGetValue(clipId, out var t) ? t : null;
    public event Action? LibraryRefreshed;

    public int PrimaryRefreshRate { get; private set; } = 60;

    public AppController()
    {
        Library = new LibraryService(Settings);
        Recorder = new RecorderService(Settings);
        Replay = new ReplayService(Settings);
        Replay.Failed += m => Notify?.Invoke("Replay", m);
        Replay.Notice += (title, m) => Notify?.Invoke(title, m);
        AudioEngine.MicBlocked += () => Notify?.Invoke("Microphone blocked by Windows",
            "Windows isn't letting Persocord use your mic, so clips have no voice. Turn on Settings → Privacy & security → " +
            "Microphone → \"Let desktop apps access your microphone\".");
        ElevatedAudio.Enabled = Settings.Current.ElevatedAppTracks;
        ElevatedAudio.Asking += name => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            Notify?.Invoke($"{name} runs as administrator",
                $"Windows will ask to let Persocord record {name}'s sound on its own track. " +
                "Asked once per session. If you say no, its sound stays in All PC audio and the editor's Unknown track.")));
        AudioTracks.AppNotSeparable += name => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            Notify?.Invoke($"{name}: no separate sound track",
                $"{name} runs as administrator, so Windows won't let Persocord record it on its own track. " +
                "It's still in All PC audio, and the editor shows it as an Unknown track you can edit.")));
        Replay.StateChanged += () => Application.Current?.Dispatcher.BeginInvoke(new Action(() => ReplayStateChanged?.Invoke()));
        Recorder.Tick += OnTick;
        Recorder.Failed += Status;
        Recorder.PauseStateChanged += OnPauseStateChanged;

        Recorder.Died += message => Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            Notify?.Invoke("Recording stopped", message);
            _saidWhyStopped = true;
            try { await FinishAsync(); }   // keeps whatever was captured before the problem
            finally
            {
                _saidWhyStopped = false;
                // Whatever happened, nothing is left on screen: frame, bar, rings.
                if (!Recorder.IsRecording) TearDownOverlays();
            }
        }));

        // The cue marks the moment picture is really being captured, so
        // hearing it means "go".
        Recorder.CaptureLive += first => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            Cue(first ? CueKind.Start : CueKind.Resume)));

        Recorder.LimitReached += () => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            Status("Reached the length limit.");
            _ = FinishAsync();
        }));
    }

    /* ------------------------------------------------------------------ */

    public void Startup()
    {
        if (!Settings.IsLoaded) Settings.Load();
        PrimaryRefreshRate = DetectRefresh();

        Library.FolderTrouble += (_, message) =>
            Notify?.Invoke("Clips folder", message);

        Library.FolderRecovered += path =>
        {
            Notify?.Invoke("Clips folder found",
                $"It had moved to:\n{path}\n\nPersocord is using it there now.");
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(() => LibraryRefreshed?.Invoke()));
        };

        Library.FilesChanged += () => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            Library.ReconcileWithDisk();
            LibraryRefreshed?.Invoke();
            _ = FillInFoundClipsAsync();
        }));

        Library.Start();
        StartupWork.Track("reading new clips", FillInFoundClipsAsync());

        _hotkeys.Attach();
        _hotkeys.RegistrationFailed += msg => Notify?.Invoke("Persocord", msg);
        ApplyHotkeys();

        // The one-time checks run now, in the background, so pressing record
        // later never waits on them.
        StartupWork.Track("checking the graphics card", EncoderDetector.WarmAsync());
        _ = CheckDiskAsync(quiet: true);

        // The replay runs from the start, until switched off (or, set to games
        // only, from when a game starts until a few minutes after it closes).
        Games.SetMyGames(Settings.Current.MyGames);
        Games.Changed += game => Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnGameChanged(game)));
        Games.Start();
        // A two-chip laptop found earlier: FFmpeg keeps running on the chip
        // that drives the screen.
        if (Settings.Current.CaptureOnScreenGpu) HybridGpu.SetPowerSaving(true);

        // Old temp files cleared a while after starting (never in the way of startup).
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(45)); TempJanitor.Run(); });

        SyncReplay();

        // The player's engine loads quietly in the background, so the first
        // clip opens without a stall.
        StartupWork.Track("loading the video player", Views.PlayerView.WarmUpAsync());
        Health.Start();

        // A fresh start after long running, while nobody's at the PC.
        Refresh = new SelfRefresh(BusyWith, show => RefreshWarning?.Invoke(show), () =>
        {
            bool library = _libraryWindow is { IsVisible: true };
            if (Application.Current is App app) app.Restart(showLibrary: library);
        });
        Refresh.Start();
    }

    /* ---- the fresh start ---------------------------------------------------- */

    public SelfRefresh? Refresh { get; private set; }

    /// <summary>The 10-minute notice before a refresh: shown (true) or taken away (false).</summary>
    public event Action<bool>? RefreshWarning;

    /// <summary>What Persocord is in the middle of right now (null when it's free to restart).</summary>
    public string? BusyWith()
    {
        if (Recorder.IsRecording || _busy || _picking) return "a recording";
        if (_pendingClips.Count > 0) return "saving a replay";
        if (_uploadStatus.Count > 0) return "an upload";
        if (Library.Items.Any(c => c.State == ClipState.Processing)) return "working on a clip";
        if (Views.StudioWindow.OpenCount > 0) return "the editor";
        if (Views.SetupWindow.IsOpen) return "the setup";
        return null;
    }

    /* ---- replay --------------------------------------------------------- */

    public void ToggleReplay()
    {
        var s = Settings.Current;
        s.ReplayEnabled = !s.ReplayEnabled;
        Settings.Save(s);
        SyncReplay();
        bool waiting = ReplayWaiting;
        Status(!s.ReplayEnabled ? "Replay off." : waiting ? "Replay on: waiting for a game." : "Replay on.");
        Notify?.Invoke(s.ReplayEnabled ? "Replay on" : "Replay off",
            !s.ReplayEnabled ? "Replay will no longer capture your screen."
            : waiting ? "It starts by itself when a game runs."
            : "Screen capture has started.");
    }

    /* ---- only while a game runs ------------------------------------------- */

    /// <summary>How long the replay keeps going after a game closes (so its last moments can still be saved).</summary>
    public static readonly TimeSpan AfterGame = TimeSpan.FromMinutes(5);

    private DateTime _gameGraceUntil = DateTime.MinValue;
    private System.Windows.Threading.DispatcherTimer? _graceTimer;

    /// <summary>Whether the replay should be running right now.</summary>
    private bool ReplayWanted
    {
        get
        {
            var s = Settings.Current;
            if (!s.ReplayEnabled) return false;
            if (!s.ReplayGamesOnly) return true;
            return Games.Current != null || DateTime.UtcNow < _gameGraceUntil;
        }
    }

    /// <summary>On, set to games only, and no game running: waiting for one.</summary>
    public bool ReplayWaiting => Settings.Current.ReplayEnabled && Settings.Current.ReplayGamesOnly && !Replay.IsRunning;

    /// <summary>Starts or stops the replay to match the settings and whether a game is running.</summary>
    private void SyncReplay()
    {
        if (ReplayWanted) { if (!Replay.IsRunning) Replay.Start(); }
        else if (Replay.IsRunning) Replay.Stop();
        ReplayStateChanged?.Invoke();
    }

    private void OnGameChanged(DetectedGame? game)
    {
        if (!Settings.Current.ReplayGamesOnly) return;
        if (game != null)
        {
            _gameGraceUntil = DateTime.MinValue;
            _graceTimer?.Stop();
            if (!Replay.IsRunning && Settings.Current.ReplayEnabled) Log.Write($"Replay: {game.Name} started, so the replay starts");
        }
        else if (Replay.IsRunning)
        {
            // Kept going a while, so the end of the game can still be saved.
            _gameGraceUntil = DateTime.UtcNow + AfterGame;
            Log.Write($"Replay: the game closed; the replay stops in {(int)AfterGame.TotalMinutes} minutes unless another starts");
            _graceTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = AfterGame + TimeSpan.FromSeconds(2) };
            _graceTimer.Tick -= GraceOver;
            _graceTimer.Tick += GraceOver;
            _graceTimer.Stop();
            _graceTimer.Start();
            return;
        }
        SyncReplay();
    }

    private void GraceOver(object? sender, EventArgs e)
    {
        _graceTimer?.Stop();
        if (!ReplayWanted && Replay.IsRunning) Log.Write("Replay: no game for 5 minutes, so the replay waits for the next one");
        SyncReplay();
    }

    /// <summary>"Count this as a game" (tray menu): the program counts as a game from now on.</summary>
    public void AddGame(string path)
    {
        var s = Settings.Current.Clone();
        if (!s.MyGames.Contains(path, StringComparer.OrdinalIgnoreCase)) s.MyGames.Add(path);
        Settings.Save(s);
        Games.Added(path);
        var name = GameDetector.NameOf(path);
        Log.Write($"Game added by hand: {name} ({Path.GetFileName(path)})");
        Notify?.Invoke($"{name} counts as a game",
            Settings.Current.ReplayGamesOnly ? "The replay runs whenever it does." : "Its clips are named and sorted as a game's.");
        SyncReplay();
    }

    /// <summary>After a reset or import: colours, hotkeys and the replay all follow the new settings.</summary>
    public void ApplyAll(AppSettings before)
    {
        // The look is separate (LookStore): settings changes never touch it.
        ElevatedAudio.Enabled = Settings.Current.ElevatedAppTracks;
        ApplyHotkeys();
        ApplyReplay(before);
        ReplayStateChanged?.Invoke();
    }

    /// <summary>After Settings are saved: starts, stops or restarts the replay as needed.</summary>
    public void ApplyReplay(AppSettings before)
    {
        var s = Settings.Current;
        // The length isn't in here: changing it doesn't restart the replay (what's recorded stays).
        string Sig(AppSettings x) => string.Join("|", x.ReplayMonitor, x.ReplayMonitorId, x.ReplayHeight, x.ReplayFps,
            x.ReplayQuality, x.ReplayBitrateKbps, x.ReplayCursor, x.ReplayTrackPc,
            x.ReplayTrackMic, x.MicDeviceId, x.SystemDeviceId, x.AppTracks, x.MaxAppTracks,
            x.ReplayCodec, x.ReplayEncoder, x.ReplayUseBitrate,
            x.SafetyCpuPercent, x.SafetyRamMb, x.SafetyGpuLowPriority, x.SafetyCpuHardCap, x.SafetyGpuAdaptive, x.ReplayScaleFullRange,
            x.ReplayLight);

        Games.SetMyGames(s.MyGames);
        // Games only switched on with no game running: the replay waits from now.
        if (s.ReplayGamesOnly && !before.ReplayGamesOnly && Games.Current == null) _gameGraceUntil = DateTime.MinValue;
        if (!ReplayWanted) { Replay.Stop(); ReplayStateChanged?.Invoke(); }
        else if (!Replay.IsRunning) SyncReplay();
        else if (Sig(before) != Sig(s)) Replay.Restart();
        else if (before.ReplayLongest() != s.ReplayLongest() && !Replay.SetLongest(s.ReplayLongest())) Replay.Restart();
    }

    /// <summary>The replay hotkey: keeps the last few seconds as a clip.</summary>
    public async Task SaveReplayAsync(int? seconds = null)
    {
        if (!Replay.IsRunning)
        {
            if (ReplayWaiting) Notify?.Invoke("The replay is waiting for a game", "It starts by itself when a game runs.");
            else Notify?.Invoke("Replay is off", "Switch it on from the clip list, the tray menu or Settings → Replay.");
            return;
        }
        // Not capturing and nothing held: said straight away, without a "saved" first.
        if (Replay.Trouble is { Length: > 0 } trouble && Replay.SecondsHeld < 4)
        {
            Notify?.Invoke("Replay isn't capturing", $"{char.ToUpper(trouble[0])}{trouble[1..]}. It keeps trying by itself.");
            return;
        }

        var s = Settings.Current;
        var game = Games.Current;
        var app = game?.Name ?? WindowTitleResolver.AppNameAt(System.Windows.Forms.Screen.PrimaryScreen!.Bounds);

        // The clip's identity exists from the start, so the sound and the
        // notification can come the instant the hotkey is pressed; clicking
        // the notification waits the split second until the clip is filed.
        var clip = new ClipRecord { App = app, State = ClipState.Ready, Tags = new() { "replay" } };
        _pendingClips.Add(clip.Id);
        Cue(CueKind.Replay);
        NotifyClip?.Invoke("Replay saved",
            // The length actually saved: the key's own, or shorter after "Start over after saving".
            $"The last {Duration(Replay.PlannedSeconds(seconds))}{(game != null ? " of " + game.Name : "")}." +
            (s.CopyReplayOnSave ? " Copying it now." : ""), clip.Id);

        try
        {
            Status("Saving replay…");
            var path = await Replay.SaveAsync(seconds);
            if (path == null) { Failed(Replay.LastError ?? "Nothing to save yet: the replay has only just started."); return; }

            var info = await ClipEditor.ProbeAsync(path);
            // A file with sound but no picture is no clip: said, not filed.
            // (Only when it could be read: a probe that failed says nothing about the file.)
            if (info != null && info.Width <= 0)
            {
                Log.Write("Replay save: the file had no picture, only sound; not kept");
                try { File.Delete(path); } catch { }
                Failed("The replay's picture wasn't ready, so nothing was kept. Give it a few seconds and save again.");
                return;
            }
            clip.Title = WindowTitleResolver.BuildTitle(s.TitleTemplate, app, info?.Width ?? 0, info?.Height ?? 0, (int)Math.Round(info?.Fps ?? 0));
            clip.Width = info?.Width ?? 0;
            clip.Height = info?.Height ?? 0;
            clip.Fps = (int)Math.Round(info?.Fps ?? 0);
            clip.DurationMs = (long)((info?.Duration ?? 0) * 1000);

            clip.FileName = await FileIntoLibraryAsync(path, clip);
            if (string.IsNullOrEmpty(clip.FileName)) { Failed("The file couldn't be moved into the clips folder."); return; }

            if (s.AutoSortIntoFolders && Library.FolderForApp(clip.App, create: true) is string home)
                await Task.Run(() => Library.MoveClip(clip, home));

            clip.Size = SafeSize(Library.PathOf(clip));
            Library.Add(clip);
            Status("Replay saved.");

            // Saved and announced first; the clipboard can take its time after.
            if (s.CopyReplayOnSave) _ = CopyThenSayAsync(clip);
            _ = FinishInBackgroundAsync(clip);
        }
        catch (Exception ex)
        {
            // Said, never lost silently in the background.
            Log.Error("save replay", ex);
            Failed("Something went wrong while filing it. Settings → About → Create diagnostics report shows why.");
        }
        finally { _pendingClips.Remove(clip.Id); }

        void Failed(string why)
        {
            Status("The replay couldn't be saved.");
            Notify?.Invoke("Replay not saved", why);
        }
    }

    private readonly HashSet<string> _pendingClips = new();

    /// <summary>A notification from elsewhere in the app (Persocord's own card, or Windows').</summary>
    public void Say(string title, string message) => Notify?.Invoke(title, message);

    /// <summary>Keys that couldn't be set because another program holds them.</summary>
    public IReadOnlyList<string> HotkeysTaken { get { lock (_hotkeys.Taken) return _hotkeys.Taken.ToList(); } }

    public void ApplyHotkeys()
    {
        _hotkeys.UnregisterAll();
        var s = Settings.Current;
        _hotkeys.Register(s.HotkeyRegion, () => _ = StartRegionRecordingAsync(false));
        _hotkeys.Register(s.HotkeyPause, TogglePause);
        _hotkeys.Register(s.HotkeyStop, () => _ = FinishAsync());
        _hotkeys.Register(s.HotkeyCancel, () => _ = CancelAsync());
        _hotkeys.Register(s.HotkeySaveReplay, () => _ = SaveReplayAsync());
        // Extra save setups: each key saves its own length.
        if (!string.IsNullOrWhiteSpace(s.HotkeySaveReplay2))
            _hotkeys.Register(s.HotkeySaveReplay2, () => _ = SaveReplayAsync(Settings.Current.ReplaySeconds2));
        if (!string.IsNullOrWhiteSpace(s.HotkeySaveReplay3))
            _hotkeys.Register(s.HotkeySaveReplay3, () => _ = SaveReplayAsync(Settings.Current.ReplaySeconds3));
        if (!string.IsNullOrWhiteSpace(s.HotkeyToggleReplay)) _hotkeys.Register(s.HotkeyToggleReplay, ToggleReplay);
    }

    /* ---- share links -------------------------------------------------- */

    /// <summary>
    /// One-click upload with the default settings. Until the link warning has
    /// been acknowledged, the full menu opens instead, so it is always seen.
    /// </summary>
    public async Task UploadAsync(ClipRecord clip, Window? owner = null)
    {
        var s = Settings.Current;
        if (!s.LinkWarningAcknowledged) { await UploadWithMenuAsync(clip, owner); return; }
        await UploadCoreAsync(clip, s.LinkDefaults ?? new LinkChoice());
    }

    /// <summary>The full menu: every duration and host, then upload.</summary>
    public async Task UploadWithMenuAsync(ClipRecord clip, Window? owner = null)
    {
        if (clip.Private) { PrivateRefusal(); return; }

        var s = Settings.Current;
        var dlg = new UploadDialog(clip, s.LinkDefaults ?? new LinkChoice(), s.LinkWarningAcknowledged);
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        else { dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen; dlg.Topmost = true; }

        if (dlg.ShowDialog() != true) return;

        bool save = false;
        if (dlg.WarningAcknowledged) { s.LinkWarningAcknowledged = true; save = true; }
        if (dlg.MakeDefault) { s.LinkDefaults = dlg.Result; save = true; }
        if (save) Settings.Save();

        await UploadCoreAsync(clip, dlg.Result);
    }

    private async Task UploadCoreAsync(ClipRecord clip, LinkChoice choice)
    {
        if (clip.Private) { PrivateRefusal(); return; }
        if (_uploadStatus.ContainsKey(clip.Id)) return;   // already on its way
        choice = choice.Copy();
        LinkChoice.Tidy(choice);   // a choice from an older version or a shared code: only hosts still offered

        var path = Library.PathOf(clip);
        if (!File.Exists(path)) { Notify?.Invoke("Can't upload", "That clip's file is gone."); return; }

        long size = SafeSize(path);
        bool big = !clip.IsGif && size > LinkCatalog.DiscordInlineLimit;
        var mode = big ? choice.BigClip : BigClipMode.FullPlain;

        var hosts = LinkCatalog.Candidates(choice, LinkCatalog.EffectiveSize(choice, size, clip.IsGif));
        var duration = LinkCatalog.Duration(choice.Duration);
        if (hosts.Count == 0 || duration == null)
        {
            Notify?.Invoke("Can't upload this clip",
                $"None of your chosen hosts can take a {clip.SizeText} clip for " +
                $"{duration?.Label.ToLowerInvariant() ?? "that long"}. Use the arrow beside Upload to pick something else.");
            return;
        }

        void Report(string text)
        {
            _uploadStatus[clip.Id] = text;
            LinkProgress?.Invoke(clip.Id, text);
        }

        ServiceInfo host = hosts[0];
        int lastPct = -1;
        var progress = new Progress<double>(f =>
        {
            int pct = (int)(f * 100);
            if (pct == lastPct) return;
            lastPct = pct;
            Report($"Uploading to {host.Name}… {pct}%");
        });

        string upload = path;
        string? shrunk = null;
        try
        {
            if (mode == BigClipMode.Shrink)
            {
                // A separate copy for the link; the clip on disk stays full size.
                Report("Shrinking to play inline in Discord…");
                Status("Shrinking the clip so Discord plays it inline…");
                shrunk = Path.Combine(Paths.Temp, $"link_{clip.Id}.mp4");
                var st = Settings.Current;
                var hw = st.ShrinkOnGpu ? await ClipEditor.BestEncoderAsync() : EncoderKind.X264;
                // A link's copy: only the main mix (all a viewer hears), so the picture gets the room.
                if (!await MediaTools.ShrinkToFitAsync(path, shrunk, LinkCatalog.ShrinkTarget, st.AudioBitrateKbps, hw, keepAllTracks: false))
                {
                    Notify?.Invoke("Upload failed", "The clip couldn't be shrunk to play inline. Try the full-size options from the arrow menu.");
                    return;
                }
                upload = shrunk;
            }

            // Each suitable host in turn: when one is down or blocked, the
            // next is tried without being asked, and the failure is remembered
            // so later uploads go to a working host first.
            ClipLink? link = null;
            var errors = new System.Collections.Generic.List<string>();
            foreach (var candidate in hosts)
            {
                host = candidate;
                lastPct = -1;
                try
                {
                    Report(errors.Count == 0 ? $"Uploading to {host.Name}…" : $"Trying {host.Name} instead…");
                    Status(host.Id == LinkService.Catbox
                        ? "Uploading to Catbox. It's very slow; this can take a while…"
                        : $"Uploading to {host.Name}…");
                    try
                    {
                        link = await LinkUploader.UploadAsync(upload, host, duration, progress, System.Threading.CancellationToken.None);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("(5"))
                    {
                        // A server error is often a passing hiccup (Litterbox
                        // has them on and off), so one more go before moving on.
                        Log.Write($"{host.Name} server error, retrying once: {ex.Message}");
                        Report($"{host.Name} hiccuped, retrying…");
                        await Task.Delay(3000);
                        lastPct = -1;
                        link = await LinkUploader.UploadAsync(upload, host, duration, progress, System.Threading.CancellationToken.None);
                    }
                    LinkCatalog.Failures.Remove(host.Id);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"upload to {host.Name}", ex);
                    var why = ShortReason(ex);
                    LinkCatalog.Failures[host.Id] = why;
                    errors.Add($"{host.Name}: {why}");
                }
            }

            if (link == null)
            {
                Status("Upload failed.");
                Notify?.Invoke("Upload failed", string.Join("\n", errors));
                return;
            }
            link.Shrunk = shrunk != null;

            // Litterbox links don't play in Discord on their own; through
            // x266.mov's page they do. Same as the full-size big-clip option.
            bool viaX266 = mode == BigClipMode.FullViaX266 || host.Id == LinkService.Litterbox;
            if (viaX266)
            {
                // The preview image goes to the same host with the same
                // lifetime, so nothing outlives the video.
                string? thumb = null;
                var thumbPath = Library.ThumbPathOf(clip);
                if (thumbPath != null && File.Exists(thumbPath))
                {
                    try
                    {
                        Report("Uploading the preview image…");
                        var t = await LinkUploader.UploadAsync(thumbPath, host, duration,
                            new Progress<double>(_ => { }), System.Threading.CancellationToken.None);
                        thumb = t.Url;
                        link.ThumbUrl = t.Url;
                        link.ThumbDeleteToken = t.DeleteToken;
                    }
                    catch (Exception ex) { Log.Error("thumbnail upload", ex); }
                }

                link.RawUrl = link.Url;
                link.Url = LinkUploader.X266Link(link.Url, thumb, clip.Width, clip.Height);
            }

            clip.Link = link;
            Library.Save();

            CopyLink(link.Url);
            Status("Link copied. Ctrl+V in Discord.");
            var how = mode switch
            {
                BigClipMode.Shrink => "  ·  shrunk to play inline",
                _ when viaX266 => "  ·  via x266.mov",
                _ when big => "  ·  plain link (too big to play inline)",
                _ => "",
            };
            Notify?.Invoke("Link copied", $"{host.Name}  ·  {link.TimeLeftText}{how}\n{link.Url}");
        }
        catch (Exception ex)
        {
            Log.Error("upload", ex);
            Status("Upload failed.");
            Notify?.Invoke("Upload failed", ex.Message);
        }
        finally
        {
            if (shrunk != null) { try { File.Delete(shrunk); } catch { } }
            _uploadStatus.Remove(clip.Id);
            LinkProgress?.Invoke(clip.Id, null);
        }
    }

    /// <summary>
    /// Takes a link down early. Only Oshi allows that, from each upload's own
    /// management page; its exact request isn't documented, so rather than
    /// guess, the page opens: one click there, then confirm here.
    /// </summary>
    public Task KillLinkAsync(ClipRecord clip)
    {
        var link = clip.Link;
        if (link == null) return Task.CompletedTask;
        if (!link.CanKill)
        {
            Notify?.Invoke("Can't kill this link", $"{link.Service} doesn't allow taking links down early.");
            return Task.CompletedTask;
        }

        try
        {
            // Only ever a web page: what a host sent back is never run as anything else.
            if (IsWebPage(link.DeleteToken)) Process.Start(new ProcessStartInfo(link.DeleteToken!) { UseShellExecute = true })?.Dispose();
            if (IsWebPage(link.ThumbDeleteToken))
                Process.Start(new ProcessStartInfo(link.ThumbDeleteToken!) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log.Error("open manage page", ex); }

        var done = Dialog.Show(
            $"{link.Service}'s management page for this upload has opened in your browser. " +
            "Destroy the file there, then press Yes to mark the link as dead." +
            (link.ThumbDeleteToken != null ? "\n\nA second page opened for the preview image; destroy that too." : ""),
            "Kill link", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (done == MessageBoxResult.Yes)
        {
            clip.Link = null;
            Library.Save();
            Status("Link marked as dead.");
        }
        return Task.CompletedTask;
    }

    private static bool IsWebPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>One line on why an upload failed, for a notification.</summary>
    private static string ShortReason(Exception ex)
    {
        var inner = ex is System.Net.Http.HttpRequestException && ex.InnerException != null ? ex.InnerException : ex;
        var text = inner.Message;
        if (text.Contains("actively refused")) return "refused to connect (down, or blocked on your network)";
        if (text.Contains("500")) return "server error on their side (500)";
        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "timed out";
        int cut = text.IndexOf('<');
        if (cut > 0) text = text[..cut].Trim();
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    public void TogglePrivate(ClipRecord clip)
    {
        clip.Private = !clip.Private;
        Library.Save();
    }

    private void PrivateRefusal() =>
        Notify?.Invoke("This clip is private",
            "Private clips can't be uploaded. Untick Private in the clip list first.");

    /// <summary>Puts a link on the clipboard without holding up the window.</summary>
    public static void CopyLink(string url) => _ = ShareService.CopyTextAsync(url);

    /* ---- launching ---------------------------------------------------- */

    /// <summary>
    /// What happens when Persocord is opened from its icon, either fresh or
    /// while it is already running in the tray.
    /// </summary>
    public void HandleLaunch(LaunchRequest request)
    {
        switch (request)
        {
            case LaunchRequest.Tray:
                return;
            case LaunchRequest.Library:
                ShowLibrary();
                return;
            case LaunchRequest.Record:
                _ = StartRegionRecordingAsync(false);
                return;
            default:
                // Opening it again mid-recording stops and saves, the same as
                // pressing the record hotkey again.
                if (Settings.Current.OpenAction == OpenAction.ClipList && !Recorder.IsRecording)
                    ShowLibrary();
                else
                    _ = StartRegionRecordingAsync(false);
                return;
        }
    }

    /* ---- recording ---------------------------------------------------- */

    public async Task StartRegionRecordingAsync(bool asGif)
    {
        if (_busy || _picking) return;
        if (Recorder.IsRecording) { await FinishAsync(); return; }
        if (!await ReadyToRecordAsync()) return;

        var s = Settings.Current;
        var last = s.RememberLastRegion && s.LastRegion.Length == 4
            ? new Rectangle(s.LastRegion[0], s.LastRegion[1], s.LastRegion[2], s.LastRegion[3])
            : (Rectangle?)null;

        Rectangle? chosen;
        _picking = true;
        try
        {
            // Opening the cue output now means the start cue plays the
            // instant the area is picked, not a beat later.
            if (s.PlaySounds) CuePlayer.Shared.Warm();

            var picker = new RegionSelector(LookStore.Current.PickerColor, last, LookStore.Current.PickerDimPercent);
            chosen = picker.ShowDialog() == true ? picker.Result : null;
            if (picker.OpenClipsRequested) { ShowLibrary(); return; }
        }
        finally { _picking = false; }

        if (chosen == null) return;
        await BeginAsync(chosen.Value, asGif);
    }

    /// <summary>
    /// Refuses to start when there is nowhere to put the result. Discovering
    /// a full library after a two-minute take is a bad way to find out.
    /// </summary>
    private Task<bool> ReadyToRecordAsync()
    {
        if (!Paths.FFmpegFound)
        {
            Dialog.Show(
                "Persocord needs ffmpeg.exe to record, and cannot find it.\n\n" +
                "Copy ffmpeg.exe into the same folder as Persocord.exe:\n" +
                System.IO.Path.GetDirectoryName(Environment.ProcessPath) +
                "\n\nRebuilding with build.ps1 also puts it there.",
                "Persocord", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.FromResult(false);
        }

        Library.CheckFolder();

        if (!Library.FolderExists)
        {
            Library.EnsureFolder();
            if (!Library.FolderExists)
            {
                Notify?.Invoke("Cannot record",
                    $"The clips folder is unreachable:\n{Library.Folder}\n\n" +
                    "Pick a different one in Settings.");
                return Task.FromResult(false);
            }
        }

        // Being over the size limit never stops a recording or keeps anyone
        // out of the app; it only earns a reminder.
        _ = CheckDiskAsync(quiet: false);
        return Task.FromResult(true);
    }

    private async Task BeginAsync(Rectangle region, bool asGif)
    {
        var s = Settings.Current;

        if (s.CountdownSeconds > 0)
            await CountdownOverlay.RunAsync(region, s.CountdownSeconds, LookStore.Current.CountdownColor);

        // FFmpeg first: every millisecond before it starts is a millisecond
        // missing from the front of the clip. The overlays can follow.
        if (!await Recorder.StartAsync(new RecordRequest(region, asGif)))
            return;

        // Anything left up from an earlier take goes first, so a frame or bar is never orphaned on screen.
        TearDownOverlays();

        if (s.ShowClickHighlights)
        {
            _clicks = new ClickHighlightOverlay();
            _clicks.Start(region, LookStore.Current.ClickRingColor, s.ClickHighlightSize);
        }

        if (s.ShowRecordingBorder)
        {
            _border = new RecordingBorder(region, s);
            _border.Show();
        }

        if (s.ShowRecordingBar) ShowBar(region, s);

        Status(asGif ? "Recording a GIF." : "Recording.");
    }

    private void ShowBar(Rectangle region, AppSettings s)
    {
        _bar = new RecordingBar(
            region,
            s.WarnAtSeconds > 0 ? s.WarnAtSeconds : 120,
            Recorder.SystemAvailable, Recorder.MicAvailable,
            !Recorder.SystemMuted, !Recorder.MicMuted,
            s.LastBarPosition);

        _bar.PauseClicked    += TogglePause;
        _bar.RestartClicked  += () => _ = RestartAsync();
        _bar.SettingsClicked += ShowSettings;
        _bar.CancelClicked   += () => _ = CancelAsync();
        _bar.DoneClicked     += () => _ = FinishAsync();
        _bar.DoneUploadClicked += () => _ = FinishAsync(upload: true);

        _bar.PcAudioToggled += on =>
        {
            Recorder.SystemMuted = !on;
            Status(on ? "Desktop sound on." : "Desktop sound muted.");
        };
        _bar.MicToggled += on =>
        {
            Recorder.MicMuted = !on;
            Status(on ? "Microphone on." : "Microphone muted.");
        };

        // Remember where it was dragged to, so it comes back there next time.
        _bar.Moved += (x, y) =>
        {
            Settings.Current.LastBarPosition = new[] { x, y };
            Settings.Save();
        };

        _bar.Show();
    }

    private void OnTick(TimeSpan elapsed)
    {
        var s = Settings.Current;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            _border?.Update(elapsed, s.WarnAtSeconds, s.WarnAtSeconds > 0);
            _bar?.Update(elapsed);
        }));
    }

    private void OnPauseStateChanged(bool paused)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _border?.SetPaused(paused);
            _bar?.SetPaused(paused);

            var s = Settings.Current;
            if (paused)
            {
                _clicks?.Stop();
                _clicks = null;
            }
            else if (s.ShowClickHighlights && _clicks == null)
            {
                _clicks = new ClickHighlightOverlay();
                _clicks.Start(Recorder.CurrentRegion, LookStore.Current.ClickRingColor, s.ClickHighlightSize);
            }
        });

        Status(paused ? "Paused. Press the same keys again to carry on." : "Recording again.");
    }

    /// <summary>Instant in both directions; nothing here waits on FFmpeg.</summary>
    public void TogglePause()
    {
        if (!Recorder.IsRecording || _busy) return;

        if (Recorder.IsPaused)
        {
            // The resume cue plays when capture is live again (CaptureLive).
            Recorder.Resume();
        }
        else
        {
            // Pause first: the audio gate is shut before the cue can sound.
            Recorder.Pause();
            Cue(CueKind.Pause);
        }
    }

    /// <summary>
    /// Plays a cue. Where Windows cannot leave Persocord's own sounds out of
    /// desktop capture, the recording gets silence for the cue's length
    /// instead, so the cue still never ends up in a clip.
    /// </summary>
    public void PlayCue(CueKind kind) => Cue(kind);

    /// <summary>"30 seconds", "2 minutes", "1 minute 30 seconds".</summary>
    private static string Duration(int seconds)
    {
        int m = seconds / 60, sec = seconds % 60;
        string Part(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        if (m == 0) return Part(sec, "second");
        return sec == 0 ? Part(m, "minute") : $"{Part(m, "minute")} {Part(sec, "second")}";
    }

    private async Task CopyThenSayAsync(ClipRecord clip)
    {
        Status(await ShareService.CopyFileAsync(Library.PathOf(clip))
            ? "Copied. Ctrl+V in Discord."
            : "Saved, but Windows wouldn't let go of the clipboard. Use Copy in the clip list.");
    }

    private void Cue(CueKind kind)
    {
        var s = Settings.Current;
        if (!s.PlaySounds) return;
        var cue = s.CueFor(kind);
        if (!cue.Enabled) return;

        var length = CuePlayer.Shared.Play(cue.Sound, s.CueVolume / 100f);
        if (length > TimeSpan.Zero && (Recorder.IsRecording || kind == CueKind.Start))
            Recorder.SuppressOwnSounds(length + TimeSpan.FromMilliseconds(150));
    }

    public async Task RestartAsync()
    {
        if (!Recorder.IsRecording || _busy) return;
        Application.Current?.Dispatcher.Invoke(() => _bar?.ResetTimer());
        if (await Recorder.RestartAsync()) Status("Started over.");
        else { TearDownOverlays(); Status("Could not start over."); }
    }

    public async Task CancelAsync()
    {
        if (!Recorder.IsRecording) return;
        await Recorder.CancelAsync();
        TearDownOverlays();
        Cue(CueKind.Cancelled);
        Status("Cancelled. Nothing was saved.");
        ShowLibrary();
    }

    /* ---- finishing ---------------------------------------------------- */

    private bool _saidWhyStopped;

    public async Task FinishAsync(bool upload = false)
    {
        if (!Recorder.IsRecording || _busy) return;
        _busy = true;

        try
        {
            var region = Recorder.CurrentRegion;
            var joining = Recorder.SegmentCount > 1;
            if (joining) Status("Joining the pieces…");

            // The bar reports the wait instead of vanishing, so a slow join
            // does not look like the clip was lost.
            Application.Current?.Dispatcher.Invoke(() =>
                _bar?.SetBusy(joining ? "joining…" : "finishing…"));

            var stopping = Recorder.StopAsync();
            TearDownCaptureOverlays();
            var result = await stopping;
            TearDownOverlays();

            if (!result.Ok)
            {
                Status("Recording failed.");
                // Already explained when it stopped: one notice, not two.
                if (!_saidWhyStopped) Notify?.Invoke("Recording failed", result.Error ?? "No details available.");
                return;
            }

            var s = Settings.Current;
            var app = WindowTitleResolver.AppNameAt(region);
            var clip = new ClipRecord
            {
                App = app,
                Title = WindowTitleResolver.BuildTitle(
                    s.TitleTemplate, app, result.Width, result.Height, result.Fps),
                Width = result.Width,
                Height = result.Height,
                Fps = result.Fps,
                DurationMs = result.DurationMs,
                State = ClipState.Ready,
            };

            clip.FileName = await FileIntoLibraryAsync(result.FilePath, clip);
            if (string.IsNullOrEmpty(clip.FileName))
            {
                Notify?.Invoke("Could not save",
                    $"The clip could not be written to:\n{Library.Folder}\n\n" +
                    "It is still in the temporary folder; the log has the path.");
                Log.Write("clip left at " + result.FilePath);
                return;
            }

            // Into its game's or app's folder, made if needed: a Roblox clip
            // lands in "Roblox", a desktop one in "Desktop".
            if (s.AutoSortIntoFolders && Library.FolderForApp(clip.App, create: true) is string home)
            {
                if (await Task.Run(() => Library.MoveClip(clip, home)))
                    Log.Write($"Sorted into {home}");
            }

            clip.Size = SafeSize(Library.PathOf(clip));
            Library.Add(clip);

            // Saved and announced first; copying comes right after, so a slow
            // clipboard never holds anything up.
            Status("Saved.");
            Cue(CueKind.Saved);
            if (s.ShowLibraryOnFinish)
            {
                ShowLibrary();
                _libraryWindow?.Reveal(clip);
            }
            else NotifyClip?.Invoke(clip.IsGif ? "GIF saved" : "Clip saved", clip.Title, clip.Id);
            if (s.CopyFileOnFinish) _ = CopyThenSayAsync(clip);

            _ = FinishInBackgroundAsync(clip);

            // Like Gyazo: the link replaces the file on the clipboard when ready.
            if (upload || s.UploadAfterRecording) _ = UploadAsync(clip);
        }
        finally { _busy = false; }
    }

    private bool _fillingIn;
    private bool _overCapWarned;

    /// <summary>
    /// Clips that turned up in the folder (copied in, duplicated, or left by
    /// an older version) get their length and a thumbnail, one at a time in
    /// the background.
    /// </summary>
    private async Task FillInFoundClipsAsync()
    {
        if (_fillingIn) return;
        _fillingIn = true;
        try
        {
            bool changed = false;
            foreach (var clip in Library.Items.ToList())
            {
                var path = Library.PathOf(clip);
                if (!File.Exists(path)) continue;

                var thumb = Library.ThumbPathOf(clip);
                bool needsThumb = !clip.IsGif && (thumb == null || !File.Exists(thumb));
                bool needsDetails = clip.DurationMs <= 0 || clip.Width <= 0;
                if (!needsThumb && !needsDetails) continue;

                try
                {
                    if (needsDetails)
                    {
                        // Length, size and frame rate: what the editor, links
                        // and "Fit for Discord" need, for clips from anywhere.
                        var info = await ClipEditor.ProbeAsync(path);
                        if (info != null)
                        {
                            if (info.Duration > 0) clip.DurationMs = (long)(info.Duration * 1000);
                            if (info.Width > 0) { clip.Width = info.Width; clip.Height = info.Height; }
                            if (info.Fps > 0 && clip.Fps <= 0) clip.Fps = (int)Math.Round(info.Fps);
                            changed = true;
                        }
                    }
                    // Thumbnails come from Windows when the list shows the clip
                    // (one is made only for a clip Windows can't show).
                }
                catch (Exception ex) { Log.Error("fill in " + clip.FileName, ex); }
            }

            if (changed)
            {
                Library.Save();
                LibraryRefreshed?.Invoke();
            }
        }
        finally { _fillingIn = false; }
    }

    /// <summary>Thumbnail, size check and cleanup, none of which the user waits for.</summary>
    private async Task FinishInBackgroundAsync(ClipRecord clip)
    {
        try
        {
            var path = Library.PathOf(clip);

            // The on-screen timer starts at the click; the file starts at the
            // first captured frame. The file is the truth.
            var seconds = await MediaTools.DurationSecondsAsync(path);
            if (seconds > 0) clip.DurationMs = (long)(seconds * 1000);

            // No thumbnail made here: Windows provides it when the list shows
            // the clip (one is made only if Windows can't).
            Library.Save();
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(() => LibraryRefreshed?.Invoke()));


            await CheckDiskAsync(quiet: false);
        }
        catch (Exception ex) { Log.Error("post-finish", ex); }
    }

    /* ---- size for sharing ---------------------------------------------- */

    /// <summary>
    /// Local files skip the upload wait but run straight into Discord's
    /// attachment ceiling, which a two-minute 1080p clip clears easily. This
    /// is where that gets noticed rather than in the message box.
    /// </summary>
    /// <summary>
    /// Deletes a file, waiting for whatever has it open to let go: a few
    /// seconds of tries, then (if it's still in use) in the background for a
    /// few minutes. True if it's gone straight away.
    /// </summary>
    private async Task<bool> DeleteSoonAsync(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            if (!File.Exists(path)) return true;
            await Task.Delay(250);
        }
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 30 && File.Exists(path); i++)
            {
                await Task.Delay(10_000);
                try { File.Delete(path); } catch { }
            }
            if (!File.Exists(path)) Library.Recount();
        });
        return false;
    }

    /// <summary>
    /// Compress Size: makes a copy of the clip that fits
    /// <paramref name="targetBytes"/>, on the best encoder this PC has.
    /// </summary>
    public async Task<bool> CompressAsync(ClipRecord clip, long targetBytes, bool? keepOriginal = null)
    {
        var s = Settings.Current;
        if (targetBytes <= 0) return false;
        if (clip.Size > 0 && clip.Size <= targetBytes)
        {
            Status($"Already under {targetBytes / (1024 * 1024)} MB.");
            return false;
        }
        var source = Library.PathOf(clip);
        if (!File.Exists(source)) return false;

        clip.State = ClipState.Processing;
        clip.Note = "Compressing…";
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => LibraryRefreshed?.Invoke()));
        Status($"Compressing to {targetBytes / (1024 * 1024)} MB…");

        // Beside the original, named after the size it was made for.
        var mb = targetBytes / (1024 * 1024);
        var smallName = Path.Combine(LibraryService.FolderOf(clip),
            Path.GetFileNameWithoutExtension(clip.FileName) + $"_{mb}MB.mp4");
        var smallPath = Path.Combine(Library.Folder, smallName);
        Library.Filing(smallName, seconds: 3600);   // being written for a while

        bool ok;
        try
        {
            var hw = s.ShrinkOnGpu ? await ClipEditor.BestEncoderAsync() : EncoderKind.X264;
            ok = await MediaTools.ShrinkToFitAsync(source, smallPath, targetBytes, s.AudioBitrateKbps, hw);
        }
        catch (Exception ex) { Log.Error("compress", ex); ok = false; }   // never left showing "Compressing…"

        clip.State = ClipState.Ready;

        if (!ok)
        {
            clip.Note = "Could not compress";
            Library.Save();
            Status("Could not compress the clip.");
            Notify?.Invoke("Compress failed", "The log has the details.");
            return false;
        }

        // What it really came to. Over the size asked for (the clip too long
        // for it, say), that's said plainly rather than calling it that size.
        long made = SafeSize(smallPath);
        bool over = made > targetBytes;
        string madeText = $"{made / 1048576.0:0.#} MB";
        string label = over ? madeText : $"{mb} MB";
        string note = over ? $"Compressed to {madeText} (aimed for {mb} MB)" : $"Compressed to {mb} MB";
        if (over) Log.Write($"Compress: came to {madeText}, over the {mb} MB asked for");

        ClipRecord result;
        if (keepOriginal ?? s.KeepOriginalWhenShrinking)
        {
            // The compressed copy is a clip of its own, next to the original:
            // both show in the list, and deleting one never touches the other.
            clip.Note = null;
            result = new ClipRecord
            {
                Title = $"{clip.Title} ({label})",
                App = clip.App,
                FileName = smallName,
                Width = clip.Width, Height = clip.Height, Fps = clip.Fps, DurationMs = clip.DurationMs,
                Size = made,
                Created = clip.Created,
                Tags = clip.Tags.ToList(),
                Private = clip.Private,
                State = ClipState.Ready,
                Note = note,
            };
            Library.Add(result);
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => LibraryRefreshed?.Invoke()));
        }
        else
        {
            // Replaced: the smaller file takes the original's place. The
            // original is let go of first (the player or a hover preview
            // could still have it open, and then it quietly stayed on disk,
            // still counted towards the size limit with no clip to show for it).
            _libraryWindow?.LetGoOf(source);
            var thumb = Library.ThumbPathOf(clip);
            clip.OriginalFileName = null;
            clip.FileName = smallName;
            clip.Size = made;
            clip.Note = note;
            result = clip;
            Library.Save();
            if (!await DeleteSoonAsync(source))
                Log.Write($"Compress: the original is still in use; it will be deleted once it's free: {source}");
            if (thumb != null) { try { File.Delete(thumb); } catch { } }
        }
        // The size limit counts what's really on disk now.
        await Task.Run(() => Library.Recount());

        if (s.CopyFileOnFinish) _ = ShareService.CopyFileAsync(smallPath);
        Status($"Compressed to {result.SizeText}." + (over ? $" It couldn't get under {mb} MB." : "") + (s.CopyFileOnFinish ? " Copied, ready to paste." : ""));
        NotifyClip?.Invoke(over ? "Compressed, but over the size" : "Compressed",
            over ? $"{result.Title} came to {result.SizeText}; {mb} MB was too small for its length. Trim it, or pick a bigger size."
                 : $"{result.Title} is {result.SizeText}.", result.Id);
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => LibraryRefreshed?.Invoke()));
        return true;
    }

    /* ---- disk ---------------------------------------------------------- */

    /// <summary>
    /// Keeps an eye on the clips folder's size. It only ever tells: nothing
    /// is deleted automatically, and nothing is blocked. The gauge in the clip
    /// list shows where things stand; deleting is always the user's call.
    /// </summary>
    public Task CheckDiskAsync(bool quiet)
    {
        var s = Settings.Current;
        var disk = Library.Disk();

        if (disk.OverCap)
        {
            if (!_overCapWarned && !quiet)
            {
                _overCapWarned = true;
                Notify?.Invoke("Clips folder over its limit",
                    $"Using {Gb(disk.Used)} of the {Gb(disk.Cap)} you allowed. Nothing will be deleted " +
                    "and recording carries on. Delete clips you don't need, or raise the limit in Settings.");
            }
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => LibraryRefreshed?.Invoke()));
        }
        else if (disk.OverWarn && !_diskWarned && !quiet)
        {
            _diskWarned = true;
            Notify?.Invoke("Clips folder filling up",
                $"{Gb(disk.Used)} of {Gb(disk.Cap)} used. Nothing is ever deleted automatically; " +
                "clear out old clips when it suits you.");
        }
        else if (!disk.OverWarn)
        {
            _diskWarned = false;
            _overCapWarned = false;
        }
        return Task.CompletedTask;
    }

    private static string Gb(long b) =>
        b >= 1073741824L ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576.0:0} MB";

    /* ---- helpers ------------------------------------------------------- */

    private async Task<string> FileIntoLibraryAsync(string tempPath, ClipRecord clip)
    {
        try
        {
            Library.EnsureFolder();

            var stem = Sanitise(clip.Title);
            var ext = Path.GetExtension(tempPath);
            var name = $"{stem}{ext}";
            var dest = Path.Combine(Library.Folder, name);

            int n = 2;
            while (File.Exists(dest))
            {
                name = $"{stem} ({n++}){ext}";
                dest = Path.Combine(Library.Folder, name);
            }

            // Temp and library can sit on different drives, where Move is a
            // copy underneath and worth keeping off the UI thread. Marked as
            // Persocord's own first, so the folder watcher doesn't list it too.
            Library.Filing(name);
            await Task.Run(async () =>
            {
                // Something can hold the new file for a moment (an antivirus
                // scan, a thumbnail being made): tried again for a few seconds
                // rather than losing the clip.
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Move(tempPath, dest); return; }
                    catch (IOException ex) when (attempt < 12 && File.Exists(tempPath))
                    {
                        if (attempt == 1) Log.Write($"Filing the clip: the file is busy ({ex.Message.Trim()}); trying again");
                        // A move between drives copies first: a copy left behind by a failed try goes.
                        try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                        await Task.Delay(Math.Min(250 * attempt, 1500));
                    }
                    catch (IOException) when (File.Exists(tempPath))
                    {
                        // Still held after ~10 s: kept as a copy (the original in temp is
                        // cleared later by the temp cleanup), so the clip is never lost.
                        try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                        File.Copy(tempPath, dest);
                        Log.Write("Filing the clip: the file stayed busy; filed a copy instead");
                        return;
                    }
                }
            });
            return name;
        }
        catch (Exception ex)
        {
            Log.Error("file into library", ex);
            return "";
        }
    }

    private static readonly HashSet<char> BadNameChars = new(Path.GetInvalidFileNameChars());

    // Names Windows keeps for devices: a file can't be called these.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    private static string Sanitise(string title)
    {
        var clean = new string(title
            .Select(ch => BadNameChars.Contains(ch) ? '-' : ch)
            .ToArray())
            .Replace("—", "-")
            .Trim();

        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        if (clean.Length > 80) clean = clean[..80].TrimEnd();
        // Windows drops a trailing dot, and refuses device names.
        clean = clean.TrimEnd('.', ' ');
        if (DeviceNames.Contains(clean)) clean += " clip";
        return string.IsNullOrWhiteSpace(clean)
            ? $"Clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}"
            : clean;
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>
    /// Takes down what frames the capture area. Done the moment capture
    /// stops, so stopping feels instant even while the file is still being
    /// put together; only the bar stays, to show that work.
    /// </summary>
    private void TearDownCaptureOverlays()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try { _clicks?.Stop(); } catch { }
            try { _border?.Close(); } catch { }
            _clicks = null;
            _border = null;
        });
    }

    private void TearDownOverlays()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try { _clicks?.Stop(); } catch { }
            try { _border?.Close(); } catch { }
            try { _bar?.Close(); } catch { }
            _clicks = null;
            _border = null;
            _bar = null;
        });
    }

    private void Status(string text) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => StatusChanged?.Invoke(text)));

    private static int DetectRefresh()
    {
        try
        {
            var dm = new Native.DEVMODE();
            dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DEVMODE));
            if (Native.EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency > 1)
                return dm.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }

    /* ---- windows ------------------------------------------------------- */

    /* ---- editing --------------------------------------------------------- */

    private readonly System.Collections.Generic.Dictionary<string, Window> _editors = new();

    /// <summary>
    /// Before a clip is deleted: the player, the hover preview and its editor
    /// let go of the file, so Windows lets it go at once.
    /// </summary>
    public void ReleaseClip(ClipRecord clip)
    {
        try
        {
            _libraryWindow?.LetGoOf(Library.PathOf(clip));
            if (_editors.TryGetValue(clip.Id, out var editor)) editor.Close();
        }
        catch (Exception ex) { Log.Error("release clip", ex); }
    }

    public void OpenEditor(ClipRecord clip)
    {
        if (clip.IsGif)
        {
            Notify?.Invoke("Can't edit GIFs", "The editor works on video clips. Record as video to edit, then convert.");
            return;
        }
        // Nothing plays behind the editor (a hover preview or the clip player).
        _libraryWindow?.Hush();
        if (!File.Exists(Library.PathOf(clip)))
        {
            Notify?.Invoke("Can't edit", "That clip's file is gone.");
            return;
        }

        if (_editors.TryGetValue(clip.Id, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var editor = new StudioWindow(this, clip);
        _editors[clip.Id] = editor;
        editor.Closed += (_, _) => _editors.Remove(clip.Id);
        editor.Show();
    }


    /// <summary>
    /// Files an edited clip: as a new clip beside the original, or in place
    /// of it. Length, thumbnail and size checks then follow in the background
    /// as for a fresh recording.
    /// </summary>
    public async Task<bool> ApplyEditAsync(ClipRecord source, string editedFile, bool replace,
                                           int sourceWidth, int sourceHeight, EditPlan plan)
    {
        int w = plan.Crop?.Width ?? sourceWidth, h = plan.Crop?.Height ?? sourceHeight;
        long ms = (long)(plan.ResultSeconds * 1000);

        if (replace)
        {
            _libraryWindow?.LetGoOf(Library.PathOf(source));
            if (!await Task.Run(() => Library.ReplaceFile(source, editedFile))) return false;
            source.Width = w;
            source.Height = h;
            source.DurationMs = ms;
            source.Note = "Edited";
            Library.Save();
            LibraryRefreshed?.Invoke();
            _ = FinishInBackgroundAsync(source);
            return true;
        }

        var clip = new ClipRecord
        {
            App = source.App,
            Title = source.Title + " (edited)",
            Width = w,
            Height = h,
            Fps = source.Fps,
            DurationMs = ms,
            Private = source.Private,
            State = ClipState.Ready,
            Note = "Edited copy",
        };
        clip.FileName = await FileIntoLibraryAsync(editedFile, clip);
        if (string.IsNullOrEmpty(clip.FileName)) return false;

        // An edited copy sits next to its original.
        var sourceFolder = LibraryService.FolderOf(source);
        if (sourceFolder.Length > 0) await Task.Run(() => Library.MoveClip(clip, sourceFolder));

        clip.Size = SafeSize(Library.PathOf(clip));
        Library.Add(clip);
        LibraryRefreshed?.Invoke();
        _ = FinishInBackgroundAsync(clip);
        return true;
    }

    /// <summary>
    /// Turns a clip from another recorder (MKV, MOV, WebM…) into an MP4, the
    /// kind Discord plays inline. A straight copy when the contents allow,
    /// which is instant; otherwise re-encoded on the graphics card.
    /// </summary>
    public async Task ConvertToMp4Async(ClipRecord clip, Window owner)
    {
        var path = Library.PathOf(clip);
        var info = await ClipEditor.ProbeAsync(path);
        if (info == null || info.Duration <= 0)
        {
            Notify?.Invoke("Can't convert", "Persocord couldn't read this file.");
            return;
        }

        var choice = SaveChoiceDialog.Ask(owner, "Convert to MP4",
            "Make an MP4 copy and keep this file, or replace it with the MP4?",
            "Make an MP4 copy", "Replace with MP4");
        if (choice == SaveChoice.Cancel) return;

        var output = Path.Combine(Paths.Temp, $"conv_{Guid.NewGuid():N}.mp4");
        Status("Converting to MP4…");
        var progress = new Progress<double>(f => Status($"Converting to MP4… {f * 100:0}%"));
        var plan = new EditPlan { In = 0, Out = info.Duration };

        var (ok, instant) = await ClipEditor.ExportAsync(path, info, plan, output,
            await ClipEditor.BestEncoderAsync(), Settings.Current.QualityCq, progress, System.Threading.CancellationToken.None);
        if (!ok)
        {
            try { File.Delete(output); } catch { }
            Status("Conversion failed.");
            Notify?.Invoke("Conversion failed", "The file couldn't be converted. The log has the details.");
            return;
        }

        if (choice == SaveChoice.Second)
        {
            if (!await Task.Run(() => Library.ReplaceFile(clip, output, ".mp4")))
            {
                Notify?.Invoke("Conversion failed", "The MP4 was made but couldn't replace the original.");
                return;
            }
            Library.Save();
            LibraryRefreshed?.Invoke();
            _ = FinishInBackgroundAsync(clip);
        }
        else
        {
            var copy = new ClipRecord
            {
                App = clip.App,
                Title = clip.Title,
                Width = info.Width,
                Height = info.Height,
                Fps = (int)Math.Round(info.Fps),
                DurationMs = (long)(info.Duration * 1000),
                Created = clip.Created,
                Private = clip.Private,
                Tags = new System.Collections.Generic.List<string>(clip.Tags ?? new()),
                State = ClipState.Ready,
                Note = "Converted",
            };
            copy.FileName = await FileIntoLibraryAsync(output, copy);
            if (string.IsNullOrEmpty(copy.FileName))
            {
                Notify?.Invoke("Conversion failed", "The MP4 couldn't be put in the clips folder.");
                return;
            }
            copy.Size = SafeSize(Library.PathOf(copy));
            Library.Add(copy);
            LibraryRefreshed?.Invoke();
            _ = FinishInBackgroundAsync(copy);
        }

        Status(instant ? "Converted to MP4 instantly." : "Converted to MP4.");
    }

    private bool _joining;

    /// <summary>
    /// Joins clips, oldest first, into one new clip. Clips recorded with the
    /// same settings are joined instantly; mixed ones are re-encoded to match.
    /// </summary>
    public async Task JoinAsync(System.Collections.Generic.IReadOnlyList<ClipRecord> picked, Window owner)
    {
        if (_joining) return;
        var clips = picked.Where(c => !c.IsGif && File.Exists(Library.PathOf(c)))
                          .OrderBy(c => c.Created).ToList();
        if (clips.Count < 2)
        {
            Notify?.Invoke("Can't join", "Pick two or more video clips (GIFs can't be joined).");
            return;
        }

        var choice = SaveChoiceDialog.Ask(owner, "Join clips",
            $"Join {clips.Count} clips into one new clip, oldest first. Keep the originals?",
            "Join, keep originals", "Join, remove originals");
        if (choice == SaveChoice.Cancel) return;

        _joining = true;
        var output = Path.Combine(Paths.Temp, $"join_{Guid.NewGuid():N}.mp4");
        try
        {
            Status($"Joining {clips.Count} clips…");
            var hw = await ClipEditor.BestEncoderAsync();
            var progress = new Progress<double>(f => Status($"Joining {clips.Count} clips… {f * 100:0}%"));

            bool ok = await ClipEditor.JoinAsync(clips.Select(Library.PathOf).ToList(), output,
                hw, Settings.Current.QualityCq, progress, System.Threading.CancellationToken.None);
            if (!ok)
            {
                Status("Join failed.");
                Notify?.Invoke("Join failed", "The clips couldn't be joined. The log has the details.");
                try { File.Delete(output); } catch { }
                return;
            }

            var info = await ClipEditor.ProbeAsync(output);
            var joined = new ClipRecord
            {
                App = clips[0].App,
                Title = $"Joined · {clips[0].Title}",
                Width = info?.Width ?? clips[0].Width,
                Height = info?.Height ?? clips[0].Height,
                Fps = clips[0].Fps,
                DurationMs = (long)((info?.Duration ?? 0) * 1000),
                // One private clip makes the whole joined clip private.
                Private = clips.Any(c => c.Private),
                State = ClipState.Ready,
                Note = $"Joined from {clips.Count} clips",
            };
            joined.FileName = await FileIntoLibraryAsync(output, joined);
            if (string.IsNullOrEmpty(joined.FileName))
            {
                Notify?.Invoke("Join failed", "The joined clip couldn't be put in the clips folder.");
                return;
            }
            joined.Size = SafeSize(Library.PathOf(joined));
            Library.Add(joined);

            if (choice == SaveChoice.Second)
                foreach (var c in clips) { ReleaseClip(c); await Library.RemoveAsync(c); }

            LibraryRefreshed?.Invoke();
            _ = FinishInBackgroundAsync(joined);
            Status($"Joined {clips.Count} clips.");
        }
        finally { _joining = false; }
    }

    public void ShowLibrary()
    {
        Application.Current?.Dispatcher.Invoke(() => Guard("open the clip list", () =>
        {
            // Asked again while it's still opening (showing a window lets
            // Windows deliver the next click in the middle of it): once is enough.
            if (_openingLibrary) return;
            _openingLibrary = true;
            try
            {
                _libraryWindow ??= new LibraryWindow(this);
                _libraryWindow.Show();
                if (_libraryWindow.WindowState == WindowState.Minimized)
                    _libraryWindow.WindowState = WindowState.Normal;
                _libraryWindow.Activate();
                _libraryWindow.Refresh();
            }
            finally { _openingLibrary = false; }
        }));
    }

    private bool _openingLibrary;

    public void ShowSettings()
    {
        Application.Current?.Dispatcher.Invoke(() => Guard("open settings", () =>
        {
            if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }));
    }

    /// <summary>
    /// Runs UI work that is triggered from the tray icon. Those clicks arrive
    /// through native Windows code, and .NET kills the process outright when
    /// an exception crosses that boundary — no error, the app just vanishes.
    /// Catching here turns that into a message the user can read and send on.
    /// </summary>
    private static void Guard(string what, Action work)
    {
        try { work(); }
        catch (Exception ex)
        {
            Log.Write($"ERROR trying to {what}:\n{ex}");
            Dialog.Show(
                $"Persocord could not {what}.\n\n{Innermost(ex).Message}\n\n" +
                $"Full details are in:\n{Paths.LogFile}",
                "Persocord", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    private bool _shutDown;

    public void Shutdown()
    {
        if (_shutDown) return;   // reachable from the tray, the installer and exit
        _shutDown = true;

        // Off the window's thread: waiting here for work that needs this thread
        // stalled quitting for the whole four seconds.
        try { if (Recorder.IsRecording) Task.Run(() => Recorder.CancelAsync()).Wait(4000); } catch { }
        try { Replay.Stop(); Games.Dispose(); } catch { }
        TearDownOverlays();
        _hotkeys.Dispose();
        Library.Save();
        Library.Dispose();
        Settings.Save();
    }
}
