using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ColorRobustnessTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-ColorRobustness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var field = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(root, "colors.json"))) { Value = 0xFF112233 };
            var picker = Field<ColorPickerPanel>(field, "picker");
            using (var surface = new ColorPickerTestSurface(picker))
            {
                typeof(ColorField).GetField("pickerSessionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(field, true);
                picker.Value = field.Value;
                Field<TextBox>(picker, "hex").Text = "#445566";
                int changes = 0; field.ValueChanged += _ => changes++;
                field.Value = 0xFF112233;
                field.ClosePicker();
                check("external undo synchronization cannot be overwritten by an old picker draft on close", field.Value == 0xFF112233 && changes == 0);
            }
            string blocker = Path.Combine(root, "blocked"); File.WriteAllText(blocker, "file, not directory");
            var failedHistory = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(blocker, "colors.json")));
            int notified = 0; failedHistory.ValueChanged += _ => notified++;
            Exception? failure = null;
            try { failedHistory.SetUserValue(0xFF778899); } catch (Exception ex) { failure = ex; }
            check("optional recent-color write failure does not block a basic color choice", failure is null && failedHistory.Value == 0xFF778899 && notified == 1);
            check("recent-color write failure remains available as local accessible feedback", !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(failedHistory)));
            var toolEditor = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
            var toolPanel = new ToolOptionsPanel(toolEditor, new ToolPresetLibrary(Path.Combine(root, "gesture-presets.json")), new ColorLibrary(Path.Combine(root, "gesture-colors.json")));
            toolPanel.PrepareForShow();
            var toolField = Field<ColorField>(toolPanel, "foreground");
            var toolPicker = Field<ColorPickerPanel>(toolField, "picker");
            using (var surface = new ColorPickerTestSurface(toolPicker))
            {
                typeof(ColorField).GetField("pickerSessionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(toolField, true);
                toolPicker.Value = toolField.Value;
                uint initial = toolEditor.Style.Stroke;
                toolPicker.BeginGesture(); Field<TextBox>(toolPicker, "hex").Text = "#446688";
                toolPicker.EndGesture(false);
                check("tool parameter color cancellation restores only its edited color", toolEditor.Style.Stroke == initial);
                toolPicker.BeginGesture(); Field<TextBox>(toolPicker, "hex").Text = "#446688";
                var external = toolEditor.Parameters; external.Style.StrokeWidth = 17;
                toolEditor.ApplyParameters(external); toolPanel.PrepareForShow(); toolPicker.EndGesture(false); toolField.ClosePicker();
                check("external same-color parameter replacement invalidates the old gesture without restoring an old tool snapshot", toolEditor.Style.Stroke == 0xFF446688 && toolEditor.Style.StrokeWidth == 17);
            }
            foreach (bool grid in new[] { false, true })
            {
                var session = new EditorSession();
                session.ExecuteSettings("previous", d => d.Canvas.BackgroundColor = 0xFF112233);
                var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
                try
                {
                    var edit = new CanvasColorEdit(() => session, () => true, () => { }, () => { });
                    window.BeginColorGesture = edit.Begin; window.ColorGestureCompleted += edit.Complete;
                    void Apply(Action<CanvasSettings> update) { if (!edit.Preview(update)) session.ExecuteSettings("color", d => update(d.Canvas)); }
                    window.BackgroundChanged += color => Apply(c => c.BackgroundColor = color);
                    window.GridColorChanged += color => Apply(c => c.Grid.Color = color);
                    window.Refresh(session.Document.Canvas); window.PrepareForShow();
                    var canvasField = Field<ColorField>(window, grid ? "gridColor" : "background");
                    var panel = Field<ColorPickerPanel>(canvasField, "picker");
                    using var surface = new ColorPickerTestSurface(panel);
                    typeof(ColorField).GetField("pickerSessionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvasField, true);
                    panel.Value = canvasField.Value;
                    uint initial = canvasField.Value;
                    var alpha = Field<NumericValueField>(panel, "alphaField");
                    check("canvas color gesture starts a settings transaction", alpha.BeginGesture() && session.IsTransactionActive);
                    alpha.Slider.Value = 70; alpha.Slider.Value = 40;
                    alpha.EndGesture(false);
                    check("cancelled canvas color gesture restores color and leaves previous history intact", canvasField.Value == initial && session.UndoCount == 1 && !session.IsTransactionActive);
                    check("undo after cancelled canvas color still targets the prior valid edit", session.Undo() && session.Document.Canvas.BackgroundColor == 0);
                    session.Redo(); panel.Value = initial;
                    alpha.BeginGesture();
                    for (int i = 1; i <= 100; i++) alpha.Slider.Value = i;
                    alpha.Slider.Value = 25; alpha.EndGesture(true);
                    check("hundred color previews commit exactly one settings history entry", session.UndoCount == 2 && !session.IsTransactionActive);
                    canvasField.ClosePicker();
                    check("closing a committed color does not add history", session.UndoCount == 2);
                    typeof(ColorField).GetField("pickerSessionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvasField, true);
                    panel.Value = canvasField.Value;
                    session.Undo(); window.Refresh(session.Document.Canvas); window.PrepareForShow();
                    uint restored = grid ? session.Document.Canvas.Grid.Color : session.Document.Canvas.BackgroundColor;
                    canvasField.ClosePicker();
                    check("canvas undo then picker close preserves the restored model and redo branch", canvasField.Value == restored && session.UndoCount == 1 && session.RedoCount == 1);
                    session.Redo(); window.Refresh(session.Document.Canvas); window.PrepareForShow();
                    typeof(ColorField).GetField("pickerSessionOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvasField, true);
                    panel.Value = canvasField.Value;
                    alpha.BeginGesture(); alpha.Slider.Value = 60;
                    panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    check("unloading a color picker cancels its live transaction without a hidden undo entry", session.UndoCount == 2 && !session.IsTransactionActive);
                }
                finally { window.CloseForDispose(); }
            }
            foreach (var (tool, update) in new (EditorTool, Action<ToolParameters>)[]
            {
                (EditorTool.Text, p => p.Style.FontSize = .5),
                (EditorTool.Rectangle, p => p.CornerRadius = 10000001),
                (EditorTool.Blur, p => p.EffectStrength = .001)
            })
            {
                var parameters = new ToolParameters { Tool = tool }; update(parameters);
                string path = Path.Combine(root, tool + ".json");
                var library = new ToolPresetLibrary(path); library.Save("compatible", parameters);
                var reloaded = new ToolPresetLibrary(path).User.Single(p => p.Name == "compatible").Parameters;
                var editor = new EditorController(new EditorSession()); editor.ApplyParameters(reloaded);
                var panel = new ToolOptionsPanel(editor, library, new ColorLibrary(Path.Combine(root, tool + "-colors.json")));
                failure = null; try { panel.PrepareForShow(); } catch (Exception ex) { failure = ex; }
                check(tool + " accepted saved parameters display without loss or exception", failure is null && editor.Parameters.IsValid());
                string key = tool == EditorTool.Text ? "FontSize" : tool == EditorTool.Rectangle ? "CornerRadius" : "EffectStrength";
                var displayed = Field<Dictionary<string, NumericValueField>>(panel, "numericFields")[key];
                double expected = tool == EditorTool.Text ? .5 : tool == EditorTool.Rectangle ? 10000001 : .001;
                check(tool + " accepted saved value survives display and read without clamping", displayed.TryGetValue(out double actual) && actual == expected);
            }
            var compatible = new NumericValueField(1, 4096, 8, 144);
            compatible.SetModelValue(.00012345678912345);
            check("numeric model synchronization retains exact compatible values outside the new-edit and rail domains", compatible.TryGetValue(out double exact) && exact == .00012345678912345);
            compatible.BeginGesture(); compatible.Slider.Value = 25; compatible.EndGesture(false);
            check("cancelling a gesture returns to its exact compatible starting value", compatible.Value == .00012345678912345 && compatible.TryGetValue(out exact) && exact == compatible.Value);
            var complete = new ToolParameters { Tool = EditorTool.Rectangle, CornerRadius = double.MaxValue,
                DefaultWidth = 1000000, DefaultHeight = 1, EffectStrength = double.Epsilon, NumberStart = -999999, NumberStep = 999999,
                CaptureBorderWidth = 1024, Style = new() { StrokeWidth = 1024, FontSize = double.Epsilon, Opacity = .12345678912345, ArrowHeadSize = 512 } };
            var completeLibrary = new ToolPresetLibrary(Path.Combine(root, "complete.json"));
            string completeId = completeLibrary.Save("all domains", complete).Id;
            var completeReload = new ToolPresetLibrary(Path.Combine(root, "complete.json")).Apply(completeId);
            var completeEditor = new EditorController(new EditorSession());
            var completePanel = new ToolOptionsPanel(completeEditor, completeLibrary, new ColorLibrary(Path.Combine(root, "complete-colors.json")));
            foreach (var (tool, key, expected) in new[]
            {
                (EditorTool.Rectangle, "CornerRadius", double.MaxValue), (EditorTool.Rectangle, "DefaultWidth", 1000000d), (EditorTool.Rectangle, "DefaultHeight", 1d),
                (EditorTool.Freehand, "StrokeWidth", 1024d), (EditorTool.Text, "FontSize", double.Epsilon),
                (EditorTool.Number, "NumberStart", -999999d), (EditorTool.Number, "NumberStep", 999999d),
                (EditorTool.Image, "Opacity", complete.Style.Opacity * 100), (EditorTool.Line, "ArrowHeadSize", 512d),
                (EditorTool.Blur, "EffectStrength", double.Epsilon), (EditorTool.CaptureRegion, "CaptureBorderWidth", 1024d)
            })
            {
                var selection = completeReload.Clone(); selection.Tool = tool; completeEditor.ApplyParameters(selection); completePanel.PrepareForShow();
                var numeric = Field<Dictionary<string, NumericValueField>>(completePanel, "numericFields")[key];
                check(key + " persisted boundary survives the actual tool display and text read", numeric.Value == expected && numeric.TryGetValue(out double read) && read == expected);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
