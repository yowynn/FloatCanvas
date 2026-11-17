using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class NodeControllerAuditTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light"); var failures = new List<string>();
        void Case(string name, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
                var message = name + ": " + error.GetType().Name + ": " + error.Message;
                failures.Add(message); Console.WriteLine("NODE_CONTROLLER_AUDIT_FAIL " + message);
            }
        }
        void Near(PointD actual, PointD expected, string message) => check(message, actual.DistanceTo(expected) < 1e-6);
        static string Json(object value) => JsonSerializer.Serialize(value);

        Case("nested world draft, stable sample and cancel", () =>
        {
            var fixture = PopulatedNested(); var session = CreateSession(fixture.Document); session.EnterScope(fixture.Inner);
            var editor = new EditorController(session) { ScreenOrigin = new(300, -100) };
            editor.ApplyParameters(new() { Tool = EditorTool.Rectangle, Style = new() { Fill = 0xFF2468AA, StrokeWidth = 0 } });
            string before = Json(session.Document); var a = new PointD(350, 260); var b = new PointD(420, 300);
            Pointer(editor, OverlayPointerKind.Down, a); Pointer(editor, OverlayPointerKind.Move, b);
            var local = session.Document.Elements.Single(e => e.Name != "Scope fixture content"); var world = new SceneGraph(session.Document).ToWorld(local); var bounds = ElementGeometry.Bounds(world);
            check("nested drawing draft is a local child in the active scope", local.GroupId == fixture.Inner && session.IsTransactionActive);
            Near(new(bounds.Left, bounds.Top), a, "two nontrivial parent placements cancel exactly once for the live rectangle");
            check("world draft dimensions remain the actual pointer rectangle", Math.Abs(bounds.Width - 70) < 1e-6 && Math.Abs(bounds.Height - 40) < 1e-6);
            long revision = session.PreviewRevision; Pointer(editor, OverlayPointerKind.Move, b);
            check("a stationary pointer sample does not publish another geometry revision", session.PreviewRevision == revision);
            Pointer(editor, OverlayPointerKind.Cancel, b);
            check("cancel removes the entire local draft without history or group loss", Json(session.Document) == before && session.UndoCount == 0 && !session.IsTransactionActive);
            Pointer(editor, OverlayPointerKind.Down, a); Pointer(editor, OverlayPointerKind.Move, b); Pointer(editor, OverlayPointerKind.Up, b);
            world = new SceneGraph(session.Document).ToWorld(session.Document.Elements.Single(e => e.Name != "Scope fixture content")); bounds = ElementGeometry.Bounds(world);
            Near(new(bounds.Left, bounds.Top), a, "committing a nested drawing does not apply its parent inverse a second time");
            check("nested drawing commit is a single reversible local graph operation", session.UndoCount == 1 && session.Undo() && session.Document.Elements.Count(e => e.Name != "Scope fixture content") == 0 && session.Document.Groups.Count == 2);
        });

        Case("nested freehand and modifier samples stay in one world draft", () =>
        {
            var fixture = PopulatedNested(); var session = CreateSession(fixture.Document); session.EnterScope(fixture.Inner);
            var editor = new EditorController(session) { Tool = EditorTool.Freehand, ScreenOrigin = new(-200, 70) };
            var a = new PointD(350, 220); var b = new PointD(370, 230); var c = new PointD(390, 270); var d = new PointD(420, 280);
            Pointer(editor, OverlayPointerKind.Down, a); Pointer(editor, OverlayPointerKind.Move, b); Pointer(editor, OverlayPointerKind.Move, c);
            long revision = session.PreviewRevision; Pointer(editor, OverlayPointerKind.Move, c);
            check("a repeated freehand point does not dirty or append to the stroke", session.PreviewRevision == revision);
            editor.ModifiersChanged(OverlayModifiers.Shift, new(c.X + editor.ScreenOrigin.X, c.Y + editor.ScreenOrigin.Y), pointerLeftDown: true);
            Pointer(editor, OverlayPointerKind.Move, d, OverlayModifiers.Shift); Pointer(editor, OverlayPointerKind.Up, d, OverlayModifiers.Shift);
            var stroke = session.Document.Elements.Single(e => e.Name != "Scope fixture content"); var world = new SceneGraph(session.Document).ToWorld(stroke); var mapping = ElementGeometry.LocalToPlane(world);
            Near(mapping.TransformPoint(world.Points[0]), a, "nested freehand keeps its first world point after modifier-boundary updates");
            Near(mapping.TransformPoint(world.Points[^1]), d, "nested freehand keeps its final world point after commit");
            check("freehand remains an editable local point collection owned by the active group", stroke.GroupId == fixture.Inner && stroke.Points[0] == default && session.UndoCount == 1);
        });

        Case("endpoint editing resolves and publishes the parent once", () =>
        {
            var fixture = PopulatedNested(); var graph = new SceneGraph(fixture.Document);
            var line = graph.InsertElement(new() { Kind = ElementKind.Line, Points = [new(0, 0), new(90, 0)], Transform = Affine2D.Translation(20, 30),
                Style = new() { StrokeWidth = 3 } }, fixture.Inner, worldCoordinates: false);
            var session = CreateSession(fixture.Document); session.EnterScope(fixture.Inner); session.SetSelection([line.Id]);
            var editor = new EditorController(session) { Tool = EditorTool.Select }; var originalParent = graph.NodeWorldPlacement(fixture.Inner);
            var before = graph.ToWorld(line); var map = ElementGeometry.LocalToPlane(before); var start = map.TransformPoint(before.Points[0]); var end = map.TransformPoint(before.Points[^1]);
            var target = end + new PointD(15, 30);
            Pointer(editor, OverlayPointerKind.Down, end);
            check("the displayed endpoint starts the endpoint gesture for a grouped leaf", Field<string?>(editor, "gesture") == "endpoint");
            Pointer(editor, OverlayPointerKind.Move, target); Pointer(editor, OverlayPointerKind.Up, target);
            graph = new(session.Document); var edited = graph.ToWorld(graph.FindElement(line.Id)!); map = ElementGeometry.LocalToPlane(edited);
            Near(map.TransformPoint(edited.Points[0]), start, "moving a grouped endpoint preserves the opposite world endpoint");
            Near(map.TransformPoint(edited.Points[^1]), target, "moving a grouped endpoint reaches the pointer without rebaking parent transforms");
            check("endpoint editing leaves the parent placement unchanged and creates one history entry", graph.NodeWorldPlacement(fixture.Inner) == originalParent && session.UndoCount == 1);
            string committed = Json(session.Document);
            Pointer(editor, OverlayPointerKind.Down, target); Pointer(editor, OverlayPointerKind.Move, target + new PointD(30, 20)); Pointer(editor, OverlayPointerKind.Cancel, target);
            check("cancel restores the exact committed local endpoint geometry", Json(session.Document) == committed && session.UndoCount == 1 && !session.IsTransactionActive);
        });

        Case("selected group carries even an entirely locked set of children", () =>
        {
            var fixture = PopulatedNested(); var graph = new SceneGraph(fixture.Document);
            var a = Box("locked a", 20, 20); a.Locked = true; var b = Box("locked b", 100, 20); b.Locked = true;
            graph.InsertElement(a, fixture.Inner, worldCoordinates: false); graph.InsertElement(b, fixture.Inner, worldCoordinates: false);
            var session = CreateSession(fixture.Document); session.SetSelection([fixture.Inner]); var editor = new EditorController(session) { Tool = EditorTool.Select };
            var bounds = session.SelectionBounds()!.Value; var grab = bounds.Center; var offset = new PointD(28, -19);
            var localBefore = Json(session.Document.Elements); var placement = graph.FindGroup(fixture.Inner)!.Transform;
            Pointer(editor, OverlayPointerKind.Down, grab); Pointer(editor, OverlayPointerKind.Move, grab + offset); Pointer(editor, OverlayPointerKind.Up, grab + offset);
            graph = new(session.Document);
            check("locked descendants remain part of their unlocked parent group's drag", Json(session.Document.Elements) == localBefore && graph.FindGroup(fixture.Inner)!.Transform != placement && session.UndoCount == 1);
            var moved = session.SelectionBounds()!.Value;
            Near(moved.Center, grab + offset, "the complete locked-child group's displayed frame follows the pointer");
        });

        Case("a late controller Up does not commit a foreign transaction", () =>
        {
            var fixture = PopulatedNested(); var session = CreateSession(fixture.Document); session.EnterScope(fixture.Inner);
            var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
            Pointer(editor, OverlayPointerKind.Down, new(350, 220)); Pointer(editor, OverlayPointerKind.Move, new(400, 260));
            session.Cancel(); session.BeginTransaction("foreign owner"); long foreign = session.TransactionId;
            session.Document.NextNumber = 77; session.NotifySettingsChanged();
            Pointer(editor, OverlayPointerKind.Up, new(410, 270));
            check("a stale pointer edge preserves the newer owner's active transaction", session.IsTransactionActive && session.TransactionId == foreign && session.Document.NextNumber == 77 && session.UndoCount == 0 && session.Document.Elements.Count(e => e.Name != "Scope fixture content") == 0);
            session.Cancel();
        });

        Case("controller cancellation respects transaction ownership", () =>
        {
            var session = new EditorSession(); var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
            Pointer(editor, OverlayPointerKind.Down, new(350, 220)); session.Cancel();
            session.BeginTransaction("newer inspector edit"); long foreign = session.TransactionId; session.Document.NextNumber = 88; session.NotifySettingsChanged();
            editor.Cancel();
            check("clearing an obsolete controller gesture cannot roll back a newer inspector edit", session.IsTransactionActive && session.TransactionId == foreign && session.Document.NextNumber == 88);
            session.Cancel();
        });

        Case("stale move and modifier samples preserve a newer owner", () =>
        {
            foreach (bool modifier in new[] { false, true })
            {
                var session = new EditorSession(); var editor = new EditorController(session) { Tool = EditorTool.Freehand };
                Pointer(editor, OverlayPointerKind.Down, new(350, 220)); Pointer(editor, OverlayPointerKind.Move, new(370, 240)); session.Cancel();
                session.BeginTransaction("foreign after stroke"); long foreign = session.TransactionId; session.Document.NextNumber = 89; session.NotifySettingsChanged();
                if (modifier) editor.ModifiersChanged(OverlayModifiers.Shift, new(390, 260), pointerLeftDown: true);
                else Pointer(editor, OverlayPointerKind.Move, new(390, 260));
                Pointer(editor, OverlayPointerKind.Up, new(390, 260));
                check("a stale " + (modifier ? "modifier boundary" : "move sample") + " cannot write or finish a foreign transaction", session.IsTransactionActive &&
                    session.TransactionId == foreign && session.Document.NextNumber == 89 && session.Document.Elements.Count(e => e.Name != "Scope fixture content") == 0 && session.UndoCount == 0);
                session.Cancel();
            }
        });

        Case("new left and middle gestures cannot displace a foreign owner", () =>
        {
            foreach (var button in new[] { OverlayMouseButton.Left, OverlayMouseButton.Middle })
            {
                var session = new EditorSession(); var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
                session.BeginTransaction("foreign before input"); long foreign = session.TransactionId; session.Document.NextNumber = 90; session.NotifySettingsChanged();
                editor.Pointer(new(OverlayPointerKind.Down, new(350, 220), button, OverlayModifiers.None));
                editor.Pointer(new(OverlayPointerKind.Move, new(390, 260), button, OverlayModifiers.None));
                editor.Pointer(new(OverlayPointerKind.Up, new(390, 260), button, OverlayModifiers.None));
                check("new " + button + " input cannot cancel or commit another UI owner's active transaction", session.IsTransactionActive && session.TransactionId == foreign &&
                    session.Document.NextNumber == 90 && session.Document.Elements.Count(e => e.Name != "Scope fixture content") == 0 && !editor.IsTemporaryMove);
                session.Cancel();
            }
        });

        Case("presentation suspension and replacement preserve unrelated ownership", () =>
        {
            var session = new EditorSession(); var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
            Pointer(editor, OverlayPointerKind.Down, new(350, 220)); session.Cancel(); session.BeginTransaction("foreign after presentation");
            long foreign = session.TransactionId; session.Document.NextNumber = 91; session.NotifySettingsChanged(); editor.SuspendPresentedPicking();
            check("presentation suspension clears stale pointer state without canceling a foreign owner", session.IsTransactionActive && session.TransactionId == foreign && session.Document.NextNumber == 91);
            session.Cancel();
            var replacement = new EditorSession(); replacement.BeginTransaction("new document owner"); long replacementId = replacement.TransactionId;
            replacement.Document.NextNumber = 92; replacement.NotifySettingsChanged(); editor.ReplaceSession(replacement, default);
            Pointer(editor, OverlayPointerKind.Up, new(400, 260));
            check("replacing the controller session cannot publish an old pointer gesture into the new document", replacement.IsTransactionActive && replacement.TransactionId == replacementId && replacement.Document.NextNumber == 92 && replacement.Document.Elements.Count == 0);
            replacement.Cancel();
        });

        foreach (bool lateUp in new[] { false, true })
            Case("stale cancellation notification cannot finish a new inline owner " + lateUp, () =>
            {
                var session = new EditorSession(); var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
                Pointer(editor, OverlayPointerKind.Down, new(350, 220)); session.Cancel();
                session.BeginTransaction("new inline owner"); long foreign = session.TransactionId; session.Document.NextNumber = 94; session.NotifySettingsChanged();
                int notifications = 0;
                editor.Cancelling += () => { notifications++; if (session.IsTransactionActive) session.Commit(); };
                if (lateUp) Pointer(editor, OverlayPointerKind.Up, new(400, 260)); else editor.Cancel();
                check("a stale " + (lateUp ? "Up" : "Cancel") + " suppresses the FinishInlineText notification for a foreign owner", notifications == 0 &&
                    session.IsTransactionActive && session.TransactionId == foreign && session.Document.NextNumber == 94 && session.UndoCount == 0);
                session.Cancel();
            });

        Case("completed pointer ownership cannot suppress a later normal inline finish", () =>
        {
            var session = new EditorSession(); var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
            Pointer(editor, OverlayPointerKind.Down, new(350, 220)); Pointer(editor, OverlayPointerKind.Move, new(400, 260)); Pointer(editor, OverlayPointerKind.Up, new(400, 260));
            session.BeginTransaction("normal inline edit"); session.Document.NextNumber = 95; session.NotifySettingsChanged(); int notifications = 0;
            editor.Cancelling += () => { notifications++; if (session.IsTransactionActive) session.Commit(); };
            editor.Cancel();
            check("a finished pointer gesture clears its token so a later normal inline Cancel still notifies", notifications == 1 && !session.IsTransactionActive && session.Document.NextNumber == 95 && session.UndoCount == 2);
        });

        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.Freehand, EditorTool.Line, EditorTool.Number, EditorTool.Emoji })
            Case("a locked ancestor blocks new controller creation " + tool, () =>
            {
                var fixture = PopulatedNested(); var session = CreateSession(fixture.Document); session.EnterScope(fixture.Inner);
                var parent = new SceneGraph(session.Document).ParentId(fixture.Inner)!.Value; session.SetNodeLocked(parent, true);
                var editor = new EditorController(session) { Tool = tool }; string before = Json(session.Document); int history = session.UndoCount;
                Pointer(editor, OverlayPointerKind.Down, new(350, 220));
                check("locked-scope " + tool + " rejects before starting a transaction or inserting a draft", !session.IsTransactionActive &&
                    Json(session.Document) == before && session.UndoCount == history && session.Document.Elements.Count(e => e.Name != "Scope fixture content") == 0);
                Pointer(editor, OverlayPointerKind.Move, new(400, 260)); Pointer(editor, OverlayPointerKind.Up, new(400, 260));
                check("locked-scope " + tool + " cannot publish later pointer samples or advance number state", Json(session.Document) == before && session.UndoCount == history && !session.IsTransactionActive);
            });

        Case("locked-scope snapshot insertion publishes no resource", () =>
        {
            var fixture = PopulatedNested(); var session = CreateSession(fixture.Document); session.EnterScope(fixture.Inner);
            session.SetNodeLocked(new SceneGraph(session.Document).ParentId(fixture.Inner)!.Value, true);
            using var surface = SKSurface.Create(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul)); surface.Canvas.Clear(SKColors.Red);
            using var image = surface.Snapshot(); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
            var raster = new CaptureRasterResult(bytes.ToArray(), new(350, 220, 2, 2), true); string before = Json(session.Document); int history = session.UndoCount;
            bool rejected = false; try { SnapshotImageCommand.Insert(session, raster); } catch (InvalidOperationException) { rejected = true; }
            check("a locked-scope capture rejects the insertion while preserving its source graph", rejected && Json(session.Document) == before && session.UndoCount == history && !session.IsTransactionActive);
            check("a locked-scope capture rejects before adding newly captured PNG bytes", session.Assets.Count == 0);
        });

        Case("individual position fields set each selected layer to the same coordinate", () =>
        {
            var a = Box("first", 10, 20); var b = Box("second", 180, 90);
            var session = CreateSession(new() { Elements = [a,b] }); session.SetSelection([a.Id,b.Id]); session.TransformIndividually = true;
            var panel = new NodeTransformPanel(() => session, () => true); panel.Refresh();
            var fields = Field<Dictionary<string, NumericValueField>>(panel,"fields");
            fields["X"].Input.Text = "300"; Invoke(panel,"Publish","X"); panel.CompletePendingEdit();
            check("individual X assigns a shared left edge and preserves each Y", session.Document.Elements.All(e => Math.Abs(ElementGeometry.Bounds(e).Left-300)<1e-6) &&
                session.Document.Elements[0].Transform.OffsetY==20 && session.Document.Elements[1].Transform.OffsetY==90);
            fields["Y"].Input.Text = "200"; Invoke(panel,"Publish","Y"); panel.CompletePendingEdit();
            check("individual Y assigns a shared top edge", session.Document.Elements.All(e => Math.Abs(ElementGeometry.Bounds(e).Top-200)<1e-6));
            check("individual coordinates retain separate undo steps",session.UndoCount==2 && session.Undo() && session.Undo() && session.Document.Elements[1].Transform.OffsetX==180);
        });

        Case("node property panel owns only its transaction", () =>
        {
            var fixture = PopulatedNested(); var graph = new SceneGraph(fixture.Document); graph.InsertElement(Box("panel child", 10, 20), fixture.Inner, worldCoordinates: false);
            var session = CreateSession(fixture.Document); session.SetSelection([fixture.Inner]); bool mutable = true;
            var panel = new NodeTransformPanel(() => session, () => mutable); panel.Refresh();
            var fields = Field<Dictionary<string, NumericValueField>>(panel, "fields"); var original = Json(session.Document);
            fields["X"].Input.Text = (session.SelectionBounds()!.Value.Left + 20).ToString(CultureInfo.CurrentCulture); Invoke(panel, "Publish", "X");
            check("node properties begin their own live transform transaction", session.IsTransactionActive && panel.IsEditing);
            string accepted = Json(session.Document); mutable = false;
            fields["X"].Input.Text = (session.SelectionBounds()!.Value.Left + 20).ToString(CultureInfo.CurrentCulture); Invoke(panel, "Publish", "X");
            check("revoking mutation access blocks further live node-property samples", Json(session.Document) == accepted || Json(session.Document) == original);
            panel.CompletePendingEdit();
            check("losing mutation access cancels the owned node property preview", Json(session.Document) == original && !session.IsTransactionActive && session.UndoCount == 0);
            mutable = true; panel.Refresh(); fields["X"].Input.Text = (session.SelectionBounds()!.Value.Left + 25).ToString(CultureInfo.CurrentCulture); Invoke(panel, "Publish", "X");
            session.Cancel(); session.BeginTransaction("foreign panel edit"); long foreign = session.TransactionId; session.Document.NextNumber = 93; session.NotifySettingsChanged();
            panel.CompletePendingEdit();
            check("completing an obsolete node property panel cannot commit a newer transaction", session.IsTransactionActive && session.TransactionId == foreign && session.Document.NextNumber == 93);
            session.Cancel();
        });

        Case("mixed group and leaf property pivots operate in world coordinates", () =>
        {
            var fixture = PopulatedNested(); var graph = new SceneGraph(fixture.Document);
            var child = graph.InsertElement(Box("pivot group child", 10, 20), fixture.Inner, worldCoordinates: false);
            var peer = graph.InsertElement(Box("pivot root peer", 450, 300));
            var session = CreateSession(fixture.Document); session.SetSelection([fixture.Inner, peer.Id]);
            var panel = new NodeTransformPanel(() => session, () => true); panel.Refresh(); var fields = Field<Dictionary<string, NumericValueField>>(panel, "fields");
            var initial = SceneEvaluation.Build(session.Document); var pivot = session.SelectionBounds()!.Value.Center; var delta = Affine2D.Around(pivot, Affine2D.Rotation(90));
            var childLocal = Json(graph.FindElement(child.Id)!);
            fields["Angle"].Input.Text = "90"; Invoke(panel, "Publish", "Angle"); panel.CompletePendingEdit();
            foreach (var id in new[] { child.Id, peer.Id })
                Near(ElementGeometry.Bounds(SceneEvaluation.Build(session.Document).FindElement(id)!, false).Center,
                    delta.TransformPoint(ElementGeometry.Bounds(initial.FindElement(id)!, false).Center), "overall numeric rotation shares one world pivot across a group and an ungrouped leaf");
            check("numeric group rotation preserves its child's local content", Json(new SceneGraph(session.Document).FindElement(child.Id)!) == childLocal);
            check("overall property rotation is reversible as one operation", session.UndoCount == 1 && session.Undo());
            panel.Refresh(); Field<SegmentedChoice>(panel, "mode").SelectedValue = true;
            fields["Angle"].Input.Text = "90"; Invoke(panel, "Publish", "Angle"); panel.CompletePendingEdit();
            foreach (var id in new[] { child.Id, peer.Id })
                Near(ElementGeometry.Bounds(SceneEvaluation.Build(session.Document).FindElement(id)!, false).Center,
                    ElementGeometry.Bounds(initial.FindElement(id)!, false).Center, "individual numeric rotation preserves each selected node's world center");
        });

        Case("single-leaf Inspector edits source-local geometry and validates world placement", () =>
        {
            var fixture = PopulatedNested(); var graph = new SceneGraph(fixture.Document); var leaf = graph.InsertElement(Box("inspector child", 20, 30), fixture.Inner, worldCoordinates: false);
            var session = CreateSession(fixture.Document); session.EnterScope(fixture.Inner); session.SetSelection([leaf.Id]);
            var inspector = new InspectorWindow(() => session); using var surface = new InspectorTestSurface(inspector);
            var fields = Field<Dictionary<string, TextBox>>(inspector, "fields"); var parent = graph.NodeWorldPlacement(fixture.Inner); var before = graph.ToWorld(leaf);
            fields["X"].Text = "27"; inspector.CompletePendingEdit(); graph = new(session.Document);
            check("single grouped-leaf Inspector coordinates remain source-local", Math.Abs(ElementGeometry.Bounds(graph.FindElement(leaf.Id)!, false).Left - 27) < 1e-7);
            Near(graph.ToWorld(graph.FindElement(leaf.Id)!).Transform.TransformPoint(default), parent.PlaneTransform.TransformPoint(new(27, 30)), "a local Inspector edit is projected through the parent exactly once");
            check("single-leaf Inspector geometry retains parent placement and one undo entry", graph.NodeWorldPlacement(fixture.Inner) == parent && session.UndoCount == 1);
        });

        Case("Inspector rejects a locally-safe edit whose parent crosses the world horizon", () =>
        {
            var document = new SceneDocument { Viewport = new(-250, -500, 500, 900) }; var initial = CanvasCalibration.CreateInitial(document.Viewport, 4, 5);
            PointD[] quad = [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)];
            check("Inspector world-safety fixture has a strong valid projection", CanvasCalibration.TryCalibrate(initial, quad, document.Viewport, out var projection, out _));
            document.Canvas.Projection = projection; var graph = new SceneGraph(document);
            var basePoint = projection.Origin + new PointD(40, 40);
            var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Around(basePoint, Affine2D.Rotation(90)) });
            var child = graph.InsertElement(Box("safety child", basePoint.X, basePoint.Y), group.Id, worldCoordinates: false);
            var session = new EditorSession(document); session.EnterScope(group.Id); session.SetSelection([child.Id]);
            var inspector = new InspectorWindow(() => session); using var surface = new InspectorTestSurface(inspector); string before = Json(session.Document);
            var fields = Field<Dictionary<string, TextBox>>(inspector, "fields");
            var mapping = CanvasMapping.Create(document);
            var candidate = new[] { 100d, 500, 1000, 10000, 100000 }.Select(distance =>
            {
                var draft = child.Clone(); draft.Transform = Affine2D.Translation(basePoint.X - distance, basePoint.Y); return draft;
            }).FirstOrDefault(draft => mapping.IsElementSafe(draft) && !mapping.IsElementSafe(graph.ToWorld(draft))) ??
                throw new InvalidOperationException("Cannot establish the rotated-parent safety fixture.");
            check("candidate is safe in raw local coordinates but unsafe after the actual parent", mapping.IsElementSafe(candidate) && !mapping.IsElementSafe(graph.ToWorld(candidate)));
            fields["X"].Text = candidate.Transform.OffsetX.ToString(CultureInfo.CurrentCulture);
            check("Inspector world safety rejects the edit before modifying source geometry or history", Json(session.Document) == before && session.UndoCount == 0 && !session.IsTransactionActive);
            inspector.CompletePendingEdit();
        });

        if (failures.Count > 0) throw new InvalidOperationException("Node controller audit failures:\n" + string.Join("\n", failures));
    }

    private static (SceneDocument Document, Guid Inner) PopulatedNested()
    {
        var document = new SceneDocument { Viewport = new(0, 0, 1000, 800) }; var graph = new SceneGraph(document);
        var outer = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(80, 70).Compose(Affine2D.Rotation(25)).Compose(Affine2D.Scale(1.4, .8)) });
        var inner = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(90, 50).Compose(Affine2D.Rotation(-15)) }, outer.Id);
        return (document, inner.Id);
    }
    private static EditorSession CreateSession(SceneDocument document)
    {
        if (document.Elements.Count == 0)
            new SceneGraph(document).InsertElement(new() { Name = "Scope fixture content", Kind = ElementKind.Rectangle, Width = 1, Height = 1, Visible = false }, document.Groups[^1].Id, worldCoordinates: false);
        return new(document);
    }
    private static CanvasElement Box(string name, double x, double y) => new()
    {
        Name = name, Kind = ElementKind.Rectangle, Width = 40, Height = 30, Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF2468AA, StrokeWidth = 0 }
    };
    private static void Pointer(EditorController editor, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, new(point.X + editor.ScreenOrigin.X, point.Y + editor.ScreenOrigin.Y), OverlayMouseButton.Left, modifiers));
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
}
