using System.Collections.Immutable;

namespace FloatCanvas.Core;

/// <summary>Closed vector contours shared by hit testing and rendering.</summary>
public static class SymbolGeometry
{
    public static IReadOnlyList<SymbolKind> BuiltIns { get; } = Array.AsReadOnly(Enum.GetValues<SymbolKind>().Where(k => k != SymbolKind.Custom).ToArray());
    private static readonly IReadOnlyDictionary<SymbolKind, SymbolShape> shapes = BuiltIns.ToDictionary(k => k, Build);
    public static SymbolShape Shape(SymbolKind kind, SymbolShape? custom = null) => kind == SymbolKind.Custom
        ? custom ?? throw new ArgumentException("A custom symbol requires a saved outline.", nameof(custom)) : shapes[kind];

    public static IReadOnlyList<PointD> Outline(SymbolKind kind, double width, double height) => Shape(kind).Polygons(width, height)[0];

    public static bool IsValid(SymbolKind kind, SymbolShape? custom) =>
        Enum.IsDefined(kind) && (custom is null ? kind != SymbolKind.Custom : custom.IsValid());

    // Keep authored paths and saved scene coordinates intact. Size and placement are measured
    // at their visible bounds, including asymmetric shapes whose ink is off-center in the design frame.
    public static void PlaceInBox(CanvasElement element, RectD box)
    {
        var origin = FitToBox(element, box);
        element.Transform = Affine2D.Translation(origin.X, origin.Y);
    }

    public static PointD FitToBox(CanvasElement element, RectD box)
    {
        var ink = Shape(element.Symbol, element.CustomSymbol).InkBounds;
        element.Width = box.Width / ink.Width; element.Height = box.Height / ink.Height;
        return new(box.X - ink.X * element.Width, box.Y - ink.Y * element.Height);
    }

    private static SymbolShape Build(SymbolKind kind)
    {
        static ImmutableArray<SymbolVertex> Polygon(params PointD[] points) => points.Select(p => new SymbolVertex(p.X, p.Y)).ToImmutableArray();
        static ImmutableArray<SymbolVertex> Circle(double x, double y, double r)
        {
            double k = r * .5522847498307936;
            return [new(x+r,y), new(x,y+r,new(x+r,y+k),new(x+k,y+r)), new(x-r,y,new(x-k,y+r),new(x-r,y+k)),
                new(x,y-r,new(x-r,y-k),new(x-k,y-r)), new(x+r,y,new(x+k,y-r),new(x+r,y-k))];
        }
        if (kind == SymbolKind.Ring) return new() { Contours = [Circle(.5,.5,.48), Circle(.5,.5,.29)] };
        if (kind == SymbolKind.LocationPin) return new() { Contours = [
            [new(.5,1), new(.11,.38,new(.42,.89),new(.11,.58)), new(.89,.38,new(.11,-.12),new(.89,-.12)),
                new(.5,1,new(.89,.58),new(.58,.89))], Circle(.5,.35,.145)] };
        if (kind == SymbolKind.Exclamation) return new() { Contours = [
            Polygon(new(.37,.04), new(.63,.04), new(.58,.68), new(.42,.68)), Circle(.5,.86,.11)] };
        if (kind == SymbolKind.Question) return new() { Contours = [
            [new(.18,.29), new(.49,.03,new(.18,.14),new(.32,.03)), new(.82,.29,new(.68,.03),new(.82,.12)),
                new(.63,.55,new(.82,.43),new(.72,.5)), new(.57,.67,new(.58,.58),new(.57,.61)), new(.40,.67),
                new(.52,.43,new(.40,.53),new(.45,.48)), new(.63,.29,new(.59,.39),new(.63,.35)),
                new(.49,.19,new(.63,.23),new(.58,.19)), new(.35,.32,new(.40,.19),new(.36,.24))], Circle(.49,.86,.105)] };
        if (kind == SymbolKind.DoubleChevron) return new() { Contours = [
            Polygon(new(0,.12),new(.23,.12),new(.50,.50),new(.23,.88),new(0,.88),new(.27,.50)),
            Polygon(new(.50,.12),new(.73,.12),new(1,.50),new(.73,.88),new(.50,.88),new(.77,.50))] };
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
            SymbolKind.Plus => [new(.38,.04),new(.62,.04),new(.62,.38),new(.96,.38),new(.96,.62),new(.62,.62),new(.62,.96),new(.38,.96),new(.38,.62),new(.04,.62),new(.04,.38),new(.38,.38)],
            SymbolKind.Minus => [new(.04,.38),new(.96,.38),new(.96,.62),new(.04,.62)],
            SymbolKind.Arrow => [new(.02,.34),new(.56,.34),new(.56,.05),new(.99,.50),new(.56,.95),new(.56,.66),new(.02,.66)],
            SymbolKind.Lightning => [new(.58,.01),new(.18,.56),new(.45,.56),new(.32,.99),new(.85,.37),new(.57,.37),new(.70,.01)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new() { Contours = [Polygon(points.ToArray())] };
    }

    private static PointD Polar(double angle, double radius) => new(.5 + Math.Cos(angle) * radius, .5 + Math.Sin(angle) * radius);
}
