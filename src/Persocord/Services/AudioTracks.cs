using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

public enum TrackKind { Pc, App, OtherApps, Mic }

public sealed class AudioTrack
{
    public TrackKind Kind { get; init; }
    public AudioEngine Engine { get; init; } = null!;
    public string Name { get; set; } = "";

    // For app tracks: which app it carries now, and which it has carried when.
    public int? Pid { get; set; }
    public List<(string Name, DateTime From, DateTime? To)> History { get; } = new();

    /// <summary>Set once a silent capture has been reported, so it's logged only once.</summary>
    public bool ReportedSilent { get; set; }
}

/// <summary>
/// Sound kept as separate tracks:
///  - PC audio: everything the PC plays (minus Persocord's own sounds). This
///    is always complete, so nothing is ever missing from the clip.
///  - One track per app playing sound (a game, Discord, a browser…), up to a
///    set number. Tracks are handed out as apps start making sound and keep
///    their app until it closes.
///  - Other apps: every app beyond that number, mixed together.
///  - Microphone, opened only while it's on.
/// Saved files get "PC audio + microphone" as their main sound (what
/// players and Discord play), then each track on its own for the editor.
/// Capturing an app costs almost nothing; each extra track costs its sound
/// encoding, roughly 1% of one CPU core.
/// </summary>
public sealed class AudioTracks : IDisposable
{
    public const int MaxApps = 10;

    public List<AudioTrack> Tracks { get; } = new();
    private Timer? _watch;

    /// <summary>An app whose sound can't get a track of its own (it runs as administrator). Raised once per app.</summary>
    public static event Action<string>? AppNotSeparable;
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);
    private int _checking;

    private AudioTracks() { }

    public static async Task<AudioTracks> CreateAsync(
        AppSettings s, bool pc, bool apps, bool mic, bool micOn, bool pcMuted)
    {
        var t = new AudioTracks();
        if (pc) t.Tracks.Add(new AudioTrack { Kind = TrackKind.Pc, Name = "PC audio", Engine = new AudioEngine(s, true, false, pcMuted, true) });

        if (apps)
        {
            int n = Math.Clamp(s.MaxAppTracks, 1, MaxApps);
            for (int i = 0; i < n; i++)
                t.Tracks.Add(new AudioTrack { Kind = TrackKind.App, Engine = new AudioEngine(s, false, false, pcMuted, true) });
            t.Tracks.Add(new AudioTrack { Kind = TrackKind.OtherApps, Name = "Other apps", Engine = new AudioEngine(s, false, false, pcMuted, true) });
        }

        if (mic)
            t.Tracks.Add(new AudioTrack { Kind = TrackKind.Mic, Name = "Microphone", Engine = new AudioEngine(s, false, micOn, true, !micOn) });

        if (apps)
        {
            await t.CheckAppsAsync();
            t._watch = new Timer(_ => _ = t.CheckAppsAsync(), null, 2000, 2000);
        }
        return t;
    }

    /* ---- following the apps ------------------------------------------------ */

    /// <summary>
    /// Gives each newly audible app a free track (or "Other apps" when none
    /// is free), and frees the track of an app that has closed.
    /// </summary>
    private async Task CheckAppsAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var now = DateTime.Now;
            var slots = Tracks.Where(x => x.Kind == TrackKind.App).ToList();
            var other = Tracks.FirstOrDefault(x => x.Kind == TrackKind.OtherApps);
            if (other == null) return;

            // Closed apps give their track back.
            foreach (var slot in slots.Where(x => x.Pid != null && !Alive(x.Pid.Value)))
            {
                Close(slot, now);
                slot.Pid = null;
                await slot.Engine.SetAppsAsync(Array.Empty<int>());
            }
            // A track whose app has been playing for a while but whose capture
            // has delivered nothing but silence: say so once, in the log.
            foreach (var slot in slots.Where(x => x.Pid != null && !x.ReportedSilent && !x.Engine.AppSoundHeard))
            {
                DateTime since;
                lock (slot.History) since = slot.History.LastOrDefault().From;
                if (since != default && now - since > TimeSpan.FromSeconds(10))
                {
                    slot.ReportedSilent = true;
                    Log.Write($"No sound has come through for {slot.Name} yet (captures running: {slot.Engine.AppCaptures}). " +
                              $"Its track stays out of clips until it carries sound; its sound is still in All PC audio.");
                }
            }

            // Apps in "Other apps" that have closed leave it.
            foreach (var pid in _others.Keys.Where(p => !Alive(p)).ToList())
            {
                CloseOne(other, _others[pid], now);
                _others.Remove(pid);
            }

            foreach (var app in await Task.Run(AppAudio.Playing))
            {
                if (slots.Any(x => x.Pid == app.RootPid) || _others.ContainsKey(app.RootPid)) continue;

                var free = slots.FirstOrDefault(x => x.Pid == null);
                if (app.Elevated)
                    Log.Write($"{app.Name} ({app.Exe}) runs as administrator: Windows won't let Persocord record its sound " +
                              (free != null && ElevatedAudio.Enabled && !ElevatedAudio.Declined
                                  ? "directly, so it goes through the administrator helper."
                                  : "on a track of its own. It's still in All PC audio (and the editor's Unknown track)."));

                if (free != null)
                {
                    free.Pid = app.RootPid;
                    free.Name = app.Name;
                    free.ReportedSilent = false;
                    lock (free.History) free.History.Add((app.Name, now, null));
                    if (app.Elevated)
                    {
                        // Can wait on the person answering Windows, so the other apps don't wait for it.
                        var (slot, a) = (free, app);
                        _ = Task.Run(async () =>
                        {
                            if (!await slot.Engine.SetElevatedAppAsync(a.RootPid, a.Name, () => slot.Pid == a.RootPid)) NotSeparable(a.Name);
                        });
                    }
                    else await free.Engine.SetAppsAsync(new[] { app.RootPid });
                    Log.Write($"Sound track for {app.Name}");
                }
                else
                {
                    if (app.Elevated) NotSeparable(app.Name);
                    // Past the limit: mixed into "Other apps".
                    _others[app.RootPid] = app.Name;
                    lock (other.History) other.History.Add((app.Name, now, null));
                    Log.Write($"{app.Name} joins Other apps");
                }
            }
            await other.Engine.SetAppsAsync(_others.Keys.ToList());
        }
        catch (Exception ex) { Log.Error("app tracks", ex); }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    private readonly Dictionary<int, string> _others = new();

    /// <summary>Tells the person once per app that its sound couldn't get a track of its own.</summary>
    private static void NotSeparable(string name)
    {
        bool first;
        lock (Warned) first = Warned.Add(name);
        if (first) AppNotSeparable?.Invoke(name);
    }

    // The history is read by a save (on another thread) while apps come and go:
    // every change and read holds it, so a save never meets it half-changed.
    private static void CloseOne(AudioTrack t, string name, DateTime now)
    {
        lock (t.History)
        for (int i = t.History.Count - 1; i >= 0; i--)
            if (t.History[i].To == null && t.History[i].Name == name) { t.History[i] = (name, t.History[i].From, now); return; }
    }

    private static void Close(AudioTrack t, DateTime now)
    {
        lock (t.History)
        for (int i = 0; i < t.History.Count; i++)
            if (t.History[i].To == null) t.History[i] = (t.History[i].Name, t.History[i].From, now);
    }

    private static bool Alive(int pid) => Native.ProcessAlive(pid);

    /* ---- what goes into a saved file --------------------------------------- */

    /// <summary>
    /// The tracks worth keeping for a clip covering <paramref name="from"/>
    /// until now: their stream numbers and names (app tracks named after the
    /// apps they carried then; unused ones left out), plus which of them
    /// make up the main sound.
    /// </summary>
    public (List<(int Stream, string Name)> Tracks, List<int> Mix) Layout(DateTime from)
    {
        var list = new List<(int, string)>();
        var onlyInPc = new List<string>();
        bool anyApps = false;
        for (int i = 0; i < Tracks.Count; i++)
        {
            var t = Tracks[i];
            switch (t.Kind)
            {
                case TrackKind.App:
                case TrackKind.OtherApps:
                    List<string> names;
                    lock (t.History)
                        names = t.History.Where(h => h.To == null || h.To >= from)
                                         .Select(h => h.Name).Distinct().ToList();
                    // Only tracks that actually carried sound: a silent one
                    // would make unticking in the editor lose everything. An
                    // app that played but couldn't be captured on its own is
                    // named on the PC track instead, so the editor shows its
                    // sound is only there.
                    if (names.Count == 0) continue;
                    if (!t.Engine.AppSoundHeard)
                    {
                        if (t.Kind == TrackKind.App) onlyInPc.AddRange(names);
                        continue;
                    }
                    anyApps = true;
                    list.Add((i, t.Kind == TrackKind.OtherApps
                        ? "Other apps (" + string.Join(", ", names) + ")"
                        : string.Join(" / ", names)));
                    break;
                default:
                    list.Add((i, t.Name));
                    break;
            }
        }

        // The main sound: all PC audio plus the mic when PC audio is there;
        // otherwise everything kept.
        var mix = list.Any(x => Tracks[x.Item1].Kind == TrackKind.Pc)
            ? list.Where(x => Tracks[x.Item1].Kind is TrackKind.Pc or TrackKind.Mic).Select(x => x.Item1).ToList()
            : list.Select(x => x.Item1).ToList();

        // With app tracks alongside, the complete PC track is labelled as such.
        if (anyApps || onlyInPc.Count > 0)
            for (int k = 0; k < list.Count; k++)
                if (Tracks[list[k].Item1].Kind == TrackKind.Pc)
                    list[k] = (list[k].Item1, onlyInPc.Count == 0 ? "All PC audio"
                        : $"All PC audio (incl. {string.Join(", ", onlyInPc.Distinct())})");
        return (list, mix);
    }

    /* ---- controls ------------------------------------------------------------ */


    public bool ExcludesOwnSounds => Tracks.FirstOrDefault(x => x.Kind == TrackKind.Pc)?.Engine.ExcludesOwnSounds ?? true;

    /// <summary>The PC-audio button: everything the PC plays.</summary>
    public bool SystemMuted
    {
        set
        {
            foreach (var x in Tracks.Where(x => x.Kind != TrackKind.Mic))
            {
                x.Engine.SystemMuted = value;
                x.Engine.ProcessMuted = value;
            }
        }
    }

    public void SetMicLive(bool on)
    {
        foreach (var x in Tracks.Where(x => x.Kind == TrackKind.Mic)) x.Engine.SetMicLive(on);
    }

    public void SuppressUntil(long ticks)
    {
        foreach (var x in Tracks.Where(x => x.Kind == TrackKind.Pc)) x.Engine.SuppressUntil(ticks);
    }

    public void OpenGate() { foreach (var x in Tracks) x.Engine.OpenGate(); }
    public void CloseGate() { foreach (var x in Tracks) x.Engine.CloseGate(); }
    public void Finish() { _watch?.Dispose(); foreach (var x in Tracks) x.Engine.Finish(); }

    public void Dispose()
    {
        _watch?.Dispose();
        foreach (var x in Tracks) { try { x.Engine.Dispose(); } catch { } }
    }

    /// <summary>FFmpeg inputs reading every track's pipe, in order.</summary>
    /// <summary>Which input is the microphone (-1 if there's none).</summary>
    public int MicIndex => Tracks.FindIndex(t => t.Kind == TrackKind.Mic);

    public string InputArgs()
    {
        var sb = new StringBuilder();
        foreach (var x in Tracks)
            // FFmpeg 9 removed -thread_queue_size for inputs (it rejects the
            // whole command); FFmpeg 8 still benefits from it.
            sb.Append($"-f s16le -ar {AudioEngine.Rate} -ac {AudioEngine.Channels} {(FFmpegVersion.Major < 9 ? "-thread_queue_size 1024 " : "")}" +
                      $"-i \"{x.Engine.PipePath}\" ");
        return sb.ToString();
    }
}

/// <summary>
/// Arguments for putting separate tracks into a finished file: the main
/// sound first (what players and Discord play), then each kept track, named.
/// </summary>
/// <summary>The microphone's AI noise suppression (RNNoise, in FFmpeg), from the settings.</summary>
public static class MicDenoise
{
    /// <summary>The filter for the mic track, or "" when off or the model isn't there.</summary>
    public static string Filter(AppSettings s)
    {
        double mix = s.MicSuppression switch { 1 => 0.5, 2 => 0.8, 3 => 1.0, _ => 0 };
        if (mix <= 0) return "";
        var model = System.IO.Path.Combine(AppContext.BaseDirectory, "voice.rnnn");
        if (!System.IO.File.Exists(model)) return "";
        // FFmpeg reads filter settings twice: the quotes protect the path the
        // first time, and the drive's colon must also be escaped (C\:/...)
        // for the second, or it's taken as the start of another setting.
        var path = model.Replace('\\', '/').Replace(":", "\\:");
        return $"arnndn=m='{path}':mix={mix.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
}

public static class TrackMux
{
    public record Parts(string Filter, string Maps, string Codecs, string Meta);

    /// <param name="input">FFmpeg input index holding the tracks as its audio streams.</param>
    /// <param name="tracks">Kept tracks: their audio stream number in that input, and name.</param>
    /// <param name="mix">Stream numbers making up the main sound.</param>
    public static Parts Build(int input, IReadOnlyList<(int Stream, string Name)> tracks, IReadOnlyList<int> mix, int kbps)
    {
        if (tracks.Count == 0) return new("", "", "", "");
        if (tracks.Count == 1)
            return new("", $"-map {input}:a:{tracks[0].Stream} ", "", Title(0, tracks[0].Name));

        var mixIns = mix.Count > 0 ? mix : tracks.Select(t => t.Stream).ToList();
        string main = mixIns.Count == 1
            ? $"[{input}:a:{mixIns[0]}]anull[mix]"
            : $"{string.Concat(mixIns.Select(i => $"[{input}:a:{i}]"))}amix=inputs={mixIns.Count}:normalize=0:dropout_transition=0[mix]";

        var filter = $"-filter_complex \"{main}\" ";
        var maps = "-map \"[mix]\" " + string.Concat(tracks.Select(t => $"-map {input}:a:{t.Stream} "));
        var codecs = $"-c:a:0 aac -b:a:0 {Math.Clamp(kbps, 96, 320)}k ";
        var meta = new StringBuilder(Title(0, "Mix") + "-disposition:a:0 default ");
        for (int i = 0; i < tracks.Count; i++)
            meta.Append(Title(i + 1, tracks[i].Name)).Append($"-disposition:a:{i + 1} 0 ");
        return new(filter, maps, codecs, meta.ToString());
    }

    /// <summary>A sound track's name in the saved file (never empty; kept safe inside its quotes).</summary>
    internal static string Title(int stream, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? (stream == 0 ? "Mix" : "App sound") : name.Trim().TrimEnd('\\');
        name = name.Replace("\"", "'");
        return $"-metadata:s:a:{stream} title=\"{name}\" -metadata:s:a:{stream} handler_name=\"{name}\" ";
    }
}

/// <summary>The bundled FFmpeg's major version (read once; 0 if unknown).</summary>
public static class FFmpegVersion
{
    private static readonly Lazy<int> _major = new(() =>
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Paths.FFmpeg, Arguments = "-hide_banner -version",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            if (p == null) return 0;
            var first = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(5000);
            // "ffmpeg version 9.0.2-essentials_build…" or "…N-12345-g…" (a nightly: newest)
            var m = System.Text.RegularExpressions.Regex.Match(first, @"version (\d+)\.");
            if (m.Success) return int.Parse(m.Groups[1].Value);
            return first.Contains("version N-") ? 99 : 0;
        }
        catch { return 0; }
    });

    public static int Major => _major.Value;
}
