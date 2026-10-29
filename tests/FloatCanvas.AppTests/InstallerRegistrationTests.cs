using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using Microsoft.Win32;

internal static class InstallerRegistrationTests
{
    private static readonly string[] Required = { "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.Core.dll", "FloatCanvas.Localization.dll", "FloatCanvas.Rendering.dll", "FloatCanvas.Storage.dll", "FloatCanvas.Windows.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "SkiaSharp.dll", "SkiaSharp.HarfBuzz.dll", "HarfBuzzSharp.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "PACKAGE-INFO.json" };

    internal static void Run(Action<string, bool> check)
    {
        Ownership(check); PackageChanges(check); PendingUpdate(check); InvalidPackages(check); WriteFailure(check);
    }

    private static void Ownership(Action<string, bool> check)
    {
        using var f = new Fixture();
        check("installer metadata never registers a ZIP distribution", !f.Refresh() && f.Root.OpenSubKey(InstallerRegistration.RegistryPath) is null && !File.Exists(f.List));
        f.Register();
        check("installer metadata requires its existing installer marker", !f.Refresh() && !File.Exists(f.List) && f.DisplayVersion == "0.3.0");
        File.WriteAllText(f.List, "original list");
        f.Register(f.Directory + "-other");
        check("installer metadata preserves another installation at a neighboring path", !f.Refresh() && File.ReadAllText(f.List) == "original list" && f.DisplayVersion == "0.3.0");
        f.Register(Path.Combine(f.Directory, "nested"));
        check("installer metadata does not treat nested installations as the same installation", !f.Refresh() && File.ReadAllText(f.List) == "original list");
        f.Register(f.Directory.ToUpperInvariant() + Path.DirectorySeparatorChar);
        check("installer metadata accepts the same Windows path with casing and trailing separator differences", f.Refresh() && f.DisplayVersion == "0.3.1");
        using var registration = f.Root.OpenSubKey(InstallerRegistration.RegistryPath)!;
        check("installer metadata preserves unrelated uninstall settings", Equals(registration.GetValue("CustomValue"), "preserve") && Equals(registration.GetValue("UninstallString"), "owned by installer"));
        using var startup = f.Root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")!;
        check("installer metadata does not change another copy's startup command", Equals(startup.GetValue("FloatCanvas"), "another copy"));
    }

    private static void PackageChanges(Action<string, bool> check)
    {
        using var f = new Fixture(); f.Register(); File.WriteAllText(f.List, "obsolete.txt\r\n");
        check("installer metadata refreshes a completed installed package", f.Refresh());
        var files = File.ReadAllLines(f.List);
        check("uninstall list includes files added by a ZIP update", files.Contains("Docs/安装指南.md"));
        check("uninstall list contains only current package-owned paths", files.SequenceEqual(f.Files) && !files.Contains("obsolete.txt") && !files.Contains("user.notes") && !files.Contains("unins000.exe") && !files.Contains(InstallerRegistration.FilesName));
        byte[] bytes = File.ReadAllBytes(f.List);
        check("uninstall list uses UTF-8 without a byte order mark", !(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf));
        check("refreshing uninstall metadata preserves user and installer files", File.ReadAllText(Path.Combine(f.Directory, "user.notes")) == "user data" && File.ReadAllText(Path.Combine(f.Directory, "unins000.exe")) == "installer data");
        File.Delete(Path.Combine(f.Directory, "Docs", "安装指南.md"));
        f.Files = Required; f.WriteManifest("0.3.2");
        check("later updates stop claiming files the updater removed", f.Refresh() && File.ReadAllLines(f.List).SequenceEqual(Required) && f.DisplayVersion == "0.3.2");
    }

    private static void PendingUpdate(Action<string, bool> check)
    {
        using var f = new Fixture(); f.Register(); File.WriteAllText(f.List, "original list");
        string pointer = Path.Combine(f.Directory, AppUpdateInstaller.RecoveryPointerName);
        File.WriteAllText(pointer, "pending transaction");
        check("startup acknowledgement cannot publish metadata before the updater commits", !f.Refresh() && File.ReadAllText(f.List) == "original list" && f.DisplayVersion == "0.3.0");
        File.Delete(pointer);
        check("committed updates can refresh their uninstall version and file list", f.Refresh() && f.DisplayVersion == "0.3.1" && File.ReadAllLines(f.List).SequenceEqual(f.Files));
    }

    private static void InvalidPackages(Action<string, bool> check)
    {
        var changes = new (string Name, Action<Fixture> Change)[]
        {
            ("escaping path", f => { f.Files = [..f.Files, "../user.notes"]; f.WriteManifest(); }),
            ("Windows alternate stream", f => { f.Files = [..f.Files, "FloatCanvas.dll:data"]; f.WriteManifest(); }),
            ("line-separated path", f => { f.Files = [..f.Files, "extra\nuser.notes"]; f.WriteManifest(); }),
            ("duplicate path", f => { f.Files = [..f.Files, "FLOATCANVAS.DLL"]; f.WriteManifest(); }),
            ("missing required path", f => { f.Files = f.Files.Where(path => path != "FloatCanvas.dll").ToArray(); f.WriteManifest(); }),
            ("incomplete package", f => File.Delete(Path.Combine(f.Directory, "FloatCanvas.dll"))),
            ("invalid version", f => f.WriteManifest("not-a-version")),
            ("debug configuration", f => f.WriteManifest(configuration: "Debug")),
            ("installer marker in package", f => { f.Files = [..f.Files, InstallerRegistration.FilesName]; f.WriteManifest(); }),
            ("uninstaller in package", f => { f.Files = [..f.Files, "unins000.exe"]; f.WriteManifest(); }),
        };
        foreach (var (name, change) in changes)
        {
            using var f = new Fixture(); f.Register(); File.WriteAllText(f.List, "original list"); change(f);
            int errors = 0;
            check("invalid installed " + name + " cannot become an uninstall target", !f.Refresh(_ => errors++) && errors == 1 && File.ReadAllText(f.List) == "original list" && f.DisplayVersion == "0.3.0");
        }
    }

    private static void WriteFailure(Action<string, bool> check)
    {
        using var f = new Fixture(); f.Register(); File.WriteAllText(f.List, "original list");
        File.SetAttributes(f.List, FileAttributes.ReadOnly);
        try
        {
            int errors = 0;
            check("metadata write failure is reported without failing startup or publishing a new version", !f.Refresh(_ => errors++) && errors == 1 && f.DisplayVersion == "0.3.0" && File.ReadAllText(f.List) == "original list");
            check("failed atomic metadata writes leave no temporary files", !System.IO.Directory.EnumerateFiles(f.Directory, "*.tmp").Any());
        }
        finally { File.SetAttributes(f.List, FileAttributes.Normal); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _registryPath = @"Software\FloatCanvas.Tests\Installer-" + Guid.NewGuid().ToString("N");
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "FloatCanvas.Installer-" + Guid.NewGuid().ToString("N"));
        internal RegistryKey Root { get; }
        internal string[] Files { get; set; } = [..Required, "Docs/安装指南.md"];
        internal string List => Path.Combine(Directory, InstallerRegistration.FilesName);
        internal string? DisplayVersion { get { using var key = Root.OpenSubKey(InstallerRegistration.RegistryPath); return key?.GetValue("DisplayVersion") as string; } }

        internal Fixture()
        {
            Root = Registry.CurrentUser.CreateSubKey(_registryPath);
            System.IO.Directory.CreateDirectory(Directory);
            foreach (string relative in Files)
            {
                string path = Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "package content");
            }
            File.WriteAllText(Path.Combine(Directory, "user.notes"), "user data");
            File.WriteAllText(Path.Combine(Directory, "unins000.exe"), "installer data");
            WriteManifest();
        }

        internal void WriteManifest(string version = "0.3.1", string configuration = "Release") =>
            File.WriteAllText(Path.Combine(Directory, "PACKAGE-INFO.json"), JsonSerializer.Serialize(new { Product = "浮绘 FloatCanvas", Version = version, RuntimeIdentifier = "win-x64", SelfContained = false, Configuration = configuration, Files }));

        internal void Register(string? directory = null)
        {
            using var key = Root.CreateSubKey(InstallerRegistration.RegistryPath);
            key.SetValue("InstallLocation", directory ?? Directory);
            key.SetValue("DisplayVersion", "0.3.0"); key.SetValue("CustomValue", "preserve"); key.SetValue("UninstallString", "owned by installer");
            using var startup = Root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); startup.SetValue("FloatCanvas", "another copy");
        }

        internal bool Refresh(Action<Exception>? reportError = null) => InstallerRegistration.Refresh(Root, Directory, reportError);

        public void Dispose()
        {
            Root.Dispose(); Registry.CurrentUser.DeleteSubKeyTree(_registryPath, throwOnMissingSubKey: false);
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
