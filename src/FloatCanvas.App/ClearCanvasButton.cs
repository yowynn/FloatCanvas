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
internal sealed class ClearCanvasButton : Button
{
    private readonly Func<bool> canClear;
    private readonly Func<Task> clear;
    private bool executing;
    internal bool IsConfirming { get; private set; }
    internal Popup ConfirmationPopup { get; }

    internal ClearCanvasButton(Func<bool> canClear, Func<Task> clear)
    {
        this.canClear = canClear; this.clear = clear;
        Tag = "clear"; Width = MinWidth = Height = MinHeight = 26;
        Margin = new Thickness(2, 0, 2, 0); Padding = new Thickness(3);
        BorderThickness = new Thickness(1); BorderBrush = Brushes.Transparent; FocusVisualStyle = null;
        SetResourceReference(StyleProperty, "CanvasDangerButton");
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, false);
        ToolTipService.SetInitialShowDelay(this, 650);
        var text = new TextBlock { Text = L.Get("Feedback4.Toolbar.ClearQuestion"), FontSize = 13 };
        text.Foreground = Brushes.White;
        var bubble = new Border { Child = text, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4), Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 6, ShadowDepth = 1, Opacity = .18, Color = Colors.Black } };
        bubble.SetResourceReference(Border.BackgroundProperty, "CanvasDanger");
        bubble.SetResourceReference(Border.BorderBrushProperty, "CanvasDanger");
        ConfirmationPopup = new Popup { Child = bubble, PlacementTarget = this, Placement = PlacementMode.Bottom,
            VerticalOffset = 4, AllowsTransparency = true, StaysOpen = true, Focusable = false, IsHitTestVisible = false };
        Click += async (_, _) => await ActivateAsync();
        Unloaded += (_, _) => CancelConfirmation();
        Refresh();
    }

    internal async Task ActivateAsync()
    {
        if (executing) return;
        if (!canClear()) { CancelConfirmation(); return; }
        if (!IsConfirming)
        {
            IsConfirming = true; Refresh();
            if (IsLoaded)
            {
                if (Window.GetWindow(this) is { } window)
                    ConfirmationPopup.VerticalOffset = Math.Max(4, window.ActualHeight - TranslatePoint(new Point(0, ActualHeight), window).Y + 4);
                ConfirmationPopup.IsOpen = true;
            }
            return;
        }
        CancelConfirmation(); executing = true; IsEnabled = false;
        try { await clear(); }
        finally { executing = false; IsEnabled = true; }
    }

    internal void CancelConfirmation()
    {
        if (!IsConfirming) return;
        IsConfirming = false; ConfirmationPopup.IsOpen = false; Refresh();
    }

    private void Refresh()
    {
        string label = L.Get(IsConfirming ? "Feedback4.Toolbar.ClearConfirm" : "Feedback4.Toolbar.NewBlankCanvas");
        ToolTip = IsConfirming ? null : label;
        AutomationProperties.SetName(this, label);
        var path = new Path { Data = Geometry.Parse(IsConfirming ? "M3 8 L6.5 11.5 L13 4.5" :
            "M13.5,1.5 L8.8,6.2 M7.2,5.8 Q9,5.3 10.2,7.1 Q11.4,9.6 9.4,13.1 Q5.5,15.7 1.5,10 Q5.4,10 7.2,5.8 Z M4.1,12.7 L6.6,10.6 M6.8,14 L8.2,11.5"),
            Stroke = Brushes.White, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        var canvas = new Canvas { Width = 16, Height = 16, IsHitTestVisible = false }; canvas.Children.Add(path);
        Content = new Viewbox { Width = 16, Height = 16, Child = canvas, IsHitTestVisible = false };
    }
}
