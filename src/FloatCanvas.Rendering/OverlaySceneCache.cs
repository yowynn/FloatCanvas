using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using System.Numerics;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>
/// Single-thread raster cache for translation-only monitor tiles. Revision and transaction stamps are supplied
/// by the editor. Static prefix/suffix remain in z order around one continuous dynamic span.
/// Does not own the supplied SceneRenderer. Export continues to use the uncached renderer.
/// </summary>
public sealed class OverlaySceneCache(SceneRenderer renderer) : IDisposable
{
    private readonly Dictionary<TileKey, TileEntry> tiles = [];
    private ContentKey? content;
    private RootContentKey? rootContent;
    private Dictionary<Guid, RootCanvasKind>? rootAssignments;
    private long clock, byteLimit = 128L * 1024 * 1024;
    private bool disposed;
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);
    public long RasterCacheByteLimit
    {
        get => byteLimit;
        set { ObjectDisposedException.ThrowIf(disposed, this); byteLimit = Math.Max(0, value); EvictUntilFits(0); }
    }
    public long CachedRasterBytes { get; private set; }
    /// <summary>Number of actual prefix/suffix raster surfaces built, not frame count.</summary>
    public long BuildCount { get; private set; }
    /// <summary>Number of tile lookups served without rebuilding either static segment.</summary>
    public long HitCount { get; private set; }
    public long InPlaceOpacityFrameCount { get; private set; }

    /// <summary>
    /// Renders a physical device-clip tile under a translation-only matrix. The caller must advance
    /// sceneRevision after every committed content change, and transactionId on gesture boundaries.
    /// During a gesture, every mutable element must be inside the inclusive dynamic span; an unknown
    /// mutation or object removal must set bypassCache. Selection-only changes do not need a new revision.
    /// </summary>
    public void Draw(SKCanvas canvas, SceneDocument document, AssetLibrary assets, long sceneRevision,
        long transactionId, int dynamicFirst = -1, int dynamicLast = -1, bool bypassCache = false)
        => DrawCore(canvas, document, assets, sceneRevision, transactionId, dynamicFirst, dynamicLast, bypassCache, false);

    /// <summary>
    /// Draws on an exclusively owned CPU raster surface that the caller has just cleared completely
    /// to transparent. No active SaveLayer or existing destination content is allowed. Draw editor
    /// controls afterward. Live BGRA premultiplied tiles can apply group opacity directly to these
    /// pixels with the same byte rounding as SaveLayer; all other cases use the ordinary Draw path.
    /// The caller must not snapshot or access the surface concurrently with this call.
    /// An optional stable sceneIdentity allows detached snapshots to reuse cached static spans.
    /// Its revision, transaction and dynamic-span contract must match the ordinary Draw contract.
    /// </summary>
    public void DrawOnClearedRasterTarget(SKSurface surface, SceneDocument document, AssetLibrary assets, long sceneRevision,
        long transactionId, int dynamicFirst = -1, int dynamicLast = -1, bool bypassCache = false, object? sceneIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(assets);
        bool eligible = document.Kind == DocumentKind.LiveOverlay && document.Background is null &&
            (document.Canvas.BackgroundColor >> 24) == 0 && renderer.ScreenBackdrop is null &&
            document.LayerOpacity > 0 && document.LayerOpacity < 1;
        bool rootTarget = SceneRenderer.HasAuxiliaryRoots(document);
        if (eligible || rootTarget)
        {
            using var format = surface.PeekPixels();
            bool supported = format is not null &&
                format.ColorType == SKColorType.Bgra8888 && format.AlphaType == SKAlphaType.Premul;
            eligible &= supported;
            rootTarget &= supported && format!.ColorSpace is null;
        }
        if (!DrawCore(surface.Canvas, document, assets, sceneRevision, transactionId, dynamicFirst, dynamicLast, bypassCache, eligible, sceneIdentity, clearedRaster: rootTarget)) return;
        surface.Canvas.Flush();
        using var pixels = surface.PeekPixels() ?? throw new InvalidOperationException(L.Get("Overlay.RasterTargetUnreadable"));
        // The preceding caller Clear and scene draw have already detached any older snapshots.
        // Do not replace this with DstIn: its round(c*a/255) differs from SaveLayer by one byte.
        int multiplier = (byte)Math.Round(document.LayerOpacity * 255) + 1;
        var factor = new Vector<ushort>((ushort)multiplier);
        for (int y = 0; y < pixels.Height; y++)
        {
            var row = pixels.GetPixelSpan().Slice(y * pixels.RowBytes, pixels.Width * 4);
            int x = 0;
            if (Vector.IsHardwareAccelerated)
                for (; x <= row.Length - Vector<byte>.Count; x += Vector<byte>.Count)
                {
                    Vector.Widen(new Vector<byte>(row.Slice(x)), out Vector<ushort> low, out Vector<ushort> high);
                    Vector.Narrow((low * factor) >> 8, (high * factor) >> 8).CopyTo(row.Slice(x));
                }
            for (; x < row.Length; x++) row[x] = (byte)(row[x] * multiplier >> 8);
        }
        InPlaceOpacityFrameCount++;
    }

    // Returns true only when the cache composed raw annotation alpha for the raster caller.
    private bool DrawCore(SKCanvas canvas, SceneDocument document, AssetLibrary assets, long sceneRevision,
        long transactionId, int dynamicFirst, int dynamicLast, bool bypassCache, bool inPlaceOpacity, object? sceneIdentity = null, bool clearedRaster = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(assets);
        var source = document;
        document = SceneRenderer.WorldDocument(document);
        if (!ReferenceEquals(source, document))
        {
            sceneIdentity ??= source;
            if (dynamicFirst >= 0 && dynamicLast >= dynamicFirst && dynamicLast < source.Elements.Count)
            {
                var selected = source.Elements.Skip(dynamicFirst).Take(dynamicLast - dynamicFirst + 1).Select(element => element.Id).ToHashSet();
                var indices = document.Elements.Select((element, index) => (element, index)).Where(pair => selected.Contains(pair.element.Id)).Select(pair => pair.index).ToArray();
                dynamicFirst = indices.Length == 0 ? -1 : indices.Min(); dynamicLast = indices.Length == 0 ? -1 : indices.Max();
            }
        }
        bool suppliedRangeValid = dynamicFirst >= 0 && dynamicLast >= dynamicFirst && dynamicLast < document.Elements.Count;
        bool suppliedRangeInvalid = !suppliedRangeValid && (dynamicFirst != -1 || dynamicLast != -1);
        var presentation = renderer.PreparePresentation(document);
        if (presentation.DynamicFirst >= 0)
        {
            dynamicFirst = dynamicFirst >= 0 ? Math.Min(dynamicFirst, presentation.DynamicFirst) : presentation.DynamicFirst;
            dynamicLast = Math.Max(dynamicLast, presentation.DynamicLast);
        }
        var windows = renderer.PrepareWindowFrames(document);
        if (windows.DynamicFirst >= 0)
        {
            dynamicFirst = dynamicFirst >= 0 ? Math.Min(dynamicFirst, windows.DynamicFirst) : windows.DynamicFirst;
            dynamicLast = Math.Max(dynamicLast, windows.DynamicLast);
        }
        bool dynamic = dynamicFirst >= 0 && dynamicLast >= dynamicFirst && dynamicLast < document.Elements.Count;
        bool invalidRange = suppliedRangeInvalid || !dynamic && (dynamicFirst != -1 || dynamicLast != -1);
        if (SceneRenderer.HasAuxiliaryRoots(document))
        {
            // Static screen roots need their complete B/Main/F composition, rather
            // than the main-plane prefix/suffix rasterizer below. Dynamic content
            // continues to use the renderer until its own invalidation is cacheable.
            if (!clearedRaster || canvas.Context is not null || bypassCache || invalidRange || dynamic || byteLimit == 0 ||
                renderer.ScreenBackdrop is not null || renderer.LiveEffectContext is not null || renderer.PresentedImages is not null ||
                renderer.PresentationFrame is not null || renderer.WindowFrames is not null || SceneRenderer.HasLiveEffects(document) ||
                document.Elements.Any(element => element.Kind == ElementKind.Presentation || element.WindowSource is not null) ||
                renderer.HasAnimatedImages(document, assets) || !TryTile(canvas, out var rootTile))
            {
                Invalidate(); renderer.Draw(canvas, document, assets); return false;
            }
            DrawRootComposite(canvas, document, assets, sceneIdentity ?? document, sceneRevision, transactionId, rootTile, source.Background);
            // A complete composite already owns its single final opacity pass.
            return false;
        }
        if (canvas.Context is not null || bypassCache || invalidRange || renderer.UsesDepthComposition(document) || SceneRenderer.HasLiveEffects(document) ||
            !TryTile(canvas, out var tile) || byteLimit == 0)
        {
            Invalidate(); renderer.Draw(canvas, document, assets); return false;
        }
        // Animation time changes pixels only in this span, not the document revision. Preserve
        // static layers on either side and merge gesture ownership without changing z order.
        var animated = renderer.AnimatedElementRange(document, assets);
        if (animated.First >= 0)
        {
            dynamicFirst = dynamic ? Math.Min(dynamicFirst, animated.First) : animated.First;
            dynamicLast = dynamic ? Math.Max(dynamicLast, animated.Last) : animated.Last;
            dynamic = true;
        }
        var mapping = CanvasMapping.Create(document);
        var grid = document.Canvas.Grid;
        var appearance = new CanvasKey(mapping.PlaneToScreen, mapping.GridOrigin, mapping.GridCellSize,
            document.Canvas.BackgroundColor, grid.Visible, grid.Color, grid.Width, grid.Pattern);
        var key = new ContentKey(sceneIdentity ?? document, assets, sceneRevision, transactionId, dynamicFirst, dynamicLast, document.Elements.Count, document.Viewport, appearance);
        if (content != key) { Invalidate(); content = key; }
        clock++;
        if (!tiles.TryGetValue(tile, out var entry))
        {
            bool prefix = grid.Visible || (document.Canvas.BackgroundColor >> 24) != 0 ||
                HasVisible(document.Elements, 0, dynamic ? dynamicFirst : document.Elements.Count);
            bool suffix = dynamic && HasVisible(document.Elements, dynamicLast + 1, document.Elements.Count);
            long required = checked((long)tile.Bounds.Width * tile.Bounds.Height * 4 * ((prefix ? 1 : 0) + (suffix ? 1 : 0)));
            if (required > byteLimit) { Invalidate(); renderer.Draw(canvas, document, assets); return false; }
            EvictUntilFits(required);
            renderer.BeginFrame(document);
            try
            {
                SKImage? before = null, after = null;
                try
                {
                    if (prefix) before = BuildSegment(tile, document, assets, 0, dynamic ? dynamicFirst : document.Elements.Count, includeGrid: true);
                    if (suffix) after = BuildSegment(tile, document, assets, dynamicLast + 1, document.Elements.Count);
                    entry = new(before, after, required, clock); tiles.Add(tile, entry); CachedRasterBytes += required;
                }
                catch { before?.Dispose(); after?.Dispose(); throw; }
            }
            catch (OutOfMemoryException)
            {
                Invalidate(); renderer.Draw(canvas, document, assets); return false;
            }
            finally { renderer.EndFrame(); }
        }
        else { entry.Used = clock; HitCount++; }

        renderer.BeginFrame(document);
        int saved = canvas.Save();
        try
        {
            var viewport = document.Viewport;
            canvas.ClipRect(new((float)viewport.Left, (float)viewport.Top, (float)viewport.Right, (float)viewport.Bottom));
            double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
            renderer.DrawBackground(canvas, document, assets,
                includeCanvasBackground: document.Background is { Visible: true }, canvasOpacity: opacity);
            if (!(opacity > 0)) return false;
            if (opacity < 1 && !inPlaceOpacity)
            {
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
                canvas.SaveLayer(new SKRect((float)viewport.Left, (float)viewport.Top, (float)viewport.Right, (float)viewport.Bottom), paint);
            }
            DrawSegment(canvas, entry.Prefix, tile);
            if (dynamic) renderer.DrawElements(canvas, document.Elements, assets, dynamicFirst, dynamicLast + 1);
            DrawSegment(canvas, entry.Suffix, tile);
            if (opacity < 1 && !inPlaceOpacity) canvas.Restore();
            return inPlaceOpacity;
        }
        finally { canvas.RestoreToCount(saved); renderer.EndFrame(); }
    }

    private SKImage BuildSegment(TileKey tile, SceneDocument document, AssetLibrary assets, int first, int end, bool includeGrid = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(tile.Bounds.Width, tile.Bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new OutOfMemoryException(L.Get("Overlay.CacheAllocationFailed"));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(tile.TranslateX - tile.Bounds.Left, tile.TranslateY - tile.Bounds.Top);
        // The same hard viewport clip must participate in rasterization. In particular, Skia
        // resolves fractional geometry against this clip differently from clipping a flattened image.
        var viewport = document.Viewport;
        surface.Canvas.ClipRect(new((float)viewport.Left, (float)viewport.Top, (float)viewport.Right, (float)viewport.Bottom));
        if (includeGrid)
        {
            if (document.Background is not { Visible: true }) renderer.DrawPlaneColor(surface.Canvas, document, true);
            renderer.DrawGrid(surface.Canvas, document);
        }
        renderer.DrawElements(surface.Canvas, document.Elements, assets, first, end);
        BuildCount++; return surface.Snapshot();
    }

    private void DrawRootComposite(SKCanvas canvas, SceneDocument document, AssetLibrary assets, object sceneIdentity,
        long revision, long transaction, TileKey tile, CanvasElement? backgroundIdentity)
    {
        var mapping = CanvasMapping.Create(document); var grid = document.Canvas.Grid;
        var appearance = new CanvasKey(mapping.PlaneToScreen, mapping.GridOrigin, mapping.GridCellSize,
            document.Canvas.BackgroundColor, grid.Visible, grid.Color, grid.Width, grid.Pattern);
        var key = new RootContentKey(sceneIdentity, assets, assets.Count, revision, transaction, document.Elements.Count,
            document.Viewport, document.LayerOpacity, document.Kind, appearance, document.Canvas.Projection.Enabled,
            document.Canvas.DepthPriorityOcclusion, CanvasProjectionGeometry.ResolvePlaneBounds(document.Canvas.Projection), backgroundIdentity);
        bool sameRoots = rootAssignments is not null && rootAssignments.Count == document.RootCanvasAssignments.Count &&
            rootAssignments.All(pair => document.RootCanvasAssignments.TryGetValue(pair.Key, out var root) && root == pair.Value);
        if (rootContent != key || !sameRoots)
        {
            Invalidate(); rootContent = key; rootAssignments = new(document.RootCanvasAssignments);
        }
        long required = checked((long)tile.Bounds.Width * tile.Bounds.Height * 4);
        if (required > byteLimit) { Invalidate(); renderer.Draw(canvas, document, assets); return; }
        clock++;
        if (!tiles.TryGetValue(tile, out var entry))
        {
            EvictUntilFits(required);
            try
            {
                using var surface = SKSurface.Create(new SKImageInfo(tile.Bounds.Width, tile.Bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
                    ?? throw new OutOfMemoryException(L.Get("Overlay.CacheAllocationFailed"));
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Translate(tile.TranslateX - tile.Bounds.Left, tile.TranslateY - tile.Bounds.Top);
                renderer.Draw(surface.Canvas, document, assets);
                entry = new(surface.Snapshot(), null, required, clock); tiles.Add(tile, entry); CachedRasterBytes += required; BuildCount++;
            }
            catch (OutOfMemoryException)
            {
                Invalidate(); renderer.Draw(canvas, document, assets); return;
            }
        }
        else { entry.Used = clock; HitCount++; }
        DrawSegment(canvas, entry.Prefix, tile);
    }

    private static void DrawSegment(SKCanvas canvas, SKImage? image, TileKey tile)
    {
        if (image is null) return;
        int saved = canvas.Save();
        try { canvas.ResetMatrix(); canvas.DrawImage(image, tile.Bounds.Left, tile.Bounds.Top, Nearest); }
        finally { canvas.RestoreToCount(saved); }
    }
    private static bool HasVisible(IReadOnlyList<CanvasElement> elements, int first, int end)
    {
        for (int i = first; i < end; i++) if (elements[i].Visible && !(elements[i].Style.Opacity <= 0)) return true;
        return false;
    }
    private static bool TryTile(SKCanvas canvas, out TileKey tile)
    {
        var m = canvas.TotalMatrix; var clip = canvas.DeviceClipBounds;
        tile = default;
        if (!canvas.IsClipRect || clip.Width <= 0 || clip.Height <= 0 || !float.IsFinite(m.TransX) || !float.IsFinite(m.TransY)
            || m.ScaleX != 1 || m.ScaleY != 1 || m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1) return false;
        // DeviceClipBounds is the physical tile; LocalClipBounds is conservatively outset
        // in Skia and must not be used to infer its exact integer extent.
        tile = new(clip, m.TransX, m.TransY); return true;
    }
    private void EvictUntilFits(long required)
    {
        while (CachedRasterBytes + required > byteLimit && tiles.Count > 0)
        {
            var oldest = tiles.MinBy(pair => pair.Value.Used); oldest.Value.Dispose(); CachedRasterBytes -= oldest.Value.Bytes; tiles.Remove(oldest.Key);
        }
    }
    public void Invalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var entry in tiles.Values) entry.Dispose();
        tiles.Clear(); content = null; rootContent = null; rootAssignments = null; CachedRasterBytes = 0;
    }
    public void Dispose()
    {
        if (disposed) return;
        Invalidate(); disposed = true;
    }
    private readonly record struct ContentKey(object Document, AssetLibrary Assets, long Revision, long Transaction, int First, int Last, int Count, RectD Viewport, CanvasKey Canvas);
    private readonly record struct CanvasKey(Projective2D Projection, PointD Origin, double CellSize, uint Background,
        bool GridVisible, uint GridColor, double GridWidth, StrokePattern Pattern);
    private readonly record struct RootContentKey(object Document, AssetLibrary Assets, int AssetCount, long Revision, long Transaction,
        int Count, RectD Viewport, double Opacity, DocumentKind Kind, CanvasKey Canvas, bool ProjectionEnabled,
        bool DepthPriority, RectD PlaneBounds, CanvasElement? Background);
    private readonly record struct TileKey(SKRectI Bounds, float TranslateX, float TranslateY);
    private sealed class TileEntry(SKImage? prefix, SKImage? suffix, long bytes, long used) : IDisposable
    {
        public SKImage? Prefix { get; } = prefix;
        public SKImage? Suffix { get; } = suffix;
        public long Bytes { get; } = bytes;
        public long Used { get; set; } = used;
        public void Dispose() { Prefix?.Dispose(); Suffix?.Dispose(); }
    }
}
