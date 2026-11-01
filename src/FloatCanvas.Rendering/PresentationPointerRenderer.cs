using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Transient pointer ink in global physical-screen coordinates, independent of every canvas and scene node.</summary>
public static class PresentationPointerRenderer
{
    public static IReadOnlyList<RectD> DirtyBounds(PresentationFrame? frame, PresentationPointerSettings settings, RectD screenViewport)
        => Array.AsReadOnly(Build(frame, settings, screenViewport).Select(primitive => primitive.Bounds).ToArray());

    /// <summary>The caller's canvas uses physical screen coordinates; any monitor-origin translation is already applied.</summary>
    public static void Draw(SKCanvas canvas, PresentationFrame? frame, PresentationPointerSettings settings, RectD screenViewport)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var primitives = Build(frame, settings, screenViewport);
        if (primitives.Count == 0) return;
        int saved = canvas.Save();
        try
        {
            canvas.ClipRect(ToRect(screenViewport));
            using var paint = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            foreach (var primitive in primitives)
            {
                paint.Color = primitive.Color;
                paint.Style = primitive.Width == 0 ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)primitive.Width;
                if (primitive.Circle) canvas.DrawCircle(ToPoint(primitive.First), (float)primitive.Radius, paint);
                else canvas.DrawLine(ToPoint(primitive.First), ToPoint(primitive.Last), paint);
            }
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private static List<Primitive> Build(PresentationFrame? frame, PresentationPointerSettings settings, RectD viewport)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid()) throw new ArgumentException("Invalid global presentation pointer settings.", nameof(settings));
        var result = new List<Primitive>();
        if (frame is null || !settings.AnyEnabled || !Finite(viewport) || viewport.IsEmpty) return result;

        if (settings.TrailEnabled)
        {
            var cutoff = frame.Time - TimeSpan.FromMilliseconds(settings.TrailMilliseconds);
            for (int index = 1; index < frame.Trail.Count; index++)
            {
                var sample = frame.Trail[index]; var previous = frame.Trail[index - 1];
                double age = (frame.Time - sample.Timestamp).TotalMilliseconds;
                if (sample.StartsSegment || age < 0 || age >= settings.TrailMilliseconds || previous.Timestamp > sample.Timestamp) continue;
                var first = previous.ScreenPoint;
                if (previous.Timestamp < cutoff && sample.Timestamp > previous.Timestamp)
                    first += (sample.ScreenPoint - first) * ((cutoff - previous.Timestamp).TotalMilliseconds / (sample.Timestamp - previous.Timestamp).TotalMilliseconds);
                Add(first, sample.ScreenPoint, 0, settings.TrailWidthPixels,
                    Color(settings.TrailColor, 1 - age / settings.TrailMilliseconds), circle: false);
            }
        }
        if (settings.CursorEnabled && frame.CursorScreen is { } cursor)
            Add(cursor, cursor, settings.CursorRadiusPixels, 0, Color(settings.CursorColor, 1), circle: true);
        if (settings.ClicksEnabled)
            foreach (var click in frame.Clicks)
            {
                if (settings.GetClick(click.Button) is not { Enabled: true } style) continue;
                double age = (frame.Time - click.Timestamp).TotalMilliseconds;
                if (age < 0 || age >= style.DurationMilliseconds) continue;
                double progress = age / style.DurationMilliseconds;
                Add(click.ScreenPoint, click.ScreenPoint, style.RadiusPixels * (1 + progress),
                    style.Shape == PresentationClickShape.Ring ? style.StrokeWidthPixels : 0,
                    Color(style.Color, 1 - progress), circle: true);
            }
        return result;

        void Add(PointD first, PointD last, double radius, double width, SKColor color, bool circle)
        {
            if (color.Alpha == 0 || result.Count >= PresentationLimits.MaximumPrimitives) return;
            double support = width / 2 + 1;
            if (!circle)
            {
                var clip = viewport.Inflate(support);
                if (!PresentationGeometry.TryClipSegment(first, last,
                    [new(clip.Left, clip.Top), new(clip.Right, clip.Top), new(clip.Right, clip.Bottom), new(clip.Left, clip.Bottom)],
                    out first, out last)) return;
            }
            var bounds = (circle ? new RectD(first.X - radius, first.Y - radius, radius * 2, radius * 2) : RectD.FromPoints(first, last))
                .Inflate(support).Intersect(viewport);
            if (!bounds.IsEmpty) result.Add(new(first, last, radius, width, color, circle, bounds));
        }
    }

    private static SKColor Color(uint argb, double opacity)
    {
        var color = new SKColor(argb);
        return color.WithAlpha((byte)Math.Clamp((int)Math.Round(color.Alpha * opacity), 0, 255));
    }
    private static bool Finite(RectD rectangle) => double.IsFinite(rectangle.Left) && double.IsFinite(rectangle.Top) &&
        double.IsFinite(rectangle.Right) && double.IsFinite(rectangle.Bottom);
    private static SKPoint ToPoint(PointD point) => new((float)point.X, (float)point.Y);
    private static SKRect ToRect(RectD rectangle) => new((float)rectangle.Left, (float)rectangle.Top, (float)rectangle.Right, (float)rectangle.Bottom);
    private readonly record struct Primitive(PointD First, PointD Last, double Radius, double Width, SKColor Color, bool Circle, RectD Bounds);
}
