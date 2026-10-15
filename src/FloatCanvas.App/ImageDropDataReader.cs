using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Owned snapshots of one OLE representation. No COM object or borrowed storage escapes Read.</summary>
internal sealed class ImageDropPayload
{
    internal IReadOnlyList<string> FilePaths { get; }
    internal IReadOnlyList<byte[]> EncodedImages { get; }
    internal ImageDropPayload(string[] paths, byte[][] images)
    { FilePaths = Array.AsReadOnly(paths); EncodedImages = Array.AsReadOnly(images); }
}

/// <summary>Synchronous STA reader of borrowed OLE data. Never accesses the system clipboard or network.</summary>
internal static class ImageDropDataReader
{
    private const int MaxImages = 32, MaxImageBytes = 64 * 1024 * 1024, MaxTotalBytes = 128 * 1024 * 1024;
    private const int DescriptorBytes = 592, DescriptorNameOffset = 72, MaxPathListBytes = 2 * 1024 * 1024;
    private const short FileDropFormat = 15;
    private const uint FileSizeFlag = 0x40, DirectoryAttribute = 0x10;
    private static readonly short PngFormat = Format("PNG"), DescriptorFormat = Format("FileGroupDescriptorW"), ContentsFormat = Format("FileContents");
    private static readonly Encoding Unicode = new UnicodeEncoding(false, false, true);

    internal static bool Supports(ComDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            if (Has(data, FileDropFormat, TYMED.TYMED_HGLOBAL)) { _ = ReadPaths(data); return true; }
            if (Has(data, PngFormat, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM)) return true;
            if (!Has(data, DescriptorFormat, TYMED.TYMED_HGLOBAL)) return false;
            var descriptors = ReadDescriptors(data);
            return descriptors.Select((_, index) => Has(data, ContentsFormat, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM, index)).All(x => x);
        }
        catch (Exception ex) when (ex is InvalidDataException or COMException or IOException or ArgumentException or OverflowException or Win32Exception)
        { return false; }
    }

    internal static ImageDropPayload Read(ComDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (Has(data, FileDropFormat, TYMED.TYMED_HGLOBAL)) return new(ReadPaths(data), []);
        if (Has(data, PngFormat, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM))
        {
            byte[] png = ReadMedium(data, PngFormat, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM, -1, MaxImageBytes);
            RequireImage(png, ".png");
            return new([], [png]);
        }
        if (!Has(data, DescriptorFormat, TYMED.TYMED_HGLOBAL)) throw Invalid("The drop contains no supported image representation.");
        var descriptors = ReadDescriptors(data);
        var images = new List<byte[]>(descriptors.Length);
        int total = 0;
        for (int i = 0; i < descriptors.Length; i++)
        {
            var descriptor = descriptors[i];
            int limit = Math.Min(MaxImageBytes, MaxTotalBytes - total);
            byte[] bytes = ReadMedium(data, ContentsFormat, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM, i, limit, descriptor.Length);
            RequireImage(bytes, Path.GetExtension(descriptor.Name));
            total = checked(total + bytes.Length); images.Add(bytes);
        }
        return new([], images.ToArray());
    }

    private static string[] ReadPaths(ComDataObject data)
    {
        byte[] bytes = ReadMedium(data, FileDropFormat, TYMED.TYMED_HGLOBAL, -1, MaxPathListBytes);
        if (bytes.Length < 22) throw Invalid("The file list header is truncated.");
        uint offset = BitConverter.ToUInt32(bytes, 0);
        bool wide = BitConverter.ToUInt32(bytes, 16) != 0;
        if (offset < 20 || offset >= bytes.Length || (wide && (offset & 1) != 0)) throw Invalid("The file list offset is invalid.");
        int position = checked((int)offset), step = wide ? 2 : 1;
        var paths = new List<string>();
        while (true)
        {
            int end = position;
            while (end + step <= bytes.Length && (bytes[end] != 0 || (wide && bytes[end + 1] != 0))) end += step;
            if (end + step > bytes.Length) throw Invalid("The file list is not terminated.");
            if (end == position)
            {
                if (paths.Count == 0) throw Invalid("The file list is empty.");
                return paths.ToArray();
            }
            if (paths.Count == MaxImages) throw Invalid("The drop exceeds the image count limit.");
            string path = wide ? Unicode.GetString(bytes, position, end - position) : DecodeAnsi(bytes.AsSpan(position, end - position).ToArray());
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathFullyQualified(path) || !SupportedExtension(Path.GetExtension(path)))
                throw Invalid("Every dropped file must be an absolute image path with a supported extension.");
            paths.Add(path); position = end + step;
        }
    }

    private static Descriptor[] ReadDescriptors(ComDataObject data)
    {
        byte[] bytes = ReadMedium(data, DescriptorFormat, TYMED.TYMED_HGLOBAL, -1, 4 + MaxImages * DescriptorBytes);
        if (bytes.Length < 4) throw Invalid("The virtual file group header is truncated.");
        uint count = BitConverter.ToUInt32(bytes, 0);
        if (count is 0 or > MaxImages || bytes.Length < checked(4 + (int)count * DescriptorBytes)) throw Invalid("The virtual file group has an invalid count or length.");
        var descriptors = new Descriptor[count];
        long totalDeclared = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 4 + i * DescriptorBytes, nameAt = at + DescriptorNameOffset, end = nameAt;
            while (end < nameAt + 520 && (bytes[end] != 0 || bytes[end + 1] != 0)) end += 2;
            if (end == nameAt + 520) throw Invalid("A virtual file name is not terminated.");
            string name = Unicode.GetString(bytes, nameAt, end - nameAt);
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !SupportedExtension(Path.GetExtension(name)) ||
                (BitConverter.ToUInt32(bytes, at + 36) & DirectoryAttribute) != 0)
                throw Invalid("Virtual files must have plain supported image names, not paths or directories.");
            long? length = null;
            if ((BitConverter.ToUInt32(bytes, at) & FileSizeFlag) != 0)
            {
                ulong size = ((ulong)BitConverter.ToUInt32(bytes, at + 64) << 32) | BitConverter.ToUInt32(bytes, at + 68);
                if (size is 0 or > MaxImageBytes) throw Invalid("A virtual image exceeds the per-image limit or is empty.");
                length = (long)size; totalDeclared += length.Value;
                if (totalDeclared > MaxTotalBytes) throw Invalid("Virtual images exceed the total byte limit.");
            }
            descriptors[i] = new(name, length);
        }
        return descriptors;
    }

    private static byte[] ReadMedium(ComDataObject data, short format, TYMED kinds, int index, int limit, long? expected = null)
    {
        if (limit <= 0 || expected > limit) throw Invalid("The image byte budget is exhausted.");
        var request = Request(format, kinds, index);
        data.GetData(ref request, out var medium);
        try
        {
            if ((medium.tymed & kinds) == 0) throw Invalid("The source returned a medium that was not requested.");
            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                ulong size = Native.GlobalSize(medium.unionmember).ToUInt64();
                ulong length = expected is { } declared ? (ulong)declared : size;
                if (length == 0 || length > (ulong)limit || size < length) throw Invalid("The global-memory image has an invalid length.");
                nint pointer = Native.GlobalLock(medium.unionmember);
                if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { var bytes = new byte[(int)length]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
                finally { Native.GlobalUnlock(medium.unionmember); }
            }
            if (medium.tymed != TYMED.TYMED_ISTREAM || medium.unionmember == 0) throw Invalid("The source returned an unsupported storage medium.");
            // This wrapper is ours exclusively. Releasing it must not disconnect a
            // cached RCW used by the source or another consumer in this process.
            object value = Marshal.GetUniqueObjectForIUnknown(medium.unionmember);
            try
            {
                if (value is not IStream stream) throw Invalid("The source did not return an IStream.");
                try { stream.Stat(out var stat, 1); if (stat.cbSize < 0 || stat.cbSize > limit) throw Invalid("The image stream exceeds its byte limit."); }
                catch (COMException ex) when (ex.HResult is unchecked((int)0x80004001) or unchecked((int)0x80030001)) { }
                try { stream.Seek(0, 0, 0); }
                catch (COMException ex) when (ex.HResult is unchecked((int)0x80004001) or unchecked((int)0x80030001)) { }
                using var output = new MemoryStream();
                byte[] buffer = new byte[81920];
                nint readCount = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    while (true)
                    {
                        int count = (int)Math.Min(buffer.Length, (expected ?? limit) - output.Length + 1);
                        if (count <= 0) throw Invalid("The image stream exceeds its declared length or byte budget.");
                        Marshal.WriteInt32(readCount, 0); stream.Read(buffer, count, readCount);
                        int read = Marshal.ReadInt32(readCount);
                        if (read < 0 || read > count) throw Invalid("The image stream returned an invalid read count.");
                        if (read == 0) break;
                        if (output.Length + read > limit || (expected is { } exact && output.Length + read > exact)) throw Invalid("The image stream exceeds its declared length or byte budget.");
                        output.Write(buffer, 0, read);
                    }
                }
                finally { Marshal.FreeCoTaskMem(readCount); }
                if (output.Length == 0 || (expected is { } required && output.Length != required)) throw Invalid("The image stream is empty or truncated.");
                return output.ToArray();
            }
            finally { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        }
        finally { Native.ReleaseStgMedium(ref medium); }
    }

    private static bool Has(ComDataObject data, short format, TYMED medium, int index = -1)
    {
        var request = Request(format, medium, index);
        return data.QueryGetData(ref request) == 0;
    }
    private static FORMATETC Request(short format, TYMED medium, int index) => new()
    { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
    private static bool SupportedExtension(string extension) => extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";
    private static void RequireImage(byte[] bytes, string extension)
    {
        bool matches = extension.ToLowerInvariant() switch
        {
            ".png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
            ".bmp" => bytes.Length >= 14 && bytes[0] == 'B' && bytes[1] == 'M',
            ".webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!matches) throw Invalid("The encoded image signature does not match its advertised format.");
    }
    private static short Format(string name)
    {
        uint id = Native.RegisterClipboardFormatW(name);
        if (id == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((short)id);
    }
    private static string DecodeAnsi(byte[] bytes)
    {
        int length = Native.MultiByteToWideChar(0, 8, bytes, bytes.Length, null, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var chars = new char[length];
        if (Native.MultiByteToWideChar(0, 8, bytes, bytes.Length, chars, chars.Length) != length) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(chars);
    }
    private static InvalidDataException Invalid(string message) => new(L.Get("ImageDrop.Unsupported"), new InvalidDataException(message));
    private sealed record Descriptor(string Name, long? Length);
    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint RegisterClipboardFormatW(string format);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nuint GlobalSize(nint memory);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalUnlock(nint memory);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] bytes, int count, [Out] char[]? chars, int capacity);
        [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    }
}
