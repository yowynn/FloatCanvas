using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;

internal static class DepthOcclusionSettingsTests
{
    public static void Run(Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var session = new EditorSession();
        var appearance = new CanvasPreviewAppearance();
        var window = new CanvasOptionsTestWindow(CanvasPresetLibrary.OfficialOnly());
        window.Refresh(session.Document.Canvas); window.PrepareForShow();
        int requests = 0;
        window.DepthPriorityOcclusionChanged += value =>
        {
            requests++; appearance.SetDepth(value);
            window.Options.RefreshPreviewControls(value, 1);
        };
        var option = (CaptionModeSwitch)typeof(CanvasToolOptions).GetField("depthPriorityOcclusion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Options)!;
        void Click() => option.RaiseEvent(new Wpf.RoutedEventArgs(Primitives.ButtonBase.ClickEvent));
        check("flat canvas retains a disabled two-slot depth caption", option.Visibility == Wpf.Visibility.Visible && !option.IsEnabled && option.Width == 62);
        Click(); check("flat depth control does not request a preview change", requests == 0);
        session.Document.Canvas.Projection.Enabled = true; window.Refresh(session.Document.Canvas); window.PrepareForShow();
        string saved = JsonSerializer.Serialize(session.CreateCommittedSnapshot()); long revision = session.SceneRevision;
        Click();
        check("depth caption changes only the runtime preview", appearance.DepthPriorityOcclusion && !option.SelectedFirst && session.UndoCount == 0 && session.SceneRevision == revision);
        check("preview depth leaves both serialization and recovery unchanged", JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == saved && !session.Document.Canvas.DepthPriorityOcclusion);
        var canvas = appearance.Resolve(session.Document.Canvas);
        check("preview uses an independent canvas and reuses the same frozen input", canvas.DepthPriorityOcclusion && !ReferenceEquals(canvas, session.Document.Canvas) && ReferenceEquals(canvas, appearance.Resolve(session.Document.Canvas)));
        session.ExecuteSettings("other edit", doc => doc.Canvas.Grid.Width = 4); session.Undo();
        check("document undo does not change runtime depth preference", appearance.DepthPriorityOcclusion && JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == saved);
        session.Document.Canvas.Projection.Enabled = false; window.Refresh(session.Document.Canvas); window.PrepareForShow();
        check("flat mode dims the caption without losing its preview choice", !option.IsEnabled && !option.SelectedFirst);
        session.Document.Canvas.Projection.Enabled = true; window.Refresh(session.Document.Canvas); window.PrepareForShow();
        window.Options.RefreshPreviewControls(true, 1, false); Click();
        check("unavailable preview requests leave the confirmed switch state intact", requests == 1 && !option.SelectedFirst);
        window.Options.RefreshPreviewControls(true, 1); Click();
        check("runtime depth can return to layer order without history", !appearance.DepthPriorityOcclusion && option.SelectedFirst && session.UndoCount == 0);
        check("a new runtime starts with layer order regardless of legacy stored depth", !new CanvasPreviewAppearance().Resolve(new CanvasSettings { DepthPriorityOcclusion = true }).DepthPriorityOcclusion);
        window.CloseForDispose();
        ProductionPresetApplication(check);
    }
    private static void ProductionPresetApplication(Action<string, bool> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var viewport = new RectD(0, 0, 1200, 800);
        var session = new EditorSession(new() { Viewport = viewport });
        var editor = new EditorController(session);
        // Exercise the actual DesktopSession mutation entry without creating a host,
        // acquiring input or dispatching rendering against a user's desktop.
        var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
        void Set(object target, string field, object value) => CanvasOptionsTestWindow.Unwrap(target).GetType().GetField(field, flags)!.SetValue(target, value);
        Set(host, "_threadId", GetCurrentThreadId());
        Set(host, "_monitors", new List<MonitorInfo> { new("test", new(0, 0, 1200, 800), new(0, 0, 1200, 800), 96, 96, true) });
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        Set(desktop, "_host", host); Set(desktop, "_editor", editor);
        Set(desktop, "_canvasCalibration", new CanvasCalibrationController());
        Set(desktop, "_canvasReferenceLines", new List<CanvasReferenceLine>());
        Set(desktop, "_canvasGridScale", new CanvasGridScaleController());
        Set(desktop, "_pixelDirtyRegions", new List<PixelBounds>());
        Set(desktop, "_disposed", true); // Skip render dispatch only; real preset application and history still run.
        var apply = typeof(DesktopSession).GetMethod("ApplyCanvasPreset", flags)!;
        var preset = new CanvasPresetParameters { ProjectionEnabled = true, Columns = 5, Rows = 7, FlatCellSize = 73 };
        session.Document.Canvas.DepthPriorityOcclusion = true;
        session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(viewport);
        var currentProjection = session.Document.Canvas.Projection;
        var corners = currentProjection.Quad.Select(point => point + new PointD(110, 45)).ToArray();
        check("production preset fixture has a distinct calibrated starting canvas", CanvasCalibration.TryCalibrate(currentProjection, corners, viewport, out var tilted, out _));
        session.Document.Canvas.Projection = tilted;
        var expected = CanvasPresetLibrary.Apply(preset, session.Document.Canvas, viewport, viewport);
        apply.Invoke(desktop, [preset]);
        check("production ApplyCanvasPreset resets missing depth and calibration in one undo step",
            !session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 1 &&
            JsonSerializer.Serialize(session.Document.Canvas.Projection) == JsonSerializer.Serialize(expected.Projection));
        session.Undo();
        check("production canvas preset undo restores the prior depth choice and calibrated corners",
            session.Document.Canvas.DepthPriorityOcclusion && JsonSerializer.Serialize(session.Document.Canvas.Projection.Quad) == JsonSerializer.Serialize(corners));
        session.Redo();
        check("production canvas preset redo restores the default depth and reference geometry",
            !session.Document.Canvas.DepthPriorityOcclusion &&
            JsonSerializer.Serialize(session.Document.Canvas.Projection) == JsonSerializer.Serialize(expected.Projection));
        session.Undo();
        preset.DepthPriorityOcclusion = true; session.Document.Canvas.DepthPriorityOcclusion = false;
        apply.Invoke(desktop, [preset]);
        check("production ApplyCanvasPreset restores a saved enabled depth choice", session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 1);
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
}
