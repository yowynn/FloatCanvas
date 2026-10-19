using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;

internal static class InlineTextEditSafetyTests
{
    internal static void Run(Action<string,bool> check)
    {
        using var renderer=new SceneRenderer();
        var element=new CanvasElement {Kind=ElementKind.Text,Text="Before",Width=90,Height=70,
            Transform=Affine2D.Translation(50,60).Compose(Affine2D.Rotation(30)),Style=new() {FontSize=32}};
        var session=new EditorSession(new SceneDocument {Elements=[element]});
        element.Locked=true;string before=JsonSerializer.Serialize(session.Document);bool rejected=false;
        try{using var edit=new InlineTextEdit(session,element,new(),new(),renderer);}catch(InvalidOperationException){rejected=true;}
        check("inline editing rejects a locked target without changing its document or history",rejected&&JsonSerializer.Serialize(session.Document)==before&&!session.IsTransactionActive&&session.UndoCount==0);
        element.Locked=false;session.BeginTransaction("Other drawing");before=JsonSerializer.Serialize(session.Document);rejected=false;
        try{using var edit=new InlineTextEdit(session,element,new(),new(),renderer);}catch(InvalidOperationException){rejected=true;}
        check("inline input cannot cancel or overwrite another canvas transaction",rejected&&session.IsTransactionActive&&JsonSerializer.Serialize(session.Document)==before);session.Cancel();
        element=session.Document.Elements.Single();before=JsonSerializer.Serialize(element);
        using(var edit=new InlineTextEdit(session,element,new(999,999),new() {FontSize=9},renderer))
        {
            edit.Input.Text="A longer replacement that wraps";
            check("inline replacement preserves the target's width, font and rotation",element.Width==90&&element.Style.FontSize==32&&
                element.Transform==Affine2D.Translation(50,60).Compose(Affine2D.Rotation(30))&&element.Height==renderer.MeasureText(element.Text,element.Style,90).Height);
            edit.Finish(true);
        }
        check("one undo restores the exact original inline target",session.Undo()&&JsonSerializer.Serialize(session.Document.Elements.Single())==before);
        element=session.Document.Elements.Single();
        using(var edit=new InlineTextEdit(session,element,new(),new(),renderer))
        {
            edit.Input.Text="will be removed";session.Document.Elements.Remove(element);session.NotifyChanged();
            check("removing an active target finishes its transaction instead of leaving a stale editor",!edit.IsActive&&!session.IsTransactionActive&&session.Document.Elements.Count==0);
        }
        session.Undo();element=session.Document.Elements.Single();
        using(var edit=new InlineTextEdit(session,element,new(),new(),renderer))
        {
            edit.Input.Text="interrupted";session.Cancel();
            check("an external rollback detaches inline input without reviving its stale target",!edit.IsActive&&!session.IsTransactionActive&&session.Document.Elements.Single().Text=="Before");
        }
    }
}
