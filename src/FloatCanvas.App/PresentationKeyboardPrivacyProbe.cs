using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace FloatCanvas.App;

/// <summary>One lazy MTA query worker. A stalled accessibility provider cannot accumulate workers or block disposal.</summary>
internal sealed class PresentationKeyboardPrivacyProbe : IDisposable
{
    private readonly record struct RequestInfo(long Id, nint Foreground);
    private readonly object gate = new();
    private readonly Action<long, nint, bool?> completed;
    private readonly Func<bool?> query;
    private readonly Func<nint> foreground;
    private readonly TaskCompletionSource termination = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RequestInfo? pending;
    private Thread? worker;
    private bool disposed;

    internal Task Completed => termination.Task;

    internal PresentationKeyboardPrivacyProbe(Action<long, nint, bool?> completed, Func<bool?>? query = null, Func<nint>? foreground = null)
    {
        this.completed = completed ?? throw new ArgumentNullException(nameof(completed));
        this.query = query ?? QueryPasswordProperty;
        this.foreground = foreground ?? GetForegroundWindow;
    }

    internal void Request(long requestId, nint expectedForeground)
    {
        lock (gate)
        {
            if (disposed) return;
            pending = new(requestId, expectedForeground);
            if (worker is null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "FloatCanvas presentation privacy" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
            Monitor.Pulse(gate);
        }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                RequestInfo request;
                lock (gate)
                {
                    while (!disposed && pending is null) Monitor.Wait(gate);
                    if (disposed) return;
                    request = pending!.Value; pending = null;
                }
                bool? result = null;
                try
                {
                    if (request.Foreground != 0 && foreground() == request.Foreground)
                    {
                        result = query();
                        if (foreground() != request.Foreground) result = null;
                    }
                }
                catch { result = null; }
                if (Volatile.Read(ref disposed)) return;
                // The owner rechecks request ID/epoch on its dispatcher. Never hold the request gate across external code.
                try { completed(request.Id, request.Foreground, result); }
                catch { /* A callback failure must not terminate the background process or create another worker. */ }
            }
        }
        finally { termination.TrySetResult(); }
    }

    private static bool? QueryPasswordProperty()
    {
        var element = AutomationElement.FocusedElement;
        return element?.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is bool value ? value : null;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Volatile.Write(ref disposed, true); pending = null; Monitor.PulseAll(gate);
            if (worker is null) termination.TrySetResult();
        }
    }
}
