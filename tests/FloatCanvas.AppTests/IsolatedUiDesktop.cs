using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>Runs HWND fixtures away from the user's input desktop; never switches the visible desktop.</summary>
internal static class IsolatedUiDesktop
{
    internal static int Run(string[] args)
    {
        string name = "FloatCanvas.Tests." + Environment.ProcessId;
        nint desktop = CreateDesktop(name, 0, 0, 0, 0x000F01FF, 0);
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var process = new ProcessInfo();
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "WinSta0\\" + name,
                Flags = 0x100, Input = GetStdHandle(-10), Output = GetStdHandle(-11), Error = GetStdHandle(-12) };
            var inherited = new List<(nint Handle, uint Flags)>();
            try
            {
                foreach (nint handle in new[] { startup.Input, startup.Output, startup.Error }.Distinct())
                    if (handle != 0 && handle != -1 && GetHandleInformation(handle, out uint flags))
                    {
                        if (!SetHandleInformation(handle, 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        inherited.Add((handle, flags));
                    }
                string executable = Environment.ProcessPath!;
                var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(args.Where(arg => arg != "--isolated-ui-desktop")).Select(Quote)));
                if (!CreateProcess(executable, command, 0, 0, true, 0x08000000, 0, null, ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { foreach (var entry in inherited) SetHandleInformation(entry.Handle, 1, entry.Flags & 1); }
            if (WaitForSingleObject(process.Process, uint.MaxValue) != 0 || !GetExitCodeProcess(process.Process, out uint exit))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)exit);
        }
        finally
        {
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Process != 0) CloseHandle(process.Process);
            CloseDesktop(desktop);
        }
    }

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal int X, Y, Width, Height, XChars, YChars, Fill;
        internal uint Flags; internal short Show, ReservedSize;
        internal nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        internal nint Process, Thread; internal uint ProcessId, ThreadId;
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exit);
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetHandleInformation(nint handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
