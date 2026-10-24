using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Rendering;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.IO;

internal static class DataRemediationTests
{
    internal static void Run(Action<string, bool> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "data-remediation", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string prefix = Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N");
        string output = Path.Combine(root, prefix + ".floatcanvas"), thumbnail = Path.Combine(root, prefix + ".png");
        string unknown = Path.Combine(root, "user-notes.txt"); File.WriteAllText(unknown, "keep");
        string journal = PublicationCleanup.Begin(root, [output, thumbnail]); File.WriteAllText(output, "partial");
        PublicationCleanup.Finish(journal, false);
        check("failed stamp publication removes only its owned revision", !File.Exists(output) && File.Exists(unknown) && !File.Exists(journal));
        journal = PublicationCleanup.Begin(root, [output, thumbnail]); File.WriteAllText(output, "published");
        PublicationCleanup.Sweep(root, [output], DateTime.UtcNow.AddDays(2));
        check("crash after catalog commit preserves referenced revision", File.ReadAllText(output) == "published" && !File.Exists(journal));
        journal = PublicationCleanup.Begin(root, [thumbnail]); File.WriteAllText(thumbnail, "partial");
        PublicationCleanup.Sweep(root, [output], DateTime.UtcNow.AddDays(2));
        check("stale uncommitted publication is reclaimed", !File.Exists(thumbnail) && File.Exists(output) && File.Exists(unknown));
        string pending = Path.Combine(root, ".pending-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending); File.WriteAllText(Path.Combine(pending, "workspace.floatcanvas"), "partial"); File.WriteAllText(Path.Combine(pending, "unknown.txt"), "keep");
        PublicationCleanup.RemovePendingDirectory(pending);
        check("pending cleanup preserves unknown user files", !File.Exists(Path.Combine(pending, "workspace.floatcanvas")) && File.Exists(Path.Combine(pending, "unknown.txt")));
        bool rejected = false;
        try { ImageImportCommand.NormalizeFilesAsync(Enumerable.Repeat("not-read", 33).ToArray(), CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
        check("import rejects file count before opening any file", rejected);
        string oversized = Path.Combine(root, "oversized.png"); using (var file = File.Create(oversized)) file.SetLength(ImageImportBudget.MaximumFileBytes + 1);
        rejected = false; try { ImageImportCommand.NormalizeFilesAsync([oversized], CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
        check("import rejects oversized encoded file before allocation", rejected); File.Delete(oversized);
        Task.Run(() => PublicationFailuresAsync(root, check)).GetAwaiter().GetResult();
        Task.Run(() => AccumulatedImageRoundTripAsync(root, check)).GetAwaiter().GetResult();
        using var stream = new NonSeekableLargeStream(ImageImportBudget.MaximumFileBytes + 1);
        rejected = false; try { ImageImportBudget.Read(stream); } catch (InvalidDataException) { rejected = true; }
        check("nonseekable clipboard stream is bounded", rejected && stream.BytesRead <= ImageImportBudget.MaximumFileBytes + 81920);
    }
    private static async Task AccumulatedImageRoundTripAsync(string root, Action<string, bool> check)
    {
        var session = new EditorSession(new SceneDocument());
        // Stream one grayscale row at a time; never allocate five decoded 4096-square bitmaps.
        // Each real import normalizes/validates and releases its single decoded frame before the next.
        for (byte shade = 1; shade <= 5; shade++)
        {
            string imagePath = Path.Combine(root, "image-" + shade + ".png");
            await File.WriteAllBytesAsync(imagePath, SolidPng(4096, 4096, shade));
            var images = await ImageImportCommand.NormalizeFilesAsync(new[] { imagePath }, CancellationToken.None);
            ImageImportCommand.Insert(session, images, new RectD(0, 0, 800, 600));
        }
        check("five separate maximum-size image imports accumulate in one valid document", session.Document.Elements.Count == 5 && session.Assets.Count == 5);
        bool rejected = false;
        try { ImageImportBudget.ValidateImportBatch(session.Assets.Resources); } catch (InvalidDataException) { rejected = true; }
        check("the same five images still exceed a single clipboard import batch", rejected);
        var projects = new ProjectStore(); string project = Path.Combine(root, "accumulated-images.floatcanvas");
        await projects.SaveAsync(project, session.Document, session.Assets); var reopened = await projects.LoadAsync(project);
        ImageImportBudget.ValidateResources(reopened.Assets.Resources);
        check("saved accumulated images reopen through the project resource gate with byte-exact assets",
            reopened.Document.Elements.Count == 5 && reopened.Assets.Count == 5 && reopened.Assets.Resources.All(resource => resource.Data.Span.SequenceEqual(session.Assets.Get(resource.Id).Data.Span)));
        var recovery = new RecoveryStore(Path.Combine(root, "accumulated-image-recovery"));
        await recovery.SaveAsync(new WorkspaceState { LiveDocument = session.Document }, session.Assets);
        var history = new WorkspaceRecoveryHistory(recovery);
        var restored = await history.LoadAsync(new(recovery.DirectoryPath, DateTime.UtcNow, true));
        check("workspace history restores accumulated imports above the per-batch pixel budget",
            restored is not null && restored.State.LiveDocument.Elements.Count == 5 && restored.Assets.Count == 5 && restored.Assets.Resources.All(resource => resource.Data.Span.SequenceEqual(session.Assets.Get(resource.Id).Data.Span)));
        var oversized = new AssetLibrary(); oversized.Add(SolidPng(4097, 4096, 7)); rejected = false;
        try { ImageImportBudget.ValidateResources(oversized.Resources); } catch (InvalidDataException) { rejected = true; }
        check("document loading still rejects an individually oversized decoded image", rejected);
    }

    private static byte[] SolidPng(int width, int height, byte shade)
    {
        using var png = new MemoryStream(); png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height); header[8] = 8;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[width + 1]; row.AsSpan(1).Fill(shade);
            for (int y = 0; y < height; y++) zlib.Write(row);
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", Array.Empty<byte>()); return png.ToArray();
        void Chunk(string type, byte[] data)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes(type); Span<byte> value = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(value, (uint)data.Length); png.Write(value); png.Write(name); png.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte item in name.Concat(data))
            {
                crc ^= item;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
            }
            BinaryPrimitives.WriteUInt32BigEndian(value, ~crc); png.Write(value);
        }
    }
    private static async Task PublicationFailuresAsync(string root, Action<string, bool> check)
    {
        var library = new StampLibrary(Path.Combine(root, "stamp-faults")); await library.LoadAsync();
        var session = new EditorSession(new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 20, Height = 20, Style = new() { Fill = 0xFFFF0000 } }] }); session.SelectAll();
        var original = await library.SaveSelectionAsync(session, "original", StampSaveMode.RasterMerge);
        var before = Directory.GetFiles(library.DirectoryPath).Select(Path.GetFileName).Order().ToArray();
        foreach (string stage in new[] { "source", "package", "thumbnail", "before-catalog" })
        {
            library.PublicationCheckpoint = reached => { if (reached == stage) throw new IOException("injected " + stage); };
            bool failed = false;
            try { await library.SaveSelectionAsync(session, "replacement", StampSaveMode.RasterMerge, original.Id); } catch (IOException) { failed = true; }
            var after = Directory.GetFiles(library.DirectoryPath).Select(Path.GetFileName).Order().ToArray();
            check("real stamp failure at " + stage + " preserves catalog and leaves no new revision", failed && before.SequenceEqual(after) && library.Items.Single().Revision == original.Revision);
        }
        library.PublicationCheckpoint = null;
        var reopened = new StampLibrary(Path.Combine(root, "stamp-faults")); await reopened.LoadAsync();
        check("catalog remains reopenable after all partial-publication failures", (await reopened.LoadStampAsync(original.Id)).Document.Elements.Count == 1);
        var historyStore = new RecoveryStore(Path.Combine(root, "history-scale")); var history = new WorkspaceRecoveryHistory(historyStore);
        string parent = Path.Combine(historyStore.DirectoryPath, "history"); Directory.CreateDirectory(parent);
        var workspace = new WorkspaceState { LiveDocument = session.Document };
        string source = Path.Combine(root, "history-source"); var sourceStore = new RecoveryStore(source); await sourceStore.SaveAsync(workspace, session.Assets);
        for (int index = 0; index < 1000; index++)
        {
            string directory = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "workspace.floatcanvas"); File.Copy(sourceStore.CurrentPath, path);
            var package = new FileInfo(path);
            File.WriteAllText(Path.Combine(directory, "history-info.json"), JsonSerializer.Serialize(new { Fingerprint = index.ToString(), SourceFile = (string?)null, Eligible = true, PackageTicks = package.LastWriteTimeUtc.Ticks, PackageLength = package.Length }));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew(); var entries = await history.GetEntriesAsync();
        check($"1000 modern history entries load on a worker ({clock.ElapsedMilliseconds} ms)", entries.Count == 1000);
        foreach (var entry in entries) File.Delete(Path.Combine(entry.DirectoryPath, "history-info.json"));
        clock.Restart(); var legacyEntries = await history.GetEntriesAsync();
        check($"1000 legacy history packages load off the dispatcher and deduplicate ({clock.ElapsedMilliseconds} ms)", legacyEntries.Count == 1);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool cancellation = false;
        try { await history.GetEntriesAsync(cancelled.Token); } catch (OperationCanceledException) { cancellation = true; }
        check("history enumeration observes cancellation", cancellation);
    }
    private sealed class NonSeekableLargeStream(long remaining) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { int read = (int)Math.Min(count, remaining); Array.Clear(buffer, offset, read); remaining -= read; BytesRead += read; return read; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
