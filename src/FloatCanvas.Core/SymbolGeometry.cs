namespace FloatCanvas.Core;

/// <summary>Closed vector contours shared by hit testing and rendering.</summary>
public static class SymbolGeometry
{
    public static IReadOnlyList<PointD> Outline(SymbolKind kind, double width, double height)
    {
        PointD Scale(PointD p) => new(p.X * width, p.Y * height);
        IEnumerable<PointD> points = kind switch
        {
            SymbolKind.Triangle => [new(.5, (1 - Math.Sqrt(3) / 2) / 2), new(1, (1 + Math.Sqrt(3) / 2) / 2), new(0, (1 + Math.Sqrt(3) / 2) / 2)],
            SymbolKind.Star => Enumerable.Range(0, 10).Select(i => Polar(-Math.PI / 2 + i * Math.PI / 5, i % 2 == 0 ? .5 : .2)),
            SymbolKind.Heart => Enumerable.Range(0, 96).Select(i =>
            {
                double a = i * Math.PI * 2 / 96;
                return new PointD(.5 + Math.Pow(Math.Sin(a), 3) / 2,
                    (13 - (13 * Math.Cos(a) - 5 * Math.Cos(2 * a) - 2 * Math.Cos(3 * a) - Math.Cos(4 * a))) / 30);
            }),
            SymbolKind.Check => [new(.03, .48), new(.2, .34), new(.43, .6), new(.84, .06), new(1, .2), new(.45, .94)],
            SymbolKind.Cross => [new(.14, 0), new(.5, .36), new(.86, 0), new(1, .14), new(.64, .5), new(1, .86), new(.86, 1), new(.5, .64), new(.14, 1), new(0, .86), new(.36, .5), new(0, .14)],
            SymbolKind.Diamond => [new(.5, 0), new(1, .5), new(.5, 1), new(0, .5)],
            SymbolKind.Pentagon => Enumerable.Range(0, 5).Select(i => Polar(-Math.PI / 2 + i * Math.PI * 2 / 5, .5)),
            SymbolKind.Hexagon => Enumerable.Range(0, 6).Select(i => Polar(i * Math.PI / 3, .5)),
            SymbolKind.SpeechBubble => [new(.05, .05), new(.95, .05), new(.95, .73), new(.52, .73), new(.23, 1), new(.28, .73), new(.05, .73)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return points.Select(Scale).ToArray();
    }

    private static PointD Polar(double angle, double radius) => new(.5 + Math.Cos(angle) * radius, .5 + Math.Sin(angle) * radius);
}
