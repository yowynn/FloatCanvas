using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

/// <summary>Controlled modifiers for synchronous raised events, after WPF/input initialization.</summary>
internal sealed class ThreadModifierScope : IDisposable
{
    // Raised KeyEventArgs do not change Keyboard.Modifiers. SetKeyboardState
    // changes only this thread's snapshot; it generates no physical/global input.
    private readonly byte[] original = Read();
    internal ThreadModifierScope(ModifierKeys modifiers)
    {
        byte[] next = (byte[])original.Clone();
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) next[key] &= 0x7F;
        if ((modifiers & ModifierKeys.Shift) != 0) { next[0x10] |= 0x80; next[0xA0] |= 0x80; }
        if ((modifiers & ModifierKeys.Control) != 0) { next[0x11] |= 0x80; next[0xA2] |= 0x80; }
        if ((modifiers & ModifierKeys.Alt) != 0) { next[0x12] |= 0x80; next[0xA4] |= 0x80; }
        if (!SetKeyboardState(next)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    { if (!SetKeyboardState(original)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal static byte[] Read()
    {
        byte[] state = new byte[256];
        if (!GetKeyboardState(state)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return state;
    }
    internal static string Describe()
    {var state=Read();return string.Join(",",new[]{0x10,0x11,0x12,0xA0,0xA1,0xA2,0xA3,0xA4,0xA5}.Select(key=>$"{key:X2}:{state[key]:X2}"));}
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);
}
