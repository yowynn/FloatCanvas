using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class RootCanvasCacheTests
{
    internal static void Run(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene();
        using var renderer = new SceneRenderer(); using var reference = new SceneRenderer();
        using var cache = new OverlaySceneCache(renderer);
        Compare("three-root cold cache matches actual camera/grid/alpha composition");
        long builds = cache.BuildCount;
        Compare("three-root static hot cache preserves exact pixels");
        check("static mixed roots retain one bounded complete raster", cache.BuildCount == builds && cache.HitCount == 1 && cache.CachedRasterBytes == 240L * 180 * 4);
        scene.LayerOpacity = .27; Changed("whole-composite opacity change invalidates without multiplying it twice");
        scene.Canvas.BackgroundColor = 0x99664422; Changed("main background between the screen roots invalidates");
        scene.Canvas.Grid.Color = 0xAAFF99FF; Changed("main grid settings invalidate the full composite");
        scene.Canvas.Projection.Enabled = false; Changed("changing the main camera invalidates the composite");
        scene.Canvas.Grid.FlatTranslation = new(9, -3); Changed("flat main translation invalidates without moving screen roots");
        scene.Canvas.DepthPriorityOcclusion = !scene.Canvas.DepthPriorityOcclusion; Changed("depth policy is part of the complete-root content key");
        var root = scene.Elements[0]; SceneCanvasRoots.Assign(scene, root.Id, RootCanvasKind.Background);
        Changed("root reassignment invalidates even if the caller retained its revision stamp");
        SceneCanvasRoots.Assign(scene, root.Id, RootCanvasKind.Foreground); Changed("root assignment restoration cannot revive a stale raster");
        scene.Elements[1].Style.Fill = 0xCCDD5599; long revision = 2;
        Changed("authored revision invalidates changed element content", revision: revision);
        var differentAssets = new AssetLibrary(); Changed("a replacement resource library invalidates", revision: revision, library: differentAssets);
        assets.Add([1, 2, 3]); Changed("resource-library membership change invalidates", revision: revision);
        Compare("a second physical output gets its own translated raster", revision: revision, tx: -80.5f, ty: 11.25f);
        long multiBytes = cache.CachedRasterBytes, multiBuilds = cache.BuildCount;
        Compare("the first physical output keeps its own cached pixels", revision: revision);
        check("different monitor tiles do not share wrong images", multiBytes == 2 * 240L * 180 * 4 && cache.BuildCount == multiBuilds);
        cache.RasterCacheByteLimit = 240L * 180 * 4;
        check("root caches share the existing immediate LRU byte budget", cache.CachedRasterBytes <= cache.RasterCacheByteLimit);
        Compare("an evicted output tile rebuilds with exact pixels", revision: revision, tx: -80.5f, ty: 11.25f);
        cache.RasterCacheByteLimit = 32;
        Compare("an oversized composite safely falls back", revision: revision);
        check("oversized roots allocate no retained raster", cache.CachedRasterBytes == 0);
        cache.RasterCacheByteLimit = 128L * 1024 * 1024;
        Compare("a mutable dynamic range never reuses a completed composite", revision: revision, first: 1, last: 1);
        check("dynamic root scenes conservatively drop static composite state", cache.CachedRasterBytes == 0);
        Compare("caller bypass drops mixed-root caches", revision: revision, bypass: true);
        check("caller bypass retains no root raster", cache.CachedRasterBytes == 0);
        Compare("a non-unit output matrix uses correct fallback", revision: revision, scale: 1.25f);
        check("scaled output does not retain a wrongly sampled root tile", cache.CachedRasterBytes == 0);
        Compare("fractional antialiased output clips do not flatten coverage twice", revision: revision, aaClip: true);
        check("antialiased device clip conservatively bypasses root cache", cache.CachedRasterBytes == 0);
        Compare("a painted destination has the same single-opacity result", revision: revision, painted: true, ordinaryDraw: true);
        check("arbitrary existing destination pixels use direct drawing instead of flattening blend rounding", cache.CachedRasterBytes == 0);
        Compare("a different CPU channel format follows its own renderer output", revision: revision, rgba: true);
        check("RGBA output never adopts a BGRA root raster", cache.CachedRasterBytes == 0);
        Compare("a color-managed output preserves its renderer color conversion", revision: revision, srgb: true);
        check("color-managed output bypasses untagged BGRA raster caching", cache.CachedRasterBytes == 0);
        var clone = scene.Clone(); Compare("a replacement source identity invalidates", revision: revision, input: clone);
        var depth = Scene(); depth.Canvas.DepthPriorityOcclusion = true;
        depth.Canvas.Projection.Matrix = new(1, .07, 10, .02, 1, 5, 0, -.002, 1);
        var plane = CanvasProjectionGeometry.ResolvePlaneBounds(depth.Canvas.Projection);
        depth.Canvas.Projection.Quad = new List<PointD> { new(plane.Left, plane.Top), new(plane.Right, plane.Top),
            new(plane.Right, plane.Bottom), new(plane.Left, plane.Bottom) }.Select(depth.Canvas.Projection.Matrix.TransformPoint).ToList();
        depth.Elements[1].IsBillboard = true; depth.Elements[1].BillboardAnchor = new(80, 60); depth.Elements[1].BillboardHeight = -15;
        Compare("a static perspective/depth main root uses real composition inside its cache", input: depth);
        builds = cache.BuildCount; Compare("a static depth composite remains pixel-identical on cache hit", input: depth);
        check("static depth compositions reuse one complete raster", cache.BuildCount == builds && cache.CachedRasterBytes > 0);

        var animated = Scene(); var sprite = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 80,
            AssetId = assets.Add(Convert.FromBase64String("R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs="), "image/gif") };
        new SceneGraph(animated).InsertElement(sprite, rootCanvas: RootCanvasKind.Foreground);
        byte[] animationA = Compare("animated auxiliary images use the real renderer", input: animated);
        renderer.AnimationTime = reference.AnimationTime = TimeSpan.FromMilliseconds(100);
        byte[] animationB = Compare("animated auxiliary images advance without a revision change", input: animated);
        check("animation changes pixels and never retains a full static composite", !animationA.AsSpan().SequenceEqual(animationB) && cache.CachedRasterBytes == 0);
        var keyboard = Scene(); new SceneGraph(keyboard).InsertElement(new() { Kind = ElementKind.Presentation, Width = 100, Height = 70,
            Presentation = new() }, rootCanvas: RootCanvasKind.Foreground);
        Compare("keyboard runtime regions remain uncached", input: keyboard);
        check("keyboard regions never enter the static root cache", cache.CachedRasterBytes == 0);
        var window = Scene(); new SceneGraph(window).InsertElement(new() { Kind = ElementKind.Image, Width = 100, Height = 70,
            WindowSource = new() { BindingIntent = Guid.NewGuid() } }, rootCanvas: RootCanvasKind.Foreground);
        Compare("live window fallback regions remain uncached", input: window);
        check("window runtime regions never enter the static root cache", cache.CachedRasterBytes == 0);
        var filter = Scene(); new SceneGraph(filter).InsertElement(new() { Kind = ElementKind.LivePixelate, Width = 90, Height = 60,
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 5 }] }, rootCanvas: RootCanvasKind.Foreground);
        Compare("live filter prefixes remain on the renderer path", input: filter);
        check("live filters never enter the static root cache", cache.CachedRasterBytes == 0);
        var photograph = Scene(); using var picture = SKSurface.Create(new SKImageInfo(240, 180)); picture.Canvas.Clear(SKColors.White);
        using var pic = picture.Snapshot(); using var png = pic.Encode(SKEncodedImageFormat.Png, 100);
        photograph.Kind = DocumentKind.FrozenCapture; photograph.Background = new() { Kind = ElementKind.Image,
            Width = 240, Height = 180, AssetId = assets.Add(png.ToArray()) };
        Compare("frozen photograph remains outside author opacity on cold root cache", input: photograph);
        builds = cache.BuildCount; Compare("frozen photograph hot cache preserves the same root blend", input: photograph);
        check("static frozen-base three-root composite also caches", cache.BuildCount == builds && cache.CachedRasterBytes > 0);

        void Changed(string name, long revision = 1, AssetLibrary? library = null)
        {
            long before = cache.BuildCount; Compare(name, revision: revision, library: library);
            check(name + " rebuilt", cache.BuildCount > before);
        }

        byte[] Compare(string name, long revision = 1, int first = -1, int last = -1, bool bypass = false,
            float tx = 0, float ty = 0, float scale = 1, bool aaClip = false, bool painted = false, bool ordinaryDraw = false,
            SceneDocument? input = null, AssetLibrary? library = null, bool rgba = false, bool srgb = false)
        {
            var document = input ?? scene; var resources = library ?? assets;
            using var colorSpace = srgb ? SKColorSpace.CreateSrgb() : null;
            var info = new SKImageInfo(240, 180, rgba ? SKColorType.Rgba8888 : SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace);
            using var expected = SKSurface.Create(info);
            using var actual = SKSurface.Create(info);
            foreach (var canvas in new[] { expected.Canvas, actual.Canvas })
            {
                canvas.Clear(painted ? SKColors.AliceBlue : SKColors.Transparent); canvas.Translate(tx, ty); canvas.Scale(scale);
                if (aaClip) canvas.ClipRect(new(10.25f, 8.5f, 210.75f, 160.5f), SKClipOperation.Intersect, true);
            }
            int saveCount = actual.Canvas.SaveCount; var matrix = actual.Canvas.TotalMatrix; var clip = actual.Canvas.DeviceClipBounds;
            reference.Draw(expected.Canvas, document, resources);
            if (ordinaryDraw) cache.Draw(actual.Canvas, document, resources, revision, 0, first, last, bypass);
            else cache.DrawOnClearedRasterTarget(actual, document, resources, revision, 0, first, last, bypass);
            using var a = actual.PeekPixels(); using var e = expected.PeekPixels();
            bool equal = a.GetPixelSpan().SequenceEqual(e.GetPixelSpan());
            check(name, equal && actual.Canvas.SaveCount == saveCount && actual.Canvas.TotalMatrix == matrix && actual.Canvas.DeviceClipBounds == clip);
            return a.GetPixelSpan().ToArray();
        }
    }

    private static SceneDocument Scene()
    {
        var main = Box(30, 20, 120, 100, 0xAAFF3300); var sky = Box(-15, -10, 230, 170, 0xBB3355FF); var front = Box(65, 45, 45, 50, 0xDD55FF22);
        var scene = new SceneDocument { Viewport = new(-20.5, -10.25, 280, 220), Elements = [front, main, sky], LayerOpacity = .6,
            Canvas = new() { BackgroundColor = 0x44445566, Grid = new() { Visible = true, Color = 0x55332288, FlatCellSize = 30 } } };
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport); scene.Canvas.Projection.Enabled = true;
        SceneCanvasRoots.Assign(scene, sky.Id, RootCanvasKind.Background); SceneCanvasRoots.Assign(scene, front.Id, RootCanvasKind.Foreground);
        return scene;
    }

    private static CanvasElement Box(double x, double y, double width, double height, uint fill) => new() {
        Kind = ElementKind.RoundedRectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = fill, StrokeWidth = 2 } };
}
