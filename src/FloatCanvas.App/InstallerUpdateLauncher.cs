using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Hands machine updates to Setup without elevating the application or its ZIP worker.</summary>
internal static class InstallerUpdateLauncher
{
    internal static async Task LaunchAsync(PreparedInstallerUpdate update, string installDirectory, string profileDirectory,
        string monitor, CancellationToken cancellationToken)
    {
        if (InstallerRegistration.Scope(installDirectory) != InstallationScope.AllUsers)
            throw new InvalidOperationException(L.Get("InstallerUpdates.ScopeChanged"));
        string eventName = "Local\\FloatCanvas.Update." + Guid.NewGuid().ToString("N");
        using var ready = CreateReadyEvent(eventName);
        // Keep the verified executable locked against replacement until its elevated
        // wizard has initialized. Only Setup performs privileged filesystem changes.
        using var executable = VerifyInstaller(update);
        var start = CreateStartInfo(update.InstallerPath, installDirectory, profileDirectory, monitor, eventName);
        cancellationToken.ThrowIfCancellationRequested();
        using var setup = Process.Start(start) ?? throw new IOException(L.Get("InstallerUpdates.Cancelled"));
        await WaitUntilReadyAsync(ready, () => setup.HasExited, cancellationToken);
    }

    internal static async Task WaitUntilReadyAsync(EventWaitHandle ready, Func<bool> hasExited, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        bool initialized = await Task.Run(() =>
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < (timeout ?? TimeSpan.FromMinutes(5)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ready.WaitOne(100)) return true;
                if (hasExited()) throw new InvalidOperationException(L.Get("InstallerUpdates.Cancelled"));
            }
            return false;
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!initialized) throw new TimeoutException(L.Get("InstallerUpdates.Timeout"));
    }

    internal static FileStream VerifyInstaller(PreparedInstallerUpdate update)
    {
        if (!SemVersion.TryParse(update.Version, out _) || update.Sha256.Length != 64 || !System.Linq.Enumerable.All(update.Sha256, Uri.IsHexDigit))
            throw new InvalidDataException("Invalid installer identity.");
        string path = Path.GetFullPath(update.InstallerPath);
        NoReparseAncestors(path);
        if (!string.Equals(Path.GetFileName(path), "FloatCanvas-" + update.Version + "-win-x64-Setup.exe", StringComparison.Ordinal))
            throw new InvalidDataException("The installer filename does not match its release version.");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer changed after download.");
            file.Position = 0;
            if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new InvalidDataException("The installer is not a Windows executable.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static ProcessStartInfo CreateStartInfo(string installer, string installDirectory, string profileDirectory, string monitor, string eventName)
    {
        foreach (string path in new[] { installer, installDirectory, profileDirectory })
        {
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
                throw new ArgumentException("Installer handoff paths must be absolute and contain no quotes or control characters.");
            NoReparseAncestors(Path.GetFullPath(path));
        }
        const string prefix = "Local\\FloatCanvas.Update.";
        if (!eventName.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(eventName[prefix.Length..], "N", out _) || monitor is not ("auto" or "primary" or "all"))
            throw new ArgumentException("Invalid installer handoff parameters.");
        var start = new ProcessStartInfo(Path.GetFullPath(installer))
        { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(installer))! };
        // Start the non-elevated Inno loader. It requests elevation itself and keeps
        // the original user's token for the finish-page launch.
        start.ArgumentList.Add("/ALLUSERS");
        start.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)));
        start.ArgumentList.Add("/FLOATCANVASUPDATEEVENT=" + eventName);
        start.ArgumentList.Add("/FLOATCANVASUPDATEPROFILE=" + Path.GetFullPath(profileDirectory));
        start.ArgumentList.Add("/FLOATCANVASUPDATEMONITOR=" + monitor);
        return start;
    }

    private static void NoReparseAncestors(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installer handoff paths must not contain symbolic links or junctions.");
    }

    internal static EventWaitHandle CreateReadyEvent(string name)
    {
        // Grant the elevated administrator permission to acknowledge readiness even
        // when UAC uses another account. Do not grant unrelated users access.
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current Windows user could not be identified.");
        string sddl = "D:P(A;;0x100002;;;SY)(A;;0x100002;;;BA)(A;;0x1f0003;;;" + sid + ")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            SafeWaitHandle handle = CreateEvent(ref attributes, true, false, name);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            var wait = new EventWaitHandle(false, EventResetMode.ManualReset) { SafeWaitHandle = handle };
            return wait;
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int InheritHandle; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEvent(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
