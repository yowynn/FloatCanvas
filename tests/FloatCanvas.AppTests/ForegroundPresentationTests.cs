using System.IO;
using System.Reflection;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class ForegroundPresentationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, bool transitions = false)
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
        runtime.SuppressKeyboard(false); host.Render();
        check("clearing a focus quarantine retains input-free artwork", alpha == 255 && red == 255);
        PublishCurrent(); host.Render();
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
        check("starting a focus quarantine retains artwork when no keys have been published", alpha == 255 && red == 255);
        runtime.SuppressKeyboard(false);
        runtime.ObserveHookInput(new(runtime.InputEpoch,
            [new(PresentationObservedKind.KeyDown, Environment.TickCount64, VirtualKey: 0xA2, ScanCode: 0x1D, KeyLabel: "Ctrl")], null, false));
        check("a retained presentation can carry a physical key", runtime.Frame.PressedKeys.Count == 1);
        PublishCurrent(); host.Render();
        runtime.SuppressKeyboard(true); host.Render();
        check("confirmed password protection still rejects the old presentation frame immediately", alpha == 0);

        if (!transitions) return;
        runtime.SuppressKeyboard(false); PublishCurrent(); host.Render();
        runtime.Configure(demand, false, inputIdentity, "modal"); host.Render();
        check("pausing observation for a source picker retains artwork until replacement", alpha == 255 && red == 255);
        PublishCurrent(); host.Render();
        runtime.Configure(demand, true, inputIdentity, "paused");
        check("restarted capture really observes keyboard input", runtime.ObservingKeyboard);
        var resumedContinuity = runtime.PixelContinuity;
        gate.Observe();
        check("first privacy observation does not retire ordinary restart pixels", ReferenceEquals(resumedContinuity, runtime.PixelContinuity));
        host.Render();
        check("resuming observation after a source picker retains artwork until replacement", alpha == 255 && red == 255);
        gate.Observe(); host.Render();
        check("privacy probe after capture restart retains unrelated artwork", alpha == 255 && red == 255);
        PublishCurrent(); host.Render();
        runtime.Configure(demand with { Keyboard = false }, true, inputIdentity, "offscreen"); host.Render();
        check("keyboard layer leaving the viewport retains unrelated artwork", alpha == 255 && red == 255);
        PublishCurrent(); host.Render();
        runtime.Configure(demand, true, inputIdentity, "paused"); host.Render();
        check("keyboard layer entering the viewport retains unrelated artwork", alpha == 255 && red == 255);
        gate.Observe(); host.Render();
        check("privacy probe after viewport reentry retains unrelated artwork", alpha == 255 && red == 255);
        PublishCurrent(); host.Render();
        foreach (bool armed in new[] { false, true, false, true })
        {
            var continuity = runtime.PixelContinuity;
            runtime.SetArmed(armed); gate.Observe();
            check("presentation toggle and privacy refresh preserve pixel continuity: " + armed, ReferenceEquals(continuity, runtime.PixelContinuity));
            host.Render();
            check("explicit presentation toggle retains unrelated artwork: " + armed, alpha == 255 && red == 255);
            PublishCurrent(); host.Render();
        }
        foreach (var (name, change) in new (string, Action)[]
        {
            ("capture budget stop", runtime.BlockForBudget),
            ("capture budget retry", runtime.RetryIfFaulted),
            ("capture plan replacement", () => runtime.Configure(demand, true, new object(), "paused"))
        })
        {
            var continuity = runtime.PixelContinuity;
            change(); gate.Observe();
            check(name + " and privacy refresh preserve pixel continuity", ReferenceEquals(continuity, runtime.PixelContinuity));
            host.Render(); check(name + " retains unrelated artwork", alpha == 255 && red == 255);
            PublishCurrent(); host.Render();
        }
        PublishCurrent(); host.Render();
        SwitchModes("static canvas");
        using (var pixels = new SkiaSharp.SKBitmap(8, 8))
        {
            pixels.Erase(SkiaSharp.SKColors.Blue);
            using var image = SkiaSharp.SKImage.FromBitmap(pixels);
            using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            editor.Session.Add(new() { Kind = ElementKind.Image, Width = 20, Height = 20,
                Transform = Affine2D.Translation(-19940, -19940), AssetId = editor.Session.Assets.Add(png.ToArray(), "image/png"),
                WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "synthetic source" } });
        }
        editor.Session.Add(new() { Kind = ElementKind.Rectangle, Width = 20, Height = 20,
            Transform = Affine2D.Translation(-19900, -19940), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } });
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
        PublishCurrent(); host.Render();
        editor.Tool = EditorTool.Select; editor.BackgroundPickingEnabled = false; editor.ResumePresentedPicking();
        foreach (var element in editor.Session.Document.Elements.Skip(1).ToArray())
        {
            double startX = element.Transform.OffsetX;
            int undo = editor.Session.UndoCount;
            editor.Session.SetSelection([element.Id]);
            foreach (var kind in new[] { OverlayPointerKind.Down, OverlayPointerKind.Move, OverlayPointerKind.Up })
            {
                double x = startX + (kind == OverlayPointerKind.Down ? 10 : 30);
                editor.Pointer(new(kind, new(x, -19930), OverlayMouseButton.Left, OverlayModifiers.None));
                host.Render();
                check("window-source canvas retains opaque artwork while dragging " + element.Kind + ": " + kind, alpha == 255 && red == 255);
                PublishCurrent(); host.Render();
            }
            check("retained pixels do not cancel the actual " + element.Kind + " move", !editor.Session.IsTransactionActive &&
                editor.Session.UndoCount == undo + 1 && editor.Session.Document.Elements.Single(item => item.Id == element.Id).Transform.OffsetX > startX);
        }
        typeof(DesktopSession).GetMethod("ChangeCanvasViewing", Hidden)!.Invoke(desktop, [new CanvasViewTransform(1.1, default), false]);
        host.Render();
        check("window-source camera changes retain the last complete canvas", alpha == 255 && red == 255);
        typeof(DesktopSession).GetMethod("ChangeCanvasViewing", Hidden)!.Invoke(desktop, [CanvasViewTransform.Identity, false]);
        PublishCurrent(); host.Render();
        SwitchModes("window-source canvas");

        void SwitchModes(string name)
        {
            foreach (bool editing in new[] { true, false, true, false })
            {
                typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, editing);
                host.Render();
                check(name + " mode switch retains opaque artwork until replacement: " + editing, alpha == 255 && red == 255);
                PublishCurrent(); host.Render();
            }
        }

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
