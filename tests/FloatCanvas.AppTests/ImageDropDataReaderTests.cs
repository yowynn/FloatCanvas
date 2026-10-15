using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

/// <summary>Real HGLOBAL/IStream protocol ownership, not external OLE delivery or system Clipboard tests.</summary>
internal static class ImageDropDataReaderTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAI0lEQVR4nGNQbn5wSTD6NKPt/O/1DIbTXja4b7/NIN317D8AmI0MdpKmXpIAAAAASUVORK5CYII=";
    private static readonly short PngFormat = Format("PNG"), DescriptorFormat = Format("FileGroupDescriptorW"), ContentsFormat = Format("FileContents");

    internal static void Run(Action<string, bool> check)
    {
        byte[] png = Convert.FromBase64String(Png);
        var empty = new Source();
        check("OLE reader rejects an empty data object without any clipboard fallback", !ImageDropDataReader.Supports(empty) && Rejects(() => ImageDropDataReader.Read(empty)));

        foreach (bool wide in new[] { true, false })
        {
            var source = new Source().Global(15, -1, Paths([@"C:\fixture\one.PNG", @"D:\images\two.webp"], wide));
            check($"OLE {(wide ? "Unicode" : "ANSI")} file metadata supports all-image paths", ImageDropDataReader.Supports(source));
            var payload = ImageDropDataReader.Read(source);
            check($"OLE {(wide ? "Unicode" : "ANSI")} file snapshot owns the paths and no encoded image branch", payload.FilePaths.SequenceEqual(new[] { @"C:\fixture\one.PNG", @"D:\images\two.webp" }) && payload.EncodedImages.Count == 0);
            check("OLE CF_HDROP storage is released after both capability and snapshot reads", source.Handles.All(Freed));
        }
        foreach (string[] paths in new[] { new[] { @"C:\a.png", @"C:\wrong.txt" }, new[] { " " }, new[] { "relative.png" }, Array.Empty<string>(), Enumerable.Repeat(@"C:\a.png", 33).ToArray() })
        {
            var source = new Source().Global(15, -1, Paths(paths, true));
            check("OLE file capability rejects mixed, blank, relative, empty or excessive paths", !ImageDropDataReader.Supports(source));
            check("OLE file snapshot rejects the same invalid path list", Rejects(() => ImageDropDataReader.Read(source)) && source.Handles.All(Freed));
        }
        byte[] badOffset = Paths([@"C:\one.png"], true); BitConverter.GetBytes(uint.MaxValue).CopyTo(badOffset, 0);
        var invalidOffset = new Source().Global(15, -1, badOffset);
        check("OLE file-list offset cannot escape its allocation", !ImageDropDataReader.Supports(invalidOffset) && Rejects(() => ImageDropDataReader.Read(invalidOffset)));

        var global = new Source().Global(PngFormat, -1, png);
        check("OLE PNG capability does not request delayed image content", ImageDropDataReader.Supports(global) && global.Gets.Count == 0);
        var imagePayload = ImageDropDataReader.Read(global);
        check("OLE PNG HGLOBAL retains exact original bytes in its owned image branch", imagePayload.FilePaths.Count == 0 && imagePayload.EncodedImages.Count == 1 && imagePayload.EncodedImages[0].SequenceEqual(png));
        check("OLE PNG HGLOBAL is released after copying", global.Handles.All(Freed));
        check("OLE PNG payload still decodes through the production normalizer", AssetImport.Normalize(imagePayload.EncodedImages[0]).Width == 3);

        var stream = new BytesStream(png) { Position = png.Length };
        var streamed = new Source().Stream(PngFormat, -1, stream);
        var streamedPayload = ImageDropDataReader.Read(streamed);
        check("OLE PNG IStream is rewound and copied before Drop returns", streamedPayload.EncodedImages.Single().SequenceEqual(png) && stream.ReadCalls > 0);
        stream.Bytes[0] ^= 1;
        check("OLE copied image does not alias source stream bytes", streamedPayload.EncodedImages.Single().SequenceEqual(png));
        NativeStreamOwnership(png, check);

        var retained = new Source().Global(PngFormat, -1, png, sourceOwned: true);
        var retainedPayload = ImageDropDataReader.Read(retained);
        check("OLE reader honors source-owned pUnkForRelease without freeing its HGLOBAL", retained.Handles.Count == 1 && !Freed(retained.Handles[0]));
        retained.FreeSourceStorage();
        check("OLE copied PNG survives source-owned storage release", retainedPayload.EncodedImages.Single().SequenceEqual(png));

        byte[] jpeg = Encoded(SKEncodedImageFormat.Jpeg), webp = Encoded(SKEncodedImageFormat.Webp);
        var virtualFiles = Virtual([("alpha.png", png), ("photo.jpeg", jpeg), ("other.webp", webp)]);
        check("OLE virtual-file capability reads only bounded descriptors, never file contents", ImageDropDataReader.Supports(virtualFiles) && virtualFiles.Gets.All(x => x.Format == DescriptorFormat));
        var virtualPayload = ImageDropDataReader.Read(virtualFiles);
        check("OLE virtual-file snapshot reads every lindex in order as owned bytes", virtualPayload.FilePaths.Count == 0 && virtualPayload.EncodedImages.Count == 3 && virtualPayload.EncodedImages[0].SequenceEqual(png) && virtualPayload.EncodedImages[1].SequenceEqual(jpeg) && virtualPayload.EncodedImages[2].SequenceEqual(webp));
        check("OLE virtual file content requests carry all zero-based lindex values", virtualFiles.Gets.Where(x => x.Format == ContentsFormat).Select(x => x.Index).SequenceEqual(new[] { 0, 1, 2 }));
        check("OLE virtual descriptors are released after every use", virtualFiles.Handles.All(Freed));
        check("OLE virtual image bytes pass the actual PNG/JPEG/WebP production decoder", virtualPayload.EncodedImages.Select(AssetImport.Normalize).All(x => x.Width > 0 && x.Height > 0));

        foreach (var descriptor in new[]
        {
            Descriptors([( "..\\escape.png", (long?)png.Length )]),
            Descriptors([( "wrong.txt", (long?)png.Length )]),
            Descriptors([( "directory.png", (long?)png.Length )], directory: true),
            Descriptors([( new string('a', 260), (long?)png.Length )], terminateName: false),
            Descriptors([( "huge.png", (long?)(64L * 1024 * 1024 + 1) )]),
            Descriptors([( "one.png", (long?)(64L * 1024 * 1024) ), ( "two.png", (long?)(64L * 1024 * 1024) ), ( "three.png", (long?)(64L * 1024 * 1024) )]),
            new byte[] { 1, 0, 0, 0 },
            new byte[] { 33, 0, 0, 0 },
            new byte[] { 0, 0, 0, 0 }
        })
        {
            var source = new Source().Global(DescriptorFormat, -1, descriptor);
            check("OLE virtual metadata rejects traversal, unsupported type, directory, truncation and fixed byte/count limits", !ImageDropDataReader.Supports(source) && Rejects(() => ImageDropDataReader.Read(source)));
            check("OLE malformed descriptor releases its native storage without requesting file content", source.Handles.All(Freed) && source.Gets.All(x => x.Format == DescriptorFormat));
        }
        var missingContent = new Source().Global(DescriptorFormat, -1, Descriptors([("one.png", (long?)png.Length)]));
        check("OLE virtual capability rejects a missing FileContents item", !ImageDropDataReader.Supports(missingContent));
        var mismatch = Virtual([("spoof.png", jpeg)]);
        check("OLE virtual extension cannot disguise another image signature", Rejects(() => ImageDropDataReader.Read(mismatch)));
        var precedence = Virtual([("spoof.png", jpeg)]).Global(PngFormat, -1, png);
        check("OLE actual PNG representation takes precedence over virtual file names", ImageDropDataReader.Read(precedence).EncodedImages.Single().SequenceEqual(png) && precedence.Gets.All(x => x.Format == PngFormat));

        var tooLarge = new BytesStream(png) { DeclaredSize = 64L * 1024 * 1024 + 1 };
        check("OLE stream Stat rejects an oversized payload before allocating or reading it", Rejects(() => ImageDropDataReader.Read(new Source().Stream(PngFormat, -1, tooLarge))) && tooLarge.ReadCalls == 0);
        var wrongCount = new BytesStream(png) { InvalidReadCount = true };
        check("OLE stream rejects a returned count larger than the requested buffer", Rejects(() => ImageDropDataReader.Read(new Source().Stream(PngFormat, -1, wrongCount))));
        var truncated = new Source().Global(DescriptorFormat, -1, Descriptors([("one.png", (long?)(png.Length + 1))])).Stream(ContentsFormat, 0, new(png));
        check("OLE virtual stream cannot be shorter than its declared file size", Rejects(() => ImageDropDataReader.Read(truncated)));
        var overrun = new Source().Global(DescriptorFormat, -1, Descriptors([("one.png", (long?)(png.Length - 1))])).Stream(ContentsFormat, 0, new(png));
        check("OLE virtual stream cannot exceed its declared file size", Rejects(() => ImageDropDataReader.Read(overrun)));
        var unknownLength = new BytesStream(png) { UnsupportedSeekAndStat = true };
        check("OLE bounded nonseekable stream can be copied without a reported size", ImageDropDataReader.Read(new Source().Stream(PngFormat, -1, unknownLength)).EncodedImages.Single().SequenceEqual(png));
        var corruptPng = new Source().Global(PngFormat, -1, jpeg);
        check("OLE registered PNG requires PNG signature and releases bad data", Rejects(() => ImageDropDataReader.Read(corruptPng)) && corruptPng.Handles.All(Freed));
        Integration("OLE owned PNG", imagePayload, png, check);
        Integration("OLE virtual PNG/JPEG/WebP", virtualPayload, png, check);
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-OLE-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "original.png"); File.WriteAllBytes(path, png);
            var filePayload = ImageDropDataReader.Read(new Source().Global(15, -1, Paths([path], true)));
            Integration("OLE file list", filePayload, png, check);
        }
        finally { Directory.Delete(directory, recursive: true); }
        var damaged = ImageDropDataReader.Read(new Source().Global(PngFormat, -1, png[..16]));
        check("OLE advertised image still requires complete decoding in the actual import pipeline", Rejects(() => DesktopSession.NormalizeCanvasDropAsync(damaged, CancellationToken.None).GetAwaiter().GetResult()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledRead = false;
        try { DesktopSession.NormalizeCanvasDropAsync(imagePayload, cancelled.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelledRead = true; }
        check("OLE owned payload normalization honors cancellation before commit", cancelledRead);
    }

    private static void Integration(string label, ImageDropPayload payload, byte[] png, Action<string, bool> check)
    {
        var images = DesktopSession.NormalizeCanvasDropAsync(payload, CancellationToken.None).GetAwaiter().GetResult();
        var target = new EditorSession(new() { Viewport = new(300, 200, 800, 600) });
        using var request = new ImageDropRequest(target, target.Document.Viewport, CancellationToken.None);
        check(label + " freezes physical release coordinates using the actual request", request.TrySetDropPoint(new(1750, 900), new(1200, 500)));
        check(label + " normalizes then commits through the actual production methods once", request.TryCommit(request, target, null, images) && target.UndoCount == 1 && target.Document.Elements.Count == images.Count);
        var image = target.Document.Elements[0]; string id = image.AssetId!;
        check(label + " retains original PNG resource and places its center at the release point", target.Assets.Get(id).Data.Span.SequenceEqual(png) && ElementGeometry.Bounds(image).Center == new PointD(550, 400));
        Guid[] ids = target.Document.Elements.Select(e => e.Id).ToArray();
        check(label + " one undo removes the complete import and redo restores IDs and resources", target.Undo() && target.Document.Elements.Count == 0 && target.Redo() && target.Document.Elements.Select(e => e.Id).SequenceEqual(ids) && target.Assets.Get(id).Data.Span.SequenceEqual(png));
    }

    private static void NativeStreamOwnership(byte[] png, Action<string, bool> check)
    {
        nint native = Native.SHCreateMemStream(png, (uint)png.Length);
        if (native == 0) throw new OutOfMemoryException();
        object? shared = null;
        ImageDropPayload? payload = null;
        try
        {
            // A genuine native stream, not our managed BytesStream CCW. Keep the
            // source's ordinary cached RCW alive while Read uses its unique RCW.
            shared = Marshal.GetObjectForIUnknown(native);
            check("OLE native IStream fixture retains an actual shared RCW", Marshal.IsComObject(shared));
            payload = ImageDropDataReader.Read(new Source().RawStream(PngFormat, -1, native));
            check("OLE native IStream read snapshots exact bytes through its owned wrapper", payload.EncodedImages.Single().SequenceEqual(png));
            var source = (IStream)shared;
            source.Stat(out var stat, 1); source.Seek(0, 0, 0);
            byte[] prefix = new byte[8]; nint readCount = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                Marshal.WriteInt32(readCount, 0); source.Read(prefix, prefix.Length, readCount);
                check("OLE reader release leaves the source's existing shared RCW usable", stat.cbSize == png.Length && Marshal.ReadInt32(readCount) == prefix.Length && prefix.SequenceEqual(png[..8]));
            }
            finally { Marshal.FreeCoTaskMem(readCount); }
        }
        finally
        {
            // Balance only this test's GetObjectForIUnknown reference and the
            // SHCreateMemStream result. The medium transfers a separate AddRef.
            if (shared is not null && Marshal.IsComObject(shared)) Marshal.ReleaseComObject(shared);
            Marshal.Release(native);
        }
        check("OLE owned bytes survive native source and shared RCW disposal", payload!.EncodedImages.Single().SequenceEqual(png));
    }

    private static bool Rejects(Action action)
    { try { action(); return false; } catch (Exception ex) when (ex is InvalidDataException or COMException) { return true; } }
    private static short Format(string name) => unchecked((short)Native.RegisterClipboardFormatW(name));
    private static bool Freed(nint handle) => Native.GlobalFlags(handle) == 0x8000;
    private static byte[] Paths(string[] paths, bool wide)
    {
        byte[] names = (wide ? Encoding.Unicode : Encoding.ASCII).GetBytes(string.Join('\0', paths) + "\0\0");
        byte[] result = new byte[20 + names.Length]; BitConverter.GetBytes(20u).CopyTo(result, 0); BitConverter.GetBytes(wide ? 1 : 0).CopyTo(result, 16); names.CopyTo(result, 20); return result;
    }
    private static byte[] Descriptors((string Name, long? Size)[] files, bool directory = false, bool terminateName = true)
    {
        var result = new byte[4 + files.Length * 592]; BitConverter.GetBytes((uint)files.Length).CopyTo(result, 0);
        for (int i = 0; i < files.Length; i++)
        {
            int at = 4 + i * 592;
            BitConverter.GetBytes(files[i].Size.HasValue ? 0x40u : 0u).CopyTo(result, at);
            BitConverter.GetBytes(directory ? 0x10u : 0u).CopyTo(result, at + 36);
            if (files[i].Size is { } size) { BitConverter.GetBytes((uint)((ulong)size >> 32)).CopyTo(result, at + 64); BitConverter.GetBytes((uint)size).CopyTo(result, at + 68); }
            byte[] name = Encoding.Unicode.GetBytes(files[i].Name + (terminateName ? "\0" : ""));
            name.AsSpan(0, Math.Min(name.Length, 520)).CopyTo(result.AsSpan(at + 72));
        }
        return result;
    }
    private static Source Virtual((string Name, byte[] Bytes)[] files)
    {
        var source = new Source().Global(DescriptorFormat, -1, Descriptors(files.Select(f => (f.Name, (long?)f.Bytes.Length)).ToArray()));
        for (int i = 0; i < files.Length; i++) source.Stream(ContentsFormat, i, new(files[i].Bytes));
        return source;
    }
    private static byte[] Encoded(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(4, 3); bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(format, 90); return encoded!.ToArray();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Source : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private readonly Dictionary<(short, int), (TYMED Kind, Func<STGMEDIUM> Create)> entries = [];
        private readonly List<nint> sourceOwned = [];
        internal List<nint> Handles { get; } = [];
        internal List<(short Format, int Index)> Gets { get; } = [];
        internal Source Global(short format, int index, byte[] bytes, bool sourceOwned = false)
        {
            entries[(format, index)] = (TYMED.TYMED_HGLOBAL, () =>
            {
                nint handle = Native.GlobalAlloc(0x42, (nuint)bytes.Length);
                if (handle == 0) throw new OutOfMemoryException();
                nint pointer = Native.GlobalLock(handle); Marshal.Copy(bytes, 0, pointer, bytes.Length); Native.GlobalUnlock(handle);
                Handles.Add(handle); if (sourceOwned) this.sourceOwned.Add(handle);
                return new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = sourceOwned ? this : null };
            }); return this;
        }
        internal Source Stream(short format, int index, BytesStream stream)
        {
            entries[(format, index)] = (TYMED.TYMED_ISTREAM, () => new() { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)) }); return this;
        }
        internal Source RawStream(short format, int index, nint pointer)
        {
            entries[(format, index)] = (TYMED.TYMED_ISTREAM, () =>
            { Marshal.AddRef(pointer); return new() { tymed = TYMED.TYMED_ISTREAM, unionmember = pointer }; }); return this;
        }
        internal void FreeSourceStorage() { foreach (nint h in sourceOwned) Native.GlobalFree(h); sourceOwned.Clear(); }
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (QueryGetData(ref format) != 0) throw new COMException("Format unavailable", unchecked((int)0x80040064));
            Gets.Add((format.cfFormat, format.lindex)); medium = entries[(format.cfFormat, format.lindex)].Create();
        }
        public int QueryGetData(ref FORMATETC format) => entries.TryGetValue((format.cfFormat, format.lindex), out var item) && (format.tymed & item.Kind) != 0 && format.dwAspect == DVASPECT.DVASPECT_CONTENT ? 0 : unchecked((int)0x80040064);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotImplementedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = default; return 0x40130; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotImplementedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotImplementedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumerator) { enumerator = null; return unchecked((int)0x80040003); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class BytesStream(byte[] bytes) : IStream
    {
        internal byte[] Bytes { get; } = bytes.ToArray();
        internal int Position, ReadCalls;
        internal long? DeclaredSize;
        internal bool InvalidReadCount, UnsupportedSeekAndStat;
        public void Read(byte[] buffer, int count, nint read)
        {
            ReadCalls++; int copied = Math.Min(count, Bytes.Length - Position);
            Bytes.AsSpan(Position, copied).CopyTo(buffer); Position += copied;
            Marshal.WriteInt32(read, InvalidReadCount ? count + 1 : copied);
        }
        public void Seek(long offset, int origin, nint position)
        {
            if (UnsupportedSeekAndStat) throw new COMException("Nonseekable", unchecked((int)0x80030001));
            Position = checked((int)(origin == 0 ? offset : origin == 1 ? Position + offset : Bytes.Length + offset));
            if (position != 0) Marshal.WriteInt64(position, Position);
        }
        public void Stat(out STATSTG stat, int flags)
        { if (UnsupportedSeekAndStat) throw new COMException("Unknown size", unchecked((int)0x80004001)); stat = new() { type = 2, cbSize = DeclaredSize ?? Bytes.Length }; }
        public void Write(byte[] buffer, int count, nint written) => throw new NotImplementedException();
        public void SetSize(long size) => throw new NotImplementedException();
        public void CopyTo(IStream target, long count, nint read, nint written) => throw new NotImplementedException();
        public void Commit(int flags) { }
        public void Revert() => throw new NotImplementedException();
        public void LockRegion(long offset, long count, int type) => throw new NotImplementedException();
        public void UnlockRegion(long offset, long count, int type) => throw new NotImplementedException();
        public void Clone(out IStream stream) => throw new NotImplementedException();
    }
    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormatW(string name);
        [DllImport("kernel32.dll")] internal static extern nint GlobalAlloc(uint flags, nuint size);
        [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalUnlock(nint memory);
        [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
        [DllImport("kernel32.dll")] internal static extern uint GlobalFlags(nint memory);
        [DllImport("shlwapi.dll", ExactSpelling = true)] internal static extern nint SHCreateMemStream([In] byte[] bytes, uint count);
    }
}
