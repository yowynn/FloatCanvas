using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class NumberFormatPicker : OverlayComboBox
{
    private static readonly DataTemplate captionTemplate = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <TextBlock Text="{Binding Caption}" TextTrimming="CharacterEllipsis" ToolTip="{Binding FullCaption}"/>
        </DataTemplate>
        """);
    private readonly Choice custom;
    private ImmutableArray<string> shownItems = [];

    internal NumberFormatPicker()
    {
        MinHeight = PropertyPanelLayout.ControlHeight;
        SelectedValuePath = nameof(Choice.Format);
        ItemTemplate = captionTemplate;
        TextSearch.SetTextPath(this, nameof(Choice.Caption));
        SetBinding(ToolTipProperty, new Binding("SelectedItem.FullCaption") { Source = this });
        AutomationProperties.SetName(this, L.Get("Numbering.Format"));
        custom = new Choice(NumberFormat.Custom);
        foreach (var format in Enum.GetValues<NumberFormat>())
            Items.Add(format == NumberFormat.Custom ? custom : new Choice(format));
    }

    internal void SetCustomExamples(ImmutableArray<string> items)
    {
        if (items == shownItems) return;
        shownItems = items;
        string name = L.Get("Numbering.Format.Custom");
        if (items.IsDefaultOrEmpty) { custom.SetCaption(name, null); return; }
        var examples = items.Take(3).ToArray();
        string separator = L.Get("Numbering.EntrySeparator");
        string full = L.Format("Numbering.CustomExamples", name, string.Join(separator, examples));
        var boundaries = examples.Select(StringInfo.ParseCombiningCharacters).ToArray();
        int perItem = 18;
        while (boundaries.Sum(part => Math.Min(part.Length, perItem)) > 18) perItem--;
        string shortCaption = L.Format("Numbering.CustomExamples", name, string.Join(separator, examples.Select((text, i) =>
            boundaries[i].Length > perItem ? text[..boundaries[i][perItem]] + "…" : text)));
        custom.SetCaption(shortCaption, full);
    }

    private sealed class Choice(NumberFormat format) : INotifyPropertyChanged
    {
        public NumberFormat Format { get; } = format;
        public string Caption { get; private set; } = L.Get("Numbering.Format." + format);
        public string? FullCaption { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetCaption(string caption, string? fullCaption)
        {
            if (Caption == caption && FullCaption == fullCaption) return;
            Caption = caption; FullCaption = fullCaption;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        public override string ToString() => Caption;
    }
}
