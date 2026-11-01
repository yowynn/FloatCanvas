using FloatCanvas.Core;
using System.Text.Json;

internal static class CanonicalAppearanceTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var kind in Enum.GetValues<ElementKind>())
        {
            var variants = AppearanceRegistry.VariantsFor(kind);
            check(variants.Count > 0 && variants.Select(item => item.Id).Distinct().Count() == variants.Count,
                $"{kind}: stable nonduplicated appearance registry");
            foreach (var variant in variants)
            {
                var element = new CanvasElement { Kind = kind, Style = new() { Foreground = 0x00112233, Background = 0x7F445566,
                    Variant = variant.Id, AdditionalColors = new() { ["accent.1"] = 0x00667788 } } };
                check(AppearanceRegistry.Resolve(element) == variant && JsonSerializer.Deserialize<CanvasElement>(JsonSerializer.Serialize(element))!.Style.Foreground == 0x00112233,
                    $"{kind}/{variant.Id}: registry and canonical wire retain transparent RGB");
            }
        }
        var badge = new CanvasElement { Kind = ElementKind.Number, NumberTextSizeMode = NumberTextSizeMode.CapFontSize,
            Style = new() { Foreground = 0xFF112233, Background = 0xFF445566, FontSize = 11 } };
        check(AppearanceRegistry.VariantsFor(ElementKind.Number).Count == 3, "number registry exposes three independent stable variants");
        ElementAppearance.SetVariant(badge, ElementAppearance.NumberOutlineIndependent);
        check(ElementAppearance.IsOutlineNumber(badge) && ElementAppearance.NumberTextColor(badge) == badge.Style.Background,
            "third number style keeps independent text color without body fill");
        ElementAppearance.SetVariant(badge, ElementAppearance.NumberOutline);
        check(ElementAppearance.NumberTextColor(badge) == badge.Style.Foreground && badge.Style.Background == 0xFF445566 && badge.Style.FontSize == 11 &&
            badge.NumberTextSizeMode == NumberTextSizeMode.CapFontSize, "style switching preserves hidden secondary color and original font sizing");
        badge.Style.AdditionalColors["accent.2"] = 7; badge.Style.AdditionalColors["accent.1"] = 9;
        var clone = badge.Clone(); clone.Style.AdditionalColors["accent.1"] = 42;
        check(badge.Style.AdditionalColors["accent.1"] == 9, "element clone owns its extra role dictionary");
        string json = JsonSerializer.Serialize(badge);
        check(json.IndexOf("accent.1", StringComparison.Ordinal) < json.IndexOf("accent.2", StringComparison.Ordinal) &&
            !json.Contains("\"Stroke\"") && !json.Contains("\"Fill\"") && !json.Contains("\"LinkColors\"") && !json.Contains("\"NumberUsesForeground\""),
            "canonical wire is deterministically ordered and contains no legacy aliases");
        foreach (bool oldSizing in new[] { false, true })
        foreach (uint fill in new uint[] { 0x00123456, 0x7F445566 })
        {
            string raw = $$"""{"Kind":10,"Style":{"Stroke":4279312947,"Fill":{{fill}},"FontSize":13},"NumberUsesForeground":{{oldSizing.ToString().ToLowerInvariant()}}} """;
            var old = JsonSerializer.Deserialize<CanvasElement>(raw)!;
            check(old.Style.Foreground == (oldSizing || fill >> 24 == 0 ? 0xFF112233 : fill) &&
                old.Style.Background == (oldSizing ? fill : 0xFFFFFFFF) && old.Style.FontSize == 13 &&
                old.NumberTextSizeMode == (oldSizing ? NumberTextSizeMode.FitBadge : NumberTextSizeMode.CapFontSize), "legacy number independently migrates effective colors and typography");
            check(oldSizing || old.Style.AdditionalColors["legacy.fill"] == fill && old.Style.AdditionalColors["legacy.stroke"] == 0xFF112233,
                "legacy unused raw badge colors survive as named inactive roles");
        }
        var explicitOld = JsonSerializer.Deserialize<CanvasElement>("""{"Kind":10,"Style":{"Stroke":4279312947,"Fill":4282668390,"Variant":"number.outline"},"NumberUsesForeground":false}""")!;
        check(explicitOld.Style.Foreground == 0xFF112233 && explicitOld.Style.Background == 0xFF445566 && explicitOld.NumberTextSizeMode == NumberTextSizeMode.CapFontSize,
            "explicit old variant overrides historical color convention without changing old text size");
        var sparse = JsonSerializer.Deserialize<CanvasElement>("""{"Kind":10}""")!;
        check(sparse.Style.Foreground == 0xFFEF4444 && sparse.Style.Background == 0xFFFFFFFF && sparse.Style.Variant == ElementAppearance.NumberFilled &&
            sparse.NumberTextSizeMode == NumberTextSizeMode.CapFontSize, "sparse legacy number without Style retains its original red badge and white text defaults");
        foreach (bool uniform in new[] { false, true })
        {
            string raw = JsonSerializer.Serialize(new { Kind = 5, Style = new { Stroke = 0xFF112233u, Fill = 0xFF445566u,
                LinkColors = !uniform, Variant = uniform ? ElementAppearance.ShapeUniform : ElementAppearance.ShapeIndependent } });
            var old = JsonSerializer.Deserialize<CanvasElement>(raw)!;
            check(ElementAppearance.ShapeFill(old.Style) == (uniform ? 0xFF112233 : 0xFF445566) && old.Style.Background == 0xFF445566 && old.Style.LinkColors == uniform,
                "explicit legacy shape variant wins contradictory raw linked bit while preserving the hidden secondary color");
        }
        var historyElement = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 30,
            Style = new() { AdditionalColors = new() { ["accent.2"] = 2, ["accent.1"] = 1 } } };
        var session = new EditorSession(new() { Elements = [historyElement] }); session.SetSelection([historyElement.Id]);
        session.UpdateSelection(element => element.Style.AdditionalColors = element.Style.AdditionalColors.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value));
        check(session.UndoCount == 0, "changing only extra-role dictionary insertion order creates no false history entry");
        session.UpdateSelection(element => element.Style.AdditionalColors["accent.1"] = 8);
        check(session.UndoCount == 1 && session.Undo() && session.Document.Elements[0].Style.AdditionalColors["accent.1"] == 1 &&
            session.Redo() && session.Document.Elements[0].Style.AdditionalColors["accent.1"] == 8,
            "extra-color edits are deep-owned and undo/redo retain the canonical palette");
        foreach (string invalid in new[] { "", "Foreground", "foreground", "background", "bad space", "accent_1", new string('a', 33) })
            check(!AppearanceRegistry.IsValidColorRoleId(invalid), "invalid extra role rejected: " + invalid);
        var scene = new SceneDocument { Elements = [badge] };
        scene.Elements[0].Style.AdditionalColors = Enumerable.Range(0, 17).ToDictionary(i => "accent." + i, i => (uint)i);
        bool rejected = false; try { SceneLimits.ValidateCapacity(scene); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "source graph capacity rejects unbounded extra color roles");
    }
}
