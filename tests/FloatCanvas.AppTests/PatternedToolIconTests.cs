using FloatCanvas.App;
using FloatCanvas.Core;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PatternedToolIconTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("minimum highlighter dots stay substantially wider than maximum pen dots",
            LongestInkRun(Pixels(EditorTool.Highlighter, StrokePattern.Dotted, 16)) >= LongestInkRun(Pixels(EditorTool.Freehand, StrokePattern.Dotted, 1024)) * 1.35);
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        {
            double[] widths = tool == EditorTool.Freehand ? [2, 6, 15.216, 24, 60] : [16, 24, 32, 64, 128];
            foreach (var pattern in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
            {
                long previous = 0;
                foreach (double width in widths)
                {
                    byte[] sample = Pixels(tool, pattern, width), solid = Pixels(tool, StrokePattern.Solid, width);
                    long ink = 0; int outside = 0;
                    for (int i = 3; i < sample.Length; i += 4)
                    { ink += sample[i]; if (sample[i] > solid[i] + 40) outside++; }
                    check($"{tool} {pattern} width={width} visibly grows with stroke width", ink > previous * 1.05);
                    check($"{tool} {pattern} width={width} follows the same solid centerline", outside <= 8);
                    previous = ink;
                }
            }
        }
        foreach (double width in new[] { 2d, 6, 15.216, 24, 60 })
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        foreach (var pattern in new[] { StrokePattern.Dashed, StrokePattern.Dotted })
        {
            var parameters = new ToolParameters { Tool = tool, Style = new() { Foreground = 0xff448361, StrokeWidth = width, StrokePattern = pattern } };
            var before = System.Text.Json.JsonSerializer.Serialize(parameters);
            var bitmap = new FormatConvertedBitmap((BitmapSource)((Image)ToolParameterIcon.Create(parameters)).Source, PixelFormats.Pbgra32, null, 0);
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            var pixels = new byte[w * h * 4]; bitmap.CopyPixels(pixels, w * 4, 0);
            var visited = new bool[w * h];
            var components = new List<(int Width, int Height, int Count)>();
            for (int start = 0; start < visited.Length; start++)
            {
                if (visited[start] || pixels[start * 4 + 3] < 100) continue;
                var queue = new Queue<int>(); queue.Enqueue(start); visited[start] = true;
                int left = w, right = 0, top = h, bottom = 0, count = 0;
                while (queue.TryDequeue(out int current))
                {
                    int x = current % w, y = current / w;
                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); count++;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int next = ny * w + nx;
                        if (visited[next] || pixels[next * 4 + 3] < 100) continue;
                        visited[next] = true; queue.Enqueue(next);
                    }
                }
                if (count >= 4) components.Add((right - left + 1, bottom - top + 1, count));
            }
            string name = $"{tool} {pattern} width={width}";
            check(name + " retains multiple marks along its stroke", components.Count >= (pattern == StrokePattern.Dotted ? (tool == EditorTool.Freehand ? 11 : 3) : 2));
            check(name + " leaves drawing parameters unchanged", before == System.Text.Json.JsonSerializer.Serialize(parameters));
        }
    }
    private static int LongestInkRun(byte[] pixels)
    {
        int longest = 0;
        for (int x = 0; x < 96; x++)
        {
            int run = 0;
            for (int y = 0; y < 96; y++)
            {
                run = pixels[(y * 96 + x) * 4 + 3] >= 100 ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }
        return longest;
    }    private static byte[] Pixels(EditorTool tool, StrokePattern pattern, double width)
    {
        var bitmap = new FormatConvertedBitmap((BitmapSource)((Image)ToolParameterIcon.Create(new ToolParameters
            { Tool = tool, Style = new() { Foreground = 0xff448361, StrokeWidth = width, StrokePattern = pattern } })).Source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }}
