using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Opt-in actual-process project activation, using generated projects and isolated profiles only.</summary>
internal static class ShellRuntimeFixture
{
    internal static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check)
    {
        string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("FLOATCANVAS_SHELL_RUNTIME_OUTPUT") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvas-ShellRuntime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string executable = Path.GetFullPath(Environment.GetEnvironmentVariable("FLOATCANVAS_SHELL_APP_EXE") ??
            Path.Combine(AppContext.BaseDirectory, "..", "..", "FloatCanvas.App", "debug", "FloatCanvas.exe"));
        check("shell runtime uses an existing built application", File.Exists(executable));
        string run = Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(run, "profile");
        string startupProfile = Path.Combine(run, "startup-profile");
        Directory.CreateDirectory(run);
        var rows = new List<object>();
        var processes = new List<Process>();
        var closed = new List<int>();
        string? failure = null;
        try
        {
            var (toggle, hide) = FindFreeHotkeys();
            WriteSettings(profile, toggle, hide);
            WriteSettings(startupProfile, toggle, hide);
            string firstMarker = "Shell first " + Guid.NewGuid().ToString("N");
            string secondMarker = "Shell second " + Guid.NewGuid().ToString("N");
            string first = Path.Combine(run, "第一张 own project.floatcanvas");
            string second = Path.Combine(run, "第二张 own project.floatcanvas");
            var store = new ProjectStore();
            await store.SaveAsync(first, Project(firstMarker), new AssetLibrary());
            await store.SaveAsync(second, Project(secondMarker), new AssetLibrary());
            var owner = Start(executable, run, ["--profile", profile, first]);
            processes.Add(owner);
            var recoveredFirst = await WaitForProjectAsync(profile, firstMarker, owner);
            check("positional Unicode project opens in the actual app and reaches recovery", !recoveredFirst.FromBackup &&
                recoveredFirst.State.LiveDocument.Elements.Single().Text == firstMarker && recoveredFirst.State.CaptureDocument is null);
            CheckProfile(check, profile);
            rows.Add(new { Stage = "positional-open", OwnerPid = owner.Id, Project = first, Marker = firstMarker,
                Recovery = Path.Combine(profile, "recovery", "workspace.floatcanvas"), VisibleWindows = VisibleWindows(owner.Id) });
            var sender = Start(executable, run, ["--profile", profile, "--open", second]);
            processes.Add(sender);
            await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            check("same-profile second invocation forwards and exits successfully", sender.ExitCode == 0 && !owner.HasExited);
            var recoveredSecond = await WaitForProjectAsync(profile, secondMarker, owner);
            check("owner loads the forwarded project rather than retaining the first", !recoveredSecond.FromBackup &&
                recoveredSecond.State.LiveDocument.Elements.Single().Text == secondMarker && recoveredSecond.State.CaptureDocument is null);
            check("handoff uses the existing owner without a second owner startup", File.ReadLines(Path.Combine(profile, "diagnostics.log")).Count(line => line.Contains("\"Event\":\"App.Startup\"", StringComparison.Ordinal)) == 1);
            check("actual project handoff produces no unhandled app error", !File.Exists(Path.Combine(profile, "errors.log")));
            rows.Add(new { Stage = "forwarded-open", OwnerPid = owner.Id, SenderPid = sender.Id, SenderExitCode = sender.ExitCode,
                Project = second, Marker = secondMarker, VisibleWindows = VisibleWindows(owner.Id) });
            int hold = int.TryParse(Environment.GetEnvironmentVariable("FLOATCANVAS_SHELL_HOLD_SECONDS"), out var requested) ? Math.Clamp(requested, 0, 60) : 0;
            if (hold > 0) await Task.Delay(TimeSpan.FromSeconds(hold));
            await StopOwnProcessAsync(owner); closed.Add(owner.Id);
            var quiet = Start(executable, run, ["--profile", startupProfile, "--startup"]);
            processes.Add(quiet);
            await WaitForStartupAsync(startupProfile, quiet, toggle);
            CheckProfile(check, startupProfile);
            var visible = VisibleWindows(quiet.Id);
            check("quiet startup keeps welcome and toolbar hidden", !visible.Any(window => window.Title == L.Get("Welcome.Title") || window.Title == L.Get("Toolbar.Title")));
            check("quiet startup does not enable canvas input", !visible.Any(window => window.Title == "FloatCanvas Input"));
            rows.Add(new { Stage = "quiet-startup", Pid = quiet.Id, VisibleWindows = visible });
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            foreach (var process in processes)
            {
                await StopOwnProcessAsync(process);
                if (!closed.Contains(process.Id)) closed.Add(process.Id);
                process.Dispose();
            }
            File.WriteAllText(Path.Combine(directory, "shell-runtime-report.json"), JsonSerializer.Serialize(new
            {
                Executable = executable, ExecutableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))),
                RunDirectory = run, Profile = profile, StartupProfile = startupProfile, Rows = rows, Failure = failure,
                ClosedOwnPids = closed, RegistryMutationAllowed = false, UserProfileAccessed = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void CheckProfile(Action<string, bool> check, string profile)
    {
        using var startup = JsonDocument.Parse(File.ReadLines(Path.Combine(profile, "diagnostics.log")).First());
        check("actual shell process uses its isolated profile " + Path.GetFileName(profile),
            string.Equals(startup.RootElement.GetProperty("RequestedProfile").GetString(), profile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(startup.RootElement.GetProperty("ActualProfile").GetString(), profile, StringComparison.OrdinalIgnoreCase));
    }

    private static SceneDocument Project(string marker) => new()
    {
        Kind = DocumentKind.LiveOverlay, Viewport = new(0, 0, 640, 480),
        Elements = [new() { Kind = ElementKind.Text, Text = marker, Width = 400, Height = 40, Transform = Affine2D.Translation(100, 100) }]
    };

    private static void WriteSettings(string profile, HotkeyBinding toggle, HotkeyBinding hide)
    {
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), JsonSerializer.Serialize(new UserSettings
        { Toggle = toggle, Hide = hide, Language = "zh-CN", Theme = "light", PrimaryScreenOnly = true, PreferGpuAcceleration = false }));
    }

    private static (HotkeyBinding, HotkeyBinding) FindFreeHotkeys()
    {
        for (uint key = 0x7C; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x4C01, 7, key)) continue;
            try
            {
                if (!RegisterHotKey(0, 0x4C02, 7, key + 1)) continue;
                UnregisterHotKey(0, 0x4C02);
                return (new(7, key), new(7, key + 1));
            }
            finally { UnregisterHotKey(0, 0x4C01); }
        }
        throw new InvalidOperationException("No free Ctrl+Alt+Shift function-key pair for the isolated shell fixture.");
    }

    private static Process Start(string executable, string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated application.");
    }

    private static async Task<RecoveredWorkspace> WaitForProjectAsync(string profile, string marker, Process owner)
    {
        var store = new RecoveryStore(Path.Combine(profile, "recovery"));
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (owner.HasExited) throw new InvalidOperationException("Owner exited before loading the generated project: " + owner.ExitCode);
            try
            {
                var result = await store.LoadAsync();
                if (result?.State.LiveDocument.Elements.Any(element => element.Text == marker) == true) return result;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Generated project did not reach the actual app recovery package. Inspect " + profile);
    }

    private static async Task WaitForStartupAsync(string profile, Process owner, HotkeyBinding toggle)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (owner.HasExited) throw new InvalidOperationException("Quiet startup exited unexpectedly: " + owner.ExitCode);
            // An unchanged empty startup deliberately writes no recovery generation.
            // This unique pre-probed chord becomes unavailable only once the child registers it.
            bool available = RegisterHotKey(0, 0x4C03, toggle.Modifiers, toggle.VirtualKey);
            if (available) UnregisterHotKey(0, 0x4C03);
            else
            {
                await Task.Delay(500);
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Quiet startup did not register its isolated hotkey. Inspect " + profile);
    }

    private static async Task StopOwnProcessAsync(Process process)
    {
        if (process.HasExited) return;
        // The production main window may hide on Close. The fixture owns this isolated process.
        process.CloseMainWindow();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(600)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }

    private sealed record VisibleWindow(long Handle, string Title);
    private static VisibleWindow[] VisibleWindows(int processId)
    {
        var windows = new List<VisibleWindow>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint candidate);
            if (candidate == processId && IsWindowVisible(handle))
            {
                var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
                windows.Add(new(handle.ToInt64(), title.ToString()));
            }
            return true;
        }, 0);
        return windows.ToArray();
    }
    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
