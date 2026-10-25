using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Production scheduling in hidden owned windows and an independent profile. No input injection.</summary>
internal static class PreviewAnimationScheduleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string LoopGif = "R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs=";

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run animation scheduling in its own process.");
        ApplicationPaths.Configure(["--profile", System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts",
            "FloatCanvas-ui-render-fix-20261025", "animation-profile", Guid.NewGuid().ToString("N"))]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var bounds = new PixelBounds(0, 0, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> provider = () => [new("hidden-animation", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
        using var session = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        var editor = Field<EditorController>(session, "_editor");
        var preview = Field<CanvasPreviewPump>(session, "_canvasPreview");
        var uiRenderer = Field<SceneRenderer>(session, "_renderer");

        using var bitmap = new SKBitmap(2048, 2048); bitmap.Erase(SKColors.Red);
        using var still = bitmap.Encode(SKEncodedImageFormat.Webp, 90);
        string stillId = editor.Session.Assets.Add(still.ToArray(), "image/webp");
        editor.Session.Execute("static WebP scheduling", document => document.Elements = [new()
        {
            Kind = ElementKind.Image, AssetId = stillId, Width = 80, Height = 40,
            Transform = Affine2D.Translation(20, 30)
        }]);
        Invoke(session, "PrepareAnimationFrame");
        Invoke(session, "ScheduleAnimationFrame");
        check("cold production UI scheduling does not open or decode a static WebP",
            uiRenderer.ImageDecodeCount == 0 && uiRenderer.AnimationMetadataReadCount == 0 && uiRenderer.CachedImageBytes == 0);
        Invoke(session, "RequestRender");
        Until(() => !preview.Busy && Published()?.HasAnimations == false);
        check("static worker schedule returns to UI without creating a UI image cache",
            uiRenderer.ImageDecodeCount == 0 && uiRenderer.AnimationMetadataReadCount == 0 &&
            Published() is { HasAnimations: false, NextDelay: null });
        editor.Session.SelectAll();
        var staticStamp = PrepareStamp();
        Until(() => staticStamp.IsCompleted);
        check("production stamp preparation checks static WebP metadata away from its UI renderer",
            staticStamp.GetAwaiter().GetResult() is null && uiRenderer.AnimationMetadataReadCount == 0 && uiRenderer.ImageDecodeCount == 0);

        string gifId = editor.Session.Assets.Add(Convert.FromBase64String(LoopGif), "image/gif");
        editor.Session.Execute("animated worker scheduling", document => document.Elements[0].AssetId = gifId);
        Invoke(session, "RequestRender");
        Until(() => Published() is { HasAnimations: true, NextDelay: not null });
        check("worker publication starts production playback while UI metadata and pixel decoders remain unused",
            Field<Stopwatch>(session, "_animationClock").IsRunning && uiRenderer.ImageDecodeCount == 0 &&
            uiRenderer.AnimationMetadataReadCount == 0 && uiRenderer.AnimationFrameDecodeCount == 0);
        var animatedStamp = PrepareStamp();
        Until(() => animatedStamp.IsCompleted);
        check("production animated stamp preparation retains detached resources without using the UI decoder",
            animatedStamp.GetAwaiter().GetResult() is { } stamp && stamp.Assets.TryGet(gifId, out _) &&
            uiRenderer.AnimationMetadataReadCount == 0 && uiRenderer.ImageDecodeCount == 0);
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, false);
        Invoke(session, "PrepareAnimationFrame");
        check("hiding the production canvas pauses playback without touching image resources",
            !Field<Stopwatch>(session, "_animationClock").IsRunning && uiRenderer.ImageDecodeCount == 0 &&
            Field<DispatcherTimer?>(session, "_animationTimer")?.IsEnabled != true);

        SceneAnimationSchedule? Published() => preview.Frame?.GetType().GetProperty("Animation")?.GetValue(preview.Frame) as SceneAnimationSchedule;
        Task<StampRenderContext?> PrepareStamp()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            try { return (Task<StampRenderContext?>)typeof(DesktopSession).GetMethod("PrepareStampRenderingAsync", Private)!.Invoke(session, [editor.Session])!; }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        void Until(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.ElapsedMilliseconds > 15000) throw new TimeoutException("Worker animation schedule was not published.");
                var frame = new DispatcherFrame();
                app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, Private)!.Invoke(target, null);
}
