using FloatCanvas.Core;
using SkiaSharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private SKSurface? _elementOpacitySurface;
    private SKPixmap? _elementOpacityPixels;
    private SKImageInfo _elementOpacityInfo;
    private SKSurface? _ownedOpacityTarget;
    private SKPixmap? _ownedOpacityPixels;
    private byte[] _opacityTiles = [];
    private readonly List<SKRectI> _opacityRectangles = [];
    internal long CachedElementOpacityBytes => (_elementOpacitySurface is null ? 0 : (long)_elementOpacityInfo.BytesSize) + _opacityTiles.LongLength;

    // Called only by the preview worker on its cleared, exclusive base target.
    // Nested group opacity, depth composition, live effects and GPU retain Skia composition.
    internal void DrawCalibrationOnClearedRasterTarget(SKSurface surface, SceneDocument document, AssetLibrary assets, bool includeGrid)
    {
        var previous = _ownedOpacityTarget;
        var previousPixels = _ownedOpacityPixels;
        _ownedOpacityTarget = surface.Context is null && document.LayerOpacity == 1 &&
            !UsesDepthComposition(document) && !HasLiveEffects(document) ? surface : null;
        using var pixels = _ownedOpacityTarget?.PeekPixels();
        _ownedOpacityPixels = pixels;
        try { Draw(surface.Canvas, document, assets, calibrationReference: true, includeGrid: includeGrid); }
        finally { _ownedOpacityTarget = previous; _ownedOpacityPixels = previousPixels; }
    }


    private void ReleaseElementOpacitySurface()
    {
        _elementOpacityPixels?.Dispose(); _elementOpacityPixels = null;
        _elementOpacitySurface?.Dispose();
        _elementOpacitySurface = null; _opacityTiles = []; _opacityRectangles.Clear(); _opacityRectangles.TrimExcess();
    }

    private bool SupportsProjectiveOpacity(SKCanvas canvas, CanvasElement element)
    {
        var matrix = canvas.TotalMatrix;
        var transform = new Projective2D(matrix.ScaleX, matrix.SkewX, matrix.TransX,
            matrix.SkewY, matrix.ScaleY, matrix.TransY, matrix.Persp0, matrix.Persp1, matrix.Persp2);
        if (!TryOpacityBounds(transform, DrawLocalBounds(element), out _)) return false;
        using var borrowed = ReferenceEquals(canvas.Surface, _ownedOpacityTarget) ? null : canvas.Surface?.PeekPixels();
        var pixels = ReferenceEquals(canvas.Surface, _ownedOpacityTarget) ? _ownedOpacityPixels : borrowed;
        return pixels is not null && pixels.AlphaType == SKAlphaType.Premul && pixels.ColorSpace is null &&
            pixels.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool TryDrawRasterOpacity(SKCanvas destination, CanvasElement element, AssetLibrary assets, double opacity, bool clipSafe)
    {
        if (destination.Context is not null || !clipSafe ||
            element.Kind is ElementKind.Image or ElementKind.Text or ElementKind.Number || ElementGeometry.IsLiveEffect(element.Kind)) return false;
        var matrix = destination.TotalMatrix;
        var target = destination.Surface;
        if (target is null) return false;
        using var borrowed = ReferenceEquals(target, _ownedOpacityTarget) ? null : target.PeekPixels();
        var pixels = ReferenceEquals(target, _ownedOpacityTarget) ? _ownedOpacityPixels : borrowed;
        if (pixels is null || pixels.AlphaType != SKAlphaType.Premul || pixels.ColorSpace is not null ||
            pixels.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888)) return false;
        var info = pixels.Info;
        long budget = Math.Min(CacheByteLimit, 64L * 1024 * 1024);
        if ((long)info.BytesSize > budget) return false;
        // Use the actual device matrix, including monitor origin and float conversion.
        // MapRect alone cannot establish whether a projective rectangle crosses w=0.
        var transform = new Projective2D(matrix.ScaleX, matrix.SkewX, matrix.TransX,
            matrix.SkewY, matrix.ScaleY, matrix.TransY, matrix.Persp0, matrix.Persp1, matrix.Persp2);
        if (!TryOpacityBounds(transform, DrawLocalBounds(element), out var bounds)) return false;
        var clip = destination.DeviceClipBounds;
        bool localClipOrigin = !destination.IsClipRect || matrix.Persp0 != 0 || matrix.Persp1 != 0;
        int originX = localClipOrigin ? clip.Left : 0, originY = localClipOrigin ? clip.Top : 0;
        int left = (int)Math.Floor(Math.Clamp((double)bounds.Left - 2, clip.Left, clip.Right));
        int top = (int)Math.Floor(Math.Clamp((double)bounds.Top - 2, clip.Top, clip.Bottom));
        int right = (int)Math.Ceiling(Math.Clamp((double)bounds.Right + 2, clip.Left, clip.Right));
        int bottom = (int)Math.Ceiling(Math.Clamp((double)bounds.Bottom + 2, clip.Top, clip.Bottom));
        if (right <= left || bottom <= top) return true;
        var strokeRegions = StrokeOpacityRegions(element, transform, clip, budget - (long)info.BytesSize);
        if (strokeRegions is { Count: 0 }) return true;
        bool directPixels = ReferenceEquals(_ownedOpacityTarget, destination.Surface) && destination.IsClipRect &&
            originX == 0 && originY == 0 && pixels.ColorType == SKColorType.Bgra8888;
        using var deviceRegion = strokeRegions is null || directPixels ? null : new SKRegion();
        if (deviceRegion is not null && !deviceRegion.SetRects(CollectionsMarshal.AsSpan(strokeRegions!))) return true;
        using var shiftedRegion = deviceRegion is not null && (originX != 0 || originY != 0) ? new SKRegion() : null;
        if (shiftedRegion is not null) shiftedRegion.SetRects(strokeRegions!.Select(r => new SKRectI(r.Left - originX, r.Top - originY, r.Right - originX, r.Bottom - originY)).ToArray());
        ref var opacitySurface = ref _elementOpacitySurface;
        ref var opacityInfo = ref _elementOpacityInfo;
        if (opacitySurface is null || opacityInfo != info)
        {
            _elementOpacityPixels?.Dispose(); _elementOpacityPixels = null;
            opacitySurface?.Dispose(); opacitySurface = null;
            opacitySurface = SKSurface.Create(info); opacityInfo = info;
            if (opacitySurface is null) return false;
            _elementOpacityPixels = opacitySurface.PeekPixels();
        }
        var scratchPixels = _elementOpacityPixels;
        if (scratchPixels is null) return false;
        var region = new SKRect(left, top, right, bottom);
        var scratch = opacitySurface.Canvas;
        // Match the reference layer's device origin. Hard polygon clips give it a
        // nonzero origin; object-local bounds must not change the raster phase.
        int scratchSaved;
        if (directPixels)
        {
            var bytes = scratchPixels.GetPixelSpan();
            if (strokeRegions is null) ClearRectangle(bytes, scratchPixels.RowBytes, new(left, top, right, bottom));
            else foreach (var rectangle in strokeRegions) ClearRectangle(bytes, scratchPixels.RowBytes, rectangle);
        }
        else
        {
            scratchSaved = scratch.Save();
            try
            {
                scratch.ResetMatrix();
                if (deviceRegion is not null) scratch.ClipRegion(shiftedRegion ?? deviceRegion);
                else scratch.ClipRect(new SKRect(left - originX, top - originY, right - originX, bottom - originY));
                scratch.Clear(SKColors.Transparent);
            }
            finally { scratch.RestoreToCount(scratchSaved); }
        }
        scratchSaved = scratch.Save();
        try
        {
            scratch.ResetMatrix();
            scratch.ClipRect(new SKRect(clip.Left - originX, clip.Top - originY, clip.Right - originX, clip.Bottom - originY));
            scratch.Translate(-originX, -originY);
            scratch.Concat(in matrix);
            DrawLocalElementCore(scratch, element, assets, false);
        }
        finally { scratch.RestoreToCount(scratchSaved); }
        if (directPixels)
        {
            destination.Flush();
            return TryComposeOpacityPixels(scratchPixels, pixels, strokeRegions, new(left, top, right, bottom), opacity);
        }
        using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
        using var bitmap = new SKBitmap();
        if (!bitmap.InstallPixels(scratchPixels)) return false;
        // Borrow only for this synchronous CPU draw; all image/bitmap views are disposed
        // before the next scratch write. Snapshot would force a full-surface copy on write.
        bitmap.SetImmutable();
        using var image = SKImage.FromBitmap(bitmap);
        int saved = destination.Save();
        try
        {
            destination.ResetMatrix();
            if (deviceRegion is not null) destination.ClipRegion(deviceRegion);
            else destination.ClipRect(region);
            // Drawing the whole image at its device origin retains SaveLayer's blend path.
            // A cropped source/destination image rect uses a different 8-bit rounding path.
            destination.DrawImage(image, originX, originY, Nearest, alpha);
        }
        finally { destination.RestoreToCount(saved); }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private List<SKRectI>? StrokeOpacityRegions(CanvasElement element, Projective2D transform, SKRectI clip, long tileBudget)
    {
        if (element.Kind is not (ElementKind.Freehand or ElementKind.Highlighter) || element.Points.Count == 0 ||
            element.Points.Count > 4096 || !(element.Style.StrokeWidth > 0)) return null;
        const int tile = 16;
        int columns = (clip.Right + tile - 1) / tile, rows = (clip.Bottom + tile - 1) / tile;
        int size = checked(columns * rows);
        if (size > tileBudget) { _opacityTiles = []; return null; }
        if (_opacityTiles.Length < size || _opacityTiles.LongLength > tileBudget) _opacityTiles = new byte[size];
        else Array.Clear(_opacityTiles, 0, size);
        int minRow = rows, maxRow = 0, minColumn = columns, maxColumn = 0;
        double radius = (float)element.Style.StrokeWidth / 2d;
        // GetPath submits float vertices and round joins/caps. Each inflated segment
        // rectangle contains its stroke, including joins. Mapping all four corners
        // before adding a device-pixel AA guard keeps the mask conservative.
        for (int i = 0; i < Math.Max(1, element.Points.Count - 1); i++)
        {
            var a = element.Points[i]; var b = element.Points[Math.Min(i + 1, element.Points.Count - 1)];
            double x = (float)a.X, y = (float)a.Y, bx = (float)b.X, by = (float)b.Y;
            var local = new RectD(Math.Min(x, bx) - radius, Math.Min(y, by) - radius, Math.Abs(x - bx) + radius * 2, Math.Abs(y - by) + radius * 2);
            if (!TryOpacityBounds(transform, local, out var bounds)) return null;
            // Integer tile edges bound the number of region bands. Exact per-segment
            // edges otherwise spend more time building/visiting clip runs than blending.
            int left = (int)Math.Clamp(Math.Floor((bounds.Left - 2) / tile), clip.Left / tile, columns);
            int top = (int)Math.Clamp(Math.Floor((bounds.Top - 2) / tile), clip.Top / tile, rows);
            int right = (int)Math.Clamp(Math.Ceiling((bounds.Right + 2) / tile), clip.Left / tile, columns);
            int bottom = (int)Math.Clamp(Math.Ceiling((bounds.Bottom + 2) / tile), clip.Top / tile, rows);
            if (right <= left || bottom <= top) continue;
            minRow = Math.Min(minRow, top); maxRow = Math.Max(maxRow, bottom);
            minColumn = Math.Min(minColumn, left); maxColumn = Math.Max(maxColumn, right);
            for (int row = top; row < bottom; row++) _opacityTiles.AsSpan(row * columns + left, right - left).Fill(1);
        }
        // SetRects receives sorted disjoint row runs, rather than thousands of
        // overlapping segment boxes. It need not repeatedly union those boxes.
        var rectangles = _opacityRectangles; rectangles.Clear();
        for (int row = minRow; row < maxRow; row++)
        {
            int column = minColumn;
            while (column < maxColumn)
            {
                while (column < maxColumn && _opacityTiles[row * columns + column] == 0) column++;
                int first = column;
                while (column < maxColumn && _opacityTiles[row * columns + column] != 0) column++;
                if (column > first) rectangles.Add(new(Math.Max(clip.Left, first * tile), Math.Max(clip.Top, row * tile),
                    Math.Min(clip.Right, column * tile), Math.Min(clip.Bottom, (row + 1) * tile)));
            }
        }
        return rectangles;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ClearRectangle(Span<byte> bytes, int stride, SKRectI rectangle)
    {
        for (int y = rectangle.Top; y < rectangle.Bottom; y++) bytes.Slice(y * stride + rectangle.Left * 4, rectangle.Width * 4).Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryComposeOpacityPixels(SKPixmap from, SKPixmap to, List<SKRectI>? rectangles, SKRectI bounds, double opacity)
    {
        if (from.Info != to.Info || from.RowBytes != to.RowBytes) return false;
        var sourceBytes = from.GetPixelSpan(); var destinationBytes = to.GetPixelSpan();
        int multiplier = Alpha(opacity) + 1;
        if (rectangles is null) BlendRectangle(sourceBytes, destinationBytes, from.RowBytes, bounds, multiplier);
        else foreach (var rect in rectangles) BlendRectangle(sourceBytes, destinationBytes, from.RowBytes, rect, multiplier);
        return true;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlendRectangle(Span<byte> source, Span<byte> destination, int stride, SKRectI rectangle, int multiplier)
    {
        var factor = Vector128.Create((ushort)multiplier); var full = Vector128.Create((ushort)256);
        var wideFactor = Vector256.Create((ushort)multiplier); var wideFull = Vector256.Create((ushort)256);
        for (int y = rectangle.Top; y < rectangle.Bottom; y++)
        {
            int begin = y * stride + rectangle.Left * 4, end = y * stride + rectangle.Right * 4;
            int x = begin;
            if (Avx2.IsSupported)
                for (; x <= end - 32; x += 32)
                {
                    var s = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)x);
                    if (s == Vector256<byte>.Zero) continue;
                    var d = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
                    var sl = Avx2.ShiftRightLogical(Avx2.MultiplyLow(Avx2.UnpackLow(s, Vector256<byte>.Zero).AsUInt16(), wideFactor), 8);
                    var sh = Avx2.ShiftRightLogical(Avx2.MultiplyLow(Avx2.UnpackHigh(s, Vector256<byte>.Zero).AsUInt16(), wideFactor), 8);
                    if (d == Vector256<byte>.Zero)
                    {
                        Avx2.PackUnsignedSaturate(sl.AsInt16(), sh.AsInt16()).StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
                        continue;
                    }
                    var al = Avx2.ShuffleHigh(Avx2.ShuffleLow(sl, 255), 255);
                    var ah = Avx2.ShuffleHigh(Avx2.ShuffleLow(sh, 255), 255);
                    var dl = Avx2.UnpackLow(d, Vector256<byte>.Zero).AsUInt16(); var dh = Avx2.UnpackHigh(d, Vector256<byte>.Zero).AsUInt16();
                    var low = Avx2.Add(sl, Avx2.ShiftRightLogical(Avx2.MultiplyLow(dl, Avx2.Subtract(wideFull, al)), 8));
                    var high = Avx2.Add(sh, Avx2.ShiftRightLogical(Avx2.MultiplyLow(dh, Avx2.Subtract(wideFull, ah)), 8));
                    Avx2.PackUnsignedSaturate(low.AsInt16(), high.AsInt16()).StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
                }
            if (Sse2.IsSupported)
                for (; x <= end - 16; x += 16)
                {
                    var s = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)x);
                    if (s == Vector128<byte>.Zero) continue;
                    var d = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
                    var sl = Sse2.ShiftRightLogical(Sse2.MultiplyLow(Sse2.UnpackLow(s, Vector128<byte>.Zero).AsUInt16(), factor), 8);
                    var sh = Sse2.ShiftRightLogical(Sse2.MultiplyLow(Sse2.UnpackHigh(s, Vector128<byte>.Zero).AsUInt16(), factor), 8);
                    if (d == Vector128<byte>.Zero)
                    {
                        Sse2.PackUnsignedSaturate(sl.AsInt16(), sh.AsInt16()).StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
                        continue;
                    }
                    var al = Sse2.ShuffleHigh(Sse2.ShuffleLow(sl, 255), 255);
                    var ah = Sse2.ShuffleHigh(Sse2.ShuffleLow(sh, 255), 255);
                    var dl = Sse2.UnpackLow(d, Vector128<byte>.Zero).AsUInt16(); var dh = Sse2.UnpackHigh(d, Vector128<byte>.Zero).AsUInt16();
                    var low = Sse2.Add(sl, Sse2.ShiftRightLogical(Sse2.MultiplyLow(dl, Sse2.Subtract(full, al)), 8));
                    var high = Sse2.Add(sh, Sse2.ShiftRightLogical(Sse2.MultiplyLow(dh, Sse2.Subtract(full, ah)), 8));
                    Sse2.PackUnsignedSaturate(low.AsInt16(), high.AsInt16()).StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)x);
                }
            for (; x < end; x += 4)
            {
                int alpha = source[x + 3] * multiplier >> 8, inverse = 256 - alpha;
                for (int c = 0; c < 4; c++) destination[x + c] = (byte)((source[x + c] * multiplier >> 8) + (destination[x + c] * inverse >> 8));
            }
        }
    }

    private static bool TryOpacityBounds(Projective2D transform, RectD bounds, out RectD result)
    {
        result = default;
        Span<PointD> corners = stackalloc PointD[] { new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom) };
        double denominator = transform.Denominator(corners[0]);
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var corner in corners)
        {
            if (denominator * transform.Denominator(corner) <= 0 || !transform.TryTransformPoint(corner, out var point)) return false;
            left = Math.Min(left, point.X); top = Math.Min(top, point.Y); right = Math.Max(right, point.X); bottom = Math.Max(bottom, point.Y);
        }
        result = new(left, top, right - left, bottom - top);
        return double.IsFinite(result.Width) && double.IsFinite(result.Height);
    }
}
