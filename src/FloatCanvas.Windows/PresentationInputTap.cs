using System.Runtime.InteropServices;
using System.Text;

namespace FloatCanvas.Windows;

/// <summary>Metadata only; its owner serializes configuration and hook callbacks.</summary>
internal sealed class PresentationInputTap
{
    internal const uint InjectedKeyFlag = 0x100;
    private readonly PresentationInputBuffer buffer = new();
    private readonly Dictionary<(int, int, uint), string> labels = [];
    private bool keyboard, pointer;
    private long epoch;
    internal bool ObservesKeyboard => keyboard;
    internal uint Generation { get; private set; } = 1;

    internal void Configure(bool keyboard, bool pointer, long epoch)
    {
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (this.keyboard == keyboard && this.pointer == pointer && this.epoch == epoch) return;
        this.keyboard = keyboard; this.pointer = pointer; this.epoch = epoch; Clear();
    }
    internal void Clear()
    { buffer.Clear(); Generation = Generation == uint.MaxValue ? 1 : Generation + 1; }
    internal uint Keyboard(uint vk, uint scan, uint flags, bool up, long now, bool active, PresentationInputOwner owner = PresentationInputOwner.FloatCanvas)
    {
        // The presentation shows physical presses. Re-injected mapper/system edges
        // must not start a second chord or release a still-held physical key.
        // Hardware and vendor remappers commonly retransmit command keys and
        // side-specific modifiers as injected events, so those remain observable.
        if (!keyboard || !active || vk is 0 or >= 255 || scan == 255) return 0;
        int key = vk switch
        {
            0x10 => scan == 0x36 ? 0xA1 : 0xA0,
            0x11 => (flags & 1) != 0 ? 0xA3 : 0xA2,
            0x12 => (flags & 1) != 0 ? 0xA5 : 0xA4,
            _ => (int)vk
        };
        if ((flags & 0x10) != 0 && !IsInjectedCommandKey((uint)key) && key is not (>= 0xA0 and <= 0xA5)) return 0;
        uint physicalFlags = ((flags & 1) != 0 ? 2u : 0u) |
            ((flags & 0x10) != 0 ? InjectedKeyFlag : 0u);
        return buffer.Add(new(up ? PresentationObservedKind.KeyUp : PresentationObservedKind.KeyDown,
            now, Device: 0, VirtualKey: key, ScanCode: (int)scan, KeyFlags: physicalFlags,
            KeyLabel: Label(key, (int)scan, physicalFlags), InputOwner: owner)) ? Generation : 0;
    }
    internal uint Pointer(uint message, int x, int y, long now, bool active)
    {
        if (!pointer || !active) return 0;
        int button = message switch { 0x201 or 0x202 => 0, 0x204 or 0x205 => 1, 0x207 or 0x208 => 2, _ => -1 };
        if (button < 0) return 0;
        bool up = message is 0x202 or 0x205 or 0x208;
        return buffer.Add(new(up ? PresentationObservedKind.ButtonUp : PresentationObservedKind.ButtonDown,
            now, X: x, Y: y, HasPosition: true, Button: button)) ? Generation : 0;
    }
    internal bool TryDrain(uint generation, out PresentationObservedBatch? batch)
    {
        batch = null;
        if (generation != Generation) return false;
        var result = buffer.Drain(epoch);
        if (result.Changes.Count == 0 && !result.Overflowed) return false;
        batch = result; return true;
    }
    private string Label(int vk, int scan, uint flags)
    {
        var key = (vk, scan, flags); if (labels.TryGetValue(key, out var cached)) return cached;
        string label = vk switch
        {
            >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            0xA0 or 0xA1 or 0x10 => "Shift", 0xA2 or 0xA3 or 0x11 => "Ctrl", 0xA4 or 0xA5 or 0x12 => "Alt", 0x5B or 0x5C => "Win",
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "CapsLock", 0x1B => "Esc", 0x20 => "Space", 0x21 => "PgUp", 0x22 => "PgDn",
            0x23 => "End", 0x24 => "Home", 0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2C => "PrintScreen", 0x2D => "Insert", 0x2E => "Delete",
            0x5D => "Menu", 0x90 => "NumLock", 0x91 => "ScrollLock", _ => ""
        };
        if (label.Length == 0)
        {
            var text = new StringBuilder(33); int descriptor = (scan & 255) << 16 | ((flags & 2) != 0 ? 1 << 24 : 0);
            label = GetKeyNameText(descriptor, text, text.Capacity) > 0 ? text.ToString() : "Key " + vk;
        }
        if (labels.Count >= 256) labels.Clear(); labels.Add(key, label); return label;
    }
    private static bool IsInjectedCommandKey(uint virtualKey) => virtualKey is >= 0xA6 and <= 0xB7;
    [DllImport("user32.dll", EntryPoint = "GetKeyNameTextW", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int code, StringBuilder text, int maximum);
}
