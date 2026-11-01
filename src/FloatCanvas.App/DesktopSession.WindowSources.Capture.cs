using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>One explicit raster operation's owned, immutable media frames. Never retains a native capture service.</summary>
internal sealed class WindowRasterCapture : IDisposable
{
    private WindowFrameSet? frames;
    internal WindowFrameSet Frames => frames ?? throw new ObjectDisposedException(nameof(WindowRasterCapture));
    internal object OwnerContext { get; }
    internal WindowRasterCapture(WindowFrameSet frames, object ownerContext)
    {
        ArgumentNullException.ThrowIfNull(frames); ArgumentNullException.ThrowIfNull(ownerContext);
        if (!ReferenceEquals(frames.OwnerContext, ownerContext)) throw new ArgumentException("The window bundle belongs to a different render owner.", nameof(ownerContext));
        this.frames = frames.Retain(); OwnerContext = ownerContext;
    }
    internal WindowRasterCapture Retain() => new(Frames, OwnerContext);
    internal void ApplyTo(SceneRenderer renderer)
    { renderer.WindowFrames = Frames; renderer.WindowOwnerContext = OwnerContext; }
    internal WindowFrameLease? RetainFrame(Guid resourceId, SourceNodeAddress address, Guid intent) =>
        Frames.TryGet(new(resourceId, address), out var frame) && frame.BindingIntent == intent ? frame.Retain() : null;
    public void Dispose() => Interlocked.Exchange(ref frames, null)?.Dispose();
}

internal sealed partial class DesktopSession
{
    /// <summary>Freeze only already shown media. Missing/stale presentation deliberately uses the authored static fallback.</summary>
    private WindowRasterCapture? FreezeWindowSourceCapture(EditorSession target)
    {
        if (_disposed || _documentStopped || _documentExitRequested || !ReferenceEquals(target, Active) || target.IsTransactionActive ||
            _windowRuntime is null || _presentedWindowFrames is not { } frames || _presentedCanvasContext is not { } shown) return null;
        var epoch = CurrentWindowRenderEpoch();
        if (epoch is null || !ReferenceEquals(_presentedWindowOwnerContext, epoch) || !ReferenceEquals(frames.OwnerContext, epoch) ||
            !ReferenceEquals(_windowPlanSession, target) || !ReferenceEquals(_windowPlanDocument, target.Document) ||
            _windowPlanVersion != target.ContentVersion || _windowPlanScope != target.EditScopeVersion ||
            _windowPlanOrigin != _editor.ScreenOrigin || !ReferenceEquals(_windowPlanMonitors, _host.Monitors) ||
            !ReferenceEquals(shown.Session, target) || !ReferenceEquals(shown.DocumentEpoch, target.Document) ||
            shown.Origin != _editor.ScreenOrigin || !ReferenceEquals(shown.Monitors, _host.Monitors) || !ReferenceEquals(shown.WindowEpoch, epoch)) return null;
        // A resource ID can change before its first authored notification. Never
        // relabel a shown bundle merely because the mutable document reference survived.
        if (!_windowDemands.Any(demand => frames.TryGet(new(target.Document.ResourceId, demand.Source), out var frame) && frame.BindingIntent == demand.BindingIntent)) return null;
        return new(frames, epoch);
    }
}
