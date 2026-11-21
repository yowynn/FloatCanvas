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
internal class CaptionModeSwitch : Button
{
    private readonly RoundedSurfaceBorder thumb = new() { Width = 26, Height = 22, CornerRadius = new(5), BorderThickness = new(1), IsHitTestVisible = false };
    private readonly TranslateTransform offset = new();
    private bool initialized;
    private readonly Func<bool, string, FrameworkElement> icon;
    internal bool SelectedFirst { get; private set; }
    internal CaptionModeSwitch(Func<bool, string, FrameworkElement> icon)
    {
        this.icon = icon;
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
        PreviewKeyUp += (_, e) => { if (e.Key is Key.Enter or Key.Space) e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true; if (!e.IsRepeat && IsEnabled) OnClick();
        };
    }

    private Border ModeSlot(bool independent)
    {
        return new Border
        {
            Width = 26, Height = 22, IsHitTestVisible = false,
            HorizontalAlignment = independent ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Child = icon(independent, "CanvasModeInactive")
        };
    }

    protected override void OnClick()
    {
        if (IsEnabled) base.OnClick();
    }

    internal void Synchronize(bool first, bool enabled, string name, string status, string text, string? disabledReason = null)
    {
        bool changed = initialized && SelectedFirst != first;
        SelectedFirst = first; IsEnabled = enabled;
        bool independent = first;
        if (!initialized || changed)
        {
            double position = independent ? 0 : 28;
            thumb.SetResourceReference(BackgroundProperty, independent ? "CanvasModeIndependent" : "CanvasModeProject");
            thumb.Child = icon(independent, "CanvasOnAccent");
            double previous = offset.X;
            offset.BeginAnimation(TranslateTransform.XProperty, null); offset.X = position;
            if (changed && IsLoaded && SystemParameters.ClientAreaAnimation)
                offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(previous, position, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        }
        initialized = true;
        var lines = text.Split('\n');
        var tip = new StackPanel(); tip.Children.Add(new TextBlock { Text = lines[0], FontWeight = FontWeights.SemiBold });
        if (lines.Length > 1) tip.Children.Add(new TextBlock { Text = lines[1] });
        string? reason = enabled ? null : string.IsNullOrWhiteSpace(disabledReason) ? L.Get("GroupCanvas.SwitchUnavailable") : disabledReason;
        if (reason is not null) tip.Children.Add(new TextBlock { Text = reason });
        ToolTip = tip;
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetItemStatus(this, status);
        AutomationProperties.SetHelpText(this, reason is null ? text : text + "\n" + reason);
        if (changed) UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
