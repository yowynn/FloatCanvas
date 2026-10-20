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
                var element = new CanvasElement { Kind = ElementKind.Image, Width = width, Height = height,
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
