using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

/// <summary>Frozen color previews rendered with exactly the same glyph path as the document.</summary>
internal static class EmojiArtwork
{
    internal static IReadOnlyList<string> BuiltIn { get; } = Array.AsReadOnly(new[]
    {
        "😀", "😃", "😄", "😁", "😆", "😅", "😂", "😊", "😍", "🥰", "😎", "🤔", "😮", "😢", "😭",
        "😡", "🥳", "😴", "👍", "👎", "👏", "🙌", "🙏", "💪", "❤️", "💛", "💚", "💙", "⭐", "🔥"
    });
    private const int Capacity = 64;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();

    internal static BitmapSource Image(string emoji)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(emoji, out var cached)) return cached;
            var metrics = EmojiGlyphs.Measure(emoji, 96);
            var scene = new SceneDocument
            {
                Viewport = new(0, 0, Math.Ceiling(metrics.Width), Math.Ceiling(metrics.Height)),
                Elements = [new() { Kind = ElementKind.Text, Text = emoji, Width = metrics.Width, Height = metrics.Height,
                    Style = new() { FontFamily = EmojiGlyphs.FontFamily, FontSize = 96, Stroke = 0xFFFFFFFF } }]
            };
            using var renderer = new SceneRenderer();
            using var stream = new MemoryStream(renderer.ExportPng(scene, new(), true));
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze(); Cache.Add(emoji, frame); Order.Enqueue(emoji);
            while (Cache.Count > Capacity) Cache.Remove(Order.Dequeue());
            return frame;
        }
    }
}
