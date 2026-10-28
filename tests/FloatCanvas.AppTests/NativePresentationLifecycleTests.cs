using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class NativePresentationLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run native publication lifecycle in a fresh process.");
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-publication-lifecycle", Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile]);
        new UserSettings { PreferGpuAcceleration = false, ShowFloatingLogo = false, ShowPassThroughLauncher = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var first = new MonitorInfo("publish-first", new(-20000, -20000, 160, 100), new(-20000, -20000, 160, 100), 96, 96, true);
        var second = new MonitorInfo("publish-second", new(-19840, -20000, 160, 100), new(-19840, -20000, 160, 100), 96, 96, false);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [first, second];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.AllScreens, monitors, false], null)!;
        using var session = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
        var editor = Field<EditorController>(session, "_editor");
        var pump = Field<CanvasPreviewPump>(session, "_canvasPreview");
        var ready = (Action)typeof(DesktopSession).GetMethod("PresentCanvasPreview", Private)!.CreateDelegate(typeof(Action), session);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        editor.Tool = EditorTool.Select;
        editor.Session.Execute("initial publication", doc => doc.Elements =
            [new() { Kind = ElementKind.Rectangle, Width = 20, Height = 20,
                Transform = Affine2D.Translation(first.Bounds.Left + 10, first.Bounds.Top + 10),
                Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } }]);
        Settle();
        pump.FrameReady -= ready; // Compute new frames without the usual queued presentation trigger.
        var colors = new Dictionary<int, SKColor>();
        void Observe(SKSurface surface)
        {
            int left = (int)Math.Round(-surface.Canvas.TotalMatrix.TransX);
            using var pixels = surface.PeekPixels(); colors[left] = pixels.GetPixelColor(85, 15);
        }
        host.Draw += Observe;
        try
        {
            long previous = pump.Frame!.Sequence;
            editor.Session.Execute("compute B", doc =>
            {
                doc.Elements[0].Transform = Affine2D.Translation(first.Bounds.Left + 80, first.Bounds.Top + 10);
                doc.Elements[0].Style.Fill = 0xFF00FF00;
            });
            Until(() => pump.Frame!.Sequence > previous && !pump.Busy); Settle();
            if (Environment.GetCommandLineArgs().Contains("--disable-publication-adoption"))
                host.FrameSubmitted -= (Action<OverlayFrameTiming>)typeof(DesktopSession).GetMethod("CompleteCanvasSubmission", Private)!.CreateDelegate(typeof(Action<OverlayFrameTiming>), session);
            host.Render([second.Bounds]);
            check("direct native Render publishes the new pixels", colors[first.Bounds.Left].Green > 240);
            check("direct region redraw includes pending scene damage outside its caller's requested monitor", host.LastRenderedPixelCount > (long)second.Bounds.Width * second.Bounds.Height);
            check("direct native Render adopts the exact document used for its pixels", ShownX() == first.Bounds.Left + 80);
            check("direct native Render advances successful request sequence", Field<CanvasPreviewRequestState>(session, "_canvasPreviewRequests").PresentedSequence == pump.Frame.Sequence);
            var assetsBeforeControls = Field<object>(editor, "presentedAssets");
            var pixelsBeforeControls = Field<object>(session, "_presentedPixels");
            host.Render([second.Bounds]);
            check("controls-only native redraw reuses the existing picking assets and pixel baseline", ReferenceEquals(assetsBeforeControls, Field<object>(editor, "presentedAssets")) && ReferenceEquals(pixelsBeforeControls, Field<object>(session, "_presentedPixels")));

            previous = pump.Frame.Sequence;
            var screenshot = typeof(OverlayHost).GetField("_systemScreenshot", Private)!;
            screenshot.SetValue(host, Activator.CreateInstance(screenshot.FieldType)); // Empty own carrier list: no desktop capture or system UI.
            editor.Session.Execute("compute screenshot-delayed C", doc => doc.Elements[0].Transform = Affine2D.Translation(first.Bounds.Left + 110, first.Bounds.Top + 10));
            Until(() => pump.Frame!.Sequence > previous && !pump.Busy); Settle();
            Invoke(session, "QueueCanvasPresentation", new[] { first.Bounds, second.Bounds }); Settle();
            check("suppressed screenshot submission retains the preceding picking version", ShownX() == first.Bounds.Left + 80 && Field<List<PixelBounds>>(session, "_presentationDirty").Count > 0);
            Invoke(host, "EndSystemScreenshot", true);
            check("screenshot restore's direct native Render adopts delayed content without another edit", ShownX() == first.Bounds.Left + 110);
            check("successful restore drains damage held by the suppressed presentation", Field<List<PixelBounds>>(session, "_presentationDirty").Count == 0);
            previous = pump.Frame.Sequence;
            editor.Session.Execute("compute unpublished D", doc => doc.Elements[0].Transform = Affine2D.Translation(first.Bounds.Left + 80, first.Bounds.Top + 10));
            Until(() => pump.Frame!.Sequence > previous && !pump.Busy); Settle();
            check("partial-failure fixture keeps C displayed while D has finished computing", ShownX() == first.Bounds.Left + 110);

            int output = 0, failedBatches = 0, attempts = 0; bool fail = true;
            void Begin(OverlayFrameRequest _) { output = 0; attempts++; }
            void FailSecond(SKSurface _) { if (fail && ++output == 2) throw new IOException("private second-output failure"); }
            void Submitted(OverlayFrameTiming timing) { if (!timing.Succeeded && timing.Outputs.Count == 1) failedBatches++; }
            host.FrameRendering += Begin; host.Draw += FailSecond; host.FrameSubmitted += Submitted;
            try
            {
                editor.Session.BeginTransaction("external inspector edit");
                editor.Session.Document.Elements[0].Style.Stroke = 0xFF112233;
                Invoke(session, "QueueCanvasPresentation", new[] { first.Bounds, second.Bounds });
                Settle();
                check("partial native failure is retried once then stops without a dispatcher modal", attempts == 2 && failedBatches == 2);
                check("partial native failure retains full-output damage and marks presentation uncertain", Field<bool>(session, "_presentationUncertain") && Field<List<PixelBounds>>(session, "_presentationDirty").Count >= 2);
                check("partial batch really sends D to its first output while C picking is suspended", colors[first.Bounds.Left].Green > 240 && ShownX() == first.Bounds.Left + 110 && Field<bool>(editor, "presentedPickingSuspended"));
                check("publication failure preserves an unrelated newer inspector transaction", editor.Session.IsTransactionActive && editor.Session.Document.Elements[0].Style.Stroke == 0xFF112233);
                var selected = editor.Session.Selection.ToArray();
                editor.Pointer(new(OverlayPointerKind.Down, new(first.Bounds.Left + 115, first.Bounds.Top + 15), OverlayMouseButton.Left, OverlayModifiers.None));
                editor.Pointer(new(OverlayPointerKind.Up, new(first.Bounds.Left + 115, first.Bounds.Top + 15), OverlayMouseButton.Left, OverlayModifiers.None));
                check("uncertain presentation blocks picking instead of falling back to the live scene", editor.Session.Selection.SetEquals(selected) && editor.Session.IsTransactionActive);
                previous = pump.Frame!.Sequence;
                pump.Request(); Until(() => pump.Frame!.Sequence > previous && !pump.Busy);
                Invoke(session, "PresentCanvasPreview"); Settle();
                Invoke(session, "RequestPixelRender", (object)null!); Settle();
                check("late animation-style frames and automatic pixel invalidations do not restart a failed native budget", attempts == 2 && failedBatches == 2);
                fail = false;
                host.Render();
                check("explicit native refresh after failure clears suspension on coherent success", !Field<bool>(session, "_presentationUncertain") && !Field<bool>(editor, "presentedPickingSuspended") && ShownX() == first.Bounds.Left + 80);
                editor.Session.Cancel(); Settle(); host.Render();
                editor.Session.SetSelection([]); Settle();
                fail = true; attempts = failedBatches = 0;
                editor.Tool = EditorTool.Freehand;
                editor.Pointer(new(OverlayPointerKind.Down, new(first.Bounds.Left + 40, first.Bounds.Top + 60), OverlayMouseButton.Left, OverlayModifiers.None));
                check("immediate pen owns its transaction before a native publication failure", editor.Session.IsTransactionActive);
                Invoke(session, "QueueCanvasPresentation", new[] { first.Bounds, second.Bounds }); Settle();
                check("failed pen publication rolls back its own transaction without leaving a gesture", !editor.Session.IsTransactionActive && !editor.Session.Document.Elements.Any(e => e.Kind == ElementKind.Freehand));
                fail = false; Settle(); host.Render();
                host.SetVisible(false); Invoke(session, "UpdateHiddenCanvasState"); Settle();
                fail = true;
                editor.Pointer(new(OverlayPointerKind.Down, new(first.Bounds.Left + 40, first.Bounds.Top + 60), OverlayMouseButton.Left, OverlayModifiers.None));
                Settle();
                check("failed first draft on a hidden canvas rolls back and releases suspended picking after hiding its preview", !editor.Session.IsTransactionActive && !host.IsPreviewVisible && !Field<bool>(editor, "presentedPickingSuspended"));
                fail = false;
                editor.Pointer(new(OverlayPointerKind.Down, new(first.Bounds.Left + 50, first.Bounds.Top + 70), OverlayMouseButton.Left, OverlayModifiers.None));
                check("hidden canvas accepts the next pen down without waiting for an impossible hidden submission", editor.Session.IsTransactionActive);
                editor.Cancel(); Settle();
            }
            finally { host.FrameRendering -= Begin; host.Draw -= FailSecond; host.FrameSubmitted -= Submitted; }
            var sample = new EditorSession(new SceneDocument { Viewport = new(0, 0, 300, 300) });
            var direct = new EditorController(sample) { Tool = EditorTool.Freehand };
            bool reentered = false;
            sample.Changed += (_, _) =>
            {
                if (reentered || !sample.IsTransactionActive) return;
                reentered = true; direct.SuspendPresentedPicking();
            };
            direct.Pointer(new(OverlayPointerKind.Down, new(10, 10), OverlayMouseButton.Left, OverlayModifiers.None));
            check("reentrant failure during initial pen change recognizes and rolls back the just-started pointer transaction", reentered && !sample.IsTransactionActive && sample.Document.Elements.Count == 0);
            direct.ResumePresentedPicking();
            direct.Pointer(new(OverlayPointerKind.Down, new(40, 40), OverlayMouseButton.Left, OverlayModifiers.None));
            check("a subsequent pen down can begin after suspension is explicitly recovered", sample.IsTransactionActive);
            direct.Cancel(); direct.DetachSession();
        }
        finally { host.Draw -= Observe; pump.FrameReady += ready; }

        double ShownX() => Field<SceneDocument>(editor, "presentedScene").Elements[0].Transform.TransformPoint(default).X;
        void Settle() => Until(() => !pump.Busy && !Pending("_renderOperation") && !Pending("_presentOperation"));
        bool Pending(string name) => typeof(DesktopSession).GetField(name, Private)!.GetValue(session) is DispatcherOperation { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing };
        void Until(Func<bool> done)
        {
            var timeout = Stopwatch.StartNew();
            do
            {
                if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException("Native presentation fixture did not settle.");
                var frame = new DispatcherFrame(); app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false); Dispatcher.PushFrame(frame);
                Thread.Sleep(1);
            } while (!done());
        }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Invoke(object instance, string name, params object[] arguments)
    {
        try { instance.GetType().GetMethod(name, Private)!.Invoke(instance, arguments); }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); }
    }
}
