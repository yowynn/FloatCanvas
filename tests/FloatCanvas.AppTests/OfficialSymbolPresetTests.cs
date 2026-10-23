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
        string original = JsonSerializer.Serialize(current);
        var official = library.OfficialFor(EditorTool.Symbol);
        check("official symbol recipes cover every symbol kind exactly once", official.Select(p => p.Parameters.Symbol).Order().SequenceEqual(Enum.GetValues<SymbolKind>().Order()));
        foreach (var preset in official)
        {
            var applied = library.Apply(preset.Id, current);
            check(preset.Id + " declares and applies a 72 by 72 default independent of current dimensions",
                preset.Parameters.DefaultWidth == 72 && preset.Parameters.DefaultHeight == 72 && applied.DefaultWidth == 72 && applied.DefaultHeight == 72);
            check(preset.Id + " preserves its symbol geometry and existing style merge contract",
                applied.Tool == EditorTool.Symbol && applied.Symbol == preset.Parameters.Symbol && !applied.ConstrainAspect &&
                applied.CornerRadius == current.CornerRadius && applied.EffectStrength == current.EffectStrength &&
                JsonSerializer.Serialize(applied.Style) == JsonSerializer.Serialize(current.Style));
        }
        check("applying official symbol recipes does not rewrite the current working parameters", JsonSerializer.Serialize(current) == original);
        var saved = library.Save("Custom symbol dimensions", current);
        var reloaded = new ToolPresetLibrary(path);
        check("personal symbol preset dimensions survive save reload and application", JsonSerializer.Serialize(reloaded.Apply(saved.Id, new ToolParameters())) == original);
        var emoji = library.Apply("official.emoji", current);
        check("official emoji applies the shared 72 size and preserves the glyph", emoji.Style.FontSize == 72 && emoji.Emoji == current.Emoji);
        var rectangle = library.Apply("official.rectangle", current);
        check("official rectangle uses its shared 108 side", rectangle.DefaultWidth == 144 && rectangle.DefaultHeight == 108);
    }
}
