using System.Runtime.InteropServices;

namespace FloatCanvas.Rendering;

/// <summary>Windows' supported ICU API supplies Unicode bidi and line-break rules.
/// Keep the UTF-16 text pinned for the entire ICU object's lifetime.</summary>
internal static class UnicodeTextAnalysis
{
    private static readonly bool HasIcu = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362);
    internal readonly record struct DirectionRun(int Start, int Length, bool RightToLeft);
    internal static (byte Level, IReadOnlyList<DirectionRun> Runs) VisualRuns(string text, byte paragraphLevel = 0xfe)
    {
        if (text.Length == 0) return (0, []);
        if (!HasIcu) return LegacyVisualRuns(text, paragraphLevel);
        var pinned = GCHandle.Alloc(text, GCHandleType.Pinned);
        nint bidi = ubidi_open();
        if (bidi == 0) { pinned.Free(); throw new OutOfMemoryException(); }
        try
        {
            int error = 0;
            ubidi_setPara(bidi, pinned.AddrOfPinnedObject(), text.Length, paragraphLevel, 0, ref error);
            Check(error);
            int count = ubidi_countRuns(bidi, ref error); Check(error);
            var runs = new DirectionRun[count];
            for (int i = 0; i < count; i++)
            {
                int direction = ubidi_getVisualRun(bidi, i, out int start, out int length);
                runs[i] = new(start, length, direction == 1);
            }
            return (ubidi_getParaLevel(bidi), runs);
        }
        finally { ubidi_close(bidi); pinned.Free(); }
    }

    internal static HashSet<int> LineBreaks(string text)
    {
        if (!HasIcu) return LegacyLineBreaks(text);
        var pinned = GCHandle.Alloc(text, GCHandleType.Pinned);
        nint iterator = 0;
        try
        {
            int error = 0;
            iterator = ubrk_open(2, "", pinned.AddrOfPinnedObject(), text.Length, ref error); Check(error);
            if (iterator == 0) throw new OutOfMemoryException();
            var result = new HashSet<int>();
            for (int next = ubrk_next(iterator); next >= 0; next = ubrk_next(iterator)) result.Add(next);
            return result;
        }
        finally { if (iterator != 0) ubrk_close(iterator); pinned.Free(); }
    }
    private static void Check(int error) { if (error > 0) throw new InvalidOperationException("Unicode text analysis failed: " + error); }
    // Windows 10 1607 is still supported. Uniscribe is its OS text-analysis API;
    // do not silently raise the app's minimum version by requiring icu.dll.
    [StructLayout(LayoutKind.Sequential)] private struct ScriptItem { public int Start; public ushort Analysis, State; }
    private static (byte Level, ScriptItem[] Items, int Count) Itemize(string text, byte level)
    {
        if (level == 0xfe)
        {
            var types = new ushort[text.Length];
            if (!GetStringTypeW(2, text, text.Length, types)) throw new System.ComponentModel.Win32Exception();
            level = types.FirstOrDefault(value => value is 1 or 2) == 2 ? (byte)1 : (byte)0;
        }
        var items = new ScriptItem[text.Length + 2]; ushort state = level; uint control = 0;
        Marshal.ThrowExceptionForHR(ScriptItemize(text, text.Length, items.Length - 1, ref control, ref state, items, out int count));
        return (level, items, count);
    }
    internal static (byte Level, IReadOnlyList<DirectionRun> Runs) LegacyVisualRuns(string text, byte level = 0xfe)
    {
        var result = Itemize(text, level);
        var levels = result.Items.Take(result.Count).Select(item => (byte)(item.State & 31)).ToArray();
        var order = new int[result.Count];
        Marshal.ThrowExceptionForHR(ScriptLayout(order.Length, levels, order, 0));
        return (result.Level, order.Select(index => new DirectionRun(result.Items[index].Start,
            result.Items[index + 1].Start - result.Items[index].Start, (result.Items[index].Analysis & 1024) != 0)).ToArray());
    }
    internal static HashSet<int> LegacyLineBreaks(string text)
    {
        var breaks = new HashSet<int> { text.Length };
        if (text.Length == 0) return breaks;
        var result = Itemize(text, 0xfe);
        for (int i = 0; i < result.Count; i++)
        {
            string run = text[result.Items[i].Start..result.Items[i + 1].Start];
            var attributes = new byte[run.Length];
            uint analysis = result.Items[i].Analysis | ((uint)result.Items[i].State << 16);
            Marshal.ThrowExceptionForHR(ScriptBreak(run, run.Length, ref analysis, attributes));
            for (int j = 0; j < attributes.Length; j++) if ((attributes[j] & 1) != 0) breaks.Add(result.Items[i].Start + j);
        }
        return breaks;
    }
    [DllImport("usp10.dll", CharSet = CharSet.Unicode)] private static extern int ScriptItemize(string text, int length, int maximum, ref uint control, ref ushort state, [Out] ScriptItem[] items, out int count);
    [DllImport("usp10.dll")] private static extern int ScriptLayout(int count, byte[] levels, [Out] int[] order, nint reverse);
    [DllImport("usp10.dll", CharSet = CharSet.Unicode)] private static extern int ScriptBreak(string text, int length, ref uint analysis, [Out] byte[] attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetStringTypeW(uint type, string text, int length, [Out] ushort[] attributes);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern nint ubidi_open();
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ubidi_close(nint bidi);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ubidi_setPara(nint bidi, nint text, int length, byte level, nint levels, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int ubidi_countRuns(nint bidi, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int ubidi_getVisualRun(nint bidi, int index, out int start, out int length);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern byte ubidi_getParaLevel(nint bidi);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern nint ubrk_open(int type, string locale, nint text, int length, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int ubrk_next(nint iterator);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ubrk_close(nint iterator);
}
