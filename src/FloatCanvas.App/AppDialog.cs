using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class AppDialog
{
    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
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
        AppWindowChrome.ApplyDialog(window);
        var result = MessageBoxResult.Cancel;
        void Add(string key, MessageBoxResult value, bool primary = false, bool cancel = false)
        {
            var button = new Button { Content = L.Get(key), MinWidth = 76, Margin = new Thickness(6, 0, 0, 0),
                IsDefault = primary, IsCancel = cancel };
            button.Click += (_, _) => { result = value; window.DialogResult = true; };
            row.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.YesNoCancel:
                Add("Dialog.Yes", MessageBoxResult.Yes, true); Add("Dialog.No", MessageBoxResult.No);
                Add("Dialog.Cancel", MessageBoxResult.Cancel, cancel: true); break;
            case MessageBoxButton.YesNo:
                Add("Dialog.Yes", MessageBoxResult.Yes, true); Add("Dialog.No", MessageBoxResult.No); break;
            case MessageBoxButton.OKCancel:
                Add("Dialog.OK", MessageBoxResult.OK, true); Add("Dialog.Cancel", MessageBoxResult.Cancel, cancel: true); break;
            default: Add("Dialog.OK", MessageBoxResult.OK, true); break;
        }
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SessionWindowCoordinator.Close(window); e.Handled = true; } };
        SessionWindowCoordinator.ShowModal(window);
        return result;
    }
}
