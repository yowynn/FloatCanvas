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
    private readonly Func<CanvasMapping> canvasMapping;
    private readonly CanvasElement original;
    private readonly bool isNew;
    private readonly bool autoWidth;
    private readonly long transaction;
    private int anchor;
    private PointD? caretAffinity;
    private int affinityIndex = -1;
    private bool movingCaret;
    private double? verticalCaretX;
    private int ActiveCaretIndex => Input.SelectionLength > 0 && affinityIndex >= Input.SelectionStart && affinityIndex <= Input.SelectionStart + Input.SelectionLength ? affinityIndex : Input.CaretIndex;
    internal RectD Caret => Geometry.Caret(ActiveCaretIndex, ActiveCaretIndex == affinityIndex ? caretAffinity : null);
    internal TextBox Input { get; } = new() { AcceptsReturn=true,AcceptsTab=true,TextWrapping=System.Windows.TextWrapping.Wrap };
    internal CanvasElement Target { get; }
    internal bool IsActive { get; private set; } = true;
    internal bool IsSelecting { get; private set; }
    internal TextEditingGeometry Geometry { get; private set; }
    private readonly BillboardDepthLine? creationDepth;
    private readonly bool screenFirst;
    private readonly SceneDocument ownerDocument;
    private readonly long scopeVersion;
    private readonly long creationPreviewRevision;
    private readonly Projective2D creationProjection;
    private readonly Affine2D creationTransform;
    private readonly LayerPlacement parentPlacement;
    private readonly bool parentVisible, parentLocked;
    internal CanvasElement WorldTarget
    {
        get
        {
            var world = Target.Clone();
            if (screenFirst) return world;
            SceneGraph.ApplyPlacement(world, parentPlacement);
            world.Visible &= parentVisible; world.Locked |= parentLocked; world.GroupId = null; return world;
        }
    }
    private CanvasMapping CurrentCanvasMapping => canvasMapping();
    internal Projective2D LocalToScreen => screenFirst
        ? TryResolveScreenDraft(out var candidate) ? CurrentCanvasMapping.ElementToScreen(candidate) : ElementGeometry.LocalToPlane(Target)
        : CurrentCanvasMapping.ElementToScreen(WorldTarget);
    internal CanvasElement? ScreenDraft => screenFirst && IsActive ? Target.Clone() : null;
    internal bool HasScreenDraft => screenFirst && IsActive;
    internal CanvasElement? ResolvedScreenDraft => screenFirst && TryResolveScreenDraft(out var candidate) ? candidate : null;
    internal event Action? Changed;
    internal event Action? Finished;
    internal event Action? PlacementRejected;

    internal InlineTextEdit(EditorSession session,CanvasElement? existing,PointD point,ElementStyle style,SceneRenderer renderer, BillboardDepthLine? billboardDepth = null,
        bool billboardScreenFirst = false, Func<CanvasMapping>? canvasMapping = null)
    {
        this.session=session;this.renderer=renderer;isNew=existing is null;
        this.canvasMapping = canvasMapping ?? (() => CanvasMapping.Create(session.Document, session.ActiveRootCanvas));
        screenFirst = isNew && billboardScreenFirst;
        ownerDocument = session.Document; scopeVersion = session.EditScopeVersion; creationPreviewRevision = session.PreviewRevision;
        creationProjection = CurrentCanvasMapping.PlaneToScreen;
        if(session.IsTransactionActive)throw new InvalidOperationException(L.Get("Text.ActionInProgress"));
        var graph = new SceneGraph(session.Document);
        if(existing is not null&&(existing.Kind!=ElementKind.Text||!session.Document.Elements.Contains(existing)||graph.IsLocked(existing.Id)))
            throw new InvalidOperationException(L.Get("Text.CannotEdit"));
        if (isNew && session.EditScopeId is { } scope && graph.IsLocked(scope)) throw new InvalidOperationException(L.Get("Text.CannotEdit"));
        var draft=existing??new CanvasElement {Kind=ElementKind.Text,
            Style=style.Clone(),Transform=Affine2D.Translation(point.X,point.Y)};
        if (isNew) TextFrameLayout.Reflow(draft, autoWidth: true, renderer);
        creationDepth = isNew ? billboardDepth : null; creationTransform = draft.Transform;
        if (creationDepth is { } depth)
            BillboardGeometry.TryPlaceFromScreen(draft, CurrentCanvasMapping, depth,
                Projective2D.FromAffine(Affine2D.Scale(depth.Scale, depth.Scale).Compose(creationTransform)));
        session.BeginTransaction(L.Get(isNew?"History.AddElement":"History.EditText"));transaction=session.TransactionId;
        Target = isNew && !screenFirst ? graph.InsertElement(draft, session.EditScopeId, rootCanvas: session.ActiveRootCanvas) : draft;
        parentPlacement = graph.ScopeWorldPlacement(screenFirst ? session.EditScopeId : Target.GroupId);
        parentVisible = Target.GroupId is not { } parent || graph.IsVisible(parent);
        parentLocked = Target.GroupId is { } owner && graph.IsLocked(owner);
        original=Target.Clone();Input.Text=Target.Text;
        autoWidth=TextFrameLayout.UsesAutoWidth(original, renderer);
        Geometry=renderer.TextEditingGeometry(Input.Text,Target.Style,Target.Width);
        session.SetSelection(screenFirst ? [] : [Target.Id]);
        Input.TextChanged+=OnTextChanged;
        Input.SelectionChanged+=(_,_)=> { if (!movingCaret) { caretAffinity=null; affinityIndex=-1; verticalCaretX=null; } Changed?.Invoke(); };
        session.Changed+=OnSessionChanged;
        if(!isNew)Input.SelectAll();
        if (!screenFirst) session.NotifyChanged();
    }

    private static string Normalize(string text)=>text.Replace("\r\n","\n").Replace('\r','\n');
    private void OnTextChanged(object sender,TextChangedEventArgs args)
    {
        if(!IsActive)return;
        caretAffinity=null;affinityIndex=-1;verticalCaretX=null;
        Target.Text=Normalize(Input.Text);
        if(!isNew&&Target.Text==Normalize(original.Text))
        {
            Target.Text=original.Text;Target.Width=original.Width;Target.Height=original.Height;
            Target.Style.FontSize=original.Style.FontSize;Target.TextAutoWidth=original.TextAutoWidth;
        }
        else if(Target.TextFrameFixed) TextFrameLayout.Fit(Target,original.Style.FontSize,renderer);
        else TextFrameLayout.Reflow(Target, autoWidth, renderer);
        Geometry=renderer.TextEditingGeometry(Input.Text,Target.Style,Target.Width);
        PlaceBillboardText();
        if (!screenFirst) session.NotifyChanged();
        Changed?.Invoke();
    }
    private bool TryResolveScreenDraft(out CanvasElement candidate)
    {
        candidate = Target.Clone();
        if (!ReferenceEquals(ownerDocument, session.Document) || session.EditScopeVersion != scopeVersion || session.PreviewRevision != creationPreviewRevision ||
            creationProjection != CurrentCanvasMapping.PlaneToScreen || !session.CanInsertInEditScope ||
            !BillboardCreationGeometry.TryPlaceLogical(Target, CurrentCanvasMapping, default,
                creationTransform.TransformPoint(default), out candidate, out _)) return false;
        return true;
    }
    private void PlaceBillboardText()
    {
        if (creationDepth is not { } depth) return;
        var screen = Projective2D.FromAffine(Affine2D.Scale(depth.Scale, depth.Scale).Compose(creationTransform));
        var world = WorldTarget;
        if (!BillboardGeometry.TryPlaceFromScreen(world, CurrentCanvasMapping, depth, screen)) return;
        SceneGraph.ApplyPlacement(world, parentPlacement.Inverse());
        Target.Transform = world.Transform; Target.ProjectivePlacement = world.ProjectivePlacement;
        Target.IsBillboard = world.IsBillboard; Target.BillboardAnchor = world.BillboardAnchor; Target.BillboardHeight = world.BillboardHeight;
    }
    private void OnSessionChanged(object? sender,EventArgs args)
    {
        if(!IsActive)return;
        if(!session.IsTransactionActive||session.TransactionId!=transaction)
        {Detach();Finished?.Invoke();return;}
        if (screenFirst)
        {
            if (!ReferenceEquals(ownerDocument, session.Document) || scopeVersion != session.EditScopeVersion || creationPreviewRevision != session.PreviewRevision ||
                creationProjection != CurrentCanvasMapping.PlaneToScreen || !session.CanInsertInEditScope)
                Finish(commit: false);
            return;
        }
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
        verticalCaretX=null;
        var position=Geometry.HitTestPosition(local);
        int index=position.Index;
        if(down)
        {
            anchor=extend?(Input.CaretIndex==Input.SelectionStart?Input.SelectionStart+Input.SelectionLength:Input.SelectionStart):index;
            IsSelecting=true;
            if(word)
            {
                int character = Geometry.HitTestCharacter(local);
                var match=System.Text.RegularExpressions.Regex.Matches(Input.Text,@"\w+|[^\w\s]|\s+").Cast<System.Text.RegularExpressions.Match>()
                    .FirstOrDefault(match=>character>=match.Index&&character<match.Index+match.Length);
                if(match is not null)
                {
                    var boundaries=System.Globalization.StringInfo.ParseCombiningCharacters(Input.Text).Append(Input.Text.Length).ToArray();
                    int first=boundaries.Last(value=>value<=match.Index),last=boundaries.First(value=>value>=match.Index+match.Length);
                    Input.Select(first,last-first);anchor=first;IsSelecting=false;Changed?.Invoke();return;
                }
            }
        }
        if(IsSelecting)Select(Math.Min(anchor,index),Math.Abs(index-anchor));
        RememberCaret(position);
        if(up)IsSelecting=false;
        Changed?.Invoke();
    }
    internal bool Navigate(Key key,bool shift)
    {
        if(key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))return false;
        var caret=Caret;
        if (key is not (Key.Up or Key.Down)) verticalCaretX=null;
        int index=ActiveCaretIndex;
        TextCaretPosition position;
        if(key is Key.Left or Key.Right && !shift && Input.SelectionLength > 0)
            position=Geometry.CollapseSelection(Input.SelectionStart,Input.SelectionLength,key==Key.Left?-1:1);
        else if(key is Key.Left or Key.Right) position=Geometry.MoveHorizontalPosition(index,key==Key.Left?-1:1,new(caret.X,caret.Y));
        else if(key is Key.Up or Key.Down)
            position=Geometry.HitTestPosition(new(verticalCaretX ??= caret.X,caret.Y+(key==Key.Up?-.5:caret.Height+.5)));
        else
        {
            var line=Geometry.Lines.MinBy(line=>Math.Abs(line.Top-caret.Y))!;
            var stop=(key==Key.Home?line.Carets.MinBy(stop=>stop.X):line.Carets.MaxBy(stop=>stop.X))!;
            position=new(stop.Index,stop.X,line.Top);
        }
        index=position.Index;
        if(!shift) {Select(index,0);anchor=index;}
        else
        {
            if(Input.SelectionLength==0)anchor=ActiveCaretIndex;
            Select(Math.Min(anchor,index),Math.Abs(index-anchor));
        }
        RememberCaret(position);Changed?.Invoke();
        return true;
    }
    private void RememberCaret(TextCaretPosition position)
    { affinityIndex=position.Index;caretAffinity=new(position.X,position.Top); }
    private void Select(int start,int length)
    { movingCaret=true;try {Input.Select(start,length);}finally {movingCaret=false;} }
    internal void Finish(bool commit,bool selectEdited=true)
    {
        if(!IsActive)return;
        bool owns=session.IsTransactionActive&&session.TransactionId==transaction;
        Detach();
        if(owns)
        {
            CanvasElement? screenCandidate = null;
            bool unsafePlacement = commit && Target.Text.Length > 0 && (screenFirst
                ? !TryResolveScreenDraft(out screenCandidate) : !CurrentCanvasMapping.IsElementSafe(WorldTarget));
            if(!commit||unsafePlacement||isNew&&string.IsNullOrWhiteSpace(Target.Text))session.Cancel();
            else
            {
                if (screenFirst && screenCandidate is not null) new SceneGraph(session.Document).InsertElement(screenCandidate, session.EditScopeId, rootCanvas: session.ActiveRootCanvas);
                if(Target.Text.Length==0) new SceneGraph(session.Document).RemoveNodes([Target.Id]);
                session.Commit();
                if(selectEdited&&session.Document.Elements.Any(element => element.Id == Target.Id))session.SetSelection([Target.Id]);
            }
            if(unsafePlacement)PlacementRejected?.Invoke();
        }
        Finished?.Invoke();
    }
    private void Detach(){IsActive=false;IsSelecting=false;session.Changed-=OnSessionChanged;Input.TextChanged-=OnTextChanged;}
    public void Dispose()=>Finish(commit:false);
}
