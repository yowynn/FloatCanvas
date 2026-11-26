using FloatCanvas.Core;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace FloatCanvas.Rendering;

/// <summary>Whole-grapheme color shaping with an ink box shared by editing, preview and export.</summary>
public static class EmojiGlyphs
{
    private const int ReferenceSize = 256, Padding = 64, Capacity = 512;
    internal const float GlobalFrameSideEm = EmojiBoundsProfile.SideEm;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Glyph> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> Support = new(StringComparer.Ordinal);
    private static long clock;

    public static TextMetrics Measure(string emoji, double fontSize, string? emojiFontId = null)
    {
        double scale = Math.Clamp(fontSize, 1, ElementStyle.MaximumFontSize) / ReferenceSize;
        lock (Gate)
        {
            var glyph = Get(emoji, EmojiFontIds.Normalize(emojiFontId));
            return new(glyph.Safe.Width * scale, glyph.Safe.Height * scale, 1, glyph.GlyphCount, glyph.MissingCount);
        }
    }

    public static bool CanRender(string emoji, string? emojiFontId = null)
    {
        string fontId = EmojiFontIds.Normalize(emojiFontId);
        if (!EmojiCatalog.TryFind(emoji, out var entry)) return false;
        string key = fontId + '\u001f' + entry.Glyph;
        lock (Gate)
        {
            if (Support.TryGetValue(key, out bool supported)) return supported;
            var exact = ShapeExact(fontId, entry);
            supported = exact is not null;
            exact?.Blob.Dispose();
            Support.Add(key, supported);
            return supported;
        }
    }

    internal static void DrawFitted(SKCanvas canvas, string emoji, double width, double height, SKPaint paint, string? emojiFontId = null)
    {
        lock (Gate)
        {
            var glyph = Get(emoji, EmojiFontIds.Normalize(emojiFontId));
            if (glyph.Blob is null)
            {
                DrawMissingGlyph(canvas, width, height);
                return;
            }
            int saved = canvas.Save();
            try
            {
                float scaleX = (float)(width / glyph.Safe.Width);
                float scaleY = (float)(height / glyph.Safe.Height);
                float fit = FitScale(glyph.Ink, glyph.Safe);
                canvas.Translate((float)(width / 2), (float)(height / 2));
                canvas.Scale(scaleX * fit, scaleY * fit);
                canvas.Translate(-glyph.Safe.MidX, -glyph.Safe.MidY);
                canvas.DrawText(glyph.Blob, 0, 0, paint);
            }
            finally { canvas.RestoreToCount(saved); }
        }
    }

    /// <summary>UI thumbnails crop to the visible ink while document rendering retains the global frame.</summary>
    internal static void DrawPreviewFitted(SKCanvas canvas, string emoji, double width, double height, SKPaint paint, string? emojiFontId = null)
    {
        lock (Gate)
        {
            var glyph = Get(emoji, EmojiFontIds.Normalize(emojiFontId));
            if (glyph.Blob is null)
            {
                DrawMissingGlyph(canvas, width, height);
                return;
            }
            if (glyph.Ink.Width <= 0 || glyph.Ink.Height <= 0) return;
            float available = (float)Math.Min(width, height) * .84f;
            float scale = Math.Min(available / glyph.Ink.Width, available / glyph.Ink.Height);
            int saved = canvas.Save();
            try
            {
                canvas.Translate((float)(width / 2), (float)(height / 2));
                canvas.Scale(scale, scale);
                canvas.Translate(-glyph.Ink.MidX, -glyph.Ink.MidY);
                canvas.DrawText(glyph.Blob, 0, 0, paint);
            }
            finally { canvas.RestoreToCount(saved); }
        }
    }

    private static Glyph Get(string emoji, string emojiFontId)
    {
        if (!EmojiCatalog.TryFind(emoji, out var entry)) throw new ArgumentException("Expected one supported emoji.", nameof(emoji));
        emoji = entry.Glyph;
        string key = emojiFontId + '\u001f' + emoji;
        clock++;
        if (Cache.TryGetValue(key, out var old)) { old.Used = clock; return old; }
        SKTextBlob? blob = null;
        SKShaper.Result? shaped = null;
        foreach (string sourceId in EmojiFontSources.ResolveChain(emojiFontId))
        {
            if (ShapeExact(sourceId, entry) is not { } result) continue;
            blob = result.Blob;
            shaped = result.Shaped;
            break;
        }
        try
        {
            var ink = blob is not null && shaped is not null ? MeasureInk(blob, shaped.Width) : SKRect.Empty;
            var safe = GlobalFrame(emojiFontId);
            var glyph = new Glyph(blob, ink, safe, shaped?.Codepoints.Length ?? 0, shaped?.Codepoints.Count(g => g == 0) ?? 1, clock);
            Cache.Add(key, glyph);
            while (Cache.Count > Capacity)
            {
                var oldest = Cache.MinBy(pair => pair.Value.Used);
                Cache.Remove(oldest.Key); oldest.Value.Blob?.Dispose();
            }
            return glyph;
        }
        catch { blob?.Dispose(); throw; }
    }

    public static void ClearCache()
    {
        lock (Gate)
        {
            foreach (var glyph in Cache.Values) glyph.Blob?.Dispose();
            Cache.Clear();
            Support.Clear();
        }
    }

    internal static SKRect GlobalFrame(string? emojiFontId = null)
        => EmojiBoundsProfile.Frame(emojiFontId, ReferenceSize);

    internal static void DrawMissingGlyph(SKCanvas canvas, double width, double height)
    {
        float side = (float)Math.Min(width, height);
        int saved = canvas.Save();
        try
        {
            canvas.Translate((float)((width - side) / 2), (float)((height - side) / 2));
            canvas.Scale(side / 32);
            using var border = new SKPaint { Color = new SKColor(0xFF8792A6), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            using var fill = new SKPaint { Color = new SKColor(0x225E6A7D), IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(3, 3, 29, 29), 7, 7, fill);
            canvas.DrawRoundRect(new SKRect(3, 3, 29, 29), 7, 7, border);
            using var typeface = SKTypeface.FromFamilyName("Segoe UI");
            using var font = new SKFont(typeface, 14) { Edging = SKFontEdging.Antialias };
            using var ink = new SKPaint { Color = new SKColor(0xFFD1D6E0), IsAntialias = true };
            font.MeasureText("?", out var bounds);
            canvas.DrawText("?", (32 - bounds.Width) / 2 - bounds.Left,
                (32 - bounds.Height) / 2 - bounds.Top, SKTextAlign.Left, font, ink);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    /// <summary>Keeps every visible ink edge inside the frame by scaling around the frame center.</summary>
    internal static float FitScale(SKRect ink, SKRect frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || ink.IsEmpty) return 1;
        float centerX = frame.MidX, centerY = frame.MidY;
        float scale = 1;
        double halfWidth = frame.Width / 2d, halfHeight = frame.Height / 2d;
        if (ink.Left < centerX) scale = Math.Min(scale, (float)(halfWidth / (centerX - ink.Left)));
        if (ink.Right > centerX) scale = Math.Min(scale, (float)(halfWidth / (ink.Right - centerX)));
        if (ink.Top < centerY) scale = Math.Min(scale, (float)(halfHeight / (centerY - ink.Top)));
        if (ink.Bottom > centerY) scale = Math.Min(scale, (float)(halfHeight / (ink.Bottom - centerY)));
        return Math.Clamp(scale, 0, 1);
    }

    private static SKRect MeasureInk(SKTextBlob blob, float advance)
    {
        int padding = Padding;
        for (int attempt = 0; attempt < 6; attempt++, padding *= 2)
        {
            int width = Math.Max(1, checked((int)Math.Ceiling(advance))) + padding * 2;
            int height = ReferenceSize * 2 + padding * 2;
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("The emoji measurement buffer could not be created.");
            surface.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            surface.Canvas.DrawText(blob, padding, ReferenceSize + padding, paint);
            using var pixels = surface.PeekPixels();
            var bytes = pixels.GetPixelSpan();
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * pixels.RowBytes;
                for (int x = 0; x < width; x++)
                {
                    if (bytes[row + x * 4 + 3] == 0) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right < left) throw new InvalidOperationException("The emoji has no visible glyph.");
            if (left > 0 && top > 0 && right < width - 1 && bottom < height - 1)
                return new SKRect(
                    left - padding,
                    top - ReferenceSize - padding,
                    right + 1 - padding,
                    bottom + 1 - ReferenceSize - padding);
        }
        throw new InvalidOperationException("The emoji ink could not be measured without touching the safety bounds.");
    }

    private static (SKTextBlob Blob, SKShaper.Result Shaped)? ShapeExact(string fontId, EmojiEntry entry)
    {
        try
        {
            if (!EmojiFontSources.TryWithFace<(SKTextBlob Blob, SKShaper.Result Shaped)?>(fontId, face =>
                {
                    var shaped = face.Shaper.Shape(entry.Glyph, face.Font);
                    bool systemFlag = fontId == EmojiFontIds.SystemSegoe && entry.Group == EmojiGroup.Flags &&
                        (char.ConvertToUtf32(entry.Glyph, 0) is >= 0x1f1e6 and <= 0x1f1ff || entry.Glyph.EndsWith("\U000e007f", StringComparison.Ordinal));
                    if (systemFlag || shaped.Codepoints is not [not 0]) return null;
                    using var builder = new SKTextBlobBuilder();
                    builder.AddPositionedRun(shaped.Codepoints.Select(glyph => checked((ushort)glyph)).ToArray(), face.Font, shaped.Points);
                    var blob = builder.Build();
                    return blob is null ? null : (blob, shaped);
                }, out var result)) return null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private sealed class Glyph(SKTextBlob? blob, SKRect ink, SKRect safe, int glyphCount, int missingCount, long used)
    {
        public SKTextBlob? Blob { get; } = blob;
        public SKRect Ink { get; } = ink;
        public SKRect Safe { get; } = safe;
        public int GlyphCount { get; } = glyphCount;
        public int MissingCount { get; } = missingCount;
        public long Used { get; set; } = used;
    }
}
