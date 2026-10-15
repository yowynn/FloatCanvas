using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>A single temporary import, bound to the original document and placement before leaving edit.</summary>
internal sealed class ImageDropRequest : IDisposable
{
    private readonly SceneDocument document;
    private readonly CancellationTokenSource cancellation;
    private bool disposed;
    internal EditorSession Target { get; }
    internal RectD Placement { get; }
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

    internal bool TryCommit(ImageDropRequest? current, EditorSession live, EditorSession? capture, IReadOnlyList<NormalizedImage> images)
    {
        if (Completed || images.Count == 0 || !IsCurrent(current, live, capture)) return false;
        ImageImportCommand.Insert(Target, images, Placement);
        Completed = true;
        return true;
    }

    internal void Cancel() { if (!disposed) cancellation.Cancel(); }
    public void Dispose() { if (disposed) return; Cancel(); disposed = true; cancellation.Dispose(); }
}
