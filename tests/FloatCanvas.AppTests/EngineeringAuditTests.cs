using System.IO;
using FloatCanvas.App;
using Microsoft.Win32;

internal static class EngineeringAuditTests
{
    internal static void Run(Action<string, bool> check)
    {
        var secret = @"C:\Users\PrivatePerson\Documents\sensitive canvas.floatcanvas";
        string redacted = DiagnosticBundle.Redact(System.Text.Json.JsonSerializer.Serialize(new { Path = secret, Error = "Cannot open " + secret, Url = "https://example.invalid/private?token=secret" }), "PrivatePerson");
        check("diagnostic preview removes usernames, spaced project paths and URLs", !redacted.Contains("PrivatePerson") && !redacted.Contains("sensitive canvas") && !redacted.Contains("example.invalid") && !redacted.Contains("token=secret"));
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-profile-id-" + Guid.NewGuid().ToString("N"));
        string[] aliases = [root, root + Path.DirectorySeparatorChar, root.ToUpperInvariant(), Path.Combine(root, "..", Path.GetFileName(root))];
        var identities = aliases.Select(ApplicationPaths.ProfileIdentity).ToArray();
        check("same profile aliases share a mutex and IPC identity", identities.Distinct().Count() == 1);
        check("independent profiles retain independent identities", ApplicationPaths.ProfileIdentity(root + "-other") != identities[0]);
        check("profile canonicalization preserves drive roots", ApplicationPaths.CanonicalDirectory(Path.GetPathRoot(root)!) == Path.GetPathRoot(root));
        using var first = new Mutex(true, "Local\\FloatCanvas.ProfileTest." + identities[0], out bool firstCreated);
        using var second = new Mutex(true, "Local\\FloatCanvas.ProfileTest." + identities[1], out bool secondCreated);
        check("trailing separator cannot acquire a second profile owner", firstCreated && !secondCreated);
        Directory.CreateDirectory(root);
        try
        {
            var entries = DiagnosticBundle.Prepare(root, false, false);
            string destination = Path.Combine(root, "diagnostics.zip"); DiagnosticBundle.Export(destination, entries);
            using var zip = System.IO.Compression.ZipFile.OpenRead(destination);
            check("diagnostic export contains only the reviewed metadata when logs unchecked", zip.Entries.Count == 1 && zip.Entries[0].FullName == "environment.json");
            using var reader = new StreamReader(zip.Entries[0].Open());
            check("exported content exactly matches the preview", reader.ReadToEnd() == entries["environment.json"]);
        }
        finally { Directory.Delete(root, true); }
        string sandbox = @"Software\FloatCanvas.Tests\Unregister-" + Guid.NewGuid().ToString("N");
        try
        {
            using var registry = Registry.CurrentUser.CreateSubKey(sandbox);
            var own = new WindowsShellIntegration(registry, Path.Combine(root, "FloatCanvas.exe"));
            var other = new WindowsShellIntegration(registry, Path.Combine(root + "-other", "FloatCanvas.exe"));
            own.RegisterProjectType(); own.SetStartup(true);
            using (var choice = registry.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.floatcanvas\UserChoice")) choice.SetValue("ProgId", "Other.Application");
            other.UnregisterOwnedIntegration();
            check("unregistering another installation preserves the current owner", own.StartupEnabled && registry.OpenSubKey(@"Software\Classes\FloatCanvas.Project") is not null);
            own.UnregisterOwnedIntegration();
            check("owned integration is removed without deleting profile data", !own.StartupEnabled && registry.OpenSubKey(@"Software\Classes\FloatCanvas.Project") is null && registry.OpenSubKey(@"Software\FloatCanvas\Capabilities") is null);
            using var preserved = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.floatcanvas\UserChoice");
            check("unregister never changes Windows UserChoice", Equals(preserved?.GetValue("ProgId"), "Other.Application"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sandbox, false); }
    }
}
