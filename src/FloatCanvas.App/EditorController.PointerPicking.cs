using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    // Coalesce only transform/selection movement. Eraser samples and all button edges
    // retain order. An overloaded stream cancels its uncommitted gesture as one unit.
    private const int MaximumPendingPointerEvents = 256;
    private readonly List<(OverlayPointerEvent Event, bool Properties)> _pendingPointerEvents = [];
    private CancellationTokenSource? _pointerPickingCancellation;
    private bool _pointerPickingRunning, _replayingPointer;
    private long _pointerQueueEpoch;
    private EditorSession? _pointerOwnedSession;
    private long _pointerOwnedTransaction;
    private PointPreflight? _pointPreflight;
    private sealed record PointPreflight(PointD Point, Dictionary<double, Guid?> Hits, Dictionary<(Guid, double), bool> Ink);

    private bool PointerNeedsPicking(OverlayPointerEvent e)
    {
        if (Tool == EditorTool.Canvas) return false;
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick) return true;
        if (gesture == "erase") return e.Kind is OverlayPointerKind.Move or OverlayPointerKind.Up;
        if (gesture == "move" && shiftDeselectOnClick) return e.Kind == OverlayPointerKind.Up;
        if (gesture is not null || Tool == EditorTool.Canvas) return false;
        var point = new PointD(e.Position.X - ScreenOrigin.X, e.Position.Y - ScreenOrigin.Y);
        return Tool == EditorTool.Text && BillboardMode || !CanvasMapping.Create(Session.Document).TryToPlane(point, out _);
    }

    private bool QueueBackgroundPointer(OverlayPointerEvent e, bool properties)
    {
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            pendingPickingCancellation?.Cancel();
            pendingPickingCancellation?.Dispose(); pendingPickingCancellation = null;
        }
        if (e.Kind == OverlayPointerKind.Cancel || e.Button == OverlayMouseButton.Right && e.Kind == OverlayPointerKind.Down)
        {
            CancelPointerPicking(); return false;
        }
        bool needs = PointerNeedsPicking(e);
        // New unselected pen strokes need no ink query. Keep their native input path immediate.
        if (gesture is null && IsCreationTool(Tool) && Tool != EditorTool.Text && Session.Selection.Count == 0 && !properties &&
            CanvasMapping.Create(Session.Document).TryToPlane(new(e.Position.X - ScreenOrigin.X, e.Position.Y - ScreenOrigin.Y), out _)) needs = false;
        if (!_pointerPickingRunning && !needs) return false;
        if (_pendingPointerEvents.Count > 0 && e.Kind == OverlayPointerKind.Move &&
            (Tool == EditorTool.Select || gesture is "move" or "resize" or "rotate" or "endpoint" or "marquee") &&
            _pendingPointerEvents[^1].Event.Kind == OverlayPointerKind.Move)
            _pendingPointerEvents[^1] = (e, properties);
        else _pendingPointerEvents.Add((e, properties));
        if (_pendingPointerEvents.Count > MaximumPendingPointerEvents)
        {
            AbortPointerPicking(); Message?.Invoke(L.Get("Editor.PickingCancelled")); return true;
        }
        if (!_pointerPickingRunning) StartPointerDrain();
        return true;
    }

    private void StartPointerDrain()
    {
        _pointerPickingRunning = true;
        // Enter through the dispatcher even in a standalone host without an installed
        // SynchronizationContext. Every continuation and model mutation then stays on UI.
        PendingPicking = Dispatcher.CurrentDispatcher.InvokeAsync(DrainPointerEventsAsync, DispatcherPriority.Input).Task.Unwrap();
    }

    private void CancelPointerPicking()
    {
        _pointerPickingCancellation?.Cancel();
        _pointerQueueEpoch++;
        _pendingPointerEvents.Clear();
    }

    private void AbortPointerPicking()
    {
        CancelPointerPicking();
        // A preflight has no transaction of its own. In particular, a late result
        // must not roll back a newer color/inspector edit started while it waited.
        if (ReferenceEquals(_pointerOwnedSession, Session) && Session.IsTransactionActive &&
            Session.TransactionId == _pointerOwnedTransaction) Cancel();
    }

    private async Task DrainPointerEventsAsync()
    {
        _pointerPickingRunning = true;
        using var cancellation = new CancellationTokenSource();
        _pointerPickingCancellation = cancellation;
        var token = cancellation.Token;
        long queueEpoch = _pointerQueueEpoch;
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            while (_pendingPointerEvents.Count > 0 && !token.IsCancellationRequested)
            {
                var (input, properties) = _pendingPointerEvents[0]; _pendingPointerEvents.RemoveAt(0);
                var session = Session; long revision = session.PreviewRevision;
                var selection = session.Selection.ToArray(); var selectedTool = Tool;
                var origin = ScreenOrigin;
                if (PointerNeedsPicking(input))
                {
                    var point = new PointD(input.Position.X - origin.X, input.Position.Y - origin.Y);
                    var document = presentedScene ?? session.Document.Clone();
                    var resources = PickingAssets.Resources.ToArray();
                    using var images = presentedImages?.Retain();
                    var time = presentedScene is null ? AnimationTime : presentedTime;
                    _pointPreflight = await Task.Run(async () =>
                    {
                        await pickingWorker.WaitAsync(token).ConfigureAwait(false);
                        try { return PreflightPoint(document, resources, images, time, point, token); }
                        finally { pickingWorker.Release(); }
                    }, token);
                    if (token.IsCancellationRequested) break;
                    if (!ReferenceEquals(session, Session) || session.PreviewRevision != revision || ScreenOrigin != origin ||
                        Tool != selectedTool || !session.Selection.SetEquals(selection))
                    { AbortPointerPicking(); Message?.Invoke(L.Get("Editor.PickingCancelled")); break; }
                }
                _replayingPointer = true;
                try
                {
                    PointerForCanvas(input, properties);
                }
                finally { _replayingPointer = false; _pointPreflight = null; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!dispatcher.HasShutdownStarted) { AbortPointerPicking(); Message?.Invoke(error.Message); }
        }
        finally
        {
            _pointPreflight = null;
            if (_pointerQueueEpoch == queueEpoch) _pendingPointerEvents.Clear();
            _pointerPickingRunning = false;
            if (ReferenceEquals(_pointerPickingCancellation, cancellation)) _pointerPickingCancellation = null;
            if (_pendingPointerEvents.Count > 0) StartPointerDrain();
        }
    }

    private static PointPreflight PreflightPoint(SceneDocument document, AssetResource[] resources, SceneImageSnapshot? images,
        TimeSpan time, PointD point, CancellationToken token)
    {
        var assets = new AssetLibrary(); foreach (var resource in resources) assets.Add(resource);
        using var renderer = new SceneRenderer { AnimationTime = time, PresentedImages = images, CacheByteLimit = 64L * 1024 * 1024 };
        var hits = new Dictionary<double, Guid?>(); var ink = new Dictionary<(Guid, double), bool>();
        foreach (double tolerance in new double[] { 4, 7, 10 })
        {
            token.ThrowIfCancellationRequested();
            if (CanvasDepthOrder.IsEnabled(document)) hits[tolerance] = renderer.PickDepth(document, assets, point, tolerance, token)?.Id;
            else
            {
                Guid? top = null;
                foreach (var element in document.Elements)
                {
                    token.ThrowIfCancellationRequested();
                    bool hit = !element.Locked && ElementHitTestScreenCore(document, assets, renderer, element, point, tolerance, token);
                    ink[(element.Id, tolerance)] = hit;
                    if (hit) top = element.Id;
                }
                hits[tolerance] = top;
            }
        }
        return new(point, hits, ink);
    }
}
