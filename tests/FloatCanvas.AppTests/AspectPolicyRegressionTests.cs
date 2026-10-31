using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Production pointer and desktop-effect creation paths with owned, offscreen surfaces.
/// The capture callback supplies generated pixels; no desktop capture or input injection.</summary>
internal static class AspectPolicyRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var outcomes = new List<(string Name, bool Passed)>();
        SizedPlacements(Observe);
        ProjectedPlacements(Observe);
        DesktopEffects(Observe);
        foreach (var outcome in outcomes) check(outcome.Name, outcome.Passed);

        void Observe(string name, bool passed)
        {
            outcomes.Add((name, passed));
            Console.WriteLine($"ASPECT_FINDING {passed}: {name}");
        }
    }

    private static void SizedPlacements(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Number, EditorTool.Emoji })
        foreach (bool shift in new[] { false, true })
        foreach (var delta in new[] { new PointD(180, 80), new(-180, 80), new(180, -80), new(-180, -80) })
        {
            var session = new EditorSession();
            var editor = new EditorController(session);
            var preset = new ToolParameters { Tool = tool, Emoji = "😀", ConstrainAspect = false };
            editor.ApplyParameters(preset);
            string parameters = JsonSerializer.Serialize(editor.Parameters);
            var start = new PointD(500, 400); var end = start + delta;
            var modifiers = shift ? OverlayModifiers.Shift : OverlayModifiers.None;
            Send(editor, OverlayPointerKind.Down, start, modifiers);
            Send(editor, OverlayPointerKind.Move, end, modifiers);
            var element = session.Document.Elements.Single();
            var preview = ElementGeometry.Bounds(element, false);
            string name = $"{tool} Shift={shift} direction={delta}";
            Console.WriteLine($"ASPECT_PLACEMENT {name} preview={preview.Width:F3}x{preview.Height:F3} layerAspect={element.ConstrainAspect}");
            check(name + (shift ? " held Shift permits a nonuniform creation preview" : " default creation remains proportional"),
                shift ? Near(preview.Width, 180) && Near(preview.Height, 80) : Near(preview.Width, preview.Height));
            Send(editor, OverlayPointerKind.Up, end, modifiers);
            var bounds = ElementGeometry.Bounds(element, false);
            check(name + " release retains the preview and one undo step", bounds == preview && session.UndoCount == 1 && !session.IsTransactionActive);
            check(name + " gesture leaves tool parameters unchanged", JsonSerializer.Serialize(editor.Parameters) == parameters);
            check(name + " keeps its default layer aspect preference", ElementGeometry.PreservesAspect(element));
            check(name + " insertion remains one-step undo/redo", session.Undo() && session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Count == 1);
        }

        foreach (var tool in new[] { EditorTool.Number, EditorTool.Emoji })
        {
            var changing = new EditorSession(); var live = new EditorController(changing);
            live.ApplyParameters(new() { Tool = tool, Emoji = "😀" });
            string defaults = JsonSerializer.Serialize(live.Parameters);
            Send(live, OverlayPointerKind.Down, new(100, 100));
            Send(live, OverlayPointerKind.Move, new(280, 180));
            live.ModifiersChanged(OverlayModifiers.Shift, new(280, 180));
            var freelySized = ElementGeometry.Bounds(changing.Document.Elements.Single(), false);
            check(tool + " pressing Shift during the drag immediately unlocks the preview", Near(freelySized.Width, 180) && Near(freelySized.Height, 80));
            live.ModifiersChanged(OverlayModifiers.None, new(280, 180));
            var proportional = ElementGeometry.Bounds(changing.Document.Elements.Single(), false);
            check(tool + " releasing Shift during the drag restores its proportional preview", Near(proportional.Width, proportional.Height));
            live.Cancel();
            check(tool + " modifier toggles and cancellation do not persist defaults or content", changing.Document.Elements.Count == 0 && changing.UndoCount == 0 && JsonSerializer.Serialize(live.Parameters) == defaults);
            foreach (bool releaseOnly in new[] { false, true })
            foreach (bool shiftAtRelease in new[] { false, true })
            {
                var session = new EditorSession(); var editor = new EditorController(session);
                editor.ApplyParameters(new() { Tool = tool, Emoji = "😀" });
                string parameters = JsonSerializer.Serialize(editor.Parameters);
                var held = shiftAtRelease ? OverlayModifiers.None : OverlayModifiers.Shift;
                Send(editor, OverlayPointerKind.Down, new(100, 100), held);
                if (!releaseOnly) Send(editor, OverlayPointerKind.Move, new(280, 180), held);
                Send(editor, OverlayPointerKind.Up, new(280, 180), shiftAtRelease ? OverlayModifiers.Shift : OverlayModifiers.None);
                var committed = ElementGeometry.Bounds(session.Document.Elements.Single(), false);
                check($"{tool} releaseOnly={releaseOnly} ShiftAtRelease={shiftAtRelease} applies the final modifier state",
                    shiftAtRelease ? Near(committed.Width, 180) && Near(committed.Height, 80) : Near(committed.Width, committed.Height));
                check($"{tool} releaseOnly={releaseOnly} ShiftAtRelease={shiftAtRelease} commits once without changing defaults",
                    session.UndoCount == 1 && !session.IsTransactionActive && JsonSerializer.Serialize(editor.Parameters) == parameters);
            }
        }
    }

    private static void ProjectedPlacements(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Number, EditorTool.Emoji })
        foreach (bool billboard in new[] { false, true })
        foreach (bool shift in new[] { false, true })
        {
            var document = new SceneDocument { Viewport = new(0, 0, 1000, 800) };
            var projection = CanvasCalibration.CreateInitial(document.Viewport, 10, 8);
            projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1); projection.Enabled = true;
            projection.Quad = projection.Quad.Select(projection.Matrix.TransformPoint).ToList(); document.Canvas.Projection = projection;
            var session = new EditorSession(document);
            var editor = new EditorController(session) { Tool = tool, Emoji = "😀", BillboardMode = billboard };
            var mapping = CanvasMapping.Create(document);
            string name = $"{tool} {(billboard ? "Billboard" : "plane perspective")} Shift={shift}";
            if (billboard)
            {
                Send(editor, OverlayPointerKind.Move, new(300, 300));
                Send(editor, OverlayPointerKind.Down, new(300, 300));
                Send(editor, OverlayPointerKind.Up, new(300, 300));
                if (editor.ActiveBillboardDepthLine is null) throw new InvalidOperationException("The aspect fixture did not choose its production Billboard depth.");
                check(name + " chooses real depth without inserting a layer", session.Document.Elements.Count == 0 && session.UndoCount == 0);
            }
            string defaults = JsonSerializer.Serialize(editor.Parameters);
            var start = billboard ? new PointD(100, 100) : mapping.ToScreen(new(100, 100));
            var end = billboard ? new PointD(280, 180) : mapping.ToScreen(new(280, 180));
            var modifiers = shift ? OverlayModifiers.Shift : OverlayModifiers.None;
            Send(editor, OverlayPointerKind.Down, start, modifiers);
            Send(editor, OverlayPointerKind.Move, end, modifiers);
            var element = session.Document.Elements.Single();
            var initial = Bounds();
            check(name + " creation follows the aspect policy in its intended coordinate space", Expected(initial, shift));
            editor.ModifiersChanged(shift ? OverlayModifiers.None : OverlayModifiers.Shift, new(end.X, end.Y));
            check(name + " modifier changes immediately invert the projected creation preview", Expected(Bounds(), !shift));
            editor.ModifiersChanged(modifiers, new(end.X, end.Y));
            check(name + " releasing the temporary inversion restores the projected preview", Expected(Bounds(), shift));
            Send(editor, OverlayPointerKind.Up, end, modifiers);
            check(name + " commits the intended placement and default layer preference once", Expected(Bounds(), shift) &&
                element.IsBillboard == billboard && element.ConstrainAspect == true && session.UndoCount == 1 && !session.IsTransactionActive);
            check(name + " projected placement preserves tool defaults", JsonSerializer.Serialize(editor.Parameters) == defaults);
            check(name + " undo and redo retain projected placement metadata", session.Undo() && session.Document.Elements.Count == 0 && session.Redo() &&
                session.Document.Elements.Single().IsBillboard == billboard && session.Document.Elements.Single().ConstrainAspect == true);

            RectD Bounds() => billboard ? mapping.ProjectedBounds(element, false) : ElementGeometry.Bounds(element, false);
            static bool Expected(RectD bounds, bool free) => free ? Near(bounds.Width, 180) && Near(bounds.Height, 80) : Near(bounds.Width, bounds.Height);
        }
    }

    private static void DesktopEffects(Action<string, bool> check)
    {
        string originalProfile = ApplicationPaths.ProfileDirectory;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-aspect-regression", Guid.NewGuid().ToString("N"));
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var originalContext = SynchronizationContext.Current;
        var originalWindows = app.Windows.Cast<Window>().ToArray();
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Theme = "light", ShowFloatingLogo = false }.Save();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        DesktopSession? desktop = null; OverlayHost? host = null;
        try
        {
            foreach (int build in new[] { 14393, 19041 })
            {
                var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
                var bounds = new PixelBounds(-12000, -12000, 640, 360);
                Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = bounds, WorkArea = bounds }];
                host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                    [OverlayMonitorScope.PrimaryScreen, provider, false, WindowsCapabilities.FromVersion(new Version(10, 0, build))], null)!;
                desktop = new DesktopSession(app, host);
                Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
                (Field<object?>(host, "_keyboardIsolation") as IDisposable)?.Dispose();
                typeof(OverlayHost).GetField("_keyboardIsolation", Private)!.SetValue(host, null);
                // Hold the capability probe pending so a render cannot start actual WGC.
                typeof(DesktopSession).GetField("_sessionCaptureProbe", Private)!.SetValue(desktop, new TaskCompletionSource<bool>().Task);
                var editor = Field<EditorController>(desktop, "_editor");
                editor.BackgroundPickingEnabled = false;
                // Preserve the production controller event payload and core creation route,
                // replacing only the environmental clean-desktop pixel acquisition.
                typeof(EditorController).GetField("DesktopEffectRequested", Private)!.SetValue(editor, null);
                Task? operation = null; RectD? requested = null; int captures = 0;
                Func<Task<CapturedDesktop>> capture = () =>
                {
                    captures++;
                    using var surface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height)); surface.Canvas.Clear(SKColors.Blue);
                    using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                    return Task.FromResult(new CapturedDesktop(bounds, png.ToArray()));
                };
                editor.DesktopEffectRequested += (region, kind, amount, color) =>
                {
                    requested = region;
                    operation = (Task)typeof(DesktopSession).GetMethod("AddDesktopEffectCoreAsync", Private)!.Invoke(desktop,
                        [region, kind, amount, color, capture])!;
                };
                foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate })
                foreach (bool locked in new[] { false, true })
                foreach (bool shift in new[] { false, true })
                {
                    editor.Cancel(); editor.Session.Document.Elements.Clear(); editor.Session.SetSelection([]);
                    editor.ApplyParameters(new() { Tool = tool, ConstrainAspect = locked, EffectStrength = 8 });
                    // This hidden fixture has no submitted display frame. Each case
                    // explicitly uses its current document as the picking source.
                    editor.ApplyPresentedScene(null, null, TimeSpan.Zero);
                    string parameters = JsonSerializer.Serialize(editor.Parameters);
                    operation = null; requested = null; int beforeCapture = captures, beforeUndo = editor.Session.UndoCount;
                    var start = editor.Session.Document.Viewport.Center - new PointD(100, 60);
                    var end = start + new PointD(160, 80);
                    var modifiers = shift ? OverlayModifiers.Shift : OverlayModifiers.None;
                    Send(editor, OverlayPointerKind.Down, start + editor.ScreenOrigin, modifiers);
                    Send(editor, OverlayPointerKind.Move, end + editor.ScreenOrigin, modifiers);
                    Send(editor, OverlayPointerKind.Up, end + editor.ScreenOrigin, modifiers);
                    if (operation is null || requested is null) throw new InvalidOperationException("The production pointer route did not request its desktop effect.");
                    if (build >= 19041)
                    {
                        var pending = Field<object>(desktop, "_liveEffectPreparation");
                        var ready = (TaskCompletionSource<bool>)pending.GetType().GetProperty("Ready", Private)!.GetValue(pending)!;
                        ready.TrySetResult(true);
                    }
                    Await(operation);
                    var element = editor.Session.Document.Elements.Single();
                    string name = $"{tool} Windows {build} preset={locked} Shift={shift}";
                    Console.WriteLine($"ASPECT_EFFECT {name} bounds={requested.Value.Width:F3}x{requested.Value.Height:F3} layerAspect={element.ConstrainAspect} effective={ElementGeometry.PreservesAspect(element)}");
                    check(name + " creation geometry temporarily inverts only with Shift", Square(requested.Value) == (locked ^ shift));
                    check(name + " new layer inherits the persistent tool preference", element.ConstrainAspect == locked && ElementGeometry.PreservesAspect(element) == locked);
                    var restored = JsonSerializer.Deserialize<CanvasElement>(JsonSerializer.Serialize(element))!;
                    check(name + " layer preference survives cloning and JSON persistence", element.Clone().ConstrainAspect == locked &&
                        restored.ConstrainAspect == locked && ElementGeometry.PreservesAspect(restored) == locked);
                    check(name + " creation does not mutate the tool preference", JsonSerializer.Serialize(editor.Parameters) == parameters);
                    check(name + " creates one production layer with one undo step", editor.Session.UndoCount == beforeUndo + 1 &&
                        element.Kind == (build < 19041 ? ElementKind.Image : tool == EditorTool.Blur ? ElementKind.LiveBlur : ElementKind.LivePixelate));
                    check(name + " uses generated fallback pixels or accepted live readiness without desktop capture", captures - beforeCapture == (build < 19041 ? 1 : 0));
                }
                if (build >= 19041)
                {
                    editor.Cancel(); editor.Session.Document.Elements.Clear(); editor.Session.SetSelection([]);
                    var preset = new ToolParameters { Tool = EditorTool.Blur, ConstrainAspect = false, EffectStrength = 8 };
                    editor.ApplyParameters(preset); editor.ApplyPresentedScene(null, null, TimeSpan.Zero);
                    operation = null; requested = null; int beforeUndo = editor.Session.UndoCount;
                    var start = editor.Session.Document.Viewport.Center - new PointD(100, 60);
                    Send(editor, OverlayPointerKind.Down, start + editor.ScreenOrigin);
                    Send(editor, OverlayPointerKind.Move, start + new PointD(160, 80) + editor.ScreenOrigin);
                    Send(editor, OverlayPointerKind.Up, start + new PointD(160, 80) + editor.ScreenOrigin);
                    if (operation is null) throw new InvalidOperationException("The asynchronous aspect case did not request its desktop effect.");
                    var pending = Field<object>(desktop, "_liveEffectPreparation");
                    var ready = (TaskCompletionSource<bool>)pending.GetType().GetProperty("Ready", Private)!.GetValue(pending)!;
                    check("live effect aspect snapshot waits outside content and undo history", !operation.IsCompleted &&
                        editor.Session.Document.Elements.Count == 0 && editor.Session.UndoCount == beforeUndo);
                    editor.ConstrainAspect = true;
                    ready.TrySetResult(true); Await(operation);
                    var created = editor.Session.Document.Elements.Single();
                    check("live effect retains its creation-time aspect preference across asynchronous readiness", created.ConstrainAspect == false &&
                        !ElementGeometry.PreservesAspect(created) && editor.Session.UndoCount == beforeUndo + 1);
                    check("live effect completion preserves a subsequent tool choice without mutating its original preset", editor.ConstrainAspect &&
                        editor.Parameters.ConstrainAspect && !preset.ConstrainAspect);
                    check("live effect aspect snapshot survives one-step undo and redo", editor.Session.Undo() && editor.Session.Document.Elements.Count == 0 &&
                        editor.Session.Redo() && editor.Session.Document.Elements.Single().ConstrainAspect == false);
                }
                Console.WriteLine("ASPECT_SURFACES " + JsonSerializer.Serialize(new
                {
                    Build = build, host.IsEditing, host.IsVisible, host.IsPreviewVisible,
                    Native = host.Diagnostics.Select(pair => new { pair.Bounds, pair.DisplayVisible, pair.InputVisible }),
                    VisibleWindows = app.Windows.Cast<Window>().Except(originalWindows).Where(window => window.IsVisible).Select(window => window.Title)
                }));
                check("aspect effect Windows " + build + " fixture keeps displays offscreen without editing input or visible tool windows", !host.IsEditing &&
                    !host.IsKeyboardIsolationActive && host.Diagnostics.All(pair => !pair.InputVisible && pair.Bounds.Right < -10000 && pair.Bounds.Bottom < -10000) &&
                    app.Windows.Cast<Window>().Except(originalWindows).All(window => !window.IsVisible));
                desktop.Dispose(); desktop = null; host = null;
            }
        }
        finally
        {
            if (desktop is not null) desktop.Dispose(); else host?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().Except(originalWindows).ToArray()) window.Close();
            var restore = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) restore.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(restore.ToArray()); SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    private static bool Square(RectD bounds) => Near(bounds.Width, bounds.Height);
    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
    private static void Await(Task task)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (task.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("Aspect effect creation did not complete.");
        task.GetAwaiter().GetResult();
    }
}
