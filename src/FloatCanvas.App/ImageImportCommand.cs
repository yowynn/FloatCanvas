using L = FloatCanvas.Localization.AppText;
using System.IO;
using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

internal static class ImageImportCommand
{
    internal static Task<IReadOnlyList<NormalizedImage>> NormalizeFilesAsync(string[] paths, CancellationToken token) => Task.Run(async () =>
    {
        if (paths.Length > ImageImportBudget.MaximumFiles) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
        long encodedTotal = 0;
        foreach (var path in paths)
        {
            long length = new FileInfo(path).Length; ImageImportBudget.RequireBytes(length);
            encodedTotal += length; ImageImportBudget.RequireBytes(encodedTotal, ImageImportBudget.MaximumBatchBytes);
        }
        long normalizedTotal = 0, pixelTotal = 0;
        var images = new List<NormalizedImage>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            byte[] encoded = await ImageImportBudget.ReadAsync(input, token);
            pixelTotal = checked(pixelTotal + ImageDecodeBudget.ReadPixelCount(encoded));
            if (pixelTotal > ImageDecodeBudget.MaximumBatchPixels) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
            var image = AssetImport.Normalize(encoded);
            normalizedTotal += image.PngBytes.LongLength;
            ImageImportBudget.RequireBytes(normalizedTotal, ImageImportBudget.MaximumBatchBytes);
            images.Add(image);
        }
        token.ThrowIfCancellationRequested();
        return (IReadOnlyList<NormalizedImage>)images;
    }, token);

    internal static void Insert(EditorSession target, IReadOnlyList<NormalizedImage> images, RectD placement, bool replace = false, PointD? dropCenter = null, double opacity = 1)
    {
        ValidateOpacity(opacity);
        var elements = new List<CanvasElement>();
        if (!replace)
        {
            var mapping = CanvasMapping.Create(target.Document);
            int index = 0;
            foreach (var image in images)
            {
                double scale = Math.Min(1, Math.Min(placement.Width * .8 / image.Width, placement.Height * .8 / image.Height));
                double width = image.Width * scale, height = image.Height * scale;
                var center = dropCenter ?? placement.Center;
                var element = new CanvasElement { Kind = ElementKind.Image, Width = width, Height = height, Style = new() { Opacity = opacity },
                    Transform = Affine2D.Translation(center.X - width / 2 + index * 20, center.Y - height / 2 + index * 20) };
                if (!mapping.IsElementSafe(element)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
                elements.Add(element); index++;
            }
        }
        var resources = images.Select(image => (Image: image, Id: target.Assets.Add(image.PngBytes, image.MediaType))).ToArray();
        target.Execute(replace ? L.Get("History.ReplaceImage") : L.Get("Image.ImportTitle"), document =>
        {
            if (replace)
            {
                target.SelectedElements().Single(e => e.Kind == ElementKind.Image && !e.Locked).AssetId = resources[0].Id;
                return;
            }
            target.Selection.Clear();
            for (int index = 0; index < elements.Count; index++)
            {
                var element = elements[index]; element.AssetId = resources[index].Id;
                document.Elements.Add(element); target.Selection.Add(element.Id);
            }
        });
    }

    /// <summary>Fresh image pixels remain encoded; screen placement is converted to shared logical units once.</summary>
    internal static void InsertBillboard(EditorSession target, IReadOnlyList<NormalizedImage> images, RectD screenPlacement,
        BillboardDepthLine depth, PointD screenCenter, double opacity = 1)
    {
        ValidateOpacity(opacity);
        var mapping = CanvasMapping.Create(target.Document);
        var elements = new List<CanvasElement>();
        int index = 0;
        foreach (var image in images)
        {
            double fit = Math.Min(1, Math.Min(screenPlacement.Width * .8 / image.Width, screenPlacement.Height * .8 / image.Height));
            double screenWidth = image.Width * fit, screenHeight = image.Height * fit;
            var bounds = new RectD(screenCenter.X - screenWidth / 2 + index * 20,
                screenCenter.Y - screenHeight / 2 + index * 20, screenWidth, screenHeight);
            var element = SnapshotImageCommand.CreateBillboardElement(target.Assets,
                new(image.PngBytes, bounds, true), mapping, depth);
            element.Style.Opacity = opacity;
            elements.Add(element);
            index++;
        }
        target.Execute(L.Get("Image.ImportTitle"), document =>
        {
            target.Selection.Clear();
            foreach (var element in elements) { document.Elements.Add(element); target.Selection.Add(element.Id); }
        });
    }

    private static void ValidateOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
    }
}
