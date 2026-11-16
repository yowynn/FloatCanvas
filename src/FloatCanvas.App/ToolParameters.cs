using FloatCanvas.Core;
using System.Text.Json.Serialization;

namespace FloatCanvas.App;

/// <summary>Complete editable tool state, separate from any open document or selection.</summary>
internal sealed class ToolParameters
{
    private EditorTool tool = EditorTool.Freehand;
    public EditorTool Tool
    {
        get => tool;
        set
        {
            var previousKind = AppearanceKind; tool = value;
            if (value == EditorTool.Presentation) Presentation ??= new();
            if (value == EditorTool.PointerPresentation) PointerPresentation ??= new();
            if (Style is { } style && previousKind is { } previous && AppearanceRegistry.Find(previous, style.Variant) is not null &&
                (AppearanceKind is { } current ? AppearanceRegistry.Find(current, style.Variant) is null : style.Variant is not (null or ElementAppearance.Default)))
                style.Variant = ElementAppearance.Default;
        }
    }
    public ElementStyle Style { get; set; } = new() { Foreground = ColorLibrary.ForegroundRed, Variant = ElementAppearance.Default };
    public double? CornerRadius { get; set; }
    public SymbolKind Symbol { get; set; }
    public bool ConstrainAspect { get; set; }
    public double EffectStrength { get; set; } = 12;
    public string Emoji { get; set; } = "😀";
    public double DefaultWidth { get; set; } = 160;
    public double DefaultHeight { get; set; } = 100;
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    [JsonIgnore] public int NumberColorRolesVersion { get; set; } = 1;
    public NumberTextSizeMode NumberTextSizeMode { get; set; } = NumberTextSizeMode.FitBadge;
    public CaptureShape CaptureShape { get; set; } = CaptureShape.Rectangle;
    public CaptureSource CaptureSource { get; set; } = CaptureSource.Background;
    public double CaptureBorderWidth { get; set; }
    public uint CaptureBorderColor { get; set; } = ColorLibrary.ForegroundRed;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PresentationSettings? Presentation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PresentationPointerSettings? PointerPresentation { get; set; }

    public ToolParameters Clone()
    {
        var copy = (ToolParameters)MemberwiseClone(); copy.Style = Style.Clone(); copy.Presentation = Presentation?.Clone();
        copy.PointerPresentation = PointerPresentation?.Clone(); return copy;
    }

    internal ElementKind? AppearanceKind => Tool == EditorTool.Emoji ? ElementKind.Text :
        Enum.TryParse<ElementKind>(Tool.ToString(), out var kind) ? kind : null;

    // Creation-only policy: layer opacity and color alpha have different ownership.
    internal void NormalizeForCreation()
    {
        NormalizeAppearance();
        if (Style.Opacity is >= 0 and <= 1) Style.Opacity = 1;
        if (Tool == EditorTool.Highlighter && Style.StrokeWidth is >= 0 and < 16) Style.StrokeWidth = 16;
    }

    internal void NormalizeAppearance()
    {
        if (Tool == EditorTool.Presentation) Presentation ??= new();
        if (Tool == EditorTool.PointerPresentation) PointerPresentation ??= new();
        PointerPresentation?.NormalizeColorAlpha();
        if (Tool == EditorTool.Number && NumberColorRolesVersion == 0 && Style.Variant is null)
            AppearanceMigration.MigrateToolStyle(Style, ElementKind.Number, new(Style.Foreground, Style.Background, false, null), 0);
        NumberColorRolesVersion = 1;
        if (Style.Variant is null || Style.Variant == ElementAppearance.Default)
            Style.Variant = AppearanceKind is { } kind && ElementGeometry.IsBoxShape(kind) ? ElementAppearance.ShapeIndependent :
                Tool == EditorTool.Number ? ElementAppearance.NumberFilled : ElementAppearance.Default;
    }

    private bool SupportedAppearance => Style.Variant is null || (AppearanceKind is { } kind
        ? ElementAppearance.IsValidVariant(kind, Style.Variant) : Style.Variant == ElementAppearance.Default);

    internal bool IsValid(bool allowUnsupportedAppearance = false) => Enum.IsDefined(Tool) && Style is { } s && Enum.IsDefined(Symbol) && Emoji is { Length: > 0 and <= 32 } &&
        NumericParameterDomain.ForTool(nameof(NumberStart)).Contains(NumberStart) && NumericParameterDomain.ForTool(nameof(NumberStep)).Contains(NumberStep) &&
        NumberColorRolesVersion is 0 or 1 && Enum.IsDefined(NumberTextSizeMode) &&
        (Tool != EditorTool.Presentation || Presentation is not null) && (Presentation is null || Presentation.IsValid()) &&
        (Tool != EditorTool.PointerPresentation || PointerPresentation is not null) && (PointerPresentation is null || PointerPresentation.IsValid()) &&
        (allowUnsupportedAppearance && s.Variant is { Length: > 0 and <= 128 } || SupportedAppearance) &&
        AppearanceRegistry.IsValidColorRoles(s.AdditionalColors) &&
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
