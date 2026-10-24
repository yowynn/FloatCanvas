using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private SKSurface? _elementOpacitySurface;
    private SKImageInfo _elementOpacityInfo;
    internal long CachedElementOpacityBytes => _elementOpacitySurface is null ? 0 : (long)_elementOpacityInfo.Width * _elementOpacityInfo.Height * 4;

    private void ReleaseElementOpacitySurface()
    {
        _elementOpacitySurface?.Dispose();
        _elementOpacitySurface = null;
    }

    private bool TryDrawRasterOpacity(SKCanvas destination, CanvasElement element, AssetLibrary assets, double opacity)
    {
        if (destination.Context is not null || !destination.IsClipRect ||
            element.Kind is ElementKind.Image or ElementKind.Text or ElementKind.Number || ElementGeometry.IsLiveEffect(element.Kind)) return false;
        var matrix = destination.TotalMatrix;
        if (matrix.Persp0 != 0 || matrix.Persp1 != 0 || matrix.Persp2 != 1) return false;
        var target = destination.Surface;
        if (target is null) return false;
        using var pixels = target.PeekPixels();
        if (pixels is null || pixels.AlphaType != SKAlphaType.Premul || pixels.ColorSpace is not null ||
            pixels.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888)) return false;
        var info = pixels.Info;
        if ((long)info.Width * info.Height * 4 > Math.Min(CacheByteLimit, 64L * 1024 * 1024)) return false;
        var bounds = matrix.MapRect(ToRect(ElementGeometry.StrokedLocalBounds(element)));
        if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom)) return false;
        var clip = destination.DeviceClipBounds;
        int left = (int)Math.Floor(Math.Clamp((double)bounds.Left - 2, clip.Left, clip.Right));
        int top = (int)Math.Floor(Math.Clamp((double)bounds.Top - 2, clip.Top, clip.Bottom));
        int right = (int)Math.Ceiling(Math.Clamp((double)bounds.Right + 2, clip.Left, clip.Right));
        int bottom = (int)Math.Ceiling(Math.Clamp((double)bounds.Bottom + 2, clip.Top, clip.Bottom));
        if (right <= left || bottom <= top) return true;
        if (_elementOpacitySurface is null || _elementOpacityInfo != info)
        {
            _elementOpacitySurface?.Dispose();
            _elementOpacitySurface = SKSurface.Create(info);
            _elementOpacityInfo = info;
            if (_elementOpacitySurface is null) return false;
        }
        var region = new SKRect(left, top, right, bottom);
        var scratch = _elementOpacitySurface.Canvas;
        // Keep the original device origin, matrix and draw clip. Moving a SaveLayer's
        // origin to the local bounds changes antialias rasterization at fractional transforms.
        int scratchSaved = scratch.Save();
        try
        {
            scratch.ResetMatrix();
            scratch.ClipRect(region);
            scratch.Clear(SKColors.Transparent);
        }
        finally { scratch.RestoreToCount(scratchSaved); }
        scratchSaved = scratch.Save();
        try
        {
            scratch.ResetMatrix();
            scratch.ClipRect(new SKRect(clip.Left, clip.Top, clip.Right, clip.Bottom));
            scratch.SetMatrix(matrix);
            DrawLocalElementCore(scratch, element, assets, false);
        }
        finally { scratch.RestoreToCount(scratchSaved); }
        using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
        using var scratchPixels = _elementOpacitySurface.PeekPixels();
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
            destination.ClipRect(region);
            // Drawing the whole image at its device origin retains SaveLayer's blend path.
            // A cropped source/destination image rect uses a different 8-bit rounding path.
            destination.DrawImage(image, 0, 0, Nearest, alpha);
        }
        finally { destination.RestoreToCount(saved); }
        return true;
    }
}
