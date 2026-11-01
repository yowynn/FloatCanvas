namespace FloatCanvas.Core;

/// <summary>Appearance choices and color roles shared by rendering, selection editing and persistence.</summary>
public static class ElementAppearance
{
    public const string Default = "default";
    public const string ShapeIndependent = "shape.independent";
    public const string ShapeUniform = "shape.uniform";
    public const string NumberFilled = "number.filled";
    public const string NumberOutline = "number.outline";
    public const string NumberOutlineIndependent = "number.outline-independent";

    public static bool IsValidVariant(ElementKind kind, string? variant) => AppearanceRegistry.Find(kind, variant) is not null;

    public static bool LinkedColors(ElementStyle style) => style.Variant switch
    {
        ShapeUniform => true,
        ShapeIndependent => false,
        _ => false
    };

    public static uint ShapeFill(ElementStyle style) => LinkedColors(style) ? style.Stroke : style.Fill;
    public static bool IsOutlineNumber(CanvasElement element) => element.Kind == ElementKind.Number && AppearanceRegistry.Resolve(element).IsOutlineNumber;
    public static string NumberVariant(CanvasElement element) => AppearanceRegistry.Resolve(element).Id;
    public static uint PrimaryColor(CanvasElement element) => element.Style.Foreground;
    public static uint SecondaryColor(CanvasElement element) => element.Style.Background;
    public static uint NumberTextColor(CanvasElement element) => AppearanceRegistry.Resolve(element).NumberTextRole == AppearanceColorRole.Foreground ? PrimaryColor(element) : SecondaryColor(element);

    /// <summary>Changing a color preserves a legacy badge's font-size rule and its other visible color.</summary>
    public static void SetPrimaryColor(CanvasElement element, uint color)
    {
        element.Style.Foreground = color;
    }

    public static void SetSecondaryColor(CanvasElement element, uint color)
    {
        element.Style.Background = color;
    }

    public static void SetVariant(CanvasElement element, string variant)
    {
        if (!IsValidVariant(element.Kind, variant)) throw new ArgumentException("Appearance is not supported by this element type.", nameof(variant));
        element.Style.Variant = variant;
    }
}
