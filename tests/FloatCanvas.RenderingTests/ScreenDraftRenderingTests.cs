using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ScreenDraftRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        var source = Scene(); var mapping = CanvasMapping.Create(source); var assets = new AssetLibrary();
        var back = World(Box(240, 360, 0xFF0000FF, 60, 80), mapping);
        var front = World(Box(180, 300, 0xFF00FF00, 150, 100), mapping);
        source.Elements = [back, front]; source.RootOrder = [back.Id, front.Id];
        var raw = Box(200, 300, 0xFFFF0000, 100, 100); var world = World(raw, mapping);
        var evaluated = SceneEvaluation.Build(source); string before = JsonSerializer.Serialize(source);
        var temporary = SceneEvaluation.WithTransientElement(evaluated.RenderDocument, world, 1);
        check("transient world view borrows base leaves and clones only its private inserted leaf", temporary.IsRuntimeEvaluated &&
            ReferenceEquals(temporary.Elements[0], evaluated.RenderDocument.Elements[0]) && ReferenceEquals(temporary.Elements[2], evaluated.RenderDocument.Elements[1]) &&
            !ReferenceEquals(temporary.Elements[1], world) && temporary.Groups.Count == 0 && temporary.Instances.Count == 0 && temporary.Definitions.Count == 0);
        check("transient world view never changes base occurrence maps or source", evaluated.FindElement(world.Id) is null &&
            evaluated.Leaves.Count == 2 && JsonSerializer.Serialize(source) == before && !source.IsRuntimeEvaluated &&
            SceneEvaluation.Build(temporary).FindElement(world.Id) is null && ReferenceEquals(SceneEvaluation.Build(temporary).Leaves, evaluated.Leaves));
        foreach (bool depth in new[] { false, true })
        foreach (double opacity in new[] { 1d, .43 })
        {
            source.Canvas.DepthPriorityOcclusion = depth; source.LayerOpacity = opacity;
            evaluated = SceneEvaluation.Build(source);
            using var renderer = new SceneRenderer();
            using var actual = Render(renderer, evaluated.RenderDocument, assets, new(raw, 1, world));
            var reference = source.Clone(); new SceneGraph(reference).InsertElement(world, index: 1);
            using var expected = SKBitmap.Decode(renderer.ExportPng(reference, assets));
            check($"valid screen draft matches a real authored slot with depth={depth} opacity={opacity}", actual.Pixels.SequenceEqual(expected.Pixels));
        }
        source.Canvas.DepthPriorityOcclusion = false; source.LayerOpacity = 1;
        evaluated = SceneEvaluation.Build(source);
        var pending = Box(200, 80, 0xFFFF0000, 100, 100);
        check("pending screen draft ground is genuinely above the safe horizon", !BillboardGeometry.TryResolveScreenBottom(pending, mapping,
            ElementGeometry.LocalToPlane(pending), out _, out _));
        using (var renderer = new SceneRenderer())
        using (var actual = Render(renderer, evaluated.RenderDocument, assets, new(pending, 1)))
        {
            var mixed = actual.GetPixel(120, 150);
            check($"pending screen ink remains visible above horizon as a half-opacity guide ({mixed})", mixed.Red is >= 127 and <= 128 && mixed.Blue is >= 126 and <= 128 && mixed.Alpha == 255);
            check("pending painter guide respects the real scope insertion slot rather than topmost overlay", actual.GetPixel(200, 150) == SKColors.Lime);
            var clean = renderer.ExportPng(source, assets);
            using var cleanPixels = SKBitmap.Decode(clean);
            check("screen override is scoped to one draw and cannot affect later ordinary rendering", cleanPixels.GetPixel(120, 150) == SKColors.Blue);
        }
        foreach (double opacity in new[] { 1d, .43 })
        {
            source.Canvas.DepthPriorityOcclusion = true; source.LayerOpacity = opacity;
            evaluated = SceneEvaluation.Build(source);
            using var renderer = new SceneRenderer();
            using var actual = Render(renderer, evaluated.RenderDocument, assets, new(pending, 1));
            using var baseline = SKBitmap.Decode(renderer.ExportPng(source, assets));
            var overlay = new SceneDocument { Viewport = source.Viewport, LayerOpacity = opacity, Elements = [pending.Clone()] };
            overlay.Elements[0].Style.Opacity *= .5;
            using var guide = SKBitmap.Decode(renderer.ExportPng(overlay, assets));
            using var surface = SKSurface.Create(new SKImageInfo(baseline.Width, baseline.Height));
            surface.Canvas.Clear(SKColors.Transparent);
            using var baselineImage = SKImage.FromBitmap(baseline); using var guideImage = SKImage.FromBitmap(guide);
            surface.Canvas.DrawImage(baselineImage, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            surface.Canvas.DrawImage(guideImage, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            using var image = surface.Snapshot(); using var expected = SKBitmap.FromImage(image);
            check($"pending depth guide preserves the complete original depth scene and applies global alpha once ({opacity})",
                actual.Pixels.SequenceEqual(expected.Pixels));
        }
        source.Canvas.DepthPriorityOcclusion = false; source.LayerOpacity = .7;
        using var color = SKSurface.Create(new SKImageInfo(32, 32)); color.Canvas.Clear(SKColors.Orange);
        using var colorImage = color.Snapshot(); using var encoded = colorImage.Encode(SKEncodedImageFormat.Png, 100);
        string asset = assets.Add(encoded.ToArray());
        foreach (var body in new[]
        {
            new CanvasElement { Kind = ElementKind.Freehand, Points = [new(0, 0), new(100, 30)], Style = new() { Stroke = 0xFFFF00FF, StrokeWidth = 10 } },
            new CanvasElement { Kind = ElementKind.Highlighter, Points = [new(0, 0), new(100, 30)], Style = new() { Stroke = 0xFFFFFF00, StrokeWidth = 12, Opacity = .4 } },
            new CanvasElement { Kind = ElementKind.Text, Width = 130, Height = 50, Text = "screen 天空", Style = new() { FontSize = 20, Stroke = 0xFFFF0000 } },
            new CanvasElement { Kind = ElementKind.Number, Width = 50, Height = 50, Number = 8, Style = new() { FontSize = 24, Stroke = 0xFF00FF00 } },
            new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 50, AssetId = asset },
            new CanvasElement { Kind = ElementKind.LivePixelate, Width = 100, Height = 50, Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }] },
            new CanvasElement { Kind = ElementKind.LiveBlur, Width = 100, Height = 50, Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 2 }] }
        })
        {
            body.Transform = Affine2D.Translation(70, 70);
            using var renderer = new SceneRenderer();
            var baseScene = Scene(); baseScene.LayerOpacity = .7;
            var baseWorld = SceneEvaluation.Build(baseScene).RenderDocument;
            using var actual = Render(renderer, baseWorld, assets, new(body, 0));
            var screenTransform = ElementGeometry.LocalToPlane(body);
            var center = screenTransform.TransformPoint(ElementGeometry.LocalBounds(body).Center);
            if (!BillboardGeometry.TryResolveScreenPlacement(body, CanvasMapping.Create(baseScene), screenTransform, new(center.X, 400), out var pose, out _))
                throw new InvalidOperationException("Screen-only reference placement failed.");
            var referenceBody = body.Clone(); referenceBody.IsBillboard = true; referenceBody.BillboardAnchor = pose.Anchor;
            referenceBody.BillboardHeight = pose.Height; referenceBody.Transform = pose.Linear; referenceBody.ProjectivePlacement = null; referenceBody.Style.Opacity *= .5;
            var reference = Scene(); reference.LayerOpacity = .7; reference.Elements = [referenceBody];
            using var expected = SKBitmap.Decode(renderer.ExportPng(reference, assets));
            check($"pending {body.Kind} follows raw screen geometry above the horizon without source insertion", actual.Pixels.SequenceEqual(expected.Pixels) && actual.Pixels.Any(pixel => pixel.Alpha > 0));
        }
    }

    private static SKBitmap Render(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, TransientScreenElement draft)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)scene.Viewport.Width, (int)scene.Viewport.Height));
        surface.Canvas.Clear(SKColors.Transparent); renderer.DrawWithScreenDraft(surface.Canvas, scene, assets, draft);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }
    private static CanvasElement Box(double width, double height, uint color, double x, double y) => new()
    {
        Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = color, StrokeWidth = 0 }
    };
    private static CanvasElement World(CanvasElement raw, CanvasMapping mapping)
    {
        if (!BillboardGeometry.TryResolveScreenBottom(raw, mapping, ElementGeometry.LocalToPlane(raw), out var pose, out var failure)) throw new InvalidOperationException(failure.ToString());
        var world = raw.Clone(); world.IsBillboard = true; world.BillboardAnchor = pose.Anchor; world.BillboardHeight = pose.Height;
        world.Transform = pose.Linear; world.ProjectivePlacement = null; return world;
    }
    private static SceneDocument Scene() => new()
    {
        Viewport = new(0, 0, 400, 500), Canvas = new() { Projection = new() { Enabled = true,
            Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1), Quad = [new(0, 300), new(400, 300), new(400, 500), new(0, 500)] } }
    };
}
