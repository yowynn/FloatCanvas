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
        NumericParameterDomain.ForTool(nameof(NumberStart)).Contains(NumberStart) && NumericParameterDomain.ForTool(nameof(NumberStep)).Contains(NumberStep) &&
        NumberColorRolesVersion is 0 or 1 &&
        Enum.IsDefined(CaptureShape) && Enum.IsDefined(CaptureSource) && NumericParameterDomain.ForTool(nameof(CaptureBorderWidth)).Contains(CaptureBorderWidth) &&
        NumericParameterDomain.ForTool(nameof(DefaultWidth)).Contains(DefaultWidth) && NumericParameterDomain.ForTool(nameof(DefaultHeight)).Contains(DefaultHeight) &&
        !string.IsNullOrWhiteSpace(Emoji) && System.Globalization.StringInfo.GetNextTextElement(Emoji).Length == Emoji.Length &&
        (CornerRadius is null || NumericParameterDomain.ForTool(nameof(CornerRadius)).Contains(CornerRadius.Value)) &&
        NumericParameterDomain.ForTool(nameof(EffectStrength)).Contains(EffectStrength) &&
        NumericParameterDomain.ForStyle(nameof(s.StrokeWidth)).Contains(s.StrokeWidth) &&
        NumericParameterDomain.ForStyle(nameof(s.Opacity)).Contains(s.Opacity) &&
        NumericParameterDomain.ForStyle(nameof(s.FontSize)).Contains(s.FontSize) &&
        s.FontFamily is { Length: > 0 and <= 1024 } && Enum.IsDefined(s.TextAlignment) &&
        Enum.IsDefined(s.ArrowHeadStyle) && NumericParameterDomain.ForStyle(nameof(s.ArrowHeadSize)).Contains(s.ArrowHeadSize) &&
        (s.StartArrowHead is null || Enum.IsDefined(s.StartArrowHead.Value)) &&
        (s.EndArrowHead is null || Enum.IsDefined(s.EndArrowHead.Value)) && Enum.IsDefined(s.StrokePattern);
}
