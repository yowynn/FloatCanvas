namespace FloatCanvas.Core;

public readonly record struct LegacyAppearance(uint Stroke, uint Fill, bool LinkColors, string? Variant);

/// <summary>One legacy interpretation shared by wire readers and tool configuration adapters.</summary>
public static class AppearanceMigration
{
    public const string LegacyNumberOutline = "number.outline";
    public const string LegacyTwoColorNumberOutline = "number.outline-independent";

    public static void MigrateElement(CanvasElement element, LegacyAppearance legacy, bool numberUsesForeground)
    {
        MigrateToolStyle(element.Style, element.Kind, legacy, numberUsesForeground ? 1 : 0);
        element.NumberTextSizeMode = numberUsesForeground ? NumberTextSizeMode.FitBadge : NumberTextSizeMode.CapFontSize;
    }
    public static void MigrateToolStyle(ElementStyle style, ElementKind? kind, LegacyAppearance legacy, int numberColorRolesVersion)
    {
        if (numberColorRolesVersion is not (0 or 1)) throw new InvalidDataException("Unsupported legacy number color roles.");
        style.Foreground = legacy.Stroke; style.Background = legacy.Fill;
        if (kind == ElementKind.Number && legacy.Variant is null && numberColorRolesVersion == 0)
        {
            style.AdditionalColors["legacy.stroke"] = legacy.Stroke; style.AdditionalColors["legacy.fill"] = legacy.Fill;
            style.Foreground = (legacy.Fill >> 24) == 0 ? legacy.Stroke : legacy.Fill; style.Background = 0xFFFFFFFF;
        }
        style.Variant = legacy.Variant ?? (kind is { } shape && ElementGeometry.IsBoxShape(shape)
            ? legacy.LinkColors ? ElementAppearance.ShapeUniform : ElementAppearance.ShapeIndependent
            : kind == ElementKind.Number ? ElementAppearance.NumberFilled : ElementAppearance.Default);
        UpgradeNumberOutline(style, kind);
        if (kind is { } actual && AppearanceRegistry.Find(actual, style.Variant) is null) throw new InvalidDataException("Unsupported legacy appearance.");
    }

    public static bool IsLegacyNumberOutline(string? variant) =>
        variant is AppearanceMigration.LegacyNumberOutline or AppearanceMigration.LegacyTwoColorNumberOutline;

    public static void UpgradeNumberOutline(ElementStyle style, ElementKind? kind)
    {
        if (kind != ElementKind.Number || !IsLegacyNumberOutline(style.Variant)) return;
        // Both historical rings had a transparent interior. The new digit uses the ring ink.
        style.Variant = ElementAppearance.NumberOutline;
        style.SecondaryColor &= 0x00FFFFFF;
    }
}
