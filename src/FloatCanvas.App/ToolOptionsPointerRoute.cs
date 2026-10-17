using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FloatCanvas.App;

/// <summary>Classifies the press before popup commits can detach its original visual.</summary>
internal sealed class ToolOptionsPointerRoute : IDisposable
{
    private readonly InputManager input;
    private readonly Func<bool> enabled;
    private readonly Func<DependencyObject?, bool> owns;
    private readonly Window[] surfaces;
    private readonly Action close;
    private readonly object originKey = new();

    internal ToolOptionsPointerRoute(Func<bool> enabled, Func<DependencyObject?, bool> owns, Action close, params Window[] surfaces)
    {
        this.enabled = enabled; this.owns = owns; this.close = close; this.surfaces = surfaces;
        input = InputManager.Current;
        input.PreProcessInput += Remember;
        input.PostProcessInput += Complete;
    }

    private void Remember(object sender, PreProcessInputEventArgs e)
    {
        if (!enabled() || e.StagingItem.Input is not MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed } mouse) return;
        PressOrigin origin;
        // WPF copies staging data when it promotes a preview press to bubbling events.
        // Preserve that exact input lineage even if promotion uses another MouseDevice,
        // without reusing a previous independent press that happens to share a timestamp.
        if (mouse.RoutedEvent != Mouse.PreviewMouseDownEvent && e.StagingItem.GetData(originKey) is PressOrigin previous
            && previous.Timestamp == mouse.Timestamp && previous.Button == mouse.ChangedButton)
            origin = previous;
        else origin = new(mouse.Timestamp, mouse.ChangedButton, IsInside(mouse));
        e.StagingItem.SetData(originKey, origin);
    }

    private void Complete(object sender, ProcessInputEventArgs e)
    {
        if (!enabled() || e.StagingItem.Input is not MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed } mouse) return;
        bool inside = e.StagingItem.GetData(originKey) is PressOrigin origin ? origin.Inside : IsInside(mouse);
        if (!inside) close();
    }

    private bool IsInside(MouseButtonEventArgs mouse)
    {
        var source = mouse.OriginalSource as DependencyObject ?? mouse.MouseDevice.Target as DependencyObject;
        if (owns(source)) return true;
        // A captured Popup can target its PopupRoot for an outside press. Check the
        // actual surface at that press's screen position, never the active window.
        // A disconnected synthetic source has no meaningful screen position.
        if (source is null || PresentationSource.FromDependencyObject(source) is not HwndSource) return false;
        foreach (var window in surfaces)
        {
            if (!window.IsVisible || !window.IsEnabled) continue;
            var position = mouse.GetPosition(window);
            if (position.X < 0 || position.Y < 0 || position.X >= window.ActualWidth || position.Y >= window.ActualHeight) continue;
            var screen = window.PointToScreen(position);
            nint handle = new WindowInteropHelper(window).Handle;
            nint physical = WindowFromPhysicalPoint(new((int)Math.Round(screen.X), (int)Math.Round(screen.Y)));
            if (handle != 0 && GetAncestor(physical, 2) == handle) return true;
        }
        return false;
    }

    public void Dispose()
    {
        input.PreProcessInput -= Remember;
        input.PostProcessInput -= Complete;
    }

    private sealed record PressOrigin(int Timestamp, MouseButton Button, bool Inside);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
}
