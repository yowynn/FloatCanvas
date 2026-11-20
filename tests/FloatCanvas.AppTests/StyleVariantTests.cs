using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class StyleVariantTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                L.Initialize(locale); AppTheme.Apply("light");
                var colors = new ColorLibrary(Path.Combine(directory, locale + "-colors.json"));
                var editor = new EditorController(new());
                editor.ApplyParameters(new() { Tool = EditorTool.Number, Style = new() { Variant = ElementAppearance.NumberOutline, Stroke = 0xFF245BC3, Fill = 0xFFFFCC33 } });
                var panel = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(directory, locale + "-presets.json")), colors);
                panel.PrepareForShow();
                var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
                var choices = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers");
                check(locale + " outline number exposes its style and border width with its secondary fill", rows["StyleVariant"].Visibility == Visibility.Visible && rows["StrokeWidth"].Visibility == Visibility.Visible && rows["Background"].Visibility == Visibility.Visible);
                choices["StyleVariant"].SelectedValue = ElementAppearance.NumberFilled; panel.PrepareForShow();
                check(locale + " choosing filled restores the secondary color control without losing its value", editor.Parameters.Style.Variant == ElementAppearance.NumberFilled && editor.Parameters.Style.Fill == 0xFFFFCC33 && rows["Background"].Visibility == Visibility.Visible);
                var first = new CanvasElement { Kind = ElementKind.Number, Width = 60, Height = 60, Style = new() { FontSize = 8 } };
                AppearanceMigration.MigrateElement(first, new(0xFFEF4444, 0xFF336699, false, null), false);
                var second = first.Clone(true); second.Transform = Affine2D.Translation(100, 0);
                var session = new EditorSession(new() { Elements = [first, second] }); session.SetSelection([first.Id, second.Id]);
                string original = JsonSerializer.Serialize(session.Document);
                var inspector = new InspectorWindow(() => session, colorLibrary: colors);
                using var surface = new InspectorTestSurface(inspector);
                Field<SegmentedChoice>(inspector, "appearanceStyle").SelectedValue = ElementAppearance.NumberOutline;
                Invoke(inspector, "Apply");
                check(locale + " multi-selection applies the stable variant to each badge in one undo operation", session.UndoCount == 1 && session.Document.Elements.All(e => e.Style.Variant == ElementAppearance.NumberOutline));
                check(locale + " switching legacy badges keeps their small font and old sizing rule", session.Document.Elements.All(e => !e.NumberUsesForeground && e.Style.FontSize == 8 && e.Style.Stroke == 0xFF336699 && e.Style.Fill == 0xFFFFFFFF));
                check(locale + " undo restores the exact loaded legacy badge state", session.Undo() && JsonSerializer.Serialize(session.Document) == original);
                check(locale + " redo restores editable outline badges", session.Redo() && session.Document.Elements.All(ElementAppearance.IsOutlineNumber));
                var preset = new ToolPresetLibrary(Path.Combine(directory, locale + "-personal.json"));
                var saved = preset.Save("Outline", new() { Tool = EditorTool.Number, Style = new() { Variant = ElementAppearance.NumberOutline, Fill = 0xFFABCDEF } });
                var loaded = new ToolPresetLibrary(Path.Combine(directory, locale + "-personal.json")).Apply(saved.Id);
                check(locale + " personal recipes preserve stable style and hidden secondary color", loaded.Style.Variant == ElementAppearance.NumberOutline && loaded.Style.Fill == 0xFFABCDEF);
                foreach (var target in new[] { EditorTool.Select, EditorTool.Rectangle, EditorTool.Triangle, EditorTool.Emoji })
                {
                    editor.ApplyParameters(new() { Tool = EditorTool.Number, Style = new() { Variant = ElementAppearance.NumberOutline } });
                    editor.Tool = target;
                    check(locale + " switching from number to " + target + " leaves valid target parameters", editor.Parameters.IsValid() &&
                        editor.Parameters.Style.Variant == (target is EditorTool.Rectangle or EditorTool.Triangle ? ElementAppearance.ShapeIndependent : ElementAppearance.Default));
                }
                editor.ApplyParameters(new() { Tool = EditorTool.Rectangle, Style = new() { Variant = ElementAppearance.ShapeUniform } });
                editor.Tool = EditorTool.Ellipse;
                check(locale + " shape family changes retain their compatible appearance", editor.Parameters.IsValid() && editor.Style.Variant == ElementAppearance.ShapeUniform);
                editor.ApplyParameters(new() { Tool = EditorTool.Number, Style = new() { Variant = ElementAppearance.NumberOutline, Stroke = 0xFF123456, Fill = 0xFFABCDEF } });
                check(locale + " explicit variants take precedence over the old preset color-version flag", editor.Style.Stroke == 0xFF123456 && editor.Style.Fill == 0xFFABCDEF);
                foreach (var shape in new[] { EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Symbol })
                {
                    editor.ApplyParameters(new() { Tool = shape, Style = new() { Variant = ElementAppearance.ShapeUniform,
                        PrimaryColor = 0x80663322, SecondaryColor = 0x40112233, StrokePattern = StrokePattern.Dashed } });
                    panel.PrepareForShow();
                    check(locale + " solid " + shape + " shows only fill color", rows["Foreground"].Visibility == Visibility.Visible &&
                        new[] { "Background", "StrokeWidth", "StrokePattern" }.All(key => rows[key].Visibility == Visibility.Collapsed));
                    Field<SegmentedChoice>(panel, "linkColors").SelectedValue = ElementAppearance.ShapeIndependent;
                    panel.PrepareForShow();
                    panel.Measure(new Size(700, 750)); panel.Arrange(new Rect(0, 0, 700, 750)); panel.UpdateLayout();
                    string[] keys = shape is EditorTool.Rectangle or EditorTool.RoundedRectangle
                        ? ["Foreground", "Background", "StrokeWidth", "StrokePattern", "CornerRadius"] : ["Foreground", "Background", "StrokeWidth", "StrokePattern"];
                    var tops = keys.Select(key => rows[key].TranslatePoint(default, panel).Y).ToArray();
                    check(locale + " outlined " + shape + " groups colors, width and pattern before radius", keys.All(key => rows[key].Visibility == Visibility.Visible)
                        && tops.Zip(tops.Skip(1)).All(pair => pair.First < pair.Second) && editor.Style.SecondaryColor == 0x40112233);
                }
                editor.ApplyParameters(new() { Tool = EditorTool.Number, Style = new() { Variant = ElementAppearance.NumberOutline,
                    PrimaryColor = 0xFF123456, SecondaryColor = 0xFFABCDEF } });
                string statePath = Path.Combine(directory, locale + "-tool-state.json");
                var state = new ToolStateStore(statePath); state.Tools[EditorTool.Number] = editor.Parameters; state.Save();
                var restored = new ToolStateStore(statePath).Tools[EditorTool.Number];
                check(locale + " working tool state retains its outline style and both colors", restored.Style.Variant == ElementAppearance.NumberOutline && restored.Style.Stroke == 0xFF123456 && restored.Style.Fill == 0xFFABCDEF);
            }
        }
        finally { L.Initialize(language); Directory.Delete(directory, true); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
}
