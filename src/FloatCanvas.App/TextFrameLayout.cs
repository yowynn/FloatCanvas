using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>Resize the text frame without distorting glyphs, fitting its font and wrapping to the new bounds.</summary>
internal static class TextFrameLayout
{
    internal static void Transform(CanvasElement target,CanvasElement source,Affine2D delta,SceneRenderer renderer)
    {
        var center=new PointD(source.Width/2,source.Height/2);
        var placement=ElementGeometry.LocalToPlane(source);
        var before=placement.Jacobian(center);
        var after=Projective2D.FromAffine(delta).Compose(placement).Jacobian(center);
        double x=Math.Sqrt((after.M11*after.M11+after.M21*after.M21)/(before.M11*before.M11+before.M21*before.M21));
        double y=Math.Sqrt((after.M12*after.M12+after.M22*after.M22)/(before.M12*before.M12+before.M22*before.M22));
        Resize(target,source,x,y,renderer);
        var inverseScale=Affine2D.Scale(source.Width/target.Width,source.Height/target.Height);
        target.Transform=delta.Compose(source.Transform);
        if(source.ProjectivePlacement is { } projective)target.ProjectivePlacement=projective.Compose(Projective2D.FromAffine(inverseScale));
        else target.Transform=target.Transform.Compose(inverseScale);
    }
    internal static void Resize(CanvasElement target,CanvasElement source,double x,double y,SceneRenderer renderer)
    {
        var center=ElementGeometry.LocalToPlane(source).TransformPoint(new(source.Width/2,source.Height/2));
        target.Width=Math.Max(2,source.Width*x);target.Height=Math.Max(2,source.Height*y);
        target.Transform=source.Transform;target.TextFrameFixed=true;
        var movedCenter=ElementGeometry.LocalToPlane(target).TransformPoint(new(target.Width/2,target.Height/2));
        target.Transform=Affine2D.Translation(center.X-movedCenter.X,center.Y-movedCenter.Y).Compose(target.Transform);
        // A wider frame can remove wrapped lines, so the old font size is no longer
        // an upper bound. Fit the full frame while keeping glyph axes undistorted.
        Fit(target,EmojiGlyphs.IsEmoji(source.Text,source.Style)?source.Style.FontSize*y:target.Height,renderer);
    }
    internal static void Fit(CanvasElement target,double requestedSize,SceneRenderer renderer)
    {
        double maximum=Math.Clamp(requestedSize,1,2048);
        bool Fits(double size)
        {
            target.Style.FontSize=size;
            var measured=renderer.MeasureText(target.Text,target.Style,target.Width);
            return measured.Width<=target.Width+.001&&measured.Height<=target.Height+.001;
        }
        if(Fits(maximum))return;
        double minimum=1;
        for(int i=0;i<20;i++)
        {
            double middle=(minimum+maximum)/2;
            if(Fits(middle))minimum=middle;else maximum=middle;
        }
        target.Style.FontSize=minimum;
    }
}
