using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

/// <summary>Public recipe ordering, useful appearance and one-step opt-ins through the production models.</summary>
internal static class OfficialPresetDesignTests
{
    private static readonly Dictionary<EditorTool, string> FirstIds = new()
    {
        [EditorTool.Select] = "select", [EditorTool.Freehand] = "freehand", [EditorTool.Highlighter] = "highlighter",
        [EditorTool.Line] = "line", [EditorTool.Rectangle] = "rectangle", [EditorTool.Ellipse] = "circle",
        [EditorTool.Text] = "text", [EditorTool.Number] = "number", [EditorTool.Image] = "image",
        [EditorTool.Eraser] = "eraser", [EditorTool.Crop] = "crop", [EditorTool.Blur] = "blur",
        [EditorTool.Pixelate] = "pixelate", [EditorTool.CaptureRegion] = "capture", [EditorTool.Symbol] = "triangle",
        [EditorTool.Emoji] = "emoji", [EditorTool.Stamp] = "stamp"
    };

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-official-design-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string preference = L.Preference;
        try
        {
            var library = new ToolPresetLibrary(Path.Combine(folder, "tools.json"));
            var canvas = new CanvasPresetLibrary(Path.Combine(folder, "canvas.json"));
            Ordering(library, check);
            Appearance(library, check);
            CropAspect(library, check);
            CanvasOptIns(canvas, check);
            PreservePersonalFiles(folder, check);
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                foreach (var group in library.Official.GroupBy(p => ToolPresetLibrary.Family(p.Parameters.Tool)))
                    check(language + " " + group.Key + " names distinguish every official recipe", group.All(p => !string.IsNullOrWhiteSpace(p.Name)) &&
                        group.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == group.Count());
                check(language + " canvas names distinguish every official recipe", canvas.Official.Select(p => p.Name).Distinct().Count() == canvas.Official.Count);
                if (output is not null) Export(output, language, library, canvas);
            }
            L.Initialize("zh-CN"); var chinese = library.Official.ToDictionary(p => p.Id, p => p.Name);
            var chineseCanvas = canvas.Official.ToDictionary(p => p.Id, p => p.Name);
            L.Initialize("en");
            check("all official tool names follow the current interface language", library.Official.All(p => chinese[p.Id] != p.Name));
            check("all official canvas names follow the current interface language", canvas.Official.All(p => chineseCanvas[p.Id] != p.Name));
        }
        finally { L.Initialize(preference); Directory.Delete(folder, true); }
    }

    private static void Ordering(ToolPresetLibrary library, Action<string, bool> check)
    {
        var official = library.Official;
        check("official preset IDs are unique and cover every executable non-canvas tool family", official.Select(p => p.Id).Distinct().Count() == official.Count &&
            official.Select(p => ToolPresetLibrary.Family(p.Parameters.Tool)).Distinct().Order().SequenceEqual(FirstIds.Keys.Order()));
        foreach (var (tool, id) in FirstIds)
        {
            var first = library.OfficialFor(tool).First();
            check(tool + " starts with its classic standard recipe", first.Id == "official." + id && first.Parameters.Style.Stroke == ColorLibrary.ForegroundRed);
        }
        check("the circle standard is circular and the ellipse remains available", library.OfficialFor(EditorTool.Ellipse).First().Parameters is
            { ConstrainAspect: true, DefaultWidth: 108, DefaultHeight: 108 } && library.Apply("official.ellipse") is { ConstrainAspect: false, DefaultWidth: 144, DefaultHeight: 108 });
        check("tools without adjustable appearance have one useful standard each", new[] { EditorTool.Select, EditorTool.Eraser, EditorTool.Stamp }
            .All(tool => library.OfficialFor(tool).Count == 1));
    }

    private static void Appearance(ToolPresetLibrary library, Action<string, bool> check)
    {
        foreach (var preset in library.Official)
        {
            var p = library.Apply(preset.Id);
            string snapshot = JsonSerializer.Serialize(p);
            check(preset.Id + " is a valid complete independent definition", p.IsValid() && snapshot == JsonSerializer.Serialize(preset.Parameters));
            bool painted = p.Tool is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Symbol or EditorTool.Text or EditorTool.Number;
            check(preset.Id + " keeps visible paint and useful opt-in values", p.Style.Opacity > 0 && (!painted || (p.Style.Stroke >> 24) > 0 && p.Style.StrokeWidth > 0) &&
                p.EffectStrength > 0 && (p.CaptureBorderColor >> 24) > 0 && p.Style.ArrowHeadSize >= 0);
            if (p.Tool is EditorTool.CaptureRegion or EditorTool.Emoji)
                check(preset.Id + " renders visible preview pixels through the actual icon", VisibleIcon(ToolParameterIcon.Create(p, 32)));
            var dirty = new ToolParameters { Tool = EditorTool.Number, NumberStart = 91, NumberStep = 7,
                Style = new() { Stroke = 0, Fill = 0, StrokeWidth = 0, Opacity = .1, FontSize = 11 }, EffectStrength = 99,
                CaptureBorderWidth = 0, CaptureBorderColor = 0, DefaultWidth = 7, DefaultHeight = 9, ConstrainAspect = true };
            var controller = new EditorController(new EditorSession()); controller.ApplyParameters(dirty); controller.ApplyParameters(p);
            var expected = new EditorController(new EditorSession()); expected.ApplyParameters(preset.Parameters);
            check(preset.Id + " replaces unrelated tool state through the editor", JsonSerializer.Serialize(controller.Parameters) == JsonSerializer.Serialize(expected.Parameters));
            p.Style.Stroke = 0; p.Style.Fill = 0; p.EffectStrength = 999;
            check(preset.Id + " cannot mutate the next official application", JsonSerializer.Serialize(library.Apply(preset.Id)) == snapshot);
        }
        check("common drawing recipes are distinct usable property combinations", library.Apply("official.freehand-fine").Style.StrokeWidth < library.Apply("official.freehand-bold").Style.StrokeWidth &&
            library.Apply("official.freehand-dotted").Style.StrokePattern == StrokePattern.Dotted && library.Apply("official.highlighter-yellow").Style.Stroke != ColorLibrary.ForegroundRed &&
            library.Apply("official.rectangle-soft").Style.Fill == ColorLibrary.BackgroundRed && library.Apply("official.rectangle-filled").Style.LinkColors);
        check("common content recipes retain their meaningful variations", library.Apply("official.image-half").Style.Opacity == .5 && library.Apply("official.crop-square").ConstrainAspect &&
            library.Apply("official.capture-annotated").CaptureSource == CaptureSource.BackgroundAndCanvas && library.Apply("official.capture-bordered").CaptureBorderWidth > 0 &&
            library.Apply("official.blur-fine").EffectStrength < library.Apply("official.blur-strong").EffectStrength);
        foreach (var preset in library.OfficialFor(EditorTool.CaptureRegion).Where(p => p.Parameters.CaptureBorderWidth == 0))
        {
            var p = preset.Parameters.Clone(); p.CaptureBorderWidth = 4;
            check(preset.Id + " enables a visible screenshot border with one width change", p.IsValid() && (p.CaptureBorderColor >> 24) != 0);
        }
        foreach (var preset in library.OfficialFor(EditorTool.Line).Where(p => p.Parameters.Style.EndArrowHead == ArrowHeadStyle.None))
        {
            var p = preset.Parameters.Clone(); p.Style.EndArrowHead = ArrowHeadStyle.FilledTriangle;
            check(preset.Id + " enables an arrowhead with one choice and automatic size", p.IsValid() && p.Style.ArrowHeadSize == 0 && Math.Max(10, p.Style.StrokeWidth * 4) > 0);
        }
    }

    private static void CanvasOptIns(CanvasPresetLibrary library, Action<string, bool> check)
    {
        var viewport = new RectD(0, 0, 320, 240);
        check("canvas starts with the classic transparent standard", library.Official.First().Id == "official.canvas-transparent" && library.Official.First().Parameters.BackgroundColor == 0);
        foreach (var preset in library.Official)
        {
            var p = preset.Parameters;
            check(preset.Id + " keeps valid visible grid parameters while disabled", p.IsValid && p.GridWidth > 0 && p.Columns > 0 && p.Rows > 0 && (p.GridColor >> 24) != 0 &&
                (p.GridVisible || p.GridColor == ColorLibrary.ForegroundRed));
            var current = new CanvasSettings { BackgroundColor = 0xFF123456, Grid = new() { Visible = true, Color = 0, Width = 8 } };
            string before = JsonSerializer.Serialize(current);
            var applied = library.Apply(preset.Id, current, viewport, viewport);
            check(preset.Id + " applies its whole canvas recipe and leaves the source unchanged", applied.BackgroundColor == p.BackgroundColor && applied.Grid.Color == p.GridColor &&
                applied.Grid.Visible == p.GridVisible && applied.Grid.Width == p.GridWidth && applied.Grid.Pattern == p.GridPattern && JsonSerializer.Serialize(current) == before);
            if (!applied.Grid.Visible)
            {
                byte[] hidden = Render(applied, viewport);
                applied.Grid.Visible = true;
                check(preset.Id + " reveals grid pixels by toggling visibility only", !hidden.SequenceEqual(Render(applied, viewport)));
            }
            applied.Projection.Enabled = true;
            SceneValidation.Validate(new SceneDocument { Canvas = applied, Viewport = viewport });
            check(preset.Id + " enables perspective with one toggle and a valid reference", applied.Projection.Quad.Count == 4 &&
                applied.Projection.CellSize > 0 && CanvasCalibration.IsValidProjection(applied.Projection.Matrix));
        }
    }

    private static void CropAspect(ToolPresetLibrary library, Action<string, bool> check)
    {
        foreach (string id in new[] { "official.crop", "official.crop-square" })
        {
            var image = new CanvasElement { Kind = ElementKind.Image, AssetId = new string('a', 64), Width = 200, Height = 140,
                Transform = Affine2D.Translation(20, 20) };
            var session = new EditorSession(new SceneDocument { Viewport = new(0, 0, 400, 300), Elements = [image] });
            session.SetSelection([image.Id]);
            var controller = new EditorController(session); controller.ApplyParameters(library.Apply(id));
            foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, new OverlayPoint(40, 40)),
                (OverlayPointerKind.Move, new OverlayPoint(120, 80)), (OverlayPointerKind.Up, new OverlayPoint(120, 80)) })
                controller.Pointer(new(kind, point, OverlayMouseButton.Left, OverlayModifiers.Control));
            var cropped = session.Document.Elements.Single();
            check(id + " uses its advertised aspect constraint in an actual crop gesture", session.UndoCount == 1 &&
                (id == "official.crop-square" ? cropped.Width == cropped.Height : cropped.Width == 80 && cropped.Height == 40));
        }
    }

    private static byte[] Render(CanvasSettings settings, RectD viewport)
    {
        using var bitmap = new SKBitmap((int)viewport.Width, (int)viewport.Height); using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent); using var renderer = new SceneRenderer();
        renderer.Draw(canvas, new SceneDocument { Canvas = settings, Viewport = viewport }, new AssetLibrary());
        return bitmap.Bytes;
    }

    private static bool VisibleIcon(UIElement icon)
    {
        var root = new Border { Child = icon, Width = 40, Height = 40, Padding = new(4) };
        root.Measure(new Size(40, 40)); root.Arrange(new Rect(0, 0, 40, 40)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var pixels = new byte[40 * 40 * 4]; bitmap.CopyPixels(pixels, 40 * 4, 0);
        return Enumerable.Range(0, 40 * 40).Count(i => pixels[i * 4 + 3] != 0) > 20;
    }

    private static void PreservePersonalFiles(string folder, Action<string, bool> check)
    {
        string toolPath = Path.Combine(folder, "legacy-personal-tools.json"), canvasPath = Path.Combine(folder, "legacy-personal-canvas.json");
        const string tools = "{\"Version\":1,\"Presets\":[{\"Id\":\"user-example\",\"Name\":\"User example\",\"Parameters\":{\"Tool\":1,\"Style\":{\"Stroke\":4294116870,\"Fill\":0,\"StrokeWidth\":27.651}}}]}";
        const string canvases = "{\"Version\":1,\"Presets\":[{\"Id\":\"user-canvas\",\"Name\":\"My canvas\",\"Parameters\":{\"GridVisible\":false,\"GridColor\":0}}]}";
        File.WriteAllText(toolPath, tools); File.WriteAllText(canvasPath, canvases);
        var toolLibrary = new ToolPresetLibrary(toolPath); var canvasLibrary = new CanvasPresetLibrary(canvasPath);
        _ = toolLibrary.Official; _ = canvasLibrary.Official;
        check("reading official recipes leaves older personal files and explicit values untouched", File.ReadAllText(toolPath) == tools && File.ReadAllText(canvasPath) == canvases &&
            toolLibrary.User.Single().Parameters.Style.StrokeWidth == 27.651 && toolLibrary.User.Single().Parameters.Style.Stroke == 4294116870 &&
            canvasLibrary.User.Single().Parameters.GridColor == 0 && !canvasLibrary.User.Single().Parameters.GridVisible);
    }

    private static void Export(string output, string language, ToolPresetLibrary tools, CanvasPresetLibrary canvases)
    {
        Directory.CreateDirectory(output);
        var groups = tools.Official.GroupBy(p => ToolPresetLibrary.Family(p.Parameters.Tool)).Select(group => new
        {
            Tool = group.Key.ToString(), Name = ToolDisplayName.Get(group.Key), Presets = group.Select((p, i) => new
            { p.Id, p.Name, Category = i == 0 ? "standard" : "common", p.Parameters }).ToArray()
        }).ToArray();
        File.WriteAllText(Path.Combine(output, "official-presets-" + language + ".json"), JsonSerializer.Serialize(new
        {
            Language = language, Tools = groups, Canvas = canvases.Official.Select((p, i) => new
            { p.Id, p.Name, Category = i == 0 ? "standard" : "common", p.Parameters })
        }, new JsonSerializerOptions { WriteIndented = true }));
        var root = new StackPanel { Width = 1200, Background = Brushes.White };
        foreach (var group in tools.Official.GroupBy(p => ToolPresetLibrary.Family(p.Parameters.Tool)))
        {
            root.Children.Add(new TextBlock { Text = ToolDisplayName.Get(group.Key), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(12, 10, 12, 6), Foreground = Brushes.Black });
            var row = new WrapPanel { Width = 1200 };
            foreach (var preset in group) row.Children.Add(PreviewTile(preset.Name, ToolParameterIcon.Create(preset.Parameters, 32)));
            root.Children.Add(row);
        }
        root.Children.Add(new TextBlock { Text = ToolDisplayName.Get(EditorTool.Canvas), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(12, 10, 12, 6), Foreground = Brushes.Black });
        var canvasRow = new WrapPanel { Width = 1200 };
        foreach (var preset in canvases.Official) canvasRow.Children.Add(PreviewTile(preset.Name, new CanvasParameterIcon(preset.Parameters, 32)));
        root.Children.Add(canvasRow);
        root.Measure(new Size(1200, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1200, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, "official-presets-" + language + ".png")); encoder.Save(stream);
    }

    private static Border PreviewTile(string name, UIElement icon)
    {
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(icon); body.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = Brushes.Black, TextAlignment = System.Windows.TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new(4, 5, 4, 0), MaxWidth = 184 });
        return new Border { Child = body, Width = 200, Height = 78, Padding = new(4), BorderBrush = Brushes.LightGray, BorderThickness = new(0, 0, 0, 1) };
    }
}
