using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>A PNG and its exact, integer-aligned physical-pixel placement.</summary>
public sealed record CaptureRasterResult(byte[] PngBytes, RectD Bounds, bool HasVisiblePixels);

public sealed partial class SceneRenderer
{
    /// <summary>
    /// Renders a selection from a complete scene without changing its viewport or live-effect
    /// sampling context. The caller prepares the source scene, including its desired layer opacity.
    /// Display bounds are physical pixels; when provided, gaps between them remain transparent.
    /// </summary>
    public CaptureRasterResult ExportSelectionPng(SceneDocument document, AssetLibrary assets,
        CaptureSelection selection, bool includeBackground, IReadOnlyList<RectD>? physicalDisplays = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Shape) || !double.IsFinite(selection.BorderWidth) || selection.BorderWidth < 0)
            throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(selection));

        // Own the geometry for the entire render, including asynchronous callers' mutable lists.
        using var outline = CaptureOutline(selection with { Points = selection.Points.ToArray() });
        using var filled = outline.Simplify()
            ?? throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(selection));
        if (filled.IsEmpty || filled.TightBounds.Width <= 0 || filled.TightBounds.Height <= 0)
            throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(selection));
        var pathBounds = outline.TightBounds;
        double halfBorder = selection.BorderWidth / 2;
        double left = Math.Floor(pathBounds.Left - halfBorder), top = Math.Floor(pathBounds.Top - halfBorder);
        double right = Math.Ceiling(pathBounds.Right + halfBorder), bottom = Math.Ceiling(pathBounds.Bottom + halfBorder);
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right - left is < 1 or > int.MaxValue || bottom - top is < 1 or > int.MaxValue)
            throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
        int width = checked((int)(right - left)), height = checked((int)(bottom - top));
        // Skia's raster allocation and managed pixel spans use signed byte lengths.
        if ((long)width * height > int.MaxValue / 4)
            throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
        var bounds = new RectD(left, top, width, height);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        // Compose first, then apply the antialiased selection once. Clipping every individual
        // annotation at the boundary would accumulate alpha where several marks overlap.
        using var source = SKSurface.Create(info)
            ?? throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
        source.Canvas.Clear(SKColors.Transparent);
        source.Canvas.Translate(-(float)left, -(float)top);
        Draw(source.Canvas, document, assets, includeBackground);
        using var composed = source.Snapshot();
        using var output = SKSurface.Create(info)
            ?? throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
        var canvas = output.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-(float)left, -(float)top);
        if (physicalDisplays is not null)
        {
            using var displayBuilder = new SKPathBuilder { FillType = SKPathFillType.Winding };
            foreach (var display in physicalDisplays)
            {
                if (!Finite(display) || display.IsEmpty) continue;
                displayBuilder.AddRect(ToRect(display.Normalize()));
            }
            using var displayPath = displayBuilder.Detach();
            canvas.ClipPath(displayPath, antialias: false);
        }
        int saved = canvas.Save();
        canvas.ClipPath(outline, antialias: true);
        canvas.DrawImage(composed, (float)left, (float)top, Nearest);
        canvas.RestoreToCount(saved);
        if (selection.BorderWidth > 0 && (selection.BorderColor >> 24) != 0)
        {
            using var border = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(selection.BorderColor),
                StrokeWidth = (float)selection.BorderWidth, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawPath(outline, border);
        }
        using var image = output.Snapshot();
        bool visible = HasAlpha(image);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException(L.Get("Images.EncodingFailed"));
        return new(png.ToArray(), bounds, visible);
    }

    private static SKPath CaptureOutline(CaptureSelection selection)
    {
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        if (selection.Shape is CaptureShape.Rectangle or CaptureShape.Ellipse && selection.Points.Count == 0)
        {
            if (!Finite(selection.Bounds) || selection.Bounds.IsEmpty)
                throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(selection));
            var bounds = ToRect(selection.Bounds.Normalize());
            if (selection.Shape == CaptureShape.Ellipse) builder.AddOval(bounds);
            else builder.AddRect(bounds);
        }
        else
        {
            if (selection.Points.Count < 3 || selection.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(selection));
            builder.MoveTo(ToPoint(selection.Points[0]));
            foreach (var point in selection.Points.Skip(1)) builder.LineTo(ToPoint(point));
            builder.Close();
        }
        return builder.Detach();
    }

    private static bool Finite(RectD bounds) => double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);

    private static bool HasAlpha(SKImage image)
    {
        using var pixels = image.PeekPixels()
            ?? throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
        var bytes = pixels.GetPixelSpan();
        for (int y = 0; y < pixels.Height; y++)
        {
            int start = y * pixels.RowBytes;
            for (int x = 0; x < pixels.Width; x++)
                if (bytes[start + x * 4 + 3] != 0) return true;
        }
        return false;
    }
}
