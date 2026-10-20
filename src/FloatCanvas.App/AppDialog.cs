using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class AppDialog
{
    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, string? yesKey = null, string? noKey = null)
    {
        (Window Window, Func<MessageBoxResult> Result) dialog;
        try { dialog = Create(owner, message, title, buttons, image, yesKey, noKey); }
        catch (Exception ex)
        {
            // Construction can fail while displaying an earlier startup failure.
            // The primary process already logged that original exception. Keep its
            // message visible without depending on WPF resources or custom chrome.
            System.Diagnostics.Debug.WriteLine(ex);
            nint handle = owner is null ? 0 : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            return (MessageBoxResult)MessageBoxNative(handle, message, title, NativeFallbackFlags(handle, buttons, image));
        }
        SessionWindowCoordinator.ShowModal(dialog.Window);
        return dialog.Result();
    }

    internal static (Window Window, Func<MessageBoxResult> Result) Create(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, string? yesKey = null, string? noKey = null)
    {
        if (owner is not null) new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var panel = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(row, Dock.Bottom); panel.Children.Add(row);
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        panel.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 });
        var window = new Window { Title = title, Content = panel, Owner = owner, Topmost = true, ShowInTaskbar = false,
            Icon = AppIcon.WindowImage, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        AppTheme.EnsureDialogPalette(window);
        AppWindowChrome.ApplyDialog(window);
        body.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText");
        var result = MessageBoxResult.Cancel;
        void Add(string key, MessageBoxResult value, bool primary = false, bool cancel = false)
        {
            var button = new Button { Content = L.Get(key), MinWidth = 76, Margin = new Thickness(6, 0, 0, 0),
                IsDefault = primary, IsCancel = cancel };
            button.SetResourceReference(Control.BackgroundProperty, "CanvasButton");
            button.SetResourceReference(Control.ForegroundProperty, "CanvasText");
            button.SetResourceReference(Control.BorderBrushProperty, "CanvasBorder");
            button.Click += (_, _) => { result = value; window.DialogResult = true; };
            row.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.YesNoCancel:
                Add(yesKey ?? "Dialog.Yes", MessageBoxResult.Yes, true); Add(noKey ?? "Dialog.No", MessageBoxResult.No);
                Add("Dialog.Cancel", MessageBoxResult.Cancel, cancel: true); break;
            case MessageBoxButton.YesNo:
                Add(yesKey ?? "Dialog.Yes", MessageBoxResult.Yes, true); Add(noKey ?? "Dialog.No", MessageBoxResult.No); break;
            case MessageBoxButton.OKCancel:
                Add("Dialog.OK", MessageBoxResult.OK, true); Add("Dialog.Cancel", MessageBoxResult.Cancel, cancel: true); break;
            default: Add("Dialog.OK", MessageBoxResult.OK, true); break;
        }
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SessionWindowCoordinator.Close(window); e.Handled = true; } };
        return (window, () => result);
    }

    internal static uint NativeFallbackFlags(nint owner, MessageBoxButton buttons, MessageBoxImage image) =>
        (uint)buttons | (uint)image | 0x00040000U | (owner == 0 ? 0x00002000U : 0U); // TOPMOST, TASKMODAL without an owner.

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxNative(nint owner, string message, string title, uint flags);
}
