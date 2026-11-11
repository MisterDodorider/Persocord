using System.Collections.Generic;
using System;

namespace Persocord.Models;

public enum AudioMode { None, SystemOnly, MicOnly, Both }

public enum CaptureBackend { Auto, Ddagrab, Gdigrab }

public enum EncoderKind { Auto, Nvenc, Qsv, Amf, X264 }

/// <summary>
/// Video formats: H.264 plays everywhere; HEVC is smaller but not every
/// player has it; AV1 is smallest at the same quality and plays in Discord
/// on desktop and in browsers, though some phones can't.
/// </summary>
public enum VideoCodec { H264, Hevc, Av1 }

public enum ResolutionMode { Native, Fixed1080p, Fixed1440p, Fixed720p }


/// <summary>The hosts a clip can be uploaded to for a link.</summary>
public enum LinkService { Litterbox, Catbox, Uguu, Oshi }

/// <summary>
/// What to do with clips over Discord's ~80 MB limit for playing linked
/// videos inline.
/// </summary>
public enum BigClipMode
{
    Shrink,        // re-encode to ~75 MB first; plays inline, fully private
    FullViaX266,   // full size, played through x266.mov's embed page
    FullPlain,     // full size, plain link that won't play inline
}

/// <summary>What the user wants from a link: how long it lasts, whether it can
/// be killed early, and which hosts they are happy to use.</summary>
public class LinkChoice
{
    public string Duration { get; set; } = "3h";
    public bool NeedKill { get; set; }
    public List<LinkService> Services { get; set; } = DefaultServices();
    public BigClipMode BigClip { get; set; } = BigClipMode.Shrink;

    public static List<LinkService> DefaultServices() =>
        new() { LinkService.Uguu, LinkService.Catbox };

    /// <summary>Hosts that are no longer offered (Litterbox, Oshi) taken out of a saved choice.</summary>
    public static void Tidy(LinkChoice c)
    {
        c.Services ??= new List<LinkService>();
        c.Services.RemoveAll(h => h is LinkService.Litterbox or LinkService.Oshi);
        if (c.Services.Count == 0) c.Services.AddRange(DefaultServices());
        if (c.Duration is not ("3h" or "forever")) c.Duration = "3h";
        c.NeedKill = false;   // only Oshi could take a link down early
    }

    public LinkChoice Copy() => new()
    {
        Duration = Duration,
        NeedKill = NeedKill,
        BigClip = BigClip,
        Services = new List<LinkService>(Services ?? new()),
    };
}

/// <summary>What opening Persocord from its icon does.</summary>
public enum OpenAction { SelectArea, ClipList }

public enum CueKind { Start, Pause, Resume, Saved, Cancelled, Replay, Notification }

/// <summary>Where Persocord's notifications appear.</summary>
public enum ToastPosition { BottomRight, BottomCenter, BottomLeft, TopRight, TopCenter, TopLeft }

/// <summary>What clicking a notification about a clip does.</summary>
public enum ToastClick { ShowInList, OpenEditor, Play, JustClose }

/// <summary>The order of the clip list.</summary>
public enum ClipSort { Newest, Oldest, Largest, Longest, Name }

/// <summary>One cue sound: a Windows sound by file name, or a full path to any sound file.</summary>
public class SoundCue
{
    public bool Enabled { get; set; } = true;
    public string Sound { get; set; } = "";

    public SoundCue Copy() => new() { Enabled = Enabled, Sound = Sound };
}

public class AppSettings
{
    /// <summary>
    /// Which one-time upgrades have been applied to this file. Missing from
    /// older files, so it reads as 0 there and the upgrades run once.
    /// </summary>
    public int SettingsVersion { get; set; } = 0;

    /// <summary>The first-run setup has been finished or skipped.</summary>
    public bool SetupDone { get; set; } = false;

    // ---- library ----------------------------------------------------
    /// <summary>Empty means the default folder under the user's Videos.</summary>
    public string LibraryFolder { get; set; } = "";

    /// <summary>
    /// Written inside the library folder so the app can recognise it again
    /// after it is moved somewhere else.
    /// </summary>
    public string LibraryId { get; set; } = "";

    /// <summary>
    /// Hard ceiling as a share of the drive the library sits on. The library
    /// is never allowed past this, whatever else is configured.
    /// </summary>
    public double MaxDiskPercent { get; set; } = 10.0;



    /// <summary>
    /// Clips deleted by hand go to the Recycle Bin. Automatic cleanup still
    /// deletes for good, since freeing space is its whole purpose.
    /// </summary>
    public bool DeleteToRecycleBin { get; set; } = true;

    // ---- capture ----------------------------------------------------
    public int Fps { get; set; } = 60;
    public CaptureBackend Backend { get; set; } = CaptureBackend.Auto;
    public ResolutionMode Resolution { get; set; } = ResolutionMode.Fixed1080p;
    /// <summary>Only used when Resolution is Native; caps the long edge. 0 = uncapped.</summary>
    public int NativeMaxLongEdge { get; set; } = 0;

    public bool ShowCursor { get; set; } = true;
    public bool ShowClickHighlights { get; set; } = true;
    public int ClickHighlightSize { get; set; } = 42;

    public bool RememberLastRegion { get; set; } = true;
    public int[] LastRegion { get; set; } = Array.Empty<int>(); // x, y, w, h

    public bool ShowRecordingBar { get; set; } = true;
    public int[] LastBarPosition { get; set; } = Array.Empty<int>(); // x, y
    public bool ShowRecordingBorder { get; set; } = true;




    public int CountdownSeconds { get; set; } = 0;

    /// <summary>
    /// Recording stops here. Two minutes by default; set to 0 to never stop
    /// on its own and rely on the warning alone.
    /// </summary>
    public int MaxLengthSeconds { get; set; } = 120;

    /// <summary>The timer turns red here, ahead of the stop above.</summary>
    public int WarnAtSeconds { get; set; } = 105;
    /// <summary>Before 1.5, a separate switch for the warning; read once (off becomes 0 above), then gone.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? WarnPastSoftLimit { get; set; }

    // ---- audio ------------------------------------------------------
    public AudioMode Audio { get; set; } = AudioMode.None;
    public string? MicDeviceId { get; set; }
    public string? SystemDeviceId { get; set; }
    public int MicGainPercent { get; set; } = 100;
    public int SystemGainPercent { get; set; } = 100;
    public int AudioBitrateKbps { get; set; } = 192;

    // ---- encoding ---------------------------------------------------
    public EncoderKind Encoder { get; set; } = EncoderKind.Auto;
    /// <summary>0 = derive from resolution and frame rate.</summary>
    public int VideoBitrateKbps { get; set; } = 0;
    public string NvencPreset { get; set; } = "p5";

    /// <summary>
    /// Picture quality when the bitrate is automatic. Lower is sharper and
    /// bigger; 19 is close to indistinguishable from the screen for most
    /// content. Applies to every encoder.
    /// </summary>
    public int QualityCq { get; set; } = 19;

    // ---- gif --------------------------------------------------------
    public int GifMaxSeconds { get; set; } = 15;
    public int GifFps { get; set; } = 20;
    public int GifWidth { get; set; } = 640;

    // ---- sharing ----------------------------------------------------
    /// <summary>Copies the finished clip as a file, so Ctrl+V attaches it.</summary>
    public bool CopyFileOnFinish { get; set; } = true;



    /// <summary>Keep the full-quality file alongside the shrunk one.</summary>
    public bool KeepOriginalWhenShrinking { get; set; } = true;

    /// <summary>Compress Size re-encodes on the graphics card: much faster, slightly softer at tight sizes.</summary>
    public bool ShrinkOnGpu { get; set; } = true;

    // ---- behaviour --------------------------------------------------
    public bool ShowLibraryOnFinish { get; set; } = true;

    /// <summary>Files a new recording into a folder named after its app, when one exists.</summary>
    public bool AutoSortIntoFolders { get; set; } = true;

    /* ---- sound tracks ----------------------------------------------------- */


    /// <summary>Each app playing sound gets its own track (recordings and replays).</summary>
    public bool AppTracks { get; set; } = true;
    /// <summary>How many apps get their own track; any more share "Other apps". 1 to 10.</summary>
    public int MaxAppTracks { get; set; } = 4;
    /// <summary>
    /// Apps running as administrator still get their own track, through a
    /// helper Windows asks to approve (once per session, only when such an
    /// app plays sound). Off: their sound stays in All PC audio and the
    /// editor's Unknown track.
    /// </summary>
    public bool ElevatedAppTracks { get; set; } = true;

    /// <summary>
    /// Editor saves land every cut on the exact frame (re-encoding). Off (the
    /// default): trims and cuts save in seconds, each part from its keyframe.
    /// </summary>
    public bool EditExactCuts { get; set; } = false;

    /// <summary>
    /// Editor saves leave out the Mix: each sound track is in the file on its
    /// own (for video editors, who mute and balance them separately). Off by default.
    /// </summary>
    public bool EditSeparateTracks { get; set; } = false;

    /// <summary>Editor keys (only while the editor is open): split at the playhead, delete everything left of it, and right of it.</summary>
    public string EditorKeySplit { get; set; } = "Ctrl+B";
    public string EditorKeyCutLeft { get; set; } = "Q";
    public string EditorKeyCutRight { get; set; } = "E";

    /// <summary>A hotkey to switch the replay on or off (the choice is remembered).</summary>
    public string HotkeyToggleReplay { get; set; } = "";

    /* ---- replay: the last few seconds, saved on a hotkey ------------------- */

    /// <summary>Quick sizes offered by Compress Size, in MB.</summary>
    public List<int> CompressPresetsMb { get; set; } = new() { 10, 25, 50, 100, 200, 500 };

    /* ---- safety limits for recording work -------------------------------- */

    /// <summary>Most of the CPU FFmpeg may use, as a share of the whole CPU (hard cap).</summary>
    public int SafetyCpuPercent { get; set; } = 50;
    /// <summary>
    /// Enforce <see cref="SafetyCpuPercent"/> as a hard cap. Off by default:
    /// Windows enforces hard caps by pausing the process in bursts, which made
    /// replays choppy and let their sound fall behind.
    /// </summary>
    public bool SafetyCpuHardCap { get; set; } = false;
    /// <summary>Most memory FFmpeg may use (hard cap).</summary>
    public int SafetyRamMb { get; set; } = 1024;
    /// <summary>Video memory past which the replay restarts itself.</summary>
    public int SafetyVramMb { get; set; } = 512;
    /// <summary>
    /// FFmpeg waits behind games on the graphics card. Off by default: with a
    /// game keeping the card busy, the capture itself had to wait, and the
    /// replay dropped to 15-20 fps.
    /// </summary>
    public bool SafetyGpuLowPriority { get; set; } = false;

    /// <summary>
    /// Recording steps behind games on the graphics card only while the
    /// card's video memory is nearly full (at <see cref="SafetyVramFullPercent"/>).
    /// </summary>
    public bool SafetyGpuAdaptive { get; set; } = true;
    public int SafetyVramFullPercent { get; set; } = 95;

    /// <summary>The built-in player's volume (0-100), remembered between clips.</summary>
    public int PlayerVolume { get; set; } = 80;

    /// <summary>Outside folders whose videos show in Persocord (never moved or indexed).</summary>
    public List<string> PreviewFolders { get; set; } = new();


    /// <summary>Quiets the microphone between words (static, hiss, background).</summary>
    public bool MicNoiseGate { get; set; } = true;

    /// <summary>Turns down loud peaks (shouting) from the microphone.</summary>
    public bool MicLimiter { get; set; } = false;

    /// <summary>The mic is silenced below this level (dBFS) when the gate is on.</summary>
    public int MicGateDb { get; set; } = -38;
    /// <summary>AI noise suppression (RNNoise): 0 off, 1 low, 2 medium, 3 high.</summary>
    public int MicSuppression { get; set; } = 2;
    /// <summary>How loud the mic may get (dBFS) when the limit is on.</summary>
    public int MicLimiterDb { get; set; } = -8;

    /// <summary>
    /// A two-chip laptop whose screen is wired to the integrated chip: FFmpeg
    /// runs there and the replay records with its encoder. Set automatically.
    /// </summary>
    public bool CaptureOnScreenGpu { get; set; } = false;

    public bool ReplayEnabled { get; set; } = true;
    public int ReplaySeconds { get; set; } = 30;
    public string HotkeySaveReplay { get; set; } = "Alt+F10";

    /// <summary>Two more save setups, each with its own key and length (empty = off).</summary>
    public string HotkeySaveReplay2 { get; set; } = "";
    public int ReplaySeconds2 { get; set; } = 30;
    public string HotkeySaveReplay3 { get; set; } = "";
    public int ReplaySeconds3 { get; set; } = 60;

    /// <summary>The longest length any save setup asks for: what the loop keeps.</summary>
    public int ReplayLongest() => Math.Max(ReplaySeconds, Math.Max(
        string.IsNullOrWhiteSpace(HotkeySaveReplay2) ? 0 : ReplaySeconds2,
        string.IsNullOrWhiteSpace(HotkeySaveReplay3) ? 0 : ReplaySeconds3));

    /// <summary>Which screen: "" is the main one, otherwise a device name like \\.\DISPLAY2.</summary>
    public string ReplayMonitor { get; set; } = "";
    /// <summary>The chosen monitor's own ID, which doesn't change when Windows renumbers screens.</summary>
    public string ReplayMonitorId { get; set; } = "";
    /// <summary>Where it was ("x,y,w,h"), in case its ID can't be read.</summary>
    public string ReplayMonitorRect { get; set; } = "";
    /// <summary>Height of the saved video; 0 keeps the screen's own size.</summary>
    public int ReplayHeight { get; set; } = 1080;
    public int ReplayFps { get; set; } = 60;
    /// <summary>Constant-quality level (lower is better and bigger), used when no bitrate is set.</summary>
    public int ReplayQuality { get; set; } = 21;
    /// <summary>Fixed bitrate in kbps (shown in Mbps), used when <see cref="ReplayUseBitrate"/> is on.</summary>
    public int ReplayBitrateKbps { get; set; } = 12000;
    /// <summary>A fixed bitrate instead of a quality level.</summary>
    public bool ReplayUseBitrate { get; set; } = false;

    /// <summary>The replay's own video format and encoder, separate from recordings'.</summary>
    public VideoCodec ReplayCodec { get; set; } = VideoCodec.H264;
    public EncoderKind ReplayEncoder { get; set; } = EncoderKind.Auto;

    /// <summary>
    /// Moves the replay's sound earlier by this much (ms), making up for the
    /// short delay between a sound playing and it being captured.
    /// </summary>
    public int ReplaySoundSyncMs { get; set; } = 0;

    /// <summary>After a save, the next one only has what came after it.</summary>
    public bool ReplayStartOverAfterSave { get; set; } = false;

    /// <summary>The video format for area recordings (the encoder is <see cref="Encoder"/>).</summary>
    public VideoCodec RecordCodec { get; set; } = VideoCodec.H264;

    /// <summary>Area recordings use the replay's quality: format, encoder, size, frame rate and level or bitrate.</summary>
    public bool RecordLikeReplay { get; set; } = true;

    /// <summary>
    /// What an area recording actually uses: these settings, with the
    /// replay's quality in place of its own when it follows the replay.
    /// </summary>
    public AppSettings ForRecording()
    {
        if (!RecordLikeReplay) return this;
        var c = Clone();
        c.Fps = ReplayFps;
        c.Resolution = ReplayHeight switch
        {
            1440 => ResolutionMode.Fixed1440p,
            1080 => ResolutionMode.Fixed1080p,
            720 => ResolutionMode.Fixed720p,
            _ => ResolutionMode.Native,
        };
        c.NativeMaxLongEdge = 0;
        c.RecordCodec = ReplayCodec;
        c.Encoder = ReplayEncoder;
        c.QualityCq = ReplayQuality;
        c.VideoBitrateKbps = ReplayUseBitrate ? ReplayBitrateKbps : 0;
        return c;
    }
    public bool ReplayCursor { get; set; } = true;

    /// <summary>
    /// Resized replays are labelled as the graphics card's resizer produces
    /// them (full-range colour). Off uses the old labelling, for a driver
    /// whose resizer behaves differently.
    /// </summary>
    public bool ReplayScaleFullRange { get; set; } = true;

    public bool ReplayTrackPc { get; set; } = true;
    /// <summary>
    /// For PCs with fewer cores: the replay keeps PC audio and the mic but no
    /// track per app, and its recorders run behind everything else.
    /// </summary>
    public bool ReplayLight { get; set; } = false;
    public bool ReplayTrackMic { get; set; } = true;

    /// <summary>
    /// The replay runs only while a game is detected (and a few minutes after
    /// it closes), instead of all the time. Off by default.
    /// </summary>
    public bool ReplayGamesOnly { get; set; } = false;

    /// <summary>Programs added by hand as games (full paths), for ones Persocord doesn't recognise.</summary>
    public List<string> MyGames { get; set; } = new();

    /// <summary>
    /// What opening Persocord from its icon does: the clip list by default
    /// (asked during setup); picking an area straight away is the other choice.
    /// </summary>
    public OpenAction OpenAction { get; set; } = OpenAction.ClipList;

    /// <summary>
    /// What the Update button on the new-version banner does: download and
    /// install it (Persocord closes and reopens by itself), or, switched off,
    /// open the release page to get it by hand.
    /// </summary>
    public bool UpdateInstallsItself { get; set; } = true;

    // ---- sound cues -------------------------------------------------
    /// <summary>Master switch for every cue below.</summary>
    public bool PlaySounds { get; set; } = true;
    public int CueVolume { get; set; } = 70;


    // ---- share links -------------------------------------------------
    /// <summary>What the Upload button uses without asking.</summary>
    public LinkChoice LinkDefaults { get; set; } = new();

    /// <summary>Upload every finished recording with the defaults. Off unless chosen.</summary>
    public bool UploadAfterRecording { get; set; }

    /// <summary>Once ticked, the link warning shrinks to a small note.</summary>
    public bool LinkWarningAcknowledged { get; set; }

    public SoundCue CueStart { get; set; } = new() { Sound = "Speech On.wav" };
    public SoundCue CuePause { get; set; } = new() { Enabled = false, Sound = "Speech Sleep.wav" };
    public SoundCue CueResume { get; set; } = new() { Enabled = false, Sound = "Speech Disambiguation.wav" };
    public SoundCue CueSaved { get; set; } = new() { Sound = "notify.wav" };
    public SoundCue CueCancelled { get; set; } = new() { Enabled = false, Sound = "Speech Off.wav" };
    public SoundCue CueReplay { get; set; } = new() { Sound = "notify.wav" };
    /// <summary>Played with each notification. Off by default: most already come with their own cue.</summary>
    public SoundCue CueNotification { get; set; } = new() { Enabled = false, Sound = "Windows Information Bar.wav" };

    /* ---- notifications ---------------------------------------------------- */

    public bool ToastsEnabled { get; set; } = true;
    public ToastPosition ToastPosition { get; set; } = ToastPosition.BottomRight;
    /// <summary>The longest a notification stays up, in seconds (hovering keeps it open).</summary>
    public double ToastSeconds { get; set; } = 4;
    public ToastClick ToastClickAction { get; set; } = ToastClick.ShowInList;

    /// <summary>Copies a saved replay to the clipboard (after it's saved and announced).</summary>
    public bool CopyReplayOnSave { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Tokens: {app} {date} {time} {w} {h} {fps}</summary>
    public string TitleTemplate { get; set; } = "{app} — {date} {time}";

    // ---- hotkeys ----------------------------------------------------
    // Only the two everyday keys are set at first; the rest are there to
    // set, empty until someone does (the control bar does them meanwhile).
    public string HotkeyRegion { get; set; } = "Ctrl+Shift+P";
    public string HotkeyPause { get; set; } = "";
    public string HotkeyStop { get; set; } = "";
    public string HotkeyCancel { get; set; } = "";

    // How Persocord looks lives in Looks (looks.json), apart from these settings.


    public ClipSort LibrarySort { get; set; } = ClipSort.Newest;

    public AppSettings Clone()
    {
        // Cues are objects, so a plain copy would share them and edits in the
        // settings window would leak through before Save.
        var c = (AppSettings)MemberwiseClone();
        c.CueStart = (CueStart ?? new()).Copy();
        c.CuePause = (CuePause ?? new()).Copy();
        c.CueResume = (CueResume ?? new()).Copy();
        c.CueSaved = (CueSaved ?? new()).Copy();
        c.CueCancelled = (CueCancelled ?? new()).Copy();
        c.CueReplay = (CueReplay ?? new()).Copy();
        c.CueNotification = (CueNotification ?? new()).Copy();
        c.LinkDefaults = (LinkDefaults ?? new()).Copy();
        // Lists too: an edit to a copy (Settings before it's saved) never reaches the original.
        c.CompressPresetsMb = new(CompressPresetsMb ?? new());
        c.PreviewFolders = new(PreviewFolders ?? new());
        c.MyGames = new(MyGames ?? new());
        return c;
    }

    public SoundCue CueFor(CueKind k) => k switch
    {
        CueKind.Start  => CueStart ??= new(),
        CueKind.Pause  => CuePause ??= new(),
        CueKind.Resume => CueResume ??= new(),
        CueKind.Saved  => CueSaved ??= new(),
        CueKind.Replay => CueReplay ??= new(),
        CueKind.Notification => CueNotification ??= new(),
        _              => CueCancelled ??= new(),
    };

    public static string CueLabel(CueKind k) => k switch
    {
        CueKind.Start  => "Recording starts",
        CueKind.Pause  => "Paused",
        CueKind.Resume => "Resumed",
        CueKind.Saved  => "Recording saved",
        CueKind.Replay => "Replay saved",
        CueKind.Notification => "Notification pops up",
        _              => "Recording discarded",
    };
}
