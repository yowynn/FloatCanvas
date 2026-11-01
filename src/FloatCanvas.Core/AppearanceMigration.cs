namespace FloatCanvas.Core;

public readonly record struct LegacyAppearance(uint Stroke, uint Fill, bool LinkColors, string? Variant);

/// <summary>One legacy interpretation shared by wire readers and tool configuration adapters.</summary>
public static class AppearanceMigration
{
    public static void MigrateElement(CanvasElement element, LegacyAppearance legacy, bool numberUsesForeground)
    {
        MigrateToolStyle(element.Style, element.Kind, legacy, numberUsesForeground ? 1 : 0);
        element.NumberTextSizeMode = numberUsesForeground ? NumberTextSizeMode.FitBadge : NumberTextSizeMode.CapFontSize;
        element.Style.ClearLegacyInput();
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
        if (kind is { } actual && AppearanceRegistry.Find(actual, style.Variant) is null) throw new InvalidDataException("Unsupported legacy appearance.");
        style.ClearLegacyInput();
    }
    /// <summary>Canonicalizes only an owned model; never reinterprets modern colors as legacy aliases.</summary>
    public static void NormalizeElement(CanvasElement element)
    {
        if (element.Style.HasLegacyInput)
            MigrateElement(element, element.Style.LegacyInput, element.LegacyNumberUsesForegroundInput);
        else if (element.Style.Variant is null) element.Style.Variant = ElementAppearance.Default;
    }
    public static void NormalizeGraph(SceneNodeGraph graph)
    {
        foreach (var element in graph.Elements) NormalizeElement(element);
    }
    public static void NormalizeDocument(SceneDocument document)
    {
        NormalizeGraph(document); if (document.Background is { } background) NormalizeElement(background);
        foreach (var definition in document.Definitions) NormalizeGraph(definition.Graph);
    }
}
