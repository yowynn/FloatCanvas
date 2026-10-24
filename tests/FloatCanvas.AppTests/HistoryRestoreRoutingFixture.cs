using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Actual history chooser, unsaved confirmation and recovery package replacement.
/// Called only from the isolated production focus fixture; no user profile or input is used.</summary>
internal static class HistoryRestoreRoutingFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, Application app, DesktopSession session, OverlayHost host,
        EditorController editor, Window toolbar, string profile, string run, string scenario, Action<string> snapshot, Action attachWindows)
    {
        var historical = new CanvasElement { Kind = ElementKind.Ellipse, Width = 55, Height = 35, Transform = Affine2D.Translation(80, 70) };
        var original = new CanvasElement { Kind = ElementKind.Rectangle, Width = 75, Height = 45, Transform = Affine2D.Translation(180, 110) };
        var recovery = Field<RecoveryStore>(session, "_recoveryStore");
        Await(recovery.SaveAsync(new WorkspaceState { LiveDocument = new SceneDocument { Elements = [historical], Viewport = new(0, 0, 640, 360) } }, new AssetLibrary()));
        editor.Session.Add(original);
        check("history fixture seeds a real recovery package inside only its unique profile", recovery.DirectoryPath.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(recovery.CurrentPath));
        if (scenario == "history-preview-ui")
        {
            var assets = new AssetLibrary();
            for (int index = 0; index < 3; index++)
            {
                var element = index switch
                {
                    0 => new CanvasElement { Kind = ElementKind.Rectangle, Width = 310, Height = 175, Transform = Affine2D.Translation(145, 80), Style = new() { Stroke = 0xFFE53935, Fill = 0x30E53935, StrokeWidth = 10 } },
                    1 => new CanvasElement { Kind = ElementKind.Arrow, Width = 270, Height = 160, Points = [new(0, 0), new(270, 160)], Transform = Affine2D.Translation(160, 70), Style = new() { Stroke = 0xFF24A378, StrokeWidth = 14 } },
                    _ => new CanvasElement { Kind = ElementKind.Text, Text = "FloatCanvas", Width = 440, Height = 100, Transform = Affine2D.Translation(110, 100), Style = new() { Stroke = 0xFF2383E2, FontSize = 60 } }
                };
                var state = new WorkspaceState { LiveDocument = new SceneDocument { Viewport = new(0, 0, 640, 360), Elements = [element] } };
                var historyStore = new RecoveryStore(Path.Combine(recovery.DirectoryPath, "history", "visual-seed-" + index));
                byte[]? preview = index == 1 ? null : WorkspacePreview.Render(state.LiveDocument, assets);
                Await(historyStore.SaveWithPreviewAsync(state, assets, preview));
                File.SetLastWriteTimeUtc(historyStore.CurrentPath, DateTime.UtcNow.AddHours(-index - 1));
            }
            var heldTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            bool observed = false; DateTime visualDeadline = DateTime.UtcNow.AddSeconds(150);
            heldTimer.Tick += (_, _) =>
            {
                var chooser = app.Windows.Cast<Window>().FirstOrDefault(w => w.IsVisible && w.Title == L.Get("Recovery.HistoryTitle"));
                if (chooser is null) return;
                attachWindows();
                if (!observed)
                {
                    observed = true; snapshot("history-preview-ui-ready");
                    Console.WriteLine($"HISTORY_PREVIEW_UI_READY pid={Environment.ProcessId} hwnd={new WindowInteropHelper(chooser).Handle:X} title={chooser.Title} profile={profile} entries=4 timeoutSeconds=150"); Console.Out.Flush();
                }
                if (DateTime.UtcNow >= visualDeadline) { heldTimer.Stop(); chooser.Close(); }
            };
            heldTimer.Start();
            try
            {
                Await((Task)typeof(DesktopSession).GetMethod("RestoreWorkspaceHistoryAsync", Private)!.Invoke(session, null)!, 165);
                check("the isolated visual review used the actual production history chooser", observed);
            }
            finally { heldTimer.Stop(); }
            return;
        }
        bool entryEditing = scenario != "history-pass-through";
        if (!entryEditing) typeof(DesktopSession).GetMethod("SetEditing", Private)!.Invoke(session, [false, false]);
        if (!entryEditing) typeof(DesktopSession).GetMethod("OpenWelcome", Private)!.Invoke(session, [true]);
        Process? external = null; nint externalHandle = 0;
        if (scenario == "history-external")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--focus-external-helper"); start.ArgumentList.Add("--focus-helper-directory"); start.ArgumentList.Add(run);
            external = Process.Start(start) ?? throw new InvalidOperationException("Could not start own external receiver.");
            Wait(() => File.Exists(Path.Combine(run, "external-ready.json")), "external receiver readiness");
            using var ready = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "external-ready.json")));
            if (ready.RootElement.GetProperty("Process").GetInt32() != external.Id) throw new InvalidDataException("Mismatched own external receiver.");
            externalHandle = (nint)ready.RootElement.GetProperty("Handle").GetInt64();
        }
        int stage = 0; Exception? dialogFailure = null;
        Task? nativeSave = null; string savePath = Path.Combine(run, "current-before-restoration.floatcanvas");
        if (scenario != "history-pass-through")
        {
            Await(new ProjectStore().SaveAsync(savePath, new SceneDocument(), new AssetLibrary()));
            var binding = typeof(DesktopSession).GetMethod("FileState", Private)!.Invoke(session, [editor.Session])!;
            binding.GetType().GetField("Path")!.SetValue(binding, savePath);
        }
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        timer.Tick += (_, _) =>
        {
            try
            {
                attachWindows();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Production history dialog sequence did not complete.");
                if (stage == 0)
                {
                    var chooser = app.Windows.Cast<Window>().FirstOrDefault(w => w.IsVisible && w.Title == L.Get("Recovery.HistoryTitle"));
                    if (chooser is null) return;
                    snapshot("history-chooser-opened");
                    check("history restore opens the actual populated production chooser", Descendants(chooser).OfType<ListBox>().Single().Items.Count > 0 && host.IsEditing == entryEditing && host.IsKeyboardIsolationActive == entryEditing);
                    stage = 1;
                    if (scenario == "history-pass-through")
                    {
                        check("opening history closes the manually opened welcome window", Field<Window?>(session, "_welcomeWindow") is null);
                        var list = Descendants(chooser).OfType<ListBox>().Single();
                        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex);
                        list.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                            Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = item });
                    }
                    else Click(Descendants(chooser).OfType<Button>().Single(button => button.IsDefault));
                    return;
                }
                if (stage == 1)
                {
                    var confirmation = app.Windows.Cast<Window>().FirstOrDefault(w => w.IsVisible && w.Title == L.Get("App.Name") &&
                        Descendants(w).OfType<Button>().Any(button => Equals(button.Content, L.Get("History.KeepLeave"))));
                    if (confirmation is null) return;
                    snapshot("history-unsaved-confirmation-opened");
                    check("history replacement reaches the actual unsaved confirmation without changing its entry mode", host.IsEditing == entryEditing && host.IsKeyboardIsolationActive == entryEditing);
                    if (scenario == "history-mode-exit")
                    {
                        var toggle = (Action)typeof(OverlayHost).GetField("ToggleRequested", Private)!.GetValue(host)!;
                        toggle(); snapshot("history-explicit-mode-exit-before-late-confirmation");
                    }
                    if (scenario == "history-external")
                    {
                        if (!AllowSetForegroundWindow((uint)external!.Id)) throw new InvalidOperationException("Own external foreground grant failed.");
                        File.WriteAllText(Path.Combine(run, "external-activate"), "activate");
                        Wait(() => GetForegroundWindow() == externalHandle && !host.IsEditing, "external foreground release");
                        snapshot("history-external-foreground-before-late-confirmation");
                    }
                    string key = scenario == "history-cancel" ? "Dialog.Cancel" : scenario == "history-save" ? "History.SaveLeave" : "History.KeepLeave";
                    stage = 2;
                    Click(Descendants(confirmation).OfType<Button>().Single(button => Equals(button.Content, L.Get(key))));
                    return;
                }
                if (stage == 2 && scenario == "history-save")
                {
                    nint dialog = FindOwnNativeDialog();
                    if (dialog == 0) return;
                    snapshot("history-native-save-dialog-opened");
                    stage = 3;
                    nativeSave = Task.Run(() =>
                    {
                        try { FillOwnSaveDialog(dialog, savePath); }
                        catch { PostMessage(dialog, 0x10, 0, 0); throw; }
                    });
                }
            }
            catch (Exception error)
            {
                dialogFailure = error; timer.Stop();
                foreach (var window in app.Windows.Cast<Window>().Where(w => !ReferenceEquals(w, toolbar) && w.IsVisible).ToArray()) window.Close();
                nint native = FindOwnNativeDialog(); if (native != 0) PostMessage(native, 0x10, 0, 0);
            }
        };
        timer.Start();
        try
        {
            var task = (Task)typeof(DesktopSession).GetMethod("RestoreWorkspaceHistoryAsync", Private)!.Invoke(session, null)!;
            Await(task);
            nativeSave?.GetAwaiter().GetResult();
            if (dialogFailure is not null) throw dialogFailure;
            Pump(200); snapshot("history-restore-completed");
            check("history fixture completes its actual production dialog sequence", stage == (scenario == "history-pass-through" ? 1 : 2));
            if (scenario == "history-cancel")
                check("cancelling history replacement preserves the current document", editor.Session.Document.Elements.Select(element => element.Id).SequenceEqual([original.Id]));
            else if (scenario is not ("history-mode-exit" or "history-external"))
                check("restoring history replaces the live document with the selected package", editor.Session.Document.Elements.Select(element => element.Id).SequenceEqual([historical.Id]));
            if (scenario == "history-save")
            {
                var saved = new ProjectStore().LoadAsync(savePath).GetAwaiter().GetResult();
                check("choosing save writes the actual pre-restoration document into only this fixture directory", saved.Document.Elements.Select(element => element.Id).SequenceEqual([original.Id]));
            }
            bool expectedEditing = scenario == "history-cancel" ? entryEditing : scenario is not ("history-mode-exit" or "history-external");
            check("successful history restoration enters editing with its toolbar", host.IsEditing == expectedEditing && toolbar.IsVisible == expectedEditing && toolbar.IsEnabled);
            check("history restore keeps the matching actual keyboard lease without native mouse capture", host.IsKeyboardIsolationActive == expectedEditing && host.CaptureHandle == 0);
            if (scenario == "history-external") check("late history completion leaves the external receiver in foreground", GetForegroundWindow() == externalHandle);
        }
        finally
        {
            timer.Stop();
            if (external is not null)
            {
                File.WriteAllText(Path.Combine(run, "external-exit"), "exit");
                Wait(() => external.HasExited, "owned external receiver shutdown"); external.Dispose();
            }
        }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", Private)!.Invoke(button, null);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is Window window && window.Content is DependencyObject content) { yield return content; foreach (var item in Descendants(content)) yield return item; }
        else foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static void Await(Task task, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!task.IsCompleted) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("History restoration operation timed out."); Pump(25); }
        task.GetAwaiter().GetResult();
    }
    private static void Wait(Func<bool> done, string operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(operation); Pump(25); }
    }
    private static nint FindOwnNativeDialog()
    {
        nint found = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint process);
            var name = new StringBuilder(128); GetClassName(handle, name, name.Capacity);
            if (process == Environment.ProcessId && IsWindowVisible(handle) && name.ToString() == "#32770") { found = handle; return false; }
            return true;
        }, 0);
        return found;
    }
    private static void FillOwnSaveDialog(nint dialog, string path)
    {
        GetWindowThreadProcessId(dialog, out uint process);
        if (process != Environment.ProcessId) throw new InvalidOperationException("Native save target is not owned by this fixture.");
        var root = AutomationElement.FromHandle(dialog);
        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
        bool IsFileName(AutomationElement edit)
        {
            for (var node = edit; node is not null && !Automation.Compare(node, root); node = TreeWalker.ControlViewWalker.GetParent(node))
                if (node.Current.AutomationId is "FileNameControlHost" or "1148") return true;
            return false;
        }
        var target = edits.SingleOrDefault(IsFileName) ?? throw new InvalidOperationException("Could not identify the own save filename field: " + string.Join(';', edits.Select(e => e.Current.AutomationId + ":" + e.Current.Name)));
        ((ValuePattern)target.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);
        var save = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.AutomationIdProperty, "1")))
            ?? throw new InvalidOperationException("Could not identify own native save button.");
        ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private delegate bool EnumWindow(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
}
