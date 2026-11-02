using FloatCanvas.Core;
using FloatCanvas.Rendering;
using System.Windows.Threading;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private SceneDocument? presentedScene;
    private EvaluatedScene? presentedEvaluation;
    private SceneImageSnapshot? presentedImages;
    private WindowFrameSet? presentedWindowFrames;
    private object? presentedWindowOwnerContext;
    private AssetLibrary? presentedAssets;
    private TimeSpan presentedTime;
    private bool presentedPickingSuspended;
    private CancellationTokenSource? pendingPickingCancellation;
    private readonly SemaphoreSlim pickingWorker = new(1, 1);
    private SceneDocument? presentedOwner;
    private long presentedContentRevision = -1;
    private EvaluatedScene CurrentPickingEvaluation
    {
        get
        {
            if (presentedScene is not null && ReferenceEquals(presentedOwner, Session.Document) && presentedContentRevision == Session.PreviewRevision &&
                (presentedEvaluation ?? SceneEvaluation.RuntimeView(presentedScene)) is { } shown) return shown;
            // No reference/revision cache of editable DTOs: public callers may mutate
            // canvas settings or entities directly between pointer queries.
            return SceneEvaluation.Build(Session.Document);
        }
    }
    private SceneDocument PickingDocument => presentedScene ?? (viewDocument is { } display && viewPlacement is { } placement
        ? CanvasPreviewRenderer.EditingDocument(CurrentPickingEvaluation, display, placement,
            viewRootCanvas, Session.ActiveRootCanvas, Session.EditScopeId, independentCanvasView)
        : CurrentPickingEvaluation.RenderDocument);
    private AssetLibrary PickingAssets => presentedAssets ?? Session.Assets;
    private IEnumerable<CanvasElement> SelectedEditingWorldElements(bool editableOnly = false)
    {
        var selected = Session.SelectedWorldElements(editableOnly);
        if (!independentCanvasView) return selected;
        var visible = PickingDocument.Elements.Where(element => element.Visible).Select(element => element.Id).ToHashSet();
        return selected.Select(element => { element.Visible = visible.Contains(element.Id); return element; });
    }
    internal bool BackgroundPickingEnabled { get; set; }
    internal Task PendingPicking { get; private set; } = Task.CompletedTask;

    internal void ApplyPresentedScene(SceneDocument? document, SceneImageSnapshot? images, TimeSpan time,
        IReadOnlyList<AssetResource>? resources = null, long? contentRevision = null,
        WindowFrameSet? windowFrames = null, object? windowOwnerContext = null, EvaluatedScene? evaluation = null)
    {
        presentedPickingSuspended = false;
        var retained = images?.Retain();
        var windowRetained = document is not null && windowFrames is not null && ReferenceEquals(windowFrames.OwnerContext, windowOwnerContext)
            ? windowFrames.Retain() : null;
        presentedImages?.Dispose(); presentedImages = retained;
        presentedWindowFrames?.Dispose(); presentedWindowFrames = windowRetained;
        presentedWindowOwnerContext = windowRetained?.OwnerContext;
        presentedScene = document; presentedTime = time; presentedEvaluation = evaluation;
        presentedOwner = contentRevision is not null ? Session.Document : null;
        presentedContentRevision = contentRevision ?? -1;
        presentedAssets = document is null ? null : new AssetLibrary();
        if (presentedAssets is not null)
            foreach (var resource in resources ?? Session.Assets.Resources.ToArray()) presentedAssets.Add(resource);
        if (selectionRenderer is not null)
        {
            selectionRenderer.PresentedImages = retained;
            selectionRenderer.WindowFrames = windowRetained; selectionRenderer.WindowOwnerContext = presentedWindowOwnerContext;
        }
    }

    internal void ResumePresentedPicking() => presentedPickingSuspended = false;

    private bool OwnsPointerTransaction => ReferenceEquals(_pointerOwnedSession, Session) && Session.IsTransactionActive &&
        Session.TransactionId == _pointerOwnedTransaction;

    private void BeginPointerTransaction(string label)
    {
        Session.BeginTransaction(label);
        _pointerOwnedSession = Session; _pointerOwnedTransaction = Session.TransactionId;
    }

    internal void SuspendPresentedPicking()
    {
        presentedPickingSuspended = true;
        CancelPicking();
        if (gesture is not null)
        {
            bool owns = ReferenceEquals(_pointerOwnedSession, Session) && Session.IsTransactionActive &&
                Session.TransactionId == _pointerOwnedTransaction;
            CancelGesture(rollback: owns, notify: owns);
        }
    }

    internal void ClearPresentedScene()
    {
        CancelPicking();
        presentedScene = null;
        presentedEvaluation = null;
        presentedOwner = null; presentedContentRevision = -1;
        presentedAssets = null;
        presentedImages?.Dispose(); presentedImages = null;
        presentedWindowFrames?.Dispose(); presentedWindowFrames = null; presentedWindowOwnerContext = null;
        if (selectionRenderer is not null)
        { selectionRenderer.PresentedImages = null; selectionRenderer.WindowFrames = null; selectionRenderer.WindowOwnerContext = null; }
    }

    private void CancelPicking()
    {
        pendingPickingCancellation?.Cancel(); pendingPickingCancellation?.Dispose(); pendingPickingCancellation = null;
        if (!_replayingPointer) CancelPointerPicking();
    }

    private async Task SelectScreenRectangleAsync(RectD rectangle, bool additive)
    {
        CancelPicking();
        var cancellation = pendingPickingCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var session = Session;
        long revision = session.PreviewRevision;
        long scopeVersion = session.EditScopeVersion;
        var rootCanvas = PickingRootCanvas;
        var selected = session.Selection.ToArray();
        var document = PickingDocument;
        var resources = PickingAssets.Resources.ToArray();
        using var images = presentedImages?.Retain();
        using var windowFrames = presentedWindowFrames?.Retain();
        var windowOwnerContext = presentedWindowOwnerContext;
        var time = presentedScene is null ? AnimationTime : presentedTime;
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            var hits = await Task.Run(async () =>
            {
                await pickingWorker.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var assets = new AssetLibrary(); foreach (var resource in resources) assets.Add(resource);
                    using var renderer = new SceneRenderer { AnimationTime = time, PresentedImages = images,
                        WindowFrames = windowFrames, WindowOwnerContext = windowOwnerContext, CacheByteLimit = 64L * 1024 * 1024 };
                    return ScreenRectangleHits(document, assets, renderer, rectangle, token, rootCanvas);
                }
                finally { pickingWorker.Release(); }
            }, token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(Session, session) && session.PreviewRevision == revision && session.EditScopeVersion == scopeVersion &&
                    session.Selection.SetEquals(selected)) ApplyRectangleHits(hits, additive, selected);
            }, DispatcherPriority.Input);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!dispatcher.HasShutdownStarted)
                await dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) Message?.Invoke(error.Message); });
        }
    }
}
