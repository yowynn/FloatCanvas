using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

/// <summary>Pure delegated-keyboard policy checks: no hooks, HWNDs or input injection.</summary>
internal static class DelegatedKeyboardStateTests
{
    internal static void Run(Action<bool, string> check)
    {
        var ordinary = New(passModifiers: false);
        check(Key(ordinary, 0xA0) && Key(ordinary, 0x41) && Key(ordinary, 0x41, up: true) && Key(ordinary, 0xA0, up: true),
            "Ordinary isolation still suppresses the complete modifier and typing streams by default");
        var ordinaryPackets = Drain(ordinary);
        check(ordinaryPackets.Count == 4 && ordinaryPackets.All(p => !p.IsStateOnly),
            "Ordinary isolation continues to produce replayable key packets, including modifiers");

        foreach (var (key, modifier) in new (uint, OverlayModifiers)[]
        {
            (0xA0, OverlayModifiers.Shift), (0xA1, OverlayModifiers.Shift),
            (0xA2, OverlayModifiers.Control), (0xA3, OverlayModifiers.Control),
            (0xA4, OverlayModifiers.Alt), (0xA5, OverlayModifiers.Alt),
            (0x5B, OverlayModifiers.Windows), (0x5C, OverlayModifiers.Windows)
        })
        {
            var state = New();
            bool down = Key(state, key), repeat = Key(state, key, now: 120), up = Key(state, key, up: true, now: 130);
            var packets = Drain(state);
            check(!down && !repeat && !up && state.IsActive,
                "Delegated modifier down, repeat and matching up all pass unchanged: " + key);
            check(packets.Count >= 2 && packets.All(p => p.Kind == KeyboardInputPacketKind.Key && p.IsStateOnly),
                "Delegated modifier packets update private state without requesting private key replay: " + key);
            check(packets[0].Modifiers == modifier && packets[^1].Modifiers == OverlayModifiers.None && packets[^1].IsKeyUp,
                "Delegated modifier state-only snapshots retain their down and release state: " + key);
            check(packets.Zip(packets.Skip(1), (a, b) => a.Sequence < b.Sequence).All(x => x),
                "Delegated state-only snapshots retain increasing queue sequence order: " + key);
        }

        var shiftSpace = New();
        check(!Key(shiftSpace, 0xA0) && Key(shiftSpace, 0x20) && !Key(shiftSpace, 0xA0, up: true) && Key(shiftSpace, 0x20, up: true),
            "Delegated Shift+Space passes only the modifier stream and suppresses both Space edges");
        var shifted = Drain(shiftSpace);
        check(shifted.Count == 4 && shifted[0].IsStateOnly && !shifted[1].IsStateOnly && shifted[2].IsStateOnly && !shifted[3].IsStateOnly,
            "State-only Shift edges remain ordered around the private Space key packets");
        check(shifted.Select(p => p.Modifiers).SequenceEqual(new[] { OverlayModifiers.Shift, OverlayModifiers.Shift, OverlayModifiers.None, OverlayModifiers.None }),
            "Private key snapshots preserve the exact modifier state even when Shift is released before the main key");

        var ctrlZ = New();
        check(!Key(ctrlZ, 0xA2) && Key(ctrlZ, 0x5A) && Key(ctrlZ, 0x5A, up: true) && !Key(ctrlZ, 0xA2, up: true),
            "Delegated Ctrl+Z passes Ctrl while isolating the complete Z stream");
        var undo = Drain(ctrlZ);
        check(undo.Where(p => !p.IsStateOnly).All(p => p.VirtualKey == 0x5A && p.Modifiers == OverlayModifiers.Control) &&
            undo.Count(p => !p.IsStateOnly) == 2,
            "The delegated undo chord has exactly one replayable main-key down and up with Ctrl state");

        foreach (bool reverse in new[] { false, true })
        {
            var switching = New();
            if (reverse) check(!Key(switching, 0xA0), "Reverse task switching passes the original Shift down");
            check(!Key(switching, 0xA4), "Task switching passes the original Alt down");
            // Claim the state packets before ending the lease: they must never create
            // suppressed-release ownership merely because the UI consumed the snapshot.
            check(Drain(switching).All(p => p.IsStateOnly), "Task-switch modifiers have no private replay before Tab");
            check(!Key(switching, 0x09, flags: 0x20) && !switching.IsActive && switching.LastEndReason == KeyboardLeaseEndReason.SystemEscape,
                "Delegated " + (reverse ? "Shift+Alt+Tab" : "Alt+Tab") + " passes Tab and ends its lease as SystemEscape");
            check(!Key(switching, 0x09, up: true, own: false, flags: 0x20) && !Key(switching, 0xA4, up: true, own: false) &&
                (!reverse || !Key(switching, 0xA0, up: true, own: false)),
                "Task-switch main key and modifier releases all retain their original global route");
            check(Drain(switching).All(p => p.Kind != KeyboardInputPacketKind.Key || p.IsStateOnly),
                "Ending a delegated task-switch chord cannot privately replay its passed modifiers or Tab");
        }

        var ctrlEscape = New();
        check(!Key(ctrlEscape, 0xA2), "Delegated Ctrl+Esc passes the original Ctrl down");
        Drain(ctrlEscape);
        check(!Key(ctrlEscape, 0x1B) && !ctrlEscape.IsActive && ctrlEscape.LastEndReason == KeyboardLeaseEndReason.SystemEscape &&
            !Key(ctrlEscape, 0x1B, up: true, own: false) && !Key(ctrlEscape, 0xA2, up: true, own: false),
            "Delegated Ctrl+Esc ends as SystemEscape and passes both release streams");

        var windowsChord = New();
        check(!Key(windowsChord, 0x5B) && !Key(windowsChord, 0xA0) && !Key(windowsChord, 0x53) &&
            !Key(windowsChord, 0x53, up: true) && !Key(windowsChord, 0xA0, up: true) && !Key(windowsChord, 0x5B, up: true),
            "Delegated Win+Shift+S leaves every original edge available to Windows");
        check(windowsChord.IsActive && Drain(windowsChord).All(p => p.Kind == KeyboardInputPacketKind.Key && p.IsStateOnly),
            "A delegated Windows chord emits only modifier snapshots and waits for actual foreground loss");

        var typing = New();
        check(Key(typing, 0x41) && Key(typing, 0x41, now: 120) && Key(typing, 0x41, up: true, now: 130),
            "Ordinary characters remain fully suppressed in delegated mode");
        var typed = Drain(typing);
        check(typed.Count == 3 && typed.All(p => p.VirtualKey == 0x41 && !p.IsStateOnly) &&
            !typed[0].IsRepeat && typed[1].IsRepeat && typed[2].IsKeyUp,
            "Delegated characters retain one ordered private down, repeat and matching up");

        NativeSystemKeys(check);

        var ending = New();
        check(!Key(ending, 0xA0), "A delegated modifier remains passed while another key is isolated");
        Drain(ending);
        check(Key(ending, 0x58), "The held main-key down is isolated before mode exit");
        Drain(ending);
        ending.End(KeyboardLeaseEndReason.Explicit, 200);
        var balance = Drain(ending);
        check(balance.Count(p => p.Kind == KeyboardInputPacketKind.Key && !p.IsStateOnly && p.VirtualKey == 0x58 && p.IsKeyUp && p.IsLocalBalance) == 1 &&
            balance.All(p => p.Kind != KeyboardInputPacketKind.Key || p.IsStateOnly || p.VirtualKey == 0x58),
            "Mode exit balances only the isolated main key without privately replaying a passed modifier");
        check(!Key(ending, 0xA0, up: true, own: false, now: 210) && Key(ending, 0x58, own: false, now: 220) &&
            Key(ending, 0x58, up: true, own: false, now: 230) && !Key(ending, 0x59, own: false, now: 240),
            "After exit, passed modifiers retain release ownership while only the held isolated main-key tail remains swallowed");

        var reentry = New();
        reentry.End(KeyboardLeaseEndReason.Explicit, 200); Drain(reentry);
        reentry.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 300);
        check(Key(reentry, 0xA0) && Drain(reentry).All(p => !p.IsStateOnly),
            "A later ordinary lease restores the default full-isolation policy instead of inheriting delegated modifier passthrough");

        PendingBalanceHandoff(check);
    }

    private static void NativeSystemKeys(Action<bool, string> check)
    {
        // Pure policy input only: never invoke the system screenshot, lock or media actions.
        foreach (uint key in new uint[] { 0x14, 0x90, 0x91, 0x2C, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3 })
        {
            var state = New();
            bool down = Key(state, key), repeat = Key(state, key, now: 120), up = Key(state, key, up: true, now: 130);
            var packets = Drain(state);
            string label = "Delegated native system key " + key.ToString("X2");
            check(!down && !repeat && !up && state.IsActive,
                label + " preserves its entire original down, repeat and release stream");
            check(packets.Count == 3 && packets.All(p => p.Kind == KeyboardInputPacketKind.Key && p.VirtualKey == key && p.IsStateOnly) &&
                !packets[0].IsRepeat && packets[1].IsRepeat && packets[2].IsKeyUp,
                label + " emits only ordered state snapshots without private key replay");
            if (key is 0x14 or 0x90 or 0x91)
            {
                check(packets.All(p => (p.KeyboardState[(int)key] & 1) != 0) && (state.CopyLocalState()[(int)key] & 1) != 0,
                    label + " toggles local lock state once on a fresh down, retaining it through repeat and release");
                check(!Key(state, key, now: 140) && (state.CopyLocalState()[(int)key] & 1) == 0 &&
                    !Key(state, key, now: 150) && (state.CopyLocalState()[(int)key] & 1) == 0 &&
                    !Key(state, key, up: true, now: 160) && (state.CopyLocalState()[(int)key] & 1) == 0,
                    label + " toggles back only on the next fresh down and never on its repeat or release");
                check(Drain(state).All(p => p.IsStateOnly),
                    label + " never turns lock-state synchronization into a second native key message");
            }
        }
    }

    private static void PendingBalanceHandoff(Action<bool, string> check)
    {
        foreach (bool modifierHeld in new[] { false, true })
        {
            var source = New();
            Key(source, 0xA0); Key(source, 0x58);
            if (!modifierHeld) Key(source, 0xA0, up: true);
            var input = Drain(source);
            source.End(KeyboardLeaseEndReason.Explicit, 200);
            var ending = Drain(source);
            var balances = ending.Where(p => p.Kind == KeyboardInputPacketKind.Key && p.IsLocalBalance).ToArray();
            string label = "Delegated pending handoff with passed Shift held=" + modifierHeld;
            check(balances.Count(p => p.IsStateOnly && p.VirtualKey == 0) == 1,
                label + " ends with one generic state-only local-balance snapshot");
            check(balances.All(p => (p.KeyboardState[0x58] & 0x80) == 0 &&
                ((p.KeyboardState[0xA0] & 0x80) != 0) == modifierHeld &&
                ((p.KeyboardState[0x10] & 0x80) != 0) == modifierHeld),
                label + " clears the private main key without inventing a passed-modifier release");
            check(balances.Where(p => !p.IsStateOnly).All(p => p.VirtualKey == 0x58 && p.IsKeyUp),
                label + " creates replayable balancing releases only for actually isolated main keys");

            // Exercise the real consumer's lease guard without creating a host window,
            // installing hooks or claiming a native ACK. Each notification consumes
            // its sole queued packet, so completion never posts native notifications.
            var queueState = new KeyboardSuppressionState(256, 2000);
            var queued = Field<Queue<KeyboardInputPacket>>(queueState, "queue");
            var service = (KeyboardInputIsolation)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardInputIsolation));
            Set(service, "gate", new object()); Set(service, "state", queueState);
            var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
            Set(host, "_threadId", GetCurrentThreadId());
            Set(host, "_keyboardIsolation", service);
            Set(host, "<IsEditing>k__BackingField", true);
            Set(host, "_keyboardLease", 0L);
            var delivered = new List<KeyboardInputPacket>();
            byte[] local = input[^1].CopyKeyboardState();
            Set(host, "_dispatchPrivateKey", (Action<KeyboardInputPacket, Action>)((packet, complete) =>
            {
                delivered.Add(packet); local = packet.CopyKeyboardState(); complete();
            }));
            foreach (var packet in input.Concat(ending)) Consume(packet);
            check(delivered.Select(p => p.Sequence).SequenceEqual(balances.Select(p => p.Sequence)),
                label + " routes only old local balances while the next native lease is pending");
            check((local[0x58] & 0x80) == 0 && ((local[0xA0] & 0x80) != 0) == modifierHeld,
                label + " lets the pending consumer retire the old private down before another ACK");
            check(host.IsEditing && delivered.All(p => p.IsLocalBalance),
                label + " ignores old keydowns and LeaseEnded packets rather than ending the pending session");

            // Once a new lease exists, the same old balances must no longer clear its state.
            Set(host, "_keyboardLease", source.LeaseId + 1);
            delivered.Clear(); local = input[^1].CopyKeyboardState();
            byte[] newLocal = (byte[])local.Clone();
            foreach (var packet in ending) Consume(packet);
            check(delivered.Count == 0 && local.AsSpan().SequenceEqual(newLocal) && host.IsEditing,
                label + " cannot apply old balances or old end notifications after a new lease starts");

            void Consume(KeyboardInputPacket packet)
            {
                queued.Enqueue(packet);
                bool handled = (bool)typeof(OverlayHost).GetMethod("HandleKeyboardControlMessage", Private)!
                    .Invoke(host, [0x8003u, (nuint)0])!;
                check(handled && queueState.QueuedCount == 0,
                    label + " consumes the owned queue notification without a native keyboard route");
            }
        }
    }

    private static KeyboardSuppressionState New(bool passModifiers = true)
    {
        var state = new KeyboardSuppressionState(256, 2000);
        state.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 100, passModifiers: passModifiers);
        return state;
    }

    private static bool Key(KeyboardSuppressionState state, uint key, bool up = false, bool own = true, long now = 110, uint flags = 0) =>
        state.Process(key, 0, flags | (up ? 0x80u : 0), (uint)now, up,
            up ? ((flags & 0x20) != 0 ? 0x105u : 0x101u) : ((flags & 0x20) != 0 ? 0x104u : 0x100u),
            42, own, true, now);

    private static List<KeyboardInputPacket> Drain(KeyboardSuppressionState state)
    {
        var packets = new List<KeyboardInputPacket>();
        while (state.TryDequeue(out var packet))
        {
            packets.Add(packet!); state.Complete(packet!.Sequence);
        }
        return packets;
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Set(object value, string name, object field) => value.GetType().GetField(name, Private)!.SetValue(value, field);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
