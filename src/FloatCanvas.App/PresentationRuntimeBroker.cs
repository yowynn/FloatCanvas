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
    private readonly Func<bool> keyboardAvailable;
    private readonly PresentationTimeline timeline = new();
    private readonly PresentationTimeline outsideTimeline = new(), insideTimeline = new();
    private bool? observedInternalInput;
    private readonly DispatcherTimer animation;
    private readonly ushort[] gamepadState = new ushort[PresentationDeviceButtons.GamepadSlots];
    private readonly Func<int, ushort> readGamepad;
    private IPresentationInputSource? input;
    private CancellationTokenSource? starting;
    private PresentationRuntimeDemand demand = PresentationRuntimeDemand.Empty;
    private object? context;
    private long connection, epoch, sequence;
    private bool keyboardPixelsMayBeRetained;
    private int notification;
    private bool allowed, disposed, faulted;
    private PresentationKeyboardScope keyboardScope;
    private bool keyboardScopeAllowed = true;
    private bool editingInput;
    private bool changingInputRoute;
    private long rawTransitionCutoff = -1;
    private string pausedStatus = "Presentation.Status.PausedEditing";
    internal bool Armed { get; private set; }
    internal bool Observing => input?.IsRunning == true && allowed && Armed;
    internal bool HasKeyboardDemand => demand.Keyboard;
    internal bool ObservingKeyboard => Observing && demand.Keyboard && KeyboardScopeAllowsInput();
    internal bool HasKeyboardAnimation => Enum.GetValues<PresentationKeyboardScope>()
        .Any(scope => Frame.ForKeyboardScope(scope).RecentChords.Any(chord => !chord.IsHeld));
    internal long InputEpoch => input?.Epoch ?? -1;
    internal string StatusKey => !Armed ? "Presentation.Status.Stopped" : faulted ? "Presentation.Status.Unavailable" : !allowed ? pausedStatus :
        !demand.Any ? "Presentation.Status.NoLayers" : starting is not null ? "Presentation.Status.Starting" :
        demand.Keyboard && !KeyboardScopeAllowsInput() ? "Presentation.Status.OutsideScope" : timeline.KeyboardSuppressed ?
        "Presentation.Status.KeyboardProtected" : Observing ? "Presentation.Status.Running" : "Presentation.Status.Unavailable";
    internal object Lifetime { get; private set; } = new();
    // Capture pauses/restarts invalidate worker results, but not pixels already
    // displayed. Privacy/scope changes still retire those pixels immediately.
    internal object PixelContinuity { get; private set; } = new();
    internal PresentationFrame Frame { get; private set; } = PresentationFrame.Empty;
    internal PresentationFrame? PointerFrame => !disposed && Armed && allowed && demand.Pointer && !faulted ? Frame : null;
    internal PresentationFrame? PausedTrail { get; private set; }
    internal event Action<PresentationObservedBatch>? BeforeInput;

    internal PresentationRuntimeBroker(Dispatcher dispatcher, Action changed,
        Func<long, bool, bool, CancellationToken, Task<IPresentationInputSource>>? start = null, Func<TimeSpan>? clock = null,
        Func<bool>? isInternalInput = null, Func<bool>? keyboardAvailable = null, Func<int, ushort>? readGamepad = null)
    {
        this.dispatcher = dispatcher; this.changed = changed;
        this.start = start ?? (async (id, keyboard, pointer, token) => await PresentationInputObserver.StartAsync(id, keyboard, pointer, token));
        this.clock = clock ?? (() => PresentationTimeline.FromMilliseconds(Environment.TickCount64));
        this.isInternalInput = isInternalInput ?? (() => false);
        this.keyboardAvailable = keyboardAvailable ?? (() => true);
        this.readGamepad = readGamepad ?? PresentationDeviceButtons.ReadGamepad;
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
        bool registrationChanged = demand.Keyboard != next.Keyboard || demand.Pointer != next.Pointer ||
            demand.MouseButtons != next.MouseButtons || demand.GamepadButtons != next.GamepadButtons;
        bool preservePointer = !newContext && registrationChanged && allowed && canObserve && demand.Pointer && next.Pointer &&
            demand.Clicks == next.Clicks && demand.Trail == next.Trail && demand.ClickMilliseconds == next.ClickMilliseconds && demand.TrailMilliseconds == next.TrailMilliseconds;
        if (newContext || registrationChanged)
        {
            Stop(retainTrail: false, preservePointer: preservePointer, preserveKeyboardSuppression: true);
            faulted = false; context = identity;
        }
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
            keyboardScopeAllowed = canRecord; ResetKeyboardState(); PixelContinuity = new(); NewLifetime(); Publish(force: true);
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
        editingInput = enabled;
        // Editing changes the pointer route, not keyboard ownership or held-key state.
        Publish(force: true); Schedule();
    }
    internal void ObserveHookInput(PresentationObservedBatch batch)
    {
        dispatcher.VerifyAccess();
        if (disposed || !Observing || batch.Epoch != InputEpoch) return;
        BeforeInput?.Invoke(batch);
        if (!Observing || batch.Epoch != InputEpoch) return;
        if (!editingInput)
            batch = new(batch.Epoch, batch.Changes.Where(value => value.Kind is PresentationObservedKind.KeyDown or PresentationObservedKind.KeyUp or PresentationObservedKind.LockStateChanged), null, batch.Overflowed);
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
        timeline.SetKeyboardSuppressed(protectedInput); outsideTimeline.SetKeyboardSuppressed(protectedInput);
        insideTimeline.SetKeyboardSuppressed(protectedInput);
        // Retire a composited frame immediately when it may still contain key
        // history. A protected or input-free frame is safe to keep on screen.
        if (protectedInput && keyboardPixelsMayBeRetained)
        {
            PixelContinuity = new();
            keyboardPixelsMayBeRetained = false;
        }
        NewLifetime(); Publish(force: true);
    }
    internal void ClearKeyboardForFocusChange()
    { dispatcher.VerifyAccess(); ResetKeyboardState(); keyboardScopeAllowed = KeyboardScopeAllowsInput(); PixelContinuity = new(); NewLifetime(); Publish(force: true); Schedule(); }
    internal void RefreshKeyboardForFocusChange()
    {
        dispatcher.VerifyAccess();
        // A system shortcut can open another window while its physical keys are
        // still held. The privacy gate retires its own requests by foreground/request ID.
        // Keep the render lifetime unless scope or protection really changes; rotating it
        // here rejects the entire retained canvas while the replacement worker frame renders.
        RefreshKeyboardScope();
        if (demand.ScopedKeyboard) TransitionScopedOwner(CurrentInternalInput(), clock());
        Publish(force: true); Schedule();
    }
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
        try { pending = start(expected, demand.Keyboard, demand.Pointer || demand.MouseButtons, request.Token); }
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
            // Stop already retired every old keyboard event. A keyboard-only
            // subscription change may retain the independently owned mouse data.
            ResetKeyboardState(); Drain();
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
        if (batch.Changes.Count == 0 && batch.Cursor is null && !batch.Overflowed)
        { ApplyObservedInput(batch); return; } // Advance fading without re-running input/privacy work.
        BeforeInput?.Invoke(batch);
        if (!ReferenceEquals(input, service) || !allowed || !Armed) return;
        if (editingInput)
        {
            // Keep raw mouse edges for keycaps, while the hook owns editing-mode
            // click effects. Side buttons are reported only by the raw source.
            batch = new(batch.Epoch, batch.Changes
                .Where(value => value.Kind is PresentationObservedKind.DeviceRemoved or PresentationObservedKind.LockStateChanged ||
                    demand.MouseButtons && value.DeviceKind == PresentationDeviceKind.Mouse)
                .Select(value => value.DeviceKind == PresentationDeviceKind.Mouse &&
                    value.Kind is PresentationObservedKind.ButtonDown or PresentationObservedKind.ButtonUp
                    ? value with { HasPosition = false } : value),
                batch.Cursor, demand.MouseButtons && batch.Overflowed);
        }
        else if (rawTransitionCutoff >= 0)
            batch = new(batch.Epoch, batch.Changes.Where(value => value.Kind is PresentationObservedKind.DeviceRemoved or PresentationObservedKind.LockStateChanged ||
                demand.MouseButtons && value is { Kind: PresentationObservedKind.ButtonUp, DeviceKind: PresentationDeviceKind.Mouse } ||
                value.Milliseconds > rawTransitionCutoff), batch.Cursor, batch.Overflowed);
        ApplyObservedInput(batch);
    }
    private void ApplyObservedInput(PresentationObservedBatch batch)
    {
        long revision = Revision;
        RefreshKeyboardScope();
        if (batch.Overflowed) NewLifetime();
        var now = clock();
        timeline.Apply(batch, demand with { Keyboard = demand.Keyboard && keyboardScopeAllowed }, now);
        var keyboardDemand = demand with { Pointer = false, Clicks = false, Trail = false, Keyboard = demand.Keyboard && keyboardScopeAllowed };
        if (batch.Overflowed)
        {
            var overflow = new PresentationObservedBatch(batch.Epoch, [], null, true);
            outsideTimeline.Apply(overflow, keyboardDemand, now); insideTimeline.Apply(overflow, keyboardDemand, now);
            observedInternalInput = null;
        }
        // Keep native batches bounded without allocating a new input batch for every repeated key.
        var pending = new List<PresentationObservedInput>();
        void FlushScopedKeys()
        {
            if (pending.Count == 0 || observedInternalInput is not { } inside) return;
            var scoped = new PresentationObservedBatch(batch.Epoch, pending, null, false);
            (inside ? insideTimeline : outsideTimeline).Apply(scoped, keyboardDemand, now); pending.Clear();
        }
        bool? fallbackOwner = null; bool sampledOwner = false;
        foreach (var value in batch.Changes)
        {
            if (value.Kind == PresentationObservedKind.DeviceRemoved)
            {
                FlushScopedKeys();
                var removed = new PresentationObservedBatch(batch.Epoch, [value], null, false);
                outsideTimeline.Apply(removed, keyboardDemand, now); insideTimeline.Apply(removed, keyboardDemand, now);
                continue;
            }
            if (value.Kind == PresentationObservedKind.LockStateChanged)
            {
                FlushScopedKeys();
                var locks = new PresentationObservedBatch(batch.Epoch, [value], null, false);
                outsideTimeline.Apply(locks, keyboardDemand, now); insideTimeline.Apply(locks, keyboardDemand, now);
                continue;
            }
            if (value.Kind is not (PresentationObservedKind.KeyDown or PresentationObservedKind.KeyUp) &&
                !(value.Kind is PresentationObservedKind.ButtonDown or PresentationObservedKind.ButtonUp or PresentationObservedKind.Wheel &&
                  value.DeviceKind is PresentationDeviceKind.Mouse or PresentationDeviceKind.Gamepad)) continue;
            if (value.InputOwner == PresentationInputOwner.Unspecified && !sampledOwner) { fallbackOwner = CurrentInternalInput(); sampledOwner = true; }
            bool? internalInput = value.InputOwner switch
            {
                PresentationInputOwner.FloatCanvas => true,
                PresentationInputOwner.OtherApplication => false,
                PresentationInputOwner.Unspecified => fallbackOwner,
                _ => null
            };
            if (internalInput != observedInternalInput)
            {
                FlushScopedKeys(); var timestamp = PresentationTimeline.FromMilliseconds(Math.Max(0, value.Milliseconds));
                TransitionScopedOwner(internalInput, timestamp > now ? now : timestamp);
            }
            if (internalInput is not null) pending.Add(value); // Unknown ownership never enters a restricted history.
        }
        FlushScopedKeys();
        outsideTimeline.Trim(now, keyboardDemand); insideTimeline.Trim(now, keyboardDemand);
        if (revision != Revision) Publish(force: true);
        Schedule();
    }
    private void Tick()
    {
        animation.Stop(); if (!Observing || disposed) return;
        if (demand.Keyboard && !keyboardAvailable())
        { faulted = true; Stop(retainTrail: false); NotifyChanged(); return; }
        bool scopeChanged = RefreshKeyboardScope();
        if (demand.GamepadButtons) PollGamepads();
        bool transient = HasTransients; var now = clock();
        if (demand.ScopedKeyboard) scopeChanged |= TransitionScopedOwner(CurrentInternalInput(), now);
        timeline.Trim(now, demand); outsideTimeline.Trim(now, demand); insideTimeline.Trim(now, demand);
        if (scopeChanged || transient) Publish(force: true);
        Schedule();
    }
    private void Schedule()
    { if (Observing && (HasTransients || demand.GamepadButtons)) animation.Start(); else animation.Stop(); }
    private void PollGamepads()
    {
        List<PresentationObservedInput>? transitions = null;
        long timestamp = clock().Ticks / TimeSpan.TicksPerMillisecond;
        for (int slot = 0; slot < gamepadState.Length; slot++)
        {
            ushort previous = gamepadState[slot], current = readGamepad(slot);
            gamepadState[slot] = current;
            uint changedButtons = (uint)(previous ^ current);
            while (changedButtons != 0)
            {
                int button = System.Numerics.BitOperations.TrailingZeroCount(changedButtons);
                bool down = (current & (1 << button)) != 0;
                (transitions ??= []).Add(new(down ? PresentationObservedKind.ButtonDown : PresentationObservedKind.ButtonUp,
                    timestamp, Device: slot, Button: button, DeviceKind: PresentationDeviceKind.Gamepad));
                changedButtons &= changedButtons - 1;
            }
        }
        if (transitions is not null) ApplyObservedInput(new(InputEpoch, transitions, null, false));
    }
    private long Revision => timeline.Revision + outsideTimeline.Revision + insideTimeline.Revision;
    private bool HasTransients => timeline.HasTransients || outsideTimeline.HasTransients || insideTimeline.HasTransients;
    private bool? CurrentInternalInput()
    { try { return isInternalInput(); } catch { return null; } }
    private bool TransitionScopedOwner(bool? inside, TimeSpan now)
    {
        if (observedInternalInput == inside) return false;
        observedInternalInput = inside;
        long revision = Revision;
        outsideTimeline.ReleaseHeldKeys(now); insideTimeline.ReleaseHeldKeys(now);
        return Revision != revision;
    }
    private void ReleaseHeldKeys(TimeSpan now)
    { timeline.ReleaseHeldKeys(now); outsideTimeline.ReleaseHeldKeys(now); insideTimeline.ReleaseHeldKeys(now); }
    private void ClearTimelines(bool preserveKeyboardSuppression = false)
    {
        bool suppressed = preserveKeyboardSuppression && timeline.KeyboardSuppressed;
        timeline.Clear(); outsideTimeline.Clear(); insideTimeline.Clear(); observedInternalInput = null;
        if (!suppressed) return;
        timeline.SetKeyboardSuppressed(true);
        outsideTimeline.SetKeyboardSuppressed(true);
        insideTimeline.SetKeyboardSuppressed(true);
    }
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
        keyboardScopeAllowed = canRecord; ResetKeyboardState(); PixelContinuity = new(); NewLifetime(); return true;
    }
    private void ResetKeyboardState()
    {
        bool suppressed = timeline.KeyboardSuppressed;
        timeline.ClearKeyboard(); outsideTimeline.ClearKeyboard(); insideTimeline.ClearKeyboard(); observedInternalInput = null;
        if (suppressed)
        { timeline.SetKeyboardSuppressed(true); outsideTimeline.SetKeyboardSuppressed(true); insideTimeline.SetKeyboardSuppressed(true); }
    }
    private void Publish(bool force)
    {
        if (!force) return;
        var now = clock(); sequence++;
        var all = timeline.Capture(epoch, sequence, now);
        var outside = outsideTimeline.Capture(epoch, sequence, now);
        var inside = insideTimeline.Capture(epoch, sequence, now);
        keyboardPixelsMayBeRetained |= HasKeyboardInput(all) || HasKeyboardInput(outside) || HasKeyboardInput(inside);
        Frame = PresentationFrame.WithKeyboardScopes(all, outside, inside); NotifyChanged();
    }
    private static bool HasKeyboardInput(PresentationFrame frame) => frame.PressedKeys.Count > 0 || frame.RecentChords.Count > 0;
    private void Stop(bool retainTrail, bool preservePointer = false, bool preserveKeyboardSuppression = false)
    {
        editingInput = false;
        rawTransitionCutoff = -1;
        Array.Clear(gamepadState);
        ++connection; starting?.Cancel(); starting = null; animation.Stop();
        if (retainTrail && Frame.Trail.Count > 1) PausedTrail = Frame;
        else if (!retainTrail) PausedTrail = null;
        var old = input; input = null;
        if (old is not null) { old.Available -= OnAvailable; old.Stop(); old.Dispose(); }
        if (preservePointer) ResetKeyboardState(); else ClearTimelines(preserveKeyboardSuppression);
        NewLifetime(); Publish(force: true);
    }
    private void NewLifetime() { epoch++; Lifetime = new object(); }
    private void NotifyChanged() { if (!disposed) changed(); }
    public void Dispose()
    {
        if (disposed) return; dispatcher.VerifyAccess(); disposed = true; Armed = false;
        Stop(retainTrail: false); animation.Stop();
    }
}
