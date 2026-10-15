using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>A single drop, bound to its original document, viewport and physical-to-document mapping.</summary>
internal sealed class ImageDropRequest : IDisposable
{
    private readonly SceneDocument document;
    private readonly CancellationTokenSource cancellation;
    private bool disposed;
    internal EditorSession Target { get; }
    internal RectD Placement { get; }
    internal PointD? DropCenter { get; private set; }
    internal CancellationToken Token { get; }
    internal bool Completed { get; private set; }
    internal bool IsImporting { get; set; }

    internal ImageDropRequest(EditorSession target, RectD placement, CancellationToken lifetime)
    {
        Target = target; document = target.Document; Placement = placement;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = cancellation.Token;
    }

    internal bool IsCurrent(ImageDropRequest? current, EditorSession live, EditorSession? capture) =>
        !Token.IsCancellationRequested && ReferenceEquals(this, current) && ReferenceEquals(Target.Document, document) &&
        (ReferenceEquals(Target, live) || ReferenceEquals(Target, capture));

    internal bool TrySetDropPoint(PointD screenPoint, PointD screenOrigin)
    {
        if (Completed || IsImporting || Token.IsCancellationRequested || DropCenter is not null ||
            !double.IsFinite(screenPoint.X) || !double.IsFinite(screenPoint.Y) ||
            !double.IsFinite(screenOrigin.X) || !double.IsFinite(screenOrigin.Y)) return false;
        DropCenter = screenPoint - screenOrigin;
        return true;
    }

    internal bool TryCommit(ImageDropRequest? current, EditorSession live, EditorSession? capture, IReadOnlyList<NormalizedImage> images)
    {
        if (Completed || images.Count == 0 || !IsCurrent(current, live, capture)) return false;
        ImageImportCommand.Insert(Target, images, Placement, dropCenter: DropCenter);
        Completed = true;
        return true;
    }

    internal void Cancel() { if (!disposed) cancellation.Cancel(); }
    public void Dispose() { if (disposed) return; Cancel(); disposed = true; cancellation.Dispose(); }
}
