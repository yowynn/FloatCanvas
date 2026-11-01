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
    [new(ElementAppearance.ShapeIndependent, "Color.StyleSeparate", true, true), new(ElementAppearance.ShapeUniform, "Color.StyleUnified", true, false)]);
    private static readonly IReadOnlyList<AppearanceVariant> numbers = Array.AsReadOnly<AppearanceVariant>(
    [new(ElementAppearance.NumberFilled, "Style.Number.Filled", true, true),
     new(ElementAppearance.NumberOutline, "Style.Number.Outline", true, false, true, AppearanceColorRole.Foreground),
     new(ElementAppearance.NumberOutlineIndependent, "Style.Number.OutlineIndependent", true, true, true)]);
    private static readonly IReadOnlyList<AppearanceVariant> paint = Array.AsReadOnly<AppearanceVariant>([new(ElementAppearance.Default, "Style.Default", true, false)]);
    private static readonly IReadOnlyList<AppearanceVariant> inactive = Array.AsReadOnly<AppearanceVariant>([new(ElementAppearance.Default, "Style.Default", false, false)]);
    private static readonly IReadOnlyList<AppearanceVariant> presentation = Array.AsReadOnly<AppearanceVariant>([new(ElementAppearance.Default, "Style.Default", true, true)]);
    public static IReadOnlyList<AppearanceVariant> VariantsFor(ElementKind kind) => ElementGeometry.IsBoxShape(kind) ? shapes :
        kind == ElementKind.Number ? numbers : kind == ElementKind.Presentation ? presentation : ElementGeometry.IsLiveEffect(kind) ? inactive : paint;
    public static AppearanceVariant? Find(ElementKind kind, string? id)
    {
        var variants = VariantsFor(kind);
        if (id is null or ElementAppearance.Default) return variants[0];
        return variants.FirstOrDefault(variant => variant.Id == id);
    }
    public static AppearanceVariant Resolve(CanvasElement element) => Find(element.Kind, element.Style.Variant)
        ?? throw new InvalidDataException("Appearance is not supported by this element type.");
}
