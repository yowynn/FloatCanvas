using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FloatCanvas.App;

internal static class ProjectOpenDispatchTests
{
    internal static void Run(Action<string, bool> check)
    {
        var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var queue = new Queue<DesktopSession.ProjectOpenRequest>();
        void Set(string name, object value) => typeof(DesktopSession).GetField(name, flags)!.SetValue(session, value);
        bool Get(string name) => (bool)typeof(DesktopSession).GetField(name, flags)!.GetValue(session)!;
        Set("_projectOpenRequests", queue); Set("_shellReady", true);
        Set("_documentExitRequestedValue", true);
        string path = Path.Combine(Path.GetTempPath(), "pending request.floatcanvas");
        check("exiting session rejects a project without scheduling it", !session.QueueProjectOpen(path) && queue.Count == 0);
        Set("_documentExitRequestedValue", false);
        Set("_documentBusyValue", true);
        using var lifetime = new CancellationTokenSource();
        Set("_documentLifetime", lifetime);
        check("project handoff acknowledges its normalized queued path without requesting edit mode", session.QueueProjectOpen(path) &&
            queue.SequenceEqual([new DesktopSession.ProjectOpenRequest(Path.GetFullPath(path), false)]));
        check("project handoff does not execute the asynchronous drain's synchronous prefix", !Get("_openingShellProject"));
        Set("_documentExitRequestedValue", true);
        PumpOnce();
        check("a deferred request remains queued if shutdown begins before its drain", queue.Count == 1 && !Get("_openingShellProject"));
        ModalPrefixAcknowledgement(check);
    }

    private static void ModalPrefixAcknowledgement(Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var modal = new DispatcherFrame();
        var outer = new DispatcherFrame();
        bool prefixEntered = false, prefixExited = false, acknowledgedBeforeExit = false;
        var received = new Queue<string>();
        string id = Guid.NewGuid().ToString("N");
        using var server = new ProjectActivation(id, async path => await dispatcher.InvokeAsync(() =>
        {
            received.Enqueue(path);
            DesktopSession.ScheduleProjectOpenDrain(() =>
            {
                prefixEntered = true;
                Dispatcher.PushFrame(modal); // A modal-like blocking prefix before the first await.
                prefixExited = true;
                return Task.CompletedTask;
            });
            return true;
        }));
        string path = Path.Combine(Path.GetTempPath(), "ack before confirmation.floatcanvas");
        Task<bool> forwarding = Task.Run(() => ProjectActivation.ForwardAsync(id, path));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (prefixEntered && forwarding.IsCompleted)
            {
                acknowledgedBeforeExit = !prefixExited && forwarding.IsCompletedSuccessfully && forwarding.Result;
                modal.Continue = false; outer.Continue = false; timer.Stop();
            }
            else if (clock.Elapsed > TimeSpan.FromSeconds(6))
            { modal.Continue = false; outer.Continue = false; timer.Stop(); }
        };
        timer.Start(); Dispatcher.PushFrame(outer); timer.Stop();
        check("real activation pipe acknowledges before a deferred modal prefix completes", acknowledgedBeforeExit && prefixEntered && prefixExited);
        check("acknowledged modal request retains the exact normalized path", received.SequenceEqual([Path.GetFullPath(path)]));
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
