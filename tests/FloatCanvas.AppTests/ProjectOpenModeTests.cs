using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class ProjectOpenModeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Run --isolated-ui-desktop --project-open-mode-only; this fixture acquires input only on its separate desktop.");
        if (Application.Current is not null) throw new InvalidOperationException("Run project-open mode checks in a fresh process.");
        string originalProfile = ApplicationPaths.ProfileDirectory;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string originalLanguage = L.Preference;
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-project-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            ApplicationPaths.Configure(["--profile", folder, "--primary-screen"]);
            var (toggle, hide) = FreeHotkeys();
            new UserSettings { Theme = "light", Language = "en", Toggle = toggle, Hide = hide,
                PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
            string first = SaveProject(folder, "first", DocumentKind.LiveOverlay);
            string second = SaveProject(folder, "second", DocumentKind.LiveOverlay);
            string capture = SaveProject(folder, "capture", DocumentKind.FrozenCapture);
            var bounds = new PixelBounds(-20000, -20000, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("project-open", bounds, bounds, 96, 96, true)];
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
            if (Environment.GetEnvironmentVariable("FLOATCANVAS_PROJECT_OPEN_TEST_BACKEND") == "enhanced") host.EnableSystemInput();
            using var session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            // Native mode/focus transitions remain real. Ordinary keyboard suppression is
            // unnecessary for replay on this separate desktop and must not affect the user.
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
            ((IDisposable?)isolation.GetValue(host))?.Dispose(); isolation.SetValue(host, null);
            var toolbar = Field<Window>(session, "_toolbar");
            toolbar.Opacity = 0;
            using var entry = new EntryStatusObserver(Field<TextBlock>(session, "_status"));
            long beforeEntry = host.EditingSessionVersion;
            session.Start(["--profile", folder, "--primary-screen", "--open", first]);
            Wait(() => Field<bool>(session, "_shellReady") && !Field<bool>(session, "_openingShellProject") &&
                !Field<bool>(session, "_documentBusyValue"), "cold project startup");
            Observe("cold-startup");
            // A non-input desktop cannot grant ordinary foreground ownership. Observe
            // the actual entry path's success/failure feedback instead of claiming an
            // editing lease that Windows cannot grant to this unattended fixture.
            check("cold project startup loads and reveals its project and requests editing",
                Marker() == "first" && Revealed() && EntryRequested());

            ExitEditing(); Hide(); entry.Reset(); SeedEditing();
            beforeEntry = host.EditingSessionVersion;
            Await((Task)Invoke("OpenProjectPathAsync", second)!);
            Observe("direct-editing");
            check("direct project open from editing requests editing and reveals a previously hidden canvas",
                Marker() == "second" && Revealed() && EntryRequested());
            ExitEditing(); Hide(); entry.Reset(); SeedEditing();
            beforeEntry = host.EditingSessionVersion;
            Queue(first, drainImmediately: true);
            Observe("queued-editing");
            check("project handoff to an editing instance requests editing and opens the eye",
                Marker() == "first" && Revealed() && EntryRequested());

            ExitEditing(); Hide(); entry.Reset();
            long passiveVersion = host.EditingSessionVersion;
            nint passiveForeground = GetForegroundWindow();
            Queue(second);
            Observe("queued-passive");
            check("project handoff to a passive instance reveals the new project without acquiring input",
                Marker() == "second" && Passive() && Revealed() && host.EditingSessionVersion == passiveVersion &&
                GetForegroundWindow() == passiveForeground && entry.Count == 0);
            Hide(); entry.Reset(); Queue(capture);
            Observe("queued-passive-capture");
            check("passive opening retains the newly loaded frozen-capture project instead of returning to live",
                Marker() == "capture" && Editor().Session.Document.Kind == DocumentKind.FrozenCapture &&
                ReferenceEquals(Editor().Session, Field<EditorSession>(session, "_capture")) && Passive() && Revealed() &&
                host.EditingSessionVersion == passiveVersion && entry.Count == 0);
            Hide(); entry.Reset(); Await((Task)Invoke("OpenProjectPathAsync", first)!);
            check("direct project opening from passive mode also preserves input mode and reveals the canvas",
                Marker() == "first" && Passive() && Revealed() && host.EditingSessionVersion == passiveVersion && entry.Count == 0);

            var beforeFailure = Editor().Session;
            Hide();
            using (var response = new DialogResponse(app, "Dialog.OK"))
            {
                Await((Task)Invoke("OpenProjectPathAsync", Path.Combine(folder, "missing.floatcanvas"))!);
                check("failed project opening keeps the previous hidden project and passive mode",
                    response.Count == 1 && ReferenceEquals(Editor().Session, beforeFailure) && !host.IsVisible &&
                    Editor().CanvasContentHidden && Passive() && host.EditingSessionVersion == passiveVersion && entry.Count == 0);
            }
            beforeFailure.Add(new() { Kind = ElementKind.Rectangle, Width = 12, Height = 14 });
            Hide();
            long beforeCancel = beforeFailure.ContentVersion;
            using (var response = new DialogResponse(app, "Dialog.Cancel"))
            {
                Await((Task)Invoke("OpenProjectPathAsync", second)!);
                check("cancelling dirty-project replacement keeps the hidden project and its edits without changing mode",
                    response.Count == 1 && ReferenceEquals(Editor().Session, beforeFailure) &&
                    beforeFailure.ContentVersion == beforeCancel && !host.IsVisible && Editor().CanvasContentHidden &&
                    Passive() && host.EditingSessionVersion == passiveVersion && entry.Count == 0);
            }
            check("project-open mode fixture keeps every overlay within its isolated native bounds",
                host.Diagnostics.All(pair => pair.Bounds == bounds));

            object? Invoke(string method, params object?[] arguments) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(session, arguments);
            EditorController Editor() => Field<EditorController>(session, "_editor");
            string? Marker() => Editor().Session.Document.Elements.FirstOrDefault()?.Text;
            bool Revealed() => host.IsVisible && !host.IsPreviewVisible && !Editor().CanvasContentHidden;
            bool EntryRequested() => entry.Count > 0 || host.EditingSessionVersion != beforeEntry;
            bool Passive() => !host.IsEditing && !host.HasEditingInput && !host.IsKeyboardIsolationActive &&
                !toolbar.IsVisible && host.Diagnostics.All(pair => !pair.InputVisible);
            void Observe(string stage) => Console.WriteLine($"PROJECT_OPEN stage={stage} marker={Marker()} editing={host.IsEditing} input={host.HasEditingInput} visible={host.IsVisible} preview={host.IsPreviewVisible} hiddenContent={Editor().CanvasContentHidden} toolbar={toolbar.IsVisible} focusLoss={host.ForegroundLossVersion} leaseVersion={host.EditingSessionVersion} foreground={GetForegroundWindow():X} entryFeedback={entry.Count} status={Field<TextBlock>(session, "_status").Text}");
            void SeedEditing() => typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
            void ExitEditing() { Invoke("SetEditing", false, false); Pump(); }
            void Hide()
            {
                if (host.IsVisible) Invoke("ToggleCanvasVisibility");
                else Invoke("UpdateHiddenCanvasState");
                Pump();
            }
            void Queue(string path, bool drainImmediately = false)
            {
                if (!session.QueueProjectOpen(path)) throw new InvalidOperationException("Project handoff was rejected.");
                // Capture the seeded caller's mode before the message pump observes
                // that the test desktop has no actual foreground input lease.
                if (drainImmediately) Await((Task)Invoke("DrainProjectOpenRequestsAsync")!);
                Wait(() => Field<Queue<DesktopSession.ProjectOpenRequest>>(session, "_projectOpenRequests").Count == 0 &&
                    !Field<bool>(session, "_openingShellProject") && !Field<bool>(session, "_documentBusyValue"), "queued project open");
            }
        }
        finally
        {
            Pump(); SynchronizationContext.SetSynchronizationContext(oldContext);
            var restore = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) restore.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(restore.ToArray()); L.Initialize(originalLanguage);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string SaveProject(string folder, string marker, DocumentKind kind)
    {
        string path = Path.Combine(folder, marker + ".floatcanvas");
        var document = new SceneDocument { Kind = kind, Viewport = new(0, 0, 640, 360),
            Elements = [new() { Kind = ElementKind.Text, Text = marker, Width = 120, Height = 30 }] };
        new ProjectStore().SaveAsync(path, document, new AssetLibrary()).GetAwaiter().GetResult();
        return path;
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Await(Task task)
    {
        Wait(() => task.IsCompleted, "project operation"); task.GetAwaiter().GetResult(); Pump();
    }
    private static void Wait(Func<bool> completed, string operation)
    {
        var elapsed = Stopwatch.StartNew();
        while (!completed() && elapsed.Elapsed < TimeSpan.FromSeconds(20)) { Pump(); Thread.Sleep(1); }
        if (!completed()) throw new TimeoutException(operation);
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private sealed class EntryStatusObserver : IDisposable
    {
        private readonly TextBlock status;
        private readonly DependencyPropertyDescriptor property;
        private readonly EventHandler changed;
        internal int Count { get; private set; }
        internal EntryStatusObserver(TextBlock status)
        {
            this.status = status;
            property = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            changed = (_, _) =>
            {
                if (status.Text == L.Get("Overlay.FocusAcquisitionFailed") ||
                    status.Text.StartsWith(L.Get("Desktop.EditModePrefix"), StringComparison.Ordinal) ||
                    status.Text.StartsWith(L.Get("Project.OpenedPrefix"), StringComparison.Ordinal)) Count++;
            };
            property.AddValueChanged(status, changed);
        }
        internal void Reset() => Count = 0;
        public void Dispose() => property.RemoveValueChanged(status, changed);
    }
    private sealed class DialogResponse : IDisposable
    {
        private readonly DispatcherTimer timer;
        internal int Count { get; private set; }
        internal DialogResponse(Application app, string key)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                foreach (var window in app.Windows.Cast<Window>().ToArray())
                {
                    if (!window.IsVisible || window.Content is not DockPanel panel) continue;
                    var button = panel.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<Button>())
                        .FirstOrDefault(candidate => Equals(candidate.Content, L.Get(key)));
                    if (button is null) continue;
                    timer.Stop(); Count++; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                }
            };
            timer.Start();
        }
        public void Dispose() => timer.Stop();
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7C; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x4D01, 7, key)) continue;
            try
            {
                if (!RegisterHotKey(0, 0x4D02, 7, key + 1)) continue;
                UnregisterHotKey(0, 0x4D02); return (new(7, key), new(7, key + 1));
            }
            finally { UnregisterHotKey(0, 0x4D01); }
        }
        throw new InvalidOperationException("No free hotkeys for the isolated project-open fixture.");
    }
    private static string DesktopName()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
