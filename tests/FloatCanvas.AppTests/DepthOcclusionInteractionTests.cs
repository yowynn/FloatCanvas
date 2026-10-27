using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class DepthOcclusionInteractionTests
{
    internal static void Run(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 800, 600) };
        scene.Canvas.DepthPriorityOcclusion = true;
        scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1);
        scene.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 600), new(0, 600)];
        var mapping = CanvasMapping.Create(scene);
        CanvasElement Billboard(double y, uint fill)
        {
            double depth = 1 + .001 * y;
            var anchor = new PointD(150 * depth, y);
            mapping.TryGetDepthScale(anchor, out double scale);
            return new() { Kind = ElementKind.Rectangle, IsBillboard = true, Width = 160, Height = 160,
                BillboardAnchor = anchor, BillboardHeight = (mapping.ToScreen(anchor).Y - 150) / scale,
                Style = new() { Fill = fill, StrokeWidth = 0 } };
        }
        var near = Billboard(100, 0xFFFF0000); var far = Billboard(400, 0xFF0000FF);
        scene.Elements = [near, far];
        var assets = new AssetLibrary(); using var renderer = new SceneRenderer();
        var point = new PointD(165, 145);
        check("depth picking chooses near billboard over later far layer", renderer.PickDepth(scene, assets, point) == near);
        far.BillboardAnchor = near.BillboardAnchor; far.BillboardHeight = near.BillboardHeight;
        check("exact equal-depth picking follows original layer order", renderer.PickDepth(scene, assets, point) == far);
        far.BillboardAnchor += new PointD(0, 1e-7);
        check("close but distinct depth does not become a layer-order tie", renderer.PickDepth(scene, assets, point) == near);
        far = Billboard(400, 0xFF0000FF); scene.Elements = [far];
        scene.Canvas.BackgroundColor = 0xFFFFFFFF;
        check("opaque canvas base leaves the visible farther billboard pickable", renderer.PickDepth(scene, assets, point) == far);
        scene.Canvas.BackgroundColor = 0x80FFFFFF;
        check("translucent plane color retains picking through visible billboard", renderer.PickDepth(scene, assets, point) == far);
        scene.Canvas.BackgroundColor = 0;
        scene.Elements = [far, near]; near.Locked = true;
        check("opaque locked foreground prevents picking an invisible rear object", renderer.PickDepth(scene, assets, point) is null);
        near.Visible = false;
        check("hidden foreground does not block picking", renderer.PickDepth(scene, assets, point) == far);
        near.Visible = true; near.Locked = false; near.Style.Opacity = 0;
        check("zero opacity foreground does not block picking", renderer.PickDepth(scene, assets, point) == far);
        near.Style.Opacity = 1;

        using (var pixels = SKSurface.Create(new SKImageInfo(2, 1)))
        {
            pixels.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Green };
            pixels.Canvas.DrawRect(1, 0, 1, 1, paint);
            using var image = pixels.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            near.Kind = ElementKind.Image; near.AssetId = assets.Add(data.ToArray());
        }
        scene.Elements = [far, near];
        check("PNG alpha hole exposes the farther billboard to picking", renderer.PickDepth(scene, assets, new(110, 150)) == far);
        check("opaque PNG pixels pick the nearer image", renderer.PickDepth(scene, assets, new(180, 150)) == near);

        // A one-pixel GIF alternates a transparent frame and an opaque red frame, 100ms each.
        near.AssetId = assets.Add(Convert.FromBase64String("R0lGODlhAQABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAAAQABAAADAggJACH5BAkKAAAALAAAAAABAAEAAAMCGAkAOw=="), "image/gif");
        renderer.AnimationTime = TimeSpan.Zero;
        check("animated image first-frame transparency permits rear picking", renderer.PickDepth(scene, assets, point) == far);
        renderer.AnimationTime = TimeSpan.FromMilliseconds(150);
        check("animated image picking follows the currently drawn opaque frame", renderer.PickDepth(scene, assets, point) == near);

        near.Kind = ElementKind.Rectangle; scene.Elements = [near, far];
        var session = new EditorSession(scene, assets); var editor = new EditorController(session) { Tool = EditorTool.Select };
        void Click(PointD p)
        {
            editor.Pointer(new(OverlayPointerKind.Down, new(p.X, p.Y), OverlayMouseButton.Left, OverlayModifiers.None));
            editor.Pointer(new(OverlayPointerKind.Up, new(p.X, p.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        }
        Click(point);
        check("controller selects spatial foreground instead of last layer", session.Selection.SetEquals([near.Id]));
        session.SetSelection([far.Id]); Click(point);
        check("previous rear selection body does not intercept foreground click", session.Selection.SetEquals([near.Id]));
        scene.Canvas.DepthPriorityOcclusion = false; session.SetSelection([]); Click(point);
        check("disabled option preserves original layer picking", session.Selection.SetEquals([far.Id]));
        scene.Canvas.DepthPriorityOcclusion = true; scene.Canvas.Projection.Enabled = false;
        check("perspective off suspends depth rules without clearing the saved choice", !CanvasDepthOrder.IsEnabled(scene) && scene.Canvas.DepthPriorityOcclusion);
        scene.Canvas.Projection.Enabled = true; near.Kind = ElementKind.Image;
        editor.AnimationTime = TimeSpan.FromMilliseconds(150); session.SetSelection([]); Click(point);
        check("controller forwards displayed animation time into depth picking", session.Selection.SetEquals([near.Id]));
        session.SetSelection([far.Id]);
        var rear = session.Document.Elements.Single(element => element.Id == far.Id);
        var anchorBefore = rear.BillboardAnchor;
        editor.Pointer(new(OverlayPointerKind.Down, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Move, new(point.X + 20, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Up, new(point.X + 20, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        check("dragging an overlapping selected frame moves the rear selection rather than the foreground",
            session.Selection.SetEquals([far.Id]) && rear.BillboardAnchor != anchorBefore);
    }
}
