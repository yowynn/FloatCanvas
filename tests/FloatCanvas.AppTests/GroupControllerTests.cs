using System.Reflection;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class GroupControllerTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        CanvasElement Box(double x) => new() { Kind = ElementKind.Rectangle, Width = 40, Height = 30,
            Transform = Affine2D.Translation(x, 20), Style = new() { Fill = 0xFF245BC3, StrokeWidth = 0 } };
        var a = Box(20); var b = Box(100);
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600), Elements = [a, b] });
        session.SetSelection([a.Id, b.Id]); Guid group = session.GroupSelection()!.Value;
        session.Document.Elements.Single(element => element.Id == b.Id).Locked = true;
        var childBefore = session.Document.Elements.Select(element => element.Transform).ToArray();
        int history = session.UndoCount;
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        void Pointer(OverlayPointerKind kind, double x, double y) => editor.Pointer(new(kind, new(x, y), OverlayMouseButton.Left, OverlayModifiers.None));
        Pointer(OverlayPointerKind.Down, 80, 35); Pointer(OverlayPointerKind.Move, 110, 55); Pointer(OverlayPointerKind.Up, 110, 55);
        var g = session.Document.Groups.Single();
        check("canvas drag moves a selected group through its own local transform", Near(g.Transform.OffsetX, 30) && Near(g.Transform.OffsetY, 20) && session.Selection.SetEquals([group]));
        check("group drag leaves child transforms intact including the locked child", session.Document.Elements.Select(element => element.Transform).SequenceEqual(childBefore));
        check("group drag has one undo record and undo/redo restore the complete graph", session.UndoCount == history + 1 && session.Undo() && session.Document.Groups.Single().Transform == Affine2D.Identity && session.Redo());
        Pointer(OverlayPointerKind.DoubleClick, 60, 50);
        check("double-clicking a grouped leaf enters its group without another history record", session.EditScopeId == group && session.UndoCount == history + 1 && session.Selection.Count == 0);
        session.SetEditScope(null); session.SetSelection([group]); session.TransformSelection(Affine2D.Rotation(30)); session.EnterScope(group);
        editor.ApplyParameters(new() { Tool = EditorTool.Rectangle, Style = new() { Fill = 0xFFCC5533, StrokeWidth = 0 } });
        Pointer(OverlayPointerKind.Down, 250, 160); Pointer(OverlayPointerKind.Move, 300, 200); Pointer(OverlayPointerKind.Up, 300, 200);
        var created = session.Document.Elements.Last(); var graph = new SceneGraph(session.Document); var world = graph.ToWorld(created);
        check("drawing in a rotated scope inserts a real child rather than a root draft", created.GroupId == group && graph.PaintOrder(group).Contains(created.Id));
        var bounds = ElementGeometry.Bounds(world);
        check("draft world-to-local conversion preserves the actual screen rectangle", Near(bounds.X, 250) && Near(bounds.Y, 160) && Near(bounds.Width, 50) && Near(bounds.Height, 40));
        check("drawing into the group has one coherent undo and restores the same edit scope", session.Undo() && session.Document.Elements.Count == 2 && session.EditScopeId == group && session.Redo());
        session.SetSelection([]);
        editor.ApplyParameters(new() { Tool = EditorTool.Freehand, Style = new() { StrokeWidth = 3 } });
        Pointer(OverlayPointerKind.Down, 350, 200); Pointer(OverlayPointerKind.Move, 380, 220); Pointer(OverlayPointerKind.Up, 390, 230);
        var stroke = session.Document.Elements.Last();
        var strokeWorld = new SceneGraph(session.Document).ToWorld(stroke);
        check("freehand points remain local while a rotated parent's placement cancels exactly once", stroke.GroupId == group &&
            ElementGeometry.LocalToPlane(strokeWorld).TransformPoint(strokeWorld.Points[0]).DistanceTo(new(350, 200)) < 1e-7);
        session.SetEditScope(null); session.SetSelection([group]);
        editor.Tool = EditorTool.Select;
        var count = session.Document.Elements.Count;
        Pointer(OverlayPointerKind.Down, 440, 270); Pointer(OverlayPointerKind.Up, 440, 270);
        check("a completed drawing does not retain an active transaction or lose hierarchy", !session.IsTransactionActive && session.Document.Elements.Count == count);
        session.SetSelection([group]); var local = session.Document.Elements.Select(element => JsonSerializer.Serialize(element)).ToArray();
        var nodeOriginal = session.CaptureSelectionTransform();
        session.BeginTransaction("group resize"); session.TryApplySelectionTransform(nodeOriginal, Affine2D.Scale(1.2, .8), .8); session.Commit();
        check("later group resize still edits the group rather than baking its freshly drawn children", local.SequenceEqual(session.Document.Elements.Select(element => JsonSerializer.Serialize(element))));
        TransformPanel(check);
    }
    private static void TransformPanel(Action<string, bool> check)
    {
        string preference = L.Preference;
        try
        {
            foreach (var language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20, Style = new() { Fill = 0xFF245BC3, StrokeWidth = 0 } };
                var second = first.Clone(true); second.Transform = Affine2D.Translation(100, 0);
                var session = new EditorSession(new() { Elements = [first, second] }); session.SetSelection([first.Id, second.Id]);
                var panel = new NodeTransformPanel(() => session, () => true); panel.Refresh();
                var fields = Field<Dictionary<string, NumericValueField>>(panel, "fields");
                fields["Angle"].Input.Text = "90"; Invoke(panel, "Publish", "Angle"); panel.CompletePendingEdit();
                var centers = session.SelectedWorldElements().Select(element => ElementGeometry.Bounds(element).Center).ToArray();
                check(language + " overall property rotation changes the pair's layout around one pivot", Near(centers[0].X, 60) && Near(centers[1].X, 60) && Math.Abs(centers[0].Y - centers[1].Y) > 99);
                check(language + " typed group transform produces one undo step", session.UndoCount == 1 && session.Undo()); panel.Refresh();
                var mode = Field<SegmentedChoice>(panel, "mode"); mode.SelectedValue = true;
                check(language + " individual pivot choice remains visibly selected", session.TransformIndividually && Equals(mode.SelectedValue, true));
                fields["Angle"].Input.Text = "90"; Invoke(panel, "Publish", "Angle"); panel.CompletePendingEdit();
                centers = session.SelectedWorldElements().Select(element => ElementGeometry.Bounds(element).Center).ToArray();
                check(language + " individual property rotation preserves each layer center", Near(centers[0].X, 10) && Near(centers[1].X, 110) && Near(centers[0].Y, 10) && Near(centers[1].Y, 10));
                panel.Refresh(); fields["X"].Input.Text = "40"; Invoke(panel, "Publish", "X"); panel.CancelEditing();
                check(language + " canceling a live property gesture restores its prior placements", session.UndoCount == 1 && !session.IsTransactionActive && Near(session.SelectionBounds()!.Value.X, 0));
                var inspector = new InspectorWindow(() => session);
                using var surface = new InspectorTestSurface(inspector);
                var shown = Field<NodeTransformPanel>(inspector, "nodeTransforms"); var shownFields = Field<Dictionary<string, NumericValueField>>(shown, "fields");
                shownFields["X"].Input.Text = "40"; Invoke(shown, "Publish", "X");
                session.SetSelection([first.Id]);
                check(language + " changing selection completes the owned node edit and preserves the requested selection", !session.IsTransactionActive && session.Selection.SetEquals([first.Id]) &&
                    !shown.IsEditing && shown.Visibility == Visibility.Collapsed && Near(session.Document.Elements.First().Transform.TransformPoint(new(10, 10)).X, 50));
            }
        }
        finally { L.Initialize(preference); }
    }
    private static bool Near(double value, double target) => Math.Abs(value - target) < 1e-7;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
}
