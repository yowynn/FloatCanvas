using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

public sealed record TextCaretStop(int Index, double X);
public sealed record TextEditingLine(double Top, double Height, IReadOnlyList<TextCaretStop> Carets);

/// <summary>UTF-16 caret positions in the same local coordinates and wrapping as rendered text.</summary>
public sealed record TextEditingGeometry(IReadOnlyList<TextEditingLine> Lines)
{
    public RectD Caret(int index)
    {
        var line = Lines.LastOrDefault(line => index >= line.Carets[0].Index) ?? Lines[0];
        var stop = line.Carets.MinBy(stop => Math.Abs(stop.Index-index))!;
        return new(stop.X,line.Top,0,line.Height);
    }
    public int HitTest(PointD point)
    {
        var line=Lines.MinBy(line=>point.Y<line.Top?line.Top-point.Y:Math.Max(0,point.Y-line.Top-line.Height))!;
        return line.Carets.MinBy(stop=>Math.Abs(stop.X-point.X))!.Index;
    }
    public IReadOnlyList<RectD> Selection(int start,int length)
    {
        var rectangles=new List<RectD>();int end=start+length;
        if(length<=0)return rectangles;
        foreach(var line in Lines)
        {
            int first=line.Carets[0].Index,last=line.Carets[^1].Index;
            if(end<=first||start>last)continue;
            double left=line.Carets.MinBy(stop=>Math.Abs(stop.Index-Math.Max(start,first)))!.X;
            double right=line.Carets.MinBy(stop=>Math.Abs(stop.Index-Math.Min(end,last)))!.X;
            rectangles.Add(new(Math.Min(left,right),line.Top,Math.Max(2,Math.Abs(right-left)),line.Height));
        }
        return rectangles;
    }
}
