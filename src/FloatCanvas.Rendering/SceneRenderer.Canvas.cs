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
            double strideCells = stride / cell;
            var candidates = new SortedSet<double>();
            for (int i = 0; i <= (int)(last - first); i++) candidates.Add((first + i) * strideCells);
            // A long inverse-projected far edge must not make clearly separated cells
            // inside the calibration frame disappear through a global candidate stride.
            var projection = document.Canvas.Projection;
            double coverage = vertical ? projection.GridColumns : projection.GridRows;
            int fineCount = (int)Math.Min(256, Math.Ceiling(coverage / 2));
            for (int i = -fineCount; i <= fineCount; i++)
                if (anchor + i * cell >= minimum && anchor + i * cell <= maximum) candidates.Add(i);
            PointD? previousStart = null;
            PointD previousEnd = default;
            foreach (double candidate in candidates)
            {
                GridCandidateCount++;
                double coordinate = anchor + candidate * cell;
                if (!GridSpan(polygon, vertical, coordinate, out double start, out double end)) continue;
                var a = vertical ? new PointD(coordinate, start) : new PointD(start, coordinate);
                var b = vertical ? new PointD(coordinate, end) : new PointD(end, coordinate);
                var screenStart = mapping.ToScreen(a); var screenEnd = mapping.ToScreen(b);
                // Suppress a line only when its whole visible span stays within two screen
                // pixels of the last retained line. Compression at one remote corner must
                // never remove well-separated cells in the reference region.
                if (previousStart is { } previous && Crowded(screenStart, screenEnd, previous, previousEnd)) continue;
                double index = candidate;
                double densityStride = cell;
                for (int level = 0; level < 30 && index != 0 && index % 2 == 0; level++) { densityStride *= 2; index /= 2; }
                if (index != 0 && !TrimDenseSpan(mapping, vertical, coordinate, densityStride, ref screenStart, ref screenEnd)) continue;
                a = mapping.ScreenToPlane.TransformPoint(screenStart); b = mapping.ScreenToPlane.TransformPoint(screenEnd);
                if (!reference && grid.Pattern != StrokePattern.Solid)
                {
                    double period = Math.Max(.1, grid.Width) * (grid.Pattern == StrokePattern.Dotted ? 2.501 : 7);
                    UnresolvedGridDashIntervalCount += a.DistanceTo(b) / period;
                    if (!TrimUnresolvedDashes(mapping, a, b, period, grid.Pattern == StrokePattern.Dashed ? 4 : 2, ref screenStart, ref screenEnd)) continue;
                    a = mapping.ScreenToPlane.TransformPoint(screenStart); b = mapping.ScreenToPlane.TransformPoint(screenEnd);
                    GridDashIntervalCount += a.DistanceTo(b) / period;
                }
                GridLineCount++;
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

    private static bool TrimDenseSpan(CanvasMapping mapping, bool vertical, double coordinate, double stride,
        ref PointD start, ref PointD end)
    {
        const double minimumSpacing = 8;
        var inverse = mapping.ScreenToPlane;
        // A plane-grid line becomes a screen line A*x+B*y+C=0. Distances to both
        // neighboring lines are linear along this line, so clipping needs no sampling.
        for (int sign = -1; sign <= 1; sign += 2)
        {
            double neighbor = coordinate + sign * stride;
            double a = (vertical ? inverse.M11 : inverse.M21) - neighbor * inverse.M31;
            double b = (vertical ? inverse.M12 : inverse.M22) - neighbor * inverse.M32;
            double c = (vertical ? inverse.M13 : inverse.M23) - neighbor * inverse.M33;
            double norm = Math.Sqrt(a * a + b * b);
            if (norm < 1e-12) continue;
            double before = (a * start.X + b * start.Y + c) / norm;
            double after = (a * end.X + b * end.Y + c) / norm;
            double side = Math.Sign(Math.Abs(before) > Math.Abs(after) ? before : after);
            before = before * side - minimumSpacing; after = after * side - minimumSpacing;
            if (before < 0 && after < 0) return false;
            if ((before < 0) != (after < 0))
            {
                var crossing = start + (end - start) * (before / (before - after));
                if (before < 0) start = crossing; else end = crossing;
            }
        }
        return start.DistanceTo(end) > .01;
    }

    private static bool TrimUnresolvedDashes(CanvasMapping mapping, PointD planeStart, PointD planeEnd, double period, double minimumPeriod,
        ref PointD start, ref PointD end)
    {
        var direction = planeEnd - planeStart;
        double length = direction.DistanceTo(default);
        if (length < 1e-12) return false;
        var middle = (start + end) * .5;
        var planeMiddle = mapping.ScreenToPlane.TransformPoint(middle);
        double scale = mapping.PlaneToScreen.Jacobian(planeMiddle).TransformPoint(direction * (1 / length)).DistanceTo(default);
        double depth = mapping.ScreenToPlane.Denominator(middle);
        // Along a projected straight line, local dash scale is proportional to inverse
        // depth squared. Omit only the span whose dash period is below screen resolution;
        // this also bounds Skia's plane-space dash expansion near the horizon.
        double minimumDepth = depth * Math.Sqrt(minimumPeriod / (period * scale));
        double before = mapping.ScreenToPlane.Denominator(start) - minimumDepth;
        double after = mapping.ScreenToPlane.Denominator(end) - minimumDepth;
        if (before < 0 && after < 0) return false;
        if ((before < 0) != (after < 0))
        {
            var crossing = start + (end - start) * (before / (before - after));
            if (before < 0) start = crossing; else end = crossing;
        }
        return start.DistanceTo(end) > .01;
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
