using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public enum PresentationDisplayStyle { Log = 0, Keycaps = 1, Keyboard = 2 }
public enum PresentationKeyDevice { Keyboard, Mouse, Gamepad }
public enum PresentationGamepadButton
{
    Up = 1, Down, Left, Right, Start, Back, LeftStick, RightStick,
    LeftShoulder, RightShoulder, A, B, X, Y
}
[Flags]
public enum PresentationKeyStyleOverrides { None = 0, Size = 1, Font = 2, Foreground = 4, Background = 8, All = 15 }

/// <summary>Optional keycap appearance. The relative size is independent of the layer's scale.</summary>
public sealed record PresentationKeyboardKeyStyle
{
    public const double MinimumRelativeSize = -16;
    public const double MaximumRelativeSize = 16;
    public static readonly double MinimumSizePercent = 100 * Math.Pow(1.15, MinimumRelativeSize);
    public static readonly double MaximumSizePercent = 100 * Math.Pow(1.15, MaximumRelativeSize);
    public bool Enabled { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PresentationKeyStyleOverrides? Overrides { get; init; }
    public double RelativeSize { get; init; }
    public string? FontFamily { get; init; }
    public uint? Foreground { get; init; }
    public uint? Background { get; init; }

    [JsonIgnore]
    public PresentationKeyStyleOverrides EffectiveOverrides => Overrides ?? (Enabled
        ? (RelativeSize != 0 ? PresentationKeyStyleOverrides.Size : 0) |
          (FontFamily is not null ? PresentationKeyStyleOverrides.Font : 0) |
          (Foreground.HasValue ? PresentationKeyStyleOverrides.Foreground : 0) |
          (Background.HasValue ? PresentationKeyStyleOverrides.Background : 0)
        : PresentationKeyStyleOverrides.None);

    public bool IsValid() => double.IsFinite(RelativeSize) && RelativeSize is >= MinimumRelativeSize and <= MaximumRelativeSize &&
        (EffectiveOverrides & ~PresentationKeyStyleOverrides.All) == 0 &&
        (FontFamily is null || FontFamily.Length is > 0 and <= 256 && !FontFamily.Any(char.IsControl));
    public double ScaleFont(double size) => Enabled ? size * Math.Pow(1.15, RelativeSize) : size;
    public static double RelativeSizeFromPercent(double percent) =>
        Math.Clamp(Math.Log(percent / 100, 1.15), MinimumRelativeSize, MaximumRelativeSize);
}

/// <summary>One authored physical key. Coordinates use arbitrary layout units, never screen pixels.</summary>
public sealed class PresentationKeyboardKey
{
    public const double MinimumPosition = -64;
    public const double MinimumSize = .25;
    public const double MaximumSize = 16;
    public const double MaximumPosition = 64;
    public int VirtualKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PresentationKeyDevice DeviceType { get; set; }
    public int ScanCode { get; set; }
    public bool IsExtended { get; set; }
    public string Label { get; set; } = "Key";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SecondaryLabel { get; set; }
    /// <summary>A neutral layout cell that never matches a physical key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsPlaceholder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomId { get; set; }
    /// <summary>Match keys that represent the same number, lock, enter or operator across keyboard regions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MatchRelatedKeys { get; set; }
    /// <summary>Show the persistent two-state lock indicator for this lock key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowLockIndicator { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PresentationKeyboardKeyStyle? CustomStyle { get; set; }
    [JsonIgnore] public PresentationKeyboardKeyStyle? ActiveStyle
    {
        get
        {
            if (CustomStyle is not { } style) return null;
            if (style.Overrides is null) return style.Enabled ? style : null;
            var fields = style.EffectiveOverrides;
            return fields == PresentationKeyStyleOverrides.None ? null : style with
            {
                Enabled = true,
                RelativeSize = fields.HasFlag(PresentationKeyStyleOverrides.Size) ? style.RelativeSize : 0,
                FontFamily = fields.HasFlag(PresentationKeyStyleOverrides.Font) ? style.FontFamily : null,
                Foreground = fields.HasFlag(PresentationKeyStyleOverrides.Foreground) ? style.Foreground : null,
                Background = fields.HasFlag(PresentationKeyStyleOverrides.Background) ? style.Background : null
            };
        }
    }
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
    [JsonIgnore] public string Id => IsPlaceholder ? CustomId ?? "" : DeviceType == PresentationKeyDevice.Keyboard
        ? $"{VirtualKey:X2}:{ScanCode:X2}:{(IsExtended ? 1 : 0)}" : $"{DeviceType}:{VirtualKey}";
    [JsonIgnore] public bool SupportsRelatedMatch => DeviceType == PresentationKeyDevice.Keyboard && RelatedFamily(VirtualKey, ScanCode, IsExtended) != 0;
    [JsonIgnore] public bool SupportsLockIndicator => !IsPlaceholder && DeviceType == PresentationKeyDevice.Keyboard && VirtualKey is 0x14 or 0x90 or 0x91;
    public PresentationKeyboardKey Clone() => (PresentationKeyboardKey)MemberwiseClone();
    public bool Matches(PresentationKeyCap cap)
    {
        if (IsPlaceholder || DeviceType != cap.DeviceType) return false;
        if (DeviceType != PresentationKeyDevice.Keyboard) return VirtualKey == cap.VirtualKey;
        if (MatchRelatedKeys)
        {
            int family = RelatedFamily(VirtualKey, ScanCode, IsExtended);
            if (family != 0 && family == RelatedFamily(cap.VirtualKey, cap.ScanCode, cap.IsExtended)) return true;
        }
        return MatchesPhysicalKey(cap.VirtualKey, cap.ScanCode, cap.IsExtended);
    }
    public bool MatchesPhysicalKey(int virtualKey, int scanCode, bool isExtended)
    {
        if (IsPlaceholder || DeviceType != PresentationKeyDevice.Keyboard) return false;
        if (scanCode == 0 || ScanCode == 0) return virtualKey == VirtualKey;
        if (VirtualKey is >= 0xA0 and <= 0xA5)
            return virtualKey == VirtualKey && scanCode == ScanCode;
        if (scanCode != ScanCode || isExtended != IsExtended) return false;
        // Pause and NumLock share Pause's scan byte and need the virtual key too.
        return ScanCode != 0x45 || virtualKey == VirtualKey;
    }
    private static int RelatedFamily(int virtualKey, int scanCode, bool isExtended) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => 0x100,
        0x11 or 0xA2 or 0xA3 => 0x101,
        0x12 or 0xA4 or 0xA5 => 0x102,
        0x5B or 0x5C => 0x103,
        0x0D => 0x200,
        0x30 or 0x60 => 0x300,
        >= 0x31 and <= 0x39 => 0x300 + virtualKey - 0x30,
        >= 0x61 and <= 0x69 => 0x300 + virtualKey - 0x60,
        0xBB or 0x6B => 0x320,
        0xBD or 0x6D => 0x321,
        0xBE or 0x6E => 0x322,
        0xBF or 0x6F => 0x323,
        // With Num Lock off Windows sends navigation VKs for these physical
        // keys. Their non-extended scan codes distinguish the keypad cluster.
        _ => isExtended ? 0 : (virtualKey, scanCode) switch
        {
            (0x2D, 0x52) => 0x300, (0x23, 0x4F) => 0x301, (0x28, 0x50) => 0x302,
            (0x22, 0x51) => 0x303, (0x25, 0x4B) => 0x304, (0x0C, 0x4C) => 0x305,
            (0x27, 0x4D) => 0x306, (0x24, 0x47) => 0x307, (0x26, 0x48) => 0x308,
            (0x21, 0x49) => 0x309, (0x2E, 0x53) => 0x322,
            _ => 0
        }
    };
    public bool IsValid() => (IsPlaceholder
            ? CustomId is { Length: > 0 and <= PresentationLimits.MaximumKeyLabelLength } && !CustomId.Any(char.IsControl) &&
              DeviceType == PresentationKeyDevice.Keyboard && VirtualKey == 0 && ScanCode == 0 && !IsExtended && !MatchRelatedKeys && !ShowLockIndicator
            : IsValidInput(DeviceType, VirtualKey) && VirtualKey < 255 && ScanCode is >= 0 and <= 255 && CustomId is null &&
              (DeviceType == PresentationKeyDevice.Keyboard || ScanCode == 0 && !IsExtended) &&
              (!MatchRelatedKeys || SupportsRelatedMatch) && (!ShowLockIndicator || SupportsLockIndicator)) &&
        Label is { Length: > 0 and <= PresentationLimits.MaximumKeyLabelLength } && !Label.Any(char.IsControl) &&
        (SecondaryLabel is null || SecondaryLabel.Length <= PresentationLimits.MaximumKeyLabelLength && !SecondaryLabel.Any(char.IsControl)) &&
        Finite(X, MinimumPosition, MaximumPosition) && Finite(Y, MinimumPosition, MaximumPosition) &&
        Finite(Width, MinimumSize, MaximumSize) && Finite(Height, MinimumSize, MaximumSize) &&
        X + Width <= MaximumPosition && Y + Height <= MaximumPosition && (CustomStyle?.IsValid() ?? true);
    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    internal static bool IsValidInput(PresentationKeyDevice device, int code) => device switch
    {
        PresentationKeyDevice.Keyboard => code is > 0 and < 256,
        PresentationKeyDevice.Mouse => code is >= 1 and <= 7,
        PresentationKeyDevice.Gamepad => code is >= (int)PresentationGamepadButton.Up and <= (int)PresentationGamepadButton.Y,
        _ => false
    };
}

/// <summary>Owned editable layout. Null in PresentationSettings means this standard 104-key arrangement.</summary>
public sealed class PresentationKeyboardLayout
{
    public const int MaximumKeys = 128;
    public List<PresentationKeyboardKey> Keys { get; set; } = DefaultKeys();
    public int MinimumPrimitiveCost() => Keys.Where(key => key.Enabled).Sum(key => MinimumPrimitiveCost(key));
    public static int MinimumPrimitiveCost(PresentationKeyboardKey key) =>
        key.Label.Length + 3 + (key.SecondaryLabel is { Length: > 0 } sub ? sub.Length + 2 : 0);
    public static PresentationKeyboardLayout CreateDefault() => new();
    public static PresentationKeyboardKey CreatePlaceholder(string label = "Key") => new()
    {
        Label = label,
        IsPlaceholder = true,
        CustomId = "custom:" + Guid.NewGuid().ToString("N")
    };
    public PresentationKeyboardLayout Clone() => new() { Keys = Keys.Select(key => key.Clone()).ToList() };
    public bool IsValid() => Keys is not null && Keys.Count is > 0 and <= MaximumKeys && Keys.Any(key => key?.Enabled == true) &&
        Keys.All(key => key?.IsValid() == true) &&
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
