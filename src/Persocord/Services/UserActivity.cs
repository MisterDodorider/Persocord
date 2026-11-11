using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Persocord.Services;

/// <summary>
/// How long since the person last did anything: keyboard, mouse, any
/// controller, or waking the PC. Windows' own "last input" only knows the
/// keyboard and mouse, so controllers are watched as well: Xbox-style ones
/// (and most set up through Steam) by their state counter, every other
/// game controller (a PlayStation pad plugged in directly, say) by its input
/// reports, heard on a thread of its own. Nothing is read beyond "something
/// happened just now".
/// </summary>
public static class UserActivity
{
    private static long _controllerAt, _wokeAt;
    private static Timer? _xinput;
    private static Thread? _raw;
    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        long now = Environment.TickCount64;
        _controllerAt = _wokeAt = now;
        _xinput = new Timer(_ => PollXInput(), null, 2000, 1000);
        _raw = new Thread(RawInputLoop) { IsBackground = true, Name = "Persocord controller watch", Priority = ThreadPriority.BelowNormal };
        _raw.Start();
    }

    /// <summary>Time since the last keyboard or mouse input.</summary>
    public static TimeSpan SinceKeyboardMouse
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            // Both on Windows' 32-bit millisecond clock, which wraps every 49 days.
            uint ms = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(ms);
        }
    }

    /// <summary>Time since a controller was last used.</summary>
    public static TimeSpan SinceController => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _controllerAt));

    /// <summary>Time since the PC last woke from sleep.</summary>
    public static TimeSpan SinceWake => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _wokeAt));

    /// <summary>Time since anything at all: the shortest of the three.</summary>
    public static TimeSpan Idle
    {
        get
        {
            var a = SinceKeyboardMouse;
            var b = SinceController;
            var c = SinceWake;
            return a < b ? (a < c ? a : c) : (b < c ? b : c);
        }
    }

    private static void Touched() => Interlocked.Exchange(ref _controllerAt, Environment.TickCount64);

    /* ---- Xbox-style controllers ------------------------------------------- */

    private static readonly uint[] Packets = new uint[4];
    private static readonly bool[] Connected = new bool[4];
    private static int _xinputTicks;
    private static bool _noXInput;

    /// <summary>
    /// Once a second for connected controllers. An empty slot is asked only
    /// every 10 seconds: asking about a controller that isn't there is slow.
    /// </summary>
    private static DateTime _lastPoll = DateTime.UtcNow;

    private static void PollXInput()
    {
        // This runs every second; a long gap since the last time means the PC
        // was asleep (nothing runs then), so it has just woken up.
        var now = DateTime.UtcNow;
        if (now - _lastPoll > TimeSpan.FromSeconds(30)) Interlocked.Exchange(ref _wokeAt, Environment.TickCount64);
        _lastPoll = now;
        if (_noXInput) return;
        int tick = ++_xinputTicks;
        for (int i = 0; i < 4; i++)
        {
            if (!Connected[i] && tick % 10 != 0) continue;
            try
            {
                int r = XInputGetState(i, out var st);
                if (r != 0) { Connected[i] = false; continue; }
                if (Connected[i] && st.dwPacketNumber != Packets[i]) Touched();
                Connected[i] = true;
                Packets[i] = st.dwPacketNumber;
            }
            catch (DllNotFoundException) { _noXInput = true; return; }
            catch (EntryPointNotFoundException) { _noXInput = true; return; }
            catch { }
        }
    }

    /* ---- every other game controller ------------------------------------- */

    private static WndProc? _proc;   // kept alive while Windows holds it

    private static void RawInputLoop()
    {
        try
        {
            _proc = (h, msg, w, l) =>
            {
                if (msg == WM_INPUT) Touched();
                return DefWindowProc(h, msg, w, l);
            };
            var wc = new WNDCLASS { lpfnWndProc = _proc, hInstance = GetModuleHandle(null), lpszClassName = "PersocordControllerWatch" };
            if (RegisterClass(ref wc) == 0) return;
            var hwnd = CreateWindowEx(0, "PersocordControllerWatch", "", 0, 0, 0, 0, 0, new IntPtr(-3) /* message-only */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;
            // Joysticks and gamepads (Windows' standard names for them), heard in the background.
            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 4, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 5, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                Log.Write("Controller watch: Windows wouldn't list game controllers; Xbox-style ones are still noticed.");
                return;
            }
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }
        catch (Exception ex) { Log.Error("controller watch", ex); }
    }

    /* ---- Windows declarations -------------------------------------------- */

    private const uint WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int index, out XINPUT_STATE state);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
