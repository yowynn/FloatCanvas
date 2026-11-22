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
            Style = new() { Foreground = 0x00112233, Background = 0x7F445566, FontSize = 13, Variant = ElementAppearance.NumberOutline,
                AdditionalColors = new() { ["accent.2"] = 0x00123456, ["accent.1"] = 0xFFFFFFFF } } };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(23, 37), ChildOrder = [body.Id] };
        body.GroupId = group.Id;
        var definition = new PrefabDefinition { Graph = new() { Elements = [body], Groups = [group], RootOrder = [group.Id] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Rotation(23) };
        var source = new SceneDocument { Definitions = [definition], Instances = [instance], RootOrder = [instance.Id] };
        string path = Path.Combine(root, "canonical.floatcanvas"); string snapshot = JsonSerializer.Serialize(source);
        await store.SaveAsync(path, source, assets); var loaded = await store.LoadAsync(path);
        check(SceneDocument.CurrentVersion >= 12 && loaded.Document.Version == SceneDocument.CurrentVersion && JsonSerializer.Serialize(loaded.Document) == snapshot,
            "current project roundtrip retains outlined badge, extra colors and typography in shared definition graphs");
        check(JsonSerializer.Serialize(source) == snapshot && body.Style.AdditionalColors.Count == 2, "canonical save does not normalize or mutate caller-owned appearance");
        using (var zip = ZipFile.OpenRead(path))
        {
            string text; using (var reader = new StreamReader(zip.GetEntry("scene.json")!.Open())) text = await reader.ReadToEndAsync();
            check(!text.Contains("\"Stroke\"") && !text.Contains("\"Fill\"") && !text.Contains("\"LinkColors\"") && !text.Contains("\"NumberUsesForeground\""),
                "project writes only one canonical color/variant and sizing authority");
        }
        foreach (var mutation in new (string Name, Action<JsonObject> Change)[]
        {
            ("missing-primary", n => Style(n).Remove("PrimaryColor")),
            ("missing-secondary", n => Style(n).Remove("SecondaryColor")),
            ("old-foreground", n => Style(n)["Foreground"] = 7),
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
            check(rejected, "current format rejects incomplete/hybrid/noncanonical nested appearance before publication: " + mutation.Name);
        }
        // A real v8 wire: contradictory raw linked bit and both old Number conventions.
        var oldLeaf = body.Clone(); oldLeaf.GroupId = null; oldLeaf.Style.AdditionalColors.Clear();
        var oldJson = JsonSerializer.SerializeToNode(new SceneDocument { Elements = [oldLeaf] })!.AsObject();
        oldJson["Version"] = 8; LegacyFixtureSchema.RemovePrefabFields(oldJson);
        var oldStyle = oldJson["Elements"]![0]!["Style"]!.AsObject(); oldStyle.Remove("Variant"); oldStyle["Stroke"] = 0xFF112233u; oldStyle["Fill"] = 0x7F445566u;
        var oldBody = oldJson["Elements"]![0]!.AsObject(); oldBody["NumberUsesForeground"] = false;
        string legacy = Path.Combine(root, "real-v8.floatcanvas"); await Write(legacy, "project", "scene.json", oldJson, 8);
        byte[] oldBytes = await File.ReadAllBytesAsync(legacy); var upgraded = (await store.LoadAsync(legacy)).Document;
        var badge = upgraded.Elements[0];
        check(upgraded.Version == SceneDocument.CurrentVersion && !upgraded.ResourceIdentityPersistent && badge.Style.Foreground == 0x7F445566 && badge.Style.Background == 0xFFFFFFFF &&
            badge.Style.AdditionalColors["legacy.stroke"] == 0xFF112233 && badge.Style.AdditionalColors["legacy.fill"] == 0x7F445566 &&
            badge.Style.FontSize == 13 && badge.NumberTextSizeMode == NumberTextSizeMode.CapFontSize,
            "real v8 migration preserves effective legacy badge appearance, hidden raw colors, old font size and original font sizing");
        check((await File.ReadAllBytesAsync(legacy)).AsSpan().SequenceEqual(oldBytes), "loading v8 never rewrites the source package");
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
            n => n["Elements"]![0]!["Style"]!["Variant"] = AppearanceMigration.LegacyTwoColorNumberOutline,
            n => n["Elements"]![0]!["NumberTextSizeMode"] = 0,
            n => n["Elements"]![0]!["Style"]!["Stroke"] = null,
            n => n["Elements"]![0]!["Style"]!["LinkColors"] = null,
            n => n["Elements"]![0]!["NumberUsesForeground"] = null
        })
        {
            string hybrid = Path.Combine(root, Guid.NewGuid() + ".floatcanvas"); File.Copy(legacy, hybrid); await Edit(hybrid, "scene.json", change);
            bool rejected = false; try { await store.LoadAsync(hybrid); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "v8 cannot silently adopt format12 canonical fields or a newly introduced variant");
        }
        // v8 true groups, nested definition and package identity are retained, not flattened/reset.
        var package = new PrefabPackage { Definitions = [definition.Clone()], EntryDefinitionId = definition.Id };
        package.Definitions[0].Graph.Elements[0].Style.Variant = ElementAppearance.NumberOutline;
        package.Definitions[0].Graph.Elements[0].Style.AdditionalColors.Clear();
        var prefabJson = JsonSerializer.SerializeToNode(package)!.AsObject(); prefabJson["Version"] = 19;
        LegacyFixtureSchema.UseForegroundBackground(prefabJson);
        string prefabPath = Path.Combine(root, "real-v19.floatprefab"); await Write(prefabPath, "prefab", "prefab.json", prefabJson, 19);
        var prefabs = new PrefabStore("0.4.0-m5-test"); var imported = (await prefabs.LoadAsync(prefabPath)).Package;
        var importedGraph = imported.Definitions.Single().Graph;
        check(imported.Version == SceneDocument.CurrentVersion && imported.ResourceIdentityPersistent && imported.ResourceId == package.ResourceId && importedGraph.Groups[0].Transform == group.Transform &&
            importedGraph.RootOrder!.SequenceEqual([group.Id]) && importedGraph.Groups[0].ChildOrder!.SequenceEqual([body.Id]) &&
            importedGraph.Elements[0].Style.Variant == ElementAppearance.NumberOutline && importedGraph.Elements[0].NumberTextSizeMode == NumberTextSizeMode.CapFontSize,
            "published v19 prefab explicitly migrates its appearance while preserving stable IDs, real local groups and order ledgers");
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
        await VerifyPreviousColorFormat(root, source, state, package, check);
        await VerifyTextStyles(root, check);
        Console.WriteLine("Canonical appearance storage artifacts: " + root);
    }
    private static async Task VerifyPreviousColorFormat(string root, SceneDocument scene, WorkspaceState state,
        PrefabPackage prefab, Action<bool, string> check)
    {
        static JsonObject Historical<T>(T source)
        {
            var wire = JsonSerializer.SerializeToNode(source)!.AsObject();
            wire["Version"] = 20;
            LegacyFixtureSchema.UseForegroundBackground(wire);
            var style = Style(wire);
            style["Variant"] = AppearanceMigration.LegacyTwoColorNumberOutline;
            style["Foreground"] = 0x80FF0000u;
            style["Background"] = 0xFF0000FFu;
            return wire;
        }
        static bool Converted(CanvasElement badge) => badge.Style.Variant == ElementAppearance.NumberOutline &&
            badge.Style.PrimaryColor == 0x80FF0000u && badge.Style.SecondaryColor == 0x000000FFu &&
            ElementAppearance.NumberTextColor(badge) == badge.Style.PrimaryColor;

        var store = new ProjectStore();
        var old = Historical(scene);
        string path = Path.Combine(root, "red-ring-blue-digit.floatcanvas");
        await Write(path, "project", "scene.json", old, 20);
        byte[] original = await File.ReadAllBytesAsync(path);
        var read = await store.LoadAsync(path);
        byte[] afterOpen = await File.ReadAllBytesAsync(path);
        check(Converted(read.Document.Definitions[0].Graph.Elements[0]) && original.AsSpan().SequenceEqual(afterOpen),
            "v20 opens red ring / blue digit as matching red ink over transparent fill, leaving source bytes intact");

        var options = new JsonSerializerOptions { Converters = { new EmbeddedDocumentConverter() } };
        var clipboardWire = (JsonObject)old.DeepClone();
        var preview = JsonSerializer.SerializeToNode(new SubprojectCanvasSettings { Background = new()
        {
            Kind = ElementKind.Image, Width = 20, Height = 10, AssetId = new string('a', 64),
            Style = new() { PrimaryColor = 0x80123456, SecondaryColor = 0x00778899 }
        } })!;
        LegacyFixtureSchema.UseForegroundBackground(preview);
        clipboardWire["Definitions"]![0]!["PreviewCanvas"] = preview;
        var clipboard = clipboardWire.Deserialize<SceneDocument>(options)!;
        check(clipboard.Definitions[0].PreviewCanvas!.Background!.Style is { PrimaryColor: 0x80123456, SecondaryColor: 0x00778899 },
            "independent preview background uses the same historical style reader");
        check(clipboard.Version == SceneDocument.CurrentVersion && Converted(clipboard.Definitions[0].Graph.Elements[0]),
            "editable clipboard document uses the same old-color boundary");
        var wire = JsonSerializer.SerializeToNode(clipboard, options)!.AsObject();
        check(Style(wire).ContainsKey("PrimaryColor") && Style(wire).ContainsKey("SecondaryColor") &&
            !Style(wire).ContainsKey("Foreground") && !Style(wire).ContainsKey("Background"),
            "clipboard writes the current schema on its first round trip");
        var badge = read.Document.Definitions[0].Graph.Elements[0];
        badge.Style.SecondaryColor = 0x7F33AA66;
        string saved = Path.Combine(root, "edited-v21.floatcanvas");
        await store.SaveAsync(saved, read.Document, read.Assets);
        check((await store.LoadAsync(saved)).Document.Definitions[0].Graph.Elements[0].Style.SecondaryColor == 0x7F33AA66,
            "editing and reopening a migrated ring retains its newly chosen translucent fill");

        string prefabPath = Path.Combine(root, "two-tone.floatprefab");
        await Write(prefabPath, "prefab", "prefab.json", Historical(prefab), 20);
        check(Converted((await new PrefabStore().LoadAsync(prefabPath)).Package.Definitions[0].Graph.Elements[0]),
            "prefab import converts the same two-tone badge inside its definition");
        var recovery = new RecoveryStore(Path.Combine(root, "historical-recovery"));
        Directory.CreateDirectory(recovery.DirectoryPath);
        var workspace = JsonSerializer.SerializeToNode(state)!.AsObject();
        workspace["LiveDocument"] = Historical(state.LiveDocument);
        workspace["CaptureDocument"] = Historical(state.CaptureDocument!);
        workspace["PendingPrefabEdit"]!["DraftDocument"] = Historical(state.PendingPrefabEdit!.DraftDocument);
        await Write(recovery.CurrentPath, "recovery", "workspace.json", workspace, 20);
        var restored = (await recovery.LoadAsync())!.State;
        check(new[] { restored.LiveDocument, restored.CaptureDocument!, restored.PendingPrefabEdit!.DraftDocument }.All(doc =>
                doc.Version == SceneDocument.CurrentVersion && Converted(doc.Definitions[0].Graph.Elements[0])),
            "live, frozen capture and recoverable draft all arrive in the current schema");
    }

    private static async Task VerifyTextStyles(string root, Action<bool, string> check)
    {
        var variants = AppearanceRegistry.VariantsFor(ElementKind.Text);
        var scene = new SceneDocument { Elements = variants.Select(v => new CanvasElement
        {
            Kind = ElementKind.Text, Text = "集合点", Width = 160, Height = 60, TextAutoWidth = true,
            Style = new() { Variant = v.Id, PrimaryColor = 0x802383E2, FontSize = 32,
                SecondaryColor = v.Id == ElementAppearance.TextOutline ? 0x704567ABu : 0, HasTextOutlineColor = v.Id == ElementAppearance.TextOutline }
        }).ToList() };
        string path = Path.Combine(root, "three-text-styles.floatcanvas");
        var projects = new ProjectStore();
        await projects.SaveAsync(path, scene, new());
        var reopened = (await projects.LoadAsync(path)).Document;
        check(JsonSerializer.Serialize(reopened) == JsonSerializer.Serialize(scene), "text styles, both authored alpha channels, frames and fonts round-trip exactly");
        var mixed = JsonSerializer.SerializeToNode(scene)!.AsObject();
        LegacyFixtureSchema.UseForegroundBackground(mixed["Elements"]![0]);
        string hybrid = Path.Combine(root, "mixed-color-contracts.floatcanvas");
        await Write(hybrid, "project", "scene.json", mixed, 20);
        bool mixedRejected = false;
        try { await projects.LoadAsync(hybrid); } catch (InvalidDataException) { mixedRejected = true; }
        check(mixedRejected, "one package cannot mix published and current color field contracts");
        var released = JsonSerializer.SerializeToNode(scene)!.AsObject();
        LegacyFixtureSchema.UseForegroundBackground(released);
        foreach (var element in released["Elements"]!.AsArray()) element!["Style"]!["Variant"] = ElementAppearance.Default;
        string plain = Path.Combine(root, "plain-release-v20.floatcanvas");
        await Write(plain, "project", "scene.json", released, 20);
        var published = (await projects.LoadAsync(plain)).Document;
        check(published.Version == 20 && published.Elements.All(e => e.Style.Variant == ElementAppearance.Default &&
            e.Style.PrimaryColor == 0x802383E2 && e.Style.FontSize == 32), "published v20 plain text retains its colors and size");
        var oldEmoji = released.DeepClone().AsObject();
        var oldElements = oldEmoji["Elements"]!.AsArray();
        foreach (var item in oldElements) item!["Style"]!["FontFamily"] = "Segoe UI Emoji";
        oldElements[0]!["Text"] = "👩🏽‍🎨"; oldElements[1]!["Text"] = "A";
        oldElements[2]!["Text"] = "🔥🔥";
        var oldEmojiTransform = Affine2D.Translation(240, -35).Compose(Affine2D.Rotation(31)).Compose(Affine2D.Scale(2, 3));
        oldElements[0]!["Transform"] = JsonSerializer.SerializeToNode(oldEmojiTransform);
        var oldEmojiCenter = oldEmojiTransform.TransformPoint(new PointD(80, 30));
        string oldPath = Path.Combine(root, "published-emoji.floatcanvas");
        await Write(oldPath, "project", "scene.json", oldEmoji, 20);
        var migrated = (await projects.LoadAsync(oldPath)).Document;
        check(migrated.Elements[0].Kind == ElementKind.Emoji && migrated.Elements[0].Text == "👩🏽‍🎨" &&
            Math.Abs(migrated.Elements[0].Style.Opacity - published.Elements[0].Style.Opacity * 128 / 255) < .00001 &&
            migrated.Elements.Skip(1).All(e => e.Kind==ElementKind.Text), "published single emoji migrate on open; letters and multiple emoji stay text");
        var migratedEmojiBounds = ElementGeometry.Bounds(migrated.Elements[0], false);
        check(migrated.Elements[0].Width == 160 && migrated.Elements[0].Height == 60 &&
            Math.Abs(migratedEmojiBounds.Center.X - oldEmojiCenter.X) < .00001 &&
            Math.Abs(migratedEmojiBounds.Center.Y - oldEmojiCenter.Y) < .00001,
            "published non-square emoji retains its frame and transformed center");
        var prefabEmoji = new CanvasElement
        {
            Kind = ElementKind.Text, Text = "😀", Width = 160, Height = 60,
            Transform = Affine2D.Translation(-80, 45).Compose(Affine2D.Rotation(17)).Compose(Affine2D.Scale(.75, 1.4)),
            Style = new()
            {
                FontFamily = "Segoe UI Emoji", FontSize = 32,
                PrimaryColor = 0xFF2383E2, SecondaryColor = 0, Variant = ElementAppearance.Default
            }
        };
        var prefabCenter = prefabEmoji.Transform.TransformPoint(new PointD(80, 30));
        var prefabDefinition = new PrefabDefinition
        {
            Graph = new() { Elements = [prefabEmoji], RootOrder = [prefabEmoji.Id] }
        };
        var prefabPackage = new PrefabPackage { Definitions = [prefabDefinition], EntryDefinitionId = prefabDefinition.Id };
        var oldPrefabEmoji = JsonSerializer.SerializeToNode(prefabPackage)!.AsObject();
        oldPrefabEmoji["Version"] = 20;
        LegacyFixtureSchema.UseForegroundBackground(oldPrefabEmoji);
        string oldPrefabEmojiPath = Path.Combine(root, "published-emoji.floatprefab");
        await Write(oldPrefabEmojiPath, "prefab", "prefab.json", oldPrefabEmoji, 20);
        var migratedPrefabEmoji = (await new PrefabStore().LoadAsync(oldPrefabEmojiPath)).Package
            .Definitions.Single().Graph.Elements.Single();
        var migratedPrefabBounds = ElementGeometry.Bounds(migratedPrefabEmoji, false);
        check(migratedPrefabEmoji.Kind == ElementKind.Emoji &&
            migratedPrefabEmoji.Width == 160 && migratedPrefabEmoji.Height == 60 &&
            Math.Abs(migratedPrefabBounds.Center.X - prefabCenter.X) < .00001 &&
            Math.Abs(migratedPrefabBounds.Center.Y - prefabCenter.Y) < .00001,
            "published prefab emoji retains its non-square frame and transformed center");
        migrated.Elements[1].Text = "🔥";
        string newPath=Path.Combine(root,"emoji-roundtrip.floatcanvas");
        await projects.SaveAsync(newPath,migrated,new());
        var emojiRoundtrip=(await projects.LoadAsync(newPath)).Document;
        check(emojiRoundtrip.Version==20 && emojiRoundtrip.Elements[0].Kind==ElementKind.Emoji && emojiRoundtrip.Elements[1].Kind==ElementKind.Text &&
            emojiRoundtrip.Elements[0].Width==160 && emojiRoundtrip.Elements[0].Height==60 &&
            emojiRoundtrip.Elements[0].Style.Opacity==migrated.Elements[0].Style.Opacity,
            "current non-square emoji and intentionally authored single-emoji text retain their own kind and frame on reopen");
        emojiRoundtrip.Elements[0].Text="汉";
        bool invalidEmoji=false;
        try { await projects.SaveAsync(newPath,emojiRoundtrip,new()); } catch (InvalidDataException) { invalidEmoji=true; }
        check(invalidEmoji && (await projects.LoadAsync(newPath)).Document.Elements[0].Text=="👩🏽‍🎨","an invalid emoji cannot replace the saved project");
        foreach (int version in new[] { 4, 5, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 21, 22 })
        {
            released["Version"] = version;
            string draft = Path.Combine(root, "unsupported-" + version + ".floatcanvas");
            await Write(draft, "project", "scene.json", released, version);
            bool rejected = false;
            try { await projects.LoadAsync(draft); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "an unpublished version has no format migration: " + version);
        }
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
