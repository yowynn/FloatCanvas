using FloatCanvas.Core;
using System.IO;

namespace FloatCanvas.App;

/// <summary>A read-only request snapshot. Check before commit; completed commands need no later check after switching tools.</summary>
internal sealed class WindowSourceOperationGuard
{
    private readonly EditorSession owner;
    private readonly EditorController editor;
    private readonly SceneDocument document;
    private readonly long contentVersion, scopeVersion, placementVersion, editingVersion;
    private readonly PointD origin;
    private readonly Projective2D plane;
    private readonly bool billboard, editing, trackSelection;
    private readonly BillboardCreationWorkflow workflow;
    private readonly EditorTool tool;
    private readonly object? prefabContext;
    private readonly object monitors, ownerLifetime;
    private readonly HashSet<Guid>? selection;
    private bool invalid;

    internal WindowSourceOperationGuard(EditorSession owner, EditorController editor, bool editing, long editingVersion,
        object? prefabContext, object monitors, object ownerLifetime, bool trackSelection = true)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(monitors); ArgumentNullException.ThrowIfNull(ownerLifetime);
        this.owner = owner; this.editor = editor; document = owner.Document;
        contentVersion = owner.ContentVersion; scopeVersion = owner.EditScopeVersion; placementVersion = editor.PlacementContextVersion;
        origin = editor.CanvasOrigin; billboard = editor.BillboardMode; workflow = editor.BillboardWorkflow; tool = editor.Tool;
        this.editing = editing; this.editingVersion = editingVersion; this.prefabContext = prefabContext;
        this.monitors = monitors; this.ownerLifetime = ownerLifetime; this.trackSelection = trackSelection;
        selection = trackSelection ? owner.Selection.ToHashSet() : null;
        invalid = owner.IsTransactionActive || !ReferenceEquals(editor.Session, owner);
        try { plane = editor.CurrentCanvasMapping.PlaneToScreen; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or ArithmeticException)
        { invalid = true; }
    }

    internal bool IsCurrent(EditorSession owner, EditorController editor, bool editing, long editingVersion,
        object? prefabContext, object monitors, object ownerLifetime)
    {
        if (invalid) return false;
        bool current = ReferenceEquals(this.owner, owner) && ReferenceEquals(this.editor, editor) &&
            ReferenceEquals(editor.Session, owner) && ReferenceEquals(document, owner.Document) &&
            !owner.IsTransactionActive && owner.ContentVersion == contentVersion && owner.EditScopeVersion == scopeVersion &&
            editor.PlacementContextVersion == placementVersion && editor.CanvasOrigin == origin && editor.BillboardMode == billboard &&
            editor.BillboardWorkflow == workflow && editor.Tool == tool && this.editing == editing && this.editingVersion == editingVersion &&
            ReferenceEquals(this.prefabContext, prefabContext) && ReferenceEquals(this.monitors, monitors) && ReferenceEquals(this.ownerLifetime, ownerLifetime) &&
            (!trackSelection || selection!.SetEquals(owner.Selection));
        if (current)
        {
            try { current = editor.CurrentCanvasMapping.PlaneToScreen == plane; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or ArithmeticException)
            { current = false; }
        }
        if (!current) invalid = true;
        return current;
    }
}
