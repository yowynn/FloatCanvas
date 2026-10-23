namespace FloatCanvas.Windows;

public enum CaptureRestoreStatus { Pending, Restored, PassThrough, Cancelled, ForegroundChanged, GuardRejected, Failed }

/// <summary>Metadata only; never pixels, input text, or an external-window permission list.</summary>
public sealed record CaptureDiagnosticEvent(long OperationId, string Phase, string Reason, CaptureRestoreStatus Status,
    long OriginVersion, long CurrentVersion, long ExpectedForeground, long SuspendedForeground, long CurrentForeground,
    long EventForeground, uint EventTime, uint BoundaryTime, long IntentVersion, long ForegroundLossVersion,
    long ExternalInputVersion, bool UserIntentChanged, bool ExternalInputObserved)
{
    // Retained as an empty wire-format field for old diagnostic readers. Never a permission list.
    public IReadOnlyList<long> AllowedForegrounds { get; init; } = [];
    public IReadOnlyList<CaptureInputObservation> ExternalInputs { get; init; } = [];
    public CaptureVisibilityStrategy Strategy { get; init; }
}

/// <summary>A host-issued single-use permit for the exact unchanged editing transaction.</summary>
public sealed class CaptureRestoreReceipt
{
    private readonly object issuer;
    private bool consumed;
    internal CaptureRestoreReceipt(object issuer, long operation, long origin, long restored, bool editing,
        long intent, long foregroundLoss, long externalInput)
    {
        this.issuer = issuer; OperationId = operation; OriginEditingSessionVersion = origin;
        RestoredEditingSessionVersion = restored; WasEditing = editing; IntentVersion = intent;
        ForegroundLossVersion = foregroundLoss; ExternalInputVersion = externalInput;
    }
    public long OperationId { get; }
    public long OriginEditingSessionVersion { get; }
    public long RestoredEditingSessionVersion { get; }
    public bool WasEditing { get; }
    internal long IntentVersion { get; }
    internal long ForegroundLossVersion { get; }
    internal long ExternalInputVersion { get; }
    internal bool TryConsume(object owner, long operation, long origin, long current, bool editing,
        long intent, long foregroundLoss, long externalInput)
    {
        if (consumed || !ReferenceEquals(issuer, owner) || OperationId != operation || OriginEditingSessionVersion != origin ||
            RestoredEditingSessionVersion != current || WasEditing != editing || IntentVersion != intent ||
            ForegroundLossVersion != foregroundLoss || ExternalInputVersion != externalInput) return false;
        consumed = true; return true;
    }
}

internal sealed class CaptureContinuationState(object issuer, long operation, long origin, bool editing, bool visible,
    long intent, long foregroundLoss, long externalInput)
{
    public long OperationId { get; } = operation;
    public long OriginVersion { get; } = origin;
    public long CaptureVersion { get; private set; } = -1;
    public bool WasEditing { get; } = editing;
    public bool RestoreVisible { get; set; } = visible;
    public long IntentVersion { get; } = intent;
    public long ForegroundLossVersion { get; } = foregroundLoss;
    public long ExternalInputVersion { get; } = externalInput;
    public CaptureRestoreStatus Status { get; private set; } = CaptureRestoreStatus.Pending;
    public string Reason { get; private set; } = "Pending";
    public nint ExpectedForeground { get; private set; }
    public nint ReadyForeground { get; private set; }
    public uint StartTime { get; private set; }
    public uint BoundaryTime { get; private set; }
    public nint RequiredMouseCapture { get; private set; }
    public CaptureVisibilityStrategy Strategy { get; private set; }
    public bool OutcomeReported { get; set; }
    public bool UnsafeInputGeometry { get; private set; }
    public void Begin(nint foreground, uint time) { ExpectedForeground = foreground; StartTime = time; }
    public void Ready(long version, nint foreground, uint time, CaptureVisibilityStrategy strategy, nint requiredMouseCapture)
    {
        CaptureVersion = version; ReadyForeground = foreground; BoundaryTime = time; Strategy = strategy;
        RequiredMouseCapture = requiredMouseCapture;
        if (version != OriginVersion) Cancel(CaptureRestoreStatus.Cancelled, "SessionChanged");
    }
    public void ObserveForeground(nint hwnd, uint time, bool ownProcessWindow, bool delegatedTarget = false)
    {
        if (unchecked((int)(time - StartTime)) < 0 || hwnd == 0 || ownProcessWindow || delegatedTarget) return;
        Cancel(CaptureRestoreStatus.ForegroundChanged, "ForegroundEventMismatch");
    }
    public void Cancel(CaptureRestoreStatus reason = CaptureRestoreStatus.Cancelled, string detail = "ExplicitCancel")
    { if (Status == CaptureRestoreStatus.Pending) { Status = reason; Reason = detail; } }
    public void InputRestoreFailed()
    { UnsafeInputGeometry = true; Cancel(CaptureRestoreStatus.Failed, "InputGeometryRestoreFailed"); }
    public bool CanRestore(long version, long intentVersion, long lossVersion, long inputVersion) =>
        Status == CaptureRestoreStatus.Pending && CaptureVersion == OriginVersion && version == OriginVersion &&
        IntentVersion == intentVersion && ForegroundLossVersion == lossVersion && ExternalInputVersion == inputVersion;
    public CaptureRestoreReceipt Complete(long currentVersion)
    {
        if (Status != CaptureRestoreStatus.Pending || CaptureVersion != OriginVersion || currentVersion != OriginVersion)
            throw new InvalidOperationException("Clean capture cannot renew a changed editing session.");
        Status = WasEditing ? CaptureRestoreStatus.Restored : CaptureRestoreStatus.PassThrough;
        Reason = WasEditing ? "Continued" : "PassThrough";
        return new(issuer, OperationId, OriginVersion, currentVersion, WasEditing, IntentVersion, ForegroundLossVersion, ExternalInputVersion);
    }
}
internal readonly record struct CaptureRestoreResult(CaptureRestoreStatus Status, CaptureRestoreReceipt? Receipt = null);
