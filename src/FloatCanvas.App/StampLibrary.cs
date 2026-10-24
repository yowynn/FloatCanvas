using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal enum StampSaveMode { EditableGroup, RasterMerge }
internal sealed record StampRenderContext(SceneDocument Document, AssetLibrary Assets, TimeSpan AnimationTime = default,
    CapturedDesktop? ScreenCapture = null);
internal sealed record StampEntry(Guid Id, string Name, StampSaveMode Mode, double Width, double Height, DateTime UpdatedAtUtc, Guid Revision);

/// <summary>Immutable, self-contained stamp revisions published by one atomic catalog replacement.</summary>
internal sealed class StampLibrary(string profileDirectory)
{
    private sealed class Catalog
    {
        public int Version { get; set; } = 1;
        public List<StampEntry> Items { get; set; } = [];
        public Guid?[] Pins { get; set; } = new Guid?[10];
        public List<Guid>? Order { get; set; }
        public Catalog Clone() => new() { Version = Version, Items = [.. Items], Pins = (Guid?[])Pins.Clone(), Order = Items.Select(item => item.Id).ToList() };
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
    internal Action<string>? PublicationCheckpoint { get; set; }

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
                        content.Pins.Any(id => id.HasValue && content.Items.All(item => item.Id != id.Value)) ||
                        content.Order is { } order && (order.Count != content.Items.Count || order.Distinct().Count() != order.Count || order.Any(id => content.Items.All(item => item.Id != id)))) throw new InvalidDataException();
                    // Older catalogs displayed newest first. Preserve that initial presentation,
                    // then persist the user's explicit order independently of edits and renames.
                    content.Items = content.Order is { } savedOrder
                        ? savedOrder.Select(id => content.Items.Single(item => item.Id == id)).ToList()
                        : content.Items.OrderByDescending(item => item.UpdatedAtUtc).ToList();
                    catalog = content;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
                { throw new InvalidDataException(L.Get("Stamp.InvalidLibrary") + CatalogPath, ex); }
            }
            PublicationCleanup.Sweep(DirectoryPath, catalog.Items.SelectMany(item => new[] { PackagePath(item), SourcePath(item), ThumbnailPath(item) }));
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
        var original = session.CreateSelectionSnapshot(normalizeOrigin: false);
        var originalBounds = original.Viewport;
        var mapping = CanvasMapping.Create(session.Document);
        bool live = source.Elements.Any(e => ElementGeometry.IsLiveEffect(e.Kind));
        bool hasBillboards = original.Elements.Any(element => element.IsBillboard);
        bool projectedLive = hasBillboards || live && mapping.PlaneToScreen != Projective2D.Identity;
        SceneDocument? screenSource = null;
        if (projectedLive)
        {
            screenSource = original.Clone(); screenSource.Canvas = session.Document.Canvas.Clone();
            screenSource.Canvas.Grid.Visible = false; screenSource.Canvas.BackgroundColor = 0;
            var bounds = screenSource.Elements.Select(e => SceneRenderer.ProjectedBounds(session.Document, e, true))
                .Aggregate(RectD.Union).Intersect(session.Document.Viewport);
            screenSource.Viewport = new(Math.Floor(bounds.Left), Math.Floor(bounds.Top),
                Math.Ceiling(bounds.Right) - Math.Floor(bounds.Left), Math.Ceiling(bounds.Bottom) - Math.Floor(bounds.Top));
        }
        SceneDocument? context=null;
        if(renderContext is not null)
        {
            context=renderContext.Document.Clone();
            if (!projectedLive)
            {
                var shift=Affine2D.Translation(-originalBounds.X,-originalBounds.Y);
                foreach(var item in context.Elements)BillboardGeometry.ApplyPlaneTransform(item, shift);
                if(context.Background is { } background)background.Transform=shift.Compose(background.Transform);
                context.Viewport=source.Viewport;
            }
        }
        // Capture original encoded bytes before yielding; UI pruning cannot invalidate this save.
        var sourceAssets = new AssetLibrary();
        foreach (string id in source.ReferencedAssets)
        {
            var resource = session.Assets.Get(id);
            sourceAssets.Add(resource);
        }
        await gate.WaitAsync(ct);
        StampEntry entry;
        string? publication = null; bool published = false;
        StampEntry? previous = null;
        try
        {
            EnsureLoaded();
            if (replaceId is { } replace) previous = Find(replace);
            entry = new(replaceId ?? Guid.NewGuid(), name, mode, source.Viewport.Width, source.Viewport.Height, DateTime.UtcNow, Guid.NewGuid());
            publication = PublicationCleanup.Begin(DirectoryPath, new[] { PackagePath(entry), SourcePath(entry), ThumbnailPath(entry) });
            var rendered = await Task.Run(() =>
            {
                using var renderer = new SceneRenderer { AnimationTime = renderContext?.AnimationTime ?? TimeSpan.Zero };
                if (screenSource is not null)
                {
                    using var screenBackdrop = StampBackdrop(renderContext?.ScreenCapture, default, 1);
                    renderer.ScreenBackdrop = screenBackdrop; renderer.LiveEffectContext = context;
                    byte[] screenPixels = renderer.ExportPng(screenSource, renderContext?.Assets ?? sourceAssets, transparent: true);
                    return (preview: Thumbnail(screenPixels), merged: mode == StampSaveMode.RasterMerge ? screenPixels : null,
                        screenBounds: (RectD?)screenSource.Viewport);
                }
                byte[] Render(double scale)
                {
                    renderer.LiveEffectContext=context is null ? null : Scaled(context,scale);
                    using var backdrop = StampBackdrop(renderContext?.ScreenCapture, new(originalBounds.X, originalBounds.Y), scale);
                    renderer.ScreenBackdrop = backdrop;
                    return renderer.ExportPng(Scaled(source,scale),renderContext?.Assets ?? sourceAssets,transparent:true,
                        live ? SceneRenderSpace.ScreenComposite : SceneRenderSpace.PlaneContent);
                }
                byte[] preview = Render(160 / Math.Max(source.Viewport.Width, source.Viewport.Height));
                byte[]? merged = mode == StampSaveMode.RasterMerge
                    ? Render(Math.Min(2, 4096 / Math.Max(source.Viewport.Width, source.Viewport.Height)))
                    : null;
                return (preview, merged, screenBounds: (RectD?)null);
            }, ct);
            SceneDocument document = source;
            AssetLibrary assets = sourceAssets;
            if (rendered.merged is { } png)
            {
                assets = new();
                CanvasElement image;
                if (rendered.screenBounds is { } screenBounds)
                {
                    if (hasBillboards)
                    {
                        // Flatten the current screen composition once. A raster stamp is
                        // subsequently inserted as a fresh image, without hidden child depths.
                        image = new() { Kind = ElementKind.Image, AssetId = assets.Add(png),
                            Width = screenBounds.Width, Height = screenBounds.Height, Style = new() { StrokeWidth = 0 } };
                    }
                    else
                    {
                        image = SnapshotImageCommand.CreateElement(assets, new(png, screenBounds, true), mapping.PlaneToScreen);
                        image.Transform = Affine2D.Translation(-originalBounds.X, -originalBounds.Y).Compose(image.Transform);
                    }
                }
                else image = new() { Kind = ElementKind.Image, AssetId = assets.Add(png), Width = source.Viewport.Width, Height = source.Viewport.Height };
                document = new() { Viewport = hasBillboards && rendered.screenBounds is { } screen
                    ? new(0, 0, screen.Width, screen.Height) : source.Viewport, Elements = [image] };
                if (hasBillboards) entry = entry with { Width = document.Viewport.Width, Height = document.Viewport.Height };
                // A raster stamp still retains the original editable project and encoded image bytes.
                await projects.SaveAsync(SourcePath(entry), source, sourceAssets, ct);
                PublicationCheckpoint?.Invoke("source");
            }
            await projects.SaveAsync(PackagePath(entry), document, assets, ct);
            PublicationCheckpoint?.Invoke("package");
            await WriteAtomicallyAsync(ThumbnailPath(entry), rendered.preview, ct);
            PublicationCheckpoint?.Invoke("thumbnail");
            var next = catalog.Clone();
            if (previous is null)
            {
                next.Items.Insert(0, entry);
                int emptySlot = Array.FindIndex(next.Pins, id => id is null);
                if (emptySlot >= 0) next.Pins[emptySlot] = entry.Id;
            }
            else next.Items[next.Items.FindIndex(item => item.Id == previous.Id)] = entry;
            PublicationCheckpoint?.Invoke("before-catalog");
            await PublishAsync(next, ct); published = true;
            PublicationCheckpoint?.Invoke("published");
        }
        finally { PublicationCleanup.Finish(publication, published); gate.Release(); }
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
                ImageImportBudget.ValidateResources(stamp.Assets.Resources);
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
            try { var stamp = await projects.LoadAsync(entry.Mode == StampSaveMode.RasterMerge ? SourcePath(entry) : PackagePath(entry), ct); ImageImportBudget.ValidateResources(stamp.Assets.Resources); return stamp; }
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
        var transform = Affine2D.Translation(center.X - origin.X, center.Y - origin.Y);
        var mapping = CanvasMapping.Create(target.Document);
        foreach (var source in stamp.Document.Elements)
        {
            var candidate = source.Clone(); BillboardGeometry.ApplyPlaneTransform(candidate, transform);
            if (!mapping.IsElementSafe(candidate)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        }
        target.InsertDocument(stamp.Document, stamp.Assets, transform, group: true);
        target.SetSelection([]);
    }

    public static void PlaceBillboardRaster(LoadedProject stamp, EditorSession target, BillboardDepthLine depth, PointD screenCenter)
    {
        if (stamp.Document.Elements is not [ { Kind: ElementKind.Image, AssetId: { } assetId } source ])
            throw new InvalidDataException(L.Get("Stamp.InvalidLibrary"));
        var bounds = new RectD(screenCenter.X - source.Width / 2, screenCenter.Y - source.Height / 2,
            source.Width, source.Height);
        var element = SnapshotImageCommand.CreateBillboardElement(target.Assets,
            new(stamp.Assets.Get(assetId).Data.ToArray(), bounds, true), CanvasMapping.Create(target.Document), depth);
        target.Add(element, L.Get("History.InsertPreset"));
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

    public Task MoveAsync(Guid id, Guid targetId, bool after = false, CancellationToken ct = default) => MutateAsync(next =>
    {
        var moving = next.Items.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException(L.Get("Stamp.Missing"));
        var target = next.Items.FirstOrDefault(item => item.Id == targetId) ?? throw new KeyNotFoundException(L.Get("Stamp.Missing"));
        if (id == targetId) return;
        next.Items.Remove(moving);
        next.Items.Insert(next.Items.IndexOf(target) + (after ? 1 : 0), moving);
    }, ct);

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
        next.Order = next.Items.Select(item => item.Id).ToList();
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
            BillboardGeometry.ApplyPlaneTransform(element, transform);
            if (element.IsBillboard) element.BillboardHeight *= scale;
            // Live block sizes are in output document pixels. Scale their cloned
            // sampling grid with the preview/export, as well as their mask bounds.
            if (ElementGeometry.IsLiveEffect(element.Kind))
                foreach (var effect in element.Effects) effect.Amount *= scale;
        }
        if(result.Background is { } background)background.Transform=transform.Compose(background.Transform);
        result.Viewport = new(0, 0, Math.Clamp(document.Viewport.Width * scale, 1, 4096), Math.Clamp(document.Viewport.Height * scale, 1, 4096));
        return result;
    }

    private static DesktopBackdrop? StampBackdrop(CapturedDesktop? capture, PointD origin, double scale) => capture is null ? null :
        DesktopBackdrop.FromEncoded(new((capture.Bounds.Left - origin.X) * scale, (capture.Bounds.Top - origin.Y) * scale,
            capture.Bounds.Width * scale, capture.Bounds.Height * scale), capture.PngBytes);

    private static byte[] Thumbnail(byte[] png)
    {
        _ = ImageDecodeBudget.ReadPixelCount(png);
        using var image = SKImage.FromEncodedData(png) ?? throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
        double scale = 160d / Math.Max(image.Width, image.Height);
        using var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, (int)Math.Ceiling(image.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(image.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, surface.Canvas.DeviceClipBounds.Width, surface.Canvas.DeviceClipBounds.Height),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var result = surface.Snapshot(); using var encoded = result.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
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
