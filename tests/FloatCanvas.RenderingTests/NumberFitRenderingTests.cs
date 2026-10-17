using System.Globalization;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class NumberFitRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        // The real regression was 103 wrapping into "10" and "3" in a small badge.
        // Count the separate digit columns in exported pixels, as well as their bounds:
        // a clip-only workaround could hide overflow but cannot retain every digit.
        // Inspect the same local geometry at 4x output resolution so subpixel spacing
        // between small digits does not merge their projected columns at 1x.
        foreach (var sample in new[]
        {
            new Sample(103, 32, 32, 32),
            new Sample(104, 24, 24, 32),
            new Sample(9999, 48, 48, 80),
            new Sample(123456, 72, 72, 96),
            new Sample(int.MaxValue, 128, 128, 160),
            new Sample(103, 64, 18, 64),
            new Sample(808, 90, 14, 256),
            new Sample(27, 60, 60, 30),
            new Sample(8, 32, 32, 32),
            new Sample(103, 32, 32, 6)
        })
        {
            const int margin = 12;
            const int scale = 4;
            var badge = new CanvasElement
            {
                Kind = ElementKind.Number,
                Number = sample.Number,
                Width = sample.Width,
                Height = sample.Height,
                Transform = Affine2D.Translation(margin * scale, margin * scale).Compose(Affine2D.Scale(scale, scale)),
                Style = new() { Stroke = 0xFFE03E3E, FontFamily = "Microsoft YaHei UI", FontSize = sample.FontSize }
            };
            string original = JsonSerializer.Serialize(badge);
            var scene = new SceneDocument
            {
                Viewport = new(0, 0, (sample.Width + 2 * margin) * scale, (sample.Height + 2 * margin) * scale),
                Elements = [badge]
            };
            using var image = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
            var ink = WhitePixels(image);
            string name = $"number {sample.Number}, {sample.Width}x{sample.Height}, font {sample.FontSize}";
            int digitCount = sample.Number.ToString(CultureInfo.InvariantCulture).Length;
            int columns = CountRuns(ink.Select(p => p.X));
            check(name + " exports every digit side by side on one line", ink.Count > 0 && columns == digitCount);
            check(name + " keeps white text inside the ellipse with vertical padding", ink.Count > 0 && ink.All(p =>
            {
                double localX = (p.X + .5) / scale - margin, localY = (p.Y + .5) / scale - margin;
                double x = (localX - sample.Width / 2d) / (sample.Width / 2d);
                double y = (localY - sample.Height / 2d) / (sample.Height / 2d);
                return x * x + y * y < .96 && localY >= 1 && localY < sample.Height - 1;
            }));
            check(name + " rendering leaves editable size, number and preferred font unchanged", original == JsonSerializer.Serialize(badge));
            if (sample.FontSize == 6)
                check("an explicitly small number font is not enlarged to fill its badge", ink.Max(p => p.Y) - ink.Min(p => p.Y) < 6 * scale);

            if (sample is { Number: 103, Width: 32, FontSize: 32 })
            {
                badge.Transform = Affine2D.Translation(margin, margin);
                scene.Viewport = new(0, 0, sample.Width + 2 * margin, sample.Height + 2 * margin);
                using var nativeImage = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
                var nativeInk = WhitePixels(nativeImage);
                check("the reported 103 regression is also a complete single line at native 1x output", CountRuns(nativeInk.Select(p => p.X)) == 3);
                check("native 1x number ink keeps top and bottom padding", nativeInk.Count > 0 && nativeInk.All(p => p.Y >= margin + 1 && p.Y < margin + sample.Height - 1));
            }
        }

        var textStyle = new ElementStyle { FontFamily = "Microsoft YaHei UI", FontSize = 24 };
        check("ordinary text retains width-based wrapping", renderer.MeasureText("1234567890", textStyle, 30).LineCount > 1);
    }

    private static List<SKPointI> WhitePixels(SKBitmap image)
    {
        var pixels = new List<SKPointI>();
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                if (color.Alpha > 180 && color.Red > 180 && color.Green > 180 && color.Blue > 180) pixels.Add(new(x, y));
            }
        return pixels;
    }

    private static int CountRuns(IEnumerable<int> coordinates)
    {
        int previous = int.MinValue, runs = 0;
        foreach (int value in coordinates.Distinct().Order())
        {
            if (value != previous + 1) runs++;
            previous = value;
        }
        return runs;
    }

    private readonly record struct Sample(int Number, int Width, int Height, double FontSize);
}
