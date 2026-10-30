using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ToolNumericRailTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-tool-rails-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Domains(check);
            var session = new EditorSession();
            var controller = new EditorController(session);
            var panel = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(folder, "presets.json")), new ColorLibrary(Path.Combine(folder, "colors.json")));
            var fields = (Dictionary<string, NumericValueField>)typeof(ToolOptionsPanel).GetField("numericFields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
            var originalFields = fields.ToDictionary(pair => pair.Key, pair => pair.Value);
            int changes = 0; panel.ParametersChanged += () => changes++;
            foreach (var (tool, key, minimum, maximum) in new (EditorTool, string, double, double)[]
            {
                (EditorTool.Freehand, "StrokeWidth", 0, 32),
                (EditorTool.Highlighter, "StrokeWidth", 16, 64),
                (EditorTool.Line, "StrokeWidth", 0, 32),
                (EditorTool.CaptureRegion, "CaptureBorderWidth", 0, 16),
                (EditorTool.Text, "FontSize", 8, 144),
                (EditorTool.Number, "FontSize", 16, 256),
                (EditorTool.Emoji, "FontSize", 16, 256),
                (EditorTool.Text, "FontSize", 8, 144),
                (EditorTool.Number, "NumberStep", -10, 10),
                (EditorTool.Number, "NumberStart", 1, 100),
                (EditorTool.Number, "NextNumber", 1, 100),
                (EditorTool.Line, "ArrowHeadSize", 0, 64),
                (EditorTool.Rectangle, "CornerRadius", 0, 64),
                (EditorTool.Rectangle, "DefaultWidth", 1, 600),
                (EditorTool.Rectangle, "DefaultHeight", 1, 600),
                (EditorTool.Image, "Opacity", 0, 100),
                (EditorTool.Blur, "EffectStrength", .1, 64),
                (EditorTool.Pixelate, "EffectStrength", 1, 64),
                (EditorTool.Blur, "EffectStrength", .1, 64)
            })
            {
                controller.Tool = tool;
                string before = JsonSerializer.Serialize(controller.Parameters);
                panel.PrepareForShow();
                var field = fields[key];
                check($"{tool}/{key} uses its common slider range", field.Slider.Minimum == minimum && field.Slider.Maximum == maximum);
                check($"{tool}/{key} refresh retains its field without editing model or history", ReferenceEquals(field, originalFields[key]) && JsonSerializer.Serialize(controller.Parameters) == before && changes == 0 && session.UndoCount == 0);
            }
            foreach (var (tool, key, typed, read) in new (EditorTool, string, double, Func<ToolParameters, double>)[]
            {
                (EditorTool.Freehand, "StrokeWidth", 128, p => p.Style.StrokeWidth),
                (EditorTool.Highlighter, "StrokeWidth", 256, p => p.Style.StrokeWidth),
                (EditorTool.CaptureRegion, "CaptureBorderWidth", 128, p => p.CaptureBorderWidth),
                (EditorTool.Text, "FontSize", 512, p => p.Style.FontSize),
                (EditorTool.Number, "FontSize", 768, p => p.Style.FontSize),
                (EditorTool.Emoji, "FontSize", 768, p => p.Style.FontSize),
                (EditorTool.Number, "NumberStep", -123, p => p.NumberStep),
                (EditorTool.Line, "ArrowHeadSize", 256, p => p.Style.ArrowHeadSize),
                (EditorTool.Rectangle, "CornerRadius", 512, p => p.CornerRadius!.Value),
                (EditorTool.Rectangle, "DefaultWidth", 2048, p => p.DefaultWidth),
                (EditorTool.Blur, "EffectStrength", 256, p => p.EffectStrength),
                (EditorTool.Pixelate, "EffectStrength", 512, p => p.EffectStrength)
            })
            {
                controller.Tool = tool; panel.PrepareForShow();
                var field = fields[key];
                field.Input.Text = typed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                check($"{tool}/{key} still accepts manually typed values outside its rail", field.Value == typed && read(controller.Parameters) == typed);
                var scrubber = field.AnchorScrubber!;
                scrubber.TryBegin(0); scrubber.Update(60);
                check($"{tool}/{key} icon still previews beyond the common range", field.IsInteracting && field.Value > typed && read(controller.Parameters) == field.Value);
                scrubber.End(true);
            }
            check("tool numeric edits leave document undo unchanged", session.UndoCount == 0);
            var parameters = new ToolParameters { Tool = EditorTool.Number, NumberStart = -99999, NumberStep = 123,
                DefaultWidth = 2048, DefaultHeight = 1536, CornerRadius = 4096, EffectStrength = 512, CaptureBorderWidth = 512,
                Style = new() { StrokeWidth = 512, FontSize = 1024, ArrowHeadSize = 256, Opacity = .123456789 } };
            var library = new ToolPresetLibrary(Path.Combine(folder, "wide-presets.json"));
            library.Save("Values beyond common rails", parameters);
            var restored = new ToolPresetLibrary(Path.Combine(folder, "wide-presets.json")).User.Single().Parameters;
            check("saving and reopening presets preserves wide valid numeric values exactly", JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(parameters));
            check("tool rail fixture creates no native or visible window", PresentationSource.FromVisual(panel) is null && !panel.IsVisible);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Domains(Action<string, bool> check)
    {
        foreach (var (key, minimum, maximum) in new (string, double, double)[]
        {
            ("StrokeWidth", 0, 1024), ("CaptureBorderWidth", 0, 1024), ("Opacity", 0, 100),
            ("FontSize", double.Epsilon, 4096), ("NumberStart", -999999, 999999), ("NumberStep", -999999, 999999),
            ("NextNumber", int.MinValue, int.MaxValue), ("DefaultWidth", 1, 1000000), ("DefaultHeight", 1, 1000000),
            ("ArrowHeadSize", 0, 512), ("CornerRadius", 0, double.MaxValue), ("EffectStrength", double.Epsilon, 1024)
        })
        {
            var domain = NumericParameterDomain.ForTool(key);
            check(key + " retains its complete valid domain", domain.Minimum == minimum && domain.Maximum == maximum && domain.Contains(minimum) && domain.Contains(maximum));
        }
        foreach (var (key, tool) in new[] { ("StrokeWidth", EditorTool.Highlighter), ("FontSize", EditorTool.Number),
            ("FontSize", EditorTool.Emoji), ("EffectStrength", EditorTool.Pixelate) })
        {
            var domain = NumericParameterDomain.ForTool(key);
            var context = NumericParameterDomain.ForTool(key, tool);
            check($"{tool}/{key} context changes only the rail", context.Minimum == domain.Minimum && context.Maximum == domain.Maximum && context.Step == domain.Step && context.Integer == domain.Integer);
        }
    }
}
