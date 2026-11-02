using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public enum PresentationKeyboardScope { AllApplications = 0, OutsideFloatCanvas = 1, FloatCanvasOnly = 2 }
public enum PresentationClickShape { Ring = 0, Disc = 1 }

/// <summary>One button's screen-space click visualization. This is a tool preference, not a scene node.</summary>
public sealed class PresentationClickStyle
{
    public bool Enabled { get; set; } = true;
    public uint Color { get; set; } = 0xFFFFC107;
    public double RadiusPixels { get; set; } = 24;
    public double StrokeWidthPixels { get; set; } = 2;
    public double DurationMilliseconds { get; set; } = 450;
    // Compatibility input only. NormalizeColorAlpha folds this into Color once.
    public double Opacity { get; set; } = 1;
    public PresentationClickShape Shape { get; set; }
    public PresentationClickStyle Clone() => (PresentationClickStyle)MemberwiseClone();
    public void NormalizeColorAlpha()
    { if (!IsValid()) return; Color = PresentationPreferenceRange.FoldAlpha(Color, Opacity); Opacity = 1; }
    public bool IsValid() => Enum.IsDefined(Shape) && PresentationPreferenceRange.Valid(RadiusPixels, 2, 128) &&
        PresentationPreferenceRange.Valid(StrokeWidthPixels, .5, 32) && PresentationPreferenceRange.Valid(DurationMilliseconds, 100, 2000) &&
        PresentationPreferenceRange.Valid(Opacity, 0, 1);
}

/// <summary>Global physical-screen pointer effects. No input samples or canvas placement are persisted.</summary>
public sealed class PresentationPointerSettings
{
    public int ColorAlphaVersion { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool CursorEnabled { get; set; } = true;
    public bool ClicksEnabled { get; set; } = true;
    public bool TrailEnabled { get; set; }
    public uint CursorColor { get; set; } = 0x3DFFD54F;
    public double CursorRadiusPixels { get; set; } = 18;
    public double CursorOpacity { get; set; } = 1;
    public PresentationClickStyle LeftClick { get; set; } = new();
    public PresentationClickStyle RightClick { get; set; } = new() { Color = 0xFF42A5F5 };
    public PresentationClickStyle MiddleClick { get; set; } = new() { Color = 0xFFAB47BC };
    public uint TrailColor { get; set; } = 0xE6FFD54F;
    public double TrailWidthPixels { get; set; } = 3;
    public double TrailMilliseconds { get; set; } = 800;
    public double TrailOpacity { get; set; } = 1;
    [JsonIgnore] public bool AnyEnabled => Enabled && (CursorEnabled || TrailEnabled || ClicksEnabled &&
        (LeftClick?.Enabled == true || RightClick?.Enabled == true || MiddleClick?.Enabled == true));

    public PresentationPointerSettings Clone()
    {
        var copy = (PresentationPointerSettings)MemberwiseClone();
        copy.LeftClick = LeftClick?.Clone()!; copy.RightClick = RightClick?.Clone()!; copy.MiddleClick = MiddleClick?.Clone()!;
        return copy;
    }
    public void NormalizeColorAlpha()
    {
        if (!IsValid()) return;
        CursorColor = PresentationPreferenceRange.FoldAlpha(CursorColor, CursorOpacity); CursorOpacity = 1;
        TrailColor = PresentationPreferenceRange.FoldAlpha(TrailColor, TrailOpacity); TrailOpacity = 1;
        LeftClick?.NormalizeColorAlpha(); RightClick?.NormalizeColorAlpha(); MiddleClick?.NormalizeColorAlpha();
        ColorAlphaVersion = Math.Max(1, ColorAlphaVersion);
    }
    public bool IsValid() => ColorAlphaVersion >= 0 && LeftClick?.IsValid() == true && RightClick?.IsValid() == true && MiddleClick?.IsValid() == true &&
        PresentationPreferenceRange.Valid(CursorRadiusPixels, 2, 128) && PresentationPreferenceRange.Valid(CursorOpacity, 0, 1) &&
        PresentationPreferenceRange.Valid(TrailWidthPixels, 1, 64) && PresentationPreferenceRange.Valid(TrailMilliseconds, 100, 5000) &&
        PresentationPreferenceRange.Valid(TrailOpacity, 0, 1);
    public PresentationClickStyle? GetClick(PresentationMouseButton button) => button switch
    {
        PresentationMouseButton.Left => LeftClick, PresentationMouseButton.Right => RightClick,
        PresentationMouseButton.Middle => MiddleClick, _ => null
    };
}

/// <summary>Personal demonstration preferences, separate from authored keyboard display layers.</summary>
public sealed class PresentationGlobalSettings
{
    public PresentationPointerSettings Pointer { get; set; } = new();
    /// <summary>Legacy profile preference, migrated into the keyboard tool default. It never overrides authored layers.</summary>
    public PresentationKeyboardScope KeyboardScope { get; set; }
    public PresentationGlobalSettings Clone() => new() { Pointer = Pointer?.Clone()!, KeyboardScope = KeyboardScope };
    public bool IsValid() => Pointer?.IsValid() == true && Enum.IsDefined(KeyboardScope);
}

internal static class PresentationPreferenceRange
{
    internal static uint FoldAlpha(uint color, double opacity) => (color & 0xFFFFFF) |
        ((uint)Math.Clamp((int)Math.Round((color >> 24) * opacity), 0, 255) << 24);
    internal static bool Valid(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
}
