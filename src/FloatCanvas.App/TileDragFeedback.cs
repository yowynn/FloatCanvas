using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatCanvas.App;

/// <summary>Window-local drag feedback; no extra native window or input target.</summary>
internal sealed class TileDragFeedback : Adorner, IDisposable
{
    private readonly AdornerLayer layer;
    private readonly FrameworkElement source, gallery;
    private readonly ImageSource image;
    private readonly Point anchor;
    private readonly double sourceOpacity;
    private readonly bool toolTipsEnabled;
    private readonly Brush accent, halo;
    private bool disposed;
    internal Rect GhostBounds { get; private set; }
    internal Rect? TargetBounds { get; private set; }
    internal bool IsExternalTarget { get; private set; }

    private TileDragFeedback(FrameworkElement source, FrameworkElement gallery, AdornerLayer layer, ImageSource image, Point start) : base(gallery)
    {
        this.source = source; this.gallery = gallery; this.layer = layer; this.image = image;
        var origin = source.TranslatePoint(new Point(), gallery);
        anchor = start - (Vector)origin;
        GhostBounds = new Rect(origin, source.RenderSize);
        sourceOpacity = source.Opacity;
        toolTipsEnabled = System.Windows.Controls.ToolTipService.GetIsEnabled(source);
        accent = source.TryFindResource("CanvasAccent") as Brush ?? Brushes.DodgerBlue;
        halo = source.TryFindResource("CanvasSurface") as Brush ?? Brushes.White;
        IsHitTestVisible = false; Focusable = false; ClipToBounds = false;
        layer.Add(this);
        source.SetCurrentValue(OpacityProperty, sourceOpacity * .35);
        source.SetCurrentValue(System.Windows.Controls.ToolTipService.IsEnabledProperty, false);
    }

    internal static TileDragFeedback? Create(FrameworkElement source, FrameworkElement gallery, Point start)
    {
        var layer = AdornerLayer.GetAdornerLayer(gallery);
        if (layer is null || source.RenderSize.Width <= 0 || source.RenderSize.Height <= 0) return null;
        var dpi = VisualTreeHelper.GetDpi(source);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(source); image.Freeze();
        return new(source, gallery, layer, image, start);
    }

    internal void Move(Point point)
    {
        GhostBounds = new Rect(point - (Vector)anchor, source.RenderSize);
        InvalidateVisual();
    }

    internal void ShowTarget(FrameworkElement target, bool after, bool external)
    {
        var bounds = new Rect(target.TranslatePoint(new Point(), gallery), target.RenderSize);
        IsExternalTarget = external;
        TargetBounds = external ? bounds : new Rect(after ? bounds.Right : bounds.Left, bounds.Top, 0, bounds.Height);
        InvalidateVisual();
    }

    internal void ClearTarget() { TargetBounds = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext context)
    {
        context.PushOpacity(.6); context.DrawImage(image, GhostBounds); context.Pop();
        if (TargetBounds is { } target)
        {
            if (IsExternalTarget)
            {
                context.PushOpacity(.18); context.DrawRoundedRectangle(accent, null, target, 5, 5); context.Pop();
                context.DrawRoundedRectangle(null, new Pen(accent, 3), target, 5, 5);
            }
            else
            {
                var top = target.TopLeft; var bottom = target.BottomLeft;
                context.DrawLine(new Pen(halo, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, top, bottom);
                context.DrawLine(new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, top, bottom);
                context.DrawEllipse(accent, null, top, 3, 3); context.DrawEllipse(accent, null, bottom, 3, 3);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        layer.Remove(this);
        source.SetCurrentValue(OpacityProperty, sourceOpacity);
        source.SetCurrentValue(System.Windows.Controls.ToolTipService.IsEnabledProperty, toolTipsEnabled);
    }
}
