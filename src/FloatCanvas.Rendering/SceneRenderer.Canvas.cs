using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private SceneRenderSpace _renderSpace;
    private CanvasMapping? _mapping;

    /// <summary>Borrowed temporary screen pixels for export/capture. Never projected or persisted as a canvas image.</summary>
    public DesktopBackdrop? ScreenBackdrop { get; set; }

    public static RectD ProjectedBounds(SceneDocument document, CanvasElement element, bool includeStroke = false) =>
        CanvasMapping.Create(document).ProjectedBounds(element, includeStroke);

    private Projective2D TransformFor(SceneDocument? document) =>
        _renderSpace == SceneRenderSpace.PlaneContent || document is null ? Projective2D.Identity :
        ReferenceEquals(document, _drawingDocument) && _mapping is not null ? _mapping.PlaneToScreen : CanvasMapping.Create(document).PlaneToScreen;

    internal void DrawGrid(SKCanvas canvas, SceneDocument document)
        => DrawGridCore(canvas, document, false);

    private void DrawGridCore(SKCanvas canvas, SceneDocument document, bool reference)
    {
        var grid = document.Canvas.Grid;
        if (_renderSpace == SceneRenderSpace.PlaneContent || (!reference && (!grid.Visible || grid.Width <= 0 || (grid.Color >> 24) == 0))) return;
        var mapping = ReferenceEquals(document, _drawingDocument) && _mapping is not null ? _mapping : CanvasMapping.Create(document);
        var polygon = mapping.VisiblePlanePolygon;
        if (polygon.Count < 3) return;
        double cell = mapping.GridCellSize;
        if (!double.IsFinite(cell) || cell <= 0) return;
        double left = polygon.Min(p => p.X), top = polygon.Min(p => p.Y);
        double right = polygon.Max(p => p.X), bottom = polygon.Max(p => p.Y);
        if (!double.IsFinite(left + top + right + bottom)) return;
        var origin = mapping.GridOrigin;
        byte guideAlpha = grid.Visible && grid.Width > 0 && (grid.Color >> 24) != 0 && document.LayerOpacity > 0 ? (byte)88 : (byte)176;
        using var paint = new SKPaint { IsAntialias = true, Color = reference ? SKColors.White.WithAlpha(guideAlpha) : new SKColor(grid.Color),
            StrokeWidth = reference ? 1 : (float)grid.Width, Style = SKPaintStyle.Stroke,
            // Dotted strokes use near-zero dash lengths; round caps provide each dot's visible area.
            StrokeCap = !reference && grid.Pattern == StrokePattern.Dotted ? SKStrokeCap.Round : SKStrokeCap.Butt };
        using var dash = CreateDash(new ElementStyle { StrokeWidth = grid.Width, StrokePattern = grid.Pattern });
        using var whiteDashes = reference ? SKPathEffect.CreateDash([4, 4], 0) : null;
        using var blackDashes = reference ? SKPathEffect.CreateDash([4, 4], 4) : null;
        paint.PathEffect = reference ? whiteDashes : dash;
        using var darkGuide = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(guideAlpha),
            StrokeWidth = 1, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt, PathEffect = blackDashes };
        int saved = canvas.Save();
        try
        {
            if (!ClipSafePlane(canvas, document)) return;
            if (!reference) { var matrix = ToMatrix(mapping.PlaneToScreen); canvas.Concat(in matrix); }
            DrawFamily(true, left, right, origin.X);
            DrawFamily(false, top, bottom, origin.Y);
        }
        finally { canvas.RestoreToCount(saved); }

        void DrawFamily(bool vertical, double minimum, double maximum, double anchor)
        {
            // Bound work without changing the requested cell count merely because a distant
            // viewport corner is compressed. A global pixel-spacing threshold made an 8x8
            // reference suddenly become 4x4 while its central cells were still clearly separated.
            double stride = cell;
            for (int i = 0; i < 52 && (maximum - minimum) / stride > 2048; i++) stride *= 2;
            double first = Math.Ceiling((minimum - anchor) / stride), last = Math.Floor((maximum - anchor) / stride);
            if (!double.IsFinite(first) || !double.IsFinite(last) || last - first > 2048) return;
            PointD? previousStart = null;
            PointD previousEnd = default;
            for (int i = 0; i <= (int)(last - first); i++)
            {
                double coordinate = anchor + (first + i) * stride;
                if (!GridSpan(polygon, vertical, coordinate, out double start, out double end)) continue;
                var a = vertical ? new PointD(coordinate, start) : new PointD(start, coordinate);
                var b = vertical ? new PointD(coordinate, end) : new PointD(end, coordinate);
                var screenStart = mapping.ToScreen(a); var screenEnd = mapping.ToScreen(b);
                // Suppress a line only when its whole visible span stays within two screen
                // pixels of the last retained line. Compression at one remote corner must
                // never remove well-separated cells in the reference region.
                if (previousStart is { } previous && Crowded(screenStart, screenEnd, previous, previousEnd)) continue;
                previousStart = screenStart; previousEnd = screenEnd;
                if (reference)
                {
                    a = screenStart; b = screenEnd;
                    canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, darkGuide);
                }
                canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, paint);
            }
        }
    }

    private static bool Crowded(PointD first, PointD last, PointD previousFirst, PointD previousLast)
    {
        var direction = previousLast - previousFirst;
        double lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
        if (lengthSquared <= 1e-12) return false;
        double Cross(PointD point) => direction.X * (point.Y - previousFirst.Y) - direction.Y * (point.X - previousFirst.X);
        double a = Cross(first), b = Cross(last);
        return Math.Max(a * a, b * b) < 4 * lengthSquared;
    }

    private static bool GridSpan(IReadOnlyList<PointD> polygon, bool vertical, double coordinate, out double start, out double end)
    {
        start = double.PositiveInfinity; end = double.NegativeInfinity;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            double ac = vertical ? a.X : a.Y, bc = vertical ? b.X : b.Y;
            if (coordinate < Math.Min(ac, bc) || coordinate > Math.Max(ac, bc)) continue;
            if (Math.Abs(bc - ac) < 1e-12)
            {
                start = Math.Min(start, Math.Min(vertical ? a.Y : a.X, vertical ? b.Y : b.X));
                end = Math.Max(end, Math.Max(vertical ? a.Y : a.X, vertical ? b.Y : b.X));
            }
            else
            {
                double t = (coordinate - ac) / (bc - ac);
                double value = vertical ? a.Y + t * (b.Y - a.Y) : a.X + t * (b.X - a.X);
                start = Math.Min(start, value); end = Math.Max(end, value);
            }
        }
        return double.IsFinite(start) && double.IsFinite(end) && end > start;
    }
}
