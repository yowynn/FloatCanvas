namespace FloatCanvas.Core;

/// <summary>Shared spatial order for the public plane and screen-facing billboards. Larger depth is farther away.</summary>
public static class CanvasDepthOrder
{
    public static bool IsEnabled(SceneDocument document) => document.Canvas.Projection.Enabled && document.Canvas.DepthPriorityOcclusion;

    public static bool TryAnchorDepth(CanvasMapping mapping, PointD anchor, out double depth)
    {
        depth = mapping.PlaneToScreen.Denominator(anchor);
        return double.IsFinite(depth) && depth > 0 && mapping.IsVisible(anchor);
    }

    public static bool TryPlaneDepth(CanvasMapping mapping, PointD screen, out double depth)
    {
        depth = 0;
        return mapping.TryToPlane(screen, out var plane) && TryAnchorDepth(mapping, plane, out depth);
    }

    public static int CompareAt(SceneDocument document, CanvasMapping mapping, PointD screen, int first, int second)
    {
        if (!IsEnabled(document)) return first.CompareTo(second);
        double Depth(int index)
        {
            var item = document.Elements[index];
            bool valid = item.IsBillboard ? TryAnchorDepth(mapping, item.BillboardAnchor, out double value) : TryPlaneDepth(mapping, screen, out value);
            return valid ? value : double.PositiveInfinity;
        }
        int result = Depth(second).CompareTo(Depth(first));
        return result == 0 ? first.CompareTo(second) : result;
    }

    /// <summary>Original element indices, back to front. The persisted element collection is never reordered.</summary>
    public static int[] BackToFrontAt(SceneDocument document, CanvasMapping mapping, PointD screen)
    {
        var indices = Enumerable.Range(0, document.Elements.Count).ToArray();
        Array.Sort(indices, (first, second) => CompareAt(document, mapping, screen, first, second));
        return indices;
    }

    /// <summary>Convex screen polygon for a plane-depth interval. Infinity denotes the unbounded far end.</summary>
    public static IReadOnlyList<PointD> ClipPlaneDepthRange(CanvasMapping mapping, RectD viewport, double minimumDepth, double maximumDepth)
    {
        List<PointD> polygon = [.. mapping.Safety.ClipScreen(viewport)];
        if (minimumDepth > 0) polygon = Clip(polygon, p => 1 / minimumDepth - mapping.ScreenToPlane.Denominator(p));
        if (double.IsFinite(maximumDepth)) polygon = Clip(polygon, p => mapping.ScreenToPlane.Denominator(p) - 1 / maximumDepth);
        return polygon;
    }

    private static List<PointD> Clip(List<PointD> input, Func<PointD, double> side)
    {
        var output = new List<PointD>();
        if (input.Count == 0) return output;
        var previous = input[^1]; double before = side(previous);
        foreach (var current in input)
        {
            double after = side(current);
            if ((before >= 0) != (after >= 0)) output.Add(previous + (current - previous) * (before / (before - after)));
            if (after >= 0) output.Add(current);
            previous = current; before = after;
        }
        return output;
    }
}
