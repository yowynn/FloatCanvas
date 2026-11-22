using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>Resize the text frame without distorting glyphs, fitting its font and wrapping to the new bounds.</summary>
internal static class TextFrameLayout
{
    private const double MinimumLabelWidth = 40;
    private const double MaximumLabelWidth = 360;

    private static double LabelWidth(CanvasElement text, SceneRenderer renderer) =>
        Math.Clamp(renderer.MeasureText(text.Text, text.Style).Width + 4, MinimumLabelWidth, MaximumLabelWidth);

    internal static bool UsesAutoWidth(CanvasElement text, SceneRenderer renderer) =>
        !text.TextFrameFixed && (text.TextAutoWidth ||
            // Older labels saved their calculated width without recording its origin.
            text.Width >= MinimumLabelWidth && text.Width <= MaximumLabelWidth &&
            (text.Width == MinimumLabelWidth || Math.Abs(text.Width - LabelWidth(text, renderer)) < .001));

    internal static void Reflow(CanvasElement text, bool autoWidth, SceneRenderer renderer)
    {
        text.TextAutoWidth = autoWidth;
        if (autoWidth) text.Width = LabelWidth(text, renderer);
        var metrics = renderer.MeasureText(text.Text, text.Style, text.Width);
        if (autoWidth && metrics.Width > text.Width + .001)
        {
            // A glyph wider than the usual wrapping limit must still fit inside its own editable frame.
            text.Width = metrics.Width + 4;
            metrics = renderer.MeasureText(text.Text, text.Style, text.Width);
        }
        text.Height = metrics.Height;
    }

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
        target.Transform=source.Transform;target.TextFrameFixed=true;target.TextAutoWidth=false;
        var movedCenter=ElementGeometry.LocalToPlane(target).TransformPoint(new(target.Width/2,target.Height/2));
        target.Transform=Affine2D.Translation(center.X-movedCenter.X,center.Y-movedCenter.Y).Compose(target.Transform);
        // A wider frame can remove wrapped lines, so the old font size is no longer
        // an upper bound. Fit the full frame while keeping glyph axes undistorted.
        Fit(target,target.Height,renderer);
    }
    internal static void Fit(CanvasElement target,double requestedSize,SceneRenderer renderer)
        => target.Style.FontSize = renderer.FittedTextSize(target.Text, target.Style, target.Width, target.Height, requestedSize);
}
