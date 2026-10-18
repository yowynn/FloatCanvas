using System.Buffers.Binary;

namespace FloatCanvas.Storage;

// A preview is optional display data, never a resource needed to restore the workspace.
internal static class RecoveryPreview
{
    internal const string EntryName = "preview.png";
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal const int MaximumDimension = 1024;

    internal static byte[]? Snapshot(byte[]? bytes) => bytes is not null && IsValid(bytes) ? bytes.ToArray() : null;

    internal static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 57 || bytes.Length > MaximumBytes || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        int offset = 8;
        bool header = false, pixels = false;
        while (offset <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) return false;
            int count = (int)length;
            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, count);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + count, 4));
            if (Crc32(bytes.Slice(offset + 4, 4 + count)) != expectedCrc) return false;
            if (!header)
            {
                if (!type.SequenceEqual("IHDR"u8) || count != 13) return false;
                uint width = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                uint height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                if (width is 0 or > MaximumDimension || height is 0 or > MaximumDimension || data[10] != 0 || data[11] != 0 || data[12] > 1) return false;
                bool validDepth = data[9] switch
                {
                    0 => data[8] is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => data[8] is 8 or 16,
                    3 => data[8] is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth) return false;
                header = true;
            }
            else if (type.SequenceEqual("IHDR"u8)) return false;
            if (type.SequenceEqual("IDAT"u8)) pixels |= count > 0;
            offset += count + 12;
            if (type.SequenceEqual("IEND"u8)) return count == 0 && pixels && offset == bytes.Length;
        }
        return false;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }
}
