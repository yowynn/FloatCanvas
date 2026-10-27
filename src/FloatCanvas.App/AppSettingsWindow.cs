using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Settings categories share one scrollable content area and a persistent action footer.</summary>
internal sealed class AppSettingsWindow : Window
{
    private readonly ListBox navigation = new() { BorderThickness = new Thickness(0), Margin = WindowLayout.ContentMargin() };
    private readonly Grid pages = new();
    internal TextBlock Error { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    internal Button SaveButton { get; } = new() { Content = L.Get("Settings.Save"), IsDefault = true, MinWidth = 90, Padding = new Thickness(18, 8, 18, 8) };

    internal AppSettingsWindow(bool regular = false)
    {
        Title = L.Get("Settings.Title"); Width = 785; Height = 650; FontSize = 14;
        if (regular) OverlayWindowScope.UseRegularWindow(this);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(navigation, ScrollBarVisibility.Auto);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var rail = new Border { Child = navigation, BorderThickness = new Thickness(0, 0, 1, 0) };
        rail.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        body.Children.Add(rail); Grid.SetColumn(pages, 1); body.Children.Add(pages); root.Children.Add(body);
        navigation.SelectionChanged += (_, _) =>
        {
            for (int i = 0; i < pages.Children.Count; i++)
                pages.Children[i].Visibility = i == navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        };
        var actions = new Grid { Margin = WindowLayout.ContentMargin() };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Error.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText");
        AutomationProperties.SetLiveSetting(Error, AutomationLiveSetting.Polite);
        actions.Children.Add(Error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = L.Get("Recovery.Cancel"), IsCancel = true, MinWidth = 90,
            Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => SessionWindowCoordinator.Close(this);
        SaveButton.SetResourceReference(StyleProperty, "CanvasPrimaryButton");
        buttons.Children.Add(cancel); buttons.Children.Add(SaveButton);
        Grid.SetColumn(buttons, 1); actions.Children.Add(buttons);
        var footer = new Border { Child = actions, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        Content = root;
    }

    internal StackPanel AddSection(string title, string hint)
    {
        var content = new StackPanel();
        PropertyPanelLayout.Scope(content, 132);
        content.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var description = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 20) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); content.Children.Add(description);
        pages.Children.Add(new ScrollViewer { Margin = WindowLayout.ContentMargin(20, 20), Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed });
        navigation.Items.Add(new ListBoxItem { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 12, 10, 12) });
        if (navigation.SelectedIndex < 0) navigation.SelectedIndex = 0;
        return content;
    }

    internal void SelectSection(Panel section)
    {
        for (int i = 0; i < pages.Children.Count; i++)
            if (((ScrollViewer)pages.Children[i]).Content == section) navigation.SelectedIndex = i;
    }

    internal static StackPanel AddGroup(Panel section, string title)
    {
        var group = new StackPanel();
        var heading = PropertyPanelLayout.SectionTitle(title); heading.TextWrapping = TextWrapping.Wrap; heading.Margin = new(0, 0, 0, 10); group.Children.Add(heading);
        var card = new Border { Child = group, CornerRadius = new CornerRadius(8), Padding = new Thickness(16),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "CanvasInput");
        card.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        section.Children.Add(card); return group;
    }

    internal static void AddSetting(Panel section, string label, FrameworkElement control, string? hint = null)
    {
        var group = AddGroup(section, label);
        bool inline = control is NumericValueField or ComboBox or TextBox or SegmentedChoice;
        if (control is CheckBox || inline) group.Children.RemoveAt(0);
        var caption = new PropertyLabel(label == L.Get("Settings.Language") ? L.Get("Property.Language") : label) { ToolTip = label };
        AutomationProperties.SetName(control, label);
        if (control is NumericValueField numeric)
        {
            AutomationProperties.SetName(numeric.Input, label); AutomationProperties.SetName(numeric.Slider, label);
            numeric.SetLabel(caption);
        }
        group.Children.Add(control is CheckBox checkbox ? PropertyPanelLayout.BooleanRow(label, checkbox) : inline ? PropertyPanelLayout.Row(caption, control) : control);
        if (string.IsNullOrWhiteSpace(hint)) return;
        var note = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); group.Children.Add(note);
        AutomationProperties.SetHelpText(control, hint);
    }
}
