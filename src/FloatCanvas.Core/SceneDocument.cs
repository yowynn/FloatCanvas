using L = FloatCanvas.Localization.AppText;
using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public enum ElementKind { Freehand, Highlighter, Line, Arrow, DoubleArrow, Rectangle, RoundedRectangle, Ellipse, Triangle, Text, Number, Image }
public enum DocumentKind { LiveOverlay, FrozenCapture }
public enum TextAlignment { Left, Center, Right }
public enum ImageEffectKind { Blur, Pixelate, Redact }
public enum LayerOrder { Forward, Backward, Front, Back }
public enum ArrowHeadStyle { FilledTriangle, Open, Diamond }

public sealed class ElementStyle
{
    public uint Stroke { get; set; } = 0xFFEF4444;
    public uint Fill { get; set; }
    public double StrokeWidth { get; set; } = 4;
    public ArrowHeadStyle ArrowHeadStyle { get; set; } = ArrowHeadStyle.FilledTriangle;
    /// <summary>Head length in local document pixels; zero preserves automatic max(10, StrokeWidth * 4).</summary>
    public double ArrowHeadSize { get; set; }
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
    public ElementKind Kind { get; set; }
    public List<PointD> Points { get; set; } = [];
    public double Width { get; set; }
    public double Height { get; set; }
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
        Id = newId ? Guid.NewGuid() : Id, Kind = Kind, Points = [.. Points], Width = Width, Height = Height,
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

public sealed class SceneDocument
{
    public int Version { get; set; } = 1;
    public DocumentKind Kind { get; set; }
    public List<CanvasElement> Elements { get; set; } = [];
    public CanvasElement? Background { get; set; }
    public RectD Viewport { get; set; } = new(0, 0, 1920, 1080);
    public double LayerOpacity { get; set; } = 1;
    public int NextNumber { get; set; } = 1;
    public List<DisplayLayoutHint> DisplayLayout { get; set; } = [];
    [JsonIgnore] public IEnumerable<string> ReferencedAssets => Elements.Append(Background).Where(e => e?.AssetId is not null).Select(e => e!.AssetId!).Distinct(StringComparer.Ordinal);
    public SceneDocument Clone(bool newIds = false) => new()
    {
        Version = Version, Kind = Kind, Elements = Elements.Select(e => e.Clone(newIds)).ToList(), Background = Background?.Clone(newIds),
        Viewport = Viewport, LayerOpacity = LayerOpacity, NextNumber = NextNumber, DisplayLayout = DisplayLayout.Select(d => d.Clone()).ToList()
    };
    /// <summary>The supplied bitmap asset represents exactly the captured region.</summary>
    public SceneDocument CreateCapture(RectD region, string backgroundAssetId)
    {
        region = region.Normalize();
        if (region.Width < 1 || region.Height < 1) throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(region));
        var translation = Affine2D.Translation(-region.X, -region.Y);
        return new()
        {
            Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, region.Width, region.Height), LayerOpacity = LayerOpacity, NextNumber = NextNumber,
            Background = new() { Kind = ElementKind.Image, AssetId = backgroundAssetId, Width = region.Width, Height = region.Height, Locked = true },
            Elements = Elements.Where(e => e.Visible && ElementGeometry.Bounds(e).Intersects(region)).Select(e =>
            {
                var clone = e.Clone(true); clone.Transform = translation.Compose(clone.Transform); return clone;
            }).ToList(),
            DisplayLayout = DisplayLayout.Select(d => d.Clone()).ToList()
        };
    }
}
