using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Display key caps rather than WPF implementation names such as Oem3 and D1.</summary>
internal static class HotkeyDisplay
{
    public static string Format(HotkeyBinding binding)
    {
        var parts = new List<string>();
        if ((binding.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & 1) != 0) parts.Add("Alt");
        if ((binding.Modifiers & 4) != 0) parts.Add("Shift");
        if ((binding.Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyName(binding.VirtualKey));
        return string.Join("+", parts);
    }

    internal static string KeyName(uint key) => key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (key - 0x60),
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause",
        0x14 => "Caps Lock", 0x1B => "Esc", 0x20 => "Space", 0x21 => "Page Up",
        0x22 => "Page Down", 0x23 => "End", 0x24 => "Home", 0x25 => "←", 0x26 => "↑",
        0x27 => "→", 0x28 => "↓", 0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete",
        0x6A => "Num *", 0x6B => "Num +", 0x6C => "Num ,", 0x6D => "Num -", 0x6E => "Num .", 0x6F => "Num /",
        0x90 => "Num Lock", 0x91 => "Scroll Lock", 0xBA => ";", 0xBB => "=", 0xBC => ",",
        0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\",
        0xDD => "]", 0xDE => "'", 0xE2 => "\\",
        _ => System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)key) is var mapped && mapped != System.Windows.Input.Key.None
            ? mapped.ToString() : "Key 0x" + key.ToString("X2")
    };
}
