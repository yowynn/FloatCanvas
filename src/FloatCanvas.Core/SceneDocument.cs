using L = FloatCanvas.Localization.AppText;
using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public enum ElementKind { Freehand, Highlighter, Line, Arrow, DoubleArrow, Rectangle, RoundedRectangle, Ellipse, Triangle, Text, Number, Image, Symbol, LivePixelate, LiveBlur }
public enum DocumentKind { LiveOverlay, FrozenCapture }
public enum TextAlignment { Left, Center, Right }
public enum ImageEffectKind { Blur, Pixelate, Redact }
public enum LayerOrder { Forward, Backward, Front, Back }
public enum ArrowHeadStyle { FilledTriangle, Open, Diamond, None }
public enum StrokePattern { Solid, Dashed, Dotted }
public enum SymbolKind { Triangle, Star, Heart, Check, Cross, Diamond, Pentagon, Hexagon, SpeechBubble }

public sealed class ElementStyle
{
    public uint Stroke { get; set; } = 0xFFEF4444;
    public uint Fill { get; set; }
    public double StrokeWidth { get; set; } = 4;
    public ArrowHeadStyle ArrowHeadStyle { get; set; } = ArrowHeadStyle.FilledTriangle;
    /// <summary>Head length in local document pixels; zero preserves automatic max(10, StrokeWidth * 4).</summary>
    public double ArrowHeadSize { get; set; }
    /// <summary>Null retains the legacy Arrow/DoubleArrow head; None explicitly disables this end.</summary>
    public ArrowHeadStyle? StartArrowHead { get; set; }
    public ArrowHeadStyle? EndArrowHead { get; set; }
    public StrokePattern StrokePattern { get; set; }
    public double Opacity { get; set; } = 1;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 24;
    public TextAlignment TextAlignment { get; set; }
    public ElementStyle Clone() => (ElementStyle)MemberwiseClone();
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

public sealed class CanvasElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Optional user layer label. Null keeps the localized default for the element kind.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    public ElementKind Kind { get; set; }
    public List<PointD> Points { get; set; } = [];
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Local pixels; null preserves the legacy rounded rectangle's 20 percent radius.</summary>
    public double? CornerRadius { get; set; }
    public SymbolKind Symbol { get; set; }
    public Guid? GroupId { get; set; }
    public Affine2D Transform { get; set; } = Affine2D.Identity;
    public ElementStyle Style { get; set; } = new();
    public string Text { get; set; } = "";
    public int Number { get; set; } = 1;
    public string? AssetId { get; set; }
    public RectD? ImageCrop { get; set; }
    public List<ImageEffect> Effects { get; set; } = [];
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public CanvasElement Clone(bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid() : Id, Name = Name, Kind = Kind, Points = [.. Points], Width = Width, Height = Height,
        CornerRadius = CornerRadius, Symbol = Symbol, GroupId = GroupId,
        Transform = Transform, Style = Style.Clone(), Text = Text, Number = Number, AssetId = AssetId,
        ImageCrop = ImageCrop, Effects = Effects.Select(effect => effect.Clone(newId)).ToList(), Visible = Visible, Locked = Locked
    };
}

public sealed class DisplayLayoutHint
{
    public string DeviceName { get; set; } = "";
    public RectD Bounds { get; set; }
    public double DpiX { get; set; } = 96;
    public double DpiY { get; set; } = 96;
    public DisplayLayoutHint Clone() => (DisplayLayoutHint)MemberwiseClone();
}

public sealed partial class SceneDocument
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public DocumentKind Kind { get; set; }
    public List<CanvasElement> Elements { get; set; } = [];
    public List<CanvasGroup> Groups { get; set; } = [];
    public CanvasElement? Background { get; set; }
    public RectD Viewport { get; set; } = new(0, 0, 1920, 1080);
    public double LayerOpacity { get; set; } = 1;
    public int NextNumber { get; set; } = 1;
    public List<DisplayLayoutHint> DisplayLayout { get; set; } = [];
    [JsonIgnore] public IEnumerable<string> ReferencedAssets => Elements.Append(Background).Where(e => e?.AssetId is not null).Select(e => e!.AssetId!).Distinct(StringComparer.Ordinal);
    public SceneDocument Clone(bool newIds = false, IReadOnlyDictionary<string, string>? assetIdMap = null)
    {
        var groupIds = Groups.ToDictionary(g => g.Id, g => newIds ? Guid.NewGuid() : g.Id);
        CanvasElement Copy(CanvasElement element)
        {
            var copy = element.Clone(newIds);
            if (copy.GroupId is { } group) copy.GroupId = groupIds[group];
            if (copy.AssetId is { } asset && assetIdMap is not null) copy.AssetId = assetIdMap[asset];
            return copy;
        }
        return new()
        {
            Version = Version, Kind = Kind, Elements = Elements.Select(Copy).ToList(), Background = Background is null ? null : Copy(Background),
            Groups = Groups.Select(g => new CanvasGroup { Id = groupIds[g.Id], ParentId = g.ParentId is { } parent ? groupIds[parent] : null, Name = g.Name }).ToList(),
            Viewport = Viewport, LayerOpacity = LayerOpacity, NextNumber = NextNumber, DisplayLayout = DisplayLayout.Select(d => d.Clone()).ToList()
        };
    }
    /// <summary>The supplied bitmap asset represents exactly the captured region.</summary>
    public SceneDocument CreateCapture(RectD region, string backgroundAssetId)
    {
        region = region.Normalize();
        if (region.Width < 1 || region.Height < 1) throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(region));
        var translation = Affine2D.Translation(-region.X, -region.Y);
        var capture = new SceneDocument
        {
            Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, region.Width, region.Height), LayerOpacity = LayerOpacity, NextNumber = NextNumber,
            Background = new() { Kind = ElementKind.Image, AssetId = backgroundAssetId, Width = region.Width, Height = region.Height, Locked = true },
            Elements = Elements.Where(e => e.Visible && ElementGeometry.Bounds(e).Intersects(region)).Select(e =>
            {
                var clone = e.Clone(); clone.Transform = translation.Compose(clone.Transform); return clone;
            }).ToList(),
            Groups = Groups.Select(g => g.Clone()).ToList(),
            DisplayLayout = DisplayLayout.Select(d => d.Clone()).ToList()
        };
        capture.PruneGroups();
        return capture.Clone(true);
    }
}
