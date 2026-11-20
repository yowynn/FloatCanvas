using System.Diagnostics;
using System.Globalization;
using FloatCanvas.Core;
using FloatCanvas.Rendering;

internal static class UnicodeTextLayoutTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 24 };
        const string words = "hello wonderful world";
        double width = renderer.MeasureText("hello wo", style).Width + .1;
        var wrapped = renderer.TextEditingGeometry(words, style, width);
        check("Normal English line uses an available word boundary", wrapped.Lines[0].Carets[^1].Index == 6);
        check("Long word still falls back to intact graphemes", wrapped.Lines.Count > 2 && wrapped.Lines[^1].Carets[^1].Index == words.Length);
        foreach (string text in new[] { "中文 abc", "a👨‍👩‍👧‍👦e\u0301", "office ffi", "abc אבג xyz", "  (אבג)", "مرحبا بالعالم" })
        {
            var geometry = renderer.TextEditingGeometry(text, style, 0);
            var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
            check("Caret boundaries preserve graphemes: " + text, geometry.Lines.SelectMany(line => line.Carets).All(stop => boundaries.Contains(stop.Index)));
            check("Layout reaches every grapheme: " + text, boundaries.SetEquals(geometry.Lines.SelectMany(line => line.Carets).Select(stop => stop.Index)));
            check("Editing and rendering share line count: " + text, renderer.MeasureText(text, style).LineCount == geometry.Lines.Count);
        }
        var mixed = renderer.TextEditingGeometry("abc אבג xyz", style, 0);
        check("Hebrew run has descending logical carets inside LTR paragraph", mixed.Caret(5).X > mixed.Caret(6).X);
        check("Mixed RTL hit testing maps back to logical text", mixed.HitTest(new(mixed.Caret(5).X, 1)) == 5);
        check("Left arrow follows visual order inside RTL run", mixed.MoveHorizontal(5, -1) == 6);
        check("Right arrow follows visual order inside RTL run", mixed.MoveHorizontal(6, 1) == 5);
        check("Mixed-direction partial selection contains disjoint visual spans", mixed.Selection(2, 3).Count >= 2);
        var boundaryStops = mixed.Lines[0].Carets.Where(stop => stop.Index == 4).ToArray();
        check("Bidi run junction retains both visual caret affinities", boundaryStops.Select(stop => stop.X).Distinct().Count() == 2);
        foreach (var stop in boundaryStops)
            check("Clicked bidi affinity is preserved when drawing caret", Math.Abs(mixed.Caret(4, new(stop.X, 0)).X - stop.X) < .01);
        var crlf = renderer.TextEditingGeometry("ab\r\ncd", style, 0);
        check("CRLF mapping keeps original UTF16 indices", crlf.Lines[1].Carets[0].Index == 4);
        var newline = crlf.Selection(2, 2);
        check("selecting only CRLF highlights the end of its visual line", newline.Count == 1 &&
            newline[0].Width >= 2 && newline[0].X == crlf.Caret(2).X && newline[0].Y == crlf.Lines[0].Top);
        var blanks = renderer.TextEditingGeometry("\n\n", style, 100);
        check("consecutive empty paragraphs have separately selectable line breaks", blanks.Selection(0, 2).Count == 2);
        var tabbed = renderer.TextEditingGeometry("A\tB", style, 0);
        double pitch = 4 * renderer.MeasureText(" ", style).Width;
        double tabEnd = (Math.Floor(renderer.MeasureText("A", style).Width / pitch) + 1) * pitch;
        check("a tab advances to a four-space stop with one selectable source character and no missing glyph",
            Math.Abs(tabbed.Caret(2).X - tabEnd) < .01 && tabbed.Selection(1, 1).Count == 1 &&
            renderer.MeasureText("A\tB", style).MissingGlyphCount == 0);
        var afterBreak = renderer.TextEditingGeometry("ZZ\tX\r\n\tY", style, 0);
        check("tab stops restart after hard breaks and preserve CRLF indices",
            Math.Abs(afterBreak.Caret(7).X - pitch) < .01 && afterBreak.Lines[1].Carets[0].Index == 6);
        var rtlTab = renderer.TextEditingGeometry("אב\tג", style, 0).Lines.Single();
        var rtlSpace = rtlTab.Spans!.Single(span => span.Start == 2);
        double rtlEnd = rtlTab.Carets.Max(c => c.X) - rtlSpace.Left;
        check("right-to-left tabs align from the leading right edge",
            Math.Abs(rtlEnd - (Math.Floor(renderer.MeasureText("אב", style).Width / pitch) + 1) * pitch) < .01);
        const string columns = "AB\tCD AB\tCD";
        var softTabs = renderer.TextEditingGeometry(columns, style, pitch * 2 + 5);
        check("tab stops use the current visual line after soft wrap", softTabs.Lines.Count > 1 &&
            softTabs.Lines.SelectMany(line => line.Spans!).Where(span => columns[span.Start] == '\t')
                .All(span => Math.Abs(span.Right / pitch - Math.Round(span.Right / pitch)) < .001));
        var huge = style.Clone(); huge.FontSize = 2048;
        double halfSize = renderer.MeasureText("A", huge).Width; huge.FontSize = 4096;
        check("the largest supported text size renders at twice the width of 2048", Math.Abs(renderer.MeasureText("A", huge).Width / halfSize - 2) < .001);
        foreach (string sample in new[] { "会议纪要 123 ABC", "office affine Wo\tA\n\nB", "مرحبا بالعالم e\u0301 👨‍👩‍👧‍👦", "A\tB\r\nאבג xyz", "" })
        foreach (var frame in new[] { (Width: 35d, Height: 80d), (Width: 231d, Height: 100d) })
        {
            var probe = style.Clone();
            bool Fits(double size)
            {
                probe.FontSize = size;
                var m = renderer.MeasureText(sample, probe, frame.Width);
                return m.Width <= frame.Width + .001 && m.Height <= frame.Height + .001;
            }
            double low = 1, high = 128;
            for (int i = 0; i < 24; i++)
            {
                double middle = (low + high) / 2;
                if (Fits(middle)) low = middle; else high = middle;
            }
            double fitted = renderer.FittedTextSize(sample, style, frame.Width, frame.Height, 128);
            check("resizing matches exact shaping at wrap and font-fallback boundaries",
                Fits(fitted) && Math.Abs(fitted - low) < .002);
        }
        var legacyRuns = UnicodeTextAnalysis.LegacyVisualRuns("abc אבג xyz");
        Console.WriteLine("UNISCRIBE_RUNS " + System.Text.Json.JsonSerializer.Serialize(legacyRuns.Runs));
        check("Windows 1607 Uniscribe fallback retains mixed-direction runs", legacyRuns.Runs.Any(run => run.Start == 4 && run.Length == 3 && run.RightToLeft));
        check("Windows 1607 Uniscribe fallback finds English word boundaries", UnicodeTextAnalysis.LegacyLineBreaks(words).Contains(6));
        var medians = new List<double>();
        foreach (int length in new[] { 100, 1000, 5000 })
        {
            var timings = new List<double>();
            for (int pass = 0; pass < 5; pass++)
            {
                string text = new string('a', length) + pass;
                var timer = Stopwatch.StartNew(); renderer.TextEditingGeometry(text, style, 0); timer.Stop();
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }
            double median = timings.Order().ElementAt(2); medians.Add(median);
            Console.WriteLine($"TEXT_LAYOUT chars={length} median_ms={median:F3}");
        }
        check("5000-character geometry avoids quadratic prefix reshaping", medians[2] < 150 && medians[2] <= medians[1] * 9 + 5);
    }
}
