using FloatCanvas.Core;
using System.Text.Json;

internal static class DataRemediationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var stroke = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(0, 0), new(10, 10)] };
        var copy = stroke.Clone(); stroke.Points[0] = new(5, 5); copy.Points.Add(new(20, 20));
        check(copy.Points[0] == new PointD(0, 0) && stroke.Points.Count == 2, "COW path isolates edits in both directions");
        var decoded = JsonSerializer.Deserialize<CanvasElement>(JsonSerializer.Serialize(stroke))!;
        check(decoded.Points.SequenceEqual(stroke.Points), "path JSON remains a compatible array");
        var session = new EditorSession(new() { Elements = [stroke] }); session.SelectAll(); long saved = session.ContentVersion;
        session.TransformSelection(Affine2D.Translation(1, 0)); long changed = session.ContentVersion;
        check(changed != saved, "committed edit has distinct content identity");
        session.Undo(); check(session.ContentVersion == saved, "undo returns to saved identity");
        session.Redo(); check(session.ContentVersion == changed, "redo restores exact content identity");
        session.BeginTransaction("cancel"); session.Document.Elements[0].Points[0] = new(99, 99); session.NotifyChanged();
        check(session.HasPendingChanges, "preview dirty state is tracked without hashing");
        session.Cancel(); check(session.ContentVersion == changed && !session.HasPendingChanges && session.Document.Elements[0].Points[0] == new PointD(5, 5), "cancel restores content identity and path");
        session.Execute("no-op", _ => { }); check(session.ContentVersion == changed, "no-op does not lose saved identity");
        var grouped = new EditorSession(new() { Elements = [new() { Width = 5, Height = 5 }, new() { Width = 6, Height = 6 }] }); grouped.SelectAll();
        for (int i = 0; i < 64; i++) grouped.GroupSelection();
        SceneLimits.ValidateCapacity(grouped.Document);
        string before = JsonSerializer.Serialize(grouped.Document); int undo = grouped.UndoCount; var selection = grouped.Selection.ToArray();
        bool rejected = false; try { grouped.GroupSelection(); } catch (InvalidDataException) { rejected = true; }
        check(rejected && JsonSerializer.Serialize(grouped.Document) == before && grouped.UndoCount == undo && grouped.Selection.SetEquals(selection), "depth 65 rejects atomically before history publication");
        grouped.Undo(); SceneLimits.ValidateCapacity(grouped.Document); grouped.Redo(); SceneLimits.ValidateCapacity(grouped.Document);
        check(grouped.Document.Groups.Count == 64, "depth 63/64 undo and redo stay persistable");
        var capacityMethod = typeof(SceneLimits).GetMethod("ValidateJsonCapacity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        long jsonLength = JsonSerializer.SerializeToUtf8Bytes(session.Document).LongLength;
        capacityMethod.Invoke(null, [session.Document, jsonLength]); rejected = false;
        try { capacityMethod.Invoke(null, [session.Document, jsonLength - 1]); }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
        check(rejected, "streamed scene JSON capacity accepts exact bytes and rejects the next byte");
        foreach (var pattern in new[] { StrokePattern.Dashed, StrokePattern.Dotted })
        foreach (int length in new[] { 1_000, 10_000, 100_000, 1_000_000 })
        {
            var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(length, 0)], Style = new() { StrokeWidth = .1, StrokePattern = pattern } };
            check(ElementGeometry.HitTest(line, new(length / 2.0, 3)), $"bounded long {pattern} hits within screen tolerance at {length}");
            check(!ElementGeometry.HitTest(line, new(length / 2.0, 8)), $"bounded long {pattern} rejects outside tolerance at {length}");
        }
    }
}
