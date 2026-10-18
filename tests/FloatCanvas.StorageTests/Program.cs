using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
if (args.Contains("--live-effects-only"))
{
    await LivePixelateStorageTests.RunAsync(Check);
    Console.WriteLine($"PASS {checks} live-effect storage checks"); return;
}
if (args.Contains("--recovery-preview-only"))
{
    await RecoveryPreviewStorageTests.RunAsync(Check);
    Console.WriteLine($"PASS {checks} recovery preview storage checks"); return;
}
async Task Reject(Func<Task> action, string message)
{
    bool rejected = false;
    try { await action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { rejected = true; }
    Check(rejected, message);
}
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/storage-tests/" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var store = new ProjectStore();
byte[] transparentPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
using (var payload = new MemoryStream(transparentPng, 41, 11))
using (var zlib = new ZLibStream(payload, CompressionMode.Decompress))
{
    byte[] scanline = new byte[3]; zlib.ReadExactly(scanline);
    Check(transparentPng[25] == 4 && scanline[0] == 0 && scanline[2] == 0, "PNG fixture has actual alpha zero");
}
var assets = new AssetLibrary(); string assetId = assets.Add(transparentPng); assets.Add([1, 2, 3]);
var image = new CanvasElement
{
    Kind = ElementKind.Image, AssetId = assetId, Width = 240, Height = 180,
    Transform = Affine2D.Translation(-400, 250).Compose(Affine2D.Rotation(37)).Compose(Affine2D.Scale(1.2, .7)),
    ImageCrop = new(.1, .2, .7, .6), Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(.2, .3, .4, .2), Amount = 8 }]
};
var document = new SceneDocument
{
    Viewport = new(-1920, -1080, 3840, 2160), NextNumber = 42, LayerOpacity = .7,
    Elements = [image, new() { Kind = ElementKind.Freehand, Points = [new(-20, 0), new(100, 120)], Style = new() { Stroke = 0xFF22CC88, StrokeWidth = 8 } }, new() { Kind = ElementKind.Text, Text = "浮绘\n继续编辑", Width = 240, Height = 90 }],
    DisplayLayout = [new() { DeviceName = "negative-DPI", Bounds = new(-1920, 0, 1920, 1080), DpiX = 144, DpiY = 144 }]
};
string project = Path.Combine(root, "roundtrip.floatcanvas");
await store.SaveAsync(project, document.Clone(), assets);
var loaded = await store.LoadAsync(project);
Check(JsonSerializer.Serialize(loaded.Document) == JsonSerializer.Serialize(document), "project scene exact roundtrip");
Check(loaded.Assets.Count == 1 && loaded.Assets.Get(assetId).Data.Span.SequenceEqual(transparentPng), "self-contained transparent image bytes preserved; unused asset omitted");
using (var zip = ZipFile.OpenRead(project))
{
    Check(zip.Entries.Count == 3, "ZIP contains only manifest, scene, referenced asset");
    Check(zip.GetEntry("manifest.json") is not null && zip.GetEntry("scene.json") is not null && zip.GetEntry("assets/" + assetId) is not null, "stable package layout");
}
document.Elements[0].Effects.Clear();
Check(loaded.Document.Elements[0].Effects.Count == 1, "load owns independent object graph");
var session = new EditorSession(loaded.Document, loaded.Assets); session.SetSelection([image.Id]); session.TransformSelection(Affine2D.Rotation(15));
Check(session.CanUndo && session.Undo(), "loaded objects remain editable with undo");

async Task<string> Mutated(string name, Action<Dictionary<string, byte[]>> mutation)
{
    var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    using (var zip = ZipFile.OpenRead(project))
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); entries.Add(entry.FullName, buffer.ToArray());
        }
    mutation(entries);
    string path = Path.Combine(root, name + ".floatcanvas");
    await using var file = File.Create(path);
    using var output = new ZipArchive(file, ZipArchiveMode.Create);
    foreach (var (key, value) in entries) { var entry = output.CreateEntry(key); using var stream = entry.Open(); stream.Write(value); }
    return path;
}
void EditJson(Dictionary<string, byte[]> entries, string path, Action<JsonNode> edit)
{
    var node = JsonNode.Parse(entries[path])!; edit(node); entries[path] = Encoding.UTF8.GetBytes(node.ToJsonString());
}

string missing = await Mutated("missing-resource", e => e.Remove("assets/" + assetId));
await Reject(() => store.LoadAsync(missing), "missing asset rejected");
string hash = await Mutated("bad-hash", e => e["assets/" + assetId][^1] ^= 0xFF);
await Reject(() => store.LoadAsync(hash), "asset hash mismatch rejected");
string version = await Mutated("new-version", e => EditJson(e, "manifest.json", n => n["Version"] = 99));
await Reject(() => store.LoadAsync(version), "future format version rejected");
string sceneVersion = await Mutated("new-scene-version", e => EditJson(e, "scene.json", n => n["Version"] = 99));
await Reject(() => store.LoadAsync(sceneVersion), "future scene version rejected");
string missingVersion = await Mutated("missing-version", e => EditJson(e, "scene.json", n => n.AsObject().Remove("Version")));
await Reject(() => store.LoadAsync(missingVersion), "scene version cannot be inferred from default");
string duplicate = await Mutated("duplicate-id", e => EditJson(e, "scene.json", n => n["Elements"]![1]!["Id"] = image.Id.ToString()));
await Reject(() => store.LoadAsync(duplicate), "duplicate object IDs rejected");
string nullCollection = await Mutated("null-elements", e => EditJson(e, "scene.json", n => n["Elements"] = null));
await Reject(() => store.LoadAsync(nullCollection), "null collections rejected before replacing caller document");
string invalidEnum = await Mutated("invalid-enum", e => EditJson(e, "scene.json", n => n["Elements"]![0]!["Kind"] = 999));
await Reject(() => store.LoadAsync(invalidEnum), "undefined element kinds rejected");
string invalidTransform = await Mutated("singular-transform", e => EditJson(e, "scene.json", n => { n["Elements"]![0]!["Transform"]!["M11"] = 0; n["Elements"]![0]!["Transform"]!["M12"] = 0; }));
await Reject(() => store.LoadAsync(invalidTransform), "singular transformation rejected");
string invalidCrop = await Mutated("invalid-crop", e => EditJson(e, "scene.json", n => n["Elements"]![0]!["ImageCrop"]!["Width"] = 2));
await Reject(() => store.LoadAsync(invalidCrop), "out of bounds normalized crop rejected");
string badLength = await Mutated("bad-length", e => EditJson(e, "manifest.json", n => n["Assets"]![0]!["Length"] = 1));
await Reject(() => store.LoadAsync(badLength), "declared asset length mismatch rejected");
string traversal = await Mutated("unsafe-entry", e => e.Add("../../outside.txt", [1, 2, 3]));
await Reject(() => store.LoadAsync(traversal), "undeclared traversal entry rejected; nothing extracted");
string duplicateJson = await Mutated("duplicate-property", e => e["scene.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(e["scene.json"]).Replace($"\"Version\":{SceneDocument.CurrentVersion}", $"\"Version\":{SceneDocument.CurrentVersion},\"Version\":{SceneDocument.CurrentVersion}", StringComparison.Ordinal)));
await Reject(() => store.LoadAsync(duplicateJson), "ambiguous duplicate JSON properties rejected");
string truncated = Path.Combine(root, "truncated.floatcanvas");
byte[] originalPackage = await File.ReadAllBytesAsync(project); await File.WriteAllBytesAsync(truncated, originalPackage[..(originalPackage.Length / 2)]);
await Reject(() => store.LoadAsync(truncated), "truncated ZIP rejected");
Check(loaded.Document.Elements.Count == 3 && loaded.Assets.Count == 1, "failed loads leave caller current scene unchanged");

var badScene = loaded.Document.Clone(); badScene.Viewport = new(double.NaN, 0, 100, 100);
await Reject(() => store.SaveAsync(project, badScene, loaded.Assets), "nonfinite geometry rejected before save");
await Reject(() => store.SaveAsync(project, loaded.Document.Clone(), new()), "missing asset rejected before save");
Check((await File.ReadAllBytesAsync(project)).SequenceEqual(originalPackage), "invalid save preserves current good file");
var canceled = new CancellationTokenSource(); canceled.Cancel(); bool canceledObserved = false;
try { await store.SaveAsync(project, loaded.Document.Clone(), loaded.Assets, canceled.Token); } catch (OperationCanceledException) { canceledObserved = true; }
Check(canceledObserved && (await File.ReadAllBytesAsync(project)).SequenceEqual(originalPackage), "pre-canceled save preserves original file");

// Cancel after the temporary file exists, proving cancellation cannot tear the destination.
var bigAssets = new AssetLibrary(); string bigId = bigAssets.Add(new byte[64 * 1024 * 1024]);
var bigDocument = loaded.Document.Clone(); bigDocument.Elements.Clear(); bigDocument.Elements.Add(new() { Kind = ElementKind.Image, Width = 100, Height = 100, AssetId = bigId });
using (var inFlightCancellation = new CancellationTokenSource())
{
    Task saving = store.SaveAsync(project, bigDocument, bigAssets, inFlightCancellation.Token);
    while (!saving.IsCompleted && !Directory.EnumerateFiles(root, "roundtrip.floatcanvas.*.tmp").Any()) await Task.Delay(1);
    inFlightCancellation.Cancel(); bool interrupted = false;
    try { await saving; } catch (OperationCanceledException) { interrupted = true; }
    Check(interrupted, "in-flight save cancellation reached temporary-file stage");
    Check((await File.ReadAllBytesAsync(project)).SequenceEqual(originalPackage), "in-flight cancellation leaves destination byte-for-byte intact");
    Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "cancelled temporary file cleaned");
}

var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
Check(await recovery.LoadAsync() is null, "first launch has no recovery");
var live = loaded.Document.Clone(); var capture = live.CreateCapture(new(-500, 0, 1000, 800), assetId);
var state = new WorkspaceState { LiveDocument = live, CaptureDocument = capture, CaptureOrigin = new(-500, 0) };
await recovery.SaveAsync(state.Clone(), loaded.Assets);
var restored = (await recovery.LoadAsync())!;
Check(!restored.FromBackup && restored.State.LiveDocument.Kind == DocumentKind.LiveOverlay && restored.State.CaptureDocument!.Kind == DocumentKind.FrozenCapture, "both live and capture restore");
Check(restored.Assets.Count == 1 && restored.State.CaptureOrigin == new PointD(-500, 0), "recovery shares asset and preserves capture origin");
Check(JsonSerializer.Serialize(restored.State) == JsonSerializer.Serialize(state), "recovery exact workspace roundtrip");
int originalNextNumber = live.NextNumber;
state.LiveDocument.NextNumber = 88; await recovery.SaveAsync(state.Clone(), loaded.Assets);
Check(File.Exists(recovery.BackupPath), "second generation retains valid predecessor");
await File.WriteAllBytesAsync(recovery.CurrentPath, [1, 2, 3]);
var fallback = (await recovery.LoadAsync())!;
Check(fallback.FromBackup && fallback.State.LiveDocument.NextNumber == originalNextNumber, "corrupt current falls back to valid previous generation");
state.LiveDocument.NextNumber = 99; await recovery.SaveAsync(state.Clone(), loaded.Assets);
Check((await recovery.LoadAsync())!.State.LiveDocument.NextNumber == 99, "save after fallback writes a fresh current");
await File.WriteAllBytesAsync(recovery.CurrentPath, [4, 5, 6]);
Check((await recovery.LoadAsync())!.State.LiveDocument.NextNumber == originalNextNumber, "corrupt current never overwrote valid backup during save");
await File.WriteAllBytesAsync(recovery.BackupPath, [7, 8, 9]);
await Reject(async () => { await recovery.LoadAsync(); }, "all recovery generations corrupt must report error");
Check(File.Exists(recovery.CurrentPath) && File.Exists(recovery.BackupPath), "corrupt generations preserved for investigation");
byte[] brokenCurrent = await File.ReadAllBytesAsync(recovery.CurrentPath), brokenPrevious = await File.ReadAllBytesAsync(recovery.BackupPath);
var archived = await recovery.ArchiveExistingAsync();
Check(archived.Count == 2 && archived.All(path => path.StartsWith(Path.Combine(recovery.DirectoryPath, "quarantine"), StringComparison.Ordinal)), "failed recovery generations archived outside active filenames");
Check((await File.ReadAllBytesAsync(archived.Single(path => Path.GetFileName(path) == Path.GetFileName(recovery.CurrentPath)))).SequenceEqual(brokenCurrent), "corrupt current archived byte-exactly");
Check((await File.ReadAllBytesAsync(archived.Single(path => Path.GetFileName(path) == Path.GetFileName(recovery.BackupPath)))).SequenceEqual(brokenPrevious), "corrupt previous archived byte-exactly");
await recovery.SaveAsync(state.Clone(), loaded.Assets);
Check((await recovery.LoadAsync())!.State.LiveDocument.NextNumber == state.LiveDocument.NextNumber, "new recovery history can start after preserving old generations");
Check((await File.ReadAllBytesAsync(archived.Single(path => Path.GetFileName(path) == Path.GetFileName(recovery.CurrentPath)))).SequenceEqual(brokenCurrent), "later autosave cannot overwrite quarantined corrupt current");
var orderedRecovery = new RecoveryStore(Path.Combine(root, "ordered-recovery"));
var saveCalls = new List<Task>();
for (int i = 1; i <= 6; i++)
{
    var snapshot = state.Clone(); snapshot.LiveDocument.NextNumber = i;
    saveCalls.Add(orderedRecovery.SaveAsync(snapshot, loaded.Assets));
}
await Task.WhenAll(saveCalls);
Check((await orderedRecovery.LoadAsync())!.State.LiveDocument.NextNumber == 6, "concurrent recovery snapshots preserve invocation order");

await ArrowHeadStorageTests.RunAsync(store, root, Check);
await EditablePresetStorageTests.RunAsync(store, root, Check);
await FormatVersionTests.RunAsync(store, root, Check);
await LivePixelateStorageTests.RunAsync(Check);
await RecoveryPreviewStorageTests.RunAsync(Check);
Console.WriteLine($"PASS {checks} storage checks");
Console.WriteLine($"Artifacts: {root}");
