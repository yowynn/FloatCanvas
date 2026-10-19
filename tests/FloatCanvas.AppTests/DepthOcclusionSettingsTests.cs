using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;

internal static class DepthOcclusionSettingsTests
{
    public static void Run(Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        bool allowed = true;
        var session = new EditorSession();
        var editor = new EditorController(session);
        var window = new LayersWindow(() => session, () => allowed);
        var option = (Controls.CheckBox)typeof(LayersWindow).GetField("depthPriorityOcclusion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Click(bool value) { option.IsChecked = value; option.RaiseEvent(new Wpf.RoutedEventArgs(Primitives.ButtonBase.ClickEvent)); }
        string Fingerprint() => (string)typeof(DesktopSession).GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [session.Document])!;
        check("depth occlusion defaults off and its option is hidden on a flat canvas", !session.Document.Canvas.DepthPriorityOcclusion && option.Visibility == Wpf.Visibility.Collapsed);
        Click(true);
        check("hidden flat-canvas option cannot activate depth occlusion", !session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 0 && option.IsChecked == false);
        session.Document.Canvas.Projection.Enabled = true; window.Refresh();
        check("perspective canvas exposes localized depth option", option.Visibility == Wpf.Visibility.Visible && Equals(option.Content, L.Get("Layers.DepthPriorityOcclusion")) && option.IsChecked == false);
        var savedFingerprint = Fingerprint(); long beforeRevision = session.SceneRevision; int notifications = 0;
        editor.Changed += () => notifications++;
        Click(true);
        check("depth option commits one undo step and invalidates scene revision", session.Document.Canvas.DepthPriorityOcclusion && session.UndoCount == 1 && session.SceneRevision == beforeRevision + 1);
        check("depth option flows through editor changed and committed recovery snapshot", notifications > 0 && session.CreateCommittedSnapshot().Canvas.DepthPriorityOcclusion && Fingerprint() != savedFingerprint);
        check("document cloning retains independent depth setting", session.Document.Clone().Canvas.DepthPriorityOcclusion && !ReferenceEquals(session.Document.Canvas, session.Document.Clone().Canvas));
        var oldPreset = CanvasPresetParameters.From(new CanvasSettings());
        var appliedPreset = CanvasPresetLibrary.Apply(oldPreset, session.Document.Canvas, session.Document.Viewport, session.Document.Viewport);
        check("applying an existing canvas preset preserves the independent layer depth option", appliedPreset.DepthPriorityOcclusion && !appliedPreset.Projection.Enabled);
        check("official canvas presets also preserve the existing depth option", CanvasPresetLibrary.OfficialOnly().Apply("official.canvas-transparent", session.Document.Canvas,
            session.Document.Viewport, session.Document.Viewport).DepthPriorityOcclusion);
        Click(true);
        check("unchanged depth option creates no history entry", session.UndoCount == 1 && session.SceneRevision == beforeRevision + 1);
        session.Undo(); window.Refresh();
        check("depth option undo restores checkbox and saved fingerprint", !session.Document.Canvas.DepthPriorityOcclusion && option.IsChecked == false && Fingerprint() == savedFingerprint);
        session.Redo(); window.Refresh();
        check("depth option redo restores checkbox and committed setting", session.Document.Canvas.DepthPriorityOcclusion && option.IsChecked == true);
        editor.BillboardMode = true; editor.BillboardMode = false;
        check("switching insertion placement mode does not change depth option", session.Document.Canvas.DepthPriorityOcclusion);
        session.Execute("disable perspective", d => d.Canvas.Projection.Enabled = false); window.Refresh();
        check("disabling perspective hides depth option and preserves its stored value", option.Visibility == Wpf.Visibility.Collapsed && session.Document.Canvas.DepthPriorityOcclusion);
        session.Undo(); window.Refresh();
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
    }
}
