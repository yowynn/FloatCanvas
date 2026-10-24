using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatCanvas.App;

internal static class DiagnosticBundle
{
    private const int LogLimit = 2 * 1024 * 1024;
    internal static Dictionary<string, string> Prepare(string profile, bool enhanced, bool includeLogs)
    {
        var entries = new Dictionary<string, string>
        {
            ["environment.json"] = JsonSerializer.Serialize(new
            {
                Version = typeof(DiagnosticBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                CreatedUtc = DateTimeOffset.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
                OS = Environment.OSVersion.VersionString, Backend = enhanced ? "SystemOverlay" : "Desktop",
                Displays = System.Windows.Forms.Screen.AllScreens.Select(screen => new { screen.Primary, screen.Bounds, screen.WorkingArea }),
                Redacted = true
            }, new JsonSerializerOptions { WriteIndented = true })
        };
        if (includeLogs)
        {
            int remaining = LogLimit;
            foreach (string name in new[] { "errors.log", "diagnostics.log" })
            {
                if (remaining <= 0) break;
                string path = Path.Combine(profile, name);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int count = (int)Math.Min(stream.Length, remaining); bool truncated = stream.Length > count;
                stream.Seek(-count, SeekOrigin.End); byte[] bytes = new byte[count]; int actual = stream.ReadAtLeast(bytes, count, false);
                string value = Encoding.UTF8.GetString(bytes, 0, actual);
                if (truncated) { int nextLine = value.IndexOf('\n'); value = nextLine < 0 ? "[truncated]" : "[earlier records omitted]\n" + value[(nextLine + 1)..]; }
                entries[name + ".txt"] = Redact(value, profile, Environment.UserName, Environment.MachineName);
                remaining -= actual;
            }
        }
        return entries;
    }

    internal static string Redact(string text, params string[] sensitive)
    {
        foreach (string value in sensitive.Where(value => !string.IsNullOrWhiteSpace(value)).OrderByDescending(value => value.Length))
        {
            text = text.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(JsonSerializer.Serialize(value)[1..^1], "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
        text = Regex.Replace(text, @"(?i)https?://[^\s""<>]+", "[url]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\)[^""\r\n<>|]*", "[path]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    internal static void Export(string destination, IReadOnlyDictionary<string, string> entries)
    {
        string target = Path.GetFullPath(destination), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                    foreach (var entry in entries)
                    { using var writer = new StreamWriter(zip.CreateEntry(entry.Key, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)); writer.Write(entry.Value); }
                output.Flush(true);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
