using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PresentationKeyboardStateTests
{
    internal sealed record TraceEvent(string Scenario, long Time, string Source, bool Up, int VirtualKey, int ScanCode, uint Flags, int Foreground);
    internal static void Run(Action<string, bool> check)
    {
        var assembly = typeof(PresentationKeyboardStateTests).Assembly;
        using var resource = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith("WindowsKeyPhysicalTrace.json")))!;
        var trace = JsonSerializer.Deserialize<TraceEvent[]>(resource)!;
        check("captured Win+A fixture retains the missing Raw A-down and the injected Win replay",
            trace.Any(e => e.Scenario == "WinA" && e.Source == "HOOK" && e.VirtualKey == 0x41 && !e.Up) &&
            !trace.Any(e => e.Scenario == "WinA" && e.Source == "RAW" && e.VirtualKey == 0x41 && !e.Up) &&
            trace.Any(e => e.Scenario == "WinA" && (e.Flags & 0x10) != 0));
        foreach (bool editing in new[] { false, true })
        foreach (string scenario in new[] { "WinA", "HoldWin" })
        {
            using var h = new PresentationHookTestHarness(editing);
            bool uninterrupted = true; int physicalDowns = 0;
            foreach (var e in trace.Where(e => e.Scenario == scenario))
            {
                h.Now = 1000 + e.Time; h.Foreground = e.Foreground;
                h.Tick(); // Previously this sampled a false async state and fabricated releases.
                if (e.Source != "HOOK") continue;
                h.Send(e.VirtualKey, e.ScanCode, e.Flags, e.Up);
                if (scenario == "HoldWin" && (e.Flags & 0x10) == 0 && !e.Up)
                {
                    physicalDowns++;
                    uninterrupted &= h.Broker.Frame.PressedKeys.Count == 1 && h.Broker.Frame.RecentChords is [{ IsHeld: true }];
                }
            }
            var frame = h.Broker.Frame;
            if (scenario == "WinA")
                check($"captured Win+A editing={editing}: exactly one complete released Win+A chord, no residual key",
                    frame.PressedKeys.Count == 0 && frame.RecentChords is [{ IsHeld: false } chord] &&
                    chord.Keys.Select(key => key.VirtualKey).SequenceEqual(new[] { 0x5B, 0x41 }));
            else
            {
                check($"captured held Win editing={editing}: all {physicalDowns} real down/repeat events remain one held preview", physicalDowns == 120 && uninterrupted);
                check($"captured held Win editing={editing}: physical up closes once; injected down/up never adds a record",
                    frame.PressedKeys.Count == 0 && frame.RecentChords is [{ IsHeld: false, Keys.Count: 1 } chord] && chord.Keys[0].VirtualKey == 0x5B);
            }
            var scope = editing ? PresentationKeyboardScope.FloatCanvasOnly : PresentationKeyboardScope.OutsideFloatCanvas;
            check($"captured {scenario} editing={editing}: scoped history receives the same complete chord", frame.ForKeyboardScope(scope).RecentChords.Count == 1);
        }
        using (var h = new PresentationHookTestHarness(false))
        {
            h.Send(0x5B, 0x5B, 1); long epoch = h.Broker.InputEpoch;
            h.Now += 10; h.Broker.ConfigureEditingInput(true);
            h.Send(0x41, 0x1E, 0); h.Send(0x41, 0x1E, 0x80, true);
            h.Broker.ConfigureEditingInput(false); h.Send(0x5B, 0x5B, 0x81, true);
            check("editing/pass-through switching preserves the keyboard subscription and an in-progress chord",
                h.Broker.InputEpoch == epoch && h.Broker.Frame.PressedKeys.Count == 0 && h.Broker.Frame.RecentChords is [{ IsHeld: false, Keys.Count: 2 }]);
            h.Now += 1000; h.Tick();
            check("released chord expires without sampling keyboard state", h.Broker.Frame.RecentChords.Count == 0);
            h.Send(0x5B, 0x5B, 1);
            h.Input.Queue([new(PresentationObservedKind.DeviceRemoved, h.Now, Device: 9876,
                DeviceKind: PresentationDeviceKind.Keyboard)]); h.Drain();
            check("device removal cancels anonymous hook state without adding a release record",
                h.Broker.Frame.PressedKeys.Count == 0 && h.Broker.Frame.RecentChords.Count == 0);
            h.Send(0x5B, 0x5B, 1); h.Now += 10000; h.Tick();
            check("a physical long hold survives arbitrary idle timer ticks", h.Broker.Frame.RecentChords is [{ IsHeld: true }] && h.Broker.Frame.PressedKeys.Count == 1);
            h.Available = false; h.Tick();
            check("failed hook availability cancels input instead of manufacturing key-up history", !h.Broker.Observing && h.Broker.Frame.PressedKeys.Count == 0 && h.Broker.Frame.RecentChords.Count == 0);
        }
    }
}

// Exercises the production tap (via its existing internal seam), broker and timeline.
// No native hook, OS input injection or visible window is created by this replay.
internal sealed class PresentationHookTestHarness : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly object tap;
    private readonly Type tapType = typeof(PresentationInputObserver).Assembly.GetType("FloatCanvas.Windows.PresentationInputTap")!;
    internal long Now = 1000;
    internal bool Available = true;
    internal bool Inside;
    internal nint Foreground = 1;
    private readonly PresentationKeyboardPrivacyGate privacy;
    internal PresentationRuntimeBroker Broker { get; }
    internal Source Input { get; private set; } = null!;
    internal PresentationHookTestHarness(bool editing)
    {
        Inside = editing;
        Broker = new(Dispatcher.CurrentDispatcher, () => { },
            (epoch, _, _, _) => Task.FromResult<IPresentationInputSource>(Input = new(epoch)),
            () => TimeSpan.FromMilliseconds(Now), () => Inside, () => Available);
        Broker.Configure(new(true, true, true, true, true, 900, 450, 800) { ScopedKeyboard = true }, true, new object(), "paused");
        Broker.SetArmed(true); Broker.ConfigureEditingInput(editing);
        privacy = new(Dispatcher.CurrentDispatcher, Broker,
            completed => new PresentationKeyboardPrivacyProbe(completed, () => false, () => Foreground),
            () => Foreground, () => Now, externalEvents: false,
            // Trace foreground IDs represent command surfaces, not real HWNDs
            // or text editors. PrivacyGateTests exercise unclassified focus.
            isCanvasInput: _ => true);
        Broker.BeforeInput += _ => privacy.Observe();
        tap = Activator.CreateInstance(tapType, true)!;
        tapType.GetMethod("Configure", Hidden)!.Invoke(tap, [true, true, Broker.InputEpoch]);
    }
    internal void Send(int vk, int scan, uint flags, bool up = false)
    {
        tapType.GetMethod("Keyboard", Hidden)!.Invoke(tap, [(uint)vk, (uint)scan, flags, up, Now, true,
            Inside ? PresentationInputOwner.FloatCanvas : PresentationInputOwner.OtherApplication]);
        uint generation = (uint)tapType.GetProperty("Generation", Hidden)!.GetValue(tap)!;
        object?[] args = [generation, null];
        if ((bool)tapType.GetMethod("TryDrain", Hidden)!.Invoke(tap, args)!) Broker.ObserveHookInput((PresentationObservedBatch)args[1]!);
    }
    internal void Tick() { privacy.Observe(); typeof(PresentationRuntimeBroker).GetMethod("Tick", Hidden)!.Invoke(Broker, null); }
    internal void Drain() => typeof(PresentationRuntimeBroker).GetMethod("Drain", Hidden)!.Invoke(Broker, null);
    public void Dispose() { privacy.Dispose(); Broker.Dispose(); }
    internal sealed class Source(long epoch) : IPresentationInputSource
    {
        private PresentationObservedBatch batch = new(epoch, [], null, false);
        public long Epoch => epoch;
        public bool IsRunning { get; private set; } = true;
        public Task Completion => Task.CompletedTask;
        public event Action? Available;
        internal void Queue(PresentationObservedInput[] keys, PresentationObservedCursor? cursor = null)
        { batch = new(epoch, keys, cursor, false); Available?.Invoke(); }
        public PresentationObservedBatch Drain() { var result = batch; batch = new(epoch, [], null, false); return result; }
        public bool Stop() { IsRunning = false; return true; }
        public void Dispose() => Stop();
    }
}
