using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class CaptureSelectionRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        ShapesAndAlpha(check);
        BorderAndDisplays(check);
        SourcesAndCoordinates(check);
        LiveEffectContext(check);
        InvalidSelections(check);
    }

    private static void ShapesAndAlpha(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var scene = new SceneDocument { Viewport = new(0, 0, 100, 100), Elements = [Box(0, 0, 100, 100, 0x80FF0000)] };
        var assets = new AssetLibrary();
        var ellipse = Capture(CaptureShape.Ellipse, new(10, 10, 50, 40));
        var result = renderer.ExportSelectionPng(scene, assets, ellipse, false);
        using var pixels = Decode(result);
        check("ellipse output uses exact physical outer dimensions", result.Bounds == new RectD(10, 10, 50, 40) && pixels.Width == 50 && pixels.Height == 40);
        check("ellipse keeps outside pixels transparent", pixels.GetPixel(0, 0).Alpha == 0 && pixels.GetPixel(49, 39).Alpha == 0);
        check("ellipse keeps source alpha without a matte", pixels.GetPixel(25, 20).Alpha is >= 127 and <= 128 && pixels.GetPixel(25, 20).Red == 255);
        check("ellipse boundary has antialiased pixels", Pixels(pixels).Any(p => p.Alpha is > 0 and < 127));
        check("nonempty pixel content is reported", result.HasVisiblePixels);

        scene.Elements.Add(Box(0, 0, 100, 100, 0x80FF0000));
        using var overlap = Decode(renderer.ExportSelectionPng(scene, assets, ellipse, false));
        check("overlapping annotations compose before boundary antialiasing", Math.Abs(overlap.GetPixel(0, 17).Alpha - pixels.GetPixel(0, 17).Alpha * 1.5) <= 2);

        PointD[] concave = [new(10, 10), new(60, 10), new(60, 25), new(25, 25), new(25, 60), new(10, 60)];
        foreach (var shape in new[] { CaptureShape.FreehandLasso, CaptureShape.PolygonLasso })
        {
            using var lasso = Decode(renderer.ExportSelectionPng(scene, assets, Capture(shape, new(10, 10, 50, 50), concave), false));
            check($"{shape} fills a concave closed region", lasso.GetPixel(5, 40).Alpha > 180 && lasso.GetPixel(40, 5).Alpha > 180);
            check($"{shape} leaves concave cutouts transparent", lasso.GetPixel(40, 40).Alpha == 0);
        }
        PointD[] bowtie = [new(10, 10), new(60, 60), new(10, 60), new(60, 10)];
        using var crossing = Decode(renderer.ExportSelectionPng(scene, assets, Capture(CaptureShape.PolygonLasso, new(10, 10, 50, 50), bowtie), false));
        check("self-crossing lasso with zero signed area retains both lobes", crossing.GetPixel(25, 5).Alpha > 180 && crossing.GetPixel(25, 44).Alpha > 180);
        check("self-crossing lasso uses even-odd fill", crossing.GetPixel(5, 25).Alpha == 0 && crossing.GetPixel(44, 25).Alpha == 0);

        PointD[] twice = [new(10, 10), new(60, 10), new(60, 60), new(10, 60), new(10, 10), new(60, 10), new(60, 60), new(10, 60)];
        bool emptyParity = false;
        try { renderer.ExportSelectionPng(scene, assets, Capture(CaptureShape.PolygonLasso, new(10, 10, 50, 50), twice), false); }
        catch (ArgumentException) { emptyParity = true; }
        check("an even-odd contour traced twice has no area and is rejected", emptyParity);
    }

    private static void BorderAndDisplays(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = new SceneDocument { Viewport = new(-100, -100, 200, 200), Elements = [Box(-100, -100, 200, 200, 0xFF0000FF)] };
        var selection = Capture(CaptureShape.Rectangle, new(-30, -20, 50, 40)) with { BorderWidth = 6, BorderColor = 0xFFFF0000 };
        var result = renderer.ExportSelectionPng(scene, assets, selection, false);
        using var pixels = Decode(result);
        check("border expands physical placement on all four sides", result.Bounds == new RectD(-33, -23, 56, 46));
        check("baked border retains full left and right extents", pixels.GetPixel(0, 23) == SKColors.Red && pixels.GetPixel(55, 23) == SKColors.Red);
        check("baked border retains full top and bottom extents", pixels.GetPixel(28, 0) == SKColors.Red && pixels.GetPixel(28, 45) == SKColors.Red);
        check("border does not replace the selection interior", pixels.GetPixel(28, 23) == SKColors.Blue);
        check("round border joins do not extend beyond their allocated corners", pixels.GetPixel(0, 0).Alpha < 255 && pixels.GetPixel(55, 45).Alpha < 255);

        var ellipse = renderer.ExportSelectionPng(scene, assets, selection with { Shape = CaptureShape.Ellipse }, false);
        using var ellipsePixels = Decode(ellipse);
        check("ellipse border occupies its expanded bounds without clipping", ellipsePixels.GetPixel(28, 0).Red > 250 && ellipsePixels.GetPixel(0, 23).Red > 250);
        check("ellipse border preserves transparent outer corners", ellipsePixels.GetPixel(0, 0).Alpha == 0);

        RectD[] displays = [new(-100, -100, 95, 200), new(5, -100, 95, 200)];
        using var gapped = Decode(renderer.ExportSelectionPng(scene, assets, selection, false, displays));
        check("physical monitor gaps remain transparent for content and border", Enumerable.Range(0, gapped.Height).All(y => Enumerable.Range(28, 10).All(x => gapped.GetPixel(x, y).Alpha == 0)));
        check("physical monitor tiles retain full-size positions on both sides of gap", gapped.GetPixel(10, 23) == SKColors.Blue && gapped.GetPixel(45, 23) == SKColors.Blue);
        using var noDisplays = Decode(renderer.ExportSelectionPng(scene, assets, selection, false, []));
        check("an explicitly empty display set cannot leak any pixels", Pixels(noDisplays).All(p => p.Alpha == 0));
    }

    private static void SourcesAndCoordinates(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        string blue = assets.Add(Png(100, 80, (_, _) => SKColors.Blue));
        var scene = new SceneDocument
        {
            Viewport = new(-70, -50, 100, 80),
            Background = new() { Kind = ElementKind.Image, Width = 100, Height = 80, AssetId = blue, Transform = Affine2D.Translation(-70, -50) },
            Elements = [Box(-50, -35, 20, 20, 0x80FF0000)]
        };
        var original = JsonSerializer.Serialize(scene);
        var selection = Capture(CaptureShape.Rectangle, new(-60.25, -40.75, 50.5, 40.25));
        var combinedResult = renderer.ExportSelectionPng(scene, assets, selection, true);
        using var combined = Decode(combinedResult);
        using var canvasOnly = Decode(renderer.ExportSelectionPng(scene, assets, selection, false));
        var backgroundScene = scene.Clone(); backgroundScene.Elements.Clear();
        using var backgroundOnly = Decode(renderer.ExportSelectionPng(backgroundScene, assets, selection, true));
        check("fractional selection rounds outward in physical pixels at negative origin", combinedResult.Bounds == new RectD(-61, -41, 52, 41));
        check("background source excludes separately omitted annotations", backgroundOnly.GetPixel(20, 20) == SKColors.Blue);
        check("combined source composites annotations over desktop", combined.GetPixel(20, 20).Red is >= 127 and <= 128 && combined.GetPixel(20, 20).Blue is >= 127 and <= 128 && combined.GetPixel(20, 20).Alpha == 255);
        check("canvas-only source retains alpha and transparent unmarked area", canvasOnly.GetPixel(20, 20).Red == 255 && canvasOnly.GetPixel(20, 20).Alpha is >= 127 and <= 128 && canvasOnly.GetPixel(45, 30).Alpha == 0);
        check("raster export does not mutate original annotations, resources or viewport", JsonSerializer.Serialize(scene) == original && assets.Count == 1);

        var empty = new SceneDocument { Viewport = scene.Viewport };
        var transparent = renderer.ExportSelectionPng(empty, assets, selection, false);
        check("fully transparent canvas-only raster is reported before insertion", !transparent.HasVisiblePixels);
        var visibleBorder = renderer.ExportSelectionPng(empty, assets, selection with { BorderWidth = 2, BorderColor = 0xFFFF0000 }, false);
        check("an explicitly configured border makes the final PNG visible", visibleBorder.HasVisiblePixels);

        var opaque = scene.Clone(); opaque.LayerOpacity = 1;
        string snapshotAsset = assets.Add(renderer.ExportSelectionPng(opaque, assets, selection, false).PngBytes);
        var inserted = new SceneDocument { Viewport = combinedResult.Bounds, LayerOpacity = .5,
            Elements = [new() { Kind = ElementKind.Image, Width = combinedResult.Bounds.Width, Height = combinedResult.Bounds.Height,
                Transform = Affine2D.Translation(combinedResult.Bounds.X, combinedResult.Bounds.Y), AssetId = snapshotAsset }] };
        using var insertedPixels = SKBitmap.Decode(renderer.ExportPng(inserted, assets, true));
        check("snapshot image uses normal image opacity once after source preparation", insertedPixels.GetPixel(20, 20).Alpha is >= 63 and <= 64);
    }

    private static void LiveEffectContext(Action<string, bool> check)
    {
        var viewport = new RectD(-80, -40, 160, 120);
        using var backdrop = Backdrop(viewport, (x, y) => x % 13 < 5 ? SKColors.White : y % 9 < 4 ? SKColors.Blue : SKColors.Red);
        using var renderer = new SceneRenderer { LiveBackdrop = backdrop }; var assets = new AssetLibrary();
        var scene = new SceneDocument { Viewport = viewport, Elements = [Box(-70, -25, 10, 50, 0xFF00FF00),
            new() { Kind = ElementKind.LivePixelate, Width = 100, Height = 70, Transform = Affine2D.Translation(-60, -20),
                Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 17 }], Style = new() { StrokeWidth = 0 } }] };
        using var whole = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        var selection = Capture(CaptureShape.Rectangle, new(-47, -11, 61, 43));
        using var cropped = Decode(renderer.ExportSelectionPng(scene, assets, selection, false));
        bool same = true;
        for (int y = 0; y < cropped.Height; y++) for (int x = 0; x < cropped.Width; x++)
            same &= cropped.GetPixel(x, y) == whole.GetPixel(x + 33, y + 29);
        check("selection crops only final pixels and preserves live pixelation sample grid", same);
        var larger = Capture(CaptureShape.Rectangle, new(-80, -40, 160, 120));
        using var transparent = Decode(renderer.ExportSelectionPng(scene, assets, larger, false));
        check("canvas-only captures freeze live pixelation but not unrelated desktop", transparent.GetPixel(35, 35).Alpha == 255 && transparent.GetPixel(150, 110).Alpha == 0);

        var source = scene.Clone();
        source.Elements.Insert(0, Box(-60, -20, 100, 70, 0xFFFFFF00));
        renderer.LiveEffectContext = source;
        using var contextPixels = Decode(renderer.ExportSelectionPng(scene, assets, selection, false));
        check("selection preserves the explicit full-scene live-effect context", contextPixels.GetPixel(20, 20) == SKColors.Yellow && ReferenceEquals(renderer.LiveEffectContext, source));
    }

    private static void InvalidSelections(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary(); var scene = new SceneDocument();
        var invalid = new[]
        {
            Capture(CaptureShape.Rectangle, new(1, 1, 0, 40)),
            Capture(CaptureShape.Ellipse, new(double.NaN, 1, 20, 40)),
            Capture(CaptureShape.FreehandLasso, new(1, 1, 10, 10), [new(1, 1), new(2, 2)]),
            Capture(CaptureShape.PolygonLasso, new(1, 1, 10, 10), [new(1, 1), new(2, 2), new(3, 3)]),
            Capture(CaptureShape.Rectangle, new(1, 1, 20, 40)) with { BorderWidth = double.PositiveInfinity }
        };
        for (int i = 0; i < invalid.Length; i++)
        {
            bool rejected = false;
            try { renderer.ExportSelectionPng(scene, assets, invalid[i], false); }
            catch (ArgumentException) { rejected = true; }
            check($"invalid or zero-area selection {i} cannot produce an image", rejected);
        }
        bool tooLarge = false;
        try { renderer.ExportSelectionPng(scene, assets, Capture(CaptureShape.Rectangle, new(0, 0, 100000, 100000)), false); }
        catch (InvalidOperationException) { tooLarge = true; }
        check("impossible selection allocation fails before creating raster buffers", tooLarge);
    }

    private static CaptureSelection Capture(CaptureShape shape, RectD bounds, IReadOnlyList<PointD>? points = null) =>
        new(shape, CaptureSource.CanvasOnly, bounds, points ?? [], 0, 0xFFE03E3E);
    private static CanvasElement Box(double x, double y, double width, double height, uint fill) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = fill, StrokeWidth = 0 } };
    private static SKBitmap Decode(CaptureRasterResult result) => SKBitmap.Decode(result.PngBytes) ?? throw new InvalidDataException("Invalid screenshot PNG.");
    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap)
    { for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) yield return bitmap.GetPixel(x, y); }
    private static byte[] Png(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static DesktopBackdrop Backdrop(RectD bounds, Func<int, int, SKColor> color)
    {
        using var bitmap = SKBitmap.Decode(Png((int)bounds.Width, (int)bounds.Height, color));
        bitmap.SetImmutable(); return new([new(bounds, SKImage.FromBitmap(bitmap))]);
    }
}
