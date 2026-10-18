using System.IO.Compression;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class LiveBlurStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-results", "live-blur-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blur = new CanvasElement { Kind = ElementKind.LiveBlur, Width = 100, Height = 70, Transform = Affine2D.Translation(-150, -60).Compose(Affine2D.Rotation(25)),
            Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 9, Bounds = new(0, 0, 1, 1) }], Style = new() { StrokeWidth = 0 } };
        var pixelate = blur.Clone(true); pixelate.Kind = ElementKind.LivePixelate; pixelate.Effects[0].Kind = ImageEffectKind.Pixelate;
        var scene = new SceneDocument { Viewport = new(-500, -300, 1000, 600), Elements = [pixelate, blur] };
        var assets = new AssetLibrary(); var store = new ProjectStore();
        check((int)ElementKind.LivePixelate == 13 && (int)ElementKind.LiveBlur == 14 && SceneDocument.CurrentVersion == 2,
            "LiveBlur is appended without changing legacy enum values or the existing scene version");
        await store.SaveAsync(Path.Combine(root, "mixed.floatcanvas"), scene, assets);
        var loaded = await store.LoadAsync(Path.Combine(root, "mixed.floatcanvas"));
        check(JsonSerializer.Serialize(loaded.Document) == JsonSerializer.Serialize(scene), "blur and old pixelation contracts roundtrip together exactly");
        check(loaded.Assets.Count == 0 && !loaded.Document.ReferencedAssets.Any(), "dynamic blur stores no captured bitmap resource");
        using (var zip = ZipFile.OpenRead(Path.Combine(root, "mixed.floatcanvas")))
            check(!zip.Entries.Any(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal)), "a blur archive contains geometry and strength without desktop pixels");
        var session = new EditorSession(loaded.Document, loaded.Assets); session.SetSelection([blur.Id]);
        string before = JsonSerializer.Serialize(session.Document); session.TransformSelection(Affine2D.Translation(30, 20));
        check(session.UndoCount == 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Redo(), "loaded blur movement is one reversible editable operation");
        var clone = session.Document.Clone(true);
        check(clone.Elements[1].Kind == ElementKind.LiveBlur && clone.Elements[1].Id != blur.Id && clone.Elements[1].Effects[0].Id != blur.Effects[0].Id,
            "blur cloning and editable stamp sources remap object and effect identities");
        var recovery = new RecoveryStore(Path.Combine(root, "recovery")); await recovery.SaveAsync(new() { LiveDocument = scene.Clone() }, assets);
        var restored = await recovery.LoadAsync();
        check(restored is not null && JsonSerializer.Serialize(restored.State.LiveDocument) == JsonSerializer.Serialize(scene) && restored.Assets.Count == 0,
            "recovery retains both dynamic filter kinds without frozen assets");
        var legacy = scene.Clone(); legacy.Elements.RemoveAt(1); legacy.Version = 1; SceneValidation.Validate(legacy);
        check(legacy.Elements[0].Kind == ElementKind.LivePixelate, "legacy version-one pixelation documents remain readable");
        foreach (var mismatch in new[] { ImageEffectKind.Pixelate, ImageEffectKind.Redact })
        { var invalid = scene.Clone(); invalid.Elements[1].Effects[0].Kind = mismatch; check(Rejected(invalid), "LiveBlur rejects a mismatched effect kind: " + mismatch); }
        var missing = scene.Clone(); missing.Elements[1].Effects.Clear(); check(Rejected(missing), "LiveBlur requires its blur effect contract");
        var nullEffect = scene.Clone(); nullEffect.Elements[1].Effects[0] = null!; check(Rejected(nullEffect), "null blur data is rejected before dereferencing");
    }
    private static bool Rejected(SceneDocument scene)
    { try { SceneValidation.Validate(scene); return false; } catch (InvalidDataException) { return true; } }
}
