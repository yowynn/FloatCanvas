using System.Security.Cryptography;
using FloatCanvas.Core;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace FloatCanvas.Rendering;

internal sealed class EmojiFace : IDisposable
{
    internal EmojiFace(SKTypeface typeface)
    {
        Typeface = typeface;
        Font = new(typeface, 256) { Edging = SKFontEdging.Antialias, Subpixel = true };
        Shaper = new(typeface);
    }
    internal SKTypeface Typeface { get; }
    internal SKFont Font { get; }
    internal SKShaper Shaper { get; }
    public void Dispose() { Shaper.Dispose(); Font.Dispose(); Typeface.Dispose(); }
}

internal sealed record EmojiFontSource(string Id, string FamilyName, string RelativePath, bool System, string[] Fallbacks);

/// <summary>Resolves installed and app-managed emoji typefaces without bundling font files in the application.</summary>
public static class EmojiFontSources
{
    public const string SystemFamilyName = "Segoe UI Emoji";
    private static readonly object Gate = new();
    private static readonly ReaderWriterLockSlim Lifetime = new(LockRecursionPolicy.NoRecursion);
    private static readonly Dictionary<string, EmojiFace> Faces = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);
    private static readonly EmojiFontSource[] Sources =
    [
        new(EmojiFontIds.SystemSegoe, "Segoe UI Emoji", "", true, [EmojiFontIds.NotoColor]),
        new(EmojiFontIds.NotoColor, "Noto Color Emoji", @"noto-color-emoji\18.0\Noto-COLRv1.ttf", false,
            [EmojiFontIds.SystemSegoe]),
        new(EmojiFontIds.OpenMojiColor, "OpenMoji Color", @"openmoji-color\18.0\OpenMoji-COLRv0.ttf", false,
            [EmojiFontIds.NotoColor, EmojiFontIds.SystemSegoe]),
        new(EmojiFontIds.BlobmojiColor, "Blobmoji", @"blobmoji-color\1.001\Blobmoji-COLRv1.ttf", false,
            [EmojiFontIds.NotoColor, EmojiFontIds.SystemSegoe])
    ];
    private static string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatCanvas", "Fonts", "Emoji");
    public static event Action? Changed;

    public static string RootDirectory
    {
        get { lock (Gate) return root; }
    }

    public static void Configure(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        bool changed;
        Lifetime.EnterWriteLock();
        try
        {
            lock (Gate)
            {
                string next = Path.Combine(Path.GetFullPath(profileDirectory), "Fonts", "Emoji");
                if (string.Equals(root, next, StringComparison.OrdinalIgnoreCase)) return;
                root = next;
                ClearLocked(releaseFaces: true);
                changed = true;
            }
        }
        finally { Lifetime.ExitWriteLock(); }
        if (!changed) return;
        EmojiGlyphs.ClearCache();
        Changed?.Invoke();
    }

    public static string PathFor(string fontId)
    {
        var source = Find(fontId);
        if (source.System)
        {
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
            return File.Exists(system) ? system : "";
        }
        return source.RelativePath.Length == 0 ? "" : Path.Combine(RootDirectory, source.RelativePath);
    }

    public static bool IsAvailable(string fontId)
    {
        try { return TryWithFace(fontId, _ => true, out bool available) && available; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    internal static string Sha256(string fontId)
    {
        string path = PathFor(fontId);
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static IReadOnlyList<string> ResolveChain(string fontId)
    {
        string selected = EmojiFontIds.Normalize(fontId);
        var source = Find(selected);
        return [selected, .. source.Fallbacks];
    }

    internal static bool TryWithFace<T>(string fontId, Func<EmojiFace, T> read, out T result)
    {
        Lifetime.EnterReadLock();
        try
        {
            string id = EmojiFontIds.Normalize(fontId);
            EmojiFace? face;
            lock (Gate)
            {
                if (Faces.TryGetValue(id, out face)) { result = read(face); return true; }
                if (Missing.Contains(id)) { result = default!; return false; }
                var source = Find(id);
                SKTypeface? typeface = null;
                try
                {
                    if (source.System)
                    {
                        typeface = SKTypeface.FromFamilyName(source.FamilyName);
                        if (typeface is null || !string.Equals(typeface.FamilyName, source.FamilyName, StringComparison.OrdinalIgnoreCase))
                        {
                            typeface?.Dispose();
                            typeface = null;
                        }
                    }
                    else
                    {
                        string path = Path.Combine(root, source.RelativePath);
                        if (File.Exists(path)) typeface = SKTypeface.FromFile(path);
                    }
                    if (typeface is null) { Missing.Add(id); result = default!; return false; }
                    if (!string.Equals(typeface.FamilyName, source.FamilyName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Expected emoji font family '{source.FamilyName}', found '{typeface.FamilyName}'.");
                    face = new EmojiFace(typeface);
                    Faces.Add(id, face);
                }
                catch
                {
                    typeface?.Dispose();
                    Missing.Add(id);
                    throw;
                }
            }
            result = read(face);
            return true;
        }
        finally { Lifetime.ExitReadLock(); }
    }

    public static void Refresh()
    {
        Lifetime.EnterWriteLock();
        try { lock (Gate) ClearLocked(releaseFaces: true); }
        finally { Lifetime.ExitWriteLock(); }
        EmojiGlyphs.ClearCache();
        Changed?.Invoke();
    }

    private static EmojiFontSource Find(string id)
    {
        string normalized = EmojiFontIds.Normalize(id);
        return Sources.First(source => source.Id == normalized);
    }

    private static void ClearLocked(bool releaseFaces)
    {
        if (releaseFaces)
            foreach (var face in Faces.Values) face.Dispose();
        Faces.Clear();
        Missing.Clear();
    }
}
