using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A shared, owned name dialog for personal presets and color swatches.</summary>
internal static class NamePrompt
{
    internal static string? Show(DependencyObject source, string title, string initial, int maximum = 80, Window? owner = null)
    {
        owner ??= Window.GetWindow(source);
        var input = new TextBox { Text = initial, MaxLength = maximum, MinHeight = 32, Margin = new(0, 10, 0, 14) };
        var body = new StackPanel { Margin = new(18) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 14 }); body.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), MinWidth = 70, IsCancel = true };
        var save = new Button { Content = L.Get("Dialog.OK"), MinWidth = 70, IsDefault = true, Margin = new(8, 0, 0, 0), IsEnabled = !string.IsNullOrWhiteSpace(initial) };
        actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions);
        var window = new Window { Title = title, Owner = owner, Content = body, Width = 340, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Icon = AppIcon.WindowImage,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        AppWindowChrome.ApplyDialog(window);
        input.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        save.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
