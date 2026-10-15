using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.PlatformTests;

internal static class Program
{
    private static readonly List<CheckResult> Checks = [];
    private static readonly List<string> Errors = [];
    private static int _exitCode = 1;
    private static bool _waitForActivation;
    private static bool _primaryScreenProbe;

    [STAThread]
    private static int Main(string[] args)
    {
        _waitForActivation = args.Contains("--wait-for-activation");
        _primaryScreenProbe = args.Contains("--primary-screen-probe");
        var captureOnly = args.Contains("--capture-only");
        var evidenceIndex = Array.IndexOf(args, "--evidence");
        var evidencePath = evidenceIndex >= 0 && args.Length > evidenceIndex + 1
            ? Path.GetFullPath(args[evidenceIndex + 1]) : Path.Combine(FindRepository(), "artifacts", "m1",
                _primaryScreenProbe ? captureOnly ? "capture-primary-screen-probe.json" : "platform-primary-screen-probe.json" :
                captureOnly ? "capture-tests.json" : "platform-tests.json");
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (captureOnly) _exitCode = await CaptureFixture.Run(evidencePath, _primaryScreenProbe, _waitForActivation);
                else await Run(evidencePath);
            }
            catch (Exception ex) { Errors.Add(ex.ToString()); Console.Error.WriteLine(ex); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task Run(string evidencePath)
    {
        var previousForeground = Native.GetForegroundWindow();
        Native.GetCursorPos(out var previousCursor);
        MonitorInfo[] monitors = [];
        var timings = new List<double>();
        var renderTimings = new List<double>();
        InputCounts? targetCounts = null;
        var pointers = new Dictionary<OverlayPointerKind, int>();
        var pointerEvents = new List<OverlayPointerEvent>();
        var keyCount = 0;
        var toggles = 0;
        var editing = false;
        var visible = true;
        NativeTestWindow? cleanupTarget = null;
        OverlayHost? cleanupHost = null;
        PrimaryScreenFixture? primaryProbe = null;
        try
        {
            var target = cleanupTarget = new NativeTestWindow();
            var host = cleanupHost = _primaryScreenProbe ? PrimaryScreenFixture.CreateHost(out primaryProbe) : new OverlayHost();
            monitors = host.Monitors.ToArray();
            host.Error += ex => Errors.Add(ex.ToString());
            host.Pointer += e => { pointers[e.Kind] = pointers.GetValueOrDefault(e.Kind) + 1; pointerEvents.Add(e); };
            host.KeyPressed += key => { if (key == 0x41) keyCount++; };
            host.ToggleRequested += () => { editing = !editing; host.SetEditing(editing); toggles++; };
            host.HideRequested += () => { visible = !visible; if (!visible) editing = false; host.SetVisible(visible); };
            var opaque = target.ScreenPoint(120, 160);
            var blank = target.ScreenPoint(430, 240);
            var stressRender = false;
            host.Draw += surface =>
            {
                var canvas = surface.Canvas;
                using var paint = new SKPaint { Color = new SKColor(40, 110, 240, 255), IsAntialias = true };
                canvas.DrawRect(opaque.X - 30, opaque.Y - 30, 140, 85, paint);
                if (stressRender)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 3;
                    for (var i = 0; i < 1000; i++)
                    {
                        var x = opaque.X + i % 80 * 20;
                        var y = opaque.Y + i / 80 * 20;
                        canvas.DrawLine(x, y, x + 14, y + 12, paint);
                    }
                }
            };
            host.RegisterHotkeys(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide);
            host.SetVisible(true);
            host.SetEditing(false);
            host.Render();
            await Task.Delay(150);
            if (_primaryScreenProbe && _waitForActivation) await PrimaryScreenFixture.WaitForExternalActivation(target);
            else target.Activate();
            await Task.Delay(100);
            if (Native.GetForegroundWindow() != target.Handle)
            {
                if (_primaryScreenProbe && _waitForActivation)
                    throw new InvalidOperationException("Self-target lost externally established foreground; no automatic focus repair or input injection was attempted.");
                // Foreground lock can reject a background runner. Only click after putting our own
                // target above other windows and confirming the exact point belongs to that HWND.
                Native.SetWindowPos(target.Handle, -1, 0, 0, 0, 0, 0x13);
                EnsureSafePoint(target, opaque);
                if (Native.WindowFromPoint(opaque) != target.Handle)
                {
                    if (!_waitForActivation)
                        throw new InvalidOperationException($"Cannot safely activate the self-owned target; no input injected. Target={target.Handle}; hit={Native.WindowFromPoint(opaque)}; foreground={Native.GetForegroundWindow()}; point=({opaque.X},{opaque.Y}); targetVisible={Native.IsWindowVisible(target.Handle)}. Use --wait-for-activation for explicit activation of the self-owned window.");
                    Console.WriteLine($"WAITING_FOR_SELF_TARGET_ACTIVATION hwnd={target.Handle} pid={Environment.ProcessId}");
                    var activationTimer = Stopwatch.StartNew();
                    while (Native.GetForegroundWindow() != target.Handle)
                    {
                        if (activationTimer.Elapsed > TimeSpan.FromSeconds(45)) throw new InvalidOperationException("Self-target activation timed out; no input injected.");
                        await Task.Delay(20);
                    }
                }
                else { Native.Move(opaque); Native.Mouse(2); Native.Mouse(4); }
                await WaitUntil(() => Native.GetForegroundWindow() == target.Handle, "The self-owned target could not become foreground.");
                Native.SetWindowPos(target.Handle, -2, 0, 0, 0, 0, 0x13);
                await Task.Delay(50);
            }
            Assert("self-owned-target-foreground", Native.GetForegroundWindow() == target.Handle,
                "Input is injected only after the disposable test target becomes foreground.");
            Assert("monitor-window-pairs", host.Diagnostics.Count == monitors.Length && monitors.Length > 0,
                $"Actual monitors: {monitors.Length}; native display/input pairs: {host.Diagnostics.Count}.");
            foreach (var diagnostic in host.Diagnostics)
            {
                var monitor = monitors.Single(m => m.Bounds == diagnostic.Bounds);
                foreach (var (name, handle) in new[] { ("display", diagnostic.DisplayHandle), ("input", diagnostic.InputHandle) })
                {
                    Native.GetWindowRect(handle, out var actual);
                    var dpi = Native.GetDpiForWindow(handle);
                    Assert($"{monitor.DeviceName}-{name}-physical-bounds-dpi",
                        actual.Left == monitor.Bounds.Left && actual.Top == monitor.Bounds.Top &&
                        actual.Right == monitor.Bounds.Right && actual.Bottom == monitor.Bounds.Bottom && dpi == monitor.DpiX,
                        $"GetWindowRect=({actual.Left},{actual.Top})-({actual.Right},{actual.Bottom}); GetDpiForWindow={dpi}; enumerated DPI={monitor.DpiX}.");
                }
            }
            Assert("display-is-layered-transparent-noactivate", host.Diagnostics.All(d =>
                (d.DisplayExtendedStyle & 0x08080020) == 0x08080020),
                "Every real display HWND has WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE.");
            Assert("input-hidden-when-passthrough", host.Diagnostics.All(d => !Native.IsWindowVisible(d.InputHandle)),
                "Each input HWND is hidden in passthrough mode.");
            var screen = Native.GetDC(0);
            try
            {
                var color = Native.GetPixel(screen, opaque.X, opaque.Y);
                var red = color & 255; var green = color >> 8 & 255; var blue = color >> 16 & 255;
                Assert("opaque-ink-visible-on-real-desktop", Math.Abs((int)red - 40) <= 3 &&
                    Math.Abs((int)green - 110) <= 3 && Math.Abs((int)blue - 240) <= 3,
                    $"GDI GetPixel at annotated desktop point reports RGB({red},{green},{blue}), expected RGB(40,110,240).");
            }
            finally { Native.ReleaseDC(0, screen); }
            var idleFrames = host.PresentedFrameCount;
            await Task.Delay(350);
            Assert("static-passthrough-does-not-repaint", host.PresentedFrameCount == idleFrames,
                $"PresentedFrameCount remains {idleFrames} while the desktop is idle.");
            Assert("reserve-conflicting-hotkey", Native.RegisterHotKey(target.Handle, 0x1234, 0x4003, 0x4A),
                "The self-owned lower HWND reserves Ctrl+Alt+J for transaction rollback testing.");
            var conflictThrown = false;
            try { host.RegisterHotkeys(new HotkeyBinding(3, 0x4A), HotkeyBinding.DefaultHide); }
            catch (Win32Exception) { conflictThrown = true; }
            finally { Native.UnregisterHotKey(target.Handle, 0x1234); }
            Assert("conflicting-hotkey-rejected", conflictThrown,
                "Changing to a reserved shortcut fails explicitly; the next real default hotkey test checks rollback.");

            foreach (var (name, point) in new[] { ("opaque-ink", opaque), ("blank-overlay", blank) })
            {
                EnsureSafePoint(target, point);
                Assert(name + "-native-hit-target", Native.WindowFromPoint(point) == target.Handle,
                    "WindowFromPoint resolves to the self-owned lower HWND, including fully opaque drawn ink.");
                var before = target.Counts.Snapshot();
                Assert(name + "-target-still-foreground", Native.GetForegroundWindow() == target.Handle,
                    $"Immediately before lower-window input: foreground={Native.GetForegroundWindow()}, target={target.Handle}.");
                await ExercisePointerAndKeyboard(point);
                Assert(name + "-click-doubleclick-drag-wheel-key", target.Counts.Clicks > before.Clicks &&
                    target.Counts.DoubleClicks > before.DoubleClicks && target.Counts.DragMoves > before.DragMoves &&
                    target.Counts.Wheels > before.Wheels && target.Counts.KeysA > before.KeysA,
                    $"Lower window counters before={JsonSerializer.Serialize(before)}, after={JsonSerializer.Serialize(target.Counts)}, foreground={Native.GetForegroundWindow()}.");
            }

            var inputBefore = target.Counts.Snapshot();
            var pointerBefore = pointers.GetValueOrDefault(OverlayPointerKind.Down);
            var keyBefore = keyCount;
            await ToggleWithHotkey(() => toggles, 1);
            Assert("edit-visible-input-windows", host.Diagnostics.All(d => Native.IsWindowVisible(d.InputHandle)),
                "Global RegisterHotKey callback enables the actual input HWNDs.");
            EnsureSafePoint(target, opaque);
            Assert("edit-native-hit-input", host.Diagnostics.Any(d => d.InputHandle == Native.WindowFromPoint(opaque)),
                "The real input HWND is hit at the same point where lower-window input worked.");
            await ExercisePointerAndKeyboard(opaque);
            Assert("edit-blocks-underlying-input", target.Counts.SameAs(inputBefore),
                "Lower window receives no clicks, double clicks, drag, wheel, or A key during editing.");
            Assert("edit-delivers-overlay-input", pointers.GetValueOrDefault(OverlayPointerKind.Down) > pointerBefore && keyCount > keyBefore,
                "OverlayHost emits actual pointer and key events from SendInput.");
            var batchA = target.ScreenPoint(260, 300);
            var batchB = target.ScreenPoint(320, 350);
            EnsureSafePoint(target, batchA);
            Native.Move(batchA);
            await WaitUntil(() => CursorAt(batchA), "Batched pointer start did not arrive.");
            var batchStart = pointerEvents.Count;
            Native.Mouse(2); Native.Move(batchB); Native.Mouse(4); // No message-pump wait between the three inputs.
            await WaitUntil(() => pointerEvents.Skip(batchStart).Any(e => e.Kind == OverlayPointerKind.Up), "Batched pointer up did not arrive.");
            var batchEvents = pointerEvents.Skip(batchStart).ToArray();
            var batchDown = batchEvents.First(e => e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick);
            var batchUp = batchEvents.Last(e => e.Kind == OverlayPointerKind.Up);
            Assert("batched-pointer-message-coordinates", Near(batchDown.Position, batchA) && Near(batchUp.Position, batchB),
                $"Queued down={batchDown.Position}, up={batchUp.Position}; expected down=({batchA.X},{batchA.Y}), up=({batchB.X},{batchB.Y}).");
            Native.Move(opaque);
            await WaitUntil(() => CursorAt(opaque), "Multi-button test pointer did not arrive.");
            Native.Mouse(2);
            await WaitUntil(() => host.CaptureHandle != 0, "Primary button did not acquire capture.");
            Native.Mouse(8); Native.Mouse(16); // Right-button press/release while left is held.
            await Task.Delay(30);
            Assert("secondary-button-does-not-release-primary-capture", host.CaptureHandle != 0,
                "Right-click during a left-button gesture preserves the primary capture.");
            Native.Mouse(4);
            await WaitUntil(() => host.CaptureHandle == 0, "Primary release did not release capture.");
            Assert("edit-regressions-do-not-leak-input", target.Counts.SameAs(inputBefore),
                "Queued coordinates and multiple buttons still do not send input to the lower HWND.");
            await ToggleWithHotkey(() => toggles, 2);
            Assert("capture-clear-after-edit", Native.GetCapture() == 0, "No mouse capture survives edit→passthrough.");

            for (var i = 0; i < 100; i++)
            {
                var expected = toggles + 1;
                var watch = Stopwatch.StartNew();
                await ToggleWithHotkey(() => toggles, expected);
                timings.Add(watch.Elapsed.TotalMilliseconds);
                if (editing)
                {
                    EnsureSafePoint(target, opaque);
                    Native.Move(opaque);
                    Native.Mouse(2); // Hold the mouse while the next mode toggle cancels capture.
                    await WaitUntil(() => host.CaptureHandle != 0,
                        $"Pointer down did not acquire capture at cycle {i + 1}; foreground={Native.GetForegroundWindow()}, hit={Native.WindowFromPoint(opaque)}.");
                    Assert($"cycle-{i + 1:000}-input-captures", host.CaptureHandle != 0,
                        "Edit pointer-down acquires capture before cancellation.");
                }
                else
                {
                    Assert($"cycle-{i + 1:000}-capture-clear", Native.GetCapture() == 0,
                        "A global-hotkey mode switch while dragging releases capture.");
                    Native.Mouse(4);
                    Assert($"cycle-{i + 1:000}-inputs-hidden", host.Diagnostics.All(d => !Native.IsWindowVisible(d.InputHandle)),
                        "Input surfaces do not linger after the mode switch.");
                }
            }
            Assert("100-real-hotkey-mode-switches", toggles == 102 && !editing,
                "100 additional system hotkey transitions completed with the final mode restored to passthrough.");
            var togglesBeforeHide = toggles;
            await HideWithHotkey();
            await WaitUntil(() => !visible, "Hide hotkey did not arrive.");
            Assert("hide-releases-all-input", Native.GetCapture() == 0 && host.Diagnostics.All(d =>
                !Native.IsWindowVisible(d.DisplayHandle) && !Native.IsWindowVisible(d.InputHandle)),
                "Hide hotkey removes both surface types and releases capture.");
            await HideWithHotkey();
            await WaitUntil(() => visible, "Restore hotkey did not arrive.");
            Assert("restore-keeps-passthrough", toggles == togglesBeforeHide && host.Diagnostics.All(d =>
                Native.IsWindowVisible(d.DisplayHandle) && !Native.IsWindowVisible(d.InputHandle)),
                "Restore makes annotations visible without unexpectedly taking input.");

            CoordinateChecks();
            stressRender = true;
            for (var i = 0; i < 30; i++)
            {
                host.Render();
                renderTimings.Add(host.LastRenderMilliseconds);
                await Task.Yield();
            }
            stressRender = false;
            host.Render();
            targetCounts = target.Counts.Snapshot();
            _exitCode = Errors.Count == 0 && Checks.All(c => c.Passed) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Errors.Add(ex.ToString());
            Console.Error.WriteLine(ex);
        }
        finally
        {
            // Keep the owned windows alive during input cleanup, so button/key releases cannot
            // accidentally land in the user's original foreground application after destruction.
            var foreground = Native.GetForegroundWindow();
            if (cleanupTarget?.Handle == foreground || cleanupHost?.Diagnostics.Any(d => d.InputHandle == foreground) == true)
            {
                Native.Mouse(4); Native.Mouse(16);
                Native.Key(0x46, true); Native.Key(0x10, true); Native.Key(0x12, true); Native.Key(0x11, true);
                Native.ReleaseCapture();
            }
            cleanupHost?.Dispose();
            cleanupTarget?.Dispose();
            Native.SetCursorPos(previousCursor.X, previousCursor.Y);
            if (previousForeground != 0) Native.SetForegroundWindow(previousForeground);
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            var sortedTimings = timings.Order().ToArray();
            var evidence = new
            {
                schemaVersion = 1,
                generatedAt = DateTimeOffset.Now,
                os = Environment.OSVersion.VersionString,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                dpiAwareness = "PerMonitorV2 (manifest)",
                systemDoubleClickMilliseconds = Native.GetDoubleClickTime(),
                actualMonitors = monitors,
                originalCheckCount = Checks.Count,
                originalPassedCount = Checks.Count(c => c.Passed),
                primaryScreenProbe = primaryProbe?.Evidence,
                checks = Checks,
                errors = Errors,
                targetCounts,
                overlayPointerCounts = pointers.ToDictionary(p => p.Key.ToString(), p => p.Value),
                modeSwitchRoundTripMilliseconds = new { samples = sortedTimings.Length,
                    p95 = sortedTimings.Length == 0 ? (double?)null : sortedTimings[(int)Math.Ceiling(sortedTimings.Length * .95) - 1],
                    note = "Includes test polling delay and SendInput dispatch; this is not frame-render latency." },
                basic1000LineRenderMilliseconds = new { samples = renderTimings.Count,
                    p50 = Percentile(renderTimings, .5), p95 = Percentile(renderTimings, .95),
                    note = "Skia canvas line draw + full native surface presentation across actual monitors. No editable object scene yet." },
                limitations = new[] { "No external game/application was automated.",
                    "Only monitors physically present on this machine were exercised. Synthetic 100/150/200% and negative coordinates are arithmetic tests, not proof of real mixed-DPI hardware behavior.",
                    "Windows 10 and borderless/fullscreen games require separate runtime acceptance.",
                    "This runner validates M1 platform plumbing, not M2–M6 functionality or visual design." },
                passed = _exitCode == 0
            };
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Evidence: {evidencePath}");
            Console.WriteLine($"{Checks.Count(c => c.Passed)}/{Checks.Count} checks passed; {Errors.Count} errors.");
        }
    }

    private static void CoordinateChecks()
    {
        foreach (var dpi in new uint[] { 96, 144, 192 })
        {
            var monitor = new MonitorInfo("synthetic", new PixelBounds(-2560, -720, 2560, 1440),
                new PixelBounds(-2560, -720, 2560, 1400), dpi, dpi, false);
            var dip = new OverlayPoint(120, 80);
            var pixel = OverlayCoordinates.DipToPixel(dip, monitor);
            var roundtrip = OverlayCoordinates.PixelToDip(pixel, monitor);
            Assert($"coordinate-{dpi * 100 / 96}percent-negative-origin", Math.Abs(pixel.X - (-2560 + 120 * dpi / 96.0)) < 1e-9 &&
                Math.Abs(pixel.Y - (-720 + 80 * dpi / 96.0)) < 1e-9 &&
                Math.Abs(roundtrip.X - dip.X) < 1e-9 && Math.Abs(roundtrip.Y - dip.Y) < 1e-9,
                "Production coordinate conversion checked against physical-pixel arithmetic and inverse round trip; synthetic topology only.");
        }
    }

    private static double? Percentile(List<double> values, double percentile) => values.Count == 0 ? null :
        values.Order().ElementAt((int)Math.Ceiling(values.Count * percentile) - 1);

    private static bool CursorAt(Native.Point point) => Native.GetCursorPos(out var actual) &&
        Math.Abs(actual.X-point.X) <= 1 && Math.Abs(actual.Y-point.Y) <= 1;

    private static bool Near(OverlayPoint actual, Native.Point expected) =>
        Math.Abs(actual.X-expected.X) <= 1 && Math.Abs(actual.Y-expected.Y) <= 1;

    private static async Task ExercisePointerAndKeyboard(Native.Point point)
    {
        Native.Move(point);
        await WaitUntil(() => CursorAt(point),
            "Injected pointer did not arrive at the self-owned test point.");
        Native.Mouse(2); Native.Mouse(4);
        Native.Mouse(2); Native.Mouse(4); await Task.Delay(50);
        var drag = new Native.Point(point.X + 55, point.Y + 30);
        Native.Move(drag); Native.Mouse(2); await Task.Delay(20);
        Native.Move(new Native.Point(drag.X + 45, drag.Y + 25)); await Task.Delay(30);
        Native.Mouse(4); Native.Mouse(0x0800, 120);
        Native.Key(0x41); Native.Key(0x41, true); await Task.Delay(70);
    }

    private static async Task ToggleWithHotkey(Func<int> count, int expected)
    {
        Native.Key(0x11); Native.Key(0x12); Native.Key(0x46); Native.Key(0x46, true);
        Native.Key(0x12, true); Native.Key(0x11, true);
        await WaitUntil(() => count() >= expected, "Global toggle hotkey did not arrive.");
    }

    private static async Task HideWithHotkey()
    {
        Native.Key(0x11); Native.Key(0x12); Native.Key(0x10); Native.Key(0x46); Native.Key(0x46, true);
        Native.Key(0x10, true); Native.Key(0x12, true); Native.Key(0x11, true);
        await Task.Delay(25);
    }

    private static async Task WaitUntil(Func<bool> condition, string error)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException(error);
            await Task.Delay(8);
        }
    }

    private static void EnsureSafePoint(NativeTestWindow target, Native.Point point)
    {
        if (!target.OwnsPoint(point) || !target.OwnsPoint(new Native.Point(point.X + 100, point.Y + 55)))
            throw new InvalidOperationException("Test point would leave the self-owned window. No input injected.");
    }

    private static void Assert(string name, bool passed, string detail)
    {
        Checks.Add(new CheckResult(name, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException("Check failed: " + name);
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Docs"))) directory = directory.Parent;
        return directory?.FullName ?? Environment.CurrentDirectory;
    }

    private sealed record CheckResult(string Name, bool Passed, string Detail);
}
