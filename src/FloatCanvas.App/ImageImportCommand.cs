using L = FloatCanvas.Localization.AppText;
using System.IO;
using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

internal static class ImageImportCommand
{
    internal static Task<IReadOnlyList<NormalizedImage>> NormalizeFilesAsync(string[] paths, CancellationToken token) => Task.Run(async () =>
    {
        var images = new List<NormalizedImage>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            images.Add(AssetImport.Normalize(await File.ReadAllBytesAsync(path, token)));
        }
        token.ThrowIfCancellationRequested();
        return (IReadOnlyList<NormalizedImage>)images;
    }, token);

    internal static void Insert(EditorSession target, IReadOnlyList<NormalizedImage> images, RectD placement, bool replace = false, PointD? dropCenter = null)
    {
        var resources = images.Select(image => (Image: image, Id: target.Assets.Add(image.PngBytes, image.MediaType))).ToArray();
        target.Execute(replace ? L.Get("History.ReplaceImage") : L.Get("Image.ImportTitle"), document =>
        {
            if (replace)
            {
                target.SelectedElements().Single(e => e.Kind == ElementKind.Image && !e.Locked).AssetId = resources[0].Id;
                return;
            }
            target.Selection.Clear(); int index = 0;
            foreach (var resource in resources)
            {
                double scale = Math.Min(1, Math.Min(placement.Width * .8 / resource.Image.Width, placement.Height * .8 / resource.Image.Height));
                double width = resource.Image.Width * scale, height = resource.Image.Height * scale;
                var center = dropCenter ?? placement.Center;
                var element = new CanvasElement { Kind = ElementKind.Image, AssetId = resource.Id, Width = width, Height = height,
                    Transform = Affine2D.Translation(center.X - width / 2 + index * 20, center.Y - height / 2 + index * 20) };
                document.Elements.Add(element); target.Selection.Add(element.Id); index++;
            }
        });
    }

    /// <summary>Fresh image pixels remain encoded; screen placement is converted to shared logical units once.</summary>
    internal static void InsertBillboard(EditorSession target, IReadOnlyList<NormalizedImage> images, RectD screenPlacement,
        BillboardDepthLine depth, PointD screenCenter)
    {
        var mapping = CanvasMapping.Create(target.Document);
        var elements = new List<CanvasElement>();
        int index = 0;
        foreach (var image in images)
        {
            double fit = Math.Min(1, Math.Min(screenPlacement.Width * .8 / image.Width, screenPlacement.Height * .8 / image.Height));
            double screenWidth = image.Width * fit, screenHeight = image.Height * fit;
            var bounds = new RectD(screenCenter.X - screenWidth / 2 + index * 20,
                screenCenter.Y - screenHeight / 2 + index * 20, screenWidth, screenHeight);
            elements.Add(SnapshotImageCommand.CreateBillboardElement(target.Assets,
                new(image.PngBytes, bounds, true), mapping, depth));
            index++;
        }
        target.Execute(L.Get("Image.ImportTitle"), document =>
        {
            target.Selection.Clear();
            foreach (var element in elements) { document.Elements.Add(element); target.Selection.Add(element.Id); }
        });
    }
}
