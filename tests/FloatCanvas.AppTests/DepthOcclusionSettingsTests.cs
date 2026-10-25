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
        bool allowed = true;
        var session = new EditorSession();
        var editor = new EditorController(session);
        var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        window.Refresh(session.Document.Canvas, false);
        window.DepthPriorityOcclusionChanged += value =>
        {
            DesktopSession.ApplyCanvasDepthPriorityOcclusion(session, window.DisplayedSettings, value, allowed,
                change => session.Execute(L.Get("Canvas.Title"), document => change(document.Canvas)));
            window.Refresh(session.Document.Canvas, false);
        };
        var layers = new LayersWindow(() => session);
        check("Layers has no canvas depth-priority option", !Descendants(layers).OfType<Controls.CheckBox>().Any(box =>
            Equals(box.Content, L.Get("Canvas.DepthPriorityOcclusion"))));
        var option = (Controls.CheckBox)typeof(CanvasSettingsWindow).GetField("depthPriorityOcclusion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Click(bool value) { option.IsChecked = value; option.RaiseEvent(new Wpf.RoutedEventArgs(Primitives.ButtonBase.ClickEvent)); }
        string Fingerprint() => (string)typeof(DesktopSession).GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [session.Document])!;
        check("depth occlusion defaults off and its option is hidden on a flat canvas", !session.Document.Canvas.DepthPriorityOcclusion && option.Visibility == Wpf.Visibility.Collapsed);
        Click(true);
        check("hidden flat-canvas option cannot activate depth occlusion", !session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 0 && option.IsChecked == false);
        session.Document.Canvas.Projection.Enabled = true; window.Refresh(session.Document.Canvas, false);
        check("perspective canvas exposes localized depth option in Canvas settings", option.Visibility == Wpf.Visibility.Visible && Wpf.Automation.AutomationProperties.GetName(option) == L.Get("Canvas.DepthPriorityOcclusion") && ((Controls.Grid)option.Parent).Children.OfType<Controls.TextBlock>().Any(label => label.Text == L.Get("Canvas.DepthPriorityOcclusion")) && option.IsChecked == false);
        var savedFingerprint = Fingerprint(); long beforeRevision = session.SceneRevision; int notifications = 0;
        editor.Changed += () => notifications++;
        Click(true);
        check("depth option commits one undo step and invalidates scene revision", session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 1 && session.SceneRevision == beforeRevision + 1);
        check("depth option flows through editor changed and committed recovery snapshot", notifications > 0 && session.CreateCommittedSnapshot().Canvas.DepthPriorityOcclusion && Fingerprint() != savedFingerprint);
        check("document cloning retains independent depth setting", session.Document.Clone().Canvas.DepthPriorityOcclusion && !ReferenceEquals(session.Document.Canvas, session.Document.Clone().Canvas));
        var oldPreset = CanvasPresetParameters.From(new CanvasSettings());
        var appliedPreset = CanvasPresetLibrary.Apply(oldPreset, session.Document.Canvas, session.Document.Viewport, session.Document.Viewport);
        check("applying a canvas preset restores its disabled depth option", !appliedPreset.DepthPriorityOcclusion && !appliedPreset.Projection.Enabled);
        check("official canvas presets reset depth priority to its default", !CanvasPresetLibrary.OfficialOnly().Apply("official.canvas-transparent", session.Document.Canvas,
            session.Document.Viewport, session.Document.Viewport).DepthPriorityOcclusion);
        Click(true);
        check("unchanged depth option creates no history entry", session.UndoCount == 1 && session.SceneRevision == beforeRevision + 1);
        session.Undo(); window.Refresh(session.Document.Canvas, false);
        check("depth option undo restores checkbox and saved fingerprint", !session.Document.Canvas.DepthPriorityOcclusion && option.IsChecked == false && Fingerprint() == savedFingerprint);
        session.Redo(); window.Refresh(session.Document.Canvas, false);
        check("depth option redo restores checkbox and committed setting", session.Document.Canvas.DepthPriorityOcclusion && option.IsChecked == true);
        editor.BillboardMode = true; editor.BillboardMode = false;
        check("switching insertion placement mode does not change depth option", session.Document.Canvas.DepthPriorityOcclusion);
        session.Execute("disable perspective", d => d.Canvas.Projection.Enabled = false); window.Refresh(session.Document.Canvas, false);
        check("disabling perspective hides depth option and preserves its stored value", option.Visibility == Wpf.Visibility.Collapsed && session.Document.Canvas.DepthPriorityOcclusion);
        session.Undo(); window.Refresh(session.Document.Canvas, false);
        check("restoring perspective restores depth option with its previous choice", option.Visibility == Wpf.Visibility.Visible && option.IsChecked == true);
        int undoBefore = session.UndoCount; long revision = session.SceneRevision;
        allowed = false; Click(false);
        check("busy guard rejects depth edit and restores displayed state", session.Document.Canvas.DepthPriorityOcclusion && option.IsChecked == true && session.UndoCount == undoBefore && session.SceneRevision == revision);
        allowed = true; session.BeginTransaction("drawing"); Click(false);
        check("active gesture rejects depth edit without cancelling the gesture", session.IsTransactionActive && session.Document.Canvas.DepthPriorityOcclusion && option.IsChecked == true);
        session.Cancel();
        var previousSession = session;
        session = new EditorSession(new() { Canvas = new() { Projection = new() { Enabled = true } } });
        Click(true);
        check("stale panel cannot mutate the replacement session", !session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 0 && previousSession.Document.Canvas.DepthPriorityOcclusion);
        Click(true);
        check("refreshed panel edits the new session independently", session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 1);
        check("depth settings tests create no native window", new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !window.IsVisible);
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
        void Set(object target, string field, object value) => target.GetType().GetField(field, flags)!.SetValue(target, value);
        Set(host, "_threadId", GetCurrentThreadId());
        Set(host, "_monitors", new List<MonitorInfo> { new("test", new(0, 0, 1200, 800), new(0, 0, 1200, 800), 96, 96, true) });
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        Set(desktop, "_host", host); Set(desktop, "_editor", editor);
        Set(desktop, "_canvasCalibration", new CanvasCalibrationController());
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
