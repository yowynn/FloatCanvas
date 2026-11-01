namespace FloatCanvas.Core;

public enum PresentationBakeFailure { None, InvalidSource, NoVisibleTrail, CapacityExceeded, UnsafeRegion, UnrepresentableDepth, UnsafePlacement }

/// <summary>An owned candidate. Callers receive clones, never the mutable prepared entities.</summary>
public sealed class PresentationBakeCandidate
{
    private readonly CanvasElement[] strokes;
    internal PresentationBakeCandidate(IEnumerable<CanvasElement> strokes)
    {
        this.strokes = strokes.ToArray(); PointCount = this.strokes.Sum(stroke => stroke.Points.Count);
    }
    public int PointCount { get; }
    public int SegmentCount => strokes.Length;
    public IReadOnlyList<CanvasElement> CloneWorldStrokes() => Array.AsReadOnly(strokes.Select(stroke => stroke.Clone()).ToArray());
}

/// <summary>Prepares sampled centerlines and configured width, not fade animation or an exact raster mask. Never mutates a scene.</summary>
public static class PresentationTrailBake
{
    public static bool TryPrepare(CanvasElement worldSource, CanvasMapping mapping, RectD viewport, PresentationFrame frame, PointD screenOrigin,
        out PresentationBakeCandidate? candidate, out PresentationBakeFailure failure)
    {
        candidate = null; failure = PresentationBakeFailure.InvalidSource;
        if (worldSource is null || worldSource.Kind != ElementKind.Presentation || worldSource.Presentation?.IsValid() != true ||
            worldSource.Style is null || frame is null || !double.IsFinite(screenOrigin.X) || !double.IsFinite(screenOrigin.Y)) return false;
        var settings = worldSource.Presentation;
        failure = PresentationBakeFailure.NoVisibleTrail;
        if (!settings.Enabled || (settings.Modules & PresentationModules.Trail) == 0 || !worldSource.Visible || worldSource.Style.Opacity <= 0 || frame.Trail.Count == 0) return false;
        failure = PresentationBakeFailure.UnsafeRegion;
        if (!PresentationGeometry.TryScreenRegion(worldSource, mapping, viewport, out var region)) return false;
        BillboardDepthLine? depth = null;
        if (worldSource.IsBillboard)
        {
            failure = PresentationBakeFailure.UnrepresentableDepth;
            if (!mapping.PlaneToScreen.TryTransformPoint(worldSource.BillboardAnchor, out var ground) ||
                !mapping.TryGetEqualDepthLine(ground, out var line)) return false;
            depth = line;
        }
        var paths = new List<List<PointD>>(); List<PointD>? current = null;
        PointD? previous = null; TimeSpan previousTime = TimeSpan.Zero;
        long cutoff = frame.Time.Ticks - TimeSpan.FromMilliseconds(settings.TrailMilliseconds).Ticks;
        int pointCount = 0;
        bool Append(PointD point)
        {
            if (current is null)
            {
                if (paths.Count >= PresentationLimits.MaximumBakeSegments) return false;
                paths.Add(current = []);
            }
            if (current.Count != 0 && current[^1].DistanceTo(point) <= 1e-8) return true;
            if (pointCount >= PresentationLimits.MaximumBakePoints) return false;
            current.Add(point); pointCount++; return true;
        }
        foreach (var sample in frame.Trail)
        {
            var point = sample.ScreenPoint - screenOrigin;
            if (sample.Timestamp.Ticks < cutoff) { previous = point; previousTime = sample.Timestamp; current = null; continue; }
            if (sample.StartsSegment || previous is null || sample.Timestamp < previousTime)
            {
                current = null;
                if (PresentationGeometry.TryClipSegment(point, point, region, out _, out _) && !Append(point))
                { failure = PresentationBakeFailure.CapacityExceeded; return false; }
            }
            else
            {
                var segmentStart = previous.Value;
                if (previousTime.Ticks < cutoff)
                    segmentStart += (point - segmentStart) * ((double)(cutoff - previousTime.Ticks) / (sample.Timestamp.Ticks - previousTime.Ticks));
                if (PresentationGeometry.TryClipSegment(segmentStart, point, region, out var first, out var last))
                {
                    if (current is not null && current[^1].DistanceTo(first) > 1e-8) current = null;
                    if (!Append(first) || !Append(last)) { failure = PresentationBakeFailure.CapacityExceeded; return false; }
                    if (last.DistanceTo(point) > 1e-8) current = null;
                }
                else current = null;
            }
            previous = point; previousTime = sample.Timestamp;
        }
        failure = PresentationBakeFailure.NoVisibleTrail;
        if (paths.Count == 0) return false;
        var result = new List<CanvasElement>(paths.Count);
        foreach (var path in paths)
        {
            var origin = new PointD(path.Min(point => point.X), path.Min(point => point.Y));
            var stroke = new CanvasElement
            {
                Kind = ElementKind.Freehand, Points = new(path.Select(point => point - origin)), ConstrainAspect = false,
                Style = worldSource.Style.Clone()
            };
            stroke.Style.Variant = ElementAppearance.Default; stroke.Style.StrokeWidth = settings.TrailWidthPixels; stroke.Style.StrokePattern = StrokePattern.Solid;
            var screen = Projective2D.FromAffine(Affine2D.Translation(origin.X, origin.Y));
            if (depth is { } line)
            {
                failure = PresentationBakeFailure.UnrepresentableDepth;
                var center = screen.TransformPoint(ElementGeometry.LocalBounds(stroke).Center);
                if (!line.TryVerticalIntersection(center.X, out var ground) ||
                    !BillboardGeometry.TryResolveScreenPlacement(stroke, mapping, screen, ground, out var placement, out _)) return false;
                Apply(stroke, placement);
            }
            else if (mapping.ScreenToPlane.M31 == 0 && mapping.ScreenToPlane.M32 == 0 && mapping.ScreenToPlane.M33 != 0)
            {
                var plane = mapping.ScreenToPlane.Compose(screen); double w = plane.M33;
                stroke.Transform = new(plane.M11 / w, plane.M12 / w, plane.M21 / w, plane.M22 / w, plane.M13 / w, plane.M23 / w);
            }
            else
            {
                failure = PresentationBakeFailure.UnsafePlacement;
                if (!BillboardGeometry.TryResolveScreenBottom(stroke, mapping, screen, out var placement, out _)) return false;
                Apply(stroke, placement);
            }
            failure = PresentationBakeFailure.UnsafePlacement;
            if (!ElementGeometry.LocalToPlane(stroke).TryTransformBounds(ElementGeometry.StrokedLocalBounds(stroke), out _)) return false;
            result.Add(stroke);
        }
        candidate = new(result); failure = PresentationBakeFailure.None; return true;
    }
    private static void Apply(CanvasElement stroke, BillboardPlacement placement)
    {
        stroke.IsBillboard = true; stroke.BillboardAnchor = placement.Anchor; stroke.BillboardHeight = placement.Height; stroke.Transform = placement.Linear;
    }
}
