using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

/// <summary>Capture creation ignores persisted aspect locks; Shift is a transient constraint.</summary>
internal static class CaptureAspectPolicyTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-capture-aspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Gestures(check);
            LegacyState(folder, check);
            Configuration(folder, check);
            SnapshotLayer(folder, check);
            Names(check);
            if (output is not null) ExportPanels(folder, output);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Gestures(Action<string, bool> check)
    {
        foreach (var shape in new[] { CaptureShape.Rectangle, CaptureShape.Ellipse })
        foreach (string mode in new[] { "flat", "projected", "billboard" })
        foreach (bool legacyLock in new[] { false, true })
        {
            var document = new SceneDocument { Viewport = new(0, 0, 800, 600) };
            if (mode != "flat") document.Canvas.Projection = new() { Enabled = true,
                Matrix = new(1, .08, 0, 0, 1, 0, 0, .001, 1),
                Quad = [new(0, 0), new(800, 0), new(600, 400), new(0, 400)] };
            var editor = new EditorController(new(document)) { Tool = EditorTool.CaptureRegion,
                CaptureShape = shape, ConstrainAspect = legacyLock, BillboardMode = mode == "billboard" };
            if (mode == "billboard")
            {
                Pointer(editor, OverlayPointerKind.Down, new(150, 250));
                Pointer(editor, OverlayPointerKind.Up, new(150, 250));
            }
            var mapping = CanvasMapping.Create(document);
            var start = mode == "projected" ? mapping.ToScreen(new(100, 100)) : new PointD(100, 100);
            var end = mode == "projected" ? mapping.ToScreen(new(240, 190)) : new PointD(240, 190);
            string label = $"{mode} {shape} legacyLock={legacyLock}";
            CaptureSelection? completed = null; editor.CaptureSelectionCompleted += selection => completed = selection;
            Pointer(editor, OverlayPointerKind.Down, start);
            Pointer(editor, OverlayPointerKind.Move, end);
            check(label + " creates a free-ratio capture without Shift", editor.CapturePreview is { } preview &&
                !Square(preview.PlaneBounds ?? preview.Bounds));
            editor.ModifiersChanged(OverlayModifiers.Shift, new(end.X, end.Y), pointerLeftDown: true);
            check(label + " constrains immediately when Shift is pressed", editor.CapturePreview is { } shifted &&
                Square(shifted.PlaneBounds ?? shifted.Bounds));
            editor.ModifiersChanged(OverlayModifiers.None, new(end.X, end.Y), pointerLeftDown: true);
            check(label + " releases the temporary constraint without another pointer move", editor.CapturePreview is { } released &&
                !Square(released.PlaneBounds ?? released.Bounds));
            Pointer(editor, OverlayPointerKind.Up, end);
            check(label + " finishes freely after Shift release without changing the document", completed is not null &&
                !Square(completed.PlaneBounds ?? completed.Bounds) && editor.Session.Document.Elements.Count == 0 && editor.Session.UndoCount == 0);
        }
    }

    private static void LegacyState(string folder, Action<string, bool> check)
    {
        var parameters = new ToolParameters { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.Ellipse,
            ConstrainAspect = true, CaptureBorderColor = ColorLibrary.ForegroundRed, CaptureBorderWidth = 3 };
        string presetFile = Path.Combine(folder, "legacy-presets.json");
        File.WriteAllText(presetFile, JsonSerializer.Serialize(new { Version = 1, Presets = new[] { new
            { Id = "user-legacycapture", Name = "Legacy capture", Parameters = parameters } } }));
        string originalPresets = File.ReadAllText(presetFile);
        var library = new ToolPresetLibrary(presetFile);
        var editor = new EditorController(new()); editor.ApplyParameters(library.Apply("user-legacycapture"));
        check("an older personal capture preset cannot restore a persistent aspect lock", !editor.ConstrainAspect && !editor.Parameters.ConstrainAspect &&
            editor.CaptureShape == CaptureShape.Ellipse && editor.CaptureBorderWidth == 3);
        check("reading and applying an older capture preset leaves the user's original file untouched", File.ReadAllText(presetFile) == originalPresets &&
            library.User.Single().Parameters.ConstrainAspect);

        string stateFile = Path.Combine(folder, "legacy-state.json");
        var state = new ToolStateStore(stateFile); state.Tools[EditorTool.CaptureRegion] = parameters.Clone(); state.Save();
        string originalState = File.ReadAllText(stateFile);
        var restored = new ToolStateStore(stateFile); editor.ApplyParameters(restored.Tools[EditorTool.CaptureRegion]);
        check("an older capture working state cannot restore a persistent aspect lock", !editor.ConstrainAspect && !editor.Parameters.ConstrainAspect &&
            File.ReadAllText(stateFile) == originalState);
        var gallery = new ToolPresetGallery(editor, library); gallery.PrepareForShow(); gallery.SaveNamed("Fresh capture");
        check("new personal capture presets store the unlocked working parameters", !library.User.Single(p => p.Name == "Fresh capture").Parameters.ConstrainAspect);
        check("every official capture recipe starts without a persistent aspect preference", library.OfficialFor(EditorTool.CaptureRegion)
            .All(p => !p.Parameters.ConstrainAspect));
    }

    private static void Configuration(string folder, Action<string, bool> check)
    {
        var session = new EditorSession();
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 80 };
        session.Document.Elements.Add(image); session.Selection.Add(image.Id);
        var editor = new EditorController(session) { Tool = EditorTool.CaptureRegion };
        var panel = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(folder, "ui-presets.json")),
            new ColorLibrary(Path.Combine(folder, "ui-colors.json")));
        var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
        foreach (var shape in Enum.GetValues<CaptureShape>())
        {
            editor.CaptureShape = shape; panel.PrepareForShow();
            check(shape + " capture options do not expose persistent aspect controls", rows["Aspect"].Visibility == Visibility.Collapsed &&
                rows["Size"].Visibility == Visibility.Collapsed);
        }
        foreach (var tool in new[] { EditorTool.Crop, EditorTool.Blur, EditorTool.Pixelate })
        {
            editor.Tool = tool; panel.PrepareForShow();
            check(tool + " retains its separate aspect option", rows["Aspect"].Visibility == Visibility.Visible);
        }
        editor.Tool = EditorTool.Rectangle; panel.PrepareForShow();
        check("ordinary shape tools retain their size and aspect control", rows["Size"].Visibility == Visibility.Visible);
    }

    private static void SnapshotLayer(string folder, Action<string, bool> check)
    {
        using var pixels = new SKBitmap(80, 40); pixels.Erase(SKColors.Red);
        using var bitmap = SKImage.FromBitmap(pixels); using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var session = new EditorSession();
        var image = SnapshotImageCommand.Insert(session, new(encoded.ToArray(), new(30, 30, 80, 40), true));
        var inspector = new InspectorWindow(() => session, colorLibrary: new ColorLibrary(Path.Combine(folder, "layer-colors.json")));
        try
        {
            inspector.PrepareForShow();
            var keepAspect = Field<AspectRatioButton>(inspector, "keepAspect");
            check("a created screenshot layer still exposes aspect editing", keepAspect.Visibility == Visibility.Visible);
            keepAspect.IsChecked = true; keepAspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inspector.CompletePendingEdit();
            check("a screenshot layer can still enable its own aspect lock", session.Document.Elements.Single().ConstrainAspect == true);
            var fields = Field<Dictionary<string, TextBox>>(inspector, "fields");
            fields["W"].Text = "160"; inspector.CompletePendingEdit();
            var resized = session.Document.Elements.Single(); var bounds = ElementGeometry.Bounds(resized, includeStroke: false);
            check("resizing a locked screenshot layer scales both dimensions", resized.ConstrainAspect == true &&
                Math.Abs(bounds.Width - 160) < 1e-6 && Math.Abs(bounds.Height - 80) < 1e-6);
        }
        finally { inspector.Close(); }
    }

    private static void Names(Action<string, bool> check)
    {
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language); bool english = language == "en";
                check(language + " capture shape uses the requested Circle display name", L.Get("Snapshot.Shape.Ellipse") == (english ? "Circle" : "圆形"));
                check(language + " capture preset uses the matching Circle name", L.Get("Recipe.CaptureEllipse") == (english ? "Circle capture" : "圆形截图"));
                var editor = new EditorController(new()) { Tool = EditorTool.CaptureRegion };
                var panel = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                    new ColorLibrary(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
                panel.PrepareForShow();
                var choice = Field<Dictionary<string, SegmentedChoice>>(panel, "pickers")["CaptureShape"];
                var circle = choice.Items.Single(item => Equals(item.Tag, CaptureShape.Ellipse));
                string caption = ((StackPanel)circle.Content).Children.OfType<TextBlock>().Single().Text;
                check(language + " actual capture option caption tooltip and accessibility name all say Circle", caption == (english ? "Circle" : "圆形") &&
                    Equals(circle.ToolTip, caption) && System.Windows.Automation.AutomationProperties.GetName(circle) == caption);
                string hint = L.Get("ToolDescription.CaptureRegion");
                check(language + " capture hint describes holding Shift rather than reversing a stored preference", english
                    ? hint.Contains("Hold Shift", StringComparison.Ordinal) && !hint.Contains("inverts", StringComparison.OrdinalIgnoreCase)
                    : hint.Contains("按住 Shift", StringComparison.Ordinal) && !hint.Contains("反转", StringComparison.Ordinal));
            }
        }
        finally { L.Initialize(preference); }
    }

    private static bool Square(RectD bounds) => Math.Abs(bounds.Width - bounds.Height) < 1e-6;

    private static void ExportPanels(string folder, string output)
    {
        Directory.CreateDirectory(output);
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                var editor = new EditorController(new());
                editor.ApplyParameters(new() { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.Ellipse, ConstrainAspect = true });
                var panel = new ToolOptionsPanel(editor, new ToolPresetLibrary(Path.Combine(folder, "preview-presets.json")),
                    new ColorLibrary(Path.Combine(folder, "preview-colors.json")));
                panel.PrepareForShow();
                Field<ToolPresetGallery>(panel, "presets").PrepareForShow();
                panel.Measure(new Size(ToolOptionsPanel.PreferredWidth, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize)); panel.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"capture-options-{language}-{theme}.png")); encoder.Save(stream);
            }
        }
        finally { L.Initialize(preference); AppTheme.Apply("light"); }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pointer(EditorController editor, OverlayPointerKind kind, PointD point) =>
        editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
}
