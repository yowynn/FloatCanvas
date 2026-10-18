using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Capture permission is tied to the exact committed document, not whichever canvas is current later.</summary>
internal sealed class SnapshotCaptureRequest(EditorSession target, long editingVersion)
{
    private readonly SceneDocument document = target.Document;
    private readonly long revision = target.SceneRevision;
    internal Guid Id { get; } = Guid.NewGuid();
    internal EditorSession Target { get; } = target;
    internal long EditingVersion { get; private set; } = editingVersion;
    internal bool Cancelled { get; private set; }
    internal bool UserCancellation { get; private set; }
    internal string? CancellationReason { get; private set; }
    internal long? NativeOperationId { get; set; }
    internal CaptureDiagnosticEvent? NativeOutcome { get; set; }
    internal CaptureDiagnosticEvent? LatestNativeDiagnostic { get; set; }
    internal long ContentRevision => revision;
    internal void Cancel(string reason = "Cancelled", bool userInitiated = false)
    {
        if (!Cancelled) CancellationReason = reason;
        Cancelled = true; UserCancellation |= userInitiated;
    }
    internal string? ContentRejection(EditorSession current) => Cancelled ? "Cancelled:" + CancellationReason :
        !ReferenceEquals(Target, current) ? "SessionReplaced" : !ReferenceEquals(document, current.Document) ? "DocumentReplaced" :
        revision != current.SceneRevision ? "ContentChanged" : current.IsTransactionActive ? "TransactionActive" : null;
    internal bool MatchesContent(EditorSession current) => ContentRejection(current) is null;
    internal bool MatchesDocument(EditorSession current) => ReferenceEquals(Target, current) &&
        ReferenceEquals(document, current.Document) && revision == current.SceneRevision;
    internal void AcceptRestoredVersion(long version) => EditingVersion = version;
}
