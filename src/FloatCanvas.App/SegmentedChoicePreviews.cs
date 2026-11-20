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
            "perspective" => "M5,4 H11 L14,14 H2 Z M4,7.33 H12 M8,4 V14",
            "billboard" => "M3,1 H13 V12 H3 Z M8,12 V15 M5,15 H11",
            "system" or "desktop" => "M1,2 H15 V12 H1 Z M8,12 V15 M4,15 H12",
            "light" => "M8,4 A4,4 0 1 0 8,12 A4,4 0 1 0 8,4 M8,0 V2 M8,14 V16 M0,8 H2 M14,8 H16 M2,2 L3,3 M13,13 L14,14 M2,14 L3,13 M13,3 L14,2",
            "dark" => "M12,2 A6.5,6.5 0 1 0 14,12 A7,7 0 0 1 12,2 Z",
            "auto" => "M8,1 L10,6 L15,8 L10,10 L8,15 L6,10 L1,8 L6,6 Z",
            "enhanced" => "M8,1 L15,4 L8,7 L1,4 Z M1,8 L8,11 L15,8 M1,12 L8,15 L15,12",
            "background" => "M1,2 H15 V14 H1 Z M2,11 L6,7 L10,11 L13,8 L15,10",
            "canvas" => "M2,2 H14 V14 H2 Z M5,11 L11,5 M6,4 H12 V10",
            "combined" => "M1,5 H11 V15 H1 Z M5,1 H15 V11 H11 M8,8 L12,4",
            "capture-rectangle" => "M2,2 H14 V14 H2 Z",
            "capture-ellipse" => "M8,2 A6,6 0 1 0 8,14 A6,6 0 1 0 8,2 Z",
            "capture-lasso" => "M5,13 C0,12 0,5 4,3 C10,-1 17,5 14,10 C12,14 6,15 5,11 C4,8 9,9 8,12 L6,16",
            "capture-polygon" => "M2,5 L9,1 L15,6 L12,14 L3,12 Z",
            "tip-round" => "M4,5 H12 A3,3 0 0 1 12,11 H4 A3,3 0 0 1 4,5 Z",
            "tip-flat" => "M2,5 H14 V11 H2 Z",
            "line-straight" => "M2,12 L14,4",
            "line-arc" => "M2,12 A12,12 0 0 1 14,4",
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
        if(value is string variant && variant is ElementAppearance.NumberFilled or ElementAppearance.NumberOutline or ElementAppearance.ShapeIndependent or ElementAppearance.ShapeUniform)
            return StyleIcon(variant);
        if (value is CaptureShape capture) return Icon(capture switch { CaptureShape.Rectangle => "capture-rectangle", CaptureShape.Ellipse => "capture-ellipse", CaptureShape.FreehandLasso => "capture-lasso", _ => "capture-polygon" });
        if (value is CaptureSource source) return Icon(source switch { CaptureSource.Background => "background", CaptureSource.CanvasOnly => "canvas", _ => "combined" });
        if (value is HighlighterTip tip) return Icon(tip == HighlighterTip.Flat ? "tip-flat" : "tip-round");
        if (value is LinePathMode pathMode) return Icon(pathMode == LinePathMode.Arc ? "line-arc" : "line-straight");
        if (value is StrokePattern pattern) return StrokePatternIcon(pattern);
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

    private static FrameworkElement StrokePatternIcon(StrokePattern pattern)
    {
        const double size = 18, unit = 1.5, units = 11;
        const double left = (size - units * unit) / 2, top = (size - unit) / 2;
        int[] marks = pattern switch
        {
            StrokePattern.Dashed => [3, 3, 3],
            StrokePattern.Dotted => [1, 1, 1, 1, 1, 1],
            StrokePattern.DashDot => [3, 1, 3, 1],
            _ => [11]
        };
        var geometry = new GeometryGroup();
        double x = left;
        foreach (int mark in marks)
        {
            var bounds = new Rect(x, top, mark * unit, unit);
            geometry.Children.Add(mark == 1 ? new EllipseGeometry(bounds) : new RectangleGeometry(bounds, unit / 2, unit / 2));
            x += (mark + 1) * unit;
        }
        geometry.Freeze();
        var glyph = new System.Windows.Shapes.Path { Data = geometry };
        glyph.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "CanvasText");
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = size, Height = size, Children = { glyph } } };
    }

    private static FrameworkElement StyleIcon(string variant, ElementKind kind = ElementKind.Rectangle, SymbolKind symbol = default)
    {
        var grid=new Grid { Width=18,Height=18 };
        bool number=variant is ElementAppearance.NumberFilled or ElementAppearance.NumberOutline;
        bool filled=variant is ElementAppearance.NumberFilled or ElementAppearance.ShapeUniform;
        System.Windows.Shapes.Shape outline;
        if (number || kind == ElementKind.Ellipse) outline = new System.Windows.Shapes.Ellipse();
        else if (kind is ElementKind.Symbol or ElementKind.Triangle)
        {
            var points = SymbolGeometry.Outline(kind == ElementKind.Triangle ? SymbolKind.Triangle : symbol, 15, 15);
            outline = new System.Windows.Shapes.Polygon { Points = new System.Windows.Media.PointCollection(points.Select(p => new Point(p.X, p.Y))), Stretch = Stretch.Uniform };
        }
        else outline = new System.Windows.Shapes.Rectangle { RadiusX=2,RadiusY=2 };
        outline.Margin=new Thickness(1.5);outline.StrokeThickness=1.4;
        outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"CanvasText");
        outline.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,filled?"CanvasText":"CanvasHover");
        grid.Children.Add(outline);
        if(number)
        {
            var digit=new TextBlock { Text="1",FontSize=11,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
            digit.SetResourceReference(TextBlock.ForegroundProperty,filled?"CanvasInput":"CanvasText");
            grid.Children.Add(digit);
        }
        return grid;
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
        AddVariants(choice, ElementKind.Rectangle);
        return choice;
    }

    internal static void AddVariants(SegmentedChoice choice, ElementKind kind)
    {
        foreach (var variant in AppearanceRegistry.VariantsFor(kind)) Add(choice, L.Get(variant.LabelKey), variant.Id);
    }

    internal static void SetStyleFamily(SegmentedChoice choice, ElementKind kind)
    {
        var variants = AppearanceRegistry.VariantsFor(kind);
        if (choice.Items.Count == variants.Count && Equals(choice.Items[0].Tag, variants[0].Id)) return;
        choice.ReplaceOptions(variants.Select(variant => (L.Get(variant.LabelKey), (object)variant.Id,
            kind == ElementKind.Text ? TextStyleIcon(variant.Id) : StyleIcon(variant.Id))));
    }

    private static FrameworkElement TextStyleIcon(string variant)
    {
        var box = new Grid { Width = 18, Height = 18 };
        if (variant == ElementAppearance.TextLabel)
        {
            var tag = new Border { CornerRadius = new(2), BorderThickness = new(1), Margin = new(0, 2, 0, 2) };
            tag.SetResourceReference(Border.BackgroundProperty, "CanvasHover");
            tag.SetResourceReference(Border.BorderBrushProperty, "CanvasText");
            box.Children.Add(tag);
        }
        var letter = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,2 H14 V5 H10 V14 H6 V5 H2 Z"), Stretch = Stretch.Fill,
            Margin = variant == ElementAppearance.TextLabel ? new Thickness(5) : new Thickness(3),
            StrokeThickness = variant == ElementAppearance.TextOutline ? 1.2 : 0, StrokeLineJoin = PenLineJoin.Round
        };
        letter.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, variant == ElementAppearance.TextOutline ? "CanvasInput" : "CanvasText");
        letter.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText");
        box.Children.Add(letter);
        return box;
    }

    internal static void UpdateColorStyle(SegmentedChoice choice, EditorTool tool, SymbolKind symbol = default)
    {
        ElementKind kind = Enum.TryParse<ElementKind>(tool.ToString(), out var parsed) ? parsed : ElementKind.Rectangle;
        var variants = AppearanceRegistry.VariantsFor(kind);
        for (int index = 0; index < Math.Min(choice.Items.Count, variants.Count); index++)
        {
            choice.SetIcon(index, StyleIcon(variants[index].Id,kind,symbol));
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
