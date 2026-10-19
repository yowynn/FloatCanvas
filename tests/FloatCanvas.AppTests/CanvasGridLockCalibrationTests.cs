using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class CanvasGridLockCalibrationTests
{
    internal static void Run(Action<string, bool> check)
    {
        var viewport = new RectD(0, 0, 1000, 800);
        PointD[] quad = [new(250, 180), new(750, 120), new(900, 650), new(100, 650)];
        check("locked calibration starts from the reported tilted reference", CanvasCalibration.TryCalibrate(
            CanvasCalibration.CreateInitial(viewport), quad, viewport, out var projection, out _));
        projection.LockedGridCellSize = projection.CellSize;
        var document = new SceneDocument { Viewport = viewport };
        document.Canvas.Projection = projection; document.Canvas.Grid.Visible = true;
        var anchor = projection.Origin + new PointD(4 * projection.GridCellSize, 4 * projection.GridCellSize);
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, IsBillboard = true,
            Width = 200, Height = 100, BillboardAnchor = anchor, BillboardHeight = 50 };
        document.Elements.Add(rectangle);
        string unchangedElement = JsonSerializer.Serialize(rectangle);
        var session = new EditorSession(document); var controller = new CanvasCalibrationController();
        var panel = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        var columns = (TextBox)typeof(CanvasSettingsWindow).GetField("lockedColumns", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        try
        {
            panel.Refresh(document.Canvas, true);
            controller.Changed += () => panel.RefreshGridCounts(session.Document.Canvas.Projection);
            controller.Begin(session);
            var edge = (quad[0] + quad[1]) * .5;
            check("locked top edge can begin a normal calibration gesture", controller.Down(edge));
            bool coincident = true, originFollows = true, liveCounts = true;
            for (int sample = 1; sample <= 10; sample++)
            {
                controller.Move(edge + new PointD(0, -5 * sample));
                var current = session.Document.Canvas.Projection;
                var mapping = CanvasMapping.Create(session.Document);
                var gridPoint = mapping.ToScreen(mapping.GridOrigin + new PointD(4 * mapping.GridCellSize, 4 * mapping.GridCellSize));
                var ground = mapping.ToScreen(rectangle.BillboardAnchor);
                var foot = mapping.ElementToScreen(rectangle).TransformPoint(new(100, 100));
                coincident &= ground.DistanceTo(gridPoint) < 1e-7 && foot.DistanceTo(ground) < 1e-7;
                originFollows &= mapping.ToScreen(mapping.GridOrigin).DistanceTo(current.Quad[0]) < 1e-7;
                liveCounts &= columns.Text == current.GridColumns.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
            }
            check("all intermediate tilted drag samples keep grid anchor and rectangle foot together", coincident);
            check("origin follows the original reference corner throughout the drag", originFollows);
            check("fractional count readout refreshes while the calibration transaction is still active", session.IsTransactionActive && liveCounts &&
                Math.Abs(session.Document.Canvas.Projection.GridColumns - 8) > .001);
            check("calibration retains every element value and its anchor", JsonSerializer.Serialize(rectangle) == unchangedElement);
            controller.Up();
            var changedQuad = session.Document.Canvas.Projection.Quad.ToArray();
            check("locked calibration commits as one undo step", session.UndoCount == 1 && !session.IsTransactionActive);
            session.Undo(); panel.RefreshGridCounts(session.Document.Canvas.Projection);
            check("undo restores original grid counts and retains the lock", session.Document.Canvas.Projection.Quad.SequenceEqual(quad) &&
                session.Document.Canvas.Projection.LockedGridCellSize == projection.LockedGridCellSize && columns.Text == "8");
            session.Redo();
            check("redo restores calibrated geometry and locked grid correspondence", session.Document.Canvas.Projection.Quad.SequenceEqual(changedQuad) &&
                CanvasMapping.Create(session.Document).GridCellSize == projection.LockedGridCellSize);
            controller.Down(changedQuad[0]); controller.Move(changedQuad[0] + new PointD(10, 10)); controller.CancelDrag();
            check("cancelled corner drag keeps the established lock and geometry", session.Document.Canvas.Projection.Quad.SequenceEqual(changedQuad) &&
                session.Document.Canvas.Projection.LockedGridCellSize == projection.LockedGridCellSize && session.UndoCount == 1);
            Presets(session.Document.Canvas, viewport, check);
            OriginMarker(session.Document, check);
        }
        finally { controller.End(); panel.CloseForDispose(); }
    }

    private static void Presets(CanvasSettings source, RectD viewport, Action<string, bool> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-grid-lock-preset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "canvas.json"); var library = new CanvasPresetLibrary(path);
            var preset = library.Save("Grid lock reference", source);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            check("canvas presets omit the per-project grid lock and preserve the existing schema",
                !json["Presets"]![0]!["Parameters"]!["Calibration"]!.AsObject().ContainsKey("LockedGridCellSize"));
            var restored = new CanvasPresetLibrary(path).Apply(preset.Id, source, viewport, viewport);
            check("applying a personal preset releases the adjustment lock without changing its saved projection",
                restored.Projection.LockedGridCellSize is null && restored.Projection.Matrix == source.Projection.Matrix &&
                source.Projection.LockedGridCellSize is not null);
            var standard = CanvasPresetLibrary.OfficialOnly().Apply("official.canvas-graph", source, viewport, viewport);
            check("applying an official preset also releases the adjustment lock", standard.Projection.LockedGridCellSize is null);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void OriginMarker(SceneDocument document, Action<string, bool> check)
    {
        using var bitmap = new SKBitmap(1000, 800); using var canvas = new SKCanvas(bitmap);
        var origin = document.Canvas.Projection.Quad[0];
        canvas.Clear(SKColors.Transparent); DesktopSession.DrawCanvasOriginMarker(canvas, origin, document.Viewport, true);
        check("locked reference origin has a visible amber center", bitmap.GetPixel((int)origin.X, (int)origin.Y) == new SKColor(210, 139, 63));
        canvas.Clear(SKColors.Transparent); DesktopSession.DrawCanvasOriginMarker(canvas, origin, document.Viewport, false);
        check("unlocked reference origin remains identifiable without changing calibration", bitmap.GetPixel((int)origin.X, (int)origin.Y) == new SKColor(35, 131, 226));
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_GRID_LOCK_UI_OUTPUT") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output); canvas.Clear(SKColors.White);
        using var renderer = new SceneRenderer(); renderer.Draw(canvas, document, new AssetLibrary(), calibrationReference: true);
        using var frame = new SKPaint { Color = new SKColor(35, 131, 226), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        var quad = document.Canvas.Projection.Quad;
        for (int i = 0; i < 4; i++) canvas.DrawLine((float)quad[i].X, (float)quad[i].Y, (float)quad[(i + 1) % 4].X, (float)quad[(i + 1) % 4].Y, frame);
        DesktopSession.DrawCanvasOriginMarker(canvas, origin, document.Viewport, true);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(output, "calibration-origin.png")); encoded.SaveTo(file);
    }
}
