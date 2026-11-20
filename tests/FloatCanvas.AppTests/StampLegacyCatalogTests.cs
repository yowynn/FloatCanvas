using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class StampLegacyCatalogTests
{
    internal static async Task RunAsync(string profile, Action<string, bool> check)
    {
        using var bitmap = new SKBitmap(6, 4); bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = encoded.ToArray();
        foreach (int version in new[] { 1, 2 })
        {
            string folder = profile + "-v" + version, root = Path.Combine(folder, "stamps"); Directory.CreateDirectory(root);
            var assets = new AssetLibrary(); string imageId = assets.Add(png);
            var vector = new SceneDocument { Viewport = new(0, 0, 90, 40), Elements = [new() { Kind = ElementKind.Rectangle, Width = 90, Height = 40 }] };
            var raster = new SceneDocument { Viewport = vector.Viewport, Elements = [new() { Kind = ElementKind.Image, Width = 90, Height = 40, AssetId = imageId }] };
            var editable = new StampEntry(Guid.NewGuid(), "legacy vector", StampSaveMode.EditableGroup, 90, 40, DateTime.UtcNow.AddMinutes(-1), Guid.NewGuid());
            var merged = new StampEntry(Guid.NewGuid(), "legacy pixels", StampSaveMode.RasterMerge, 90, 40, DateTime.UtcNow, Guid.NewGuid());
            var shared = new StampEntry(Guid.NewGuid(), "legacy shared", StampSaveMode.SharedPrefab, 90, 40, DateTime.UtcNow.AddMinutes(-2), Guid.NewGuid());
            string Base(StampEntry entry) => Path.Combine(root, entry.Id.ToString("N") + "-" + entry.Revision.ToString("N"));
            var projects = new ProjectStore();
            await projects.SaveAsync(Base(editable) + ".floatcanvas", vector, assets);
            await projects.SaveAsync(Base(merged) + ".floatcanvas", raster, assets);
            await projects.SaveAsync(Base(merged) + ".source.floatcanvas", vector, assets);
            var oldFiles = new List<string> { Base(editable) + ".floatcanvas", Base(merged) + ".floatcanvas", Base(merged) + ".source.floatcanvas" };
            var definition = new PrefabDefinition { Name = "legacy source definition", Graph = SceneGraphClone.CloneGraph(vector).Graph };
            if (version == 2)
            {
                await new PrefabStore().SaveAsync(Base(shared) + ".floatprefab", new() { EntryDefinitionId = definition.Id, Definitions = [definition] }, assets);
                oldFiles.Add(Base(shared) + ".floatprefab");
            }
            foreach (string file in oldFiles) await AsV19(file);
            foreach (var entry in new[] { editable, merged, shared }) await File.WriteAllBytesAsync(Base(entry) + ".png", png);
            var original = oldFiles.ToDictionary(file => file, File.ReadAllBytes);
            var entries = version == 1 ? new[] { editable, merged } : new[] { shared, editable, merged };
            Guid?[] pins = new Guid?[10]; pins[0] = editable.Id; pins[9] = version == 1 ? merged.Id : shared.Id;
            var catalog = JsonSerializer.SerializeToNode(new { Version = version, Items = entries, Pins = pins })!.AsObject();
            foreach (var item in catalog["Items"]!.AsArray()) item!.AsObject().Remove("Format");
            if (version == 2) catalog["Order"] = JsonSerializer.SerializeToNode(entries.Select(entry => entry.Id).ToArray());
            await File.WriteAllTextAsync(Path.Combine(root, "library.json"), catalog.ToJsonString());
            var library = new StampLibrary(folder); await library.LoadAsync();
            var expectedOrder = version == 1 ? new[] { merged.Id, editable.Id } : entries.Select(entry => entry.Id).ToArray();
            check($"legacy v{version} catalog opens with original order and shortcuts", library.Items.Select(entry => entry.Id).SequenceEqual(expectedOrder) && library.Pins[0] == editable.Id && library.Pins[9] == pins[9]);
            var loadedVector = await library.LoadStampAsync(editable.Id); var loadedRaster = await library.LoadStampAsync(merged.Id); var loadedSource = await library.LoadSourceAsync(merged.Id);
            check($"legacy v{version} project-backed editable and raster packages retain their storage roles", loadedVector.Document.Elements.Single().Kind == ElementKind.Rectangle &&
                loadedRaster.Document.Elements.Single().Kind == ElementKind.Image && loadedSource.Document.Elements.Single().Kind == ElementKind.Rectangle && loadedRaster.Assets.Get(imageId).Data.Span.SequenceEqual(png));
            await library.RenameAsync(editable.Id, "renamed vector"); await library.RenameAsync(merged.Id, "renamed raster");
            if (version == 2) await library.RenameAsync(shared.Id, "renamed shared");
            var reloaded = new StampLibrary(folder); await reloaded.LoadAsync();
            check($"legacy v{version} rename publishes v3 while preserving order pins and the resolved resource formats", reloaded.Items.Select(entry => entry.Id).SequenceEqual(expectedOrder) &&
                reloaded.Pins.SequenceEqual(pins) && reloaded.Items.All(entry => entry.Format == (entry.Mode == StampSaveMode.SharedPrefab ? StampPackageFormat.Prefab : StampPackageFormat.LegacyProject)) && (await reloaded.LoadStampAsync(editable.Id)).Document.Elements.Single().Kind == ElementKind.Rectangle);
            foreach (var entry in entries)
            {
                var target = new EditorSession(new SceneDocument { Viewport = new(0, 0, 700, 500) });
                await reloaded.PlaceAsync(entry.Id, target, new(350, 250));
                var bounds = SceneEvaluation.Build(target.Document).RenderDocument.Elements.Select(element => ElementGeometry.Bounds(element)).Aggregate(RectD.Union);
                check($"legacy v{version} {entry.Mode} still places centered as one history operation after rename/reload", bounds.Center.DistanceTo(new(350, 250)) < 1e-7 && target.UndoCount == 1 && target.Document.Background is null);
                if (entry.Mode == StampSaveMode.SharedPrefab)
                {
                    await reloaded.PlaceAsync(entry.Id, target, new(450, 300));
                    check("legacy v2 shared prefab retains package identity and shares one definition on repeated placement", target.Document.Instances.Count == 2 && target.Document.Definitions.Count == 1 &&
                        target.Document.Definitions.Single().Source?.SourceResourceId == (await new PrefabStore().LoadAsync(Base(shared) + ".floatprefab")).Package.ResourceId);
                }
            }
            check($"legacy v{version} load rename catalog conversion and placement never rewrite any old package", original.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)));
        }
    }
    private static async Task AsV19(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (string name in new[] { "manifest.json", Path.GetExtension(path) == ".floatprefab" ? "prefab.json" : "scene.json" })
        {
            var entry = zip.GetEntry(name)!; JsonObject json;
            using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            json["Version"] = 19;
            LegacyFixtureSchema.UseForegroundBackground(json);
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString());
        }
    }
}
