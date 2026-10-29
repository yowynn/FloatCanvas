using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace FloatCanvas.App;

/// <summary>Keeps an existing per-user installation registered after a ZIP update.</summary>
internal static class InstallerRegistration
{
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatCanvas_is1";
    internal const string FilesName = "FloatCanvas.install-files.txt";

    internal static void Refresh(string directory, Action<Exception>? reportError = null) =>
        Refresh(Registry.CurrentUser, directory, reportError);

    internal static bool Refresh(RegistryKey root, string directory, Action<Exception>? reportError = null)
    {
        try
        {
            using var registration = root.OpenSubKey(RegistryPath, writable: true);
            if (registration?.GetValue("InstallLocation") is not string installed ||
                !Path.IsPathFullyQualified(installed) || !Path.IsPathFullyQualified(directory) ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(directory, FilesName)) ||
                File.Exists(Path.Combine(directory, AppUpdateInstaller.RecoveryPointerName))) return false;

            // Only the current package owns these paths. Never turn nearby user files,
            // the profile, or the installer itself into uninstall targets.
            string version = AppUpdateInstaller.RefreshInstallerFileList(directory);
            registration.SetValue("DisplayVersion", version, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or SecurityException)
        {
            reportError?.Invoke(ex);
            return false;
        }
    }
}
