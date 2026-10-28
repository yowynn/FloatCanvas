using FloatCanvas.Core;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private const int MaximumBorderContourRuns = 16_384;
    private const int BorderMaskTileSize = 256;
    // Native curved-stroke coverage depends on the raster clip. Draw a surrounding
    // band before cropping each tile, retaining its edge coverage when magnified.
    private const int BorderMaskGuard = 64;
    private const int MaximumBorderMaskBatch = 32;
    private readonly Dictionary<ImageBorderFrameKey, ImageBorderEntry> _imageBorders = [];
    internal bool CacheImageBorders { get; set; } = true;
    internal long ImageBorderCacheByteLimit { get; set; } = 64L * 1024 * 1024;
    internal long ImageBorderSupportBuildCount { get; private set; }
    internal long ImageBorderMaskBuildCount { get; private set; }
    internal long ImageBorderMaskHitCount { get; private set; }
    internal long CachedImageBorderBytes => _imageBorders.Values.Sum(entry => entry.CachedBytes);

    private void DrawImageBorder(SKCanvas canvas, CanvasElement element, SKImage image, RectD crop)
    {
        if (!(element.ImageBorderWidth > 0) || (element.Style.Stroke >> 24) == 0) return;
        // GPU effect images are released after each frame. Reuse their identical alpha
        // support by content rather than by the short-lived SKImage handle.
        int sourceFrame = _images[element.AssetId!].FrameIndex;
        bool gpuEffects = element.Effects.Count != 0 && canvas.Context is not null;
        var frameKey = new ImageBorderFrameKey(element.Id, sourceFrame);
        if (!_imageBorders.TryGetValue(frameKey, out var entry) || !entry.Matches(element, crop, gpuEffects))
        {
            var next = BuildImageBorder(element, image, crop, gpuEffects);
            // A changed recipe invalidates every animation frame. Build first so a failed
            // allocation cannot leave a disposed object published in the cache.
            foreach (var key in _imageBorders.Where(item => item.Key.Element == element.Id &&
                !item.Value.Matches(element, crop, gpuEffects)).Select(item => item.Key).ToArray())
            { _imageBorders[key].Dispose(); _imageBorders.Remove(key); }
            if (_imageBorders.Remove(frameKey, out var previous)) previous.Dispose();
            entry = next;
            _imageBorders[frameKey] = entry;
        }
        entry.Used = _clock;
        foreach (var support in _imageBorders.Where(item => item.Key.Element == element.Id).Select(item => item.Value))
        {
            if (support.BorderWidth == element.ImageBorderWidth) continue;
            support.ClearMasks(); support.BorderWidth = element.ImageBorderWidth;
        }
        // An empty animation frame needs only its small semantic entry, not empty tiles.
        if (entry.Path is { IsEmpty: true }) return;
        if (!CacheImageBorders || ImageBorderBudget < entry.OwnBytes + 256)
        { DrawImageBorderSupport(canvas, element, entry, crop, element.Style.Stroke); return; }

        var matrix = canvas.TotalMatrix;
        int offsetX = 0, offsetY = 0;
        // Moving by whole device pixels changes placement, not coverage. Keep the
        // fractional phase in the key, including while an object is being dragged.
        if (matrix.Persp0 == 0 && matrix.Persp1 == 0 && matrix.Persp2 == 1 &&
            matrix.TransX is > int.MinValue and < int.MaxValue && matrix.TransY is > int.MinValue and < int.MaxValue)
        {
            offsetX = (int)Math.Floor(matrix.TransX); offsetY = (int)Math.Floor(matrix.TransY);
            matrix.TransX -= offsetX; matrix.TransY -= offsetY;
        }
        var tiles = ImageBorderTiles(canvas.DeviceClipBounds, matrix, element, offsetX, offsetY);
        using var colorFilter = SKColorFilter.CreateBlendMode(new SKColor(element.Style.Stroke), SKBlendMode.SrcIn);
        using var tint = new SKPaint { ColorFilter = colorFilter };
        int saved = canvas.Save();
        try
        {
            canvas.ResetMatrix();
            for (int tile = 0; tile < tiles.Count;)
            {
                var key = new ImageBorderMaskKey(matrix, tiles[tile]);
                if (entry.Masks.TryGetValue(key, out var mask))
                {
                    mask.Used = _clock; ImageBorderMaskHitCount++;
                    DrawImageBorderMask(canvas, mask, offsetX, offsetY, tint); tile++; continue;
                }
                var pending = new List<ImageBorderMaskKey>(); long required = 0, workBytes = 0;
                long available = Math.Max(0, ImageBorderBudget - entry.OwnBytes);
                while (tile + pending.Count < tiles.Count && pending.Count < MaximumBorderMaskBatch)
                {
                    var next = new ImageBorderMaskKey(matrix, tiles[tile + pending.Count]);
                    if (entry.Masks.ContainsKey(next)) break;
                    long bytes = ImageBorderMask.BytesFor(next.Rectangle);
                    long rasterBytes = ImageBorderMask.RasterBytesFor(next.Rectangle);
                    if (pending.Count != 0 && (required + bytes > available || workBytes + rasterBytes > 8L * 1024 * 1024)) break;
                    pending.Add(next); required += bytes; workBytes += rasterBytes;
                }
                // Never trim decoded/effected images here: DrawImage still uses its
                // borrowed image after this call. Only border entries may be evicted.
                if (!TrimImageBorderCaches(required, entry))
                {
                    int clipped = canvas.Save();
                    try
                    {
                        var rectangle = tiles[tile];
                        canvas.ClipRect(new(rectangle.Left + offsetX, rectangle.Top + offsetY,
                            rectangle.Right + offsetX, rectangle.Bottom + offsetY));
                        canvas.SetMatrix(CanvasMatrix(matrix, offsetX, offsetY));
                        DrawImageBorderSupport(canvas, element, entry, crop, element.Style.Stroke);
                    }
                    finally { canvas.RestoreToCount(clipped); }
                    tile++; continue;
                }
                BuildImageBorderMasks(element, entry, crop, pending);
                foreach (var next in pending) DrawImageBorderMask(canvas, entry.Masks[next], offsetX, offsetY, tint);
                tile += pending.Count;
            }
        }
        finally { canvas.RestoreToCount(saved); }

        static SKMatrix CanvasMatrix(SKMatrix normalized, int x, int y)
        {
            normalized.TransX += x; normalized.TransY += y; return normalized;
        }
    }

    private long ImageBorderBudget => Math.Max(0, Math.Min(ImageBorderCacheByteLimit,
        Math.Max(0, CacheByteLimit) - _images.Values.Sum(entry => entry.CachedBytes) - _effects.Values.Sum(entry => entry.CachedBytes)));

    private bool TrimImageBorderCaches(long required = 0, ImageBorderEntry? protectedSupport = null)
    {
        long budget = ImageBorderBudget;
        while (CachedImageBorderBytes + required > budget)
        {
            var mask = _imageBorders.Values.SelectMany(support => support.Masks.Select(item => (Support: support, item.Key, Mask: item.Value)))
                .OrderBy(item => item.Mask.Used).FirstOrDefault();
            // Once an old frame's last mask has gone, release its support before
            // spending the same bytes by evicting a more recently used frame's mask.
            var empty = _imageBorders.Where(item => !ReferenceEquals(item.Value, protectedSupport) && item.Value.Masks.Count == 0)
                .OrderBy(item => item.Value.Used).FirstOrDefault();
            if (empty.Value is not null && (mask.Mask is null || empty.Value.Used <= mask.Mask.Used))
            { _imageBorders.Remove(empty.Key); empty.Value.Dispose(); continue; }
            if (mask.Mask is not null)
            { mask.Support.Masks.Remove(mask.Key); mask.Mask.Dispose(); continue; }
            var support = _imageBorders.Where(item => !ReferenceEquals(item.Value, protectedSupport)).OrderBy(item => item.Value.Used).FirstOrDefault();
            if (support.Value is null) return false;
            _imageBorders.Remove(support.Key); support.Value.Dispose();
        }
        return true;
    }

    private static List<SKRectI> ImageBorderTiles(SKRectI deviceClip, SKMatrix matrix, CanvasElement element, int offsetX, int offsetY)
    {
        var clip = new RectD((double)deviceClip.Left - offsetX, (double)deviceClip.Top - offsetY, deviceClip.Width, deviceClip.Height);
        var transform = new Projective2D(matrix.ScaleX, matrix.SkewX, matrix.TransX, matrix.SkewY, matrix.ScaleY,
            matrix.TransY, matrix.Persp0, matrix.Persp1, matrix.Persp2);
        double border = element.ImageBorderWidth;
        if (transform.TryTransformBounds(new(-border, -border, element.Width + border * 2, element.Height + border * 2), out var bounds))
            clip = clip.Intersect(bounds.Inflate(1));
        var tiles = new List<SKRectI>();
        if (clip.IsEmpty) return tiles;
        int left = (int)Math.Floor(Math.Clamp(clip.Left, int.MinValue + BorderMaskGuard, int.MaxValue - BorderMaskGuard));
        int top = (int)Math.Floor(Math.Clamp(clip.Top, int.MinValue + BorderMaskGuard, int.MaxValue - BorderMaskGuard));
        int right = (int)Math.Ceiling(Math.Clamp(clip.Right, int.MinValue + BorderMaskGuard, int.MaxValue - BorderMaskGuard));
        int bottom = (int)Math.Ceiling(Math.Clamp(clip.Bottom, int.MinValue + BorderMaskGuard, int.MaxValue - BorderMaskGuard));
        for (long y = (long)Math.Floor(top / (double)BorderMaskTileSize) * BorderMaskTileSize; y < bottom; y += BorderMaskTileSize)
            for (long x = (long)Math.Floor(left / (double)BorderMaskTileSize) * BorderMaskTileSize; x < right; x += BorderMaskTileSize)
                tiles.Add(new((int)Math.Max(x, left), (int)Math.Max(y, top), (int)Math.Min(x + BorderMaskTileSize, right), (int)Math.Min(y + BorderMaskTileSize, bottom)));
        return tiles;
    }

    private void BuildImageBorderMasks(CanvasElement element, ImageBorderEntry support, RectD crop, IReadOnlyList<ImageBorderMaskKey> keys)
    {
        var surfaces = new List<SKSurface>();
        var masks = new List<ImageBorderMask>();
        int published = 0;
        try
        {
            foreach (var key in keys)
            {
                var rectangle = key.Rectangle;
                var surface = SKSurface.Create(new SKImageInfo(rectangle.Width + BorderMaskGuard * 2, rectangle.Height + BorderMaskGuard * 2,
                    SKColorType.Bgra8888, SKAlphaType.Premul)) ?? throw new InvalidOperationException(FloatCanvas.Localization.AppText.Get("Export.BufferTooLarge"));
                surfaces.Add(surface);
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Translate(-rectangle.Left + BorderMaskGuard, -rectangle.Top + BorderMaskGuard);
                var matrix = key.Matrix;
                surface.Canvas.Concat(in matrix);
            }
            if (support.Path is { } path)
            {
                using var paint = ImageBorderPaint(element.ImageBorderWidth, SKColors.White);
                foreach (var surface in surfaces) surface.Canvas.DrawPath(path, paint);
            }
            else DrawComplexImageBorderGeometry(surfaces.Select(surface => surface.Canvas).ToArray(), element, support.Alpha!, crop, SKColors.White);
            for (int i = 0; i < surfaces.Count; i++)
            {
                using var image = surfaces[i].Snapshot();
                // Keep native BGRA rasterization, then retain its exact eight-bit alpha
                // coverage at one byte per pixel. No spatial filtering is involved.
                using var alpha = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Alpha8, SKAlphaType.Premul), image.Width);
                if (!image.ReadPixels(alpha.Info, alpha.GetPixels(), alpha.RowBytes))
                    throw new InvalidDataException(FloatCanvas.Localization.AppText.Get("Images.UnsupportedOrCorrupt"));
                alpha.SetImmutable();
                masks.Add(new(keys[i], SKImage.FromBitmap(alpha), _clock));
            }
            for (int i = 0; i < keys.Count; i++) { support.Masks.Add(keys[i], masks[i]); published++; ImageBorderMaskBuildCount++; }
        }
        finally
        {
            for (int i = published; i < masks.Count; i++) masks[i].Dispose();
            foreach (var surface in surfaces) surface.Dispose();
        }
    }

    private static void DrawImageBorderMask(SKCanvas canvas, ImageBorderMask mask, int offsetX, int offsetY, SKPaint tint)
    {
        var rectangle = mask.Key.Rectangle;
        int saved = canvas.Save();
        try
        {
            // Original AA/path clips remain on the destination and are applied once.
            canvas.ClipRect(new(rectangle.Left + offsetX, rectangle.Top + offsetY, rectangle.Right + offsetX, rectangle.Bottom + offsetY));
            canvas.DrawImage(mask.Image, rectangle.Left + offsetX - BorderMaskGuard, rectangle.Top + offsetY - BorderMaskGuard, Nearest, tint);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private static SKPaint ImageBorderPaint(double width, SKColor color) => new()
    {
        Color = color, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = (float)(width * 2),
        StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, IsAntialias = true
    };

    private static void DrawImageBorderSupport(SKCanvas canvas, CanvasElement element, ImageBorderEntry support, RectD crop, uint color)
    {
        if (support.Path is { } path)
        { using var paint = ImageBorderPaint(element.ImageBorderWidth, new(color)); canvas.DrawPath(path, paint); }
        else
        {
            var tint = new SKColor(color);
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha(tint.Alpha) };
            int saved = tint.Alpha == 255 ? canvas.Save() : canvas.SaveLayer(layer);
            try { DrawComplexImageBorderGeometry([canvas], element, support.Alpha!, crop, tint.WithAlpha(255)); }
            finally { canvas.RestoreToCount(saved); }
        }
    }

    private ImageBorderEntry BuildImageBorder(CanvasElement element, SKImage image, RectD crop, bool gpuEffects)
    {
        ImageBorderSupportBuildCount++;
        var alpha = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Alpha8, SKAlphaType.Premul));
        try
        {
            if (!image.ReadPixels(alpha.Info, alpha.GetPixels(), alpha.RowBytes))
            {
                using var pixels = SKBitmap.FromImage(image);
                if (!pixels.ExtractAlpha(alpha)) throw new InvalidDataException(FloatCanvas.Localization.AppText.Get("Images.UnsupportedOrCorrupt"));
            }
            var rectangles = new List<SKRectI>();
            var bytes = alpha.GetPixelSpan();
            int left = Math.Max(0, (int)Math.Floor(crop.Left)), right = Math.Min(alpha.Width, (int)Math.Ceiling(crop.Right));
            for (int y = Math.Max(0, (int)Math.Floor(crop.Top)); y < Math.Min(alpha.Height, (int)Math.Ceiling(crop.Bottom)); y++)
            {
                var row = bytes.Slice(y * alpha.RowBytes, alpha.Width);
                for (int x = left; x < right;)
                {
                    while (x < right && row[x] == 0) x++;
                    int start = x;
                    while (x < right && row[x] != 0) x++;
                    if (start == x) continue;
                    if (rectangles.Count == MaximumBorderContourRuns)
                        return new(element, crop, gpuEffects, null, alpha, _clock);
                    rectangles.Add(new(start, y, x, y + 1));
                }
            }
            if (rectangles.Count == 0)
            {
                alpha.Dispose();
                return new(element, crop, gpuEffects, new SKPath(), null, _clock);
            }
            using var region = new SKRegion();
            region.SetRects(CollectionsMarshal.AsSpan(rectangles));
            using var boundary = region.GetBoundaryPath();
            using var builder = new SKPathBuilder(); builder.AddRect(ToRect(crop));
            using var cropPath = builder.Detach();
            var path = boundary.Op(cropPath, SKPathOp.Intersect) ?? new SKPath();
            float sx = (float)(element.Width / crop.Width), sy = (float)(element.Height / crop.Height);
            path.Transform(new SKMatrix(sx, 0, (float)-crop.Left * sx, 0, sy, (float)-crop.Top * sy, 0, 0, 1));
            alpha.Dispose();
            return new(element, crop, gpuEffects, path, null, _clock);
        }
        catch { alpha.Dispose(); throw; }
    }

    // A checkerboard can have millions of separate contours. Keep just its bounded Alpha8
    // bitmap and stream a fixed number of expanded row runs, rather than building a huge path.
    private static void DrawComplexImageBorderGeometry(IReadOnlyList<SKCanvas> canvases, CanvasElement element, SKBitmap alpha, RectD crop, SKColor color)
    {
        using var paint = new SKPaint { Color = color.WithAlpha(255), IsAntialias = true };
        using var builder = new SKPathBuilder();
        var bytes = alpha.GetPixelSpan();
        int count = 0;
        double sx = element.Width / crop.Width, sy = element.Height / crop.Height, width = element.ImageBorderWidth;
        int left = Math.Max(0, (int)Math.Floor(crop.Left)), right = Math.Min(alpha.Width, (int)Math.Ceiling(crop.Right));
        for (int y = Math.Max(0, (int)Math.Floor(crop.Top)); y < Math.Min(alpha.Height, (int)Math.Ceiling(crop.Bottom)); y++)
        {
            var row = bytes.Slice(y * alpha.RowBytes, alpha.Width);
            for (int x = left; x < right;)
            {
                while (x < right && row[x] == 0) x++;
                int start = x;
                while (x < right && row[x] != 0) x++;
                if (start == x) continue;
                var rectangle = new SKRect((float)((Math.Max(start, crop.Left) - crop.Left) * sx - width),
                    (float)((Math.Max(y, crop.Top) - crop.Top) * sy - width),
                    (float)((Math.Min(x, crop.Right) - crop.Left) * sx + width),
                    (float)((Math.Min(y + 1, crop.Bottom) - crop.Top) * sy + width));
                builder.AddRoundRect(rectangle, (float)width, (float)width);
                if (++count == 1024)
                { using var chunk = builder.Detach(); foreach (var canvas in canvases) canvas.DrawPath(chunk, paint); count = 0; }
            }
        }
        if (count != 0) { using var chunk = builder.Detach(); foreach (var canvas in canvases) canvas.DrawPath(chunk, paint); }
    }

    private readonly record struct ImageBorderFrameKey(Guid Element, int Frame);
    private readonly record struct ImageBorderMaskKey(SKMatrix Matrix, SKRectI Rectangle);

    private sealed class ImageBorderMask(ImageBorderMaskKey key, SKImage image, long used) : IDisposable
    {
        public ImageBorderMaskKey Key { get; } = key;
        public SKImage Image { get; } = image;
        public long Used { get; set; } = used;
        public long CachedBytes => BytesFor(Key.Rectangle);
        internal static long BytesFor(SKRectI rectangle) => RasterBytesFor(rectangle) / 4 + 192;
        internal static long RasterBytesFor(SKRectI rectangle) => (long)(rectangle.Width + BorderMaskGuard * 2) * (rectangle.Height + BorderMaskGuard * 2) * 4;
        public void Dispose() => Image.Dispose();
    }

    private sealed class ImageBorderEntry(CanvasElement element, RectD crop, bool gpuEffects, SKPath? path, SKBitmap? alpha, long used) : IDisposable
    {
        private readonly double width = element.Width, height = element.Height;
        private readonly string? assetId = element.AssetId;
        private readonly EffectSnapshot[] effects = element.Effects.Select(EffectSnapshot.From).ToArray();
        public SKPath? Path { get; } = path;
        public SKBitmap? Alpha { get; } = alpha;
        public long Used { get; set; } = used;
        public double BorderWidth { get; set; } = element.ImageBorderWidth;
        public Dictionary<ImageBorderMaskKey, ImageBorderMask> Masks { get; } = [];
        public long OwnBytes { get; } = 256 + (path is { } p ? (long)p.PointCount * 8 + p.VerbCount + 64 : 0) + (alpha?.ByteCount ?? 0) + element.Effects.Count * 64L;
        public long CachedBytes => OwnBytes + Masks.Values.Sum(mask => mask.CachedBytes);
        public bool Matches(CanvasElement current, RectD sourceCrop, bool currentGpuEffects)
        {
            if (assetId != current.AssetId || crop != sourceCrop || width != current.Width ||
                height != current.Height || gpuEffects != currentGpuEffects || effects.Length != current.Effects.Count) return false;
            for (int i = 0; i < effects.Length; i++)
                if (effects[i] != EffectSnapshot.From(current.Effects[i])) return false;
            return true;
        }
        public void ClearMasks() { foreach (var mask in Masks.Values) mask.Dispose(); Masks.Clear(); }
        public void Dispose() { ClearMasks(); Path?.Dispose(); Alpha?.Dispose(); }
    }

    private static void DrawOutsideContour(SKCanvas canvas, SKPath body, double width, uint color)
    {
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)(width * 2),
            StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var expanded = stroke.GetFillPath(body);
        using var outside = expanded.Op(body, SKPathOp.Difference);
        if (outside is null) return;
        using var paint = new SKPaint { Color = new SKColor(color), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(outside, paint);
    }
}
