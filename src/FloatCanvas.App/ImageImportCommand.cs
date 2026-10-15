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
        var resources = images.Select(image => (Image: image, Id: target.Assets.Add(image.PngBytes))).ToArray();
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
}
