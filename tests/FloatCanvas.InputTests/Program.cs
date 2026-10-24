using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private static readonly Type DesktopType = Type.GetType("FloatCanvas.App.DesktopSession, FloatCanvas", true)!;
    private static readonly Type ToolType = Type.GetType("FloatCanvas.App.EditorTool, FloatCanvas", true)!;
    private static readonly List<object> Checks = [], ToolEvidence = [];
    private static readonly List<double> ToggleMilliseconds = [];
    private static readonly List<object> KeyboardReleaseEvents = [];
    private static long RunDiagnosticStart;
    private static readonly HashSet<ushort> HeldModifiers = [];
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string Report => Path.Combine(Root, "artifacts", "e2e", "input-tests.json");
    private static string RotationProbeReport => Path.Combine(Root, "artifacts", "e2e", "rotation-probe.json");
    private static bool RotationProbe;
    private static bool SuppressXProbe;
    private static bool ProbeGestureActive;
    private static string? ProbeInputViolation;
    private static string? Profile, Failure;
    private static NativeTestWindow? Lower;
    private static OverlayHost? Host;
    private static object? Session;
    private static bool MouseDown;
    private static long PointerGuards, KeyboardGuards, PointerInjections, KeyboardInjections;
    private static double IdleCpuPercentOfMachine, IdleCpuPercentOfOneCore;
    private static long IdleFrames;
    private static int LongLineMoves, LongLinePoints;
    private static IReadOnlyList<MonitorInfo> Monitors = [];
    private static Native.Point Anchor;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--presentation-logic-only", StringComparer.Ordinal)) return PresentationMeasurements.RunLogicChecks();
        if (args.Length == 2 && args[0] == "--keyboard-observer") return RunKeyboardObserver(args[1]);
        KeyboardIsolation = args.Contains("--keyboard-isolation", StringComparer.Ordinal);
        KeyboardBridgeProbe = args.Contains("--keyboard-bridge-probe", StringComparer.Ordinal);
        KeyboardBridgeNativeModalProbe = args.Contains("--keyboard-bridge-native-modal-probe", StringComparer.Ordinal);
        KeyboardBridgeFileDialogProbe = args.Contains("--keyboard-bridge-file-dialog-probe", StringComparer.Ordinal);
        NativeModalOriginalOnly = args.Contains("--native-modal-original-only", StringComparer.Ordinal);
        KeyboardBridgeImeInteractive = args.Contains("--keyboard-bridge-ime-interactive", StringComparer.Ordinal);
        ProductionImeInteractive = args.Contains("--production-ime-interactive", StringComparer.Ordinal);
        ProductionToolbarFocusProbe = args.Contains("--production-toolbar-focus-probe", StringComparer.Ordinal);
        ProductionImeReopen = args.Contains("--production-ime-reopen", StringComparer.Ordinal);
        SuppressXProbe = args.Contains("--keyboard-suppression-probe", StringComparer.Ordinal);
        PointerBoundaryProbe = args.Contains("--pointer-boundary-probe", StringComparer.Ordinal);
        PointerBoundaryAwaitFinal = args.Contains("--await-final-point", StringComparer.Ordinal);
        KeyboardWaitForActivation = args.Contains("--wait-for-activation", StringComparer.Ordinal);
        PerformanceOnly = args.Contains("--performance-only", StringComparer.Ordinal);
        PresentationThroughput = args.Contains("--presentation-throughput", StringComparer.Ordinal);
        RotationProbe = args.Contains("--rotation-probe", StringComparer.Ordinal);
        Native.GetCursorPos(out var originalCursor); nint originalForeground = Native.GetForegroundWindow();
        Profile = Path.Combine(Root, "artifacts", "e2e", ProductionToolbarFocusProbe ? "production-toolbar-focus-profiles" : KeyboardBridgeFileDialogProbe ? "keyboard-bridge-file-dialog-profiles" : KeyboardBridgeNativeModalProbe ? "keyboard-bridge-native-modal-profiles" : ProductionImeInteractive ? "production-ime-profiles" : KeyboardBridgeImeInteractive ? "keyboard-bridge-ime-profiles" : KeyboardBridgeProbe ? "keyboard-bridge-profiles" : SuppressXProbe ? "keyboard-suppression-profiles" : PointerBoundaryProbe ? "pointer-boundary-profiles" : KeyboardIsolation ? "keyboard-isolation-profiles" : PresentationThroughput ? "presentation-throughput-profiles" : PerformanceOnly ? "input-performance-profiles" : RotationProbe ? "rotation-probe-profiles" : "input-profiles", Guid.NewGuid().ToString("N"));
        Type.GetType("FloatCanvas.App.ApplicationPaths, FloatCanvas", true)!.GetMethod("Configure")!.Invoke(null, [new[] { "--profile", Profile }]);
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        int exitCode = 1;
        app.DispatcherUnhandledException += (_, e) => { Failure = e.Exception.ToString(); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try { if (ProductionToolbarFocusProbe) await RunProductionToolbarFocusProbe(app); else if (KeyboardBridgeFileDialogProbe) await RunKeyboardBridgeFileDialogProbe(app); else if (KeyboardBridgeNativeModalProbe) await RunKeyboardBridgeNativeModalProbe(app); else if (ProductionImeInteractive) await RunProductionImeInteractive(app); else if (KeyboardBridgeImeInteractive) await RunKeyboardBridgeImeInteractive(app); else if (KeyboardBridgeProbe) await RunKeyboardBridgeProbe(app); else if (SuppressXProbe) await RunKeyboardSuppressionExperiment(app); else if (PointerBoundaryProbe) await RunPointerBoundaryProbe(app); else if (KeyboardIsolation) await RunKeyboardIsolation(app); else if (PresentationThroughput) await RunPresentationThroughput(app); else if (PerformanceOnly) await RunPerformance(app); else await Run(app); if (Failure is not null) throw new InvalidOperationException(Failure); exitCode = 0; }
            catch (Exception ex) { Failure = ex.ToString(); Console.Error.WriteLine(ex); }
            finally
            {
                bool restoreOwnEnvironment = !PresentationThroughput || Lower?.Handle == Native.GetForegroundWindow() ||
                    Host?.Diagnostics.Any(w => w.InputHandle == Native.GetForegroundWindow()) == true;
                try { Cleanup(); }
                catch (Exception ex) { Failure = (Failure ?? "") + "\nCleanup: " + ex; exitCode = 1; }
                if (restoreOwnEnvironment)
                {
                    Native.SetCursorPos(originalCursor.X, originalCursor.Y);
                    if (originalForeground != 0) Native.SetForegroundWindow(originalForeground);
                }
                if (ProductionToolbarFocusProbe) WriteProductionToolbarFocusReport(exitCode == 0 && Failure is null); else if (KeyboardBridgeFileDialogProbe) WriteKeyboardBridgeFileDialogReport(exitCode == 0 && Failure is null); else if (KeyboardBridgeNativeModalProbe) WriteKeyboardBridgeNativeModalReport(exitCode == 0 && Failure is null); else if (ProductionImeInteractive) WriteProductionImeReport(exitCode == 0 && Failure is null); else if (KeyboardBridgeImeInteractive) WriteKeyboardBridgeImeReport(exitCode == 0 && Failure is null); else if (KeyboardBridgeProbe) WriteKeyboardBridgeReport(exitCode == 0 && Failure is null); else if (SuppressXProbe) WriteKeyboardSuppressionExperimentReport(exitCode == 0 && Failure is null); else if (PointerBoundaryProbe) WritePointerBoundaryReport(exitCode == 0 && Failure is null); else if (KeyboardIsolation) WriteKeyboardIsolationReport(exitCode == 0 && Failure is null); else if (PresentationThroughput) WritePresentationReport(exitCode == 0 && Failure is null); else if (PerformanceOnly) WritePerformanceReport(exitCode == 0 && Failure is null); else WriteReport(exitCode == 0 && Failure is null);
                app.Shutdown(exitCode);
            }
        };
        app.Run();
        Console.WriteLine($"Input{(ProductionToolbarFocusProbe ? " production toolbar focus" : KeyboardBridgeFileDialogProbe ? " keyboard bridge file dialog" : KeyboardBridgeNativeModalProbe ? " keyboard bridge native modal" : ProductionImeInteractive ? " production IME interactive" : KeyboardBridgeImeInteractive ? " keyboard bridge IME interactive" : KeyboardBridgeProbe ? " keyboard bridge" : SuppressXProbe ? " X suppression" : PointerBoundaryProbe ? " pointer boundary" : KeyboardIsolation ? " keyboard isolation" : PresentationThroughput ? " presentation throughput" : PerformanceOnly ? " performance" : RotationProbe ? " rotation probe" : "")} checks: {(ProductionToolbarFocusProbe ? ToolbarFocusChecks.Count : KeyboardBridgeFileDialogProbe ? FileDialogChecks.Count : KeyboardBridgeNativeModalProbe ? NativeModalChecks.Count : ProductionImeInteractive ? ProductionImeChecks.Count : KeyboardBridgeImeInteractive ? ImeChecks.Count : KeyboardBridgeProbe ? BridgeChecks.Count : SuppressXProbe ? SuppressionChecks.Count : Checks.Count)}; passed={exitCode == 0 && Failure is null}; report={(ProductionToolbarFocusProbe ? ProductionToolbarFocusReport : KeyboardBridgeFileDialogProbe ? KeyboardBridgeFileDialogReport : KeyboardBridgeNativeModalProbe ? KeyboardBridgeNativeModalReport : ProductionImeInteractive ? ProductionImeReport : KeyboardBridgeImeInteractive ? KeyboardBridgeImeReport : KeyboardBridgeProbe ? KeyboardBridgeReport : SuppressXProbe ? KeyboardSuppressionExperimentReport : PointerBoundaryProbe ? PointerBoundaryReport : KeyboardIsolation ? KeyboardIsolationReport : PresentationThroughput ? PresentationReport : PerformanceOnly ? PerformanceReport : RotationProbe ? RotationProbeReport : Report)}");
        return exitCode;
    }

    private static async Task Run(Wpf.Application app)
    {
        RunDiagnosticStart = Stopwatch.GetTimestamp();
        Lower = new NativeTestWindow(); Lower.Activate();
        await WaitForOwnFixtureActivation();
        Session = ConstructOrdinarySession(app);
        await CallAsync("InitializeDocumentsAsync", true);
        if (Field<DispatcherTimer?>("_documentRecoveryTimer") is { } recoveryTimer) { recoveryTimer.Stop(); recoveryTimer.Interval = TimeSpan.FromHours(1); }
        Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
        Check(!Host.UsesSystemInput, "Exact-HWND desktop input checks use the ordinary backend explicitly");
        Host.KeyboardIsolationReleased += reason =>
        {
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(RunDiagnosticStart).TotalMilliseconds;
            nint foreground = Native.GetForegroundWindow(); KeyboardNative.GetWindowThreadProcessId(foreground, out uint pid);
            KeyboardReleaseEvents.Add(new { Reason = reason.ToString(), ElapsedMilliseconds = elapsedMilliseconds,
                Foreground = foreground.ToInt64(), ForegroundPid = pid, Host.IsEditing, PointerInjections, LongLineMoves });
            Console.WriteLine($"KEYBOARD_ISOLATION_RELEASED: reason={reason}; elapsedMs={elapsedMilliseconds:F3}; foreground={foreground}; pid={pid}; editing={Host.IsEditing}; pointerInjections={PointerInjections}; longLineMoves={LongLineMoves}");
        };
        Host.RegisterHotkeys(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
        var first = Lower.ScreenPoint(170, 190); var last = Lower.ScreenPoint(310, 270);
        Anchor = first;
        Check(Lower.OwnsPoint(new(first.X - 50, first.Y - 60)) && Lower.OwnsPoint(new(last.X + 100, last.Y + 100)), "Tool and transform sample rectangle is strictly inside our self-owned native client");
        Call("SetEditing", true); await Task.Delay(70);
        await ActivateInput();
        var editor = Active();
        int toolRound = 0;
        foreach (string tool in RotationProbe ? Enumerable.Repeat("Freehand", 6) : new[] { "Freehand", "Highlighter", "Line", "Arrow", "DoubleArrow", "Rectangle", "RoundedRectangle", "Ellipse", "Triangle", "Number" })
        {
            toolRound++;
            var document = editor.Document.Clone(); document.Elements.Clear(); document.NextNumber = 1;
            editor.ReplaceDocument(document);
            SelectTool(tool);
            ((ElementStyle)Field<object>("_editor").GetType().GetProperty("Style")!.GetValue(Field<object>("_editor"))!).Stroke = 0xFF2383E2;
            await ActivateInput();
            if (tool == "Number") await Click(new(first.X + 70, first.Y + 40));
            else
            {
                var path = Enumerable.Range(0, 25).Select(i => new Native.Point(first.X + (last.X - first.X) * i / 24,
                    tool is "Freehand" or "Highlighter" ? first.Y + 38 + (int)Math.Round(Math.Sin(i * Math.PI * 2 / 24) * 25) : first.Y + (last.Y - first.Y) * i / 24)).ToArray();
                await Drag(path);
            }
            await WaitForRenderToSettle();
            Check(editor.Document.Elements.Count == 1, tool + ": real WM pointer input created exactly one object");
            var element = editor.Document.Elements.Single(); Guid id = element.Id;
            Check(MatchesTool(element, tool), tool + ": actual controller tool retains the requested geometry and endpoint styles");
            Check(tool is "Freehand" or "Highlighter" ? editor.Selection.Count == 0 : editor.Selection.SetEquals([id]),
                tool + ": real creation preserves its tool-specific selection policy");
            var bounds = ElementGeometry.Bounds(element, includeStroke: false);
            if (tool != "Number" && !(Math.Abs(bounds.Left - first.X) <= 2 && Math.Abs(bounds.Right - last.X) <= 2 && bounds.Height >= 40))
                ToolEvidence.Add(new { Tool = tool, Round = toolRound, Phase = "DrawingGeometryFailure", Bounds = bounds,
                    ExpectedFirst = new { first.X, first.Y }, ExpectedLast = new { last.X, last.Y },
                    element.Transform, element.Points, Host.IsEditing, Selection = editor.Selection.ToArray() });
            if (tool == "Number") Check(Math.Abs(bounds.Center.X - (first.X + 70)) <= 2 && Math.Abs(bounds.Center.Y - (first.Y + 40)) <= 2, tool + ": actual click position retained in physical pixels");
            else Check(Math.Abs(bounds.Left - first.X) <= 2 && Math.Abs(bounds.Right - last.X) <= 2 && bounds.Height >= 40, tool + ": real drag geometry matches physical desktop coordinates");
            int visiblePixels = CountBlueScreenPixels(element);
            Check(visiblePixels > 8, tool + ": ink is visible on the actual desktop over our client");
            var exported = editor.Document.Clone(); exported.Viewport = ElementGeometry.Bounds(element).Inflate(5);
            using (var renderer = new SceneRenderer())
            using (var alpha = SKBitmap.Decode(renderer.ExportPng(exported, Field<AssetLibrary>("_assets"), true)))
            {
                bool zero = false, ink = false;
                for (int y = 0; y < alpha.Height; y++) for (int x = 0; x < alpha.Width; x++) { byte a = alpha.GetPixel(x, y).Alpha; zero |= a == 0; ink |= a > 0; }
                Check(zero && ink, tool + ": production transparent export contains both true zero alpha and ink");
            }
            SelectTool("Select"); await ActivateInput(); await Task.Delay(25);
            if (tool == "Number") await Task.Delay(checked((int)Native.GetDoubleClickTime()) + 30);
            var baseline = element.Transform;
            PointD hit = HitPoint(element);
            await Drag(Line(ToNative(hit), ToNative(hit + new PointD(24, 12)), 12));
            var moved = editor.Document.Elements.Single(e => e.Id == id);
            Check(Math.Abs(moved.Transform.OffsetX - baseline.OffsetX - 24) <= 2 && Math.Abs(moved.Transform.OffsetY - baseline.OffsetY - 12) <= 2, tool + ": selection can be moved by an actual drag");
            await Undo(); Check(Near(editor.Document.Elements.Single().Transform, baseline), tool + ": actual Ctrl+Z undoes translation");
            element = editor.Document.Elements.Single(); bounds = ElementGeometry.Bounds(element);
            PointD rotate = new(bounds.Center.X, bounds.Top - 28);
            PointD rotated = bounds.Center + new PointD(-(rotate.Y - bounds.Center.Y), rotate.X - bounds.Center.X);
            var rotationPath = Line(ToNative(rotate), ToNative(rotated), 15);
            var rotationEvents = new List<object>();
            void ObserveRotation(OverlayPointerEvent e)
            {
                if (Lower!.OwnsPoint(ToNative(new(e.Position.X, e.Position.Y))))
                    rotationEvents.Add(new { At = Stopwatch.GetTimestamp(), e.Kind, e.Position, e.Button, e.Modifiers,
                        Capture = Native.GetCapture().ToInt64(), Transform = editor.Document.Elements.Single().Transform });
            }
            Host.Pointer += ObserveRotation;
            try { await Drag(rotationPath); }
            finally { Host.Pointer -= ObserveRotation; }
            var rotation = editor.Document.Elements.Single().Transform;
            double degrees = Math.Atan2(rotation.M21, rotation.M11) * 180 / Math.PI - Math.Atan2(baseline.M21, baseline.M11) * 180 / Math.PI;
            if (RotationProbe || Math.Abs(degrees - 90) >= 3)
                ToolEvidence.Add(new { Tool = tool, Round = toolRound, Phase = Math.Abs(degrees - 90) >= 3 ? "RotationFailure" : "RotationProbe", Degrees = degrees, Baseline = baseline, Actual = rotation,
                    Start = rotate, End = rotated, Selection = editor.Selection.ToArray(), editor.IsTransactionActive,
                    PlannedPath = rotationPath.Select(p => new { p.X, p.Y }).ToArray(), Events = rotationEvents });
            Check(Math.Abs(degrees - 90) < 3, tool + ": actual rotation-handle drag rotates approximately 90 degrees");
            await Undo(); Check(Near(editor.Document.Elements.Single().Transform, baseline), tool + ": actual Ctrl+Z undoes rotation");
            string beforeResize = JsonSerializer.Serialize(editor.Document.Elements.Single());
            bounds = ElementGeometry.Bounds(editor.Document.Elements.Single());
            var resize = new PointD(bounds.Right, bounds.Bottom); var resized = bounds.Center + (resize - bounds.Center) * 1.35;
            await Drag(Line(ToNative(resize), ToNative(resized), 15));
            var scaled = editor.Document.Elements.Single(); var scaledBounds = ElementGeometry.Bounds(scaled);
            Check(scaledBounds.Width > bounds.Width * 1.15 && scaledBounds.Height > bounds.Height * 1.15, tool + ": actual selection-handle drag scales the whole object");
            await Undo(); Check(JsonSerializer.Serialize(editor.Document.Elements.Single()) == beforeResize, tool + ": actual Ctrl+Z restores complete geometry and transform after scale");
            ToolEvidence.Add(new { Tool = tool, Round = toolRound, RealDragOrClick = true, ToolSelectedProgrammatically = true, VisibleBlueSamples = visiblePixels, PointCount = element.Points.Count, ElementId = id });
            Console.WriteLine($"Verified native tool: {tool}");
        }
        if (RotationProbe) return;

        // Real default RegisterHotKey behavior. Pass-through hotkeys may target only our native
        // lower HWND; drawing and undo keyboard input always require the production input HWND.
        string content = JsonSerializer.Serialize(editor.Document);
        for (int i = 0; i < 100; i++)
        {
            bool expected = !Host.IsEditing;
            if (Host.IsEditing) await ActivateInput(); else Lower.Activate();
            CheckHotkeyForeground();
            var stopwatch = Stopwatch.StartNew();
            SendKey(0x11, false, true); SendKey(0xC0, false, true);
            SendKey(0xC0, true, true); SendKey(0x11, true, true);
            await WaitUntil(() => Host.IsEditing == expected, "Real default shortcut did not toggle the production application");
            ToggleMilliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
            Check(JsonSerializer.Serialize(editor.Document) == content, $"Default shortcut toggle {i + 1}: object content remains identical");
        }
        Check(Host.IsEditing, "One hundred real shortcut switches end back in editing mode");
        await ActivateInput();
        SelectTool("Freehand");
        var longDocument = editor.Document.Clone(); longDocument.Elements.Clear(); editor.ReplaceDocument(longDocument);
        var longStart = Lower.ScreenPoint(120, 180);
        Move(longStart); Down();
        try
        {
            for (int i = 0; i < 1600; i++)
            {
                Move(new(longStart.X + i % 240, longStart.Y + 40 + (int)Math.Round(Math.Sin(i * .17) * 35))); LongLineMoves++;
                await Task.Delay(8);
                if (i >= 1000 && editor.Document.Elements.Count == 1 && editor.Document.Elements[0].Points.Count >= 1000) break;
            }
        }
        finally { Up(); }
        await Task.Delay(80);
        LongLinePoints = editor.Document.Elements.Single().Points.Count;
        Check(LongLinePoints >= 1000, "Sustained real pointer stream retains at least one thousand freehand points");
        Check(!editor.IsTransactionActive, "Long native pointer gesture commits without leaving capture or an edit transaction");
        Check(Native.GetCapture() == 0, "Production native mouse capture releases after the long stroke");
        await Undo(); Check(editor.Document.Elements.Count == 0, "Actual Ctrl+Z undoes the complete thousand-point stroke in one step");
        Call("SetEditing", false); await Task.Delay(150);
        long frames = Host.PresentedFrameCount; var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var wall = Stopwatch.StartNew();
        await Task.Delay(3000); double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
        IdleCpuPercentOfOneCore = cpuSeconds / wall.Elapsed.TotalSeconds * 100;
        IdleCpuPercentOfMachine = IdleCpuPercentOfOneCore / Environment.ProcessorCount;
        IdleFrames = Host.PresentedFrameCount - frames;
        Check(IdleFrames == 0, "Three-second static pass-through presents no additional frames");
        Check(Lower.Counts.Clicks == 0 && Lower.Counts.DoubleClicks == 0 && Lower.Counts.DragMoves == 0, "All actual drawing pointer input stayed on production input windows, never leaked to the lower application");
    }

    private static PointD HitPoint(CanvasElement element)
    {
        PointD local = ElementGeometry.UsesPoints(element.Kind) ? element.Points.Count > 2 ? element.Points[element.Points.Count / 2] : (element.Points[0] + element.Points[^1]) * .5
            : element.Kind == ElementKind.Number ? new(element.Width / 2, element.Height / 2) : new(element.Width / 2, 0);
        return element.Transform.TransformPoint(local);
    }
    private static Native.Point ToNative(PointD p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));
    private static Native.Point[] Line(Native.Point a, Native.Point b, int steps) => Enumerable.Range(0, steps + 1).Select(i => new Native.Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps)).ToArray();
    private static async Task Drag(IReadOnlyList<Native.Point> points)
    {
        void ObservePlannedInput(OverlayPointerEvent e)
        {
            if (e.Kind is OverlayPointerKind.Move or OverlayPointerKind.Down or OverlayPointerKind.Up or OverlayPointerKind.DoubleClick)
            {
                bool planned = points.Any(p => Math.Abs(p.X - e.Position.X) <= 1 && Math.Abs(p.Y - e.Position.Y) <= 1);
                bool ownForeground = Host!.Diagnostics.Any(w => w.InputHandle == Native.GetForegroundWindow());
                if (!planned || !ownForeground) ProbeInputViolation = $"Unexpected pointer event during a planned probe gesture: {e.Kind}, ({e.Position.X},{e.Position.Y}), planned={planned}, ownForeground={ownForeground}";
            }
        }
        if (RotationProbe) { Host!.Pointer += ObservePlannedInput; ProbeGestureActive = true; }
        try
        {
            Move(points[0]); Down();
            try { foreach (var point in points.Skip(1)) { Move(point); await Task.Delay(6); } }
            finally { Up(); }
            await Task.Delay(35);
            if (ProbeInputViolation is not null) throw new InvalidOperationException(ProbeInputViolation);
        }
        finally { if (RotationProbe) { ProbeGestureActive = false; Host!.Pointer -= ObservePlannedInput; } }
    }
    private static async Task Click(Native.Point point) { Move(point); Down(); await Task.Delay(8); Up(); await Task.Delay(35); }
    private static void Move(Native.Point point) { GuardPointer(point); Native.Move(point); PointerInjections++; }
    private static void Down() { Native.GetCursorPos(out var point); GuardPointer(point); Native.Mouse(2); PointerInjections++; MouseDown = true; }
    private static void Up()
    {
        if (!MouseDown) return;
        Native.GetCursorPos(out var point); GuardPointer(point, releasingHeldButton: true); Native.Mouse(4); PointerInjections++; MouseDown = false;
    }
    private static void GuardPointer(Native.Point point, bool releasingHeldButton = false)
    {
        PointerGuards++;
        if (RotationProbe && ProbeGestureActive && !releasingHeldButton && (ProbeInputViolation is not null || Host is null || !Host.Diagnostics.Any(w => w.InputHandle == Native.GetForegroundWindow())))
            throw new InvalidOperationException(ProbeInputViolation ?? "Rotation probe lost the production input foreground; further injection aborted.");
        if (Lower is null || Host is null || !Lower.OwnsPoint(point) || !Host.Diagnostics.Any(w => w.InputHandle == Native.WindowFromPoint(point)))
            throw new InvalidOperationException($"Pointer injection aborted: destination ({point.X},{point.Y}) must hit production InputHandle inside our own native client. hit={Native.WindowFromPoint(point)}, foreground={Native.GetForegroundWindow()}, lower={Lower?.Handle}, lowerOwns={Lower?.OwnsPoint(point)}, editing={Host?.IsEditing}, diagnostics={JsonSerializer.Serialize(Host?.Diagnostics.Select(w => new { DisplayHandle = w.DisplayHandle.ToInt64(), InputHandle = w.InputHandle.ToInt64(), w.Bounds, w.InputVisible, w.DisplayVisible }))}");
    }
    private static bool MatchesTool(CanvasElement element, string tool) => tool switch
    {
        "Line" => element.Kind == ElementKind.Line && ElementGeometry.ArrowStyle(element, true) == ArrowHeadStyle.None && ElementGeometry.ArrowStyle(element) == ArrowHeadStyle.None,
        "Arrow" => ElementGeometry.IsLine(element.Kind) && ElementGeometry.ArrowStyle(element, true) == ArrowHeadStyle.None && ElementGeometry.ArrowStyle(element) == ArrowHeadStyle.FilledTriangle,
        "DoubleArrow" => ElementGeometry.IsLine(element.Kind) && ElementGeometry.ArrowStyle(element, true) == ArrowHeadStyle.FilledTriangle && ElementGeometry.ArrowStyle(element) == ArrowHeadStyle.FilledTriangle,
        "RoundedRectangle" => element.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle && ElementGeometry.CornerRadius(element) > 0,
        "Triangle" => element.Kind == ElementKind.Symbol && element.Symbol == SymbolKind.Triangle,
        _ => element.Kind.ToString() == tool
    };
    private static async Task ActivateInput()
    {
        nint hwnd = Host!.Diagnostics.Single(w => w.Bounds.Contains(new(Anchor.X, Anchor.Y))).InputHandle;
        Native.SetForegroundWindow(hwnd); Native.SetFocus(hwnd);
        if (Native.GetForegroundWindow() != hwnd)
        {
            // Foreground locking may reject a background runner. A guarded click on our own
            // production input HWND is safe and is the same activation path a user takes.
            var controller = Field<object>("_editor");
            var tool = controller.GetType().GetProperty("Tool")!.GetValue(controller)!;
            var selection = Active().Selection.ToArray();
            SelectTool("Select"); await Click(Anchor);
            Active().SetSelection(selection); Call("SelectTool", tool);
        }
        if (Native.GetForegroundWindow() != hwnd) throw new InvalidOperationException($"Production input HWND {hwnd} could not become foreground ({Native.GetForegroundWindow()}); no keyboard input injected.");
    }
    private static async Task Undo()
    {
        await ActivateInput(); SendKey(0x11, false, false); SendKey(0x5A, false, false); SendKey(0x5A, true, false); SendKey(0x11, true, false);
        await Task.Delay(45);
    }
    private static void CheckHotkeyForeground()
    {
        nint foreground = Native.GetForegroundWindow();
        if (foreground != Lower!.Handle && !Host!.Diagnostics.Any(w => w.InputHandle == foreground))
            throw new InvalidOperationException("Global shortcut injection aborted: foreground is neither our own lower HWND nor production input HWND.");
    }
    private static void SendKey(ushort key, bool up, bool globalShortcut)
    {
        KeyboardGuards++;
        nint foreground = Native.GetForegroundWindow();
        bool safe = Host!.Diagnostics.Any(w => w.InputHandle == foreground) || globalShortcut && foreground == Lower!.Handle;
        if (!safe) { ReleaseModifiers(); throw new InvalidOperationException("Keyboard injection aborted on unexpected foreground; only held synthetic modifiers were released."); }
        Native.Key(key, up); KeyboardInjections++;
        if (key is 0x10 or 0x11 or 0x12) { if (up) HeldModifiers.Remove(key); else HeldModifiers.Add(key); }
    }
    private static void ReleaseModifiers()
    {
        foreach (ushort key in HeldModifiers.ToArray()) Native.Key(key, true);
        HeldModifiers.Clear();
    }
    private static int CountBlueScreenPixels(CanvasElement element)
    {
        IReadOnlyList<PointD> path = ElementGeometry.UsesPoints(element.Kind) ? element.Points : ElementGeometry.Outline(element);
        PointD[] local;
        if (ElementGeometry.UsesPoints(element.Kind))
        {
            local = Enumerable.Range(0, 36).Select(i =>
            {
                double index = i / 35d * (path.Count - 1); int a = Math.Min(path.Count - 1, (int)index), b = Math.Min(path.Count - 1, a + 1);
                return path[a] + (path[b] - path[a]) * (index - a);
            }).ToArray();
        }
        else
        {
            local = Enumerable.Range(0, 36).Select(i =>
            {
                double index = i / 36d * path.Count; int a = (int)index, b = (a + 1) % path.Count;
                return path[a] + (path[b] - path[a]) * (index - a);
            }).ToArray();
        }
        int count = 0; nint dc = Native.GetDC(0);
        try
        {
            foreach (var probe in local.Select(element.Transform.TransformPoint).Select(ToNative).DistinctBy(p => (p.X, p.Y)))
            {
                int x = probe.X, y = probe.Y;
                if (!Lower!.OwnsPoint(new(x, y))) throw new InvalidOperationException("Screen sample escaped the native fixture.");
                uint color = Native.GetPixel(dc, x, y); int r = (int)(color & 255), g = (int)(color >> 8 & 255), b = (int)(color >> 16 & 255);
                if (b > r + 15 && r + g + b < 740) count++;
            }
        }
        finally { Native.ReleaseDC(0, dc); }
        return count;
    }
    private static bool Near(Affine2D a, Affine2D b) => Math.Abs(a.M11 - b.M11) + Math.Abs(a.M12 - b.M12) + Math.Abs(a.M21 - b.M21) + Math.Abs(a.M22 - b.M22) + Math.Abs(a.OffsetX - b.OffsetX) + Math.Abs(a.OffsetY - b.OffsetY) < 1e-6;
    private static async Task WaitUntil(Func<bool> predicate, string error)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate()) { if (stopwatch.ElapsedMilliseconds > 1500) throw new InvalidOperationException(error); await Task.Delay(2); }
    }
    private static void SelectTool(string name) => Call("SelectTool", Enum.Parse(ToolType, name));
    private static EditorSession Active() => (EditorSession)DesktopType.GetProperty("Active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!;
    private static object ConstructOrdinarySession(Wpf.Application app)
    {
        // These checks guard injection against visible desktop InputHandle HWNDs. The enhanced
        // backend delegates input elsewhere and is covered by separate system-overlay fixtures.
        var policyType = DesktopType.Assembly.GetType("FloatCanvas.App.OverlayBackendProtection", true)!;
        const BindingFlags create = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var policy = Activator.CreateInstance(policyType, create, null, [Profile!, true, false, null], null)!;
        return Activator.CreateInstance(DesktopType, create, null, [app, null, policy, null], null)!;
    }
    private static T Field<T>(string name) => (T)DesktopType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!;
    private static object? Call(string name, params object?[] args)
    {
        var method = DesktopType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var parameters = method.GetParameters();
        if (args.Length < parameters.Length && parameters.Skip(args.Length).All(parameter => parameter.IsOptional))
        {
            var supplied = args.Length;
            Array.Resize(ref args, parameters.Length);
            for (int index = supplied; index < parameters.Length; index++)
                args[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : Type.Missing;
        }
        try { return method.Invoke(Session, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static async Task CallAsync(string name, params object?[] args) => await (Task)Call(name, args)!;
    private static void Check(bool value, string name) { Checks.Add(new { Name = name, Passed = value }); if (!value) throw new InvalidOperationException(name); }
    private static void Cleanup()
    {
        try
        {
            ReleaseModifiers();
            if (MouseDown && Host?.IsEditing == true && Lower is not null) { Move(Anchor); Up(); }
        }
        finally
        {
            Native.ReleaseCapture();
            try { if (Session is IDisposable session) session.Dispose(); }
            finally { Session = null; Host = null; Lower?.Dispose(); Lower = null; }
        }
    }
    private static void WriteReport(bool passed)
    {
        string destination = RotationProbe ? RotationProbeReport : Report;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, JsonSerializer.Serialize(new
        {
            Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile, Checks, ToolEvidence, RotationProbe, ProbeInputViolation,
            WaitForInitialOwnFixtureActivation = KeyboardWaitForActivation,
            ToolSelection = "Reflection invokes production SelectTool; drawing and transformations use real guarded SendInput and native WM messages.",
            PointerGuards, KeyboardGuards, PointerInjections, KeyboardInjections, KeyboardReleaseEvents,
            HotkeySwitches = ToggleMilliseconds.Count, HotkeySwitchP95Milliseconds = ToggleMilliseconds.Count == 0 ? 0 : ToggleMilliseconds.Order().ElementAt((int)Math.Ceiling(ToggleMilliseconds.Count * .95) - 1),
            IdleSamplingSeconds = 3, IdleFrames, IdleCpuPercentOfMachine, IdleCpuPercentOfOneCore,
            LongLineMoves, LongLinePoints, Monitors, ProcessorCount = Environment.ProcessorCount, Os = Environment.OSVersion.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
