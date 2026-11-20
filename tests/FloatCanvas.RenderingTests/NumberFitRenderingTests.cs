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
        CheckBadgeInkAndRing(renderer, assets, check);
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
                Style = new() { FontFamily = "Microsoft YaHei UI", FontSize = sample.FontSize }
            };
            // These historical fitting cases use the pre-v12 badge colors and capped
            // font rule. Interpret their raw legacy values once before rendering.
            AppearanceMigration.MigrateElement(badge, new(0xFFE03E3E, 0, false, null), numberUsesForeground: false);
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

    private static void CheckBadgeInkAndRing(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        const int scale = 4;
        var badge = new CanvasElement { Kind = ElementKind.Number, Width = 72, Height = 72,
            NumberTextSizeMode = NumberTextSizeMode.FitBadge, Transform = Affine2D.Scale(scale, scale),
            Style = new() { Variant = ElementAppearance.NumberFilled, PrimaryColor = 0xFF304967, SecondaryColor = 0xFFFFFFFF } };
        var scene = new SceneDocument { Elements = [badge], Viewport = new(0, 0, 72 * scale, 72 * scale) };
        byte[] Draw() => renderer.ExportPng(scene, assets, true);
        foreach (var font in new[] { "Microsoft YaHei UI", "Segoe UI", "Arial", "Georgia", "Gabriola", "Bahnschrift" })
        foreach (var number in new[] { 1, 8, 17, -12, 103, int.MinValue })
        {
            badge.Style.FontFamily = font; badge.Number = number;
            using var pixels = SKBitmap.Decode(Draw());
            var ink = WhitePixels(pixels);
            double dx = (ink.Min(p => p.X) + ink.Max(p => p.X) + 1) / (2d * scale) - badge.Width / 2;
            double dy = (ink.Min(p => p.Y) + ink.Max(p => p.Y) + 1) / (2d * scale) - badge.Height / 2;
            check($"{font} {number} visible glyphs are centered (offset {dx:F2}, {dy:F2} px)", Math.Abs(dx) <= .75 && Math.Abs(dy) <= .75);
        }
        badge.Style.Variant = ElementAppearance.NumberOutline; badge.Style.FontFamily = "Microsoft YaHei UI";
        badge.Style.SecondaryColor = 0; badge.Number = -103;
        foreach (int diameter in new[] { 28, 56, 112 })
        {
            badge.Width = badge.Height = diameter; scene.Viewport = new(0, 0, diameter * scale, diameter * scale);
            badge.Style.StrokeWidth = 0;
            var reference = Draw();
            foreach (int oldWidth in new[] { 3, 16, 1024 })
            {
                badge.Style.StrokeWidth = oldWidth;
                check(diameter + " px number rim is independent of stored line width " + oldWidth, reference.SequenceEqual(Draw()));
            }
            using var pixels = SKBitmap.Decode(reference);
            int rim = 0;
            while (rim < pixels.Height && pixels.GetPixel(pixels.Width / 2, rim).Alpha >= 128) rim++;
            check(diameter + " px outlined badge uses a proportional inside ring", Math.Abs(rim / (double)scale - diameter * (3d / 56)) <= .5);
            // Between the inner rim and the fitted label there must be a continuous clear band.
            double innerRadius = (diameter / 2d - diameter * (3d / 56)) * scale;
            bool clearBand = true;
            for (int y = 0; y < pixels.Height; y++)
            for (int x = 0; x < pixels.Width; x++)
            {
                double radius = double.Hypot(x + .5 - pixels.Width / 2d, y + .5 - pixels.Height / 2d);
                if (radius > .925 * innerRadius && radius < innerRadius - 1 && pixels.GetPixel(x, y).Alpha >= 128) clearBand = false;
            }
            check(diameter + " px outline keeps even signed multi-digit ink clear of the ring", clearBand);
        }
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
