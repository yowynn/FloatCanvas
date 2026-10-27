using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;

// User edits require a visible picker. Keep its native test host offscreen and nonactivating.
internal sealed class ColorPickerTestSurface : IDisposable
{
    private readonly ColorPickerPanel picker;
    private readonly Decorator? parent;
    private readonly HwndSource source;
    internal ColorPickerTestSurface(ColorPickerPanel picker)
    {
        this.picker = picker;
        parent = picker.Parent as Decorator;
        if (parent is not null) parent.Child = null;
        source = new HwndSource(new HwndSourceParameters("FloatCanvas.ColorPickerDraft")
        {
            Width = 1100, Height = 700, PositionX = -20000, PositionY = -20000,
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x080000A0
        }) { RootVisual = new Border { Opacity = 0, Child = picker } };
        ShowWindow(source.Handle, 4);
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        if (!picker.IsVisible || !picker.IsLoaded) throw new InvalidOperationException("Color draft fixture did not become visible and loaded.");
        picker.PrepareForShow();
    }
    public void Dispose()
    {
        if (source.RootVisual is Border border) border.Child = null;
        source.RootVisual = null;
        source.Dispose();
        if (parent is not null) parent.Child = picker;
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}
