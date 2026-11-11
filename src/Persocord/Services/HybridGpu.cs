using System;
using Microsoft.Win32;

namespace Persocord.Services;

/// <summary>
/// Laptops with two graphics chips: the screen is often wired to the
/// integrated one (Intel), while FFmpeg may be started on the NVIDIA card,
/// which Windows then refuses screen capture from. Windows' own per-app
/// graphics preference (Settings → Display → Graphics) sets which chip
/// FFmpeg runs on; "power saving" is the one that drives the screen.
/// </summary>
public static class HybridGpu
{
    private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>Runs FFmpeg on the power-saving chip (true) or leaves it to Windows (false).</summary>
    public static void SetPowerSaving(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (k == null) return;
            if (on) k.SetValue(Paths.FFmpeg, "GpuPreference=1;");
            else if (k.GetValue(Paths.FFmpeg) != null) k.DeleteValue(Paths.FFmpeg, false);
        }
        catch (Exception ex) { Log.Error("graphics preference", ex); }
    }
}
