using FloatCanvas.Core;
using FloatCanvas.Rendering;
using System.Windows.Threading;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private SceneDocument? presentedScene;
    private SceneImageSnapshot? presentedImages;
    private AssetLibrary? presentedAssets;
    private TimeSpan presentedTime;
    private bool presentedPickingSuspended;
    private CancellationTokenSource? pendingPickingCancellation;
    private readonly SemaphoreSlim pickingWorker = new(1, 1);
    private SceneDocument PickingDocument => presentedScene ?? Session.Document;
    private AssetLibrary PickingAssets => presentedAssets ?? Session.Assets;
    internal bool BackgroundPickingEnabled { get; set; }
    internal Task PendingPicking { get; private set; } = Task.CompletedTask;

    internal void ApplyPresentedScene(SceneDocument? document, SceneImageSnapshot? images, TimeSpan time,
        IReadOnlyList<AssetResource>? resources = null)
    {
        presentedPickingSuspended = false;
        var retained = images?.Retain();
        presentedImages?.Dispose(); presentedImages = retained;
        presentedScene = document; presentedTime = time;
        presentedAssets = document is null ? null : new AssetLibrary();
        if (presentedAssets is not null)
            foreach (var resource in resources ?? Session.Assets.Resources.ToArray()) presentedAssets.Add(resource);
        if (selectionRenderer is not null) selectionRenderer.PresentedImages = retained;
    }

    internal void ResumePresentedPicking() => presentedPickingSuspended = false;

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
        presentedAssets = null;
        presentedImages?.Dispose(); presentedImages = null;
        if (selectionRenderer is not null) selectionRenderer.PresentedImages = null;
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
        var selected = session.Selection.ToArray();
        var document = presentedScene ?? session.Document.Clone();
        var resources = PickingAssets.Resources.ToArray();
        using var images = presentedImages?.Retain();
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
                    using var renderer = new SceneRenderer { AnimationTime = time, PresentedImages = images, CacheByteLimit = 64L * 1024 * 1024 };
                    return ScreenRectangleHits(document, assets, renderer, rectangle, token);
                }
                finally { pickingWorker.Release(); }
            }, token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(Session, session) && session.PreviewRevision == revision &&
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
