using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Complete editable tool state, separate from any open document or selection.</summary>
internal sealed class ToolParameters
{
    public EditorTool Tool { get; set; } = EditorTool.Freehand;
    public ElementStyle Style { get; set; } = new() { Stroke = ColorLibrary.ForegroundRed };
    public double? CornerRadius { get; set; }
    public SymbolKind Symbol { get; set; }
    public bool ConstrainAspect { get; set; }
    public double EffectStrength { get; set; } = 12;
    public string Emoji { get; set; } = "😀";
    public double DefaultWidth { get; set; } = 160;
    public double DefaultHeight { get; set; } = 100;
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    public int NumberColorRolesVersion { get; set; } = 1;
    public CaptureShape CaptureShape { get; set; } = CaptureShape.Rectangle;
    public CaptureSource CaptureSource { get; set; } = CaptureSource.Background;
    public double CaptureBorderWidth { get; set; }
    public uint CaptureBorderColor { get; set; } = ColorLibrary.ForegroundRed;

    public ToolParameters Clone()
    {
        var copy = (ToolParameters)MemberwiseClone(); copy.Style = Style.Clone(); return copy;
    }

    internal bool IsValid() => Enum.IsDefined(Tool) && Style is { } s && Enum.IsDefined(Symbol) && Emoji is { Length: > 0 and <= 32 } &&
        NumberStart is >= -999999 and <= 999999 && NumberStep is >= -999999 and <= 999999 &&
        NumberColorRolesVersion is 0 or 1 &&
        Enum.IsDefined(CaptureShape) && Enum.IsDefined(CaptureSource) && double.IsFinite(CaptureBorderWidth) && CaptureBorderWidth is >= 0 and <= 1024 &&
        double.IsFinite(DefaultWidth) && DefaultWidth >= 1 && DefaultWidth <= 1000000 && double.IsFinite(DefaultHeight) && DefaultHeight >= 1 && DefaultHeight <= 1000000 &&
        !string.IsNullOrWhiteSpace(Emoji) && System.Globalization.StringInfo.GetNextTextElement(Emoji).Length == Emoji.Length &&
        (CornerRadius is null || double.IsFinite(CornerRadius.Value) && CornerRadius >= 0) &&
        double.IsFinite(EffectStrength) && EffectStrength > 0 && EffectStrength <= 1024 &&
        double.IsFinite(s.StrokeWidth) && s.StrokeWidth >= 0 && s.StrokeWidth <= 1024 &&
        double.IsFinite(s.Opacity) && s.Opacity >= 0 && s.Opacity <= 1 &&
        double.IsFinite(s.FontSize) && s.FontSize > 0 && s.FontSize <= 4096 &&
        s.FontFamily is { Length: > 0 and <= 1024 } && Enum.IsDefined(s.TextAlignment) &&
        Enum.IsDefined(s.ArrowHeadStyle) && double.IsFinite(s.ArrowHeadSize) && s.ArrowHeadSize >= 0 && s.ArrowHeadSize <= 512 &&
        (s.StartArrowHead is null || Enum.IsDefined(s.StartArrowHead.Value)) &&
        (s.EndArrowHead is null || Enum.IsDefined(s.EndArrowHead.Value)) && Enum.IsDefined(s.StrokePattern);
}
