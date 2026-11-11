using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// Loads and saves settings so that nothing is lost across updates.
///
/// Three rules make that hold. The file lives in AppData, which installing
/// or updating the app never touches. A file that cannot be read in full is
/// salvaged option by option rather than replaced with defaults, and the
/// original is set aside before anything is written over it. And every save
/// goes to a temporary file first and is swapped in whole, so a crash
/// mid-write cannot leave a half-written file behind.
/// </summary>
public class SettingsService
{
    /// <summary>Bump when adding a one-time upgrade in <see cref="Upgrade"/>.</summary>
    public const int LatestVersion = 11;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string BackupFile => Paths.SettingsFile + ".backup";

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? Changed;

    /// <summary>True once the saved settings have been read.</summary>
    public bool IsLoaded { get; private set; }

    public void Load()
    {
        IsLoaded = true;
        var loaded = Read(Paths.SettingsFile, isBackup: false)
                  ?? Read(BackupFile, isBackup: true);

        if (loaded == null)
        {
            // Genuinely first run: nothing to upgrade.
            Current = new AppSettings { SettingsVersion = LatestVersion };
            Save();
            return;
        }

        Current = loaded;
        Tidy(Current);
        if (Upgrade(Current)) Save();

        // Keep one known-good copy to fall back on.
        try
        {
            if (File.Exists(Paths.SettingsFile))
                File.Copy(Paths.SettingsFile, BackupFile, true);
        }
        catch (Exception ex) { Log.Error("settings backup", ex); }
    }

    private static AppSettings? Read(string path, bool isBackup)
    {
        if (!File.Exists(path)) return null;

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { Log.Error("settings read", ex); return null; }

        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(text, Opts);
            if (s != null)
            {
                if (isBackup) Log.Write("Settings restored from backup");
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Settings file did not read cleanly ({ex.Message}); salvaging what it can");
        }

        // Something in the file did not fit, typically one option whose type
        // changed. Rather than throw everything away, keep every option that
        // still reads and default only the ones that do not.
        var salvaged = Salvage(text);
        SetAside(path);
        return salvaged;
    }

    private static AppSettings? Salvage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var result = new AppSettings();
            foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite) continue;

                foreach (var el in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(el.Name, prop.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var value = JsonSerializer.Deserialize(el.Value.GetRawText(), prop.PropertyType, Opts);
                        prop.SetValue(result, value);
                    }
                    catch
                    {
                        Log.Write($"Setting '{prop.Name}' could not be read and was reset");
                    }
                    break;
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("settings salvage", ex);
            return null;
        }
    }

    /// <summary>Keeps a damaged file next to the real one instead of overwriting it.</summary>
    private static void SetAside(string path)
    {
        try
        {
            var copy = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, copy, true);
            Log.Write($"Kept the unreadable settings file as {copy}");
        }
        catch (Exception ex) { Log.Error("settings set aside", ex); }
    }

    /// <summary>
    /// One-time changes to existing settings, each applied exactly once and in
    /// order. These are the only place an update is allowed to alter a saved
    /// choice, and each needs a clear reason.
    /// </summary>
    private static bool Upgrade(AppSettings s)
    {
        bool changed = false;

        if (s.SettingsVersion < 1)
        {
            // 1.1: desktop sound starts off, as requested. Only the old
            // default is switched; a microphone choice is left as it was.
            if (s.Audio == AudioMode.SystemOnly) s.Audio = AudioMode.None;
            else if (s.Audio == AudioMode.Both) s.Audio = AudioMode.MicOnly;
            s.SettingsVersion = 1;
            changed = true;
        }

        if (s.SettingsVersion < 2)
        {
            // 1.4: the microphone is no longer opened on every recording just
            // in case, which kept Windows' microphone indicator lit. Anyone
            // recording with the mic on is unaffected.
            s.SettingsVersion = 2;
            changed = true;
        }

        if (s.SettingsVersion < 3)
        {
            // 1.6: 0x0.st stopped taking uploads; Oshi and Uguu were added.
            s.LinkDefaults ??= new LinkChoice();
            var hosts = s.LinkDefaults.Services ??= new List<LinkService>();
            foreach (var h in new[] { LinkService.Uguu, LinkService.Oshi })
                if (!hosts.Contains(h)) hosts.Add(h);
            s.SettingsVersion = 3;
            changed = true;
        }

        if (s.SettingsVersion < 4)
        {
            // 1.10: the recording frame became brushed metal (now part of
            // the look, see LookStore).
            s.SettingsVersion = 4;
            changed = true;
        }

        if (s.SettingsVersion < 5)
        {
            // 1.12: the clip list opens after each recording by default.
            s.ShowLibraryOnFinish = true;
            s.SettingsVersion = 5;
            changed = true;
        }

        if (s.SettingsVersion < 6)
        {
            // 1.17: bitrate became a mode of its own; a bitrate already typed
            // in keeps being used.
            if (s.ReplayBitrateKbps > 0) s.ReplayUseBitrate = true;
            else s.ReplayBitrateKbps = 12000;
            s.SettingsVersion = 6;
            changed = true;
        }

        if (s.SettingsVersion < 7)
        {
            // 1.18.2: the first safety limits were too tight. A hard CPU cap
            // of 20% froze FFmpeg in short bursts, and low GPU priority made
            // the capture wait behind the game; together, 15-20 fps replays.
            if (s.SafetyCpuPercent <= 20) s.SafetyCpuPercent = 50;
            s.SafetyGpuLowPriority = false;
            s.SettingsVersion = 7;
            changed = true;
        }

        if (s.SettingsVersion < 8)
        {
            // 1.18.4: the hard CPU cap became optional (soft limit by default).
            s.SafetyCpuHardCap = false;
            s.SettingsVersion = 8;
            changed = true;
        }

        if (s.SettingsVersion < 9)
        {
            // 1.0.0: Persocord starts with Windows by default.
            s.StartWithWindows = true;
            s.SettingsVersion = 9;
            changed = true;
        }

        if (s.SettingsVersion < 10)
        {
            // 1.5: everyone goes through the new setup once. It shows their
            // settings as they are and only changes what they change there.
            // Area recordings keep the quality chosen rather than switching
            // to the replay's.
            s.SetupDone = false;
            s.RecordLikeReplay = false;
            // The warning's on/off switch became "0 = never".
            if (s.WarnPastSoftLimit == false) s.WarnAtSeconds = 0;
            s.WarnPastSoftLimit = null;
            s.SettingsVersion = 10;
            changed = true;
        }

        if (s.SettingsVersion < 11)
        {
            // 1.5.60: Litterbox (server errors) and Oshi (blocked or its
            // certificate failing on many networks) were removed as hosts.
            s.LinkDefaults ??= new LinkChoice();
            LinkChoice.Tidy(s.LinkDefaults);
            s.SettingsVersion = 11;
            changed = true;
        }

        return changed;
    }

    private static readonly System.Text.RegularExpressions.Regex NvencPresetName = new("^p[1-7]$");

    /// <summary>
    /// Every value a real one, whatever wrote the file (an older version, a hand
    /// edit, an imported file or code): nothing missing, every choice one that
    /// exists, and nothing that reaches FFmpeg's command line but the few
    /// values it's meant to take. Only broken values change; good ones never do.
    /// </summary>
    public static void Tidy(AppSettings s)
    {
        var std = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            var t = prop.PropertyType;
            var v = prop.GetValue(s);
            if (t.IsEnum) { if (v == null || !Enum.IsDefined(t, v)) prop.SetValue(s, prop.GetValue(std)); }
            else if (t == typeof(double)) { if (v is double d && (double.IsNaN(d) || double.IsInfinity(d))) prop.SetValue(s, prop.GetValue(std)); }
            // Nothing that should be there is ever missing (the PC-specific ones may be).
            else if (v == null && Nullable.GetUnderlyingType(t) == null && prop.Name is not (nameof(AppSettings.MicDeviceId) or nameof(AppSettings.SystemDeviceId)))
                prop.SetValue(s, prop.GetValue(std));
        }
        // Goes into FFmpeg's command line: only ever one of NVIDIA's own presets.
        if (!NvencPresetName.IsMatch(s.NvencPreset)) s.NvencPreset = std.NvencPreset;
        s.LinkDefaults.Services ??= LinkChoice.DefaultServices();
        s.LinkDefaults.Duration ??= std.LinkDefaults.Duration;
        foreach (var k in Enum.GetValues<CueKind>()) s.CueFor(k).Sound ??= "";
        s.PreviewFolders.RemoveAll(f => string.IsNullOrWhiteSpace(f));
        // Only real program paths, each once, and never so many it's a mistake.
        s.MyGames = s.MyGames.Where(g => !string.IsNullOrWhiteSpace(g) && g.Length < 1024 &&
                                          g.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
        s.CompressPresetsMb.RemoveAll(mb => mb <= 0);
        if (s.CompressPresetsMb.Count == 0) s.CompressPresetsMb.AddRange(std.CompressPresetsMb);
        // Numbers that can't be zero or less without breaking a recording.
        if (s.Fps <= 0) s.Fps = std.Fps;
        if (s.ReplayFps <= 0) s.ReplayFps = std.ReplayFps;
        if (s.ReplaySeconds <= 0) s.ReplaySeconds = std.ReplaySeconds;
        if (s.ReplaySeconds2 <= 0) s.ReplaySeconds2 = std.ReplaySeconds2;
        if (s.ReplaySeconds3 <= 0) s.ReplaySeconds3 = std.ReplaySeconds3;
        if (s.MaxAppTracks is < 1 or > 10) s.MaxAppTracks = Math.Clamp(s.MaxAppTracks, 1, 10);
        if (s.ReplayHeight < 0) s.ReplayHeight = std.ReplayHeight;
    }

    /* ---- reset, export, import -------------------------------------------- */

    /// <summary>
    /// Every option back to how Persocord ships, except where the clips live:
    /// the clips folder and its identity are never touched, so no clip is lost.
    /// </summary>
    public void ResetToDefaults()
    {
        var keep = Current;
        var fresh = new AppSettings
        {
            SettingsVersion = LatestVersion,
            LibraryFolder = keep.LibraryFolder,
            LibraryId = keep.LibraryId,
            SetupDone = true,
        };
        Save(fresh);
        Log.Write("Settings reset to defaults (clips folder kept)");
    }

    /// <summary>Writes every option to a file (not where the clips live).</summary>
    public void Export(string path)
    {
        var copy = Current.Clone();
        copy.LibraryFolder = "";
        copy.LibraryId = "";
        File.WriteAllText(path, JsonSerializer.Serialize(copy, Opts));
    }

    /// <summary>
    /// Reads options from an exported file. The clips folder stays as it is
    /// here; older files are upgraded like saved settings are.
    /// Returns an error message, or null when it worked.
    /// </summary>
    public string? Import(string path)
    {
        try
        {
            // A settings file is a few kilobytes; anything far bigger isn't one.
            if (new FileInfo(path).Length > 1024 * 1024) return "That file isn't a Persocord settings file (or it's damaged).";
            var imported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
            if (imported == null) return "That file doesn't contain Persocord settings.";
            imported.LibraryFolder = Current.LibraryFolder;
            imported.LibraryId = Current.LibraryId;
            Tidy(imported);
            Upgrade(imported);
            Save(imported);
            Log.Write("Settings imported from " + Path.GetFileName(path));
            return null;
        }
        catch (JsonException) { return "That file isn't a Persocord settings file (or it's damaged)."; }
        catch (Exception ex) { Log.Error("import settings", ex); return "The file couldn't be read."; }
    }

    public void Save(AppSettings? s = null)
    {
        if (s != null) Current = s;
        try
        {
            Tidy(Current);
            WriteWhole(Paths.SettingsFile, JsonSerializer.Serialize(Current, Opts));
            ApplyStartWithWindows(Current.StartWithWindows);
            Changed?.Invoke(Current);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    /// <summary>
    /// Writes to a temporary file and swaps it in, so the real file is always
    /// either the old version or the new one, never half of each.
    /// </summary>
    public static void WriteWhole(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void ApplyStartWithWindows(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            // Written on every save, so after an install moves the app the
            // startup entry follows it the next time anything is saved.
            // --tray keeps a sign-in launch silent.
            if (on) key.SetValue("Persocord", $"\"{Environment.ProcessPath}\" --tray");
            else    key.DeleteValue("Persocord", false);
        }
        catch (Exception ex) { Log.Error("startup registry", ex); }
    }
}
