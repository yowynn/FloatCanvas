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

    internal static void Run(Action<string, bool> check, bool geometryOnly = false)
    {
        var previousContext = SynchronizationContext.Current;
        try { RunFixture(check, geometryOnly); }
        finally
        {
            var fixtureContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            Console.WriteLine($"CANVAS_SYNC_CONTEXT caller={previousContext?.GetType().Name ?? "none"} fixture={fixtureContext?.GetType().Name ?? "none"} restored={SynchronizationContext.Current?.GetType().Name ?? "none"}");
        }
        check("canvas anchor fixture restores the caller's synchronization context before subsequent async tests", ReferenceEquals(previousContext, SynchronizationContext.Current));
    }

    private static void RunFixture(Action<string, bool> check, bool geometryOnly)
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
            if (geometryOnly) { GeometryLifetimes(app, check); SamplingWorkflow(app, check); return; }
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

    private static void SamplingWorkflow(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app, offscreenMonitor: true);
        var session = fixture.Editor.Session;
        session.Document.Canvas.Projection.Enabled = true; CanvasProjectionGeometry.InitializePlane(session.Document.Canvas);
        int undo = session.UndoCount;
        fixture.Invoke("BeginCanvasFourLines");
        check("four-line capture hides previously visible panels without altering history", !fixture.Panel.IsVisible && !fixture.Secondary.IsVisible && session.UndoCount == undo);
        PointD[][] segments = [[new(200, 200), new(500, 200)], [new(200, 500), new(500, 500)], [new(200, 200), new(200, 500)], [new(500, 200), new(500, 500)]];
        foreach (var pair in segments) { fixture.Pointer(OverlayPointerKind.Down, pair[0]); fixture.Pointer(OverlayPointerKind.Move, pair[1]); fixture.Pointer(OverlayPointerKind.Up, pair[1]); }
        check("four-line completion waits for explicit confirmation without an undo entry", Field<List<CanvasReferenceLine>>(fixture.Session, "_canvasReferenceLines").Count == 4 && session.UndoCount == undo);
        using (var raster = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(800, 600)))
        {
            raster.Canvas.Clear(SkiaSharp.SKColors.Transparent);
            fixture.Invoke("DrawCanvasGridCalibration", raster.Canvas);
            using var image = raster.Snapshot(); using var pixels = SkiaSharp.SKBitmap.FromImage(image);
            check("production reference-line overlay renders the sampled frame", pixels.GetPixel(200, 350).Alpha > 0);
        }
        fixture.Invoke("OnKey", 0x08);
        check("Backspace removes only the last reference line", Field<List<CanvasReferenceLine>>(fixture.Session, "_canvasReferenceLines").Count == 3 && session.UndoCount == undo);
        fixture.Pointer(OverlayPointerKind.Down, segments[3][0]); fixture.Pointer(OverlayPointerKind.Up, segments[3][1]);
        fixture.Invoke("OnKey", 0x0D); Pump();
        check("confirmed four-line frame creates one undo and restores previous panels", session.UndoCount == undo + 1 && fixture.Panel.IsVisible && fixture.Secondary.IsVisible && !fixture.Hidden.IsVisible);
        check("four-line result maps the requested frame corners", session.Document.Canvas.Projection.Quad[0].DistanceTo(new(200, 200)) < 1e-6 && session.Document.Canvas.Projection.Quad[2].DistanceTo(new(500, 500)) < 1e-6);
        session.Document.Canvas.Grid.Visible = false;
        session.Document.Canvas.Grid.Color = 0xFFFF0000;
        typeof(DesktopSession).GetField("_canvasGuideOpacity", Private)!.SetValue(fixture.Session, 0d);
        fixture.Invoke("BeginCanvasGridCalibration");
        fixture.Pointer(OverlayPointerKind.Down, new(230, 230)); fixture.Pointer(OverlayPointerKind.Move, new(350, 380));
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Field<CanvasGridCalibrationWindow>(fixture.Session, "_canvasGridCalibrationWindow");
            var fields = Descendants(dialog).OfType<NumericValueField>().ToArray();
            fields[0].Input.Text = "4"; fields[1].Input.Text = "5";
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            WaitForSafeCandidate(dialog);
            var preview = Field<CanvasSettings>(fixture.Session, "_canvasGridCalibrationPreview");
            check("rectangle popup suppresses the custom red grid without changing original visibility or history", !preview.Grid.Visible && !session.Document.Canvas.Grid.Visible && session.UndoCount == undo + 1);
            var context = (CanvasPreviewContext)fixture.Invoke("CurrentPreviewContext")!;
            check("sample preview forces opaque reference guides despite zero user opacity", context.Calibration && context.GuideOpacity == 1 && context.OpaqueCalibrationGuides);
            using (var renderer = new FloatCanvas.Rendering.SceneRenderer())
            using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(64,64)))
            {
                var sample = new SceneDocument { Viewport = new(0,0,64,64), Canvas = preview.Clone() };
                sample.Canvas.Projection.Enabled = false;
                sample.Canvas.Grid.FlatOrigin = new(16.5,16.5); sample.Canvas.Grid.FlatCellSize = 16;
                surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
                typeof(FloatCanvas.Rendering.SceneRenderer).GetMethod("DrawCalibrationGuides", Private)!.Invoke(renderer,
                    [surface.Canvas, sample, context.GuideOpacity, context.OpaqueCalibrationGuides]);
                using var image = surface.Snapshot(); using var bitmap = SkiaSharp.SKBitmap.FromImage(image);
                var pixels = bitmap.Pixels;
                check("sample reference pixels contain opaque black and white without custom red", pixels.Any(p => p.Alpha == 255 && p.Red == 0) &&
                    pixels.Any(p => p.Alpha == 255 && p.Red == 255) && pixels.All(p => p.Red == p.Green && p.Green == p.Blue));
            }
            fields[0].Input.Text = "bad"; Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            check("invalid typed counts clear the stale preview immediately", typeof(DesktopSession).GetField("_canvasGridCalibrationPreview", Private)!.GetValue(fixture.Session) is null);
            check("clearing sample preview restores the user's reference opacity", ((CanvasPreviewContext)fixture.Invoke("CurrentPreviewContext")!).GuideOpacity == 0);
            fields[0].Input.Text = "4";
            Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(b => Equals(b.Content, L.Get("Canvas.AdjustGridSample"))).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }));
        fixture.Pointer(OverlayPointerKind.Up, new(350, 380)); Pump();
        check("returning to adjust preserves counts and keeps auxiliary windows hidden", Field<CanvasGridCalibrationDraft>(fixture.Session, "_canvasGridDraft").Columns == "4" && !fixture.Panel.IsVisible && session.UndoCount == undo + 1);
        fixture.Pointer(OverlayPointerKind.Down, new(350, 380)); fixture.Pointer(OverlayPointerKind.Up, new(360, 390));
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Field<CanvasGridCalibrationWindow>(fixture.Session, "_canvasGridCalibrationWindow");
            check("Escape from sample adjustment returns to the confirmation dialog with retained counts", Field<CanvasGridCalibrationDraft>(fixture.Session, "_canvasGridDraft").Columns == "4");
            WaitForSafeCandidate(dialog);
            Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }));
        fixture.Invoke("OnKey", 0x1B); Pump();
        check("sample application creates one undo while preserving hidden grid setting and restoring panels", session.UndoCount == undo + 2 && !session.Document.Canvas.Grid.Visible && fixture.Panel.IsVisible);
        check("sample application preserves authored red and reference opacity", session.Document.Canvas.Grid.Color == 0xFFFF0000 && Field<double>(fixture.Session,"_canvasGuideOpacity") == 0);
        check("calibration undo and redo remain single steps", session.Undo() && session.Redo());
        fixture.Panel.Show(); fixture.Secondary.Show();
        fixture.Invoke("BeginCanvasGridCalibration"); fixture.Editor.Tool = EditorTool.Select; fixture.Invoke("SynchronizeCanvasTool");
        Console.WriteLine($"SAMPLE_TOOL_EXIT active={typeof(DesktopSession).GetField("_canvasGridCalibrationSession", Private)!.GetValue(fixture.Session) is not null} panel={fixture.Panel.IsVisible} secondary={fixture.Secondary.IsVisible} tool={fixture.Editor.Tool}");
        check("switching away from Canvas cancels sampling and restores previous panels", typeof(DesktopSession).GetField("_canvasGridCalibrationSession", Private)!.GetValue(fixture.Session) is null && fixture.Panel.IsVisible);
        fixture.AssertNoDesktopInput(check);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void WaitForSafeCandidate(CanvasGridCalibrationWindow dialog)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (dialog.State == CanvasGridCalibrationWindow.CandidateState.Checking && deadline.Elapsed.TotalSeconds < 10)
        { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(1); }
        if (dialog.State != CanvasGridCalibrationWindow.CandidateState.Safe) throw new InvalidOperationException("Expected a safety-checked calibration candidate: " + dialog.State);
    }

    private static void GeometryLifetimes(Application app, Action<string, bool> check)
    {
        var foreground = GetForegroundWindow();
        using var fixture = new Fixture(app, offscreenMonitor: true);
        var target = fixture.Editor.Session;
        target.Document.Canvas.Projection.Enabled = true; CanvasProjectionGeometry.InitializePlane(target.Document.Canvas);
        fixture.Editor.Tool = EditorTool.Canvas;
        var before = System.Text.Json.JsonSerializer.Serialize(target.Document); int undo = target.UndoCount;
        void SetField(string name, object? value) => typeof(DesktopSession).GetField(name, Private)!.SetValue(fixture.Session, value);
        var hiddenRequest = new SnapshotCaptureRequest(target, fixture.Host.EditingSessionVersion); SetField("_canvasRectificationRequest", hiddenRequest);
        fixture.Invoke("ToggleCanvasVisibility");
        check("production hide route irreversibly cancels rectification even while editing remains enabled", hiddenRequest.Cancelled && !fixture.Host.IsVisible);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(fixture.Host, true);
        check("showing the same canvas cannot revive the old rectification request", !hiddenRequest.MatchesContent(target) && hiddenRequest.Cancelled);
        SetField("_canvasRectificationRequest", null);
        var modeRequest = new SnapshotCaptureRequest(target, fixture.Host.EditingSessionVersion); SetField("_canvasRectificationRequest", modeRequest);
        fixture.Invoke("SetEditing", false, false);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(fixture.Host, true);
        check("leaving and re-entering editing cannot revive a pending rectification result", modeRequest.Cancelled && !modeRequest.MatchesContent(target));
        SetField("_canvasRectificationRequest", null);
        fixture.Invoke("BeginCanvasGridCalibration");
        check("known-cell calibration starts with the exact editor session and no document mutation", ReferenceEquals(Field<EditorSession>(fixture.Session, "_canvasGridCalibrationSession"), target) && target.UndoCount == undo);
        // This fixture sends plain Escape. Never borrow Alt/Ctrl state left by
        // an earlier HWND/input fixture; restore the exact thread state afterward.
        using (new ThreadModifierScope(System.Windows.Input.ModifierKeys.None)) fixture.Invoke("OnKey", 0x1B);
        if (typeof(DesktopSession).GetField("_canvasGridCalibrationSession", Private)!.GetValue(fixture.Session) is not null)
            Console.WriteLine($"CALIBRATION_ESCAPE modal={SessionKeyboardRouter.IsModal} modifiers={OverlayHost.CurrentModifiers} focus={System.Windows.Input.Keyboard.FocusedElement} focusVisible={(System.Windows.Input.Keyboard.FocusedElement as UIElement)?.IsVisible}");
        check("Escape clears known-cell state without a history entry", typeof(DesktopSession).GetField("_canvasGridCalibrationSession", Private)!.GetValue(fixture.Session) is null && target.UndoCount == undo);
        fixture.Invoke("BeginCanvasGridCalibration");
        var cancelledTarget = Field<CanvasSettingsTarget>(fixture.Session, "_canvasGridCalibrationTarget");
        fixture.Invoke("EndCanvasCalibration", false, true);
        fixture.Invoke("FinishCanvasGridCalibration", cancelledTarget);
        check("late known-cell completion after cancellation never opens a dialog or commits a fit", typeof(DesktopSession).GetField("_canvasGridCalibrationSession", Private)!.GetValue(fixture.Session) is null && target.UndoCount == undo);
        fixture.Invoke("BeginCanvasGridCalibration");
        var staleTarget = Field<CanvasSettingsTarget>(fixture.Session, "_canvasGridCalibrationTarget");
        fixture.Invoke("EndCanvasCalibration", false, true);
        fixture.Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        fixture.Invoke("BeginCanvasGridCalibration");
        var freshTarget = Field<CanvasSettingsTarget>(fixture.Session, "_canvasGridCalibrationTarget");
        long revision = target.SceneRevision;
        fixture.Invoke("FinishCanvasGridCalibration", staleTarget);
        check("a queued calibration A cannot borrow a fresh same-session B target after a view-mode change", !ReferenceEquals(staleTarget, freshTarget) && ReferenceEquals(Field<CanvasSettingsTarget>(fixture.Session, "_canvasGridCalibrationTarget"), freshTarget) && freshTarget.IsCurrent && target.SceneRevision == revision && target.UndoCount == undo);
        fixture.Invoke("EndCanvasCalibration", false, true);
        fixture.Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        check("cancellation paths preserve the complete document and history", System.Text.Json.JsonSerializer.Serialize(target.Document) == before && target.UndoCount == undo);
        foreach (var root in new[] { RootCanvasKind.Foreground, RootCanvasKind.Background })
        {
            target.SetRootCanvas(root);
            target.ChangeEditingSubprojectCanvas("prepare calibration canvas", header => header.Canvas.Projection = target.Document.Canvas.Projection.Clone());
        }
        fixture.Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary); target.SetRootCanvas(RootCanvasKind.Foreground);
        fixture.Invoke("BeginCanvasGridCalibration");
        var oldRootTarget = Field<CanvasSettingsTarget>(fixture.Session, "_canvasGridCalibrationTarget");
        fixture.Invoke("EndCanvasCalibration", false, true); target.SetRootCanvas(RootCanvasKind.Background);
        fixture.Invoke("BeginCanvasGridCalibration");
        var newRootTarget = Field<CanvasSettingsTarget>(fixture.Session, "_canvasGridCalibrationTarget");
        revision = target.SceneRevision; undo = target.UndoCount;
        fixture.Invoke("FinishCanvasGridCalibration", oldRootTarget);
        check("a queued foreground calibration cannot overwrite or cancel a new background calibration at the same content revision", ReferenceEquals(Field<CanvasSettingsTarget>(fixture.Session, "_canvasGridCalibrationTarget"), newRootTarget) && newRootTarget.IsCurrent && !oldRootTarget.IsCurrent && target.SceneRevision == revision && target.UndoCount == undo);
        fixture.Invoke("EndCanvasCalibration", false, true); target.SetRootCanvas(RootCanvasKind.Main); fixture.Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        fixture.Host.SetPreviewVisible(false);
        check("geometry lifecycle fixture never leaves a native input HWND visible", fixture.Host.Diagnostics.All(w => !IsWindowVisible(w.InputHandle)));
        check("geometry lifecycle fixture preserves the actual foreground window", GetForegroundWindow() == foreground);
        check("geometry lifecycle fixture stays outside all physical displays and never isolates the keyboard", fixture.Host.Diagnostics.All(w =>
            w.Bounds.Right < -10000 && w.Bounds.Bottom < -10000) && !fixture.Host.IsKeyboardIsolationActive);
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
        check("temporarily hiding the Canvas panel preserves its pinned state", fixture.Pinned);
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
            fixture.Panel.IsVisible && Wanted() && fixture.Pinned);
        fixture.Begin(); fixture.Invoke("ShowEditingWindows");
        fixture.Pointer(OverlayPointerKind.Down, points[0]); fixture.Pointer(OverlayPointerKind.Up, points[0]); Pump();
        fixture.Invoke("OnKey", 0x1B); Pump();
        check("Escape restores real wanted layers after a partial anchor capture", layers.IsVisible && fixture.Panel.IsVisible &&
            Wanted() && fixture.Pinned && !fixture.Calibration.IsPlacing);
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
        fixture.Panel.IsVisibleChanged += (_, _) => { if (fixture.Panel.IsVisible && fixture.Canvas.IsVisible) previousToolPanelShows++; };
        fixture.Invoke("ActivateTool", EditorTool.Rectangle);
        check("switching tools cancels anchor placement, restores other panels and closes the previous tool panel", !fixture.Calibration.IsPlacing &&
            fixture.Editor.Tool == EditorTool.Rectangle && !fixture.Canvas.IsVisible && fixture.Panel.IsVisible && fixture.Secondary.IsVisible);
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
        check("real Canvas pointer route starts flat grid panning at an unobstructed rendered grid intersection", scale.IsDragging);
        fixture.Pointer(OverlayPointerKind.Move, point + new PointD(70, 20)); fixture.Pointer(OverlayPointerKind.Up, point + new PointD(70, 20));
        check("flat grid drag through the production route pans the origin once without changing spacing", !scale.IsDragging &&
            document.Canvas.Grid.FlatOrigin == point + new PointD(70, 20) &&
            CanvasGridGeometry.ResolveCellSize(document.Canvas, document.Viewport) == before && fixture.Editor.Session.UndoCount == 1);
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
        internal readonly Window Panel;
        internal readonly CanvasToolOptions Canvas;
        internal bool Pinned => Field<bool>(Session, "_toolOptionsPinned");
        internal readonly Window Secondary, Hidden;

        internal Fixture(Application app, bool offscreenMonitor = false)
        {
            App = app;
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = offscreenMonitor ? new PixelBounds(-18000, -18000, 640, 360) : new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = bounds, WorkArea = bounds }];
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            Session = new DesktopSession(app, Host);
            Field<System.Windows.Forms.NotifyIcon>(Session, "_tray").Visible = false;
            Editor = Field<EditorController>(Session, "_editor");
            Calibration = Field<CanvasCalibrationController>(Session, "_canvasCalibration");
            Toolbar = Field<Window>(Session, "_toolbar"); Offscreen(Toolbar); Toolbar.Show();
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(Host, true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(Host, true);
            Editor.Session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(Editor.Session.Document.Viewport);
            Invoke("ActivateTool", EditorTool.Canvas);
            Invoke("OpenCanvasSettings");
            Panel = Field<Window>(Session, "_toolOptionsWindow");
            Canvas = Field<CanvasToolOptions>(Session, "_canvasToolOptions");
            Field<System.Windows.Controls.Button>(Session, "_toolOptionsPin").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Secondary = new Window { Owner = Toolbar, Width = 150, Height = 100 };
            Hidden = new Window { Owner = Toolbar, Width = 150, Height = 100 };
            Offscreen(Panel); Offscreen(Secondary); Offscreen(Hidden);
            var coordinator = Field<SessionWindowCoordinator>(Session, "_windowCoordinator");
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
            if (App.Windows.Cast<Window>().Contains(Panel)) Panel.Close();
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

    private static T Field<T>(object target, string name)
    {
        if (target is Window { Content: ToolOptionsPanel panel }) target = typeof(ToolOptionsPanel).GetField("canvasContent", Private)!.GetValue(panel)!;
        return (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
