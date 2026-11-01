using System.Text.Json;
using FloatCanvas.Core;

internal static class ElementAppearanceTests
{
    internal static void Run(Action<bool, string> check)
    {
        var legacy = new CanvasElement { Kind = ElementKind.Number, Width = 60, Height = 60,
            Style = new() { Stroke = 0xFF112233, Fill = 0xFF446688, FontSize = 9 } };
        AppearanceMigration.MigrateElement(legacy, new(0xFF112233, 0xFF446688, false, null), false);
        string before = JsonSerializer.Serialize(legacy);
        check(ElementAppearance.PrimaryColor(legacy) == 0xFF446688 && ElementAppearance.SecondaryColor(legacy) == 0xFFFFFFFF, "legacy badge color roles match its rendered body and text");
        check(JsonSerializer.Serialize(legacy) == before, "reading legacy appearance does not migrate or dirty the element");
        ElementAppearance.SetPrimaryColor(legacy, 0xFF223344);
        check(legacy.Style.Variant == ElementAppearance.NumberFilled && legacy.Style.Stroke == 0xFF223344 && legacy.Style.Fill == 0xFFFFFFFF, "editing legacy primary preserves its white text");
        check(!legacy.NumberUsesForeground && legacy.Style.FontSize == 9, "editing legacy color preserves its original font sizing rule");
        ElementAppearance.SetSecondaryColor(legacy, 0xFF557799);
        ElementAppearance.SetVariant(legacy, ElementAppearance.NumberOutline);
        check(ElementAppearance.NumberTextColor(legacy) == legacy.Style.Stroke, "outline number uses primary for both ring and text");
        check(legacy.Style.Fill == 0xFF557799, "outline appearance retains the hidden secondary color");
        ElementAppearance.SetVariant(legacy, ElementAppearance.NumberFilled);
        check(ElementAppearance.NumberTextColor(legacy) == 0xFF557799, "switching back restores the secondary text color");
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 50,
            Style = new() { Stroke = 0xFFCC5533, Fill = 0, LinkColors = true, Variant = ElementAppearance.ShapeIndependent } };
        check(ElementAppearance.ShapeFill(shape.Style) == 0 && !ElementGeometry.HitTest(shape, new(50, 25), 0), "explicit independent style overrides the old linked bit in rendering geometry");
        ElementAppearance.SetVariant(shape, ElementAppearance.ShapeUniform);
        check(ElementAppearance.ShapeFill(shape.Style) == shape.Style.Stroke && ElementGeometry.HitTest(shape, new(50, 25), 0), "uniform style fills and picks the same interior");
        check(shape.Style.Fill == 0, "uniform style preserves the unused fill color");
        check(!ElementAppearance.IsValidVariant(ElementKind.Rectangle, ElementAppearance.NumberOutline) && !ElementAppearance.IsValidVariant(ElementKind.Number, "number.unknown"), "variants are validated against their element type");
        check(JsonSerializer.Deserialize<CanvasElement>(JsonSerializer.Serialize(shape))!.Style.Variant == ElementAppearance.ShapeUniform, "variant survives serialization and clone");
        check(shape.Clone().Style.Variant == shape.Style.Variant, "appearance is copied into independent element snapshots");
    }
}
