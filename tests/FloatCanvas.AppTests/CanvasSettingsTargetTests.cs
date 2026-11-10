using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CanvasSettingsTargetTests
{
    internal static void Run(Action<string, bool> check)
    {
        GroupAndRootTargets(check); Gestures(check); Calibration(check); SafetyCoordinates(check);
    }

    private static EditorSession GroupSession(out Guid groupId)
    {
        var group = new CanvasGroup { Transform = Affine2D.Translation(300, 200), PreviewCanvas = new() { Origin = new(20, 30), Viewport = new(0, 0, 800, 600) } };
        group.PreviewCanvas.Canvas.Grid.FlatCellSize = 40;
        group.PreviewCanvas.Canvas.Projection = CanvasGridGeometry.CreateFlatReference(group.PreviewCanvas.Viewport, 8, 6, 40);
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30, GroupId = group.Id, Transform = Affine2D.Translation(100, 100) };
        var session = new EditorSession(new() { Groups = [group], Elements = [leaf], Viewport = new(0, 0, 1200, 900) });
        session.SetEditScope(group.Id); groupId = group.Id; return session;
    }

    private static void GroupAndRootTargets(Action<string, bool> check)
    {
        var session = GroupSession(out var groupId); var independent = new CanvasSettingsTarget(session, true);
        string root = JsonSerializer.Serialize(session.Document.Canvas); var placement = session.Document.Groups[0].Transform;
        independent.Change(settings => settings.BackgroundColor = 0xff123456);
        check("independent group canvas settings write its own header and preserve the project canvas and placement", session.Document.Groups[0].PreviewCanvas!.Canvas.BackgroundColor == 0xff123456 && JsonSerializer.Serialize(session.Document.Canvas) == root && session.Document.Groups[0].Transform == placement && session.UndoCount == 1);
        independent.ChangeOpacity(.45);
        check("independent group opacity belongs to its preview rather than the host project or group transform", session.Document.Groups[0].PreviewCanvas!.LayerOpacity == .45 && session.Document.LayerOpacity == 1 && session.Document.Groups[0].Transform == placement);
        check("independent canvas opacity supports exact undo and redo", session.Undo() && session.Document.Groups[0].PreviewCanvas!.LayerOpacity == 1 && session.Redo() && session.Document.Groups[0].PreviewCanvas!.LayerOpacity == .45);
        var project = new CanvasSettingsTarget(session); project.Change(settings => settings.BackgroundColor = 0xffabcdef);
        check("root-project editing inside the same group targets the project canvas", session.Document.Canvas.BackgroundColor == 0xffabcdef && session.Document.Groups[0].PreviewCanvas!.Canvas.BackgroundColor == 0xff123456);
        independent = new(session, true); session.SetEditScope(null);
        check("a stale entered-group target rejects edits after leaving the group", !independent.Change(settings => settings.BackgroundColor = 0xff000000) && !independent.ChangeOpacity(.1) && session.Document.Groups.Single(group => group.Id == groupId).PreviewCanvas!.LayerOpacity == .45);
        session.SetRootCanvas(RootCanvasKind.Foreground); independent = new(session, true);
        independent.Change(settings => { settings.BackgroundColor = 0xff345678; settings.Projection.Enabled = true; }); independent.ChangeOpacity(.6);
        check("foreground independent preview owns perspective color and opacity without changing screen-plane project composition", session.Document.RootPreviewCanvases[RootCanvasKind.Foreground] is { LayerOpacity: .6, Canvas.BackgroundColor: 0xff345678, Canvas.Projection.Enabled: true } && session.Document.Canvas.BackgroundColor == 0xffabcdef && session.Document.LayerOpacity == 1 && !CanvasMapping.Create(session.Document, RootCanvasKind.Foreground).IsPerspective);
        session.SetRootCanvas(RootCanvasKind.Background);
        check("a target cannot silently follow selection into another root canvas", !independent.ChangeOpacity(.2) && session.Document.RootPreviewCanvases[RootCanvasKind.Foreground].LayerOpacity == .6);
        bool context = true; var captured = new CanvasSettingsTarget(session, true, () => context); context = false;
        check("mode and prefab context guards reject late callbacks even when the session and root stay the same", !captured.Change(settings => settings.Grid.Visible = true));
    }

    private static void Gestures(Action<string, bool> check)
    {
        var session = GroupSession(out _); string before = JsonSerializer.Serialize(session.Document); bool context = true;
        var edit = new CanvasColorEdit(() => session, () => true, () => { }, () => { }, () => new(session, true, () => context));
        check("group canvas colors begin a normal settings transaction", edit.Begin());
        edit.Preview(settings => settings.BackgroundColor = 0xff010203); edit.Preview(settings => settings.Grid.Width = 5);
        check("group canvas color and numeric preview keep the committed recovery snapshot unchanged", session.IsTransactionActive && session.UndoCount == 0 && session.Document.Groups[0].PreviewCanvas!.Canvas.Grid.Width == 5 && JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == before);
        edit.Complete(true);
        check("one independent canvas gesture commits once and undo restores its complete header", session.UndoCount == 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        context = true; edit.Begin(); edit.Preview(settings => settings.Grid.Width = 7); context = false;
        check("late numeric input after a view-mode change cannot update another canvas", !edit.Preview(settings => settings.Grid.Width = 9));
        edit.Complete(true);
        check("ending a stale canvas gesture cancels its owned transaction without creating history", !session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == before && session.UndoCount == 0);
        var scale = new CanvasGridScaleController(); var target = new CanvasSettingsTarget(session, true); var reference = target.Viewport;
        var start = reference.Center + new PointD(100, 0); string main = JsonSerializer.Serialize(session.Document.Canvas);
        check("flat grid dragging accepts an independent group canvas", scale.Down(session, start, reference, target));
        scale.Move(reference.Center + new PointD(200, 0)); scale.Up();
        check("flat grid dragging changes only the independent grid in one history unit", session.Document.Groups[0].PreviewCanvas!.Canvas.Grid.FlatCellSize > 40 && JsonSerializer.Serialize(session.Document.Canvas) == main && session.UndoCount == 1);
    }

    private static void Calibration(Action<string, bool> check)
    {
        var session = GroupSession(out _); session.ChangeEditingSubprojectCanvas("projection", header => header.Canvas.Projection.Enabled = true);
        var target = new CanvasSettingsTarget(session, true); var calibration = new CanvasCalibrationController(); var original = target.Settings.Projection.Quad.ToArray();
        var host = JsonSerializer.Serialize(session.Document.Canvas); int undo = session.UndoCount;
        calibration.Begin(session, target);
        check("independent group calibration picks its own corner coordinates", calibration.Down(original[0]));
        calibration.Move(original[0] + new PointD(-20, -10), FloatCanvas.Windows.OverlayModifiers.Control); calibration.Up();
        if (calibration.IsAwaitingResolution) calibration.Resolve(CanvasSafetyAction.Keep);
        check("four-corner calibration previews and commits only the group's projection", session.Document.Groups[0].PreviewCanvas!.Canvas.Projection.Quad[0] == original[0] + new PointD(-20, -10) && JsonSerializer.Serialize(session.Document.Canvas) == host && session.UndoCount == undo + 1);
        check("group calibration undo restores its prior four corners", session.Undo() && session.Document.Groups[0].PreviewCanvas!.Canvas.Projection.Quad.SequenceEqual(original));
        calibration.End();
        session.SetEditScope(null); session.SetRootCanvas(RootCanvasKind.Foreground); target = new(session, true);
        calibration.BeginPlacement(session, target);
        foreach (var point in new[] { new PointD(50, 50), new(650, 50), new(650, 450), new(50, 450) }) calibration.Down(point);
        calibration.Up(); if (calibration.IsAwaitingResolution) calibration.Resolve(CanvasSafetyAction.Keep);
        check("four-point placement stores a foreground preview projection without touching the Main canvas", session.Document.RootPreviewCanvases[RootCanvasKind.Foreground].Canvas.Projection.Quad.Count == 4 && JsonSerializer.Serialize(session.Document.Canvas) == host);
        calibration.End();
    }

    private static void SafetyCoordinates(Action<string, bool> check)
    {
        var session = GroupSession(out var groupId); var target = new CanvasSettingsTarget(session, true);
        var display = target.CreateDisplayDocument(); var leaf = display.Elements.Single(); var delta = new PointD(12, 18);
        var expected = session.Document.Elements.Single().Transform.Compose(Affine2D.Translation(delta.X, delta.Y));
        SceneGraph.ApplyPlacement(leaf, new(Affine2D.Translation(delta.X, delta.Y), 1));
        session.BeginTransaction("relocate"); target.ResolveGeometry(display, [leaf.Id], CanvasSafetyAction.Relocate); session.Commit();
        check("safety relocation converts independent display coordinates back to local authored coordinates", session.Document.Elements.Single().Transform == expected && session.Document.Elements.Single().GroupId == groupId && session.Document.Groups[0].Transform == Affine2D.Translation(300, 200));
        display = new CanvasSettingsTarget(session, true).CreateDisplayDocument(); session.BeginTransaction("delete");
        new CanvasSettingsTarget(session, true).ResolveGeometry(display, [leaf.Id], CanvasSafetyAction.Delete); session.Commit();
        check("independent safety deletion dissolves its now-empty group and normalizes the scope", session.Document.Elements.Count == 0 && session.Document.Groups.Count == 0 && session.EditScopeId is null);
        check("one safety-deletion undo restores the containing group and artwork", session.Undo() && session.Document.Elements.Single().GroupId == groupId && session.Document.Groups.Single().Id == groupId);
    }
}
