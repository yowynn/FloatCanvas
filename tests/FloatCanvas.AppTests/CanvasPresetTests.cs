using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CanvasPresetTests
{
    internal static void Run(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-canvas-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Persistence(folder, check); Calibration(folder, check); Controls(folder, check);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Persistence(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "profile-a", "canvas-presets.json"); var library = new CanvasPresetLibrary(path);
        var settings = new CanvasSettings { BackgroundColor = 0x80456789, Grid = new() { Visible = true, Color = 0xFFAABBCC, Width = 64, Pattern = StrokePattern.Dashed } };
        settings.Projection = CanvasCalibration.CreateInitial(new(80, 100, 1920, 1080), 12, 9); settings.Projection.Enabled = true;
        var preset = library.Save("  Perspective grid  ", settings); string json = File.ReadAllText(path);
        check("canvas presets persist portable values without screen geometry", !json.Contains("Quad") && !json.Contains("Matrix") && !json.Contains("Origin") && !json.Contains("CellSize") &&
            json.Contains("ProjectionEnabled") && json.Contains("BackgroundColor"));
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
            CanvasCalibration.IsStableViewport(applied.Projection.Matrix, viewport) && CanvasPresetParameters.From(applied).Matches(parameters));
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
        check("reusing a canvas recipe on another monitor does not restore saved screen positions", onSecondScreen.Projection.Quad.SequenceEqual(CanvasCalibration.CreateInitial(secondScreen, 13, 7).Quad));
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
