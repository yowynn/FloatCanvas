using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class AssetImportLosslessTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Construct PNG scanlines directly. A premultiplied fixture encoder would
        // quantize these colors before the production import operation is tested.
        byte[] rgba = [23, 64, 224, 210, 17, 211, 73, 128, 249, 31, 113, 64, 31, 71, 19, 255,
            41, 93, 224, 210, 3, 133, 253, 1, 19, 87, 201, 254, 0, 0, 0, 0];
        byte[] input = Png(rgba, 4, 2), before = input.ToArray();
        check("Straight-alpha fixture retains its independently encoded RGBA", Pixels(input).SequenceEqual(rgba));
        var normalized = AssetImport.Normalize(input);
        byte[] result = Pixels(normalized.PngBytes);
        int differing = Enumerable.Range(0, rgba.Length / 4).Count(i => !rgba.AsSpan(i * 4, 4).SequenceEqual(result.AsSpan(i * 4, 4)));
        check($"Upright PNG preserves exact decoded RGBA ({differing} differing pixels)", differing == 0);
        check("Upright PNG preserves the original encoded resource", normalized.PngBytes.SequenceEqual(before));
        check("Upright PNG reports the decoded dimensions", normalized.Width == 4 && normalized.Height == 2);
        check("Normalization does not modify caller-owned bytes", input.SequenceEqual(before));
        check("Preserved PNG resource does not alias the caller array", !ReferenceEquals(input, normalized.PngBytes));
        input[^1] ^= 1;
        check("Caller mutation cannot alter a normalized PNG resource", normalized.PngBytes.SequenceEqual(before));
        bool rejected = false;
        try { AssetImport.Normalize(before[..(before.Length / 2)]); }
        catch (InvalidDataException) { rejected = true; }
        check("Truncated PNG with a valid signature and header is rejected", rejected);
    }

    private static byte[] Pixels(byte[] png)
    {
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("PNG codec unavailable.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("PNG decode failed.");
        var rgba = new byte[checked(info.Width * info.Height * 4)];
        Marshal.Copy(bitmap.GetPixels(), rgba, 0, rgba.Length);
        return rgba;
    }

    private static byte[] Png(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; header[9] = 6; // 8-bit RGBA, not palette or premultiplied pixels.
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            for (int y = 0; y < height; y++) { zlib.WriteByte(0); zlib.Write(rgba.AsSpan(y * width * 4, width * 4)); }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length); output.Write(word);
        byte[] name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in name.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
    }
}
