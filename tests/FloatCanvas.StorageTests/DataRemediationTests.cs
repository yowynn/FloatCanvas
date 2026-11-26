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
        string newPath = Path.Combine(root, "new-project.floatcanvas");
        var missingVersion = await ProjectFileVersion.ReadAsync(newPath);
        var newVersion = await store.SaveVersionedAsync(newPath, first, assets, expectedVersion: missingVersion);
        check(File.Exists(newPath) && newVersion == await ProjectFileVersion.ReadAsync(newPath) &&
            (await store.LoadAsync(newPath)).Document.Elements.Count == 1,
            "versioned save creates a new project when the expected version is missing");
        await store.SaveAsync(path, first, assets); var opened = await store.LoadAsync(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var currentVersion = await store.SaveVersionedAsync(path, first, assets, expectedVersion: opened.FileVersion);
        check(currentVersion == await ProjectFileVersion.ReadAsync(path), "timestamp-only change is not a content conflict");
        var external = first.Clone(); external.NextNumber = 42; await store.SaveAsync(path, external, assets);
        byte[] externalBytes = await File.ReadAllBytesAsync(path); rejected = false;
        try { await store.SaveVersionedAsync(path, first, assets, expectedVersion: currentVersion); } catch (ProjectFileConflictException) { rejected = true; }
        byte[] afterConflict = await File.ReadAllBytesAsync(path);
        check(rejected && externalBytes.SequenceEqual(afterConflict), "external replacement is preserved byte-exact on conflict");
        string concurrentPath = Path.Combine(root, "concurrent-save.floatcanvas");
        await store.SaveAsync(concurrentPath, first, assets);
        var concurrentVersion = (await store.LoadAsync(concurrentPath)).FileVersion!;
        var writerA = first.Clone(); writerA.NextNumber = 101;
        var writerB = first.Clone(); writerB.NextNumber = 202;
        var concurrentWriters = new[] { new ProjectStore(), new ProjectStore() };
        async Task<int> SaveConcurrent(ProjectStore writer, SceneDocument document)
        {
            try { await writer.SaveVersionedAsync(concurrentPath, document, assets, expectedVersion: concurrentVersion); return 1; }
            catch (ProjectFileConflictException) { return 0; }
        }
        int[] concurrentResults = await Task.WhenAll(SaveConcurrent(concurrentWriters[0], writerA), SaveConcurrent(concurrentWriters[1], writerB));
        int concurrentWinner = (await store.LoadAsync(concurrentPath)).Document.NextNumber;
        check(concurrentResults.Sum() == 1 && concurrentWinner is 101 or 202 &&
            !Directory.EnumerateFiles(root, "concurrent-save.floatcanvas.*.save-*").Any(),
            "two stores saving one path serialize their compare-and-swap and keep one coherent winner");
        string racedPath = Path.Combine(root, "raced-conflict.floatcanvas");
        await store.SaveAsync(racedPath, first, assets); var racedVersion = (await store.LoadAsync(racedPath)).FileVersion!;
        string externalPath = Path.Combine(root, "raced-external.floatcanvas");
        await store.SaveAsync(externalPath, external, assets); byte[] racedExternal = await File.ReadAllBytesAsync(externalPath);
        var packageCodec = typeof(ProjectStore).Assembly.GetType("FloatCanvas.Storage.PackageCodec")!;
        var replaceBarrier = packageCodec.GetProperty("BeforeVersionedReplaceForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        string createdDuringSave = Path.Combine(root, "created-during-save.floatcanvas");
        byte[] createdExternal = await File.ReadAllBytesAsync(path);
        replaceBarrier.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, createdExternal)));
        rejected = false;
        try { await store.SaveVersionedAsync(createdDuringSave, first, assets, expectedVersion: await ProjectFileVersion.ReadAsync(createdDuringSave)); }
        catch (ProjectFileConflictException) { rejected = true; }
        finally { replaceBarrier.SetValue(null, null); }
        byte[] createdAfter = await File.ReadAllBytesAsync(createdDuringSave);
        check(rejected && createdExternal.SequenceEqual(createdAfter),
            "a file created after missing-version validation is preserved as an external conflict");
        replaceBarrier.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, racedExternal)));
        rejected = false;
        try { await store.SaveVersionedAsync(racedPath, first, assets, expectedVersion: racedVersion); }
        catch (ProjectFileConflictException) { rejected = true; }
        finally { replaceBarrier.SetValue(null, null); }
        byte[] racedAfter = await File.ReadAllBytesAsync(racedPath);
        check(rejected && racedExternal.SequenceEqual(racedAfter),
            "external write racing the final replacement is rolled back byte-exact");
        check(!Directory.EnumerateFiles(root, "raced-conflict.floatcanvas.*.save-*").Any(),
            "racing replacement leaves no abandoned backup or rejected save file");
        string doubleRacePath = Path.Combine(root, "double-race.floatcanvas");
        await store.SaveAsync(doubleRacePath, first, assets); var doubleRaceVersion = (await store.LoadAsync(doubleRacePath)).FileVersion!;
        var firstExternal = first.Clone(); firstExternal.NextNumber = 51;
        var laterExternal = first.Clone(); laterExternal.NextNumber = 77;
        string firstExternalPath = Path.Combine(root, "first-external.floatcanvas");
        string laterExternalPath = Path.Combine(root, "later-external.floatcanvas");
        await store.SaveAsync(firstExternalPath, firstExternal, assets);
        await store.SaveAsync(laterExternalPath, laterExternal, assets);
        byte[] firstExternalBytes = await File.ReadAllBytesAsync(firstExternalPath);
        byte[] laterExternalBytes = await File.ReadAllBytesAsync(laterExternalPath);
        var beforeReplace = packageCodec.GetProperty("BeforeVersionedReplaceForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        var afterReplaceRace = packageCodec.GetProperty("AfterVersionedReplaceForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        beforeReplace.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, firstExternalBytes)));
        afterReplaceRace.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, laterExternalBytes)));
        rejected = false;
        try { await store.SaveVersionedAsync(doubleRacePath, first, assets, expectedVersion: doubleRaceVersion); }
        catch (ProjectFileConflictException) { rejected = true; }
        finally
        {
            beforeReplace.SetValue(null, null);
            afterReplaceRace.SetValue(null, null);
        }
        byte[] doubleRaceAfter = await File.ReadAllBytesAsync(doubleRacePath);
        check(rejected && laterExternalBytes.SequenceEqual(doubleRaceAfter),
            "a second external write after replacement remains the latest target version");
        check(!Directory.EnumerateFiles(root, "double-race.floatcanvas.*.save-*").Any(),
            "double-write conflict removes only stale internal generations");
        string partialRacePath = Path.Combine(root, "partial-external.floatcanvas");
        await store.SaveAsync(partialRacePath, first, assets);
        byte[] partialOriginal = await File.ReadAllBytesAsync(partialRacePath);
        var partialRaceVersion = (await store.LoadAsync(partialRacePath)).FileVersion!;
        afterReplaceRace.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, [1, 2, 3])));
        ProjectFileConflictException? partialRace = null;
        try { await store.SaveVersionedAsync(partialRacePath, first, assets, expectedVersion: partialRaceVersion); }
        catch (ProjectFileConflictException error) { partialRace = error; }
        finally { afterReplaceRace.SetValue(null, null); }
        byte[] partialAfter = await File.ReadAllBytesAsync(partialRacePath);
        byte[] partialPreservedBytes = partialRace?.PreservedPath is { } partialPreserved && File.Exists(partialPreserved)
            ? await File.ReadAllBytesAsync(partialPreserved) : [];
        check(partialPreservedBytes.Length > 0 && partialOriginal.SequenceEqual(partialPreservedBytes) &&
            partialAfter.SequenceEqual(new byte[] { 1, 2, 3 }),
            "a partial external write preserves the displaced valid generation");
        if (partialRace?.PreservedPath is { } partialPreservedPath) File.Delete(partialPreservedPath);
        string rollbackRacePath = Path.Combine(root, "rollback-race.floatcanvas");
        await store.SaveAsync(rollbackRacePath, first, assets); var rollbackRaceVersion = (await store.LoadAsync(rollbackRacePath)).FileVersion!;
        var beforeRollback = packageCodec.GetProperty("BeforeVersionedRollbackForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        beforeReplace.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, firstExternalBytes)));
        afterReplaceRace.SetValue(null, null);
        beforeRollback.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, laterExternalBytes)));
        rejected = false;
        try { await store.SaveVersionedAsync(rollbackRacePath, first, assets, expectedVersion: rollbackRaceVersion); }
        catch (ProjectFileConflictException) { rejected = true; }
        finally
        {
            beforeReplace.SetValue(null, null);
            afterReplaceRace.SetValue(null, null);
            beforeRollback.SetValue(null, null);
        }
        byte[] rollbackRaceAfter = await File.ReadAllBytesAsync(rollbackRacePath);
        check(rejected && laterExternalBytes.SequenceEqual(rollbackRaceAfter),
            "external write between conflict detection and rollback remains the target version");
        check(!Directory.EnumerateFiles(root, "rollback-race.floatcanvas.*.save-*").Any(),
            "bounded rollback removes only the known stale external generation");
        var postCommitRead = packageCodec.GetMethod("ReadPostCommitVersionAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var postCommitTask = (Task<ProjectFileVersion>)postCommitRead.Invoke(null, [root, path])!;
        ProjectFileConflictException? unreadableConflict = null;
        try { await postCommitTask; }
        catch (ProjectFileConflictException error) { unreadableConflict = error; }
        check(unreadableConflict?.PreservedPath == path,
            "post-commit hash read failure returns the actionable preserved path");
        string cancellationPath = Path.Combine(root, "commit-cancellation.floatcanvas");
        await store.SaveAsync(cancellationPath, first, assets); var cancellationVersion = (await store.LoadAsync(cancellationPath)).FileVersion!;
        var afterReplace = packageCodec.GetProperty("AfterVersionedReplaceForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var commitCancellation = new CancellationTokenSource();
        afterReplace.SetValue(null, new Action<string>(_ => commitCancellation.Cancel()));
        var committedDocument = first.Clone();
        committedDocument.Elements.Add(new() { Kind = ElementKind.Text, Text = "committed" });
        try
        {
            await store.SaveVersionedAsync(cancellationPath, committedDocument, assets,
                expectedVersion: cancellationVersion, ct: commitCancellation.Token);
        }
        finally { afterReplace.SetValue(null, null); }
        var committedReadback = await store.LoadAsync(cancellationPath);
        check(committedReadback.Document.Elements.Any(element => element.Text == "committed"),
            "cancellation after the atomic replace does not report a committed save as cancelled");
        File.Delete(path); rejected = false;
        try { await store.SaveVersionedAsync(path, first, assets, expectedVersion: currentVersion); } catch (ProjectFileConflictException) { rejected = true; }
        check(rejected && !File.Exists(path), "external deletion is a conflict and is not silently recreated");
        string disappearancePath = Path.Combine(root, "delete-before-replace.floatcanvas");
        await store.SaveAsync(disappearancePath, first, assets);
        var disappearanceVersion = (await store.LoadAsync(disappearancePath)).FileVersion!;
        beforeReplace.SetValue(null, new Action<string>(File.Delete));
        rejected = false;
        try { await store.SaveVersionedAsync(disappearancePath, first, assets, expectedVersion: disappearanceVersion); }
        catch (ProjectFileConflictException) { rejected = true; }
        finally { beforeReplace.SetValue(null, null); }
        check(rejected && !File.Exists(disappearancePath),
            "deletion between version validation and replacement is reported as a conflict");
        string cacheRacePath = Path.Combine(root, "delete-before-cache-read.floatcanvas");
        await store.SaveAsync(cacheRacePath, first, assets);
        var cacheRaceVersion = (await store.LoadAsync(cacheRacePath)).FileVersion!;
        var beforeCacheRead = packageCodec.GetProperty("BeforeCommittedCacheReadForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        beforeCacheRead.SetValue(null, new Action<string>(File.Delete));
        ProjectFileConflictException? cacheRace = null;
        try { await store.SaveVersionedAsync(cacheRacePath, first, assets, expectedVersion: cacheRaceVersion); }
        catch (ProjectFileConflictException error) { cacheRace = error; }
        finally { beforeCacheRead.SetValue(null, null); }
        check(cacheRace is not null && !File.Exists(cacheRacePath) && cacheRace.PreservedPath is { } preserved &&
            File.Exists(preserved), "post-commit cache failure preserves the replaced generation and reports a conflict");
        if (cacheRace?.PreservedPath is { } preservedPath) File.Delete(preservedPath);
        string cacheReplacementPath = Path.Combine(root, "replace-before-cache-read.floatcanvas");
        await store.SaveAsync(cacheReplacementPath, first, assets);
        var cacheReplacementVersion = (await store.LoadAsync(cacheReplacementPath)).FileVersion!;
        beforeCacheRead.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, externalBytes)));
        ProjectFileConflictException? cacheReplacement = null;
        try { await store.SaveVersionedAsync(cacheReplacementPath, first, assets, expectedVersion: cacheReplacementVersion); }
        catch (ProjectFileConflictException error) { cacheReplacement = error; }
        finally { beforeCacheRead.SetValue(null, null); }
        byte[] cacheReplacementAfter = await File.ReadAllBytesAsync(cacheReplacementPath);
        check(cacheReplacement is not null && externalBytes.SequenceEqual(cacheReplacementAfter) &&
            cacheReplacement.PreservedPath is { } cachePreserved && File.Exists(cachePreserved),
            "a valid external replacement before cache read is preserved and reported as a conflict");
        if (cacheReplacement?.PreservedPath is { } cachePreservedPath) File.Delete(cachePreservedPath);
        var preserveIntent = packageCodec.GetProperty("PreserveSaveIntentForTesting",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        string crashPath = Path.Combine(root, "post-replace-crash.floatcanvas");
        await store.SaveAsync(crashPath, first, assets);
        var crashVersion = (await store.LoadAsync(crashPath)).FileVersion!;
        var crashedDocument = first.Clone();
        crashedDocument.NextNumber = 314;
        preserveIntent.SetValue(null, true);
        afterReplaceRace.SetValue(null, new Action<string>(_ => throw new InvalidOperationException("simulated process termination")));
        try
        {
            await store.SaveVersionedAsync(crashPath, crashedDocument, assets, expectedVersion: crashVersion);
            throw new InvalidOperationException("simulated crash did not interrupt the save");
        }
        catch (InvalidOperationException error) when (error.Message == "simulated process termination") { }
        finally
        {
            preserveIntent.SetValue(null, false);
            afterReplaceRace.SetValue(null, null);
        }
        check(File.Exists(crashPath + ".save-intent.json") &&
            Directory.EnumerateFiles(root, "post-replace-crash.floatcanvas.*.save-backup").Any(),
            "a replace crash leaves a deterministic intent and the displaced generation");
        var recoveredCrash = await store.LoadAsync(crashPath);
        check(recoveredCrash.Document.NextNumber == 314 &&
            File.Exists(crashPath + ".save-intent.json") &&
            Directory.EnumerateFiles(root, "post-replace-crash.floatcanvas.*.save-backup").Any(),
            "opening a completed post-replace save reads the target and leaves the intent and backup intact");
        await store.SaveVersionedAsync(crashPath, recoveredCrash.Document, assets, expectedVersion: recoveredCrash.FileVersion);
        check(!File.Exists(crashPath + ".save-intent.json") && !Directory.EnumerateFiles(root, "post-replace-crash.floatcanvas.*.save-backup").Any(),
            "an explicit save clears the completed intent and its owned backup");
        string corruptCrashPath = Path.Combine(root, "corrupt-post-replace-crash.floatcanvas");
        await store.SaveAsync(corruptCrashPath, first, assets);
        var corruptCrashVersion = (await store.LoadAsync(corruptCrashPath)).FileVersion!;
        preserveIntent.SetValue(null, true);
        afterReplaceRace.SetValue(null, new Action<string>(_ => throw new InvalidOperationException("simulated process termination")));
        try
        {
            await store.SaveVersionedAsync(corruptCrashPath, crashedDocument, assets, expectedVersion: corruptCrashVersion);
            throw new InvalidOperationException("simulated crash did not interrupt the corrupt-target save");
        }
        catch (InvalidOperationException error) when (error.Message == "simulated process termination") { }
        finally
        {
            preserveIntent.SetValue(null, false);
            afterReplaceRace.SetValue(null, null);
        }
        await File.WriteAllBytesAsync(corruptCrashPath, [1, 2, 3]);
        bool rejectedCorruptRead = false;
        try { await store.LoadAsync(corruptCrashPath); }
        catch (InvalidDataException) { rejectedCorruptRead = true; }
        check(rejectedCorruptRead && (await File.ReadAllBytesAsync(corruptCrashPath)).SequenceEqual(new byte[] { 1, 2, 3 }) &&
            File.Exists(corruptCrashPath + ".save-intent.json"), "opening a corrupt target leaves the original bytes and intent untouched");
        ProjectFileConflictException? corruptCrashConflict = null;
        try { await store.SaveVersionedAsync(corruptCrashPath, first, assets, expectedVersion: corruptCrashVersion); }
        catch (ProjectFileConflictException error) { corruptCrashConflict = error; }
        var recoveredCorruptCrash = await store.LoadAsync(corruptCrashPath);
        check(corruptCrashConflict?.PreservedPath is { } corruptPath && File.Exists(corruptPath) &&
            recoveredCorruptCrash.Document.Elements.Count == first.Elements.Count &&
            !File.Exists(corruptCrashPath + ".save-intent.json"),
            "explicit saving reconciles a corrupt target and preserves the corrupt bytes before reporting a conflict");
        if (corruptCrashConflict?.PreservedPath is { } corruptPreserved) File.Delete(corruptPreserved);

        string externalCrashPath = Path.Combine(root, "external-post-replace-crash.floatcanvas");
        await store.SaveAsync(externalCrashPath, first, assets);
        var externalCrashVersion = (await store.LoadAsync(externalCrashPath)).FileVersion!;
        byte[] crashExternalBytes = await File.ReadAllBytesAsync(externalPath);
        preserveIntent.SetValue(null, true);
        beforeReplace.SetValue(null, new Action<string>(target => File.WriteAllBytes(target, crashExternalBytes)));
        afterReplaceRace.SetValue(null, new Action<string>(_ => throw new InvalidOperationException("simulated process termination")));
        try
        {
            await store.SaveVersionedAsync(externalCrashPath, crashedDocument, assets, expectedVersion: externalCrashVersion);
            throw new InvalidOperationException("simulated crash did not interrupt the external race");
        }
        catch (InvalidOperationException error) when (error.Message == "simulated process termination") { }
        finally
        {
            preserveIntent.SetValue(null, false);
            beforeReplace.SetValue(null, null);
            afterReplaceRace.SetValue(null, null);
        }
        ProjectFileConflictException? crashConflict = null;
        var visibleCrash = await store.LoadAsync(externalCrashPath);
        check(visibleCrash.Document.NextNumber == 314 && File.Exists(externalCrashPath + ".save-intent.json"),
            "opening a crash-displaced target reads the visible version without restoring external bytes");
        try { await store.SaveVersionedAsync(externalCrashPath, first, assets, expectedVersion: visibleCrash.FileVersion); }
        catch (ProjectFileConflictException error) { crashConflict = error; }
        byte[] restoredExternal = await File.ReadAllBytesAsync(externalCrashPath);
        byte[] preservedCandidate = crashConflict?.PreservedPath is { } preservedCrashPath && File.Exists(preservedCrashPath)
            ? await File.ReadAllBytesAsync(preservedCrashPath) : [];
        check(crashExternalBytes.SequenceEqual(restoredExternal) && preservedCandidate.Length > 0 &&
            !File.Exists(externalCrashPath + ".save-intent.json"),
            "an explicit save reconciles a crash-displaced external version and preserves the interrupted candidate");
        if (crashConflict?.PreservedPath is { } crashCandidatePath) File.Delete(crashCandidatePath);

        string supersededPath = Path.Combine(root, "superseded-intent.floatcanvas");
        await store.SaveAsync(supersededPath, first, assets);
        var beforeInterruption = await ProjectFileVersion.ReadAsync(supersededPath);
        preserveIntent.SetValue(null, true);
        afterReplaceRace.SetValue(null, new Action<string>(_ => throw new InvalidOperationException("simulated process termination")));
        try { await store.SaveVersionedAsync(supersededPath, crashedDocument, assets, expectedVersion: beforeInterruption); }
        catch (InvalidOperationException error) when (error.Message == "simulated process termination") { }
        finally { preserveIntent.SetValue(null, false); afterReplaceRace.SetValue(null, null); }
        await File.WriteAllBytesAsync(supersededPath, crashExternalBytes);
        var newer = await store.LoadAsync(supersededPath);
        check((await File.ReadAllBytesAsync(supersededPath)).SequenceEqual(crashExternalBytes) && File.Exists(supersededPath + ".save-intent.json"),
            "a valid superseding package is readable without touching the old intent or file");
        await store.SaveVersionedAsync(supersededPath, newer.Document, newer.Assets, expectedVersion: newer.FileVersion);
        check(!File.Exists(supersededPath + ".save-intent.json"), "saving a valid superseding package retires its stale intent");
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
        Task<T> Read<T>(string name, long maximum) => (Task<T>)read.MakeGenericMethod(typeof(T)).Invoke(null, [archive.GetEntry(name)!, maximum, CancellationToken.None, null, SceneDocument.CurrentVersion])!;
    }
}
