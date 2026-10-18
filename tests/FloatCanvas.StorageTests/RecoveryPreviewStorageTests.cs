using System.Buffers.Binary;
using System.IO.Compression;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class RecoveryPreviewStorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "recovery-preview-tests", Guid.NewGuid().ToString("N"));
        var store = new RecoveryStore(root);
        var assets = new AssetLibrary();
        var state = new WorkspaceState { LiveDocument = new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 50, Height = 40 }] } };
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
        check(await store.LoadPreviewAsync() is null, "missing history has no preview");
        await store.SaveAsync(state, assets);
        check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.State.LiveDocument.Elements.Count == 1, "legacy recovery without preview remains readable");

        state.LiveDocument.NextNumber = 2;
        byte[] mutablePreview = png.ToArray();
        Task saving = store.SaveWithPreviewAsync(state, assets, mutablePreview);
        mutablePreview[0] = 0;
        await saving;
        check((await store.LoadPreviewAsync())!.SequenceEqual(png), "save captures preview bytes before asynchronous work");
        check((await store.LoadAsync())!.State.LiveDocument.NextNumber == 2, "preview and editable document restore from the same generation");
        check(await store.LoadPreviewAsync(fromBackup: true) is null, "previous generation keeps its original missing preview");
        using (var archive = ZipFile.OpenRead(store.CurrentPath))
            check(archive.Entries.Count == 3 && archive.GetEntry("preview.png") is not null && archive.GetEntry("manifest.json") is not null && archive.GetEntry("workspace.json") is not null,
                "thumbnail is embedded in the atomic recovery package outside scene assets");

        byte[] packageWithPreview = await File.ReadAllBytesAsync(store.CurrentPath);
        state.LiveDocument.NextNumber = 3;
        await store.SaveWithPreviewAsync(state, assets, png);
        check((await store.LoadPreviewAsync(fromBackup: true))!.SequenceEqual(png), "rotation preserves previous generation preview");
        check((await File.ReadAllBytesAsync(store.BackupPath)).SequenceEqual(packageWithPreview), "backup carries package and preview byte exactly");
        string historyDirectory = Path.Combine(root, "history", "copied-generation");
        Directory.CreateDirectory(historyDirectory);
        File.Copy(store.CurrentPath, Path.Combine(historyDirectory, "workspace.floatcanvas"));
        var history = new RecoveryStore(historyDirectory);
        check((await history.LoadPreviewAsync())!.SequenceEqual(png) && (await history.LoadAsync())!.State.LiveDocument.NextNumber == 3,
            "history package copy carries its own matching preview without sidecar paths");

        await RewriteEntryAsync(store.CurrentPath, "preview.png", [1, 2, 3]);
        check(await store.LoadPreviewAsync() is null, "non-PNG preview is unavailable");
        check((await store.LoadAsync())!.State.LiveDocument.NextNumber == 3, "damaged preview does not invalidate editable recovery");
        state.LiveDocument.NextNumber = 4;
        await store.SaveWithPreviewAsync(state, assets, png);
        check((await new RecoveryStore(root).LoadAsync())!.State.LiveDocument.NextNumber == 4 && await store.LoadPreviewAsync(fromBackup: true) is null,
            "valid workspace with damaged optional preview remains eligible for backup rotation");

        byte[] corruptCrc = png.ToArray(); corruptCrc[45] ^= 1;
        await RewriteEntryAsync(store.CurrentPath, "preview.png", corruptCrc);
        check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.State.LiveDocument.NextNumber == 4, "bad PNG chunk CRC is ignored without losing workspace");
        await RewriteEntryAsync(store.CurrentPath, "preview.png", png[..^12]);
        check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.State.LiveDocument.NextNumber == 4, "incomplete PNG is ignored without losing workspace");
        await RewriteEntryAsync(store.CurrentPath, "preview.png", new byte[2 * 1024 * 1024 + 1]);
        check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.State.LiveDocument.NextNumber == 4, "oversized optional preview is not decoded or needed to restore");
        byte[] overDimension = png.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(overDimension.AsSpan(16, 4), 1025);
        BinaryPrimitives.WriteUInt32BigEndian(overDimension.AsSpan(29, 4), Crc32(overDimension.AsSpan(12, 17)));
        await RewriteEntryAsync(store.CurrentPath, "preview.png", overDimension);
        check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.State.LiveDocument.NextNumber == 4, "oversized PNG dimensions are rejected before UI decoding");

        foreach (byte[]? invalid in new byte[]?[] { null, [1, 2, 3], corruptCrc, overDimension, new byte[2 * 1024 * 1024 + 1] })
        {
            state.LiveDocument.NextNumber++;
            await store.SaveWithPreviewAsync(state, assets, invalid);
            check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.State.LiveDocument.NextNumber == state.LiveDocument.NextNumber,
                "invalid or absent display preview never blocks valid recovery save");
        }

        await store.SaveWithPreviewAsync(state, assets, png);
        string assetId = assets.Add(png);
        state.LiveDocument.NextNumber = 99;
        state.LiveDocument.Elements.Add(new() { Kind = ElementKind.Image, AssetId = assetId, Width = 20, Height = 20 });
        state.CaptureDocument = state.LiveDocument.CreateCapture(new(-100, 20, 200, 100), assetId);
        state.CaptureOrigin = new(-100, 20);
        await store.SaveWithPreviewAsync(state, assets, png);
        var fullWorkspace = (await store.LoadAsync())!;
        check(fullWorkspace.Assets.Count == 1 && fullWorkspace.State.CaptureDocument is not null && fullWorkspace.State.CaptureOrigin == state.CaptureOrigin &&
            fullWorkspace.Assets.Get(assetId).Data.Span.SequenceEqual(png), "preview coexists with self-contained live/capture image resources");
        byte[] completePackage = await File.ReadAllBytesAsync(store.CurrentPath);
        await RewriteEntryAsync(store.CurrentPath, "workspace.json", [1, 2, 3]);
        check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.FromBackup && (await store.LoadPreviewAsync(fromBackup: true))!.SequenceEqual(png),
            "valid thumbnail on an invalid current payload is not confused with the recovered previous generation");
        await File.WriteAllBytesAsync(store.CurrentPath, completePackage);
        await RewriteEntryAsync(store.CurrentPath, "assets/" + assetId, [1, 2, 3]);
        check(await store.LoadPreviewAsync() is null && (await store.LoadAsync())!.FromBackup, "valid thumbnail on corrupt current image resources is not presented as restorable");
        await File.WriteAllBytesAsync(store.CurrentPath, completePackage);
        byte[] beforeCancellation = await File.ReadAllBytesAsync(store.CurrentPath);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool canceledSave = false, canceledRead = false;
            try { await store.SaveWithPreviewAsync(state, assets, png, cancellation.Token); } catch (OperationCanceledException) { canceledSave = true; }
            try { await store.LoadPreviewAsync(ct: cancellation.Token); } catch (OperationCanceledException) { canceledRead = true; }
            check(canceledSave && canceledRead && beforeCancellation.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.CurrentPath)), "cancellation preserves atomic package including preview and is not swallowed");
        }

        await RewriteEntryAsync(store.CurrentPath, "other.png", png);
        bool unknownRejected = false;
        try { await new RecoveryStore(root).LoadAsync(); } catch (InvalidDataException) { unknownRejected = true; }
        // LoadAsync correctly falls back to the previous generation; inspect a copy with no backup.
        File.Copy(store.CurrentPath, Path.Combine(historyDirectory, "workspace.floatcanvas"), true);
        try { await history.LoadAsync(); } catch (InvalidDataException) { unknownRejected = true; }
        check(unknownRejected, "optional preview does not permit unrelated extra ZIP entries");

        string projectPath = Path.Combine(root, "project.floatcanvas");
        var projectStore = new ProjectStore();
        await projectStore.SaveAsync(projectPath, state.LiveDocument, assets);
        await RewriteEntryAsync(projectPath, "preview.png", png);
        bool projectRejected = false;
        try { await projectStore.LoadAsync(projectPath); } catch (InvalidDataException) { projectRejected = true; }
        check(projectRejected, "project package contract stays strict and unchanged");
        check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "preview saves leave no temporary package artifacts");
        Console.WriteLine($"Recovery preview artifacts: {root}");
    }

    private static async Task RewriteEntryAsync(string path, string entryName, byte[] data)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await stream.WriteAsync(data);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }
}
