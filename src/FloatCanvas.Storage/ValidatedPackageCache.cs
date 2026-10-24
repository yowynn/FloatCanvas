using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FloatCanvas.Storage;

/// <summary>Process-local validation evidence for an unchanged file identity. Unsupported systems revalidate.</summary>
internal static class ValidatedPackageCache
{
    private readonly record struct Identity(uint Volume, ulong FileId, long Size, long Changed, long Written);
    private static readonly ConcurrentDictionary<(Identity, string), byte> validated = new();
    internal static bool Contains(SafeFileHandle handle, string kind) => TryIdentity(handle, out var identity) && validated.ContainsKey((identity, kind));
    internal static void Remember(SafeFileHandle handle, string kind)
    {
        if (!TryIdentity(handle, out var identity)) return;
        if (validated.Count >= 2048) validated.Clear();
        validated.TryAdd((identity, kind), 0);
    }
    private static bool TryIdentity(SafeFileHandle handle, out Identity identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(handle, out var file) ||
            !GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>())) return false;
        identity = new(file.VolumeSerialNumber, ((ulong)file.FileIndexHigh << 32) | file.FileIndexLow,
            ((long)file.FileSizeHigh << 32) | file.FileSizeLow, basic.ChangeTime, basic.LastWriteTime);
        return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out BasicInfo info, uint size);
}
