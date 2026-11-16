using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using FloatCanvas.Rendering;

internal static class ToolPresetAuditTests
{
    internal static void Run(Action<string, bool> check, string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var library = new ToolPresetLibrary(Path.Combine(output, "isolated-presets.json"));
        var controller = new EditorController(new EditorSession());
        var panel = new ToolOptionsPanel(controller, library, new ColorLibrary(Path.Combine(output, "isolated-colors.json")));
        var rows = (Dictionary<string, FrameworkElement>)typeof(ToolOptionsPanel).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        var fields = (Dictionary<string, NumericValueField>)typeof(ToolOptionsPanel).GetField("numericFields", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        var catalog = new List<string> { "# Official preset inventory", "", "| ID | Name | Tool | Width | Height | Locked | Stroke | Pattern | Foreground | Background |", "|---|---|---|---:|---:|---|---:|---|---|---|" };
        foreach (var preset in library.Official)
        {
            var p = library.Apply(preset.Id);
            check(preset.Id + " is valid with layer opacity excluded", p.IsValid() && p.Style.Opacity == 1);
            if (p.Tool == EditorTool.Symbol) check(preset.Id + " locks its natural shape", p.ConstrainAspect);
            if (p.Tool is EditorTool.Rectangle or EditorTool.Ellipse) check(preset.Id + " starts square but allows free dragging", !p.ConstrainAspect && p.DefaultWidth == p.DefaultHeight);
            if (p.Tool == EditorTool.Crop)
            {
                var cropTarget = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 60 };
                controller.Session.Document.Elements.Add(cropTarget); controller.Session.SetSelection([cropTarget.Id]);
            }
            controller.ApplyParameters(p); panel.PrepareForShow();
            check(preset.Id + " applies its intended tool with valid context", controller.Tool == p.Tool);
            check(preset.Id + " hides tool-level opacity", rows["Opacity"].Visibility == Visibility.Collapsed);
            if (p.Tool == EditorTool.Highlighter)
            {
                var field = fields["StrokeWidth"]; field.Input.Text = "16"; Send(field.Slider, Key.Left);
                check("highlighter keyboard cannot cross the creation minimum", field.Value == 16 && controller.Parameters.Style.StrokeWidth == 16);
            }
            if (p.Tool is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Symbol)
            {
                var drawingSession = new EditorSession(); var drawingController = new EditorController(drawingSession); drawingController.ApplyParameters(p);
                drawingController.Pointer(new(OverlayPointerKind.Down, new(20, 20), OverlayMouseButton.Left, OverlayModifiers.None));
                drawingController.Pointer(new(OverlayPointerKind.Move, new(150, 90), OverlayMouseButton.Left, OverlayModifiers.None));
                drawingController.Pointer(new(OverlayPointerKind.Up, new(150, 90), OverlayMouseButton.Left, OverlayModifiers.None));
                check(preset.Id + " creates artwork with full layer opacity", drawingSession.Document.Elements.Count == 1 && drawingSession.Document.Elements[0].Style.Opacity == 1);
                var element = drawingSession.Document.Elements[0];
                if (p.Tool == EditorTool.Symbol) check(preset.Id + " preserves natural proportions during non-square drag", Math.Abs(element.Width - element.Height) < .001);
                if (p.Tool is EditorTool.Rectangle or EditorTool.Ellipse) check(preset.Id + " permits non-square drag", Math.Abs(element.Width - element.Height) > 1);
                drawingSession.Document.Viewport = new(0, 0, 200, 180);
                using var renderer = new SceneRenderer();
                File.WriteAllBytes(Path.Combine(output, preset.Id + "-drawing.png"), renderer.ExportPng(drawingSession.Document, drawingSession.Assets, true));
            }
            catalog.Add($"| {preset.Id} | {preset.Name} | {p.Tool} | {p.DefaultWidth} | {p.DefaultHeight} | {p.ConstrainAspect} | {p.Style.StrokeWidth} | {p.Style.StrokePattern} | {p.Style.Foreground:X8} | {p.Style.Background:X8} |");
        }
        var canvasPresets = CanvasPresetLibrary.OfficialOnly().Official;
        foreach (var preset in canvasPresets) check(preset.Id + " is a valid distinct canvas recipe", preset.Parameters.IsValid);
        File.WriteAllText(Path.Combine(output, "canvas-presets.json"), JsonSerializer.Serialize(canvasPresets, new JsonSerializerOptions { WriteIndented = true }));
        DrawingContexts(output);
        File.WriteAllLines(Path.Combine(output, "official-presets.md"), catalog);
        File.WriteAllText(Path.Combine(output, "official-presets.json"), JsonSerializer.Serialize(library.Official, new JsonSerializerOptions { WriteIndented = true }));
        foreach (string id in new[] { "square", "circle", "image-half", "image-faint", "symbol-green", "text-heading", "blur-fine", "pixelate-fine", "highlighter-green", "number-blue" })
            check("retired ID remains applicable: " + id, library.Apply("official." + id).IsValid());
        var old = new ToolParameters { Tool = EditorTool.Emoji, Style = new() { Opacity = .25, Foreground = 0x80112233, Background = 0x40445566 } };
        string legacyPath = Path.Combine(output, "legacy-presets.json");
        string legacy = JsonSerializer.Serialize(new { Version = 2, Presets = new[] { new ToolPreset { Id = "user-legacy", Name = "Legacy", Parameters = old } } });
        File.WriteAllText(legacyPath, legacy);
        var older = new ToolPresetLibrary(legacyPath); var applied = older.Apply("user-legacy");
        controller.ApplyParameters(applied);
        check("legacy preset normalization is read-only and retains color alpha", File.ReadAllText(legacyPath) == legacy && applied.Style.Opacity == 1 && applied.Style.Foreground == old.Style.Foreground && old.Style.Opacity == .25);
        var saved = library.Save("Layer opacity excluded", old);
        check("newly saved presets exclude layer opacity without mutating caller", saved.Parameters.Style.Opacity == 1 && old.Style.Opacity == .25);
        var layer = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20, Style = new() { Opacity = .25 } };
        controller.Session.Document.Elements.Add(layer); controller.ApplyParameters(new ToolParameters { Tool = EditorTool.Rectangle, Style = new() { Opacity = .4 } });
        check("tool normalization leaves authored layer opacity untouched", layer.Style.Opacity == .25 && controller.Parameters.Style.Opacity == 1);
        foreach (var (key, tool, expected) in new (string, EditorTool?, double)[] { ("StrokeWidth", EditorTool.Freehand, 1), ("EffectStrength", EditorTool.Pixelate, 1), ("EffectStrength", EditorTool.Blur, .1), ("TrailWidthPixels", null, .1), ("LeftClick.StrokeWidthPixels", null, .1), ("KeyHoldMilliseconds", null, 10), ("TrailMilliseconds", null, 10), ("FontSize", null, 1) })
        {
            var numeric = new NumericValueField(0, 5000, 0, 64, step: .25) { Value = 100.5, KeyboardStep = NumericKeyboardSteps.ForProperty(key, tool) };
            Send(numeric.Slider, Key.Right); check(key + " increments the real value beyond the rail", Math.Abs(numeric.Value - 100.5 - expected) < .0001);
            Send(numeric.Slider, Key.Left); check(key + " reverses its precise step", numeric.Value == 100.5);
            check(key + " does not alter pointer sensitivity", numeric.Slider.SmallChange == .25);
            numeric.Value = 5000; Send(numeric.Slider, Key.Right); check(key + " respects hard maximum", numeric.Value == 5000);
            numeric.Input.IsReadOnly = true; Send(numeric.Slider, Key.Left); check(key + " respects read-only", numeric.Value == 5000);
        }
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            var sheet = new StackPanel { Background = (Brush)Application.Current!.Resources["CanvasSurface"] };
            foreach (var group in library.Official.GroupBy(p => ToolPresetLibrary.Family(p.Parameters.Tool)))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
                row.Children.Add(new TextBlock { Text = group.Key.ToString(), Width = 135, Foreground = (Brush)Application.Current!.Resources["CanvasText"] });
                foreach (var preset in group)
                {
                    var card = new StackPanel { Width = 100 };
                    card.Children.Add(ToolPresetGallery.CreatePreview(preset.Parameters));
                    card.Children.Add(new TextBlock { Text = preset.Name, TextWrapping = TextWrapping.Wrap, FontSize = 11, TextAlignment = System.Windows.TextAlignment.Center, Foreground = (Brush)Application.Current!.Resources["CanvasText"] }); row.Children.Add(card);
                }
                sheet.Children.Add(row);
            }
            sheet.Measure(new Size(760, double.PositiveInfinity)); sheet.Arrange(new Rect(new Point(), sheet.DesiredSize)); sheet.UpdateLayout();
            var bitmap = new RenderTargetBitmap(760, (int)Math.Ceiling(sheet.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(sheet);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, "official-" + theme + ".png")); encoder.Save(file);
        }
    }
    private static void DrawingContexts(string output)
    {
        var sheet = new StackPanel { Background = Brushes.White };
        var pattern = new DrawingGroup();
        using (var draw = pattern.Open())
        {
            draw.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(0, 0, 20, 20));
            draw.DrawRectangle(Brushes.Wheat, null, new Rect(0, 0, 10, 10));
            draw.DrawLine(new Pen(Brushes.SlateGray, 2), new Point(0, 20), new Point(20, 0));
        }
        var busy = new DrawingBrush(pattern) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 20, 20) };
        foreach (string file in Directory.GetFiles(output, "*-drawing.png").Order())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(file).Replace("official.", "").Replace("-drawing", ""), Width = 125, FontSize = 11 });
            foreach (Brush background in new Brush[] { Brushes.White, new SolidColorBrush(Color.FromRgb(32, 36, 44)), busy })
                row.Children.Add(new Border { Width = 160, Height = 100, Background = background,
                    Child = new Image { Source = new BitmapImage(new Uri(file)), Stretch = Stretch.Uniform } });
            sheet.Children.Add(row);
        }
        sheet.Measure(new Size(610, double.PositiveInfinity)); sheet.Arrange(new Rect(new Point(), sheet.DesiredSize)); sheet.UpdateLayout();
        var bitmap = new RenderTargetBitmap(610, (int)Math.Ceiling(sheet.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(sheet);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, "drawing-contexts.png")); encoder.Save(stream);
    }
    private static void Send(UIElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new Source(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private sealed class Source : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
