using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Persocord.Services;

/// <summary>
/// Which graphics chip FFmpeg captures a screen through.
///
/// On laptops with two chips (Intel + NVIDIA), Windows can show the screen to
/// Persocord as hanging off one chip and to FFmpeg as hanging off the other
/// (each app's graphics preference changes what it sees). Capturing through
/// the chip Persocord sees then fails with "Failed to enumerate DXGI output".
/// When that happens the other chips are tried, and the one that works is
/// remembered per screen, so it's right from the start next time.
/// </summary>
public static class CaptureChip
{
    private static readonly object Gate = new();
    private static Dictionary<string, uint>? _map;
    private static string File_ => Path.Combine(Paths.Root, "capture-chip.txt");

    /// <summary>Vendor id 0 means "FFmpeg's own pick" (no chip named).</summary>
    public const uint FFmpegsPick = 0;

    /// <summary>FFmpeg's capture couldn't find the screen on the chip it was given.</summary>
    // Only that exact refusal: a locked PC or a game switching modes fails differently,
    // and must never move the capture off a chip that works.
    public static bool OutputMissing(string ffmpegErrors) =>
        ffmpegErrors.Contains("Failed to enumerate DXGI output", StringComparison.OrdinalIgnoreCase);

    /// <summary>The chips to try, in order: the one Persocord sees first, then the others, then FFmpeg's own pick.</summary>
    public static List<uint> Candidates(uint seen)
    {
        var list = new List<uint> { seen };
        foreach (var v in DxgiOutputs.AdapterVendors())
            if (!list.Contains(v)) list.Add(v);
        if (!list.Contains(FFmpegsPick)) list.Add(FFmpegsPick);
        return list;
    }

    /// <summary>The chip that worked for this screen before, if it isn't the one Persocord sees.</summary>
    public static uint? Learned(string screen)
    {
        lock (Gate) return Load().TryGetValue(screen, out var v) ? v : null;
    }

    /// <summary>The screen as FFmpeg should be told about it.</summary>
    public static DxgiTarget Apply(DxgiTarget t) =>
        Learned(t.DeviceName) is { } v && v != t.VendorId ? t with { VendorId = v, Adapter = 0 } : t;

    public static void Learn(string screen, uint vendor, uint seen)
    {
        lock (Gate)
        {
            var m = Load();
            if (vendor == seen) { if (!m.Remove(screen)) return; }
            else if (m.TryGetValue(screen, out var was) && was == vendor) return;
            else m[screen] = vendor;
            Save(m);
        }
    }

    public static void Forget(string screen)
    {
        lock (Gate)
        {
            var m = Load();
            if (m.Remove(screen)) Save(m);
        }
    }

    public static string Name(uint vendor) => vendor switch
    {
        0x10DE => "NVIDIA", 0x8086 => "Intel", 0x1002 or 0x1022 => "AMD",
        FFmpegsPick => "default", _ => "other",
    };

    private static Dictionary<string, uint> Load()
    {
        if (_map != null) return _map;
        _map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(File_))
                foreach (var line in File.ReadAllLines(File_))
                {
                    int eq = line.LastIndexOf('=');
                    if (eq > 0 && uint.TryParse(line[(eq + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var v))
                        _map[line[..eq]] = v;
                }
        }
        catch { }
        return _map;
    }

    private static void Save(Dictionary<string, uint> m)
    {
        try { File.WriteAllLines(File_, m.Select(kv => $"{kv.Key}={kv.Value:x4}")); }
        catch (Exception ex) { Log.Error("capture chip", ex); }
    }

    /// <summary>
    /// How often the screen is grabbed for a video of <paramref name="fps"/>
    /// frames a second; FFmpeg's fps filter then keeps exactly that many.
    /// Grabbing at just the video's rate missed a frame every so often: the
    /// capture paces itself by sleeping, and Windows gives FFmpeg a coarse
    /// sleep (about 15.6 ms against a 16.7 ms frame), so with any extra work
    /// a frame came too late and the one before was shown twice (measured:
    /// 14-20% of frames repeated while playing, about 5% grabbing at 240).
    /// Grabbing faster, it wakes the moment the game draws a new picture.
    /// At most 480: above that FFmpeg's wait for a picture rounds to nothing
    /// and it would spin the CPU.
    /// </summary>
    public static int GrabRate(int fps) => Math.Clamp(fps * 4, 240, 480);
}
