using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>
/// Turns preferences into a short code to paste in chat, and back.
///
/// Only settings that differ from the defaults go in, which keeps the code
/// short. Anything tied to one PC never goes in: folders, audio devices,
/// the last recorded area, startup, and choices like the video encoder that
/// may not exist on a friend's hardware.
/// </summary>
public static class ShareCode
{
    private const string Prefix = "PERSO1-";

    private static readonly HashSet<string> Personal = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(AppSettings.SettingsVersion),
        nameof(AppSettings.LibraryFolder),
        nameof(AppSettings.ReplayMonitor),
        nameof(AppSettings.ReplayMonitorId),
        nameof(AppSettings.ReplayMonitorRect),
        nameof(AppSettings.LibraryId),
        nameof(AppSettings.LastRegion),
        nameof(AppSettings.MicDeviceId),
        nameof(AppSettings.SystemDeviceId),
        nameof(AppSettings.StartWithWindows),
        nameof(AppSettings.LinkWarningAcknowledged),
        nameof(AppSettings.Encoder),
        nameof(AppSettings.Backend),
        nameof(AppSettings.WarnPastSoftLimit),
        nameof(AppSettings.SetupDone),
        // Folders on this PC (their paths hold the Windows user name).
        nameof(AppSettings.PreviewFolders),
        nameof(AppSettings.LastBarPosition),
        // Programs on this PC (paths again).
        nameof(AppSettings.MyGames),
    };

    /// <summary>A settings code (never anything about how Persocord looks: that has its own code).</summary>
    public static string Export(AppSettings s)
    {
        // A cue that's a sound file on this PC stays here: its path holds the
        // Windows user name, and the file isn't on a friend's PC anyway.
        var copy = s.Clone();
        var std = new AppSettings();
        foreach (var k in Enum.GetValues<CueKind>())
            if (IsOwnFile(copy.CueFor(k).Sound)) copy.CueFor(k).Sound = std.CueFor(k).Sound;
        return DiffCode.Export(copy, std, Prefix, Personal);
    }

    /// <summary>A path to a file (not one of Windows' own sounds, which are given by name).</summary>
    private static bool IsOwnFile(string? sound) =>
        !string.IsNullOrEmpty(sound) && (sound.Contains('\\') || sound.Contains('/') || sound.Contains(':'));

    /// <summary>
    /// Applies a code's settings onto <paramref name="target"/>. Returns how
    /// many were applied, or throws with a plain reason if the code is bad.
    /// </summary>
    public static int Import(string code, AppSettings target)
    {
        if ((code ?? "").Contains(LookStore.CodePrefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That's a look code (colours and style). Import it under Settings → Customization.");
        var obj = DiffCode.Decode(code ?? "", Prefix, "a Persocord settings code");
        var before = target.Clone();
        int applied = DiffCode.Apply(obj, target, Personal);
        // A code never points Persocord at a file on this PC: such a cue keeps the sound it had.
        foreach (var k in Enum.GetValues<CueKind>())
            if (IsOwnFile(target.CueFor(k).Sound)) target.CueFor(k).Sound = before.CueFor(k).Sound;
        SettingsService.Tidy(target);
        return applied;
    }
}

/// <summary>
/// The machinery behind share codes: only what differs from the defaults,
/// as JSON, compressed, in characters that survive chat apps.
/// </summary>
public static class DiffCode
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Export<T>(T current, T defaults, string prefix, ISet<string> skip)
    {
        var mine = JsonSerializer.SerializeToNode(current, Opts)!.AsObject();
        var std = JsonSerializer.SerializeToNode(defaults, Opts)!.AsObject();

        var diff = new JsonObject();
        foreach (var (key, value) in mine)
        {
            if (skip.Contains(key)) continue;
            var d = std[key];
            if (value?.ToJsonString() == d?.ToJsonString()) continue;
            diff[key] = value?.DeepClone();
        }

        var bytes = Encoding.UTF8.GetBytes(diff.ToJsonString());
        using var packed = new MemoryStream();
        using (var brotli = new BrotliStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(bytes);

        return prefix + Convert.ToBase64String(packed.ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Codes are a few hundred characters; anything far bigger isn't a real one.</summary>
    public const int MaxCodeChars = 16_000;
    /// <summary>The most a code may unpack to (a "zip bomb" can't fill the memory).</summary>
    public const int MaxJsonBytes = 64 * 1024;

    public static JsonObject Decode(string code, string prefix, string what)
    {
        code = (code ?? "").Trim();
        if (code.Length > MaxCodeChars) throw new FormatException($"That's far too long to be {what}.");
        int at = code.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) throw new FormatException($"That isn't {what}. It should start with {prefix}");

        // Chat apps sometimes wrap or add spaces; ignore anything that
        // can't be part of the code.
        var body = new string(code[(at + prefix.Length)..]
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray())
            .Replace('-', '+').Replace('_', '/');
        body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');

        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(body));
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            // Unpacked a piece at a time, and stopped the moment it's too big.
            var buffer = new byte[8192];
            int n;
            while ((n = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + n > MaxJsonBytes) throw new InvalidDataException("too big");
                output.Write(buffer, 0, n);
            }
            // Plain data only: an object of values, nested at most a few levels.
            var node = JsonNode.Parse(output.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 6 });
            return node as JsonObject ?? throw new InvalidDataException("not an object");
        }
        catch
        {
            throw new FormatException("That code looks cut off or damaged. Copy the whole thing and try again.");
        }
    }

    /// <summary>Applies the values onto <paramref name="target"/>; unknown ones (from a newer version) are skipped.</summary>
    public static int Apply<T>(JsonObject obj, T target, ISet<string> skip) where T : notnull
    {
        int applied = 0;
        foreach (var (key, value) in obj)
        {
            if (skip.Contains(key) || value == null) continue;
            var prop = typeof(T).GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) continue;
            try
            {
                prop.SetValue(target, value.Deserialize(prop.PropertyType, Opts));
                applied++;
            }
            catch (Exception ex) { Log.Error("share code " + key, ex); }
        }
        return applied;
    }
}
