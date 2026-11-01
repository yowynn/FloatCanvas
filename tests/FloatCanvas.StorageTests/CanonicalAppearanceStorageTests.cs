using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CanonicalAppearanceStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "canonical-appearance", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var assets = new AssetLibrary(); var store = new ProjectStore("0.4.0-m5-test");
        var body = new CanvasElement { Kind = ElementKind.Number, Width = 60, Height = 60, Number = 23,
            NumberTextSizeMode = NumberTextSizeMode.CapFontSize,
            Style = new() { Foreground = 0x00112233, Background = 0x7F445566, FontSize = 13, Variant = ElementAppearance.NumberOutlineIndependent,
                AdditionalColors = new() { ["accent.2"] = 0x00123456, ["accent.1"] = 0xFFFFFFFF } } };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(23, 37), ChildOrder = [body.Id] };
        body.GroupId = group.Id;
        var definition = new PrefabDefinition { Graph = new() { Elements = [body], Groups = [group], RootOrder = [group.Id] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Rotation(23) };
        var source = new SceneDocument { Definitions = [definition], Instances = [instance], RootOrder = [instance.Id] };
        string path = Path.Combine(root, "canonical.floatcanvas"); string snapshot = JsonSerializer.Serialize(source);
        await store.SaveAsync(path, source, assets); var loaded = await store.LoadAsync(path);
        check(SceneDocument.CurrentVersion >= 12 && loaded.Document.Version == SceneDocument.CurrentVersion && JsonSerializer.Serialize(loaded.Document) == snapshot,
            "format12 project roundtrip retains third variant, extra colors and typography in shared definition graphs");
        check(JsonSerializer.Serialize(source) == snapshot && body.Style.AdditionalColors.Count == 2, "canonical save does not normalize or mutate caller-owned appearance");
        using (var zip = ZipFile.OpenRead(path))
        {
            string text; using (var reader = new StreamReader(zip.GetEntry("scene.json")!.Open())) text = await reader.ReadToEndAsync();
            check(!text.Contains("\"Stroke\"") && !text.Contains("\"Fill\"") && !text.Contains("\"LinkColors\"") && !text.Contains("\"NumberUsesForeground\""),
                "project writes only one canonical color/variant and sizing authority");
        }
        foreach (var mutation in new (string Name, Action<JsonObject> Change)[]
        {
            ("missing-foreground", n => Style(n).Remove("Foreground")),
            ("missing-background", n => Style(n).Remove("Background")),
            ("missing-extra", n => Style(n).Remove("AdditionalColors")),
            ("missing-variant", n => Style(n).Remove("Variant")),
            ("null-variant", n => Style(n)["Variant"] = null),
            ("unknown-variant", n => Style(n)["Variant"] = "number.future"),
            ("wrong-type-variant", n => Style(n)["Variant"] = ElementAppearance.ShapeUniform),
            ("alias-stroke", n => Style(n)["Stroke"] = 7),
            ("alias-fill", n => Style(n)["Fill"] = 7),
            ("alias-link", n => Style(n)["LinkColors"] = true),
            ("alias-number", n => Leaf(n)["NumberUsesForeground"] = true),
            ("missing-sizing", n => Leaf(n).Remove("NumberTextSizeMode")),
            ("unknown-sizing", n => Leaf(n)["NumberTextSizeMode"] = 77),
            ("null-extra", n => Style(n)["AdditionalColors"] = null),
            ("reserved-role", n => Style(n)["AdditionalColors"]!["foreground"] = 1),
            ("uppercase-role", n => Style(n)["AdditionalColors"]!["Accent"] = 1),
            ("too-many-roles", n => Style(n)["AdditionalColors"] = new JsonObject(Enumerable.Range(0,17).Select(i => KeyValuePair.Create<string,JsonNode?>("accent."+i, JsonValue.Create(i)))))
        })
        {
            string invalid = Path.Combine(root, mutation.Name + ".floatcanvas"); File.Copy(path, invalid);
            await Edit(invalid, "scene.json", mutation.Change);
            bool rejected = false; try { await store.LoadAsync(invalid); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "format12 rejects incomplete/hybrid/noncanonical nested appearance before publication: " + mutation.Name);
        }
        // A real v11 wire: contradictory raw linked bit and both old Number conventions.
        var oldLeaf = body.Clone(); oldLeaf.GroupId = null; oldLeaf.Style.AdditionalColors.Clear();
        var oldJson = JsonSerializer.SerializeToNode(new SceneDocument { Elements = [oldLeaf] })!.AsObject();
        oldJson["Version"] = 11; LegacyFixtureSchema.UseLegacyAppearance(oldJson);
        var oldStyle = oldJson["Elements"]![0]!["Style"]!.AsObject(); oldStyle.Remove("Variant"); oldStyle["Stroke"] = 0xFF112233u; oldStyle["Fill"] = 0x7F445566u;
        var oldBody = oldJson["Elements"]![0]!.AsObject(); oldBody["NumberUsesForeground"] = false;
        string legacy = Path.Combine(root, "real-v11.floatcanvas"); await Write(legacy, "project", "scene.json", oldJson, 11);
        byte[] oldBytes = await File.ReadAllBytesAsync(legacy); var upgraded = (await store.LoadAsync(legacy)).Document;
        var badge = upgraded.Elements[0];
        check(upgraded.Version == SceneDocument.CurrentVersion && upgraded.ResourceIdentityPersistent && badge.Style.Foreground == 0x7F445566 && badge.Style.Background == 0xFFFFFFFF &&
            badge.Style.AdditionalColors["legacy.stroke"] == 0xFF112233 && badge.Style.AdditionalColors["legacy.fill"] == 0x7F445566 &&
            badge.Style.FontSize == 13 && badge.NumberTextSizeMode == NumberTextSizeMode.CapFontSize,
            "real v11 migration preserves effective legacy badge appearance, hidden raw colors, old font size and persistent resource identity");
        check((await File.ReadAllBytesAsync(legacy)).AsSpan().SequenceEqual(oldBytes), "loading v11 never rewrites the source package");
        var sparse = (JsonObject)oldJson.DeepClone(); sparse["Version"] = 1; LegacyFixtureSchema.RemovePrefabFields(sparse);
        sparse["Elements"]![0]!.AsObject().Remove("Style");
        string sparsePath = Path.Combine(root, "sparse-v1.floatcanvas"); await Write(sparsePath, "project", "scene.json", sparse, 1);
        var sparseElement = (await store.LoadAsync(sparsePath)).Document.Elements[0];
        check(sparseElement.Style.Foreground == 0xFFEF4444 && sparseElement.Style.Background == 0xFFFFFFFF && sparseElement.Style.FontSize == 24,
            "sparse v1 package without Style preserves the original badge defaults rather than adopting canonical transparent text");
        await store.SaveAsync(Path.Combine(root, "resaved.floatcanvas"), upgraded, assets);
        check(JsonSerializer.Serialize((await store.LoadAsync(Path.Combine(root, "resaved.floatcanvas"))).Document) == JsonSerializer.Serialize(upgraded),
            "migrated historical number is stable after canonical resave/reopen");
        foreach (var change in new Action<JsonObject>[]
        {
            n => n["Elements"]![0]!["Style"]!["Foreground"] = 1,
            n => n["Elements"]![0]!["Style"]!["AdditionalColors"] = new JsonObject(),
            n => n["Elements"]![0]!["Style"]!["Variant"] = ElementAppearance.NumberOutlineIndependent,
            n => n["Elements"]![0]!["NumberTextSizeMode"] = 0,
            n => n["Elements"]![0]!["Style"]!["Stroke"] = null,
            n => n["Elements"]![0]!["Style"]!["LinkColors"] = null,
            n => n["Elements"]![0]!["NumberUsesForeground"] = null
        })
        {
            string hybrid = Path.Combine(root, Guid.NewGuid() + ".floatcanvas"); File.Copy(legacy, hybrid); await Edit(hybrid, "scene.json", change);
            bool rejected = false; try { await store.LoadAsync(hybrid); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "v11 cannot silently adopt format12 canonical fields or a newly introduced variant");
        }
        // v11 true groups, nested definition and package identity are retained, not flattened/reset.
        var package = new PrefabPackage { Definitions = [definition.Clone()], EntryDefinitionId = definition.Id };
        package.Definitions[0].Graph.Elements[0].Style.Variant = ElementAppearance.NumberOutline;
        package.Definitions[0].Graph.Elements[0].Style.AdditionalColors.Clear();
        var prefabJson = JsonSerializer.SerializeToNode(package)!.AsObject(); prefabJson["Version"] = 11;
        foreach (var item in prefabJson["Definitions"]!.AsArray()) LegacyFixtureSchema.UseLegacyAppearance(item!["Graph"]!.AsObject());
        string prefabPath = Path.Combine(root, "real-v11.floatprefab"); await Write(prefabPath, "prefab", "prefab.json", prefabJson, 11);
        var prefabs = new PrefabStore("0.4.0-m5-test"); var imported = (await prefabs.LoadAsync(prefabPath)).Package;
        var importedGraph = imported.Definitions.Single().Graph;
        check(imported.Version == SceneDocument.CurrentVersion && imported.ResourceIdentityPersistent && imported.ResourceId == package.ResourceId && importedGraph.Groups[0].Transform == group.Transform &&
            importedGraph.RootOrder!.SequenceEqual([group.Id]) && importedGraph.Groups[0].ChildOrder!.SequenceEqual([body.Id]) &&
            importedGraph.Elements[0].Style.Variant == ElementAppearance.NumberOutline && importedGraph.Elements[0].NumberTextSizeMode == NumberTextSizeMode.CapFontSize,
            "format11 prefab explicitly migrates its appearance while preserving stable IDs, real local groups and order ledgers");
        string newPrefab = Path.Combine(root, "resaved.floatprefab"); await prefabs.SaveAsync(newPrefab, imported, assets);
        check(JsonSerializer.Serialize((await prefabs.LoadAsync(newPrefab)).Package) == JsonSerializer.Serialize(imported), "migrated prefab12 package reopens without appearance drift");
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        var capture = source.Clone(); capture.Kind = DocumentKind.FrozenCapture;
        var state = new WorkspaceState { LiveDocument = source, CaptureDocument = capture,
            PendingPrefabEdit = new() { OwnerResourceId = source.ResourceId, DefinitionId = definition.Id, ExpectedRevision = definition.Revision,
                DraftDocument = source.Clone() } };
        await recovery.SaveAsync(state, assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State) == JsonSerializer.Serialize(state),
            "canonical appearance survives live/capture/recoverable definition-draft graphs with independent extra colors");
        Console.WriteLine("Canonical appearance storage artifacts: " + root);
    }
    private static JsonObject Leaf(JsonObject source) => source["Definitions"]![0]!["Graph"]!["Elements"]![0]!.AsObject();
    private static JsonObject Style(JsonObject source) => Leaf(source)["Style"]!.AsObject();
    private static async Task Edit(string path, string entryName, Action<JsonObject> mutation)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry(entryName)!; JsonObject json;
        using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        mutation(json); entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry(entryName).Open()); await writer.WriteAsync(json.ToJsonString());
    }
    private static async Task Write(string path, string kind, string entry, JsonObject content, int version)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(new JsonObject
        { ["Format"] = "FloatCanvas." + kind, ["Version"] = version, ["Payload"] = entry, ["Assets"] = new JsonArray(), ["CreatedWith"] = "0.4.0-m4", ["LastSavedWith"] = "0.4.0-m4" }.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) await writer.WriteAsync(content.ToJsonString());
    }
}
