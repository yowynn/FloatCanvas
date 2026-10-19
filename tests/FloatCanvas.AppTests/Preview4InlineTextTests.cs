using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using FloatCanvas.Rendering;
using System.Windows;

internal static class Preview4InlineTextTests
{
    internal static void Run(Action<string,bool> check)
    {
        var text=new CanvasElement {Kind=ElementKind.Text,Text="Edit here",Width=180,Height=50,Transform=Affine2D.Translation(100,100)};
        var session=new EditorSession(new SceneDocument {Elements=[text]});session.SetSelection([text.Id]);
        var controller=new EditorController(session) {Tool=EditorTool.Text};
        CanvasElement? requested=null;int requests=0;
        controller.EditText+=(existing,_)=>{requested=existing;requests++;};
        controller.Pointer(new(OverlayPointerKind.Down,new(130,120),OverlayMouseButton.Left,OverlayModifiers.None));
        check("the text tool starts editing the selected text at its canvas position instead of moving it",requests==1&&ReferenceEquals(requested,text)&&!session.IsTransactionActive);
        controller.Pointer(new(OverlayPointerKind.Up,new(130,120),OverlayMouseButton.Left,OverlayModifiers.None));
        controller.Pointer(new(OverlayPointerKind.Down,new(130,120),OverlayMouseButton.Left,OverlayModifiers.Control));
        check("Ctrl click inside existing text requests a new text layer",requests==2&&requested is null);
        _=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        AppTheme.Apply("light");
        using var renderer=new SceneRenderer();
        var fresh=new EditorSession();
        using(var edit=new InlineTextEdit(fresh,null,new(123,89),new() {FontSize=28},renderer))
        {
            check("a new inline draft immediately has its own selected layer and transaction",fresh.Document.Elements.Count==1&&fresh.IsTransactionActive&&fresh.Selection.SetEquals([edit.Target.Id]));
            edit.Input.Text="中文 inline 👩‍🎨\r\nSecond line";
            check("typing previews the actual canvas text while retaining one uncommitted operation",edit.Target.Text=="中文 inline 👩‍🎨\nSecond line"&&fresh.UndoCount==0&&edit.Target.Height>28);
            var geometry=edit.Geometry;
            var boundaries=System.Globalization.StringInfo.ParseCombiningCharacters(edit.Input.Text).Append(edit.Input.Text.Length).ToHashSet();
            check("caret geometry uses whole graphemes and preserves original CRLF text indices",geometry.Lines.SelectMany(l=>l.Carets).All(c=>boundaries.Contains(c.Index))&&geometry.Lines[^1].Carets[^1].Index==edit.Input.Text.Length);
            using var pixels=SkiaSharp.SKBitmap.Decode(renderer.ExportPng(fresh.Document,fresh.Assets,true));
            check("the typing draft renders through the real scene renderer",Enumerable.Range(0,pixels.Width*pixels.Height).Any(i=>pixels.GetPixel(i%pixels.Width,i/pixels.Width).Alpha>0));
            edit.Finish(true);
            check("Enter-style completion keeps the text selected and commits one undo entry",!edit.IsActive&&!fresh.IsTransactionActive&&fresh.UndoCount==1&&fresh.Selection.Count==1);
        }
        check("one undo removes the entire new text input",fresh.Undo()&&fresh.Document.Elements.Count==0);
        check("redo restores all typed Unicode and newlines",fresh.Redo()&&fresh.Document.Elements.Single().Text=="中文 inline 👩‍🎨\nSecond line");
        var target=fresh.Document.Elements.Single();int undo=fresh.UndoCount;
        using(var edit=new InlineTextEdit(fresh,target,new(),new(),renderer))edit.Finish(true);
        check("opening and accepting existing text without changes adds no history",fresh.UndoCount==undo);
        using(var edit=new InlineTextEdit(fresh,target,new(),new(),renderer))
        {edit.Input.Text="cancelled";edit.Finish(false);}
        check("Escape restores the exact original text in one step without adding undo",fresh.Document.Elements.Single().Text=="中文 inline 👩‍🎨\nSecond line"&&fresh.UndoCount==undo);
        target=fresh.Document.Elements.Single();
        var other=new CanvasElement {Kind=ElementKind.Rectangle,Width=30,Height=30};fresh.Add(other);
        using(var edit=new InlineTextEdit(fresh,target,new(),new(),renderer))
        {
            edit.Input.Text="commit on layer change";fresh.SetSelection([other.Id]);
            check("changing layers commits the text while preserving the newly selected layer",!edit.IsActive&&target.Text=="commit on layer change"&&fresh.Selection.SetEquals([other.Id])&&!fresh.IsTransactionActive);
        }
        var blank=new EditorSession();
        using(var edit=new InlineTextEdit(blank,null,new(10,10),new(),renderer))edit.Finish(true);
        check("an empty new text input leaves no blank layer or undo entry",blank.Document.Elements.Count==0&&blank.UndoCount==0);
        var style=new ElementStyle {FontSize=24};
        var layout=renderer.TextEditingGeometry("A\r\n中 e\u0301 👩‍🎨",style,240);
        foreach(var line in layout.Lines)foreach(var stop in line.Carets)
            check("canvas hit testing resolves shaped caret "+stop.Index,layout.HitTest(new(stop.X,line.Top+line.Height/2))==stop.Index);
        const string graphemes="e\u0301👩‍🎨中";
        var wrapped=renderer.TextEditingGeometry(graphemes,style,1);
        var allowed=System.Globalization.StringInfo.ParseCombiningCharacters(graphemes).Append(graphemes.Length).ToHashSet();
        check("narrow wrapping keeps combining marks and joined emoji on complete grapheme boundaries",
            wrapped.Lines.Count==3&&wrapped.Lines.SelectMany(line=>line.Carets).All(stop=>allowed.Contains(stop.Index)));
        var viewport=new RectD(0,0,800,600);
        var projection=CanvasCalibration.CreateInitial(viewport);
        check("inline pointer fixture has valid perspective",CanvasCalibration.TryCalibrate(projection,
            [new(80,60),new(720,100),new(660,540),new(110,500)],viewport,out projection,out _));
        var projected=new EditorSession(new SceneDocument {Viewport=viewport,Canvas=new() {Projection=projection}});
        using(var edit=new InlineTextEdit(projected,null,new(180,160),style,renderer))
        {
            edit.Target.Transform=edit.Target.Transform.Compose(Affine2D.Rotation(18));
            edit.Input.Text="Canvas selection";
            var line=edit.Geometry.Lines[0];
            PointD Screen(int index)=>edit.LocalToScreen.TransformPoint(new(line.Carets[index].X,line.Top+line.Height/2));
            edit.Pointer(Screen(2),true,false);edit.Pointer(Screen(7),false,true);
            check("canvas drag selects the intended text through rotation and perspective",
                edit.Input.SelectionStart==2&&edit.Input.SelectionLength==5&&!edit.IsSelecting&&edit.Contains(Screen(4)));
            edit.Input.Text=edit.Input.Text.Remove(edit.Input.SelectionStart,edit.Input.SelectionLength).Insert(edit.Input.SelectionStart," replacement ");
            edit.Input.Text="A 👩‍🎨 B";
            var emoji=edit.Geometry.Caret(2);
            var emojiPoint=edit.LocalToScreen.TransformPoint(new(emoji.X+.1,emoji.Y+emoji.Height/2));
            edit.Pointer(emojiPoint,true,true,word:true);
            check("double click selects the complete joined emoji instead of a surrogate or joiner",edit.Input.SelectedText=="👩‍🎨");
            edit.Finish(true);
        }
        check("a projected selection edit remains a single committed canvas operation",projected.UndoCount==1&&!projected.IsTransactionActive);
    }
}
