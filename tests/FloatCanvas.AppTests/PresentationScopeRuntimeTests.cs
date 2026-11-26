using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using System.Diagnostics;
using System.Windows.Threading;

internal static class PresentationScopeRuntimeTests
{
    private static readonly PresentationRuntimeDemand Keyboard = new(true, false, false, false, true, 900, 450, 800);
    private static readonly PresentationRuntimeDemand Both = new(true, true, true, true, true, 900, 450, 800);
    private static PresentationObservedInput Key(int vk, bool down = true, long time = 1000) =>
        new(down ? PresentationObservedKind.KeyDown : PresentationObservedKind.KeyUp, time, 1, vk, vk, 0, vk == 0x11 ? "Ctrl" : "C");
    private static PresentationObservedInput Lock(int vk, bool locked) =>
        new(PresentationObservedKind.LockStateChanged, 1000, VirtualKey: vk, IsLocked: locked);
    private static PresentationObservedCursor Cursor(int x, int y) => new(x, y, 1000);

    internal static void Run(Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            DefaultsAndPointerSwitch(dispatcher, check);
            ScopeAndPointerIndependence(dispatcher, check);
            ScopePollWithoutInput(dispatcher, check);
            SuppressionAndBeforeInput(dispatcher, check);
            FaultAndUnknownScope(dispatcher, check);
            AuthoredScopes(dispatcher, check);
            ModifierOnlyScopeDamage(dispatcher, check);
            PointerContinuityDuringKeyboardSubscription(dispatcher, check);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void DefaultsAndPointerSwitch(Dispatcher dispatcher, Action<string, bool> check)
    {
        var factory = new FakeFactory(); var clock = new FakeClock(); var identity = new object(); int changes = 0;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => changes++, factory.Start, () => clock.Time, () => true);
        broker.Configure(Keyboard, true, identity, "paused");
        check("direct brokers stay unarmed until their desktop explicitly enables observation", !broker.Armed && factory.Requests.Count == 0);
        broker.SetArmed(true); var source = factory.Requests.Single().Source;
        check("default all-applications scope records internal editing input", broker.ObservingKeyboard && broker.Observing);
        check("keyboard-only native demand does not register the mouse role", factory.Requests.Single().Keyboard && !factory.Requests.Single().Pointer);
        int before = changes; broker.SetArmed(true);
        check("repeating SetArmed true is idempotent without another native registration or publish", factory.Requests.Count == 1 && changes == before);
        source.Queue([Key(0x11), Key(0x43)], Cursor(10, 20)); Pump(dispatcher);
        check("keyboard-only demand records semantic chords while discarding optional pointer samples",
            broker.Frame.RecentChords.Count == 1 && broker.Frame.RecentChords[0].Keys.Count == 2 && broker.Frame.CursorScreen is null);
        var lifetime = broker.Lifetime;
        broker.Configure(Both, true, identity, "paused");
        var both = factory.Requests[1];
        check("enabling global pointer effects restarts with both native roles and retires keyboard-only history",
            factory.Requests.Count == 2 && both is { Keyboard: true, Pointer: true } && source.StopCount > 0 &&
            !ReferenceEquals(lifetime, broker.Lifetime) && broker.Frame.RecentChords.Count == 0);
        both.Source.Queue(cursor: Cursor(20, 30)); Pump(dispatcher);
        check("the replacement service publishes pointer data after its settings are enabled",
            broker.Frame.CursorScreen == new PointD(20, 30));
        broker.Configure(Keyboard, true, identity, "paused");
        var keyboard = factory.Requests[2];
        check("disabling global pointer effects restarts as keyboard-only",
            factory.Requests.Count == 3 && keyboard is { Keyboard: true, Pointer: false } && both.Source.StopCount > 0 &&
            broker.Frame.CursorScreen is null);
        keyboard.Source.Queue([Key(0x11, time: 1100), Key(0x43, time: 1100)]); Pump(dispatcher);
        keyboard.Source.Queue([Key(0x43, false, 1200)]); Pump(dispatcher);
        check("released keyboard events expose their animation state independently of pointer demand", broker.HasKeyboardAnimation);
        clock.Time = TimeSpan.FromMilliseconds(2000); PumpUntil(dispatcher, () => !broker.HasKeyboardAnimation);
        check("keyboard animation ends when released semantic history expires", broker.Frame.RecentChords.Count == 0);
        broker.Configure(Both with { Keyboard = false, AllPhysicalKeys = false }, true, identity, "paused");
        check("a genuine keyboard registration-role change still replaces the native source",
            factory.Requests.Count == 4 && !factory.Requests[3].Keyboard && factory.Requests[3].Pointer &&
            keyboard.Source.StopCount > 0 && keyboard.Source.DisposeCount > 0);
        broker.Configure(PresentationRuntimeDemand.Empty, true, identity, "paused");
        check("removing the final keyboard or pointer demand closes the service", !broker.Observing && factory.Requests[3].Source.StopCount > 0);
    }

    private static void ScopeAndPointerIndependence(Dispatcher dispatcher, Action<string, bool> check)
    {
        bool internalInput = false; var factory = new FakeFactory(); var clock = new FakeClock();
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start, () => clock.Time, () => internalInput);
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas);
        broker.Configure(Both, true, new object(), "paused"); broker.SetArmed(true);
        var source = factory.Requests.Single().Source;
        source.Queue([Key(0x11), Key(0x43)], Cursor(10, 10)); Pump(dispatcher);
        source.Queue([Lock(0x14, true), Lock(0x90, true)]); Pump(dispatcher);
        check("global lock indicators remain visible in every keyboard scope",
            broker.Frame.LockState == (PresentationLockState.CapsLock | PresentationLockState.NumLock) &&
            Enum.GetValues<PresentationKeyboardScope>().All(scope =>
                broker.Frame.ForKeyboardScope(scope).LockState == broker.Frame.LockState));
        source.Queue(cursor: Cursor(30, 30)); Pump(dispatcher);
        check("outside-only scope accepts external shortcuts and retains independent pointer trails",
            broker.ObservingKeyboard && broker.Frame.RecentChords.Count == 1 && broker.Frame.Trail.Count == 2);
        var pointerFrame = broker.Frame; var lifetime = broker.Lifetime;
        internalInput = true;
        source.Queue([Key(0x43, false), Key(0x11, false), Key(0x11), Key(0x43)], Cursor(40, 40)); Pump(dispatcher);
        check("entering FloatCanvas in outside-only scope filters keys while pointer tracking continues",
            broker.Observing && !broker.ObservingKeyboard && broker.Frame.PressedKeys.Count == 0 && broker.Frame.RecentChords.Count == 0 && broker.Frame.CursorScreen == new PointD(40, 40));
        check("scope eligibility changes retire old display lifetimes without restarting the service or losing its pointer trail",
            !ReferenceEquals(lifetime, broker.Lifetime) && factory.Requests.Count == 1 && source.StopCount == 0 && broker.Frame.Trail.Count == 3 &&
            pointerFrame.Trail.All(sample => broker.Frame.Trail.Contains(sample)));
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly);
        source.Queue([Key(0x11), Key(0x43)], Cursor(50, 50)); Pump(dispatcher);
        check("FloatCanvas-only scope records the internal keyboard while the global mouse remains available",
            broker.ObservingKeyboard && broker.Frame.RecentChords.Count == 1 && broker.Frame.CursorScreen == new PointD(50, 50));
        internalInput = false;
        source.Queue([Key(0x11), Key(0x43)], Cursor(60, 60)); Pump(dispatcher);
        check("FloatCanvas-only scope rejects external keyboard input without suppressing its pointer",
            !broker.ObservingKeyboard && broker.Frame.RecentChords.Count == 0 && broker.Frame.CursorScreen == new PointD(60, 60) && factory.Requests.Count == 1);
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.AllApplications);
        source.Queue([Key(0x11), Key(0x43)]); Pump(dispatcher);
        check("returning to all-applications scope starts a clean semantic keyboard history", broker.ObservingKeyboard && broker.Frame.RecentChords.Count == 1 && broker.Frame.PressedKeys.Count == 2);
    }

    private static void ScopePollWithoutInput(Dispatcher dispatcher, Action<string, bool> check)
    {
        bool internalInput = false; var factory = new FakeFactory(); var clock = new FakeClock(); int changes = 0;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => changes++, factory.Start, () => clock.Time, () => internalInput);
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas);
        broker.Configure(Keyboard, true, new object(), "paused"); broker.SetArmed(true);
        var source = factory.Requests.Single().Source;
        source.Queue([Key(0x11), Key(0x43)]); Pump(dispatcher);
        long sequence = broker.Frame.Sequence; object lifetime = broker.Lifetime;
        internalInput = true;
        check("ObservingKeyboard reflects the current scoped input owner before a new key packet arrives", !broker.ObservingKeyboard);
        broker.RefreshKeyboardForFocusChange(); Pump(dispatcher);
        check("a foreground/focus event clears old keyboard pixels even when no new input arrives",
            broker.Frame.Sequence > sequence && !ReferenceEquals(lifetime, broker.Lifetime) && source.StopCount == 0);
        sequence = broker.Frame.Sequence; int callbacks = changes; PumpFor(dispatcher, 115);
        check("an unchanged scoped owner performs no repeated frame publication or native restart",
            broker.Frame.Sequence == sequence && changes == callbacks && factory.Requests.Count == 1);
        internalInput = false; broker.RefreshKeyboardForFocusChange(); Pump(dispatcher);
        check("returning to the allowed scope cannot resurrect keys held before the scope boundary",
            broker.ObservingKeyboard && broker.Frame.PressedKeys.Count == 0 && broker.Frame.RecentChords.Count == 0);
        source.Queue([Key(0x43, false), Key(0x11, false)]); Pump(dispatcher);
        check("stale key-up packets after a scope boundary stay silent", broker.Frame.RecentChords.Count == 0);
    }

    private static void SuppressionAndBeforeInput(Dispatcher dispatcher, Action<string, bool> check)
    {
        bool internalInput = false; var factory = new FakeFactory(); var clock = new FakeClock();
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start, () => clock.Time, () => internalInput);
        broker.Configure(Both, true, new object(), "paused"); broker.SetArmed(true);
        var source = factory.Requests.Single().Source;
        source.Queue(cursor: Cursor(12, 34)); Pump(dispatcher);
        broker.SuppressKeyboard(true);
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas);
        check("explicit scope changes preserve known password suppression and the pointer snapshot",
            broker.Frame.KeyboardSuppressed && broker.Frame.CursorScreen == new PointD(12, 34) && source.StopCount == 0);
        long epoch = broker.Frame.Epoch; internalInput = true;
        PumpUntil(dispatcher, () => broker.Frame.Epoch > epoch);
        source.Queue([Key(0x11), Key(0x43)], Cursor(30, 40), overflow: true); Pump(dispatcher);
        check("scope changes and overflow cannot clear the keyboard privacy gate", broker.Frame.KeyboardSuppressed && broker.Frame.RecentChords.Count == 0 && broker.Frame.CursorScreen == new PointD(30, 40));
        broker.ClearKeyboardForFocusChange();
        check("focus keyboard cleanup keeps known suppression without clearing pointer state", broker.Frame.KeyboardSuppressed && broker.Frame.CursorScreen == new PointD(30, 40));
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.AllApplications); broker.SuppressKeyboard(false);
        source.Queue([Key(0x11), Key(0x43)]); Pump(dispatcher);
        check("a later explicit privacy release permits fresh keyboard input", !broker.Frame.KeyboardSuppressed && broker.Frame.RecentChords.Count == 1);
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas); internalInput = false;
        broker.BeforeInput += _ => internalInput = true;
        source.Queue([Key(0x11), Key(0x43)], Cursor(44, 55)); Pump(dispatcher);
        check("the keyboard scope is evaluated after BeforeInput while mouse data remains independent",
            !broker.ObservingKeyboard && broker.Frame.RecentChords.Count == 0 && broker.Frame.CursorScreen == new PointD(44, 55));
    }

    private static void FaultAndUnknownScope(Dispatcher dispatcher, Action<string, bool> check)
    {
        var factory = new FakeFactory(); var clock = new FakeClock(); int predicateCalls = 0;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start, () => clock.Time,
            () => { predicateCalls++; throw new InvalidOperationException("Synthetic owner unavailable"); });
        broker.Configure(Both, true, new object(), "paused"); broker.SetArmed(true);
        var source = factory.Requests.Single().Source;
        source.Queue([Key(0x11), Key(0x43)], Cursor(10, 20)); Pump(dispatcher);
        check("all-applications recording survives unavailable internal-owner detection", broker.Frame.RecentChords.Count == 1 && predicateCalls > 0 && broker.Frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).RecentChords.Count == 0);
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas);
        source.Queue([Key(0x11), Key(0x43)], Cursor(30, 40)); Pump(dispatcher);
        check("an unknown scoped owner fails keyboard recording closed rather than accidentally treating it as outside",
            !broker.ObservingKeyboard && broker.Frame.PressedKeys.Count == 0 && broker.Frame.RecentChords.Count == 0 && broker.Frame.CursorScreen == new PointD(30, 40));
        bool rejected = false; try { broker.ConfigureKeyboardScope((PresentationKeyboardScope)99); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check("invalid scope values are rejected without replacing the active source", rejected && factory.Requests.Count == 1 && source.StopCount == 0);
        broker.BlockForBudget(); int count = factory.Requests.Count; long sequence = broker.Frame.Sequence;
        PumpFor(dispatcher, 100);
        check("budget-blocked input stays stopped without automatic retry polling", !broker.Observing && factory.Requests.Count == count && broker.Frame.Sequence == sequence);
        broker.RetryIfFaulted();
        check("an explicit meaningful-plan retry can recover a budget-blocked source", broker.Observing && factory.Requests.Count == count + 1);
        count = factory.Requests.Count; sequence = broker.Frame.Sequence; broker.RetryIfFaulted();
        check("RetryIfFaulted is a no-op for a healthy source", factory.Requests.Count == count && broker.Frame.Sequence == sequence);

        var stoppedFactory = new FakeFactory { InitiallyRunning = false };
        using var stopped = new PresentationRuntimeBroker(dispatcher, () => { }, stoppedFactory.Start, () => clock.Time);
        stopped.Configure(Keyboard, true, new object(), "paused"); stopped.SetArmed(true);
        var dead = stoppedFactory.Requests.Single().Source;
        dead.Queue([Key(0x11), Key(0x43)]); Pump(dispatcher);
        check("a source that never becomes running cannot publish keyboard events", !stopped.Observing && !stopped.ObservingKeyboard && stopped.Frame.PressedKeys.Count == 0 && dead.StopCount > 0 && dead.DisposeCount > 0);
    }

    private static void AuthoredScopes(Dispatcher dispatcher, Action<string, bool> check)
    {
        var factory = new FakeFactory(); var clock = new FakeClock(); bool internalInput = false;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start, () => clock.Time, () => internalInput);
        broker.Configure(Both with { ScopedKeyboard = true }, true, new object(), "paused"); broker.SetArmed(true);
        var source = factory.Requests.Single().Source;
        PresentationObservedInput Event(int vk, bool inside, bool down, long time) => Key(vk, down, time) with
        { InputOwner = inside ? PresentationInputOwner.FloatCanvas : PresentationInputOwner.OtherApplication };
        // Foreground has already moved back by drain time: event ownership must survive batching.
        source.Queue([Event(0x41, false, true, 990), Event(0x41, false, false, 991), Event(0x42, true, true, 992), Event(0x42, true, false, 993)]);
        Pump(dispatcher);
        var all = broker.Frame; var outside = all.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas); var inside = all.ForKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly);
        check("one source batch retains separate internal and external keyboard histories", all.RecentChords.Count == 2 && outside.RecentChords.Single().Keys.Single().VirtualKey == 0x41 && inside.RecentChords.Single().Keys.Single().VirtualKey == 0x42);
        check("authored scoped snapshots share one atomic identity and bounded aggregate payload", all.Epoch == outside.Epoch && all.Sequence == inside.Sequence && all.Time == inside.Time && all.PayloadBytes <= PresentationLimits.MaximumScopedFramePayloadBytes && factory.Requests.Count == 1);
        internalInput = true; clock.Time += TimeSpan.FromMilliseconds(10);
        source.Queue([Event(0x11, true, true, 1001), Event(0x43, false, true, 1002)]); Pump(dispatcher);
        outside = broker.Frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas);
        check("cross-scope focus transitions cannot combine one app's modifier with another app's chord", outside.RecentChords.Last().Keys.Count == 1 && outside.RecentChords.Last().Keys.Single().VirtualKey == 0x43);
        int outsideCount = outside.RecentChords.Count, insideCount = broker.Frame.ForKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly).RecentChords.Count;
        source.Queue([Key(0x5A, time: 1003) with { InputOwner = PresentationInputOwner.Unknown }]); Pump(dispatcher);
        check("an explicitly unknown sampled owner cannot fall back to the later foreground application", broker.Frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).RecentChords.Count == outsideCount && broker.Frame.ForKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly).RecentChords.Count == insideCount && broker.Frame.RecentChords.Last().Keys.Last().VirtualKey == 0x5A);
        broker.SuppressKeyboard(true); source.Queue([Event(0x44, false, true, 1003), Event(0x45, true, true, 1004)], Cursor(80, 90)); Pump(dispatcher);
        check("privacy suppression erases and blocks every scoped keyboard history while preserving pointer pixels", Enum.GetValues<PresentationKeyboardScope>().All(scope => broker.Frame.ForKeyboardScope(scope) is { KeyboardSuppressed: true, PressedKeys.Count: 0, RecentChords.Count: 0 }) && broker.Frame.CursorScreen == new PointD(80, 90));
        broker.SuppressKeyboard(false); source.Queue([Event(0x46, false, true, 1005)]); Pump(dispatcher);
        check("privacy release accepts only fresh events in their authored scope", broker.Frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).RecentChords.Count == 1 && broker.Frame.ForKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly).RecentChords.Count == 0);
        broker.SetArmed(false);
        check("the global stop clears all authored scopes and closes the single input source", !broker.Observing && source.StopCount == 1 && Enum.GetValues<PresentationKeyboardScope>().All(scope => broker.Frame.ForKeyboardScope(scope).RecentChords.Count == 0));
    }

    private static void PointerContinuityDuringKeyboardSubscription(Dispatcher dispatcher, Action<string, bool> check)
    {
        var factory = new FakeFactory(); var clock = new FakeClock(); var identity = new object();
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start, () => clock.Time);
        var pointerOnly = Both with { Keyboard = false, AllPhysicalKeys = false };
        broker.Configure(Both, true, identity, "paused"); broker.SetArmed(true);
        var source = factory.Requests.Single().Source;
        source.Queue([Key(0x11), Key(0x43), new(PresentationObservedKind.ButtonDown, 1000, X: 10, Y: 20, HasPosition: true)], Cursor(10, 20)); Pump(dispatcher);
        source.Queue(cursor: Cursor(20, 30)); Pump(dispatcher);
        var before = broker.Frame; var lifetime = broker.Lifetime;
        check("pointer continuity fixture begins with live keyboard clicks and a two-point trail", before.RecentChords.Count == 1 && before.Clicks.Count == 1 && before.Trail.Count == 2);
        factory.DeferNextStart = true; broker.Configure(pointerOnly, true, identity, "paused");
        check("removing only the keyboard subscription preserves global pointer clicks trail and cursor across native restart", broker.Frame.Clicks.SequenceEqual(before.Clicks) && broker.Frame.Trail.SequenceEqual(before.Trail) && broker.Frame.CursorScreen == before.CursorScreen && factory.Requests.Count == 2 && source.StopCount == 1 && source.DisposeCount == 1);
        check("global pointer pixels remain available while the replacement observer is still starting", !broker.Observing && broker.PointerFrame is { } retained && retained.Clicks.SequenceEqual(before.Clicks) && retained.Trail.SequenceEqual(before.Trail));
        factory.CompleteStart(); PumpUntil(dispatcher, () => broker.Observing);
        check("keyboard role restart retires every keyboard scope and its privacy lifetime", !ReferenceEquals(lifetime, broker.Lifetime) && Enum.GetValues<PresentationKeyboardScope>().All(scope => broker.Frame.ForKeyboardScope(scope).PressedKeys.Count == 0 && broker.Frame.ForKeyboardScope(scope).RecentChords.Count == 0));
        clock.Time = TimeSpan.FromMilliseconds(1100); factory.Requests.Last().Source.Queue(cursor: new(30, 40, 1100)); Pump(dispatcher);
        check("new observer motion continues the original bounded trail with original timestamps", broker.Frame.Trail.Count == 3 && broker.Frame.Trail.Take(2).SequenceEqual(before.Trail) && broker.Frame.Clicks.Single().Timestamp == TimeSpan.FromMilliseconds(1000));
        before = broker.Frame; broker.SuppressKeyboard(true); broker.Configure(Both, true, identity, "paused");
        check("adding keyboard observation preserves pointer state and known privacy suppression", broker.Frame.Clicks.SequenceEqual(before.Clicks) && broker.Frame.Trail.SequenceEqual(before.Trail) && Enum.GetValues<PresentationKeyboardScope>().All(scope => broker.Frame.ForKeyboardScope(scope) is { KeyboardSuppressed: true, PressedKeys.Count: 0, RecentChords.Count: 0 }));
        broker.SuppressKeyboard(false); factory.Requests.Last().Source.Queue([Key(0x41, time: 1100)]); Pump(dispatcher);
        check("restarted keyboard observation accepts only fresh events without resurrecting old modifiers or chords", broker.Frame.PressedKeys.Single().VirtualKey == 0x41 && broker.Frame.RecentChords.Single().Keys.Single().VirtualKey == 0x41);
        clock.Time = TimeSpan.FromMilliseconds(2000); factory.Requests.Last().Source.Queue(); Pump(dispatcher);
        check("retained pointer transients still expire by their original event times", broker.Frame.Clicks.Count == 0 && broker.Frame.Trail.Count == 0);
        clock.Time = TimeSpan.FromMilliseconds(2100); factory.Requests.Last().Source.Queue([new(PresentationObservedKind.ButtonDown, 2100, X: 40, Y: 50, HasPosition: true)], new(40, 50, 2100)); Pump(dispatcher);
        var nextContext = new object(); broker.Configure(pointerOnly, true, nextContext, "paused");
        check("a new document context still clears global pointer state instead of importing the previous context", broker.Frame.Clicks.Count == 0 && broker.Frame.Trail.Count == 0 && broker.Frame.CursorScreen is null);
        factory.Requests.Last().Source.Queue([new(PresentationObservedKind.ButtonDown, 2100, X: 50, Y: 60, HasPosition: true)], new(50, 60, 2100)); Pump(dispatcher);
        broker.Configure(Keyboard, true, nextContext, "paused");
        check("changing the pointer master alongside keyboard subscription does not retain its old mouse state", broker.Frame.Clicks.Count == 0 && broker.Frame.Trail.Count == 0 && broker.Frame.CursorScreen is null && broker.PointerFrame is null);
        broker.Configure(Both, true, nextContext, "paused");
        factory.Requests.Last().Source.Queue([new(PresentationObservedKind.ButtonDown, 2100, X: 60, Y: 70, HasPosition: true)], new(60, 70, 2100)); Pump(dispatcher);
        broker.Configure(pointerOnly, false, nextContext, "paused");
        check("disallowed observation still clears the pointer instead of using restart retention", !broker.Observing && broker.Frame.Clicks.Count == 0 && broker.Frame.Trail.Count == 0 && broker.Frame.CursorScreen is null);
    }

    private sealed class FakeClock { internal TimeSpan Time = TimeSpan.FromMilliseconds(1000); }
    private static void ModifierOnlyScopeDamage(Dispatcher dispatcher, Action<string, bool> check)
    {
        var factory = new FakeFactory(); var clock = new FakeClock(); bool inside = false;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { }, factory.Start, () => clock.Time, () => inside);
        broker.Configure(Keyboard with { ScopedKeyboard = true }, true, new object(), "paused"); broker.SetArmed(true);
        factory.Requests.Single().Source.Queue([Key(0x11) with { InputOwner = PresentationInputOwner.OtherApplication }]); Pump(dispatcher);
        var before = broker.Frame; inside = true; clock.Time += TimeSpan.FromMilliseconds(10);
        broker.RefreshKeyboardForFocusChange(); Pump(dispatcher);
        var same = typeof(DesktopSession).GetMethod("SameKeyboardPresentation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        check("scope-only modifier release repaints restricted live keyboards even when the all-applications keys and chord animation stay unchanged",
            before.PressedKeys.SequenceEqual(broker.Frame.PressedKeys) && !broker.HasKeyboardAnimation && !(bool)same.Invoke(null, [before, broker.Frame])!);
    }
    private sealed class FakeFactory
    {
        internal sealed record Request(long Epoch, bool Keyboard, bool Pointer, FakeSource Source);
        internal readonly List<Request> Requests = [];
        internal bool InitiallyRunning = true;
        internal bool DeferNextStart;
        private TaskCompletionSource<IPresentationInputSource>? pendingStart;
        internal void CompleteStart() => pendingStart!.SetResult(Requests.Last().Source);
        internal Task<IPresentationInputSource> Start(long epoch, bool keyboard, bool pointer, CancellationToken token)
        {
            var source = new FakeSource(epoch, InitiallyRunning);
            Requests.Add(new(epoch, keyboard, pointer, source));
            if (DeferNextStart)
            {
                DeferNextStart = false; pendingStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return pendingStart.Task;
            }
            return Task.FromResult<IPresentationInputSource>(source);
        }
    }
    private sealed class FakeSource(long epoch, bool running) : IPresentationInputSource
    {
        private readonly List<PresentationObservedInput> changes = [];
        private PresentationObservedCursor? cursor;
        private bool overflow;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Epoch => epoch;
        public bool IsRunning { get; private set; } = running;
        public Task Completion => completion.Task;
        public event Action? Available;
        internal int StopCount, DisposeCount;
        internal void Queue(PresentationObservedInput[]? keys = null, PresentationObservedCursor? cursor = null, bool overflow = false)
        {
            if (keys is not null) changes.AddRange(keys);
            if (cursor is not null) this.cursor = cursor;
            this.overflow |= overflow; Available?.Invoke();
        }
        public PresentationObservedBatch Drain()
        {
            var batch = new PresentationObservedBatch(epoch, changes, cursor, overflow);
            changes.Clear(); cursor = null; overflow = false; return batch;
        }
        public bool Stop() { StopCount++; IsRunning = false; completion.TrySetResult(); return true; }
        public void Dispose() { DisposeCount++; IsRunning = false; completion.TrySetResult(); }
    }
    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Synthetic scope transition did not settle");
            Pump(dispatcher); Thread.Sleep(1);
        }
        Pump(dispatcher);
    }
    private static void PumpFor(Dispatcher dispatcher, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Pump(dispatcher); Thread.Sleep(1); }
    }
}
