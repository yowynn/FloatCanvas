using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;

namespace FloatCanvas.App;

/// <summary>Preserves normal child mouse capture across the hosted dropdown's HWND boundary.</summary>
internal class OverlayComboBox : ComboBox
{
    static OverlayComboBox() => EventManager.RegisterClassHandler(typeof(OverlayComboBox), Mouse.LostMouseCaptureEvent,
        new MouseEventHandler(OnHostedCaptureChanged));

    internal OverlayComboBox() => SetResourceReference(StyleProperty, typeof(ComboBox));

    private static void OnHostedCaptureChanged(object sender, MouseEventArgs args)
    {
        var combo = (OverlayComboBox)sender;
        if (!combo.IsDropDownOpen || combo.Template?.FindName("PART_Popup", combo) is not OverlayPopup { IsOpen: true, Child: { } content }) return;
        // WPF recognizes a native PopupRoot here; the hosted root needs the same
        // inside-child rule without treating a scrollbar's temporary capture as dismissal.
        if (Mouse.Captured is DependencyObject captured && OverlayPopupWindow.BelongsTo(captured, content)) args.Handled = true;
        else if (Mouse.Captured is null && GetCapture() == 0 && args.OriginalSource is DependencyObject origin && OverlayPopupWindow.BelongsTo(origin, content))
        {
            args.Handled = true;
            Mouse.Capture(combo, CaptureMode.SubTree);
        }
    }
    [DllImport("user32.dll")] private static extern nint GetCapture();
}
