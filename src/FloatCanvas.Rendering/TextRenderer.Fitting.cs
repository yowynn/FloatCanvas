using System.Globalization;
using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

internal sealed partial class TextRenderer
{
    // Grapheme advances and legal break positions are shared by wrapping and the frame-size search.
    private sealed class ParagraphWidths
    {
        private readonly int[] boundaries;
        private readonly double[] advances;
        private readonly bool[] breakAfter;
        private readonly double tabPitch;

        public ParagraphWidths(string text, IEnumerable<TextVisualSpan> spans, int sourceStart, double tabPitch)
        {
            this.tabPitch = tabPitch;
            boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
            advances = new double[boundaries.Length - 1];
            breakAfter = new bool[advances.Length];
            if (text.Length == 0) return;
            var widths = spans.Where(s => s.Start < sourceStart + text.Length)
                .ToDictionary(s => s.Start - sourceStart, s => s.Right - s.Left);
            var breaks = UnicodeTextAnalysis.LineBreaks(text);
            for (int i = 0; i < advances.Length; i++)
            {
                advances[i] = text[boundaries[i]] == '\t' ? -1 : widths.GetValueOrDefault(boundaries[i]);
                breakAfter[i] = breaks.Contains(boundaries[i + 1]);
            }
        }

        public IEnumerable<(int Start, int End, double Width)> Lines(double width)
        {
            if (advances.Length == 0) yield return (0, 0, 0);
            int start = 0;
            while (start < advances.Length)
            {
                int end = start, lastBreak = start;
                double advance = 0, breakWidth = 0;
                while (end < advances.Length)
                {
                    double next = advances[end] < 0 ? TabAdvance(advance, tabPitch) : advances[end];
                    if (end > start && advance + next > width) break;
                    advance += next;
                    end++;
                    if (breakAfter[end - 1]) { lastBreak = end; breakWidth = advance; }
                }
                if (end < advances.Length && lastBreak > start) { end = lastBreak; advance = breakWidth; }
                yield return (boundaries[start], boundaries[end], advance);
                start = end;
            }
        }
    }

    private sealed record FitParagraph(ParagraphWidths Widths, double LineHeight);

    internal double FitSize(string text, ElementStyle style, double width, double height, double maximum)
    {
        const double referenceSize = 64, fontPrecision = .001;
        // Keep measurements with the bounded layout cache. A moving frame changes width/height
        // on every input event, while the underlying text, graphemes and break opportunities persist.
        var reference = GetLayout(new(text, style.FontFamily, referenceSize, 0, TextAlignment.Left));
        if (reference.FitParagraphs is null)
        {
            double tabPitch = text.Contains('\t') ? GetFont(new(style.FontFamily, referenceSize)).TabPitch : 0;
            reference.FitParagraphs = reference.Geometry.Lines.Select(line =>
            {
                int start = line.Carets[0].Index, end = line.Carets[^1].Index;
                return new FitParagraph(new(text[start..end], line.Spans!, start, tabPitch), line.Height);
            }).ToArray();
        }
        bool EstimatedFit(double size)
        {
            var insets = Insets.For(style.Variant, size, style.TextOutlineWidthAt(size));
            double innerWidth = width - 2 * insets.Horizontal, innerHeight = height - insets.Top - insets.Bottom;
            if (innerWidth <= 0 || innerHeight <= 0) return false;
            double scale = referenceSize / size, usedHeight = 0;
            foreach (var paragraph in reference.FitParagraphs)
            foreach (var line in paragraph.Widths.Lines(innerWidth * scale))
            {
                usedHeight += paragraph.LineHeight;
                if (usedHeight > (innerHeight + .001) * scale || line.Width > (innerWidth + .001) * scale) return false;
            }
            return true;
        }
        bool ExactFit(double size)
        {
            var metrics = GetLayout(text, style, width, size).Metrics;
            return metrics.Width <= width + .001 && metrics.Height <= height + .001;
        }
        static double Refine(double low, double high, double tolerance, Func<double, bool> fits)
        {
            while (low + tolerance < high)
            {
                double middle = (low + high) / 2;
                if (fits(middle)) low = middle; else high = middle;
            }
            return low;
        }

        // Contextual glyphs can reshape at soft breaks and fallback fonts can have different
        // ascents on different lines. Confirm both fit and maximality using the actual renderer.
        double candidate = EstimatedFit(maximum) ? maximum : Refine(1, maximum, fontPrecision / 16, EstimatedFit);
        double low = candidate, high = candidate, step = fontPrecision;
        if (ExactFit(candidate))
        {
            while (high < maximum)
            {
                high = Math.Min(maximum, high + step);
                if (!ExactFit(high)) break;
                low = high;
                step *= 8;
            }
        }
        else
        {
            while (low > 1)
            {
                low = Math.Max(1, low - step);
                if (low == 1 || ExactFit(low)) break;
                high = low;
                step *= 8;
            }
        }
        return Refine(low, high, fontPrecision, ExactFit);
    }
}
