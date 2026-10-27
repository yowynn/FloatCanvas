using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using System.Reflection;
using System.Runtime.CompilerServices;

/// <summary>Checks the real preview worker and compositor rather than a second grid implementation.</summary>
internal static class CanvasToolRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        var scene = Scene();
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 88, Height = 56,
            Transform = Affine2D.Translation(20, 20), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } });
        scene.Canvas.Grid = new() { Visible = true, Color = 0xFFFF0000, Width = 6, FlatCellSize = 16, FlatOrigin = new(64, 48) };
        using (var normal = Worker(scene, calibration: false))
        using (var canvasTool = Worker(scene, calibration: true))
        {
            // Two pixels away from the reference's one-pixel stroke but on the solid six-pixel grid.
            check("ordinary tools keep the configured grid beneath an opaque object", normal.GetPixel(66, 38) == SKColors.Lime);
            check("selecting the canvas tool keeps the same solid grid beneath that object", canvasTool.GetPixel(66, 38) == SKColors.Lime);
        }
        using (var normal = Worker(scene, calibration: false))
        using (var canvasTool = Worker(scene, calibration: true, guideOpacity: 0))
            check("disabling reference guides leaves exactly the same base pixels for canvas and ordinary tools", canvasTool.Bytes.SequenceEqual(normal.Bytes));

        foreach (double opacity in new[] { 0d, .4, 1d })
        {
            scene = Scene(); scene.Canvas.BackgroundColor = 0xFF284E9A; scene.LayerOpacity = opacity;
            byte expectedAlpha = (byte)Math.Round(opacity * 255);
            using var direct = Direct(scene);
            using var cached = Cached(scene);
            using var preview = Worker(scene, calibration: false);
            check($"canvas background uses global opacity {opacity} in the direct compositor", direct.GetPixel(25, 25).Alpha == expectedAlpha);
            check($"canvas background uses global opacity {opacity} in the cached compositor", cached.GetPixel(25, 25).Alpha == expectedAlpha);
            check($"canvas background uses global opacity {opacity} in the actual preview worker", preview.GetPixel(25, 25).Alpha == expectedAlpha);
        }

        scene = Scene(); scene.LayerOpacity = .5; scene.Canvas.BackgroundColor = 0xFFFF0000;
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 60, Height = 50,
            Transform = Affine2D.Translation(10, 10), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } });
        using (var direct = Direct(scene))
        using (var cached = Cached(scene))
        {
            var sample = direct.GetPixel(25, 25);
            check("canvas base and objects receive global opacity once after their composition", sample.Alpha == 128 && sample.Green == 255 && sample.Red == 0);
            check("cached canvas-base opacity matches the direct compositor byte for byte", cached.Bytes.SequenceEqual(direct.Bytes));
        }
        scene.Background = new() { Kind = ElementKind.Rectangle, Width = 128, Height = 96,
            Style = new() { Fill = 0xFF0000FF, StrokeWidth = 0 } };
        using (var photograph = Direct(scene))
            check("a frozen background retains its existing position above canvas color and below annotations", photograph.GetPixel(100, 80) == SKColors.Blue && photograph.GetPixel(25, 25).Alpha == 255);

        scene = Scene(); scene.Canvas.Grid = new() { Visible = true, Color = 0xFFFF0000, Width = 6, FlatCellSize = 16, FlatOrigin = new(64, 48) };
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport, 8, 6);
        scene.Canvas.Projection.Enabled = true; scene.Canvas.Projection.Matrix = Projective2D.Identity;
        scene.Canvas.DepthPriorityOcclusion = true;
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 88, Height = 56, IsBillboard = true,
            BillboardAnchor = new(64, 90), BillboardHeight = 56, Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } });
        scene.Canvas.BackgroundColor = 0xFFFF0000;
        using (var depth = Direct(scene))
        {
            check("depth mode still draws the canvas base beneath a billboard", depth.GetPixel(64, 50) == SKColors.Lime);
        }
        scene.LayerOpacity = 0;
        using (var depth = Direct(scene)) check("zero global opacity hides the canvas base in depth mode too", depth.GetPixel(25, 25).Alpha == 0);

        GuideOpacity(check);
        BasePresentationContext(check);
    }

    private static void GuideOpacity(Action<string, bool> check)
    {
        var scene = Scene();
        using var full = Worker(scene, calibration: true);
        using var half = Worker(scene, calibration: true, guideOpacity: .5);
        using var none = Worker(scene, calibration: true, guideOpacity: 0);
        // Sample one central vertical line between horizontal rows. At crossings
        // two independent guides compose, so final alpha does not halve linearly.
        byte MaximumAlpha(SKBitmap bitmap) => Enumerable.Range(63, 2)
            .SelectMany(x => Enumerable.Range(43, 3).Select(y => bitmap.GetPixel(x, y).Alpha)).Max();
        check("reference grid is visible at its default session opacity", MaximumAlpha(full) > 80);
        check("reference grid session opacity multiplies its normal opacity", Math.Abs(MaximumAlpha(half) * 2 - MaximumAlpha(full)) <= 2);
        check("zero reference opacity removes only the reference pixels", none.Bytes.All(value => value == 0));
        scene.Canvas.Grid = new() { Visible = true, Color = 0xFFFF0000, Width = .01 };
        using var subdued = Worker(scene, calibration: true);
        using var subduedHalf = Worker(scene, calibration: true, guideOpacity: .5);
        check("visible physical grid still subdues the reference grid", MaximumAlpha(subdued) + 10 < MaximumAlpha(full));
        check("session opacity also multiplies the already subdued reference grid", Math.Abs(MaximumAlpha(subduedHalf) * 2 - MaximumAlpha(subdued)) <= 3);
    }

    private static void BasePresentationContext(Action<string, bool> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var scene = Scene(); scene.Canvas.Grid.Visible = true; scene.Canvas.BackgroundColor = 0xFF445566;
        var session = new EditorSession(scene); var editor = new EditorController(session);
        var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        void Set(object instance, string name, object value) => instance.GetType().GetField(name, flags)!.SetValue(instance, value);
        void HostFlag(string name, bool value) => typeof(OverlayHost).GetProperty(name)!.SetValue(host, value);
        Set(host, "_monitors", new List<MonitorInfo> { new("fixture", new(0, 0, 128, 96), new(0, 0, 128, 96), 96, 96, true) });
        HostFlag(nameof(OverlayHost.IsVisible), true); HostFlag(nameof(OverlayHost.IsEditing), true);
        Set(desktop, "_host", host); Set(desktop, "_editor", editor);
        Set(desktop, "_canvasCalibration", new CanvasCalibrationController());
        Set(desktop, "_hiddenCanvasElements", new HashSet<Guid>()); Set(desktop, "_canvasGuideOpacity", 1d);
        CanvasPreviewContext Context() => (CanvasPreviewContext)typeof(DesktopSession).GetMethod("CurrentPreviewContext", flags)!.Invoke(desktop, null)!;
        var normal = Context(); editor.Tool = EditorTool.Canvas; var selected = Context();
        check("actual canvas tool transition reuses the matching base content epoch instead of blanking it", selected.Calibration && selected.HasSameContent(normal));
        Set(desktop, "_canvasGuideOpacity", .25d); var dimmed = Context();
        check("changing guide opacity keeps the valid base frame while requesting a different auxiliary frame", dimmed.GuideOpacity == .25 && dimmed != selected && dimmed.HasSameContent(selected));
        HostFlag(nameof(OverlayHost.IsVisible), false); var hidden = Context();
        check("hiding the canvas rejects a previously visible base frame", !hidden.HasSameContent(dimmed));
        var hiddenScene = (SceneDocument)typeof(DesktopSession).GetMethod("HiddenCanvasPreview", flags)!.Invoke(desktop, null)!;
        check("hidden canvas never retains physical grid or background when the canvas tool is selected", !hiddenScene.Canvas.Grid.Visible && hiddenScene.Canvas.BackgroundColor == 0);
        HostFlag(nameof(OverlayHost.IsVisible), true); var shown = Context();
        check("a visible-hidden-visible transition cannot reuse its earlier epoch", !shown.HasSameContent(dimmed));
        Set(host, "_monitors", new List<MonitorInfo> { new("replacement", new(0, 0, 128, 96), new(0, 0, 128, 96), 96, 96, true) });
        var monitors = Context();
        check("a monitor topology replacement rejects the previous base frame", !monitors.HasSameContent(shown));
        editor.ReplaceSession(new EditorSession(Scene()), new(0, 0)); var replaced = Context();
        check("a session replacement rejects previous document pixels", !replaced.HasSameContent(monitors));
        editor.ReplaceSession(editor.Session, new(20, -10)); var translated = Context();
        check("an origin change rejects the previous unshifted base frame", !translated.HasSameContent(replaced));
        check("a document epoch replacement rejects an otherwise matching frame", !translated.HasSameContent(translated with { DocumentEpoch = new object() }));
    }

    private static SceneDocument Scene() => new() { Viewport = new(0, 0, 128, 96) };

    private static SKBitmap Worker(SceneDocument scene, bool calibration, double guideOpacity = 1)
    {
        using var worker = new CanvasPreviewRenderer();
        using var snapshot = new CanvasPreviewSnapshot
        {
            Context = new(scene, new object(), new(0, 0), false, calibration, false, GuideOpacity: guideOpacity), SceneIdentity = scene,
            Document = scene, Resources = [], Outputs = [new(0, 0, 128, 96)], Revision = 1
        };
        using var frame = worker.Render(snapshot);
        return PreviewTestPixels.Read(frame.Pixels);
    }

    private static SKBitmap Direct(SceneDocument scene)
    {
        using var renderer = new SceneRenderer(); using var surface = Surface();
        surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, scene, new());
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static SKBitmap Cached(SceneDocument scene)
    {
        using var renderer = new SceneRenderer(); using var cache = new OverlaySceneCache(renderer); using var surface = Surface();
        surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, new(), 1, 0);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static SKSurface Surface() => SKSurface.Create(new SKImageInfo(128, 96, SKColorType.Bgra8888, SKAlphaType.Premul));
}
