namespace FloatCanvas.App;

internal sealed record CanvasPreviewRequestKey(CanvasPreviewContext Context, long ContentRevision, long DraftRevision, bool PreferGpu);

// The request reservation is distinct from a successfully presented frame. Failures
// release only their own reservation, and get one automatic retry per unchanged input.
internal sealed class CanvasPreviewRequestState
{
    private CanvasPreviewRequestKey? requested, failureKey;
    private long requestedSequence;
    private int failures;
    internal long PresentedSequence { get; private set; }

    internal bool TryRequest(CanvasPreviewRequestKey key, long sequence, bool force = false)
    {
        if (!force && requested == key) return false;
        if (failureKey != key) { failureKey = key; failures = 0; }
        requested = key; requestedSequence = sequence;
        return true;
    }
    internal bool Fail(long sequence, out bool retry)
    {
        retry = false;
        if (requested is null || requestedSequence != sequence) return false;
        requested = null;
        retry = ++failures == 1;
        return true;
    }
    internal void Presented(long sequence)
    {
        PresentedSequence = sequence;
        if (sequence == requestedSequence) failures = 0;
    }
}
