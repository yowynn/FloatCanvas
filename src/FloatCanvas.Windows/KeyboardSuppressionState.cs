using L = FloatCanvas.Localization.AppText;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FloatCanvas.WindowsTests")]

namespace FloatCanvas.Windows;

/// <summary>Pure state machine; its owner serializes calls. No native APIs or UI callbacks.</summary>
internal sealed class KeyboardSuppressionState
{
    private readonly Queue<KeyboardInputPacket> queue = new();
    private readonly int capacity;
    private readonly long tailLifetime;
    private readonly byte[] local = new byte[256];
    private readonly bool[] physical = new bool[256], inherited = new bool[256], suppressed = new bool[256];
    private readonly bool[] visibilityCommand = new bool[256];
    private readonly bool[] passedDownOwnership = new bool[256];
    private readonly bool[] claimedDown = new bool[256];
    private readonly long[] tails = new long[256];
    private readonly uint[] scans = new uint[256], flags = new uint[256];
    private readonly nint[] targets = new nint[256];
    private HotkeyBinding toggle, hide;
    internal bool CommandsSuspended { get; set; }
    private long sequence, lease, inFlight;
    private KeyboardInputPacket? inFlightPacket;
    private bool notificationPending;
    private bool foregroundResumeVeto;
    private nint inputMethodOrigin;
    private long inputMethodDeadline;
    private bool passModifiers;

    public KeyboardSuppressionState(int capacity, long tailLifetime)
    {
        // 254 possible VKs plus command/end control packets must fit after clearing pending input.
        if (capacity < 256 || capacity > 8192) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (tailLifetime <= 0) throw new ArgumentOutOfRangeException(nameof(tailLifetime));
        this.capacity = capacity; this.tailLifetime = tailLifetime;
    }
    public bool IsActive { get; private set; }
    public long LeaseId => lease;
    public int QueuedCount => queue.Count;
    public long SuppressedCount { get; private set; }
    public long TailSuppressedCount { get; private set; }
    public KeyboardLeaseEndReason? LastEndReason { get; private set; }
    internal bool CanResumeForegroundLoss => !IsActive && LastEndReason == KeyboardLeaseEndReason.ForegroundLost && !foregroundResumeVeto;
    internal byte[] CopyLocalState() => (byte[])local.Clone();
    internal OverlayModifiers CurrentModifiers => Modifiers();

    public void UpdateHotkeys(HotkeyBinding toggle, HotkeyBinding hide)
    {
        ValidateHotkey(toggle); ValidateHotkey(hide);
        if (toggle == hide) throw new ArgumentException(L.Get("Hotkeys.DuplicateBindings"));
        this.toggle = toggle; this.hide = hide;
    }

    public long Begin(byte[] initialState, HotkeyBinding toggle, HotkeyBinding hide, long now, bool passModifiers = false)
    {
        if (initialState.Length != 256) throw new ArgumentException(L.Get("Keyboard.InvalidStateLength"), nameof(initialState));
        ValidateHotkey(toggle); ValidateHotkey(hide);
        if (toggle == hide) throw new ArgumentException(L.Get("Hotkeys.DuplicateBindings"));
        if (IsActive) End(KeyboardLeaseEndReason.Replaced, now);
        CancelInputMethodSwitch();
        // Keep queued balancing/end packets from the old lease, but never stale input keydowns.
        var controls = queue.Where(p => p.IsLocalBalance || p.Kind == KeyboardInputPacketKind.LeaseEnded).ToArray();
        queue.Clear(); foreach (var packet in controls) queue.Enqueue(packet);
        Array.Copy(initialState, local, 256); Array.Clear(physical); Array.Clear(inherited); Array.Clear(suppressed); Array.Clear(visibilityCommand);
        ExpireTails(now);
        for (int i = 1; i < 255; i++)
        {
            // The current async snapshot owns local state. A passed down may still be
            // pending anywhere in the hook chain, so only its stream ownership survives
            // until an observed up; it must not force a stale local modifier down.
            // A missed historical up conservatively passes the next complete stream,
            // rather than risking swallowing an outstanding global release.
            physical[i] = (local[i] & 0x80) != 0;
            inherited[i] = physical[i] || passedDownOwnership[i];
            if (tails[i] != 0) { inherited[i] = false; physical[i] = true; local[i] &= 0x7F; }
        }
        NormalizeModifiers();
        this.toggle = toggle; this.hide = hide; lease++; IsActive = true; LastEndReason = null; foregroundResumeVeto = false;
        this.passModifiers = passModifiers;
        return lease;
    }

    /// <returns>True only when this event must be suppressed.</returns>
    public bool Process(uint vk, uint scan, uint eventFlags, uint time, bool up, uint message,
        nint foreground, bool ownsForeground, bool healthy, long now, OverlayPoint? pointerPosition = null, bool? pointerLeftDown = null)
    {
        if (vk is 0 or >= 255) return false;
        int key = Canonical((int)vk, scan, eventFlags);
        ExpireTails(now);
        // A bounded exception after lease end: only the exact previously swallowed key stream.
        if (ConsumeTail(key, scan, eventFlags, up, now)) return true;
        bool switchNavigation = IsInputMethodNavigation(key, up, now);
        if (!up && (!ownsForeground && !switchNavigation ||
            ownsForeground && key is not (0x5B or 0x5C or 0x20))) CancelInputMethodSwitch();
        if (!IsActive)
        {
            if (!ownsForeground && !up && !switchNavigation) foregroundResumeVeto = true;
            if (switchNavigation) TrackInputMethodNavigation(key, up, now);
            TrackPassed(key, up); return false;
        }
        if (!ownsForeground || !healthy)
        {
            bool freshExternalDown = !ownsForeground && !up && !suppressed[key] && !inherited[key] && !switchNavigation;
            End(!ownsForeground ? KeyboardLeaseEndReason.ForegroundLost : KeyboardLeaseEndReason.HeartbeatExpired, now);
            if (freshExternalDown) foregroundResumeVeto = true;
            if (ConsumeTail(key, scan, eventFlags, up, now)) return true;
            if (switchNavigation) TrackInputMethodNavigation(key, up, now);
            TrackPassed(key, up); return false;
        }
        if (!up && key == 0x20 && (Modifiers() & OverlayModifiers.Windows) != 0 && !suppressed[key])
        { inputMethodOrigin = foreground; inputMethodDeadline = now + 2000; }
        if (inherited[key])
        {
            UpdateLocal(key, up, false);
            if (up) inherited[key] = false;
            TrackPassed(key, up);
            if (passModifiers)
            {
                if (queue.Count >= capacity) End(KeyboardLeaseEndReason.QueueOverflow, now);
                else queue.Enqueue(Packet(KeyboardInputPacketKind.Key, vk, scan, eventFlags, time, up, !up,
                    foreground, message: message, stateOnly: true));
            }
            return false; // Inherited global down must retain repeats and physical release.
        }
        if (!up && IsEscape(key, out var escape))
        {
            if (escape == KeyboardLeaseEndReason.EmergencyEscape)
            {
                UpdateLocal(key, false, !physical[key]); suppressed[key] = true;
                scans[key] = scan; flags[key] = eventFlags; targets[key] = foreground;
                End(escape, now); SuppressedCount++; return true;
            }
            End(escape, now);
            TrackPassed(key, up);
            return false; // No global reconstruction of an already-suppressed Alt/Ctrl modifier.
        }
        // Windows shortcuts belong to the OS. Keep editing until an actual foreground
        // change, and pass each new chord's whole stream (including release after Win-up).
        // A stream we already swallowed must still retain its matching release.
        if (key is 0x5B or 0x5C || (Modifiers() & OverlayModifiers.Windows) != 0 && !suppressed[key])
        {
            if (!up && (key is 0x5B or 0x5C ? !physical[key] : key != 0x20)) CancelInputMethodSwitch();
            UpdateLocal(key, up, !up && !physical[key]);
            inherited[key] = !up;
            TrackPassed(key, up);
            if (passModifiers)
            {
                if (queue.Count >= capacity) End(KeyboardLeaseEndReason.QueueOverflow, now);
                else queue.Enqueue(Packet(KeyboardInputPacketKind.Key, vk, scan, eventFlags, time, up, false,
                    foreground, message: message, stateOnly: true));
            }
            return false;
        }
        bool passedNativeKey = passModifiers && (key is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or
            0x14 or 0x90 or 0x91 or 0x2C or >= 0xAD and <= 0xB3);
        if (up && !suppressed[key] && !passedNativeKey) { UpdateLocal(key, true, false); TrackPassed(key, true); return false; }
        // Visibility is independent of editing. A consumed visibility key keeps its
        // stream until physical release even if its modifiers have already been lifted.
        if (visibilityCommand[key] && !up) { SuppressedCount++; return true; }
        if (queue.Count >= capacity)
        {
            End(KeyboardLeaseEndReason.QueueOverflow, now);
            if (ConsumeTail(key, scan, eventFlags, up, now)) return true;
            TrackPassed(key, up);
            return false; // The current, undelivered input fails open.
        }
        bool repeat = !up && physical[key];
        UpdateLocal(key, up, !repeat && !up);
        // Delegation still delivers these original messages to our UI. Preserve their
        // global streams for Alt+Tab, reverse switching and Ctrl+Esc, along with lock,
        // screenshot and media keys. Only publish an ordered state snapshot, never a
        // second key message through the private bridge.
        if (passedNativeKey)
        {
            scans[key] = scan; flags[key] = eventFlags; targets[key] = foreground;
            queue.Enqueue(Packet(KeyboardInputPacketKind.Key, vk, scan, eventFlags, time, up, repeat,
                foreground, message: message, pointerPosition: pointerPosition, pointerLeftDown: pointerLeftDown, stateOnly: true));
            TrackPassed(key, up);
            return false;
        }
        suppressed[key] = !up; scans[key] = scan; flags[key] = eventFlags; targets[key] = foreground;
        SuppressedCount++;
        if (visibilityCommand[key])
        {
            visibilityCommand[key] = false;
            queue.Enqueue(Packet(KeyboardInputPacketKind.Key, vk, scan, eventFlags, time, true, false, foreground,
                balance:true, message:message));
            return true;
        }
        if (!CommandsSuspended && !up && !repeat && Matches(hide, vk))
        {
            visibilityCommand[key] = true;
            queue.Enqueue(Packet(KeyboardInputPacketKind.Hide, 0, 0, 0, 0, false, false, 0));
            return true;
        }
        if (!CommandsSuspended && !up && !repeat && Matches(toggle, vk))
        {
            End(KeyboardLeaseEndReason.Toggle, now, KeyboardInputPacketKind.Toggle);
            return true;
        }
        queue.Enqueue(Packet(KeyboardInputPacketKind.Key, vk, scan, eventFlags, time, up, repeat, foreground, message: message, pointerPosition: pointerPosition, pointerLeftDown: pointerLeftDown));
        return true;
    }

    private bool Matches(HotkeyBinding binding, uint vk) => binding.VirtualKey == vk && (binding.Modifiers & 15) == (uint)Modifiers();
    private OverlayModifiers Modifiers() => ((local[0x10] & 0x80) != 0 ? OverlayModifiers.Shift : 0) |
        ((local[0x11] & 0x80) != 0 ? OverlayModifiers.Control : 0) |
        ((local[0x12] & 0x80) != 0 ? OverlayModifiers.Alt : 0) |
        (((local[0x5B] | local[0x5C]) & 0x80) != 0 ? OverlayModifiers.Windows : 0);
    private bool IsEscape(int key, out KeyboardLeaseEndReason reason)
    {
        var modifiers = Modifiers();
        reason = KeyboardLeaseEndReason.SystemEscape;
        if (key == 0x1B && (modifiers & (OverlayModifiers.Control | OverlayModifiers.Alt)) == (OverlayModifiers.Control | OverlayModifiers.Alt))
        { reason = KeyboardLeaseEndReason.EmergencyEscape; return true; }
        return (key is 9 or 0x1B && (modifiers & OverlayModifiers.Alt) != 0) ||
            (key == 0x1B && (modifiers & OverlayModifiers.Control) != 0) ||
            (key == 0x2E && (modifiers & (OverlayModifiers.Control | OverlayModifiers.Alt)) == (OverlayModifiers.Control | OverlayModifiers.Alt));
    }
    private static int Canonical(int key, uint scan, uint eventFlags) => key switch
    {
        0x10 => scan == 0x36 ? 0xA1 : 0xA0,
        0x11 => (eventFlags & 1) != 0 ? 0xA3 : 0xA2,
        0x12 => (eventFlags & 1) != 0 ? 0xA5 : 0xA4,
        _ => key
    };
    internal static void ValidateHotkey(HotkeyBinding binding)
    {
        if (binding.VirtualKey is 0 or >= 255 || (binding.Modifiers & ~15u) != 0)
            throw new ArgumentOutOfRangeException(nameof(binding));
        if ((binding.Modifiers & 8) != 0 || binding.VirtualKey is 0x5B or 0x5C)
            throw new ArgumentException(L.Get("Hotkeys.WindowsKeyReserved"), nameof(binding));
        if (binding.VirtualKey == 0x1B && (binding.Modifiers & 3) == 3)
            throw new ArgumentException(L.Get("Hotkeys.EmergencyReleaseReserved"), nameof(binding));
        if ((binding.VirtualKey is 9 or 0x1B && (binding.Modifiers & 1) != 0) ||
            (binding.VirtualKey == 0x1B && (binding.Modifiers & 2) != 0) ||
            (binding.VirtualKey == 0x2E && (binding.Modifiers & 3) == 3))
            throw new ArgumentException(L.Get("Hotkeys.SystemExitReserved"), nameof(binding));
    }
    private bool ConsumeTail(int key, uint scan, uint eventFlags, bool up, long now)
    {
        if (tails[key] == 0 || !(scans[key] == scan || scan == 0 || scans[key] == 0) || ((flags[key] ^ eventFlags) & 1) != 0) return false;
        if (up) { tails[key] = 0; physical[key] = false; }
        else tails[key] = now + tailLifetime; // Idle expiry, not a maximum duration while repeats continue.
        TailSuppressedCount++; return true;
    }
    private void UpdateLocal(int key, bool up, bool freshDown)
    {
        physical[key] = !up;
        if (up) local[key] &= 0x7F;
        else { local[key] |= 0x80; if (freshDown && key is 0x14 or 0x90 or 0x91) local[key] ^= 1; }
        NormalizeModifiers();
    }
    private void NormalizeModifiers()
    {
        local[0x10] = (byte)((local[0x10] & 0x7F) | ((local[0xA0] | local[0xA1]) & 0x80));
        local[0x11] = (byte)((local[0x11] & 0x7F) | ((local[0xA2] | local[0xA3]) & 0x80));
        local[0x12] = (byte)((local[0x12] & 0x7F) | ((local[0xA4] | local[0xA5]) & 0x80));
    }
    private void TrackPassed(int key, bool up) { passedDownOwnership[key] = !up; }
    internal bool TryGetInputMethodSwitch(long expectedLease, long now, out nint origin)
    {
        origin = inputMethodOrigin;
        return expectedLease == lease && origin != 0 && now < inputMethodDeadline && !foregroundResumeVeto &&
            (IsActive || LastEndReason == KeyboardLeaseEndReason.ForegroundLost);
    }
    internal void CancelInputMethodSwitch() { inputMethodOrigin = 0; inputMethodDeadline = 0; }
    private bool IsInputMethodNavigation(int key, bool up, long now) =>
        TryGetInputMethodSwitch(lease, now, out _) && (key is 0x5B or 0x5C
            ? up || physical[key] : key == 0x20 && (Modifiers() & OverlayModifiers.Windows) != 0);
    private void TrackInputMethodNavigation(int key, bool up, long now)
    {
        UpdateLocal(key, up, false);
        if (key == 0x20 && !up) inputMethodDeadline = now + 2000;
    }
    internal void ObserveExternalPointerPress(long now)
    {
        if (IsActive) End(KeyboardLeaseEndReason.ForegroundLost, now);
        // A physical press explicitly targeted another process. Returning to an own window
        // before the UI drains its queue must not make this old lease resumable again.
        foregroundResumeVeto = true;
    }
    public void End(KeyboardLeaseEndReason reason, long now, KeyboardInputPacketKind? command = null)
    {
        if (reason != KeyboardLeaseEndReason.ForegroundLost) CancelInputMethodSwitch();
        if (!IsActive) return;
        IsActive = false; LastEndReason = reason;
        Array.Clear(visibilityCommand);
        // Merge pending old-lease releases with new ones: quick Begin/End cannot lose balance
        // or grow an unbounded backlog of control packets while a UI packet is in flight.
        var balanceKeys = new HashSet<int>(queue.Where(p => p.IsLocalBalance && !p.IsStateOnly).Select(p => (int)p.VirtualKey));
        for (int i = 1; i < 255; i++) if (claimedDown[i]) balanceKeys.Add(i);
        if (inFlightPacket is { Kind: KeyboardInputPacketKind.Key, IsStateOnly: false } pending)
            balanceKeys.Add(Canonical((int)pending.VirtualKey, pending.ScanCode, pending.Flags));
        queue.Clear();
        for (int i = 1; i < 255; i++)
        {
            if (!suppressed[i]) continue;
            suppressed[i] = false; tails[i] = now + tailLifetime; balanceKeys.Add(i);
        }
        foreach (int i in balanceKeys.Order())
        {
            local[i] &= 0x7F;
            NormalizeModifiers();
            queue.Enqueue(Packet(KeyboardInputPacketKind.Key, (uint)i, scans[i], flags[i] | 0x80, 0, true, false,
                targets[i], balance: true, message: (flags[i] & 0x20) != 0 ? 0x105u : 0x101u));
        }
        // Retire pending snapshots even when no private key remains held. Passed
        // modifiers keep their native stream and never acquire a private key-up.
        if (passModifiers) queue.Enqueue(Packet(KeyboardInputPacketKind.Key, 0, 0, 0, 0, false, false, 0,
            balance: true, stateOnly: true));
        if (command is { } kind) queue.Enqueue(Packet(kind, 0, 0, 0, 0, false, false, 0, reason: reason));
        queue.Enqueue(Packet(KeyboardInputPacketKind.LeaseEnded, 0, 0, 0, 0, false, false, 0, reason: reason));
    }
    private KeyboardInputPacket Packet(KeyboardInputPacketKind kind, uint key, uint scan, uint eventFlags, uint time,
        bool up, bool repeat, nint foreground, bool balance = false, KeyboardLeaseEndReason? reason = null, uint message = 0, OverlayPoint? pointerPosition = null, bool? pointerLeftDown = null, bool stateOnly = false) =>
        new(++sequence, lease, kind, key, scan, eventFlags, time, up, repeat, foreground, local, balance, reason, message) {PointerPosition=pointerPosition,PointerLeftDown=pointerLeftDown,IsStateOnly=stateOnly};
    public void ExpireTails(long now)
    {
        for (int i = 1; i < 255; i++) if (tails[i] != 0 && now >= tails[i]) { tails[i] = 0; physical[i] = false; }
    }
    public bool NeedsNotification => queue.Count != 0 && inFlight == 0 && !notificationPending;
    public void MarkNotificationPosted() => notificationPending = true;
    public bool TryDequeue(out KeyboardInputPacket? packet)
    {
        notificationPending = false;
        if (inFlight != 0 || !queue.TryDequeue(out packet)) { packet = null; return false; }
        inFlight = packet.Sequence; inFlightPacket = packet;
        if (packet.Kind == KeyboardInputPacketKind.Key && !packet.IsStateOnly)
            claimedDown[Canonical((int)packet.VirtualKey, packet.ScanCode, packet.Flags)] = !packet.IsKeyUp;
        return true;
    }
    public bool Complete(long packetSequence)
    {
        if (inFlight != packetSequence || packetSequence == 0) return false;
        inFlight = 0; inFlightPacket = null; return true;
    }
}
