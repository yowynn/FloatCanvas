using FloatCanvas.Core;
using System.Windows;

namespace FloatCanvas.App;

/// <summary>Stable type artwork for the directory; no asset decoding or live preview is required.</summary>
internal static class LayerTypeIcon
{
    internal static FrameworkElement Create(ElementKind? kind, bool group = false, bool instance = false, bool linked = false, bool windowSource = false)
        => LayersWindow.IconPath(Geometry(kind, group, instance, linked, windowSource));

    internal static string Geometry(ElementKind? kind, bool group = false, bool instance = false, bool linked = false, bool windowSource = false)
    {
        if (instance) return linked ? LayersWindow.LinkedInstance : LayersWindow.Instance;
        if (group) return LayersWindow.Folder;
        if (windowSource) return "M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,5.5 H14.5 M4,4 H4.1 M6,4 H6.1 M6,8 L10,10 L6,12 Z";
        return kind switch
        {
            ElementKind.Freehand => "M2,12 C3,5 5,3 7,5 C9,7 4,11 7,12 C10,14 11,5 14,4",
            ElementKind.Highlighter => "M5,2 L12,6 L9,12 L3,9 Z M3,9 L2,12 L6,14 L9,12 M2,14 H13",
            ElementKind.Line => "M3,13 L13,3",
            ElementKind.Arrow => "M3,13 L13,3 M7,3 H13 V9",
            ElementKind.DoubleArrow => "M2,13 L14,2 M2,7 V13 H8 M8,2 H14 V8",
            ElementKind.Rectangle => "M2,3 H14 V13 H2 Z",
            ElementKind.RoundedRectangle => "M5,3 H11 Q14,3 14,6 V10 Q14,13 11,13 H5 Q2,13 2,10 V6 Q2,3 5,3 Z",
            ElementKind.Ellipse => "M14,8 A6,5 0 1 1 2,8 A6,5 0 1 1 14,8 Z",
            ElementKind.Triangle => "M8,2 L14,14 H2 Z",
            ElementKind.Text => "M2,3 H14 M8,3 V14 M5,14 H11",
            ElementKind.Number => "M14.5,8 A6.5,6.5 0 1 1 1.5,8 A6.5,6.5 0 1 1 14.5,8 Z M6,4 L5,12 M10,4 L9,12 M4,6.5 H12 M3.5,9.5 H11.5",
            ElementKind.Image => "M1.5,2.5 H14.5 V13.5 H1.5 Z M3,11 L6.5,7.5 L9,10 L11,8 L13,10 M10,5 A1,1 0 1 1 12,5 A1,1 0 1 1 10,5",
            ElementKind.Symbol => "M8,1.7 L9.8,5.8 L14.3,6.4 L11,9.6 L11.8,14.1 L8,12 L4.2,14.1 L5,9.6 L1.7,6.4 L6.2,5.8 Z",
            ElementKind.LivePixelate => "M2,2 H7 V7 H2 Z M9,2 H14 V7 H9 Z M2,9 H7 V14 H2 Z M9,9 H14 V14 H9 Z",
            ElementKind.LiveBlur => "M2,2 H14 V14 H2 Z M4,5.5 C6,3.5 10,7.5 12,5.5 M4,8 C6,6 10,10 12,8 M4,10.5 C6,8.5 10,12.5 12,10.5",
            ElementKind.Presentation => "M1.5,3 H14.5 V13 H1.5 Z M4,6 H4.5 M7.5,6 H8 M11,6 H11.5 M4,8.5 H4.5 M7.5,8.5 H8 M11,8.5 H11.5 M5,11 H11",
            _ => "M2,2 H14 V14 H2 Z M5,6 H11 M5,9 H11"
        };
    }
}
