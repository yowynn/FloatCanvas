namespace FloatCanvas.Core;

public readonly record struct CanvasReferenceLine(PointD Start, PointD End);
public enum CanvasLineFitError { None, MissingLines, ShortLine, IndistinctDirections, InvalidQuadrilateral }
public static class CanvasLineCalibration
{
    public static bool TryGetQuad(IReadOnlyList<CanvasReferenceLine> lines, double canvasUnitsPerDip,
        out PointD[] quad, out CanvasLineFitError error)
    {
        quad = []; error = CanvasLineFitError.MissingLines;
        if (lines.Count != 4) return false;
        error = CanvasLineFitError.ShortLine;
        if (!double.IsFinite(canvasUnitsPerDip) || canvasUnitsPerDip <= 0 || lines.Any(l =>
            !double.IsFinite(l.Start.X + l.Start.Y + l.End.X + l.End.Y) || l.Start.DistanceTo(l.End) / canvasUnitsPerDip < 8)) return false;
        PointD[] intersections = new PointD[4];
        (int H, int V)[] pairs = [(0, 2), (0, 3), (1, 3), (1, 2)];
        error = CanvasLineFitError.IndistinctDirections;
        for (int i = 0; i < 4; i++)
        {
            var a = lines[pairs[i].H]; var b = lines[pairs[i].V];
            var d = a.End - a.Start; var e = b.End - b.Start; var offset = b.Start - a.Start;
            double cross = d.X * e.Y - d.Y * e.X;
            if (Math.Abs(cross) / (a.Start.DistanceTo(a.End) * b.Start.DistanceTo(b.End)) < .01) return false;
            intersections[i] = a.Start + d * ((offset.X * e.Y - offset.Y * e.X) / cross);
        }
        error = CanvasLineFitError.InvalidQuadrilateral;
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = intersections[(i + 1) % 4] - intersections[i]; var b = intersections[(i + 2) % 4] - intersections[(i + 1) % 4];
            double cross = a.X * b.Y - a.Y * b.X;
            if (!double.IsFinite(cross) || Math.Abs(cross) < 1e-6 || sign != 0 && Math.Sign(cross) != sign) return false;
            sign = Math.Sign(cross);
        }
        if (!CanvasCalibration.TryOrderCorners(intersections, out quad)) return false;
        error = CanvasLineFitError.None; return true;
    }
}
