using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>Observes queue delay without relying on the dispatcher to report that it is blocked.
/// One outstanding probe, bounded diagnostic work, and no input or process-exit action.</summary>
internal sealed class DispatcherResponsivenessMonitor : IDisposable
{
    internal const long DelayThresholdMilliseconds = 500;
    private readonly object gate = new();
    private readonly Func<Action, bool> post;
    private readonly Func<long> clock;
    private readonly Action<DispatcherDelaySample> report;
    private readonly System.Threading.Timer? timer;
    private bool disposed, pending, delayed;
    private long sequence, sentAt;

    internal DispatcherResponsivenessMonitor(Dispatcher dispatcher, ApplicationDiagnostics diagnostics)
        : this(action =>
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return false;
            dispatcher.BeginInvoke(DispatcherPriority.Input, action); return true;
        }, () => Environment.TickCount64, DiagnosticReporter(diagnostics))
    {
        timer = new(_ => Check(), null, 250, 250);
    }

    // The same queue/time seam supports deterministic delayed/recovered/late-callback tests.
    internal DispatcherResponsivenessMonitor(Func<Action, bool> post, Func<long> clock, Action<DispatcherDelaySample> report)
    { this.post = post; this.clock = clock; this.report = report; }

    internal void Check()
    {
        long request = 0;
        DispatcherDelaySample? sample = null;
        lock (gate)
        {
            if (disposed) return;
            long now = clock();
            if (pending)
            {
                if (!delayed && now - sentAt >= DelayThresholdMilliseconds)
                { delayed = true; sample = new(sequence, "Delayed", now - sentAt, true); }
            }
            else { pending = true; delayed = false; sentAt = now; request = ++sequence; }
        }
        if (sample is not null) report(sample);
        if (request == 0) return;
        try
        {
            if (post(() => Complete(request))) return;
        }
        catch (InvalidOperationException) { } // Dispatcher shutdown can race the enqueue.
        lock (gate) if (sequence == request) pending = false;
    }

    private void Complete(long request)
    {
        DispatcherDelaySample? sample = null;
        lock (gate)
        {
            if (disposed || !pending || sequence != request) return;
            long elapsed = Math.Max(0, clock() - sentAt);
            if (delayed || elapsed >= DelayThresholdMilliseconds) sample = new(request, "Recovered", elapsed, delayed);
            pending = delayed = false;
        }
        if (sample is not null) report(sample);
    }

    private static Action<DispatcherDelaySample> DiagnosticReporter(ApplicationDiagnostics diagnostics)
    {
        var gate = new object();
        bool writing = false;
        DispatcherDelaySample? latest = null;
        return sample =>
        {
            // Never write files from the dispatcher or accumulate disk work during a stall.
            lock (gate)
            {
                latest = sample;
                if (writing) return;
                writing = true;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (true)
                {
                    DispatcherDelaySample? next;
                    lock (gate)
                    {
                        next = latest; latest = null;
                        if (next is null) { writing = false; return; }
                    }
                    diagnostics.Write("UI.Dispatcher.Delay", next);
                }
            });
        };
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; pending = delayed = false; }
        timer?.Dispose();
    }
}

internal sealed record DispatcherDelaySample(long Probe, string State, long DelayMilliseconds, bool ObservedWhilePending);
