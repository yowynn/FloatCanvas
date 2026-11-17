using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CanvasHistoryPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false,
            DocumentEpoch: new object(), ContinuityEpoch: new object());
        check("retaining history pixels does not accept an obsolete scene", context.CanRetainPixelsFor(context with { DocumentEpoch = new object() }) &&
            !context.HasSameScene(context with { DocumentEpoch = new object() }));
        var windows = context with { WindowEpoch = new object(), WindowContinuityEpoch = new object() };
        check("window render-plan changes retain submitted pixels within the same owner", windows.CanRetainPixelsFor(windows with { WindowEpoch = new object() }));
        check("window owner retirement cannot retain old source pixels", !windows.CanRetainPixelsFor(windows with { WindowContinuityEpoch = new object() }));
        var presentation = context with { PresentationEpoch = new object(), PresentationContinuityEpoch = new object() };
        check("capture pause can retain submitted pixels", presentation.CanRetainPixelsFor(presentation with { PresentationEpoch = null }));
        check("privacy change while capture is paused rejects submitted pixels", !presentation.CanRetainPixelsFor(presentation with { PresentationEpoch = null, PresentationContinuityEpoch = new object() }));
        foreach (var blocked in new[] { context with { ContinuityEpoch = new object() }, context with { Session = new object() },
            context with { Monitors = new object() }, context with { Hidden = true }, context with { VisibilityEpoch = new object() },
            context with { PresentationEpoch = new object() }, context with { WindowEpoch = new object() },
            context with { ScreenDraftEpoch = new object() } })
            check("new lifecycle or privacy context never retains old pixels", !context.CanRetainPixelsFor(blocked));
        ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-history-presentation", Guid.NewGuid().ToString("N"))]);
        new UserSettings { PreferGpuAcceleration = false, ShowFloatingLogo = false, ShowPassThroughLauncher = false }.Save();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var monitor = new MonitorInfo("history", new(-20000, -20000, 160, 100), new(-20000, -20000, 160, 100), 96, 96, true);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [monitor];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.AllScreens, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var editor = Field<EditorController>(desktop, "_editor");
        var pump = Field<CanvasPreviewPump>(desktop, "_canvasPreview");
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        editor.Tool = EditorTool.Line;
        editor.Session.Execute("fixture", doc => doc.Elements = [new() { Kind = ElementKind.Rectangle, Width = 30, Height = 30,
            Transform = Affine2D.Translation(-19990, -19990), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } }]);
        Settle();
        int alpha = -1, red = -1, green = -1, submissions = 0;
        host.Draw += surface => { using var pixels = surface.PeekPixels(); var color = pixels.GetPixelColor(15, 15);
            alpha = color.Alpha; red = color.Red; green = color.Green; submissions++; };
        host.Render(); check("history fixture starts with opaque red artwork", alpha == 255 && red == 255);
        foreach (var tool in new[] { EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow })
        {
            editor.Tool = tool; Send(OverlayPointerKind.Down); Settle(); host.Render();
            int before = submissions; Send(OverlayPointerKind.Up); host.Render();
            check(tool + " click retains the preceding native pixels until rollback renders", alpha == 255 && submissions == before);
            Settle(); host.Render(); check(tool + " rollback publishes complete artwork without a new layer", alpha == 255 && editor.Session.Document.Elements.Count == 1);
        }
        editor.Session.Execute("green", doc => doc.Elements[0].Style.Fill = 0xFF00FF00); Settle(); host.Render();
        check("changed artwork is green", green == 255 && alpha == 255);
        int count = submissions; editor.Session.Undo(); host.Render();
        check("undo keeps the last green frame while red is pending", submissions == count && green == 255 && alpha == 255);
        Settle(); host.Render(); check("undo publishes the red replacement", red == 255 && green == 0 && alpha == 255);
        count = submissions; editor.Session.Redo(); host.Render();
        check("redo keeps the red frame while green is pending", submissions == count && red == 255 && alpha == 255);
        Settle(); host.Render(); check("redo publishes the green replacement", green == 255 && red == 0 && alpha == 255);
        editor.Session.BeginTransaction("draft"); editor.Session.Document.Elements[0].Style.Fill = 0xFFFF0000;
        editor.Session.NotifyChanged(); Settle(); host.Render();
        count = submissions; editor.Session.Cancel(); host.Render();
        check("cancel retains the displayed draft until restoration is ready", submissions == count && red == 255 && alpha == 255);
        Settle(); host.Render(); check("cancel publishes restored green artwork", green == 255 && alpha == 255);
        count = submissions; editor.Session.Undo(); editor.Session.Redo(); editor.Session.Undo(); host.Render();
        check("coalesced history changes retain pixels without an intermediate clear", submissions == count && alpha == 255);
        Settle(); host.Render(); check("coalesced history publishes only the final red state", red == 255 && green == 0 && alpha == 255);
        count = submissions; editor.Session.ReplaceDocument(new()); host.Render();
        check("a different document does not retain the old artwork", submissions > count && alpha == 0);
        Settle();

        void Send(OverlayPointerKind kind) => editor.Pointer(new(kind, new(-19900, -19930), OverlayMouseButton.Left, OverlayModifiers.None));
        void Settle()
        {
            var timer = Stopwatch.StartNew();
            do
            {
                if (timer.ElapsedMilliseconds > 10000) throw new TimeoutException("History presentation did not settle.");
                var frame = new DispatcherFrame(); app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame); Thread.Sleep(1);
            } while (pump.Busy || Pending("_renderOperation") || Pending("_presentOperation"));
        }
        bool Pending(string name) => typeof(DesktopSession).GetField(name, Private)!.GetValue(desktop) is DispatcherOperation
            { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing };
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
}
