using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using Button = System.Windows.Controls.Button;
using Wpf = System.Windows;

namespace FloatCanvas.SessionTests;

internal static partial class Program
{
    private static bool ToolbarZOrderProbe;
    private static bool ToolbarZCaptureRestoreExtension;
    private static string ToolbarZOrderReportPath => Path.Combine(Root, "artifacts", "e2e", "session-toolbar-z-order.json");
    private static readonly List<object> ToolbarZOrderStages = [];
    private static readonly List<CheckResult> ToolbarZRestoreChecks = [];

    private static async Task RunToolbarZOrderProbe(Wpf.Application application)
    {
        using var lower = new NativeTestWindow();
        Check(Native.SetWindowPos(lower.Handle, new nint(-1), 0, 0, 0, 0, 0x13),
            "Own lower fixture is topmost without any foreign activation");
        lower.Activate();
        if (WaitForActivation)
        {
            Console.WriteLine($"WAITING_FOR_SESSION_TARGET_ACTIVATION hwnd={lower.Handle} pid={Environment.ProcessId}");
            var activation = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() != lower.Handle)
            {
                if (activation.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("Own session target activation timed out; no input injected.");
                await Task.Delay(20);
            }
        }
        Check(Native.GetForegroundWindow() == lower.Handle, "Exact own lower HWND has initial foreground before production construction");

        // This recovery background is generated from a solid color. No desktop image is
        // acquired, so this scenario cannot depend on capture-affinity restoration.
        int left = Native.GetSystemMetrics(76), top = Native.GetSystemMetrics(77);
        int width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        Check(width > 0 && height > 0, "Virtual desktop has a valid physical-pixel fixture extent");
        var assets = new AssetLibrary();
        using (var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Cannot allocate own solid-color fixture."))
        {
            surface.Canvas.Clear(SKColors.White);
            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            string id = assets.Add(png.ToArray());
            var live = new SceneDocument { Viewport = new(left, top, width, height) };
            var capture = new SceneDocument
            {
                Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, width, height),
                Background = new() { Kind = ElementKind.Image, AssetId = id, Width = width, Height = height, Locked = true },
                Elements = [new() { Kind = ElementKind.Rectangle, Width = 70, Height = 40,
                    Style = new() { Fill = 0xFFFF3333, Stroke = 0xFFFF3333 }, Transform = Affine2D.Translation(70, 180) }]
            };
            await new RecoveryStore(Path.Combine(Profile!, "recovery")).SaveAsync(
                new() { LiveDocument = live, CaptureDocument = capture, CaptureOrigin = new(left, top) }, assets);
        }

        object? session = null;
        OverlayHost? host = null;
        KeyboardInputIsolation? isolation = null;
        try
        {
            session = Construct(application);
            host = AttachSessionDiagnostics(session, lower.Handle);
            await CallAsync(session, "InitializeDocumentsAsync", true);
            PauseAutomaticRecovery(session);
            isolation = (KeyboardInputIsolation?)typeof(OverlayHost)
                .GetField("_keyboardIsolation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host);
            var live = Field<EditorSession>(session, "_live");
            var capture = Field<EditorSession?>(session, "_capture");
            Check(ReferenceEquals(Active(session), live) && capture is not null && !host.IsEditing,
                "Real recovery restores both own documents and starts on live pass-through");
            bool entry = (bool)Call(session, "SetEditing", true)!;
            var toolbar = Field<Wpf.Window>(session, "_toolbar");
            var row = (StackPanel)((Border)toolbar.Content).Child;
            var blue = row.Children.OfType<Button>().Single(button => button.Content is string label && label == "蓝");
            ToolbarZOrderStages.Add(new { Phase = "InitialSetEditingReturned", Result = entry,
                host.IsEditing, host.IsKeyboardIsolationActive, ToolbarVisible = toolbar.IsVisible,
                Foreground = Native.GetForegroundWindow().ToInt64(), Toolbar = new WindowInteropHelper(toolbar).Handle.ToInt64() });
            Check(entry && host.IsEditing, "Single actual production SetEditing returns true without a correcting activation");
            await SettleOwnToolbarScene(application);
            var before = ObserveToolbarZOrder("LiveBeforeResume", session, host, toolbar, blue);
            ToolbarZOrderStages.Add(before);
            Check(before.Editing && before.Isolation && before.ToolbarVisible && before.ToolbarEnabled && before.CenterHitToolbar && before.BluePixels >= 3 && before.ToolbarAboveCoveringOverlays,
                "Live baseline toolbar is enabled, physically hittable, above covering overlays and visibly contains its blue glyph");

            // Original production method seam: no tool-window Show/Activate/SetWindowPos,
            // input injection, or focus assignment is added before or after this call.
            Call(session, "ResumeCapture");
            ToolbarZOrderStages.Add(ObserveToolbarZOrder("ResumeReturnedImmediately", session, host, toolbar, blue));
            await SettleOwnToolbarScene(application);
            var after = ObserveToolbarZOrder("CaptureAfterResume", session, host, toolbar, blue);
            ToolbarZOrderStages.Add(after);
            Results.Add(new("Resume uses the recovered capture while editing and the existing toolbar remain visible and enabled",
                ReferenceEquals(Active(session), capture) && after.Editing && after.Isolation && after.ToolbarVisible && after.ToolbarEnabled));
            Results.Add(new("After ResumeCapture, the real toolbar button center still hits the toolbar HWND", after.CenterHitToolbar));
            Results.Add(new("After ResumeCapture, the existing toolbar remains above every covering visible display/input HWND", after.ToolbarAboveCoveringOverlays));
            Results.Add(new("After ResumeCapture, the toolbar's real blue glyph remains visible in screen pixels", after.BluePixels >= 3));
            if (ToolbarZCaptureRestoreExtension)
            {
                nint toolbarHandle = new WindowInteropHelper(toolbar).Handle;
                using (host.HideForCapture([toolbarHandle]))
                    ToolbarZOrderStages.Add(new { Phase = "InsideOwnCaptureVisibilityScope", host.IsEditing, host.IsVisible,
                        ToolbarNativeVisible = Native.IsWindowVisible(toolbarHandle),
                        Windows = host.Diagnostics.Select(window => new
                        {
                            DisplayHandle = window.DisplayHandle.ToInt64(), InputHandle = window.InputHandle.ToInt64(),
                            window.Bounds, window.DisplayExtendedStyle, window.InputExtendedStyle,
                            window.DisplayVisible, window.InputVisible
                        }).ToArray() });
                await SettleOwnToolbarScene(application);
                var restored = ObserveToolbarZOrder("CaptureAfterOwnHideRestore", session, host, toolbar, blue);
                ToolbarZOrderStages.Add(restored);
                ToolbarZRestoreChecks.Add(new("Own capture scope restores editing/keyboard isolation and original toolbar visibility without replacing the draft",
                    restored.Editing && restored.Isolation && restored.ToolbarVisible && restored.ToolbarEnabled && ReferenceEquals(Active(session), capture)));
                ToolbarZRestoreChecks.Add(new("After capture scope restoration, the real toolbar button center still hits its own HWND", restored.CenterHitToolbar));
                ToolbarZRestoreChecks.Add(new("After capture scope restoration, the toolbar is above its covering display/input HWNDs", restored.ToolbarAboveCoveringOverlays));
                ToolbarZRestoreChecks.Add(new("After capture scope restoration, screen pixels retain the toolbar's blue glyph", restored.BluePixels >= 3));

                // Additional observation (not another passing assertion): exercise the
                // same scope with the real owned Inspector above its toolbar owner.
                Call(session, "ToggleInspector");
                var inspector = Field<Wpf.Window>(session, "_inspector");
                await SettleOwnToolbarScene(application);
                nint inspectorHandle = new WindowInteropHelper(inspector).Handle;
                var inspectorBefore = ObserveToolbarZOrder("InspectorBeforeOwnHideRestore", session, host, toolbar, blue);
                int beforeInspectorRank = CurrentToolbarZRank(inspectorHandle);
                using (host.HideForCapture([toolbarHandle, inspectorHandle])) { }
                await SettleOwnToolbarScene(application);
                var inspectorAfter = ObserveToolbarZOrder("InspectorAfterOwnHideRestore", session, host, toolbar, blue);
                ToolbarZOrderStages.Add(new { Phase = "OwnedInspectorCaptureRestoreObservation", InspectorHandle = inspectorHandle.ToInt64(),
                    InspectorVisible = inspector.IsVisible, InspectorNativeVisible = Native.IsWindowVisible(inspectorHandle),
                    BeforeInspectorZRank = beforeInspectorRank, AfterInspectorZRank = CurrentToolbarZRank(inspectorHandle),
                    BeforeToolbar = inspectorBefore, AfterToolbar = inspectorAfter,
                    AssertionScope = "Observation only: the Inspector may overlap toolbar pixels; the original11 and toolbar-only capture4 remain separate." });
                Call(session, "ToggleInspector");
            }
        }
        finally
        {
            if (session is IDisposable disposable)
            {
                try { Call(session, "SetEditing", false); }
                finally { disposable.Dispose(); }
            }
            ToolbarZOrderStages.Add(new { Phase = "AfterDispose", KeyboardThreadAlive = isolation?.IsThreadAlive,
                KeyboardLeaseActive = isolation?.IsLeaseActive, KeyboardQueue = isolation?.QueuedPacketCount,
                Error = isolation?.LastWin32Error, Failure = isolation?.Failure?.ToString() });
        }
        Check(isolation is not null && !isolation.IsThreadAlive && !isolation.IsLeaseActive && isolation.Failure is null,
            "Production keyboard lease and worker stop after the independent display probe");
        if (Results.Concat(ToolbarZRestoreChecks).Any(result => !result.Passed)) throw new InvalidOperationException("Recovered screenshot covers or intercepts the already visible toolbar; see z-order and pixel evidence.");
    }

    private static int CurrentToolbarZRank(nint target)
    {
        var visited = new HashSet<nint>();
        nint window = ToolbarZNative.GetTopWindow(0);
        for (int rank = 0; window != 0 && rank < 16384 && visited.Add(window); rank++, window = ToolbarZNative.GetWindow(window, 2))
            if (window == target) return rank;
        return int.MaxValue;
    }

    private static async Task SettleOwnToolbarScene(Wpf.Application application)
    {
        await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        // DwmFlush is only a composition observation boundary here, not a screenshot
        // freshness guarantee. Z-rank and physical hit testing are independent evidence.
        ToolbarZNative.DwmFlush();
    }

    private static ToolbarZObservation ObserveToolbarZOrder(string phase, object session, OverlayHost host, Wpf.Window toolbar, Button blue)
    {
        nint toolbarHandle = new WindowInteropHelper(toolbar).Handle;
        var corner = blue.PointToScreen(new(0, 0));
        var end = blue.PointToScreen(new(blue.ActualWidth, blue.ActualHeight));
        var center = blue.PointToScreen(new(blue.ActualWidth / 2, blue.ActualHeight / 2));
        var centerPoint = new Native.Point((int)Math.Round(center.X), (int)Math.Round(center.Y));
        var windows = host.Diagnostics;
        var ranks = new Dictionary<nint, int>();
        nint cursor = ToolbarZNative.GetTopWindow(0);
        var visited = new HashSet<nint>();
        for (int rank = 0; cursor != 0 && rank < 16384 && visited.Add(cursor); rank++, cursor = ToolbarZNative.GetWindow(cursor, 2))
            ranks[cursor] = rank;
        int Rank(nint window) => ranks.TryGetValue(window, out int rank) ? rank : int.MaxValue;
        var overlays = windows.SelectMany(window => new[]
        {
            new ToolbarZWindow("Display", window.DisplayHandle.ToInt64(), Rank(window.DisplayHandle), window.DisplayVisible,
                window.Bounds.Contains(new(centerPoint.X, centerPoint.Y))),
            new ToolbarZWindow("Input", window.InputHandle.ToInt64(), Rank(window.InputHandle), window.InputVisible,
                window.Bounds.Contains(new(centerPoint.X, centerPoint.Y)))
        }).ToArray();
        int toolbarRank = Rank(toolbarHandle);
        int bluePixels = 0, whitePixels = 0, samples = 0;
        // Read the same exact own-button ROI in one GDI transfer, including layered
        // windows. Per-pixel screen GetPixel causes thousands of synchronous GPU
        // readbacks and can stall this UI thread long enough to alter the measured
        // keyboard lease. Pixel classification below only reads locked local memory.
        int sampleLeft = (int)Math.Ceiling(corner.X) + 2, sampleTop = (int)Math.Ceiling(corner.Y) + 2;
        int sampleWidth = (int)Math.Floor(end.X) - 2 - sampleLeft;
        int sampleHeight = (int)Math.Floor(end.Y) - 2 - sampleTop;
        if (sampleWidth <= 0 || sampleHeight <= 0) throw new InvalidOperationException("Own toolbar button has no measurable physical interior.");
        var readback = Stopwatch.StartNew();
        using var bitmap = new System.Drawing.Bitmap(sampleWidth, sampleHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            // Graphics.CopyFromScreen rejects the combined enum in .NET 10, so use
            // one native SRCCOPY|CAPTUREBLT into the bitmap's DC instead.
            nint screenDc = Native.GetDC(0);
            try
            {
                if (screenDc == 0) throw new System.ComponentModel.Win32Exception("Cannot obtain screen DC for the own toolbar ROI.");
                nint targetDc = graphics.GetHdc();
                try
                {
                    if (!ToolbarZNative.BitBlt(targetDc, 0, 0, sampleWidth, sampleHeight, screenDc, sampleLeft, sampleTop, 0x40CC0020))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Own toolbar ROI readback failed.");
                }
                finally { graphics.ReleaseHdc(targetDc); }
            }
            finally { if (screenDc != 0) Native.ReleaseDC(0, screenDc); }
        }
        var bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, sampleWidth, sampleHeight),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[sampleWidth * 4];
            for (int y = 0; y < sampleHeight; y++)
            {
                Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                for (int x = 0; x < sampleWidth; x++)
                {
                    int offset = x * 4;
                    int b = row[offset], green = row[offset + 1], red = row[offset + 2];
                    samples++;
                    if (b > red + 50 && b > 140 && green > 70) bluePixels++;
                    if (red >= 250 && green >= 250 && b >= 250) whitePixels++;
                }
            }
        }
        finally { bitmap.UnlockBits(bits); }
        readback.Stop();
        nint hit = Native.WindowFromPoint(centerPoint);
        return new(phase, Stopwatch.GetTimestamp(), host.IsEditing, host.IsKeyboardIsolationActive,
            Active(session).Document.Kind.ToString(), toolbar.IsVisible, toolbar.IsEnabled, toolbar.IsActive,
            toolbar.IsKeyboardFocusWithin, toolbarHandle.ToInt64(), toolbarRank, Native.GetForegroundWindow().ToInt64(),
            GetFocus().ToInt64(), hit.ToInt64(), hit == toolbarHandle, bluePixels, whitePixels, samples, readback.Elapsed.TotalMilliseconds,
            toolbarRank != int.MaxValue && overlays.Where(window => window.Visible && window.CoversButton).All(window => toolbarRank < window.ZRank),
            centerPoint.X, centerPoint.Y, overlays, Field<TextBlock>(session, "_status").Text);
    }

    private static void WriteToolbarZOrderReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ToolbarZOrderReportPath)!);
        File.WriteAllText(ToolbarZOrderReportPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, Timestamp = DateTimeOffset.Now, Profile,
            Checks = Results.ToArray(), Stages = ToolbarZOrderStages.ToArray(), SessionDiagnostics = SessionDiagnostics.ToArray(),
            OriginalRegression = new { ExpectedCount = 11, Passed = Results.Count == 11 && Results.All(result => result.Passed) },
            CaptureRestoreExtension = new { Requested = ToolbarZCaptureRestoreExtension, ExpectedCount = 4,
                Checks = ToolbarZRestoreChecks.ToArray(), Passed = ToolbarZCaptureRestoreExtension && ToolbarZRestoreChecks.Count == 4 && ToolbarZRestoreChecks.All(result => result.Passed) },
            PhysicalInputInjection = false, DesktopCapture = false, OwnToolbarRoiReadback = true, CaptureAffinityScopeCreated = ToolbarZCaptureRestoreExtension,
            Method = "Fresh recovery contains own synthetic white background and vector marker; a single real production editing entry establishes a blue-glyph toolbar pixel/hit/z-rank baseline. Production ResumeCapture is then called while the toolbar is already visible, followed only by readback. No corrective activation, re-show, raise or focus assignment is performed.",
            Scope = "Independent opt-in toolbar display/hit/z-order regression using production methods; does not prove actual screenshot acquisition, mouse invocation, other tool windows, games or full v1 completion. Pixel samples are only within the own blue-button rectangle; full desktop pixels are neither captured nor retained."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record ToolbarZWindow(string Kind, long Handle, int ZRank, bool Visible, bool CoversButton);
    private sealed record ToolbarZObservation(string Phase, long At, bool Editing, bool Isolation, string DocumentKind,
        bool ToolbarVisible, bool ToolbarEnabled, bool ToolbarActive, bool ToolbarKeyboardFocus, long ToolbarHandle,
        int ToolbarZRank, long ForegroundHandle, long FocusHandle, long CenterHitHandle, bool CenterHitToolbar,
        int BluePixels, int WhitePixels, int SamplePixels, double PixelReadbackMilliseconds, bool ToolbarAboveCoveringOverlays, int CenterX, int CenterY,
        ToolbarZWindow[] OverlayWindows, string Status);
    private static class ToolbarZNative
    {
        [DllImport("user32.dll")] internal static extern nint GetTopWindow(nint parent);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(nint destination, int x, int y, int width, int height,
            nint source, int sourceX, int sourceY, uint operation);
    }
}
