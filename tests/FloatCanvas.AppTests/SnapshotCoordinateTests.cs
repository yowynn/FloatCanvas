using FloatCanvas.App;
using FloatCanvas.Core;
using SkiaSharp;

internal static class SnapshotCoordinateTests
{
    internal static void Run(Action<string, bool> check)
    {
        RectD[] displays = [new(-150, -40, 120, 140), new(20, -40, 140, 140)];
        var selection = new CaptureSelection(CaptureShape.Ellipse, CaptureSource.CanvasOnly,
            new(-80.2, -10.4, 160.7, 70.6), [], 0, 0xFFE03E3E);
        var expected = new RectD(-81, -11, 162, 72);
        byte[]? first = null;
        foreach (var (leftDpi, rightDpi) in new[] { (96, 120), (120, 144), (144, 192), (168, 216), (192, 96), (216, 288) })
        {
            string label = $"synthetic {leftDpi}/{rightDpi} DPI ";
            var scene = new SceneDocument { Viewport = new(-150, -40, 310, 140), Elements =
                [new() { Kind = ElementKind.Rectangle, Width = 310, Height = 140, Transform = Affine2D.Translation(-150, -40), Style = new() { Fill = 0x8000FFFF, StrokeWidth = 0 } }],
                DisplayLayout = displays.Select((bounds, i) => new DisplayLayoutHint { DeviceName = "synthetic-" + i, Bounds = bounds,
                    DpiX = i == 0 ? leftDpi : rightDpi, DpiY = i == 0 ? leftDpi : rightDpi }).ToList() };
            var raster = SnapshotImageCommand.Render(scene, [], selection, null, displays);
            using var pixels = SKBitmap.Decode(raster.PngBytes);
            check(label + "keeps negative physical bounds and identical pixels", raster.Bounds == expected && pixels.Width == 162 && pixels.Height == 72 &&
                (first is null || first.AsSpan().SequenceEqual(raster.PngBytes)));
            first ??= raster.PngBytes;
            check(label + "retains transparent physical display gaps", pixels.GetPixel(11, 31).Alpha is >= 127 and <= 128 &&
                pixels.GetPixel(71, 31).Alpha == 0 && pixels.GetPixel(131, 31).Alpha is >= 127 and <= 128);
            var session = new EditorSession(scene);
            var image = SnapshotImageCommand.Insert(session, raster);
            check(label + "inserts the ordinary image at one-to-one physical size", image.Width == 162 && image.Height == 72 &&
                image.Transform == Affine2D.Translation(-81, -11) && session.UndoCount == 1);
        }
    }
}
