using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A local draft of the one custom mapping in the number tool. Only OK publishes it.</summary>
internal sealed class NumberSequenceEditorWindow : Window
{
    private readonly NumberSequenceSettings original;
    private readonly TextBox items = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new(8, 6, 8, 6),
        VerticalContentAlignment = VerticalAlignment.Top
    };
    private readonly NumericValueField first;
    private readonly SegmentedChoice overflow = new();
    private readonly Button split = ActionButton("Numbering.Split"), accept = ActionButton("Dialog.OK");
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new(0, 6, 0, 0) };
    private ImmutableArray<string> draftItems;
    internal NumberSequenceSettings? Result { get; private set; }

    internal NumberSequenceEditorWindow(NumberSequenceSettings original, Window? owner)
    {
        this.original = original;
        Owner = owner; Title = L.Get("Numbering.EditorTitle"); Icon = AppIcon.WindowImage;
        Width = PropertyPanelLayout.WindowWidth; Height = 430; MinWidth = 340; MinHeight = 340;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Topmost = true;
        PropertyPanelLayout.Scope(this);
        var body = new DockPanel { Margin = WindowLayout.ContentMargin() };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var cancel = ActionButton("Dialog.Cancel"); cancel.IsCancel = true; cancel.MinWidth = accept.MinWidth = 78;
        AppInputPolicy.AttachFormSubmit(this, accept);
        accept.Margin = new(8, 0, 0, 0);
        footer.Children.Add(cancel); footer.Children.Add(accept); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);

        var settings = new StackPanel { Margin = new(0, 6, 0, 0) };
        DockPanel.SetDock(settings, Dock.Bottom); body.Children.Add(settings);
        var domain = NumericParameterDomain.ForTool("SequenceFirst");
        var input = new TextBox();
        string firstLabel = L.Get("Numbering.First");
        AutomationProperties.SetName(input, firstLabel);
        first = new NumericValueField(input, domain.Minimum, domain.Maximum, domain.RailMinimum, domain.RailMaximum, domain.Step, integerOnly: true) { Unit = "#" };
        first.SetScrubAnchor(NumericPropertyKind.Number, firstLabel);
        var caption = new PropertyLabel(firstLabel); first.SetLabel(caption);
        var firstRow = PropertyPanelLayout.Row(caption, first); firstRow.Margin = PropertyPanelLayout.RowMargin; settings.Children.Add(firstRow);
        overflow.Add(L.Get("Numbering.OriginalNumber"), false, new NumericPropertyIcon(NumericPropertyKind.Number));
        overflow.Add(L.Get("Numbering.Cycle"), true, SegmentedChoicePreviews.Icon("repeat"));
        var overflowRow = PropertyPanelLayout.Row(new PropertyLabel(L.Get("Numbering.Overflow")), overflow);
        AutomationProperties.SetName(overflow, L.Get("Numbering.Overflow")); overflowRow.Margin = PropertyPanelLayout.RowMargin; settings.Children.Add(overflowRow);
        error.SetResourceReference(ForegroundProperty, "CanvasErrorText"); settings.Children.Add(error);

        var header = new DockPanel { Margin = new(0, 0, 0, 6) };
        DockPanel.SetDock(split, Dock.Right); header.Children.Add(split);
        header.Children.Add(new PropertyLabel(L.Get("Numbering.Items")));
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        AutomationProperties.SetName(items, L.Get("Numbering.OnePerLine")); items.ToolTip = L.Get("Numbering.OnePerLine");
        body.Children.Add(items); Content = body;
        if (!SessionWindowCoordinator.IsEditingWindow(owner)) OverlayWindowScope.UseRegularWindow(this);
        AppWindowChrome.ApplyDialog(this);

        draftItems = original.Items;
        items.Text = string.Join(Environment.NewLine, draftItems); first.SetModelValue(original.FirstNumber); overflow.SelectedValue = original.Cycle;
        items.TextChanged += (_, _) =>
        {
            draftItems = [.. items.Text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
            Validate();
        };
        first.ValueChanged += _ => Validate(); first.InvalidInput += () => Validate();
        split.Click += (_, _) =>
        {
            if (draftItems.Length != 1) return;
            var parts = StringInfo.GetTextElementEnumerator(draftItems[0]);
            var labels = new List<string>();
            while (parts.MoveNext()) labels.Add(parts.GetTextElement());
            items.Text = string.Join(Environment.NewLine, labels);
        };
        cancel.Click += (_, _) => SessionWindowCoordinator.SetModalResult(this, false);
        accept.Click += (_, _) =>
        {
            if (!Validate() || !first.TryGetValue(out var value)) return;
            bool cycle = overflow.SelectedValue is true;
            Result = value == original.FirstNumber && cycle == original.Cycle && draftItems.SequenceEqual(original.Items)
                ? original : original with { Items = draftItems, FirstNumber = (int)value, Cycle = cycle };
            SessionWindowCoordinator.SetModalResult(this, true);
        };
        Validate();
        Loaded += (_, _) => items.Focus();
    }

    private bool Validate()
    {
        string issue = !draftItems.All(NumberSequence.IsSingleLineLabel) ? L.Get("Numbering.InvalidLabel")
            : !first.TryGetValue(out _) ? L.Get("ToolOptions.InvalidNumber") : "";
        split.IsEnabled = draftItems.Length == 1 && StringInfo.GetNextTextElement(draftItems[0]).Length < draftItems[0].Length;
        accept.IsEnabled = issue.Length == 0;
        error.Text = issue; error.Visibility = accept.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        return accept.IsEnabled;
    }

    private static Button ActionButton(string key)
    {
        string label = L.Get(key);
        var button = new Button { Content = label, MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(10, 3, 10, 3) };
        AutomationProperties.SetName(button, label);
        return button;
    }
}
