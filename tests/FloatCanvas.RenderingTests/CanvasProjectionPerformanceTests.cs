using System.Diagnostics;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class CanvasProjectionPerformanceTests
{
    internal static void Run(Action<string, bool> check)
    {
        const int width = 3840, height = 2160, frames = 4;
        var assets = new AssetLibrary();
        string gif = assets.Add(Convert.FromBase64String("R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs="), "image/gif");
        var scene = new SceneDocument { Viewport = new(0, 0, width, height) };
        for (int i = 0; i < 60; i++) scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 120, Height = 80, Transform = Affine2D.Translation((i % 12) * 280, (i / 12) * 320), Style = new() { Fill = 0x90F08030, StrokeWidth = 2 } });
        using var renderer = new SceneRenderer(); using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Measure("identity stable", false, false);
        var reference = CanvasCalibration.CreateInitial(scene.Viewport, 8, 6);
        check("4K fixture obtains stable nonidentity calibration", CanvasCalibration.TryCalibrate(reference,
            [new(100, 80), new(3500, 130), new(3650, 1980), new(180, 2050)], scene.Viewport, out var calibrated, out _));
        calibrated.Enabled = true; scene.Canvas.Projection = calibrated;
        check("4K calibration mapping is nonidentity", scene.Canvas.Projection.Matrix.TransformPoint(new(1500, 900)).DistanceTo(new(1500, 900)) > 1);
        Measure("projection stable", false, false);
        Measure("projection calibration changing", true, false);
        scene.Canvas.Projection = calibrated.Clone();
        scene.Elements.Insert(20, new() { Kind = ElementKind.Image, AssetId = gif, Width = 500, Height = 300, Transform = Affine2D.Translation(800, 500) });
        scene.Elements.Insert(30, new() { Kind = ElementKind.LivePixelate, Width = 450, Height = 300, Transform = Affine2D.Translation(1500, 600), Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 12 }] });
        using var backdropSurface = SKSurface.Create(new SKImageInfo(width, height)); backdropSurface.Canvas.Clear(SKColors.Blue);
        using var snapshot = backdropSurface.Snapshot(); using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var backdrop = DesktopBackdrop.FromEncoded(scene.Viewport, data.ToArray()); renderer.LiveBackdrop = backdrop;
        Measure("projection GIF and live filter", false, true);
        void Measure(string label, bool changing, bool dynamic)
        {
            long revision = dynamic ? 2 : 1;
            Draw(0); long built = cache.BuildCount; long bytes = cache.CachedRasterBytes;
            var timer = Stopwatch.StartNew();
            var initialMatrix = scene.Canvas.Projection.Matrix;
            for (int i = 0; i < frames; i++) { if (changing) scene.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(i + 1, 0)).Compose(initialMatrix); Draw(i + 1); }
            timer.Stop();
            check(label + " completes bounded 4K frames", timer.Elapsed < TimeSpan.FromSeconds(30));
            check(label + " retains bounded raster cache", cache.CachedRasterBytes <= 3L * width * height * 4);
            if (!changing && !dynamic) check(label + " reuses stable tiles", cache.BuildCount == built);
            if (changing) check(label + " invalidates preview matrix changes", cache.BuildCount > built);
            Console.WriteLine($"PERF raster {label}: {frames} frames {timer.Elapsed.TotalMilliseconds:F2} ms; builds={cache.BuildCount - built}; bytes={cache.CachedRasterBytes}; warmBytes={bytes}");
            void Draw(int frame) { renderer.AnimationTime = TimeSpan.FromMilliseconds(frame * 100); surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, revision, 0); surface.Canvas.Flush(); }
        }
    }
}
