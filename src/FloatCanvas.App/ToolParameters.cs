using FloatCanvas.Core;
using System.Text.Json.Serialization;

namespace FloatCanvas.App;

internal enum LinePathMode { Straight, Arc }

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
            if (value != EditorTool.Number) Numbering = null;
            if (value != EditorTool.Symbol) { CustomSymbol = null; if (Symbol == SymbolKind.Custom) Symbol = SymbolKind.Triangle; }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SymbolShape? CustomSymbol { get; set; }
    public bool ConstrainAspect { get; set; }
    public double EffectStrength { get; set; } = 12;
    public string Emoji { get; set; } = "😀";
    public double DefaultWidth { get; set; } = 160;
    public double DefaultHeight { get; set; } = 100;
    internal double SymbolSize => Math.Max(DefaultWidth, DefaultHeight);
    internal void SetSymbolSize(double edge)
    {
        var size = SymbolGeometry.Shape(Symbol, CustomSymbol).SizeAtLongEdge(edge);
        // The shared preset dimensions start at 1 px. Placement computes the exact
        // short edge from the artwork, including subpixel edges on very small symbols.
        DefaultWidth = Math.Max(1, size.X); DefaultHeight = Math.Max(1, size.Y);
        ConstrainAspect = true;
    }
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NumberSequenceSettings? Numbering { get; set; }
    public NumberTextSizeMode NumberTextSizeMode { get; set; } = NumberTextSizeMode.FitBadge;
    public CaptureShape CaptureShape { get; set; } = CaptureShape.Rectangle;
    public CaptureSource CaptureSource { get; set; } = CaptureSource.Background;
    public double CaptureBorderWidth { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PresentationSettings? Presentation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PresentationPointerSettings? PointerPresentation { get; set; }

    // Toolbar state tracks the immutable sequence separately, and a thumbnail
    // depends on only one resolved label. Avoid serializing a long list on each frame.
    internal string FingerprintWithoutNumbering() => Fingerprint(includeNumbering: false);
    internal string Fingerprint(bool includeNumbering = true)
    {
        if (CustomSymbol is null && (includeNumbering || Numbering is null)) return System.Text.Json.JsonSerializer.Serialize(this);
        var snapshot = (ToolParameters)MemberwiseClone();
        if (!includeNumbering) snapshot.Numbering = null;
        snapshot.CustomSymbol = null;
        return System.Text.Json.JsonSerializer.Serialize(snapshot) + CustomSymbol?.Fingerprint;
    }

    public ToolParameters Clone()
    {
        var copy = (ToolParameters)MemberwiseClone(); copy.Style = Style.Clone(); copy.Presentation = Presentation?.Clone();
        copy.PointerPresentation = PointerPresentation?.Clone(); return copy;
    }

    internal ElementKind? AppearanceKind => Tool == EditorTool.Emoji ? null :
        Enum.TryParse<ElementKind>(Tool.ToString(), out var kind) ? kind : null;

    // Creation-only policy: layer opacity and color alpha have different ownership.
    internal void NormalizeForCreation()
    {
        NormalizeAppearance();
        if (Tool == EditorTool.Symbol && DefaultWidth > 0 && DefaultHeight > 0 && SymbolGeometry.IsValid(Symbol, CustomSymbol))
            SetSymbolSize(SymbolSize);
        if (Style.Opacity is >= 0 and <= 1) Style.Opacity = 1;
        if (Tool == EditorTool.Highlighter && Style.StrokeWidth is >= 0 and < 16) Style.StrokeWidth = 16;
    }

    internal void NormalizeAppearance()
    {
        if (Tool == EditorTool.Presentation) Presentation ??= new();
        if (Tool == EditorTool.PointerPresentation) PointerPresentation ??= new();
        if (Style.Variant is null || Style.Variant == ElementAppearance.Default)
            Style.Variant = AppearanceKind is { } kind && ElementGeometry.IsBoxShape(kind) ? ElementAppearance.ShapeIndependent :
                Tool == EditorTool.Number ? ElementAppearance.NumberFilled : ElementAppearance.Default;
    }

    private bool SupportedAppearance => Style.Variant is null || (AppearanceKind is { } kind
        ? ElementAppearance.IsValidVariant(kind, Style.Variant) : Style.Variant == ElementAppearance.Default);

    internal bool IsValid(bool allowUnsupportedAppearance = false) => Enum.IsDefined(Tool) && Style is { } s && Enum.IsDefined(Symbol) && Emoji is { Length: > 0 and <= 32 } &&
        NumericParameterDomain.ForTool(nameof(NumberStart)).Contains(NumberStart) && NumericParameterDomain.ForTool(nameof(NumberStep)).Contains(NumberStep) &&
        SymbolGeometry.IsValid(Symbol, CustomSymbol) && (CustomSymbol is null || Tool == EditorTool.Symbol) &&
        Enum.IsDefined(NumberTextSizeMode) && (Numbering is null || Tool == EditorTool.Number && Numbering.IsValid()) &&
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
        (s.LineArcBulge is null or >= -ElementGeometry.MaximumLineArcBulge and <= ElementGeometry.MaximumLineArcBulge) &&
        (s.StartArrowHead is null || Enum.IsDefined(s.StartArrowHead.Value)) &&
        (s.EndArrowHead is null || Enum.IsDefined(s.EndArrowHead.Value)) && Enum.IsDefined(s.StrokePattern) &&
        Enum.IsDefined(s.HighlighterTip) && NumericParameterDomain.ForStyle(nameof(s.HighlighterTipAngle)).Contains(s.HighlighterTipAngle) &&
        NumericParameterDomain.ForStyle(nameof(s.HighlighterInkContrast)).Contains(s.HighlighterInkContrast);
}
