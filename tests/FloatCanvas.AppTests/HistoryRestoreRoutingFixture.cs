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
                var chooser = app.Windows.Cast<Window>().FirstOrDefault(w => w.IsVisible && w is WorkspaceHistoryWindow);
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
        bool entryEditing = scenario is not ("history-pass-through" or "history-entry-failure");
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
                    var chooser = app.Windows.Cast<Window>().FirstOrDefault(w => w.IsVisible && w is WorkspaceHistoryWindow);
                    if (chooser is null) return;
                    var list = Descendants(chooser).OfType<ListBox>().Single();
                    if (list.Items.Count == 0 || list.SelectedIndex < 0) return;
                    snapshot("history-chooser-opened");
                    check("history restore opens the actual populated production chooser", host.IsEditing == entryEditing && host.IsKeyboardIsolationActive == entryEditing);
                    stage = 1;
                    if (scenario == "history-pass-through")
                    {
                        check("opening history closes the manually opened welcome window", Field<Window?>(session, "_welcomeWindow") is null);
                        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex);
                        list.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                            Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = item });
                    }
                    else Click(Descendants(chooser).OfType<Button>().Single(button => button.IsDefault));
                    return;
                }
                if (stage == 1)
                {
                    var confirmation = app.Windows.Cast<Window>().FirstOrDefault(w => w.IsVisible && w.Title == WindowTitles.Format(L.Get("App.Name"), !host.IsEditing) &&
                        Descendants(w).OfType<Button>().Any(button => Equals(button.Content, L.Get("Dialog.No"))));
                    if (confirmation is null) return;
                    if (scenario == "history-pass-through")
                        throw new InvalidOperationException("An unbound workspace unexpectedly opened a save confirmation.");
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
                    string key = scenario == "history-cancel" ? "Dialog.Cancel" : scenario == "history-save" ? "Dialog.Yes" : "Dialog.No";
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
            var recoveryHistory = scenario == "history-entry-failure"
                ? (WorkspaceRecoveryHistory)typeof(DesktopSession).GetProperty("RecoveryHistory", Private)!
                    .GetValue(session)!
                : null;
            long entryEpoch = recoveryHistory is null ? 0 : Field<long>(recoveryHistory, "entryEpoch");
            if (scenario == "history-entry-failure")
                typeof(DesktopSession).GetField("_overlaySwitchPending", Private)!.SetValue(session, true);
            var task = (Task)typeof(DesktopSession).GetMethod("RestoreWorkspaceHistoryAsync", Private)!.Invoke(session, null)!;
            Await(task);
            nativeSave?.GetAwaiter().GetResult();
            if (dialogFailure is not null) throw dialogFailure;
            Pump(200); snapshot("history-restore-completed");
            Wait(() => !host.IsEditing || host.HasEditingInput, "restored input lease");
            check("history fixture completes its actual production dialog sequence", stage == (scenario == "history-pass-through" ? 1 : 2));
            if (scenario == "history-pass-through")
            {
                var history = (WorkspaceRecoveryHistory)typeof(DesktopSession).GetProperty("RecoveryHistory", Private)!.GetValue(session)!;
                var archived = history.GetEntries().Where(entry => !entry.IsCurrent)
                    .Select(entry => history.LoadAsync(entry).GetAwaiter().GetResult());
                check("the unbound workspace is archived before replacement",
                    archived.Any(item => item?.State.LiveDocument.Elements.Select(element => element.Id).SequenceEqual([original.Id]) == true));
            }
            if (scenario == "history-cancel")
                check("cancelling history replacement preserves the current document", editor.Session.Document.Elements.Select(element => element.Id).SequenceEqual([original.Id]));
            else if (scenario is not ("history-mode-exit" or "history-external"))
                check("restoring history replaces the live document with the selected package", editor.Session.Document.Elements.Select(element => element.Id).SequenceEqual([historical.Id]));
            if (scenario == "history-save")
            {
                var saved = new ProjectStore().LoadAsync(savePath).GetAwaiter().GetResult();
                check("choosing save writes the actual pre-restoration document into only this fixture directory", saved.Document.Elements.Select(element => element.Id).SequenceEqual([original.Id]));
            }
            if (recoveryHistory is not null)
            {
                Wait(() => Field<long>(recoveryHistory, "entryEpoch") > entryEpoch, "restored recovery baseline");
                check("history persistence starts even when interaction entry fails",
                    Field<long>(recoveryHistory, "entryEpoch") > entryEpoch);
            }
            // ToggleEditing intentionally ignores a global-mode request while a native
            // modal owns input. history-mode-exit therefore remains editing afterward.
            bool expectedEditing = scenario == "history-cancel" ? entryEditing :
                scenario is not ("history-external" or "history-entry-failure");
            check("successful history restoration enters editing with an interactable native toolbar",
                host.IsEditing == expectedEditing && toolbar.IsVisible == expectedEditing &&
                (!expectedEditing || toolbar.IsEnabled && IsWindowEnabled(new WindowInteropHelper(toolbar).Handle)) &&
                !Field<bool>(session, "_documentBusyValue") &&
                !Field<bool>(session, "_documentExitRequestedValue"));
            check("history restore keeps the matching actual keyboard lease without native mouse capture", host.IsKeyboardIsolationActive == expectedEditing && host.CaptureHandle == 0);
            if (scenario is not ("history-cancel" or "history-mode-exit" or "history-external" or "history-entry-failure"))
            {
                Wait(() => WindowRevealGate.IsPresented(toolbar), "restored toolbar reveal");
                var buttons = (Dictionary<EditorTool, Button>)session.GetType()
                    .GetField("_toolbarToolButtons", Private)!.GetValue(session)!;
                var originalTool = editor.Tool;
                var requested = originalTool == EditorTool.Symbol ? EditorTool.Emoji : EditorTool.Symbol;
                var button = buttons[requested];
                toolbar.UpdateLayout();
                var buttonCenter = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), toolbar);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(toolbar);
                nint toolbarHandle = new WindowInteropHelper(toolbar).Handle;
                if (!GetWindowRect(toolbarHandle, out var toolbarRect))
                    throw new InvalidOperationException("Could not read the restored toolbar's native rectangle.");
                var point = new Point(toolbarRect.Left + buttonCenter.X * dpi.DpiScaleX,
                    toolbarRect.Top + buttonCenter.Y * dpi.DpiScaleY);
                var local = button.PointFromScreen(point);
                check("history toolbar click geometry resolves to the requested visible button",
                    button.IsVisible && button.IsEnabled && button.InputHitTest(local) is DependencyObject);
                var target = new FocusSessionRoutingFixture.PixelPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
                Console.WriteLine("HISTORY_TOOLBAR_TARGET " + string.Join(";",
                    app.Windows.Cast<Window>().Select(candidate =>
                    {
                        nint handle = new WindowInteropHelper(candidate).Handle;
                        return $"{candidate.Title}:{handle:X}:visible={candidate.IsVisible}:enabled={candidate.IsEnabled}:" +
                            $"native={IsWindowEnabled(handle)}";
                    }).Append($"canvas={string.Join(',', host.Diagnostics.Select(pair => $"{pair.DisplayHandle:X}/{pair.InputHandle:X}"))}") ));
                Console.WriteLine($"HISTORY_TOOLBAR_POINT requested={requested} screen={point.X:0.##},{point.Y:0.##} " +
                    $"client={buttonCenter.X:0.##},{buttonCenter.Y:0.##} dpi={dpi.DpiScaleX:0.###},{dpi.DpiScaleY:0.###} " +
                    $"native={toolbarRect.Left},{toolbarRect.Top},{toolbarRect.Right},{toolbarRect.Bottom}");
                var physicalTarget = FocusSessionRoutingFixture.WindowAtPhysicalPoint(
                    (int)Math.Round(point.X), (int)Math.Round(point.Y));
                Console.WriteLine("HISTORY_TOOLBAR_PHYSICAL_TARGET expected=" + DescribeWindow(toolbarHandle) +
                    " actual=" + DescribeWindow(physicalTarget) +
                    " root=" + DescribeWindow(GetAncestor(physicalTarget, 2)));
                check("restored toolbar owns its physical button point without a test-only raise",
                    physicalTarget == toolbarHandle);
                int buttonClicks = 0;
                var clickCounter = new RoutedEventHandler((_, _) => buttonClicks++);
                button.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, clickCounter, true);
                try
                {
                    FocusSessionRoutingFixture.DriveOwnPointer(toolbarHandle, target, target,
                        allowDelegatedInput: host.UsesSystemInput && host.HasEditingInput);
                    Pump(150);
                    snapshot("history-toolbar-physical-click");
                    Console.WriteLine($"HISTORY_TOOLBAR_RESULT requested={requested} actual={editor.Tool} clicks={buttonClicks} " +
                        $"editing={host.IsEditing} lease={host.IsKeyboardIsolationActive} toolbar={toolbar.IsVisible}/{toolbar.IsEnabled} " +
                        $"capture={host.CaptureHandle}");
                    check("a physical toolbar click still works after successful history restoration",
                        buttonClicks == 1 && editor.Tool == requested &&
                        host.IsEditing && host.IsKeyboardIsolationActive &&
                        toolbar.IsVisible && toolbar.IsEnabled && host.CaptureHandle == 0);
                }
                finally
                {
                    button.RemoveHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, clickCounter);
                }

                if (scenario == "history-editing")
                {
                    var setEditing = typeof(DesktopSession).GetMethod("SetEditing", Private)!;
                    setEditing.Invoke(session, [false, false]);
                    check("restored canvas can leave editing", !host.IsEditing && !toolbar.IsVisible);
                    check("restored canvas can enter editing again", (bool)setEditing.Invoke(session, [true, false])!);
                    Wait(() => host.IsKeyboardIsolationActive && WindowRevealGate.IsPresented(toolbar), "toolbar after reentry");
                    var next = buttons[originalTool];
                    var center = next.PointToScreen(new Point(next.ActualWidth / 2, next.ActualHeight / 2));
                    var nextPoint = new FocusSessionRoutingFixture.PixelPoint((int)Math.Round(center.X), (int)Math.Round(center.Y));
                    FocusSessionRoutingFixture.DriveOwnPointer(toolbarHandle, nextPoint, nextPoint,
                        allowDelegatedInput: host.UsesSystemInput && host.HasEditingInput);
                    check("the first toolbar click after reentry switches tools",
                        editor.Tool == originalTool && host.IsEditing && host.IsKeyboardIsolationActive);
                }
            }
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
    private static string DescribeWindow(nint handle)
    {
        if (handle == 0 || !IsWindow(handle)) return "0";
        var className = new StringBuilder(128);
        GetClassName(handle, className, className.Capacity);
        GetWindowThreadProcessId(handle, out uint process);
        GetWindowRect(handle, out var bounds);
        return $"{handle:X}[process={process},class={className},visible={IsWindowVisible(handle)},enabled={IsWindowEnabled(handle)}," +
            $"rect={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom},owner={GetWindow(handle, 4):X},root={GetAncestor(handle, 2):X}]";
    }
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
    internal static nint FindOwnNativeDialog()
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
    internal static void FillOwnSaveDialog(nint dialog, string path, bool postCommand = false)
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
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!save.Current.IsEnabled && DateTime.UtcNow < deadline) Thread.Sleep(25);
        if (postCommand)
        {
            if (!save.Current.IsEnabled || !PostMessage(dialog, 0x111, 1, 0)) throw new InvalidOperationException("Own native save command was not available.");
        }
        else ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
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
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
}
