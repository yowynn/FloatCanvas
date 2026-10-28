using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>A single drop, bound to its original document, viewport and physical-to-document mapping.</summary>
internal sealed class ImageDropRequest : IDisposable
{
    private readonly SceneDocument document;
    private readonly CancellationTokenSource cancellation;
    private readonly Projective2D planeToScreen;
    private bool disposed;
    internal EditorSession Target { get; }
    internal RectD Placement { get; }
    internal double Opacity { get; }
    internal PointD? DropCenter { get; private set; }
    internal CancellationToken Token { get; }
    internal bool Completed { get; private set; }
    internal bool IsImporting { get; set; }

    internal ImageDropRequest(EditorSession target, RectD placement, CancellationToken lifetime, double opacity = 1)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        Target = target; document = target.Document; Placement = placement; Opacity = opacity;
        planeToScreen = CanvasMapping.Create(document).PlaneToScreen;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = cancellation.Token;
    }

    internal bool IsCurrent(ImageDropRequest? current, EditorSession live, EditorSession? capture) =>
        !Token.IsCancellationRequested && ReferenceEquals(this, current) && ReferenceEquals(Target.Document, document) &&
        CanvasMapping.Create(Target.Document).PlaneToScreen == planeToScreen &&
        (ReferenceEquals(Target, live) || ReferenceEquals(Target, capture));

    internal bool TrySetDropPoint(PointD screenPoint, PointD screenOrigin)
    {
        if (Completed || IsImporting || Token.IsCancellationRequested || DropCenter is not null ||
            !double.IsFinite(screenPoint.X) || !double.IsFinite(screenPoint.Y) ||
            !double.IsFinite(screenOrigin.X) || !double.IsFinite(screenOrigin.Y)) return false;
        if (!planeToScreen.TryInverse(out var inverse) || !inverse.TryTransformPoint(screenPoint - screenOrigin, out var point)) return false;
        DropCenter = point;
        return true;
    }

    internal bool TryCommit(ImageDropRequest? current, EditorSession live, EditorSession? capture, IReadOnlyList<NormalizedImage> images)
    {
        if (Completed || images.Count == 0 || !IsCurrent(current, live, capture)) return false;
        ImageImportCommand.Insert(Target, images, Placement, dropCenter: DropCenter, opacity: Opacity);
        Completed = true;
        return true;
    }

    internal bool TryCompleteDeferred(ImageDropRequest? current, EditorSession live, EditorSession? capture)
    {
        if (Completed || !IsCurrent(current, live, capture)) return false;
        Completed = true;
        return true;
    }

    internal void Cancel() { if (!disposed) cancellation.Cancel(); }
    public void Dispose() { if (disposed) return; Cancel(); disposed = true; cancellation.Dispose(); }
}
