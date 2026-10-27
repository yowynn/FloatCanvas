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
    internal static object Choice(string label, object value)
    {
        if (value is not StrokePattern && value is not ArrowHeadStyle) return label;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var drawing = new DrawingGroup();
        var pen = new Pen(Brushes.Gray, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value is StrokePattern pattern) pen.DashStyle = pattern switch { StrokePattern.Dashed => new DashStyle([4d, 3d], 0), StrokePattern.Dotted => new DashStyle([1d, 3d], 0), _ => DashStyles.Solid };
        drawing.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M2,10 H42")));
        if (value is ArrowHeadStyle arrow && arrow != ArrowHeadStyle.None)
        {
            var geometry = Geometry.Parse(arrow switch { ArrowHeadStyle.Open => "M32,3 L42,10 L32,17", ArrowHeadStyle.Diamond => "M30,10 L36,4 L42,10 L36,16 Z", _ => "M31,3 L42,10 L31,17 Z" });
            drawing.Children.Add(new GeometryDrawing(arrow == ArrowHeadStyle.Open ? null : Brushes.Gray, pen, geometry));
        }
        drawing.Freeze(); row.Children.Add(new Image { Source = new DrawingImage(drawing), Width = 44, Height = 20, Margin = new(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); return row;
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
