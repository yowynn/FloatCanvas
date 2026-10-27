using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Opt-in real native snapshot gestures and actual clean DXGI capture, with an
/// explicit independent profile, real keyboard isolation and guarded own-HWND input only.</summary>
internal static class SnapshotRoutingFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, EditorController editor,
        Window toolbar, string run, string scenario, Action<string> snapshot)
    {
        if (scenario == "snapshot-zero-foreground")
            throw new ArgumentException("snapshot-zero-foreground was retired with foreground-suspending capture. Use snapshot-inplace-background or snapshot-inplace-desktop; historical reports remain unchanged.");
        bool forceLegacy = Environment.GetCommandLineArgs().Contains("--snapshot-force-legacy");
        if (forceLegacy)
        {
            var legacy = typeof(OverlayHost).GetProperty("ForceLegacyCleanCapture", Private)
                ?? throw new InvalidOperationException("This build has no forced legacy clean-capture seam.");
            legacy.SetValue(host, true);
        }
        var primary = host.PrimaryMonitor;
        var area = new PixelBounds(primary.WorkArea.Left + 80, primary.WorkArea.Top + 210, 620, 410);
        var input = host.Diagnostics.Single().InputHandle;
        using var driver = new OwnInput(input, area);
        var captureButton = Field<Dictionary<EditorTool, Button>>(session, "_toolbarToolButtons")[EditorTool.CaptureRegion];
        captureButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // Like the other normal tools: first press selects; another opens its options.
        captureButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Field<ToolOptionsPanel>(session, "_toolOptionsPanel").Refresh();
        var options = Field<Window>(session, "_toolOptionsWindow");
        var pickers = Field<Dictionary<string, SegmentedChoice>>(Field<ToolOptionsPanel>(session, "_toolOptionsPanel"), "pickers");
        check("snapshot production options expose all four selection shapes and three sources", options.IsVisible &&
            pickers["CaptureShape"].Items.Count == 4 && pickers["CaptureSource"].Items.Count == 3 && editor.Tool == EditorTool.CaptureRegion);
        Pump(50);
        var dpi = VisualTreeHelper.GetDpi(options);
        var optionsImage = new RenderTargetBitmap((int)Math.Ceiling(options.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(options.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        optionsImage.Render(options);
        var optionsEncoder = new PngBitmapEncoder(); optionsEncoder.Frames.Add(BitmapFrame.Create(optionsImage));
        using (var optionsFile = File.Create(Path.Combine(run, "snapshot-options-window.png"))) optionsEncoder.Save(optionsFile);
        SystemCommands.CloseWindowCommand.Execute(null, options); Pump(60);
        if (scenario is "snapshot-boundaries" or "snapshot-external" or "snapshot-inplace-boundaries" or "snapshot-inplace-external") { Boundaries(); return; }
        if (scenario is "snapshot-repeat-background" or "snapshot-desktop-background" ||
            scenario.StartsWith("snapshot-inplace-", StringComparison.Ordinal)) { RepeatBackground(); return; }
        foreach (var shape in Enum.GetValues<CaptureShape>())
        {
            Seed(); Configure(shape, CaptureSource.CanvasOnly);
            int count = editor.Session.Document.Elements.Count, undo = editor.Session.UndoCount;
            string original = JsonSerializer.Serialize(editor.Session.Document.Elements.Single());
            driver.Move(20, 20); driver.Down();
            if (shape == CaptureShape.PolygonLasso)
            {
                driver.Up(); driver.Move(240, 20); driver.Click(); driver.Move(220, 180); driver.Click();
                driver.Move(30, 160); driver.Click();
            }
            else
            {
                driver.Move(240, 20); driver.Move(240, 180);
                if (shape == CaptureShape.FreehandLasso) driver.Move(30, 180);
            }
            check("native " + shape + " selection is a transient preview without document or undo changes", editor.HasCaptureGesture &&
                editor.CapturePreview is not null && editor.Session.Document.Elements.Count == count && editor.Session.UndoCount == undo && !editor.Session.IsTransactionActive);
            if (shape == CaptureShape.PolygonLasso) { driver.Key(0x0D, false, allowSuspension: true); driver.Key(0x0D, true, allowSuspension: true); }
            else driver.Up();
            AwaitSnapshot();
            var image = editor.Session.Document.Elements.Last();
            check("native " + shape + " inserts once at original coordinates and selects the new image", image.Kind == ElementKind.Image &&
                editor.Session.Document.Elements.Count == count + 1 && editor.Session.UndoCount == undo + 1 &&
                editor.Session.Selection.SetEquals([image.Id]) && Math.Abs(image.Transform.OffsetX - (area.Left + 20)) < 1 && Math.Abs(image.Transform.OffsetY - (area.Top + 20)) < 1);
            check("native " + shape + " leaves original annotation and live workspace intact", editor.Session.Document.Kind == DocumentKind.LiveOverlay &&
                editor.Session.Document.Background is null && JsonSerializer.Serialize(editor.Session.Document.Elements.First()) == original);
            using var pixels = SKBitmap.Decode(editor.Session.Assets.Get(image.AssetId!).Data.Span);
            check("native " + shape + " PNG contains the selected canvas annotation", pixels.GetPixel(pixels.Width / 2, pixels.Height / 2).Alpha > 0);
            if (shape == CaptureShape.Ellipse)
                check("native ellipse screenshot preserves transparent corners", pixels.GetPixel(0, 0).Alpha == 0);
            File.WriteAllBytes(Path.Combine(run, "snapshot-" + shape.ToString().ToLowerInvariant() + ".png"), editor.Session.Assets.Get(image.AssetId!).Data.ToArray());
            check("native " + shape + " screenshot is one complete undo and redo", editor.Session.Undo() && editor.Session.Document.Elements.Count == count &&
                editor.Session.Redo() && editor.Session.Document.Elements.Count == count + 1);
            check("native " + shape + " completion keeps editing, toolbar and actual isolation", host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible && GetCapture() == 0);
            snapshot("snapshot-" + shape + "-inserted");
        }

        // The actual selected Image is still manipulated by the ordinary creation-tool selection path.
        var selected = editor.Session.SelectedElements().Single().Clone(); var selectedBounds = ElementGeometry.Bounds(selected, false);
        int beforeCount = editor.Session.Document.Elements.Count, beforeUndo = editor.Session.UndoCount;
        driver.MoveWorld(selectedBounds.Center.X, selectedBounds.Center.Y); driver.Down();
        driver.MoveWorld(selectedBounds.Center.X + 18, selectedBounds.Center.Y + 12); driver.Up();
        var moved = editor.Session.Document.Elements.Single(e => e.Id == selected.Id);
        check("native snapshot tool drags its selected image without creating another screenshot", editor.Session.Document.Elements.Count == beforeCount &&
            editor.Session.UndoCount == beforeUndo + 1 && Math.Abs(moved.Transform.OffsetX - selected.Transform.OffsetX - 18) < 1 && Math.Abs(moved.Transform.OffsetY - selected.Transform.OffsetY - 12) < 1);

        Configure(CaptureShape.Rectangle, CaptureSource.CanvasOnly);
        selected = moved.Clone(); selectedBounds = ElementGeometry.Bounds(selected, false); beforeUndo = editor.Session.UndoCount;
        driver.Key(0xA2, false); driver.MoveWorld(selectedBounds.Center.X, selectedBounds.Center.Y); driver.Down();
        driver.MoveWorld(selectedBounds.Center.X + 85, selectedBounds.Center.Y + 55); driver.Key(0xA2, true);
        driver.MoveWorld(selectedBounds.Center.X + 105, selectedBounds.Center.Y + 70); driver.Up(); AwaitSnapshot();
        moved = editor.Session.Document.Elements.Single(e => e.Id == selected.Id);
        check("native Ctrl snapshot retains capture ownership after Ctrl is released mid-drag", editor.Session.Document.Elements.Count == beforeCount + 1 &&
            editor.Session.UndoCount == beforeUndo + 1 && moved.Transform == selected.Transform);

        editor.Session.SetSelection([]); beforeCount = editor.Session.Document.Elements.Count; beforeUndo = editor.Session.UndoCount;
        driver.Move(330, 30); driver.Down(); driver.Move(470, 160); driver.Key(0x1B, false); driver.Key(0x1B, true); driver.Up();
        check("native Escape cancels a pending selection without insertion or edit-mode exit", !editor.HasCaptureGesture && editor.CapturePreview is null &&
            editor.Session.Document.Elements.Count == beforeCount && editor.Session.UndoCount == beforeUndo && host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible);

        // Crucial production path: real DXGI fresh capture preserves the actual editing lease.
        Seed(); Configure(CaptureShape.Rectangle, CaptureSource.Background);
        beforeUndo = editor.Session.UndoCount; long originVersion = host.EditingSessionVersion;
        object? captureState = null;
        var captureObserver = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1) };
        captureObserver.Tick += (_, _) => { captureState ??= NullableField(host, "_captureOperation"); };
        captureObserver.Start();
        try { driver.Move(20, 20); driver.Down(); driver.Move(240, 180); driver.Up(allowSuspension: true); AwaitSnapshot(); }
        finally { captureObserver.Stop(); }
        snapshot("snapshot-real-background-restored");
        Console.WriteLine($"SNAPSHOT_CAPTURE_OUTCOME status={captureState?.GetType().GetProperty("Status")?.GetValue(captureState)} origin={originVersion} restored={host.EditingSessionVersion} count={editor.Session.Document.Elements.Count}");
        check("real desktop snapshot continues its unchanged editing session exactly once", editor.Session.Document.Elements.Count == 2 &&
            editor.Session.Document.Elements.Last().Kind == ElementKind.Image && editor.Session.UndoCount == beforeUndo + 1 &&
            host.EditingSessionVersion == originVersion && host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible && toolbar.IsEnabled);
        check("real desktop screenshot is still a selected image in the same live workspace", editor.Session.Document.Kind == DocumentKind.LiveOverlay &&
            editor.Session.Document.Background is null && editor.Session.Selection.SetEquals([editor.Session.Document.Elements.Last().Id]) && GetCapture() == 0);
        if (scenario == "snapshot-ui")
        {
            Seed(); Configure(CaptureShape.Ellipse, CaptureSource.CanvasOnly);
            captureButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Field<ToolOptionsPanel>(session, "_toolOptionsPanel").Refresh();
            Console.WriteLine($"SNAPSHOT_UI_READY pid={Environment.ProcessId} hwnd={new WindowInteropHelper(options).Handle:X} run={run} timeoutSeconds=150"); Console.Out.Flush();
            var deadline = DateTime.UtcNow.AddSeconds(150);
            while (options.IsVisible && DateTime.UtcNow < deadline) Pump(50);
        }

        void RepeatBackground()
        {
            var arguments = Environment.GetCommandLineArgs();
            bool inPlace = true; // Historical repeat/desktop names are aliases of the current capture contract.
            bool repeatEnter = scenario == "snapshot-inplace-enter-repeat";
            bool desktopBackground = scenario is "snapshot-desktop-background" or "snapshot-inplace-desktop";
            int countIndex = Array.IndexOf(arguments, "--snapshot-repeat-count");
            int rounds = countIndex < 0 ? (repeatEnter ? 1 : 2) : countIndex + 1 < arguments.Length && int.TryParse(arguments[countIndex + 1], out int count) && count is >= 1 and <= 10
                ? count : throw new ArgumentException("--snapshot-repeat-count must be between 1 and 10.");
            CaptureShape[] shapes = repeatEnter ? [CaptureShape.PolygonLasso] : desktopBackground || inPlace
                ? [CaptureShape.Rectangle, CaptureShape.FreehandLasso, CaptureShape.Rectangle, CaptureShape.Ellipse]
                : [CaptureShape.Rectangle, CaptureShape.FreehandLasso, CaptureShape.Rectangle, CaptureShape.PolygonLasso, CaptureShape.Rectangle];
            var outcomes = new List<object>();
            var trace = new List<object>();
            object? operation = null;
            CaptureDiagnosticEvent? suspended = null;
            int number = 0;
            object? desktopEntry = null;
            NativeWindowIdentity? desktopTarget = null;
            int consumedReceipts = 0;
            int focusLosses = 0, leaseEnds = 0, queuedEnterRepeats = 0;
            bool caseActive = false, captureStateStable = true;
            long expectedEpoch = 0;
            Exception? inputError = null;
            void ObserveCaptureState()
            {
                if (inPlace && caseActive && host.IsCapturing &&
                    (!host.IsEditing || !host.IsKeyboardIsolationActive || host.EditingSessionVersion != expectedEpoch)) captureStateStable = false;
                if (inPlace && caseActive && host.IsCapturing && suspended is { Phase: "Ready" } ready &&
                    GetForegroundWindow() != (nint)ready.ExpectedForeground) captureStateStable = false;
            }
            Action focusLost = () => { if (caseActive) focusLosses++; ObserveCaptureState(); };
            Action<KeyboardLeaseEndReason> leaseReleased = _ => { if (caseActive) leaseEnds++; ObserveCaptureState(); };
            Action<CaptureDiagnosticEvent> captureDiagnostic = diagnostic =>
            {
                if (diagnostic.Phase is "Suspended" or "Ready") suspended = diagnostic;
                if (diagnostic.Phase == "Receipt" && diagnostic.Reason == "ReceiptConsumed") consumedReceipts++;
                ObserveCaptureState();
                if (repeatEnter && caseActive && diagnostic.Phase == "Ready" && queuedEnterRepeats == 0)
                {
                    // Queue only repeats of the already-authorized held Enter. Do not
                    // pump from the diagnostic callback or synthesize another gesture.
                    try { for (int i = 0; i < 8; i++) { driver.QueueHeldKeyRepeat(0x0D); queuedEnterRepeats++; } }
                    catch (Exception error) { inputError = error; }
                }
                Record("NativeDiagnostic:" + diagnostic.Phase);
                trace.Add(new
                {
                    Stage = "NativeDiagnosticMetadata", Case = number, Diagnostic = diagnostic,
                    ExpectedWindow = WindowIdentity((nint)diagnostic.ExpectedForeground),
                    SuspendedWindow = WindowIdentity((nint)diagnostic.SuspendedForeground),
                    CurrentWindow = WindowIdentity((nint)diagnostic.CurrentForeground),
                    EventWindow = WindowIdentity((nint)diagnostic.EventForeground)
                });
            };
            void Record(string stage)
            {
                operation ??= NullableField(host, "_captureOperation");
                var foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out uint process);
                trace.Add(new
                {
                    Stage = stage, Case = number, AtUtc = DateTimeOffset.UtcNow, Tick = Environment.TickCount64,
                    Foreground = (long)foreground, ForegroundProcess = process,
                    ForegroundWindow = WindowIdentity(foreground),
                    host.IsCapturing, host.IsEditing, Lease = host.IsKeyboardIsolationActive, host.EditingSessionVersion, host.ForegroundLossVersion,
                    ExternalInputVersion = Property(NullableField(host, "_keyboardIsolation"), "ExternalInputVersion"),
                    ToolbarVisible = toolbar.IsVisible, ToolbarEnabled = toolbar.IsEnabled,
                    CaptureOperation = operation?.GetType().GetProperties().ToDictionary(property => property.Name,
                        property => JsonValue(property.GetValue(operation)))
                });
            }
            void Persist()
            {
                File.WriteAllLines(Path.Combine(run, "snapshot-repeat-trace.jsonl"), trace.Select(value => JsonSerializer.Serialize(value)));
                File.WriteAllText(Path.Combine(run, "snapshot-repeat-results.json"), JsonSerializer.Serialize(new
                {
                    Scenario = scenario, Rounds = rounds, Sequence = shapes.Select(shape => shape.ToString()).ToArray(),
                    Source = CaptureSource.Background.ToString(), BorderWidth = 12.135,
                    ConfigurationPath = "ProductionWpfOptionsAndDropdown", HiddenFailureRecovery = false,
                    DesktopEntry = desktopEntry,
                    ForegroundObservation = "ActualWin32",
                    ExpectedCaptureBehavior = "KeepForegroundEpochAndLease",
                    ForceLegacyStrategy = forceLegacy,
                    Outcomes = outcomes
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (desktopBackground)
            {
                var shell = GetShellWindow();
                var shellIdentity = WindowIdentity(shell);
                desktopTarget = shellIdentity;
                desktopEntry = new { Target = shellIdentity, BeforeTransfer = WindowIdentity(GetForegroundWindow()) };
                Persist();
                check("desktop capture setup resolves a real Explorer Shell desktop", shell != 0 && shellIdentity.Process != Environment.ProcessId &&
                    shellIdentity.Class == "Progman" && shellIdentity.Visible && shellIdentity.Enabled);
                // Exit/hide only this fixture's editing UI. Never minimize, close or hide
                // an external application, including another running FloatCanvas instance.
                Invoke(session, "SetEditing", false, false);
                if (!SetForegroundWindow(shell) && GetForegroundWindow() != shell)
                    throw new InvalidOperationException("Desktop fixture could not activate GetShellWindow. Prepare the desktop explicitly; no external windows were hidden.");
                Wait(() => GetForegroundWindow() == shell, "actual Shell desktop foreground before edit entry", 3);
                var actualDesktop = WindowIdentity(GetForegroundWindow());
                desktopEntry = new { Target = shellIdentity, ActualBeforeEntry = actualDesktop };
                Record("ActualDesktopBeforeEditing"); Persist();
                check("desktop capture enters directly from actual Shell foreground", actualDesktop.Handle == (long)shell &&
                    actualDesktop.Process == shellIdentity.Process && actualDesktop.Thread == shellIdentity.Thread);
                check("desktop capture explicitly enters its production editor from Shell", (bool)Invoke(session, "SetEditing", true, false)!);
                check("desktop capture records the actual Shell as previous foreground", Field<nint>(host, "_previousForeground") == shell);
                Pump(60);
            }
            Seed();
            host.CaptureDiagnostic += captureDiagnostic;
            host.EditingFocusLost += focusLost; host.KeyboardIsolationReleased += leaseReleased;
            try
            {
                for (int round = 0; round < rounds; round++)
                foreach (var shape in shapes)
                {
                    number++; operation = null; suspended = null; consumedReceipts = 0;
                    focusLosses = 0; leaseEnds = 0; queuedEnterRepeats = 0; captureStateStable = true; inputError = null;
                    check("repeated desktop capture " + number + " starts with the same active edit session", host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible);
                    // The selected previous image must not turn this new selection into a move.
                    editor.Session.SetSelection([]);
                    if (!options.IsVisible) { captureButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(30); }
                    check("repeated desktop capture " + number + " opens its actual production options", options.IsVisible);
                    options.Activate();
                    var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel"); panel.Refresh();
                    Select("CaptureShape", shape); Select("CaptureSource", CaptureSource.Background);
                    var width = Field<Dictionary<string, NumericValueField>>(panel, "numericFields")["CaptureBorderWidth"].Input;
                    width.Focus(); width.Text = "12.135"; Pump(30);
                    check("repeated desktop capture " + number + " applies its actual options event path", editor.CaptureShape == shape &&
                        editor.CaptureSource == CaptureSource.Background && Math.Abs(editor.CaptureBorderWidth - 12.135) < .000001);
                    int beforeCount = editor.Session.Document.Elements.Count, beforeUndo = editor.Session.UndoCount;
                    long beforeVersion = host.EditingSessionVersion;
                    long beforeExternalInput = (long)Property(NullableField(host, "_keyboardIsolation"), "ExternalInputVersion")!;
                    expectedEpoch = beforeVersion;
                    bool passed = false; string? error = null; string? lastState = null;
                    var observer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1) };
                    observer.Tick += (_, _) =>
                    {
                        ObserveCaptureState();
                        operation ??= NullableField(host, "_captureOperation");
                        string state = host.IsCapturing + ":" + GetForegroundWindow() + ":" + Property(operation, "Status") + ":" + host.EditingSessionVersion;
                        if (lastState != state) { lastState = state; Record("CapturePoll"); }
                    };
                    Record("BeforeGesture"); caseActive = true; observer.Start();
                    try
                    {
                        // The original native press also dismisses the still-open unpinned
                        // options window. Do not close it or insert a focus-repair delay here.
                        driver.Move(20, 20); driver.Down();
                        if (shape == CaptureShape.PolygonLasso)
                        {
                            driver.Up(); driver.Move(240, 20); driver.Click(); driver.Move(220, 180); driver.Click(); driver.Move(30, 160); driver.Click();
                            driver.Key(0x0D, false, allowSuspension: true);
                            if (repeatEnter)
                                Wait(() => queuedEnterRepeats == 8 || !editor.HasCaptureGesture && Field<Task>(session, "_snapshotCaptureTask").IsCompleted,
                                    "capture Ready while the original Enter remains held", 15);
                            driver.Key(0x0D, true, allowSuspension: true);
                        }
                        else
                        {
                            driver.Move(240, 20); driver.Move(240, 180);
                            if (shape == CaptureShape.FreehandLasso) driver.Move(30, 180);
                            driver.Up(allowSuspension: true);
                        }
                        AwaitSnapshot();
                        if (inputError is not null) throw new InvalidOperationException("Owned Enter repeat injection failed.", inputError);
                        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
                        passed = editor.Session.Document.Elements.Count == beforeCount + 1 && editor.Session.Document.Elements.Last().Kind == ElementKind.Image &&
                            editor.Session.UndoCount == beforeUndo + 1 && editor.Session.Selection.SetEquals([editor.Session.Document.Elements.Last().Id]) &&
                            host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible && toolbar.IsEnabled &&
                            host.EditingSessionVersion == beforeVersion && captureStateStable && focusLosses == 0 && leaseEnds == 0 && suspended?.Phase == "Ready" &&
                            foregroundProcess == Environment.ProcessId && GetCapture() == 0 && consumedReceipts == 1 &&
                            (!inPlace || (long)Property(NullableField(host, "_keyboardIsolation"), "ExternalInputVersion")! == beforeExternalInput) &&
                            (!repeatEnter || queuedEnterRepeats == 8);
                    }
                    catch (Exception failure) { error = failure.ToString(); }
                    finally { observer.Stop(); caseActive = false; Record("AfterGesture"); }
                    bool expectedStrategy = !forceLegacy || suspended?.Strategy == CaptureVisibilityStrategy.LegacyInputOffscreen;
                    var expectedDesktop = suspended is null ? null : WindowIdentity((nint)suspended.ExpectedForeground);
                    bool desktopBoundary = !desktopBackground || inPlace && desktopTarget is not null || expectedDesktop is not null && desktopTarget is not null &&
                        expectedDesktop.Process == desktopTarget.Process && expectedDesktop.Thread == desktopTarget.Thread &&
                        expectedDesktop.Class is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow_WASDK";
                    outcomes.Add(new { Case = number, Round = round + 1, Shape = shape.ToString(), Passed = passed && desktopBoundary && expectedStrategy, Error = error,
                        BeforeCount = beforeCount, AfterCount = editor.Session.Document.Elements.Count, BeforeUndo = beforeUndo,
                        AfterUndo = editor.Session.UndoCount, BeforeVersion = beforeVersion, AfterVersion = host.EditingSessionVersion,
                        DesktopBoundary = desktopBoundary, ExpectedDesktop = expectedDesktop,
                        ConsumedReceipts = consumedReceipts, FocusLosses = focusLosses, LeaseEnds = leaseEnds, CaptureStateStable = captureStateStable,
                        BeforeExternalInput = beforeExternalInput, AfterExternalInput = Property(NullableField(host, "_keyboardIsolation"), "ExternalInputVersion"),
                        QueuedEnterRepeats = queuedEnterRepeats, Suspended = suspended });
                    Persist(); snapshot("repeated-background-" + number + "-" + shape);
                    if (forceLegacy) check("capture " + number + " exercises actual legacy input-offscreen strategy", expectedStrategy);
                    if (desktopBackground) check("desktop capture " + number + " retains its verified Shell desktop origin", desktopBoundary);
                    check("repeated desktop capture " + number + " inserts once and retains editing without hidden recovery", passed);
                }
            }
            finally
            {
                host.CaptureDiagnostic -= captureDiagnostic;
                host.EditingFocusLost -= focusLost; host.KeyboardIsolationReleased -= leaseReleased; Persist();
            }
            void Select(string key, object value)
            {
                var picker = pickers[key]; Pump(30);
                picker.SelectedItem = picker.Items.Cast<RadioButton>().Single(item => Equals(item.Tag, value));
                Pump(30);
            }
        }
        void Seed()
        {
            editor.Cancel();
            editor.Session.Execute("isolated snapshot source setup", d =>
            {
                d.Elements.Clear(); d.Background = null;
                d.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 180, Height = 120,
                    Transform = Affine2D.Translation(area.Left + 40, area.Top + 40), Style = new() { Fill = 0xFFE53935, StrokeWidth = 0 } });
            });
            editor.Session.SetSelection([]);
        }
        void Configure(CaptureShape shape, CaptureSource source)
        {
            var parameters = editor.Parameters; parameters.Tool = EditorTool.CaptureRegion; parameters.CaptureShape = shape;
            parameters.CaptureSource = source; parameters.CaptureBorderWidth = 0; editor.ApplyParameters(parameters); Pump(30);
        }
        void AwaitSnapshot()
        {
            var task = Field<Task>(session, "_snapshotCaptureTask"); Wait(() => task.IsCompleted, "actual production snapshot completion", 15);
            task.GetAwaiter().GetResult(); Pump(60);
        }
        void Reenter()
        {
            host.SetVisible(true); toolbar.Show(); var handle = new WindowInteropHelper(toolbar).Handle;
            if (!SetForegroundWindow(handle) && GetForegroundWindow() != handle) throw new InvalidOperationException("Could not enter the next independent snapshot case.");
            SetFocus(handle);
            check("a distinct snapshot boundary case explicitly enters its own editor", (bool)Invoke(session, "SetEditing", true, false)!);
            Pump(60);
        }
        void Boundaries()
        {
            bool inPlace = true; // Historical boundary names use the same current capture contract.
            bool externalOnly = scenario is "snapshot-external" or "snapshot-inplace-external";
            Process? receiver = null;
            try
            {
                if (!externalOnly)
                {
                    Seed(); editor.Session.Execute("empty snapshot source", document => document.Elements.Clear());
                    Configure(CaptureShape.Rectangle, CaptureSource.CanvasOnly);
                    var parameters = editor.Parameters; parameters.CaptureBorderWidth = 3; editor.ApplyParameters(parameters);
                    int borderUndo = editor.Session.UndoCount;
                    driver.Move(20, 20); driver.Down(); driver.Move(240, 180); driver.Up(); AwaitSnapshot();
                    var bordered = editor.Session.Document.Elements.Single();
                    using var borderPixels = SKBitmap.Decode(editor.Session.Assets.Get(bordered.AssetId!).Data.Span);
                    check("native canvas-only empty selection inserts its configured three-pixel border with transparent interior", bordered.Kind == ElementKind.Image &&
                        editor.Session.UndoCount == borderUndo + 1 && borderPixels.GetPixel(borderPixels.Width / 2, borderPixels.Height / 2).Alpha == 0 &&
                        borderPixels.GetPixel(1, borderPixels.Height / 2).Alpha > 0 && bordered.Width > 220 && bordered.Height > 160);
                }
                foreach (string reason in externalOnly ? new[] { "external" } : new[] { "exit", "hide", "document", "external" })
                {
                    if (!host.IsEditing) Reenter();
                    Seed(); Configure(CaptureShape.Rectangle, CaptureSource.Background);
                    int count = editor.Session.Document.Elements.Count;
                    long originalVersion = host.EditingSessionVersion;
                    nint receiverHandle = 0;
                    if (reason == "external")
                    {
                        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                        start.ArgumentList.Add("--focus-external-helper"); start.ArgumentList.Add("--focus-helper-directory"); start.ArgumentList.Add(run);
                        receiver = Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned external receiver.");
                        Wait(() => File.Exists(Path.Combine(run, "external-ready.json")), "owned external receiver");
                        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "external-ready.json")));
                        if (ready.RootElement.GetProperty("Process").GetInt32() != receiver.Id) throw new InvalidDataException("Wrong external receiver process.");
                        receiverHandle = (nint)ready.RootElement.GetProperty("Handle").GetInt64();
                        // The independent receiver must not compete for test foreground
                        // before the explicit external-switch injection.
                        ShowWindow(receiverHandle, 0);
                        AllowSetForegroundWindow((uint)receiver.Id);
                    }
                    bool injected = false; Exception? timerError = null;
                    var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1) };
                    timer.Tick += (_, _) =>
                    {
                        if (!host.IsCapturing) return;
                        timer.Stop(); injected = true;
                        try
                        {
                            if (reason == "exit") Invoke(session, "SetEditing", false, false);
                            else if (reason == "hide") Invoke(session, "ToggleCanvasVisibility");
                            else if (reason == "document") editor.Session.ReplaceDocument(new() { Viewport = editor.Session.Document.Viewport });
                            else
                            {
                                ShowWindow(receiverHandle, 8);
                                if (!SetForegroundWindow(receiverHandle) && GetForegroundWindow() != receiverHandle) throw new InvalidOperationException("Could not activate own external receiver.");
                                // Cross-thread SetForegroundWindow may return before the
                                // receiver processes activation. Observe the actual target;
                                // an immediate read can report the preceding foreground.
                                Wait(() => GetForegroundWindow() == receiverHandle, "actual external receiver foreground", 2);
                                check("external capture receiver owns its actual independent foreground", GetForegroundWindow() == receiverHandle);
                                if (inPlace)
                                {
                                    SendOwnedReceiverKey(receiverHandle, receiver!.Id, 0x75); // F6 has no text or system command semantics.
                                    Wait(() => ReceiverSawF6(Path.Combine(run, "external-input.json")),
                                        "actual external receiver observes both F6 key events", 2);
                                }
                            }
                            snapshot("snapshot-cancel-during-capture-" + reason);
                        }
                        catch (Exception error) { timerError = error; }
                    };
                    timer.Start();
                    try { driver.Move(20, 20); driver.Down(); driver.Move(240, 180); driver.Up(allowSuspension: true); AwaitSnapshot(); }
                    finally { timer.Stop(); }
                    if (timerError is not null) throw new InvalidOperationException("Snapshot boundary " + reason, timerError);
                    check(reason + " is observed while the actual native capture scope is active", injected);
                    if (inPlace && reason == "document")
                        check("a changed document cancels insertion while preserving its healthy edit session", host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible &&
                            host.EditingSessionVersion == originalVersion && editor.Session.Document.Elements.Count == 0);
                    else
                        check("late snapshot does not insert or reawaken after " + reason, !host.IsEditing && !host.IsKeyboardIsolationActive && !toolbar.IsVisible &&
                            editor.Session.Document.Elements.Count == (reason == "document" ? 0 : count));
                    if (reason == "hide") check("explicit hide survives capture disposal", !host.IsVisible);
                    if (reason == "external") check("late capture keeps its external receiver foreground", GetForegroundWindow() == receiverHandle);
                    if (inPlace && reason == "external")
                    {
                        using var received = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "external-input.json")));
                        check("real external input fails open during clean capture", received.RootElement.GetProperty("F6Down").GetInt32() == 1 &&
                            received.RootElement.GetProperty("F6Up").GetInt32() == 1);
                    }
                    snapshot("snapshot-boundary-finished-" + reason);
                }
            }
            finally
            {
                if (receiver is not null)
                {
                    File.WriteAllText(Path.Combine(run, "external-exit"), "exit");
                    Wait(() => receiver.HasExited, "owned snapshot receiver shutdown"); receiver.Dispose();
                }
            }
        }
    }
    private static object? NullableField(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static object? Property(object? instance, string name) => instance?.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    private sealed record NativeWindowIdentity(long Handle, uint Process, uint Thread, string Class, bool Visible, bool Enabled);
    private static NativeWindowIdentity WindowIdentity(nint handle)
    {
        uint thread = GetWindowThreadProcessId(handle, out uint process);
        var name = new StringBuilder(256); GetClassNameW(handle, name, name.Capacity);
        return new((long)handle, process, thread, name.ToString(), IsWindowVisible(handle), IsWindowEnabled(handle));
    }
    private static object? JsonValue(object? value) => value switch
    {
        nint handle => (long)handle,
        IEnumerable<nint> handles => handles.Select(handle => (long)handle).ToArray(),
        _ => value
    };
    private static void SendOwnedReceiverKey(nint target, int process, ushort key)
    {
        if (target == 0 || GetForegroundWindow() != target || GetWindowThreadProcessId(target, out uint actual) == 0 || actual != process)
            throw new InvalidOperationException("External test input requires its exact independently owned receiver foreground.");
        NativeInput[] pair =
        [
            new() { Type = 1, Data = new() { Keyboard = new() { Key = key } } },
            new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = 2 } } }
        ];
        uint sent = SendInput(2, pair, Marshal.SizeOf<NativeInput>());
        if (sent != 2)
        {
            int error = Marshal.GetLastWin32Error();
            if (sent == 1) SendInput(1, [pair[1]], Marshal.SizeOf<NativeInput>());
            throw new System.ComponentModel.Win32Exception(error);
        }
        Pump(20);
    }
    private static bool ReceiverSawF6(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var received = JsonDocument.Parse(file);
            return received.RootElement.GetProperty("F6Up").GetInt32() > 0;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }
    private static T Field<T>(object instance, string name) => (T)NullableField(instance, name)!;
    private static object? Invoke(object instance, string name, params object[] arguments) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, arguments);
    private static void Wait(Func<bool> done, string operation, int seconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(operation); Pump(10); }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private sealed class OwnInput : IDisposable
    {
        private readonly nint target, previousDpi;
        private readonly PixelBounds area;
        private readonly PixelPoint original;
        private bool held, control;
        private readonly HashSet<ushort> heldKeys = [];
        internal OwnInput(nint target, PixelBounds area) { this.target = target; this.area = area; previousDpi = SetThreadDpiAwarenessContext(-4); GetCursorPos(out original); }
        internal void Move(int x, int y) => MoveWorld(area.Left + x, area.Top + y);
        internal void MoveWorld(double x, double y)
        {
            Check(); var point = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
            if (!area.Contains(new(point.X, point.Y)) || WindowFromPhysicalPoint(point) != target) throw new InvalidOperationException("Snapshot input stopped outside its bounded own canvas ROI.");
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Send(new() { Type = 0, Data = new() { Mouse = new() { X = (int)Math.Round((x - left) * 65535 / (width - 1)), Y = (int)Math.Round((y - top) * 65535 / (height - 1)), Flags = 0xC001 } } });
        }
        internal void Down() { Send(new() { Type = 0, Data = new() { Mouse = new() { Flags = 2 } } }); held = true; }
        internal void Up(bool allowSuspension = false) { Send(new() { Type = 0, Data = new() { Mouse = new() { Flags = 4 } } }, allowSuspension); held = false; }
        internal void Click() { Down(); Up(); }
        internal void Key(ushort key, bool up, bool allowSuspension = false)
        {
            bool releaseOwnTail = up && heldKeys.Contains(key);
            // Enter down can legitimately start clean capture and suspend our foreground.
            // Only its already-injected matching release may finish across that boundary.
            if (!releaseOwnTail) Check();
            if (SendInput(1, [new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } }], Marshal.SizeOf<NativeInput>()) != 1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (up) heldKeys.Remove(key); else heldKeys.Add(key);
            if (key == 0xA2) control = !up;
            Pump(allowSuspension ? 3 : 40);
            if (!allowSuspension && !releaseOwnTail) Check();
        }
        internal void QueueHeldKeyRepeat(ushort key)
        {
            if (!heldKeys.Contains(key)) throw new InvalidOperationException("A repeat requires its own already-injected held key.");
            Check();
            if (SendInput(1, [new() { Type = 1, Data = new() { Keyboard = new() { Key = key } } }], Marshal.SizeOf<NativeInput>()) != 1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private void Send(NativeInput input, bool allowSuspension = false)
        {
            Check();
            if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Pump(allowSuspension ? 3 : 40); if (!allowSuspension) Check();
        }
        private void Check()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
            if (process != Environment.ProcessId || GetCapture() is var capture && capture != 0 && capture != target)
                throw new InvalidOperationException("Snapshot fixture lost its own foreground/capture; no more input will be sent.");
        }
        public void Dispose()
        {
            foreach (var key in heldKeys.ToArray()) Key(key, true, allowSuspension: true);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
            if (process == Environment.ProcessId) { if (held) Up(); if (control) Key(0xA2, true); SetCursorPos(original.X, original.Y); }
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);
}
