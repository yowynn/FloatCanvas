using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class CanvasIndependentStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "independent-canvas-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var viewport = new RectD(0, 0, 1500, 1000);
        var canvas = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(viewport), Grid = new() { FlatCellSize = 91.456, FlatOrigin = new(750, 500) } };
        CanvasProjectionGeometry.InitializePlane(canvas);
        check(CanvasProjectionGeometry.TryResize(canvas.Projection, 840, 1188, out var resized, out _), "independent paper storage fixture changes the source dimensions");
        canvas.Projection = resized; canvas.Projection.Enabled = true;
        canvas.Grid.PlaneCellSize = 37.125; canvas.Grid.PlaneOrigin = new(-12.125, 143);
        var document = new SceneDocument { Viewport = viewport, Canvas = canvas };
        var assets = new AssetLibrary(); var store = new ProjectStore("0.4.0"); var path = Path.Combine(directory, "a4.floatcanvas");
        string before = JsonSerializer.Serialize(document.Canvas);
        await store.SaveAsync(path, document, assets);
        var loaded = (await store.LoadAsync(path)).Document;
        check(JsonSerializer.Serialize(loaded.Canvas) == before && JsonSerializer.Serialize(document.Canvas) == before,
            "v9 project preserves independent plane, lattice, separate flat spacing and the untouched source");
        check(loaded.Canvas.Projection.Matrix == canvas.Projection.Matrix && loaded.Canvas.Projection.PinPoint == canvas.Projection.PinPoint,
            "v9 loading never recalibrates fixed paper geometry or its pin");
        var capture = document.Clone(); capture.Kind = DocumentKind.FrozenCapture; capture.Canvas.Projection.Enabled = false;
        capture.Canvas.Grid.PlaneCellSize = 18.75; capture.Canvas.Grid.FlatCellSize = 54.321;
        var recovery = new RecoveryStore(Path.Combine(directory, "recovery"), "0.4.0");
        await recovery.SaveAsync(new() { LiveDocument = document, CaptureDocument = capture }, assets);
        var restored = (await recovery.LoadAsync())!.State;
        check(JsonSerializer.Serialize(restored.LiveDocument.Canvas) == before &&
            JsonSerializer.Serialize(restored.CaptureDocument!.Canvas) == JsonSerializer.Serialize(capture.Canvas),
            "recovery retains different enabled and dormant independent plane/lattice settings");
        byte[] original = await File.ReadAllBytesAsync(path);
        foreach (var corrupt in new Action<CanvasSettings>[]
        {
            c => c.Grid.PlaneCellSize = 0,
            c => c.Grid.PlaneOrigin = new(double.NaN, 0),
            c => c.Projection.PlaneBounds = c.Projection.PlaneBounds!.Value with { Width = 841 }
        })
        {
            var invalid = document.Clone(); corrupt(invalid.Canvas); bool rejected = false;
            try { await store.SaveAsync(path, invalid, assets); } catch (InvalidDataException) { rejected = true; }
            check(rejected && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(original),
                "invalid independent canvas data cannot replace an existing valid package");
        }
    }
}
