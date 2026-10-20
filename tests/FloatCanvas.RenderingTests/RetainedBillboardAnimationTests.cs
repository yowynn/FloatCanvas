using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class RetainedBillboardAnimationTests
{
    private const string LoopGif = "R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs=";

    internal static void Run(Action<string, bool> check)
    {
        var bytes = Convert.FromBase64String(LoopGif);
        var assets = new AssetLibrary();
        string id = assets.Add(bytes, "image/gif");
        var scene = new SceneDocument { Viewport = new(0, -400, 240, 1000) };
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport);
        scene.Canvas.Projection.Enabled = true;
        var original = Projective2D.FromAffine(Affine2D.Translation(-2000, -3800));
        scene.Canvas.Projection.Matrix = original;
        var anchor = new PointD(2100, 4000);
        var board = new CanvasElement { Kind = ElementKind.Image, AssetId = id, IsBillboard = true,
            BillboardAnchor = anchor, Width = 400, Height = 600, Style = new() { StrokeWidth = 0 } };
        scene.Elements.Add(board);
        using var renderer = new SceneRenderer();
        check("visible retained GIF initially schedules animation without decoding frames", renderer.HasAnimatedImages(scene, assets) &&
            renderer.GetNextAnimationDelay(scene, assets, TimeSpan.Zero) == TimeSpan.FromMilliseconds(100) && renderer.AnimationFrameDecodeCount == 0);

        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .005, 1);
        foreach (var sample in new[] { (Name: "near-horizon margin", Anchor: anchor),
            (Name: "far side", Anchor: new PointD(100, -300)), (Name: "exact pole", Anchor: new PointD(100, -200)) })
        {
            board.BillboardAnchor = sample.Anchor;
            var mapping = CanvasMapping.Create(scene);
            var projected = mapping.ProjectedBounds(board, includeStroke: false);
            check(sample.Name + " hidden GIF fixture reproduces the empty-bounds origin-intersection trap", !mapping.IsElementSafe(board) &&
                projected == default && projected.Intersects(scene.Viewport));
            check(sample.Name + " hidden retained GIF requests no animation refresh or decoding", !renderer.HasAnimatedImages(scene, assets) &&
                renderer.GetNextAnimationDelay(scene, assets, TimeSpan.Zero) is null && renderer.AnimationFrameDecodeCount == 0);
            using var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
            check(sample.Name + " retained GIF renders no pixels and remains undecoded", pixels.Pixels.All(pixel => pixel.Alpha == 0) &&
                renderer.AnimationFrameDecodeCount == 0 && renderer.NextAnimationDelay is null);
        }

        board.BillboardAnchor = anchor;
        scene.Canvas.Projection.Matrix = original;
        check("restoring the perspective resumes the retained GIF's animation schedule", renderer.HasAnimatedImages(scene, assets) &&
            renderer.GetNextAnimationDelay(scene, assets, TimeSpan.Zero) == TimeSpan.FromMilliseconds(100));
        using (var restored = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
            check("restoring the perspective displays the retained GIF again", restored.GetPixel(100, 600).Alpha == 255 &&
                renderer.AnimationFrameDecodeCount == 1 && renderer.NextAnimationDelay is not null);
        check("hidden animation scheduling preserves original GIF bytes and logical dimensions", assets.Get(id).Data.Span.SequenceEqual(bytes) &&
            board.Width == 400 && board.Height == 600 && board.BillboardAnchor == anchor);
    }
}
