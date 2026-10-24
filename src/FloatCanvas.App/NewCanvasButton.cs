using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Two deliberate clicks share one hit target; the hint never captures input.</summary>
internal sealed class NewCanvasButton : Button
{
    private readonly Func<bool> canCreate;
    private readonly Func<Task> create;
    private readonly Func<bool> needsConfirmation;
    private bool executing;
    internal bool IsConfirming { get; private set; }
    internal Popup ConfirmationPopup { get; }
    private readonly PopupHandle confirmationDisplay;

    internal NewCanvasButton(Func<bool> canCreate, Func<Task> create,Func<bool>? needsConfirmation=null)
    {
        this.canCreate = canCreate; this.create = create;
        this.needsConfirmation=needsConfirmation??(()=>true);
        Tag = "new-canvas"; Width = MinWidth = Height = MinHeight = 26;
        Margin = new Thickness(2, 0, 2, 0); Padding = new Thickness(3);
        BorderThickness = new Thickness(1); BorderBrush = Brushes.Transparent;
        FocusVisualStyle = null;
        SetResourceReference(StyleProperty, typeof(Button));
        SetResourceReference(BackgroundProperty,"CanvasButton");
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, false);
        ToolTipService.SetInitialShowDelay(this, 650);
        var text = new TextBlock { Text = L.Get("History.NewQuestion"), FontSize = 14, FontWeight=FontWeights.SemiBold };
        text.SetResourceReference(TextBlock.ForegroundProperty,"CanvasText");
        var bubble = new ConfirmationBubble { Child = text, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4, 4, 4, 12), Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 6, ShadowDepth = 1, Opacity = .18, Color = Colors.Black } };
        bubble.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        bubble.SetResourceReference(Border.BorderBrushProperty, "CanvasAccent");
        // HwndSource autosizing excludes its root's Margin. Keep the shadow gutter
        // inside a marginless layout root so the native client fits the whole bubble.
        var popupRoot=new Grid();popupRoot.Children.Add(bubble);
        ConfirmationPopup = new Popup { Child = popupRoot, PlacementTarget = this, Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (size, target, _) =>
            {
                return [new(new Point((target.Width - size.Width) / 2, -size.Height - 1), PopupPrimaryAxis.None)];
            },
            AllowsTransparency = true, StaysOpen = true, Focusable = false, IsHitTestVisible = false };
        confirmationDisplay=PopupHandle.For(ConfirmationPopup);
        Click += async (_, _) => await ActivateAsync();
        Unloaded += (_, _) => CancelConfirmation();
        Refresh();
    }

    internal async Task ActivateAsync()
    {
        if (executing) return;
        if (!canCreate()) { CancelConfirmation(); return; }
        if (!IsConfirming && needsConfirmation())
        {
            IsConfirming = true; Refresh();
            if (IsLoaded)
            {
                confirmationDisplay.IsOpen = true;
            }
            return;
        }
        CancelConfirmation(); executing = true; IsEnabled = false;
        try { await create(); }
        finally { executing = false; IsEnabled = true; }
    }

    internal void CancelConfirmation()
    {
        if (!IsConfirming) return;
        IsConfirming = false; confirmationDisplay.IsOpen = false; Refresh();
    }

    private void Refresh()
    {
        string label = L.Get(IsConfirming ? "History.NewConfirm" : "Feedback4.Toolbar.NewBlankCanvas");
        ToolTip = IsConfirming ? null : label + "  Ctrl+N";
        AutomationProperties.SetName(this, label);
        var path = new Path { Data = Geometry.Parse(IsConfirming ? "M3 8 L6.5 11.5 L13 4.5" :
            CommandGlyphs.NewCanvas),
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        path.SetResourceReference(Shape.StrokeProperty,"CanvasText");
        var canvas = new Canvas { Width = 16, Height = 16, IsHitTestVisible = false }; canvas.Children.Add(path);
        Content = new Viewbox { Width = 16, Height = 16, Child = canvas, IsHitTestVisible = false };
    }
}

/// <summary>The body and pointer share one filled outline, including the shadow.</summary>
internal sealed class ConfirmationBubble : Border
{
    internal static Geometry Outline(Size size)
    {
        double width = Math.Max(2, size.Width), height = Math.Max(2, size.Height);
        var body = new RectangleGeometry(new Rect(1, 1, width - 2, height - 2), 6, 6);
        var tail = new StreamGeometry();
        using (var context = tail.Open())
        {
            context.BeginFigure(new Point(width / 2 - 6, height - 2), true, true);
            context.LineTo(new Point(width / 2, height + 5), true, false);
            context.LineTo(new Point(width / 2 + 6, height - 2), true, false);
        }
        var outline = Geometry.Combine(body, tail, GeometryCombineMode.Union, null);
        outline.Freeze();
        return outline;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (RenderSize.Width < 2 || RenderSize.Height < 2) return;
        drawingContext.DrawGeometry(Background, new Pen(BorderBrush, 2) { LineJoin = PenLineJoin.Round }, Outline(RenderSize));
    }
}
