using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using SkiaSharp;

internal static class LiveEffectStampTests
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        string profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FloatCanvas-live-effect-stamp-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(profile);
        try
        {
            var assets = new AssetLibrary(); string blue = assets.Add(Png(SKColors.Blue)), lime = assets.Add(Png(SKColors.Lime));
            var mask = new CanvasElement { Kind = ElementKind.LivePixelate, Width = 80, Height = 60, Transform = Affine2D.Translation(20, 20),
                Style = new() { StrokeWidth = 0 }, Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(0, 0, 1, 1), Amount = 20 }] };
            var below = new CanvasElement { Kind = ElementKind.Image, AssetId = lime, Width = 40, Height = 60, Transform = Affine2D.Translation(20, 20) };
            var above = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 60, Transform = Affine2D.Translation(20, 20), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
            var document = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 160, 120),
                Background = new() { Kind = ElementKind.Image, AssetId = blue, Width = 160, Height = 120 }, Elements = [below, mask, above] };
            var session = new EditorSession(document, assets); session.SetSelection([mask.Id]);
            string original = JsonSerializer.Serialize(document);
            var library = new StampLibrary(profile); await library.LoadAsync();
            var context = new StampRenderContext(document.Clone(), assets);
            var entry = await library.SaveSelectionAsync(session, "Frozen-source live mask", StampSaveMode.RasterMerge, renderContext: context);
            var merged = await library.LoadStampAsync(entry.Id);
            check("a merged live-region stamp becomes one portable image at its original region dimensions", merged.Document.Elements is [{ Kind: ElementKind.Image, Width: 80, Height: 60 }] && merged.Assets.Count == 1);
            using (var pixels = SKBitmap.Decode(merged.Assets.Get(merged.Document.Elements[0].AssetId!).Data.ToArray()))
                check("merged live-region pixels include the lower image and frozen background while excluding upper objects", pixels.GetPixel(pixels.Width / 4, pixels.Height / 2) == SKColors.Lime && pixels.GetPixel(pixels.Width * 3 / 4, pixels.Height / 2) == SKColors.Blue);
            using (var thumbnail = SKBitmap.Decode(await library.GetThumbnailAsync(entry.Id)))
                check("live-region stamp thumbnails use the same full-scene context", thumbnail.GetPixel(thumbnail.Width / 4, thumbnail.Height / 2) == SKColors.Lime && thumbnail.GetPixel(thumbnail.Width * 3 / 4, thumbnail.Height / 2) == SKColors.Blue);
            var source = await library.LoadSourceAsync(entry.Id);
            check("a merged stamp retains an independently editable live-region source", source.Document.Elements is [{ Kind: ElementKind.LivePixelate }] && source.Document.Elements[0].Effects.Single().Amount == 20);
            check("the editable source excludes both frozen desktop and unselected image resources", source.Assets.Count == 0 && source.Document.Background is null && !source.Document.ReferencedAssets.Any());
            check("rendering a live-region stamp does not mutate the source canvas or its background", JsonSerializer.Serialize(document) == original && session.Selection.SetEquals([mask.Id]) && assets.Count == 2);
            var vectorEntry = await library.SaveSelectionAsync(session, "Editable live region", renderContext: context);
            var hover = await StampPreviewTooltip.GetPreviewAsync(library, vectorEntry);
            byte[] hoverPixels = new byte[hover!.PixelWidth * hover.PixelHeight * 4];
            hover.CopyPixels(hoverPixels, hover.PixelWidth * 4, 0);
            int left = ((hover.PixelHeight / 2) * hover.PixelWidth + hover.PixelWidth / 4) * 4;
            int right = ((hover.PixelHeight / 2) * hover.PixelWidth + hover.PixelWidth * 3 / 4) * 4;
            check("editable live-region hover uses its saved full-scene appearance instead of a gray source render",
                hoverPixels[left + 1] > 250 && hoverPixels[left + 2] < 5 && hoverPixels[right] > 250 && hoverPixels[right + 1] < 5);
            var vector = await library.LoadStampAsync(vectorEntry.Id);
            var target = new EditorSession(); StampLibrary.Place(vector, target, new(300, 200));
            check("editable live-region stamps remain dynamic objects when placed on another canvas", target.Document.Elements is [{ Kind: ElementKind.LivePixelate }] && target.Assets.Count == 0 && target.UndoCount == 1);
            check("live-region stamp placement and undo do not introduce screenshot resources", target.Undo() && target.Document.Elements.Count == 0 && target.Assets.Count == 0);

            var gradientAssets = new AssetLibrary(); string gradient = gradientAssets.Add(GradientPng());
            var gradientMask = new CanvasElement { Kind = ElementKind.LivePixelate, Width = 320, Height = 160,
                Style = new() { StrokeWidth = 0 }, Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(0, 0, 1, 1), Amount = 20 }] };
            var gradientDocument = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 320, 160),
                Background = new() { Kind = ElementKind.Image, AssetId = gradient, Width = 320, Height = 160 }, Elements = [gradientMask] };
            var gradientSession = new EditorSession(gradientDocument, gradientAssets); gradientSession.SetSelection([gradientMask.Id]);
            string gradientBefore = JsonSerializer.Serialize(gradientDocument);
            var gradientEntry = await library.SaveSelectionAsync(gradientSession, "Proportional live blocks", StampSaveMode.RasterMerge,
                renderContext: new(gradientDocument.Clone(), gradientAssets));
            var gradientMerged = await library.LoadStampAsync(gradientEntry.Id);
            using var gradientPreview = SKBitmap.Decode(await library.GetThumbnailAsync(gradientEntry.Id));
            using var gradientRaster = SKBitmap.Decode(gradientMerged.Assets.Get(gradientMerged.Document.Elements.Single().AssetId!).Data.ToArray());
            check("live block proportions match between a half-size thumbnail and a double-size raster stamp",
                gradientPreview.Width == 160 && gradientRaster.Width == 640 && Runs(gradientPreview) == 16 && Runs(gradientRaster) == 16 &&
                Enumerable.Range(0, 16).All(block => gradientPreview.GetPixel(block * 10 + 5, 40) == gradientRaster.GetPixel(block * 40 + 20, 160)));
            var gradientSource = await library.LoadSourceAsync(gradientEntry.Id);
            check("scaling a live stamp samples cloned effects and leaves its editable source strength unchanged",
                JsonSerializer.Serialize(gradientDocument) == gradientBefore && gradientMask.Effects.Single().Amount == 20 && gradientSource.Document.Elements.Single().Effects.Single().Amount == 20);
        }
        finally
        {
            string temporary = Path.GetFullPath(Path.GetTempPath());
            if (!profile.StartsWith(Path.Combine(temporary, "FloatCanvas-live-effect-stamp-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected stamp fixture path");
            Directory.Delete(profile, recursive: true);
        }
    }
    private static byte[] Png(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(16, 16)); surface.Canvas.Clear(color);
        using var image = surface.Snapshot(); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); return bytes.ToArray();
    }
    private static byte[] GradientPng()
    {
        using var bitmap = new SKBitmap(320, 160);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte value = (byte)(x * 255 / (bitmap.Width - 1)); bitmap.SetPixel(x, y, new(value, value, value));
            }
        using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); return bytes.ToArray();
    }
    private static int Runs(SKBitmap bitmap)
    {
        int runs = 1, y = bitmap.Height / 2;
        for (int x = 1; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y) != bitmap.GetPixel(x - 1, y)) runs++;
        return runs;
    }
}
