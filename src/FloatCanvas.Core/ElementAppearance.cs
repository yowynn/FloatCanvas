namespace FloatCanvas.Core;

/// <summary>Appearance choices and color roles shared by rendering, selection editing and persistence.</summary>
public static class ElementAppearance
{
    public const string Default = "default";
    public const string ShapeIndependent = "shape.independent";
    public const string ShapeUniform = "shape.uniform";
    public const string TextOutline = "text.outline";
    public const string TextLabel = "text.label";
    public const string NumberFilled = "number.filled";
    public const string NumberOutline = "number.bordered";

    public static bool IsValidVariant(ElementKind kind, string? variant) => AppearanceRegistry.Find(kind, variant) is not null;

    public static bool LinkedColors(ElementStyle style) => style.Variant == ShapeUniform;

    public static bool HasShapeStroke(ElementStyle style) => !LinkedColors(style);
    public static uint ShapeFill(ElementStyle style) => HasShapeStroke(style) ? style.SecondaryColor : style.PrimaryColor;
    public static bool IsOutlineNumber(CanvasElement element) => element.Kind == ElementKind.Number && AppearanceRegistry.Resolve(element).IsOutlineNumber;
    public static uint PrimaryColor(CanvasElement element) => element.Style.PrimaryColor;
    public static uint SecondaryColor(CanvasElement element) => element.Style.SecondaryColor;
    public static uint NumberTextColor(CanvasElement element) => AppearanceRegistry.Resolve(element).NumberTextRole == AppearanceColorRole.Foreground ? PrimaryColor(element) : SecondaryColor(element);

    /// <summary>Changing a color preserves a legacy badge's font-size rule and its other visible color.</summary>
    public static void SetPrimaryColor(CanvasElement element, uint color)
    {
        element.Style.PrimaryColor = color;
    }

    public static void SetSecondaryColor(CanvasElement element, uint color)
    {
        element.Style.SecondaryColor = color;
    }

    public static void SetVariant(CanvasElement element, string variant) => SetVariant(element.Style, element.Kind, variant);

    public static void SetVariant(ElementStyle style, ElementKind kind, string variant)
    {
        if (!IsValidVariant(kind, variant)) throw new ArgumentException("Appearance is not supported by this element type.", nameof(variant));
        if (kind == ElementKind.Text)
        {
            if (style.Variant == TextOutline) style.HasTextOutlineColor = true;
            if (variant == TextOutline && !style.HasTextOutlineColor)
            {
                style.SecondaryColor = BuiltInColors.InitialTextOutline(style.PrimaryColor);
                style.HasTextOutlineColor = true;
            }
        }
        style.Variant = variant;
    }
}
