using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A captured canvas owner. A late gesture cannot write a different root or entered group.</summary>
internal sealed class CanvasSettingsTarget
{
    private readonly SceneDocument document;
    private readonly long scopeVersion;
    private readonly Guid? scope;
    private readonly RootCanvasKind root;
    private readonly Func<bool>? contextCurrent;
    private readonly RectD? editingViewport;
    internal EditorSession Session { get; }
    internal bool Independent { get; }
    internal bool IsCurrent => ReferenceEquals(Session.Document, document) && Session.EditScopeVersion == scopeVersion &&
        Session.EditScopeId == scope && Session.ActiveRootCanvas == root && contextCurrent?.Invoke() != false;
    internal bool IsWritable => !Session.IsReadOnly && Session.CanInsertInEditScope;
    internal CanvasSettings Settings
    {
        get
        {
            if (!Independent) return document.Canvas;
            var preview = Session.GetEditingSubprojectCanvas();
            return editingViewport is { } bounds && bounds != preview.Viewport
                ? GroupCanvasScene.ResolveEditingCanvas(preview.Canvas, preview.Viewport) : preview.Canvas;
        }
    }
    internal RectD Viewport => editingViewport ?? (Independent ? Session.GetEditingSubprojectCanvas().Viewport : document.Viewport);
    internal double LayerOpacity => Independent ? Session.GetEditingSubprojectCanvas().LayerOpacity : document.LayerOpacity;

    internal CanvasSettingsTarget(EditorSession session, bool independent = false, Func<bool>? contextCurrent = null, RectD? editingViewport = null)
    {
        Session = session; document = session.Document; scopeVersion = session.EditScopeVersion;
        scope = session.EditScopeId; root = session.ActiveRootCanvas; Independent = independent; this.contextCurrent = contextCurrent;
        this.editingViewport = editingViewport;
    }

    internal bool Change(Action<CanvasSettings> change, string? label = null)
    {
        if (!IsCurrent || !IsWritable) return false;
        label ??= L.Get("Canvas.Title");
        if (Independent) Session.ChangeEditingSubprojectCanvas(label, header => change(header.Canvas));
        else if (Session.IsTransactionActive) { change(document.Canvas); Session.NotifySettingsChanged(); }
        else Session.ExecuteSettings(label, scene => change(scene.Canvas));
        return true;
    }

    internal bool Replace(CanvasSettings settings, string? label = null) => Change(canvas =>
    { var copy = settings.Clone(); canvas.BackgroundColor = copy.BackgroundColor; canvas.DepthPriorityOcclusion = copy.DepthPriorityOcclusion; canvas.Grid = copy.Grid; canvas.Projection = copy.Projection; }, label);

    internal bool ChangeOpacity(double opacity)
    {
        if (!IsCurrent || !IsWritable) return false;
        if (Independent) Session.ChangeEditingSubprojectCanvas(L.Get("History.LayerOpacity"), header => header.LayerOpacity = opacity);
        else if (Session.IsTransactionActive) { document.LayerOpacity = opacity; Session.NotifySettingsChanged(); }
        else Session.ExecuteSettings(L.Get("History.LayerOpacity"), scene => scene.LayerOpacity = opacity);
        return true;
    }

    internal SceneDocument CreateDisplayDocument() => (Independent
        ? new GroupCanvasScene().Capture(Session, null, GroupCanvasMode.Temporary, default, Session.Assets, editingViewport).Document
        : document).Clone();

    internal bool ResolveGeometry(SceneDocument resolved, IReadOnlyList<Guid> affected, CanvasSafetyAction action)
    {
        if (!IsCurrent || !Session.IsTransactionActive) return false;
        var previewGraph = new SceneGraph(resolved); var graph = new SceneGraph(document);
        var roots = CanvasSafetyResolution.RootNodes(resolved, affected);
        if (action == CanvasSafetyAction.Delete) { graph.RemoveNodes(roots); return true; }
        if (action != CanvasSafetyAction.Relocate) return true;
        var origin = Independent ? Session.GetEditingSubprojectCanvas().Origin : default;
        var restore = Affine2D.Translation(origin.X, origin.Y);
        foreach (var id in roots)
        {
            if (previewGraph.FindElement(id) is { } preview && graph.FindElement(id) is { } authored)
            {
                var placement = preview.Clone(); SceneGraph.ApplyPlacement(placement, new(restore, 1));
                EditorSession.CopyPlacement(authored, placement);
            }
            else if (previewGraph.FindGroup(id) is { } previewGroup && graph.FindGroup(id) is { } authoredGroup)
                authoredGroup.Transform = restore.Compose(previewGroup.Transform);
            else if (previewGraph.FindInstance(id) is { } previewInstance && graph.FindInstance(id) is { } authoredInstance)
                authoredInstance.Transform = restore.Compose(previewInstance.Transform);
        }
        return true;
    }
}
