using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class WindowsShellIntegration
{
    internal const string ProgId = "FloatCanvas.Project";
    private readonly RegistryKey _root;
    private readonly string _executable;
    private readonly bool _notifyShell;

    internal WindowsShellIntegration(RegistryKey root, string executable, bool notifyShell = false)
    {
        _root = root;
        _executable = Path.GetFullPath(executable);
        if (_executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) throw new ArgumentException(nameof(executable));
        _notifyShell = notifyShell;
    }

    internal static WindowsShellIntegration Current() => new(Registry.CurrentUser,
        ApplicationPaths.LauncherPath, true);
    internal string OpenCommand => $"\"{_executable}\" --open \"%1\"";
    internal string StartupCommand => $"\"{_executable}\" --startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal bool StartupEnabled
    {
        get { using var key = _root.OpenSubKey(RunKey); return string.Equals(key?.GetValue("FloatCanvas") as string, StartupCommand, StringComparison.OrdinalIgnoreCase); }
    }

    internal void SetStartup(bool enabled)
    {
        using var key = _root.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue("FloatCanvas", StartupCommand, RegistryValueKind.String);
        else if (string.Equals(key.GetValue("FloatCanvas") as string, StartupCommand, StringComparison.OrdinalIgnoreCase)) key.DeleteValue("FloatCanvas", false);
    }

    internal void RegisterProjectType()
    {
        void Set(string keyPath, string name, string value)
        {
            using var key = _root.CreateSubKey(keyPath, true);
            key.SetValue(name, value, RegistryValueKind.String);
        }
        const string classes = @"Software\Classes\";
        Set(classes + ProgId, "", L.Get("Shell.ProjectType"));
        Set(classes + ProgId + @"\DefaultIcon", "", $"\"{_executable}\",0");
        Set(classes + ProgId + @"\shell\open\command", "", OpenCommand);
        using (var extension = _root.CreateSubKey(classes + ".floatcanvas", true))
        {
            // Existing file associations and Windows UserChoice remain under the user's control.
            if (extension.GetValue("") is not string current || string.IsNullOrWhiteSpace(current)) extension.SetValue("", ProgId);
        }
        Set(classes + @".floatcanvas\OpenWithProgids", ProgId, "");
        const string app = classes + @"Applications\FloatCanvas.exe";
        Set(app, "FriendlyAppName", L.Get("App.Name"));
        Set(app + @"\shell\open\command", "", OpenCommand);
        Set(app + @"\SupportedTypes", ".floatcanvas", "");
        const string capabilities = @"Software\FloatCanvas\Capabilities";
        Set(capabilities, "ApplicationName", "FloatCanvas");
        Set(capabilities, "ApplicationDescription", L.Get("Shell.ProjectType"));
        Set(capabilities, "InstallPath", _executable);
        Set(capabilities + @"\FileAssociations", ".floatcanvas", ProgId);
        Set(@"Software\RegisteredApplications", "FloatCanvas", capabilities);
        if (_notifyShell) SHChangeNotify(0x08000000, 0, 0, 0);
    }

    internal void UnregisterOwnedIntegration()
    {
        const string classes = @"Software\Classes\";
        bool Owns(string key, string name, string expected)
        { using var value = _root.OpenSubKey(key); return string.Equals(value?.GetValue(name) as string, expected, StringComparison.OrdinalIgnoreCase); }
        void Remove(string key, string name)
        {
            using (var value = _root.OpenSubKey(key, true)) value?.DeleteValue(name, false);
            // Only remove now-empty keys; foreign values and subkeys survive.
            using var remaining = _root.OpenSubKey(key);
            bool empty = remaining is not null && remaining.ValueCount == 0 && remaining.SubKeyCount == 0;
            remaining?.Dispose();
            if (empty) _root.DeleteSubKey(key, false);
        }
        void Empty(string key) { Remove(key, "__FloatCanvasNoSuchValue__"); }
        bool projectOwned = Owns(classes + ProgId + @"\shell\open\command", "", OpenCommand);
        if (projectOwned)
        {
            Remove(classes + ProgId + @"\shell\open\command", "");
            Empty(classes + ProgId + @"\shell\open"); Empty(classes + ProgId + @"\shell");
            if (Owns(classes + ProgId + @"\DefaultIcon", "", $"\"{_executable}\",0")) Remove(classes + ProgId + @"\DefaultIcon", "");
            Remove(classes + ProgId, "");
            Remove(classes + @".floatcanvas\OpenWithProgids", ProgId);
            if (Owns(classes + ".floatcanvas", "", ProgId)) Remove(classes + ".floatcanvas", "");
        }
        const string app = classes + @"Applications\FloatCanvas.exe";
        if (Owns(app + @"\shell\open\command", "", OpenCommand))
        {
            Remove(app + @"\shell\open\command", ""); Empty(app + @"\shell\open"); Empty(app + @"\shell");
            Remove(app + @"\SupportedTypes", ".floatcanvas"); Remove(app, "FriendlyAppName");
        }
        const string capabilities = @"Software\FloatCanvas\Capabilities";
        if (Owns(capabilities, "InstallPath", _executable) || (projectOwned && !HasValue(capabilities, "InstallPath")))
        {
            if (Owns(@"Software\RegisteredApplications", "FloatCanvas", capabilities)) Remove(@"Software\RegisteredApplications", "FloatCanvas");
            Remove(capabilities + @"\FileAssociations", ".floatcanvas");
            Remove(capabilities, "ApplicationName"); Remove(capabilities, "ApplicationDescription"); Remove(capabilities, "InstallPath");
            Empty(@"Software\FloatCanvas");
        }
        SetStartup(false);
        if (_notifyShell) SHChangeNotify(0x08000000, 0, 0, 0);
    }

    private bool HasValue(string path, string name) { using var key = _root.OpenSubKey(path); return key?.GetValue(name) is not null; }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
