using L = FloatCanvas.Localization.AppText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.Core;

namespace FloatCanvas.App;

internal static class ToolOptionPreviews
{
    internal static ComboBox FontPicker()
    {
        return new FontPickerControl();
    }
    internal static bool IsFontSelectionRefresh(ComboBox picker) => picker is FontPickerControl { IsSelectionRefresh: true };
    internal static string? SelectedFontName(ComboBox picker) => picker.SelectedItem switch
    {
        FontPickerControl.FontChoice font => font.Name,
        ComboBoxItem { Tag: string name } => name,
        _ => null
    };
    internal static void ClearFontSelection(ComboBox picker)
    { if (picker is FontPickerControl fonts) fonts.ClearFontSelection(); else picker.SelectedIndex = -1; }
    internal static void SelectFont(ComboBox picker, string name)
    {
        if (picker is FontPickerControl fonts) { fonts.SelectFont(name); return; }
        var item = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Tag, name));
        bool installed = Fonts.SystemFontFamilies.Any(font => StringComparer.OrdinalIgnoreCase.Equals(font.Source, name));
        if (item is null) { item = new ComboBoxItem { Tag = name }; TextSearch.SetText(item, name); picker.Items.Insert(0, item); }
        using var resolved = SkiaSharp.SKTypeface.FromFamilyName(name) ?? SkiaSharp.SKTypeface.FromFamilyName("Segoe UI");
        string label = installed ? name : L.Format("Audit.MissingFont", name, resolved.FamilyName);
        item.Content = new TextBlock { Text = label, FontFamily = new(installed ? name : "Segoe UI"), TextWrapping = TextWrapping.Wrap };
        item.ToolTip = installed ? null : label + "\n" + L.Get("Audit.ReplaceFont");
        System.Windows.Automation.AutomationProperties.SetHelpText(picker, installed ? "" : label);
        picker.SelectedItem = item;
    }
    internal static FrameworkElement StrokeWidth(NumericValueField field)
    {
        var preview = new Border { Height = 18, Margin = new(0, 2, 0, 0), ClipToBounds = true };
        var bar = new Border { VerticalAlignment = VerticalAlignment.Center, CornerRadius = new(2) };
        bar.SetResourceReference(Border.BackgroundProperty, "CanvasText"); preview.Child = bar;
        void Update() { bar.Height = Math.Clamp(field.Value, 1, 18); preview.ToolTip = field.Value.ToString("0.###") + " px"; }
        field.Input.TextChanged += (_, _) => Update(); field.ValueChanged += _ => Update(); Update(); return preview;
    }
}
