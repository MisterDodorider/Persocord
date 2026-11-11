using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Persocord.Views;

/// <summary>
/// A hotkey field that listens instead of taking typing: click it, press the
/// combination, done. Esc puts back what was there; Backspace or Delete on
/// their own clears it.
/// </summary>
public class HotkeyBox : TextBox
{
    private string _before = "";

    /// <summary>Lets a single letter or number be the key (for keys that only work inside a window, like the editor's).</summary>
    public bool AllowPlain { get; set; }
    private bool _listening;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the keys. Esc cancels, Backspace clears.";
        GotKeyboardFocus += (_, _) => Listen();
        LostKeyboardFocus += (_, _) => StopListening(keep: true);
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!IsKeyboardFocused) { Focus(); e.Handled = true; }
            else if (!_listening) Listen();
        };
    }

    private void Listen()
    {
        if (_listening) return;
        _listening = true;
        _before = Text;
        Text = "Press keys…";
        SetResourceReference(BorderBrushProperty, "Brass");
    }

    private void StopListening(bool keep)
    {
        if (!_listening) return;
        _listening = false;
        if (!keep || Text == "Press keys…" || Text.EndsWith("+…")) Text = _before;
        ClearValue(BorderBrushProperty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        if (!_listening) { Listen(); }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (key == Key.Escape && mods == ModifierKeys.None) { StopListening(keep: false); Keyboard.ClearFocus(); return; }
        if ((key == Key.Back || key == Key.Delete) && mods == ModifierKeys.None)
        {
            Text = "";
            _before = "";
            StopListening(keep: true);
            Keyboard.ClearFocus();
            return;
        }

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (win) parts.Add("Win");

        // Modifiers alone: show them while waiting for the main key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            Text = parts.Count > 0 ? string.Join("+", parts) + "+…" : "Press keys…";
            return;
        }

        // A bare letter or number would fire while typing anywhere, so a
        // modifier is required for those; function keys and the like can
        // stand alone.
        bool plain = key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9;
        if (parts.Count == 0 && plain && !AllowPlain)
        {
            Text = "Add Ctrl, Alt or Shift…";
            return;
        }

        parts.Add(KeyName(key));
        Text = string.Join("+", parts);
        _before = Text;
        StopListening(keep: true);
        Keyboard.ClearFocus();
    }

    /// <summary>Names the way the hotkey reader expects: "2" for the 2 key, "F9", "A".</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };
}
