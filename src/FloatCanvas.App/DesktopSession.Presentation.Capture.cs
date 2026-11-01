using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>One explicitly requested raster's immutable input; it owns no observer or source document.</summary>
internal sealed record PresentationCaptureSnapshot(PresentationFrame Frame, PointD Origin)
{
    internal void ApplyTo(SceneRenderer renderer)
    {
        renderer.PresentationFrame = Frame;
        renderer.PresentationOrigin = Origin;
    }
}

/// <summary>Guards the last shown input across the editing pause, without reading unpublished input.</summary>
internal sealed record PresentationCaptureState(EditorSession Owner, SceneDocument Document, Guid ResourceId,
    long ContentVersion, long ScopeVersion, PointD Origin, object Monitors, object Lifetime, PresentationFrame Frame)
{
    internal bool IsCurrent(EditorSession owner, PointD origin, object monitors, object? lifetime) =>
        ReferenceEquals(Owner, owner) && ReferenceEquals(Document, owner.Document) && ResourceId == owner.Document.ResourceId &&
        ContentVersion == owner.ContentVersion && ScopeVersion == owner.EditScopeVersion && Origin == origin &&
        ReferenceEquals(Monitors, monitors) && ReferenceEquals(Lifetime, lifetime);

    internal PresentationCaptureSnapshot Freeze() => new(Frame, Origin);
}

internal sealed partial class DesktopSession
{
    private PresentationCaptureState? _pausedPresentationCapture;

    private PresentationCaptureState? CurrentPresentationCapture(EditorSession target)
    {
        if (!ReferenceEquals(target, Active) || _presentationRuntime is not { } runtime) return null;
        var origin = _editor.ScreenOrigin; var monitors = _host.Monitors;
        // The producer may already have a newer, unpresented input frame. Only the accepted
        // preview frame is eligible, and its source plan must still describe this document.
        if (_presentedPresentationFrame is { } frame && ReferenceEquals(_presentedPresentationLifetime, runtime.Lifetime) &&
            _presentedPresentationRevision == target.PreviewRevision &&
            ReferenceEquals(_presentedPresentationPlan, _presentationContext) &&
            ReferenceEquals(_presentationOwner, target) && ReferenceEquals(_presentationDocument, target.Document) &&
            _presentationResource == target.Document.ResourceId && _presentationVersion == target.ContentVersion && _presentationPlanScope == target.EditScopeVersion &&
            _presentationPlanOrigin == origin && ReferenceEquals(_presentationPlanMonitors, monitors))
            return new(target, target.Document, target.Document.ResourceId, target.ContentVersion, target.EditScopeVersion,
                origin, monitors, runtime.Lifetime, frame);
        return _pausedPresentationCapture is { } paused && paused.IsCurrent(target, origin, monitors, runtime.Lifetime) ? paused : null;
    }

    private PresentationCaptureSnapshot? FreezePresentationCapture(EditorSession target) => CurrentPresentationCapture(target)?.Freeze();

    private PresentationCaptureState? CapturePresentationBeforePause(bool retain, PresentationRuntimeBroker runtime)
    {
        if (!retain) { _pausedPresentationCapture = null; return null; }
        return ReferenceEquals(runtime, _presentationRuntime) ? CurrentPresentationCapture(Active) : null;
    }

    private void CompletePresentationCapturePause(PresentationCaptureState? captured, PresentationRuntimeBroker runtime)
    {
        _pausedPresentationCapture = captured is not null && ReferenceEquals(runtime, _presentationRuntime) &&
            captured.IsCurrent(Active, _editor.ScreenOrigin, _host.Monitors, captured.Lifetime)
            ? captured with { Lifetime = runtime.Lifetime } : null;
    }
}
