using System.IO;
using System.Windows.Media.Imaging;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private static Task<byte[]?> ReadClipboardImageAsync(Wpf.IDataObject data, CancellationToken ct)
    {
        // Acquire clipboard/OLE objects on the UI apartment; bounded copying and encoding do
        // not need to keep the dispatcher occupied. Freeze WPF pixels before handing them off.
        foreach (string format in new[] { "GIF", "image/gif", "WebP", "WEBP", "image/webp", "PNG" })
        {
            if (!data.GetDataPresent(format, autoConvert: false)) continue;
            object? value = data.GetData(format, autoConvert: false);
            if (value is not byte[] && value is not Stream) continue;
            return Task.Run<byte[]?>(() =>
            {
                ct.ThrowIfCancellationRequested();
                byte[] encoded = value is byte[] bytes ? bytes : ImageImportBudget.Read((Stream)value);
                ImageImportBudget.RequireBytes(encoded.LongLength);
                bool valid = format == "PNG" ? encoded.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) :
                    format is "GIF" or "image/gif" ? encoded.AsSpan().StartsWith("GIF87a"u8) || encoded.AsSpan().StartsWith("GIF89a"u8) :
                    encoded.Length >= 12 && encoded.AsSpan(0, 4).SequenceEqual("RIFF"u8) && encoded.AsSpan(8, 4).SequenceEqual("WEBP"u8);
                if (!valid) throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
                ct.ThrowIfCancellationRequested(); return encoded;
            }, ct);
        }
        BitmapSource? bitmap = data.GetData(Wpf.DataFormats.Bitmap, autoConvert: true) as BitmapSource;
        if (bitmap is null && Wpf.Clipboard.ContainsImage()) bitmap = Wpf.Clipboard.GetImage();
        if (bitmap is null) return Task.FromResult<byte[]?>(null);
        ImageDecodeBudget.ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
        if (!bitmap.IsFrozen) { bitmap = bitmap.CloneCurrentValue(); bitmap.Freeze(); }
        return Task.Run<byte[]?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); ImageImportBudget.RequireBytes(output.Length);
            ct.ThrowIfCancellationRequested(); return output.ToArray();
        }, ct);
    }
}
