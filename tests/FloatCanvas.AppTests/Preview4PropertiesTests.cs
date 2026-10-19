using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

internal static class Preview4PropertiesTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        _=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        AppTheme.Apply("light");
        var text=new CanvasElement {Kind=ElementKind.Text,Text="Resize text",Width=200,Height=60,Style=new() {FontSize=24},Transform=Affine2D.Translation(100,100)};
        var session=new EditorSession(new SceneDocument {Elements=[text]});session.SetSelection([text.Id]);
        var inspector=new InspectorWindow(()=>session);
        var aspect=Field<CheckBox>(inspector,"keepAspect");
        check("text layers default to unconstrained frame resizing",aspect.IsChecked==false);
        aspect.IsChecked=true;aspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));inspector.CompletePendingEdit();inspector.Refresh();
        check("the aspect checkbox writes the layer preference and retains it after refresh",text.ConstrainAspect==true&&aspect.IsChecked==true);
        check("the layer preference survives clone and JSON persistence",text.Clone().ConstrainAspect==true&&JsonSerializer.Deserialize<CanvasElement>(JsonSerializer.Serialize(text))!.ConstrainAspect==true);
        check("one undo restores the default aspect preference",session.Undo()&&session.Document.Elements.Single().ConstrainAspect is null);
        inspector.Refresh();check("text refresh after undo returns to the unconstrained default",aspect.IsChecked==false);
        var panel=Field<StackPanel>(inspector,"propertyPanel");
        var containers=Field<Dictionary<string,FrameworkElement>>(inspector,"fieldContainers");
        int Row(string key)
        {
            DependencyObject row=containers[key];
            while(LogicalTreeHelper.GetParent(row) is { } parent&&!ReferenceEquals(parent,panel))row=parent;
            return panel.Children.IndexOf((UIElement)row);
        }
        check("text content precedes appearance and geometry while position is at the bottom",
            Row("Text")<Row("Opacity")&&Row("Opacity")<Row("X"));
        using var renderer=new SceneRenderer();
        foreach(var kind in new[] {ElementKind.Text,ElementKind.Rectangle,ElementKind.Image,ElementKind.Number})
        foreach(bool keep in new[] {false,true})foreach(bool shift in new[] {false,true})
        {
            var element=new CanvasElement {Kind=kind,ConstrainAspect=keep,Width=200,Height=60,Text="Frame fit 中文 text",Style=new() {FontSize=24},Transform=Affine2D.Translation(100,100)};
            var state=new EditorSession(new SceneDocument {Elements=[element]});state.SetSelection([element.Id]);
            var controller=new EditorController(state) {Tool=EditorTool.Select};
            var modifiers=shift?OverlayModifiers.Shift:OverlayModifiers.None;
            controller.Pointer(new(OverlayPointerKind.Down,new(300,160),OverlayMouseButton.Left,modifiers));
            controller.Pointer(new(OverlayPointerKind.Move,new(400,160),OverlayMouseButton.Left,modifiers));
            controller.Pointer(new(OverlayPointerKind.Up,new(400,160),OverlayMouseButton.Left,modifiers));
            var size=ElementGeometry.Bounds(element,false);
            bool uniform=Math.Abs(size.Width/200-size.Height/60)<.001;
            check($"{kind} aspect={keep} Shift={shift} uses the inverse drag constraint",uniform==(keep^shift)&&state.UndoCount==1);
            if(kind==ElementKind.Text)
            {
                var measured=renderer.MeasureText(element.Text,element.Style,element.Width);
                check($"text frame aspect={keep} Shift={shift} fits text without scaling glyph axes",
                    element.TextFrameFixed&&measured.Width<=element.Width+.01&&measured.Height<=element.Height+.01&&
                    Math.Abs(element.Transform.M11-1)<1e-8&&Math.Abs(element.Transform.M22-1)<1e-8);
                var original=element.Clone();
                using(var edit=new InlineTextEdit(state,element,new(),new(),renderer))
                {edit.Input.Text="Longer content that must wrap and shrink within the original resized frame. 中文文字。";edit.Finish(true);}
                check("editing resized text preserves its fixed frame and fits the new content",element.Width==original.Width&&element.Height==original.Height&&renderer.MeasureText(element.Text,element.Style,element.Width).Height<=element.Height+.01);
            }
        }
        var requested=new List<PointD>();var propertiesController=new EditorController(session) {Tool=EditorTool.Select};propertiesController.PropertiesRequested+=requested.Add;
        propertiesController.Pointer(new(OverlayPointerKind.DoubleClick,new(110,110),OverlayMouseButton.Left,OverlayModifiers.None));
        check("double clicking a single layer selects it and requests properties at the pointer",requested.SequenceEqual([new PointD(110,110)])&&session.Selection.Count==1&&!session.IsTransactionActive);
        propertiesController.Tool=EditorTool.Rectangle;
        propertiesController.Pointer(new(OverlayPointerKind.DoubleClick,new(110,110),OverlayMouseButton.Left,OverlayModifiers.None));
        check("double click properties also works while a drawing tool is selected",requested.Count==2&&!session.IsTransactionActive);
        var multiText=new CanvasElement {Kind=ElementKind.Text,Text="Multiple selection text wraps inside its frame",Width=180,Height=60,Style=new() {FontSize=22},Transform=Affine2D.Translation(100,100)};
        var multiShape=new CanvasElement {Kind=ElementKind.Rectangle,Width=40,Height=40,Transform=Affine2D.Translation(300,100),Style=new() {StrokeWidth=0}};
        var multi=new EditorSession(new SceneDocument {Elements=[multiText,multiShape]});multi.SetSelection([multiText.Id,multiShape.Id]);
        var multiController=new EditorController(multi) {Tool=EditorTool.Select};
        foreach(var (kind,point) in new[] {(OverlayPointerKind.Down,new OverlayPoint(340,160)),(OverlayPointerKind.Move,new OverlayPoint(440,160)),(OverlayPointerKind.Up,new OverlayPoint(440,160))})
            multiController.Pointer(new(kind,point,OverlayMouseButton.Left,OverlayModifiers.None));
        check("nonuniform multi-selection resize also fits text into its changed frame",multiText.TextFrameFixed&&multiText.Width>180&&multiText.Height==60&&renderer.MeasureText(multiText.Text,multiText.Style,multiText.Width).Height<=multiText.Height+.01&&multi.UndoCount==1);
        var constrained=multiText.Clone();constrained.ConstrainAspect=false;
        string projectPath=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FloatCanvas-properties-"+Guid.NewGuid().ToString("N")+".floatcanvas");
        var store=new FloatCanvas.Storage.ProjectStore();var project=new SceneDocument {Elements=[constrained]};
        store.SaveAsync(projectPath,project,new()).GetAwaiter().GetResult();
        var loaded=store.LoadAsync(projectPath).GetAwaiter().GetResult().Document.Elements.Single();
        check("actual project packages retain frame sizing and explicit unconstrained preference",loaded.TextFrameFixed&&loaded.ConstrainAspect==false&&loaded.Width==constrained.Width);
        var tiny=new CanvasElement {Kind=ElementKind.Text,Text=new string('中',100),Width=14,Height=8,TextFrameFixed=true,Style=new() {FontSize=1},Transform=Affine2D.Translation(20,20)};
        using(var pixels=SkiaSharp.SKBitmap.Decode(renderer.ExportPng(new SceneDocument {Viewport=new(0,0,60,60),Elements=[tiny]},new(),true)))
        {
            bool inside=true;
            for(int y=0;y<pixels.Height;y++)for(int x=0;x<pixels.Width;x++)
                if(pixels.GetPixel(x,y).Alpha>0&&(x<20||x>=34||y<20||y>=28))inside=false;
            check("even a minimum-font text frame never renders text outside its bounds",inside);
        }
        var pin=Field<Button>(inspector,"pin");pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("the property panel exposes the shared pin action",inspector.IsPinned);
        inspector.Dismiss();check("explicit dismissal resets the panel pin",!inspector.IsPinned);
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
}
