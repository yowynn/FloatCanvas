namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    // Detached definition sessions keep their entry origin alongside the document
    // header. It is copied back to the definition when the draft is published.
    internal PointD IndependentCanvasOrigin { get => Document.IndependentCanvasOrigin; set => Document.IndependentCanvasOrigin = value; }

    /// <summary>The current scope's authored independent canvas. Reading never creates metadata.</summary>
    public SubprojectCanvasSettings GetEditingSubprojectCanvas()
    {
        if (EditScopeId is { } scope)
            return Document.Groups.FirstOrDefault(group => group.Id == scope)?.PreviewCanvas?.Clone() ?? new();
        return GetRootSubprojectCanvas(ActiveRootCanvas);
    }

    /// <summary>A root's independent header, regardless of the current entered group.</summary>
    public SubprojectCanvasSettings GetRootSubprojectCanvas(RootCanvasKind root = RootCanvasKind.Main)
    {
        _ = SceneCanvasRoots.PaintRank(root);
        if (root != RootCanvasKind.Main) return Document.RootPreviewCanvases.GetValueOrDefault(root)?.Clone() ?? new();
        return SubprojectCanvasSettings.FromDocument(Document, IndependentCanvasOrigin);
    }

    /// <summary>Maps author-world coordinates to the current independent preview. Does not move authored content.</summary>
    public LayerPlacement GetEditingSubprojectPlacement()
    {
        var settings = GetEditingSubprojectCanvas();
        return new LayerPlacement(Affine2D.Translation(-settings.Origin.X, -settings.Origin.Y), 1)
            .Compose(SceneGraph.Read(Document).ScopeWorldPlacement(EditScopeId).Inverse());
    }

    /// <summary>Mutates the current independent header inside an existing or new history operation.</summary>
    public void ChangeEditingSubprojectCanvas(string label, Action<SubprojectCanvasSettings> change)
    {
        RequireWritableScope();
        void Apply(SceneDocument document)
        {
            var settings = GetEditingSubprojectCanvas(); change(settings);
            if (EditScopeId is { } scope) new SceneGraph(document).FindGroup(scope)!.PreviewCanvas = settings;
            else if (ActiveRootCanvas != RootCanvasKind.Main) document.RootPreviewCanvases[ActiveRootCanvas] = settings;
            else { settings.ApplyTo(document); IndependentCanvasOrigin = settings.Origin; }
        }
        if (IsTransactionActive) { Apply(Document); NotifySettingsChanged(); }
        else ExecuteSettings(label, Apply);
    }

    /// <summary>Retains the current independent appearance after CreateSelectionSnapshot rebases selected world coordinates.</summary>
    public SubprojectCanvasSettings BuildSelectionPreviewSettings(SceneDocument snapshot, bool normalizeOrigin = true)
    {
        var source = GetEditingSubprojectCanvas();
        var selection = CreateSelectionSnapshot(normalizeOrigin: false);
        var offset = normalizeOrigin ? new PointD(selection.Viewport.X, selection.Viewport.Y) : default;
        var newToOld = GetEditingSubprojectPlacement().Compose(new(Affine2D.Translation(offset.X, offset.Y), 1));
        return source.Rebase(newToOld, snapshot.Viewport);
    }
}
