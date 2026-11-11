using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Persocord.Services;

/// <summary>A game that is running now.</summary>
public record DetectedGame(int Pid, string Name, string ExePath);

/// <summary>
/// Notices when a game is running, cheaply: a look every few seconds at the
/// window in front, checked against
///  - Windows' own list of games you've played (the registry keeps their
///    exact paths, so a match is certain),
///  - Discord's public list of detectable games (exe name to game name,
///    downloaded in the background and refreshed weekly),
///  - and, failing both, whether the window fills its screen like a game.
/// A detected game is remembered until its process exits, so alt-tabbing
/// away doesn't lose it.
/// </summary>
public sealed class GameDetector : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private HashSet<string> _windowsGames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _discordGames = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _poll;
    private DetectedGame? _current;

    /// <summary>Apps that fill the screen but aren't games.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "discord",
        "discordptb", "discordcanary", "vlc", "mpc-hc64", "mpv", "potplayermini64", "obs64", "persocord",
        "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "lockapp", "textinputhost", "code", "devenv", "powerpnt", "winword", "excel", "spotify",
        // Screenshot tools and overlays cover the screen for a moment.
        "lightshot", "sharex", "greenshot", "snippingtool", "screenclippinghost", "gyazo", "gyazoreplay",
        "flameshot", "picpick", "snagit32", "snagiteditor", "steamwebhelper", "gamebar", "gamebarftserver",
        "nvidia overlay", "nvcontainer", "radeonsoftware", "amdrssrcext", "medal", "overwolf", "taskmgr",
    };

    public DetectedGame? Current => _current;

    /// <summary>Raised off the UI thread when a game starts or stops being detected.</summary>
    public event Action<DetectedGame?>? Changed;

    public void Start()
    {
        _ = Task.Run(LoadListsAsync);
        _poll = new Timer(_ => Check(), null, 2000, 3000);
    }

    /* ---- the lists ------------------------------------------------------- */

    private async Task LoadListsAsync()
    {
        try { _windowsGames = ReadWindowsGames(); }
        catch (Exception ex) { Log.Error("windows game list", ex); }

        var cache = Path.Combine(Paths.Root, "games.json");
        try
        {
            if (File.Exists(cache)) _discordGames = ParseDiscord(await File.ReadAllTextAsync(cache));

            bool stale = !File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < DateTime.UtcNow.AddDays(-7);
            if (stale)
            {
                var json = await Http.GetStringAsync("https://discord.com/api/v9/applications/detectable");
                var parsed = ParseDiscord(json);
                if (parsed.Count > 100)
                {
                    // Keep only what's needed: exe name → game name.
                    await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(parsed));
                    _discordGames = parsed;
                }
            }
            Log.Write($"Game lists: {_windowsGames.Count} from Windows, {_discordGames.Count} from Discord");
        }
        catch (Exception ex) { Log.Write("Discord game list unavailable: " + ex.Message); }
    }

    private static HashSet<string> ReadWindowsGames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
        if (root == null) return set;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("MatchedExeFullPath") is string path && path.Length > 0) set.Add(path);
        }
        return set;
    }

    /// <summary>
    /// Reads either Discord's full list or the small cache. The full list is
    /// an array of apps with a name and executables; the cache is a flat
    /// dictionary.
    /// </summary>
    private static Dictionary<string, string> ParseDiscord(string json)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
            return map;
        }

        foreach (var app in doc.RootElement.EnumerateArray())
        {
            if (!app.TryGetProperty("name", out var n) || !app.TryGetProperty("executables", out var exes)) continue;
            var name = n.GetString();
            if (string.IsNullOrEmpty(name) || exes.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in exes.EnumerateArray())
            {
                if (e.TryGetProperty("os", out var os) && os.GetString() != "win32") continue;
                if (!e.TryGetProperty("name", out var en)) continue;
                var file = Path.GetFileName((en.GetString() ?? "").Replace('/', '\\'));
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) map.TryAdd(file, name);
            }
        }
        return map;
    }

    /* ---- the check -------------------------------------------------------- */

    private int _checking;
    private uint _lastPid;
    private IntPtr _lastWindow;
    private string? _lastPath;

    private void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            // A remembered game stays until its process is gone.
            if (_current != null && !Alive(_current.Pid)) Set(null);

            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            if (_current is { } cur && cur.Pid == (int)pid) { _lastApp = (cur.Pid, cur.ExePath); return; }

            // The same program in front as last time: its path isn't looked up again.
            string? path;
            // (The window too: a program that closed can't hand its number on unnoticed.)
            if (pid == _lastPid && hwnd == _lastWindow) path = _lastPath;
            else { path = ProcessPath((int)pid); _lastPid = pid; _lastWindow = hwnd; _lastPath = path; }
            if (path == null) return;
            var exe = Path.GetFileName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            // The last program in front that isn't Windows itself: what "Count
            // this as a game" in the tray menu means (by then the taskbar is in front).
            if (!Shell.Contains(stem)) _lastApp = ((int)pid, path);

            string? name = null;
            // Added by hand: a game, whatever the lists below say.
            if (_myGames.Contains(path)) name = FriendlyName(path);
            else if (NotGames.Contains(stem)) return;
            else if (_windowsGames.Contains(path)) name = FriendlyName(path);
            else if (_discordGames.TryGetValue(exe, out var dn)) name = dn;
            else if (FillsScreen(hwnd)) name = FriendlyName(path);

            if (name != null) Set(new DetectedGame((int)pid, name, path));
        }
        catch (Exception ex) { Log.Error("game check", ex); }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    /* ---- games added by hand ---------------------------------------------- */

    private HashSet<string> _myGames = new(StringComparer.OrdinalIgnoreCase);
    private (int Pid, string Path)? _lastApp;

    /// <summary>Windows' own parts that come to the front (the taskbar, Start), never "the app in front".</summary>
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "persocord", "applicationframehost", "searchhost", "searchapp", "startmenuexperiencehost",
        "shellexperiencehost", "lockapp", "textinputhost", "shellhost", "systemsettings",
    };

    /// <summary>The games added by hand (full paths), from Settings.</summary>
    public void SetMyGames(IEnumerable<string> paths) =>
        _myGames = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

    /// <summary>The last program that was in front (not Windows' own parts), if it's still running: its path and name.</summary>
    public (string Path, string Name)? LastApp =>
        _lastApp is { } a && Alive(a.Pid) ? (a.Path, FriendlyName(a.Path)) : null;

    /// <summary>A program was just added as a game: if it's the one running, it counts now.</summary>
    public void Added(string path)
    {
        _myGames.Add(path);
        if (_lastApp is { } a && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase) && Alive(a.Pid))
            Set(new DetectedGame(a.Pid, FriendlyName(path), path));
    }

    /// <summary>A display name for a program file.</summary>
    public static string NameOf(string path) => FriendlyName(path);

    private void Set(DetectedGame? game)
    {
        if (_current?.Pid == game?.Pid) return;
        _current = game;
        Log.Write(game == null ? "Game closed" : $"Game detected: {game.Name} ({Path.GetFileName(game.ExePath)})");
        Changed?.Invoke(game);
    }

    private static string FriendlyName(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var n = !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName : v.FileDescription;
            if (!string.IsNullOrWhiteSpace(n) && n.Length <= 40) return n.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool FillsScreen(IntPtr hwnd)
    {
        // Screenshot overlays are always-on-top, see-through windows; games aren't.
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        if ((ex & 0x8) != 0 && (ex & 0x80000) != 0) return false;   // topmost + layered
        if ((ex & 0x80) != 0) return false;                           // tool window
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private static bool Alive(int pid) => Native.ProcessAlive(pid);

    /* ---- processes ---------------------------------------------------------- */

    /// <summary>Every process: its parent and its program file name.</summary>
    public static Dictionary<int, (int Parent, string Exe)> ProcessTable()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref e))
                do map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile ?? "");
                while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /* ---- Win32 ------------------------------------------------------------- */

    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(0x1000, false, (uint)pid);   // query limited information: works on most games
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO info);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);

    public void Dispose() => _poll?.Dispose();
}
