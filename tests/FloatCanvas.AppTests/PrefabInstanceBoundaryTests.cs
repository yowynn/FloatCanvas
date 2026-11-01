using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PrefabInstanceBoundaryTests
{
    internal static void Run(Action<string, bool> check)
    {
        Clipboard(check); Recovery(check); Input(check);
    }
    internal static (EditorSession Session, PrefabDefinition Definition, PrefabInstance Instance) Fixture(double x = 100)
    {
        var visible = new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 40, Locked = true, Style = new() { Fill = 0xFF3388BB, StrokeWidth = 0 } };
        var hidden = new CanvasElement { Kind = ElementKind.Ellipse, Width = 30, Height = 50, Transform = Affine2D.Translation(90, 20),
            Visible = false, Locked = true, Style = new() { Fill = 0xFFBB4433, StrokeWidth = 0 } };
        var definition = new PrefabDefinition { Name = "shared", Graph = new() { Elements = [visible, hidden] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id, Name = "instance", Transform = Affine2D.Translation(x, 100) };
        var source = new SceneDocument { Viewport = new(0, 0, 1920, 1080), Instances = [instance], Definitions = [definition], RootOrder = [instance.Id] };
        var session = new EditorSession(source); session.SetSelection([instance.Id]); return (session, definition, instance);
    }
    private static void Clipboard(Action<string, bool> check)
    {
        var fixture = Fixture(); var source = fixture.Session; var copy = ObjectClipboardCommand.CreateSnapshot(source);
        check("copying an instance retains its complete hidden definition and no authored expansion", copy.Instances.Count == 1 && copy.Elements.Count == 0 &&
            copy.Definitions.Count == 1 && copy.Definitions[0].Graph.Elements.Count == 2 && !copy.Definitions[0].Graph.Elements[1].Visible);
        ObjectClipboardCommand.Insert(source, copy, source.Assets, source.Document.Viewport);
        check("same-owner direct instance paste shares the existing definition", source.Document.Definitions.Count == 1 && source.Document.Instances.Count == 2 &&
            source.Document.Instances.All(instance => instance.DefinitionId == fixture.Definition.Id) && source.Selection.Count == 1);
        Guid token = ObjectClipboardCommand.RegisterTransfer(source, copy); string json = JsonSerializer.Serialize(copy);
        source.Execute("edit current shared definition", document => document.Definitions[0].Graph.Elements[0].Style.Fill = 0xFF55AA22);
        var restored = JsonSerializer.Deserialize<SceneDocument>(json)!;
        ObjectClipboardCommand.Insert(source, restored, source.Assets, source.Document.Viewport, token);
        check("same-process clipboard restoration keeps sharing the owner's current edited definition", source.Document.Definitions.Count == 1 && source.Document.Instances.Count == 3 &&
            SceneEvaluation.Build(source.Document).Leaves.Where(leaf => leaf.RenderElement.Visible).All(leaf => leaf.RenderElement.Style.Fill == 0xFF55AA22));
        var other = new EditorSession(); other.Document.Definitions.Add(fixture.Definition.Clone());
        ObjectClipboardCommand.Insert(other, restored, source.Assets, other.Document.Viewport, token);
        check("another owner never merges a clipboard definition merely because its Guid matches", other.Document.Definitions.Count == 2 &&
            other.Document.Instances.Single().DefinitionId != fixture.Definition.Id && other.Document.Elements.Count == 0);
        var modified = JsonSerializer.Deserialize<SceneDocument>(json)!; modified.Definitions[0].Graph.Elements[0].Style.Fill = 0xFFAA44CC;
        ObjectClipboardCommand.Insert(source, modified, source.Assets, source.Document.Viewport, token);
        check("a modified external payload cannot claim within-owner identity with a copied token", source.Document.Definitions.Count == 2 &&
            source.Document.Instances.Single(instance => source.Selection.Contains(instance.Id)).DefinitionId != fixture.Definition.Id);
        var fresh = Fixture(); var retained = ObjectClipboardCommand.CreateSnapshot(fresh.Session);
        new SceneGraph(fresh.Session.Document).RemoveNodes([fresh.Instance.Id]); fresh.Session.Document.Definitions.Clear();
        ObjectClipboardCommand.Insert(fresh.Session, retained, fresh.Session.Assets, fresh.Session.Document.Viewport);
        check("pasting after the original unused definition was deleted restores an independent complete copy", fresh.Session.Document.Instances.Count == 1 &&
            fresh.Session.Document.Definitions.Count == 1 && fresh.Session.Document.Instances[0].DefinitionId != fresh.Definition.Id);
        var empty = Fixture(); empty.Definition.Graph.Elements.Clear();
        var emptyCopy = ObjectClipboardCommand.CreateSnapshot(empty.Session); var destination = new EditorSession();
        ObjectClipboardCommand.Insert(destination, emptyCopy, empty.Session.Assets, destination.Document.Viewport);
        check("an empty shared instance remains a real copyable node with its definition", destination.Document.Elements.Count == 0 && destination.Document.Instances.Count == 1 &&
            destination.Document.Definitions.Single().Graph.Elements.Count == 0 && destination.Selection.Contains(destination.Document.Instances.Single().Id));
    }
    private static void Recovery(Action<string, bool> check)
    {
        var fixture = Fixture(2400); var session = fixture.Session; string definitions = JsonSerializer.Serialize(session.Document.Definitions), before = JsonSerializer.Serialize(session.Document);
        check("move into view relocates a whole instance containing locked and hidden definition members", MoveIntoViewCommand.Execute(session,
            [(new(0, 0, 1920, 1080), new(0, 40, 1920, 1040))], default) && session.Document.Elements.Count == 0 &&
            JsonSerializer.Serialize(session.Document.Definitions) == definitions && session.Selection.SetEquals([fixture.Instance.Id]));
        var world = SceneEvaluation.Build(session.Document);
        check("instance recovery uses world occurrence bounds and retains hidden member flags", world.Leaves.Any(leaf => leaf.RenderElement.Visible &&
            !CanvasMapping.Create(session.Document).ProjectedBounds(leaf.RenderElement).Intersect(session.Document.Viewport).IsEmpty) &&
            world.Leaves.Any(leaf => !leaf.RenderElement.Visible && leaf.RenderElement.Locked));
        check("instance move recovery is one exact undo", session.UndoCount == 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        fixture = Fixture(-1_000_000_000); session = fixture.Session;
        session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(session.Document.Viewport); session.Document.Canvas.Projection.Enabled = true;
        definitions = JsonSerializer.Serialize(session.Document.Definitions); var affected = CanvasSafetyResolution.AffectedIds(session.Document);
        check("unsafe occurrences report their actual host instance node rather than virtual IDs", affected.SequenceEqual([fixture.Instance.Id]));
        check("safety relocation accepts whole-instance plan roots and preserves the shared definition", CanvasSafetyResolution.TryRelocate(session.Document, [fixture.Instance.Id]) &&
            JsonSerializer.Serialize(session.Document.Definitions) == definitions && SceneEvaluation.Build(session.Document).Leaves.All(leaf => CanvasMapping.Create(session.Document).IsElementSafe(leaf.RenderElement)));
    }
    private static void Input(Action<string, bool> check)
    {
        foreach (bool depth in new[] { false, true })
        {
            var fixture = Fixture(); var session = fixture.Session;
            if (depth)
            {
                session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(session.Document.Viewport); session.Document.Canvas.Projection.Enabled = true;
                session.Document.Canvas.DepthPriorityOcclusion = true;
            }
            session.SetSelection([]); var editor = new EditorController(session) { Tool = EditorTool.Select };
            var first = SceneEvaluation.Build(session.Document).Leaves.First(leaf => leaf.RenderElement.Visible);
            var point = CanvasMapping.Create(session.Document).ProjectedBounds(first.RenderElement).Center;
            string definition = JsonSerializer.Serialize(session.Document.Definitions);
            Send(editor, OverlayPointerKind.Down, point); Send(editor, OverlayPointerKind.Up, point);
            check($"real canvas selects an unlocked instance whose definition leaves are all locked depth={depth}", session.Selection.SetEquals([fixture.Instance.Id]) &&
                session.UndoCount == 0 && !session.Selection.Contains(first.RuntimeElementId));
            var before = SceneEvaluation.Build(session.Document).Leaves.First(leaf => leaf.RenderElement.Visible).RenderElement;
            Send(editor, OverlayPointerKind.Down, point); Send(editor, OverlayPointerKind.Move, point + new PointD(30, 15)); Send(editor, OverlayPointerKind.Up, point + new PointD(30, 15));
            var after = SceneEvaluation.Build(session.Document).Leaves.First(leaf => leaf.RenderElement.Visible).RenderElement;
            check($"dragging the locked-member instance changes only its real placement depth={depth}", session.UndoCount == 1 &&
                JsonSerializer.Serialize(session.Document.Definitions) == definition && session.Selection.SetEquals([fixture.Instance.Id]) && after.Transform != before.Transform);
            check($"locked-member instance drag has one complete undo depth={depth}", session.Undo() &&
                JsonSerializer.Serialize(session.Document.Definitions) == definition && SceneEvaluation.Build(session.Document).Leaves.First(leaf => leaf.RenderElement.Visible).RenderElement.Transform == before.Transform);
            int edits = 0; editor.PrefabEditRequested += id => { if (id == fixture.Instance.Id) edits++; };
            Send(editor, OverlayPointerKind.DoubleClick, point);
            check($"double-click requests definition editing instead of a virtual leaf inspector depth={depth}", edits == 1 && !session.IsTransactionActive &&
                session.Document.Elements.Count == 0 && JsonSerializer.Serialize(session.Document.Definitions) == definition);
        }
    }
    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point) => editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
}
