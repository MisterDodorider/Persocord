using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace Persocord.Services;

/// <summary>
/// Works out a sensible name for a clip from whatever sits under the capture
/// region. Falls back to a timestamp when the region spans windows or sits on
/// bare desktop, which is common enough not to treat as an error.
/// </summary>
public static class WindowTitleResolver
{
    public static string AppNameAt(Rectangle region)
    {
        try
        {
            var pt = new Native.POINT(
                region.X + region.Width / 2,
                region.Y + region.Height / 2);

            var hwnd = Native.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return "";

            hwnd = Native.GetAncestor(hwnd, 2); // GA_ROOT
            if (hwnd == IntPtr.Zero) return "";

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";

            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;

            if (IsShell(name))
            {
                // Region is over the desktop or taskbar; try the window that
                // actually has focus instead.
                var fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    Native.GetWindowThreadProcessId(fg, out uint fpid);
                    if (fpid != 0 && fpid != pid)
                    {
                        using var fp = Process.GetProcessById((int)fpid);
                        if (!IsShell(fp.ProcessName)) return Pretty(fp.ProcessName);
                    }
                }
                return "";
            }

            return Pretty(name);
        }
        catch { return ""; }
    }

    private static bool IsShell(string n) =>
        n.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
        n.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
        n.Equals("Persocord", StringComparison.OrdinalIgnoreCase) ||
        n.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);

    private static readonly System.Collections.Generic.Dictionary<string, string> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox",
            ["Discord"] = "Discord", ["RobloxPlayerBeta"] = "Roblox",
            ["RobloxStudioBeta"] = "Roblox Studio", ["blender"] = "Blender",
            ["UnrealEditor"] = "Unreal", ["Code"] = "VS Code",
            ["devenv"] = "Visual Studio", ["obs64"] = "OBS",
            ["vlc"] = "VLC", ["Spotify"] = "Spotify", ["steam"] = "Steam",
            ["WINWORD"] = "Word", ["EXCEL"] = "Excel", ["POWERPNT"] = "PowerPoint",
        };

    private static string Pretty(string proc)
    {
        if (Known.TryGetValue(proc, out var nice)) return nice;

        // Turn "SomeApp" into "Some App" so auto titles read like titles.
        var sb = new StringBuilder();
        for (int i = 0; i < proc.Length; i++)
        {
            if (i > 0 && char.IsUpper(proc[i]) && !char.IsUpper(proc[i - 1])) sb.Append(' ');
            sb.Append(proc[i]);
        }
        return sb.ToString();
    }

    public static string BuildTitle(string template, string app, int w, int h, int fps)
    {
        var now = DateTime.Now;
        var result = template
            .Replace("{app}", string.IsNullOrWhiteSpace(app) ? "Clip" : app)
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{w}", w.ToString())
            .Replace("{h}", h.ToString())
            .Replace("{fps}", fps.ToString());
        return result.Trim().Length == 0 ? $"Clip {now:yyyy-MM-dd HH:mm}" : result.Trim();
    }
}
