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
        Environment.ProcessPath ?? throw new InvalidOperationException(L.Get("Shell.NoExecutable")), true);
    internal string OpenCommand => $"\"{_executable}\" --open \"%1\"";
    internal string StartupCommand => $"\"{_executable}\" --startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal bool StartupEnabled
    {
        get { using var key = _root.OpenSubKey(RunKey); return key?.GetValue("FloatCanvas") is string value && !string.IsNullOrWhiteSpace(value); }
    }

    internal void SetStartup(bool enabled)
    {
        using var key = _root.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue("FloatCanvas", StartupCommand, RegistryValueKind.String);
        else key.DeleteValue("FloatCanvas", false);
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
        Set(capabilities + @"\FileAssociations", ".floatcanvas", ProgId);
        Set(@"Software\RegisteredApplications", "FloatCanvas", capabilities);
        if (_notifyShell) SHChangeNotify(0x08000000, 0, 0, 0);
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
