using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FloatCanvas.Windows;

/// <summary>Explicitly selected runtime window metadata. This value must never enter project serialization.</summary>
public sealed record WindowCaptureTarget(nint Window, uint ProcessId, uint ThreadId, long ProcessCreationTime,
    string Title, string ProcessName, string WindowClass)
{
    public bool IsCurrent => WindowCaptureCatalog.IsCurrent(this);
}

/// <summary>Metadata only, invoked by an explicit window-selection action. Never captures a frame or activates a window.</summary>
public static class WindowCaptureCatalog
{
    public static IReadOnlyList<WindowCaptureTarget> Enumerate()
    {
        var result = new List<WindowCaptureTarget>(); Exception? error = null;
        bool Visit(nint window, nint state)
        {
            try
            {
                if (!IsWindowVisible(window) || GetWindow(window, 4) != 0 || (GetWindowLong(window, -20) & 0x80) != 0) return true;
                if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if (TryRead(window, out var target) && target.ProcessId != Environment.ProcessId && target.Title.Length > 0) result.Add(target);
                return true;
            }
            catch (Exception exception) { error = exception; return false; }
        }
        if (!EnumWindows(Visit, 0)) throw error ?? new Win32Exception(Marshal.GetLastWin32Error());
        return Array.AsReadOnly(result.OrderBy(target => target.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.Title, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    public static bool TryRead(nint window, out WindowCaptureTarget target)
    {
        target = null!;
        if (window == 0 || !IsWindow(window)) return false;
        uint thread = GetWindowThreadProcessId(window, out uint process); if (thread == 0 || process == 0) return false;
        nint handle = OpenProcess(0x1000, false, process); if (handle == 0) return false;
        try
        {
            if (!GetProcessTimes(handle, out long creation, out _, out _, out _)) return false;
            var name = new StringBuilder(32768); uint length = (uint)name.Capacity;
            string executable = QueryFullProcessImageName(handle, 0, name, ref length) ? Path.GetFileNameWithoutExtension(name.ToString()) : "";
            var title = new StringBuilder(513); GetWindowText(window, title, title.Capacity);
            var klass = new StringBuilder(257); if (GetClassName(window, klass, klass.Capacity) <= 0) return false;
            if (GetWindowThreadProcessId(window, out uint finalProcess) != thread || finalProcess != process || !IsWindow(window)) return false;
            target = new(window, process, thread, creation, Sanitize(title.ToString(), 512), Sanitize(executable, 260), Sanitize(klass.ToString(), 256));
            return true;
        }
        finally { CloseHandle(handle); }
    }

    public static bool IsMinimized(WindowCaptureTarget target) => target.IsCurrent && IsIconic(target.Window);
    public static bool IsVisible(WindowCaptureTarget target) => target.IsCurrent && IsWindowVisible(target.Window) &&
        (DwmGetWindowAttribute(target.Window, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0);
    internal static bool IsCurrent(WindowCaptureTarget target)
    {
        if (!IsWindow(target.Window) || GetWindowThreadProcessId(target.Window, out uint process) != target.ThreadId || process != target.ProcessId) return false;
        nint handle = OpenProcess(0x1000, false, process); if (handle == 0) return false;
        try
        {
            var klass = new StringBuilder(257);
            return GetProcessTimes(handle, out long created, out _, out _, out _) && created == target.ProcessCreationTime &&
                GetClassName(target.Window, klass, klass.Capacity) > 0 && klass.ToString() == target.WindowClass;
        }
        finally { CloseHandle(handle); }
    }
    private static string Sanitize(string value, int maximum) => new(value.Take(maximum).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    private delegate bool WindowEnumeration(nint window, nint state);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowEnumeration callback, nint state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int maximum);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint length);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
}
