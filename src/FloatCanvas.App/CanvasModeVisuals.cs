using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>One canvas in focus, or the complete project containing several canvases.</summary>
internal static class CanvasModeVisuals
{
    internal const string Independent = "M1,5 V1 H5 M11,1 H15 V5 M15,11 V15 H11 M5,15 H1 V11 M4,4 H12 V12 H4 Z";
    internal const string Project = "M1,2 H15 V14 H1 Z M4,5 H9 V9 H4 Z M7,9 V11 H12 V7 H9";

    internal static FrameworkElement Icon(bool independent, double size = 16, string foreground = "CanvasText")
    {
        var icon = CommandGlyphs.Create(independent ? Independent : Project);
        icon.Width = icon.Height = size;
        var path = ((Canvas)((Viewbox)icon).Child).Children.OfType<System.Windows.Shapes.Path>().Single();
        path.StrokeThickness = 1.3;
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, foreground);
        return icon;
    }
}
