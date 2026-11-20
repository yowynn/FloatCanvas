using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

/// <summary>A vertex ends a line, or a cubic segment when both controls are present.</summary>
public readonly record struct SymbolVertex(double X, double Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PointD? Control1 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PointD? Control2 = null)
{
    [JsonIgnore] public PointD Point => new(X, Y);
}

/// <summary>Closed contours in a unit frame. Alternate filled regions and holes use the even-odd rule.</summary>
public sealed class SymbolShape
{
    public const int MaximumVertices = 32_768;
    public double AspectRatio { get; init; } = 1;
    public ImmutableArray<ImmutableArray<SymbolVertex>> Contours { get; init; } = [];
    private bool? valid;
    private string? fingerprint;
    private ImmutableArray<ImmutableArray<PointD>> flattened;
    private bool[]? holes;
    private int? vertexCount;
    private readonly Lazy<RectD> inkBounds;

    public SymbolShape() => inkBounds = new(MeasureInk);

    // Bounds are in the authored unit frame. Cubic control points may lie outside the painted curve.
    [JsonIgnore] public RectD InkBounds => inkBounds.Value;

    public PointD SizeAtLongEdge(double edge)
    {
        var box = InkBounds;
        double aspect = AspectRatio * box.Width / box.Height;
        return aspect >= 1 ? new(edge, edge / aspect) : new(edge * aspect, edge);
    }

    public RectD InkBox(double width, double height)
    {
        var b = InkBounds;
        return new(b.X * width, b.Y * height, b.Width * width, b.Height * height);
    }

    private RectD MeasureInk()
    {
        double left = double.PositiveInfinity, top = left, right = double.NegativeInfinity, bottom = right;
        foreach (var contour in Contours)
        {
            var start = contour[0].Point;
            foreach (var v in contour)
            {
                var end = v.Point;
                left = Math.Min(left, end.X); right = Math.Max(right, end.X);
                top = Math.Min(top, end.Y); bottom = Math.Max(bottom, end.Y);
                if (v.Control1 is { } a && v.Control2 is { } b)
                {
                    IncludeExtrema(start.X, a.X, b.X, end.X, ref left, ref right);
                    IncludeExtrema(start.Y, a.Y, b.Y, end.Y, ref top, ref bottom);
                }
                start = end;
            }
        }
        return new(left, top, right - left, bottom - top);
    }

    private static void IncludeExtrema(double p, double q, double r, double s, ref double min, ref double max)
    {
        double a = -p + 3*q - 3*r + s, b = 2*(p - 2*q + r), c = q - p;
        double low = min, high = max, discriminant = b*b - 4*a*c;
        if (Math.Abs(a) < 1e-14)
        {
            if (Math.Abs(b) > 1e-14) Include(-c / b);
        }
        else if (discriminant >= 0)
        {
            double root = Math.Sqrt(discriminant);
            Include((-b - root) / (2*a)); Include((-b + root) / (2*a));
        }
        min = low; max = high;
        void Include(double t)
        {
            if (t <= 0 || t >= 1) return;
            double u = 1-t, value = u*u*u*p + 3*u*u*t*q + 3*u*t*t*r + t*t*t*s;
            low = Math.Min(low, value); high = Math.Max(high, value);
        }
    }

    [JsonIgnore] public int VertexCount => vertexCount ??= Contours.IsDefault ? 0 : Contours.Sum(c => c.IsDefault ? 0 : c.Length);
    [JsonIgnore] public string Fingerprint => fingerprint ??= Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
    public bool IsValid() => valid ??= double.IsFinite(AspectRatio) && AspectRatio > 0 && !Contours.IsDefaultOrEmpty &&
        Contours.Length <= MaximumVertices / 2 && VertexCount <= MaximumVertices &&
        Contours.All(c => !c.IsDefaultOrEmpty && (c.Length >= 3 || c.Length == 2 && c[1].Control1.HasValue) && c[0].Control1 is null &&
            c.All(v => Finite(v.Point) && (v.Control1, v.Control2) switch
            {
                (null, null) => true, ({ } a, { } b) => Finite(a) && Finite(b), _ => false
            })) && InkBounds.Width > 0 && InkBounds.Height > 0;

    private static bool Finite(PointD p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) <= 4 && Math.Abs(p.Y) <= 4;

    public IReadOnlyList<IReadOnlyList<PointD>> Polygons(double width, double height)
    {
        return Flattened().Select(c => (IReadOnlyList<PointD>)c.Select(p => new PointD(p.X * width, p.Y * height)).ToArray()).ToArray();
    }

    private ImmutableArray<ImmutableArray<PointD>> Flattened() =>
        flattened.IsDefault ? flattened = Contours.Select(Flatten).ToImmutableArray() : flattened;

    public bool IsHole(int index)
    {
        if (Contours.Length == 1) return false;
        if (holes is null)
        {
            var loops = Flattened();
            var bounds = loops.Select(p => (Left: p.Min(v => v.X), Top: p.Min(v => v.Y), Right: p.Max(v => v.X), Bottom: p.Max(v => v.Y))).ToArray();
            var result = new bool[loops.Length];
            for (int i = 0; i < loops.Length; i++)
            {
                var sample = loops[i][0];
                for (int j = 0; j < loops.Length; j++)
                {
                    var b = bounds[j];
                    if (i != j && sample.X >= b.Left && sample.X <= b.Right && sample.Y >= b.Top && sample.Y <= b.Bottom &&
                        ElementGeometry.PointInPolygon(sample, loops[j])) result[i] = !result[i];
                }
            }
            holes = result;
        }
        return holes[index];
    }

    private static ImmutableArray<PointD> Flatten(ImmutableArray<SymbolVertex> contour)
    {
        var points = ImmutableArray.CreateBuilder<PointD>();
        points.Add(contour[0].Point);
        foreach (var vertex in contour.AsSpan()[1..])
        {
            if (vertex.Control1 is { } a && vertex.Control2 is { } b)
                Cubic(points[^1], a, b, vertex.Point, 0);
            else points.Add(vertex.Point);
        }
        if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
        return points.ToImmutable();

        void Cubic(PointD start, PointD a, PointD b, PointD end, int depth)
        {
            if (depth >= 9 || Math.Max(ElementGeometry.DistanceToSegment(a, start, end), ElementGeometry.DistanceToSegment(b, start, end)) < .00025)
            { points.Add(end); return; }
            var ab = (start + a) * .5; var bc = (a + b) * .5; var cd = (b + end) * .5;
            var left = (ab + bc) * .5; var right = (bc + cd) * .5; var mid = (left + right) * .5;
            Cubic(start, ab, left, mid, depth + 1); Cubic(mid, right, cd, end, depth + 1);
        }
    }
}
