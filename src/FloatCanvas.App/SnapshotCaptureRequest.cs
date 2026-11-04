using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Capture permission is tied to the exact committed document, not whichever canvas is current later.</summary>
internal sealed class SnapshotCaptureRequest(EditorSession target, long editingVersion, object? viewingEpoch = null, PointD? canvasOrigin = null,
    Func<bool>? contextCurrent = null)
{
    private readonly SceneDocument document = target.Document;
    private readonly long revision = target.SceneRevision;
    private readonly long editScopeVersion = target.EditScopeVersion;
    private readonly object? view = viewingEpoch;
    private readonly PointD? origin = canvasOrigin;
    private readonly Func<bool>? context = contextCurrent;
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
    internal long EditScopeVersion => editScopeVersion;
    internal void Cancel(string reason = "Cancelled", bool userInitiated = false)
    {
        if (!Cancelled) CancellationReason = reason;
        Cancelled = true; UserCancellation |= userInitiated;
    }
    internal string? ContentRejection(EditorSession current, object? currentView = null, PointD? currentOrigin = null) => Cancelled ? "Cancelled:" + CancellationReason :
        context?.Invoke() == false ? "ContextChanged" :
        view is not null && !ReferenceEquals(view, currentView) ? "ViewChanged" :
        origin is not null && origin != currentOrigin ? "ScreenOriginChanged" :
        !ReferenceEquals(Target, current) ? "SessionReplaced" : !ReferenceEquals(document, current.Document) ? "DocumentReplaced" :
        editScopeVersion != current.EditScopeVersion ? "EditScopeChanged" :
        revision != current.SceneRevision ? "ContentChanged" : current.IsTransactionActive ? "TransactionActive" : null;
    internal bool MatchesContent(EditorSession current, object? currentView = null, PointD? currentOrigin = null) => ContentRejection(current, currentView, currentOrigin) is null;
    internal bool MatchesDocument(EditorSession current) => ReferenceEquals(Target, current) &&
        ReferenceEquals(document, current.Document) && revision == current.SceneRevision && editScopeVersion == current.EditScopeVersion;
    internal void AcceptRestoredVersion(long version) => EditingVersion = version;
}
