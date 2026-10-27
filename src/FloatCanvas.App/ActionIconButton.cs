using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FloatCanvas.App;

internal enum PanelActionIcon { Apply, Add, Save, Delete, Rename, Pin, Unpin, Eyedropper, Transparent, Refresh, Close, Use, Source, Previous, Next }

/// <summary>Compact panel actions share the caption's 16-DIP, rounded-line icon grid.</summary>
internal static class ActionIconButton
{
    internal static void UpdatePin(Button button,bool pinned)
    {
        button.Width=button.Height=28;button.MinWidth=button.MinHeight=0;
        button.Padding=new Thickness(0);button.Margin=new Thickness(3,0,3,0);
        button.SetResourceReference(FrameworkElement.StyleProperty,"CanvasCaptionButton");
        UpdateToggleAppearance(button,pinned);
        button.RenderTransformOrigin=new Point(.5,.5);
        button.RenderTransform=new ScaleTransform(pinned?1.08:1,pinned?1.08:1);
        if(button.Content is FrameworkElement glyph)
        {glyph.RenderTransformOrigin=new Point(.5,.5);glyph.RenderTransform=new RotateTransform(pinned?-35:0);}
        string label=FloatCanvas.Localization.AppText.Get(pinned?"ToolbarV2.UnpinOptions":"ToolbarV2.PinOptions");
        button.ToolTip=label;AutomationProperties.SetName(button,label);
    }
    internal static void UpdateToggleAppearance(Button button,bool active)
    {
        button.SetResourceReference(Control.BackgroundProperty,active?"CanvasHiddenSurface":"CanvasButton");
        button.SetResourceReference(Control.ForegroundProperty,active?"CanvasHiddenText":"CanvasText");
        if(active)button.SetResourceReference(Control.BorderBrushProperty,"CanvasHiddenBorder");
        else button.BorderBrush=Brushes.Transparent;
        button.BorderThickness=new Thickness(active?1:0);
        SetGlyphColor(button.Content,active?"CanvasHiddenText":"CanvasText");
    }
    private static void SetGlyphColor(object? content,string resource)
    {
        switch(content)
        {
            case Shape shape: shape.SetResourceReference(Shape.StrokeProperty,resource);break;
            case Viewbox view: SetGlyphColor(view.Child,resource);break;
            case Panel panel: foreach(var child in panel.Children)SetGlyphColor(child,resource);break;
            case ContentPresenter presenter: SetGlyphColor(presenter.Content,resource);break;
            case ContentControl control: SetGlyphColor(control.Content,resource);break;
        }
    }
    internal static Button Create(PanelActionIcon icon, string label, Action action, string? shortcut = null)
    {
        var button = new Button { Content = Glyph(icon), Width = 30, Height = 30, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(6), Margin = new Thickness(2), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = string.IsNullOrWhiteSpace(shortcut) ? label : label + " (" + shortcut + ")" };
        button.SetResourceReference(FrameworkElement.StyleProperty, "CanvasCaptionButton");
        AutomationProperties.SetName(button, label);
        if (shortcut is not null) AutomationProperties.SetAcceleratorKey(button, shortcut);
        ToolTipService.SetInitialShowDelay(button, 650);
        button.Click += (_, _) => action();
        return button;
    }

    internal static FrameworkElement Glyph(PanelActionIcon icon, double size = 16)
    {
        string data = icon switch
        {
            PanelActionIcon.Apply => "M3 8 L6.5 11.5 L13 4.5",
            PanelActionIcon.Add => "M8 2.5 V13.5 M2.5 8 H13.5",
            PanelActionIcon.Save => "M3 2.5 H11 L13.5 5 V13.5 H2.5 V2.5 Z M5 2.5 V6 H10 V2.5 M5 13.5 V9 H11 V13.5",
            PanelActionIcon.Delete => "M2.5 4.5 H13.5 M6 4.5 V2.5 H10 V4.5 M4 4.5 L4.7 13.5 H11.3 L12 4.5 M6.5 7 V11 M9.5 7 V11",
            PanelActionIcon.Rename => "M3 10 L10.5 2.5 L13.5 5.5 L6 13 H3 Z M9 4 L12 7",
            PanelActionIcon.Pin => "M5 2.5 H11 M6 2.5 V6.5 L3.5 9 V10 H12.5 V9 L10 6.5 V2.5 M8 10 V14",
            PanelActionIcon.Unpin => "M5 2.5 H11 M6 2.5 V6.5 L3.5 9 V10 H12.5 V9 L10 6.5 V2.5 M8 10 V14 M2 2 L14 14",
            PanelActionIcon.Eyedropper => "M6 5.5 L10.5 10 M8 5.5 L11.5 2 L14 4.5 L10.5 8 M7 6.5 L3 10.5 L2.5 13.5 L5.5 13 L9.5 9",
            PanelActionIcon.Transparent => "M8 2 C6 5 3.5 7.5 3.5 10 A4.5 4.5 0 0 0 12.5 10 C12.5 7.5 10 5 8 2 Z M2 2 L14 14",
            PanelActionIcon.Refresh => "M12.5 5 A5 5 0 1 0 13 10 M9 5 H13 V1.5",
            PanelActionIcon.Close => "M4 4 L12 12 M12 4 L4 12",
            PanelActionIcon.Use => "M5 3 L12 8 L5 13 Z",
            PanelActionIcon.Source => "M6 3 H2.5 V6.5 M10 3 H13.5 V6.5 M2.5 10 V13.5 H6 M10 13.5 H13.5 V10 M6 6 H10 V10 H6 Z",
            PanelActionIcon.Previous => "M10.5 3 L5.5 8 L10.5 13",
            PanelActionIcon.Next => "M5.5 3 L10.5 8 L5.5 13",
            _ => throw new ArgumentOutOfRangeException(nameof(icon))
        };
        var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        path.SetResourceReference(Shape.StrokeProperty, "CanvasText");
        var canvas = new Canvas { Width = 16, Height = 16, IsHitTestVisible = false }; canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas, IsHitTestVisible = false };
    }
}
