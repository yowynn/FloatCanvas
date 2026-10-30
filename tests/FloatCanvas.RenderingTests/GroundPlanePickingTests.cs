using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class GroundPlanePickingTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var scene = Scene();
        var board = scene.Elements.Single();
        check("opaque canvas ground prevents picking the submerged billboard", renderer.PickDepth(scene, assets, new(80, 100), 0) is null);
        check("above-ground billboard remains pickable over opaque ground", renderer.PickDepth(scene, assets, new(80, 35), 0)?.Id == board.Id);
        check("ground at exact anchor depth remains below the billboard", renderer.PickDepth(scene, assets, new(80, 50), 0)?.Id == board.Id);

        var missedFront = board.Clone(true);
        var mapping = CanvasMapping.Create(scene);
        mapping.TryToPlane(new(500, 100), out var missedAnchor);
        missedFront.BillboardAnchor = missedAnchor;
        scene.Elements.Add(missedFront);
        check("missing a nearer object still stops picking at opaque ground before farther ink", renderer.PickDepth(scene, assets, new(80, 100), 0) is null);
        scene.Elements.Remove(missedFront);

        scene.Canvas.BackgroundColor = 0x8000FF00;
        check("translucent canvas ground leaves a submerged billboard eligible", renderer.PickDepth(scene, assets, new(80, 100), 0)?.Id == board.Id);
        scene.LayerOpacity = .25;
        scene.Canvas.BackgroundColor = 0xFF00FF00;
        check("global opacity preserves ground occlusion inside the composed canvas", renderer.PickDepth(scene, assets, new(80, 100), 0) is null);
        scene.LayerOpacity = 0;
        check("zero global opacity has no pickable canvas ink", renderer.PickDepth(scene, assets, new(80, 35), 0) is null);
        scene.LayerOpacity = 1;
        scene.Canvas.DepthPriorityOcclusion = false;
        check("disabled depth priority retains ordinary billboard picking over the canvas color", renderer.PickDepth(scene, assets, new(80, 100), 0)?.Id == board.Id);
        scene.Canvas.DepthPriorityOcclusion = true;

        scene.Background = new() { Kind = ElementKind.Image, Visible = true };
        check("visible frozen photograph keeps canvas color behind the objects for picking", renderer.PickDepth(scene, assets, new(80, 100), 0)?.Id == board.Id);
        scene.Background.Visible = false;
        check("hidden photograph restores canvas ground as a picking occluder", renderer.PickDepth(scene, assets, new(80, 100), 0) is null);
        scene.Background = null;
        Grid(renderer, assets, check);
        Locked(renderer, assets, check);
        CancellationAndDisposal(scene, assets, check);
    }

    private static void Grid(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene();
        scene.Canvas.BackgroundColor = 0;
        scene.Canvas.Grid = new() { Visible = true, Color = 0xFF00FF00, Width = 4 };
        var board = scene.Elements.Single();
        board.Visible = false;
        using var surface = SKSurface.Create(new SKImageInfo(160, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
        renderer.Draw(surface.Canvas, scene, assets);
        using var image = surface.Snapshot();
        using var pixels = SKBitmap.FromImage(image);
        PointD Find(bool ink) => (from y in Enumerable.Range(80, 26)
            from x in Enumerable.Range(45, 70)
            let alpha = pixels.GetPixel(x, y).Alpha
            where ink ? alpha == 255 : alpha == 0
            select new PointD(x + .5, y + .5)).First();
        var line = Find(true); var gap = Find(false);
        board.Visible = true;
        check("opaque grid stroke blocks the submerged billboard at its actual ink", renderer.PickDepth(scene, assets, line, 0) is null);
        check("transparent grid cell leaves the submerged billboard pickable", renderer.PickDepth(scene, assets, gap, 0)?.Id == board.Id);
        scene.Canvas.Grid.Color = 0x8000FF00;
        check("translucent grid stroke leaves submerged objects eligible", renderer.PickDepth(scene, assets, line, 0)?.Id == board.Id);
        scene.Canvas.BackgroundColor = 0xFE00FF00;
        check("ground color and grid combine their alpha before rejecting submerged picks", renderer.PickDepth(scene, assets, line, 0) is null);
        scene.Canvas.Grid.Color = 0xFF00FF00;
        scene.Canvas.BackgroundColor = 0;
        scene.Background = new() { Kind = ElementKind.Image, Visible = true };
        check("grid remains an actual plane occluder above the frozen photograph", renderer.PickDepth(scene, assets, line, 0) is null);
    }

    private static void Locked(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene();
        var behind = scene.Elements.Single();
        var locked = behind.Clone(true); locked.Locked = true;
        scene.Elements.Add(locked);
        check("opaque locked foreground object still blocks picking the unlocked object below", renderer.PickDepth(scene, assets, new(80, 35), 0) is null);
        locked.Style.Opacity = .5;
        check("translucent locked foreground leaves the unlocked object eligible", renderer.PickDepth(scene, assets, new(80, 35), 0)?.Id == behind.Id);
    }

    private static void CancellationAndDisposal(SceneDocument scene, AssetLibrary assets, Action<string, bool> check)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var renderer = new SceneRenderer();
        bool cancelled = false;
        try { renderer.PickDepth(scene, assets, new(80, 100), 0, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check("depth ground picking observes cancellation before raster sampling", cancelled);
        check("cancelled ground picking releases frame state for the next query", renderer.PickDepth(scene, assets, new(80, 35), 0)?.Id == scene.Elements[0].Id);
        renderer.Dispose();
        bool disposed = false;
        try { renderer.PickDepth(scene, assets, new(80, 100), 0); }
        catch (ObjectDisposedException) { disposed = true; }
        check("disposed renderer rejects depth ground picking", disposed);
    }

    private static SceneDocument Scene()
    {
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, -.003, 1);
        var scene = new SceneDocument { Viewport = new(0, 0, 160, 140), Canvas = new()
        {
            BackgroundColor = 0xFF00FF00, DepthPriorityOcclusion = true,
            Projection = new() { Enabled = true, CellSize = 20, Matrix = matrix,
                Quad = [new(0, 0), new(160, 0), new(250, 187.5), new(0, 187.5)] }
        } };
        var mapping = CanvasMapping.Create(scene);
        mapping.TryToPlane(new(80, 50), out var anchor);
        mapping.TryGetDepthScale(anchor, out double scale);
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, IsBillboard = true, BillboardAnchor = anchor,
            BillboardHeight = -20 / scale, Width = 100 / scale, Height = 90 / scale,
            Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } });
        return scene;
    }
}
