using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ImageAlphaBorderRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        StaticContours(check);
        Transparency(check);
        CropAndTransforms(check);
        Animation(check);
        FragmentedAlpha(check);
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_ALPHA_BORDER_UI_OUTPUT") is { Length: > 0 } output)
            WriteEvidence(output);
    }

    private static void StaticContours(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Png(24, 24, (x, y) => x is >= 8 and < 16 && y is >= 8 and < 16 ? SKColors.Blue : SKColors.Transparent));
        var scene = Scene(id, 24, 24);
        var image = scene.Elements[0];
        using var renderer = new SceneRenderer();
        using (var actual = Render(renderer, scene, assets))
        {
            check("transparent PNG border follows its opaque subject inside the source rectangle", actual.GetPixel(26, 32) == SKColors.Red);
            check("transparent PNG padding does not acquire a rectangular frame", actual.GetPixel(18, 32).Alpha == 0 && actual.GetPixel(22, 22).Alpha == 0);
            check("opaque subject is painted above the expanded border color", actual.GetPixel(32, 32) == SKColors.Blue);
            // This corner pixel's lower-right area intersects a radius-three round join.
            // Its coverage is small; a square expansion would fill the whole pixel.
            byte cornerAlpha = actual.GetPixel(25, 25).Alpha;
            check($"round envelope keeps square bounding-box corner coverage small (alpha {cornerAlpha})", cornerAlpha < 128);
        }
        image.ImageBorderWidth = 0;
        using (var actual = Render(renderer, scene, assets))
            check("zero image border leaves transparent subject surroundings unchanged", actual.GetPixel(26, 32).Alpha == 0 && actual.GetPixel(32, 32) == SKColors.Blue);
        image.ImageBorderWidth = 3;
        image.AssetId = assets.Add(Png(24, 24, (_, _) => SKColors.Transparent));
        using (var actual = Render(renderer, scene, assets))
            check("an entirely transparent image has no alpha envelope to outline", AllTransparent(actual));
        image.AssetId = assets.Add(Png(24, 24, (_, _) => SKColors.Blue));
        using (var actual = Render(renderer, scene, assets))
            check("fully opaque images retain a full-width outside border", actual.GetPixel(18, 32) == SKColors.Red && actual.GetPixel(20, 32) == SKColors.Blue);

        image.AssetId = assets.Add(Png(24, 24, (x, y) => x is >= 3 and < 21 && y is >= 3 and < 21 &&
            !(x is >= 8 and < 16 && y is >= 8 and < 16) ? SKColors.Blue : SKColors.Transparent));
        image.ImageBorderWidth = 2;
        using (var actual = Render(renderer, scene, assets))
            check("the envelope expands around internal transparent holes without filling distant empty pixels", actual.GetPixel(28, 32) == SKColors.Red && actual.GetPixel(32, 32).Alpha == 0);
    }

    private static void Transparency(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Png(24, 24, (x, y) => x is >= 8 and < 16 && y is >= 8 and < 16 ? new SKColor(0, 0, 255, 128) : SKColors.Transparent));
        var scene = Scene(id, 24, 24);
        var image = scene.Elements[0];
        using var renderer = new SceneRenderer();
        using (var actual = Render(renderer, scene, assets))
        {
            var body = actual.GetPixel(32, 32);
            check("semitransparent subject naturally mixes with solid border paint underneath", body.Alpha == 255 && body.Red is >= 126 and <= 128 && body.Blue is >= 127 and <= 129 && body.Green == 0);
            check("semitransparent alpha generates a solid-color envelope rather than a faint copy", actual.GetPixel(26, 32) == SKColors.Red);
        }
        image.AssetId = assets.Add(Png(24, 24, (x, y) => x == 12 && y == 12 ? new SKColor(255, 255, 255, 1) : SKColors.Transparent));
        using (var actual = Render(renderer, scene, assets))
            check("every nonzero source alpha contributes to the envelope including alpha one", actual.GetPixel(30, 32) == SKColors.Red);
        image.AssetId = id;
        image.Style.Opacity = .5;
        using (var actual = Render(renderer, scene, assets))
            check("element opacity is applied once after image and envelope composition", actual.GetPixel(32, 32).Alpha is >= 127 and <= 128 && actual.GetPixel(26, 32).Alpha is >= 127 and <= 128);
        image.Style.Opacity = 1;
        image.Style.Stroke = 0x80FF0000;
        using (var actual = Render(renderer, scene, assets))
        {
            var body = actual.GetPixel(32, 32);
            check("translucent border paint remains beneath the original semitransparent pixels", body.Alpha is >= 191 and <= 193 && body.Red is >= 84 and <= 86 && body.Blue is >= 169 and <= 171);
        }
        image.Style.Stroke = 0x00FF0000;
        using (var actual = Render(renderer, scene, assets))
            check("transparent border paint does not change original image alpha", actual.GetPixel(26, 32).Alpha == 0 && actual.GetPixel(32, 32).Alpha == 128);
    }

    private static void CropAndTransforms(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Png(24, 24, (x, y) => x is >= 8 and < 16 && y is >= 8 and < 16 ? SKColors.Blue : SKColors.Transparent));
        var scene = Scene(id, 24, 24);
        var image = scene.Elements[0];
        using var renderer = new SceneRenderer();
        image.Width = 48; image.Height = 48;
        using (var actual = Render(renderer, scene, assets))
            check("envelope width stays in object units when source pixels are scaled", actual.GetPixel(34, 44) == SKColors.Red && actual.GetPixel(32, 44).Alpha == 0);
        image.Width = 24; image.Height = 24;
        image.ImageCrop = new(1d / 3, 1d / 3, 1d / 3, 1d / 3);
        using (var actual = Render(renderer, scene, assets))
            check("cropped visible pixels generate a border around the displayed crop", actual.GetPixel(18, 32).Red == 255 && actual.GetPixel(32, 32) == SKColors.Blue);
        image.ImageCrop = new(0, 0, .25, 1);
        using (var actual = Render(renderer, scene, assets))
            check("cropped-away subject pixels cannot generate a border", AllTransparent(actual));
        image.ImageCrop = null;
        image.Transform = Affine2D.Translation(50, 20).Compose(Affine2D.Rotation(90));
        using (var actual = Render(renderer, scene, assets))
            check("alpha envelope rotates in the image plane with its body", actual.GetPixel(38, 26) == SKColors.Red && actual.GetPixel(38, 32) == SKColors.Blue && actual.GetPixel(38, 18).Alpha == 0);
    }

    private static void Animation(Action<string, bool> check)
    {
        using var first = new SKBitmap(32, 24); first.Erase(SKColors.Transparent);
        using var second = new SKBitmap(32, 24); second.Erase(SKColors.Transparent);
        for (int y = 8; y < 16; y++)
            for (int x = 8; x < 16; x++) { first.SetPixel(x, y, SKColors.Blue); second.SetPixel(x + 12, y, SKColors.Lime); }
        using var encoded = SKWebpEncoder.EncodeAnimated([
            new(first, TimeSpan.FromMilliseconds(70)), new(second, TimeSpan.FromMilliseconds(130))], new(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Alpha-border animation fixture encoding failed.");
        var assets = new AssetLibrary(); string id = assets.Add(encoded.ToArray(), "image/webp");
        var scene = Scene(id, 32, 24);
        using var renderer = new SceneRenderer();
        var scheduled = renderer.CaptureAnimationSchedule(scene, assets, TimeSpan.Zero);
        check("animated image damage includes the whole conservative outside-border extent", scheduled.DirtyBounds.SequenceEqual([
            SceneRenderer.ProjectedBounds(scene, scene.Elements[0], includeStroke: true).Intersect(scene.Viewport)]));
        check("alpha-border animation schedule stays metadata-only", renderer.AnimationFrameDecodeCount == 0);
        using (var actual = Render(renderer, scene, assets))
            check("animated image initially outlines the first frame's real alpha silhouette", actual.GetPixel(26, 32) == SKColors.Red && actual.GetPixel(49, 32).Alpha == 0);
        check("alpha envelope generation preserves source animation timing", renderer.NextAnimationDelay == TimeSpan.FromMilliseconds(70));
        long firstDecode = renderer.AnimationFrameDecodeCount;
        using (var repeat = Render(renderer, scene, assets))
            check("drawing unchanged animation time reuses the decoded source frame", renderer.AnimationFrameDecodeCount == firstDecode && repeat.GetPixel(26, 32) == SKColors.Red);
        renderer.AnimationTime = TimeSpan.FromMilliseconds(70);
        using (var actual = Render(renderer, scene, assets))
            check("animated alpha envelope moves with the next composited frame without stale border pixels", actual.GetPixel(26, 32).Alpha == 0 && actual.GetPixel(49, 32) == SKColors.Red && actual.GetPixel(44, 32) == SKColors.Lime);
        check("border redraw does not add a second animation clock or change frame delay", renderer.NextAnimationDelay == TimeSpan.FromMilliseconds(130));
        renderer.AnimationTime = TimeSpan.Zero;
        using (var rewind = Render(renderer, scene, assets))
            check("rewinding the animation restores the earlier envelope", rewind.GetPixel(26, 32) == SKColors.Red && rewind.GetPixel(49, 32).Alpha == 0);
        check("animated border composition never mutates stored image bytes", assets.Get(id).Data.Span.SequenceEqual(encoded.ToArray()));

        using var cache = new OverlaySceneCache(renderer);
        using var target = SKSurface.Create(new SKImageInfo(96, 96));
        renderer.AnimationTime = TimeSpan.Zero; target.Canvas.Clear(SKColors.Transparent); cache.Draw(target.Canvas, scene, assets, 1, 0);
        renderer.AnimationTime = TimeSpan.FromMilliseconds(70); target.Canvas.Clear(SKColors.Transparent); cache.Draw(target.Canvas, scene, assets, 1, 0);
        using (var actual = SKBitmap.FromImage(target.Snapshot()))
            check("retained scene cache updates the alpha border without a document edit", actual.GetPixel(26, 32).Alpha == 0 && actual.GetPixel(49, 32) == SKColors.Red);

        var image = scene.Elements[0];
        image.ImageCrop = new(.625, 1d / 3, .25, 1d / 3);
        image.Transform = Affine2D.Translation(-33, 20);
        long decodedBeforeSchedule = renderer.AnimationFrameDecodeCount;
        var edgeSchedule = renderer.CaptureAnimationSchedule(scene, assets, TimeSpan.FromMilliseconds(70));
        check("animated border crossing into a viewport keeps scheduling when the body is outside", edgeSchedule.HasAnimations &&
            edgeSchedule.NextDelay == TimeSpan.FromMilliseconds(130) && edgeSchedule.DirtyBounds.Any(bound => bound.Contains(new(0, 32))));
        check("border-only viewport intersection still avoids decoding in schedule queries", renderer.AnimationFrameDecodeCount == decodedBeforeSchedule);
        using (var actual = Render(renderer, scene, assets))
            check("an outside border can render while the cropped image body is completely offscreen", actual.GetPixel(0, 32) == SKColors.Red);
    }

    private static void FragmentedAlpha(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        byte[] bytes = Png(256, 256, (x, y) => ((x + y) & 1) == 0 ? new SKColor(0, 0, 255, 128) : SKColors.Transparent);
        string id = assets.Add(bytes);
        var scene = new SceneDocument { Viewport = new(0, 0, 288, 288), Elements = [new()
            { Kind = ElementKind.Image, AssetId = id, Width = 256, Height = 256, ImageBorderWidth = 2,
              Transform = Affine2D.Translation(16, 16), Style = new() { Stroke = 0x80FF0000 } }] };
        using var renderer = new SceneRenderer();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var actual = Render(renderer, scene, assets))
        {
            var mixed = actual.GetPixel(116, 116); var hole = actual.GetPixel(117, 116);
            check("highly fragmented alpha retains one border color layer across overlapping expansions", hole.Alpha == 128 && hole.Red == 255 && hole.Blue == 0);
            check("fragmented-alpha fallback keeps body and border composition identical to simple contours", mixed.Alpha is >= 191 and <= 193 && mixed.Red is >= 84 and <= 86 && mixed.Blue is >= 169 and <= 171);
            check("fragmented-alpha envelope reaches outward without painting beyond its configured radius", actual.GetPixel(14, 116).Alpha > 0 && actual.GetPixel(12, 116).Alpha == 0);
        }
        double firstMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        using (var repeated = Render(renderer, scene, assets))
            check("fragmented-alpha repeated rendering is stable", repeated.GetPixel(117, 116).Alpha == 128);
        Console.WriteLine($"ALPHA_BORDER_FRAGMENTED 32768 source row-runs: first={firstMs:F3}ms repeat={watch.Elapsed.TotalMilliseconds:F3}ms");
        scene.Elements[0].ImageCrop = new(.5, 0, .5, 1);
        using (var cropped = Render(renderer, scene, assets))
            check("fragmented-alpha envelope respects the current displayed crop", cropped.GetPixel(12, 116).Alpha == 0 && cropped.GetPixel(144, 116).Alpha > 0);
        renderer.CacheByteLimit = 0;
        using (var trimmed = Render(renderer, scene, assets))
            check("alpha-border masks and source frames obey the image cache budget after drawing", renderer.CachedImageBytes == 0 && trimmed.GetPixel(144, 116).Alpha > 0);
        scene.Elements[0].AssetId = assets.Add(Png(256, 256, (_, _) => SKColors.Transparent));
        using (var replaced = Render(renderer, scene, assets))
            check("replacing a fragmented source with transparent pixels does not reuse the previous border", AllTransparent(replaced));
        check("fragmented border rendering preserves original encoded bytes", assets.Get(id).Data.Span.SequenceEqual(bytes));
    }

    private static SceneDocument Scene(string id, double width, double height) => new()
    {
        Viewport = new(0, 0, 96, 96), Elements = [new() { Kind = ElementKind.Image, AssetId = id, Width = width, Height = height,
            Transform = Affine2D.Translation(20, 20), ImageBorderWidth = 3, Style = new() { Stroke = 0xFFFF0000 } }]
    };

    private static byte[] Png(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, color(x, y));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }

    private static SKBitmap Render(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets) => SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
    private static bool AllTransparent(SKBitmap bitmap) => Enumerable.Range(0, bitmap.Height).All(y => Enumerable.Range(0, bitmap.Width).All(x => bitmap.GetPixel(x, y).Alpha == 0));

    private static void WriteEvidence(string output)
    {
        using var source = EvidenceSource(255, false);
        using var translucent = EvidenceSource(112, false);
        using var later = EvidenceSource(255, true);
        using var png = source.Encode(SKEncodedImageFormat.Png, 100);
        using var translucentPng = translucent.Encode(SKEncodedImageFormat.Png, 100);
        using var animated = SKWebpEncoder.EncodeAnimated([
            new(source, TimeSpan.FromMilliseconds(70)), new(later, TimeSpan.FromMilliseconds(130))], new(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Alpha-border preview animation encoding failed.");
        var assets = new AssetLibrary();
        string opaqueId = assets.Add(png.ToArray());
        string translucentId = assets.Add(translucentPng.ToArray());
        string animationId = assets.Add(animated.ToArray(), "image/webp");
        using var renderer = new SceneRenderer();
        using var sheet = SKSurface.Create(new SKImageInfo(1000, 835))
            ?? throw new InvalidOperationException("Alpha-border preview surface allocation failed.");
        var canvas = sheet.Canvas;
        canvas.Clear(new SKColor(0xFFF5F6F8));
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        using var heading = new SKFont(typeface, 22);
        using var label = new SKFont(typeface, 17);
        using var small = new SKFont(typeface, 14);
        using var ink = new SKPaint { Color = new SKColor(0xFF202630), IsAntialias = true };
        using var checker = new SKPaint();
        canvas.DrawText("图像 Alpha 轮廓描边 · 真实渲染器输出", 24, 34, SKTextAlign.Left, heading, ink);
        canvas.DrawText("红色描边 6 px；棋盘格仅表示透明区域。下排为同一动图的两个合成帧。", 24, 60, SKTextAlign.Left, small, ink);
        DrawCase(opaqueId, 0, 0, "PNG 原图：主体外是透明背景", TimeSpan.Zero);
        DrawCase(opaqueId, 6, 1, "描边沿主体轮廓向外扩展", TimeSpan.Zero);
        DrawCase(translucentId, 0, 2, "半透明原图：蓝色主体 Alpha = 112", TimeSpan.Zero);
        DrawCase(translucentId, 6, 3, "半透明主体与下方红色底色自然混合", TimeSpan.Zero);
        DrawCase(animationId, 6, 4, "动图第 1 帧 · 0 ms", TimeSpan.Zero);
        DrawCase(animationId, 6, 5, "动图第 2 帧 · 70 ms：轮廓随帧变化", TimeSpan.FromMilliseconds(70));
        canvas.DrawText("这是功能渲染验证图，不是应用界面的截图。", 24, 816, SKTextAlign.Left, small, ink);
        using var result = sheet.Snapshot();
        using var encoded = result.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Alpha-border preview PNG encoding failed.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllBytes(output, encoded.ToArray());

        void DrawCase(string assetId, double width, int index, string title, TimeSpan time)
        {
            float left = 24 + (index % 2) * 492, top = 80 + (index / 2) * 240;
            canvas.DrawText(title, left, top + 22, SKTextAlign.Left, label, ink);
            for (int y = 0; y < 180; y += 12)
                for (int x = 0; x < 464; x += 12)
                {
                    checker.Color = ((x / 12 + y / 12) & 1) == 0 ? SKColors.White : new SKColor(0xFFE1E4E9);
                    canvas.DrawRect(left + x, top + 36 + y, Math.Min(12, 464 - x), Math.Min(12, 180 - y), checker);
                }
            var scene = new SceneDocument { Viewport = new(0, 0, 464, 180), Elements = [new()
                { Kind = ElementKind.Image, AssetId = assetId, Width = 160, Height = 160, ImageBorderWidth = width,
                  Transform = Affine2D.Translation(152, 10), Style = new() { Stroke = 0xFFFF4C57 } }] };
            renderer.AnimationTime = time;
            using var frame = SKImage.FromEncodedData(renderer.ExportPng(scene, assets, true))
                ?? throw new InvalidOperationException("Alpha-border preview frame decoding failed.");
            canvas.DrawImage(frame, left, top + 36, new SKSamplingOptions(SKFilterMode.Nearest));
        }
    }

    private static SKBitmap EvidenceSource(byte alpha, bool alternate)
    {
        var bitmap = new SKBitmap(128, 128); bitmap.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(bitmap);
        using var fill = new SKPaint { Color = new SKColor(38, 130, 220, alpha), IsAntialias = true };
        if (alternate)
        {
            canvas.DrawCircle(78, 64, 34, fill);
            canvas.DrawCircle(40, 85, 16, fill);
        }
        else
        {
            using var path = new SKPathBuilder(); path.MoveTo(64, 16); path.LineTo(112, 110); path.LineTo(16, 110); path.Close();
            using var triangle = path.Detach(); canvas.DrawPath(triangle, fill);
        }
        return bitmap;
    }
}
