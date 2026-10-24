using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;

internal static class Phase2ColorWindowTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        foreach (bool enhanced in new[] { false, true })
        {
            var owner = new Window { Title = "Phase2 color owner", Width = 500, Height = 200, ShowActivated = false, ShowInTaskbar = false };
            using var scope = enhanced ? new OverlayWindowScope(new WindowInteropHelper(owner).EnsureHandle(), () => true) : null;
            var field = new ColorField(ColorRole.Foreground);
            var outside = new Button { Content = "Source action" };
            var body = new StackPanel(); body.Children.Add(field); body.Children.Add(outside);
            owner.Content = body; owner.Show(); Pump(); field.OpenPicker(); Pump();
            try
            {
                var color = Window.GetWindow(field.PickerRoot)!;
                check("color picker uses a real owned WPF window for enhanced=" + enhanced, color is not null && color != owner && color.Owner == owner);
                check("color window provides ordinary caption controls", color!.Template.FindName("TitleBar", color) is FrameworkElement &&
                    color.Template.FindName("Close", color) is Button && !string.IsNullOrEmpty(color.Title));
                using (var retention = PopupChildDialog.RetainFor(field.PickerRoot))
                    check("child-dialog retention keeps the regular color window open", retention is not null && field.IsPickerOpen);
                var picker = (ColorPickerPanel)typeof(ColorField).GetField("picker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(field)!;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                bool sawModal = false;
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current!.Windows.Cast<Window>().FirstOrDefault(value => value.IsVisible && value.Owner == color);
                    if (dialog is null) return;
                    sawModal = !IsWindowEnabled(new WindowInteropHelper(color).Handle) && field.IsPickerOpen;
                    timer.Stop(); dialog.DialogResult = false;
                };
                timer.Start();
                try { typeof(ColorPickerPanel).GetMethod("SaveSwatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null); }
                finally { timer.Stop(); }
                check("the actual naming modal belongs to and retains the color window", sawModal && field.IsPickerOpen && color.IsEnabled);
                bool closedBeforePress = false;
                outside.PreviewMouseDown += (_, _) => closedBeforePress = !field.IsPickerOpen;
                typeof(ColorPopupOwnershipTests).GetMethod("Press", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [outside]);
                check("a source-window press closes the secondary window before the source handles it", closedBeforePress && !field.IsPickerOpen);
                field.OpenPicker(); Pump(); color = Window.GetWindow(field.PickerRoot)!;
                color.Close(); Pump();
                check("closing the caption closes the color-picker session", !field.IsPickerOpen);
                field.OpenPicker(); Pump(); owner.Hide(); Pump();
                check("hiding a source window closes its color child", !field.IsPickerOpen);
            }
            finally { field.ClosePicker(); owner.Close(); }
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint handle);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
