using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

internal sealed partial class TextRenderer
{
    private const double LabelBorder = .052, LabelRadius = .19;
    private readonly record struct Insets(double Horizontal, double Top, double Bottom)
    {
        public static Insets For(string? variant, double size, double outlineWidth) => variant switch
        {
            ElementAppearance.TextOutline => new(outlineWidth, outlineWidth, outlineWidth),
            ElementAppearance.TextLabel => new(size * (.38 + LabelBorder), size * (.12 + LabelBorder), size * (.16 + LabelBorder)),
            _ => default
        };
        public double InnerWidth(double width) => width <= 0 ? 0 : Math.Max(.001, width - Horizontal * 2);
    }

    internal void DrawElement(SKCanvas canvas, CanvasElement element, SKPaint paint)
    {
        var style = element.Style;
        if (style.Variant is not (ElementAppearance.TextOutline or ElementAppearance.TextLabel))
        {
            Draw(canvas, element.Text, style, element.Width, paint);
            return;
        }

        bool label = style.Variant == ElementAppearance.TextLabel;
        byte inkAlpha = (byte)(style.PrimaryColor >> 24), edgeAlpha = (byte)(style.SecondaryColor >> 24);
        double outlineWidth = label ? 0 : style.TextOutlineWidthAt(style.FontSize);
        bool drawOutline = outlineWidth > 0 && edgeAlpha > 0;
        if (inkAlpha == 0 && !drawOutline) return;
        var runs = GetLayout(element.Text, style, element.Width).Runs;
        int layer = -1;
        if (inkAlpha < 255 && (label || drawOutline))
        {
            var bounds = SKRect.Create((float)element.Width, (float)element.Height);
            foreach (var run in runs)
            {
                var ink = run.Blob.Bounds;
                ink.Offset(run.X, run.Y);
                bounds = SKRect.Union(bounds, ink);
            }
            bounds.Inflate((float)(outlineWidth + 1), (float)(outlineWidth + 1));
            using var opacity = label ? new SKPaint { Color = SKColors.White.WithAlpha(inkAlpha) } : null;
            layer = canvas.SaveLayer(bounds, opacity);
        }
        var previousBlend = paint.BlendMode;
        var previousColor = paint.Color;
        var previousJoin = paint.StrokeJoin;
        float previousWidth = paint.StrokeWidth;
        try
        {
            if (label)
            {
                var colors = BuiltInColors.LabelColors(style.PrimaryColor);
                var box = new SKRect(0, 0, (float)element.Width, (float)element.Height);
                float border = (float)Math.Min(style.FontSize * LabelBorder, Math.Min(box.Width, box.Height) / 2);
                float radius = (float)Math.Min(style.FontSize * LabelRadius, Math.Min(box.Width, box.Height) / 2);
                paint.Color = new SKColor(colors.Border);
                canvas.DrawRoundRect(box, radius, radius, paint);
                box.Inflate(-border, -border);
                paint.Color = new SKColor(colors.Tint);
                float inner = Math.Max(0, radius - border);
                if (!box.IsEmpty) canvas.DrawRoundRect(box, inner, inner, paint);
                paint.Color = new SKColor(colors.Letter);
            }
            else
            {
                if (drawOutline)
                {
                    paint.Color = new SKColor(style.SecondaryColor);
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = (float)(2 * outlineWidth);
                    paint.StrokeJoin = SKStrokeJoin.Round;
                    foreach (var run in runs) canvas.DrawText(run.Blob, run.X, run.Y, paint);
                    paint.Style = SKPaintStyle.Fill;
                    // Replace the inner half of the stroke with the glyph's own ink.
                    // Transparent glyphs leave a hollow outline without erasing the backdrop.
                    if (layer >= 0) paint.BlendMode = SKBlendMode.Src;
                }
                paint.Color = new SKColor(style.PrimaryColor);
            }
            foreach (var run in runs) canvas.DrawText(run.Blob, run.X, run.Y, paint);
        }
        finally
        {
            paint.BlendMode = previousBlend;
            paint.Color = previousColor;
            paint.Style = SKPaintStyle.Fill;
            paint.StrokeJoin = previousJoin;
            paint.StrokeWidth = previousWidth;
            if (layer >= 0) canvas.RestoreToCount(layer);
        }
    }
}
