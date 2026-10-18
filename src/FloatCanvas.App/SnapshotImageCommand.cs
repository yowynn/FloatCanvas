using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class SnapshotImageCommand
{
    internal static CaptureRasterResult Render(SceneDocument committedScene, IEnumerable<AssetResource> resources,
        CaptureSelection selection, CapturedDesktop? desktop, IReadOnlyList<RectD> displays, TimeSpan animationTime = default)
    {
        var scene = committedScene.Clone();
        // The new Image receives canvas opacity in the ordinary render pass, exactly once.
        scene.LayerOpacity = 1;
        var assets = new AssetLibrary();
        foreach (var resource in resources) assets.Add(resource.Data.ToArray(), resource.MediaType);
        bool background = selection.Source != CaptureSource.CanvasOnly;
        if (background && desktop is null) throw new InvalidOperationException("Desktop pixels are required for this capture source.");
        if (desktop is not null)
        {
            string id = assets.Add(desktop.PngBytes);
            scene.Background = new CanvasElement { Kind = ElementKind.Image, AssetId = id,
                Width = desktop.Bounds.Width, Height = desktop.Bounds.Height,
                Transform = Affine2D.Translation(desktop.Bounds.Left, desktop.Bounds.Top), Locked = true };
        }
        if (selection.Source == CaptureSource.Background) { scene.Elements.Clear(); scene.Groups.Clear(); }
        using var renderer = new SceneRenderer { AnimationTime = animationTime };
        // DrawBackground is disabled for CanvasOnly, but this complete backdrop remains
        // available to the dynamic mosaic's own sampling pass.
        return renderer.ExportSelectionPng(scene, assets, selection, background, displays);
    }

    internal static CanvasElement Insert(EditorSession session, CaptureRasterResult raster)
    {
        if (!raster.HasVisiblePixels) throw new InvalidOperationException(L.Get("Snapshot.Empty"));
        string id = session.Assets.Add(raster.PngBytes);
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = id,
            Width = raster.Bounds.Width, Height = raster.Bounds.Height,
            Transform = Affine2D.Translation(raster.Bounds.Left, raster.Bounds.Top), Style = new() { StrokeWidth = 0 } };
        session.Add(image, L.Get("History.Snapshot"));
        return image;
    }
}
