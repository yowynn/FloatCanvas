using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>A toolbar-owned extension with an independent button rhythm and one shared outline.</summary>
internal sealed class ToolbarUpperRow : Grid
{
    internal const double ExtensionHeight = 33;
    internal const double ButtonSize = 26;
    internal const double ButtonSlot = 30;
    internal const double SurfaceGap = 3;
    internal const double CornerRadiusValue = 3;
    internal const double OuterCornerRadius = CornerRadiusValue + 3;
    internal const double Headroom = ExtensionHeight + SurfaceGap + 3 - ToolbarLauncher.BodyTop;
    private readonly Border strip;
    private readonly Border confirmation;
    private readonly RootCanvasToolbarHeader header;
    private readonly System.Windows.Shapes.Path outline = new() { IsHitTestVisible = false, StrokeThickness = 1 };
    private readonly System.Windows.Shapes.Path connector = new() { IsHitTestVisible = false, StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private Grid? shell;
    private Border? body;
    private ToolbarLauncher? launcher;
    private Rect lastBody, lastStrip;
    internal bool IsExpanded { get; private set; }
    internal event Action? ExpansionChanged;

    internal ToolbarUpperRow(Grid main, RootCanvasToolbarHeader header, IReadOnlyList<Button> tools, NewCanvasButton create)
    {
        this.header = header;
        outline.SetResourceReference(EffectProperty,"ToolbarEdgeEffect");
        Height = ExtensionHeight; HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top;
        Margin = new(103, 3, 0, 0);
        foreach (var column in main.ColumnDefinitions) ColumnDefinitions.Add(new() { Width = column.Width });
        header.Margin = new(-1,0,1,0); header.VerticalAlignment = VerticalAlignment.Top; header.Height = ExtensionHeight;
        header.MaxWidth = main.ColumnDefinitions.Take(12).Sum(column => column.Width.Value);
        SetColumnSpan(header, 12); Children.Add(header);
        // Keep the buttons at their original top position; the extra DIP below
        // absorbs fractional-DPI border rounding without clipping their frames.
        var slots = new Grid { VerticalAlignment = VerticalAlignment.Top }; slots.Resources["CanvasButtonCornerRadius"] = new CornerRadius(CornerRadiusValue);
        int slot = 0;
        for (int index = 0; index < tools.Count; index++)
        {
            if (index == 2)
            {
                slots.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
                var separator = new Border { Width = 1, Margin = new(4, 4, 4, 4) };
                separator.SetResourceReference(Border.BackgroundProperty, "CanvasBorder");
                SetColumn(separator, slot++); slots.Children.Add(separator);
            }
            slots.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
            SetColumn(tools[index], slot++); slots.Children.Add(tools[index]);
        }
        strip = new Border { Child = slots, Width = tools.Count * 30 + (tools.Count > 2 ? 10 : 0) + 2, Height = ExtensionHeight, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new(-1, 0, 0, 0),
            Padding = new(0, 2, 0, 2), BorderThickness = new(1), BorderBrush = Brushes.Transparent };
        SetColumn(strip, 12); SetColumnSpan(strip, ColumnDefinitions.Count - 12); Children.Add(strip);
        var text = new TextBlock { Text = FloatCanvas.Localization.AppText.Get("Toolbar.NewCanvasConfirm"), FontSize = 12,
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText");
        confirmation = new Border { Child = text, Height = ExtensionHeight, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new(4), BorderThickness = new(1), Padding = new(8, 0, 8, 0),
            IsHitTestVisible = true, ToolTip = text.Text, Visibility = Visibility.Collapsed };
        confirmation.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        confirmation.SetResourceReference(Border.BorderBrushProperty, "CanvasAccent");
        SetZIndex(confirmation, 20); SetZIndex(header, 10);
        SetColumn(confirmation, 24); SetColumnSpan(confirmation, 8); Children.Add(confirmation);
        create.UseInlineConfirmation = true;
        create.ConfirmationChanged += () => confirmation.Visibility = create.IsConfirming ? Visibility.Visible : Visibility.Collapsed;
        outline.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "CanvasSurface");
        outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasBorder");
        connector.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasHiddenBorder");
        SetExpanded(false);
    }

    internal void AttachSurface(Grid surface, Border mainBody, ToolbarLauncher mainLauncher)
    {
        shell = surface; body = mainBody; launcher = mainLauncher;
        shell.Children.Insert(0, outline);
        shell.Children.Add(connector);
        shell.LayoutUpdated += (_, _) => RefreshOutline();
        IsVisibleChanged += (_, _) => RefreshSurface();
        RefreshSurface();
    }

    private void RefreshSurface()
    {
        if (body is null || launcher is null) return;
        bool expanded = IsExpanded && Visibility == Visibility.Visible;
        double offset = expanded ? Headroom : 0;
        Height = ExtensionHeight;
        header.Margin = new(-1, offset, 1, 0);
        confirmation.Margin = new(0, offset, 0, 0);
        body.Margin = new(ToolbarLauncher.BodyLeft, ToolbarLauncher.BodyTop + offset, 3, 3);
        launcher.Margin = new(0, offset, 0, 0);
        outline.Visibility = connector.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded) { body.Background = Brushes.Transparent; body.BorderBrush = Brushes.Transparent; body.Effect = null; }
        else
        {
            body.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
            body.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
            body.SetResourceReference(EffectProperty,"ToolbarEdgeEffect");
        }
    }

    private void RefreshOutline()
    {
        if (shell is null || body is null || outline.Visibility != Visibility.Visible || strip.ActualWidth <= 0 || body.ActualHeight <= 0) return;
        var bodyRect = new Rect(body.TranslatePoint(new(), shell), new Size(body.ActualWidth, body.ActualHeight));
        var stripRect = new Rect(strip.TranslatePoint(new(), shell), new Size(strip.ActualWidth, strip.ActualHeight));
        if (bodyRect == lastBody && stripRect == lastStrip) return;
        lastBody = bodyRect; lastStrip = stripRect;
        bodyRect.Inflate(-.5, -.5); stripRect.Inflate(-.5, -.5);
        // The outer radius includes the three-DIP offset to the edge button,
        // keeping both rounded frames concentric.
        double radius = OuterCornerRadius - outline.StrokeThickness / 2;
        var geometry = new GeometryGroup();
        geometry.Children.Add(new RectangleGeometry(bodyRect, radius, radius));
        geometry.Children.Add(new RectangleGeometry(stripRect, radius, radius));
        double center = stripRect.Left + 15.5;
        var connection = new GeometryGroup();
        connection.Children.Add(new LineGeometry(new Point(center, stripRect.Bottom), new Point(center, bodyRect.Top + 2.5)));
        connection.Children.Add(new LineGeometry(new Point(center - 4, stripRect.Bottom), new Point(center + 4, stripRect.Bottom)));
        connection.Freeze(); connector.Data = connection;
        geometry.Freeze(); outline.Data = geometry;
    }

    internal void SetExpanded(bool expanded)
    {
        bool changed = IsExpanded != expanded; IsExpanded = expanded;
        strip.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RefreshSurface();
        if (changed) ExpansionChanged?.Invoke();
    }
}
