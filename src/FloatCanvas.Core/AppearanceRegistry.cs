namespace FloatCanvas.Core;

public enum AppearanceColorRole { Foreground, Background }
public enum NumberTextSizeMode { CapFontSize = 0, FitBadge = 1 }
public sealed record AppearanceVariant(string Id, string LabelKey, bool ForegroundActive, bool BackgroundActive,
    bool IsOutlineNumber = false, AppearanceColorRole NumberTextRole = AppearanceColorRole.Background);

/// <summary>Stable appearance capabilities. Physical paint roles remain explicit per element family.</summary>
public static class AppearanceRegistry
{
    public const int MaximumAdditionalColors = 16;
    public static bool IsValidColorRoleId(string? id) => id is { Length: > 0 and <= 32 } && id[0] is >= 'a' and <= 'z' &&
        id is not ("foreground" or "background") && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    public static bool IsValidColorRoles(IReadOnlyDictionary<string, uint>? roles) => roles is not null && roles.Count <= MaximumAdditionalColors && roles.Keys.All(IsValidColorRoleId);
    private static readonly IReadOnlyList<AppearanceVariant> shapes = Array.AsReadOnly<AppearanceVariant>(
    [new(ElementAppearance.ShapeUniform, "Style.Solid", true, false), new(ElementAppearance.ShapeIndependent, "Style.Outline", true, true)]);
    private static readonly IReadOnlyList<AppearanceVariant> numbers = Array.AsReadOnly<AppearanceVariant>(
    [new(ElementAppearance.NumberFilled, "Style.Solid", true, true),
     new(ElementAppearance.NumberOutline, "Style.Outline", true, true, true, AppearanceColorRole.Foreground)]);
    private static readonly IReadOnlyList<AppearanceVariant> text = Array.AsReadOnly<AppearanceVariant>(
    [new(ElementAppearance.Default, "Style.Text.Plain", true, false), new(ElementAppearance.TextOutline, "Style.Outline", true, true),
     new(ElementAppearance.TextLabel, "Style.Text.Label", true, false)]);
    private static readonly IReadOnlyList<AppearanceVariant> paint = Array.AsReadOnly<AppearanceVariant>([new(ElementAppearance.Default, "Style.Default", true, false)]);
    private static readonly IReadOnlyList<AppearanceVariant> inactive = Array.AsReadOnly<AppearanceVariant>([new(ElementAppearance.Default, "Style.Default", false, false)]);
    private static readonly IReadOnlyList<AppearanceVariant> presentation = Array.AsReadOnly<AppearanceVariant>([new(ElementAppearance.Default, "Style.Default", true, true)]);
    public static IReadOnlyList<AppearanceVariant> VariantsFor(ElementKind kind) => ElementGeometry.IsBoxShape(kind) ? shapes :
        kind == ElementKind.Number ? numbers : kind == ElementKind.Text ? text : kind == ElementKind.Presentation ? presentation : kind == ElementKind.Emoji || ElementGeometry.IsLiveEffect(kind) ? inactive : paint;
    public static AppearanceVariant? Find(ElementKind kind, string? id)
    {
        var variants = VariantsFor(kind);
        if (id is null or ElementAppearance.Default) return ElementGeometry.IsBoxShape(kind) ? shapes[1] : variants[0];
        return variants.FirstOrDefault(variant => variant.Id == id);
    }
    public static AppearanceVariant Resolve(CanvasElement element) => Find(element.Kind, element.Style.Variant)
        ?? throw new InvalidDataException("Appearance is not supported by this element type.");

    public static string ColorLabel(ElementKind kind, ElementStyle style, bool secondary)
    {
        if (kind == ElementKind.Number)
            return style.Variant == ElementAppearance.NumberOutline
                ? secondary ? "Color.BadgeFill" : "Color.BadgeInk"
                : secondary ? "Color.NumberText" : "Color.BadgeFill";
        if (ElementGeometry.IsBoxShape(kind))
            return secondary || !ElementAppearance.HasShapeStroke(style) ? "Color.Interior" : "Color.Outline";
        if (kind == ElementKind.Presentation) return secondary ? "Presentation.Background" : "Presentation.Foreground";
        if (kind == ElementKind.Text && secondary && style.Variant == ElementAppearance.TextOutline) return "Color.Outline";
        if (secondary) return "Color.Secondary";
        return kind switch
        {
            ElementKind.Freehand or ElementKind.Highlighter => "Color.Stroke",
            ElementKind.Text => style.Variant == ElementAppearance.TextLabel ? "Color.LabelTheme" : "Color.TextInk",
            ElementKind.Image => "Snapshot.BorderColor",
            _ => "Color.Unified"
        };
    }
}
