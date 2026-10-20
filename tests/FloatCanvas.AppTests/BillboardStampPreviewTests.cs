using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

/// <summary>Actual saved stamp and hover pixels, without a tooltip/native window or user library.</summary>
internal static class BillboardStampPreviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        var report = check; var failures = new List<string>();
        check = (name, passed) =>
        {
            if (passed) report(name, true);
            else { failures.Add(name); Console.WriteLine("BILLBOARD STAMP PREVIEW FAIL: " + name); }
        };
        Task.Run(() => RunAsync(check)).GetAwaiter().GetResult();
        report("all billboard stamp preview coordinates and preservation assertions pass (" + failures.Count + " failures)", failures.Count == 0);
    }

    private static async Task RunAsync(Action<string, bool> check)
    {
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_BILLBOARD_STAMP_PREVIEW_TEST_DIR") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvas-BillboardStampPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var library = new StampLibrary(Path.Combine(output, "isolated-library-" + Guid.NewGuid().ToString("N")));
        await library.LoadAsync();
        var rows = new List<object>();
        foreach (bool perspective in new[] { false, true })
        foreach (string shape in perspective ? new[] { "mixed-positive" } : new[] { "surface", "billboard-positive", "billboard-negative", "mixed-positive", "mixed-negative", "billboard-large" })
        foreach (var mode in new[] { StampSaveMode.EditableGroup, StampSaveMode.RasterMerge })
        {
            string label = (perspective ? "perspective-" : "identity-") + shape + "-" + mode;
            var source = Scene(shape, perspective); source.SelectAll();
            string sourceBefore = JsonSerializer.Serialize(source.Document);
            var entry = await library.SaveSelectionAsync(source, label, mode);
            var loaded = await library.LoadStampAsync(entry.Id);
            string loadedBefore = JsonSerializer.Serialize(loaded.Document);
            byte[] thumbnail = await library.GetThumbnailAsync(entry.Id);
            var actual = await StampPreviewTooltip.GetPreviewAsync(library, entry);
            check(label + " produces a frozen real hover bitmap", actual is { IsFrozen: true });
            if (actual is null) continue;
            byte[] hover = Encode(actual), expected = ExpectedCanonical(loaded, entry);
            await File.WriteAllBytesAsync(Path.Combine(output, label + "-thumbnail.png"), thumbnail);
            await File.WriteAllBytesAsync(Path.Combine(output, label + "-hover.png"), hover);
            await File.WriteAllBytesAsync(Path.Combine(output, label + "-expected.png"), expected);
            using var actualPixels = SKBitmap.Decode(hover);
            using var expectedPixels = SKBitmap.Decode(expected);
            using var smallPixels = SKBitmap.Decode(thumbnail);
            check(label + " keeps the complete canonical output dimensions", actualPixels.Width == expectedPixels.Width && actualPixels.Height == expectedPixels.Height);
            var colors = shape == "surface" ? new[] { "blue" } : shape.StartsWith("billboard", StringComparison.Ordinal) ? new[] { "red" } : new[] { "blue", "red" };
            var colorRows = new List<object>();
            foreach (string color in colors)
            {
                var observed = Bounds(actualPixels, color); var wanted = Bounds(expectedPixels, color); var small = Bounds(smallPixels, color);
                bool close = Close(observed, wanted, 2) && wanted.Count > 0 && observed.Count > 0 && Math.Abs(observed.Count / (double)wanted.Count - 1) < .05;
                check(label + " keeps the complete " + color + " artwork at its canonical relative position", close);
                if (!perspective)
                    check(label + " agrees with its saved thumbnail for " + color + " visual placement",
                        NormalizedClose(observed, actualPixels, small, smallPixels));
                colorRows.Add(new { Color = color, Actual = observed, Expected = wanted, SavedThumbnail = small, Passed = close });
            }
            check(label + " preview leaves both the original canvas and loaded stamp model unchanged",
                JsonSerializer.Serialize(source.Document) == sourceBefore && JsonSerializer.Serialize(loaded.Document) == loadedBefore);
            var editable = await library.LoadSourceAsync(entry.Id);
            var face = editable.Document.Elements.FirstOrDefault(item => item.IsBillboard);
            check(label + " retains editable placement and signed elevation through saving",
                shape == "surface" ? face is null : face is not null && face.BillboardHeight == (shape.EndsWith("negative", StringComparison.Ordinal) ? -80 : 80));
            rows.Add(new { Label = label, Mode = mode.ToString(), Perspective = perspective, EntryWidth = entry.Width, EntryHeight = entry.Height,
                ActualWidth = actualPixels.Width, ActualHeight = actualPixels.Height, ThumbnailWidth = smallPixels.Width, ThumbnailHeight = smallPixels.Height,
                Colors = colorRows, Space = perspective ? "hover canonical normalized flat; saved thumbnail uses source screen projection" : "identity screen and canonical flat agree" });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "pixel-bounds.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("BILLBOARD_STAMP_PREVIEW_ARTIFACTS=" + Path.GetFullPath(output));
    }

    private static EditorSession Scene(string shape, bool perspective)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600), Canvas = new()
        {
            Projection = new() { Enabled = true, Matrix = perspective ? new(1, .08, 0, 0, 1, 0, 0, .001, 1) : Projective2D.Identity,
                Quad = [new(0, 0), new(800, 0), new(800, 600), new(0, 600)] }
        } };
        if (shape == "surface" || shape.StartsWith("mixed", StringComparison.Ordinal))
            document.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 80, Height = 80,
                Transform = Affine2D.Translation(80, 230), ProjectivePlacement = perspective ? new(1, .04, 0, 0, 1, 0, 0, .001, 1) : null,
                Style = new() { Fill = 0xFF2040FF, StrokeWidth = 0 } });
        if (shape != "surface")
            document.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = shape == "billboard-large" ? 640 : 80,
                Height = shape == "billboard-large" ? 400 : 80, IsBillboard = true,
                BillboardAnchor = shape == "billboard-large" ? new(400, 300) : new(300, 300), BillboardHeight = shape.EndsWith("negative", StringComparison.Ordinal) ? -80 : 80,
                Transform = perspective ? Affine2D.Rotation(17) : Affine2D.Identity, Style = new() { Fill = 0xFFFF2020, StrokeWidth = 0 } });
        return new(document);
    }

    private static byte[] ExpectedCanonical(LoadedProject stamp, StampEntry entry)
    {
        // Independent oracle: bake the existing local-to-flat visual map into ordinary
        // projective elements before fitting. This does not reproduce the preview's
        // anchor/elevation mutation algorithm or rely on the saved thumbnail's projection.
        double factor = 320 / Math.Max(stamp.Document.Viewport.Width, stamp.Document.Viewport.Height);
        var fit = Projective2D.FromAffine(Affine2D.Scale(factor, factor).Compose(Affine2D.Translation(-stamp.Document.Viewport.X, -stamp.Document.Viewport.Y)));
        var document = new SceneDocument { Viewport = new(0, 0, Math.Max(1, entry.Width * factor), Math.Max(1, entry.Height * factor)), LayerOpacity = stamp.Document.LayerOpacity };
        foreach (var source in stamp.Document.Elements)
        {
            var element = source.Clone();
            element.ProjectivePlacement = fit.Compose(BillboardGeometry.ElementToTarget(source, Projective2D.Identity));
            element.Transform = Affine2D.Identity; element.IsBillboard = false;
            document.Elements.Add(element);
        }
        using var renderer = new SceneRenderer();
        return renderer.ExportPng(document, stamp.Assets, transparent: true);
    }

    private readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom, int Count);
    private static PixelBounds Bounds(SKBitmap bitmap, string color)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1, count = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            bool matches = color == "red" ? pixel.Red > pixel.Blue + 80 && pixel.Red > pixel.Green + 80 : pixel.Blue > pixel.Red + 80 && pixel.Blue > pixel.Green + 80;
            if (pixel.Alpha < 200 || !matches) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); count++;
        }
        return new(left, top, right, bottom, count);
    }
    private static bool Close(PixelBounds actual, PixelBounds expected, double tolerance) => actual.Count > 0 && expected.Count > 0 &&
        Math.Abs(actual.Left - expected.Left) <= tolerance && Math.Abs(actual.Top - expected.Top) <= tolerance &&
        Math.Abs(actual.Right - expected.Right) <= tolerance && Math.Abs(actual.Bottom - expected.Bottom) <= tolerance;
    private static bool NormalizedClose(PixelBounds actual, SKBitmap actualBitmap, PixelBounds thumbnail, SKBitmap thumbnailBitmap)
    {
        if (actual.Count == 0 || thumbnail.Count == 0) return false;
        return Math.Abs(actual.Left - thumbnail.Left * actualBitmap.Width / (double)thumbnailBitmap.Width) <= 3 &&
            Math.Abs(actual.Top - thumbnail.Top * actualBitmap.Height / (double)thumbnailBitmap.Height) <= 3 &&
            Math.Abs(actual.Right + 1 - (thumbnail.Right + 1) * actualBitmap.Width / (double)thumbnailBitmap.Width) <= 3 &&
            Math.Abs(actual.Bottom + 1 - (thumbnail.Bottom + 1) * actualBitmap.Height / (double)thumbnailBitmap.Height) <= 3;
    }
    private static byte[] Encode(BitmapSource bitmap)
    {
        using var output = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output); return output.ToArray();
    }
}
