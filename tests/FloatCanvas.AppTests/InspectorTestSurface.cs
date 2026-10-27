using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using FloatCanvas.App;

internal sealed class InspectorTestSurface : IDisposable
{
    private readonly InspectorWindow window;
    private readonly nint handle;
    internal InspectorTestSurface(InspectorWindow window)
    {
        this.window = window;
        window.Left = window.Top = -20000; window.ShowActivated = false; window.ShowInTaskbar = false; window.Opacity = 0;
        window.PrepareForShow(); window.Show();
        handle = new WindowInteropHelper(window).Handle;
        Pump();
        window.Left = window.Top = -20000;
        ((Button)typeof(InspectorWindow).GetField("pin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    public void Dispose() { window.Hide(); HwndSource.FromHwnd(handle)?.Dispose(); Pump(); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
}
