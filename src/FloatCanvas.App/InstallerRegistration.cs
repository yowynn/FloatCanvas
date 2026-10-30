using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace FloatCanvas.App;

internal enum InstallationScope { Portable, CurrentUser, AllUsers }

/// <summary>Identifies installer-owned locations and maintains current-user ZIP updates.</summary>
internal static class InstallerRegistration
{
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatCanvas_is1";
    internal const string FilesName = "FloatCanvas.install-files.txt";

    internal static InstallationScope Scope(string directory)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return Scope(Registry.CurrentUser, machine, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { return InstallationScope.Portable; }
    }

    internal static InstallationScope Scope(RegistryKey currentUser, RegistryKey machine, string directory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory) || !File.Exists(Path.Combine(directory, FilesName))) return InstallationScope.Portable;
            using var allUsers = machine.OpenSubKey(RegistryPath);
            if (Matches(allUsers, directory)) return InstallationScope.AllUsers;
            using var user = currentUser.OpenSubKey(RegistryPath);
            return Matches(user, directory) ? InstallationScope.CurrentUser : InstallationScope.Portable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        { return InstallationScope.Portable; }
    }

    private static bool Matches(RegistryKey? registration, string directory) =>
        registration?.GetValue("InstallLocation") is string installed &&
        Path.IsPathFullyQualified(installed) && Path.IsPathFullyQualified(directory) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);

    internal static void Refresh(string directory, Action<Exception>? reportError = null)
    {
        // A normal application never writes the machine installation. Its elevated Setup
        // owns both the machine registration and the uninstall file list.
        if (Scope(directory) != InstallationScope.AllUsers) Refresh(Registry.CurrentUser, directory, reportError);
    }

    internal static bool Refresh(RegistryKey root, string directory, Action<Exception>? reportError = null)
    {
        try
        {
            using var registration = root.OpenSubKey(RegistryPath, writable: true);
            if (!Matches(registration, directory) ||
                !File.Exists(Path.Combine(directory, FilesName)) ||
                File.Exists(Path.Combine(directory, AppUpdateInstaller.RecoveryPointerName))) return false;

            // Only the current package owns these paths. Never turn nearby user files,
            // the profile, or the installer itself into uninstall targets.
            string version = AppUpdateInstaller.RefreshInstallerFileList(directory);
            registration!.SetValue("DisplayVersion", version, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or SecurityException)
        {
            reportError?.Invoke(ex);
            return false;
        }
    }
}
