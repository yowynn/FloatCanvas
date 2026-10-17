using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using Wpf = System.Windows;

namespace FloatCanvas.SessionTests;

internal static partial class Program
{
    private static readonly Type DesktopType = Type.GetType("FloatCanvas.App.DesktopSession, FloatCanvas", throwOnError: true)!;
    private static readonly List<CheckResult> Results = [];
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string ReportPath => Path.Combine(Root, "artifacts", "e2e", "session-tests.json");
    private static string? Profile;
    private static string? Failure;
    private static long IdleFrames;
    private static RectD RetainedScreenshotBounds;
    private static ErrorModalEvidence? ErrorModal;
    private static bool WaitForActivation;
    private static bool RasterOpacityProbe;
    private static readonly Stopwatch DiagnosticClock = Stopwatch.StartNew();
    private static readonly List<object> SessionDiagnostics = [];

    [STAThread]
    private static int Main(string[] args)
    {
        WaitForActivation = args.Contains("--wait-for-activation");
        RasterOpacityProbe = args.Contains("--raster-opacity-probe");
        ExternalClipboardProbe = args.Contains("--external-clipboard-probe");
        ToolbarZOrderProbe = args.Contains("--toolbar-z-order-probe");
        ToolbarZCaptureRestoreExtension = args.Contains("--capture-restore-extension");
        ToolbarZCanvasActivationExtension = args.Contains("--canvas-activation-extension");
        if (args.Contains("--pointer-surface")) return ShowPointerSurface();
        if (args.Contains("--manual-surface")) return ShowManualSurface();
        if (args.Contains("--prepare"))
        {
            PrepareFixture().GetAwaiter().GetResult();
            return 0;
        }
        Profile = Path.Combine(Root, "artifacts", "e2e", "session-profiles", Guid.NewGuid().ToString("N"));
        ConfigureProfile(Profile);
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Failure = "Session test exceeded 90 seconds, possibly an unexpected production error dialog.";
            try { if (ToolbarZOrderProbe) WriteToolbarZOrderReport(false); else WriteReport(false); }
            catch (Exception ex) { Console.Error.WriteLine("Watchdog report failed: " + ex); }
            finally { Environment.Exit(1); }
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        var application = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        application.DispatcherUnhandledException += (_, e) => { Failure = e.Exception.ToString(); e.Handled = true; };
        int exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try { if (ToolbarZOrderProbe) await RunToolbarZOrderProbe(application); else await Run(application); exitCode = 0; }
            catch (Exception ex) { Failure = ex.ToString(); Console.Error.WriteLine(ex); }
            finally
            {
                try
                {
                    if (ToolbarZOrderProbe) WriteToolbarZOrderReport(exitCode == 0 && Failure is null);
                    else WriteReport(exitCode == 0 && Failure is null);
                }
                catch (Exception ex)
                {
                    Failure = (Failure is null ? "" : Failure + "\n") + "Report: " + ex;
                    Console.Error.WriteLine("Final report failed: " + ex); exitCode = 1;
                }
                finally { application.Shutdown(exitCode); }
            }
        };
        application.Run();
        Console.WriteLine($"Session checks: {Results.Count(r => r.Passed)}/{Results.Count}; report: {(ToolbarZOrderProbe ? ToolbarZOrderReportPath : ReportPath)}");
        return exitCode;
    }

    // A nonprivate backdrop for real application UI acceptance. It creates no overlay,
    // session, hooks or injected input, and is never part of the default test run.
    private static int ShowManualSurface()
    {
        var application = new Wpf.Application();
        var elapsed = Stopwatch.StartNew();
        string closeReason = "Window close message or external shutdown", lastInput = "None";
        var canvas = new System.Windows.Controls.Canvas { Background = System.Windows.Media.Brushes.White };
        var window = new Wpf.Window
        {
            Title = "浮绘验收底板 · 自建内容", Content = canvas, Topmost = true,
            WindowState = Wpf.WindowState.Maximized, WindowStyle = Wpf.WindowStyle.None,
            ResizeMode = Wpf.ResizeMode.NoResize
        };
        // Target-window activation helpers can restore a maximized window before input.
        // Keep this opt-in backdrop covering the monitor so capture never exposes another app.
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == Wpf.WindowState.Normal)
                window.WindowState = Wpf.WindowState.Maximized;
        };
        window.PreviewKeyDown += (_, e) => lastInput = "Key: " + e.Key;
        window.PreviewMouseDown += (_, e) =>
        {
            var point = e.GetPosition(window);
            lastInput = $"Mouse: {e.ChangedButton} at {point.X:F1},{point.Y:F1} DIP";
        };
        window.Closing += (_, _) => Console.WriteLine("MANUAL_SURFACE_CLOSING " + JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.Now, ProcessId = Environment.ProcessId, ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            Reason = closeReason, LastOwnInput = lastInput, ForegroundHandle = Native.GetForegroundWindow().ToInt64()
        }));
        void Label(string text, double x, double y, double size = 24)
        {
            var label = new System.Windows.Controls.TextBlock
            {
                Text = text, FontSize = size, Foreground = System.Windows.Media.Brushes.DimGray,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI")
            };
            System.Windows.Controls.Canvas.SetLeft(label, x); System.Windows.Controls.Canvas.SetTop(label, y);
            canvas.Children.Add(label);
        }
        Label("FloatCanvas · 自建验收底板", 48, 90, 32);
        Label("此窗口只包含测试图形。截图请选择白色区域，不包含其他应用。", 48, 145, 18);
        foreach (var (x, color) in new[] { (80d, System.Windows.Media.Brushes.LightBlue),
                     (320d, System.Windows.Media.Brushes.LightGreen), (560d, System.Windows.Media.Brushes.MistyRose) })
        {
            var rectangle = new System.Windows.Shapes.Rectangle { Width = 180, Height = 120, Fill = color };
            System.Windows.Controls.Canvas.SetLeft(rectangle, x); System.Windows.Controls.Canvas.SetTop(rectangle, 250);
            canvas.Children.Add(rectangle);
        }
        var close = new System.Windows.Controls.Button
        { Content = "关闭验收底板", Padding = new Wpf.Thickness(12, 6, 12, 6) };
        close.Click += (_, _) => { closeReason = "Own close button"; window.Close(); };
        System.Windows.Controls.Canvas.SetLeft(close, 48); System.Windows.Controls.Canvas.SetTop(close, 20);
        canvas.Children.Add(close);
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        deadline.Tick += (_, _) => { closeReason = "Twenty minute deadline"; window.Close(); };
        window.Closed += (_, _) => { deadline.Stop(); Console.WriteLine($"MANUAL_SURFACE_CLOSED PID={Environment.ProcessId}"); };
        window.ContentRendered += (_, _) => Console.WriteLine($"MANUAL_SURFACE_READY PID={Environment.ProcessId}; close the own window to end; 20-minute deadline.");
        deadline.Start(); application.Run(window); return 0;
    }

    private static async Task PrepareFixture()
    {
        string directory = Path.Combine(Root, "artifacts", "e2e-ui-profile", "recovery");
        var assets = new AssetLibrary(); string id = assets.Add(FixturePng());
        var live = FixtureDocument(new(150, 160, 700, 600));
        live.Elements.Add(new() { Kind = ElementKind.Image, AssetId = id, Width = 288, Height = 192, Transform = Affine2D.Translation(200, 390) });
        await new RecoveryStore(directory).SaveAsync(new() { LiveDocument = live }, assets);
        Console.WriteLine($"Prepared nonprivate UI fixture: {directory}");
    }

    private static SceneDocument FixtureDocument(RectD area) => new()
    {
        Viewport = new(0, 0, 1920, 1080), NextNumber = 8,
        Elements =
        [
            new() { Kind = ElementKind.Text, Text = "浮绘 · 中文多行\n这是一份自建验收样本", Width = 290, Height = 95, Style = new() { FontSize = 22, Stroke = 0xFF2383E2 }, Transform = Affine2D.Translation(area.X + 10, area.Y + 12) },
            new() { Kind = ElementKind.Number, Number = 7, Width = 46, Height = 46, Style = new() { FontSize = 24, Stroke = 0xFFEB5757, Fill = 0xFFFFEEEE }, Transform = Affine2D.Translation(area.X + 174, area.Y + 145) },
            new() { Kind = ElementKind.Rectangle, Width = 80, Height = 48, Style = new() { Stroke = 0xFF2383E2, Fill = 0x442383E2, StrokeWidth = 3 }, Transform = Affine2D.Translation(area.X + 15, area.Y + 126).Compose(Affine2D.Rotation(12)) }
        ]
    };

    private static byte[] FixturePng()
    {
        using var surface = SKSurface.Create(new SKImageInfo(240, 160, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(35, 131, 226, 180), IsAntialias = true };
        surface.Canvas.DrawRoundRect(new SKRect(18, 20, 220, 140), 16, 16, paint);
        paint.Color = new(250, 100, 90, 220); surface.Canvas.DrawCircle(122, 82, 40, paint);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task Run(Wpf.Application application)
    {
        using var lower = new NativeTestWindow();
        // Keep the fixture visible even when Windows denies foreground activation. The production
        // overlay is created afterwards and remains above this own-process test window.
        Check(Native.SetWindowPos(lower.Handle, new nint(-1), 0, 0, 0, 0, 0x13), "Own lower fixture stays above unrelated desktop windows without activating them");
        lower.Activate();
        RecordSessionState("LowerActivateReturned", null, null, lower.Handle);
        if (WaitForActivation)
        {
            Console.WriteLine($"WAITING_FOR_SESSION_TARGET_ACTIVATION hwnd={lower.Handle} pid={Environment.ProcessId}");
            var activation = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() != lower.Handle)
            {
                if (activation.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("Own session target activation timed out; no input was injected.");
                await Task.Delay(20);
            }
            RecordSessionState("InitialActivationConfirmed", null, null, lower.Handle);
        }
        Native.GetClientRect(lower.Handle, out var client);
        var ownedOrigin = lower.ScreenPoint(32, 130);
        var safe = new RectD(ownedOrigin.X, ownedOrigin.Y, Math.Min(340, client.Right - 64), Math.Min(240, client.Bottom - 156));
        Check(safe.Width >= 300 && safe.Height >= 180, "Self-owned native window has an adequate sample rectangle");
        Check(lower.OwnsPoint(new((int)safe.Left, (int)safe.Top)) && lower.OwnsPoint(new((int)safe.Right - 1, (int)safe.Bottom - 1)), "Every retained screenshot pixel belongs to our native test client");
        object? session = null;
        try
        {
            session = Construct(application);
            var host = AttachSessionDiagnostics(session, lower.Handle);
            RecordSessionState("BeforeInitialize", session, host, lower.Handle);
            await CallAsync(session, "InitializeDocumentsAsync", true);
            PauseAutomaticRecovery(session);
            RecordSessionState("AfterInitialize", session, host, lower.Handle);
            Check(!host.IsEditing, "Production initialization defaults to pass-through");
            Check(!Property<bool>(session, "DocumentOperationBusy"), "Initialization releases interaction lock");
            var live = Field<EditorSession>(session, "_live");
            var seeded = FixtureDocument(safe);
            // Deliberate Core fixtures, not evidence of physical mouse drawing.
            foreach (var element in seeded.Elements) live.Add(element, "集成验收自建对象");
            Check(live.Document.Elements.Count == 3, "Three fixture objects inserted through Core, not mouse automation");
            RecordSessionState("BeforeRecoveryDeadline", session, host, lower.Handle);
            await RecoveryDeadline(session, live);
            RecordSessionState("AfterRecoveryDeadline", session, host, lower.Handle);
            string fixturePath = Path.Combine(Profile!, "fixture-alpha.png"); Directory.CreateDirectory(Profile!);
            await File.WriteAllBytesAsync(fixturePath, FixturePng());
            RecordSessionState("BeforeImport", session, host, lower.Handle);
            await CallAsync(session, "ImportImagesAsync", new string[] { fixturePath }, false);
            RecordSessionState("AfterImport", session, host, lower.Handle);
            Check(live.Document.Elements.Count == 4 && live.SelectedElements().Single().Kind == ElementKind.Image, "Production image import creates and selects an editable image");
            var imported = live.SelectedElements().Single();
            live.UpdateSelection(e => e.Transform = Affine2D.Translation(safe.X + 225, safe.Y + 120).Compose(Affine2D.Scale(.4, .4)), "将测试图片放入自建窗口");
            var assets = Field<AssetLibrary>(session, "_assets");
            using (var fixture = SKBitmap.Decode(assets.Get(imported.AssetId!).Data.Span))
                Check(fixture.GetPixel(0, 0).Alpha == 0, "Production import preserves actual transparent alpha");
            RecordSessionState("BeforeSetEditing", session, host, lower.Handle, requestedEditing: true);
            bool editingAccepted = (bool)Call(session, "SetEditing", true)!;
            RecordSessionState("SetEditingReturned", session, host, lower.Handle, requestedEditing: true, returned: editingAccepted);
            await Task.Delay(80);
            RecordSessionState("AfterExisting80msEntryWait", session, host, lower.Handle, requestedEditing: true, returned: editingAccepted);
            var toolbar = Field<Wpf.Window>(session, "_toolbar");
            Check(toolbar.IsVisible && host.IsEditing, "Production editor activation shows toolbar and captures input");
            Call(session, "ToggleInspector");
            var inspector = Field<Wpf.Window>(session, "_inspector");
            SetProperty(session, "_documentBusy", true);
            Check(!toolbar.IsEnabled && !inspector.IsEnabled, "Actual WPF toolbar and inspector are disabled while busy");
            bool entered = false; Call(session, "RunUiAction", (Action)(() => entered = true));
            Check(!entered, "Production external UI action entry rejects busy reentry");
            SetProperty(session, "_documentBusy", false);
            SetProperty(session, "_documentExitRequested", true);
            Check(Property<bool>(session, "DocumentOperationBusy") && !toolbar.IsEnabled && !inspector.IsEnabled, "Exit drain uses the same actual interaction lock");
            SetProperty(session, "_documentExitRequested", false);
            Check(toolbar.IsEnabled && inspector.IsEnabled, "Interaction lock releases after IO or failed exit");
            Call(session, "ToggleInspector");

            VerifyErrorModal(session, live, host, toolbar);

            // Clipboard is a genuine production roundtrip, scoped and restored before desktop capture.
            var originalClipboard = SnapshotClipboard();
            try
            {
                live.SelectAll(); string[] originalIds = live.Document.Elements.Select(e => e.Id.ToString()).ToArray();
                await CallAsync(session, "CopySelectionAsync");
                var clipboard = Wpf.Clipboard.GetDataObject()!;
                Check(clipboard.GetDataPresent("FloatCanvas.Objects.v1") && clipboard.GetDataPresent("PNG") && clipboard.GetDataPresent(Wpf.DataFormats.Bitmap), "Production copy writes editable objects and interoperable PNG/Bitmap");
                await CallAsync(session, "PasteAsync");
                Check(live.Document.Elements.Count == 8 && live.Selection.Count == 4, "Production paste restores all selected editable objects");
                Check(live.SelectedElements().All(e => !originalIds.Contains(e.Id.ToString())) && live.Document.Elements.Select(e => e.Id).Distinct().Count() == 8, "Pasted objects receive unique IDs");
                Check(live.SelectedElements().Single(e => e.Kind == ElementKind.Image).AssetId == imported.AssetId, "Clipboard image asset deduplicates to original hash");
                Check(live.SelectedElements().Any(e => e.Kind == ElementKind.Text && e.Text.Contains("中文多行")), "Chinese multiline text stays a text object after paste");
                live.TransformSelection(Affine2D.Translation(1, 0));
                Check(live.Undo() && live.Redo(), "Pasted group remains editable with undo and redo");
                if (ExternalClipboardProbe) await VerifyExternalClipboardPaste(session, live);
            }
            finally
            {
                try
                {
                    if (originalClipboard is null) Wpf.Clipboard.Clear(); else Wpf.Clipboard.SetDataObject(originalClipboard, true);
                    ExternalClipboardRestored = ExternalClipboardProbe;
                }
                catch (Exception ex) { throw new InvalidOperationException("Session fixture could not restore the original clipboard.", ex); }
            }

            // WPF Popup owns an independent HWND absent from Application.Windows. Exercise the
            // production capture exclusion against an opaque red popup inside our white client.
            var center = safe.Center;
            var anchor = (Wpf.UIElement)toolbar.Content;
            var localAnchor = anchor.PointFromScreen(new Wpf.Point(center.X, center.Y));
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint,
                HorizontalOffset = localAnchor.X - 36, VerticalOffset = localAnchor.Y - 36,
                AllowsTransparency = true, StaysOpen = true,
                Child = new System.Windows.Controls.Border { Width = 72, Height = 72, Background = System.Windows.Media.Brushes.Red }
            };
            popup.IsOpen = true;
            try
            {
                await Task.Delay(100);
                var popupSource = (HwndSource)Wpf.PresentationSource.FromVisual(popup.Child)!;
                nint popupHandle = popupSource.Handle;
                Check(popupHandle != 0 && Native.IsWindowVisible(popupHandle), "Real opaque WPF popup HWND exists before capture");
                Check(application.Windows.Cast<Wpf.Window>().All(window => new WindowInteropHelper(window).Handle != popupHandle), "Popup HWND is absent from Application.Windows");
                Native.GetWindowRect(popupHandle, out var popupRect);
                var popupCenter = new Native.Point((popupRect.Left + popupRect.Right) / 2, (popupRect.Top + popupRect.Bottom) / 2);
                Check(safe.Contains(new(popupCenter.X, popupCenter.Y)) && lower.OwnsPoint(popupCenter), "Popup sample point remains inside our nonprivate native client");
                uint beforePopupPixel = ReadScreenPixel(popupCenter);
                RecordPopupPixel("BeforeCapture", session, host, lower.Handle, toolbar, popup, popupHandle, popupCenter, beforePopupPixel);
                Check(IsRed(beforePopupPixel), "Actual desktop popup center is opaque red before exclusion");
                SetProperty(session, "_documentBusy", true);
                var clean = await (Task<CapturedDesktop>)Call(session, "CaptureCleanDesktopAsync")!;
                using (var bitmap = SKBitmap.Decode(clean.PngBytes))
                {
                    var color = bitmap.GetPixel(popupCenter.X - clean.Bounds.Left, popupCenter.Y - clean.Bounds.Top);
                    Check(color.Red >= 245 && color.Green >= 245 && color.Blue >= 245 && color.Alpha == 255,
                        $"Production clean capture excludes Popup HWND and reveals underlying white client (actual {color})");
                }
                await Task.Delay(80);
                uint restoredPopupPixel = ReadScreenPixel(popupCenter);
                RecordPopupPixel("AfterRestore", session, host, lower.Handle, toolbar, popup, popupHandle, popupCenter, restoredPopupPixel);
                Check(Native.IsWindowVisible(popupHandle) && IsRed(restoredPopupPixel), "Capture scope restores original Popup visibility and pixels");
            }
            finally { popup.IsOpen = false; SetProperty(session, "_documentBusy", false); }

            var outside = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 35,
                Transform = Affine2D.Translation(safe.Right + 80, safe.Top + 25) };
            var partial = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 45,
                Transform = Affine2D.Translation(safe.Right - 8, safe.Top + 55) };
            live.Add(outside); live.Add(partial);
            string liveContent = JsonSerializer.Serialize(live.Document);
            var oldCounts = lower.Counts.Snapshot();
            await CallAsync(session, "StartCaptureAsync", false);
            PauseAutomaticRecovery(session);
            var capture = Field<EditorSession>(session, "_capture");
            var captureOrigin = Field<PointD>(session, "_captureOrigin");
            double virtualWidth = host.Monitors.Max(m => m.Bounds.Right) - host.Monitors.Min(m => m.Bounds.Left);
            double virtualHeight = host.Monitors.Max(m => m.Bounds.Bottom) - host.Monitors.Min(m => m.Bounds.Top);
            Check(capture.Document.Viewport.Width == virtualWidth && capture.Document.Viewport.Height == virtualHeight, "Production full-screen command captures the entire virtual desktop");
            Check(capture.Document.Kind == DocumentKind.FrozenCapture && capture.Document.Background?.Kind == ElementKind.Image, "Production capture creates an independent frozen document and background");
            Check(capture.Document.Elements.Count == live.Document.Elements.Count && capture.Document.Elements.All(e => live.Document.Elements.All(original => original.Id != e.Id)), "Frozen capture contains independent clones of all visible annotations");
            Check(lower.Counts.SameAs(oldCounts), "Session acceptance used no injected mouse or keyboard events");
            var localSafe = new RectD(safe.X - captureOrigin.X, safe.Y - captureOrigin.Y, safe.Width, safe.Height);
            Call(session, "FinishCaptureRegion", localSafe);
            Check(capture.Document.Viewport == localSafe, "Production region completion sets crop without cutting object geometry");
            Check(capture.Document.Elements.Count == 9 && capture.Document.Elements.All(e => e.Transform.OffsetX != outside.Transform.OffsetX - captureOrigin.X),
                "Region capture removes annotations wholly outside the selected region");
            Check(capture.Document.Elements.Any(e => e.Width == partial.Width && e.Height == partial.Height &&
                e.Transform.OffsetX == partial.Transform.OffsetX - captureOrigin.X), "Partly intersecting annotations retain their complete geometry");
            Check(capture.Undo() && capture.Document.Elements.Count == 10 && capture.Redo() && capture.Document.Elements.Count == 9,
                "Region filtering and viewport cropping undo and redo atomically");
            capture.SetSelection([capture.Document.Elements[0].Id]);
            capture.TransformSelection(Affine2D.Translation(3, 2));
            Check(JsonSerializer.Serialize(live.Document) == liveContent, "Editing screenshot clones never changes live document");

            // Privacy boundary: full desktop capture exists only in memory. Replace its image with a
            // crop of our own client rectangle before any project/recovery file can be written.
            RetainOwnedCrop(capture, assets, localSafe);
            RetainedScreenshotBounds = safe;
            Check(capture.Document.Background!.Width == safe.Width && capture.Document.Background.Height == safe.Height, "Only self-owned screenshot pixels survive into persisted fixtures");
            Call(session, "ReturnToLive");
            Check(ReferenceEquals(Active(session), live) && Field<EditorSession>(session, "_capture") == capture, "Return-to-live preserves screenshot draft without merging changes");
            Call(session, "ResumeCapture");
            Check(ReferenceEquals(Active(session), capture), "Production resume restores screenshot draft");
            Call(session, "SetEditing", false);
            Check(!host.IsEditing && ReferenceEquals(Active(session), live) && !toolbar.IsVisible && !inspector.IsVisible, "Pass-through from screenshot returns to live and hides every editor surface");
            await Task.Delay(130); long frames = host.PresentedFrameCount;
            await Task.Delay(230); IdleFrames = host.PresentedFrameCount - frames;
            Check(IdleFrames == 0, "Static pass-through presents zero stale rendering frames");
            long beforeBatch = host.PresentedFrameCount;
            for (int i = 0; i < 100; i++) Call(session, "RequestRender");
            Check(Property<bool>(session, "IsRenderPending"), "A batch of rendering requests schedules one pending dispatcher operation");
            await Task.Delay(120);
            Check(!Property<bool>(session, "IsRenderPending") && host.PresentedFrameCount - beforeBatch == host.Monitors.Count,
                "One hundred rendering requests coalesce into exactly one complete multi-output presentation");
            if (RasterOpacityProbe)
            {
                var cache = Field<FloatCanvas.Rendering.OverlaySceneCache>(session, "_overlayCache");
                long beforeOpacity = cache.InPlaceOpacityFrameCount;
                live.Execute("半透明光栅验收", d => d.LayerOpacity = .7);
                await Task.Delay(80);
                Check(cache.InPlaceOpacityFrameCount == beforeOpacity + host.Monitors.Count,
                    "Production Host DIB callback uses exact in-place group opacity for every live monitor");
                Check(live.Undo() && live.Document.LayerOpacity == 1, "Opacity probe restores the original live document through undo");
                await Task.Delay(80);
                Check(cache.InPlaceOpacityFrameCount == beforeOpacity + host.Monitors.Count,
                    "Fully opaque production frames bypass the in-place alpha pass");
            }
            await CallAsync(session, "FlushRecoveryAsync");
            var recovery = await new RecoveryStore(Path.Combine(Profile!, "recovery")).LoadAsync();
            Check(recovery is not null && recovery.State.LiveDocument.Elements.Count == 10 && recovery.State.CaptureDocument?.Elements.Count == 9, "Production recovery flush persists live and screenshot documents together");
            Check(recovery!.State.CaptureOrigin == captureOrigin, "Production recovery persists screenshot screen origin");
            CheckPersistedAssetsAreOwned(recovery.Assets, safe);
            string projectPath = Path.Combine(Profile!, "owned-capture.floatcanvas");
            var projectStore = Field<ProjectStore>(session, "_projectStore");
            await projectStore.SaveAsync(projectPath, capture.Document.Clone(), assets);
            var project = await projectStore.LoadAsync(projectPath);
            Check(project.Document.Viewport == localSafe && project.Document.Elements.Count == 9, "Production project store roundtrips the cropped editable screenshot");
            CheckPersistedAssetsAreOwned(project.Assets, safe);
            ((IDisposable)session).Dispose(); session = null; await Task.Delay(50);

            session = Construct(application);
            AttachSessionDiagnostics(session, lower.Handle);
            await CallAsync(session, "InitializeDocumentsAsync", true); PauseAutomaticRecovery(session);
            Call(session, "OnMonitorsChanged");
            var restoredLive = Field<EditorSession>(session, "_live");
            var restoredCapture = Field<EditorSession>(session, "_capture");
            Check(!Field<OverlayHost>(session, "_host").IsEditing && ReferenceEquals(Active(session), restoredLive), "Production reinitialization restores documents in live pass-through mode");
            Check(restoredLive.Document.Elements.Count == 10 && restoredCapture.Document.Elements.Count == 9, "Second real DesktopSession restores both complete object graphs");
            Check(restoredLive.Document.Elements.Any(e => e.Kind == ElementKind.Text && e.Text.Contains("中文多行")) && restoredLive.Document.Elements.Any(e => e.Kind == ElementKind.Number && e.Number == 7), "Chinese text and number semantics survive restart");
            var editable = restoredLive.Document.Elements.First(); restoredLive.SetSelection([editable.Id]);
            var transform = editable.Transform; restoredLive.TransformSelection(Affine2D.Translation(4, 6));
            Check(restoredLive.Undo() && restoredLive.Document.Elements.First(e => e.Id == editable.Id).Transform == transform && restoredLive.Redo(), "Restored objects continue editing with fresh undo and redo history");
            await CallAsync(session, "FlushRecoveryAsync");
            Check(File.Exists(Path.Combine(Profile!, "recovery", "workspace.floatcanvas")), "Final production flush completes on isolated profile");
        }
        finally
        {
            if (session is IDisposable disposable)
            {
                RecordSessionState("BeforeFinalDispose", session, Field<OverlayHost>(session, "_host"), lower.Handle);
                disposable.Dispose();
            }
        }
    }

    // Observation only: no foreground correction, input injection, extra wait or retry is
    // added around the original production entry. A synchronous rejection and a later
    // focus/watchdog release must remain distinguishable in a failing report.
    private static OverlayHost AttachSessionDiagnostics(object session, nint lowerHandle)
    {
        var host = Field<OverlayHost>(session, "_host");
        host.EditingFocusLost += () => RecordSessionState("EditingFocusLost", session, host, lowerHandle);
        host.KeyboardIsolationReleased += reason =>
            RecordSessionState("KeyboardIsolationReleased", session, host, lowerHandle, reason: reason.ToString());
        RecordSessionState("SessionConstructed", session, host, lowerHandle);
        return host;
    }

    private static void RecordSessionState(string phase, object? session, OverlayHost? host, nint lowerHandle,
        bool? requestedEditing = null, bool? returned = null, string? reason = null)
    {
        nint foreground = Native.GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out uint foregroundPid);
        var toolbar = session is null ? null : Field<Wpf.Window>(session, "_toolbar");
        var isolation = host is null ? null : (KeyboardInputIsolation?)typeof(OverlayHost)
            .GetField("_keyboardIsolation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
        long? hostLease = host is null ? null : (long)typeof(OverlayHost)
            .GetField("_keyboardLease", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var sample = new
        {
            Phase = phase, TimestampUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = DiagnosticClock.Elapsed.TotalMilliseconds,
            RequestedEditing = requestedEditing, SetEditingReturned = returned, ReleaseEventReason = reason,
            LowerHandle = (long)lowerHandle, ForegroundHandle = (long)foreground, ForegroundThreadId = foregroundThread,
            ForegroundProcessId = foregroundPid, ForegroundIsOwnProcess = foregroundPid == Environment.ProcessId,
            ForegroundIsLower = foreground == lowerHandle, CurrentThreadFocusHandle = (long)GetFocus(),
            ToolbarHandle = toolbar is null ? 0 : (long)new WindowInteropHelper(toolbar).Handle,
            ToolbarVisible = toolbar?.IsVisible, ToolbarEnabled = toolbar?.IsEnabled,
            Status = session is null ? null : Field<System.Windows.Controls.TextBlock>(session, "_status").Text,
            DocumentBusy = session is null ? (bool?)null : Property<bool>(session, "DocumentOperationBusy"),
            IsEditing = host?.IsEditing, IsVisible = host?.IsVisible,
            KeyboardIsolationEnabled = host?.IsKeyboardIsolationEnabled, KeyboardIsolationActive = host?.IsKeyboardIsolationActive,
            HostKeyboardLeaseId = hostLease, ComponentKeyboardLeaseId = isolation?.CurrentLeaseId,
            LastKeyboardEndReason = isolation?.LastEndReason?.ToString(), KeyboardFailure = isolation?.Failure?.ToString(),
            KeyboardLastWin32Error = isolation?.LastWin32Error, KeyboardThreadAlive = isolation?.IsThreadAlive,
            QueuedKeyboardPackets = isolation?.QueuedPacketCount, ForegroundLossVersion = host?.ForegroundLossVersion,
            CaptureHandle = host is null ? (long?)null : (long)host.CaptureHandle,
            InputWindows = host?.Diagnostics.Select(window => new
            {
                InputHandle = (long)window.InputHandle, DisplayHandle = (long)window.DisplayHandle,
                window.InputVisible, window.DisplayVisible, window.Bounds
            }).ToArray()
        };
        lock (SessionDiagnostics) SessionDiagnostics.Add(sample);
        Console.WriteLine($"SESSION_STATE {JsonSerializer.Serialize(sample)}");
    }

    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private static void RetainOwnedCrop(EditorSession capture, AssetLibrary assets, RectD rectangle)
    {
        var background = capture.Document.Background!;
        using var full = SKImage.FromEncodedData(assets.Get(background.AssetId!).Data.Span);
        using var surface = SKSurface.Create(new SKImageInfo((int)rectangle.Width, (int)rectangle.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(full, -(float)rectangle.X, -(float)rectangle.Y, new SKSamplingOptions(SKFilterMode.Nearest));
        using var owned = surface.Snapshot(); using var encoded = owned.Encode(SKEncodedImageFormat.Png, 100);
        string id = assets.Add(encoded.ToArray());
        capture.Document.Background = new() { Kind = ElementKind.Image, AssetId = id, Width = rectangle.Width, Height = rectangle.Height, Transform = Affine2D.Translation(rectangle.X, rectangle.Y), Locked = true };
        capture.ReplaceDocument(capture.Document.Clone()); // Drop test capture history that could restore the unpersisted full desktop bitmap.
    }
    private static void CheckPersistedAssetsAreOwned(AssetLibrary assets, RectD safe)
    {
        foreach (var resource in assets.Resources)
        {
            using var image = SKImage.FromEncodedData(resource.Data.Span);
            Check(image.Width <= safe.Width && image.Height <= safe.Height, "Every written raster is a small test fixture or self-owned crop");
        }
    }
    private static bool IsRed(uint pixel) => (pixel & 255) >= 245 && ((pixel >> 8) & 255) <= 10 && ((pixel >> 16) & 255) <= 10;
    private static uint ReadScreenPixel(Native.Point point)
    {
        nint dc = Native.GetDC(0);
        try
        {
            return Native.GetPixel(dc, point.X, point.Y);
        }
        finally { Native.ReleaseDC(0, dc); }
    }
    private static Wpf.DataObject? SnapshotClipboard()
    {
        var original = Wpf.Clipboard.GetDataObject();
        if (original is null) return null;
        string[] formats = original.GetFormats(false);
        if (formats.Length == 0) return null;
        if (formats.Length > 128) throw new InvalidOperationException("Original clipboard has too many formats to snapshot safely; clipboard has not been overwritten.");
        var snapshot = new Wpf.DataObject();
        long snapshotBytes = 0;
        void Reserve(long bytes)
        {
            if (bytes < 0 || bytes > 256L * 1024 * 1024 - snapshotBytes) throw new InvalidOperationException("Clipboard snapshot exceeds its 256 MiB budget.");
            snapshotBytes += bytes;
        }
        foreach (string format in formats)
        {
            try
            {
                object? value = original.GetData(format, false);
                if (value is Stream stream)
                {
                    if (!stream.CanRead || !stream.CanSeek) throw new InvalidOperationException("Clipboard stream cannot be read and restored safely.");
                    long originalPosition = stream.Position;
                    Reserve(stream.Length);
                    byte[] copied = new byte[checked((int)stream.Length)];
                    try { stream.Position = 0; stream.ReadExactly(copied); }
                    finally { stream.Position = originalPosition; }
                    value = new MemoryStream(copied, false);
                }
                else if (value is BitmapSource bitmap) { Reserve((long)bitmap.PixelWidth * bitmap.PixelHeight * 4); var clone = bitmap.Clone(); clone.Freeze(); value = clone; }
                else if (value is byte[] bytes) { Reserve(bytes.Length); value = bytes.Clone(); }
                else if (value is string[] names) { Reserve(names.Sum(name => (long)name.Length * 2)); value = names.Clone(); }
                else if (value is System.Drawing.Bitmap drawing) { Reserve((long)drawing.Width * drawing.Height * 4); value = drawing.Clone(); }
                else if (value is string text) Reserve((long)text.Length * 2);
                else throw new InvalidOperationException("Clipboard format cannot be snapshotted as an owned value.");
                snapshot.SetData(format, value, false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Original clipboard format '{format}' could not be snapshotted; clipboard has not been overwritten.", ex);
            }
        }
        return snapshot;
    }
    private static void PauseAutomaticRecovery(object session)
    {
        if (Field<DispatcherTimer?>(session, "_documentRecoveryTimer") is { } timer) { timer.Stop(); timer.Interval = TimeSpan.FromHours(1); }
    }
    private static void VerifyErrorModal(object session, EditorSession live, OverlayHost host, Wpf.Window toolbar)
    {
        static string Hash(SceneDocument doc) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(doc)));
        string before = Hash(live.Document);
        Guid[] selection = live.Selection.Order().ToArray();
        int undo = live.UndoCount;
        ErrorModal = new();
        ErrorModalFixture.Observe(() => Call(session, "ShowError", ErrorModalFixture.Message), toolbar, host, ErrorModal, failure =>
        {
            Failure = failure; WriteReport(false); Environment.Exit(1);
        });
        Check(ErrorModal.DialogHandle != 0 && ErrorModal.Visible && ErrorModal.Enabled && ErrorModal.OwnerDisabled,
            "Production custom error dialog is a visible enabled self-owned modal with its toolbar disabled");
        // Foreground activation can be denied by Windows when this executable was launched by a
        // background shell. Record the actual HWND, without forcing foreground or claiming keyboard proof.
        Check(ErrorModal.Topmost && !ErrorModal.VisibleOverlayAboveDialog && ErrorModal.OkRootAtPhysicalCenter,
            "Owned error dialog and its OK button are physically above every visible production overlay window");
        Check(ErrorModal.OkIsOwnedEnabledVisible && ErrorModal.CloseCommandInvoked && ErrorModal.Returned && ErrorModal.DialogClosed && ErrorModal.OwnerEnabledAfter,
            "The actual self-owned confirmation button closes the modal and restores its owner");
        Check(Hash(live.Document) == before && live.Selection.Order().SequenceEqual(selection) && live.UndoCount == undo,
            "Showing and dismissing a production error preserves exact document, selection and history");
        Check(host.IsEditing && toolbar.IsVisible && toolbar.IsEnabled, "Editor remains active after the error modal returns");
        live.TransformSelection(Affine2D.Translation(3, 2), "错误提示后的编辑验收");
        Check(Hash(live.Document) != before && live.UndoCount == undo + 1, "A normal production transaction succeeds immediately after the error modal");
        Check(live.Undo() && Hash(live.Document) == before && live.Selection.Order().SequenceEqual(selection),
            "Undo after the error modal restores the exact preceding document and selection");
    }
    private static async Task RecoveryDeadline(object session, EditorSession live)
    {
        var timer = Field<DispatcherTimer>(session, "_documentRecoveryTimer");
        timer.Stop(); timer.Interval = TimeSpan.FromSeconds(1);
        var store = new RecoveryStore(Path.Combine(Profile!, "recovery"));
        int originalNextNumber = live.Document.NextNumber;
        for (int i = 0; i < 16; i++)
        {
            int committed = 100 + i;
            live.Execute("连续提交验收", d => d.NextNumber = committed);
            await Task.Delay(90);
        }
        Check(File.Exists(store.CurrentPath), "Continuous subsecond commits cannot postpone recovery until input becomes quiet");
        var duringEdits = await store.LoadAsync();
        Check(duringEdits is not null && duringEdits.State.LiveDocument.NextNumber is >= 100 and <= 115,
            "Recovery saves an actual committed version while short edits continue");
        timer.Stop();
        live.Execute("恢复样本序号", d => d.NextNumber = originalNextNumber);
        live.BeginTransaction("尚未结束的长手势");
        var pending = new CanvasElement { Kind = ElementKind.Rectangle, Width = 10, Height = 10 };
        live.Document.Elements.Add(pending); live.NotifyChanged();
        try
        {
            await Task.Delay(1400);
            var whileGesture = await store.LoadAsync();
            Check(whileGesture is not null && whileGesture.State.LiveDocument.NextNumber == originalNextNumber,
                "An ongoing long gesture does not block the preceding committed recovery deadline");
            Check(whileGesture!.State.LiveDocument.Elements.All(e => e.Id != pending.Id), "Recovery never writes uncommitted gesture geometry");
        }
        finally { live.Cancel(); PauseAutomaticRecovery(session); }
    }
    private static object Construct(Wpf.Application app) => Activator.CreateInstance(DesktopType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [app], null)!;
    private static EditorSession Active(object session) => Property<EditorSession>(session, "Active");
    private static T Field<T>(object instance, string name) => (T)DesktopType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static T Property<T>(object instance, string name) => (T)DesktopType.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetProperty(object instance, string name, object value) => DesktopType.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object?[] args)
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
        try { return method.Invoke(instance, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static async Task CallAsync(object instance, string name, params object?[] args) => await ((Task)Call(instance, name, args)!);
    private static void ConfigureProfile(string directory) => Type.GetType("FloatCanvas.App.ApplicationPaths, FloatCanvas", true)!.GetMethod("Configure", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [new[] { "--profile", directory }]);
    private static void Check(bool condition, string description)
    {
        lock (Results) Results.Add(new(description, condition));
        if (!condition) throw new InvalidOperationException(description);
    }
    private static void WriteReport(bool passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        CheckResult[] checks; lock (Results) checks = Results.ToArray();
        object[] sessionDiagnostics; lock (SessionDiagnostics) sessionDiagnostics = SessionDiagnostics.ToArray();
        File.WriteAllText(ReportPath, JsonSerializer.Serialize(new
        {
            Passed = passed, Timestamp = DateTimeOffset.Now, Profile, Checks = checks, Failure,
            ExternalClipboardProbe, ExternalClipboardRestored, ExternalClipboardCases, PopupPixelDiagnostics,
            ExternalClipboardScope = "Opt-in PNG/Bitmap/FileDrop data objects without FloatCanvas.Objects.v1, real Clipboard and production PasteAsync, then direct managed controller move and undo. This does not exercise a physical Ctrl+V keystroke, file dialog or OS drag-and-drop.",
            SessionDiagnostics = sessionDiagnostics,
            SessionDiagnosticScope = "Observations at existing fixture boundaries and production release events; no new activation, input injection, retry or waiting beyond the original 80 ms entry wait. CurrentThreadFocusHandle is GetFocus on the WPF/overlay UI thread, not another process's focus. Status and handles belong to this own session.",
            PhysicalInputInjection = false, SeededObjectsAreCoreFixtures = true, IdlePresentedFrames = IdleFrames,
            ScreenshotRetention = "Only a crop inside our own native test-window client; full desktop captures remain in memory and are removed before any project/recovery write.",
            RetainedScreenshotBounds, Runtime = Environment.Version.ToString(), Os = Environment.OSVersion.ToString(), ErrorModal,
            ErrorModalForegroundScope = "Actual foreground HWND is observed without forcing activation; IsForeground=false limits this run to visibility, unobstructed physical confirmation-button targeting and self-owned dismissal, not keyboard foreground proof."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed record CheckResult(string Description, bool Passed);
}
