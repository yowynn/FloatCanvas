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

/// <summary>Production input/render/presentation in hidden 4K HWNDs. No input injection or user profile.</summary>
internal static class BrushPreviewTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);

    internal static void Run(Action<string, bool> check)
    {
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts",
            "FloatCanvas-brush-preview-tests", Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile]);
        new UserSettings { Language = "en", PreferGpuAcceleration = false }.Save();
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint foreground = GetForegroundWindow();
        var bounds = new PixelBounds(0, 0, 3840, 2160);
        Func<IReadOnlyList<MonitorInfo>> provider = () => [new("hidden-brush-fixture", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
        using var session = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
        // Enable rendering/guides without ever showing or acquiring input on the native windows.
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        var editor = Field<EditorController>(session, "_editor");
        editor.Tool = EditorTool.Freehand;
        editor.Style.Stroke = 0xFF2383E2;
        editor.Style.StrokeWidth = 4;
        var preview = Field<CanvasPreviewPump>(session, "_canvasPreview");
        Exception? failure = null;
        Invoke(session, "RequestRender"); Settle();

        // Block only raster computation. Exercise the real RenderFrame, Draw and FrameReady handlers.
        preview.Dispose();
        using var began = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim(true);
        using var renderer = new CanvasPreviewRenderer();
        bool block = false;
        var capture = (Func<CanvasPreviewSnapshot>)typeof(DesktopSession).GetMethod("CaptureCanvasPreview", Private)!
            .CreateDelegate(typeof(Func<CanvasPreviewSnapshot>), session);
        preview = new(app.Dispatcher, capture, snapshot =>
        {
            if (block) { began.Set(); if (!release.Wait(10000)) throw new TimeoutException("Blocked preview was not released."); }
            return renderer.Render(snapshot);
        });
        typeof(DesktopSession).GetField("_canvasPreview", Private)!.SetValue(session, preview);
        preview.FrameReady += (Action)typeof(DesktopSession).GetMethod("PresentCanvasPreview", Private)!.CreateDelegate(typeof(Action), session);
        preview.Failed += error => failure = error;
        try
        {
            Invoke(session, "RequestRender"); Settle();
            block = true; release.Reset(); began.Reset();
            long presented = host.PresentedFrameCount;
            Send(OverlayPointerKind.Down, 200, 1200);
            Until(() => began.IsSet);
            for (int n = 1; n <= 60; n++)
            {
                int step = n;
                app.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Send(OverlayPointerKind.Move, 200 + step * 3, 1200 + step * .1));
            }
            Until(() => !Pending() && editor.Session.Document.Elements.Single().Points.Count == 61);
            long stalePresents = host.PresentedFrameCount - presented;
            Console.WriteLine($"BRUSH_BLOCKED_UI stalePresents={stalePresents} points={editor.Session.Document.Elements.Single().Points.Count}");
            check("ordinary brush input does not upload unchanged old pixels while its worker is blocked", stalePresents == 0);
            release.Set(); block = false; Settle();
            using (var pixels = SKBitmap.FromImage(preview.Frame!.Pixels.Tiles[0].Image))
                check("coalesced brush preview contains the final queued pointer position", pixels.GetPixel(377, 1206).Blue > 150);
            Send(OverlayPointerKind.Up, 380, 1206); Settle();

            var latency = new List<double>();
            for (int stroke = 0; stroke < 4; stroke++)
            {
                var clock = new Stopwatch(); double first = -1;
                void Ready() { if (first < 0) first = clock.Elapsed.TotalMilliseconds; }
                preview.FrameReady += Ready;
                double y = 1300 + stroke * 40;
                app.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { clock.Start(); Send(OverlayPointerKind.Down, 200, y); });
                for (int n = 1; n <= 60; n++)
                {
                    int step = n;
                    app.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Send(OverlayPointerKind.Move, 200 + step * 3, y + step * .1));
                }
                Until(() => first >= 0 && !Pending() && !preview.Busy);
                preview.FrameReady -= Ready;
                latency.Add(first);
                using (var pixels = SKBitmap.FromImage(preview.Frame!.Pixels.Tiles[0].Image))
                    check("ordinary brush keeps the final pixels under queued input " + stroke, pixels.GetPixel(377, (int)y + 6).Blue > 150);
                Send(OverlayPointerKind.Up, 380, y + 6); Settle();
            }
            Console.WriteLine("BRUSH_FIRST_FRAME_MS " + JsonSerializer.Serialize(latency));
            check("every ordinary 4K stroke presents promptly under queued pointer input", latency.All(ms => ms < 100));

            // The optimization must not make UI selection guides wait behind expensive raster work.
            editor.Session.Execute("selection guide", doc => doc.Elements = [new()
            {
                Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(100, 100),
                Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 }
            }]);
            editor.Tool = EditorTool.Select; editor.Session.SelectAll(); Settle();
            block = true; began.Reset(); release.Reset(); preview.Request();
            check("selection fixture blocks only its raster worker", began.Wait(2000));
            bool guideMoved = false; SKColor observedGuide = default;
            void Observe(SKSurface surface)
            {
                using var pixels = surface.PeekPixels(); var color = pixels.GetPixelColor(160, 130); observedGuide = color;
                // The antialiased blue edge partly covers the old red rectangle.
                guideMoved |= color.Blue > 140 && color.Blue > color.Red + 60;
            }
            host.Draw += Observe;
            presented = host.PresentedFrameCount;
            Send(OverlayPointerKind.Down, 150, 130); Send(OverlayPointerKind.Move, 190, 160);
            Until(() => !Pending());
            host.Draw -= Observe;
            Console.WriteLine($"BRUSH_GUIDES editing={host.IsEditing} busy={preview.Busy} presents={host.PresentedFrameCount - presented} color={observedGuide} corners={JsonSerializer.Serialize(editor.SelectionScreenCorners)} selection={editor.Session.Selection.Count}");
            check("selection guides move on UI while the preview worker remains blocked", preview.Busy && guideMoved && host.PresentedFrameCount > presented);
            Send(OverlayPointerKind.Up, 190, 160);
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false);
            presented = host.PresentedFrameCount;
            Invoke(session, "RequestRender"); Until(() => !Pending());
            check("leaving editing clears the previously visible guides without waiting for raster work", preview.Busy && host.PresentedFrameCount == presented + 1);
            presented = host.PresentedFrameCount;
            Invoke(session, "RequestRender"); Until(() => !Pending());
            check("cleared guides do not cause repeated uploads of the same old frame", host.PresentedFrameCount == presented);
            release.Set(); block = false; Settle();
            check("hidden brush fixture keeps foreground and every native input/display window hidden", GetForegroundWindow() == foreground &&
                host.Diagnostics.All(window => !IsWindowVisible(window.InputHandle) && !IsWindowVisible(window.DisplayHandle)));
        }
        finally
        {
            release.Set(); block = false;
            try { Until(() => !preview.Busy, timeout: 15000); }
            finally { preview.Dispose(); }
        }

        void Settle() => Until(() => !Pending() && !preview.Busy, timeout: 15000);
        void Until(Func<bool> ready, int timeout = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Brush preview did not settle.");
                var frame = new DispatcherFrame();
                app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
        }
        bool Pending()
        {
            if (failure is not null) throw new InvalidOperationException("Preview failed.", failure);
            return Field<DispatcherOperation?>(session, "_renderOperation")?.Status == DispatcherOperationStatus.Pending;
        }
        void Send(OverlayPointerKind kind, double x, double y) => editor.Pointer(new(kind, new(x, y), OverlayMouseButton.Left, OverlayModifiers.None));
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, Private)!.Invoke(target, null);
}
