using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>UI-owned password metadata gate. Unclassified focus never publishes keyboard input.</summary>
internal sealed class PresentationKeyboardPrivacyGate : IDisposable
{
    private const uint EventSystemForeground = 0x0003, EventObjectFocus = 0x8005;
    private const uint WinEventOutOfContext = 0;
    private readonly Dispatcher dispatcher;
    private readonly PresentationRuntimeBroker runtime;
    private readonly PresentationKeyboardPrivacyProbe probe;
    private readonly Func<nint> foreground;
    private readonly Func<nint, bool> isCanvasInput;
    private readonly Func<long> milliseconds;
    private readonly DispatcherTimer timer;
    private readonly bool externalEventsReliable;
    private long requestId, sourceEpoch = -1, requestedAt = long.MinValue, pendingId;
    private nint window;
    private object? requestedLifetime;
    private readonly WinEventProcedure winEventProcedure;
    private nint foregroundHook, focusHook;
    private bool disposed, active, canvasInput;
    private int focusEventPending;

    internal PresentationKeyboardPrivacyGate(Dispatcher dispatcher, PresentationRuntimeBroker runtime,
        Func<Action<long, nint, bool?>, PresentationKeyboardPrivacyProbe>? createProbe = null,
        Func<nint>? foreground = null, Func<long>? milliseconds = null, bool externalEvents = true,
        Func<nint, bool>? isCanvasInput = null)
    {
        this.dispatcher = dispatcher; this.runtime = runtime;
        this.foreground = foreground ?? GetForegroundWindow;
        this.isCanvasInput = isCanvasInput ?? (_ => false);
        this.milliseconds = milliseconds ?? (() => Environment.TickCount64);
        probe = createProbe?.Invoke(Completed) ?? new(Completed);
        timer = new(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Observe();
        winEventProcedure = OnWinEvent;
        if (externalEvents)
        {
            foregroundHook = RegisterHook(EventSystemForeground);
            focusHook = RegisterHook(EventObjectFocus);
        }
        externalEventsReliable = !externalEvents || foregroundHook != 0 && focusHook != 0;
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
        bool resumed = !active;
        active = true; timer.Start();
        if (resumed) runtime.SuppressKeyboard(true);
    }

    internal void Observe() => Observe(externalFocusEvent: false);

    private void Observe(bool externalFocusEvent)
    {
        dispatcher.VerifyAccess(); Refresh(); if (!active) return;
        long now = milliseconds(); nint current = foreground();
        bool focusChanged = externalFocusEvent || Interlocked.Exchange(ref focusEventPending, 0) != 0;
        bool knownCanvas = current != 0 && externalEventsReliable && isCanvasInput(current);
        if (sourceEpoch != runtime.InputEpoch || window != current || focusChanged || canvasInput != knownCanvas ||
            requestedLifetime is not null && !ReferenceEquals(requestedLifetime, runtime.Lifetime))
        {
            sourceEpoch = runtime.InputEpoch; window = current; canvasInput = knownCanvas;
            ++requestId; pendingId = 0; requestedAt = long.MinValue; requestedLifetime = null;
            runtime.SuppressKeyboard(!knownCanvas);
            runtime.RefreshKeyboardForFocusChange();
        }
        if (knownCanvas) return;
        if (pendingId != 0 || requestedAt != long.MinValue && now - requestedAt < 250) return;
        pendingId = ++requestId; requestedAt = now; requestedLifetime = runtime.Lifetime;
        probe.Request(pendingId, current);
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (disposed || !active || Interlocked.Exchange(ref focusEventPending, 1) != 0) return;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (Interlocked.Exchange(ref focusEventPending, 0) != 0 && !disposed && active) Observe(externalFocusEvent: true);
            }));
        }
        catch (InvalidOperationException) { Volatile.Write(ref focusEventPending, 0); }
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
            Volatile.Read(ref focusEventPending) != 0 ||
            sourceEpoch != runtime.InputEpoch || window != expectedWindow || foreground() != expectedWindow ||
            !ReferenceEquals(requestedLifetime, runtime.Lifetime)) return;
        pendingId = 0; requestedLifetime = null;
        if (password is { } known) runtime.SuppressKeyboard(known || !externalEventsReliable);
    }

    private nint RegisterHook(uint eventType) =>
        HookRegistrationForTesting?.Invoke(eventType) ?? SetWinEventHook(eventType, eventType, 0,
            winEventProcedure, 0, 0, WinEventOutOfContext);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    internal static Func<uint, nint>? HookRegistrationForTesting { get; set; }
    public void Dispose()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        disposed = true; active = false; ++requestId; pendingId = 0; timer.Stop(); probe.Dispose();
        if (foregroundHook != 0) { UnhookWinEvent(foregroundHook); foregroundHook = 0; }
        if (focusHook != 0) { UnhookWinEvent(focusHook); focusHook = 0; }
    }

    private delegate void WinEventProcedure(nint hook, uint eventType, nint hwnd, int objectId, int childId,
        uint eventThread, uint eventTime);
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProcedure callback,
        uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
}
