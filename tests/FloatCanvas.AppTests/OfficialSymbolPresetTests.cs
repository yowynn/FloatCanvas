using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class OfficialSymbolPresetTests
{
    internal static void Run(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "symbol-dimensions.json");
        var library = new ToolPresetLibrary(path);
        var current = new ToolParameters
        {
            Tool = EditorTool.Symbol, Symbol = SymbolKind.Heart, DefaultWidth = 234, DefaultHeight = 123,
            ConstrainAspect = true, CornerRadius = 17, EffectStrength = 42,
            Style = new() { Stroke = 0xAB123456, Fill = 0x80456789, StrokeWidth = 7, Opacity = .6 }
        };
        current.NormalizeAppearance();
        string original = JsonSerializer.Serialize(current);
        var official = library.OfficialFor(EditorTool.Symbol);
        check("official symbol recipes focus on four reusable styles with a shared standard shape", official.Count == 4 && official.All(p => p.Parameters.Symbol == SymbolKind.Triangle));
        foreach (var preset in official)
        {
            var applied = library.Apply(preset.Id);
            check(preset.Id + " applies a 72 px long edge with the triangle's natural proportions",
                preset.Parameters.SymbolSize == 72 && applied.DefaultWidth == 72 && Math.Abs(applied.DefaultHeight - 36 * Math.Sqrt(3)) < 1e-8);
            check(preset.Id + " restores complete symbol geometry and fixed style",
                applied.Tool == EditorTool.Symbol && applied.Symbol == preset.Parameters.Symbol && applied.ConstrainAspect &&
                applied.CornerRadius == preset.Parameters.CornerRadius && applied.EffectStrength == preset.Parameters.EffectStrength &&
                JsonSerializer.Serialize(applied.Style) == JsonSerializer.Serialize(preset.Parameters.Style));
        }
        check("applying official symbol recipes does not rewrite the current working parameters", JsonSerializer.Serialize(current) == original);
        var saved = library.Save("Custom symbol dimensions", current);
        var reloaded = new ToolPresetLibrary(path);
        check("personal symbol preset dimensions survive save reload and application", JsonSerializer.Serialize(reloaded.Apply(saved.Id)) == JsonSerializer.Serialize(saved.Parameters));
        current.Emoji = "🚀";
        var emoji = library.Apply("official.emoji");
        check("official emoji applies its fixed 72 size and default glyph", emoji.Style.FontSize == 72 && emoji.Emoji == new ToolParameters().Emoji);
        var rectangle = library.Apply("official.rectangle");
        check("official rectangle uses its shared 108 side", rectangle.DefaultWidth == 108 && rectangle.DefaultHeight == 108);
    }
}
