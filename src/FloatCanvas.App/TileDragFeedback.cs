using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatCanvas.App;

/// <summary>One shared insertion marker and a non-interactive drag visual.</summary>
internal sealed class TileDragFeedback : Adorner, IDisposable
{
    private readonly AdornerLayer layer;
    private readonly FrameworkElement source, gallery;
    private readonly BitmapSource image;
    private readonly Point anchor;
    private readonly (FrameworkElement Element,double Opacity,bool ToolTips)[] sources;
    private readonly Brush accent, halo;
    private bool disposed;
    private DragGhost? ghost;
    internal Rect GhostBounds { get; private set; }
    internal Rect? TargetBounds { get; private set; }
    internal bool IsExternalTarget { get; private set; }

    private TileDragFeedback(FrameworkElement source, FrameworkElement gallery, AdornerLayer layer, BitmapSource image, Point start,IEnumerable<FrameworkElement> moving) : base(gallery)
    {
        this.source = source; this.gallery = gallery; this.layer = layer; this.image = image;
        var origin = source.TranslatePoint(new Point(), gallery);
        anchor = start - (Vector)origin;
        GhostBounds = new Rect(origin, source.RenderSize);
        sources=moving.Distinct().Select(element=>(element,element.Opacity,System.Windows.Controls.ToolTipService.GetIsEnabled(element))).ToArray();
        accent = source.TryFindResource("CanvasAccent") as Brush ?? Brushes.DodgerBlue;
        halo = source.TryFindResource("CanvasSurface") as Brush ?? Brushes.White;
        IsHitTestVisible = false; Focusable = false; ClipToBounds = false;
        layer.Add(this);
        foreach(var state in sources)
        {
            state.Element.SetCurrentValue(OpacityProperty,state.Opacity*.35);
            state.Element.SetCurrentValue(System.Windows.Controls.ToolTipService.IsEnabledProperty,false);
        }
    }

    internal static TileDragFeedback? Create(FrameworkElement source, FrameworkElement gallery, Point start,IEnumerable<FrameworkElement>? moving=null)
    {
        var layer = GetLayer(gallery);
        if (layer is null || source.RenderSize.Width <= 0 || source.RenderSize.Height <= 0) return null;
        var dpi = VisualTreeHelper.GetDpi(source);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var elements=(moving??[source]).Distinct().ToArray();
        var snapshot=new DrawingVisual();
        using(var context=snapshot.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(source) {Stretch=Stretch.Fill},null,new Rect(source.RenderSize));
            if(elements.Length>1)
            {
                var text=new FormattedText(elements.Length.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.White,dpi.PixelsPerDip);
                double width=Math.Max(22,text.Width+10),height=22;
                var badge=new Rect(Math.Max(0,source.ActualWidth-width),Math.Max(0,source.ActualHeight-height),width,height);
                context.DrawRoundedRectangle(source.TryFindResource("CanvasAccent") as Brush??Brushes.DodgerBlue,null,badge,7,7);
                context.DrawText(text,new Point(badge.X+(width-text.Width)/2,badge.Y+(height-text.Height)/2));
            }
        }
        image.Render(snapshot); image.Freeze();
        var feedback=new TileDragFeedback(source,gallery,layer,image,start,elements);
        try
        {
            if(PresentationSource.FromVisual(source) is not null)
                feedback.ghost=new(source,image,gallery.PointToScreen(feedback.GhostBounds.TopLeft));
            return feedback;
        }
        catch{feedback.Dispose();throw;}
    }

    internal static AdornerLayer? GetLayer(FrameworkElement gallery)
    {
        var layer=AdornerLayer.GetAdornerLayer(gallery);
        for(DependencyObject? parent=gallery;parent is not null;parent=VisualTreeHelper.GetParent(parent))
            if(parent is AdornerDecorator decorator)layer=decorator.AdornerLayer;
        return layer;
    }

    internal void Move(Point point)
    {
        GhostBounds = new Rect(point - (Vector)anchor, source.RenderSize);
        ghost?.Move(gallery.PointToScreen(GhostBounds.TopLeft));
        InvalidateVisual();
    }

    internal void ShowExternalTarget(FrameworkElement target)
    {
        var bounds = new Rect(target.TranslatePoint(new Point(), gallery), target.RenderSize);
        IsExternalTarget = true;TargetBounds = bounds;
        InvalidateVisual();
    }
    internal void ShowInsertion(Rect bounds){IsExternalTarget=false;TargetBounds=bounds;InvalidateVisual();}

    internal void ClearTarget() { TargetBounds = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext context)
    {
        if(ghost is null){context.PushOpacity(.6); context.DrawImage(image, GhostBounds); context.Pop();}
        if (TargetBounds is { } target)
        {
            if (IsExternalTarget)
            {
                context.PushOpacity(.18); context.DrawRoundedRectangle(accent, null, target, 5, 5); context.Pop();
                context.DrawRoundedRectangle(null, new Pen(accent, 3), target, 5, 5);
            }
            else
            {
                var top = target.TopLeft; var bottom = target.Width>0?target.TopRight:target.BottomLeft;
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
        ghost?.Dispose();ghost=null;
        foreach(var state in sources)
        {
            state.Element.SetCurrentValue(OpacityProperty,state.Opacity);
            state.Element.SetCurrentValue(System.Windows.Controls.ToolTipService.IsEnabledProperty,state.ToolTips);
        }
    }
}
