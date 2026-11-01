using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public enum PresentationDisplayStyle { Log = 0, Keycaps = 1, Keyboard = 2 }

/// <summary>One authored physical key. Coordinates use arbitrary layout units, never screen pixels.</summary>
public sealed class PresentationKeyboardKey
{
    public int VirtualKey { get; set; }
    public int ScanCode { get; set; }
    public bool IsExtended { get; set; }
    public string Label { get; set; } = "Key";
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
    [JsonIgnore] public string Id => $"{VirtualKey:X2}:{ScanCode:X2}:{(IsExtended ? 1 : 0)}";
    public PresentationKeyboardKey Clone() => (PresentationKeyboardKey)MemberwiseClone();
    public bool IsValid() => VirtualKey is > 0 and < 255 && ScanCode is > 0 and <= 255 &&
        Label is { Length: > 0 and <= PresentationLimits.MaximumKeyLabelLength } && !Label.Any(char.IsControl) &&
        Finite(X, 0, 64) && Finite(Y, 0, 64) && Finite(Width, .25, 16) && Finite(Height, .25, 16) &&
        X + Width <= 64 && Y + Height <= 64;
    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

/// <summary>Owned editable layout. Null in PresentationSettings means this standard 104-key arrangement.</summary>
public sealed class PresentationKeyboardLayout
{
    public const int MaximumKeys = 128;
    public List<PresentationKeyboardKey> Keys { get; set; } = DefaultKeys();
    public static PresentationKeyboardLayout CreateDefault() => new();
    public PresentationKeyboardLayout Clone() => new() { Keys = Keys.Select(key => key.Clone()).ToList() };
    public bool IsValid() => Keys is not null && Keys.Count <= MaximumKeys && Keys.All(key => key?.IsValid() == true) &&
        Keys.Select(key => key.Id).Distinct(StringComparer.Ordinal).Count() == Keys.Count;

    private static List<PresentationKeyboardKey> DefaultKeys()
    {
        var keys = new List<PresentationKeyboardKey>(104);
        void Add(int vk, int scan, string label, double x, double y, double width = 1, double height = 1, bool extended = false) =>
            keys.Add(new() { VirtualKey = vk, ScanCode = scan, Label = label, X = x, Y = y, Width = width, Height = height, IsExtended = extended });
        Add(0x1B, 0x01, "Esc", 0, 0);
        for (int i = 0; i < 12; i++) Add(0x70 + i, i < 10 ? 0x3B + i : 0x57 + i - 10, "F" + (i + 1), 2 + i + (i >= 4 ? .5 : 0) + (i >= 8 ? .5 : 0), 0);
        Add(0x2C, 0x37, "PrtSc", 15.5, 0, extended: true); Add(0x91, 0x46, "Scroll", 16.5, 0); Add(0x13, 0x45, "Pause", 17.5, 0);
        Add(0xC0, 0x29, "`", 0, 1.5);
        for (int i = 0; i < 10; i++) Add(i == 9 ? 0x30 : 0x31 + i, 0x02 + i, i == 9 ? "0" : (i + 1).ToString(), i + 1, 1.5);
        Add(0xBD, 0x0C, "-", 11, 1.5); Add(0xBB, 0x0D, "=", 12, 1.5); Add(0x08, 0x0E, "Backspace", 13, 1.5, 2);
        Add(0x09, 0x0F, "Tab", 0, 2.5, 1.5);
        string top = "QWERTYUIOP"; for (int i = 0; i < top.Length; i++) Add(top[i], 0x10 + i, top[i].ToString(), 1.5 + i, 2.5);
        Add(0xDB, 0x1A, "[", 11.5, 2.5); Add(0xDD, 0x1B, "]", 12.5, 2.5); Add(0xDC, 0x2B, "\\", 13.5, 2.5, 1.5);
        Add(0x14, 0x3A, "Caps", 0, 3.5, 1.75);
        string home = "ASDFGHJKL"; for (int i = 0; i < home.Length; i++) Add(home[i], 0x1E + i, home[i].ToString(), 1.75 + i, 3.5);
        Add(0xBA, 0x27, ";", 10.75, 3.5); Add(0xDE, 0x28, "'", 11.75, 3.5); Add(0x0D, 0x1C, "Enter", 12.75, 3.5, 2.25);
        Add(0xA0, 0x2A, "Shift", 0, 4.5, 2.25);
        string bottom = "ZXCVBNM"; for (int i = 0; i < bottom.Length; i++) Add(bottom[i], 0x2C + i, bottom[i].ToString(), 2.25 + i, 4.5);
        Add(0xBC, 0x33, ",", 9.25, 4.5); Add(0xBE, 0x34, ".", 10.25, 4.5); Add(0xBF, 0x35, "/", 11.25, 4.5); Add(0xA1, 0x36, "Shift", 12.25, 4.5, 2.75);
        Add(0xA2, 0x1D, "Ctrl", 0, 5.5, 1.25); Add(0x5B, 0x5B, "Win", 1.25, 5.5, 1.25, extended: true); Add(0xA4, 0x38, "Alt", 2.5, 5.5, 1.25);
        Add(0x20, 0x39, "Space", 3.75, 5.5, 6.25); Add(0xA5, 0x38, "Alt", 10, 5.5, 1.25, extended: true);
        Add(0x5C, 0x5C, "Win", 11.25, 5.5, 1.25, extended: true); Add(0x5D, 0x5D, "Menu", 12.5, 5.5, 1.25, extended: true); Add(0xA3, 0x1D, "Ctrl", 13.75, 5.5, 1.25, extended: true);
        Add(0x2D, 0x52, "Ins", 15.5, 1.5, extended: true); Add(0x24, 0x47, "Home", 16.5, 1.5, extended: true); Add(0x21, 0x49, "PgUp", 17.5, 1.5, extended: true);
        Add(0x2E, 0x53, "Del", 15.5, 2.5, extended: true); Add(0x23, 0x4F, "End", 16.5, 2.5, extended: true); Add(0x22, 0x51, "PgDn", 17.5, 2.5, extended: true);
        Add(0x26, 0x48, "↑", 16.5, 4.5, extended: true); Add(0x25, 0x4B, "←", 15.5, 5.5, extended: true); Add(0x28, 0x50, "↓", 16.5, 5.5, extended: true); Add(0x27, 0x4D, "→", 17.5, 5.5, extended: true);
        Add(0x90, 0x45, "Num", 19, 1.5); Add(0x6F, 0x35, "/", 20, 1.5, extended: true); Add(0x6A, 0x37, "*", 21, 1.5); Add(0x6D, 0x4A, "-", 22, 1.5);
        int[] scans = [0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49];
        for (int i = 1; i <= 9; i++) Add(0x60 + i, scans[i], i.ToString(), 19 + (i - 1) % 3, 4.5 - (i - 1) / 3);
        Add(0x6B, 0x4E, "+", 22, 2.5, height: 2); Add(0x0D, 0x1C, "Enter", 22, 4.5, height: 2, extended: true);
        Add(0x60, 0x52, "0", 19, 5.5, 2); Add(0x6E, 0x53, ".", 21, 5.5);
        return keys;
    }
}
