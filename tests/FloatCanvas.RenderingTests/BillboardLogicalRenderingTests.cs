using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class BillboardLogicalRenderingTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = new SceneDocument { Viewport = new(0, 0, 800, 700) };
        scene.Canvas.Projection = new() { Enabled = true, Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1),
            Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)] };
        scene.Canvas.Grid.Visible = false; scene.Canvas.BackgroundColor = 0;
        var map = CanvasMapping.Create(scene);
        var source = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100,
            Style = new() { Fill = 0, Stroke = 0xFF308CE8, StrokeWidth = 4 } };
        foreach (bool drag in new[] { false, true })
        {
            CanvasElement Place(double y)
            {
                CanvasElement item;
                bool ok = drag ? BillboardCreationGeometry.TryFromScreenContour(source, map,
                    Projective2D.FromAffine(Affine2D.Translation(220, y - 50)), out item, out _)
                    : BillboardCreationGeometry.TryPlaceLogical(source, map, new(80, 50), new(300, y), out item, out _);
                if (!ok) throw new InvalidOperationException("Unsafe render fixture");
                return item;
            }
            var far = Place(350); var near = Place(600);
            byte[] Pixels(CanvasElement item)
            {
                scene.Elements = [item]; return renderer.ExportPng(scene, assets, true);
            }
            double StrokePixels(CanvasElement item)
            {
                using var pixels = SKBitmap.Decode(Pixels(item));
                var bounds = map.ProjectedBounds(item, false); int row = (int)Math.Round(bounds.Center.Y);
                // Integrated alpha measures actual antialiased stroke coverage, not a parameter value.
                return Enumerable.Range(Math.Max(0, (int)Math.Floor(bounds.Left) - 2), 12).Sum(x => pixels.GetPixel(x, row).Alpha / 255d);
            }
            double farStroke = StrokePixels(far), nearStroke = StrokePixels(near);
            map.TryGetDepthScale(far.BillboardAnchor, out double fs); map.TryGetDepthScale(near.BillboardAnchor, out double ns);
            check((drag ? "drag" : "click") + " raster stroke follows actual depth", nearStroke > farStroke + .5 && Math.Abs(farStroke - 4 * fs) < .15 && Math.Abs(nearStroke - 4 * ns) < .15);
            var moved = far.Clone(); moved.BillboardAnchor = near.BillboardAnchor;
            check((drag ? "drag" : "click") + " moved stroke has matching pixel coverage at equal depth", Math.Abs(StrokePixels(moved) - nearStroke) < .15);
            if (!drag)
            {
                using var a = SKBitmap.Decode(Pixels(moved)); using var b = SKBitmap.Decode(Pixels(near));
                check("equal logical preset at equal depth produces identical raster pixels", a.Pixels.SequenceEqual(b.Pixels));
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    scene.Elements = [far, near]; File.WriteAllBytes(Path.Combine(output, "logical-far-near.png"), renderer.ExportPng(scene, assets, true));
                    File.WriteAllBytes(Path.Combine(output, "moved-equal-depth.png"), Pixels(moved));
                }
            }
            Console.WriteLine($"BILLBOARD_PIXELS mode={(drag ? "drag" : "click")} far={farStroke:F4} near={nearStroke:F4} expected={4 * fs:F4}/{4 * ns:F4}");
        }
    }
}
