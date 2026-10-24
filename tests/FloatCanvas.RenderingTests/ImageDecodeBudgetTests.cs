using System.Buffers.Binary;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ImageDecodeBudgetTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = encoded.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 4097);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 4096);
        uint crc = 0xffffffff;
        foreach (byte value in png.AsSpan(12, 17))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), ~crc);
        bool rejectedBeforeDecode = false;
        try { AssetImport.Normalize(png); }
        catch (InvalidDataException error) { rejectedBeforeDecode = error.Message == FloatCanvas.Localization.AppText.Get("Images.BufferTooLarge"); }
        check("Oversized static PNG is rejected from its header before pixel allocation or decoding", rejectedBeforeDecode);
        var assets = new AssetLibrary(); var asset = assets.Add(png);
        var document = new SceneDocument { Viewport = new(0, 0, 16, 16) };
        document.Elements.Add(new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 16, Height = 16 });
        using var renderer = new SceneRenderer();
        bool reopenRejected = false;
        try { renderer.ExportPng(document, assets, true); }
        catch (InvalidDataException error) { reopenRejected = error.Message == FloatCanvas.Localization.AppText.Get("Images.BufferTooLarge"); }
        check("Reopened encoded assets obey the same predecode pixel budget", reopenRejected);
        check("Boundary dimensions are accepted without allocating a decoded image", ImageDecodeBudget.ValidateDimensions(4096, 4096) == ImageDecodeBudget.MaximumPixels);
        foreach (var format in new[] { SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp })
        {
            using var compressed = image.Encode(format, 100);
            byte[] header = compressed.ToArray();
            if (format == SKEncodedImageFormat.Jpeg)
            {
                int sof = Enumerable.Range(0, header.Length - 9).First(i => header[i] == 0xff && header[i + 1] is 0xc0 or 0xc1 or 0xc2);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(sof + 5), 8192);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(sof + 7), 8192);
            }
            else if (header.AsSpan(12, 4).SequenceEqual("VP8L"u8))
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(21), 8191u | (8191u << 14));
            else
            {
                int signature = Enumerable.Range(0, header.Length - 7).First(i => header[i] == 0x9d && header[i + 1] == 1 && header[i + 2] == 0x2a);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(signature + 3), 8192);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(signature + 5), 8192);
            }
            bool rejected = false;
            try { AssetImport.Normalize(header); }
            catch (InvalidDataException error) { rejected = error.Message == FloatCanvas.Localization.AppText.Get("Images.BufferTooLarge"); }
            check($"Oversized static {format} is rejected from its header before decoding", rejected);
        }
    }
}
