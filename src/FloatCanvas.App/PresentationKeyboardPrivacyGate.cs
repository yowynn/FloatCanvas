using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>UI-owned password metadata gate. Unknown metadata keeps the explicitly selected key display policy.</summary>
internal sealed class PresentationKeyboardPrivacyGate : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly PresentationRuntimeBroker runtime;
    private readonly PresentationKeyboardPrivacyProbe probe;
    private readonly Func<nint> foreground;
    private readonly Func<long> milliseconds;
    private readonly DispatcherTimer timer;
    private long requestId, sourceEpoch = -1, requestedAt = long.MinValue, pendingId;
    private nint window;
    private object? requestedLifetime;
    private bool disposed, active;

    internal PresentationKeyboardPrivacyGate(Dispatcher dispatcher, PresentationRuntimeBroker runtime,
        Func<Action<long, nint, bool?>, PresentationKeyboardPrivacyProbe>? createProbe = null,
        Func<nint>? foreground = null, Func<long>? milliseconds = null)
    {
        this.dispatcher = dispatcher; this.runtime = runtime;
        this.foreground = foreground ?? GetForegroundWindow;
        this.milliseconds = milliseconds ?? (() => Environment.TickCount64);
        probe = createProbe?.Invoke(Completed) ?? new(Completed);
        timer = new(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => Observe();
    }

    internal void Refresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        if (!runtime.ObservingKeyboard)
        {
            if (active) { ++requestId; pendingId = 0; sourceEpoch = -1; requestedLifetime = null; window = 0; }
            active = false; timer.Stop(); return;
        }
        active = true; timer.Start();
    }

    internal void Observe()
    {
        dispatcher.VerifyAccess(); Refresh(); if (!active) return;
        long now = milliseconds(); nint current = foreground();
        if (sourceEpoch != runtime.InputEpoch || window != current)
        {
            sourceEpoch = runtime.InputEpoch; window = current;
            ++requestId; pendingId = 0; requestedAt = long.MinValue; requestedLifetime = null;
            // Starting a source already clears its old keyboard state. Retire
            // metadata requests here without invalidating the whole canvas again;
            // scope changes and confirmed password protection remain strict.
            runtime.RefreshKeyboardForFocusChange();
        }
        if (pendingId != 0 && now - requestedAt >= 200)
        {
            ++requestId; pendingId = 0;
            runtime.SuppressKeyboard(false);
        }
        if (pendingId != 0 || (requestedAt != long.MinValue && now - requestedAt < 250)) return;
        pendingId = ++requestId; requestedAt = now; requestedLifetime = runtime.Lifetime;
        probe.Request(pendingId, current);
    }

    private void Completed(long id, nint expectedWindow, bool? password)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        try { dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Accept(id, expectedWindow, password))); }
        catch (InvalidOperationException) { }
    }

    private void Accept(long id, nint expectedWindow, bool? password)
    {
        if (disposed || !active || id != pendingId || id != requestId || !runtime.ObservingKeyboard ||
            sourceEpoch != runtime.InputEpoch || window != expectedWindow || foreground() != expectedWindow ||
            !ReferenceEquals(requestedLifetime, runtime.Lifetime)) return;
        pendingId = 0;
        runtime.SuppressKeyboard(password == true);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    public void Dispose()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        disposed = true; active = false; ++requestId; pendingId = 0; timer.Stop(); probe.Dispose();
    }
}
