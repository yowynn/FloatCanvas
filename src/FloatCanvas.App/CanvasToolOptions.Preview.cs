using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class CanvasToolOptions
{
    private readonly StackPanel headerActions = new() { Orientation = Orientation.Horizontal };
    private readonly CaptionModeSwitch depthPriorityOcclusion = new(DepthIcon);
    private readonly ToolbarOpacitySlider guideOpacity = new() { Width = 62, Height = 28, Margin = new(3, 0, 3, 0) };
    private bool depthPreview, previewAllowed = true;

    private void InitializePreviewControls()
    {
        headerActions.Children.Add(depthPriorityOcclusion); headerActions.Children.Add(guideOpacity);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(headerActions, true);
        depthPriorityOcclusion.Click += (_, _) =>
        {
            if (!refreshing && current.Projection.Enabled && previewAllowed)
                DepthPriorityOcclusionChanged?.Invoke(!depthPreview);
        };
        AutomationProperties.SetName(guideOpacity, L.Get("Canvas.GuideOpacity"));
        guideOpacity.ValueChanged += (_, _) =>
        {
            UpdateOpacityDescription();
            if (!refreshing) GuideOpacityChanged?.Invoke(guideOpacity.Value);
        };
        guideOpacity.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !guideOpacity.IsPointerInteracting) return;
            guideOpacity.CancelPointerGesture(); e.Handled = true;
        };
        UpdateOpacityDescription(); SynchronizeDepthSwitch();
    }

    internal void RefreshPreviewControls(bool depth, double opacity, bool allowed = true)
    {
        depthPreview = depth; previewAllowed = allowed;
        SynchronizeDepthSwitch(); RefreshGuideOpacity(opacity);
    }

    internal void RefreshGuideOpacity(double opacity)
    {
        if (guideOpacity.IsPointerInteracting) return;
        bool wasRefreshing = refreshing; refreshing = true;
        try { guideOpacity.Value = Math.Clamp(opacity, 0, 1); UpdateOpacityDescription(); }
        finally { refreshing = wasRefreshing; }
    }

    private void SynchronizeDepthSwitch() => depthPriorityOcclusion.Synchronize(!depthPreview,
        current.Projection.Enabled && previewAllowed, L.Get("Canvas.OcclusionMode"),
        L.Get(depthPreview ? "Canvas.DepthPriorityOcclusion" : "Canvas.LayerOcclusion"),
        L.Get(depthPreview ? "Canvas.SwitchLayerOcclusion" : "Canvas.SwitchDepthOcclusion"),
        L.Get(!current.Projection.Enabled ? "Canvas.DepthRequiresPerspective" : "GroupCanvas.SwitchUnavailable"));

    private void UpdateOpacityDescription()
    {
        string value = L.Get("Canvas.GuideOpacity") + $" · {guideOpacity.Value:P0}";
        guideOpacity.ToolTip = value + "\n" + L.Get("Canvas.GuideOpacityHint");
        AutomationProperties.SetItemStatus(guideOpacity, value);
        AutomationProperties.SetHelpText(guideOpacity, L.Get("Canvas.GuideOpacityHint"));
    }

    private static FrameworkElement DepthIcon(bool layers, string foreground)
    {
        var icon = CommandGlyphs.Create(layers
            ? "M1,5 L8,1 L15,5 L8,9 Z M1,8 L8,12 L15,8 M1,11 L8,15 L15,11"
            : "M1,14 L5,8 H11 L15,14 Z M4,10 V5 H8 V10 Z M9,7 V1 H13 V7 Z");
        icon.Width = icon.Height = 16;
        var path = ((Canvas)((Viewbox)icon).Child).Children.OfType<System.Windows.Shapes.Path>().Single();
        path.StrokeThickness = 1.3; path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, foreground);
        return icon;
    }
}
