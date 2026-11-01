using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>One UI-owned runtime. Native transitions and fading never call an EditorSession.</summary>
internal sealed class PresentationRuntimeBroker : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Action changed;
    private readonly Func<long, bool, bool, CancellationToken, Task<IPresentationInputSource>> start;
    private readonly Func<TimeSpan> clock;
    private readonly Func<bool> isInternalInput;
    private readonly PresentationTimeline timeline = new();
    private readonly DispatcherTimer animation;
    private IPresentationInputSource? input;
    private CancellationTokenSource? starting;
    private PresentationRuntimeDemand demand = PresentationRuntimeDemand.Empty;
    private object? context;
    private long connection, epoch, sequence;
    private int notification;
    private bool allowed, disposed, faulted;
    private PresentationKeyboardScope keyboardScope;
    private bool keyboardScopeAllowed = true;
    private bool editingInput;
    private bool changingInputRoute;
    private long rawTransitionCutoff = -1;
    internal long EditingInputEpoch { get; private set; }
    private string pausedStatus = "Presentation.Status.PausedEditing";
    internal bool Armed { get; private set; }
    internal bool Observing => input?.IsRunning == true && allowed && Armed;
    internal bool ObservingKeyboard => Observing && demand.Keyboard && KeyboardScopeAllowsInput();
    internal bool HasKeyboardAnimation => Frame.RecentChords.Any(chord => !chord.IsHeld);
    internal long InputEpoch => input?.Epoch ?? -1;
    internal string StatusKey => !Armed ? "Presentation.Status.Stopped" : faulted ? "Presentation.Status.Unavailable" : !allowed ? pausedStatus :
        !demand.Any ? "Presentation.Status.NoLayers" : starting is not null ? "Presentation.Status.Starting" :
        demand.Keyboard && !KeyboardScopeAllowsInput() ? "Presentation.Status.OutsideScope" : timeline.KeyboardSuppressed ?
        "Presentation.Status.KeyboardProtected" : Observing ? "Presentation.Status.Running" : "Presentation.Status.Unavailable";
    internal object Lifetime { get; private set; } = new();
    internal PresentationFrame Frame { get; private set; } = PresentationFrame.Empty;
    internal PresentationFrame? PausedTrail { get; private set; }
    internal event Action<PresentationObservedBatch>? BeforeInput;

    internal PresentationRuntimeBroker(Dispatcher dispatcher, Action changed,
        Func<long, bool, bool, CancellationToken, Task<IPresentationInputSource>>? start = null, Func<TimeSpan>? clock = null,
        Func<bool>? isInternalInput = null)
    {
        this.dispatcher = dispatcher; this.changed = changed;
        this.start = start ?? (async (id, keyboard, pointer, token) => await PresentationInputObserver.StartAsync(id, keyboard, pointer, token));
        this.clock = clock ?? (() => PresentationTimeline.FromMilliseconds(Environment.TickCount64));
        this.isInternalInput = isInternalInput ?? (() => false);
        animation = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        animation.Tick += (_, _) => Tick();
    }
    internal void Toggle()
        => SetArmed(!Armed);
    internal void SetArmed(bool value)
    {
        dispatcher.VerifyAccess(); if (disposed || Armed == value) return;
        Armed = value; faulted = false;
        if (!Armed) Stop(retainTrail: true); else { PausedTrail = null; Reconcile(); }
        NotifyChanged();
    }
    internal void Configure(PresentationRuntimeDemand next, bool canObserve, object identity, string pauseStatus)
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        bool newContext = !ReferenceEquals(context, identity);
        bool registrationChanged = demand.Keyboard != next.Keyboard;
        if (newContext || registrationChanged) { Stop(retainTrail: false); faulted = false; context = identity; }
        demand = next; pausedStatus = pauseStatus;
        if (allowed && !canObserve) Stop(retainTrail: true);
        allowed = canObserve;
        if (!canObserve || !next.Any) { if (input is not null || starting is not null) Stop(retainTrail: false); }
        else Reconcile();
        if (Observing && RefreshKeyboardScope()) Publish(force: true);
        Schedule();
    }
    internal void ConfigureKeyboardScope(PresentationKeyboardScope scope)
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        bool changed = keyboardScope != scope; keyboardScope = scope;
        bool canRecord = KeyboardScopeAllowsInput();
        if (changed || canRecord != keyboardScopeAllowed)
        {
            keyboardScopeAllowed = canRecord; ResetKeyboardState(); NewLifetime(); Publish(force: true);
        }
        Schedule();
    }
    internal void ConfigureEditingInput(bool enabled)
    {
        dispatcher.VerifyAccess(); if (disposed || editingInput == enabled || changingInputRoute) return;
        if (enabled)
        {
            changingInputRoute = true;
            try { Drain(); }
            finally { changingInputRoute = false; }
            if (disposed || !Observing) return;
        }
        else rawTransitionCutoff = clock().Ticks / TimeSpan.TicksPerMillisecond;
        editingInput = enabled; EditingInputEpoch++;
        timeline.ReleaseHeldKeys(clock()); NewLifetime(); Publish(force: true); Schedule();
    }
    internal void ObserveEditingInput(PresentationObservedBatch batch)
    {
        dispatcher.VerifyAccess();
        if (disposed || !Observing || !editingInput || batch.Epoch != EditingInputEpoch) return;
        BeforeInput?.Invoke(batch);
        if (!Observing || !editingInput || batch.Epoch != EditingInputEpoch) return;
        ApplyObservedInput(batch);
    }
    internal void Pause(bool retainTrail)
    {
        dispatcher.VerifyAccess();
        if (!allowed && input is null && starting is null) { if (!retainTrail) PausedTrail = null; return; }
        allowed = false; Stop(retainTrail);
    }
    internal void SuppressKeyboard(bool protectedInput)
    {
        dispatcher.VerifyAccess(); if (timeline.KeyboardSuppressed == protectedInput) return;
        timeline.SetKeyboardSuppressed(protectedInput); NewLifetime(); Publish(force: true);
    }
    internal void ClearKeyboardForFocusChange()
    { dispatcher.VerifyAccess(); ResetKeyboardState(); keyboardScopeAllowed = KeyboardScopeAllowsInput(); NewLifetime(); Publish(force: true); Schedule(); }
    internal void BlockForBudget()
    { dispatcher.VerifyAccess(); faulted = true; Stop(retainTrail: false); NotifyChanged(); }
    internal void RetryIfFaulted()
    {
        dispatcher.VerifyAccess(); if (disposed || !faulted) return;
        faulted = false; Reconcile(); NotifyChanged();
    }
    internal void ClearPausedTrail() { PausedTrail = null; NotifyChanged(); }
    internal void Disarm()
    { Armed = false; faulted = false; Stop(retainTrail: false); NotifyChanged(); }

    private void Reconcile()
    {
        if (!Armed || !allowed || !demand.Any || faulted || disposed || input is not null || starting is not null) return;
        BeginStart();
    }
    private void BeginStart()
    {
        long expected = ++connection; var request = new CancellationTokenSource(); starting = request; PausedTrail = null; NewLifetime(); NotifyChanged();
        Task<IPresentationInputSource> pending;
        try { pending = start(expected, demand.Keyboard, true, request.Token); }
        catch (Exception exception) { CompleteStart(expected, request, null, exception); return; }
        _ = ObserveStartAsync(pending, expected, request);
    }
    private async Task ObserveStartAsync(Task<IPresentationInputSource> pending, long expected, CancellationTokenSource request)
    {
        IPresentationInputSource? service = null;
        Exception? failure = null;
        try { service = await pending.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        if (dispatcher.CheckAccess()) { CompleteStart(expected, request, service, failure); return; }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { service?.Stop(); service?.Dispose(); return; }
        try
        {
            var operation = dispatcher.InvokeAsync(() => CompleteStart(expected, request, service, failure), DispatcherPriority.Input);
            await operation.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) { service?.Stop(); service?.Dispose(); }
        catch (InvalidOperationException) { service?.Stop(); service?.Dispose(); }
    }
    private void CompleteStart(long expected, CancellationTokenSource request, IPresentationInputSource? service, Exception? failure)
    {
        dispatcher.VerifyAccess();
        try
        {
            if (disposed || connection != expected || !Armed || !allowed || request.IsCancellationRequested)
            { service?.Stop(); service?.Dispose(); return; }
            if (failure is not null || service is null)
            {
                starting = null; faulted = true; Stop(retainTrail: false); NotifyChanged(); return;
            }
            input = service; service.Available += OnAvailable; starting = null; PausedTrail = null;
            timeline.Clear(); Drain();
            if (ReferenceEquals(input, service) && connection == expected && allowed && Armed) { Publish(force: true); NotifyChanged(); }
        }
        finally { if (ReferenceEquals(starting, request)) starting = null; request.Dispose(); }
    }
    private void OnAvailable()
    {
        if (disposed || Interlocked.Exchange(ref notification, 1) != 0) return;
        try { dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { Interlocked.Exchange(ref notification, 0); if (!disposed) Drain(); })); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref notification, 0); }
    }
    private void Drain()
    {
        dispatcher.VerifyAccess(); var service = input;
        if (service is null || !Armed || !allowed) return;
        if (!service.IsRunning) { faulted = true; Stop(retainTrail: false); NotifyChanged(); return; }
        var batch = service.Drain(); if (batch.Epoch != service.Epoch || service.Epoch != connection) return;
        BeforeInput?.Invoke(batch);
        if (!ReferenceEquals(input, service) || !allowed || !Armed) return;
        if (editingInput)
        {
            if (batch.Changes.Any(value => value.Kind == PresentationObservedKind.DeviceRemoved))
            { timeline.ReleaseHeldKeys(clock()); Publish(force: true); }
            batch = new(batch.Epoch, [], batch.Cursor, false);
        }
        else if (rawTransitionCutoff >= 0)
            batch = new(batch.Epoch, batch.Changes.Where(value => value.Kind == PresentationObservedKind.DeviceRemoved || value.Milliseconds > rawTransitionCutoff), batch.Cursor, batch.Overflowed);
        ApplyObservedInput(batch);
    }
    private void ApplyObservedInput(PresentationObservedBatch batch)
    {
        long revision = timeline.Revision;
        RefreshKeyboardScope();
        if (batch.Overflowed) NewLifetime();
        timeline.Apply(batch, demand with { Keyboard = demand.Keyboard && keyboardScopeAllowed }, clock());
        if (revision != timeline.Revision) Publish(force: true);
        Schedule();
    }
    private void Tick()
    {
        animation.Stop(); if (!Observing || disposed) return;
        bool scopeChanged = RefreshKeyboardScope();
        bool transient = timeline.HasTransients; timeline.Trim(clock(), demand);
        if (scopeChanged || transient) Publish(force: true);
        Schedule();
    }
    private void Schedule()
    { if (Observing && (timeline.HasTransients || demand.Keyboard && keyboardScope != PresentationKeyboardScope.AllApplications)) animation.Start(); else animation.Stop(); }
    private bool KeyboardScopeAllowsInput()
    {
        if (keyboardScope == PresentationKeyboardScope.AllApplications) return true;
        try
        {
            bool internalInput = isInternalInput();
            return keyboardScope == PresentationKeyboardScope.FloatCanvasOnly ? internalInput : !internalInput;
        }
        catch { return false; }
    }
    private bool RefreshKeyboardScope()
    {
        bool canRecord = KeyboardScopeAllowsInput();
        if (canRecord == keyboardScopeAllowed) return false;
        keyboardScopeAllowed = canRecord; ResetKeyboardState(); NewLifetime(); return true;
    }
    private void ResetKeyboardState()
    {
        bool suppressed = timeline.KeyboardSuppressed;
        timeline.ClearKeyboard();
        if (suppressed) timeline.SetKeyboardSuppressed(true);
    }
    private void Publish(bool force)
    {
        if (!force) return;
        Frame = timeline.Capture(epoch, ++sequence, clock()); NotifyChanged();
    }
    private void Stop(bool retainTrail)
    {
        editingInput = false; EditingInputEpoch++;
        rawTransitionCutoff = -1;
        ++connection; starting?.Cancel(); starting = null; animation.Stop();
        if (retainTrail && Frame.Trail.Count > 1) PausedTrail = Frame;
        else if (!retainTrail) PausedTrail = null;
        var old = input; input = null;
        if (old is not null) { old.Available -= OnAvailable; old.Stop(); old.Dispose(); }
        timeline.Clear(); NewLifetime(); Frame = timeline.Capture(epoch, ++sequence, clock()); NotifyChanged();
    }
    private void NewLifetime() { epoch++; Lifetime = new object(); }
    private void NotifyChanged() { if (!disposed) changed(); }
    public void Dispose()
    {
        if (disposed) return; dispatcher.VerifyAccess(); disposed = true; Armed = false;
        Stop(retainTrail: false); animation.Stop();
    }
}
