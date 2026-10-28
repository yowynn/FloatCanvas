using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Documents;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class SegmentedChoicePreviews
{
    internal static FrameworkElement Icon(string name)
    {
        string path = name switch
        {
            "flat" or "surface" => "M2,4 L14,4 L14,14 L2,14 Z M2,9 H14 M8,4 V14",
            "perspective" => "M5,2 L14,5 L11,14 L2,11 Z M3.5,6.5 L12.5,9.5 M9.5,3.5 L6.5,12.5",
            "billboard" => "M3,1 H13 V12 H3 Z M8,12 V15 M5,15 H11",
            "system" or "desktop" => "M1,2 H15 V12 H1 Z M8,12 V15 M4,15 H12",
            "light" => "M8,4 A4,4 0 1 0 8,12 A4,4 0 1 0 8,4 M8,0 V2 M8,14 V16 M0,8 H2 M14,8 H16 M2,2 L3,3 M13,13 L14,14 M2,14 L3,13 M13,3 L14,2",
            "dark" => "M12,2 A6.5,6.5 0 1 0 14,12 A7,7 0 0 1 12,2 Z",
            "auto" => "M8,1 L10,6 L15,8 L10,10 L8,15 L6,10 L1,8 L6,6 Z",
            "enhanced" => "M8,1 L15,4 L8,7 L1,4 Z M1,8 L8,11 L15,8 M1,12 L8,15 L15,12",
            "background" => "M1,2 H15 V14 H1 Z M2,11 L6,7 L10,11 L13,8 L15,10",
            "canvas" => "M2,2 H14 V14 H2 Z M5,11 L11,5 M6,4 H12 V10",
            "combined" => "M1,5 H11 V15 H1 Z M5,1 H15 V11 H11 M8,8 L12,4",
            "language" => "M1,3 H9 M5,1 V3 M3,4 C3,7 6,10 9,11 M8,4 C8,7 5,10 1,11 M9,15 L12,7 L15,15 M10,12 H14",
            _ => "M2,2 H14 V14 H2 Z"
        };
        var shape = new System.Windows.Shapes.Path { Data = Geometry.Parse(path), StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText");
        return new Viewbox { Width = 18, Height = 18, Child = new Canvas { Width = 16, Height = 16, Children = { shape } } };
    }

    internal static FrameworkElement Icon(object value)
    {
        if (value is CaptureShape capture) return (FrameworkElement)ToolParameterIcon.Create(new ToolParameters { Tool = EditorTool.CaptureRegion, CaptureShape = capture }, 18);
        if (value is CaptureSource source) return Icon(source switch { CaptureSource.Background => "background", CaptureSource.CanvasOnly => "canvas", _ => "combined" });
        var drawing = new DrawingGroup();
        var image = new Image { Width = 18, Height = 18, Stretch = Stretch.Uniform };
        image.SetResourceReference(TextElement.ForegroundProperty, "CanvasText");
        var brush = Application.Current?.TryFindResource("CanvasText") as Brush ?? SystemColors.ControlTextBrush;
        var pen = new Pen(brush, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        BindingOperations.SetBinding(pen, Pen.BrushProperty, new Binding { Source = image, Path = new PropertyPath(TextElement.ForegroundProperty) });
        if (value is FloatCanvas.Core.TextAlignment alignment)
        {
            for (int index = 0; index < 3; index++)
            {
                double width = index == 1 ? 10 : 16;
                double left = alignment == FloatCanvas.Core.TextAlignment.Right ? 18 - width : alignment == FloatCanvas.Core.TextAlignment.Center ? (18 - width) / 2 : 0;
                drawing.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new(left, 3 + index * 5), new(left + width, 3 + index * 5))));
            }
        }
        else
        {
            if (value is StrokePattern pattern) pen.DashStyle = pattern switch { StrokePattern.Dashed => new DashStyle([3d, 2d], 0), StrokePattern.Dotted => new DashStyle([.5, 2d], 0), _ => DashStyles.Solid };
            drawing.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M0,8 H18")));
            if (value is ArrowHeadStyle arrow && arrow != ArrowHeadStyle.None)
            {
                var head = new GeometryDrawing(null, pen, Geometry.Parse(arrow switch
                { ArrowHeadStyle.Open => "M12,3 L18,8 L12,13", ArrowHeadStyle.Diamond => "M8,8 L13,3 L18,8 L13,13 Z", _ => "M11,3 L18,8 L11,13 Z" }));
                if (arrow != ArrowHeadStyle.Open) BindingOperations.SetBinding(head, GeometryDrawing.BrushProperty, new Binding { Source = image, Path = new PropertyPath(TextElement.ForegroundProperty) });
                drawing.Children.Add(head);
            }
        }
        image.Source = new DrawingImage(drawing); return image;
    }

    internal static void Add(SegmentedChoice choice, string label, object value)
    {
        string? key = value switch
        {
            ArrowHeadStyle.FilledTriangle => "Choice.Filled", ArrowHeadStyle.Open => "Choice.Open", ArrowHeadStyle.Diamond => "Choice.Diamond", ArrowHeadStyle.None => "Choice.None",
            CaptureShape.Rectangle => "Choice.Rectangle", CaptureShape.Ellipse => "Snapshot.Shape.Ellipse", CaptureShape.FreehandLasso => "Choice.Lasso", CaptureShape.PolygonLasso => "Choice.Polygon",
            CaptureSource.Background => "Choice.Background", CaptureSource.BackgroundAndCanvas => "Choice.Combined", CaptureSource.CanvasOnly => "Choice.Canvas",
            FloatCanvas.Core.TextAlignment.Left => "Choice.Left", FloatCanvas.Core.TextAlignment.Center => "Choice.Center", FloatCanvas.Core.TextAlignment.Right => "Choice.Right",
            _ => null
        };
        choice.Add(key is null ? label : L.Get(key), value, Icon(value));
        choice.Items[^1].ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetHelpText(choice.Items[^1], label);
    }

    internal static SegmentedChoice CanvasMode()
    {
        var choice = new SegmentedChoice();
        choice.Add(L.Get("Canvas.Flat"), false, Icon("flat")); choice.Add(L.Get("Canvas.Perspective"), true, Icon("perspective"));
        return choice;
    }

    internal static SegmentedChoice ColorStyle()
    {
        var choice = new SegmentedChoice();
        choice.Add(L.Get("Color.StyleSeparate"), false, Icon("flat")); choice.Add(L.Get("Color.StyleUnified"), true, Icon("flat"));
        return choice;
    }

    internal static void UpdateColorStyle(SegmentedChoice choice, EditorTool tool, SymbolKind symbol = default)
    {
        for (int index = 0; index < 2; index++)
        {
            var sample = new ToolParameters { Tool = tool, Symbol = symbol, DefaultWidth = 120, DefaultHeight = 90, CornerRadius = tool == EditorTool.RoundedRectangle ? 18 : 0 };
            sample.Style.Stroke = 0xFF4285D4; sample.Style.Fill = 0x554285D4; sample.Style.StrokeWidth = 7; sample.Style.LinkColors = index == 1;
            choice.SetIcon(index, (FrameworkElement)ToolParameterIcon.Create(sample, 18));
        }
    }

    internal static void UpdateColorStyle(SegmentedChoice choice, ElementKind kind, SymbolKind symbol) => UpdateColorStyle(choice,
        kind switch { ElementKind.RoundedRectangle => EditorTool.RoundedRectangle, ElementKind.Ellipse => EditorTool.Ellipse, ElementKind.Triangle => EditorTool.Triangle, ElementKind.Symbol => EditorTool.Symbol, _ => EditorTool.Rectangle }, symbol);

    internal static SegmentedChoice Appearance(string selected)
    {
        var choice = new SegmentedChoice();
        choice.Add(L.Get("Choice.System"), "system", Icon("system"));
        choice.Add(L.Get("Settings.LightTheme"), "light", Icon("light"));
        choice.Add(L.Get("Settings.DarkTheme"), "dark", Icon("dark"));
        choice.SelectedValue = selected is "light" or "dark" ? selected : "system";
        return choice;
    }

    internal static SegmentedChoice Language(string selected)
    {
        var choice = new SegmentedChoice();
        choice.Add(L.Get("Choice.Auto"), "auto", Icon("system"));
        choice.Add(L.Get("Choice.Chinese"), "zh-CN", new TextBlock { Text = "中", TextAlignment = System.Windows.TextAlignment.Center });
        choice.Add(L.Get("Settings.EnglishLanguage"), "en", new TextBlock { Text = "EN", TextAlignment = System.Windows.TextAlignment.Center });
        choice.SelectedValue = L.NormalizePreference(selected);
        return choice;
    }

    internal static SegmentedChoice OverlayMode(OverlayBackendMode selected)
    {
        var choice = new SegmentedChoice();
        choice.Add(L.Get("Choice.Auto"), 0, Icon("auto"));
        choice.Add(L.Get("OverlaySafety.ModeEnhanced"), 1, Icon("enhanced"));
        choice.Add(L.Get("OverlaySafety.ModeDesktop"), 2, Icon("desktop"));
        choice.SelectedIndex = (int)selected;
        return choice;
    }
}
