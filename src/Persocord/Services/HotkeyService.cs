using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Persocord.Services;

/// <summary>
/// Global hotkeys, parsed from strings like "Ctrl+Shift+2" so they can live in
/// the settings file and be edited without a key-capture widget.
/// </summary>
public class HotkeyService : IDisposable
{
    private readonly Dictionary<int, Action> _actions = new();
    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private int _nextId = 1;

    public event Action<string>? RegistrationFailed;

    public void Attach()
    {
        // A hidden message-only window owns the registrations, so hotkeys work
        // with no visible window open.
        var helper = new Window
        {
            Width = 0, Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Visibility = Visibility.Hidden,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        helper.Show();
        helper.Hide();

        _hwnd = new WindowInteropHelper(helper).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
    }

    public void Register(string combo, Action action)
    {
        if (string.IsNullOrWhiteSpace(combo) || _hwnd == IntPtr.Zero) return;
        if (!TryParse(combo, out uint mods, out uint vk))
        {
            RegistrationFailed?.Invoke($"{combo} is not a key combination Persocord understands.");
            return;
        }

        int id = _nextId++;
        if (Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk))
        {
            _actions[id] = action;
        }
        else
        {
            lock (Taken) Taken.Add(combo);
            RegistrationFailed?.Invoke(
                $"{combo} is already taken by another program. Pick a different one in settings.");
        }
    }

    /// <summary>Keys another program already holds, from the last time they were set up.</summary>
    public List<string> Taken { get; } = new();

    public void UnregisterAll()
    {
        lock (Taken) Taken.Clear();
        foreach (var id in _actions.Keys)
            try { Native.UnregisterHotKey(_hwnd, id); } catch { }
        _actions.Clear();
        _nextId = 1;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var act))
        {
            handled = true;
            try { act(); } catch (Exception ex) { Log.Error("hotkey action", ex); }
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            bool last = i == parts.Length - 1;

            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; continue;
                case "shift": mods |= Native.MOD_SHIFT; continue;
                case "alt": mods |= Native.MOD_ALT; continue;
                case "win": case "windows": mods |= Native.MOD_WIN; continue;
            }

            if (!last) return false;

            var key = ToKey(p);
            if (key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        return vk != 0;
    }

    /// <summary>Whether a key press (inside a window) is this combination, modifiers and all.</summary>
    public static bool Matches(string combo, Key key, ModifierKeys mods)
    {
        if (string.IsNullOrWhiteSpace(combo) || !TryParse(combo, out uint want, out uint vk)) return false;
        if ((uint)KeyInterop.VirtualKeyFromKey(key) != vk) return false;
        uint have = 0;
        if (mods.HasFlag(ModifierKeys.Control)) have |= Native.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Shift)) have |= Native.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Alt)) have |= Native.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Windows)) have |= Native.MOD_WIN;
        return have == want;
    }

    private static Key ToKey(string s)
    {
        if (s.Length == 1 && char.IsDigit(s[0]))
            return (Key)Enum.Parse(typeof(Key), "D" + s);
        if (Enum.TryParse<Key>(s, true, out var k)) return k;
        return Key.None;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(Hook);
        _source?.Dispose();
    }
}
