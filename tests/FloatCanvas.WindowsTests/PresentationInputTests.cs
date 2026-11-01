using System.Buffers.Binary;
using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class PresentationInputTests
{
    internal static void Run(Action<bool, string> check)
    {
        var buffer = new PresentationInputBuffer(); int signals = 0;
        for (int index = 0; index < 10000; index++) if (buffer.Move(new(index, -index, index))) signals++;
        var moved = buffer.Drain(7);
        check(signals == 1 && moved.Changes.Count == 0 && moved.Cursor == new PresentationObservedCursor(9999, -9999, 9999), "Pointer movement coalesces to one owned physical point and one wakeup");
        check(buffer.Drain(7).Cursor is null, "A drained cursor cannot replay");
        for (int index = 0; index < 512; index++) buffer.Add(new(PresentationObservedKind.KeyDown, index, VirtualKey: 65, KeyLabel: "A"));
        check(buffer.HighWater == 512 && buffer.Drain(7).Changes.Count == 512, "The transition capacity includes exactly 512 entries");
        for (int index = 0; index < 513; index++) buffer.Add(new(PresentationObservedKind.KeyDown, index, VirtualKey: 66, KeyLabel: "B"));
        var overflow = buffer.Drain(7);
        check(overflow.Overflowed && overflow.Changes.Count == 1 && buffer.Dropped == 512, "Overflow resets stale held-key transitions and retains a bounded latest batch");
        check(!buffer.Drain(7).Overflowed, "Overflow is acknowledged once");
        buffer.Close();
        check(!buffer.Add(new(PresentationObservedKind.KeyDown, 1)) && !buffer.Move(new(10, 20, 1)) && buffer.Drain(7).Changes.Count == 0,
            "Closing the gate atomically rejects packets already in flight");
        var values = new[] { new PresentationObservedInput(PresentationObservedKind.KeyDown, 1, VirtualKey: 65, KeyLabel: "A") };
        var owned = new PresentationObservedBatch(1, values, null, false); values[0] = values[0] with { VirtualKey = 90 };
        check(owned.Changes[0].VirtualKey == 65 && owned.Changes is IList<PresentationObservedInput> list && list.IsReadOnly,
            "Public batches own their transition array and expose a read-only view");
        bool oversized = false;
        try { _ = new PresentationObservedBatch(1, Enumerable.Repeat(values[0], 513), null, false); } catch (ArgumentException) { oversized = true; }
        check(oversized, "A fake or alternate input backend cannot publish an oversized batch");

        foreach (int pointerSize in new[] { 4, 8 })
        {
            int header = pointerSize == 8 ? 24 : 16;
            var keyboard = Packet(1, header + 16, pointerSize);
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header), 0x1D);
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header + 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header + 6), 0x11);
            check(PresentationRawInputDecoder.TryKeyboard(keyboard, pointerSize, out var key) && key.Device == 12345 && key.VirtualKey == 0xA3 && key.Flags == 2 && !key.Released,
                pointerSize + "-byte pointer keyboard decoding preserves the right Control physical identity");
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header + 2), 3);
            check(PresentationRawInputDecoder.TryKeyboard(keyboard, pointerSize, out var released) && released.Released && released.Flags == key.Flags && released.ScanCode == key.ScanCode,
                pointerSize + "-byte pointer make and break share one physical key");
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header), 0x36);
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header + 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header + 6), 0x10);
            check(PresentationRawInputDecoder.TryKeyboard(keyboard, pointerSize, out key) && key.VirtualKey == 0xA1,
                pointerSize + "-byte pointer right Shift is identified from its scan code");
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header + 6), 255);
            check(!PresentationRawInputDecoder.TryKeyboard(keyboard, pointerSize, out _), "Unmapped raw virtual keys are ignored");
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header + 6), 65);
            BinaryPrimitives.WriteUInt16LittleEndian(keyboard.AsSpan(header), 255);
            check(!PresentationRawInputDecoder.TryKeyboard(keyboard, pointerSize, out _), "Keyboard overrun packets are not displayed as keys");
            var mouse = Packet(0, header + 24, pointerSize);
            BinaryPrimitives.WriteUInt16LittleEndian(mouse.AsSpan(header + 4), 1 | 512 | 1024);
            check(PresentationRawInputDecoder.TryMouse(mouse, pointerSize, out long device, out ushort buttons) && device == 12345 && buttons == 1537,
                pointerSize + "-byte pointer mouse union is aligned independently of relative movement");
            BinaryPrimitives.WriteUInt32LittleEndian(mouse.AsSpan(4), (uint)(mouse.Length + 1));
            check(!PresentationRawInputDecoder.TryMouse(mouse, pointerSize, out _, out _), "A raw packet with inconsistent size is rejected");
            check(!PresentationRawInputDecoder.TryKeyboard(keyboard.AsSpan(0, header), pointerSize, out _), "Truncated raw input does not read beyond its owned bytes");
        }
        check(!PresentationRawInputDecoder.TryKeyboard(new byte[64], 5, out _), "Unknown pointer sizes are rejected");
        check(PresentationInputObserver.ExpandMessageTime(99, 120) == 99, "Delayed raw input retains its event time rather than consumption time");
        check(PresentationInputObserver.ExpandMessageTime(uint.MaxValue - 9, (1L << 32) + 50) == (1L << 32) - 10, "Raw event timestamps expand correctly across the 32-bit clock wrap");
        check(PresentationInputObserver.ExpandMessageTime(120, 120) == 120 && PresentationInputObserver.ExpandMessageTime(0, 0) == 0, "Current and startup raw timestamps remain nonnegative");
    }
    private static byte[] Packet(uint type, int size, int pointerSize)
    {
        var bytes = new byte[size]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, type); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)size);
        if (pointerSize == 8) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 12345); else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 12345);
        return bytes;
    }
}
