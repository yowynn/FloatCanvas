using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class CanvasPreservedLayersStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "preserved-layers-storage-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, .005, 1);
        var group = new CanvasGroup { Name = "Preserved mixed group" };
        var child = new CanvasGroup { Name = "Hidden locked billboards", ParentId = group.Id };
        var document = new SceneDocument { Viewport = new(0, -400, 1000, 1000), Groups = [group, child],
            Canvas = new() { Projection = new() { Enabled = true, Matrix = matrix, Columns = 100, Rows = 100, CellSize = 1, PinPlanePoint = new(0, 100), ReferenceBounds = new(0, 0, 100, 100),
                Quad = new PointD[] { new(0, 0), new(100, 0), new(100, 100), new(0, 100) }.Select(matrix.TransformPoint).ToList() } } };
        document.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 120, Height = 100,
            GroupId = group.Id, Transform = Affine2D.Translation(100, 4000), Style = new() { StrokeWidth = 0, Fill = 0xFF112233 } });
        foreach (double y in new[] { 4000d, -300d, -200d })
            document.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 70, Height = 120,
                GroupId = child.Id, IsBillboard = true, BillboardAnchor = new(100, y), BillboardHeight = -10,
                Transform = Affine2D.Rotation(25), Visible = false, Locked = true, Style = new() { StrokeWidth = 4 } });
        var mapping = CanvasMapping.Create(document);
        check(document.Elements.All(element => !mapping.IsElementSafe(element)), "preserved fixtures cover the near-horizon band, far side and exact pole");
        string original = JsonSerializer.Serialize(document);
        var store = new ProjectStore(); string path = Path.Combine(root, "preserved.floatcanvas");
        await store.SaveAsync(path, document, new());
        var loaded = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(loaded.Document) == original && loaded.Document.Version == SceneDocument.CurrentVersion,
            "preserved unsafe layers roundtrip exactly without a schema change or normalization");
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = document }, new());
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State.LiveDocument) == original,
            "recovery retains unsafe layers, nested groups and hidden locked billboard state");

        var restored = loaded.Document.Clone();
        restored.Canvas.Projection.Enabled = false;
        SceneValidation.Validate(restored);
        check(restored.Elements.All(element => CanvasMapping.Create(restored).IsElementSafe(element)) &&
            JsonSerializer.Serialize(restored.Elements) == JsonSerializer.Serialize(document.Elements),
            "restoring a flat mapping makes retained geometry safe without changing its logical content");

        foreach (var invalid in new (string Name, Action<CanvasElement> Edit)[]
        {
            ("nonfinite anchor", element => element.BillboardAnchor = new(double.NaN, -300)),
            ("nonfinite height", element => element.BillboardHeight = double.PositiveInfinity),
            ("singular local transform", element => element.Transform = Affine2D.Scale(0, 1)),
            ("singular projective placement", element => element.ProjectivePlacement = new(0, 0, 0, 0, 1, 0, 0, 0, 1)),
            ("invalid style", element => element.Style.StrokeWidth = double.NaN),
            ("negative geometry size", element => element.Width = -1),
            ("nonfinite path", element => element.Points = [new(double.NaN, 0)])
        })
        {
            var candidate = document.Clone(); invalid.Edit(candidate.Elements[1]);
            bool rejected = false;
            try { SceneValidation.Validate(candidate); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "retained unsafe billboard still rejects " + invalid.Name);
        }
    }
}
