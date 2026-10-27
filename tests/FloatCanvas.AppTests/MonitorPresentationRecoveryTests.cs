using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class MonitorPresentationRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run monitor presentation recovery in a fresh process.");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-MonitorPresentation", Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", folder]);
        new UserSettings { PreferGpuAcceleration = false, ShowFloatingLogo = false, ShowPassThroughLauncher = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint foreground = GetForegroundWindow();
        var primary = new MonitorInfo("presentation-primary", new(-12000, -12000, 160, 100), new(-12000, -12000, 160, 100), 96, 96, true);
        var second = new MonitorInfo("presentation-secondary", new(-11840, -12000, 160, 100), new(-11840, -12000, 160, 100), 144, 144, false);
        IReadOnlyList<MonitorInfo> layout = [primary, second];
        Func<IReadOnlyList<MonitorInfo>> provider = () => layout;
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
        using var session = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
        var editor = Field<EditorController>(session, "_editor");
        var preview = Field<CanvasPreviewPump>(session, "_canvasPreview");
        Exception? failure = null;
        preview.Failed += error => failure = error;
        var pixels = new Dictionary<string, SKColor>();
        void Observe(SKSurface surface)
        {
            int left = (int)Math.Round(-surface.Canvas.TotalMatrix.TransX);
            int top = (int)Math.Round(-surface.Canvas.TotalMatrix.TransY);
            var output = host.Monitors.Single(m => m.Bounds.Left == left && m.Bounds.Top == top);
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            pixels[output.DeviceName] = bitmap.GetPixel(50, 40);
        }
        host.Draw += Observe;
        try
        {
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
            editor.Session.Execute("monitor recovery fixture", doc => doc.Elements =
            [new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 60,
                Transform = Affine2D.Translation(primary.Bounds.Left + 10, primary.Bounds.Top + 10),
                Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } }]);
            Settle();
            check("monitor recovery begins with visible canvas pixels and an unchanged foreground", Red(primary) && GetForegroundWindow() == foreground);
            string elementsBefore = JsonSerializer.Serialize(editor.Session.Document.Elements);
            int undoBefore = editor.Session.UndoCount;

            var previous = host.Diagnostics.Single().DisplayHandle;
            host.SetMonitorScope(OverlayMonitorScope.AllScreens);
            Settle();
            check("scope expansion replaces HWNDs and preserves canvas content without another edit", host.Diagnostics.Count == 2 &&
                host.Diagnostics[0].DisplayHandle != previous && host.IsVisible && Red(primary));
            var currentContext = (CanvasPreviewContext)typeof(DesktopSession).GetMethod("CurrentPreviewContext", Private)!.Invoke(session, null)!;
            var presentedContext = (CanvasPreviewContext?)typeof(DesktopSession).GetField("_presentedCanvasContext", Private)!.GetValue(session);
            check("replacement output pixels belong to the current adopted preview context", preview.Frame?.Context == currentContext && presentedContext == currentContext);
            check("newly added empty output is still transparent", pixels.TryGetValue(second.DeviceName, out var secondaryPixel) && secondaryPixel.Alpha == 0);

            previous = host.Diagnostics[0].DisplayHandle;
            layout = [primary];
            SendMessageW(host.ControlHandle, 0x8001, 0, 0);
            Settle();
            check("display disconnection republishes existing pixels to the replacement HWND", host.Diagnostics.Count == 1 &&
                host.Diagnostics[0].DisplayHandle != previous && host.IsVisible && Red(primary));

            previous = host.Diagnostics[0].DisplayHandle;
            typeof(OverlayHost).GetMethod("RebuildMonitors", Private)!.Invoke(host, null);
            Settle();
            check("same-layout surface recreation republishes existing pixels without content damage", host.Diagnostics[0].DisplayHandle != previous &&
                host.IsVisible && Red(primary));
            host.SetVisible(false);
            SendMessageW(host.ControlHandle, 0x8001, 0, 0);
            Settle();
            check("monitor changes preserve a deliberately hidden canvas", !host.IsVisible && host.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible));
            host.SetVisible(true); Settle();
            check("showing the rebuilt hidden canvas restores existing content without a drawing edit", host.IsVisible && Red(primary));
            check("native output recovery leaves canvas objects and undo history unchanged", JsonSerializer.Serialize(editor.Session.Document.Elements) == elementsBefore &&
                editor.Session.UndoCount == undoBefore && !editor.Session.IsTransactionActive);
            check("monitor recovery never enters editing or changes the user's foreground", !host.IsEditing && GetForegroundWindow() == foreground);
        }
        finally { host.Draw -= Observe; }

        bool Red(MonitorInfo monitor) => pixels.TryGetValue(monitor.DeviceName, out var color) && color.Red > 245 && color.Green < 10 && color.Blue < 10 && color.Alpha == 255;
        void Settle()
        {
            var clock = Stopwatch.StartNew();
            while (preview.Busy || Pending("_renderOperation") || Pending("_presentOperation"))
            {
                if (failure is not null) throw new InvalidOperationException("Monitor preview failed.", failure);
                if (clock.ElapsedMilliseconds > 10000) throw new TimeoutException("Monitor preview did not settle.");
                var frame = new DispatcherFrame();
                app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
        }
        bool Pending(string name) => typeof(DesktopSession).GetField(name, Private)!.GetValue(session) is DispatcherOperation { Status: DispatcherOperationStatus.Pending };
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
