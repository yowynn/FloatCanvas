using FloatCanvas.Core;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Rendering;

public sealed record SymbolTraceResult(SymbolShape Shape, RectD Bounds);

/// <summary>An immutable selection snapshot. Reuse the sampled alpha and the last isoline while editing the preview.</summary>
public sealed class SymbolTraceSource
{
    public const double DefaultOpacity = 50, DefaultDetail = 50;
    private readonly byte[]? alpha;
    private readonly int width, height;
    private readonly SymbolTraceResult? exact;
    private sealed record ContourLevel(int Cutoff, List<PointD[]> Loops);
    private ContourLevel? lastLevel;

    public bool IsRaster => alpha is not null;
    public double FrameWidth => IsRaster ? width : exact?.Bounds.Width ?? 1;
    public double FrameHeight => IsRaster ? height : 1;

    internal SymbolTraceSource(SymbolShape? shape)
    {
        if (shape is not null) exact = new(shape, new(0, 0, shape.AspectRatio, 1));
    }

    internal SymbolTraceSource(byte[] alpha, int width, int height)
    { this.alpha = alpha; this.width = width; this.height = height; }

    public SymbolTraceResult? Trace(double opacityPercent = DefaultOpacity, double detailPercent = DefaultDetail, CancellationToken token = default)
    {
        if (!double.IsFinite(opacityPercent) || opacityPercent is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(opacityPercent));
        if (!double.IsFinite(detailPercent) || detailPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(detailPercent));
        token.ThrowIfCancellationRequested();
        if (alpha is null) return exact;
        int cutoff = (int)Math.Ceiling(opacityPercent * 255 / 100);
        var level = Volatile.Read(ref lastLevel);
        if (level?.Cutoff != cutoff)
        {
            level = new(cutoff, TraceIsolines(cutoff, token));
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref lastLevel, level);
        }
        if (level.Loops.Count == 0) return null;
        double smoothness = 1 - detailPercent / 100;
        double tolerance = .15 + smoothness * smoothness * Math.Max(2, .01 * Math.Max(width, height));
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        int vertices = 0;
        foreach (var loop in level.Loops)
        {
            token.ThrowIfCancellationRequested();
            vertices += SymbolCurveFitter.Append(builder, loop, tolerance, token);
            if (vertices > SymbolShape.MaximumVertices) throw new InvalidDataException(L.Get("Symbol.ExtractComplex"));
        }
        using var path = builder.Detach();
        var box = path.TightBounds;
        var result = SceneRenderer.ReadSymbolPath(path);
        return result is null ? null : new(result, new(box.Left, box.Top, box.Width, box.Height));
    }

    private List<PointD[]> TraceIsolines(int cutoff, CancellationToken token)
    {
        var values = alpha!;
        // Each lattice edge has one ID, shared by its two cells (horizontal even, vertical odd).
        // Orient each segment with ink on its right. Diagonally touching solid pixels remain separate.
        var next = new Dictionary<int, int>();
        for (int y = 0; y < height - 1; y++)
        {
            if ((y & 15) == 0) token.ThrowIfCancellationRequested();
            int i = y * width;
            int leftBits = (values[i] >= cutoff ? 1 : 0) | (values[i + width] >= cutoff ? 8 : 0);
            for (int x = 0; x < width - 1; x++, i++)
            {
                int rightBits = (values[i + 1] >= cutoff ? 2 : 0) | (values[i + width + 1] >= cutoff ? 4 : 0);
                int cell = leftBits | rightBits;
                leftBits = ((rightBits & 2) >> 1) | ((rightBits & 4) << 1);
                if (cell is 0 or 15) continue;
                int top = 2 * i, left = top + 1, right = top + 3, bottom = top + 2 * width;
                switch (cell)
                {
                    case 1: next.Add(top, left); break;
                    case 2: next.Add(right, top); break;
                    case 3: next.Add(right, left); break;
                    case 4: next.Add(bottom, right); break;
                    case 5: next.Add(top, left); next.Add(bottom, right); break;
                    case 6: next.Add(bottom, top); break;
                    case 7: next.Add(bottom, left); break;
                    case 8: next.Add(left, bottom); break;
                    case 9: next.Add(top, bottom); break;
                    case 10: next.Add(right, top); next.Add(left, bottom); break;
                    case 11: next.Add(right, bottom); break;
                    case 12: next.Add(left, right); break;
                    case 13: next.Add(top, right); break;
                    case 14: next.Add(left, top); break;
                }
                if (next.Count > 1_000_000) throw new InvalidDataException(L.Get("Symbol.ExtractComplex"));
            }
        }
        var loops = new List<PointD[]>();
        foreach (int first in next.Keys.ToArray())
        {
            if (!next.ContainsKey(first)) continue;
            var points = new List<PointD>(); int id = first;
            do
            {
                if ((points.Count & 1023) == 0) token.ThrowIfCancellationRequested();
                int pixel = id >> 1; bool vertical = (id & 1) != 0;
                int a = values[pixel], b = values[pixel + (vertical ? width : 1)];
                // A hard 0/255 step is a pixel boundary. For fractional alpha interpolate the isoline.
                double t = Math.Abs(b - a) == 255 ? .5 : (cutoff - .5 - a) / (b - a);
                points.Add(new(pixel % width + .5 + (vertical ? 0 : t), pixel / width + .5 + (vertical ? t : 0)));
                if (!next.Remove(id, out id)) throw new InvalidDataException(L.Get("Symbol.ExtractComplex"));
            } while (id != first);
            if (points.Count >= 3) loops.Add(points.ToArray());
        }
        return loops;
    }
}
