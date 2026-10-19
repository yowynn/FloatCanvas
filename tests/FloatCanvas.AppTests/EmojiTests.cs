using System.IO;
using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

internal static class EmojiTests
{
    public static void Run(Action<string, bool> check)
    {
        check("emoji catalog retains all thirty distinct choices", EmojiArtwork.BuiltIn.Count == 30 && EmojiArtwork.BuiltIn.Distinct().Count() == 30);
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        foreach (string emoji in EmojiArtwork.BuiltIn)
        {
            foreach (double size in new[] { 12d, 48d, 96d })
            {
                var metrics = EmojiGlyphs.Measure(emoji, size);
                check(emoji + " shapes one color glyph without missing characters at " + size, metrics is { GlyphCount: 1, MissingGlyphCount: 0, LineCount: 1 } && metrics.Width > 0 && metrics.Height > 0);
                var element = Element(emoji, size);
                element.Transform = Affine2D.Translation(10, 10);
                var scene = new SceneDocument { Viewport = new(0, 0, Math.Ceiling(metrics.Width) + 20, Math.Ceiling(metrics.Height) + 20), Elements = [element] };
                using var bitmap = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
                var ink = Ink(bitmap);
                Console.WriteLine($"EMOJI_INK {emoji} size={size} metrics={metrics.Width:F4}x{metrics.Height:F4} ink={ink}");
                check(emoji + " visible ink matches its selection rectangle at " + size,
                    Math.Abs(ink.Left - 10) <= 1 && Math.Abs(ink.Top - 10) <= 1 &&
                    Math.Abs(ink.Right - (10 + metrics.Width)) <= 1.5 && Math.Abs(ink.Bottom - (10 + metrics.Height)) <= 1.5);
                check(emoji + " is rendered in color rather than monochrome at " + size, bitmap.Pixels.Count(Colorful) > Math.Max(2, size * size * .015));
                check(emoji + " selection center and visible ink center agree at " + size,
                    Math.Abs((ink.Left + ink.Right) / 2d - (10 + metrics.Width / 2)) <= 1 &&
                    Math.Abs((ink.Top + ink.Bottom) / 2d - (10 + metrics.Height / 2)) <= 1);
            }
            var preview = EmojiArtwork.Image(emoji);
            check(emoji + " preview is frozen and reused", preview.IsFrozen && ReferenceEquals(preview, EmojiArtwork.Image(emoji)));
            var matchScene = new SceneDocument { Viewport = new(0, 0, preview.PixelWidth, preview.PixelHeight), Elements = [Element(emoji, 96)] };
            using var expected = SKBitmap.Decode(renderer.ExportPng(matchScene, assets, true));
            var converted = new Imaging.FormatConvertedBitmap(preview, Media.PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[preview.PixelWidth * preview.PixelHeight * 4];
            converted.CopyPixels(pixels, preview.PixelWidth * 4, 0);
            bool matches = true;
            for (int y = 0; y < preview.PixelHeight && matches; y++)
                for (int x = 0; x < preview.PixelWidth; x++)
                {
                    var color = expected.GetPixel(x, y); int i = (y * preview.PixelWidth + x) * 4;
                    if (pixels[i + 3] != color.Alpha || color.Alpha > 128 &&
                        (Math.Abs(pixels[i] - color.Blue) > 1 || Math.Abs(pixels[i + 1] - color.Green) > 1 || Math.Abs(pixels[i + 2] - color.Red) > 1))
                    { matches = false; break; }
                }
            check(emoji + " gallery preview matches exported color and alpha", matches);
        }
        var sequence = EmojiGlyphs.Measure("👩‍🎨", 18);
        check("ZWJ emoji is shaped as one complete editable grapheme", sequence.GlyphCount == 1 && sequence.MissingGlyphCount == 0);
        Parallel.For(0, 90, i => EmojiGlyphs.Measure(EmojiArtwork.BuiltIn[i % 30], 48));
        check("shared emoji measurement cache supports concurrent rendering callers", EmojiGlyphs.Measure("😀", 48) == EmojiGlyphs.Measure("😀", 48));
        CreationAndEditing(check);
        PreviewGrid(renderer, assets);
    }

    private static CanvasElement Element(string emoji, double size)
    {
        var metrics = EmojiGlyphs.Measure(emoji, size);
        return new() { Kind = ElementKind.Text, Text = emoji, Width = metrics.Width, Height = metrics.Height,
            Style = new() { FontFamily = EmojiGlyphs.FontFamily, FontSize = size, Stroke = 0xFF333333 } };
    }

    private static void CreationAndEditing(Action<string, bool> check)
    {
        var session = new EditorSession();
        var controller = new EditorController(session) { Tool = EditorTool.Emoji, Emoji = "🤔", ScreenOrigin = new(-300, 100) };
        controller.Style.FontSize = 18;
        controller.Pointer(new(OverlayPointerKind.Down, new(-250, 160), OverlayMouseButton.Left, OverlayModifiers.None));
        controller.Pointer(new(OverlayPointerKind.Up, new(-250, 160), OverlayMouseButton.Left, OverlayModifiers.None));
        var element = session.Document.Elements.Single(); var metrics = EmojiGlyphs.Measure("🤔", 18);
        check("emoji creation centers a tightly bounded editable text element at the document click", element.Kind == ElementKind.Text && element.Text == "🤔" && element.Style.FontSize == 18 &&
            element.Width == metrics.Width && element.Height == metrics.Height && element.Transform == Affine2D.Translation(50 - metrics.Width / 2, 60 - metrics.Height / 2));
        check("emoji creation remains one undoable object", session.UndoCount == 1 && session.Undo() && session.Document.Elements.Count == 0 && session.Redo());
        var inspector = new InspectorWindow(() => session);
        var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
        fields["FontSize"].Text = "36";
        Invoke(inspector, "Apply"); element = session.Document.Elements.Single(); metrics = EmojiGlyphs.Measure("🤔", 36);
        Console.WriteLine($"EMOJI_PROPERTY size={element.Style.FontSize} box={element.Width}x{element.Height} expected={metrics.Width}x{metrics.Height} selection={session.Selection.Count} status={((Controls.TextBlock)Field(inspector, "status")).Text}");
        check("emoji property size edits update both tight dimensions while preserving the explicit font size", element.Style.FontSize == 36 && element.Width == metrics.Width && element.Height == metrics.Height);
        using var renderer = new SceneRenderer();
        using(var edit=new InlineTextEdit(session,element,new(),element.Style,renderer)) {edit.Input.Text="❤️";edit.Finish(true);}
        element = session.Document.Elements.Single(); metrics = EmojiGlyphs.Measure("❤️", 36);
        check("editing an emoji sequence recomputes its tight dimensions without rasterizing the object", element.Kind == ElementKind.Text && element.Text == "❤️" && element.Width == metrics.Width && element.Height == metrics.Height);
        check("emoji detached edit checks create no native window", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0);
    }

    private static (int Left, int Top, int Right, int Bottom) Ink(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Alpha > 0) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return (left, top, right + 1, bottom + 1);
    }
    private static bool Colorful(SKColor c) => c.Alpha > 128 && Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 35;
    private static void PreviewGrid(SceneRenderer renderer, AssetLibrary assets)
    {
        string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_EMOJI_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var scene = new SceneDocument { Viewport = new(0, 0, 600, 500) };
        for (int i = 0; i < EmojiArtwork.BuiltIn.Count; i++)
        {
            var element = Element(EmojiArtwork.BuiltIn[i], 72);
            element.Transform = Affine2D.Translation((i % 6) * 100 + 50 - element.Width / 2, (i / 6) * 100 + 50 - element.Height / 2);
            scene.Elements.Add(element);
        }
        File.WriteAllBytes(Path.Combine(output, "all-thirty-color-emoji.png"), renderer.ExportPng(scene, assets, true));
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
