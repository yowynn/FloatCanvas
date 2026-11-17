using System.IO;
using System.Reflection;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class ForegroundPresentationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-foreground-presentation", Guid.NewGuid().ToString("N"))]);
        new UserSettings { PreferGpuAcceleration = false, ShowFloatingLogo = false, ShowPassThroughLauncher = false }.Save();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var bounds = new PixelBounds(-20000, -20000, 160, 100);
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
            [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => [new("foreground", bounds, bounds, 96, 96, true)]), false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var editor = Field<EditorController>(desktop, "_editor");
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        editor.Session.Execute("artwork", doc => doc.Elements = [new() { Kind = ElementKind.Rectangle, Width = 30, Height = 30,
            Transform = Affine2D.Translation(-19990, -19990), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } }]);
        // Keep the source synthetic: this fixture does not acquire global input or query other apps.
        using var runtime = new PresentationRuntimeBroker(app.Dispatcher, () => { },
            (epoch, _, _, _) => Task.FromResult<IPresentationInputSource>(new PresentationRuntimeTests.FakeSource(epoch, [])));
        Field<PresentationRuntimeBroker>(desktop, "_presentationRuntime")?.Dispose();
        typeof(DesktopSession).GetField("_presentationRuntime", Hidden)!.SetValue(desktop, runtime);
        var demand = new PresentationRuntimeDemand(true, false, false, false, false, 900, 450, 800);
        var inputIdentity = new object();
        runtime.Configure(demand, true, inputIdentity, "paused");
        runtime.SetArmed(true);
        nint foreground = 101;
        using var gate = new PresentationKeyboardPrivacyGate(app.Dispatcher, runtime,
            completed => new(completed, () => false, () => foreground), () => foreground);
        gate.Observe();
        var pump = Field<CanvasPreviewPump>(desktop, "_canvasPreview");
        using var renderer = new CanvasPreviewRenderer();
        PublishCurrent();
        int alpha = -1, red = -1;
        host.Draw += surface => { using var pixels = surface.PeekPixels(); var color = pixels.GetPixelColor(15, 15); alpha = color.Alpha; red = color.Red; };
        host.Render();
        check($"foreground fixture submits opaque artwork in pass-through mode (editing={host.IsEditing}, keyboard={runtime.ObservingKeyboard}, alpha={alpha}, red={red})", !host.IsEditing && runtime.ObservingKeyboard && alpha == 255 && red == 255);
        foreach (nint next in new nint[] { 202, 101, 202, 101 })
        {
            foreground = next; gate.Observe();
            // Render before the replacement worker frame is available, as pointer damage does in production.
            host.Render();
            check("ordinary foreground changes never submit a transparent artwork frame", alpha == 255 && red == 255);
            PublishCurrent(); host.Render();
        }
        foreach (var (name, refresh) in new (string, Action)[]
        {
            ("unchanged foreground metadata", gate.Observe),
            ("identical runtime configuration", () => runtime.Configure(demand, true, inputIdentity, "paused")),
            ("unchanged keyboard scope", () => runtime.ConfigureKeyboardScope(PresentationKeyboardScope.AllApplications)),
            ("unchanged password policy", () => runtime.SuppressKeyboard(false)),
            ("editing input route", () => runtime.ConfigureEditingInput(true)),
            ("pass-through input route", () => runtime.ConfigureEditingInput(false))
        })
        {
            refresh(); host.Render();
            check(name + " preserves already submitted artwork", alpha == 255 && red == 255);
        }
        runtime.SuppressKeyboard(true); host.Render();
        check("confirmed password protection still rejects the old presentation frame immediately", alpha == 0);

        void PublishCurrent()
        {
            using var snapshot = (CanvasPreviewSnapshot)typeof(DesktopSession).GetMethod("CaptureCanvasPreview", Hidden)!.Invoke(desktop, null)!;
            var frame = renderer.Render(snapshot);
            pump.Frame?.Dispose();
            typeof(CanvasPreviewPump).GetProperty("Frame", Hidden)!.SetValue(pump, frame);
        }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
}
