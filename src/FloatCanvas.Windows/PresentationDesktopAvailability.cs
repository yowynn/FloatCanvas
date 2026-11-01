using System.Runtime.InteropServices;
using System.Text;

namespace FloatCanvas.Windows;

public static class PresentationDesktopAvailability
{
    /// <summary>Metadata only; secure and inactive desktops never acquire the presentation runtime gate.</summary>
    public static bool IsCurrentInputDesktop()
    {
        nint input = OpenInputDesktop(0, false, 1); if (input == 0) return false;
        try
        {
            var left = new StringBuilder(256); var right = new StringBuilder(256);
            return GetUserObjectInformation(input, 2, left, 512, out _) &&
                GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, right, 512, out _) &&
                string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(input); }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, uint size, out uint needed);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
