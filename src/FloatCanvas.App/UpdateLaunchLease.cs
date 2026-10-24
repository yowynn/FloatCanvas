using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FloatCanvas.App;

/// <summary>Binds an update helper and its not-yet-acknowledged child to one OS lifetime.</summary>
internal sealed class UpdateLaunchLease : IDisposable
{
    private nint handle;
    internal UpdateLaunchLease()
    {
        handle = CreateJobObject(0, null);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var info = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(handle, GetCurrentProcess()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { CloseHandle(handle); handle = 0; throw; }
    }

    internal void Release()
    {
        if (handle == 0) return;
        var info = new ExtendedLimits();
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(handle); handle = 0;
    }

    public void Dispose()
    {
        // Normal exceptions still need this helper alive to roll back. In the rare
        // failure to clear the flag, keep the handle until process exit; closing it
        // here would terminate the helper in the middle of recovery.
        try { Release(); } catch (Win32Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { internal long ProcessTime, JobTime; internal uint Flags; internal nuint MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveProcesses; internal nuint Affinity; internal uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
