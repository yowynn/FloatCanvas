namespace FloatCanvas.Windows;

public enum KeyboardInputPacketKind { Key, Toggle, Hide, LeaseEnded }
public enum KeyboardLeaseEndReason
{
    Explicit, Toggle, Hide, ForegroundLost, HeartbeatExpired, QueueOverflow,
    NotificationFailed, SystemEscape, EmergencyEscape, HookFailure, Disposed, Replaced
}

/// <summary>A private, ordered input packet. It never represents a global SendInput event.</summary>
public sealed class KeyboardInputPacket
{
    private readonly byte[] keyboardState;
    internal KeyboardInputPacket(long sequence, long leaseId, KeyboardInputPacketKind kind,
        uint virtualKey, uint scanCode, uint flags, uint time, bool isKeyUp, bool isRepeat,
        nint foregroundWindow, byte[] state, bool isLocalBalance = false,
        KeyboardLeaseEndReason? endReason = null, uint nativeMessage = 0)
    {
        Sequence = sequence; LeaseId = leaseId; Kind = kind; VirtualKey = virtualKey; ScanCode = scanCode;
        Flags = flags; Time = time; IsKeyUp = isKeyUp; IsRepeat = isRepeat;
        ForegroundWindow = foregroundWindow; keyboardState = (byte[])state.Clone();
        IsLocalBalance = isLocalBalance; EndReason = endReason; NativeMessage = nativeMessage;
        Modifiers = ((state[0x10] & 0x80) != 0 ? OverlayModifiers.Shift : 0) |
            ((state[0x11] & 0x80) != 0 ? OverlayModifiers.Control : 0) |
            ((state[0x12] & 0x80) != 0 ? OverlayModifiers.Alt : 0) |
            (((state[0x5B] | state[0x5C]) & 0x80) != 0 ? OverlayModifiers.Windows : 0);
    }
    public long Sequence { get; }
    public long LeaseId { get; }
    public KeyboardInputPacketKind Kind { get; }
    public uint VirtualKey { get; }
    public uint ScanCode { get; }
    /// <summary>Original KBDLLHOOKSTRUCT flags, including extended, injected and Alt context flags.</summary>
    public uint Flags { get; }
    public uint Time { get; }
    public uint NativeMessage { get; }
    public bool IsKeyUp { get; }
    public bool IsRepeat { get; }
    public bool IsLocalBalance { get; }
    public nint ForegroundWindow { get; }
    public OverlayModifiers Modifiers { get; }
    /// <summary>Physical pointer position sampled with a Shift transition, before UI dispatch.</summary>
    public OverlayPoint? PointerPosition { get; internal init; }
    public bool? PointerLeftDown { get; internal init; }
    public KeyboardLeaseEndReason? EndReason { get; }
    public ReadOnlySpan<byte> KeyboardState => keyboardState;
    public byte[] CopyKeyboardState() => (byte[])keyboardState.Clone();
}
