namespace FloatCanvas.Windows;

public readonly record struct OverlayPoint(double X, double Y);

public readonly record struct PixelBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool Contains(OverlayPoint point) => point.X >= Left && point.Y >= Top && point.X < Right && point.Y < Bottom;
}

public sealed record MonitorInfo(string DeviceName, PixelBounds Bounds, PixelBounds WorkArea, uint DpiX, uint DpiY, bool IsPrimary);

public enum OverlayMonitorScope { AllScreens, PrimaryScreen, SpecificScreen }

[Flags]
public enum OverlayModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }
public enum OverlayPointerKind { Down, Move, Up, DoubleClick, Wheel, Cancel }
public enum OverlayMouseButton { None, Left, Right, Middle }
/// <summary>Native keydown metadata; modifiers belong to this UI message and repeat is its previous-down bit.</summary>
public sealed record OverlayKeyEvent(int VirtualKey, OverlayModifiers Modifiers, bool IsRepeat);
public sealed record OverlayPointerEvent(OverlayPointerKind Kind, OverlayPoint Position, OverlayMouseButton Button, OverlayModifiers Modifiers, int WheelDelta = 0)
{
    public uint? Time { get; init; }
}

/// <summary>Modifiers use the Win32 MOD_ALT/CONTROL/SHIFT/WIN bit values. MOD_NOREPEAT is applied automatically.</summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public static HotkeyBinding DefaultToggle => new(2, 0xC0);
    public static HotkeyBinding DefaultHide => new(6, 0xC0);
}

public sealed record OverlayWindowDiagnostic(nint DisplayHandle, nint InputHandle, PixelBounds Bounds,
    long DisplayExtendedStyle, long InputExtendedStyle, bool DisplayVisible, bool InputVisible);
