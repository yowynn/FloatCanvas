using System.IO;
using System.Reflection;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class NodeOpacityTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check, string output)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var group = new CanvasGroup { Name = "Group" };
        var child = new CanvasElement { Kind=ElementKind.Rectangle,GroupId=group.Id,Width=40,Height=40,
            Transform=Affine2D.Translation(10,10),Style=new(){Fill=0xFFFF0000,StrokeWidth=0,Opacity=.8} };
        var peer = child.Clone(); peer.Id=Guid.NewGuid();peer.GroupId=null;peer.Transform=Affine2D.Translation(80,10);peer.Style.Opacity=.3;
        var document = new SceneDocument {Viewport=new(0,0,160,100),Groups=[group],Elements=[child,peer]};
        var session = new EditorSession(document);session.SetSelection([group.Id,peer.Id]);
        var panel = new NodeTransformPanel(()=>session,()=>true);panel.Refresh();
        var fields=(Dictionary<string,NumericValueField>)typeof(NodeTransformPanel).GetField("fields",Hidden)!.GetValue(panel)!;
        check("mixed group and layer opacity exposes a percent field",fields["Opacity"].IsEnabled && fields["Opacity"].Unit=="%" && fields["Opacity"].Input.Text=="");
        fields["Opacity"].Input.Text="50";typeof(NodeTransformPanel).GetMethod("Publish",Hidden)!.Invoke(panel,["Opacity"]);panel.CompletePendingEdit();
        check("group opacity is independent while each selected peer receives the value",group.Opacity==.5 && child.Style.Opacity==.8 && peer.Style.Opacity==.5 && session.UndoCount==1);
        var scene=SceneEvaluation.Build(session.Document);
        check("evaluated descendants inherit group opacity once",Math.Abs(scene.Leaves.Single(l=>l.SourceElementId==child.Id).RenderElement.Style.Opacity-.4)<1e-9);
        check("draft evaluation also inherits group opacity",Math.Abs(scene.ResolveElement(child).Style.Opacity-.4)<1e-9);
        check("selected world elements retain inherited opacity", Math.Abs(session.SelectedWorldElements().Single(e => e.Id == child.Id).Style.Opacity - .4) < 1e-9);
        check("invalid opacity input leaves the selection unchanged", !session.TrySetSelectionOpacity(double.NaN) && !session.TrySetSelectionOpacity(-.1) && group.Opacity == .5);
        using(var renderer=new SceneRenderer())
        using(var surface=SKSurface.Create(new SKImageInfo(160,100)))
        {
            surface.Canvas.Clear(SKColors.Transparent);renderer.Draw(surface.Canvas,session.Document,session.Assets);
            using var image=surface.Snapshot();using var pixels=SKBitmap.FromImage(image);
            check("rendered group opacity reaches the pixel alpha",Math.Abs(pixels.GetPixel(20,20).Alpha-102)<=1);
        }
        check("opacity batch undo restores group and peer without touching child settings",session.Undo() && session.Document.Groups.Single().Opacity==1 && session.Document.Elements.Single(e=>e.Id==peer.Id).Style.Opacity==.3 && session.Redo());
        panel.Refresh();fields["Opacity"].Input.Text="20";typeof(NodeTransformPanel).GetMethod("Publish",Hidden)!.Invoke(panel,["Opacity"]);panel.CancelEditing();
        check("cancelled opacity gesture restores both targets",session.Document.Groups.Single().Opacity==.5 && session.Document.Elements.Single(e=>e.Id==peer.Id).Style.Opacity==.5);
        var previous=SceneEvaluation.Build(session.Document);session.SetSelection([group.Id]);session.BeginTransaction("opacity preview");session.TrySetSelectionOpacity(.25);
        var incremental=SceneEvaluation.UpdatePlacements(session.Document,previous,[group.Id],new SceneEvaluationCache());
        check("incremental group opacity refreshes descendants and draft resolution",Math.Abs(incremental.Leaves.Single(l=>l.SourceElementId==child.Id).RenderElement.Style.Opacity-.2)<1e-9 && Math.Abs(incremental.ResolveElement(session.Document.Elements.Single(e=>e.Id==child.Id)).Style.Opacity-.2)<1e-9);session.Cancel();
        Directory.CreateDirectory(output);var path=Path.Combine(output,"opacity.floatcanvas");var store=new ProjectStore();
        store.SaveAsync(path,session.Document,session.Assets).GetAwaiter().GetResult();var loaded=store.LoadAsync(path).GetAwaiter().GetResult();
        check("project roundtrip retains container and child opacity",loaded.Document.Version==20 && loaded.Document.Groups.Single().Opacity==.5 && loaded.Document.Elements.Single(e=>e.Id==child.Id).Style.Opacity==.8);
        session.SetSelection([group.Id]);session.UngroupSelection();
        check("ungroup preserves visible opacity",session.Document.Groups.Count==0 && Math.Abs(session.Document.Elements.Single(e=>e.Id==child.Id).Style.Opacity-.4)<1e-9);session.Undo();
        var prefab=session.CreatePrefabFromSelection("Shared");
        var instance=session.Document.Instances.Single();session.SetSelection([instance.Id]);session.TrySetSelectionOpacity(.5);
        check("shared instance opacity multiplies definition values without rewriting them",Math.Abs(SceneEvaluation.Build(session.Document).Leaves.Single(l=>l.HostNodeId==instance.Id).RenderElement.Style.Opacity-.2)<1e-9 && session.Document.Definitions.Single().Graph.Groups.Single().Opacity==.5);
        store.SaveAsync(Path.Combine(output,"shared-opacity.floatcanvas"),session.Document,session.Assets).GetAwaiter().GetResult();
        var roundtrip=store.LoadAsync(Path.Combine(output,"shared-opacity.floatcanvas")).GetAwaiter().GetResult();
        check("shared instance opacity survives save and load",roundtrip.Document.Instances.Single().Opacity==.5);
    }
}
