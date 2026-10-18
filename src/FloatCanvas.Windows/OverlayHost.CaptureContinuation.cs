using L = FloatCanvas.Localization.AppText;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private long _captureIntentVersion, _captureOperationId;
    private CaptureContinuationState? _captureOperation;
    private nint _captureGeometryWindow;
    // Test-only capability downgrade: production uses the actual OS capability.
    internal bool ForceLegacyCleanCapture { get; set; }
    public event Action<CaptureDiagnosticEvent>? CaptureDiagnostic;
    public bool IsCapturing => _captureActive;

    public void CancelPendingCapture()
    {
        CheckThread(); _captureIntentVersion++;
        if (_captureOperation is { } operation)
        { operation.Cancel(); ReportCapture(operation, "Cancel", operation.Reason); }
    }

    /// <summary>Captures without transferring foreground, ending keyboard isolation or changing the editing epoch.</summary>
    public CaptureVisibilityScope BeginCleanCapture(IEnumerable<nint>? additionalWindows = null, Func<bool>? canRestoreEditing = null)
    {
        CheckThread(); EndDropReceptionCore();
        if (_captureActive) throw new InvalidOperationException(L.Get("Capture.AlreadyInProgress"));
        var operation = new CaptureContinuationState(this, ++_captureOperationId, EditingSessionVersion, IsEditing, IsVisible,
            _captureIntentVersion, ForegroundLossVersion, ExternalCaptureInputVersion);
        operation.Begin(GetForegroundWindow(), unchecked((uint)Environment.TickCount64));
        if (IsEditing && _keyboardIsolation is { IsLeaseActive: false }) operation.Cancel(CaptureRestoreStatus.Failed, "LeaseFailure");
        if (IsEditing && !IsForegroundFromThisProcess()) operation.Cancel(CaptureRestoreStatus.ForegroundChanged, "UnexpectedForeground");
        _captureActive = true; _captureOperation = operation;
        ReportCapture(operation, "Begin", operation.Reason);
        try
        {
            return new CaptureVisibilityScope(_windows.Select(pair => pair.Display.Handle).Concat(additionalWindows ?? []),
                _windows.Select(pair => pair.Input.Handle),
                permit => CompleteCapturedHost(operation, permit && CanContinueCapture(operation, canRestoreEditing)),
                (strategy, mouseCapture) =>
                {
                    operation.Ready(EditingSessionVersion, GetForegroundWindow(), unchecked((uint)Environment.TickCount64), strategy, mouseCapture);
                    ReportCapture(operation, "Ready", operation.Reason);
                }, Capabilities, () => CanContinueCapture(operation, canRestoreEditing, allowPreparing: true), ForceLegacyCleanCapture,
                MoveCaptureInput, ReleaseCaptureInput, _ =>
                {
                    operation.InputRestoreFailed();
                    ReportCapture(operation, "RestoreFailure", "InputGeometryRestoreFailed", CaptureRestoreStatus.Failed);
                });
        }
        catch
        {
            _captureActive = false; _captureOperation = null;
            if (!operation.OutcomeReported) ReportCapture(operation, "Outcome", "Failed", CaptureRestoreStatus.Failed);
            throw;
        }
    }

    /// <summary>Compatibility name; clean capture no longer hides the active input HWND or suspends editing.</summary>
    public CaptureVisibilityScope HideForCapture(IEnumerable<nint>? additionalWindows = null, Func<bool>? canRestoreEditing = null) =>
        BeginCleanCapture(additionalWindows, canRestoreEditing);

    public bool TryConsumeCaptureContinuation(CaptureRestoreReceipt receipt, long originalEditingSessionVersion,
        out long restoredEditingSessionVersion)
    {
        CheckThread(); ArgumentNullException.ThrowIfNull(receipt); restoredEditingSessionVersion = 0;
        if (_captureActive || (receipt.WasEditing && (!IsForegroundFromThisProcess() ||
            (_keyboardIsolation is not null && !_keyboardIsolation.IsLeaseActive))) ||
            !receipt.TryConsume(this, _captureOperationId, originalEditingSessionVersion, EditingSessionVersion, IsEditing,
                _captureIntentVersion, ForegroundLossVersion, ExternalCaptureInputVersion))
        { ReportCaptureReceipt(receipt, "ReceiptRejected"); return false; }
        restoredEditingSessionVersion = receipt.RestoredEditingSessionVersion;
        ReportCaptureReceipt(receipt, "ReceiptConsumed"); return true;
    }
    private long ExternalCaptureInputVersion => _keyboardIsolation?.ExternalInputVersion ?? 0;

    private bool CanContinueCapture(CaptureContinuationState operation, Func<bool>? appGuard, bool allowPreparing = false)
    {
        bool Reject(CaptureRestoreStatus status, string reason)
        { operation.Cancel(status, reason); ReportCapture(operation, "RestoreCheck", operation.Reason); return false; }
        if (_disposed) return Reject(CaptureRestoreStatus.Cancelled, "Disposed");
        CheckThread();
        if (ExternalCaptureInputVersion != operation.ExternalInputVersion)
            return Reject(CaptureRestoreStatus.ForegroundChanged, "ExternalInput");
        if (ForegroundLossVersion != operation.ForegroundLossVersion)
            return Reject(CaptureRestoreStatus.ForegroundChanged, "SessionChanged");
        bool samePreparation = allowPreparing && operation.CaptureVersion < 0 && operation.Status == CaptureRestoreStatus.Pending &&
            operation.OriginVersion == EditingSessionVersion && operation.IntentVersion == _captureIntentVersion;
        if (!ReferenceEquals(_captureOperation, operation) || (!samePreparation && !operation.CanRestore(EditingSessionVersion,
            _captureIntentVersion, ForegroundLossVersion, ExternalCaptureInputVersion)) || IsEditing != operation.WasEditing)
            return Reject(CaptureRestoreStatus.Cancelled, "SessionChanged");
        if (appGuard?.Invoke() == false) return Reject(CaptureRestoreStatus.GuardRejected, "AppGuard");
        if (_keyboardIsolation is { } isolation && (isolation.Failure is not null || !isolation.IsThreadAlive ||
            operation.WasEditing && !isolation.IsLeaseActive)) return Reject(CaptureRestoreStatus.Failed, "LeaseFailure");
        if (operation.WasEditing && !IsForegroundFromThisProcess())
            return Reject(CaptureRestoreStatus.ForegroundChanged, GetForegroundWindow() == 0 ? "ZeroForeground" : "UnexpectedForeground");
        if (operation.RequiredMouseCapture != 0 && GetCapture() != operation.RequiredMouseCapture)
            return Reject(CaptureRestoreStatus.Cancelled, "PointerCaptureLost");
        ReportCapture(operation, "RestoreCheck", "Allowed"); return true;
    }

    private CaptureRestoreResult CompleteCapturedHost(CaptureContinuationState operation, bool permit)
    {
        bool intentChanged = _captureIntentVersion != operation.IntentVersion;
        bool inputObserved = ExternalCaptureInputVersion != operation.ExternalInputVersion;
        CaptureRestoreResult Outcome(CaptureRestoreStatus status, string reason, CaptureRestoreReceipt? receipt = null)
        {
            ReportCapture(operation, "Outcome", reason, status, userIntentChanged: intentChanged, externalInputObserved: inputObserved);
            return new(status, receipt);
        }
        _captureActive = false; _captureOperation = null;
        if (_disposed) return Outcome(CaptureRestoreStatus.Cancelled, "Disposed");
        CheckThread();
        // A carrier that cannot return on-screen is no longer a healthy input surface.
        // Fail open instead of leaving an invisible editing window holding keyboard input.
        if (operation.UnsafeInputGeometry)
        {
            if (IsEditing) EndEditingAfterFocusLoss();
            return Outcome(CaptureRestoreStatus.Failed, "InputGeometryRestoreFailed");
        }
        if (operation.CaptureVersion < 0) return Outcome(CaptureRestoreStatus.Failed, "Failed");
        if (!permit)
        {
            operation.Cancel(CaptureRestoreStatus.Failed, "Failed");
            // A capture/render/App guard failure is not an instruction to exit editing.
            // Only actual outside focus or a dead lease releases input, without activation.
            if (IsEditing && (!IsForegroundFromThisProcess() || _keyboardIsolation is { IsLeaseActive: false }))
                EndEditingAfterFocusLoss();
            return Outcome(operation.Status, operation.Reason);
        }
        var receipt = operation.Complete(EditingSessionVersion);
        return Outcome(operation.Status, operation.Reason, receipt);
    }

    private void MoveCaptureInput(nint handle, PixelBounds bounds)
    {
        nint previous = _captureGeometryWindow; _captureGeometryWindow = handle;
        try
        {
            if (!SetWindowPos(handle, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x14) ||
                !GetWindowRect(handle, out var actual) || actual.Bounds != bounds)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Capture input geometry could not be applied.");
        }
        finally { _captureGeometryWindow = previous; }
    }
    private void ReleaseCaptureInput(nint handle)
    {
        if (GetCapture() != handle) return;
        bool previous = _releasingCapture; _releasingCapture = true;
        try { ReleaseCapture(); }
        finally { _releasingCapture = previous; }
    }

    private void ReportCapture(CaptureContinuationState operation, string phase, string reason,
        CaptureRestoreStatus? status = null, nint eventForeground = 0, uint eventTime = 0,
        bool? userIntentChanged = null, bool? externalInputObserved = null)
    {
        if (phase == "Outcome") operation.OutcomeReported = true;
        ReportCapture(new(operation.OperationId, phase, reason, status ?? operation.Status, operation.OriginVersion,
            EditingSessionVersion, operation.ExpectedForeground, operation.ReadyForeground, GetForegroundWindow(), eventForeground, eventTime,
            operation.BoundaryTime, _captureIntentVersion, ForegroundLossVersion, ExternalCaptureInputVersion,
            userIntentChanged ?? _captureIntentVersion != operation.IntentVersion,
            externalInputObserved ?? ExternalCaptureInputVersion != operation.ExternalInputVersion)
        { Strategy = operation.Strategy, ExternalInputs = _keyboardIsolation?.GetCaptureInputObservationsAfter(operation.ExternalInputVersion, operation.StartTime) ?? [] });
    }
    private void ReportCaptureReceipt(CaptureRestoreReceipt receipt, string reason) =>
        ReportCapture(new(receipt.OperationId, "Receipt", reason, reason == "ReceiptConsumed" ?
            (receipt.WasEditing ? CaptureRestoreStatus.Restored : CaptureRestoreStatus.PassThrough) : CaptureRestoreStatus.Cancelled,
            receipt.OriginEditingSessionVersion, EditingSessionVersion, 0, 0, GetForegroundWindow(), 0, 0, 0,
            _captureIntentVersion, ForegroundLossVersion, ExternalCaptureInputVersion, _captureIntentVersion != receipt.IntentVersion,
            ExternalCaptureInputVersion != receipt.ExternalInputVersion)
        { ExternalInputs = _keyboardIsolation?.GetCaptureInputObservationsAfter(receipt.ExternalInputVersion) ?? [] });
    private void ReportCapture(CaptureDiagnosticEvent diagnostic)
    {
        if (CaptureDiagnostic is not { } handlers) return;
        foreach (Action<CaptureDiagnosticEvent> handler in handlers.GetInvocationList())
            try { handler(diagnostic); } catch (Exception) { /* Diagnostic failures cannot change capture behavior. */ }
    }
}
