using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class WindowSourcePersistenceTests
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "window-persistence", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var assets = new AssetLibrary(); string oldImage = assets.Add(Png(SKColors.Black)); string draftOnly = assets.Add(Png(SKColors.Lime));
        var image = Image(oldImage); image.ImageCrop = new(.5, 0, .5, 1); image.ImageFrame = [new(0, 0), new(1, 0), new(.5, 1)];
        image.Style.Opacity = .37; image.Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1) }];
        var document = new SceneDocument { Elements = [image] }; var session = new EditorSession(document, assets);
        var budget = new WindowFrameBudget(1024); using var pixels = new SKBitmap(8, 4); pixels.Erase(SKColors.Red);
        for (int x = 4; x < 8; x++) for (int y = 0; y < 4; y++) pixels.SetPixel(x, y, SKColors.Blue);
        var frame = WindowFrameLease.FromBitmapCopy(new(document.ResourceId, new(null, image.Id)), image.WindowSource!.BindingIntent,
            new(Guid.NewGuid(), 1, 1), pixels, budget: budget);
        string before = JsonSerializer.Serialize(document); long contentVersion = session.ContentVersion;
        WindowSourceFallbackPlan prepared;
        using (var capture = new WindowDocumentPackageCapture(document, assets.Resources, (address, intent) => address.NodeId == image.Id ? frame.Retain() : null))
        {
            frame.Dispose();
            prepared = await Task.Run(() => capture.Prepare()).ConfigureAwait(false);
            check("package encoding owns retained frame pixels after caller publication release", capture.FrameCount == 1 && budget.RetainedBytes == 128);
        }
        check("package completion releases its retained raster budget", budget.RetainedBytes == 0);
        var local = Library(prepared.Resources); var store = new ProjectStore("0.4.0-m7-persistence-test");
        string path = Path.Combine(root, "saved.floatcanvas"); await store.SaveAsync(path, prepared.Document, local).ConfigureAwait(false);
        var loaded = await store.LoadAsync(path).ConfigureAwait(false); string fallback = loaded.Document.Elements[0].AssetId!;
        using (var bitmap = SKBitmap.Decode(loaded.Assets.Get(fallback).Data.ToArray()))
            check("package fallback is the complete uncropped unprocessed source frame", bitmap.Width == 8 && bitmap.Height == 4 && bitmap.GetPixel(1, 1) == SKColors.Red && bitmap.GetPixel(6, 1) == SKColors.Blue);
        check("package projection preserves crop mask effects fit and opacity as authored editable fields", loaded.Document.Elements[0].ImageCrop == image.ImageCrop &&
            loaded.Document.Elements[0].ImageFrame!.SequenceEqual(image.ImageFrame!) && loaded.Document.Elements[0].Effects.Count == 1 && loaded.Document.Elements[0].Style.Opacity == .37 &&
            loaded.Document.Elements[0].WindowSource!.Fit == WindowSourceFit.Contain);
        check("save pixels never publish to active document assets version undo or history", JsonSerializer.Serialize(document) == before && assets.Count == 2 &&
            session.ContentVersion == contentVersion && session.UndoCount == 0 && !session.HasPendingChanges && !assets.TryGet(fallback, out _));
        check("App document savepoint ignores fallback pixels using the original JSON hash algorithm", Fingerprint(document) == Fingerprint(loaded.Document));
        var plain = document.Clone(); plain.Elements[0].WindowSource = null;
        check("ordinary App document savepoint keeps its old exact serialized-byte fingerprint", Fingerprint(plain) == Sha(plain));
        var plainChanged = plain.Clone(); plainChanged.Elements[0].AssetId = draftOnly;
        check("ordinary image asset changes remain authored in the App savepoint", Fingerprint(plain) != Fingerprint(plainChanged));
        var explicitRebind = loaded.Document.Clone(); explicitRebind.Elements[0].WindowSource!.BindingIntent = Guid.NewGuid();
        check("explicit same-hints window rebind remains an App authored change", Fingerprint(loaded.Document) != Fingerprint(explicitRebind));

        // Each draft has the same resource and node ID; only explicit original-document mapping selects its pixels.
        var workspace = new WorkspaceState { LiveDocument = document.Clone(), CaptureDocument = document.Clone(),
            PendingPrefabEdit = Draft(document.Clone(), Draft(document.Clone())) };
        workspace.CaptureDocument.Kind = DocumentKind.FrozenCapture;
        var docs = WindowSourcePersistence.Documents(workspace).ToArray(); var colors = new[] { SKColors.Red, SKColors.Blue, SKColors.Yellow, SKColors.Fuchsia };
        var frames = new Dictionary<SceneDocument, WindowFrameLease>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < docs.Length; i++)
        {
            using var p = new SKBitmap(4, 4); p.Erase(colors[i]);
            frames.Add(docs[i], WindowFrameLease.FromBitmapCopy(new(docs[i].ResourceId, new(null, image.Id)), image.WindowSource!.BindingIntent, new(Guid.NewGuid(), 1, i), p));
        }
        docs[^1].Elements.Add(new() { Kind = ElementKind.Image, AssetId = draftOnly, Width = 3, Height = 3 });
        string workspaceBefore = JsonSerializer.Serialize(workspace); WindowWorkspacePackagePlan recovered;
        using (var capture = new WindowWorkspacePackageCapture(workspace, assets.Resources, (d, address, intent) => address.NodeId == image.Id ? frames[d].Retain() : null))
            recovered = await Task.Run(() => capture.Prepare()).ConfigureAwait(false);
        foreach (var f in frames.Values) f.Dispose();
        var recovery = new RecoveryStore(Path.Combine(root, "recovery")); await recovery.SaveAsync(recovered.State, Library(recovered.Resources)).ConfigureAwait(false);
        var recoveredLoad = (await recovery.LoadAsync().ConfigureAwait(false))!; var recoveredDocs = WindowSourcePersistence.Documents(recoveredLoad.State).ToArray();
        for (int i = 0; i < docs.Length; i++)
        {
            using var decoded = SKBitmap.Decode(recoveredLoad.Assets.Get(recoveredDocs[i].Elements[0].AssetId!).Data.ToArray());
            check("workspace uses explicit owner mapping for same-ID live/capture/nested draft " + i, decoded.GetPixel(0, 0) == colors[i]);
        }
        check("workspace and parent draft-only assets survive package closure", recoveredLoad.Assets.TryGet(draftOnly, out _) && recoveredDocs[^1].Elements[1].AssetId == draftOnly && JsonSerializer.Serialize(workspace) == workspaceBefore);
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        typeof(DesktopSession).GetField("_assets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(desktop, assets);
        var recoveryResources = (AssetResource[])typeof(DesktopSession).GetMethod("RecoveryResourceSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(desktop, [workspace])!;
        check("actual Desktop recovery resource snapshot includes all parent draft-only assets", recoveryResources.Select(r => r.Id).ToHashSet().SetEquals([oldImage, draftOnly]));
        check("App workspace fingerprint normalizes every pending ParentEdit fallback", WorkspaceFingerprint(workspace) == WorkspaceFingerprint(recoveredLoad.State));
        var withoutSources = workspace.Clone(); foreach (var d in WindowSourcePersistence.Documents(withoutSources)) foreach (var e in d.Elements) e.WindowSource = null;
        check("ordinary workspace fingerprint retains its exact pre-M7 serialized bytes", WorkspaceFingerprint(withoutSources) == Sha(withoutSources));
        var history = new WorkspaceRecoveryHistory(recovery); history.BeginFreshWorkspace(workspace);
        check("media-only fallback updates do not fork author recovery history", !history.ShouldWrite(recoveredLoad.State));
        var changed = recoveredLoad.State.Clone(); changed.PendingPrefabEdit!.ParentEdit!.DraftDocument.Elements[0].WindowSource!.BindingIntent = Guid.NewGuid();
        check("a parent draft source rebind still advances recovery author content", history.ShouldWrite(changed));

        var definition = new PrefabDefinition { Graph = new() { Elements = [image.Clone()] } };
        var sourceSnapshot = document.Clone(); sourceSnapshot.Definitions = [definition];
        Guid savedRevision = Guid.NewGuid(); var mediaOnly = WindowSourcePersistence.CreateSourcePrefabPackage(sourceSnapshot, definition.Id, false, savedRevision);
        check("media-only source-file save keeps the latest successfully saved author revision", mediaOnly.Definitions.Single().Revision == savedRevision && mediaOnly.ResourceId == document.ResourceId);
        var authored = WindowSourcePersistence.CreateSourcePrefabPackage(sourceSnapshot, definition.Id, true, savedRevision);
        check("source author edits get a new prefab revision while leaving active definitions untouched", authored.Definitions[0].Revision != savedRevision && sourceSnapshot.Definitions[0].Revision == definition.Revision);
        var threeRoots = sourceSnapshot.Clone();
        var auxiliaryDefinition = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Ellipse, Width = 20, Height = 20 }] } };
        var auxiliary = new PrefabInstance { DefinitionId = auxiliaryDefinition.Id };
        new SceneGraph(threeRoots).InsertInstance(auxiliary, rootCanvas: RootCanvasKind.Foreground);
        threeRoots.Definitions.Add(auxiliaryDefinition);
        threeRoots.Canvas.BackgroundColor = 0xFF126644; threeRoots.Background = new() { Kind = ElementKind.Image, AssetId = oldImage, Width = 50, Height = 20 };
        var mainOnly = WindowSourcePersistence.CreateSourcePrefabPackage(threeRoots, definition.Id, true, savedRevision);
        check("saving prefab source keeps Main graph only and drops auxiliary-only definition dependencies", mainOnly.Definitions.Count == 1 &&
            mainOnly.Definitions.Single().Graph.Instances.Count == 0 && mainOnly.Definitions.Single().Graph.Elements.Count == 1);
        check("prefab source canvas properties remain preview-only and the complete source snapshot stays intact", mainOnly.Definitions.Single().PreviewCanvas?.Canvas.BackgroundColor == 0xFF126644 &&
            mainOnly.Definitions.Single().PreviewCanvas?.Background?.AssetId == oldImage && threeRoots.Instances.Count == 1 && threeRoots.RootCanvasAssignments.Count == 1);
        var sourcePackage = WindowSourcePersistence.CreateSourcePrefabPackage(WithDefinitions(prepared.Document, definition), definition.Id, false, savedRevision);
        var prefabStore = new PrefabStore("0.4.0-m7-persistence-test"); string prefabPath = Path.Combine(root, "saved.floatprefab");
        await prefabStore.SaveAsync(prefabPath, sourcePackage, local).ConfigureAwait(false);
        check("source-prefab root graph packages its complete latest fallback without flattening editable content", (await prefabStore.LoadAsync(prefabPath).ConfigureAwait(false)).Package.Definitions[0].Graph.Elements[0].AssetId == fallback);

        var copyOwner = new EditorSession(document.Clone(), assets); copyOwner.SetSelection([image.Id]); var selected = ObjectClipboardCommand.CreateSnapshot(copyOwner);
        var copied = prepared.Document.Clone(); copied.Viewport = selected.Viewport; copied.LayerOpacity = selected.LayerOpacity;
        Guid transfer = ObjectClipboardCommand.RegisterTransfer(copyOwner, copied);
        var proof = typeof(ObjectClipboardCommand).GetMethod("SameOwner", BindingFlags.Static | BindingFlags.NonPublic)!;
        var transferred = JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(copied))!;
        var tampered = transferred.Clone(); tampered.Elements[0].AssetId = oldImage;
        check("object-copy ownership proof stays strict over the final projected package bytes", (bool)proof.Invoke(null, [copyOwner, transferred, transfer])! && !(bool)proof.Invoke(null, [copyOwner, tampered, transfer])!);
        string clipboardWire = JsonSerializer.Serialize(new { Document = copied }); WindowSourcePersistence.ValidateClipboardSources(clipboardWire);
        foreach (string field in new[] { "BindingIntent", "TitleHint", "ProcessNameHint", "WindowClassHint", "Fit" })
        {
            var invalid = JsonNode.Parse(clipboardWire)!.AsObject(); invalid["Document"]!["Elements"]![0]!["WindowSource"]!.AsObject().Remove(field);
            Reject(() => WindowSourcePersistence.ValidateClipboardSources(invalid.ToJsonString()), check, "clipboard canonical source requires authored field " + field);
        }
        var legacyClipboard = JsonNode.Parse(clipboardWire)!.AsObject(); legacyClipboard["Document"]!["Version"] = 13; legacyClipboard["Document"]!["Elements"]![0]!["WindowSource"] = null;
        Reject(() => WindowSourcePersistence.ValidateClipboardSources(legacyClipboard.ToJsonString()), check, "legacy clipboard cannot silently discard an explicit null future source field");
        var runtimeClipboard = JsonNode.Parse(clipboardWire)!.AsObject(); runtimeClipboard["Document"]!["Elements"]![0]!["WindowSource"]!["Hwnd"] = 123;
        Reject(() => WindowSourcePersistence.ValidateClipboardSources(runtimeClipboard.ToJsonString()), check, "clipboard source wire rejects runtime handle injection rather than ignoring it");
        Reject(() => WindowSourcePersistence.ValidateClipboardSources(clipboardWire.Replace("\"WindowSource\":", "\"WindowSource\":null,\"WindowSource\":")), check, "clipboard source wire rejects a duplicate source authority");
        ObjectClipboardCommand.Insert(copyOwner, JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(copied))!, local, new(-100, -100, 500, 500), transfer);
        check("clipboard proof is registered after fallback projection and its package contains its own PNG", copyOwner.UndoCount == 1 && copyOwner.Document.Elements.Count == 2 && copyOwner.Assets.TryGet(fallback, out _));

        var clock = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc); var media = new WindowMediaCheckpointState();
        media.Record(session, clock); long captured = media.Generation;
        check("automatic media checkpoint waits30seconds without changing author dirty state", !media.IsDue(clock.AddSeconds(29)) && media.IsDue(clock.AddSeconds(30)) && session.ContentVersion == contentVersion);
        media.Record(session, clock.AddSeconds(31)); media.Saved(captured, clock.AddSeconds(30));
        check("frames arriving during save remain pending with a new30second checkpoint floor", media.Pending && !media.IsDue(clock.AddSeconds(59)) && media.IsDue(clock.AddSeconds(60)));
        var anotherOwner = new EditorSession(document.Clone()); media.Record(anotherOwner, clock.AddSeconds(31));
        check("media generations remain tied to explicit EditorSession ownership", media.ForOwner(anotherOwner) > media.ForOwner(session));
        media.Saved(media.Generation, clock.AddSeconds(60)); check("successful checkpoint clears only the captured generation", !media.Pending);
        Console.WriteLine("Window persistence artifacts: " + root);
    }
    private static SceneDocument WithDefinitions(SceneDocument document, PrefabDefinition definition) { var copy = document.Clone(); copy.Definitions = [definition.Clone()]; return copy; }
    private static PrefabDraftState Draft(SceneDocument document, PrefabDraftState? parent = null) => new() { OwnerResourceId = document.ResourceId, DefinitionId = Guid.NewGuid(), ExpectedRevision = Guid.NewGuid(), DraftDocument = document, ParentEdit = parent };
    private static CanvasElement Image(string asset) => new() { Kind = ElementKind.Image, AssetId = asset, Width = 100, Height = 200, WindowSource = new() { BindingIntent = Guid.NewGuid() } };
    private static byte[] Png(SKColor color) { using var bitmap = new SKBitmap(2, 2); bitmap.Erase(color); using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray(); }
    private static AssetLibrary Library(IEnumerable<AssetResource> resources) { var assets = new AssetLibrary(); foreach (var resource in resources) assets.Add(resource); return assets; }
    private static string Sha<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string Fingerprint(SceneDocument document) => (string)typeof(DesktopSession).GetMethod("Fingerprint", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [document])!;
    private static string WorkspaceFingerprint(WorkspaceState workspace) => (string)typeof(DesktopSession).GetMethod("WorkspaceFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [workspace])!;
    private static void Reject(Action action, Action<string, bool> check, string name) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } check(name, rejected); }
}
