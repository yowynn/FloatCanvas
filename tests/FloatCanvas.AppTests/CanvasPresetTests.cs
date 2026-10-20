using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class CanvasPresetTests
{
    internal static void Run(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-canvas-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Persistence(folder, check); Calibration(folder, check); CalibrationSnapshots(folder, check); Controls(folder, check);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Persistence(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "profile-a", "canvas-presets.json"); var library = new CanvasPresetLibrary(path);
        var settings = new CanvasSettings { BackgroundColor = 0x80456789, Grid = new() { Visible = true, Color = 0xFFAABBCC, Width = 64, Pattern = StrokePattern.Dashed } };
        settings.Projection = CanvasCalibration.CreateInitial(new(80, 100, 1920, 1080), 12, 9); settings.Projection.Enabled = true;
        var preset = library.Save("  Perspective grid  ", settings); string json = File.ReadAllText(path);
        check("personal canvas presets persist complete calibration and canvas fields in version two", json.Contains("\"Version\": 2") && json.Contains("Calibration") && json.Contains("Quad") && json.Contains("Matrix") && json.Contains("Origin") && json.Contains("CellSize") &&
            json.Contains("ProjectionEnabled") && json.Contains("BackgroundColor") && !json.Contains("Monitor") && !json.Contains("Display"));
        check("canvas preset save trims names and preserves legal wide grid lines", preset.Name == "Perspective grid" && preset.Parameters.GridWidth == 64);
        settings.BackgroundColor = 0; settings.Grid.Width = 2;
        check("saved canvas recipes are independent snapshots", library.User.Single().Parameters.BackgroundColor == 0x80456789 && library.User.Single().Parameters.GridWidth == 64);
        var reloaded = new CanvasPresetLibrary(path);
        check("canvas presets reload every portable field", reloaded.User.Single().Parameters.Matches(preset.Parameters));
        reloaded.User.Single().Parameters.Columns = 2;
        check("reading a canvas preset cannot mutate its stored values", reloaded.User.Single().Parameters.Columns == 12);
        var other = new CanvasPresetLibrary(Path.Combine(folder, "profile-b", "canvas-presets.json"));
        check("canvas preset profiles remain independent", other.User.Count == 0 && !File.Exists(Path.Combine(folder, "profile-b", "canvas-presets.json")));
        reloaded.Rename(preset.Id, "Renamed");
        var second = reloaded.Save("Second", new() { Grid = new() { Width = .25 } });
        var third = reloaded.Save("Third", new() { Grid = new() { Width = double.Epsilon } });
        reloaded.Move(third.Id, preset.Id);
        check("canvas recipe rename and reorder survive restart", new CanvasPresetLibrary(path).User.Select(p => p.Name).SequenceEqual(new[] { "Third", "Renamed", "Second" }));
        check("canvas preset persistence accepts all positive legal widths", new CanvasPresetLibrary(path).User[0].Parameters.GridWidth == double.Epsilon && new CanvasPresetLibrary(path).User[2].Parameters.GridWidth == .25);
        reloaded.Update(second.Id, new() { BackgroundColor = 0xFF003366 });
        check("overwriting a canvas preset updates only that user snapshot", reloaded.User.Single(p => p.Id == second.Id).Parameters.BackgroundColor == 0xFF003366 && reloaded.User.Single(p => p.Id == preset.Id).Parameters.GridWidth == 64);
        string officialId = reloaded.Official.First().Id; string before = File.ReadAllText(path);
        bool renameRejected = Reject(() => reloaded.Rename(officialId, "Changed"));
        bool overwriteRejected = Reject(() => reloaded.Update(officialId, new()));
        bool deleteRejected = !reloaded.Delete(officialId);
        check("official canvas presets cannot be changed or deleted", renameRejected && overwriteRejected && deleteRejected && File.ReadAllText(path) == before);
        var official = reloaded.Official.First(); official.Parameters.GridWidth = 9;
        check("official canvas definitions return independent immutable recipes", reloaded.Official.First().Parameters.GridWidth == 1);
        check("invalid canvas recipe saves preserve the previous file", Reject(() => reloaded.Save("Invalid", new() { Grid = new() { Width = 65 } })) &&
            Reject(() => reloaded.Save("Invalid", new() { Grid = new() { Width = 0 } })) && File.ReadAllText(path) == before);
        check("deleting a custom canvas preset persists without touching its peers", reloaded.Delete(second.Id) && new CanvasPresetLibrary(path).User.Count == 2 && new CanvasPresetLibrary(path).User.Any(p => p.Id == preset.Id));
        string corrupt = Path.Combine(folder, "corrupt.json"); File.WriteAllText(corrupt, "{\"Version\":1,\"Presets\":[{\"Id\":\"user-bad\",\"Name\":\"bad\",\"Parameters\":{\"Quad\":[]}}]}");
        string corruptBefore = File.ReadAllText(corrupt);
        check("unknown screen-bound preset fields are rejected without overwriting the library", Reject(() => _ = new CanvasPresetLibrary(corrupt)) && File.ReadAllText(corrupt) == corruptBefore);
        var readOnly = CanvasPresetLibrary.OfficialOnly();
        check("damaged-library fallback retains official presets and cannot write custom files", readOnly.Official.Count == 5 && !readOnly.CanEdit && Reject(() => readOnly.Save("No write", new())));
    }

    private static void Calibration(string folder, Action<string, bool> check)
    {
        var viewport = new RectD(-200, -100, 1600, 1000); var reference = new RectD(0, 0, 1000, 700);
        var current = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(reference, 8, 6) };
        var originalQuad = current.Projection.Quad.ToArray(); var originalOrigin = current.Projection.Origin;
        var parameters = new CanvasPresetParameters { BackgroundColor = 0xFF445566, ProjectionEnabled = true, Columns = 13, Rows = 7,
            GridVisible = true, GridColor = 0xA0334455, GridWidth = .25, GridPattern = StrokePattern.Dotted };
        string before = JsonSerializer.Serialize(current);
        var applied = CanvasPresetLibrary.Apply(parameters, current, reference, viewport);
        check("applying a canvas preset leaves its source document untouched", JsonSerializer.Serialize(current) == before);
        check("changing preset counts preserves current screen corners and plane origin", applied.Projection.Quad.SequenceEqual(originalQuad) && applied.Projection.Origin == originalOrigin);
        check("preset counts recalculate a valid transform with all portable fields", applied.Projection.Columns == 13 && applied.Projection.Rows == 7 && applied.Projection.Enabled &&
            CanvasCalibration.IsStableViewport(applied.Projection.Matrix, viewport) && parameters.Matches(CanvasPresetParameters.From(applied)));
        var sameCounts = CanvasPresetParameters.From(applied); sameCounts.BackgroundColor = 0;
        var recolored = CanvasPresetLibrary.Apply(sameCounts, applied, reference, viewport);
        check("style-only presets preserve the existing calibration exactly", JsonSerializer.Serialize(recolored.Projection) == JsonSerializer.Serialize(applied.Projection));
        var initialized = CanvasPresetLibrary.Apply(parameters, new(), reference, viewport);
        check("presets initialize an uncalibrated canvas from the current screen reference", initialized.Projection.Quad.SequenceEqual(CanvasCalibration.CreateInitial(reference, 13, 7).Quad));
        var hidden = CanvasPresetLibrary.Apply(new() { Columns = 10, Rows = 4 }, new(), reference, viewport);
        check("plain presets preserve an uninitialized reference until needed", hidden.Projection.Quad.Count == 0 && hidden.Projection.Columns == 10 && hidden.Projection.Rows == 4 && !hidden.Projection.Enabled);
        var broken = new CanvasSettings(); broken.Projection.Quad = [new(0, 0), new(200, 0), new(0, 200), new(200, 200)];
        string brokenBefore = JsonSerializer.Serialize(broken);
        check("invalid calibration refuses a preset without partial mutation", Reject(() => CanvasPresetLibrary.Apply(parameters, broken, reference, viewport)) && JsonSerializer.Serialize(broken) == brokenBefore);
        check("preset initialization refuses a viewport too small for valid calibration", Reject(() => CanvasPresetLibrary.Apply(parameters, new(), new(0, 0, 1, 1), new(0, 0, 1, 1))));
        var library = new CanvasPresetLibrary(Path.Combine(folder, "calibration.json"));
        string id = library.Save("Portable", applied).Id;
        var secondScreen = new RectD(2400, 0, 2560, 1440);
        var onSecondScreen = library.Apply(id, new(), secondScreen, secondScreen);
        check("personal canvas recipes restore saved screen positions rather than the selected reference screen", onSecondScreen.Projection.Quad.SequenceEqual(applied.Projection.Quad));
    }

    private static void CalibrationSnapshots(string folder, Action<string, bool> check)
    {
        var viewport = new RectD(-250, -180, 500, 500);
        var reference = CanvasCalibration.CreateInitial(viewport, 4, 5);
        PointD[] quad = [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)];
        check("snapshot fixture uses a stable nonaffine calibration", CanvasCalibration.TryCalibrate(reference, quad, viewport, out var projection, out _));
        var source = new CanvasSettings { BackgroundColor = 0xFFF1F4FA, Projection = projection, Grid = new() { Visible = true, Width = .75, Color = 0x80557799 } };
        string path = Path.Combine(folder, "snapshots.json"); var library = new CanvasPresetLibrary(path);
        var saved = library.Save("Perspective", source); var reloaded = new CanvasPresetLibrary(path);
        var fresh = new CanvasSettings(); string freshBefore = JsonSerializer.Serialize(fresh);
        var applied = reloaded.Apply(saved.Id, fresh, new(1000, 1000, 800, 600), viewport);
        check("save reload and apply restores every calibrated projection field exactly", JsonSerializer.Serialize(applied.Projection) == JsonSerializer.Serialize(source.Projection) && JsonSerializer.Serialize(fresh) == freshBefore);
        var plane = projection.Origin + new PointD(150, 250);
        check("save reload and apply preserves the actual projective point mapping", projection.Matrix.TransformPoint(plane).DistanceTo(applied.Projection.Matrix.TransformPoint(plane)) < 1e-10);
        var originalDocument = new SceneDocument { Viewport = viewport, Canvas = source.Clone() };
        originalDocument.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 180, Height = 220, Transform = Affine2D.Translation(projection.Origin.X + 80, projection.Origin.Y + 100), Style = new() { Fill = 0xFFFF0000, Stroke = 0xFF223344, StrokeWidth = 3 } });
        var restoredDocument = originalDocument.Clone(); restoredDocument.Canvas = applied;
        using (var renderer = new SceneRenderer())
        using (var expected = SKBitmap.Decode(renderer.ExportPng(originalDocument, new())))
        using (var actual = SKBitmap.Decode(renderer.ExportPng(restoredDocument, new())))
            check("saved perspective reproduces identical real exported PNG pixels for the same scene", expected.Width == actual.Width && expected.Height == actual.Height && expected.Pixels.Any(p => p == SKColors.Red) && expected.Pixels.SequenceEqual(actual.Pixels));
        source.Projection.Quad[0] = new(999, 999);
        saved.Parameters.Calibration!.Quad[1] = new(888, 888); applied.Projection.Quad[2] = new(777, 777);
        var exposed = reloaded.User.Single(); exposed.Parameters.Calibration!.Quad[3] = new(666, 666);
        check("source returned preset loaded preset and applied canvas each own their calibration lists", library.User.Single().Parameters.Calibration!.Quad.SequenceEqual(quad) && reloaded.User.Single().Parameters.Calibration!.Quad.SequenceEqual(quad));
        var current = reloaded.Apply(saved.Id, fresh, viewport, viewport); var parameters = reloaded.User.Single().Parameters;
        var clone = parameters.Clone(); clone.Calibration!.Quad[0] = new(555, 555);
        check("cloning canvas preset parameters deep copies calibration", parameters.Calibration!.Quad.SequenceEqual(quad));
        var modified = CanvasPresetParameters.From(current); modified.Calibration!.Origin += new PointD(1, 0);
        check("personal preset matching detects changed geometry while portable recipes ignore geometry", !parameters.Matches(modified) && new CanvasPresetParameters { BackgroundColor = parameters.BackgroundColor, ProjectionEnabled = parameters.ProjectionEnabled, Columns = 4, Rows = 5, GridVisible = true, GridColor = parameters.GridColor, GridWidth = .75 }.Matches(modified));
        var disabled = current.Clone(); disabled.Projection.Enabled = false;
        var disabledSaved = reloaded.Save("Disabled perspective", disabled);
        var extendedViewport = new RectD(-250, -500, 500, 900);
        var horizonApplied = reloaded.Apply(saved.Id, fresh, viewport, extendedViewport);
        var horizonMapping = CanvasMapping.Create(new SceneDocument { Canvas = horizonApplied, Viewport = extendedViewport });
        check("enabled snapshot preserves its projection when a new viewport crosses its horizon without changing the target", horizonApplied.Projection.Enabled &&
            horizonApplied.Projection.Matrix == current.Projection.Matrix && JsonSerializer.Serialize(fresh) == freshBefore &&
            horizonMapping.SafeScreenPolygon.Count >= 3 && horizonMapping.SafeScreenPolygon.All(p => horizonMapping.TryToPlane(p, out _)));
        var disabledApplied = new CanvasPresetLibrary(path).Apply(disabledSaved.Id, fresh, viewport, extendedViewport);
        check("disabled calibration survives restart and a viewport change without being forced enabled", !disabledApplied.Projection.Enabled && disabledApplied.Projection.Quad.SequenceEqual(quad) && disabledApplied.Projection.Matrix == current.Projection.Matrix);
        string beforeInvalid = File.ReadAllText(path);
        foreach (var corrupt in new Action<CanvasProjectionSettings>[] {
            p => p.Quad[2] = p.Quad[0], p => p.Matrix = Projective2D.Identity, p => p.Matrix = p.Matrix with { M11 = double.NaN },
            p => p.CellSize = 0, p => p.Origin = new(double.PositiveInfinity, 0), p => p.AlgorithmVersion = 999, p => p.Rows++ })
        {
            var invalid = current.Clone(); corrupt(invalid.Projection);
            check("invalid snapshot save refuses inconsistent or nonfinite calibration atomically", Reject(() => reloaded.Update(saved.Id, invalid)) && File.ReadAllText(path) == beforeInvalid);
        }
        var session = new EditorSession(new() { Viewport = viewport });
        var restored = reloaded.Apply(saved.Id, session.Document.Canvas, viewport, viewport);
        session.Execute("Canvas preset", document => document.Canvas = restored);
        check("applying a calibrated preset is one undoable canvas transaction", session.UndoCount == 1 && session.Document.Canvas.Projection.Quad.SequenceEqual(quad) && session.Undo() && session.Document.Canvas.Projection.Quad.Count == 0 && session.Redo() && session.Document.Canvas.Projection.Quad.SequenceEqual(quad));
        var updated = current.Clone(); updated.Projection.Enabled = false;
        reloaded.Update(saved.Id, updated);
        check("overwriting replaces the full deep calibration snapshot after reload", !new CanvasPresetLibrary(path).User.Single(p => p.Id == saved.Id).Parameters.Calibration!.Enabled && new CanvasPresetLibrary(path).User.Single(p => p.Id == saved.Id).Parameters.Calibration!.Quad.SequenceEqual(quad));
        string valid = File.ReadAllText(path);
        foreach (var corrupt in new Action<JsonNode>[] {
            n => n["Version"] = 1, n => n["Version"] = 99,
            n => n["Presets"]![0]!["Parameters"]!["TargetDisplay"] = "DISPLAY6",
            n => n["Presets"]![0]!["Parameters"]!["Calibration"]!["TargetDisplay"] = "DISPLAY6",
            n => n["Presets"]![0]!["Parameters"]!["Calibration"]!.AsObject().Remove("Matrix"),
            n => n["Presets"]![0]!["Parameters"]!["Calibration"]!["Matrix"]!.AsObject().Remove("M11") })
        {
            var node = JsonNode.Parse(valid)!; corrupt(node); string brokenPath = Path.Combine(folder, "snapshot-broken-" + Guid.NewGuid().ToString("N") + ".json");
            string bytes = node.ToJsonString(); File.WriteAllText(brokenPath, bytes);
            check("invalid versions incomplete snapshots and display-bound fields leave the library untouched", Reject(() => _ = new CanvasPresetLibrary(brokenPath)) && File.ReadAllText(brokenPath) == bytes);
        }
        var legacyNode = JsonNode.Parse(valid)!; legacyNode["Version"] = 1;
        foreach (var item in legacyNode["Presets"]!.AsArray()) item!["Parameters"]!.AsObject().Remove("Calibration");
        string legacyPath = Path.Combine(folder, "legacy.json"), legacyBytes = legacyNode.ToJsonString(); File.WriteAllText(legacyPath, legacyBytes);
        var legacy = new CanvasPresetLibrary(legacyPath);
        check("reading a version-one canvas library performs no migration write", File.ReadAllText(legacyPath) == legacyBytes && legacy.User.All(p => p.Parameters.Calibration is null));
        var legacyApplied = legacy.Apply(saved.Id, current, viewport, viewport);
        check("legacy recipes preserve the current canvas geometry", legacyApplied.Projection.Quad.SequenceEqual(quad) && legacyApplied.Projection.Matrix == current.Projection.Matrix);
        legacy.Save("New perspective", current);
        var migrated = new CanvasPresetLibrary(legacyPath);
        check("an explicit edit writes version two while retaining all legacy recipes and their portable semantics", File.ReadAllText(legacyPath).Contains("\"Version\": 2") && migrated.User.Count == legacy.User.Count && migrated.User.Single(p => p.Id == saved.Id).Parameters.Calibration is null && migrated.User.Last().Parameters.Calibration is not null);
    }

    private static void Controls(string folder, Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        var library = new CanvasPresetLibrary(Path.Combine(folder, "controls.json"));
        var session = new EditorSession(); var window = new CanvasSettingsWindow(library);
        try
        {
            var gallery = Field<CanvasPresetGallery>(window, "presets");
            int applied = 0, propertyEdits = 0;
            window.BackgroundChanged += _ => propertyEdits++;
            window.GridWidthChanged += _ => propertyEdits++;
            window.PresetApplied += parameters =>
            {
                applied++; var settings = CanvasPresetLibrary.Apply(parameters, session.Document.Canvas, new(0, 0, 1920, 1080), session.Document.Viewport);
                session.Execute("Canvas preset", document => document.Canvas = settings); window.Refresh(session.Document.Canvas, false);
            };
            window.Refresh(new(), false);
            check("canvas panel uses separate official and user recipe sections", gallery.OfficialTiles.Children.Count == 5 && gallery.UserTiles.Children.Count == 0 && window.Width > 500);
            check("official canvas recipe tiles expose no edit context menu", gallery.OfficialTiles.Children.Cast<Button>().All(button => button.ContextMenu is null));
            gallery.Apply("official.canvas-graph");
            check("one canvas preset updates the canvas in one undo entry without property events", applied == 1 && session.UndoCount == 1 && session.Document.Canvas.Grid.Visible && propertyEdits == 0);
            check("the applied canvas recipe is visibly selected", gallery.SelectedId == "official.canvas-graph");
            session.Undo(); window.Refresh(session.Document.Canvas, false);
            check("undo refresh restores the canvas and clears a mismatched preset selection", !session.Document.Canvas.Grid.Visible && session.Document.Canvas.BackgroundColor == 0 && gallery.SelectedId == "" && applied == 1);
            gallery.SaveNamed("Current"); string selected = gallery.SelectedId;
            check("saving a canvas preset does not apply it or change document history", applied == 1 && session.UndoCount == 0 && gallery.UserTiles.Children.Count == 1 && selected.StartsWith("user-"));
            check("user canvas recipe tiles provide overwrite rename and delete actions", ((Button)gallery.UserTiles.Children[0]).ContextMenu!.Items.OfType<MenuItem>().Count() == 3);
            gallery.Delete(selected);
            check("deleting a selected canvas recipe leaves the current canvas unchanged", gallery.UserTiles.Children.Count == 0 && gallery.SelectedId == "" && applied == 1 && session.UndoCount == 0);
            var width = Field<NumericValueField>(window, "width");
            foreach (double value in new[] { 64d, .25, double.Epsilon })
            {
                window.Refresh(new() { Grid = new() { Width = value } }, false);
                check("canvas width restores legal stored value " + value, width.TryGetValue(out double actual) && actual == value && width.Slider.Minimum == 1 && width.Slider.Maximum == 20 && propertyEdits == 0);
            }
            var content = (FrameworkElement)window.Content; content.Measure(new(600, 540)); content.Arrange(new Rect(0, 0, 600, 540)); content.UpdateLayout();
            check("canvas panel content lays out both property and preset columns", Field<ColorField>(window, "background").ActualWidth > 100 && gallery.ActualWidth >= 196);
            var projection = CanvasCalibration.CreateInitial(session.Document.Viewport); var quad = projection.Quad.ToArray(); quad[0] += new PointD(10, 10);
            CanvasCalibration.TryCalibrate(projection, quad, session.Document.Viewport, out projection, out _);
            session.Document.Canvas.Projection = projection; window.Refresh(session.Document.Canvas, false);
            gallery.SaveNamed("Perspective selection"); string perspectiveId = gallery.SelectedId;
            var changedQuad = quad.ToArray(); changedQuad[1] += new PointD(-5, 5);
            CanvasCalibration.TryCalibrate(projection, changedQuad, session.Document.Viewport, out var changedProjection, out _);
            session.Document.Canvas.Projection = changedProjection; window.Refresh(session.Document.Canvas, false);
            check("changing only four-corner geometry clears a saved personal preset highlight", gallery.SelectedId == "");
            gallery.Apply("official.canvas-graph");
            check("official canvas recipes stay selected after resetting the plane", gallery.SelectedId == "official.canvas-graph" && session.Document.Canvas.Projection.Quad.SequenceEqual(CanvasCalibration.CreateInitial(session.Document.Viewport).Quad));
            gallery.Apply(perspectiveId);
            check("reapplying a personal canvas recipe restores its calibration and highlight", gallery.SelectedId == perspectiveId && session.Document.Canvas.Projection.Quad.SequenceEqual(quad));
            session.Document.Canvas.Projection = changedProjection.Clone(); window.Refresh(session.Document.Canvas, false);
            var savedTile = gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, perspectiveId));
            savedTile.ContextMenu!.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check("overwriting a changed personal calibration selects the new matching snapshot", gallery.SelectedId == perspectiveId && new CanvasPresetLibrary(Path.Combine(folder, "controls.json")).User.Single(p => p.Id == perspectiveId).Parameters.Calibration!.Quad.SequenceEqual(changedQuad));
        }
        finally { window.CloseForDispose(); }
        string previousProfile = ApplicationPaths.ProfileDirectory; string corruptProfile = Path.Combine(folder, "broken-profile");
        Directory.CreateDirectory(corruptProfile); string corruptFile = Path.Combine(corruptProfile, "canvas-presets.json"); File.WriteAllText(corruptFile, "broken library");
        ApplicationPaths.Configure(["--profile", corruptProfile]);
        CanvasSettingsWindow? fallback = null;
        try
        {
            fallback = new(); var gallery = Field<CanvasPresetGallery>(fallback, "presets");
            check("canvas panel keeps official presets available when its user library is corrupt", gallery.OfficialTiles.Children.Count == 5 && !Field<CanvasPresetLibrary>(gallery, "library").CanEdit &&
                !string.IsNullOrWhiteSpace(Field<TextBlock>(fallback, "status").Text) && File.ReadAllText(corruptFile) == "broken library");
        }
        finally { fallback?.CloseForDispose(); ApplicationPaths.Configure(["--profile", previousProfile]); }
    }
    private static bool Reject(Action action) { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or KeyNotFoundException or InvalidOperationException) { return true; } }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
