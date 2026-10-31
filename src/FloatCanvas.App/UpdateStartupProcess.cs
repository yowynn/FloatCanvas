using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace FloatCanvas.App;

/// <summary>Recognizes the native parent awaited by portable updaters predating the Host split.</summary>
internal static class UpdateStartupProcess
{
    internal const string LauncherIdEnvironment = "FLOATCANVAS_UPDATE_LAUNCHER_PID";
    internal const string LauncherCreationEnvironment = "FLOATCANVAS_UPDATE_LAUNCHER_CREATION_FILETIME";

    internal static Process? TryGetLauncher(string installDirectory, string? processId, string? creationFileTime)
    {
        if (!int.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0 ||
            !long.TryParse(creationFileTime, NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0)
            return null;
        Process? launcher = null;
        try
        {
            if (!Path.IsPathFullyQualified(installDirectory) || ParentProcessId() != id) return null;
            launcher = Process.GetProcessById(id);
            string expected = Path.GetFullPath(Path.Combine(installDirectory, "FloatCanvas.exe"));
            string? image = launcher.MainModule?.FileName;
            if (launcher.HasExited || launcher.StartTime.ToUniversalTime().ToFileTimeUtc() != created ||
                image is null || !string.Equals(Path.GetFullPath(image), expected, StringComparison.OrdinalIgnoreCase))
                return null;
            var valid = launcher; launcher = null; return valid;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or
            NotSupportedException or IOException or UnauthorizedAccessException or SecurityException)
        { return null; }
        finally { launcher?.Dispose(); }
    }

    private static int ParentProcessId()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (!Process32First(snapshot, ref entry)) return 0;
        do
        {
            if (entry.ProcessId == (uint)Environment.ProcessId) return checked((int)entry.ParentProcessId);
        }
        while (Process32Next(snapshot, ref entry));
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal nuint DefaultHeap;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
