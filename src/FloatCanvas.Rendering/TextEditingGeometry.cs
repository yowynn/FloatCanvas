using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

public sealed record TextCaretStop(int Index, double X);
public sealed record TextCaretPosition(int Index, double X, double Top);
public sealed record TextVisualSpan(int Start, int End, double Left, double Right, bool RightToLeft = false);
public sealed record TextEditingLine(double Top, double Height, IReadOnlyList<TextCaretStop> Carets, IReadOnlyList<TextVisualSpan>? Spans = null);

/// <summary>UTF-16 caret positions in the same local coordinates and wrapping as rendered text.</summary>
public sealed record TextEditingGeometry(IReadOnlyList<TextEditingLine> Lines)
{
    public TextCaretPosition CollapseSelection(int start, int length, int direction)
    {
        var selected = Lines.SelectMany(line => (line.Spans ?? []).Where(span => span.Start < start + length && span.End > start)
            .Select(span => (Line: line, Span: span))).OrderBy(value => value.Line.Top).ThenBy(value => value.Span.Left).ToArray();
        if (selected.Length == 0) return new(start, Caret(start).X, Caret(start).Y);
        var edge = direction < 0 ? selected[0] : selected[^1];
        int index = (direction < 0) == edge.Span.RightToLeft ? edge.Span.End : edge.Span.Start;
        return new(index, direction < 0 ? edge.Span.Left : edge.Span.Right, edge.Line.Top);
    }
    public int MoveHorizontal(int index, int direction) => MoveHorizontalPosition(index, direction).Index;
    public TextCaretPosition MoveHorizontalPosition(int index, int direction, PointD? affinity = null)
    {
        var caret = Caret(index, affinity);
        var line = Lines.MinBy(line => Math.Abs(line.Top - caret.Y))!;
        double x = caret.X;
        var next = line.Carets.Where(stop => direction < 0 ? stop.X < x - .001 : stop.X > x + .001)
            .OrderBy(stop => Math.Abs(stop.X - x)).ThenBy(stop => Math.Abs(stop.Index - index)).FirstOrDefault();
        if (next is not null) return new(next.Index, next.X, line.Top);
        int lineIndex = Lines.ToList().IndexOf(line) + Math.Sign(direction);
        if (lineIndex < 0 || lineIndex >= Lines.Count) return new(index, x, line.Top);
        var adjacent = (direction < 0 ? Lines[lineIndex].Carets.MaxBy(stop => stop.X) : Lines[lineIndex].Carets.MinBy(stop => stop.X))!;
        return new(adjacent.Index, adjacent.X, Lines[lineIndex].Top);
    }
    public RectD Caret(int index, PointD? affinity = null)
    {
        var line = Lines.LastOrDefault(line => index >= line.Carets[0].Index) ?? Lines[0];
        if (affinity is { } point)
            line = Lines.Where(candidate => candidate.Carets.Any(stop => stop.Index == index)).MinBy(candidate => Math.Abs(candidate.Top - point.Y)) ?? line;
        var stop = line.Carets.OrderBy(stop => Math.Abs(stop.Index-index)).ThenBy(stop => affinity is { } point ? Math.Abs(stop.X - point.X) : 0).First();
        return new(stop.X,line.Top,0,line.Height);
    }
    public int HitTest(PointD point) => HitTestPosition(point).Index;
    public TextCaretPosition HitTestPosition(PointD point)
    {
        var line=Lines.MinBy(line=>point.Y<line.Top?line.Top-point.Y:Math.Max(0,point.Y-line.Top-line.Height))!;
        var stop = line.Carets.MinBy(stop=>Math.Abs(stop.X-point.X))!;
        return new(stop.Index, stop.X, line.Top);
    }
    public IReadOnlyList<RectD> Selection(int start,int length)
    {
        var rectangles=new List<RectD>();int end=start+length;
        if(length<=0)return rectangles;
        foreach(var line in Lines)
        {
            if (line.Spans is { } spans)
            {
                foreach (var span in spans.Where(span => span.Start < end && span.End > start).OrderBy(span => span.Left))
                {
                    if (rectangles.Count > 0 && rectangles[^1] is { } previous && previous.Y == line.Top && Math.Abs(previous.Right - span.Left) < .1)
                        rectangles[^1] = new(previous.X, previous.Y, Math.Max(2, span.Right - previous.X), previous.Height);
                    else rectangles.Add(new(span.Left, line.Top, Math.Max(2, span.Right - span.Left), line.Height));
                }
                continue;
            }
            int first=line.Carets[0].Index,last=line.Carets[^1].Index;
            if(end<=first||start>last)continue;
            double left=line.Carets.MinBy(stop=>Math.Abs(stop.Index-Math.Max(start,first)))!.X;
            double right=line.Carets.MinBy(stop=>Math.Abs(stop.Index-Math.Min(end,last)))!.X;
            rectangles.Add(new(Math.Min(left,right),line.Top,Math.Max(2,Math.Abs(right-left)),line.Height));
        }
        return rectangles;
    }
}
