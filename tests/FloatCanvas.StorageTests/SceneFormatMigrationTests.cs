using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SceneFormatMigrationTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var firstWriter = new ProjectStore("1.2.3-test");
        var nextWriter = new ProjectStore("2.0.0-test");
        var assets = new AssetLibrary();
        var scene = Fixture();
        string original = JsonSerializer.Serialize(scene);
        var snapshot = scene.Clone();
        var editor = new EditorSession(scene, assets);
        editor.Execute("change", d => d.NextNumber++);
        string path = Path.Combine(root, "new.floatcanvas");
        await firstWriter.SaveAsync(path, editor.Document, assets);
        check(scene.Provenance.CreatedWith == "1.2.3-test" && snapshot.Provenance.IsEstablished &&
            scene.Provenance.LastSavedWith == "1.2.3-test", "a successful first save establishes the origin shared by editor snapshots");
        editor.Undo();
        check(JsonSerializer.Serialize(editor.Document) == original && editor.Document.Provenance.CreatedWith == "1.2.3-test",
            "undo restores content while retaining successfully saved package provenance");
        await nextWriter.SaveAsync(Path.Combine(root, "copy.floatcanvas"), editor.Document.Clone(), assets);
        check(editor.Document.Provenance.CreatedWith == "1.2.3-test" && editor.Document.Provenance.LastSavedWith == "2.0.0-test" &&
            JsonSerializer.Serialize(editor.Document) == original, "save as updates software provenance without changing creation software or editable content");
        var current = await nextWriter.LoadAsync(Path.Combine(root, "copy.floatcanvas"));
        check(current.Document.Provenance.CreatedWith == "1.2.3-test" && current.Document.Provenance.LastSavedWith == "2.0.0-test" &&
            JsonSerializer.Serialize(current.Document) == original, "current packages reopen with exact appearance data and software provenance");
        using (var zip = ZipFile.OpenRead(path))
        {
            var manifest = await Read(zip, "manifest.json");
            check(manifest["Version"]!.GetValue<int>() == SceneDocument.CurrentVersion && manifest["CreatedWith"]!.GetValue<string>() == "1.2.3-test" &&
                manifest["LastSavedWith"]!.GetValue<string>() == "1.2.3-test", "package format and writer software versions are recorded separately");
            check(!(await Read(zip, "scene.json")).ContainsKey("Provenance"), "package provenance is excluded from editable scene JSON");
        }

        foreach (int version in new[] { 1, 2, 3, 6, 8 })
        {
            string legacyPath = Path.Combine(root, $"legacy-{version}.floatcanvas");
            var legacyJson = JsonSerializer.SerializeToNode(Fixture())!.AsObject();
            Downgrade(legacyJson, version);
            await WritePackage(legacyPath, "project", version, legacyJson);
            byte[] before = await File.ReadAllBytesAsync(legacyPath);
            var legacy = await firstWriter.LoadAsync(legacyPath);
            check(legacy.Document.Version == SceneDocument.CurrentVersion && legacy.Document.Provenance.IsEstablished &&
                legacy.Document.Provenance.CreatedWith is null && legacy.Document.Provenance.LastSavedWith is null,
                $"v{version} explicitly upgrades while retaining unknown historical software provenance");
            check(JsonSerializer.Serialize(legacy.Document.Elements) == JsonSerializer.Serialize(Fixture().Elements),
                $"v{version} retains unused linked-color fill, both badge color conventions and legacy font sizes exactly");
            check((await File.ReadAllBytesAsync(legacyPath)).AsSpan().SequenceEqual(before), $"v{version} loading does not rewrite the source package");
            string migratedPath = Path.Combine(root, $"migrated-{version}.floatcanvas");
            await nextWriter.SaveAsync(migratedPath, legacy.Document, legacy.Assets);
            var reopened = await nextWriter.LoadAsync(migratedPath);
            check(reopened.Document.Provenance.CreatedWith is null && reopened.Document.Provenance.LastSavedWith == "2.0.0-test" &&
                JsonSerializer.Serialize(reopened.Document) == JsonSerializer.Serialize(legacy.Document),
                $"v{version} resave and reopen are stable without inventing its creation software");
        }

        var failed = Fixture(); var failedSnapshot = failed.Clone();
        byte[] existing = await File.ReadAllBytesAsync(path);
        bool rejected = false;
        try { await nextWriter.SaveVersionedAsync(path, failed, assets, expectedVersion: new ProjectFileVersion("external-change")); }
        catch (ProjectFileConflictException) { rejected = true; }
        check(rejected && !failed.Provenance.IsEstablished && !failedSnapshot.Provenance.IsEstablished &&
            (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(existing), "an atomic-save conflict preserves the package and never establishes unsaved provenance");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await firstWriter.SaveAsync(path, current.Document, assets, canceled.Token); } catch (OperationCanceledException) { }
        }
        check(current.Document.Provenance.LastSavedWith == "2.0.0-test", "a canceled save retains prior successful software provenance");

        foreach (var mutation in new (string Name, Action<JsonObject> Change)[]
        {
            ("future-format", n => n["Version"] = SceneDocument.CurrentVersion + 1),
            ("missing-creator", n => n.Remove("CreatedWith")),
            ("missing-writer", n => n.Remove("LastSavedWith")),
            ("null-writer", n => n["LastSavedWith"] = null),
            ("non-text-creator", n => n["CreatedWith"] = 42),
            ("oversized-writer", n => n["LastSavedWith"] = new string('x', 129)),
            ("control-character", n => n["CreatedWith"] = "invalid\nversion")
        })
        {
            string invalidPath = Path.Combine(root, mutation.Name + ".floatcanvas"); File.Copy(path, invalidPath);
            using (var zip = ZipFile.Open(invalidPath, ZipArchiveMode.Update))
            { var manifest = await Read(zip, "manifest.json"); mutation.Change(manifest); await Replace(zip, "manifest.json", manifest); }
            rejected = false;
            try { await firstWriter.LoadAsync(invalidPath); } catch (InvalidDataException) { rejected = true; }
            check(rejected, mutation.Name + " is rejected before replacing a current document");
        }
        string badScene = Path.Combine(root, "future-scene.floatcanvas"); File.Copy(path, badScene);
        using (var zip = ZipFile.Open(badScene, ZipArchiveMode.Update))
        { var json = await Read(zip, "scene.json"); json["Version"] = SceneDocument.CurrentVersion + 1; await Replace(zip, "scene.json", json); }
        rejected = false; try { await firstWriter.LoadAsync(badScene); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "future scene versions cannot enter the current-format migration path");

        var recovery = new RecoveryStore(Path.Combine(root, "recovery"), "2.0.0-test");
        var legacyLive = JsonSerializer.SerializeToNode(Fixture())!.AsObject(); Downgrade(legacyLive, 8);
        var legacyCapture = (JsonObject)legacyLive.DeepClone(); legacyCapture["Kind"] = (int)DocumentKind.FrozenCapture;
        await WritePackage(recovery.CurrentPath, "recovery", 8, new JsonObject
        { ["LiveDocument"] = legacyLive, ["CaptureDocument"] = legacyCapture, ["CaptureOrigin"] = JsonSerializer.SerializeToNode(new PointD(12, 34)) });
        var recovered = (await recovery.LoadAsync())!;
        check(recovered.State.LiveDocument.Version == SceneDocument.CurrentVersion && recovered.State.CaptureDocument!.Version == SceneDocument.CurrentVersion &&
            ReferenceEquals(recovered.State.LiveDocument.Provenance, recovered.State.CaptureDocument.Provenance), "recovery explicitly upgrades both documents and shares their loaded package origin");
        await recovery.SaveAsync(recovered.State.Clone(), recovered.Assets);
        var recoveredAgain = (await recovery.LoadAsync())!;
        check(recoveredAgain.State.LiveDocument.Provenance.CreatedWith is null && recoveredAgain.State.LiveDocument.Provenance.LastSavedWith == "2.0.0-test" &&
            JsonSerializer.Serialize(recoveredAgain.State) == JsonSerializer.Serialize(recovered.State), "legacy recovery resave retains unknown creator and exact live/capture content");
        var freshRecovery = new RecoveryStore(Path.Combine(root, "fresh-recovery"), "1.2.3-test");
        var fresh = new WorkspaceState { LiveDocument = Fixture() };
        await freshRecovery.SaveAsync(fresh.Clone(), assets);
        check(fresh.LiveDocument.Provenance.CreatedWith == "1.2.3-test", "a successful first autosave establishes the origin shared with the active document");
        await LegacyGeometryAsync(root, check);
        Console.WriteLine($"Migration artifacts: {root}");
    }

    private static async Task LegacyGeometryAsync(string root, Action<bool, string> check)
    {
        PointD[] quad = [new(120, 180), new(820, 130), new(930, 720), new(70, 680)];
        check(CanvasCalibration.TryGridToScreen(8, 6, quad, out var gridToScreen), "legacy geometry fixture has a finite perspective transform");
        var source = Fixture(); var origin = new PointD(20, 30); const double cellSize = 17;
        source.Canvas.Projection = new()
        {
            Enabled = true, Columns = 8, Rows = 6, Origin = origin, CellSize = cellSize, LockedGridCellSize = 13,
            Quad = [..quad], AlgorithmVersion = 2, PinLocation = CanvasPinLocation.BottomRight,
            ReferenceBounds = new(origin.X, origin.Y, 8 * cellSize, 6 * cellSize),
            Matrix = gridToScreen.Compose(Projective2D.FromAffine(Affine2D.Scale(1 / cellSize, 1 / cellSize).Compose(Affine2D.Translation(-origin.X, -origin.Y))))
        };
        source.Canvas.Grid.FlatCellSize = 11; source.Canvas.Grid.FlatOrigin = new(40, 60); source.Canvas.Grid.FlatTranslation = new(3, 9);
        var oldMatrix = source.Canvas.Projection.Matrix; var oldPin = source.Canvas.Projection.PinPoint;
        string path = Path.Combine(root, "legacy-v8-perspective.floatcanvas");
        var json = JsonSerializer.SerializeToNode(source)!.AsObject(); json["Version"] = 8;
        LegacyFixtureSchema.RemovePrefabFields(json);
        json["Canvas"]!["Projection"]!.AsObject().Remove("PlaneBounds");
        json["Canvas"]!["Grid"]!.AsObject().Remove("PlaneCellSize"); json["Canvas"]!["Grid"]!.AsObject().Remove("PlaneOrigin");
        await WritePackage(path, "project", 8, json); byte[] original = await File.ReadAllBytesAsync(path);
        var store = new ProjectStore("2.0.0-test"); var migrated = await store.LoadAsync(path);
        var projection = migrated.Document.Canvas.Projection; var grid = migrated.Document.Canvas.Grid;
        check(projection.Matrix == oldMatrix && projection.PinPoint == oldPin && projection.PlaneBounds == new RectD(origin.X, origin.Y, 8 * cellSize, 6 * cellSize),
            "v8 separates source bounds without recalibrating the stored projection or moving its pin");
        check(grid.PlaneCellSize == 13 && grid.PlaneOrigin == oldPin && grid.FlatCellSize == 11 && grid.FlatOrigin == new PointD(40, 60) && grid.FlatTranslation == new PointD(3, 9),
            "v8 migration preserves locked perspective-grid spacing, phase and all independent flat-grid placement");
        check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(original), "perspective migration keeps the original v8 package byte-exact");
        string currentPath = Path.Combine(root, "migrated-v8-perspective.floatcanvas");
        await store.SaveAsync(currentPath, migrated.Document, migrated.Assets);
        check(JsonSerializer.Serialize((await store.LoadAsync(currentPath)).Document) == JsonSerializer.Serialize(migrated.Document),
            "migrated source geometry and independent grids are stable after a current-format reopen");
        foreach (string field in new[] { "PlaneBounds", "PlaneOrigin" })
        {
            string invalidPath = Path.Combine(root, "incomplete-" + field + ".floatcanvas"); File.Copy(currentPath, invalidPath);
            using (var zip = ZipFile.Open(invalidPath, ZipArchiveMode.Update))
            {
                var payload = await Read(zip, "scene.json");
                if (field == "PlaneBounds") payload["Canvas"]!["Projection"]![field]!.AsObject().Remove("Width");
                else payload["Canvas"]!["Grid"]![field]!.AsObject().Remove("Y");
                await Replace(zip, "scene.json", payload);
            }
            bool rejected = false; try { await store.LoadAsync(invalidPath); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "partially recorded " + field + " cannot be completed by current-model defaults");
        }
    }

    private static SceneDocument Fixture()
    {
        var document = new SceneDocument { Elements =
        [
            new() { Id = Guid.Parse("815bd38b-8f28-4e51-aef0-33d6b59f79ec"), Kind = ElementKind.Rectangle, Width = 80, Height = 50,
                Style = new() { Stroke = 0x7F112233, Fill = 0x33445566, LinkColors = true, FontSize = 17 } },
            new() { Id = Guid.Parse("a603b468-d329-4a76-8886-ea3ed6d4481e"), Kind = ElementKind.Number, Width = 60, Height = 60, Number = 8,
                Style = new() { Stroke = 0xFF778899, Fill = 0xFF234567, FontSize = 11 } },
            new() { Id = Guid.Parse("49a8a899-598e-42a8-b2db-f634ba36de71"), Kind = ElementKind.Number, Width = 60, Height = 60, Number = 9, NumberUsesForeground = true,
                Style = new() { Stroke = 0xFF123456, Fill = 0xFFEEDDCC, FontSize = 19 } }
        ] };
        foreach (var element in document.Elements)
            AppearanceMigration.MigrateElement(element, new(element.Style.Stroke, element.Style.Fill, element.Style.LinkColors,
                element.Style.Variant == ElementAppearance.Default ? null : element.Style.Variant), element.NumberUsesForeground);
        return document;
    }

    private static void Downgrade(JsonObject scene, int version)
    {
        scene["Version"] = version;
        LegacyFixtureSchema.RemovePrefabFields(scene);
        if (version < 3) { scene.Remove("Canvas"); return; }
        var canvas = scene["Canvas"]!.AsObject(); var projection = canvas["Projection"]!.AsObject(); var grid = canvas["Grid"]!.AsObject();
        if (version < 5) canvas.Remove("DepthPriorityOcclusion");
        if (version is 6 or 7) projection["LockedGridCellSize"] = null;
        if (version < 7) { grid.Remove("FlatCellSize"); grid.Remove("FlatOrigin"); }
        if (version < 8)
        {
            projection.Remove("PinLocation"); projection.Remove("PinPlanePoint"); projection.Remove("ReferenceBounds");
            projection["AlgorithmVersion"] = 1; grid.Remove("FlatTranslation");
        }
    }

    private static async Task WritePackage(string path, string kind, int version, JsonObject payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        string payloadName = kind == "project" ? "scene.json" : "workspace.json";
        var manifest = new JsonObject { ["Format"] = "FloatCanvas." + kind, ["Version"] = version, ["Payload"] = payloadName, ["Assets"] = new JsonArray() };
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(manifest.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry(payloadName).Open())) await writer.WriteAsync(payload.ToJsonString());
    }
    private static async Task<JsonObject> Read(ZipArchive zip, string name)
    { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject(); }
    private static async Task Replace(ZipArchive zip, string name, JsonObject json)
    { zip.GetEntry(name)!.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString()); }
}
