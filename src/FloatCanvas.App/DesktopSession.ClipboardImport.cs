using System.IO;
using System.Windows.Media;
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
        BitmapSource? bitmap = ReadClipboardBitmap(data);
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

    private static BitmapSource? ReadClipboardBitmap(Wpf.IDataObject data)
    {
        // Prefer an original legacy DIB to WPF's CF_BITMAP conversion. Photoshop's
        // 24-bit DIB can become Bgra32 with undefined alpha, then encode as invisible.
        // An explicitly advertised DIBV5 may carry real alpha; keep that path intact.
        string dibV5 = Wpf.DataFormats.GetDataFormat(17).Name;
        if (!data.GetFormats(autoConvert: false).Contains(dibV5) && data.GetDataPresent(Wpf.DataFormats.Dib, autoConvert: false))
        {
            object? value = data.GetData(Wpf.DataFormats.Dib, autoConvert: false);
            byte[]? dib = value is Stream stream ? ImageImportBudget.Read(stream) : value as byte[];
            if (dib is not null)
            {
                ImageImportBudget.RequireBytes(dib.LongLength);
                if (dib.Length >= 40 && BitConverter.ToUInt32(dib, 0) == 40 &&
                    BitConverter.ToUInt32(dib, 16) == 0 && BitConverter.ToUInt16(dib, 14) is 24 or 32)
                {
                    int width = BitConverter.ToInt32(dib, 4), signedHeight = BitConverter.ToInt32(dib, 8);
                    if (signedHeight == int.MinValue || BitConverter.ToUInt16(dib, 12) != 1)
                        throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
                    int height = Math.Abs(signedHeight), bits = BitConverter.ToUInt16(dib, 14);
                    ImageDecodeBudget.ValidateDimensions(width, height);
                    int stride = checked((int)(((long)width * bits + 31) / 32 * 4));
                    long offset = 40L + BitConverter.ToUInt32(dib, 32) * 4L;
                    long length = (long)stride * height;
                    if (offset + length > dib.LongLength)
                        throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
                    byte[] pixels = new byte[checked((int)length)];
                    for (int y = 0; y < height; y++)
                    {
                        int sourceRow = signedHeight < 0 ? y : height - 1 - y;
                        Buffer.BlockCopy(dib, checked((int)(offset + sourceRow * (long)stride)), pixels, y * stride, stride);
                    }
                    // BI_RGB has BGR pixels; the fourth byte in 32-bit DIBs is reserved.
                    var source = BitmapSource.Create(width, height, 96, 96,
                        bits == 24 ? PixelFormats.Bgr24 : PixelFormats.Bgr32, null, pixels, stride);
                    source.Freeze();
                    return source;
                }
            }
        }
        BitmapSource? bitmap = data.GetData(Wpf.DataFormats.Bitmap, autoConvert: true) as BitmapSource;
        if (bitmap is null && Wpf.Clipboard.ContainsImage()) bitmap = Wpf.Clipboard.GetImage();
        return bitmap;
    }
}
