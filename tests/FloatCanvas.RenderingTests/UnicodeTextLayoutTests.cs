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
