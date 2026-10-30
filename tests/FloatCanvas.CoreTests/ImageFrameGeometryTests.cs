using System.Text.Json;
using FloatCanvas.Core;

internal static class ImageFrameGeometryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var projection = new Projective2D(1, .18, 80, .08, 1, 40, .0008, .0004, 1);
        var plane = new RectD(120, 100, 500, 380);
        var contour = Corners(plane).Select(projection.TransformPoint).ToArray();
        var raster = projection.TransformBounds(plane);
        raster = new(Math.Floor(raster.Left), Math.Floor(raster.Top),
            Math.Ceiling(raster.Right) - Math.Floor(raster.Left), Math.Ceiling(raster.Bottom) - Math.Floor(raster.Top));
        var image = new CanvasElement
        {
            Kind = ElementKind.Image, Width = raster.Width, Height = raster.Height,
            ProjectivePlacement = projection.Inverse().Compose(Projective2D.FromAffine(Affine2D.Translation(raster.X, raster.Y))),
            ImageFrame = contour.Select(p => new PointD((p.X - raster.Left) / raster.Width, (p.Y - raster.Top) / raster.Height)).ToList()
        };
        check(Near(ElementGeometry.Bounds(image), plane), "capture frame removes inverse-projected transparent bitmap corners");
        check(ElementGeometry.LocalBounds(image) == new RectD(0, 0, raster.Width, raster.Height), "capture frame preserves full raster allocation geometry");
        var imported = image.Clone(); imported.ImageFrame = null;
        check(ElementGeometry.Bounds(imported).Width > plane.Width + 10, "ordinary imported images retain source bitmap padding");
        foreach (var kind in Enum.GetValues<ImageEffectKind>())
        {
            var effected = image.Clone();
            effected.Effects = [new() { Kind = kind, Bounds = new(0, 0, 1, 1) }];
            check(Near(ElementGeometry.Bounds(effected), ElementGeometry.Bounds(imported)) && effected.ImageFrame!.SequenceEqual(image.ImageFrame!),
                kind + " image effects preserve capture metadata while covering the complete editable raster");
        }

        var round = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 100, ImageBorderWidth = 10,
            ImageFrame = [new(0, 0), new(0, 0), new(0, 0)], ProjectivePlacement = new(1, 0, 0, 0, 1, 0, .01, 0, 1) };
        var roundBounds = ElementGeometry.Bounds(round);
        double vertical = 10 / Math.Sqrt(.99);
        check(Near(roundBounds, new(-10 / .9, -vertical, 10 / 1.1 + 10 / .9, 2 * vertical)),
            "projective round border envelope uses circle extrema rather than square inflation");
        round.Style.Stroke = 0;
        check(ElementGeometry.Bounds(round) == default, "transparent capture border does not expand its geometric frame");

        var affine = new CanvasElement { Kind = ElementKind.Image, Width = 80, Height = 60, ImageBorderWidth = 7,
            ImageFrame = [new(.1, .2), new(.9, .3), new(.4, .8)],
            Transform = Affine2D.Translation(200, 100).Compose(Affine2D.Rotation(31)).Compose(Affine2D.Scale(2, .7)) };
        var mapped = affine.ImageFrame.Select(p => affine.Transform.TransformPoint(new(p.X * affine.Width, p.Y * affine.Height))).ToArray();
        double rx = 7 * Math.Sqrt(affine.Transform.M11 * affine.Transform.M11 + affine.Transform.M12 * affine.Transform.M12);
        double ry = 7 * Math.Sqrt(affine.Transform.M21 * affine.Transform.M21 + affine.Transform.M22 * affine.Transform.M22);
        var expected = new RectD(mapped.Min(p => p.X) - rx, mapped.Min(p => p.Y) - ry,
            mapped.Max(p => p.X) - mapped.Min(p => p.X) + 2 * rx, mapped.Max(p => p.Y) - mapped.Min(p => p.Y) + 2 * ry);
        check(Near(ElementGeometry.Bounds(affine), expected), "nonuniform rotated capture border has its exact elliptical envelope");

        var clone = image.Clone(); clone.ImageFrame![0] = new(.5, .5);
        check(clone.ImageFrame[0] != image.ImageFrame![0], "capture frame clones own their contour points");
        var restored = JsonSerializer.Deserialize<CanvasElement>(JsonSerializer.Serialize(image))!;
        check(restored.ImageFrame!.SequenceEqual(image.ImageFrame!) && Near(ElementGeometry.Bounds(restored), plane), "capture frame and exact bounds survive JSON persistence");

        var cropped = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 100,
            ImageFrame = [new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9)] };
        ElementGeometry.CropImageFrame(cropped, new(20, 30, 50, 40));
        cropped.Width = 50; cropped.Height = 40; cropped.Transform = Affine2D.Translation(20, 30);
        check(Near(ElementGeometry.Bounds(cropped), new(20, 30, 50, 40)), "cropping clips and renormalizes the capture frame before resizing the body");
        var tangent = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 100,
            ImageFrame = [new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9)] };
        ElementGeometry.CropImageFrame(tangent, new(90, 30, 10, 40));
        tangent.Width = 10; tangent.Height = 40; tangent.Transform = Affine2D.Translation(90, 30);
        check(tangent.ImageFrame is null && Near(ElementGeometry.Bounds(tangent), new(90, 30, 10, 40)),
            "crop tangent to the capture contour retains an editable nonzero bitmap frame");
        var crossed = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 100,
            ImageFrame = [new(.1, .1), new(.9, .9), new(.9, .1), new(.1, .9)] };
        ElementGeometry.CropImageFrame(crossed, new(20, 20, 60, 60));
        check(crossed.ImageFrame is { Count: >= 3 }, "self-intersecting capture crop retains its nonzero extent despite zero signed area");
        cropped.IsBillboard = true;
        check(ElementGeometry.Bounds(cropped) == BillboardGeometry.LocalPlacement(cropped, cropped.BillboardAnchor, 1)
            .TransformBounds(ElementGeometry.StrokedLocalBounds(cropped)), "billboard bounds retain the ordinary bitmap frame");
    }

    private static PointD[] Corners(RectD bounds) => [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
    private static bool Near(RectD actual, RectD expected) => Math.Abs(actual.X - expected.X) < 1e-8 && Math.Abs(actual.Y - expected.Y) < 1e-8 &&
        Math.Abs(actual.Width - expected.Width) < 1e-8 && Math.Abs(actual.Height - expected.Height) < 1e-8;
}
