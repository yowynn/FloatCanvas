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

internal enum StampSaveMode { EditableGroup, RasterMerge, SharedPrefab }
internal enum StampPackageFormat { LegacyProject, Prefab }
internal sealed record LoadedStamp(SceneDocument Document, AssetLibrary Assets, PrefabPackage? SharedPrefab = null, string? Name = null,
    RectD? ContentBounds = null)
{
    public static implicit operator LoadedStamp(LoadedProject project) => new(project.Document, project.Assets);
    public static implicit operator LoadedProject(LoadedStamp stamp) => new(stamp.Document, stamp.Assets);
}
internal sealed record StampRenderContext(SceneDocument Document, AssetLibrary Assets, TimeSpan AnimationTime = default,
    CapturedDesktop? ScreenCapture = null, PointD ScreenOrigin = default, bool WindowFallbacksPrepared = false);
internal sealed record StampEntry(Guid Id, string Name, StampSaveMode Mode, double Width, double Height, DateTime UpdatedAtUtc, Guid Revision,
    StampPackageFormat? Format = null);

/// <summary>Immutable, self-contained stamp revisions published by one atomic catalog replacement.</summary>
internal sealed class StampLibrary(string profileDirectory)
{
    private sealed class Catalog
    {
        public int Version { get; set; } = 3;
        public List<StampEntry> Items { get; set; } = [];
        public Guid?[] Pins { get; set; } = new Guid?[10];
        public List<Guid>? Order { get; set; }
        public Catalog Clone() => new() { Version = Version, Items = [.. Items], Pins = (Guid?[])Pins.Clone(), Order = Items.Select(item => item.Id).ToList() };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ProjectStore projects = new();
    private readonly PrefabStore prefabs = new();
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
                    if (content is null || content.Version is not (1 or 2 or 3) || content.Items is null || content.Pins is not { Length: 10 } ||
                        content.Items.Any(item => item is null || item.Id == Guid.Empty || item.Revision == Guid.Empty ||
                            string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 120 || !Enum.IsDefined(item.Mode) || content.Version == 1 && item.Mode == StampSaveMode.SharedPrefab ||
                            item.Format is { } format && (content.Version < 3 || !Enum.IsDefined(format) || format == StampPackageFormat.LegacyProject && item.Mode == StampSaveMode.SharedPrefab) ||
                            !double.IsFinite(item.Width) || !double.IsFinite(item.Height) || item.Width <= 0 || item.Height <= 0) ||
                        content.Items.Select(item => item.Id).Distinct().Count() != content.Items.Count ||
                        content.Pins.Any(id => id.HasValue && content.Items.All(item => item.Id != id.Value)) ||
                        content.Order is { } order && (order.Count != content.Items.Count || order.Distinct().Count() != order.Count || order.Any(id => content.Items.All(item => item.Id != id)))) throw new InvalidDataException();
                    // Older catalogs displayed newest first. Preserve that initial presentation,
                    // then persist the user's explicit order independently of edits and renames.
                    content.Items = content.Order is { } savedOrder
                        ? savedOrder.Select(id => content.Items.Single(item => item.Id == id)).ToList()
                        : content.Items.OrderByDescending(item => item.UpdatedAtUtc).ToList();
                    for (int i = 0; i < content.Items.Count; i++)
                    {
                        var entry = content.Items[i];
                        if (entry.Format is null)
                            content.Items[i] = entry with { Format = entry.Mode == StampSaveMode.SharedPrefab
                                ? StampPackageFormat.Prefab : StampPackageFormat.LegacyProject };
                    }
                    content.Version = 3;
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
        var original = session.CreateSelectionSnapshot(normalizeOrigin: false);
        var previewCanvas = session.BuildSelectionPreviewSettings(source);
        var preparedAssets = session.Assets;
        if (renderContext is { WindowFallbacksPrepared: true })
        {
            ProjectWindowFallbacks(source, renderContext.Document);
            ProjectWindowFallbacks(original, renderContext.Document);
            preparedAssets = new();
            foreach (var resource in session.Assets.Resources.Concat(renderContext.Assets.Resources)) preparedAssets.Add(resource);
        }
        if (mode != StampSaveMode.SharedPrefab)
        {
            source = Independent(source, preparedAssets); original = Independent(original, preparedAssets);
        }
        if (source.Elements.Count == 0 && (mode != StampSaveMode.SharedPrefab || source.Groups.Count == 0 && source.Instances.Count == 0)) throw new InvalidOperationException(L.Get("Stamp.NoSelection"));
        var originalBounds = original.Viewport;
        var mapping = CanvasMapping.Create(session.Document, session.ActiveRootCanvas);
        var worldSource = SceneEvaluation.Build(source).RenderDocument;
        var worldOriginal = SceneEvaluation.Build(original).RenderDocument;
        bool live = worldSource.Elements.Any(e => ElementGeometry.IsLiveEffect(e.Kind));
        bool hasBillboards = worldOriginal.Elements.Any(element => element.IsBillboard);
        bool projectedLive = hasBillboards || live && mapping.PlaneToScreen != Projective2D.Identity;
        SceneDocument? screenSource = null;
        if (projectedLive && (live || mode == StampSaveMode.RasterMerge))
        {
            screenSource = worldOriginal; screenSource.Canvas = session.Document.Canvas.Clone();
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
            context.Canvas.Grid.Visible = false;
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
        foreach (string id in source.ReferencedAssets.Concat(previewCanvas.ReferencedAssetIds).Distinct())
        {
            var resource = preparedAssets.Get(id);
            sourceAssets.Add(resource);
        }
        var renderAssets = renderContext is { WindowFallbacksPrepared: true } ? preparedAssets : renderContext?.Assets ?? sourceAssets;
        await gate.WaitAsync(ct);
        StampEntry entry;
        string? publication = null; bool published = false;
        StampEntry? previous = null;
        try
        {
            EnsureLoaded();
            if (replaceId is { } replace) previous = Find(replace);
            entry = new(replaceId ?? Guid.NewGuid(), name, mode, source.Viewport.Width, source.Viewport.Height, DateTime.UtcNow, Guid.NewGuid(), StampPackageFormat.Prefab);
            publication = PublicationCleanup.Begin(DirectoryPath, new[] { PackagePath(entry), SourcePath(entry), ThumbnailPath(entry) });
            var rendered = await Task.Run(() =>
            {
                using var renderer = new SceneRenderer { AnimationTime = renderContext?.AnimationTime ?? TimeSpan.Zero };
                if (screenSource is not null)
                {
                    using var screenBackdrop = StampBackdrop(renderContext?.ScreenCapture, renderContext?.ScreenOrigin ?? default, 1);
                    renderer.ScreenBackdrop = screenBackdrop; renderer.LiveEffectContext = context;
                    byte[] screenPixels = renderer.ExportPng(screenSource, renderAssets, transparent: true, useAuthoredFrame: false);
                    return (preview: Thumbnail(screenPixels), merged: mode == StampSaveMode.RasterMerge ? screenPixels : null,
                        screenBounds: (RectD?)screenSource.Viewport);
                }
                byte[] Render(double scale)
                {
                    renderer.LiveEffectContext=context is null ? null : Scaled(context,scale);
                    using var backdrop = StampBackdrop(renderContext?.ScreenCapture,
                        new PointD(originalBounds.X, originalBounds.Y) + (renderContext?.ScreenOrigin ?? default), scale);
                    renderer.ScreenBackdrop = backdrop;
                    var local = Scaled(source, scale);
                    local.Canvas.Grid.Visible = false;
                    // A stamp preview owns local ink, independent of the author's screen-root slot.
                    local.RootCanvasAssignments.Clear();
                    return renderer.ExportPng(local,renderAssets,transparent:true,
                        live ? SceneRenderSpace.ScreenComposite : SceneRenderSpace.PlaneContent, useAuthoredFrame: false);
                }
                byte[] preview = live ? Render(160 / Math.Max(source.Viewport.Width, source.Viewport.Height)) : [];
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
                await prefabs.SaveAsync(SourcePath(entry), SharedPackage(source, sourceAssets, name, entry.Id, previewCanvas), sourceAssets, ct);
                PublicationCheckpoint?.Invoke("source");
            }
            var packagePreview = hasBillboards && rendered.merged is not null
                ? new SubprojectCanvasSettings { Viewport = document.Viewport }
                : previewCanvas;
            if (!ReferenceEquals(assets, sourceAssets))
                foreach (string id in packagePreview.ReferencedAssetIds) assets.Add(sourceAssets.Get(id));
            await prefabs.SaveAsync(PackagePath(entry), SharedPackage(document, assets, name, entry.Id, packagePreview), assets, ct);
            PublicationCheckpoint?.Invoke("package");
            byte[] thumbnail = live ? rendered.preview : await Task.Run(() => RenderPreview(document, assets, packagePreview), ct);
            await WriteAtomicallyAsync(ThumbnailPath(entry), thumbnail, ct);
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

    public async Task<LoadedStamp> LoadStampAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            var entry = Find(id);
            try
            {
                if (UsesPrefab(entry)) return PrefabView(await prefabs.LoadAsync(PackagePath(entry), ct), entry, shared: entry.Mode == StampSaveMode.SharedPrefab);
                var stamp = await projects.LoadAsync(PackagePath(entry), ct);
                ImageImportBudget.ValidateResources(stamp.Assets.Resources);
                if (stamp.Document.Elements.Count == 0) throw new InvalidDataException();
                return new(stamp.Document, stamp.Assets, Name: entry.Name, ContentBounds: stamp.Document.Viewport);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or NotSupportedException)
            { throw new InvalidDataException(L.Get("Stamp.Corrupt") + entry.Name, ex); }
        }
        finally { gate.Release(); }
    }

    public async Task<LoadedStamp> LoadSourceAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            var entry = Find(id);
            try
            {
                if (UsesPrefab(entry))
                    return PrefabView(await prefabs.LoadAsync(entry.Mode == StampSaveMode.RasterMerge ? SourcePath(entry) : PackagePath(entry), ct),
                        entry, shared: entry.Mode == StampSaveMode.SharedPrefab);
                var stamp = await projects.LoadAsync(entry.Mode == StampSaveMode.RasterMerge ? SourcePath(entry) : PackagePath(entry), ct);
                ImageImportBudget.ValidateResources(stamp.Assets.Resources); return new(stamp.Document, stamp.Assets, Name: entry.Name, ContentBounds: stamp.Document.Viewport);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or NotSupportedException)
            { throw new InvalidDataException(L.Get("Stamp.Corrupt") + entry.Name, ex); }
        }
        finally { gate.Release(); }
    }

    public async Task PlaceAsync(Guid id, EditorSession target, PointD center, CancellationToken ct = default) =>
        Place(await LoadStampAsync(id, ct), target, center);

    public static void Place(LoadedStamp stamp, EditorSession target, PointD center, string? name = null, ContainerBillboardInsertion? billboard = null)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)) throw new ArgumentOutOfRangeException(nameof(center));
        string? placementName = name ?? stamp.Name;
        var origin = (stamp.ContentBounds ?? stamp.Document.Viewport).Center;
        var transform = Affine2D.Translation(center.X - origin.X, center.Y - origin.Y);
        if (stamp.SharedPrefab is { } package)
        {
            var entry = package.Definitions.Single(definition => definition.Id == package.EntryDefinitionId);
            var existing = target.Document.Definitions.FirstOrDefault(definition => definition.Source is { IsLinked: false } source &&
                source.SourceResourceId == package.ResourceId && source.EntryDefinitionId == package.EntryDefinitionId && source.LastAcceptedContentRevision == entry.Revision);
            if (existing is not null) target.PlacePrefab(existing.Id, new(transform, 1), name: placementName, select: false, billboard: billboard);
            else target.ImportPrefab(package, stamp.Assets, new(transform, 1), new ExternalPrefabSource
            {
                SourceResourceId = package.ResourceId, EntryKind = PrefabSourceKind.PrefabDefinition,
                EntryDefinitionId = package.EntryDefinitionId, LastAcceptedContentRevision = entry.Revision, ImportOrigin = entry.Pivot
            }, select: false, name: placementName, billboard: billboard);
            target.SetSelection([]); return;
        }
        var mapping = CanvasMapping.Create(target.Document, target.ActiveRootCanvas);
        foreach (var source in billboard is null ? SceneEvaluation.Build(stamp.Document).RenderDocument.Elements : [])
        {
            var candidate = source.Clone(); BillboardGeometry.ApplyPlaneTransform(candidate, transform);
            if (!mapping.IsElementSafe(candidate)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        }
        target.InsertDocument(stamp.Document, stamp.Assets, transform, group: true, groupName: placementName, billboard: billboard);
        target.SetSelection([]);
    }

    public static void PlaceBillboardRaster(LoadedStamp stamp, EditorSession target, BillboardDepthLine? depth, PointD screenCenter, string? name = null,
        CanvasMapping? placementMapping = null, RectD? logicalCanvasBounds = null)
    {
        target.RequireWritableScope();
        if (target.IsTransactionActive) throw new InvalidOperationException(L.Get("Text.ActionInProgress"));
        if (stamp.Document.Elements is not [ { Kind: ElementKind.Image, AssetId: { } assetId } source ])
            throw new InvalidDataException(L.Get("Stamp.InvalidLibrary"));
        var bounds = new RectD(screenCenter.X - source.Width / 2, screenCenter.Y - source.Height / 2,
            source.Width, source.Height);
        var mapping = placementMapping ?? CanvasMapping.Create(target.Document, target.ActiveRootCanvas);
        var element = depth is null ? BillboardImagePlacement.Prepare(target, mapping, source.Width, source.Height, screenCenter, logicalCanvasBounds)
            : SnapshotImageCommand.PrepareBillboardElement(new([], bounds, true), mapping, depth);
        element.AssetId = target.Assets.Add(stamp.Assets.Get(assetId).Data.ToArray());
        element.Name = name ?? stamp.Name;
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
        next.Version = 3;
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
        var result = SceneEvaluation.Build(document).RenderDocument;
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
    private static SceneDocument Independent(SceneDocument document, AssetLibrary assets)
    {
        if (document.Instances.Count == 0) return document;
        var session = new EditorSession(document, assets);
        foreach (var instance in document.Instances.Select(instance => instance.Id).ToArray()) session.DetachPrefab(instance);
        return session.Document;
    }
    // Project before Independent remaps definition members into new authored nodes.
    // This is package-local data; ordinary library previews only read its static PNGs.
    private static void ProjectWindowFallbacks(SceneDocument target, SceneDocument prepared)
    {
        if (target.ResourceId != prepared.ResourceId) throw new InvalidDataException("Stamp media belongs to a different resource owner.");
        var replacements = Windows(prepared).ToDictionary(pair => pair.Address, pair => pair.Element);
        foreach (var pair in Windows(target))
            if (replacements.TryGetValue(pair.Address, out var original) &&
                pair.Element.WindowSource!.BindingIntent == original.WindowSource!.BindingIntent)
                pair.Element.AssetId = original.AssetId;
        static IEnumerable<(SourceNodeAddress Address, CanvasElement Element)> Windows(SceneDocument document) =>
            document.Elements.Where(element => element.WindowSource is not null).Select(element => (new SourceNodeAddress(null, element.Id), element))
                .Concat(document.Definitions.SelectMany(definition => definition.Graph.Elements.Where(element => element.WindowSource is not null)
                    .Select(element => (new SourceNodeAddress(definition.Id, element.Id), element))));
    }
    private static byte[] RenderPreview(SceneDocument document, AssetLibrary assets, SubprojectCanvasSettings settings)
    {
        var preview = document.Clone(); preview.RootCanvasAssignments.Clear(); settings.ApplyTo(preview);
        using var renderer = new SceneRenderer();
        return StampPreviewScene.Render(renderer, StampPreviewScene.Prepare(preview), assets, 160);
    }

    private static PrefabPackage SharedPackage(SceneDocument document, AssetLibrary assets, string name, Guid resourceId,
        SubprojectCanvasSettings preview)
    {
        var local = document.Clone(); local.RootCanvasAssignments.Clear();
        var entry = new PrefabDefinition { Name = name, Graph = SceneGraphClone.CloneGraph(local).Graph, PreviewCanvas = preview.Clone() };
        local.Definitions.Add(entry);
        var session = new EditorSession(local, assets);
        var package = session.ExportPrefab(entry.Id);
        package.ResourceId = resourceId; return package;
    }
    private static LoadedStamp PrefabView(LoadedPrefab loaded, StampEntry entry, bool shared)
    {
        ImageImportBudget.ValidateResources(loaded.Assets.Resources);
        var definition = loaded.Package.Definitions.Single(item => item.Id == loaded.Package.EntryDefinitionId);
        var content = SceneGraphClone.CloneGraph(definition.Graph).Graph;
        var bounds = new RectD(0, 0, entry.Width, entry.Height);
        var document = new SceneDocument
        {
            ResourceId = loaded.Package.ResourceId, Elements = shared ? [] : content.Elements,
            Groups = shared ? [] : content.Groups, Instances = shared ? [] : content.Instances,
            RootOrder = shared ? null : content.RootOrder, Viewport = bounds,
            Definitions = loaded.Package.Definitions.Where(item => shared || item.Id != definition.Id).Select(item => item.Clone()).ToList()
        };
        if (shared) new SceneGraph(document).InsertInstance(new() { DefinitionId = definition.Id });
        definition.PreviewCanvas?.ApplyTo(document);
        return new(document, loaded.Assets, shared ? loaded.Package : null, entry.Name, bounds);
    }
    private static bool UsesPrefab(StampEntry entry) => entry.Format == StampPackageFormat.Prefab;
    private string PackagePath(StampEntry entry) => ResourceBase(entry) + (UsesPrefab(entry) ? ".floatprefab" : ".floatcanvas");
    private string SourcePath(StampEntry entry) => ResourceBase(entry) + (entry.Format == StampPackageFormat.Prefab ? ".source.floatprefab" : ".source.floatcanvas");
    private string ThumbnailPath(StampEntry entry) => ResourceBase(entry) + ".png";
    private void DeleteRevision(StampEntry entry)
    {
        // Only filenames derived from this exact immutable revision can be removed.
        foreach (string path in new[] { PackagePath(entry), SourcePath(entry), ThumbnailPath(entry) })
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
