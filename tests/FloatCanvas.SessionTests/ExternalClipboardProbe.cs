using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Imaging;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using Wpf = System.Windows;

namespace FloatCanvas.SessionTests;

internal static partial class Program
{
    private static bool ExternalClipboardProbe, ExternalClipboardRestored;
    private static readonly List<object> ExternalClipboardCases = [];

    // Called only inside the existing production clipboard test's snapshot/restore scope.
    // It neither creates a file dialog nor injects a global keyboard or pointer event.
    private static async Task VerifyExternalClipboardPaste(object session, EditorSession live)
    {
        byte[] png = FixturePng();
        string fixturePath = Path.Combine(Profile!, "external-clipboard-alpha.png");
        await File.WriteAllBytesAsync(fixturePath, png);
        object controller = Field<object>(session, "_editor");
        Type controllerType = controller.GetType();
        Type toolType = controllerType.GetProperty("Tool")!.PropertyType;
        MethodInfo pointer = controllerType.GetMethod("Pointer")!;
        var assets = Field<AssetLibrary>(session, "_assets");
        foreach (string format in new[] { "PNG", "Bitmap", "FileDrop" })
        {
            var data = new Wpf.DataObject();
            using var pngStream = new MemoryStream(png, false);
            if (format == "PNG") data.SetData("PNG", pngStream, false);
            else if (format == "Bitmap")
            {
                var bitmap = BitmapFrame.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                bitmap.Freeze(); data.SetData(Wpf.DataFormats.Bitmap, bitmap, false);
            }
            else data.SetData(Wpf.DataFormats.FileDrop, new[] { fixturePath }, false);
            Wpf.Clipboard.SetDataObject(data, true);
            var exchanged = Wpf.Clipboard.GetDataObject() ?? throw new InvalidOperationException("External clipboard payload did not survive the exchange.");
            string[] storedFormats = exchanged.GetFormats(false);
            Check(!exchanged.GetDataPresent("FloatCanvas.Objects.v1", false), format + " fixture contains no private editable-object format");
            bool hasPng = exchanged.GetDataPresent("PNG", false);
            bool hasBitmap = exchanged.GetDataPresent(Wpf.DataFormats.Bitmap, true) || Wpf.Clipboard.ContainsImage();
            bool hasFileDrop = exchanged.GetDataPresent(Wpf.DataFormats.FileDrop, false);
            Check(format switch
            {
                "PNG" => hasPng,
                "Bitmap" => !hasPng && hasBitmap,
                _ => !hasPng && !hasBitmap && hasFileDrop
            }, format + " fixture preserves the actual production branch prerequisites after clipboard exchange");

            byte? exchangedCornerAlpha = null;
            SKColor? exchangedCenter = null;
            if (format == "Bitmap")
            {
                var bitmap = exchanged.GetData(Wpf.DataFormats.Bitmap, true) as BitmapSource ?? Wpf.Clipboard.GetImage()
                    ?? throw new InvalidOperationException("The exchanged Bitmap format is not readable.");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var encoded = new MemoryStream(); encoder.Save(encoded);
                using var pixels = SKBitmap.Decode(encoded.ToArray());
                Check(pixels is { Width: 240, Height: 160 }, "Bitmap exchange preserves image dimensions");
                exchangedCornerAlpha = pixels.GetPixel(0, 0).Alpha;
                exchangedCenter = pixels.GetPixel(120, 80);
            }

            string before = JsonSerializer.Serialize(live.Document);
            int countBefore = live.Document.Elements.Count, undoBefore = live.UndoCount;
            var previousIds = live.Document.Elements.Select(e => e.Id).ToHashSet();
            Call(session, "SelectTool", Enum.Parse(toolType, "Freehand"));
            Check(controllerType.GetProperty("Tool")!.GetValue(controller)!.ToString() == "Freehand", format + " starts from a drawing tool rather than selection");
            await CallAsync(session, "PasteAsync");
            var added = live.Document.Elements.Where(e => !previousIds.Contains(e.Id)).ToArray();
            Check(live.Document.Elements.Count == countBefore + 1 && added is [{ Kind: ElementKind.Image }] && live.Selection.SetEquals([added[0].Id]),
                format + " production paste creates exactly one new selected editable image");
            var image = added[0];
            string pastedTool = controllerType.GetProperty("Tool")!.GetValue(controller)!.ToString()!;
            using (var pixels = SKBitmap.Decode(assets.Get(image.AssetId!).Data.Span))
            {
                Check(pixels is { Width: 240, Height: 160 }, format + " production paste keeps image dimensions");
                if (format == "Bitmap")
                    Check(pixels.GetPixel(0, 0).Alpha == exchangedCornerAlpha && pixels.GetPixel(120, 80) == exchangedCenter,
                        "Bitmap paste preserves the actually exchanged alpha/pixels rather than assuming Windows retained original PNG bytes");
                else Check(pixels.GetPixel(0, 0).Alpha == 0 && pixels.GetPixel(120, 80).Alpha > 0,
                    format + " paste retains the source PNG's actual transparent corner");
            }
            ExternalClipboardCases.Add(new { Format = format, StoredFormats = storedFormats, NewImageId = image.Id, image.AssetId,
                ToolAfterPaste = pastedTool, ExchangedBitmapCornerAlpha = exchangedCornerAlpha,
                UndoBefore = undoBefore, UndoAfterPaste = live.UndoCount });
            Check(pastedTool == "Select", format + " production paste switches from drawing to selection for immediate object editing");
            Check(live.UndoCount == undoBefore + 1, format + " paste is exactly one undo operation");
            string pasted = JsonSerializer.Serialize(live.Document);
            var originalTransform = image.Transform;
            var center = image.Transform.TransformPoint(new(image.Width / 2, image.Height / 2));
            var movedCenter = center + new PointD(35, 20);
            foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, center), (OverlayPointerKind.Move, movedCenter), (OverlayPointerKind.Up, movedCenter) })
                pointer.Invoke(controller, [new OverlayPointerEvent(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None)]);
            var moved = live.Document.Elements.Single(e => e.Id == image.Id);
            Check(live.Document.Elements.Count == countBefore + 1 && live.UndoCount == undoBefore + 2 &&
                moved.Transform == Affine2D.Translation(35, 20).Compose(originalTransform), format + " immediately moves the pasted image through the actual controller instead of drawing a stroke");
            Check(live.Undo() && JsonSerializer.Serialize(live.Document) == pasted, format + " one undo restores the pasted image before movement");
            Check(live.Undo() && live.UndoCount == undoBefore && JsonSerializer.Serialize(live.Document) == before,
                format + " second undo removes only the paste and restores the exact preceding document");
        }
    }
}
