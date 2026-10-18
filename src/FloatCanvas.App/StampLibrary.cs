using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal enum StampSaveMode { EditableGroup, RasterMerge }
internal sealed record StampRenderContext(SceneDocument Document, AssetLibrary Assets, TimeSpan AnimationTime = default);
internal sealed record StampEntry(Guid Id, string Name, StampSaveMode Mode, double Width, double Height, DateTime UpdatedAtUtc, Guid Revision);

/// <summary>Immutable, self-contained stamp revisions published by one atomic catalog replacement.</summary>
internal sealed class StampLibrary(string profileDirectory)
{
    private sealed class Catalog
    {
        public int Version { get; set; } = 1;
        public List<StampEntry> Items { get; set; } = [];
        public Guid?[] Pins { get; set; } = new Guid?[10];
        public Catalog Clone() => new() { Version = Version, Items = [.. Items], Pins = (Guid?[])Pins.Clone() };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ProjectStore projects = new();
    private Catalog catalog = new();
    private bool loaded;
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(profileDirectory), "stamps");
    private string CatalogPath => Path.Combine(DirectoryPath, "library.json");
    public IReadOnlyList<StampEntry> Items => catalog.Items.AsReadOnly();
    public IReadOnlyList<Guid?> Pins => Array.AsReadOnly(catalog.Pins);
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (loaded) return;
            if (File.Exists(CatalogPath))
            {
                try
                {
                    if (new FileInfo(CatalogPath).Length > 32 * 1024 * 1024) throw new InvalidDataException();
                    var content = JsonSerializer.Deserialize<Catalog>(await File.ReadAllBytesAsync(CatalogPath, ct), JsonOptions);
                    if (content is null || content.Version != 1 || content.Items is null || content.Pins is not { Length: 10 } ||
                        content.Items.Any(item => item is null || item.Id == Guid.Empty || item.Revision == Guid.Empty ||
                            string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 120 || !Enum.IsDefined(item.Mode) ||
                            !double.IsFinite(item.Width) || !double.IsFinite(item.Height) || item.Width <= 0 || item.Height <= 0) ||
                        content.Items.Select(item => item.Id).Distinct().Count() != content.Items.Count ||
                        content.Pins.Any(id => id.HasValue && content.Items.All(item => item.Id != id.Value))) throw new InvalidDataException();
                    catalog = content;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
                { throw new InvalidDataException(L.Get("Stamp.InvalidLibrary") + CatalogPath, ex); }
            }
            loaded = true;
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    public async Task<StampEntry> SaveSelectionAsync(EditorSession session, string name, StampSaveMode mode = StampSaveMode.EditableGroup,
        Guid? replaceId = null, CancellationToken ct = default, StampRenderContext? renderContext = null)
    {
        name = ValidateName(name);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var source = session.CreateSelectionSnapshot(normalizeOrigin: true);
        if (source.Elements.Count == 0) throw new InvalidOperationException(L.Get("Stamp.NoSelection"));
        SceneDocument? context=null;
        if(renderContext is not null)
        {
            context=renderContext.Document.Clone();
            var original=session.CreateSelectionSnapshot(normalizeOrigin:false).Viewport;
            var shift=Affine2D.Translation(-original.X,-original.Y);
            foreach(var item in context.Elements)item.Transform=shift.Compose(item.Transform);
            if(context.Background is { } background)background.Transform=shift.Compose(background.Transform);
            context.Viewport=source.Viewport;
        }
        // Capture original encoded bytes before yielding; UI pruning cannot invalidate this save.
        var sourceAssets = new AssetLibrary();
        foreach (string id in source.ReferencedAssets)
        {
            var resource = session.Assets.Get(id);
            sourceAssets.Add(resource.Data.ToArray(), resource.MediaType);
        }
        await gate.WaitAsync(ct);
        StampEntry entry;
        StampEntry? previous = null;
        try
        {
            EnsureLoaded();
            if (replaceId is { } replace) previous = Find(replace);
            entry = new(replaceId ?? Guid.NewGuid(), name, mode, source.Viewport.Width, source.Viewport.Height, DateTime.UtcNow, Guid.NewGuid());
            var rendered = await Task.Run(() =>
            {
                using var renderer = new SceneRenderer { AnimationTime = renderContext?.AnimationTime ?? TimeSpan.Zero };
                byte[] Render(double scale)
                {
                    renderer.LiveEffectContext=context is null ? null : Scaled(context,scale);
                    return renderer.ExportPng(Scaled(source,scale),renderContext?.Assets ?? sourceAssets,transparent:true);
                }
                byte[] preview = Render(160 / Math.Max(source.Viewport.Width, source.Viewport.Height));
                byte[]? merged = mode == StampSaveMode.RasterMerge
                    ? Render(Math.Min(2, 4096 / Math.Max(source.Viewport.Width, source.Viewport.Height)))
                    : null;
                return (preview, merged);
            }, ct);
            SceneDocument document = source;
            AssetLibrary assets = sourceAssets;
            if (rendered.merged is { } png)
            {
                assets = new();
                string id = assets.Add(png);
                document = new() { Viewport = source.Viewport, Elements = [new() { Kind = ElementKind.Image, AssetId = id, Width = source.Viewport.Width, Height = source.Viewport.Height }] };
                // A raster stamp still retains the original editable project and encoded image bytes.
                await projects.SaveAsync(SourcePath(entry), source, sourceAssets, ct);
            }
            await projects.SaveAsync(PackagePath(entry), document, assets, ct);
            await WriteAtomicallyAsync(ThumbnailPath(entry), rendered.preview, ct);
            var next = catalog.Clone();
            if (previous is null)
            {
                next.Items.Add(entry);
                int emptySlot = Array.FindIndex(next.Pins, id => id is null);
                if (emptySlot >= 0) next.Pins[emptySlot] = entry.Id;
            }
            else next.Items[next.Items.FindIndex(item => item.Id == previous.Id)] = entry;
            await PublishAsync(next, ct);
        }
        finally { gate.Release(); }
        if (previous is not null) DeleteRevision(previous);
        Changed?.Invoke();
        return entry;
    }

    public async Task<LoadedProject> LoadStampAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            var entry = Find(id);
            try
            {
                var stamp = await projects.LoadAsync(PackagePath(entry), ct);
                if (stamp.Document.Elements.Count == 0) throw new InvalidDataException();
                return stamp;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or NotSupportedException)
            { throw new InvalidDataException(L.Get("Stamp.Corrupt") + entry.Name, ex); }
        }
        finally { gate.Release(); }
    }

    public async Task<LoadedProject> LoadSourceAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            var entry = Find(id);
            try { return await projects.LoadAsync(entry.Mode == StampSaveMode.RasterMerge ? SourcePath(entry) : PackagePath(entry), ct); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or NotSupportedException)
            { throw new InvalidDataException(L.Get("Stamp.Corrupt") + entry.Name, ex); }
        }
        finally { gate.Release(); }
    }

    public async Task PlaceAsync(Guid id, EditorSession target, PointD center, CancellationToken ct = default) =>
        Place(await LoadStampAsync(id, ct), target, center);

    public static void Place(LoadedProject stamp, EditorSession target, PointD center)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)) throw new ArgumentOutOfRangeException(nameof(center));
        var origin = stamp.Document.Viewport.Center;
        target.InsertDocument(stamp.Document, stamp.Assets, Affine2D.Translation(center.X - origin.X, center.Y - origin.Y), group: true);
        target.SetSelection([]);
    }

    public async Task<byte[]> GetThumbnailAsync(Guid id, CancellationToken ct = default)
    {
        EnsureLoaded();
        var entry = Find(id);
        try { return await File.ReadAllBytesAsync(ThumbnailPath(entry), ct); }
        catch (IOException ex) { throw new InvalidDataException(L.Get("Stamp.Corrupt") + entry.Name, ex); }
    }

    public Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        name = ValidateName(name);
        return MutateAsync(next => { int index = next.Items.FindIndex(item => item.Id == id); if (index < 0) throw new KeyNotFoundException(L.Get("Stamp.Missing")); next.Items[index] = next.Items[index] with { Name = name }; }, ct);
    }

    public Task PinAsync(int slot, Guid? id, CancellationToken ct = default)
    {
        if (slot is < 0 or >= 10) throw new ArgumentOutOfRangeException(nameof(slot));
        return MutateAsync(next => { if (id is { } value && next.Items.All(item => item.Id != value)) throw new KeyNotFoundException(L.Get("Stamp.Missing")); next.Pins[slot] = id; }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        StampEntry? removed = null;
        await MutateAsync(next =>
        {
            removed = next.Items.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException(L.Get("Stamp.Missing"));
            next.Items.Remove(removed);
            for (int slot = 0; slot < 10; slot++) if (next.Pins[slot] == id) next.Pins[slot] = null;
        }, ct);
        DeleteRevision(removed!);
    }

    private async Task MutateAsync(Action<Catalog> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { EnsureLoaded(); var next = catalog.Clone(); change(next); await PublishAsync(next, ct); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    private async Task PublishAsync(Catalog next, CancellationToken ct)
    {
        await WriteAtomicallyAsync(CatalogPath, JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions), ct);
        catalog = next;
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true); }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static SceneDocument Scaled(SceneDocument document, double scale)
    {
        var result = document.Clone();
        var transform = Affine2D.Scale(scale, scale);
        foreach (var element in result.Elements)
        {
            element.Transform = transform.Compose(element.Transform);
            // Live block sizes are in output document pixels. Scale their cloned
            // sampling grid with the preview/export, as well as their mask bounds.
            if (ElementGeometry.IsLiveEffect(element.Kind))
                foreach (var effect in element.Effects) effect.Amount *= scale;
        }
        if(result.Background is { } background)background.Transform=transform.Compose(background.Transform);
        result.Viewport = new(0, 0, Math.Clamp(document.Viewport.Width * scale, 1, 4096), Math.Clamp(document.Viewport.Height * scale, 1, 4096));
        return result;
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120) throw new ArgumentException(L.Get("Stamp.EmptyName"), nameof(name));
        return name;
    }
    private void EnsureLoaded() { if (!loaded) throw new InvalidOperationException(L.Get("Stamp.LoadFirst")); }
    private StampEntry Find(Guid id) => catalog.Items.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException(L.Get("Stamp.Missing"));
    private string ResourceBase(StampEntry entry) => Path.Combine(DirectoryPath, entry.Id.ToString("N") + "-" + entry.Revision.ToString("N"));
    private string PackagePath(StampEntry entry) => ResourceBase(entry) + ".floatcanvas";
    private string SourcePath(StampEntry entry) => ResourceBase(entry) + ".source.floatcanvas";
    private string ThumbnailPath(StampEntry entry) => ResourceBase(entry) + ".png";
    private void DeleteRevision(StampEntry entry)
    {
        // Only filenames derived from this exact immutable revision can be removed.
        foreach (string path in new[] { PackagePath(entry), SourcePath(entry), ThumbnailPath(entry) })
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
