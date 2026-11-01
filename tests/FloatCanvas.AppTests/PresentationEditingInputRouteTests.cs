using System.Reflection;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PresentationEditingInputRouteTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var demand = new PresentationRuntimeDemand(true, true, true, true, true, 900, 450, 800);
        var identity = new object(); Source? source = null; bool internalFocus = true; long now = 1000;
        using var broker = new PresentationRuntimeBroker(dispatcher, () => { },
            (epoch, _, _, _) => Task.FromResult<IPresentationInputSource>(source = new(epoch)),
            () => TimeSpan.FromMilliseconds(now), () => internalFocus);
        broker.Configure(demand, true, identity, "paused"); broker.SetArmed(true);
        int beforeInput = 0; broker.BeforeInput += _ => beforeInput++;
        Route(true);
        int beforeEditingBatch = beforeInput;
        Observe([Key(0x11), Key(0x43), Click()]);
        check("editing input reaches semantic Ctrl+C and click drawing even when raw transitions are absent",
            broker.Frame.RecentChords is [var chord] && chord.Keys.Select(key => key.Label).SequenceEqual(new[] { "Ctrl", "C" }) && broker.Frame.Clicks.Count == 1);
        check("editing input goes through the same BeforeInput privacy boundary", beforeInput == beforeEditingBatch + 1);
        var token = Token(); var lifetime = broker.Lifetime; Route(true);
        check("refreshing an unchanged editing route preserves its epoch and display lifetime", token == Token() && ReferenceEquals(lifetime, broker.Lifetime));
        source!.Queue([Key(0x11), Key(0x43), Click()], new(70, 80, 1000)); Drain();
        check("editing mode consumes each transition once while raw cursor polling continues",
            broker.Frame.RecentChords.Count == 1 && broker.Frame.Clicks.Count == 1 && broker.Frame.CursorScreen == new PointD(70, 80));
        Observe([Key(0x43, true), Key(0x11, true)]);
        check("editing key releases close the complete chord without a residual modifier",
            broker.Frame.PressedKeys.Count == 0 && broker.Frame.RecentChords is [{ IsHeld: false }]);
        broker.SuppressKeyboard(true); Observe([Key(0x41), Click()]);
        check("editing keyboard still obeys password suppression while pointer clicks remain active",
            broker.Frame.KeyboardSuppressed && broker.Frame.RecentChords.Count == 0 && broker.Frame.Clicks.Count == 2);
        broker.SuppressKeyboard(false); broker.ConfigureKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas);
        Observe([Key(0x41), Click()]);
        check("editing input obeys the selected application scope without suppressing the mouse",
            broker.Frame.RecentChords.Count == 0 && broker.Frame.Clicks.Count == 3);
        broker.ConfigureKeyboardScope(PresentationKeyboardScope.AllApplications);
        Observe([Key(0x11), Key(0x43)]);
        Route(false);
        check("leaving editing releases held keys but retains complete fading keyboard history",
            broker.Frame.PressedKeys.Count == 0 && broker.Frame.RecentChords is [{ IsHeld: false }]);
        int clicks = broker.Frame.Clicks.Count;
        Observe([Key(0x41), Click()], token);
        check("a delayed editing batch cannot replay after returning to raw input", broker.Frame.RecentChords.Count == 1 && broker.Frame.Clicks.Count == clicks);
        now = 1010;
        source.Queue([Key(0x42) with { Milliseconds = now }, Click() with { Milliseconds = now }]); Drain();
        check("raw input resumes outside editing and accepts keyboard and click transitions",
            broker.Frame.RecentChords.Count == 2 && broker.Frame.Clicks.Count == clicks + 1);
        Route(true);
        Observe([Key(0x41), Click()], token);
        check("reentering editing rejects the previous route generation", broker.Frame.RecentChords.Count == 2 && broker.Frame.Clicks.Count == clicks + 1);
        Observe([Key(0x41), Click()]);
        check("the new editing generation records again without restarting its native source",
            broker.Frame.RecentChords.Count == 3 && broker.Frame.Clicks.Count == clicks + 2 && source.StopCount == 0);
        token = Token(); broker.Configure(PresentationRuntimeDemand.Empty, true, identity, "paused");
        Observe([Key(0x43), Click()], token);
        check("removing all demand stops editing input and discards in-flight packets", !broker.Observing && broker.Frame.RecentChords.Count == 0 && broker.Frame.Clicks.Count == 0 && source.StopCount > 0);

        broker.Configure(demand, true, identity, "paused");
        source!.Queue([Key(0x58) with { Milliseconds = now }]);
        Route(true);
        check("entering editing consumes pending raw input before changing routes", broker.Frame.RecentChords is [{ IsHeld: false } pending] && pending.Keys[^1].Label == "X");
        Observe([Key(0x11), Key(0x43), Click()]);
        source.Queue([Key(0x11), Key(0x43), Click()]);
        now = 1020; Route(false); Drain();
        check("raw events queued during editing cannot replay across the exit boundary", broker.Frame.RecentChords.Count == 2 && broker.Frame.Clicks.Count == 1 && broker.Frame.PressedKeys.Count == 0);
        source.Queue([Key(0x43), Click()]); Drain();
        check("late native delivery with an older event timestamp cannot revive stale keys or clicks", broker.Frame.RecentChords.Count == 2 && broker.Frame.Clicks.Count == 1 && broker.Frame.PressedKeys.Count == 0);
        now = 1030;
        source.Queue([Key(0x41) with { Milliseconds = now }]); Drain(); Route(true);
        Observe([Key(0x11), Key(0x43)]);
        source.Queue([new(PresentationObservedKind.DeviceRemoved, now, Device: 9876)]); Drain();
        check("removing a physical device releases anonymous editing held keys without discarding history", broker.Frame.RecentChords.Count == 4 && broker.Frame.RecentChords.All(value => !value.IsHeld) && broker.Frame.PressedKeys.Count == 0);

        void Route(bool enabled) => typeof(PresentationRuntimeBroker).GetMethod("ConfigureEditingInput", Hidden)?.Invoke(broker, [enabled]);
        long Token() => (long?)typeof(PresentationRuntimeBroker).GetProperty("EditingInputEpoch", Hidden)?.GetValue(broker) ?? 1;
        void Observe(PresentationObservedInput[] values, long? epoch = null) => typeof(PresentationRuntimeBroker).GetMethod("ObserveEditingInput", Hidden)?.Invoke(broker, [new PresentationObservedBatch(epoch ?? Token(), values, null, false)]);
        void Drain() => typeof(PresentationRuntimeBroker).GetMethod("Drain", Hidden)!.Invoke(broker, null);
    }
    private static PresentationObservedInput Key(int vk, bool up = false) => new(up ? PresentationObservedKind.KeyUp : PresentationObservedKind.KeyDown,
        1000, 1, vk, vk, 0, vk == 0x11 ? "Ctrl" : ((char)vk).ToString());
    private static PresentationObservedInput Click() => new(PresentationObservedKind.ButtonDown, 1000, 1, X: 70, Y: 80, HasPosition: true, Button: 0);
    private sealed class Source(long epoch) : IPresentationInputSource
    {
        private PresentationObservedBatch pending = new(epoch, [], null, false);
        public long Epoch => epoch;
        public bool IsRunning { get; private set; } = true;
        public Task Completion => Task.CompletedTask;
        public event Action? Available;
        public int StopCount { get; private set; }
        internal void Queue(PresentationObservedInput[] values, PresentationObservedCursor? point = null)
        { pending = new(epoch, values, point, false); Available?.Invoke(); }
        public PresentationObservedBatch Drain() { var batch = pending; pending = new(epoch, [], null, false); return batch; }
        public bool Stop() { StopCount++; IsRunning = false; return true; }
        public void Dispose() => IsRunning = false;
    }
}
