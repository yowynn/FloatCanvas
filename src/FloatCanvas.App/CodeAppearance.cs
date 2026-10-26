using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>The existing welcome-page keycap appearance, shared by shortcuts and code.</summary>
internal static class CodeAppearance
{
    internal static readonly FontFamily Font = new("Cascadia Mono, Consolas");
    internal const double FontSize = 13;

    internal static TextBlock Text(string value = "")
    {
        var text = new TextBlock { Text = value, FontFamily = Font, FontSize = FontSize, VerticalAlignment = VerticalAlignment.Center, LineHeight = double.NaN };
        text.SetResourceReference(TextBlock.ForegroundProperty, "CanvasCodeText"); return text;
    }

    internal static Border Frame(UIElement content)
    {
        var frame = new Border { Child = content, Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        frame.SetResourceReference(Border.BackgroundProperty, "CanvasCodeSurface"); return frame;
    }

    internal static Border Selectable(string value, bool multiline = false)
    {
        if (!multiline) return Frame(Text(value));
        var text = new TextBox { Text = value, Style = null, FontFamily = Font, FontSize = FontSize, IsReadOnly = true,
            IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), MinHeight = 0, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top };
        TextBlock.SetLineHeight(text, double.NaN);
        text.SetResourceReference(Control.ForegroundProperty, "CanvasCodeText"); return Frame(text);
    }
}
