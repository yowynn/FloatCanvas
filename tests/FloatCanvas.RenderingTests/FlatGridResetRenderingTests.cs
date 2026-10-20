using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class FlatGridResetRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        foreach (var fixture in Fixtures())
        foreach (var pattern in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
        {
            string label = $"{fixture.Name} {pattern}";
            var flat = Scene(fixture, pattern);
            var reset = Reset(flat, fixture.Reference);
            var mapping = CanvasMapping.Create(reset);
            check($"{label} reset is an identity projection with the primary-screen grid origin and exact spacing",
                reset.Canvas.Projection.Matrix == Projective2D.Identity &&
                mapping.GridOrigin.DistanceTo(flat.Canvas.Grid.FlatOrigin!.Value) < 1e-8 &&
                mapping.GridCellSize == fixture.CellSize);
            check($"{label} reset keeps the original layer content and its screen bounds",
                JsonSerializer.Serialize(flat.Elements) == JsonSerializer.Serialize(reset.Elements) &&
                SceneRenderer.ProjectedBounds(flat, flat.Elements[0], true) == SceneRenderer.ProjectedBounds(reset, reset.Elements[0], true));
            using (var ordinaryFlat = Pixels(renderer, flat, assets))
            using (var ordinaryReset = Pixels(renderer, reset, assets))
            {
                check($"{label} flat and freshly reset perspective have byte-identical ordinary RGBA pixels", Same(ordinaryFlat, ordinaryReset));
                check($"{label} flat and freshly reset perspective have byte-identical PNG output",
                    renderer.ExportPng(flat, assets, true).SequenceEqual(renderer.ExportPng(reset, assets, true)));
            }
            using (var referenceFlat = Pixels(renderer, flat, assets, reference: true))
            using (var referenceReset = Pixels(renderer, reset, assets, reference: true))
                check($"{label} flat and freshly reset perspective have byte-identical reference-grid pixels", Same(referenceFlat, referenceReset));

            flat.Canvas.DepthPriorityOcclusion = true;
            flat.Elements.Add(Board(flat.Viewport.Center));
            reset = Reset(flat, fixture.Reference);
            using var depthFlat = Pixels(renderer, flat, assets);
            using var depthReset = Pixels(renderer, reset, assets);
            check($"{label} depth composition after reset matches the flat grid and original layers exactly", Same(depthFlat, depthReset));
        }
        foreach (var pattern in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
            RepeatedToggle(renderer, assets, pattern, check);
    }

    private static void RepeatedToggle(SceneRenderer renderer, AssetLibrary assets, StrokePattern pattern, Action<string, bool> check)
    {
        var fixture = new Fixture("decimal repeated toggle", new(-1000, -350, 1920, 1080), new(-210, 50, 360, 240), 73.1);
        var scene = Reset(Scene(fixture, pattern), fixture.Reference);
        scene.Canvas.Projection.Enabled = false;
        string before = JsonSerializer.Serialize(scene);
        using var expected = Pixels(renderer, scene, assets);
        bool pixelsStable = true, valuesStable = true;
        for (int i = 0; i < 100; i++)
        {
            scene.Canvas.Projection.Enabled = true;
            using (var perspective = Pixels(renderer, scene, assets)) pixelsStable &= Same(expected, perspective);
            valuesStable &= CanvasMapping.Create(scene).GridCellSize == fixture.CellSize;
            scene.Canvas.Projection.Enabled = false;
            using (var flat = Pixels(renderer, scene, assets)) pixelsStable &= Same(expected, flat);
            valuesStable &= JsonSerializer.Serialize(scene) == before;
        }
        check($"{pattern} 100 perspective round trips retain identical grid and layer pixels", pixelsStable);
        check($"{pattern} 100 perspective round trips retain exact counts, spacing, corners and original document values", valuesStable);
    }

    public static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var fixture = Fixtures().Single(f => f.CellSize == 73.1);
        foreach (var pattern in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
        {
            var flat = Scene(fixture, pattern);
            var reset = Reset(flat, fixture.Reference);
            using var flatPixels = Pixels(renderer, flat, assets);
            using var resetPixels = Pixels(renderer, reset, assets);
            using var flatImage = SKImage.FromBitmap(flatPixels);
            using var resetImage = SKImage.FromBitmap(resetPixels);
            string name = pattern.ToString().ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(directory, $"flat-{name}.png"), renderer.ExportPng(flat, assets, true));
            File.WriteAllBytes(Path.Combine(directory, $"reset-{name}.png"), renderer.ExportPng(reset, assets, true));
            using var atlas = SKSurface.Create(new SKImageInfo(flatPixels.Width * 2 + 24, flatPixels.Height + 44, SKColorType.Bgra8888, SKAlphaType.Premul));
            atlas.Canvas.Clear(SKColors.White);
            atlas.Canvas.DrawImage(flatImage, 0, 44, new SKSamplingOptions(SKFilterMode.Nearest));
            atlas.Canvas.DrawImage(resetImage, flatPixels.Width + 24, 44, new SKSamplingOptions(SKFilterMode.Nearest));
            using var font = new SKFont(SKTypeface.Default, 18);
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF334155) };
            atlas.Canvas.DrawText($"Flat grid: 73.1 px / {pattern}", 12, 28, SKTextAlign.Left, font, paint);
            atlas.Canvas.DrawText("Perspective enabled, then Reset", flatPixels.Width + 36, 28, SKTextAlign.Left, font, paint);
            using var snapshot = atlas.Snapshot();
            using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(directory, $"flat-reset-{name}-comparison.png"), png.ToArray());
        }
    }

    private static IEnumerable<Fixture> Fixtures()
    {
        yield return new("binary-exact fractional spacing", new(0, 0, 800, 600), new(-120, -100, 1040, 800), 73.125);
        yield return new("decimal spacing with negative primary origin on all-screen viewport", new(-640, -180, 800, 600), new(-1100, -380, 1280, 800), 73.1);
        yield return new("minimum editable spacing with maximum count clamp", new(-2000, -500, 3200, 1800), new(-720, 180, 640, 480), 1);
        yield return new("maximum spacing with one-cell count clamp", new(-640, -180, 800, 600), new(-560, -120, 640, 480), CanvasGridGeometry.MaximumCellSize);
    }

    private static SceneDocument Scene(Fixture fixture, StrokePattern pattern)
    {
        var scene = new SceneDocument { Viewport = fixture.Viewport };
        scene.Canvas.Grid = new()
        {
            Visible = true, Width = 2, Color = 0xFF238A80, Pattern = pattern,
            FlatCellSize = fixture.CellSize, FlatOrigin = fixture.Reference.Center
        };
        var position = scene.Viewport.Center + new PointD(45, -35);
        scene.Elements.Add(new()
        {
            Kind = ElementKind.Rectangle, Width = 44, Height = 28,
            Transform = Affine2D.Translation(position.X, position.Y),
            Style = new() { Fill = 0xFFE65B45, StrokeWidth = 0 }
        });
        return scene;
    }

    private static SceneDocument Reset(SceneDocument flat, RectD reference)
    {
        var reset = flat.Clone();
        double size = reset.Canvas.Grid.FlatCellSize!.Value;
        var counts = CanvasGridGeometry.CountsForCellSize(reference, size);
        reset.Canvas.Projection = CanvasGridGeometry.CreateFlatReference(reference, counts.Columns, counts.Rows, size);
        reset.Canvas.Projection.Enabled = true;
        return reset;
    }

    private static CanvasElement Board(PointD center) => new()
    {
        Kind = ElementKind.Rectangle, IsBillboard = true,
        BillboardAnchor = center + new PointD(-75, 50), BillboardHeight = 20, Width = 30, Height = 32,
        Style = new() { Fill = 0xFF567ACC, StrokeWidth = 0 }
    };

    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, bool reference = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)scene.Viewport.Width, (int)scene.Viewport.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(-(float)scene.Viewport.X, -(float)scene.Viewport.Y);
        renderer.Draw(surface.Canvas, scene, assets, calibrationReference: reference);
        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }

    private static bool Same(SKBitmap a, SKBitmap b) => a.Width == b.Width && a.Height == b.Height && a.Bytes.SequenceEqual(b.Bytes);
    private sealed record Fixture(string Name, RectD Reference, RectD Viewport, double CellSize);
}
