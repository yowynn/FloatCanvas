using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Production four-point commands with hidden overlay HWNDs and offscreen, nonactivating panels.</summary>
internal static class CanvasAnchorCaptureTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string originalProfile = ApplicationPaths.ProfileDirectory, originalLanguage = L.Preference;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-canvas-anchors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Theme = "light", Language = "zh-CN" }.Save();
        try
        {
            LayersStayHidden(app, check);
            Completion(app, check);
            Cancellation(app, check);
            Lifetime(app, check);
            PinnedResume(app, check);
            FlatGridPointer(app, check);
        }
        finally
        {
            var restore = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) restore.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(restore.ToArray()); L.Initialize(originalLanguage);
            Directory.Delete(profile, recursive: true);
        }
    }

    private static void Completion(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var canvas = fixture.Editor.Session.Document.Canvas;
        var initial = canvas.Projection.Clone();
        int undo = fixture.Editor.Session.UndoCount;
        var points = CanvasCalibration.CreateInitial(fixture.Editor.Session.Document.Viewport, 8, 6).Quad;
        fixture.Begin();
        check("four-point capture hides every previously visible panel and keeps the toolbar", fixture.Calibration.IsPlacing &&
            !fixture.Panel.IsVisible && !fixture.Secondary.IsVisible && fixture.Toolbar.IsVisible);
        check("four-point capture keeps previously hidden panels hidden", !fixture.Hidden.IsVisible);
        check("temporarily hiding the Canvas panel preserves its pinned state", fixture.Panel.IsPinned);
        foreach (int index in new[] { 2, 0, 3 })
        {
            fixture.Pointer(OverlayPointerKind.Down, points[index]); fixture.Pointer(OverlayPointerKind.Up, points[index]);
        }
        check("first three anchor points leave the original projection and history unchanged", fixture.Calibration.IsPlacing &&
            fixture.Editor.Session.UndoCount == undo && canvas.Projection.Quad.SequenceEqual(initial.Quad));
        fixture.Pointer(OverlayPointerKind.Down, points[1]);
        check("fourth anchor press does not restore windows before the pointer is released", !fixture.Panel.IsVisible && !fixture.Secondary.IsVisible);
        fixture.Pointer(OverlayPointerKind.Up, points[1]);
        check("fourth anchor release restores the exact visible panel set", !fixture.Calibration.IsPlacing && fixture.Calibration.IsActive &&
            fixture.Panel.IsVisible && fixture.Secondary.IsVisible && !fixture.Hidden.IsVisible && fixture.Toolbar.IsVisible);
        check("four-point capture creates one complete projection history step", fixture.Editor.Session.UndoCount == undo + 1 &&
            fixture.Editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(points));
        fixture.Editor.Session.Undo();
        check("one undo restores the complete original anchor projection", fixture.Editor.Session.UndoCount == undo &&
            fixture.Editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(initial.Quad));
        fixture.AssertNoDesktopInput(check);
    }

    private static void LayersStayHidden(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var layers = fixture.AttachLayers();
        bool Wanted() => Field<bool>(fixture.Session, "_layersWanted");
        var points = CanvasCalibration.CreateInitial(fixture.Editor.Session.Document.Viewport, 8, 6).Quad;
        check("real layer panel begins visible with its production wanted flag", layers.IsVisible && Wanted() && fixture.Panel.IsVisible);
        fixture.Begin();
        check("anchor capture initially hides the actual layer and Canvas panels", !layers.IsVisible && !fixture.Panel.IsVisible);
        for (int stage = 0; stage < 3; stage++)
        {
            // WM_ACTIVATE/InputActivated restores wanted windows through this production
            // entry before its pointer callback. Replay that boundary without native input.
            fixture.Invoke("ShowEditingWindows");
            fixture.Pointer(OverlayPointerKind.Down, points[stage]); Pump();
            check($"anchor point {stage + 1} press keeps real wanted layer and Canvas panels hidden", !layers.IsVisible && !fixture.Panel.IsVisible);
            fixture.Invoke("ShowEditingWindows");
            fixture.Pointer(OverlayPointerKind.Up, points[stage]); Pump();
            check($"anchor point {stage + 1} release keeps real wanted layer and Canvas panels hidden", !layers.IsVisible && !fixture.Panel.IsVisible);
        }
        fixture.Pointer(OverlayPointerKind.Down, points[3]);
        fixture.Invoke("ShowEditingWindows"); Pump();
        check("activation after the fourth press still cannot restore panels before release", !fixture.Calibration.IsPlacing &&
            !layers.IsVisible && !fixture.Panel.IsVisible);
        fixture.Pointer(OverlayPointerKind.Up, points[3]); Pump();
        check("fourth release restores the real layer and Canvas panels without changing wanted or pin state", layers.IsVisible &&
            fixture.Panel.IsVisible && Wanted() && fixture.Panel.IsPinned);
        fixture.Begin(); fixture.Invoke("ShowEditingWindows");
        fixture.Pointer(OverlayPointerKind.Down, points[0]); fixture.Pointer(OverlayPointerKind.Up, points[0]); Pump();
        fixture.Invoke("OnKey", 0x1B); Pump();
        check("Escape restores real wanted layers after a partial anchor capture", layers.IsVisible && fixture.Panel.IsVisible &&
            Wanted() && fixture.Panel.IsPinned && !fixture.Calibration.IsPlacing);
        fixture.AssertNoDesktopInput(check);
    }

    private static void Cancellation(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var before = fixture.Editor.Session.Document.Canvas.Projection.Clone();
        int undo = fixture.Editor.Session.UndoCount;
        fixture.Begin();
        fixture.Pointer(OverlayPointerKind.Down, new(140, 90)); fixture.Pointer(OverlayPointerKind.Up, new(140, 90));
        fixture.Invoke("OnKey", 0x1B);
        check("Escape cancels four-point capture and restores the panels in one command", !fixture.Calibration.IsPlacing &&
            fixture.Panel.IsVisible && fixture.Secondary.IsVisible && !fixture.Hidden.IsVisible && fixture.Editor.Tool == EditorTool.Canvas);
        check("cancelled anchor placement does not change projection or history", fixture.Editor.Session.UndoCount == undo &&
            fixture.Editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(before.Quad));
        fixture.Begin(); fixture.Invoke("BeginCanvasFourPoints");
        fixture.Invoke("OnKey", 0x1B);
        check("restarting an active capture does not lose its original window snapshot", fixture.Panel.IsVisible && fixture.Secondary.IsVisible && !fixture.Hidden.IsVisible);
        fixture.Begin();
        int previousToolPanelShows = 0;
        fixture.Panel.IsVisibleChanged += (_, _) => { if (fixture.Panel.IsVisible) previousToolPanelShows++; };
        fixture.Invoke("ActivateTool", EditorTool.Rectangle);
        check("switching tools cancels anchor placement, restores other panels and closes the previous tool panel", !fixture.Calibration.IsPlacing &&
            fixture.Editor.Tool == EditorTool.Rectangle && !fixture.Panel.IsVisible && fixture.Secondary.IsVisible);
        check("switching tools never briefly shows the previous Canvas panel", previousToolPanelShows == 0);
        fixture.AssertNoDesktopInput(check);
    }

    private static void Lifetime(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        fixture.Begin(); fixture.Secondary.Close(); fixture.Invoke("OnKey", 0x1B);
        check("a panel closed during capture is not resurrected on cancellation", fixture.Panel.IsVisible && !fixture.App.Windows.Cast<Window>().Contains(fixture.Secondary));
        fixture.Begin(); fixture.Invoke("SetEditing", false, false); Pump();
        check("exiting editing abandons the snapshot without resurrecting its panels", !fixture.Host.IsEditing && !fixture.Panel.IsVisible &&
            !fixture.Hidden.IsVisible && !fixture.Calibration.IsPlacing);
        fixture.AssertNoDesktopInput(check);
    }

    private static void FlatGridPointer(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        fixture.Invoke("ActivateTool", EditorTool.Canvas);
        fixture.Panel.Hide(); fixture.Secondary.Hide();
        var document = fixture.Editor.Session.Document;
        document.Canvas.Projection.Enabled = false; document.Canvas.Grid.Visible = true;
        fixture.Invoke("SynchronizeCanvasTool");
        var point = document.Canvas.Grid.FlatOrigin ?? document.Viewport.Center;
        double before = CanvasGridGeometry.ResolveCellSize(document.Canvas, document.Viewport);
        fixture.Pointer(OverlayPointerKind.Down, point);
        var scale = Field<CanvasGridScaleController>(fixture.Session, "_canvasGridScale");
        check("real Canvas pointer route starts flat scaling at an unobstructed rendered grid intersection", scale.IsDragging);
        fixture.Pointer(OverlayPointerKind.Move, point + new PointD(70, 20)); fixture.Pointer(OverlayPointerKind.Up, point + new PointD(70, 20));
        check("flat grid drag through the production route changes spacing once", !scale.IsDragging &&
            CanvasGridGeometry.ResolveCellSize(document.Canvas, document.Viewport) != before && fixture.Editor.Session.UndoCount == 1);
        fixture.AssertNoDesktopInput(check);
    }

    private static void PinnedResume(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var coordinator = Field<SessionWindowCoordinator>(fixture.Session, "_windowCoordinator");
        coordinator.Register(fixture.Secondary, () => false);
        fixture.Begin(); fixture.Invoke("SetEditing", false, false); Pump();
        check("mode exit during capture leaves both pinned and unpinned panels hidden", !fixture.Panel.IsVisible && !fixture.Secondary.IsVisible);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(fixture.Host, true);
        coordinator.ResumeEditingWindows();
        check("the next editing session resumes only previously visible pinned panels", fixture.Panel.IsVisible &&
            !fixture.Secondary.IsVisible && !fixture.Hidden.IsVisible);
        fixture.AssertNoDesktopInput(check);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Application App;
        internal readonly OverlayHost Host;
        internal readonly DesktopSession Session;
        internal readonly EditorController Editor;
        internal readonly CanvasCalibrationController Calibration;
        internal readonly Window Toolbar;
        internal readonly CanvasSettingsWindow Panel;
        internal readonly Window Secondary, Hidden;

        internal Fixture(Application app)
        {
            App = app;
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = bounds, WorkArea = bounds }];
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            Session = new DesktopSession(app, Host);
            Field<System.Windows.Forms.NotifyIcon>(Session, "_tray").Visible = false;
            Editor = Field<EditorController>(Session, "_editor");
            Calibration = Field<CanvasCalibrationController>(Session, "_canvasCalibration");
            Toolbar = Field<Window>(Session, "_toolbar"); Offscreen(Toolbar); Toolbar.Show();
            Panel = new CanvasSettingsWindow { Owner = Toolbar };
            Field<System.Windows.Controls.Button>(Panel, "pin").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Secondary = new Window { Owner = Toolbar, Width = 150, Height = 100 };
            Hidden = new Window { Owner = Toolbar, Width = 150, Height = 100 };
            Offscreen(Panel); Offscreen(Secondary); Offscreen(Hidden);
            typeof(DesktopSession).GetField("_canvasSettingsWindow", Private)!.SetValue(Session, Panel);
            var coordinator = Field<SessionWindowCoordinator>(Session, "_windowCoordinator");
            coordinator.Register(Panel, () => Panel.IsPinned);
            coordinator.Register(Secondary, () => true);
            coordinator.Register(Hidden, () => true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(Host, true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(Host, true);
            Editor.Session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(Editor.Session.Document.Viewport);
            Panel.Show(); Secondary.Show();
        }

        internal void Begin() => Invoke("BeginCanvasFourPoints");
        internal LayersWindow AttachLayers()
        {
            var layers = new LayersWindow(() => Editor.Session, () => true) { Owner = Toolbar };
            Offscreen(layers);
            typeof(DesktopSession).GetField("_layers", Private)!.SetValue(Session, layers);
            typeof(DesktopSession).GetField("_layersWanted", Private)!.SetValue(Session, true);
            layers.Dismissed += () => typeof(DesktopSession).GetField("_layersWanted", Private)!.SetValue(Session, false);
            Field<SessionWindowCoordinator>(Session, "_windowCoordinator").Register(layers, () => true);
            layers.Show();
            return layers;
        }
        internal object? Invoke(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(Session, arguments);
        internal void Pointer(OverlayPointerKind kind, PointD point) => Invoke("HandleCanvasPointer", new OverlayPointerEvent(kind,
            new(point.X + Editor.ScreenOrigin.X, point.Y + Editor.ScreenOrigin.Y), OverlayMouseButton.Left, OverlayModifiers.None));

        internal void AssertNoDesktopInput(Action<string, bool> check) => check("anchor fixture keeps overlay HWNDs hidden and never acquires desktop input",
            Host.Diagnostics.All(window => !IsWindowVisible(window.InputHandle) && !IsWindowVisible(window.DisplayHandle)) &&
            !Host.IsKeyboardIsolationActive);

        public void Dispose()
        {
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(Host, false);
            Session.Dispose();
            Panel.CloseForDispose();
            foreach (var window in new[] { Secondary, Hidden })
                if (App.Windows.Cast<Window>().Contains(window)) window.Close();
            Pump();
        }

        private static void Offscreen(Window window)
        {
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Opacity = 0;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = desktop.Left - 10000; window.Top = desktop.Top - 10000;
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
