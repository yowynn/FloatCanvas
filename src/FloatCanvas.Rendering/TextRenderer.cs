using System.Text;
using System.Globalization;
using FloatCanvas.Core;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using HarfBuzzSharp;

namespace FloatCanvas.Rendering;

public readonly record struct TextMetrics(double Width, double Height, int LineCount, int GlyphCount, int MissingGlyphCount);

/// <summary>Shared shaped glyphs, Unicode wrapping and visual caret geometry for drawing and editing.</summary>
internal sealed partial class TextRenderer : IDisposable
{
    private readonly Dictionary<FontKey, FontEntry> _fonts = [];
    private readonly Dictionary<LayoutKey, Layout> _layouts = [];
    private long _clock;

    public TextMetrics Measure(string text, ElementStyle style, double width) => GetLayout(text, style, width).Metrics;
    public TextEditingGeometry EditingGeometry(string text, ElementStyle style, double width) => GetLayout(text, style, width).Geometry;
    public void Draw(SKCanvas canvas, string text, ElementStyle style, double width, SKPaint paint)
    {
        foreach (var run in GetLayout(text, style, width).Runs) canvas.DrawText(run.Blob, run.X, run.Y, paint);
    }

    private Layout GetLayout(string text, ElementStyle style, double width) =>
        GetLayout(new(text, style.FontFamily, Math.Clamp(style.FontSize, 1, ElementStyle.MaximumFontSize), Math.Max(0, width), style.TextAlignment, style.Variant));

    private Layout GetLayout(LayoutKey key)
    {
        _clock++;
        if (_layouts.TryGetValue(key, out var cached)) { cached.Used = _clock; return cached; }
        string text = key.Text;
        var primary = GetFont(new(key.Family, key.Size));
        var normalized = new StringBuilder(); var originalIndices = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; character = '\n'; }
            normalized.Append(character); originalIndices.Add(i + 1);
        }
        var runs = new List<TextRun>(); var editing = new List<TextEditingLine>();
        var insets = Insets.For(key.Variant, key.Size);
        double contentWidth = insets.InnerWidth(key.Width);
        float y = (float)insets.Top, maxWidth = 0; int glyphCount = 0, missing = 0, offset = 0;
        try
        {
            foreach (string paragraph in normalized.ToString().Split('\n'))
            {
                var paragraphShape = ShapeLine(paragraph, primary);
                foreach (var (line, start) in Wrap(paragraph, paragraphShape, contentWidth, primary))
                {
                    var shaped = line.Length == paragraph.Length ? paragraphShape : ShapeLine(line, primary, paragraphShape.Level);
                    float ascent = -primary.Font.Metrics.Ascent, descent = primary.Font.Metrics.Descent;
                    foreach (var run in shaped.Runs)
                    {
                        ascent = Math.Max(ascent, -run.Font.Font.Metrics.Ascent);
                        descent = Math.Max(descent, run.Font.Font.Metrics.Descent);
                    }
                    float x = key.Alignment switch
                    {
                        TextAlignment.Center when contentWidth > 0 => ((float)contentWidth - shaped.Width) / 2,
                        TextAlignment.Right when contentWidth > 0 => (float)contentWidth - shaped.Width,
                        _ => 0
                    };
                    x += (float)insets.Horizontal;
                    float height = Math.Max((float)key.Size * 1.35f, ascent + descent);
                    var stops = new List<TextCaretStop>(); var spans = new List<TextVisualSpan>();
                    if (shaped.Runs.Count == 0) stops.Add(new(originalIndices[offset + start], x));
                    foreach (var run in shaped.Runs)
                    {
                        if (run.Shape is { Codepoints.Length: > 0 } shape)
                        {
                            using var builder = new SKTextBlobBuilder();
                            builder.AddPositionedRun(shape.Codepoints.Select(g => checked((ushort)g)).ToArray(), run.Font.Font, shape.Points);
                            if (builder.Build() is { } blob) runs.Add(new(blob, x + run.X, y + ascent));
                            glyphCount += shape.Codepoints.Length; missing += shape.Codepoints.Count(g => g == 0);
                        }
                        foreach (var span in run.Spans)
                        {
                            int first = originalIndices[offset + start + span.Start], last = originalIndices[offset + start + span.End];
                            double left = x + run.X + span.Left, right = x + run.X + span.Right;
                            spans.Add(new(first, last, left, right, run.Rtl));
                            stops.Add(new(first, run.Rtl ? right : left));
                            stops.Add(new(last, run.Rtl ? left : right));
                        }
                    }
                    int end = offset + start + line.Length;
                    if (end == offset + paragraph.Length && end < normalized.Length)
                    {
                        // A hard break has a selectable footprint, but adds no glyph, width or extra caret.
                        bool rtl = (paragraphShape.Level & 1) != 0;
                        double marker = Math.Max(2, key.Size / 4), edge = rtl ? x : x + shaped.Width;
                        spans.Add(new(originalIndices[end], originalIndices[end + 1], rtl ? edge - marker : edge,
                            rtl ? edge : edge + marker, rtl));
                    }
                    // Keep logical order for line lookup; bidi boundaries may have two visual affinities.
                    editing.Add(new(y, height, stops.Distinct().OrderBy(stop => stop.Index).ToArray(), spans));
                    maxWidth = Math.Max(maxWidth, shaped.Width); y += height;
                }
                offset += paragraph.Length + 1;
            }
            var layout = new Layout(runs, new(maxWidth + insets.Horizontal * 2, y + insets.Bottom, editing.Count, glyphCount, missing), new(editing), _clock);
            _layouts.Add(key, layout);
            while (_layouts.Count > 256)
            {
                var oldest = _layouts.MinBy(p => p.Value.Used); oldest.Value.Dispose(); _layouts.Remove(oldest.Key);
            }
            while (_fonts.Count > 256)
            {
                var oldest = _fonts.MinBy(p => p.Value.Used); oldest.Value.Dispose(); _fonts.Remove(oldest.Key);
            }
            return layout;
        }
        catch { foreach (var run in runs) run.Blob.Dispose(); throw; }
    }

    private ShapedLine ShapeLine(string line, FontEntry primary, byte level = 0xfe)
    {
        var direction = UnicodeTextAnalysis.VisualRuns(line, level);
        var runs = new List<ShapedRun>(); float x = 0;
        // Tab stops are measured from the leading edge of the visual line.
        bool fromRight = (direction.Level & 1) != 0 && line.Contains('\t');
        foreach (var bidi in fromRight ? direction.Runs.Reverse() : direction.Runs)
        {
            var segments = Segment(line.Substring(bidi.Start, bidi.Length), primary).ToArray();
            foreach (var segment in bidi.RightToLeft != fromRight ? segments.Reverse() : segments)
            {
                if (segment.Text == "\t")
                {
                    float advance = (float)TabAdvance(x, primary.TabPitch);
                    int index = bidi.Start + segment.Start;
                    runs.Add(new(primary, null, x, bidi.RightToLeft, [new(index, index + 1, 0, advance)]));
                    x += advance;
                    continue;
                }
                using var buffer = new HarfBuzzSharp.Buffer();
                buffer.AddUtf16(segment.Text); buffer.GuessSegmentProperties();
                buffer.Direction = bidi.RightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
                var shape = segment.Font.Shaper.Shape(buffer, segment.Font.Font);
                var positions = buffer.GetGlyphPositionSpan();
                var clusterBounds = new Dictionary<int, (float Left, float Right)>(); float pen = 0;
                for (int i = 0; i < shape.Clusters.Length; i++)
                {
                    int cluster = checked((int)shape.Clusters[i]);
                    float next = pen + positions[i].XAdvance * segment.Font.Font.Size * segment.Font.Font.ScaleX / 512;
                    if (clusterBounds.TryGetValue(cluster, out var bounds)) clusterBounds[cluster] = (Math.Min(bounds.Left, pen), Math.Max(bounds.Right, next));
                    else clusterBounds[cluster] = (pen, next);
                    pen = next;
                }
                var boundaries = StringInfo.ParseCombiningCharacters(segment.Text).Append(segment.Text.Length).ToArray();
                var clusterStarts = clusterBounds.Keys.Order().Append(segment.Text.Length).ToArray();
                var spans = new List<TextVisualSpan>(); int boundary = 0;
                for (int c = 0; c + 1 < clusterStarts.Length; c++)
                {
                    int first = clusterStarts[c], end = clusterStarts[c + 1];
                    while (boundary < boundaries.Length && boundaries[boundary] < first) boundary++;
                    int startBoundary = boundary;
                    while (boundary < boundaries.Length && boundaries[boundary] < end) boundary++;
                    int count = boundary - startBoundary; var bounds = clusterBounds[first];
                    for (int b = 0; b < count; b++)
                    {
                        int visual = bidi.RightToLeft ? count - b - 1 : b;
                        float left = bounds.Left + (bounds.Right - bounds.Left) * visual / count;
                        float right = bounds.Left + (bounds.Right - bounds.Left) * (visual + 1) / count;
                        spans.Add(new(bidi.Start + segment.Start + boundaries[startBoundary + b],
                            bidi.Start + segment.Start + boundaries[startBoundary + b + 1], left, right));
                    }
                }
                runs.Add(new(segment.Font, shape, x, bidi.RightToLeft, spans)); x += shape.Width;
            }
        }
        if (fromRight)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                float width = run.Shape?.Width ?? (float)run.Spans[0].Right;
                runs[i] = run with { X = x - run.X - width };
            }
            runs.Reverse();
        }
        return new(direction.Level, runs, x);
    }

    private static double TabAdvance(double x, double pitch) =>
        (Math.Floor(x / pitch + 1e-7) + 1) * pitch - x;

    private static IEnumerable<(string Text, int Start)> Wrap(string text, ShapedLine shaped, double width, FontEntry primary)
    {
        if (width <= 0 || text.Length == 0 || shaped.Width <= width) { yield return (text, 0); yield break; }
        var paragraph = new ParagraphWidths(text, shaped.Runs.SelectMany(run => run.Spans), 0,
            text.Contains('\t') ? primary.TabPitch : 0);
        foreach (var line in paragraph.Lines(width)) yield return (text[line.Start..line.End], line.Start);
    }

    private IEnumerable<(FontEntry Font, string Text, int Start)> Segment(string text, FontEntry primary)
    {
        FontEntry? active = null; Script script = Script.Unknown;
        var content = new StringBuilder(); int start = 0, offset = 0;
        var characters = StringInfo.GetTextElementEnumerator(text);
        while (characters.MoveNext())
        {
            string character = characters.GetTextElement();
            if (character == "\t")
            {
                if (active is not null) yield return (active, content.ToString(), start);
                yield return (primary, character, offset);
                content.Clear(); active = null; script = Script.Unknown; start = ++offset;
                continue;
            }
            var rune = character.EnumerateRunes().First();
            var font = Resolve(primary, rune.Value); Script nextScript = UnicodeFunctions.Default.GetScript(rune.Value);
            bool specific = nextScript != Script.Common && nextScript != Script.Inherited;
            if (active is not null && (active != font || specific && script != Script.Unknown && script != nextScript))
            {
                yield return (active, content.ToString(), start); content.Clear(); start = offset; script = Script.Unknown;
            }
            active = font; if (specific) script = nextScript;
            content.Append(character); offset += character.Length;
        }
        if (active is not null) yield return (active, content.ToString(), start);
    }

    private FontEntry Resolve(FontEntry primary, int rune)
    {
        primary.Used = _clock;
        if (primary.Font.ContainsGlyph(rune)) return primary;
        using var fallback = SKFontManager.Default.MatchCharacter(primary.Typeface.FamilyName, ["zh-Hans", "en"], rune);
        return fallback is null ? primary : GetFont(new(fallback.FamilyName, primary.Font.Size));
    }
    private FontEntry GetFont(FontKey key)
    {
        if (_fonts.TryGetValue(key, out var cached)) { cached.Used = _clock; return cached; }
        var typeface = SKTypeface.FromFamilyName(key.Family) ?? SKTypeface.FromFamilyName("Segoe UI");
        var entry = new FontEntry(typeface, new SKFont(typeface, (float)key.Size) { Edging = SKFontEdging.Antialias, Subpixel = true }, new SKShaper(typeface), _clock);
        _fonts.Add(key, entry); return entry;
    }
    public void Dispose()
    {
        foreach (var layout in _layouts.Values) layout.Dispose(); foreach (var font in _fonts.Values) font.Dispose();
        _layouts.Clear(); _fonts.Clear();
    }
    private readonly record struct FontKey(string Family, double Size);
    private readonly record struct LayoutKey(string Text, string Family, double Size, double Width, TextAlignment Alignment, string? Variant = ElementAppearance.Default);
    private sealed record TextRun(SKTextBlob Blob, float X, float Y);
    private sealed record ShapedLine(byte Level, List<ShapedRun> Runs, float Width);
    private sealed record ShapedRun(FontEntry Font, SKShaper.Result? Shape, float X, bool Rtl, List<TextVisualSpan> Spans);
    private sealed class Layout(List<TextRun> runs, TextMetrics metrics, TextEditingGeometry geometry, long used) : IDisposable
    {
        public List<TextRun> Runs { get; } = runs;
        public TextMetrics Metrics { get; } = metrics;
        public TextEditingGeometry Geometry { get; } = geometry;
        public FitParagraph[]? FitParagraphs { get; set; }
        public long Used { get; set; } = used;
        public void Dispose() { foreach (var run in Runs) run.Blob.Dispose(); }
    }
    private sealed class FontEntry(SKTypeface typeface, SKFont font, SKShaper shaper, long used) : IDisposable
    {
        public SKTypeface Typeface { get; } = typeface;
        public SKFont Font { get; } = font;
        public SKShaper Shaper { get; } = shaper;
        private float? tabPitch;
        public float TabPitch => tabPitch ??= Math.Max(.001f, 4 * Shaper.Shape(" ", Font).Width);
        public long Used { get; set; } = used;
        public void Dispose() { Shaper.Dispose(); Font.Dispose(); Typeface.Dispose(); }
    }
}
