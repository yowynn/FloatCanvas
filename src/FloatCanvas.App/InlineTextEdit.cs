using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One inline text transaction. WPF owns text input; canvas geometry owns selection and carets.</summary>
internal sealed class InlineTextEdit : IDisposable
{
    internal const double FramePadding = 4;
    private readonly EditorSession session;
    private readonly SceneRenderer renderer;
    private readonly CanvasElement original;
    private readonly bool isNew;
    private readonly long transaction;
    private int anchor;
    internal TextBox Input { get; } = new() { AcceptsReturn=true,AcceptsTab=true,TextWrapping=System.Windows.TextWrapping.Wrap };
    internal CanvasElement Target { get; }
    internal bool IsActive { get; private set; } = true;
    internal bool IsSelecting { get; private set; }
    internal TextEditingGeometry Geometry { get; private set; }
    private readonly BillboardDepthLine? creationDepth;
    private readonly Affine2D creationTransform;
    internal Projective2D LocalToScreen => CanvasMapping.Create(session.Document).ElementToScreen(Target);
    internal event Action? Changed;
    internal event Action? Finished;

    internal InlineTextEdit(EditorSession session,CanvasElement? existing,PointD point,ElementStyle style,SceneRenderer renderer, BillboardDepthLine? billboardDepth = null)
    {
        this.session=session;this.renderer=renderer;isNew=existing is null;
        if(session.IsTransactionActive)throw new InvalidOperationException(L.Get("Text.ActionInProgress"));
        if(existing is not null&&(existing.Kind!=ElementKind.Text||existing.Locked||!session.Document.Elements.Contains(existing)))
            throw new InvalidOperationException(L.Get("Text.CannotEdit"));
        Target=existing??new CanvasElement {Kind=ElementKind.Text,Width=40,Height=style.FontSize*1.35,
            Style=style.Clone(),Transform=Affine2D.Translation(point.X,point.Y)};
        creationDepth = isNew ? billboardDepth : null; creationTransform = Target.Transform;
        PlaceBillboardText();
        original=Target.Clone();Input.Text=Target.Text;
        Geometry=renderer.TextEditingGeometry(Input.Text,Target.Style,Target.Width);
        session.BeginTransaction(L.Get(isNew?"History.AddElement":"History.EditText"));transaction=session.TransactionId;
        if(isNew)session.Document.Elements.Add(Target);
        session.SetSelection([Target.Id]);
        Input.TextChanged+=OnTextChanged;
        Input.SelectionChanged+=(_,_)=>Changed?.Invoke();
        session.Changed+=OnSessionChanged;
        if(!isNew)Input.SelectAll();
        session.NotifyChanged();
    }

    private static string Normalize(string text)=>text.Replace("\r\n","\n").Replace('\r','\n');
    private void OnTextChanged(object sender,TextChangedEventArgs args)
    {
        if(!IsActive)return;
        Target.Text=Normalize(Input.Text);
        if(!isNew&&Target.Text==Normalize(original.Text))
        {Target.Text=original.Text;Target.Width=original.Width;Target.Height=original.Height;Target.Style.FontSize=original.Style.FontSize;}
        else
        {
            if(Target.TextFrameFixed)TextFrameLayout.Fit(Target,original.Style.FontSize,renderer);
            else if(EmojiGlyphs.IsEmoji(Target.Text,Target.Style))
            {var metrics=renderer.MeasureText(Target.Text,Target.Style);Target.Width=metrics.Width;Target.Height=metrics.Height;}
            else
            {
                if(isNew)Target.Width=Math.Clamp(renderer.MeasureText(Target.Text,Target.Style).Width+4,40,360);
                Target.Height=Math.Max(Target.Style.FontSize*1.35,renderer.MeasureText(Target.Text,Target.Style,Target.Width).Height);
            }
        }
        Geometry=renderer.TextEditingGeometry(Input.Text,Target.Style,Target.Width);
        PlaceBillboardText();
        session.NotifyChanged();Changed?.Invoke();
    }
    private void PlaceBillboardText()
    {
        if (creationDepth is not { } depth) return;
        var screen = Projective2D.FromAffine(Affine2D.Scale(depth.Scale, depth.Scale).Compose(creationTransform));
        BillboardGeometry.TryPlaceFromScreen(Target, CanvasMapping.Create(session.Document), depth, screen);
    }
    private void OnSessionChanged(object? sender,EventArgs args)
    {
        if(!IsActive)return;
        if(!session.IsTransactionActive||session.TransactionId!=transaction)
        {Detach();Finished?.Invoke();return;}
        if(!session.Document.Elements.Contains(Target)||Target.Kind!=ElementKind.Text||Target.Locked)
        {Finish(commit:true,selectEdited:false);return;}
        if(!session.Selection.SetEquals([Target.Id]))Finish(commit:true,selectEdited:false);
    }
    internal bool Contains(PointD screen)
    {
        return LocalToScreen.TryInverse(out var inverse)&&inverse.TryTransformPoint(screen,out var point)&&
            new RectD(-FramePadding,-FramePadding,Target.Width+FramePadding*2,Target.Height+FramePadding*2).Contains(point);
    }
    internal void Pointer(PointD screen,bool down,bool up,bool extend=false,bool word=false)
    {
        if(!IsActive||!LocalToScreen.TryInverse(out var inverse)||!inverse.TryTransformPoint(screen,out var local))return;
        if(!down&&!IsSelecting)return;
        int index=Geometry.HitTest(local);
        if(down)
        {
            anchor=extend?(Input.CaretIndex==Input.SelectionStart?Input.SelectionStart+Input.SelectionLength:Input.SelectionStart):index;
            IsSelecting=true;
            if(word)
            {
                var match=System.Text.RegularExpressions.Regex.Matches(Input.Text,@"\w+|[^\w\s]|\s+").Cast<System.Text.RegularExpressions.Match>()
                    .FirstOrDefault(match=>index>=match.Index&&index<match.Index+match.Length);
                if(match is not null)
                {
                    var boundaries=System.Globalization.StringInfo.ParseCombiningCharacters(Input.Text).Append(Input.Text.Length).ToArray();
                    int first=boundaries.Last(value=>value<=match.Index),last=boundaries.First(value=>value>=match.Index+match.Length);
                    Input.Select(first,last-first);anchor=first;IsSelecting=false;Changed?.Invoke();return;
                }
            }
        }
        if(IsSelecting)Input.Select(Math.Min(anchor,index),Math.Abs(index-anchor));
        if(up)IsSelecting=false;
        Changed?.Invoke();
    }
    internal bool Navigate(Key key,bool shift)
    {
        if(key is not (Key.Up or Key.Down or Key.Home or Key.End))return false;
        var caret=Geometry.Caret(Input.CaretIndex);
        int index=Input.CaretIndex;
        if(key is Key.Up or Key.Down)
            index=Geometry.HitTest(new(caret.X,caret.Y+(key==Key.Up?-.5:caret.Height+.5)));
        else
        {
            var line=Geometry.Lines.LastOrDefault(line=>index>=line.Carets[0].Index)??Geometry.Lines[0];
            index=key==Key.Home?line.Carets[0].Index:line.Carets[^1].Index;
        }
        if(!shift) {Input.Select(index,0);anchor=index;}
        else
        {
            if(Input.SelectionLength==0)anchor=Input.CaretIndex;
            Input.Select(Math.Min(anchor,index),Math.Abs(index-anchor));
        }
        return true;
    }
    internal void Finish(bool commit,bool selectEdited=true)
    {
        if(!IsActive)return;
        bool owns=session.IsTransactionActive&&session.TransactionId==transaction;
        Detach();
        if(owns)
        {
            if(!commit||isNew&&string.IsNullOrWhiteSpace(Target.Text))session.Cancel();
            else
            {
                if(Target.Text.Length==0)session.Document.Elements.Remove(Target);
                session.Commit();
                if(selectEdited&&session.Document.Elements.Contains(Target))session.SetSelection([Target.Id]);
            }
        }
        Finished?.Invoke();
    }
    private void Detach(){IsActive=false;IsSelecting=false;session.Changed-=OnSessionChanged;Input.TextChanged-=OnTextChanged;}
    public void Dispose()=>Finish(commit:false);
}
