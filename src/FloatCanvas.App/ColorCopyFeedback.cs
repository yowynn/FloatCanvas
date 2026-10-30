using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A short copy receipt anchored to its swatch, without taking input.</summary>
internal sealed class ColorCopyFeedback
{
    private readonly FrameworkElement target;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private OverlayPopupWindow? window;
    private OverlayWindowScope? ownerScope;
    internal bool IsOpen => window is not null;

    internal ColorCopyFeedback(FrameworkElement target)
    {
        this.target = target;
        timer.Tick += (_, _) => Close();
        target.Unloaded += (_, _) => Close();
        target.IsVisibleChanged += (_, _) => { if (!target.IsVisible) Close(); };
    }

    internal void Show()
    {
        Close();
        if (!target.IsVisible || !target.IsLoaded || PresentationSource.FromVisual(target) is not HwndSource { IsDisposed: false } source) return;
        // A receipt belongs to its actual source window. The shared canvas owner
        // would make it a sibling that can disappear behind a raised tool panel.
        ownerScope = new OverlayWindowScope(source.Handle, () => false, installAsCurrent: false);
        var message = new TextBlock { Text = L.Get("Color.Copied"), FontSize = 12 };
        message.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText");
        var bubble = new Border { Child = message, CornerRadius = new(6), Padding = new(9, 5, 9, 5), BorderThickness = new(1) };
        bubble.SetResourceReference(Border.BackgroundProperty, "CanvasInput");
        bubble.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        window = new OverlayPopupWindow(bubble, target, PlacementMode.Bottom, 0, 4, Close,
            hitTestVisible: false, ownerScope: ownerScope, returnFocus: false);
        window.Show(); timer.Start();
    }

    internal void Close()
    {
        timer.Stop();
        var previous = window; window = null; previous?.Dispose();
        var scope = ownerScope; ownerScope = null; scope?.Dispose();
    }
}
