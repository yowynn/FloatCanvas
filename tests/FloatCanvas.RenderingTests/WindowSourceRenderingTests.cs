using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using System.Text.Json;

internal static class WindowSourceRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        LeaseOwnership(check); Layout(check); LivePixels(check); EffectsAndFreeze(check); InstancesAndPrefix(check);
    }

    internal static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory); var assets = new AssetLibrary(); var element = Image(assets);
        element.Width = 260; element.Height = 220; element.Transform = Affine2D.Translation(50, 20);
        element.ImageBorderWidth = 3; element.Style.Foreground = 0xFF8FD1FF; element.ImageCrop = new(.125, 0, .75, 1);
        var source = Scene(element); source.Viewport = new(0, 0, 360, 260); source.Canvas.BackgroundColor = 0xFF26323A;
        using var pixels = Pixels(320, 180, (x, y) => x is > 100 and < 230 && y is > 45 and < 135
            ? new SKColor(0xFF54C3A5) : new SKColor((byte)(30 + x / 2), (byte)(45 + y / 2), (byte)(100 + y / 3)));
        using var lease = Frame(source, element, pixels); object context = new(); using var set = WindowFrameSet.Capture(context, [lease]);
        using var renderer = new SceneRenderer { WindowFrames = set, WindowOwnerContext = context };
        File.WriteAllBytes(Path.Combine(directory, "window-contain-synthetic.png"), renderer.ExportPng(source, assets));
        var body = renderer.PrepareFrozenWindowBody(source, element, assets);
        File.WriteAllBytes(Path.Combine(directory, "window-frozen-body-synthetic.png"), body.PngBytes);
    }

    private static void LeaseOwnership(Action<string, bool> check)
    {
        var owner = Guid.NewGuid(); var source = new WindowSourceKey(owner, new(null, Guid.NewGuid())); var intent = Guid.NewGuid();
        var frameId = new WindowFrameId(Guid.NewGuid(), 3, long.MaxValue); var budget = new WindowFrameBudget(64);
        using var bitmap = new SKBitmap(4, 4); bitmap.Erase(SKColors.Red);
        var first = WindowFrameLease.FromBitmapCopy(source, intent, frameId, bitmap, 40, 40, budget);
        bitmap.Erase(SKColors.Blue);
        using (var copied = SKBitmap.FromImage(first.Image))
            check("window frame copies mutable bitmap pixels exactly once into independent immutable content", copied.GetPixel(0, 0) == SKColors.Red);
        check("window frame retains native content dimensions and a full 64-bit sequence", first.ContentWidth == 40 && first.ContentHeight == 40 && first.Width == 4 && first.Height == 4 && first.Id.Sequence == long.MaxValue);
        var second = first.Retain(); first.Dispose(); first.Dispose();
        check("disposing a publisher handle leaves a retained consumer and unique byte charge alive", budget.RetainedBytes == 64 && second.Image.Width == 4);
        check("new frames are rejected while the budget is held by an old displayed frame", Throws<InvalidOperationException>(() => WindowFrameLease.FromBitmapCopy(source, intent, frameId, bitmap, budget: budget)) && budget.RetainedBytes == 64);
        var otherSource = new WindowSourceKey(owner, new(Guid.NewGuid(), Guid.NewGuid())); using var alias = second.ForSource(otherSource, intent);
        object context = new(); var bundle = WindowFrameSet.Capture(context, [second, alias]); var retained = bundle.Retain(); bundle.Dispose(); second.Dispose();
        check("a frame bundle keeps explicit owner identity and two source keys sharing one backing", ReferenceEquals(retained.OwnerContext, context) && retained.Count == 2 && retained.PixelBytes == 64 && budget.RetainedBytes == 64);
        check("multiple occurrences never require asset-id keyed copies of the same source pixels", retained.TryGet(source, out var one) && retained.TryGet(otherSource, out var two) && ReferenceEquals(one.Image, two.Image));
        check("cross-resource frame aliases cannot inherit another owner's binding", Throws<ArgumentException>(() => alias.ForSource(new(Guid.NewGuid(), new(null, Guid.NewGuid())), intent)));
        retained.Dispose(); alias.Dispose();
        check("only final frame lease release returns its unique backing to the budget", budget.RetainedBytes == 0);
        check("disposed frame and bundle handles reject resurrection", Throws<ObjectDisposedException>(() => first.Retain()) && Throws<ObjectDisposedException>(() => bundle.Retain()));
        using var recovered = WindowFrameLease.FromBitmapCopy(source, intent, frameId, bitmap, budget: budget);
        check("frame publication can recover after old displayed leases release their budget", budget.RetainedBytes == 64);
        check("duplicate bundle keys fail without stealing caller ownership or leaking a retained handle", Throws<ArgumentException>(() => WindowFrameSet.Capture(context, [recovered, recovered])) && recovered.Image.Width == 4 && budget.RetainedBytes == 64);
        var handles = Enumerable.Range(0, 5).Select(_ => recovered.ForSource(new(owner, new(null, Guid.NewGuid())), intent)).ToArray();
        try { check("the four-source cap counts real authored source addresses", Throws<ArgumentOutOfRangeException>(() => WindowFrameSet.Capture(context, handles)) && budget.RetainedBytes == 64); }
        finally { foreach (var handle in handles) handle.Dispose(); }
        using var oversized = new SKBitmap(1921, 1);
        check("an over-cap frame is rejected before publication and never consumes retained bytes", Throws<ArgumentOutOfRangeException>(() => WindowFrameLease.FromBitmapCopy(source, intent, frameId, oversized, budget: budget)) && budget.RetainedBytes == 64);
        check("empty binding or recycled invalid connection identities cannot become frames", Throws<ArgumentException>(() => WindowFrameLease.FromBitmapCopy(source, Guid.Empty, frameId, bitmap, budget: budget)) &&
            Throws<ArgumentException>(() => WindowFrameLease.FromBitmapCopy(source, intent, new(Guid.Empty, 1, 1), bitmap, budget: budget)));
        using var image = SKImage.FromPixelCopy(bitmap.PeekPixels());
        check("failed image ownership transfer leaves the caller's image usable", Throws<InvalidOperationException>(() => WindowFrameLease.FromImage(source, intent, frameId, image, budget: budget)) && image.Width == 4);
        var transferredBudget = new WindowFrameBudget(64); var native = new SKBitmap(4, 4); native.Erase(SKColors.Green); native.SetImmutable();
        var sharedImage = SKImage.FromBitmap(native);
        using var transferred = WindowFrameLease.FromImage(source, intent, frameId, sharedImage, budget: transferredBudget);
        native.Dispose();
        using var nativeReleased = SKBitmap.FromImage(transferred.Image);
        check("a transferred image retains immutable native bitmap pixels after the producer disposes its bitmap", nativeReleased.GetPixel(0, 0) == SKColors.Green && transferredBudget.RetainedBytes == 64);
        var paddedBudget = new WindowFrameBudget(512);
        using var padded = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul), 32); padded.Erase(SKColors.Red);
        var paddedLease = WindowFrameLease.FromBitmapCopy(source, intent, frameId, padded, budget: paddedBudget);
        using (var actualPixels = paddedLease.Image.PeekPixels())
            check("padded source stride is charged by the actual copied backing rather than assumed tight rows", paddedLease.PixelBytes == (long)actualPixels.RowBytes * actualPixels.Height && paddedBudget.RetainedBytes == paddedLease.PixelBytes);
        paddedLease.Dispose(); check("final padded-copy release returns the complete actual backing charge", paddedBudget.RetainedBytes == 0);
    }

    private static void Layout(Action<string, bool> check)
    {
        bool valid = WindowImageLayout.TryCreate(200, 100, 120, 120, null, true, out var contain);
        check("contain preserves the complete window's aspect inside the fixed authored body", valid && contain.SourcePixels == new RectD(0, 0, 200, 100) && contain.Destination == new RectD(0, 30, 120, 60));
        check("crop is applied once in full-window normalized coordinates before contain", WindowImageLayout.TryCreate(200, 100, 120, 120, new(.25, 0, .5, 1), true, out var cropped) && cropped.SourcePixels == new RectD(50, 0, 100, 100) && cropped.Destination == new RectD(0, 0, 120, 120));
        check("stretch retains the ordinary image fill-body geometry", WindowImageLayout.TryCreate(200, 100, 120, 120, null, false, out var stretch) && stretch.Destination == new RectD(0, 0, 120, 120));
        check("source resize changes only letterbox layout rather than authored target dimensions", WindowImageLayout.TryCreate(100, 200, 120, 120, null, true, out var portrait) && portrait.Destination == new RectD(30, 0, 60, 120));
        check("contain uses native content aspect despite downsample integer rounding", WindowImageLayout.TryCreate(303, 1080, 100, 100, null, true, out var rounded, 1080, 3840) && rounded.SourcePixels == new RectD(0, 0, 303, 1080) && rounded.Destination == new RectD(35.9375, 0, 28.125, 100));
        check("invalid or empty source crop cannot produce nonfinite draw geometry", !WindowImageLayout.TryCreate(200, 100, 120, 120, new(2, 2, 1, 1), true, out _) && !WindowImageLayout.TryCreate(200, 100, double.NaN, 120, null, true, out _) && !WindowImageLayout.TryCreate(0, 100, 120, 120, null, true, out _));
    }

    private static bool Throws<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }

    private static void LivePixels(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var element = Image(assets); var source = Scene(element); object context = new();
        string before = JsonSerializer.Serialize(source);
        using var red = Pixels(200, 100, (_, _) => SKColors.Red);
        using var lease = Frame(source, element, red); using var set = WindowFrameSet.Capture(context, [lease]);
        using var renderer = new SceneRenderer { WindowFrames = set, WindowOwnerContext = context };
        using var live = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        check("live window draw uses a full-window frame with transparent contain letterbox", live.GetPixel(50, 50) == SKColors.Red && live.GetPixel(50, 10).Alpha == 0);
        check("window alpha picking excludes contain letterbox and hits actual body", renderer.PickDepth(source, assets, new(50, 10), 0) is null && renderer.PickDepth(source, assets, new(50, 50), 0)?.Id == element.Id);
        renderer.WindowOwnerContext = new object();
        using var wrongOwner = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        check("equal resource identity cannot adopt frames from a different explicit editor context", wrongOwner.GetPixel(50, 50) == SKColors.Blue && wrongOwner.GetPixel(50, 10) == SKColors.Blue);
        renderer.WindowOwnerContext = context; var oldIntent = element.WindowSource!.BindingIntent; element.WindowSource.BindingIntent = Guid.NewGuid();
        using var rebound = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        check("rebind rejects an old frame at the same source address", rebound.GetPixel(50, 50) == SKColors.Blue);
        element.WindowSource.BindingIntent = oldIntent;
        element.ImageCrop = new(.25, 0, .5, 1);
        using var cropped = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        check("full-window crop changes content aspect before fitting rather than cropping an already fitted target", cropped.GetPixel(50, 10) == SKColors.Red);
        element.ImageCrop = null;
        var second = Image(assets); second.Transform = Affine2D.Translation(120, 0); source.Elements.Add(second); source.Viewport = new(0, 0, 240, 120);
        using var green = Pixels(200, 100, (_, _) => SKColors.Green); using var greenLease = Frame(source, second, green);
        using var two = WindowFrameSet.Capture(context, [lease, greenLease]); renderer.WindowFrames = two;
        using var both = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        check("two sources sharing the same fallback asset cannot collide in the runtime image map", element.AssetId == second.AssetId && both.GetPixel(50, 50) == SKColors.Red && both.GetPixel(170, 50) == SKColors.Green);
        source.Elements.Remove(second); source.Viewport = new(0, 0, 120, 120);
        check("live image rendering crop picking and binding checks never add assets or author runtime identity", assets.Count == 1 && !source.IsRuntimeEvaluated && source.Elements[0].WindowSource!.BindingIntent == oldIntent && !before.Contains("ConnectionId", StringComparison.Ordinal));
        using var plain = new SceneRenderer(); using var fallback = SKBitmap.Decode(plain.ExportPng(source, assets, true));
        check("unconfigured export uses only the authored fallback without a frame or service", fallback.GetPixel(50, 50) == SKColors.Blue);
        source.Elements[0].IsBillboard = true; source.Elements[0].BillboardAnchor = new(60, 95); source.Elements[0].BillboardHeight = 35;
        source.Elements[0].Transform = Affine2D.Rotation(12); source.Canvas.Projection.Enabled = true;
        source.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .002, 1); source.Canvas.DepthPriorityOcclusion = true;
        renderer.WindowFrames = set;
        using var billboard = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        var point = CanvasMapping.Create(source).ElementToScreen(source.Elements[0]).TransformPoint(new(50, 50));
        check("window pixels preserve billboard world placement and depth picking", billboard.Pixels.Any(pixel => pixel.Red > 200 && pixel.Blue < 20) && renderer.PickDepth(source, assets, point, 0)?.Id == element.Id);
    }

    private static void EffectsAndFreeze(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var element = Image(assets); element.WindowSource!.Fit = WindowSourceFit.Stretch;
        element.Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(0, 0, 1, 1), Amount = 4 }];
        element.ImageBorderWidth = 3; element.Style.Foreground = 0xFFFFFF00;
        var source = Scene(element); object context = new();
        using var firstPixels = Pixels(100, 100, (x, _) => x < 50 ? SKColors.Red : SKColors.Transparent);
        using var secondPixels = Pixels(100, 100, (x, _) => x >= 50 ? SKColors.Blue : SKColors.Transparent);
        var connection = Guid.NewGuid();
        using var a = Frame(source, element, firstPixels, 1, connection); using var b = Frame(source, element, secondPixels, 1 + (1L << 32), connection);
        using var first = WindowFrameSet.Capture(context, [a]); using var next = WindowFrameSet.Capture(context, [b]);
        using var renderer = new SceneRenderer { WindowFrames = first, WindowOwnerContext = context };
        using var red = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        renderer.WindowFrames = next; using var blue = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        check("effect images follow full runtime frame identity rather than fallback asset and int frame", red.GetPixel(20, 50).Red > 200 && blue.GetPixel(20, 50).Alpha == 0 && blue.GetPixel(80, 50).Blue > 200);
        check("border alpha support changes with the actual live frame", red.GetPixel(48, 50).Red > 0 && blue.GetPixel(1, 50).Alpha == 0 && blue.GetPixel(52, 50).Alpha > 0);
        check("previous frame pixels remain immutable after a new frame replaces caches", red.GetPixel(20, 50).Red > 200 && red.GetPixel(80, 50).Alpha == 0);
        check("live border support retains a bounded current frame instead of every sequence", renderer.CachedImageBorderBytes < 200_000);
        element.WindowSource.Fit = WindowSourceFit.Contain; element.Width = 100; element.Height = 150; source.Viewport = new(0, 0, 120, 170);
        element.ImageCrop = new(.1, 0, .8, 1); element.Style.Opacity = .43; source.LayerOpacity = .62;
        element.ImageFrame = [new(0, 0), new(1, 0), new(.5, 1)];
        string before = JsonSerializer.Serialize(source);
        var body = renderer.PrepareFrozenWindowBody(source, element, assets);
        var frozenElement = element.Clone(); frozenElement.AssetId = assets.Add(body.PngBytes);
        frozenElement.WindowSource = null; frozenElement.ImageCrop = null; frozenElement.ImageFrame = null; frozenElement.Effects.Clear();
        var frozen = source.Clone(); frozen.Elements = [frozenElement];
        using var originalPixels = SKBitmap.Decode(renderer.ExportPng(source, assets, true));
        using var frozenPixels = SKBitmap.Decode(renderer.ExportPng(frozen, assets, true));
        var differences = originalPixels.Pixels.Zip(frozenPixels.Pixels).Select((pair, index) => (pair, index)).Where(item => item.pair.First != item.pair.Second).ToArray();
        Console.WriteLine("WINDOW_FREEZE_DIFF " + JsonSerializer.Serialize(new { Count = differences.Length,
            Bounds = differences.Length == 0 ? null : new[] { differences.Min(item => item.index % originalPixels.Width), differences.Min(item => item.index / originalPixels.Width), differences.Max(item => item.index % originalPixels.Width), differences.Max(item => item.index / originalPixels.Width) },
            Samples = differences.Take(5).Select(item => new { X = item.index % originalPixels.Width, Y = item.index / originalPixels.Width, Before = item.pair.First.ToString(), After = item.pair.Second.ToString() }) }));
        check("freeze body preserves natural source sampling and authored logical dimensions", body.LogicalWidth == 100 && body.LogicalHeight == 150 && body.PixelWidth == 80 && body.PixelHeight == 120);
        check("freezing retains visible local body without baking placement opacity or border twice", Near(originalPixels, frozenPixels, 1));
        check("freeze materialization never mutates author crop effects contour opacity or source binding", JsonSerializer.Serialize(source) == before && element.Effects.Count == 1 && element.WindowSource is not null);
        element.Width = 8000; element.Height = 4000;
        var bounded = renderer.PrepareFrozenWindowBody(source, element, assets);
        check("large logical freeze body obeys the explicit published bitmap cap", bounded.PixelWidth <= 1920 && bounded.PixelHeight <= 1080 && bounded.LogicalWidth == 8000 && bounded.LogicalHeight == 4000);
    }

    private static void InstancesAndPrefix(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var element = Image(assets); var definition = new PrefabDefinition { Graph = new() { Elements = [element] } };
        var firstInstance = new PrefabInstance { DefinitionId = definition.Id }; var secondInstance = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(120, 0) };
        var source = new SceneDocument { Definitions = [definition], Instances = [firstInstance, secondInstance], RootOrder = [firstInstance.Id, secondInstance.Id], Viewport = new(0, 0, 240, 120) };
        source.Canvas.Grid.Visible = false; object context = new(); using var red = Pixels(200, 100, (_, _) => SKColors.Red);
        using var lease = WindowFrameLease.FromBitmapCopy(new(source.ResourceId, new(definition.Id, element.Id)), element.WindowSource!.BindingIntent,
            new(Guid.NewGuid(), 1, 1), red);
        using var frames = WindowFrameSet.Capture(context, [lease]); using var renderer = new SceneRenderer { WindowFrames = frames, WindowOwnerContext = context };
        var evaluation = SceneEvaluation.Build(source);
        using var pixels = SKBitmap.Decode(renderer.ExportPng(evaluation.RenderDocument, assets, true));
        check("definition occurrences share one source frame at their distinct world placements", frames.Count == 1 && pixels.GetPixel(50, 50) == SKColors.Red && pixels.GetPixel(170, 50) == SKColors.Red);
        var hit = renderer.PickDepth(evaluation.RenderDocument, assets, new(170, 50), 0);
        check("runtime alpha hit maps the correct instance host rather than its definition source id", hit is not null && evaluation.ResolveSelectableNode(hit.Id) == secondInstance.Id);
        var draft = source.Clone(); draft.Elements = [element.Clone()]; draft.Instances.Clear(); draft.RootOrder = null; draft.Definitions.Clear();
        using var draftPixels = SKBitmap.Decode(renderer.ExportPng(draft, assets, true));
        check("a definition draft root does not guess an owner definition binding from a naked source GUID", draftPixels.GetPixel(50, 50) == SKColors.Blue);
        var direct = Scene(element.Clone()); var live = direct.Elements[0];
        direct.Elements.Add(new() { Kind = ElementKind.LivePixelate, Width = 100, Height = 100, Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1, Bounds = new(0, 0, 1, 1) }] });
        using var directLease = Frame(direct, live, red); using var directFrames = WindowFrameSet.Capture(context, [directLease]); renderer.WindowFrames = directFrames;
        using var prefix = SKBitmap.Decode(renderer.ExportPng(direct, assets, true));
        check("live filter sampling reads the same fixed window frame from its lower painter prefix", prefix.GetPixel(50, 50) == SKColors.Red);
    }

    private static CanvasElement Image(AssetLibrary assets) => new()
    {
        Kind = ElementKind.Image, Width = 100, Height = 100, AssetId = assets.Add(Png(SKColors.Blue)),
        WindowSource = new() { BindingIntent = Guid.NewGuid(), Fit = WindowSourceFit.Contain }, Style = new() { StrokeWidth = 0 }
    };
    private static SceneDocument Scene(CanvasElement image)
    { var scene = new SceneDocument { Viewport = new(0, 0, 120, 120), Elements = [image] }; scene.Canvas.Grid.Visible = false; return scene; }
    private static WindowFrameLease Frame(SceneDocument source, CanvasElement element, SKBitmap pixels, long sequence = 1, Guid? connection = null) =>
        WindowFrameLease.FromBitmapCopy(new(source.ResourceId, new(null, element.Id)), element.WindowSource!.BindingIntent, new(connection ?? Guid.NewGuid(), 1, sequence), pixels);
    private static SKBitmap Pixels(int width, int height, Func<int, int, SKColor> color)
    { var pixels = new SKBitmap(width, height); for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixels.SetPixel(x, y, color(x, y)); return pixels; }
    private static byte[] Png(SKColor color)
    { using var bitmap = Pixels(20, 20, (_, _) => color); using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray(); }
    private static bool Near(SKBitmap first, SKBitmap second, int tolerance)
    {
        if (first.Width != second.Width || first.Height != second.Height) return false;
        using var a = first.PeekPixels(); using var b = second.PeekPixels();
        var firstBytes = a.GetPixelSpan(); var secondBytes = b.GetPixelSpan();
        // Compare the stored premultiplied pixels. Unpremultiplication amplifies one
        // rounding byte into many RGB values on an almost transparent AA boundary.
        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width * 4; x++)
            if (Math.Abs(firstBytes[y * a.RowBytes + x] - secondBytes[y * b.RowBytes + x]) > tolerance) return false;
        return true;
    }
}
