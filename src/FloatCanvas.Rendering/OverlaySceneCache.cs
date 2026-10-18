using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using System.Numerics;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>
/// UI-thread raster cache for translation-only monitor tiles. Revision and transaction stamps are supplied
/// by the editor. Static prefix/suffix remain in z order around one continuous dynamic span.
/// Does not own the supplied SceneRenderer. Export continues to use the uncached renderer.
/// </summary>
public sealed class OverlaySceneCache(SceneRenderer renderer) : IDisposable
{
    private readonly Dictionary<TileKey, TileEntry> tiles = [];
    private ContentKey? content;
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
    /// </summary>
    public void DrawOnClearedRasterTarget(SKSurface surface, SceneDocument document, AssetLibrary assets, long sceneRevision,
        long transactionId, int dynamicFirst = -1, int dynamicLast = -1, bool bypassCache = false)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(assets);
        bool eligible = document.Kind == DocumentKind.LiveOverlay && document.Background is null &&
            document.LayerOpacity > 0 && document.LayerOpacity < 1;
        if (eligible)
        {
            using var format = surface.PeekPixels();
            eligible = format is not null &&
                format.ColorType == SKColorType.Bgra8888 && format.AlphaType == SKAlphaType.Premul;
        }
        if (!DrawCore(surface.Canvas, document, assets, sceneRevision, transactionId, dynamicFirst, dynamicLast, bypassCache, eligible)) return;
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
        long transactionId, int dynamicFirst, int dynamicLast, bool bypassCache, bool inPlaceOpacity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(assets);
        bool dynamic = dynamicFirst >= 0 && dynamicLast >= dynamicFirst && dynamicLast < document.Elements.Count;
        bool invalidRange = !dynamic && (dynamicFirst != -1 || dynamicLast != -1);
        if (canvas.Context is not null || bypassCache || invalidRange || SceneRenderer.HasLiveEffects(document) ||
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
        var key = new ContentKey(document, assets, sceneRevision, transactionId, dynamicFirst, dynamicLast, document.Elements.Count, document.Viewport);
        if (content != key) { Invalidate(); content = key; }
        clock++;
        if (!tiles.TryGetValue(tile, out var entry))
        {
            bool prefix = HasVisible(document.Elements, 0, dynamic ? dynamicFirst : document.Elements.Count);
            bool suffix = dynamic && HasVisible(document.Elements, dynamicLast + 1, document.Elements.Count);
            long required = checked((long)tile.Bounds.Width * tile.Bounds.Height * 4 * ((prefix ? 1 : 0) + (suffix ? 1 : 0)));
            if (required > byteLimit) { Invalidate(); renderer.Draw(canvas, document, assets); return false; }
            EvictUntilFits(required);
            renderer.BeginFrame();
            try
            {
                SKImage? before = null, after = null;
                try
                {
                    if (prefix) before = BuildSegment(tile, document, assets, 0, dynamic ? dynamicFirst : document.Elements.Count);
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

        renderer.BeginFrame();
        int saved = canvas.Save();
        try
        {
            var viewport = document.Viewport;
            canvas.ClipRect(new((float)viewport.Left, (float)viewport.Top, (float)viewport.Right, (float)viewport.Bottom));
            renderer.DrawBackground(canvas, document, assets);
            double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
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

    private SKImage BuildSegment(TileKey tile, SceneDocument document, AssetLibrary assets, int first, int end)
    {
        using var surface = SKSurface.Create(new SKImageInfo(tile.Bounds.Width, tile.Bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new OutOfMemoryException(L.Get("Overlay.CacheAllocationFailed"));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(tile.TranslateX - tile.Bounds.Left, tile.TranslateY - tile.Bounds.Top);
        // The same hard viewport clip must participate in rasterization. In particular, Skia
        // resolves fractional geometry against this clip differently from clipping a flattened image.
        var viewport = document.Viewport;
        surface.Canvas.ClipRect(new((float)viewport.Left, (float)viewport.Top, (float)viewport.Right, (float)viewport.Bottom));
        renderer.DrawElements(surface.Canvas, document.Elements, assets, first, end);
        BuildCount++; return surface.Snapshot();
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
        tiles.Clear(); content = null; CachedRasterBytes = 0;
    }
    public void Dispose()
    {
        if (disposed) return;
        Invalidate(); disposed = true;
    }
    private readonly record struct ContentKey(SceneDocument Document, AssetLibrary Assets, long Revision, long Transaction, int First, int Last, int Count, RectD Viewport);
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
