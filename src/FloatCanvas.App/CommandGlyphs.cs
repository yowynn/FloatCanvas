using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace FloatCanvas.App;

/// <summary>Command artwork shared by menus and their matching toolbar actions. No fallback glyph.</summary>
internal static class CommandGlyphs
{
    internal const string File = "M3,1 H10 L14,5 V15 H3 Z M10,1 V5 H14 M6,8 H11 M6,11 H10";
    internal const string EditObjects = "M1,1 H4 V4 H1 Z M12,1 H15 V4 H12 Z M1,12 H4 V15 H1 Z M12,12 H15 V15 H12 Z M4,2.5 H12 M2.5,4 V12 M13.5,4 V12 M4,13.5 H12 M6,6 H10 V10 H6 Z";
    internal const string DrawingTools = "M8,1 C3,1 1,4 1,8 C1,12 4,15 8,15 H10 C12,15 12,12 10,12 H9 C7,12 8,9 11,9 H13 C17,8 14,1 8,1 Z M5,4 H5.1 M10,4 H10.1 M4,8 H4.1";
    internal const string ImageTools = "M1,1 H12 V10 H1 Z M2,8 L5,5 L8,8 M9,4 H9.1 M8,15 L15,8 M12,8 H15 V11";
    internal const string CanvasTools = "M2,3 H14 V14 H2 Z M2,7 H14 M6,3 V14 M0,1 H4 M0,1 V5 M12,16 H16 V12";
    internal const string Welcome = "M1,7 L8,1 L15,7 M3,6 V15 H13 V6 M6,15 V10 H10 V15";
    internal const string Settings = "M6,1 H10 L10.5,4 L13,3 L15,6 L13,8 L15,10 L13,13 L10.5,12 L10,15 H6 L5.5,12 L3,13 L1,10 L3,8 L1,6 L3,3 L5.5,4 Z M8,5 A3,3 0 1 1 7.99,5";
    internal const string FloatingLogo = "M1,4 L4,7 L8,2 L12,7 L15,4 L13,13 H3 Z M3,15 H13";
    internal const string NewCanvas = "M3,1 L10,1 L14,5 L14,15 L3,15 Z M10,1 L10,5 L14,5 M6,10 L11,10 M8.5,7.5 L8.5,12.5";
    internal const string ClearCanvas = "M13.5,1.5 L8.8,6.2 M7.2,5.8 Q9,5.3 10.2,7.1 Q11.4,9.6 9.4,13.1 Q5.5,15.7 1.5,10 Q5.4,10 7.2,5.8 Z M4.1,12.7 L6.6,10.6 M6.8,14 L8.2,11.5";
    internal const string Open = "M1,4 H6 L8,6 H15 L13,14 H1 Z M1,4 V2 H6 L8,4 H13 V6";
    internal const string Save = "M2,1 L12,1 L15,4 L15,15 L2,15 Z M5,1 L5,6 L10,6 L10,1 M5,15 V10 H12 V15";
    internal const string SaveAs = "M2,1 H12 L15,4 V8 M2,1 V15 H7 M5,1 V6 H10 V1 M8,12 L13,7 L16,10 L11,15 H8 Z";
    internal const string Export = "M2,8 V14 H14 V8 M8,11 V1 M4,5 L8,1 L12,5";
    internal const string ExportTransparent = "M1,8 V15 H15 V8 M1,11 H6 M6,8 V15 M10,8 V15 M10,11 H15 M8,7 V1 M5,4 L8,1 L11,4";
    internal const string History = "M3,4 A6,6 0 1 1 2,10 M1,1 V5 H5 M8,4 V8 L11,10";
    internal const string Undo = "M6,3 L2,7 L6,11 M2,7 L10,7 Q14,7 14,13";
    internal const string Redo = "M10,3 L14,7 L10,11 M14,7 L6,7 Q2,7 2,13";
    internal const string Copy = "M6,2 H14 V12 H6 Z M3,5 H2 V15 H10 V14";
    internal const string Paste = "M5,3 H2 V15 H14 V3 H11 M5,1 H11 V5 H5 Z M5,9 H11 M5,12 H9";
    internal const string Duplicate = "M1,1 H10 V10 H1 Z M6,5 H15 V15 H6 Z M8,10 H13 M10.5,7.5 V12.5";
    internal const string Delete = "M2,4 H14 M6,4 V1 H10 V4 M4,4 L5,15 H11 L12,4 M6.5,7 V12 M9.5,7 V12";
    internal const string Group = "M1,1 H15 V15 H1 Z M4,4 H8 V8 H4 Z M8,8 H12 V12 H8 Z";
    internal const string Ungroup = "M1,1 H6 V6 H1 Z M10,10 H15 V15 H10 Z M10,6 L15,1 M11,1 H15 V5 M1,11 V15 H5 M1,15 L6,10";
    internal const string Image = "M1,2 H15 V14 H1 Z M1,11.5 L5,7.5 L8.5,11 L11,8.5 L15,12.5 M12,5.5 A1,1 0 1 1 10,5.5 A1,1 0 1 1 12,5.5";
    internal const string ReplaceImage = "M1,2 H11 V8 M1,2 V13 H7 M2,10 L5,7 L8,10 M9,11 H16 M13,8 L16,11 L13,14";
    internal const string Canvas = "M3,2 H13 L15,14 H1 Z M6,2 L5,14 M10,2 L11,14 M2,8 H14";
    internal const string CanvasFlat = "M1,2 H15 V14 H1 Z M5,2 V14 M11,2 V14 M1,8 H15";
    private const string PlacementPlane = "M8,1.5 L15,8 L8,14.5 L1,8 Z";
    private const string PlacementFigure = "M6.6,4.05 A1.75,1.75 0 1 1 9.4,4.05 Q11,4.5 11,6 V8 H5 V6 Q5,4.5 6.6,4.05 Z";
    internal static readonly string Surface = SurfacePlacementGlyph();
    // The same plane's rear edges leave real gaps around the shared upright figure.
    internal const string Billboard = "M2.5,6.607 L1,8 L8,14.5 L15,8 L13.5,6.607 " + PlacementFigure;
    internal static string Placement(bool billboard)=>billboard?Billboard:Surface;
    internal static double StrokeThickness(string geometry)=>1.5;

    private static string SurfacePlacementGlyph()
    {
        var figure=Geometry.Parse(PlacementFigure).Clone();
        var bounds=figure.Bounds;double cx=bounds.X+bounds.Width/2,cy=bounds.Y+bounds.Height/2;
        // The square's axes follow the diamond edges, rather than the screen's vertical axis.
        const double x=.6,y=x*6.5/7;
        figure.Transform=new MatrixTransform(new Matrix(x,y,-x,y,8-x*(cx-cy),8-y*(cx+cy)));
        return PlacementPlane+" "+figure.GetFlattenedPathGeometry(.01,ToleranceType.Absolute).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    internal const string Visibility = "M1,8 Q8,-1 15,8 Q8,17 1,8 Z M8,5 A3,3 0 1 1 7.99,5";
    internal const string MoveIntoView = "M1,5 V1 H5 M11,1 H15 V5 M15,11 V15 H11 M5,15 H1 V11 M3,8 H13 M10,5 L13,8 L10,11";
    internal const string Properties = "M1,3 L15,3 M1,8 L15,8 M1,13 L15,13 M5,1 L5,5 M11,6 L11,10 M6,11 L6,15";
    internal const string ToolOptions = "M10,1 L8,3 L9,6 L12,7 L14,5 L15,6 C15,9 12,11 9,10 L4,15 C2,16 0,14 1,12 L6,7 C5,4 6,1 10,1 Z M2.7,12.5 L3.5,13.3";
    internal const string Layers = "M1,5 L8,1 L15,5 L8,9 Z M1,9 L8,13 L15,9 M1,13 L8,17 L15,13";
    internal const string Stamp = "M6,8.5 V7 C6,5.5 4.8,4.7 4.8,3.2 C4.8,.6 11.2,.6 11.2,3.2 C11.2,4.7 10,5.5 10,7 V8.5 M6,8.5 H10 Q12,8.5 12,11 M6,8.5 Q4,8.5 4,11 M1.5,11 H14.5 V13.5 H1.5 Z M3,15.5 H13";
    internal const string NewStamp = "M4.5,8.5 V7 C4.5,5.5 3.5,4.7 3.5,3.2 C3.5,.6 8.5,.6 8.5,3.2 C8.5,4.7 7.5,5.5 7.5,7 V8.5 M4.5,8.5 H7.5 Q9.5,8.5 9.5,11 M4.5,8.5 Q2.5,8.5 2.5,11 M1,11 H11 V13.5 H1 Z M2,15.5 H10 M12.5,2.5 V7.5 M10,5 H15";
    internal const string Updates = "M3,5 A6,6 0 0 1 13,3 L15,5 M15,1 V5 H11 M13,11 A6,6 0 0 1 3,13 L1,11 M1,15 V11 H5";
    internal const string PassThrough = "M2,8 L6,12 L14,3";
    internal const string Exit = "M9,2 L2,2 L2,14 L9,14 M7,8 L16,8 M12,4 L16,8 L12,12";
    internal const string Display = "M1,2 H15 V11 H1 Z M8,11 V15 M5,15 H11";
    internal const string Windows = "M2,1 H11 V11 H2 Z M5,5 H15 V15 H5 M2,5 H11 M11,9 H15";
    internal const string CollapseToolbar = "M7,3 L2,8 L7,13 M14,3 L9,8 L14,13";
    internal const string ExpandToolbar = "M2,3 L7,8 L2,13 M9,3 L14,8 L9,13";

    internal static string Tool(EditorTool tool) => tool switch
    {
        EditorTool.Canvas => Canvas,
        EditorTool.Select => "M3,1 L3,15 L7,11 L10,16 L12,15 L9,10 L15,10 Z",
        EditorTool.Eraser => "M1,10 L8,2 L15,9 L8,16 L6,16 Z M5,6 L12,13 M8,16 L16,16",
        EditorTool.Freehand => "M2,13 L3,9 L11,1 L15,5 L7,13 Z M3,9 L7,13 M10,2 L14,6",
        EditorTool.Highlighter => "M3,11 L10,2 L14,5 L8,14 L3,14 Z M1,16 L12,16",
        EditorTool.Line => "M2,14 L14,2",
        EditorTool.Arrow => "M2,14 L14,2 M7,2 H14 V9",
        EditorTool.DoubleArrow => "M2,14 L14,2 M7,2 H14 V9 M2,7 V14 H9",
        EditorTool.Rectangle => "M3,2 L13,2 Q15,2 15,4 L15,12 Q15,14 13,14 L3,14 Q1,14 1,12 L1,4 Q1,2 3,2 Z",
        EditorTool.RoundedRectangle => "M5,2 H11 Q15,2 15,6 V10 Q15,14 11,14 H5 Q1,14 1,10 V6 Q1,2 5,2 Z",
        EditorTool.Ellipse => "M1,8 C1,0 15,0 15,8 C15,16 1,16 1,8 Z",
        EditorTool.Triangle => "M8,1 L15,15 H1 Z",
        EditorTool.Text => "M2,2 L14,2 M8,2 L8,15 M5,15 L11,15",
        EditorTool.Number => "M8,1 A7,7 0 1 1 7.99,1 M6,6 L8,4 L8,12 M6,12 L10,12",
        EditorTool.Symbol => "M8,1 L10,6 L15,6 L11,10 L13,15 L8,12 L3,15 L5,10 L1,6 L6,6 Z",
        EditorTool.Emoji => "M8,1 A7,7 0 1 1 7.99,1 M5,6 L5.1,6 M11,6 L11.1,6 M4,9 Q8,15 12,9",
        EditorTool.Crop => "M4,1 L4,11 Q4,12 5,12 L15,12 M1,4 L11,4 Q12,4 12,5 L12,15",
        EditorTool.CaptureRegion => "M1,6 L1,2 L5,2 M11,2 L15,2 L15,6 M15,10 L15,14 L11,14 M5,14 L1,14 L1,10 M4,5 L12,5 L12,11 L4,11 Z",
        EditorTool.Blur or EditorTool.Redact => "M1,1 L15,1 L15,15 L1,15 Z M5,5 L5.1,5 M11,5 L11.1,5 M8,8 L8.1,8 M5,11 L5.1,11 M11,11 L11.1,11",
        EditorTool.Pixelate => "M1,1 L15,1 L15,15 L1,15 Z M1,6 L15,6 M1,11 L15,11 M6,1 L6,15 M11,1 L11,15",
        EditorTool.Image => Image,
        EditorTool.Stamp => Stamp,
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };

    internal static FrameworkElement Create(string geometry)
    {
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), StrokeThickness = StrokeThickness(geometry), StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        path.SetResourceReference(Shape.StrokeProperty, "CanvasText");
        var canvas = new Canvas { Width = 16, Height = 16, IsHitTestVisible = false }; canvas.Children.Add(path);
        return new Viewbox { Width = 16, Height = 16, Stretch = Stretch.Uniform, Child = canvas, IsHitTestVisible = false };
    }

    internal static Forms.ToolStripMenuItem TrayItem(string label, string geometry, EventHandler click)
    {
        var color = System.Drawing.SystemColors.MenuText;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), 1.5)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            context.PushTransform(new ScaleTransform(2, 2));
            context.DrawGeometry(null, pen, Geometry.Parse(geometry)); context.Pop();
        }
        var bitmap = new RenderTargetBitmap(36, 36, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var decoded = new System.Drawing.Bitmap(stream);
        var image = new System.Drawing.Bitmap(decoded);
        var item = new Forms.ToolStripMenuItem(label, image, click);
        item.Disposed += (_, _) => image.Dispose();
        return item;
    }
}
