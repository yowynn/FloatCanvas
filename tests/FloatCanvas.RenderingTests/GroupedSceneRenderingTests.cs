using System.Text;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class GroupedSceneRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (bool perspective in new[] { false, true })
        foreach (bool depth in new[] { false, true })
        {
            var fixture = Create(perspective, depth, filters: true);
            using var actual = new SceneRenderer(); using var reference = new SceneRenderer();
            string before = JsonSerializer.Serialize(fixture.Source);
            using var desktop = Backdrop(fixture.Source.Viewport); actual.LiveBackdrop = reference.LiveBackdrop = desktop;
            foreach (int milliseconds in new[] { 0, 120 })
            {
                actual.AnimationTime = reference.AnimationTime = TimeSpan.FromMilliseconds(milliseconds);
                using var rendered = SKBitmap.Decode(actual.ExportPng(fixture.Source, fixture.Assets, true));
                using var expected = SKBitmap.Decode(reference.ExportPng(fixture.Reference(), fixture.Assets, true));
                check($"nested shapes image animation billboard and filters match independent world rendering perspective={perspective} depth={depth} time={milliseconds}",
                    rendered.Pixels.SequenceEqual(expected.Pixels));
            }
            var flat = fixture.Reference();
            var sourceImage = fixture.Source.Elements.Single(item => item.Kind == ElementKind.Image && item.Id == fixture.ImageId);
            var worldImage = flat.Elements.Single(item => item.Id == fixture.ImageId);
            var point = CanvasMapping.Create(flat).ElementToScreen(worldImage).TransformPoint(new(worldImage.Width * .8, worldImage.Height * .5));
            check("grouped image picking uses the same world occurrence as pixels", actual.PickDepth(fixture.Source, fixture.Assets, point, 0)?.Id == reference.PickDepth(flat, fixture.Assets, point, 0)?.Id);
            var bounds = SceneRenderer.ProjectedBounds(flat, worldImage, true);
            check("grouped image ink intersection follows ancestor placement", actual.IntersectsElementInk(fixture.Source, fixture.Assets, sourceImage, bounds));
            var draft = sourceImage.Clone(); draft.Transform = Affine2D.Translation(15, 6).Compose(draft.Transform);
            var draftWorld = fixture.ManualWorld(draft);
            check("bounds resolve a draft's new transform rather than the stored leaf with the same ID", SceneRenderer.ProjectedBounds(fixture.Source, draft, true) ==
                CanvasMapping.Create(flat).ProjectedBounds(draftWorld, true));
            var sourceEffect = fixture.Source.Elements.First(item => ElementGeometry.IsLiveEffect(item.Kind));
            check("live capture ROI includes nested world placement and screen-pixel filter support", SceneRenderer.LiveEffectSamplingBounds(fixture.Source, sourceEffect) ==
                SceneRenderer.LiveEffectSamplingBounds(flat, flat.Elements.Single(item => item.Id == sourceEffect.Id)));
            check("animation damage and next-frame scheduling use evaluated image bounds", actual.CaptureAnimationSchedule(fixture.Source, fixture.Assets, TimeSpan.Zero).DirtyBounds.SequenceEqual(
                reference.CaptureAnimationSchedule(flat, fixture.Assets, TimeSpan.Zero).DirtyBounds));
            var evaluated = SceneEvaluation.Build(fixture.Source);
            using var worldPixels = SKBitmap.Decode(actual.ExportPng(evaluated.RenderDocument, fixture.Assets, true));
            using var sourcePixels = SKBitmap.Decode(actual.ExportPng(fixture.Source, fixture.Assets, true));
            check("runtime world documents are never transformed twice", worldPixels.Pixels.SequenceEqual(sourcePixels.Pixels));
            check("rendering picking animation and sampling leave the source document unchanged", JsonSerializer.Serialize(fixture.Source) == before && !fixture.Source.IsRuntimeEvaluated);
            var isolated = new SceneDocument { Viewport = flat.Viewport, Canvas = flat.Canvas.Clone(), Elements = [flat.Elements.First(item => item.Kind == ElementKind.LiveBlur).Clone()] };
            actual.LiveEffectContext = fixture.Source; reference.LiveEffectContext = flat;
            using (var selectedActual = SKBitmap.Decode(actual.ExportPng(isolated, fixture.Assets, true)))
            using (var selectedExpected = SKBitmap.Decode(reference.ExportPng(isolated, fixture.Assets, true)))
                check("isolated grouped live-filter exports sample their complete evaluated lower scene", selectedActual.Pixels.SequenceEqual(selectedExpected.Pixels));
            actual.LiveEffectContext = reference.LiveEffectContext = null;
            fixture.Source.Groups[0].Visible = false;
            check("hidden ancestors stop descendant animation and live capture scheduling", !actual.HasAnimatedImages(fixture.Source, fixture.Assets) && !SceneRenderer.HasLiveEffects(fixture.Source));
        }
        CachedDrawOrder(check);
        LegacyPaintSlots(check);
    }

    private static void CachedDrawOrder(Action<string, bool> check)
    {
        var fixture = Create(false, false, filters: false);
        using var renderer = new SceneRenderer(); using var cache = new OverlaySceneCache(renderer);
        using var target = SKSurface.Create(new SKImageInfo(500, 400, SKColorType.Bgra8888, SKAlphaType.Premul));
        int sourceIndex = fixture.Source.Elements.FindIndex(item => item.Id == fixture.ImageId);
        target.Canvas.Clear(SKColors.Transparent); cache.Draw(target.Canvas, fixture.Source, fixture.Assets, 1, 7, sourceIndex, sourceIndex);
        fixture.Source.Elements[sourceIndex].Transform = Affine2D.Translation(13, 3).Compose(fixture.Source.Elements[sourceIndex].Transform);
        target.Canvas.Clear(SKColors.Transparent); cache.Draw(target.Canvas, fixture.Source, fixture.Assets, 1, 7, sourceIndex, sourceIndex);
        using var pixels = target.Snapshot(); using var actual = SKBitmap.FromImage(pixels);
        using var reference = new SceneRenderer(); using var expected = SKBitmap.Decode(reference.ExportPng(fixture.Reference(), fixture.Assets, true));
        int difference = actual.Pixels.Zip(expected.Pixels, (a, b) => Math.Max(Math.Max(Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green)), Math.Max(Math.Abs(a.Blue - b.Blue), Math.Abs(a.Alpha - b.Alpha)))).Max();
        // Cached transparent suffixes can differ by one byte from direct AA blending over the opaque prefix.
        Console.WriteLine($"GROUP_CACHE_DIAGNOSTIC hit={cache.HitCount} maxDifference={difference}");
        check("raster cache remaps a source dynamic index into nested paint order", difference <= 1 && cache.HitCount > 0);
    }

    private static void LegacyPaintSlots(Action<string, bool> check)
    {
        var group = new CanvasGroup();
        var first = new CanvasElement { Kind = ElementKind.Ellipse, GroupId = group.Id, Width = 90, Height = 90, Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        var middle = first.Clone(true); middle.GroupId = null; middle.Style.Fill = 0xFF00FF00;
        var last = first.Clone(true); last.Style.Fill = 0x800000FF;
        var source = new SceneDocument { Viewport = new(0, 0, 100, 100), Groups = [group], Elements = [first, middle, last] };
        var flat = source.Clone(); flat.Groups.Clear(); foreach (var leaf in flat.Elements) leaf.GroupId = null;
        using var renderer = new SceneRenderer();
        using var actual = SKBitmap.Decode(renderer.ExportPng(source, new(), true)); using var expected = SKBitmap.Decode(renderer.ExportPng(flat, new(), true));
        check("v9 interleaved legacy groups retain exact original translucent paint slots", actual.Pixels.SequenceEqual(expected.Pixels));
        group.Locked = true;
        check("locked ancestor participates in the same occlusion-aware picking", renderer.PickDepth(source, new(), new(45, 45), 0)?.Id == middle.Id);
    }

    private sealed class Fixture(SceneDocument source, AssetLibrary assets, Guid imageId, Guid outerId, Guid innerId, Affine2D outer, Affine2D inner)
    {
        internal SceneDocument Source { get; } = source;
        internal AssetLibrary Assets { get; } = assets;
        internal Guid ImageId { get; } = imageId;
        internal CanvasElement ManualWorld(CanvasElement element)
        {
            var result = element.Clone();
            var parent = element.GroupId == innerId ? outer.Compose(inner) : element.GroupId == outerId ? outer : Affine2D.Identity;
            if (result.IsBillboard)
            {
                result.BillboardAnchor = parent.TransformPoint(result.BillboardAnchor);
                result.Transform = (parent with { OffsetX = 0, OffsetY = 0 }).Compose(result.Transform with { OffsetX = 0, OffsetY = 0 });
                result.BillboardHeight *= element.GroupId == innerId ? 1.4 * 1.1 : element.GroupId == outerId ? 1.4 : 1;
            }
            else result.Transform = parent.Compose(result.Transform);
            result.GroupId = null; return result;
        }
        internal SceneDocument Reference()
        {
            var result = new SceneDocument { Viewport = Source.Viewport, Canvas = Source.Canvas.Clone(), LayerOpacity = Source.LayerOpacity };
            // Source flat table deliberately has another order than the explicit nested paint blocks.
            var ordered = new List<Guid>();
            void Walk(Guid id)
            {
                var group = Source.Groups.FirstOrDefault(item => item.Id == id);
                if (group is null) ordered.Add(id); else foreach (var child in group.ChildOrder!) Walk(child);
            }
            foreach (var id in Source.RootOrder!) Walk(id);
            result.Elements = ordered.Select(id => ManualWorld(Source.Elements.Single(item => item.Id == id))).ToList(); return result;
        }
    }
    private static Fixture Create(bool perspective, bool depth, bool filters)
    {
        var outerTransform = Affine2D.Translation(65, 45).Compose(Affine2D.Rotation(9)).Compose(Affine2D.Scale(1.1, .9));
        var innerTransform = Affine2D.Translation(30, 25).Compose(Affine2D.Rotation(-5)).Compose(Affine2D.Scale(.85, 1.2));
        var outer = new CanvasGroup { Transform = outerTransform, ElevationScale = 1.4, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var inner = new CanvasGroup { ParentId = outer.Id, Transform = innerTransform, ElevationScale = 1.1, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var assets = new AssetLibrary();
        using var bitmap = new SKBitmap(8, 8); for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) bitmap.SetPixel(x, y, x < 3 ? SKColors.Transparent : SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var picture = new CanvasElement { Kind = ElementKind.Image, AssetId = assets.Add(png.ToArray()), GroupId = inner.Id, Width = 60, Height = 45,
            Transform = Affine2D.Translation(130, 30), Style = new() { StrokeWidth = 0 }, Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 2, Bounds = new(0, 0, 1, 1) }] };
        var animation = new CanvasElement { Kind = ElementKind.Image, AssetId = assets.Add(Animation(), "image/gif"), GroupId = inner.Id, Width = 70, Height = 40, Transform = Affine2D.Translation(40, 110), Style = new() { StrokeWidth = 0 } };
        var shape = new CanvasElement { Kind = ElementKind.Ellipse, Width = 55, Height = 35, GroupId = inner.Id, Transform = Affine2D.Translation(10, 15), Style = new() { Stroke = 0xFF006622, Fill = 0xB000EE33, StrokeWidth = 3 } };
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "Nested 世界", GroupId = inner.Id, Width = 130, Height = 35,
            Transform = Affine2D.Translation(180, 125), Style = new() { Stroke = 0xFF88EEFF, FontSize = 18, StrokeWidth = 0 } };
        var board = new CanvasElement { Kind = ElementKind.Rectangle, GroupId = inner.Id, IsBillboard = true, BillboardAnchor = new(180, 190), BillboardHeight = 35, Width = 60, Height = 75,
            Transform = Affine2D.Rotation(12), Style = new() { Fill = 0xB0FFAA00, StrokeWidth = 0 } };
        var top = new CanvasElement { Kind = ElementKind.Symbol, Symbol = SymbolKind.Star, GroupId = outer.Id, Width = 45, Height = 45, Transform = Affine2D.Translation(285, 20), Style = new() { Fill = 0xFFCC22DD, StrokeWidth = 0 } };
        var underlay = new CanvasElement { Kind = ElementKind.Rectangle, Width = 500, Height = 400, Style = new() { Fill = 0xFF112233, StrokeWidth = 0 } };
        var above = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(360, 160)], Transform = Affine2D.Translation(10, 250), Style = new() { Stroke = 0xFFF0F0FF, StrokeWidth = 3 } };
        var leaves = new List<CanvasElement> { picture, above, board, underlay, animation, shape, top, text };
        inner.ChildOrder = [shape.Id, picture.Id, animation.Id, board.Id, text.Id]; outer.ChildOrder = [inner.Id, top.Id];
        if (filters)
        {
            foreach (var kind in new[] { ElementKind.LivePixelate, ElementKind.LiveBlur })
            {
                var effect = new CanvasElement { Kind = kind, GroupId = inner.Id, Width = 130, Height = 80, Transform = Affine2D.Translation(75, kind == ElementKind.LiveBlur ? 75 : 50),
                    Style = new() { StrokeWidth = 0, Opacity = .8 }, Effects = [new() { Kind = kind == ElementKind.LiveBlur ? ImageEffectKind.Blur : ImageEffectKind.Pixelate, Amount = 4, Bounds = new(0, 0, 1, 1) }] };
                leaves.Insert(1, effect); inner.ChildOrder.Add(effect.Id);
            }
        }
        var source = new SceneDocument { Viewport = new(0, 0, 500, 400), Groups = [outer, inner], Elements = leaves, RootOrder = [underlay.Id, outer.Id, above.Id] };
        if (perspective)
        {
            var initial = CanvasCalibration.CreateInitial(source.Viewport);
            CanvasCalibration.TryCalibrate(initial, [new(50, 60), new(440, 35), new(480, 350), new(15, 365)], source.Viewport, out var projection, out _);
            source.Canvas.Projection = projection;
        }
        source.Canvas.DepthPriorityOcclusion = depth;
        return new(source, assets, picture.Id, outer.Id, inner.Id, outerTransform, innerTransform);
    }
    private static DesktopBackdrop Backdrop(RectD viewport)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)viewport.Width, (int)viewport.Height));
        surface.Canvas.Clear(SKColors.Navy); using var paint = new SKPaint { Color = SKColors.OrangeRed }; surface.Canvas.DrawRect(0, 0, 250, 400, paint);
        return new([new(viewport, surface.Snapshot())]);
    }
    private static byte[] Animation()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("GIF89a"u8); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((byte)0xF0); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(new byte[] { 255, 0, 0, 0, 0, 255 });
        writer.Write(new byte[] { 0x21, 0xFF, 11 }); writer.Write("NETSCAPE2.0"u8); writer.Write(new byte[] { 3, 1, 0, 0, 0 });
        foreach (byte color in new byte[] { 0, 1 })
        {
            writer.Write(new byte[] { 0x21, 0xF9, 4, 4, 10, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2 });
            int codes = 4 | (color << 3) | (5 << 6); writer.Write((byte)codes); writer.Write((byte)(codes >> 8)); writer.Write((byte)0);
        }
        writer.Write((byte)0x3B); return stream.ToArray();
    }
}
