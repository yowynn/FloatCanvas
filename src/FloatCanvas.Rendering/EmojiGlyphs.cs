using System.Globalization;
using FloatCanvas.Core;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace FloatCanvas.Rendering;

/// <summary>Whole-grapheme color shaping with an ink box shared by editing, preview and export.</summary>
public static class EmojiGlyphs
{
    public const string FontFamily = "Segoe UI Emoji";
    private const int ReferenceSize = 256, Padding = 64, Capacity = 128;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Glyph> Cache = new(StringComparer.Ordinal);
    // All native font, shaper and cached blob access is serialized. There is only one font size.
    private static readonly Lazy<SKTypeface> Typeface = new(() => SKTypeface.FromFamilyName(FontFamily));
    private static readonly Lazy<SKFont> Font = new(() => new SKFont(Typeface.Value, ReferenceSize) { Edging = SKFontEdging.Antialias, Subpixel = true });
    private static readonly Lazy<SKShaper> Shaper = new(() => new SKShaper(Typeface.Value));
    private static long clock;

    public static bool IsEmoji(string text, ElementStyle style) =>
        string.Equals(style.FontFamily, FontFamily, StringComparison.OrdinalIgnoreCase) &&
        IsSingleGrapheme(text);

    public static TextMetrics Measure(string emoji, double fontSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emoji);
        if (!IsSingleGrapheme(emoji)) throw new ArgumentException("Expected a single emoji grapheme.", nameof(emoji));
        double scale = Math.Clamp(fontSize, 1, 2048) / ReferenceSize;
        lock (Gate)
        {
            var glyph = Get(emoji);
            return new(glyph.Ink.Width * scale, glyph.Ink.Height * scale, 1, glyph.GlyphCount, glyph.MissingCount);
        }
    }

    internal static void Draw(SKCanvas canvas, string emoji, double fontSize, SKPaint paint)
    {
        lock (Gate)
        {
            var glyph = Get(emoji);
            int saved = canvas.Save();
            try
            {
                float scale = (float)(Math.Clamp(fontSize, 1, 2048) / ReferenceSize);
                canvas.Scale(scale, scale);
                canvas.Translate(-glyph.Ink.Left, -glyph.Ink.Top);
                canvas.DrawText(glyph.Blob, 0, 0, paint);
            }
            finally { canvas.RestoreToCount(saved); }
        }
    }

    private static bool IsSingleGrapheme(string text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 32 &&
        StringInfo.GetNextTextElement(text).Length == text.Length;

    private static Glyph Get(string emoji)
    {
        clock++;
        if (Cache.TryGetValue(emoji, out var old)) { old.Used = clock; return old; }
        var shaped = Shaper.Value.Shape(emoji, Font.Value);
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(shaped.Codepoints.Select(g => checked((ushort)g)).ToArray(), Font.Value, shaped.Points);
        var blob = builder.Build() ?? throw new InvalidOperationException("The emoji could not be shaped.");
        try
        {
            int width = Math.Max(1, checked((int)Math.Ceiling(shaped.Width))) + Padding * 2;
            using var surface = SKSurface.Create(new SKImageInfo(width, ReferenceSize * 2 + Padding * 2, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("The emoji measurement buffer could not be created.");
            surface.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            surface.Canvas.DrawText(blob, Padding, ReferenceSize + Padding, paint);
            using var image = surface.Snapshot();
            using var pixels = SKBitmap.FromImage(image);
            int left = pixels.Width, top = pixels.Height, right = -1, bottom = -1;
            for (int y = 0; y < pixels.Height; y++)
                for (int x = 0; x < pixels.Width; x++)
                    if (pixels.GetPixel(x, y).Alpha > 0)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            if (right < left) throw new InvalidOperationException("The emoji has no visible glyph.");
            var ink = new SKRect(left - Padding, top - ReferenceSize - Padding, right + 1 - Padding, bottom + 1 - ReferenceSize - Padding);
            var glyph = new Glyph(blob, ink, shaped.Codepoints.Length, shaped.Codepoints.Count(g => g == 0), clock);
            Cache.Add(emoji, glyph);
            while (Cache.Count > Capacity)
            {
                var oldest = Cache.MinBy(pair => pair.Value.Used);
                Cache.Remove(oldest.Key); oldest.Value.Blob.Dispose();
            }
            return glyph;
        }
        catch { blob.Dispose(); throw; }
    }

    private sealed class Glyph(SKTextBlob blob, SKRect ink, int glyphCount, int missingCount, long used)
    {
        public SKTextBlob Blob { get; } = blob;
        public SKRect Ink { get; } = ink;
        public int GlyphCount { get; } = glyphCount;
        public int MissingCount { get; } = missingCount;
        public long Used { get; set; } = used;
    }
}
