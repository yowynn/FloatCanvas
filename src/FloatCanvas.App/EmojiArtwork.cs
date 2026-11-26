using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using FloatCanvas.Rendering;
using SkiaSharp;

namespace FloatCanvas.App;

/// <summary>Frozen color thumbnails that crop the font's ink while document rendering keeps the shared frame.</summary>
internal static class EmojiArtwork
{
    private const int Capacity = 192;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static BitmapSource? placeholder;
    private static long clock;
    private static long generation;
    internal static BitmapSource Placeholder => placeholder ??= CreatePlaceholder();

    static EmojiArtwork() => EmojiFontSources.Changed += Clear;

    internal static BitmapSource Image(string emoji, string? emojiFontId = null, bool allowFallback = true)
    {
        try { return ImageCore(emoji, emojiFontId, allowFallback); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or InvalidOperationException or OverflowException) { return Placeholder; }
    }

    private static BitmapSource ImageCore(string emoji, string? emojiFontId, bool allowFallback)
    {
        string fontId = FloatCanvas.Core.EmojiFontIds.Normalize(emojiFontId);
        string key = fontId + '\u001f' + (allowFallback ? '1' : '0') + '\u001f' + emoji;
        long startedGeneration;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                cached.Used = ++clock;
                return cached.Image;
            }
            startedGeneration = generation;
        }
        if (!allowFallback && !EmojiGlyphs.CanRender(emoji,fontId)) return Placeholder;
        const int width=96,height=96;
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        EmojiGlyphs.DrawPreviewFitted(canvas, emoji, width, height, paint, fontId);
        var frame = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null,
            bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        frame.Freeze();
        bool retry;
        lock (Gate)
        {
            retry = generation != startedGeneration;
            if (!retry && Cache.TryGetValue(key, out var cached))
            {
                cached.Used = ++clock;
                return cached.Image;
            }
            if (!retry)
            {
                Cache.Add(key, new(frame, ++clock));
                while (Cache.Count > Capacity)
                {
                    var oldest = Cache.MinBy(pair => pair.Value.Used);
                    Cache.Remove(oldest.Key);
                }
                return frame;
            }
        }
        return Image(emoji, fontId, allowFallback);
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            generation++;
        }
    }

    private sealed class CacheEntry(BitmapSource image, long used)
    {
        internal BitmapSource Image { get; } = image;
        internal long Used { get; set; } = used;
    }

    private static BitmapSource CreatePlaceholder()
    {
        using var bitmap=new SKBitmap(32,32,SKColorType.Bgra8888,SKAlphaType.Premul);
        using var canvas=new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        EmojiGlyphs.DrawMissingGlyph(canvas, bitmap.Width, bitmap.Height);
        var frame=BitmapSource.Create(bitmap.Width,bitmap.Height,96,96,PixelFormats.Pbgra32,null,
            bitmap.GetPixels(),bitmap.ByteCount,bitmap.RowBytes);
        frame.Freeze();
        return frame;
    }
}
