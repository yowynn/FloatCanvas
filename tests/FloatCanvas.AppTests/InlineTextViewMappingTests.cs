using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

/// <summary>Inline geometry and the actual invisible IME HWND use the displayed view, not the source camera.</summary>
internal static class InlineTextViewMappingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal)) throw new InvalidOperationException("Use --isolated-ui-desktop --inline-text-view-mapping-only.");
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        using var renderer = new SceneRenderer();
        DefaultMapping(check, renderer); Existing(check, renderer); NewText(check, renderer);
        ScreenDraft(check, renderer); NativeCaret(check, renderer);
    }

    internal static void RunPhysical(Action<string, bool> check)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal)) throw new InvalidOperationException("Use --isolated-ui-desktop --canvas-physical-monitors-only.");
        var priorDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            ApplicationPaths.Configure(["--profile", System.IO.Path.Combine(AppContext.BaseDirectory, "physical-monitor-" + Guid.NewGuid().ToString("N"))]);
            new UserSettings { PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.AllScreens, false], null)!;
            var monitors = host.Monitors.ToArray();
            Console.WriteLine("PHYSICAL_MONITORS " + JsonSerializer.Serialize(monitors));
            check("physical monitor acceptance requires at least two actually enumerated Windows displays", monitors.Length >= 2);
            check("physical monitor acceptance observes different effective DPI values", monitors.Select(m => m.DpiX).Distinct().Count() >= 2);
            for (int index = 0; index < monitors.Length; index++)
            {
                var monitor = monitors[index]; var output = host.Diagnostics[index];
                check("actual overlay HWND uses physical bounds and effective DPI for " + monitor.DeviceName,
                    output.Bounds == monitor.Bounds && GetDpiForWindow(output.DisplayHandle) == monitor.DpiX);
                var local = new OverlayPoint(147.25, 83.5); var pixel = OverlayCoordinates.DipToPixel(local, monitor);
                var restored = OverlayCoordinates.PixelToDip(pixel, monitor);
                check("actual monitor-local DIP conversion preserves desktop offset for " + monitor.DeviceName,
                    Math.Abs(pixel.X - monitor.Bounds.Left - local.X * monitor.DpiX / 96d) < 1e-7 &&
                    Math.Abs(pixel.Y - monitor.Bounds.Top - local.Y * monitor.DpiY / 96d) < 1e-7 &&
                    Math.Abs(restored.X - local.X) < 1e-7 && Math.Abs(restored.Y - local.Y) < 1e-7);
            }
            using var renderer = new SceneRenderer();
            var session = Source(true, out var text, out _); var controller = new EditorController(session);
            var owner = new Window { Width = 400, Height = 200, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            _ = new WindowInteropHelper(owner).EnsureHandle();
            try
            {
                controller.SetCanvasView(CanvasMapping.CreateViewing(session.Document, new(1.7, new(45, 30))), default, new object());
                using var edit = new InlineTextEdit(session, text, default, text.Style, renderer, canvasMapping: () => controller.CurrentCanvasMapping);
                PointD origin = default;
                using var input = new CanvasTextInput(owner, edit, () => origin);
                foreach (bool perspective in new[] { false, true })
                foreach (var monitor in monitors.Concat(monitors.Take(1)))
                {
                    var mapping = perspective ? ProjectedView(new(.7, new(60, 20)))
                        : CanvasMapping.CreateViewing(session.Document, new(1.7, new(45, 30)));
                    controller.SetCanvasView(mapping, default, new object());
                    var anchor = new PointD(monitor.Bounds.Left + monitor.Bounds.Width * .45, monitor.Bounds.Top + monitor.Bounds.Height * .45);
                    origin = anchor - edit.LocalToScreen.TransformPoint(new(edit.Caret.X, edit.Caret.Y));
                    bool aligned = CaretMatches(input, edit, origin);
                    var carrier = (Window)typeof(CanvasTextInput).GetField("window", Hidden)!.GetValue(input)!;
                    uint dpi = GetDpiForWindow(new WindowInteropHelper(carrier).Handle);
                    check($"real DPI transition aligns native text caret on {monitor.DeviceName} perspective={perspective}", aligned && dpi == monitor.DpiX);
                }
                edit.Finish(false);
            }
            finally { SessionWindowCoordinator.Close(owner); }

            var screens = monitors.Select(m => new RectD(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height)).ToArray();
            bool shared = false;
            foreach (var upper in screens)
            foreach (var lower in screens.Where(other => other != upper && Math.Abs(other.Top - upper.Bottom) < 1))
            {
                double left = Math.Max(upper.Left, lower.Left), right = Math.Min(upper.Right, lower.Right);
                if (right - left <= 80) continue;
                shared = true; double x = (left + right) / 2;
                check("actual shared monitor border does not auto-pan on either side", CanvasEdgePan.Velocity(new(x, upper.Bottom - 1), screens) == default &&
                    CanvasEdgePan.Velocity(new(x, lower.Top + 1), screens) == default);
            }
            check("actual layout exposes a shared monitor border for this acceptance", shared);
            var bottom = screens.OrderByDescending(s => s.Bottom).First();
            check("actual outer monitor edge still auto-pans", CanvasEdgePan.Velocity(new(bottom.Center.X, bottom.Bottom - 1), screens).Y > 0);
            check("physical fixture keeps both real output windows hidden and never captures desktop input", !host.IsEditing && host.Diagnostics.All(d => !d.DisplayVisible && !d.InputVisible));
        }
        finally { if (priorDpi != 0) SetThreadDpiAwarenessContext(priorDpi); }
    }

    private static EditorSession Source(bool text, out CanvasElement element, out Guid scope)
    {
        element = new() { Kind = ElementKind.Text, Text = "abcd", Width = 180, Height = 50,
            Transform = Affine2D.Translation(8, 11), Style = new() { FontFamily = "Segoe UI", FontSize = 24 } };
        var group = new CanvasGroup { Name = "Text group", Transform = Affine2D.Translation(35, 25).Compose(Affine2D.Scale(2, 2)),
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = text ? [element.Id] : [] };
        scope = group.Id; element.GroupId = scope;
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600), Groups = [group], Elements = text ? [element] : [], RootOrder = [group.Id] };
        document.Canvas.Grid.FlatTranslation = new(-75, 40);
        var session = new EditorSession(document); session.SetEditScope(scope); return session;
    }
    private static CanvasMapping FlatView(PointD translation)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600) }; document.Canvas.Grid.FlatTranslation = translation;
        return CanvasMapping.Create(document);
    }
    private static CanvasMapping ProjectedView(CanvasViewTransform? viewing = null)
    {
        var viewport = new RectD(0, 0, 800, 600);
        if (!CanvasCalibration.TryCalibrate(CanvasCalibration.CreateInitial(viewport), [new(80, 60), new(720, 100), new(660, 540), new(110, 500)], viewport, out var projection, out _))
            throw new InvalidOperationException("Projection fixture did not calibrate.");
        projection.Enabled = true; var document = new SceneDocument { Viewport = viewport, Canvas = new() { Projection = projection } };
        return viewing is { } camera ? CanvasMapping.CreateViewing(document, camera) : CanvasMapping.Create(document);
    }
    private static void DefaultMapping(Action<string, bool> check, SceneRenderer renderer)
    {
        var session = Source(true, out var text, out _); string before = JsonSerializer.Serialize(session.Document);
        using (var edit = new InlineTextEdit(session, text, default, text.Style, renderer))
        {
            check("inline default constructor retains the source camera and ancestor placement", edit.LocalToScreen.TransformPoint(default).DistanceTo(new(-24, 87)) < 1e-7);
            edit.Finish(true);
        }
        check("unchanged inline default edit retains exact document and zero history", session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == before);
    }
    private static void Existing(Action<string, bool> check, SceneRenderer renderer)
    {
        var session = Source(true, out var text, out _); string before = JsonSerializer.Serialize(session.Document), camera = JsonSerializer.Serialize(session.Document.Canvas);
        var local = text.Transform; var mapping = FlatView(new(420, -50));
        using (var edit = new InlineTextEdit(session, text, default, text.Style, renderer, canvasMapping: () => mapping))
        {
            check("inline existing text frame uses displayed camera exactly once after ancestor placement", edit.LocalToScreen.TransformPoint(default).DistanceTo(new(471, -3)) < 1e-7 && text.Transform == local);
            var caret = edit.Geometry.Caret(2); var point = new PointD(471 + (caret.X + .1) * 2, -3 + (caret.Y + caret.Height / 2) * 2);
            edit.Pointer(point, true, true);
            check("inline displayed text pointer selects the intended shaped caret rather than source-camera coordinates", edit.Input.CaretIndex == 2 && edit.Contains(point) && !edit.Contains(new(-24, 87)));
            mapping = FlatView(new(620, 80));
            check("inline mapping factory follows the current display without rewriting source placement", edit.LocalToScreen.TransformPoint(default).DistanceTo(new(671, 127)) < 1e-7 && text.Transform == local);
            edit.Input.Text = "Mapped replacement 中"; edit.Finish(true);
        }
        check("mapped inline commit preserves source camera and local transform in one history entry", session.UndoCount == 1 && session.Document.Elements.Single().Transform == local && JsonSerializer.Serialize(session.Document.Canvas) == camera);
        check("mapped inline Undo restores the exact source document", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
    }
    private static void NewText(Action<string, bool> check, SceneRenderer renderer)
    {
        var session = Source(false, out _, out var scope); string before = JsonSerializer.Serialize(session.Document);
        var mapping = FlatView(new(420, -50));
        using (var edit = new InlineTextEdit(session, null, new(180, 160), new() { FontSize = 24 }, renderer, canvasMapping: () => mapping))
        {
            edit.Input.Text = "Root view 🧭";
            check("mapped inline creation stores group-local coordinates while its frame follows the displayed view", edit.Target.GroupId == scope && edit.LocalToScreen.TransformPoint(default).DistanceTo(new(600, 110)) < 1e-7 && edit.Target.Transform == new Affine2D(.5, 0, 0, .5, 72.5, 67.5));
            edit.Finish(true);
        }
        check("mapped inline creation commits as one source operation", session.UndoCount == 1 && new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single()).Transform.OffsetX == 180);
        check("mapped inline creation Undo restores the empty group and its exact source camera", session.Undo() && JsonSerializer.Serialize(session.Document) == before);
    }
    private static void ScreenDraft(Action<string, bool> check, SceneRenderer renderer)
    {
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600) }); var mapping = ProjectedView();
        using (var edit = new InlineTextEdit(session, null, new(180, 160), new() { FontSize = 24 }, renderer, billboardScreenFirst: true, canvasMapping: () => mapping))
        {
            edit.Input.Text = "Displayed billboard";
            check("screen-first mapped text remains private until its source placement is resolved", session.Document.Elements.Count == 0 && session.UndoCount == 0 && edit.HasScreenDraft);
            edit.Finish(true);
        }
        var text = session.Document.Elements.Single();
        check("screen-first mapped text resolves against the displayed perspective instead of the flat source camera", text.IsBillboard && mapping.ElementToScreen(text).TransformPoint(default).DistanceTo(new(180, 160)) < 1e-6 && session.UndoCount == 1);
        check("screen-first mapped text Undo removes the complete creation", session.Undo() && session.Document.Elements.Count == 0);
        using (var edit = new InlineTextEdit(session, null, new(180, 160), new() { FontSize = 24 }, renderer, billboardScreenFirst: true, canvasMapping: () => mapping))
        {
            edit.Input.Text = "Do not place into a different view"; mapping = FlatView(new(50, 15)); session.NotifyChanged();
            check("screen-first inline text rejects a changed displayed mapping without publishing history", !edit.IsActive && !session.IsTransactionActive && session.Document.Elements.Count == 0 && session.UndoCount == 0);
        }
    }
    private static void NativeCaret(Action<string, bool> check, SceneRenderer renderer)
    {
        var session = Source(true, out var text, out _); var controller = new EditorController(session) { ScreenOrigin = new(700, 800) };
        controller.SetCanvasView(FlatView(new(420, -50)), new(-1500, 900), new object());
        var owner = new Window { Width = 400, Height = 200, Left = -22000, Top = -22000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        _ = new WindowInteropHelper(owner).EnsureHandle();
        try
        {
            using var edit = new InlineTextEdit(session, text, default, text.Style, renderer, canvasMapping: () => controller.CurrentCanvasMapping);
            using var input = new CanvasTextInput(owner, edit, () => controller.CanvasOrigin);
            edit.Input.Select(2, 0);
            check("actual invisible IME carrier aligns its native caret with displayed text plus CanvasOrigin", CaretMatches(input, edit, controller.CanvasOrigin));
            controller.SetCanvasView(FlatView(new(620, 80)), new(-1420, 945), new object()); edit.Input.Select(3, 0);
            check("actual IME carrier follows the current view and origin while source ScreenOrigin remains unchanged", CaretMatches(input, edit, controller.CanvasOrigin) && controller.ScreenOrigin == new PointD(700, 800));
            var display = new SceneDocument { Viewport = new(0, 0, 800, 600) };
            display.Canvas.Grid.FlatTranslation = new(420, -50);
            controller.SetCanvasView(CanvasMapping.CreateViewing(display, new(.5, new(30, 20))), new(-1500, 900), new object()); edit.Input.Select(1, 0);
            check("actual IME native caret follows post-projection zoom and pan without author-data scaling", CaretMatches(input, edit, controller.CanvasOrigin) && text.Transform == new Affine2D(1, 0, 0, 1, 8, 11));
            edit.Finish(false);
        }
        finally { SessionWindowCoordinator.Close(owner); }
    }
    private static bool CaretMatches(CanvasTextInput input, InlineTextEdit edit, PointD origin)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        typeof(CanvasTextInput).GetMethod("UpdatePlacement", Hidden)!.Invoke(input, null);
        var window = (Window)typeof(CanvasTextInput).GetField("window", Hidden)!.GetValue(input)!;
        var native = edit.Input.GetRectFromCharacterIndex(edit.Input.CaretIndex); var dpi = VisualTreeHelper.GetDpi(window);
        var expected = edit.LocalToScreen.TransformPoint(new(edit.Caret.X, edit.Caret.Y)) + origin;
        var previous = SetThreadDpiAwarenessContext(-4); NativeRect rect;
        try { if (!GetWindowRect(new WindowInteropHelper(window).Handle, out rect)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        var inWindow = edit.Input.TranslatePoint(native.TopLeft, window);
        var actual = new PointD(rect.Left + inWindow.X * dpi.DpiScaleX, rect.Top + inWindow.Y * dpi.DpiScaleY);
        Console.WriteLine($"INLINE_VIEW_CARET expected={expected} actual={actual} dpi={dpi.DpiScaleX} nativeEmpty={native.IsEmpty}");
        return window.IsVisible && window.Opacity == 0 && !native.IsEmpty && actual.DistanceTo(expected) < 2;
    }
    private static string DesktopName() { var name = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return name.ToString(); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
}
