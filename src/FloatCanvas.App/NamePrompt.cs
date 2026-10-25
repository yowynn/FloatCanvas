using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A shared, owned name dialog for personal presets and color swatches.</summary>
internal static class NamePrompt
{
    internal static void AttachValidation(TextBox input, Button save, Panel body)
    {
        var error = new TextBlock { Text = "⚠ " + L.Get("Audit.RequiredName"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        error.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        body.Children.Add(error);
        void Validate()
        {
            save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            error.Visibility = save.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetHelpText(input, save.IsEnabled ? "" : error.Text);
        }
        input.TextChanged += (_, _) => Validate(); Validate();
    }

    internal static string? Show(DependencyObject source, string title, string initial, int maximum = 80, Window? owner = null)
    {
        using var popup = PopupChildDialog.RetainFor(source);
        owner ??= Window.GetWindow(source);
        var input = new TextBox { Text = initial, MaxLength = maximum, Height = PropertyPanelLayout.ControlHeight, MinHeight = PropertyPanelLayout.ControlHeight, Margin = new(0, 10, 0, 14) };
        var body = new StackPanel { Margin = WindowLayout.ContentMargin(18, 18) };
        body.Children.Add(PropertyPanelLayout.Label(title)); body.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), MinWidth = 70, IsCancel = true };
        var save = new Button { Content = L.Get("Dialog.OK"), MinWidth = 70, IsDefault = true, Margin = new(8, 0, 0, 0), IsEnabled = !string.IsNullOrWhiteSpace(initial) };
        actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions);
        var window = new Window { Title = title, Owner = owner, Content = body, Width = 340, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Icon = AppIcon.WindowImage,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        AppWindowChrome.ApplyDialog(window);
        AttachValidation(input, save, body);
        save.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return SessionWindowCoordinator.ShowModal(window) == true ? input.Text.Trim() : null;
    }
}
