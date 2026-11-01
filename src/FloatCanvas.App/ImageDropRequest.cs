using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>A single drop, bound to its original document, viewport and physical-to-document mapping.</summary>
internal sealed class ImageDropRequest : IDisposable
{
    private readonly SceneDocument document;
    private readonly CancellationTokenSource cancellation;
    private readonly Projective2D planeToScreen;
    private readonly CanvasMapping placementMapping;
    private readonly long contentVersion, scopeVersion;
    private readonly bool initiallyWritable;
    private readonly Func<CanvasMapping> mapping;
    private readonly Func<object?>? viewEpoch;
    private readonly object? initialViewEpoch;
    private Func<bool>? completionGuard;
    private bool invalidated;
    private bool disposed;
    internal EditorSession Target { get; }
    internal RectD Placement { get; }
    internal double Opacity { get; }
    internal PointD? DropCenter { get; private set; }
    internal CancellationToken Token { get; }
    internal bool Completed { get; private set; }
    internal bool IsImporting { get; set; }

    internal ImageDropRequest(EditorSession target, RectD placement, CancellationToken lifetime, double opacity = 1,
        Func<CanvasMapping>? mapping = null, Func<object?>? viewEpoch = null)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        Target = target; document = target.Document; Placement = placement; Opacity = opacity;
        contentVersion = target.ContentVersion; scopeVersion = target.EditScopeVersion;
        initiallyWritable = !target.IsTransactionActive && target.CanInsertInEditScope;
        this.mapping = mapping ?? (() => CanvasMapping.Create(Target.Document));
        this.viewEpoch = viewEpoch; initialViewEpoch = viewEpoch?.Invoke();
        placementMapping = this.mapping(); planeToScreen = placementMapping.PlaneToScreen;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = cancellation.Token;
    }

    internal bool IsCurrent(ImageDropRequest? current, EditorSession live, EditorSession? capture, EditorSession? editingOwner = null)
    {
        if (invalidated) return false;
        bool valid = !Token.IsCancellationRequested && initiallyWritable && !Target.IsTransactionActive && Target.CanInsertInEditScope &&
            Target.ContentVersion == contentVersion && Target.EditScopeVersion == scopeVersion &&
            ReferenceEquals(this, current) && ReferenceEquals(Target.Document, document) &&
            mapping().PlaneToScreen == planeToScreen && (viewEpoch is null || ReferenceEquals(initialViewEpoch, viewEpoch())) &&
            (ReferenceEquals(Target, live) || ReferenceEquals(Target, capture) || ReferenceEquals(Target, editingOwner)) && (completionGuard?.Invoke() ?? true);
        if (!valid) invalidated = true;
        return valid;
    }

    internal void BindCompletionGuard(Func<bool> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (completionGuard is not null) throw new InvalidOperationException("The drop completion context is already bound.");
        completionGuard = guard;
    }

    internal bool TrySetDropPoint(PointD screenPoint, PointD screenOrigin)
    {
        if (Completed || IsImporting || Token.IsCancellationRequested || DropCenter is not null ||
            !double.IsFinite(screenPoint.X) || !double.IsFinite(screenPoint.Y) ||
            !double.IsFinite(screenOrigin.X) || !double.IsFinite(screenOrigin.Y)) return false;
        if (!placementMapping.TryToPlane(screenPoint - screenOrigin, out var point)) return false;
        DropCenter = point;
        return true;
    }

    internal bool TryCommit(ImageDropRequest? current, EditorSession live, EditorSession? capture, IReadOnlyList<NormalizedImage> images, EditorSession? editingOwner = null)
    {
        if (Completed || images.Count == 0 || !IsCurrent(current, live, capture, editingOwner)) return false;
        ImageImportCommand.Insert(Target, images, Placement, dropCenter: DropCenter, opacity: Opacity);
        Completed = true;
        return true;
    }

    internal bool TryCompleteDeferred(ImageDropRequest? current, EditorSession live, EditorSession? capture, EditorSession? editingOwner = null)
    {
        if (Completed || !IsCurrent(current, live, capture, editingOwner)) return false;
        Completed = true;
        return true;
    }

    internal bool TryCommit(ImageDropRequest? current, EditorSession live, EditorSession? capture,
        PreparedCanvasContentBatch batch, EditorSession? editingOwner = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (Completed || !IsCurrent(current, live, capture, editingOwner) || !batch.TryCommit(Target)) return false;
        Completed = true;
        return true;
    }

    internal void Cancel() { if (!disposed) cancellation.Cancel(); }
    public void Dispose() { if (disposed) return; Cancel(); disposed = true; completionGuard = null; cancellation.Dispose(); }
}
