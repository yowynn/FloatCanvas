using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class NumberBoundsTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (double stroke in new[] { 0d, 4, 32, 80 })
        {
            var session = new EditorSession(new() { Viewport = new(0, 0, 100, 100) });
            var number = new CanvasElement { Kind = ElementKind.Number, Width = 40, Height = 40, Transform = Affine2D.Translation(30, 30), Style = new() { StrokeWidth = stroke, Fill = 0xFF2383E2 } };
            session.Add(number);
            using var renderer = new SceneRenderer();
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(session.Document, session.Assets, true));
            var painted = Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).Where(x => bitmap.GetPixel(x, y).Alpha > 16).Select(x => new PointD(x, y))).ToArray();
            var bounds = session.SelectionBounds()!.Value;
            Console.WriteLine($"Number stroke={stroke}: selection={bounds}, alpha=({painted.Min(p => p.X)},{painted.Min(p => p.Y)})-({painted.Max(p => p.X)},{painted.Max(p => p.Y)})");
            check("number selection matches actual filled badge at stroke " + stroke, Math.Abs(bounds.Left - painted.Min(p => p.X)) <= 1 && Math.Abs(bounds.Top - painted.Min(p => p.Y)) <= 1 &&
                Math.Abs(bounds.Right - painted.Max(p => p.X) - 1) <= 1 && Math.Abs(bounds.Bottom - painted.Max(p => p.Y) - 1) <= 1);
            check("number does not hit an invisible inherited stroke at " + stroke, !ElementGeometry.HitTest(number, new(25, 50), 0) && ElementGeometry.HitTest(number, new(50, 50), 0));
            var rotated = number.Clone(); rotated.Transform = Affine2D.Translation(50, 50).Compose(Affine2D.Rotation(30)).Compose(Affine2D.Translation(-20, -20));
            var actual = ElementGeometry.Bounds(rotated); var expected = rotated.Transform.TransformBounds(new(0, 0, 40, 40));
            check("number transformed geometric frame ignores nonexistent outline at " + stroke, actual == expected);
        }
    }
}
