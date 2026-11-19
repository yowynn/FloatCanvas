using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private static SKColor HighlighterInk(SKColor color) => color.WithAlpha((byte)Math.Round(color.Alpha * .35));

    internal static void DrawStrokeTip(SKCanvas canvas, ElementKind kind, ElementStyle style,
        Projective2D placement, double layerOpacity)
    {
        var ink = new SKColor(style.Foreground);
        if (kind == ElementKind.Highlighter)
            ink = ink.WithAlpha((byte)Math.Round(HighlighterInkProfile.FootprintAlpha(HighlighterInk(ink).Alpha, style.HighlighterInkContrast)));
        ink = WithElementOpacity(ink, Math.Clamp(style.Opacity * layerOpacity, 0, 1));
        if (ink.Alpha == 0 || style.StrokeWidth <= 0) return;
        using var paint = new SKPaint { Color = ink, IsAntialias = true };
        int saved = canvas.Save();
        try
        {
            var matrix = ToMatrix(placement);
            canvas.Concat(in matrix);
            if (kind == ElementKind.Highlighter && style.HighlighterTip == HighlighterTip.Flat)
            {
                using var builder = new SKPathBuilder();
                foreach (var polygon in ElementGeometry.FlatHighlighterPolygons([default], style))
                    AddHead(builder, polygon, true);
                using var tip = builder.Detach();
                canvas.DrawPath(tip, paint);
            }
            else canvas.DrawCircle(0, 0, (float)(style.StrokeWidth / 2), paint);
        }
        finally { canvas.RestoreToCount(saved); }
    }
}
