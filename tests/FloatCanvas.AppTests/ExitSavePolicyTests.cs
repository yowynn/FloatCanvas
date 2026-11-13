using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;

internal static class ExitSavePolicyTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Exercise the actual modal confirmation, including its cancel/leave result.
        Case("clean unbound exit", true, false, false, null, null, 0, false);
        Case("dirty unbound exit", true, true, false, null, null, 0, false);
        Case("clean bound exit", true, false, true, null, null, 0, false);
        Case("dirty bound exit cancel", true, true, true, null, null, 1, true);
        Case("dirty bound exit keep history", true, true, true, null, null, 1, false);
        Case("unbound live and capture exit", true, true, false, true, false, 0, false);
        Case("unbound live and bound capture exit cancel", true, true, false, true, true, 1, true);
        Case("bound live and unbound capture exit keep history", true, true, true, true, false, 1, false);
        Case("both bound exit keep history", true, true, true, true, true, 2, false);
        Case("replace dirty unbound workspace cancel", false, true, false, null, null, 1, true);
        Case("replace dirty unbound live and capture keep history", false, true, false, true, false, 2, false);
        ArchiveUnbound(check, app);

        void Case(string name, bool exit, bool dirty, bool bound, bool? captureDirty, bool? captureBound, int expectedPrompts, bool cancel)
        {
            var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
            var files = typeof(DesktopSession).GetField("_documentFiles", Hidden)!;
            files.SetValue(desktop, Activator.CreateInstance(files.FieldType));
            var live = Session(dirty, bound);
            typeof(DesktopSession).GetField("_live", Hidden)!.SetValue(desktop, live);
            if (captureDirty is bool dirtyCapture)
                typeof(DesktopSession).GetField("_capture", Hidden)!.SetValue(desktop, Session(dirtyCapture, captureBound == true));
            int prompts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                foreach (var dialog in app.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray())
                {
                    var buttons = Descendants(dialog).OfType<Button>().ToArray();
                    var leave = buttons.SingleOrDefault(b => Equals(b.Content, FloatCanvas.Localization.AppText.Get("Dialog.No")));
                    if (leave is null) continue;
                    prompts++;
                    var button = cancel ? buttons.Single(b => b.IsCancel) : leave;
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            timer.Start();
            try
            {
                var task = (Task<bool>)Call(desktop, "ConfirmLeaveWorkspaceAsync", exit)!;
                bool result = task.GetAwaiter().GetResult();
                check(name + " prompts only for eligible dirty sessions", prompts == expectedPrompts);
                check(name + " respects cancel/leave", result == !(cancel && expectedPrompts > 0));
                check(name + " never creates a project file without saving", !File.Exists(Path.Combine(ApplicationPaths.ProfileDirectory, "exit-policy-unused.floatcanvas")));
            }
            finally { timer.Stop(); }

            EditorSession Session(bool changed, bool linked)
            {
                var session = new EditorSession();
                var state = Call(desktop, "FileState", session)!;
                state.GetType().GetField("SavedContentVersion")!.SetValue(state, session.ContentVersion);
                if (linked) state.GetType().GetField("Path")!.SetValue(state, Path.Combine(ApplicationPaths.ProfileDirectory, "exit-policy-unused.floatcanvas"));
                if (changed) session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20 });
                return session;
            }
        }
    }

    private static void ArchiveUnbound(Action<string, bool> check, Application app)
    {
        string originalProfile = ApplicationPaths.ProfileDirectory;
        ApplicationPaths.Configure(["--profile", Path.Combine(originalProfile, "exit-archive-" + Guid.NewGuid().ToString("N"))]);
        try
        {
            MonitorInfo[] screens = [new("exit-policy", new(0, 0, 640, 360), new(0, 0, 640, 330), 96, 96, true)];
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
                [OverlayMonitorScope.AllScreens, (Func<IReadOnlyList<MonitorInfo>>)(() => screens), false], null)!;
            using var desktop = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
            var live = Field<EditorSession>(desktop, "_live");
            var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 27, Height = 19 };
            live.Add(element);
            check("unbound archive fixture has no file binding", Call(desktop, "FileState", live)!.GetType().GetField("Path")!.GetValue(Call(desktop, "FileState", live)) is null);
            check("unbound exit confirmation permits normal recovery and archive", Await((Task<bool>)Call(desktop, "ConfirmLeaveWorkspaceAsync", true)!));
            Await((Task)Call(desktop, "FlushRecoveryAsync")!);
            Await((Task)Call(desktop, "ArchiveWorkspaceAsync", false)!);
            var store = Field<RecoveryStore>(desktop, "_recoveryStore");
            var recovered = Await(store.LoadAsync());
            check("unbound exit recovery retains authored artwork", recovered?.State.LiveDocument.Elements.Any(e => e.Id == element.Id && e.Width == 27) == true);
            var history = (WorkspaceRecoveryHistory)typeof(DesktopSession).GetProperty("RecoveryHistory", Hidden)!.GetValue(desktop)!;
            var archive = history.GetEntries().Single(entry => !entry.IsCurrent);
            var restored = Await(history.LoadAsync(archive));
            check("unbound exit history retains recoverable artwork", restored?.State.LiveDocument.Elements.Any(e => e.Id == element.Id && e.Height == 19) == true);
        }
        finally { ApplicationPaths.Configure(["--profile", originalProfile]); }
    }

    private static object? Call(DesktopSession desktop, string name, params object?[] args) =>
        typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, args);
    private static T Field<T>(DesktopSession desktop, string name) => (T)typeof(DesktopSession).GetField(name, Hidden)!.GetValue(desktop)!;
    private static void Await(Task task)
    {
        while (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
