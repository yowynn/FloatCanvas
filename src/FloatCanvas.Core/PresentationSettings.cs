using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

[Flags]
public enum PresentationModules { None = 0, Keys = 1, Cursor = 2, Clicks = 4, Trail = 8 }
public enum PresentationKeyDisplayMode { ShortcutChords = 0, AllPhysicalKeys = 1 }
public enum PresentationPanelAlignment { BottomLeft = 0, BottomCenter = 1, BottomRight = 2, TopLeft = 3, TopCenter = 4, TopRight = 5 }

/// <summary>Authored configuration only. Input, arm state and animation never enter this object.</summary>
public sealed class PresentationSettings
{
    /// <summary>Legacy wire value only; visible authored presentation layers always display keyboard input.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Legacy wire value only; it neither disables keyboard display nor enables scene-bound mouse effects.</summary>
    public PresentationModules Modules { get; set; } = PresentationModules.Keys;
    public PresentationKeyDisplayMode KeyDisplay { get; set; } = PresentationKeyDisplayMode.AllPhysicalKeys;
    /// <summary>Authored input scope for this keyboard layer, independent of other layers.</summary>
    public PresentationKeyboardScope KeyboardScope { get; set; } = PresentationKeyboardScope.AllApplications;
    public PresentationDisplayStyle DisplayStyle { get; set; } = PresentationDisplayStyle.Log;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PresentationKeyboardLayout? KeyboardLayout { get; set; }
    public int RecentChordLimit { get; set; } = 4;
    public double KeyHoldMilliseconds { get; set; } = 900;
    public PresentationPanelAlignment KeyPanelAlignment { get; set; }
    public double KeyPanelMaximumWidth { get; set; } = 360;
    public double CursorRadiusPixels { get; set; } = 18;
    public double RippleMilliseconds { get; set; } = 450;
    public double TrailMilliseconds { get; set; } = 800;
    public double TrailWidthPixels { get; set; } = 3;
    public PresentationSettings Clone()
    {
        var copy = (PresentationSettings)MemberwiseClone(); copy.KeyboardLayout = KeyboardLayout?.Clone(); return copy;
    }
    public bool IsValid() => (Modules & ~(PresentationModules.Keys | PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail)) == 0 &&
        Enum.IsDefined(KeyDisplay) && Enum.IsDefined(KeyboardScope) && Enum.IsDefined(DisplayStyle) &&
        (KeyboardLayout?.IsValid() ?? true) && Enum.IsDefined(KeyPanelAlignment) && RecentChordLimit is >= 1 and <= PresentationLimits.MaximumChords &&
        Range(KeyHoldMilliseconds, 100, 5000) && Range(KeyPanelMaximumWidth, 1, 1_000_000) &&
        Range(CursorRadiusPixels, 2, 128) && Range(RippleMilliseconds, 100, 2000) &&
        Range(TrailMilliseconds, 100, 5000) && Range(TrailWidthPixels, 1, 64);
    private static bool Range(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
}

public static class PresentationLimits
{
    public const int MaximumHeldKeys = 128, MaximumChordKeys = 16, MaximumChords = 8, MaximumClicks = 32, MaximumTrailPoints = 512;
    public const int MaximumFramePayloadBytes = 65536, MaximumPrimitives = 8192, MaximumBakePoints = 512, MaximumBakeSegments = 32;
    public const int MaximumScopedFramePayloadBytes = MaximumFramePayloadBytes * 3;
    public const int MaximumKeyLabelLength = 64;
    public static bool IsValidElement(CanvasElement element) => element.Kind == ElementKind.Presentation
        ? element.Presentation?.IsValid() == true && double.IsFinite(element.Width) && element.Width > 0 && double.IsFinite(element.Height) && element.Height > 0 &&
          element.Points is { Count: 0 } && element.AssetId is null && element.Effects is { Count: 0 }
        : element.Presentation is null;
}
