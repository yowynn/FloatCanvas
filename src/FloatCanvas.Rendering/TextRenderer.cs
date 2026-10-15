using System.Text;
using FloatCanvas.Core;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace FloatCanvas.Rendering;

public readonly record struct TextMetrics(double Width, double Height, int LineCount, int GlyphCount, int MissingGlyphCount);

/// <summary>HarfBuzz shaped text with per-rune font fallback and cached immutable glyph blobs.</summary>
internal sealed class TextRenderer : IDisposable
{
    private readonly Dictionary<FontKey, FontEntry> _fonts = [];
    private readonly Dictionary<LayoutKey, Layout> _layouts = [];
    private long _clock;

    public TextMetrics Measure(string text, ElementStyle style, double width) => GetLayout(text, style, width).Metrics;

    public void Draw(SKCanvas canvas, string text, ElementStyle style, double width, SKPaint paint)
    {
        var layout = GetLayout(text, style, width);
        foreach (var run in layout.Runs) canvas.DrawText(run.Blob, run.X, run.Y, paint);
    }

    private Layout GetLayout(string text, ElementStyle style, double width)
    {
        _clock++;
        var key = new LayoutKey(text, style.FontFamily, Math.Clamp(style.FontSize, 1, 2048), Math.Max(0, width), style.TextAlignment);
        if (_layouts.TryGetValue(key, out var cached)) { cached.Used = _clock; return cached; }
        var primary = GetFont(new(key.Family, key.Size));
        var lines = Wrap(text.Replace("\r\n", "\n").Replace('\r', '\n'), primary, key.Width);
        var runs = new List<TextRun>();
        float y = 0, maxWidth = 0;
        int glyphCount = 0, missing = 0;
        try
        {
            foreach (string line in lines)
            {
                var shaped = new List<(FontEntry Font, SKShaper.Result Shape)>();
                float lineWidth = 0, ascent = -primary.Font.Metrics.Ascent, descent = primary.Font.Metrics.Descent;
                foreach (var (font, content) in Segment(line, primary))
                {
                    var result = font.Shaper.Shape(content, font.Font);
                    shaped.Add((font, result));
                    lineWidth += result.Width;
                    ascent = Math.Max(ascent, -font.Font.Metrics.Ascent);
                    descent = Math.Max(descent, font.Font.Metrics.Descent);
                    glyphCount += result.Codepoints.Length;
                    missing += result.Codepoints.Count(g => g == 0);
                }
                float x = key.Alignment switch
                {
                    TextAlignment.Center when key.Width > 0 => ((float)key.Width - lineWidth) / 2,
                    TextAlignment.Right when key.Width > 0 => (float)key.Width - lineWidth,
                    _ => 0
                };
                foreach (var (font, shape) in shaped)
                {
                    if (shape.Codepoints.Length > 0)
                    {
                        using var builder = new SKTextBlobBuilder();
                        ushort[] glyphs = shape.Codepoints.Select(g => checked((ushort)g)).ToArray();
                        builder.AddPositionedRun(glyphs, font.Font, shape.Points);
                        var blob = builder.Build();
                        if (blob is not null) runs.Add(new(blob, x, y + ascent));
                    }
                    x += shape.Width;
                }
                maxWidth = Math.Max(maxWidth, lineWidth);
                y += Math.Max((float)key.Size * 1.35f, ascent + descent);
            }
            var layout = new Layout(runs, new(maxWidth, y, lines.Count, glyphCount, missing), _clock);
            _layouts.Add(key, layout);
            while (_layouts.Count > 256)
            {
                var oldest = _layouts.MinBy(p => p.Value.Used);
                oldest.Value.Dispose(); _layouts.Remove(oldest.Key);
            }
            while (_fonts.Count > 256)
            {
                var oldest = _fonts.MinBy(p => p.Value.Used);
                oldest.Value.Dispose(); _fonts.Remove(oldest.Key);
            }
            return layout;
        }
        catch { foreach (var run in runs) run.Blob.Dispose(); throw; }
    }

    private List<string> Wrap(string text, FontEntry primary, double width)
    {
        var lines = new List<string>();
        foreach (string sourceLine in text.Split('\n'))
        {
            if (width <= 0 || sourceLine.Length == 0) { lines.Add(sourceLine); continue; }
            var current = new StringBuilder();
            float advance = 0;
            foreach (var rune in sourceLine.EnumerateRunes())
            {
                string character = rune.ToString();
                var font = Resolve(primary, rune.Value);
                float next = font.Font.MeasureText(character);
                if (current.Length > 0 && advance + next > width)
                {
                    lines.Add(current.ToString()); current.Clear(); advance = 0;
                }
                current.Append(character); advance += next;
            }
            lines.Add(current.ToString());
        }
        return lines;
    }

    private IEnumerable<(FontEntry Font, string Text)> Segment(string line, FontEntry primary)
    {
        FontEntry? active = null;
        var content = new StringBuilder();
        foreach (var rune in line.EnumerateRunes())
        {
            var font = Resolve(primary, rune.Value);
            if (active is not null && active != font)
            {
                yield return (active, content.ToString()); content.Clear();
            }
            active = font; content.Append(rune.ToString());
        }
        if (active is not null) yield return (active, content.ToString());
    }

    private FontEntry Resolve(FontEntry primary, int rune)
    {
        primary.Used = _clock;
        if (primary.Font.ContainsGlyph(rune)) return primary;
        using var fallback = SKFontManager.Default.MatchCharacter(primary.Typeface.FamilyName, ["zh-Hans", "en"], rune);
        if (fallback is null) return primary;
        return GetFont(new(fallback.FamilyName, primary.Font.Size));
    }

    private FontEntry GetFont(FontKey key)
    {
        if (_fonts.TryGetValue(key, out var cached)) { cached.Used = _clock; return cached; }
        var typeface = SKTypeface.FromFamilyName(key.Family) ?? SKTypeface.FromFamilyName("Segoe UI");
        var entry = new FontEntry(typeface, new SKFont(typeface, (float)key.Size) { Edging = SKFontEdging.Antialias, Subpixel = true }, new SKShaper(typeface), _clock);
        _fonts.Add(key, entry);
        return entry;
    }

    public void Dispose()
    {
        foreach (var layout in _layouts.Values) layout.Dispose();
        foreach (var font in _fonts.Values) font.Dispose();
        _layouts.Clear(); _fonts.Clear();
    }

    private readonly record struct FontKey(string Family, double Size);
    private readonly record struct LayoutKey(string Text, string Family, double Size, double Width, TextAlignment Alignment);
    private sealed record TextRun(SKTextBlob Blob, float X, float Y);
    private sealed class Layout(List<TextRun> runs, TextMetrics metrics, long used) : IDisposable
    {
        public List<TextRun> Runs { get; } = runs;
        public TextMetrics Metrics { get; } = metrics;
        public long Used { get; set; } = used;
        public void Dispose() { foreach (var run in Runs) run.Blob.Dispose(); }
    }
    private sealed class FontEntry(SKTypeface typeface, SKFont font, SKShaper shaper, long used) : IDisposable
    {
        public SKTypeface Typeface { get; } = typeface;
        public SKFont Font { get; } = font;
        public SKShaper Shaper { get; } = shaper;
        public long Used { get; set; } = used;
        public void Dispose() { Shaper.Dispose(); Font.Dispose(); Typeface.Dispose(); }
    }
}
