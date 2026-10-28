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
        => ShowCore(owner, message, title, buttons, image,
            () => Create(owner, message, title, buttons, image, yesKey, noKey), MessageBoxNative);

    internal static MessageBoxResult ShowCore(Window? owner, string message, string title,
        MessageBoxButton buttons, MessageBoxImage image, Func<(Window Window, Func<MessageBoxResult> Result)> create,
        Func<nint, string, string, uint, int> showNative)
    {
        (Window Window, Func<MessageBoxResult> Result) dialog;
        try { dialog = create(); }
        catch (Exception ex)
        {
            // Construction can fail while displaying an earlier startup failure.
            // The primary process already logged that original exception. Keep its
            // message visible without depending on WPF resources or custom chrome.
            System.Diagnostics.Debug.WriteLine(ex);
            bool editing = SessionWindowCoordinator.IsEditingWindow(owner);
            nint handle = owner is null || (!editing && !OverlayWindowScope.IsRegularWindow(owner)) ? 0 : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            return SessionWindowCoordinator.RunNativeModal(owner, () =>
                (MessageBoxResult)showNative(handle, message, WindowTitles.Format(title, !editing), NativeFallbackFlags(handle, buttons, image, editing)));
        }
        SessionWindowCoordinator.ShowModal(dialog.Window);
        return dialog.Result();
    }

    internal static (Window Window, Func<MessageBoxResult> Result) Create(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, string? yesKey = null, string? noKey = null)
    {
        if (owner is not null) new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var panel = new DockPanel { Margin = WindowLayout.ContentMargin(20, 20), LastChildFill = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(row, Dock.Bottom); panel.Children.Add(row);
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        panel.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 });
        var window = new Window { Title = title, Content = panel, Owner = owner, Topmost = true, ShowInTaskbar = false,
            Icon = AppIcon.WindowImage, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        if (!SessionWindowCoordinator.IsEditingWindow(owner)) OverlayWindowScope.UseRegularWindow(window);
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
            button.Click += (_, _) => { result = value; SessionWindowCoordinator.SetModalResult(window, true); };
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

    internal static uint NativeFallbackFlags(nint owner, MessageBoxButton buttons, MessageBoxImage image, bool editing = false) =>
        (uint)buttons | (uint)image | (editing ? 0x00040000U : 0U) | (owner == 0 ? 0x00002000U : 0U);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxNative(nint owner, string message, string title, uint flags);
}
