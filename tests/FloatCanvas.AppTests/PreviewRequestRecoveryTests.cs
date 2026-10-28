using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class PreviewRequestRecoveryTests
{
    internal static void Run(Action<string, bool> check)
    {
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false);
        var key = new CanvasPreviewRequestKey(context, 1, 0, false);
        var state = new CanvasPreviewRequestState();
        check("request reservation deduplicates the same in-flight input", state.TryRequest(key, 1) && !state.TryRequest(key, 2));
        check("failed request releases its reservation and retries once", state.Fail(1, out bool retry) && retry && state.TryRequest(key, 2));
        check("late failure cannot invalidate newer in-flight input", !state.Fail(1, out _) && !state.TryRequest(key, 3));
        check("permanent failure stops automatic retries without blocking manual refresh", state.Fail(2, out retry) && !retry && state.TryRequest(key, 3));
        state.Presented(3);
        check("successful native presentation records the actual sequence", state.PresentedSequence == 3);

        var dispatcher = Dispatcher.CurrentDispatcher;
        state = new();
        int current = 1, attempts = 0, failures = 0;
        bool permanent = false;
        using var pump = new CanvasPreviewPump(dispatcher, () => new()
        {
            Context = context, SceneIdentity = context.Session, Document = new(), Resources = [], Outputs = [], ContentRevision = current
        }, snapshot =>
        {
            attempts++;
            if (snapshot.ContentRevision > 1 && (permanent || attempts == 2)) throw new InvalidOperationException("private render failure");
            return new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, 0)
            { Sequence = snapshot.Sequence, ContentRevision = snapshot.ContentRevision };
        });
        void Request()
        {
            if (state.TryRequest(key with { ContentRevision = current }, pump.RequestedSequence + 1)) pump.Request();
        }
        pump.FrameReady += () => state.Presented(pump.Frame!.Sequence);
        pump.RequestFailed += (_, sequence, _) =>
        {
            failures++;
            if (state.Fail(sequence, out bool autoRetry) && autoRetry) Request();
        };
        Request(); Phase2RenderTests.Until(() => !pump.Busy && pump.Frame?.ContentRevision == 1);
        current = 2; Request();
        Phase2RenderTests.Until(() => !pump.Busy && pump.Frame?.ContentRevision == 2);
        check("A success then unchanged B failure recovers B without another edit", failures == 1 && attempts == 3 && state.PresentedSequence == 3);
        permanent = true; current = 3; Request();
        Phase2RenderTests.Until(() => !pump.Busy && failures == 3);
        check("permanently failing input retains prior pixels and has a finite automatic budget", attempts == 5 && pump.Frame?.ContentRevision == 2);
        permanent = false; Request(); Phase2RenderTests.Until(() => !pump.Busy && pump.Frame?.ContentRevision == 3);
        check("ordinary refresh after permanent failure can present unchanged content", attempts == 6 && pump.Frame!.Sequence == state.PresentedSequence);

        int captureAttempts = 0, captureFailures = 0;
        long failedSequence = 0;
        using var capturing = new CanvasPreviewPump(dispatcher, () =>
        {
            if (++captureAttempts == 1) throw new InvalidOperationException("private synchronous capture failure");
            return new() { Context = context, SceneIdentity = context.Session, Document = new(), Resources = [], Outputs = [] };
        }, snapshot => new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, 0) { Sequence = snapshot.Sequence });
        capturing.RequestFailed += (_, sequence, _) => { captureFailures++; failedSequence = sequence; };
        capturing.Request();
        check("synchronous capture failure reports its exact reservation and clears busy", captureFailures == 1 && failedSequence == 1 && !capturing.Busy);
        capturing.Request(); Phase2RenderTests.Until(() => !capturing.Busy && capturing.Frame is not null);
        check("unchanged request can recover after capture failure", captureAttempts == 2 && capturing.Frame!.Sequence == 2);
    }
}
