using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class CanvasAuthoringReferenceTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "canvas-reference", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var document = new SceneDocument { Viewport = new(0, 0, 1920, 1200),
            AuthoringReference = new(new(0, 60, 1920, 1080), new(0, 0, 1920, 1200), new(0, 60, 1920, 1080)),
            DisplayLayout = [new() { DeviceName = "saved-screen", Bounds = new(0, 0, 1920, 1200) }],
            Elements = [new() { Kind = ElementKind.Rectangle, Width = 50, Height = 40 }] };
        var assets = new AssetLibrary(); var store = new ProjectStore(); string path = Path.Combine(directory, "reference.floatcanvas");
        await store.SaveAsync(path, document, assets); var loaded = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(loaded.Document) == JsonSerializer.Serialize(document),
            "current format round-trips authored extent, physical reference screen and effective content basis exactly");
        using (var zip = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            check(JsonNode.Parse(await reader.ReadToEndAsync())!["Version"]!.GetValue<int>() == SceneDocument.CurrentVersion,
                "current format is declared in the envelope so earlier readers reject the richer semantics");
        }
        var clone = loaded.Document.Clone(); clone.Viewport = new(50, 20, 3840, 2160);
        check(clone.AuthoringReference == document.AuthoringReference && CanvasAuthoringReference.FromSavedDocument(clone).Frame == document.AuthoringReference.Frame,
            "runtime viewport changes and clones retain the original authored reference");
        var changedReference = document.Clone(); changedReference.AuthoringReference = document.AuthoringReference with { Frame = new(0, 0, 1000, 1000) };
        check(AuthoredSceneContent.Fingerprint(document) != AuthoredSceneContent.Fingerprint(changedReference),
            "authored reference changes participate in content savepoints and history identity");
        var recovery = new RecoveryStore(Path.Combine(directory, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = document.Clone() }, assets);
        check((await recovery.LoadAsync())!.State.LiveDocument.AuthoringReference == document.AuthoringReference,
            "workspace recovery retains all three authored reference rectangles");
        string legacyPath = Path.Combine(directory, "legacy17.floatcanvas"); File.Copy(path, legacyPath);
        await Mutate(legacyPath, scene => scene.Remove("AuthoringReference"));
        byte[] untouched = await File.ReadAllBytesAsync(legacyPath); var legacy = await store.LoadAsync(legacyPath);
        check(legacy.Document.Version == SceneDocument.CurrentVersion && legacy.Document.AuthoringReference is null && (await File.ReadAllBytesAsync(legacyPath)).SequenceEqual(untouched),
            "reading format17 upgrades only the owned DTO and never rewrites or guesses reference metadata");
        check(CanvasAuthoringReference.FromSavedDocument(legacy.Document).ReferenceScreenBounds == legacy.Document.DisplayLayout.Single().Bounds,
            "legacy inference uses the saved screen evidence before a runtime viewport synchronization");
        string smuggled = Path.Combine(directory, "future-reference-in17.floatcanvas"); File.Copy(path, smuggled);
        await Mutate(smuggled, _ => { }); bool rejected = false;
        try { await store.LoadAsync(smuggled); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "format17 cannot smuggle a format18 authored reference through the strict reader");
        var invalid = document.Clone(); invalid.AuthoringReference = document.AuthoringReference with { ContentReferenceBounds = new(0, 0, -1, 100) };
        rejected = false; try { SceneValidation.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "invalid effective conversion bounds fail validation instead of being normalized");
    }
    private static async Task Mutate(string path, Action<JsonObject> update)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (string name in new[] { "manifest.json", "scene.json" })
        {
            JsonObject value;
            using (var reader = new StreamReader(zip.GetEntry(name)!.Open())) value = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            value["Version"] = 17; if (name == "scene.json") update(value);
            zip.GetEntry(name)!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(value.ToJsonString());
        }
    }
}
