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
        var text = new TextBlock { Text = L.Get("History.NewQuestion"), FontSize = 12, FontWeight=FontWeights.SemiBold };
        text.SetResourceReference(TextBlock.ForegroundProperty,"CanvasText");
        var bubble = new Border { Child = text, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2), Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(4, 4, 4, 10), Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 6, ShadowDepth = 1, Opacity = .18, Color = Colors.Black } };
        bubble.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        bubble.SetResourceReference(Border.BorderBrushProperty, "CanvasAccent");
        // HwndSource autosizing excludes its root's Margin. Keep the shadow gutter
        // inside a marginless layout root so the native client fits the whole bubble.
        var popupRoot=new Grid();popupRoot.Children.Add(bubble);
        var tail = new Path { Data = Geometry.Parse("M0,0 L6,6 L12,0"), Width = 12, Height = 7,
            StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4), IsHitTestVisible = false };
        tail.SetResourceReference(Shape.FillProperty, "CanvasSurface");
        tail.SetResourceReference(Shape.StrokeProperty, "CanvasAccent");
        popupRoot.Children.Add(tail);
        ConfirmationPopup = new Popup { Child = popupRoot, PlacementTarget = this, Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (size, target, _) =>
            {
                double top = Window.GetWindow(this) is { } window ? TranslatePoint(new Point(), window).Y : ToolbarLauncher.Expansion + 5;
                return [new(new Point((target.Width - size.Width) / 2, Math.Max(-top, -size.Height - 1)), PopupPrimaryAxis.None)];
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
