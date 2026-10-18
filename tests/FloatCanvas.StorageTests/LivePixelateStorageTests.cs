using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class LivePixelateStorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await LiveBlurStorageTests.RunAsync(check);
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FloatCanvas-live-effect-storage-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var effect = new CanvasElement { Kind = ElementKind.LivePixelate, Name = "Live mask", Width = 240, Height = 100,
                Transform = Affine2D.Translation(-300, -80).Compose(Affine2D.Rotation(23)).Compose(Affine2D.Scale(1.2, .7)),
                Style = new() { Opacity = .8, StrokeWidth = 0 }, Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 17, Bounds = new(0, 0, 1, 1) }] };
            var document = new SceneDocument { Viewport = new(-1920, -1080, 3840, 2160), NextNumber = 42, Elements = [effect] };
            var assets = new AssetLibrary(); var store = new ProjectStore(); string path = Path.Combine(root, "live.floatcanvas");
            await store.SaveAsync(path, document, assets);
            var loaded = await store.LoadAsync(path);
            check(JsonSerializer.Serialize(document) == JsonSerializer.Serialize(loaded.Document), "live effect project roundtrip preserves geometry, strength and document sequence");
            check(loaded.Assets.Count == 0 && !loaded.Document.ReferencedAssets.Any() && loaded.Document.Elements[0].AssetId is null,
                "reopening a live effect does not require a screenshot resource");
            using (var zip = ZipFile.OpenRead(path))
                check(!zip.Entries.Any(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)), "live effect archive contains no frozen desktop pixels");
            var session = new EditorSession(loaded.Document, loaded.Assets); session.SelectAll();
            string original = JsonSerializer.Serialize(session.Document);
            session.TransformSelection(Affine2D.Translation(40, 15));
            check(session.UndoCount == 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == original && session.Redo(),
                "reopened live effect transforms remain one reversible document operation");
            session.UpdateSelection(element => element.Effects[0].Amount = 31);
            check(session.Document.Elements[0].Effects[0].Amount == 31 && session.Undo() && session.Document.Elements[0].Effects[0].Amount == 17,
                "live effect strength edits retain their independent undo history");
            var copy = session.Document.Clone(true);
            check(copy.Elements.Single().Kind == ElementKind.LivePixelate && copy.Elements[0].Id != effect.Id && copy.Elements[0].Effects[0].Id != effect.Effects[0].Id && !copy.ReferencedAssets.Any(),
                "cloning a live region preserves editable effect data and remaps its identities");
            var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
            await recovery.SaveAsync(new WorkspaceState { LiveDocument = session.Document.Clone() }, session.Assets);
            var restored = await recovery.LoadAsync();
            check(restored is not null && JsonSerializer.Serialize(restored.State.LiveDocument) == JsonSerializer.Serialize(session.Document) && restored.Assets.Count == 0,
                "workspace recovery retains live regions without capturing desktop assets");
            var noEffect = document.Clone(); noEffect.Elements[0].Effects.Clear();
            check(Rejected(noEffect), "a live region without an effect contract is rejected before loading");
            var wrongEffect = document.Clone(); wrongEffect.Elements[0].Effects[0].Kind = ImageEffectKind.Blur;
            check(Rejected(wrongEffect), "a non-pixelation effect cannot masquerade as a live pixelation region");
            var nullEffect = document.Clone(); nullEffect.Elements[0].Effects[0] = null!;
            check(Rejected(nullEffect), "a null live effect is rejected as invalid data before any member is read");
            await recovery.SaveAsync(new WorkspaceState { LiveDocument = session.Document.Clone() }, session.Assets);
            using (var archive = ZipFile.Open(recovery.CurrentPath, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("workspace.json")!; JsonNode json;
                using (var stream = entry.Open()) json = JsonNode.Parse(stream)!;
                ((JsonArray)json["LiveDocument"]!["Elements"]![0]!["Effects"]!)[0] = null;
                entry.Delete(); var replacement = archive.CreateEntry("workspace.json");
                using var output = replacement.Open(); await JsonSerializer.SerializeAsync(output, json);
            }
            var fallback = await recovery.LoadAsync();
            check(fallback is { FromBackup: true } && fallback.State.LiveDocument.Elements[0].Kind == ElementKind.LivePixelate,
                "a null effect in damaged current recovery falls back to the valid previous generation");
        }
        finally
        {
            string temporary = Path.GetFullPath(Path.GetTempPath());
            if (!root.StartsWith(Path.Combine(temporary, "FloatCanvas-live-effect-storage-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected live-effect fixture path");
            Directory.Delete(root, recursive: true);
        }
    }
    private static bool Rejected(SceneDocument document)
    {
        try { SceneValidation.Validate(document); return false; }
        catch (InvalidDataException) { return true; }
    }
}
