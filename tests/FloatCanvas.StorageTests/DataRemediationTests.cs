using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.Reflection;
using System.IO.Compression;

internal static class DataRemediationTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await WorkspaceJsonCapacityAsync(check);
        var method = typeof(ProjectStore).Assembly.GetType("FloatCanvas.Storage.PackageCodec")!.GetMethod("EnsureExpandedCapacity", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (long size in new[] { 99L, 100L }) method.Invoke(null, [size - 30, 10L, 20L, 100L]);
        bool rejected = false; try { method.Invoke(null, [71L, 10L, 20L, 100L]); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
        check(rejected, "save expanded budget includes manifest and payload at +1 boundary");
        string root = Path.Combine(AppContext.BaseDirectory, "data-remediation", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "conflict.floatcanvas"); var store = new ProjectStore(); var assets = new AssetLibrary();
        var first = new SceneDocument { Elements = [new() { Width = 20, Height = 10 }] };
        await store.SaveAsync(path, first, assets); var opened = await store.LoadAsync(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var currentVersion = await store.SaveVersionedAsync(path, first, assets, expectedVersion: opened.FileVersion);
        check(currentVersion == await ProjectFileVersion.ReadAsync(path), "timestamp-only change is not a content conflict");
        var external = first.Clone(); external.NextNumber = 42; await store.SaveAsync(path, external, assets);
        byte[] externalBytes = await File.ReadAllBytesAsync(path); rejected = false;
        try { await store.SaveVersionedAsync(path, first, assets, expectedVersion: currentVersion); } catch (ProjectFileConflictException) { rejected = true; }
        byte[] afterConflict = await File.ReadAllBytesAsync(path);
        check(rejected && externalBytes.SequenceEqual(afterConflict), "external replacement is preserved byte-exact on conflict");
        File.Delete(path); rejected = false;
        try { await store.SaveVersionedAsync(path, first, assets, expectedVersion: currentVersion); } catch (ProjectFileConflictException) { rejected = true; }
        check(rejected && !File.Exists(path), "external deletion is a conflict and is not silently recreated");
        var immutable = new AssetLibrary(); byte[] original = [1, 2, 3]; string id = immutable.Add(original); var shared = new AssetLibrary(); shared.Add(immutable.Get(id)); original[0] = 9;
        check(ReferenceEquals(immutable.Get(id), shared.Get(id)) && shared.Get(id).Data.Span[0] == 1, "immutable resource snapshots share existing ownership without copying");
        var recoveryAssets = new AssetLibrary(); byte[] payload = new byte[1000]; new Random(123).NextBytes(payload);
        string recoveryId = recoveryAssets.Add(payload); var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        byte[] preview = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        var workspace = new WorkspaceState { LiveDocument = new() { Elements = [new() { Kind = ElementKind.Image, AssetId = recoveryId, Width = 1, Height = 1 }] } };
        await recovery.SaveWithPreviewAsync(workspace, recoveryAssets, preview);
        check(await recovery.LoadPreviewAsync() is not null, "verified newly saved recovery preview is available");
        DateTime originalWrite = File.GetLastWriteTimeUtc(recovery.CurrentPath); byte[] package = await File.ReadAllBytesAsync(recovery.CurrentPath);
        int offset = package.AsSpan().IndexOf(payload); check(offset >= 0, "test locates exact uncompressed recovery asset");
        package[offset] ^= 1; await File.WriteAllBytesAsync(recovery.CurrentPath, package); File.SetLastWriteTimeUtc(recovery.CurrentPath, originalWrite);
        check(await recovery.LoadPreviewAsync() is null, "validation cache rejects same-length tampering even with restored write timestamp");
    }

    private static async Task WorkspaceJsonCapacityAsync(Action<bool, string> check)
    {
        // Exercise the production serializer and bounded ZIP reader with a scaled scene
        // ceiling, rather than allocating two 70-MiB text strings in the default CI suite.
        var codec = typeof(ProjectStore).Assembly.GetType("FloatCanvas.Storage.PackageCodec")!;
        var serialize = codec.GetMethod("SerializeJson", BindingFlags.Static | BindingFlags.NonPublic)!;
        var capacity = codec.GetMethod("JsonCapacity", BindingFlags.Static | BindingFlags.NonPublic)!;
        var read = codec.GetMethod("ReadJsonAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var live = new SceneDocument { Elements = [new() { Kind = ElementKind.Text, Text = new string('x', 8192), Width = 100, Height = 50 }] };
        var capture = live.Clone(); capture.Kind = DocumentKind.FrozenCapture;
        var workspace = new WorkspaceState { LiveDocument = live, CaptureDocument = capture, CaptureOrigin = new(20, 30) };
        SceneValidation.Validate(workspace);
        byte[] sceneBytes = Write("scene.json", live, SceneLimits.MaximumSceneJsonBytes);
        long sceneLimit = sceneBytes.LongLength;
        byte[] workspaceBytes = Write("workspace.json", workspace, sceneLimit);
        long workspaceLimit = (long)capacity.Invoke(null, ["workspace.json", sceneLimit])!;
        check(workspaceBytes.LongLength > sceneLimit && workspaceBytes.LongLength <= workspaceLimit &&
            Write("scene.json", live, sceneLimit).SequenceEqual(sceneBytes), "two individually legal scenes serialize into the recovery envelope beyond one scene budget");
        bool rejected = false;
        try { Write("scene.json", live, sceneLimit - 1); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
        check(rejected, "project serialization retains its exact single-scene ceiling");
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = zip.CreateEntry("workspace.json").Open()) stream.Write(workspaceBytes);
            using (var stream = zip.CreateEntry("scene.json").Open()) stream.Write(sceneBytes);
        }
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read);
        var restored = await Read<WorkspaceState>("workspace.json", workspaceLimit);
        check(restored.LiveDocument.Elements.Single().Text == live.Elements.Single().Text && restored.CaptureDocument?.Elements.Single().Text == capture.Elements.Single().Text && restored.CaptureOrigin == workspace.CaptureOrigin,
            "bounded recovery JSON reader accepts the same two-scene payload produced by save");
        rejected = false;
        try { await Read<SceneDocument>("scene.json", sceneLimit - 1); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "project JSON reader still rejects a single-scene payload above its ceiling");
        rejected = false;
        try { await Read<WorkspaceState>("workspace.json", workspaceBytes.LongLength - 1); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "recovery JSON reader retains an enforced finite expanded-byte ceiling");

        byte[] Write<T>(string name, T value, long maximumScene) => (byte[])serialize.MakeGenericMethod(typeof(T)).Invoke(null, [name, value, maximumScene])!;
        Task<T> Read<T>(string name, long maximum) => (Task<T>)read.MakeGenericMethod(typeof(T)).Invoke(null, [archive.GetEntry(name)!, maximum, CancellationToken.None])!;
    }
}
