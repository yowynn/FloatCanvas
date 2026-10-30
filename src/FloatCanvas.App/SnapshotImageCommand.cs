using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class SnapshotImageCommand
{
    internal static CaptureRasterResult Render(SceneDocument committedScene, IEnumerable<AssetResource> resources,
        CaptureSelection selection, CapturedDesktop? desktop, IReadOnlyList<RectD> displays, TimeSpan animationTime = default,
        PointD screenOrigin = default)
    {
        if (!double.IsFinite(selection.BorderWidth) || selection.BorderWidth < 0)
            throw new ArgumentException(L.Get("Capture.EmptyRegion"), nameof(selection));
        selection = selection with { Points = selection.Points.ToArray() };
        var scene = committedScene.Clone();
        // The new Image receives canvas opacity in the ordinary render pass, exactly once.
        scene.LayerOpacity = 1;
        var assets = new AssetLibrary();
        foreach (var resource in resources) assets.Add(resource.Data.ToArray(), resource.MediaType);
        bool background = selection.Source != CaptureSource.CanvasOnly;
        if (background && desktop is null) throw new InvalidOperationException("Desktop pixels are required for this capture source.");
        using var backdrop = desktop is null ? null : DesktopBackdrop.FromEncoded(
            new(desktop.Bounds.Left - screenOrigin.X, desktop.Bounds.Top - screenOrigin.Y, desktop.Bounds.Width, desktop.Bounds.Height), desktop.PngBytes);
        // A snapshot samples the fresh desktop, never the previously frozen capture.
        scene.Background = null;
        if (selection.Source == CaptureSource.Background)
        {
            scene.Elements.Clear(); scene.Groups.Clear(); scene.Background = null;
            scene.Canvas.BackgroundColor = 0; scene.Canvas.Grid.Visible = false;
        }
        using var renderer = new SceneRenderer { AnimationTime = animationTime, ScreenBackdrop = backdrop };
        // DrawBackground is disabled for CanvasOnly, but this complete backdrop remains
        // available to the dynamic mosaic's own sampling pass.
        // Keep the body unframed so later layer edits can change the border without
        // resampling pixels or painting over a previous baked border.
        var raster = renderer.ExportSelectionPng(scene, assets, selection with { BorderWidth = 0 }, background, displays);
        // A perspective screenshot has transparent corners in its screen-aligned PNG.
        // Retain its actual contour separately so the plane edit frame excludes those corners,
        // while the original sampled pixels and their placement remain unchanged.
        var contour = selection.Points.Count > 0 ? selection.Points : EditorController.Corners(selection.Bounds);
        return raster with
        {
            ImageBorderWidth = selection.BorderWidth, ImageBorderColor = selection.BorderColor,
            ImageFrame = selection.BillboardDepthLine is null && CanvasMapping.Create(scene).PlaneToScreen != Projective2D.Identity
                ? contour.Select(point => new PointD(Math.Clamp((point.X - raster.Bounds.X) / raster.Bounds.Width, 0, 1),
                    Math.Clamp((point.Y - raster.Bounds.Y) / raster.Bounds.Height, 0, 1))).ToArray() : null
        };
    }

    internal static CanvasElement Insert(EditorSession session, CaptureRasterResult raster, Projective2D? capturedPlaneToScreen = null,
        BillboardDepthLine? billboardDepth = null)
    {
        var mapping = CanvasMapping.Create(session.Document);
        var image = billboardDepth is { } depth
            ? CreateBillboardElement(session.Assets, raster, mapping, depth)
            : CreateElement(session.Assets, raster, capturedPlaneToScreen ?? mapping.PlaneToScreen);
        if (!mapping.IsElementSafe(image)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        session.Add(image, L.Get("History.Snapshot"));
        return image;
    }

    internal static CanvasElement CreateBillboardElement(AssetLibrary assets, CaptureRasterResult raster,
        CanvasMapping mapping, BillboardDepthLine depth)
    {
        if (!raster.HasVisiblePixels) throw new InvalidOperationException(L.Get("Snapshot.Empty"));
        var image = new CanvasElement { Kind = ElementKind.Image,
            Width = raster.Bounds.Width / depth.Scale, Height = raster.Bounds.Height / depth.Scale,
            Style = new() { StrokeWidth = 0 } };
        ConfigureBorder(image, raster, depth.Scale);
        var placement = Projective2D.FromAffine(Affine2D.Translation(raster.Bounds.X, raster.Bounds.Y)
            .Compose(Affine2D.Scale(depth.Scale, depth.Scale)));
        if (!BillboardGeometry.TryPlaceFromScreen(image, mapping, depth, placement) || !mapping.IsElementSafe(image))
            throw new InvalidOperationException(L.Get("Perspective.SnapshotOutsidePlane"));
        image.AssetId = assets.Add(raster.PngBytes);
        return image;
    }

    internal static CanvasElement CreateElement(AssetLibrary assets, CaptureRasterResult raster, Projective2D planeToScreen)
    {
        if (!raster.HasVisiblePixels) throw new InvalidOperationException(L.Get("Snapshot.Empty"));
        var safety = new CanvasProjectionSafety(planeToScreen);
        if (!EditorController.Corners(raster.Bounds).All(safety.IsScreenSafe))
            throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        var screenPlacement = Affine2D.Translation(raster.Bounds.Left, raster.Bounds.Top);
        Projective2D? placement = null;
        if (planeToScreen != Projective2D.Identity)
        {
            if (!planeToScreen.TryInverse(out var inverse)) throw new InvalidOperationException(L.Get("Perspective.SnapshotOutsidePlane"));
            var candidate = inverse.Compose(Projective2D.FromAffine(screenPlacement));
            var corners = EditorController.Corners(new(0, 0, raster.Bounds.Width, raster.Bounds.Height));
            var denominators = corners.Select(candidate.Denominator).ToArray();
            if (denominators.Any(d => !double.IsFinite(d) || Math.Abs(d) < 1e-9) ||
                denominators.Any(d => Math.Sign(d) != Math.Sign(denominators[0])) ||
                corners.Any(p => !candidate.TryTransformPoint(p, out _)))
                throw new InvalidOperationException(L.Get("Perspective.SnapshotOutsidePlane"));
            placement = candidate;
        }
        string id = assets.Add(raster.PngBytes);
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = id,
            Width = raster.Bounds.Width, Height = raster.Bounds.Height,
            Transform = placement is null ? screenPlacement : Affine2D.Identity,
            ProjectivePlacement = placement, ImageFrame = raster.ImageFrame?.ToList(), Style = new() { StrokeWidth = 0 } };
        ConfigureBorder(image, raster);
        return image;
    }

    private static void ConfigureBorder(CanvasElement image, CaptureRasterResult raster, double scale = 1)
    {
        image.ImageBorderWidth = raster.ImageBorderWidth / scale;
        if (raster.ImageBorderColor is { } color) image.Style.Stroke = color;
    }
}
