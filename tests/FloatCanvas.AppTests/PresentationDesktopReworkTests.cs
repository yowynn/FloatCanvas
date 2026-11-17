using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real DesktopSession paths with injected availability, input and password metadata on a never-shown desktop.</summary>
internal static class PresentationDesktopReworkTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        RequirePrivateDesktop();
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private-desktop child process.");
        string folder = Path.Combine(output ?? AppContext.BaseDirectory, "presentation-desktop-rework-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ApplicationPaths.Configure(["--profile", Path.Combine(folder, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-24000, -23000, 800, 700);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("presentation-rework", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false; Field<Window>(desktop, "_toolbar").Opacity = 0;
        var editor = Field<EditorController>(desktop, "_editor"); editor.BackgroundPickingEnabled = false;
        var session = editor.Session;
        var originalRuntime = Field<PresentationRuntimeBroker>(desktop, "_presentationRuntime");
        check("actual desktop constructor auto-arms presentation without starting an empty observer", originalRuntime.Armed && !originalRuntime.Observing && originalRuntime.InputEpoch == -1);
        check("the genuine input-desktop check stays false throughout this private fixture", !PresentationDesktopAvailability.IsCurrentInputDesktop());
        originalRuntime.Dispose();
        long now = 1000; int passwordQueries = 0; var starts = new List<(bool Keyboard, bool Pointer, FakeSource Source)>();
        var runtime = new PresentationRuntimeBroker(app.Dispatcher, () => Call("OnPresentationRuntimeChanged"),
            (epoch, keyboard, pointer, _) =>
            {
                var source = new FakeSource(epoch); starts.Add((keyboard, pointer, source));
                return Task.FromResult<IPresentationInputSource>(source);
            }, () => TimeSpan.FromMilliseconds(now), () => host.IsEditing);
        Set(desktop, "_presentationRuntime", runtime);
        var privacy = new PresentationKeyboardPrivacyGate(app.Dispatcher, runtime,
            completed => new PresentationKeyboardPrivacyProbe(completed, () => { Interlocked.Increment(ref passwordQueries); return false; }, () => 1),
            () => 1, () => now);
        Set(desktop, "_presentationPrivacy", privacy);
        // Only this synthetic backend is available. The production default remains the real desktop check.
        Set(desktop, "_presentationDesktopAvailable", (Func<bool>)(() => true));
        runtime.BeforeInput += _ => { Call("UpdatePresentationRuntime"); privacy.Observe(); };
        Call("InitializePresentation");
        check("controlled fake availability cannot create demand without a key layer or pointer master", runtime.Armed && starts.Count == 0 && !Demand().Any && !runtime.Observing);

        var viewport = new RectD(bounds.Left - editor.ScreenOrigin.X, bounds.Top - editor.ScreenOrigin.Y, bounds.Width, bounds.Height);
        session.Execute("prepare transparent presentation test canvas", document =>
        {
            document.Viewport = viewport; document.Canvas.Grid.Visible = false; document.Canvas.BackgroundColor = 0;
            document.Canvas.Projection.Enabled = false;
        });
        host.SetVisible(true); SetEditing(true);
        using var worker = new CanvasPreviewRenderer();
        using (var baseline = Capture()) using (var frame = worker.Render(baseline)) Complete(frame, baseline.Context);
        editor.ApplyParameters(new() { Tool = EditorTool.Presentation, Presentation = new() { Modules = PresentationModules.Keys, KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys },
            Style = new() { Foreground = 0xffffffff, Background = 0xdd20252f, FontSize = 22, Variant = "default" } });
        Send(OverlayPointerKind.Down, viewport.Left + 40, viewport.Top + 40, OverlayModifiers.Control);
        Send(OverlayPointerKind.Move, viewport.Left + 340, viewport.Top + 160, OverlayModifiers.Control);
        Send(OverlayPointerKind.Up, viewport.Left + 340, viewport.Top + 160, OverlayModifiers.Control);
        var keyLayer = session.Document.Elements.Single();
        check("drawing a keyboard layer starts recording automatically in editing metadata", keyLayer.Kind == ElementKind.Presentation && Demand().Keyboard && !Demand().Pointer && runtime.ObservingKeyboard && starts.Count == 1 && starts[0].Keyboard);
        check("the controlled editing fixture never acquires a real input-desktop or keyboard isolation lease", host.IsEditing && !PresentationDesktopAvailability.IsCurrentInputDesktop() && !host.IsKeyboardIsolationActive);
        using (var empty = Capture()) using (var frame = worker.Render(empty)) using (var pixels = Pixels(frame))
        {
            check("an empty keyboard display has no authored scene pixels", Count(pixels) == 0);
            Complete(frame, empty.Context);
        }
        using (var guides = DrawGuides())
        {
            Save(guides, "empty-keyboard-editing-guides.png");
            check("actual editing-guide drawing exposes the transparent keyboard extent", Count(guides) > 100 && guides.GetPixel(100, 100).Alpha > 0 && guides.GetPixel(650, 500).Alpha == 0);
        }
        foreach (var style in new[] { PresentationDisplayStyle.Keycaps, PresentationDisplayStyle.Keyboard, PresentationDisplayStyle.Log })
        {
            session.Execute("change guide display style", document => document.Elements.Single().Presentation!.DisplayStyle = style);
            using var guides = DrawGuides();
            check($"editing guide only belongs to log styles: {style}",
                (Count(guides) > 100) == (style != PresentationDisplayStyle.Keyboard));
        }
        editor.Tool = EditorTool.Select; session.SetSelection([]);
        Send(OverlayPointerKind.Down, viewport.Left + 100, viewport.Top + 100);
        Send(OverlayPointerKind.Up, viewport.Left + 100, viewport.Top + 100);
        check("the empty keyboard extent can be selected through the real editor pointer path", session.Selection.SetEquals([keyLayer.Id]));

        session.Execute("preserve obsolete keyboard flags", document =>
        {
            document.Elements.Single().Presentation!.Enabled = false;
            document.Elements.Single().Presentation!.Modules = PresentationModules.None;
        });
        check("obsolete enable and module flags cannot disable an existing visible keyboard layer", Demand().Keyboard && runtime.ObservingKeyboard && starts.Count == 1);
        session.Execute("hide keyboard layer", document => document.Elements.Single().Visible = false);
        check("hiding the keyboard layer stops its demand without a global stop command", !Demand().Keyboard && !Demand().Any && !runtime.Observing);
        session.Execute("show keyboard layer", document => document.Elements.Single().Visible = true);
        check("showing the layer automatically resumes its keyboard observer", Demand().Keyboard && runtime.ObservingKeyboard && starts.Count == 2);

        string untouched = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount, assets = session.Assets.Count;
        Feed([Key(0x11, "Ctrl", 29)]);
        check("modifier-only input has a held preview", runtime.Frame.RecentChords is [{ IsHeld: true }]);
        Feed([Key(0x43, "C", 46)]);
        check("global keyboard input outside any pointer region produces one Ctrl+C chord", runtime.Frame.RecentChords is [var chord] && chord.Keys.Select(key => key.Label).SequenceEqual(new[] { "Ctrl", "C" }));
        using (var capture = Capture()) using (var frame = worker.Render(capture)) using (var pixels = Pixels(frame))
        {
            Save(pixels, "actual-keyboard-worker.png");
            check("the actual desktop snapshot and worker render the live keyboard chord", ReferenceEquals(capture.PresentationFrame, runtime.Frame) && frame.PresentationFrameSnapshot?.RecentChords.Count == 1 && Count(pixels) > 100 && pixels.GetPixel(650, 500).Alpha == 0);
            Complete(frame, capture.Context);
        }
        Feed([Key(0x43, "C", 46, up: true), Key(0x11, "Ctrl", 29, up: true)]);
        check("releasing Ctrl+C retains one complete chord without a residual Ctrl row", runtime.Frame.PressedKeys.Count == 0 && runtime.Frame.RecentChords is [{ IsHeld: false } released] && released.Keys.Count == 2);
        check("keyboard observation and worker rendering do not mutate history or assets", session.UndoCount == undo && session.Assets.Count == assets && JsonSerializer.Serialize(session.Document) == untouched);

        int keyboardSourceCount = starts.Count; long keyboardEpoch = runtime.InputEpoch;
        var preferences = Settings().PresentationGlobal.Clone(); preferences.Pointer.Enabled = true;
        preferences.Pointer.CursorEnabled = true; preferences.Pointer.ClicksEnabled = false; preferences.Pointer.TrailEnabled = true;
        preferences.Pointer.CursorColor = 0xff00ff00; preferences.Pointer.CursorRadiusPixels = 12; preferences.Pointer.CursorOpacity = 1;
        Apply(preferences);
        check("enabling pointer effects keeps an active keyboard source and its recent chord", starts.Count == keyboardSourceCount && runtime.InputEpoch == keyboardEpoch && runtime.Frame.RecentChords.Count == 1 && Demand().Keyboard && Demand().Pointer);
        preferences = Settings().PresentationGlobal.Clone(); preferences.Pointer.ClicksEnabled = true;
        preferences.Pointer.LeftClick.Shape = preferences.Pointer.RightClick.Shape = preferences.Pointer.MiddleClick.Shape = PresentationClickShape.Disc;
        Apply(preferences);
        Feed([
            new(PresentationObservedKind.ButtonDown, now + 10, X: bounds.Left + 470, Y: bounds.Top + 250, HasPosition: true, Button: 0),
            new(PresentationObservedKind.ButtonDown, now + 10, X: bounds.Left + 580, Y: bounds.Top + 250, HasPosition: true, Button: 1),
            new(PresentationObservedKind.ButtonDown, now + 10, X: bounds.Left + 690, Y: bounds.Top + 250, HasPosition: true, Button: 2)]);
        using (var clicks = DrawPointer())
        {
            Save(clicks, "global-left-right-middle-clicks.png");
            check("global raw button events reach their independent left right and middle screen styles", Demand().Clicks && runtime.Frame.Clicks.Count == 3 && clicks.GetPixel(470, 250) == new SKColor(0xffffc107) && clicks.GetPixel(580, 250) == new SKColor(0xff42a5f5) && clicks.GetPixel(690, 250) == new SKColor(0xffab47bc));
        }
        preferences = Settings().PresentationGlobal.Clone(); preferences.Pointer.ClicksEnabled = false; Apply(preferences);
        Feed(cursor: new(bounds.Left + 650, bounds.Top + 300, now + 10));
        Feed(cursor: new(bounds.Left + 680, bounds.Top + 330, now + 10));
        check("global pointer demand requires no pointer layer or authored pointer module", session.Document.Elements.Count == 1 && session.Document.Elements[0].Presentation is { Enabled: false, Modules: PresentationModules.None } && Demand().Keyboard && Demand().Pointer && Demand().Trail);
        using (var pointerPixels = DrawPointer())
        {
            Save(pointerPixels, "global-pointer-outside-keyboard.png");
            check("actual desktop pointer draw paints negative screen coordinates outside the keyboard region", Count(pointerPixels) > 20 && pointerPixels.GetPixel(680, 330).Green > 200 && pointerPixels.GetPixel(680, 330).Alpha > 0);
        }
        using (var capture = Capture()) using (var frame = worker.Render(capture)) using (var pixels = Pixels(frame))
            check("global pointer pixels never enter the scene preview", pixels.GetPixel(680, 330).Alpha == 0);
        using (var renderer = new SceneRenderer()) using (var encoded = SKBitmap.Decode(renderer.ExportPng(session.Document, session.Assets, true)))
            check("ordinary image export contains no global pointer or runtime keyboard input", Count(encoded) == 0);

        ClearDamage(); preferences = Settings().PresentationGlobal.Clone(); preferences.Pointer.CursorColor = 0xff0066ff; preferences.Pointer.CursorRadiusPixels = 4;
        Apply(preferences);
        var oldEdge = new PointD(bounds.Left + 690, bounds.Top + 330);
        check("changing cursor radius schedules cleanup of the formerly larger screen mark", Dirty().Any(region => region.Contains(new((int)oldEdge.X, (int)oldEdge.Y))));
        using (var changed = DrawPointer())
            check("cursor color and radius update actual screen pixels", changed.GetPixel(680, 330).Blue > 200 && changed.GetPixel(690, 330).Alpha == 0);
        ClearDamage(); Call("TogglePresentationPointer");
        check("pointer master off clears its drawing and schedules old-pixel removal", !Settings().PresentationGlobal.Pointer.Enabled && Field<PresentationFrame?>(desktop, "_pointerPresentationFrame") is null && Dirty().Any(region => region.Contains(new(bounds.Left + 680, bounds.Top + 330))));
        using (var off = DrawPointer()) check("disabled pointer draws no pixels", Count(off) == 0);
        check("pointer off preserves styles and does not stop or clear keyboard recording", Settings().PresentationGlobal.Pointer.CursorColor == 0xff0066ff && Settings().PresentationGlobal.Pointer.CursorRadiusPixels == 4 && starts.Count == keyboardSourceCount && runtime.InputEpoch == keyboardEpoch && runtime.Frame.RecentChords.Count == 1);
        check("global mouse commands leave authored source history and resources untouched", session.UndoCount == undo && session.Assets.Count == assets && JsonSerializer.Serialize(session.Document) == untouched);

        Call("TogglePresentationPointer");
        session.Execute("remove keyboard layer", document => new SceneGraph(document).RemoveNodes([keyLayer.Id]));
        check("deleting the last keyboard layer ends only keyboard demand", !Demand().Keyboard && Demand().Pointer && runtime.Observing && !runtime.ObservingKeyboard && starts[^1].Keyboard == false);
        Feed([Key(0x41, "A", 30)], new(bounds.Left + 650, bounds.Top + 500, now + 10));
        check("pointer-only observation cannot add keyboard records", runtime.Frame.RecentChords.Count == 0 && runtime.Frame.PressedKeys.Count == 0);
        int pointerStarts = starts.Count; long pointerEpoch = runtime.InputEpoch; object pointerContext = Field<object>(desktop, "_presentationInputContext");
        using var fixedPointer = DrawPointer();
        session.Execute("change canvas opacity", document => document.LayerOpacity = .5);
        using (var faded = DrawPointer())
            check("foreground pointer effects inherit overall canvas opacity", Math.Abs(faded.GetPixel(650, 500).Alpha - fixedPointer.GetPixel(650, 500).Alpha * .5) <= 1);
        session.Execute("restore canvas opacity", document => document.LayerOpacity = 1);
        session.Execute("change canvas viewport and projection", document =>
        {
            document.Viewport = new(-10, -10, 40, 40); document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
            document.Canvas.Projection.Enabled = true;
        });
        editor.ScreenOrigin += new PointD(57, -23); Call("UpdatePresentationRuntime");
        using (var after = DrawPointer())
            check("viewport perspective and origin cannot move foreground pointer pixels", fixedPointer.Bytes.SequenceEqual(after.Bytes) && after.GetPixel(650, 500).Alpha > 0);
        check("pointer-only canvas changes reuse the actual source and stable input context", starts.Count == pointerStarts && runtime.InputEpoch == pointerEpoch && ReferenceEquals(pointerContext, Field<object>(desktop, "_presentationInputContext")));
        SetEditing(false); host.SetVisible(false); Call("UpdateHiddenCanvasState");
        check("hidden canvas hides the pointer host and stops observation without disabling the master", !host.IsVisible && !host.IsPreviewVisible && !runtime.Observing && Settings().PresentationGlobal.Pointer.Enabled);
        using (var hidden = DrawPointer()) check("hidden canvas has no foreground pointer pixels", Count(hidden) == 0);

        host.SetVisible(true);
        session.Execute("restore visible keyboard region", document =>
        {
            document.Viewport = viewport; document.Canvas.Projection.Enabled = false; document.LayerOpacity = 1;
            new SceneGraph(document).InsertElement(keyLayer.Clone());
        });
        Feed(cursor: new(bounds.Left + 650, bounds.Top + 500, now + 10));
        using (var shown = DrawPointer()) check("showing the canvas restores pointer presentation", Demand().Pointer && shown.GetPixel(650, 500).Alpha > 0);
        session.Execute("zero canvas opacity", document => document.LayerOpacity = 0);
        using (var transparent = DrawPointer()) check("zero overall opacity removes all pointer pixels", Count(transparent) == 0 && !Demand().Pointer);
        session.Execute("restore overall opacity", document => document.LayerOpacity = 1);
        host.SetVisible(false); Call("UpdateHiddenCanvasState");
        Feed([Key(0x5a, "Z", 44)]);
        check("hiding canvas removes both keyboard and pointer demand", !Demand().Keyboard && !Demand().Pointer && !runtime.Observing && runtime.Frame.RecentChords.Count == 0);
        Call("TogglePresentationPointer"); Call("UpdateHiddenCanvasState");
        check("turning off the last global effect removes the hidden preview and observer", !host.IsVisible && !host.IsPreviewVisible && !runtime.Observing && !Demand().Any);
        check("all fixture source objects are synthetic and the real private-desktop gate stayed false", starts.All(request => request.Source.GetType() == typeof(FakeSource)) && starts.All(request => !request.Source.IsRunning) && !PresentationDesktopAvailability.IsCurrentInputDesktop() && !host.IsKeyboardIsolationActive && !host.HasEditingInput);
        check("password checks used only the injected false metadata provider", passwordQueries > 0 && Field<PresentationKeyboardPrivacyGate>(desktop, "_presentationPrivacy") == privacy);
        Console.WriteLine("PRESENTATION_DESKTOP_REWORK " + folder);

        UserSettings Settings() => Field<UserSettings>(desktop, "_settings");
        PresentationRuntimeDemand Demand() => Field<PresentationRuntimeDemand>(runtime, "demand");
        void Apply(PresentationGlobalSettings value) => Call("ApplyPresentationGlobalSettings", value);
        CanvasPreviewSnapshot Capture() => (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!;
        void Complete(CanvasPreviewFrame frame, CanvasPreviewContext context)
        {
            Set(desktop, "_submittingCanvasContext", context); Set(desktop, "_submittingCanvasFrame", frame.Retain());
            Call("CompleteCanvasSubmission", new OverlayFrameTiming(0, 0, true, [new("presentation-rework", bounds, 0, 0, 0, 0)]));
        }
        void SetEditing(bool value) => typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, value);
        void Feed(IEnumerable<PresentationObservedInput>? changes = null, PresentationObservedCursor? cursor = null)
        {
            now += 10; starts[^1].Source.Queue(changes, cursor);
            typeof(PresentationRuntimeBroker).GetMethod("Drain", Hidden)!.Invoke(runtime, null);
        }
        PresentationObservedInput Key(int vk, string label, int scan, bool up = false) => new(up ? PresentationObservedKind.KeyUp : PresentationObservedKind.KeyDown, now + 10, 1, vk, scan, 0, label);
        void Send(OverlayPointerKind kind, double x, double y, OverlayModifiers modifiers = OverlayModifiers.None)
        { editor.Pointer(new(kind, new(x + editor.ScreenOrigin.X, y + editor.ScreenOrigin.Y), OverlayMouseButton.Left, modifiers)); }
        SKBitmap Pixels(CanvasPreviewFrame rendered)
        { using var surface = Surface(); rendered.Pixels.Draw(surface.Canvas); using var image = surface.Snapshot(); return SKBitmap.FromImage(image); }
        SKBitmap DrawPointer()
        { using var surface = Surface(); Call("DrawPresentationPointer", surface.Canvas); using var image = surface.Snapshot(); return SKBitmap.FromImage(image); }
        SKBitmap DrawGuides()
        { using var surface = Surface(); surface.Canvas.Translate((float)editor.ScreenOrigin.X, (float)editor.ScreenOrigin.Y); Call("DrawPresentationLayerGuides", surface.Canvas); using var image = surface.Snapshot(); return SKBitmap.FromImage(image); }
        SKSurface Surface()
        { var surface = SKSurface.Create(new SKImageInfo(800, 700, SKColorType.Bgra8888, SKAlphaType.Premul)); surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(-bounds.Left, -bounds.Top); return surface; }
        List<PixelBounds> Dirty() => Field<List<PixelBounds>>(desktop, "_presentationDirty");
        void ClearDamage()
        {
            (Field<DispatcherOperation?>(desktop, "_presentOperation"))?.Abort(); Set(desktop, "_presentOperation", null!); Dirty().Clear();
        }
        void Save(SKBitmap bitmap, string name)
        { using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(Path.Combine(folder, name), encoded.ToArray()); }
        object? Call(string method, params object?[] arguments)
        {
            try { return typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, arguments); }
            catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    private static int Count(SKBitmap bitmap) => bitmap.Pixels.Count(pixel => pixel.Alpha != 0);
    private sealed class FakeSource(long epoch) : IPresentationInputSource
    {
        private readonly List<PresentationObservedInput> pending = [];
        private PresentationObservedCursor? cursor;
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Epoch => epoch;
        public bool IsRunning { get; private set; } = true;
        public Task Completion => completed.Task;
        public event Action? Available;
        internal void Queue(IEnumerable<PresentationObservedInput>? input, PresentationObservedCursor? point)
        { if (!IsRunning) return; if (input is not null) pending.AddRange(input); if (point is not null) cursor = point; Available?.Invoke(); }
        public PresentationObservedBatch Drain()
        { var batch = new PresentationObservedBatch(Epoch, pending, cursor, false); pending.Clear(); cursor = null; return batch; }
        public bool Stop() { IsRunning = false; completed.TrySetResult(); return true; }
        public void Dispose() { IsRunning = false; completed.TrySetResult(); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static void RequirePrivateDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) || !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --presentation-desktop-rework-only.");
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5b31, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5b32, 7, key + 1)) continue; UnregisterHotKey(0, 0x5b32); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5b31); }
        }
        throw new InvalidOperationException("No free synthetic fixture hotkeys.");
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint handle, int id);
}
