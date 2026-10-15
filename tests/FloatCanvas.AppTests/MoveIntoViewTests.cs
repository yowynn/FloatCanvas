using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class MoveIntoViewTests
{
    internal static void Run(Action<string, bool> check)
    {
        var results = new List<(string Name, bool Passed)>();
        foreach (var screenOrigin in new[] { new PointD(0, 0), new PointD(-800, -300) })
        {
            var document = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(1000, 500, 600, 400),
                Background = new() { Kind = ElementKind.Image, Width = 1920, Height = 1080, Locked = true },
                Elements = [Box(1100, 600)] };
            var session = new EditorSession(document);
            string before = JsonSerializer.Serialize(document);
            var originalBackground = JsonSerializer.Serialize(document.Background);
            Execute(session, screenOrigin);
            var actual = ElementGeometry.Bounds(session.Document.Elements.Single());
            bool visible = actual.Intersects(document.Viewport) && document.Viewport.Contains(actual.Center);
            results.Add(($"Frozen crop stays visible after MoveIntoView at screen origin {screenOrigin}", visible));
            results.Add(($"MoveIntoView preserves frozen viewport/background at origin {screenOrigin}",
                session.Document.Viewport == new RectD(1000, 500, 600, 400) && JsonSerializer.Serialize(session.Document.Background) == originalBackground));
            results.Add(($"MoveIntoView is one undoable operation at origin {screenOrigin}",
                session.UndoCount == 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before));
        }
        var movable = Box(1100, 600);
        var locked = Box(1400, 700); locked.Locked = true;
        var hidden = Box(1700, 1000); hidden.Visible = false;
        var mixed = new EditorSession(new() { Kind = DocumentKind.FrozenCapture, Viewport = new(1000, 500, 600, 400), Elements = [movable, locked, hidden] });
        var lockedTransform = locked.Transform; var hiddenTransform = hidden.Transform;
        Execute(mixed, new(0, 0));
        results.Add(("MoveIntoView does not transform a locked element", mixed.Document.Elements.Single(e => e.Id == locked.Id).Transform == lockedTransform));
        results.Add(("MoveIntoView does not transform a hidden element", mixed.Document.Elements.Single(e => e.Id == hidden.Id).Transform == hiddenTransform));
        var lockedOnly = new EditorSession(new() { Elements = [locked.Clone()] });
        string lockedBefore = JsonSerializer.Serialize(lockedOnly.Document);
        Execute(lockedOnly, new(0, 0));
        results.Add(("MoveIntoView with only locked content leaves document/history unchanged",
            lockedOnly.UndoCount == 0 && JsonSerializer.Serialize(lockedOnly.Document) == lockedBefore));
        var outside = new EditorSession(new() { Kind = DocumentKind.FrozenCapture, Viewport = new(3000, 2000, 600, 400), Elements = [Box(3100, 2100)] });
        string outsideBefore = JsonSerializer.Serialize(outside.Document);
        bool movedOutside = Execute(outside, new(0, 0));
        results.Add(("MoveIntoView safely leaves a fully off-screen crop unchanged", !movedOutside && outside.UndoCount == 0 && JsonSerializer.Serialize(outside.Document) == outsideBefore));
        var live = new EditorSession(new() { Viewport = new(0, 0, 1920, 1080), Elements = [Box(1100, 600)] });
        Execute(live, new(0, 0));
        var liveBounds = ElementGeometry.Bounds(live.Document.Elements.Single());
        results.Add(("MoveIntoView retains the normal live-work-area placement", liveBounds.Left == 40 && liveBounds.Top == 140));
        var group = new EditorSession(new() { Kind = DocumentKind.FrozenCapture, Viewport = new(1000, 500, 600, 400), Elements = [Box(1100, 600), Box(1250, 650)] });
        Execute(group, new(0, 0));
        results.Add(("MoveIntoView preserves editable group spacing in one undo step", group.UndoCount == 1 &&
            group.Document.Elements[1].Transform.OffsetX - group.Document.Elements[0].Transform.OffsetX == 150 &&
            group.Document.Elements[1].Transform.OffsetY - group.Document.Elements[0].Transform.OffsetY == 50));
        foreach (var result in results) Console.WriteLine($"MOVE_INTO_VIEW {result.Name}: passed={result.Passed}");
        foreach (var result in results) check(result.Name, result.Passed);
    }

    private static CanvasElement Box(double x, double y) => new() { Kind = ElementKind.Rectangle, Width = 80, Height = 60,
        Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF2383E2, StrokeWidth = 0 } };

    // The same command used by the actual menu; no DesktopSession/Host/UI fixture is constructed.
    private static bool Execute(EditorSession session, PointD screenOrigin) =>
        MoveIntoViewCommand.Execute(session, new(0, 40, 1920, 1040), screenOrigin);
}
