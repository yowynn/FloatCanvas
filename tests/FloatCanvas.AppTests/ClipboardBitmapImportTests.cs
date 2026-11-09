using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using Wpf = System.Windows;

internal static class ClipboardBitmapImportTests
{
    internal static void Run(Action<string, bool> check, string? capturedDib = null)
    {
        foreach (int bits in new[] { 24, 32 })
        foreach (bool topDown in new[] { false, true })
        foreach (bool async in new[] { false, true })
            VerifyLegacy(bits, topDown, async, check);
        VerifyAlpha(check);
        VerifyInvalid(check);
        if (capturedDib is not null) VerifyCaptured(File.ReadAllBytes(capturedDib), check);
    }

    private static void VerifyLegacy(int bits, bool topDown, bool async, Action<string, bool> check)
    {
        // Two padded BGR rows or BGRX rows with arbitrary reserved bytes. The
        // converted bitmap has zero alpha, reproducing an invisible selected image.
        byte[] dib = LegacyDib(bits, topDown);
        var data = Data(dib);
        using var stream = (MemoryStream)data.GetData(Wpf.DataFormats.Dib)!;
        stream.Position = stream.Length; // Clipboard readers must rewind.
        var session = new EditorSession();
        ImageImportCommand.Insert(session, [AssetImport.Normalize(Read(data, async)!)], new(0, 0, 100, 100));
        var element = session.Document.Elements.Single();
        string name = $"legacy {bits}-bit clipboard (topDown={topDown}, async={async})";
        check(name + " inserts a selected image", element.Kind == ElementKind.Image && session.Selection.Contains(element.Id));
        using var decoded = SKBitmap.Decode(session.Assets.Get(element.AssetId!).Data.ToArray());
        check(name + " retains opaque colors, row order and padding", decoded.GetPixel(0, 0) == SKColors.Red &&
            decoded.GetPixel(1, 0) == SKColors.Lime && decoded.GetPixel(0, 1) == SKColors.Blue && decoded.GetPixel(1, 1) == SKColors.Yellow);
        check(name + " remains undoable", session.Undo() && session.Document.Elements.Count == 0 && session.Redo());
    }

    private static byte[] LegacyDib(int bits, bool topDown = false)
    {
        var dib = new byte[56];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(2).CopyTo(dib, 4);
        BitConverter.GetBytes(topDown ? -2 : 2).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)bits).CopyTo(dib, 14);
        byte[] top = bits == 24 ? [0, 0, 255, 0, 255, 0, 123, 123] : [0, 0, 255, 0, 0, 255, 0, 127];
        byte[] bottom = bits == 24 ? [255, 0, 0, 0, 255, 255, 123, 123] : [255, 0, 0, 200, 0, 255, 255, 255];
        top.CopyTo(dib, topDown ? 40 : 48);
        bottom.CopyTo(dib, topDown ? 48 : 40);
        return dib;
    }

    private static Wpf.DataObject Data(byte[] dib)
    {
        int width = BitConverter.ToInt32(dib, 4), height = Math.Abs(BitConverter.ToInt32(dib, 8));
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        var data = new Wpf.DataObject();
        data.SetData(Wpf.DataFormats.Bitmap, bitmap, autoConvert: false);
        data.SetData(Wpf.DataFormats.Dib, new MemoryStream(dib, writable: false), autoConvert: false);
        return data;
    }

    private static byte[]? Read(Wpf.IDataObject data, bool async = true)
    {
        if (!async) return DesktopSession.ReadClipboardImage(data);
        var method = typeof(DesktopSession).GetMethod("ReadClipboardImageAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        return ((Task<byte[]?>)method.Invoke(null, [data, CancellationToken.None])!).GetAwaiter().GetResult();
    }

    private static void VerifyAlpha(Action<string, bool> check)
    {
        foreach (bool async in new[] { false, true })
        {
            var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 128, 0, 0, 0, 0 }, 8);
            var data = new Wpf.DataObject(); data.SetData(Wpf.DataFormats.Bitmap, source, autoConvert: false);
            using var bitmapOnly = SKBitmap.Decode(Read(data, async)!);
            check($"bitmap-only source retains partial and zero alpha (async={async})",
                bitmapOnly.GetPixel(0, 0).Alpha == 128 && bitmapOnly.GetPixel(1, 0).Alpha == 0);

            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var encoded = new MemoryStream(); encoder.Save(encoded); byte[] png = encoded.ToArray();
            var pngData = Data(LegacyDib(24)); pngData.SetData("PNG", png, autoConvert: false);
            check($"original transparent PNG takes priority over legacy DIB (async={async})", Read(pngData, async)!.SequenceEqual(png));

            // An explicit V5 representation takes priority over a legacy fallback;
            // WPF's Bitmap representation here carries the source's genuine alpha.
            data.SetData(Wpf.DataFormats.Dib, new MemoryStream(LegacyDib(32)), autoConvert: false);
            data.SetData(Wpf.DataFormats.GetDataFormat(17).Name, new MemoryStream(new byte[124]), autoConvert: false);
            using var v5 = SKBitmap.Decode(Read(data, async)!);
            check($"explicit DIBV5 retains genuine alpha (async={async})", v5.GetPixel(0, 0).Alpha == 128 && v5.GetPixel(1, 0).Alpha == 0);
        }
    }

    private static void VerifyInvalid(Action<string, bool> check)
    {
        var truncated = Data(LegacyDib(24)[..55]);
        var oversized = Data(LegacyDib(24));
        byte[] header = LegacyDib(24); BitConverter.GetBytes(int.MaxValue).CopyTo(header, 4);
        oversized.SetData(Wpf.DataFormats.Dib, new MemoryStream(header), autoConvert: false);
        foreach (var (name, data) in new[] { ("truncated pixels", truncated), ("oversized dimensions", oversized) })
        {
            bool rejected = false;
            try { _ = Read(data); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
            catch (InvalidDataException) { rejected = true; }
            check("legacy clipboard rejects " + name + " before inserting an image", rejected);
        }
    }

    private static void VerifyCaptured(byte[] dib, Action<string, bool> check)
    {
        int width = BitConverter.ToInt32(dib, 4), height = BitConverter.ToInt32(dib, 8);
        if (BitConverter.ToUInt16(dib, 14) != 24 || height <= 0) throw new InvalidDataException("Expected a bottom-up 24-bit captured DIB.");
        int stride = (width * 3 + 3) / 4 * 4;
        using var bitmap = SKBitmap.Decode(Read(Data(dib))!);
        int mismatched = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = 40 + (height - 1 - y) * stride + x * 3;
            if (bitmap.GetPixel(x, y) != new SKColor(dib[offset + 2], dib[offset + 1], dib[offset], 255)) mismatched++;
        }
        check($"captured Photoshop {width}x{height} DIB matches every original color with opaque alpha (mismatched={mismatched})", mismatched == 0);
    }
}
