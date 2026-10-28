using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class SymbolStylePresetTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-symbol-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var library = new ToolPresetLibrary(Path.Combine(folder, "presets.json"));
            var official = library.OfficialFor(EditorTool.Symbol);
            check("symbol presets offer five styles on one standard geometry rather than a recipe per shape", official.Count == 5 &&
                official.All(p => p.Parameters.Symbol == SymbolKind.Triangle) && official.First().Id == "official.triangle");
            check("the five symbol styles have distinct paint combinations", official.Select(p => JsonSerializer.Serialize(p.Parameters.Style)).Distinct().Count() == 5);
            foreach (var preset in official)
            {
                var session = new EditorSession();
                var editor = new EditorController(session); editor.ApplyParameters(preset.Parameters);
                var panel = new ToolOptionsPanel(editor, library, new ColorLibrary(Path.Combine(folder, "colors.json")));
                panel.PrepareForShow();
                var gallery = (UniformGrid)typeof(ToolOptionsPanel).GetField("symbolGallery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                int index = 0;
                foreach (var symbol in Enum.GetValues<SymbolKind>())
                {
                    var expected = preset.Parameters.Clone(); expected.Symbol = symbol;
                    gallery.Children.OfType<Button>().Single(button => Equals(button.Tag, symbol)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(preset.Id + " switches to " + symbol + " with one shape click while retaining the entire style", JsonSerializer.Serialize(editor.Parameters) == JsonSerializer.Serialize(expected));
                    double x = 90 + index++ * 180;
                    editor.Pointer(new(OverlayPointerKind.Down, new(x, 100), OverlayMouseButton.Left, OverlayModifiers.Control));
                    editor.Pointer(new(OverlayPointerKind.Up, new(x, 100), OverlayMouseButton.Left, OverlayModifiers.Control));
                    var inserted = session.Document.Elements.Last();
                    check(preset.Id + " draws an editable " + symbol + " using its chosen style", inserted.Kind == ElementKind.Symbol && inserted.Symbol == symbol &&
                        JsonSerializer.Serialize(inserted.Style) == JsonSerializer.Serialize(expected.Style));
                }
            }
            var personal = new ToolParameters { Tool = EditorTool.Symbol, Symbol = SymbolKind.Heart, Style = new() { Stroke = 0xFF123456, Fill = 0xAB654321 } };
            var saved = library.Save("Personal shape", personal);
            string bytes = File.ReadAllText(Path.Combine(folder, "presets.json"));
            var reloaded = new ToolPresetLibrary(Path.Combine(folder, "presets.json")); _ = reloaded.Official;
            check("simplifying official styles preserves saved personal geometry and paint without rewriting its file", JsonSerializer.Serialize(reloaded.Apply(saved.Id)) == JsonSerializer.Serialize(personal) &&
                File.ReadAllText(Path.Combine(folder, "presets.json")) == bytes);
        }
        finally { Directory.Delete(folder, true); }
    }
}
