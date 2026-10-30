using System.Windows.Input;
using EzForensics.Core.Settings;

namespace EzForensics.App.Services;

/// <summary>Turns WPF key events into canonical gesture text ("Ctrl+Shift+F") for <see cref="HotKeys"/>.</summary>
public static class HotKeyInput
{
    /// <summary>Key token for a key, or null for lone modifiers and keys that carry no meaning (IME, dead keys).</summary>
    public static string? KeyToken(Key k) => k switch
    {
        Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed => null,
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin => null,
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(k - Key.NumPad0),
        Key.Escape => "Esc",
        Key.Return => "Enter",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        _ => k.ToString(),
    };

    /// <summary>Gesture for a key event (modifiers from the keyboard), or null when only modifiers are down.</summary>
    public static HotKeyGesture? FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var token = KeyToken(key);
        if (token == null) return null;
        var m = Keyboard.Modifiers;
        return new HotKeyGesture((m & ModifierKeys.Control) != 0, (m & ModifierKeys.Alt) != 0, (m & ModifierKeys.Shift) != 0, token);
    }
}
