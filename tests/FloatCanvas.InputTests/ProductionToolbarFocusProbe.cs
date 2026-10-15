using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static bool ProductionToolbarFocusProbe;
    private static string ProductionToolbarFocusReport => Path.Combine(Root, "artifacts", "e2e", "production-toolbar-focus.json");
    private static readonly List<ToolbarFocusCheck> ToolbarFocusChecks = [];
    private static readonly List<object> ToolbarFocusStages = [], ToolbarFocusEvents = [];
    private static string? ToolbarFocusImage, ToolbarFocusProject;
    private static object? ToolbarFocusHook, ToolbarFocusImageFixture;

    private static async Task RunProductionToolbarFocusProbe(Wpf.Application app)
    {
        Directory.CreateDirectory(Profile!);
        ToolbarFocusImage = Path.Combine(Profile!, "toolbar-focus-probe.png");
        ToolbarFocusProject = Path.Combine(Profile!, "toolbar-focus-probe.floatcanvas");
        if (ToolbarFocusImage.Concat(ToolbarFocusProject).Any(c => c > 127))
            throw new InvalidOperationException("This explicit actual-keyboard probe requires ASCII fixture paths.");
        string publicImage = Path.Combine(Root, "tests", "FloatCanvas.InputTests", "Fixtures", "toolbar-focus.png");
        File.Copy(publicImage, ToolbarFocusImage, overwrite: true);
        using (var bitmap = SKBitmap.Decode(ToolbarFocusImage) ?? throw new InvalidDataException("The public transparent fixture could not be decoded."))
        {
            ToolbarFocusImageFixture = new { Source = publicImage, CopiedTo = ToolbarFocusImage, bitmap.Width, bitmap.Height,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ToolbarFocusImage))) };
        }
        using var observer = await KeyboardObserverClient.StartAsync();
        await ActivateKeyboardFixture(observer);
        // Match the already verified production-IME fixture entry: finish an exact-child
        // guarded key round-trip before asking the new session to acquire foreground.
        // Merely observing the coordinator's activation can precede its action completing.
        await observer.ResetAsync(); await PulseKeyboardX(observer, editing: false);
        var initialKeys = await observer.SnapshotAsync();
        ToolbarFocusStages.Add(new { Phase = "OwnInitialKeyboardPositiveControl", Snapshot = initialKeys,
            Native = FileDialogNative.Focus("BeforeProductionConstruction") });
        ToolbarFocusRequire(initialKeys.WmDown > 0 && initialKeys.WmUp > 0 && initialKeys.AsyncPresses > 0 && RawPositive(initialKeys),
            "The exact independent child completes a real guarded keyboard round-trip before production editing entry");
        Session = Activator.CreateInstance(DesktopType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [app], null)!;
        KeyboardInputIsolation? isolation = null;
        Wpf.Window? toolbar = null, inspector = null;
        nint toolbarHandle = 0, activeDialog = 0;
        KeyEventHandler? observeKey = null, observeInspectorKey = null;

        bool Busy() => (bool)DesktopType.GetProperty("DocumentOperationBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!;
        string Tool() => Field<object>("_editor").GetType().GetProperty("Tool")!.GetValue(Field<object>("_editor"))!.ToString()!;
        int ObservedA() => ToolbarFocusEvents.Count(e => e is ToolbarFocusKey { Key: "A", Down: true });
        object FocusDescription(object? value) => value is null ? new { Type = "<null>", InToolbar = false, InInspector = false, Label = "" } :
            new { Type = value.GetType().FullName ?? value.GetType().Name,
                InToolbar = value is Wpf.DependencyObject dependency && ReferenceEquals(Wpf.Window.GetWindow(dependency), toolbar),
                InInspector = value is Wpf.DependencyObject item && ReferenceEquals(Wpf.Window.GetWindow(item), inspector),
                Label = value is Button button && button.Content is string label ? label : "" };
        object Snapshot(string phase) => new
        {
            Phase = phase, At = Stopwatch.GetTimestamp(), Tool = Tool(), Busy = Busy(),
            Editing = Host?.IsEditing, Isolation = Host?.IsKeyboardIsolationActive,
            ToolbarHandle = toolbarHandle.ToInt64(), ToolbarEnabled = toolbar?.IsEnabled, ToolbarActive = toolbar?.IsActive,
            ToolbarHasKeyboardFocus = toolbar?.IsKeyboardFocusWithin,
            WpfFocus = FocusDescription(Keyboard.FocusedElement),
            LogicalFocus = FocusDescription(toolbar is null ? null : FocusManager.GetFocusedElement(toolbar)),
            InspectorVisible = inspector?.IsVisible, InspectorEnabled = inspector?.IsEnabled,
            InspectorHasKeyboardFocus = inspector?.IsKeyboardFocusWithin,
            InspectorLogicalFocus = FocusDescription(inspector is null ? null : FocusManager.GetFocusedElement(inspector)),
            Native = FileDialogNative.Focus(phase), ObservedADowns = ObservedA(),
            SuppressedKeys = isolation?.SuppressedCount, Queue = isolation?.QueuedPacketCount,
            ElementCount = Active().Document.Elements.Count
        };
        void PhysicalClick(Wpf.FrameworkElement control, string phase, Wpf.Window? ownerWindow = null)
        {
            if (toolbar is null || !control.IsVisible || !control.IsEnabled) throw new InvalidOperationException("The exact production control is unavailable.");
            nint targetWindow = ownerWindow is null ? toolbarHandle : new WindowInteropHelper(ownerWindow).Handle;
            Wpf.Point screen = control.PointToScreen(new(control.ActualWidth / 2, control.ActualHeight / 2));
            var point = new Native.Point((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            void Guard()
            {
                if (!NativeModalNative.IsOwn(targetWindow) || Native.WindowFromPoint(point) != targetWindow)
                    throw new InvalidOperationException("The physical production button is not under the exact own toolbar HWND; no click injected.");
            }
            Guard(); Native.Move(point); Guard();
            Native.Mouse(2); MouseDown = true;
            try { Guard(); Native.Mouse(4); MouseDown = false; }
            finally { if (MouseDown) { Native.Mouse(4); MouseDown = false; } }
            ToolbarFocusEvents.Add(new { Kind = "ActualButtonClick", Phase = phase, At = Stopwatch.GetTimestamp(), point.X, point.Y,
                ControlType = control.GetType().Name,
                Button = control is Button { Content: string text } ? text : control.ToolTip?.ToString() });
        }
        async Task EnterFileDialog(string title, string path)
        {
            bool appeared = await NativeModalWait(() =>
            {
                var foreground = Native.GetForegroundWindow();
                if (!NativeModalNative.IsOwn(foreground) || NativeModalNative.ClassName(foreground) != "#32770" ||
                    NativeModalNative.Caption(foreground) != title) return false;
                activeDialog = foreground; return true;
            }, 3000);
            ToolbarFocusRequire(appeared, title + ": the original production native dialog appeared");
            ToolbarFocusStages.Add(FileDialogNative.Focus(title + ":Opened"));
            NativeModalChord(observer, activeDialog, [0x12, 0x4E]);
            NativeModalChord(observer, activeDialog, [0x11, 0x41]);
            foreach (var character in path.Select(FileDialogNative.Map))
            {
                NativeModalChord(observer, activeDialog, character.Shift ? [(ushort)0x10, character.VirtualKey] : [character.VirtualKey]);
                await Task.Delay(4);
            }
            bool typed = await NativeModalWait(() => string.Equals(FileDialogNative.FocusText(), path, StringComparison.OrdinalIgnoreCase), 2000);
            ToolbarFocusStages.Add(FileDialogNative.Focus(title + ":AfterTypedPath"));
            ToolbarFocusRequire(typed, title + ": actual guarded keys type the complete fixture path without setting a control value");
            NativeModalChord(observer, activeDialog, [0x0D]);
            await ProductionImeWait(() => Native.GetForegroundWindow() != activeDialog && !Busy(),
                "The production file operation did not leave its dialog and finish.", TimeSpan.FromSeconds(5));
            activeDialog = 0;
        }
        static double ImageAngle(CanvasElement image) => Math.Atan2(image.Transform.M21, image.Transform.M11) * 180 / Math.PI;
        async Task<bool> SaveContainsImage(double? expectedAngle = null)
        {
            if (!File.Exists(ToolbarFocusProject)) return false;
            var loaded = await new ProjectStore().LoadAsync(ToolbarFocusProject);
            var images = loaded.Document.Elements.Where(e => e.Kind == ElementKind.Image).ToArray();
            return images.Length == 1 && (expectedAngle is null || Math.Abs(ImageAngle(images[0]) - expectedAngle.Value) < .001);
        }
        async Task<bool> WaitForSavedImage(long previousWrite, string phase, double? expectedAngle = null)
        {
            long started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
            {
                if (File.Exists(ToolbarFocusProject) && File.GetLastWriteTimeUtc(ToolbarFocusProject).Ticks > previousWrite && !Busy())
                {
                    bool imageSaved = await SaveContainsImage(expectedAngle);
                    // Loading yields the UI thread; recheck after it, not only before the
                    // await. A previous image-containing file cannot satisfy this command.
                    long written = File.GetLastWriteTimeUtc(ToolbarFocusProject).Ticks;
                    if (imageSaved && written > previousWrite && !Busy())
                    {
                        ToolbarFocusStages.Add(new { Phase = phase, PreviousWriteUtcTicks = previousWrite,
                            CompletedWriteUtcTicks = written, Busy = false, ImageSaved = true, ExpectedImageAngle = expectedAngle });
                        return true;
                    }
                }
                await Task.Delay(15);
            }
            ToolbarFocusStages.Add(new { Phase = phase, PreviousWriteUtcTicks = previousWrite,
                CompletedWriteUtcTicks = File.GetLastWriteTimeUtc(ToolbarFocusProject!).Ticks, Busy = Busy(), TimedOut = true });
            return false;
        }

        try
        {
            await CallAsync("InitializeDocumentsAsync", false);
            Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
            isolation = (KeyboardInputIsolation?)typeof(OverlayHost).GetField("_keyboardIsolation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host);
            toolbar = Field<Wpf.Window>("_toolbar");
            ToolbarFocusStages.Add(Snapshot("AfterProductionInitializeBeforeEditing"));
            object? entryResult = Call("SetEditing", true);
            toolbarHandle = new WindowInteropHelper(toolbar).Handle;
            ToolbarFocusStages.Add(new { Phase = "ProductionSetEditingReturned", Result = entryResult,
                Host.IsEditing, Host.IsKeyboardIsolationActive, Host.ForegroundLossVersion, Host.IsVisible,
                IsolationActive = isolation?.IsLeaseActive, LastEndReason = isolation?.LastEndReason?.ToString(),
                IsolationFailure = isolation?.Failure?.ToString(), Status = Field<TextBlock>("_status").Text,
                State = Snapshot("ImmediatelyAfterSetEditing") });
            await Task.Delay(80);
            ToolbarFocusStages.Add(Snapshot("AfterEditingEntrySettled"));
            ToolbarFocusRequire(entryResult is true && Host.IsEditing && Host.IsKeyboardIsolationActive,
                "The actual production session enters isolated editing and remains active after its initial UI turn");
            var row = (StackPanel)((Border)toolbar.Content).Child;
            var blue = row.Children.OfType<Button>().Single(b => b.Content is string label && label == "蓝");
            var import = row.Children.OfType<Button>().Single(b => b.ToolTip?.ToString() == "贴图");
            observeKey = (_, e) => ToolbarFocusEvents.Add(new ToolbarFocusKey(e.Key.ToString(), true, Keyboard.Modifiers.ToString(),
                e.OriginalSource.GetType().FullName ?? "", e.Handled, Stopwatch.GetTimestamp()));
            toolbar.AddHandler(Keyboard.PreviewKeyDownEvent, observeKey, handledEventsToo: true);
            Host.KeyboardIsolationReleased += reason => ToolbarFocusEvents.Add(new { Kind = "IsolationReleased", Reason = reason.ToString(), At = Stopwatch.GetTimestamp() });
            await Task.Delay(80);
            ToolbarFocusRequire(Active().Document.Elements.Count == 0, "The isolated production document begins empty");

            PhysicalClick(blue, "BaselineBlue"); await Task.Delay(40);
            ToolbarFocusStages.Add(Snapshot("BaselineAfterPhysicalBlueClick"));
            int aBefore = ObservedA(); NativeModalChord(observer, toolbarHandle, [0x41]);
            await Task.Delay(100);
            ToolbarFocusRequire(Tool() == "Arrow" && ObservedA() > aBefore,
                "After a real physical blue-button focus, actual A reaches the production toolbar and selects Arrow");
            NativeModalChord(observer, toolbarHandle, [0x11, 0x53]);
            await EnterFileDialog("保存可编辑的浮绘工程", ToolbarFocusProject);
            ToolbarFocusRequire(File.Exists(ToolbarFocusProject) && !await SaveContainsImage(),
                "The same focused production Ctrl+S saves the original empty document through its actual file dialog");
            ToolbarFocusStages.Add(Snapshot("AfterBaselineSaveWithoutRefocus"));

            PhysicalClick(import, "ActualProductionImport");
            await EnterFileDialog("导入图片", ToolbarFocusImage);
            ToolbarFocusRequire(Active().Document.Elements.Count(e => e.Kind == ElementKind.Image) == 1,
                "The real production ImportImagesAsync receives the native file selection and inserts one retained image");
            ToolbarFocusStages.Add(Snapshot("AfterActualImportWithoutRefocus"));
            ToolbarFocusRequire(Host.IsEditing && Host.IsKeyboardIsolationActive && Native.GetForegroundWindow() == toolbarHandle && toolbar.IsEnabled,
                "After import the original enabled production toolbar is foreground without any correcting focus call");

            // Do not click, Activate, SetFocus or Keyboard.Focus between the native dialog and
            // these commands. Clearing focus artificially would not reproduce the product path.
            aBefore = ObservedA(); NativeModalChord(observer, toolbarHandle, [0x41]); await Task.Delay(120);
            bool aAfterImport = Tool() == "Arrow" && ObservedA() > aBefore;
            ToolbarFocusStages.Add(Snapshot("AfterImportActualAWithoutRefocus"));
            ToolbarFocusChecks.Add(new("Without another click after import, actual A reaches the toolbar and selects Arrow", aAfterImport));
            long importWriteBefore = File.GetLastWriteTimeUtc(ToolbarFocusProject).Ticks;
            NativeModalChord(observer, toolbarHandle, [0x11, 0x53]);
            bool savedAfterImport = await WaitForSavedImage(importWriteBefore, "PostImportSaveGeneration");
            ToolbarFocusStages.Add(Snapshot("AfterImportActualCtrlSWithoutRefocus"));
            ToolbarFocusChecks.Add(new("Without another click after import, actual Ctrl+S persists the imported image to the existing project", savedAfterImport));

            PhysicalClick(blue, "RecoveryPhysicalBlue"); await Task.Delay(40);
            aBefore = ObservedA(); NativeModalChord(observer, toolbarHandle, [0x41]); await Task.Delay(100);
            ToolbarFocusRequire(Tool() == "Arrow" && ObservedA() > aBefore,
                "A subsequent real physical blue-button focus makes the same actual A command work");
            long recoveryWriteBefore = File.GetLastWriteTimeUtc(ToolbarFocusProject).Ticks;
            NativeModalChord(observer, toolbarHandle, [0x11, 0x53]);
            ToolbarFocusRequire(await WaitForSavedImage(recoveryWriteBefore, "PhysicalRecoverySaveGeneration"),
                "After that physical refocus the same Ctrl+S persists the retained image");
            ToolbarFocusStages.Add(Snapshot("AfterPhysicalRefocusRecoveryControls"));

            // Preserve the original 16 checks as their own regression scope. This additional
            // route uses the actual Inspector and never clears or assigns its keyboard focus.
            var properties = row.Children.OfType<Button>().Single(b => b.ToolTip?.ToString() == "属性与图层");
            PhysicalClick(properties, "ActualInspectorButton");
            await ProductionImeWait(() => Field<Wpf.Window?>("_inspector")?.IsVisible == true,
                "The actual properties button did not open the production Inspector.", TimeSpan.FromSeconds(2));
            inspector = Field<Wpf.Window>("_inspector");
            nint inspectorHandle = new WindowInteropHelper(inspector).Handle;
            ToolbarFocusRequire(NativeModalNative.IsOwn(inspectorHandle) && inspector.IsVisible,
                "Actual properties-button click opens the original production Inspector", "Inspector");
            observeInspectorKey = (_, e) => ToolbarFocusEvents.Add(new ToolbarFocusKey(e.Key.ToString(), true,
                Keyboard.Modifiers.ToString(), e.OriginalSource.GetType().FullName ?? "", e.Handled, Stopwatch.GetTimestamp(), "Inspector"));
            inspector.AddHandler(Keyboard.PreviewKeyDownEvent, observeInspectorKey, handledEventsToo: true);
            var body = (StackPanel)((ScrollViewer)inspector.Content).Content;
            var reload = body.Children.OfType<WrapPanel>().SelectMany(panel => panel.Children.OfType<Button>())
                .Single(button => button.Content is string label && label == "重载当前选择");
            var apply = body.Children.OfType<WrapPanel>().SelectMany(panel => panel.Children.OfType<Button>())
                .Single(button => button.Content is string label && label == "应用属性");
            PhysicalClick(reload, "InspectorEscapeBaselineControl", inspector); await Task.Delay(40);
            ToolbarFocusStages.Add(Snapshot("InspectorPhysicallyFocusedEscapeBaseline"));
            NativeModalChord(observer, inspectorHandle, [0x1B]);
            ToolbarFocusRequire(await NativeModalWait(() => !inspector.IsVisible, 1000),
                "With real Inspector control focus, actual Escape hides the original Inspector", "Inspector");

            PhysicalClick(properties, "ReopenActualInspectorButton");
            await ProductionImeWait(() => inspector.IsVisible, "The original Inspector did not reopen.", TimeSpan.FromSeconds(2));
            ToolbarFocusRequire(inspector.IsVisible, "The actual properties button reopens the same retained Inspector", "Inspector");
            // Match the observed production sequence instead of saving an unchanged reload:
            // actual physical Angle field focus, real Ctrl+A/3/0, then the original Apply button.
            // Reading the field dictionary obtains a control target; no text or focus is assigned.
            var inspectorFields = (Dictionary<string, TextBox>)inspector.GetType()
                .GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspector)!;
            var angleField = inspectorFields["Angle"];
            var imageBeforeAngle = Active().SelectedElements().Single(element => element.Kind == ElementKind.Image);
            Guid angleImageId = imageBeforeAngle.Id;
            int undoBeforeAngle = Active().UndoCount;
            PhysicalClick(angleField, "InspectorActualAngleField", inspector);
            bool angleFocused = await NativeModalWait(() => ReferenceEquals(Keyboard.FocusedElement, angleField) &&
                Native.GetForegroundWindow() == inspectorHandle, 1000);
            ToolbarFocusStages.Add(new { Phase = "InspectorAfterPhysicalAngleClickObserved", FocusEstablished = angleFocused,
                ActualText = angleField.Text, angleField.SelectionStart, angleField.SelectionLength,
                State = Snapshot("InspectorBeforeActualAngleTyping") });
            if (!angleFocused) throw new InvalidOperationException("The actual mouse click did not establish the original Angle field's keyboard focus; no typing sent.");
            NativeModalChord(observer, inspectorHandle, [0x11, 0x41]);
            NativeModalChord(observer, inspectorHandle, [0x33]);
            NativeModalChord(observer, inspectorHandle, [0x30]);
            bool angleTyped = await NativeModalWait(() => angleField.Text == "30", 1000);
            ToolbarFocusStages.Add(new { Phase = "InspectorAfterActualAngleTypingObserved", ActualText = angleField.Text,
                angleField.SelectionStart, angleField.SelectionLength, State = Snapshot("InspectorAfterActualAngleTyping") });
            ToolbarFocusRequire(angleTyped,
                "Actual guarded Ctrl+A/3/0 enters 30 in the original Inspector Angle field without assigning its text", "InspectorAngleApply");
            PhysicalClick(apply, "InspectorActualApplyThirtyDegrees", inspector);
            bool appliedAngle = await NativeModalWait(() =>
                Active().Document.Elements.SingleOrDefault(element => element.Id == angleImageId) is { } image &&
                Math.Abs(ImageAngle(image) - 30) < .001 && Active().UndoCount == undoBeforeAngle + 1, 1000);
            ToolbarFocusStages.Add(new { Phase = "InspectorActualAngleApplyModel", ImageId = angleImageId,
                RequestedAngle = 30, ActualAngle = ImageAngle(Active().Document.Elements.Single(element => element.Id == angleImageId)),
                UndoBefore = undoBeforeAngle, UndoAfter = Active().UndoCount, State = Snapshot("InspectorAfterActualAngleApply") });
            ToolbarFocusRequire(appliedAngle,
                "The physical production Apply button rotates that retained image to 30 degrees in one undo operation", "InspectorAngleApply");
            long writeBefore = File.GetLastWriteTimeUtc(ToolbarFocusProject).Ticks;
            ToolbarFocusStages.Add(Snapshot("InspectorBeforeActualCtrlS"));
            NativeModalChord(observer, inspectorHandle, [0x11, 0x53]);
            bool inspectorSaved = await WaitForSavedImage(writeBefore, "InspectorChangedImageSaveGeneration", expectedAngle: 30);
            ToolbarFocusRequire(inspectorSaved,
                "Actual Inspector Ctrl+S updates this project generation with the imported image still rotated 30 degrees", "Inspector");
            ToolbarFocusStages.Add(Snapshot("InspectorAfterActualCtrlSWithoutRefocus"));
            ToolbarFocusRequire(inspector.IsEnabled && inspector.IsVisible && Host.IsEditing && Host.IsKeyboardIsolationActive &&
                Native.GetForegroundWindow() == inspectorHandle,
                "The enabled original Inspector remains foreground after save with editing and isolation active", "Inspector");
            NativeModalChord(observer, inspectorHandle, [0x1B]);
            bool inspectorEscAfterSave = await NativeModalWait(() => !inspector.IsVisible, 1000);
            ToolbarFocusStages.Add(Snapshot("InspectorAfterSaveActualEscapeWithoutRefocus"));
            ToolbarFocusChecks.Add(new("Without another click after Inspector Ctrl+S, actual Escape hides the original Inspector",
                inspectorEscAfterSave, "Inspector"));
            if (!inspector.IsVisible)
            {
                PhysicalClick(properties, "ReopenInspectorForPhysicalRecoveryControl");
                await ProductionImeWait(() => inspector.IsVisible, "The recovery Inspector did not reopen.", TimeSpan.FromSeconds(2));
            }
            PhysicalClick(reload, "InspectorPhysicalRecoveryControl", inspector); await Task.Delay(40);
            NativeModalChord(observer, inspectorHandle, [0x1B]);
            ToolbarFocusRequire(await NativeModalWait(() => !inspector.IsVisible, 1000),
                "A subsequent physical Inspector control click makes the same actual Escape work", "Inspector");
            ToolbarFocusStages.Add(Snapshot("InspectorAfterPhysicalRecoveryControl"));
            await CallAsync("FlushRecoveryAsync"); Call("SetEditing", false);
        }
        finally
        {
            foreach (ushort key in NativeModalHeldKeys.ToArray()) { Native.Key(key, true); NativeModalHeldKeys.Remove(key); }
            if (activeDialog != 0) NativeModalNative.CloseOwnDialog(activeDialog);
            if (toolbar is not null && observeKey is not null) toolbar.RemoveHandler(Keyboard.PreviewKeyDownEvent, observeKey);
            if (inspector is not null && observeInspectorKey is not null) inspector.RemoveHandler(Keyboard.PreviewKeyDownEvent, observeInspectorKey);
            if (Session is IDisposable session)
            {
                try { Call("SetEditing", false); }
                finally { session.Dispose(); Session = null; Host = null; }
            }
            if (isolation is not null) ToolbarFocusHook = new { isolation.IsThreadAlive, isolation.IsLeaseActive,
                isolation.SuppressedCount, isolation.QueuedPacketCount, isolation.LastWin32Error, Failure = isolation.Failure?.ToString(),
                PendingAfterDispose = ToolbarFocusDisposedPackets(isolation) };
        }
        ToolbarFocusRequire(isolation is not null && !isolation.IsThreadAlive && isolation.Failure is null && isolation.LastWin32Error == 0,
            "The actual production hook and worker stop cleanly after the toolbar focus probe");
        if (ToolbarFocusChecks.Any(c => !c.Passed)) throw new InvalidOperationException("Production toolbar commands lost keyboard focus after the real import; see independent evidence.");
    }

    private static void ToolbarFocusRequire(bool value, string name, string scope = "Original16")
    { ToolbarFocusChecks.Add(new(name, value, scope)); if (!value) throw new InvalidOperationException(name); }
    private static object[] ToolbarFocusDisposedPackets(KeyboardInputIsolation isolation)
    {
        // Read only after the worker has stopped. These diagnostics distinguish a canceled
        // LeaseEnded notification from undelivered key input; they never drain or mutate it.
        if (isolation.IsThreadAlive) return [];
        object state = typeof(KeyboardInputIsolation).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolation)!;
        var queue = (System.Collections.IEnumerable)state.GetType().GetField("queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        return queue.Cast<KeyboardInputPacket>().Select(packet => (object)new { Kind = packet.Kind.ToString(), packet.Sequence,
            packet.LeaseId, packet.VirtualKey, packet.IsKeyUp, packet.IsLocalBalance, EndReason = packet.EndReason?.ToString() }).ToArray();
    }
    private static void WriteProductionToolbarFocusReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProductionToolbarFocusReport)!);
        File.WriteAllText(ProductionToolbarFocusReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile,
            ProductionDesktopSession = true, FullInputIsolation = false, Checks = ToolbarFocusChecks,
            OriginalToolbarRegression = new { ExpectedCount = 16,
                Checks = ToolbarFocusChecks.Where(check => check.Scope == "Original16").ToArray(),
                Passed = ToolbarFocusChecks.Count(check => check.Scope == "Original16") == 16 &&
                    ToolbarFocusChecks.Where(check => check.Scope == "Original16").All(check => check.Passed) },
            InspectorRegression = new { ExpectedCount = 7,
                Checks = ToolbarFocusChecks.Where(check => check.Scope == "Inspector").ToArray(),
                Passed = ToolbarFocusChecks.Count(check => check.Scope == "Inspector") == 7 &&
                    ToolbarFocusChecks.Where(check => check.Scope == "Inspector").All(check => check.Passed) },
            InspectorAngleApplySetup = new { ExpectedCount = 2,
                Checks = ToolbarFocusChecks.Where(check => check.Scope == "InspectorAngleApply").ToArray(),
                Passed = ToolbarFocusChecks.Count(check => check.Scope == "InspectorAngleApply") == 2 &&
                    ToolbarFocusChecks.Where(check => check.Scope == "InspectorAngleApply").All(check => check.Passed) },
            Stages = ToolbarFocusStages, Events = ToolbarFocusEvents, InputTrace = NativeModalInputTrace,
            Image = ToolbarFocusImage, ImageFixture = ToolbarFocusImageFixture, Project = ToolbarFocusProject, HookAfterDispose = ToolbarFocusHook, Monitors,
            Method = "Original16: real DesktopSession, physical blue/import buttons, original SaveFileDialog/ImportImagesAsync, actual ASCII file input, then A/Ctrl+S without a focus correction; physical blue recovery controls. The checked-in self-created Fixtures/toolbar-focus.png is byte-identical to the public transparent-fixture.png used by the 25-check angle-match report and is copied to the test profile (the old 23-check reload baseline used an 8x8 image). Inspector: physical reload focus establishes the real Escape positive control; then physical Angle-field focus and real Ctrl+A/3/0 enter 30, a physical original Apply click changes the retained image by exactly one undo, and actual Ctrl+S must persist a new project generation with that 30-degree image. Escape must then hide the same Inspector without another click. Original16, Inspector7, and the two changed-document setup checks are reported separately. Native/WPF focus and actual saved projects are read only. No Focus(null), assigned WPF focus, synthetic KeyEvent or changed production bridge is used.",
            Safety = "All programmatic keydowns require the exact own production toolbar/dialog HWND and PID. Physical button points are derived from original WPF controls and verified by WindowFromPoint before input. Only self-created profile PNG/project files are selected or written. Finally closes owned dialog/session and stops the observer and hook.",
            Limitations = new[] { "This is the original production import/save and toolbar shortcut seam on this machine, not all property/text windows or native file-dialog focus states.",
                "The fixture does not intentionally clear focus, alter IME state, steal foreign foreground or repair the observed state before the two post-import assertions.",
                "No game, full mouse isolation, other IME or physical HID coverage is added." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed record ToolbarFocusCheck(string Name, bool Passed, string Scope = "Original16");
    private sealed record ToolbarFocusKey(string Key, bool Down, string Modifiers, string OriginalSource, bool Handled, long At, string WindowScope = "Toolbar");
}
