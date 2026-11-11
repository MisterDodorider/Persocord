using System;
using System.Runtime.InteropServices;

namespace Persocord.Services;

/// <summary>
/// Which physical monitor a screen is. Windows' names (DISPLAY1, DISPLAY2)
/// can swap when a monitor sleeps, wakes or reconnects; the monitor's own ID
/// doesn't, so a chosen screen stays chosen.
/// </summary>
public static class MonitorIdentity
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice dd, uint flags);

    /// <summary>The monitor on a screen: its lasting ID and its name (e.g. "LG ULTRAGEAR").</summary>
    public static (string Id, string Name) Of(string deviceName)
    {
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(deviceName, i, ref dd, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */)) break;
                if ((dd.StateFlags & 0x1) != 0 && !string.IsNullOrEmpty(dd.DeviceID))
                    return (dd.DeviceID, (dd.DeviceString ?? "").Trim());
            }
        }
        catch { }
        return ("", "");
    }

    public static string RectOf(System.Drawing.Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";
}
