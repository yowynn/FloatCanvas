using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.Core;

namespace FloatCanvas.App;

internal static class ToolOptionPreviews
{
    internal static ComboBox FontPicker()
    {
        var picker = new ComboBox { MinHeight = 32, IsTextSearchEnabled = true, MaxDropDownHeight = 300 };
        VirtualizingPanel.SetIsVirtualizing(picker, true);
        foreach (var font in Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase))
        {
            string name = font.Source;
            var sample = new TextBlock { Text = name, FontFamily = font, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            var item = new ComboBoxItem { Tag = name, Content = sample }; TextSearch.SetText(item, name); picker.Items.Add(item);
        }
        return picker;
    }
    internal static void SelectFont(ComboBox picker, string name)
    {
        var item = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Tag, name));
        // A project may use a font that is currently missing; keep its name visible and editable through replacement.
        if (item is null)
        {
            item = new ComboBoxItem { Tag = name, Content = new TextBlock { Text = name, FontFamily = new(name) } };
            TextSearch.SetText(item, name); picker.Items.Insert(0, item);
        }
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
