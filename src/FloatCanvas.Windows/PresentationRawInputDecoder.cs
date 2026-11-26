using System.Buffers.Binary;

namespace FloatCanvas.Windows;

/// <summary>Only keyboard and mouse transition metadata are decoded from owned raw-input bytes.</summary>
internal static class PresentationRawInputDecoder
{
    internal readonly record struct Keyboard(long Device, int VirtualKey, int ScanCode, uint Flags, bool Released);
    internal static bool TryKeyboard(ReadOnlySpan<byte> bytes, int pointerSize, out Keyboard key)
    {
        key = default;
        int header = pointerSize == 8 ? 24 : pointerSize == 4 ? 16 : 0;
        if (header == 0 || bytes.Length < header + 16 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length) return false;
        int scan = BinaryPrimitives.ReadUInt16LittleEndian(bytes[header..]);
        uint flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(header + 2)..]);
        int vk = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(header + 6)..]);
        if (vk is <= 0 or >= 255 || scan == 255 || (flags & ~7u) != 0) return false;
        if (vk == 0x10) vk = scan == 0x36 ? 0xA1 : 0xA0;
        else if (vk == 0x11) vk = (flags & 2) != 0 ? 0xA3 : 0xA2;
        else if (vk == 0x12) vk = (flags & 2) != 0 ? 0xA5 : 0xA4;
        key = new(Device(bytes, pointerSize), vk, scan, flags & 6, (flags & 1) != 0); return true;
    }
    internal static bool TryMouse(ReadOnlySpan<byte> bytes, int pointerSize, out long device, out ushort buttons, out short wheel)
    {
        device = 0; buttons = 0; wheel = 0;
        int header = pointerSize == 8 ? 24 : pointerSize == 4 ? 16 : 0;
        if (header == 0 || bytes.Length < header + 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length) return false;
        device = Device(bytes, pointerSize); buttons = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(header + 4)..]);
        if ((buttons & 0x0400) != 0) wheel = BinaryPrimitives.ReadInt16LittleEndian(bytes[(header + 6)..]);
        return true;
    }
    private static long Device(ReadOnlySpan<byte> bytes, int pointerSize) => pointerSize == 8
        ? BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
}
