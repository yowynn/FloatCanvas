using L = FloatCanvas.Localization.AppText;
using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public enum ElementKind { Freehand, Highlighter, Line, Arrow, DoubleArrow, Rectangle, RoundedRectangle, Ellipse, Triangle, Text, Number, Image, Symbol, LivePixelate, LiveBlur, Presentation }
public enum DocumentKind { LiveOverlay, FrozenCapture }
public enum TextAlignment { Left, Center, Right }
public enum ImageEffectKind { Blur = 0, Pixelate = 1 }
public enum LayerOrder { Forward, Backward, Front, Back }
public enum ArrowHeadStyle { FilledTriangle, Open, Diamond, None }
public enum StrokePattern { Solid, Dashed, Dotted, DashDot }
public enum HighlighterTip { Round, Flat }
public enum SymbolKind { Triangle, Star, Heart, Check, Cross, Diamond, Pentagon, Hexagon, SpeechBubble }

public sealed class ElementStyle : IJsonOnDeserialized
{
    private uint foreground = 0xFFEF4444, background;
    private uint? legacyStroke, legacyFill;
    private bool? legacyLinkColors;
    private bool canonicalColorsRead;
    private bool deserialized;
    private string? variant = ElementAppearance.Default;
    private bool variantRead;
    public uint Foreground { get => foreground; set { foreground = value; canonicalColorsRead = true; } }
    public uint Background { get => background; set { background = value; canonicalColorsRead = true; } }
    [JsonConverter(typeof(AppearanceColorRolesConverter))]
    public Dictionary<string, uint> AdditionalColors { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Stable appearance identifier. Null is accepted only as a transitional in-memory default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variant { get => variant; set { variant = value; variantRead = true; } }
    [JsonIgnore] public uint Stroke { get => Foreground; set => Foreground = value; }
    [JsonIgnore] public uint Fill { get => Background; set => Background = value; }
    [JsonIgnore] public bool LinkColors
    {
        get => Variant == ElementAppearance.ShapeUniform;
        set { if (Variant is null or ElementAppearance.Default or ElementAppearance.ShapeIndependent or ElementAppearance.ShapeUniform) Variant = value ? ElementAppearance.ShapeUniform : ElementAppearance.ShapeIndependent; }
    }
    // Read-only wire aliases. Getters never emit a second persisted color authority.
    [JsonPropertyName("Stroke"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? LegacyStrokeWire { get => null; set => legacyStroke = value; }
    [JsonPropertyName("Fill"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? LegacyFillWire { get => null; set => legacyFill = value; }
    [JsonPropertyName("LinkColors"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyLinkColorsWire { get => null; set => legacyLinkColors = value; }
    [JsonIgnore] internal bool HasLegacyInput => deserialized && !canonicalColorsRead || legacyStroke is not null || legacyFill is not null || legacyLinkColors is not null;
    [JsonIgnore] internal LegacyAppearance LegacyInput => new(legacyStroke ?? foreground, legacyFill ?? background, legacyLinkColors ?? false, variantRead ? Variant : null);
    internal void ClearLegacyInput() { legacyStroke = null; legacyFill = null; legacyLinkColors = null; canonicalColorsRead = true; }
    void IJsonOnDeserialized.OnDeserialized() => deserialized = true;
    public double StrokeWidth { get; set; } = 4;
    public ArrowHeadStyle ArrowHeadStyle { get; set; } = ArrowHeadStyle.FilledTriangle;
    /// <summary>Head length in local document pixels; zero preserves automatic max(10, StrokeWidth * 4).</summary>
    public double ArrowHeadSize { get; set; }
    /// <summary>Null retains the legacy Arrow/DoubleArrow head; None explicitly disables this end.</summary>
    public ArrowHeadStyle? StartArrowHead { get; set; }
    public ArrowHeadStyle? EndArrowHead { get; set; }
    /// <summary>Signed perpendicular handle offset from the chord midpoint, in chord lengths. Null selects a straight line.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LineArcBulge { get; set; }
    /// <summary>Handle offset along the chord from its midpoint, in chord lengths.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double LineArcOffset { get; set; }
    public StrokePattern StrokePattern { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public HighlighterTip HighlighterTip { get; set; }
    /// <summary>Clockwise degrees from vertical in the stroke's local coordinates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double HighlighterTipAngle { get; set; }
    public double HighlighterInkContrast { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 24;
    public TextAlignment TextAlignment { get; set; }
    public ElementStyle Clone()
    {
        var copy = (ElementStyle)MemberwiseClone(); copy.AdditionalColors = new(AdditionalColors, StringComparer.Ordinal); return copy;
    }
}

public sealed class ImageEffect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ImageEffectKind Kind { get; set; }
    public RectD Bounds { get; set; }
    public double Amount { get; set; } = 12;
    public uint Color { get; set; } = 0xFF111111;
    public ImageEffect Clone(bool newId = false) => new() { Id = newId ? Guid.NewGuid() : Id, Kind = Kind, Bounds = Bounds, Amount = Amount, Color = Color };
}

public sealed class CanvasElement : IJsonOnDeserialized
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Optional user layer label. Null keeps the localized default for the element kind.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    public ElementKind Kind { get; set; }
    public PointCollection Points { get; set; } = [];
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Null uses the element kind's default; text and box shapes resize freely.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConstrainAspect { get; set; }
    /// <summary>A resized text frame keeps its bounds while text is fitted and wrapped inside it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TextFrameFixed { get; set; }
    /// <summary>Local pixels; null preserves the legacy rounded rectangle's 20 percent radius.</summary>
    public double? CornerRadius { get; set; }
    public SymbolKind Symbol { get; set; }
    public Guid? GroupId { get; set; }
    public Affine2D Transform { get; set; } = Affine2D.Identity;
    /// <summary>Optional local placement applied before the ordinary editable affine transform.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Projective2D? ProjectivePlacement { get; set; }
    /// <summary>Screen-facing placement using the canvas's shared depth scale. Missing in older files means planar.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsBillboard { get; set; }
    [JsonIgnore] public BillboardSurfacePlacement? BillboardSurface { get; set; }
    /// <summary>Ground position in canvas-plane coordinates. Independent of the content's local center.</summary>
    public PointD BillboardAnchor { get; set; }
    /// <summary>Logical height above the ground anchor; screen distance is height times the depth scale.</summary>
    public double BillboardHeight { get; set; }
    private ElementStyle style = new();
    private bool styleRead;
    public ElementStyle Style { get => style; set { style = value; styleRead = true; } }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PresentationSettings? Presentation { get; set; }
    public string Text { get; set; } = "";
    public int Number { get; set; } = 1;
    /// <summary>Badge text can fit its body or retain the historical FontSize cap, independently of its colors.</summary>
    public NumberTextSizeMode NumberTextSizeMode { get; set; }
    [JsonIgnore] public bool NumberUsesForeground
    {
        get => NumberTextSizeMode == global::FloatCanvas.Core.NumberTextSizeMode.FitBadge;
        set => NumberTextSizeMode = value ? global::FloatCanvas.Core.NumberTextSizeMode.FitBadge : global::FloatCanvas.Core.NumberTextSizeMode.CapFontSize;
    }
    [JsonIgnore] internal bool LegacyNumberUsesForegroundInput { get; private set; }
    [JsonPropertyName("NumberUsesForeground"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyNumberUsesForegroundWire { get => null; set => LegacyNumberUsesForegroundInput = value ?? false; }
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Style is null) return;
        if (!styleRead) AppearanceMigration.MigrateElement(this, new(Style.Foreground, Style.Background, false, null), LegacyNumberUsesForegroundInput);
        else AppearanceMigration.NormalizeElement(this);
    }
    public string? AssetId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowSourceDescriptor? WindowSource { get; set; }
    public RectD? ImageCrop { get; set; }
    /// <summary>Capture contour in normalized image-body coordinates. Null retains the source bitmap's full frame.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PointD>? ImageFrame { get; set; }
    /// <summary>Expansion of nonzero image alpha, painted below the image. Missing means no border, regardless of legacy Style.StrokeWidth.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ImageBorderWidth { get; set; }
    public List<ImageEffect> Effects { get; set; } = [];
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public CanvasElement Clone(bool newId = false)
    {
        // Preserve scalar state without generating a throwaway ID/style/point list for every history or preview copy.
        // Mutable owned data remains detached; PointCollection.Snapshot retains its copy-on-write contract.
        var copy = (CanvasElement)MemberwiseClone();
        if (newId) copy.Id = Guid.NewGuid();
        copy.Points = Points.Snapshot(); copy.Style = Style.Clone(); copy.Presentation = Presentation?.Clone();
        copy.WindowSource = WindowSource?.Clone(); copy.ImageFrame = ImageFrame?.ToList();
        copy.Effects = Effects.Select(effect => effect.Clone(newId)).ToList(); copy.LegacyNumberUsesForegroundInput = false;
        return copy;
    }
}

public sealed class DisplayLayoutHint
{
    public string DeviceName { get; set; } = "";
    public RectD Bounds { get; set; }
    public double DpiX { get; set; } = 96;
    public double DpiY { get; set; } = 96;
    public DisplayLayoutHint Clone() => (DisplayLayoutHint)MemberwiseClone();
}

public sealed partial class SceneDocument : SceneNodeGraph
{
    private SceneResourceIdentity resourceIdentity = new(Guid.NewGuid());
    public const int CurrentVersion = 20;
    public int Version { get; set; } = CurrentVersion;
    public Guid ResourceId { get => resourceIdentity.Id; set => resourceIdentity = new(value); }
    public List<PrefabDefinition> Definitions { get; set; } = [];
    public Dictionary<Guid, RootCanvasKind> RootCanvasAssignments { get; set; } = [];
    public Dictionary<RootCanvasKind, SubprojectCanvasSettings> RootPreviewCanvases { get; set; } = [];
    [JsonIgnore] public bool ResourceIdentityPersistent => resourceIdentity.Persistent;
    public void RecordResourceIdentityPersistence() => resourceIdentity.Persistent = true;
    [JsonIgnore] public DocumentProvenance Provenance { get; set; } = new();
    // An entry definition's Origin accompanies its detached header in editor
    // snapshots; package writers retain it on that definition, never on a scene.
    [JsonIgnore] internal PointD IndependentCanvasOrigin { get; set; }
    public DocumentKind Kind { get; set; }
    public CanvasElement? Background { get; set; }
    public CanvasSettings Canvas { get; set; } = new();
    public RectD Viewport { get; set; } = new(0, 0, 1920, 1080);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanvasAuthoringReference? AuthoringReference { get; set; }
    public double LayerOpacity { get; set; } = 1;
    public int NextNumber { get; set; } = 1;
    public List<DisplayLayoutHint> DisplayLayout { get; set; } = [];
    [JsonIgnore] public IEnumerable<string> ReferencedAssets => ReferencedAssetIds.Concat(Background?.AssetId is { } asset ? [asset] : [])
        .Concat(RootPreviewCanvases.Values.SelectMany(preview => preview.ReferencedAssetIds))
        .Concat(Definitions.SelectMany(definition => definition.Graph.ReferencedAssetIds.Concat(definition.PreviewCanvas?.ReferencedAssetIds ?? [])))
        .Distinct(StringComparer.Ordinal);
    public SceneDocument Clone(bool newIds = false, IReadOnlyDictionary<string, string>? assetIdMap = null)
    {
        var cloned = SceneGraphClone.CloneGraph(this, newIds ? GraphCloneIdentity.NewNodes : GraphCloneIdentity.Preserve, assetIds: assetIdMap);
        var graph = cloned.Graph;
        var background = Background?.Clone(newIds);
        if (background?.AssetId is { } asset && assetIdMap is not null) background.AssetId = assetIdMap[asset];
        var result = new SceneDocument
        {
            Version = Version, Provenance = Provenance, IsRuntimeEvaluated = graph.IsRuntimeEvaluated, Kind = Kind, IndependentCanvasOrigin = IndependentCanvasOrigin,
            Elements = graph.Elements, Groups = graph.Groups, Instances = graph.Instances, RootOrder = graph.RootOrder,
            RootCanvasAssignments = RootCanvasAssignments.ToDictionary(pair => cloned.NodeIdMap[pair.Key], pair => pair.Value),
            RootPreviewCanvases = RootPreviewCanvases.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(assetIdMap)),
            Definitions = Definitions.Select(definition => definition.Clone(assetIdMap)).ToList(), Background = background, Canvas = Canvas.Clone(),
            Viewport = Viewport, AuthoringReference = AuthoringReference, LayerOpacity = LayerOpacity, NextNumber = NextNumber, DisplayLayout = DisplayLayout.Select(d => d.Clone()).ToList()
        };
        result.resourceIdentity = resourceIdentity; return result;
    }
    /// <summary>Private topology workspace. Only supplied roots may have their element placement changed.
    /// Unchanged geometry/resources are borrowed for synchronous preparation and never mutated.</summary>
    internal SceneDocument CreatePlacementDraft(IReadOnlySet<Guid> roots)
    {
        var result = (SceneDocument)MemberwiseClone();
        result.Elements = Elements.Select(element => roots.Contains(element.Id) ? element.Clone() : element).ToList();
        result.Groups = Groups.Select(group => group.Clone()).ToList();
        result.Instances = Instances.Select(instance => instance.Clone()).ToList();
        result.RootOrder = RootOrder?.ToList();
        result.RootCanvasAssignments = new(RootCanvasAssignments);
        return result;
    }
    /// <summary>The supplied bitmap asset represents exactly the captured region.</summary>
    public SceneDocument CreateCapture(RectD region, string backgroundAssetId)
    {
        region = region.Normalize();
        if (region.Width < 1 || region.Height < 1) throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(region));
        var translation = Affine2D.Translation(-region.X, -region.Y);
        var evaluation = SceneEvaluation.Build(this);
        var capture = CloneElements(evaluation.Leaves.Where(leaf => leaf.RenderElement.Visible && ElementGeometry.Bounds(leaf.RenderElement).Intersects(region)).Select(leaf => leaf.HostNodeId));
        capture.Version = CurrentVersion; capture.Provenance = new(); capture.Kind = DocumentKind.FrozenCapture;
        capture.Viewport = new(0, 0, region.Width, region.Height);
        capture.Background = new() { Kind = ElementKind.Image, AssetId = backgroundAssetId, Width = region.Width, Height = region.Height, Locked = true };
        // Translate ownership roots once; descendants retain genuine local geometry.
        foreach (var group in capture.Groups.Where(group => group.ParentId is null))
            group.Transform = translation.Compose(group.Transform);
        foreach (var element in capture.Elements.Where(element => element.GroupId is null))
            BillboardGeometry.ApplyPlaneTransform(element, translation);
        foreach (var instance in capture.Instances.Where(instance => instance.ParentId is null))
            instance.Transform = translation.Compose(instance.Transform);
        capture.ResourceId = Guid.NewGuid();
        capture.DisplayLayout = DisplayLayout.Select(display => display.Clone()).ToList();
        return capture.Clone(true);
    }
}
