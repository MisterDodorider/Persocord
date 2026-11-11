using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;

namespace Persocord.Services;

/// <summary>An app that's making sound right now. Elevated: it runs as administrator (and Persocord doesn't).</summary>
public sealed record AudibleApp(int RootPid, string Name, string Exe, bool Elevated = false);

/// <summary>
/// Which apps are actually playing sound, from Windows' own per-app volume
/// meters (the same ones the volume mixer shows). Nothing is recorded here;
/// it's a quick look, done every couple of seconds.
///
/// Apps are grouped by their top process: a browser's tabs all play through
/// one audio process under the browser, so a browser counts as one app,
/// and so does Discord with its helper processes.
/// </summary>
public static class AppAudio
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    // The sound devices and their session lists, opened once and kept (looked
    // over again every half minute, for devices plugged in or out). Opening them
    // afresh for every check, every 2 seconds on every device, is what made
    // Persocord's memory grow by tens of MB an hour with app tracks on.
    private static MMDeviceEnumerator? _enumerator;
    private static List<MMDevice> _devices = new();
    private static DateTime _devicesAt = DateTime.MinValue;

    private static List<MMDevice> Devices()
    {
        if (_enumerator != null && (DateTime.UtcNow - _devicesAt).TotalSeconds < 30) return _devices;
        foreach (var d in _devices) try { d.Dispose(); } catch { }
        _devices = new();
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            _devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        }
        catch (Exception ex) { Log.Error("sound devices", ex); }
        _devicesAt = DateTime.UtcNow;
        return _devices;
    }

    private static readonly object DevicesGate = new();

    /// <summary>The apps playing sound right now (one entry per app, by its top process).</summary>
    public static List<AudibleApp> Playing()
    {
        // One check at a time: the devices are shared between checks.
        lock (DevicesGate) return PlayingNow();
    }

    private static List<AudibleApp> PlayingNow()
    {
        var found = new Dictionary<int, AudibleApp>();
        Dictionary<int, (int Parent, string Exe)>? table = null;
        try
        {
            foreach (var dev in Devices())
            {
                try
                {
                    var mgr = dev.AudioSessionManager;   // made once per device, then reused
                    mgr.RefreshSessions();
                    var sessions = mgr.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var sc = sessions[i];
                        if (sc.IsSystemSoundsSession || sc.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                        if (sc.AudioMeterInformation.MasterPeakValue < 0.0005f) continue;   // open but silent
                        int pid = (int)sc.GetProcessID;
                        if (pid <= 0 || pid == Environment.ProcessId) continue;

                        lock (Known) if (Ignored.ContainsKey(pid)) { Ignored[pid] = DateTime.UtcNow; continue; }

                        // Worked out once per playing process: the list of every
                        // process on the PC is only taken for one not seen before.
                        lock (Known)
                            if (Known.TryGetValue(pid, out var seen) && Native.ProcessAlive(seen.App.RootPid))
                            {
                                Known[pid] = (seen.App, DateTime.UtcNow);
                                if (seen.App.RootPid != Environment.ProcessId) found.TryAdd(seen.App.RootPid, seen.App);
                                continue;
                            }

                        table ??= GameDetector.ProcessTable();
                        int root = Root(pid, table);
                        if (root == Environment.ProcessId || found.ContainsKey(root)) continue;
                        var exe = table.TryGetValue(root, out var r) ? r.Exe : "";
                        var ownExe = table.TryGetValue(pid, out var own) ? own.Exe : "";
                        // A program that has already closed: Windows keeps its sound
                        // session around for a few seconds. It got a nameless track
                        // that was given back and taken again every 2 seconds (seen
                        // right after a restart, from the old copy of Persocord).
                        if (exe.Length == 0 || !Native.ProcessAlive(root)) continue;
                        // Windows' own sound plumbing (audiodg plays every app's
                        // sound through its effects) isn't an app: no track, and
                        // never a reason to ask for administrator rights.
                        if (IsWindowsAudio(exe) || IsWindowsAudio(ownExe))
                        {
                            lock (Known) Ignored[pid] = DateTime.UtcNow;
                            continue;
                        }
                        bool elevated = !SelfElevated && (IsElevated(pid) == true || IsElevated(root) == true);
                        var app = new AudibleApp(root, FriendlyName(root, exe), exe, elevated);
                        found[root] = app;
                        lock (Known) Known[pid] = (app, DateTime.UtcNow);
                    }
                }
                catch (Exception ex) { Log.Error("audio sessions", ex); _devicesAt = DateTime.MinValue; }
            }
        }
        catch (Exception ex) { Log.Error("audio sessions", ex); }
        lock (Known)
        {
            foreach (var old in Known.Where(k => (DateTime.UtcNow - k.Value.Seen).TotalMinutes > 2).Select(k => k.Key).ToList())
                Known.Remove(old);
            foreach (var old in Ignored.Where(k => (DateTime.UtcNow - k.Value).TotalMinutes > 2).Select(k => k.Key).ToList())
                Ignored.Remove(old);
        }
        return found.Values.ToList();
    }

    private static readonly Dictionary<int, (AudibleApp App, DateTime Seen)> Known = new();
    private static readonly Dictionary<int, DateTime> Ignored = new();

    /// <summary>Windows' own processes that can show up as playing sound.</summary>
    private static readonly HashSet<string> WindowsAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg.exe", "svchost.exe", "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe",
        "services.exe", "lsass.exe", "dwm.exe", "winlogon.exe", "fontdrvhost.exe", "AudioSrv",
    };

    private static bool IsWindowsAudio(string exe) =>
        exe.Length > 0 && WindowsAudio.Contains(System.IO.Path.GetFileName(exe));

    /* ---- running as administrator ------------------------------------------ */

    // Windows won't let a normal app record the sound of a program running as
    // administrator: the capture just hears silence, with no error.

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static readonly bool SelfElevated = IsElevated(Environment.ProcessId) == true;

    /// <summary>Whether a process runs as administrator (null if it can't be told).</summary>
    public static bool? IsElevated(int pid)
    {
        const uint QueryLimited = 0x1000, TokenQuery = 0x0008;
        const int TokenElevation = 20, AccessDenied = 5;
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(h, TokenQuery, out var token))
                // A normal app may not look inside an administrator's process.
                return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == AccessDenied ? true : null;
            try { return GetTokenInformation(token, TokenElevation, out int e, 4, out _) ? e != 0 : null; }
            finally { CloseHandle(token); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>Climbs to the top process with the same program name (a browser, Discord…).</summary>
    private static int Root(int pid, Dictionary<int, (int Parent, string Exe)> table)
    {
        int cur = pid;
        for (int guard = 0; guard < 16; guard++)
        {
            if (!table.TryGetValue(cur, out var me)) break;
            if (!table.TryGetValue(me.Parent, out var parent)) break;
            if (!string.Equals(parent.Exe, me.Exe, StringComparison.OrdinalIgnoreCase)) break;
            cur = me.Parent;
        }
        return cur;
    }

    private static string FriendlyName(int pid, string exe)
    {
        if (Names.TryGetValue(exe, out var cached)) return cached;
        string name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            var path = GameDetector.ProcessPath(pid);
            if (path != null)
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                var n = !string.IsNullOrWhiteSpace(v.FileDescription) ? v.FileDescription : v.ProductName;
                if (!string.IsNullOrWhiteSpace(n) && n.Length <= 32) name = n.Trim();
            }
        }
        catch { }
        if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
        Names[exe] = name;
        return name;
    }
}
