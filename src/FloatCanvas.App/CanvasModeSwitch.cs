using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A controlled mode switch: clicks request a change; only the owner supplies state.</summary>
internal sealed class CanvasModeSwitch : Button
{
    private readonly Border thumb = new() { Width = 26, Height = 22, CornerRadius = new(5), BorderThickness = new(1), IsHitTestVisible = false };
    private readonly TranslateTransform offset = new();
    private bool initialized;
    internal GroupCanvasMode Mode { get; private set; }
    internal CanvasModeSwitch()
    {
        Width = 62; Height = 28; MinWidth = MinHeight = 0; Padding = new(2); Margin = new(3,0,3,0);
        SetResourceReference(StyleProperty, "CanvasCaptionButton");
        BorderThickness = new(1); SetResourceReference(BorderBrushProperty, "CanvasBorder");
        UseLayoutRounding = true;
        thumb.RenderTransform = offset; thumb.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        // The selected icon travels on one solid accent tile. The exposed icon
        // remains neutral; neither slot receives input independently.
        thumb.SetResourceReference(BorderBrushProperty, "CanvasModeSelectedBorder");
        var springs = new Grid { IsHitTestVisible = false, FlowDirection = FlowDirection.LeftToRight };
        springs.Children.Add(ModeSlot(true)); springs.Children.Add(ModeSlot(false)); springs.Children.Add(thumb);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Center;
        Content = new Grid { Width = 54, Height = 22, IsHitTestVisible = false, Children = { springs } };
        ToolTipService.SetShowOnDisabled(this, true);
        IsKeyboardFocusedChanged += (_, _) => SetResourceReference(BorderBrushProperty, IsKeyboardFocused ? "CanvasAccent" : "CanvasBorder");
        PreviewKeyUp += (_, e) => { if (e.Key is Key.Enter or Key.Space) e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true; if (!e.IsRepeat && IsEnabled) OnClick();
        };
    }

    private static Border ModeSlot(bool independent)
    {
        return new Border
        {
            Width = 26, Height = 22, IsHitTestVisible = false,
            HorizontalAlignment = independent ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Child = CanvasModeVisuals.Icon(independent, foreground: "CanvasModeInactive")
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var previous = Keyboard.FocusedElement;
        base.OnMouseLeftButtonDown(e);
        // Mouse use must not leave a latent Space activation behind when the
        // user returns to canvas navigation. Tab focus still belongs to the switch.
        if (!IsKeyboardFocused || ReferenceEquals(previous, this)) return;
        if (previous is DependencyObject source &&
            ReferenceEquals(PresentationSource.FromDependencyObject(source), PresentationSource.FromVisual(this)))
            Keyboard.Focus(previous);
        if (IsKeyboardFocused) Keyboard.ClearFocus();
    }

    protected override void OnClick()
    {
        if (IsEnabled) base.OnClick();
    }

    internal void Synchronize(GroupCanvasMode mode, bool enabled, string? disabledReason = null)
    {
        bool changed = initialized && Mode != mode;
        Mode = mode; IsEnabled = enabled;
        bool independent = mode == GroupCanvasMode.Temporary;
        if (!initialized || changed)
        {
            double position = independent ? 0 : 28;
            thumb.SetResourceReference(BackgroundProperty, independent ? "CanvasModeIndependent" : "CanvasModeProject");
            thumb.Child = CanvasModeVisuals.Icon(independent, foreground: "CanvasOnAccent");
            double previous = offset.X;
            offset.BeginAnimation(TranslateTransform.XProperty, null); offset.X = position;
            if (changed && IsLoaded && SystemParameters.ClientAreaAnimation)
                offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(previous, position, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        }
        initialized = true;
        string text = L.Get(independent ? "GroupCanvas.SwitchIndependent" : "GroupCanvas.SwitchProject");
        var lines = text.Split('\n');
        var tip = new StackPanel(); tip.Children.Add(new TextBlock { Text = lines[0], FontWeight = FontWeights.SemiBold });
        if (lines.Length > 1) tip.Children.Add(new TextBlock { Text = lines[1] });
        string? reason = enabled ? null : string.IsNullOrWhiteSpace(disabledReason) ? L.Get("GroupCanvas.SwitchUnavailable") : disabledReason;
        if (reason is not null) tip.Children.Add(new TextBlock { Text = reason });
        ToolTip = tip;
        AutomationProperties.SetName(this, L.Get("GroupCanvas.Display"));
        AutomationProperties.SetItemStatus(this, L.Get(independent ? "GroupCanvas.Temporary" : "GroupCanvas.RootProject"));
        AutomationProperties.SetHelpText(this, reason is null ? text : text + "\n" + reason);
        if (changed) UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
