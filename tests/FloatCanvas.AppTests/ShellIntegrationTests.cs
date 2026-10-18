using System.IO;
using Microsoft.Win32;
using FloatCanvas.App;

internal static class ShellIntegrationTests
{
    internal static void Run(Action<string, bool> check)
    {
        string path = Path.Combine(Path.GetTempPath(), "a b", "画布.FLOATCANVAS");
        check("shell parses an Explorer positional project with spaces and Unicode", ProjectLaunchRequest.Parse([path]) == Path.GetFullPath(path));
        check("shell parses explicit open alongside isolated profile", ProjectLaunchRequest.Parse(["--profile", "own", "--open", path, "--primary-screen"]) == Path.GetFullPath(path));
        check("startup-only invocation carries no project", ProjectLaunchRequest.Parse(["--startup"]) is null);
        check("isolated and smoke launches cannot register shell integration", !ProjectLaunchRequest.AllowsRegistration(["--profile", "own"]) && !ProjectLaunchRequest.AllowsRegistration(["--smoke"]));
        check("ordinary launches register project type", ProjectLaunchRequest.AllowsRegistration([]));
        foreach (var args in new[] { new[] { "--open" }, new[] { path, path }, new[] { "bad.png" }, new[] { "--unknown" }, new[] { "line\n.floatcanvas" } })
        {
            bool rejected = false;
            try { ProjectLaunchRequest.Parse(args); } catch (ArgumentException) { rejected = true; }
            check("invalid shell arguments rejected: " + string.Join(" ", args).Replace('\n', ' '), rejected);
        }
        string sandbox = @"Software\FloatCanvas.Tests\Shell-" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(sandbox);
            string exe = Path.Combine(Path.GetTempPath(), "应用 folder", "FloatCanvas.exe");
            var shell = new WindowsShellIntegration(root, exe);
            check("fresh Windows startup preference is off", !shell.StartupEnabled);
            shell.SetStartup(true);
            using (var run = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")!)
            {
                check("startup uses quoted executable and quiet startup flag", Equals(run.GetValue("FloatCanvas"), $"\"{exe}\" --startup"));
            }
            check("startup enabled state reads actual registry", shell.StartupEnabled);
            shell.SetStartup(false);
            check("disabling startup removes only the application value", !shell.StartupEnabled);
            shell.RegisterProjectType();
            using (var command = root.OpenSubKey(@"Software\Classes\FloatCanvas.Project\shell\open\command")!)
                check("project association quotes executable and document placeholder", Equals(command.GetValue(""), $"\"{exe}\" --open \"%1\""));
            using (var extension = root.OpenSubKey(@"Software\Classes\.floatcanvas", true)!)
            {
                check("unclaimed extension receives FloatCanvas project type", Equals(extension.GetValue(""), WindowsShellIntegration.ProgId));
                extension.SetValue("", "OtherApp.Project");
                extension.SetValue("Unrelated", "preserve");
            }
            using (var userChoice = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.floatcanvas\UserChoice"))
            { userChoice.SetValue("ProgId", "UserChosen.App"); userChoice.SetValue("Hash", "preserve"); }
            shell.RegisterProjectType();
            using (var extension = root.OpenSubKey(@"Software\Classes\.floatcanvas")!)
                check("repeat registration preserves existing app default and unknown data", Equals(extension.GetValue(""), "OtherApp.Project") && Equals(extension.GetValue("Unrelated"), "preserve"));
            using (var userChoice = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.floatcanvas\UserChoice")!)
                check("registration preserves Windows UserChoice and hash", Equals(userChoice.GetValue("ProgId"), "UserChosen.App") && Equals(userChoice.GetValue("Hash"), "preserve"));
            using (var candidates = root.OpenSubKey(@"Software\Classes\.floatcanvas\OpenWithProgids")!)
                check("FloatCanvas is an Open with candidate", candidates.GetValueNames().Contains(WindowsShellIntegration.ProgId));
            using (var capabilities = root.OpenSubKey(@"Software\FloatCanvas\Capabilities\FileAssociations")!)
                check("Default Apps capabilities advertise the project extension", Equals(capabilities.GetValue(".floatcanvas"), WindowsShellIntegration.ProgId));
            using (var applications = root.OpenSubKey(@"Software\Classes\Applications\FloatCanvas.exe\SupportedTypes")!)
                check("executable supports Always use this app for floatcanvas", applications.GetValueNames().Contains(".floatcanvas"));
            check("file registration leaves startup preference off", !shell.StartupEnabled);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sandbox, false); }
        ActivationAsync(check, path).GetAwaiter().GetResult();
        RecoveredStartup(check, path);
    }

    private static void RecoveredStartup(Action<string, bool> check, string path)
    {
        // Reach the completion used by a successful hotkey-settings retry without
        // constructing a host, showing UI, or registering Windows integration.
        var session = (DesktopSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(DesktopSession).GetField(name, flags)!.SetValue(session, value);
        T Get<T>(string name) => (T)typeof(DesktopSession).GetField(name, flags)!.GetValue(session)!;
        Task Complete() => (Task)typeof(DesktopSession).GetMethod("CompleteStartupAsync", flags)!.Invoke(session, null)!;
        Directory.CreateDirectory(ApplicationPaths.ProfileDirectory);
        using var lifetime = new CancellationTokenSource();
        Set("_documentLifetime", lifetime); Set("_projectOpenRequests", new Queue<string>());
        Set("_quietStartup", true); Set("_documentBusyValue", true);
        session.QueueProjectOpen(path);
        check("project handoff stays queued while startup waits for hotkey recovery", Get<Queue<string>>("_projectOpenRequests").Count == 1 && !Get<bool>("_openingShellProject"));
        Set("_startupPending", true);
        Task completing = Complete();
        check("successful hotkey recovery enables and starts draining the pending project", Get<bool>("_shellReady") && !Get<bool>("_startupPending") && Get<bool>("_openingShellProject") && !completing.IsCompleted);
        check("repeated startup completion cannot launch a duplicate drain", Complete().IsCompletedSuccessfully && Get<Queue<string>>("_projectOpenRequests").Count == 1 && Get<bool>("_openingShellProject"));
        Set("_documentExitRequestedValue", true);
        check("an exiting desktop session rejects new queued projects", !session.QueueProjectOpen(path) && Get<Queue<string>>("_projectOpenRequests").Count == 1);
        lifetime.Cancel();
        completing.GetAwaiter().GetResult();
        check("shutdown unwinds a startup drain without losing its busy request", !Get<bool>("_openingShellProject") && Get<Queue<string>>("_projectOpenRequests").Count == 1);
    }

    private static async Task ActivationAsync(Action<string, bool> check, string path)
    {
        string id = Guid.NewGuid().ToString("N");
        var received = new List<string>();
        using var server = new ProjectActivation(id, value => { lock (received) received.Add(value); });
        check("running instance accepts the first project request", await ProjectActivation.ForwardAsync(id, path));
        string second = Path.Combine(Path.GetTempPath(), "second.floatcanvas");
        check("running instance accepts a subsequent project request", await ProjectActivation.ForwardAsync(id, second));
        lock (received) check("single instance handoff retains exact paths and request order", received.SequenceEqual([Path.GetFullPath(path), Path.GetFullPath(second)]));
        string rejectedId = Guid.NewGuid().ToString("N");
        bool accepting = false;
        using var rejecting = new ProjectActivation(rejectedId, _ => Task.FromResult(accepting));
        check("an exiting receiver rejects handoff instead of acknowledging a dropped project", !await ProjectActivation.ForwardAsync(rejectedId, path));
        accepting = true;
        check("a rejected handoff does not stop the activation listener", await ProjectActivation.ForwardAsync(rejectedId, second));
        check("missing running instance reports a bounded failure", !await ProjectActivation.ForwardAsync(Guid.NewGuid().ToString("N"), path, 100));
    }
}
