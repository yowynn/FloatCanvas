using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using System.Text.Json;

internal static class ImageOutsideStrokeTests
{
    internal static void Run(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 120, 100) };
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 120, Height = 100,
            Style = new() { Fill = 0xFF0088FF, StrokeWidth = 0 } });
        var selection = new CaptureSelection(CaptureShape.Rectangle, CaptureSource.CanvasOnly,
            new(30, 25, 40, 30), [], 6, 0xFFFF0000);
        using var renderer = new SceneRenderer();
        var raster = renderer.ExportSelectionPng(scene, new AssetLibrary(), selection, false);
        check("capture border lies completely outside the selected body and expands by its full width",
            raster.Bounds == new RectD(24, 19, 52, 42));
        using var decoded = SKBitmap.Decode(raster.PngBytes);
        check("an outside capture border does not recolor pixels inside its selection",
            decoded.GetPixel((int)(31-raster.Bounds.X), (int)(40-raster.Bounds.Y)) == new SKColor(0xFF0088FF));
        check("capture border paints the full outside band",
            decoded.GetPixel((int)(26-raster.Bounds.X), (int)(40-raster.Bounds.Y)) == SKColors.Red);
        EditableSnapshot(scene, selection, check);
        Images(check);
        Marquee(check);
        Billboard(check);
        EmptyBodyWithBorder(check);
    }

    private static void EditableSnapshot(SceneDocument source, CaptureSelection selection, Action<string, bool> check)
    {
        foreach (var shape in new[] { CaptureShape.Rectangle, CaptureShape.Ellipse, CaptureShape.PolygonLasso })
        {
            var capture = selection with { Shape = shape, Points = shape == CaptureShape.PolygonLasso
                ? [new(30, 25), new(70, 25), new(65, 55), new(30, 50)] : [] };
            var raster = SnapshotImageCommand.Render(source, [], capture, null, [source.Viewport]);
            var target = new EditorSession(new SceneDocument { Viewport = source.Viewport });
            var image = SnapshotImageCommand.Insert(target, raster);
            check(shape + " screenshot stores editable outside-border metadata without baking it into the body size",
                raster.Bounds == capture.Bounds && image.Width == 40 && image.Height == 30 &&
                image.ImageBorderWidth == 6 && image.Style.Stroke == 0xFFFF0000 && image.Points.Count == 0);
            using var body = SKBitmap.Decode(raster.PngBytes);
            check(shape + " screenshot body pixels retain their source colors", body.GetPixel(20, 15) == new SKColor(0xFF0088FF));
            var controller = new EditorController(target) { Tool = EditorTool.Select };
            var corners = controller.SelectionScreenCorners;
            check(shape + " screenshot selection surrounds the full outside border", corners.Count == 4 &&
                corners.Min(p => p.X) == 24 && corners.Max(p => p.X) == 76 &&
                corners.Min(p => p.Y) == 19 && corners.Max(p => p.Y) == 61);
            var before = image.Clone();
            target.Execute("edit image border", document => document.Elements[0].ImageBorderWidth = 10);
            check(shape + " border editing changes visual bounds without changing body dimensions or asset bytes",
                target.Document.Elements[0] is { Width: 40, Height: 30, ImageBorderWidth: 10 } &&
                target.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
            check(shape + " border edits undo and redo with the complete original image state", target.Undo() &&
                target.Document.Elements[0].ImageBorderWidth == before.ImageBorderWidth && target.Redo() &&
                target.Document.Elements[0].ImageBorderWidth == 10);
            var loaded = JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(target.Document))!;
            SceneValidation.Validate(loaded);
            check(shape + " image border survives persistence without separate contour metadata", loaded.Elements[0].ImageBorderWidth == 10 &&
                loaded.Elements[0].Points.Count == 0 && loaded.Elements[0].AssetId == image.AssetId);
        }
    }

    private static void Images(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        using var bitmap = new SKBitmap(40, 30); bitmap.Erase(SKColors.Blue);
        using var bodyImage = SKImage.FromBitmap(bitmap); using var encoded = bodyImage.Encode(SKEncodedImageFormat.Png, 100);
        string asset = assets.Add(encoded.ToArray());
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 40, Height = 30,
            Transform = Affine2D.Translation(30, 25), Style = new() { Stroke = 0xFFFF0000, StrokeWidth = 17 } };
        var scene = new SceneDocument { Viewport = new(0, 0, 120, 100), Elements = [image] };
        using (var oldPixels = Render(scene, assets))
            check("legacy image style width does not introduce an unexpected border", oldPixels.GetPixel(29, 40).Alpha == 0 && oldPixels.GetPixel(31, 40) == SKColors.Blue);
        image.ImageBorderWidth = 6;
        using (var pixels = Render(scene, assets))
        {
            check("imported image gets a full outside frame without covering its body", pixels.GetPixel(26, 40) == SKColors.Red && pixels.GetPixel(31, 40) == SKColors.Blue);
            check("outside frame stays within the expanded selection bounds", pixels.GetPixel(23, 40).Alpha == 0 &&
                ElementGeometry.Bounds(image) == new RectD(24, 19, 52, 42) && ElementGeometry.Bounds(image, false) == new RectD(30, 25, 40, 30));
            check("the actual outer stroke is selectable", ElementGeometry.HitTest(image, new(26, 40), 0) && !ElementGeometry.HitTest(image, new(22, 40), 0));
        }
        image.Style.Opacity = .5; image.Style.Stroke = 0x80FF0000;
        using (var pixels = Render(scene, assets))
            check("image and translucent outside border receive element opacity only once", pixels.GetPixel(26, 40).Alpha is >= 63 and <= 65 && pixels.GetPixel(31, 40).Alpha is >= 127 and <= 128);

        image.Style.Opacity = 1; image.Style.Stroke = 0xFFFF0000;
        image.Transform = Affine2D.Translation(50, 35).Compose(Affine2D.Rotation(24));
        using (var pixels = Render(scene, assets))
        {
            var bounds = ElementGeometry.Bounds(image);
            check("rotated image bounds include every rendered stroke pixel", AllPixelsInside(pixels, bounds));
        }
        image.Transform = Affine2D.Translation(30, 25); image.ProjectivePlacement = new(1, .05, 0, .03, 1, 0, .001, 0, 1);
        var session = new EditorSession(scene); session.Selection.Add(image.Id);
        var frame = new EditorController(session) { Tool = EditorTool.Select }.SelectionScreenCorners;
        check("projectively placed image keeps its expanded frame on the canvas plane", frame.Count == 4 &&
            frame.Min(point => point.X) <= ElementGeometry.Bounds(image).Left && frame.Max(point => point.X) >= ElementGeometry.Bounds(image).Right);

        var outlined = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 40, Height = 30, ImageBorderWidth = 6,
            Points = new([new(-10, 0), new(40, 0), new(40, 30), new(-10, 30)]) };
        check("legacy image points cannot alter alpha-border bounds or arithmetic body dimensions",
            ElementGeometry.StrokedLocalBounds(outlined) == new RectD(-6, -6, 52, 42) &&
            ElementGeometry.Bounds(outlined, false) == new RectD(0, 0, 40, 30));
        outlined.ImageBorderWidth = 0;
        check("legacy image points cannot expand an unframed image's selection", ElementGeometry.StrokedLocalBounds(outlined) == new RectD(0, 0, 40, 30));
        outlined.ImageBorderWidth = 6;
        var replacement = new EditorSession(new SceneDocument { Viewport = scene.Viewport, Elements = [outlined] }, assets);
        replacement.Selection.Add(outlined.Id);
        using var replacementBitmap = new SKBitmap(40, 30); replacementBitmap.Erase(SKColors.Green);
        using var replacementImage = SKImage.FromBitmap(replacementBitmap);
        using var replacementData = replacementImage.Encode(SKEncodedImageFormat.Png, 100);
        ImageImportCommand.Insert(replacement, [new(replacementData.ToArray(), 40, 30)], scene.Viewport, replace: true);
        string replacementAsset = replacement.Document.Elements.Single().AssetId!;
        check("replacing an image retains its editable border and body size while adopting the new pixels",
            replacement.Document.Elements.Single() is { ImageBorderWidth: 6, Width: 40, Height: 30 } && replacementAsset != asset);
        check("image replacement can undo and redo its source exactly", replacement.Undo() &&
            replacement.Document.Elements.Single().AssetId == asset && replacement.Redo() && replacement.Document.Elements.Single().AssetId == replacementAsset);
        Crop(assets, asset, check);
    }

    private static void Crop(AssetLibrary assets, string asset, Action<string, bool> check)
    {
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 40, Height = 30,
            ImageBorderWidth = 6, Transform = Affine2D.Translation(30, 25), Style = new() { Stroke = 0xFFFF0000 } };
        var scene = new SceneDocument { Viewport = new(0, 0, 120, 100), Elements = [image] };
        var session = new EditorSession(scene, assets); session.SetSelection([image.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Crop };
        editor.Pointer(new(OverlayPointerKind.Down, new(40, 30), OverlayMouseButton.Left, OverlayModifiers.Control));
        editor.Pointer(new(OverlayPointerKind.Move, new(60, 45), OverlayMouseButton.Left, OverlayModifiers.Control));
        editor.Pointer(new(OverlayPointerKind.Up, new(60, 45), OverlayMouseButton.Left, OverlayModifiers.Control));
        var cropped = session.Document.Elements.Single();
        check("image crop retains the editable alpha border while changing only the visible source body",
            cropped is { Width: 20, Height: 15, ImageBorderWidth: 6, Points.Count: 0 } && cropped.ImageCrop is not null &&
            cropped.AssetId == asset && cropped.Transform == Affine2D.Translation(40, 30));
        using (var pixels = Render(session.Document, assets))
            check("the cropped body's new edge receives its full outside border",
                pixels.GetPixel(36, 37) == SKColors.Red && pixels.GetPixel(41, 31) == SKColors.Blue);
        check("image crop undo and redo retain the source and alpha-border width", session.Undo() &&
            session.Document.Elements.Single() is { Width: 40, Height: 30, ImageBorderWidth: 6, ImageCrop: null } && session.Redo() &&
            session.Document.Elements.Single() is { Width: 20, Height: 15, ImageBorderWidth: 6, ImageCrop: not null });
    }

    private static SKBitmap Render(SceneDocument scene, AssetLibrary assets)
    {
        using var renderer = new SceneRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(120, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, scene, assets, includeBackground: false);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static void Marquee(Action<string, bool> check)
    {
        using var bitmap = new SKBitmap(80, 60); bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(new SKRect(10, 5, 70, 55), paint);
            paint.BlendMode = SKBlendMode.Clear;
            canvas.DrawRect(new SKRect(20, 15, 60, 45), paint);
        }
        using var pixels = SKImage.FromBitmap(bitmap); using var encoded = pixels.Encode(SKEncodedImageFormat.Png, 100);
        var assets = new AssetLibrary(); string id = assets.Add(encoded.ToArray());
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 80, Height = 60,
            Transform = Affine2D.Translation(20, 20), ImageBorderWidth = 3, Style = new() { Stroke = 0xFFFF0000 } };
        var scene = new SceneDocument { Viewport = new(0, 0, 120, 100), Elements = [image] };
        var session = new EditorSession(scene, assets);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        Drag(new(14, 36), new(25, 44));
        check("image marquee does not select transparent padding inside its conservative border envelope", session.Selection.Count == 0);
        Drag(new(55, 48), new(65, 52));
        check("image marquee does not select a transparent hole beyond the alpha-border width", session.Selection.Count == 0);

        // Use an opaque body touching its left texture edge so its outside border
        // is entirely beyond the ordinary image body contour.
        bitmap.Erase(SKColors.Blue);
        using var solid = SKImage.FromBitmap(bitmap); using var solidData = solid.Encode(SKEncodedImageFormat.Png, 100);
        image.AssetId = assets.Add(solidData.ToArray());
        Drag(new(6, 36), new(18, 44));
        check("image marquee selects real outside-border pixels without touching the image body", session.Selection.SetEquals([image.Id]));

        void Drag(OverlayPoint first, OverlayPoint last)
        {
            editor.Pointer(new(OverlayPointerKind.Down, first, OverlayMouseButton.Left, OverlayModifiers.None));
            editor.Pointer(new(OverlayPointerKind.Move, last, OverlayMouseButton.Left, OverlayModifiers.None));
            if (editor.Marquee is null) throw new InvalidOperationException("Image alpha-border fixture must enter a marquee gesture.");
            editor.Pointer(new(OverlayPointerKind.Up, last, OverlayMouseButton.Left, OverlayModifiers.None));
        }
    }

    private static void Billboard(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 120, 100) };
        scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Projection.CellSize = 15;
        scene.Canvas.Projection.Quad = [new(0, 0), new(60, 0), new(60, 45), new(0, 45)];
        scene.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Scale(.5, .5));
        var mapping = CanvasMapping.Create(scene);
        check("image border fixture uses a true half-scale billboard depth", mapping.TryGetEqualDepthLine(new(60, 80), out var depth) && depth.Scale == .5);
        using var bitmap = new SKBitmap(40, 30); bitmap.Erase(SKColors.Blue);
        using var encodedImage = SKImage.FromBitmap(bitmap); using var data = encodedImage.Encode(SKEncodedImageFormat.Png, 100);
        var raster = new CaptureRasterResult(data.ToArray(), new(30, 25, 40, 30), true)
            { ImageBorderWidth = 6, ImageBorderColor = 0xFFFF0000 };
        var session = new EditorSession(scene);
        var image = SnapshotImageCommand.Insert(session, raster, mapping.PlaneToScreen, depth);
        check("billboard screenshot stores body dimensions and border in the same shared logical units", image.Width == 80 && image.Height == 60 && image.ImageBorderWidth == 12);
        var frame = new EditorController(session) { Tool = EditorTool.Select }.SelectionScreenCorners;
        check("billboard selection includes the complete outside border", frame.Count == 4 &&
            Math.Abs(frame.Min(point => point.X) - 24) < 1e-6 && Math.Abs(frame.Max(point => point.X) - 76) < 1e-6 &&
            Math.Abs(frame.Min(point => point.Y) - 19) < 1e-6 && Math.Abs(frame.Max(point => point.Y) - 61) < 1e-6);
        using (var pixels = Render(session.Document, session.Assets))
            check("billboard outside border keeps its captured screen width and unpainted interior", pixels.GetPixel(26, 40) == SKColors.Red && pixels.GetPixel(31, 40) == SKColors.Blue);
        session.Document.Elements.Single().ImageBorderWidth = 1200; SceneValidation.Validate(session.Document);
        check("large logical borders produced at distant depth remain valid project data", session.Document.Elements.Single().Clone().ImageBorderWidth == 1200);
    }

    private static bool AllPixelsInside(SKBitmap pixels, RectD bounds)
    {
        for (int y = 0; y < pixels.Height; y++)
            for (int x = 0; x < pixels.Width; x++)
                if (pixels.GetPixel(x, y).Alpha != 0 && (x + 1 < bounds.Left || x > bounds.Right || y + 1 < bounds.Top || y > bounds.Bottom)) return false;
        return true;
    }

    private static void EmptyBodyWithBorder(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 120, 100) };
        var selection = new CaptureSelection(CaptureShape.Rectangle, CaptureSource.CanvasOnly, new(30, 25, 40, 30), [], 6, 0xFFFF0000);
        var raster = SnapshotImageCommand.Render(scene, [], selection, null, [scene.Viewport]);
        using var body = SKBitmap.Decode(raster.PngBytes);
        check("an entirely transparent capture remains empty because there is no alpha silhouette to expand", !raster.HasVisiblePixels &&
            Enumerable.Range(0, body.Height).All(y => Enumerable.Range(0, body.Width).All(x => body.GetPixel(x, y).Alpha == 0)));
        var session = new EditorSession(scene);
        bool rejected = false;
        try { SnapshotImageCommand.Insert(session, raster); }
        catch (InvalidOperationException) { rejected = true; }
        check("snapshot insertion rejects an empty alpha silhouette even when its border is enabled",
            rejected && session.Document.Elements.Count == 0 && session.UndoCount == 0);
        check("an invisible border cannot turn an empty capture into content", !SnapshotImageCommand.Render(new SceneDocument { Viewport = scene.Viewport }, [],
            selection with { BorderColor = 0 }, null, [scene.Viewport]).HasVisiblePixels);
        check("editable borders do not bypass missing displays or an entirely off-display capture",
            !SnapshotImageCommand.Render(new SceneDocument { Viewport = scene.Viewport }, [], selection, null, []).HasVisiblePixels &&
            !SnapshotImageCommand.Render(new SceneDocument { Viewport = scene.Viewport }, [], selection with { Bounds = new(200, 200, 40, 30) }, null, [scene.Viewport]).HasVisiblePixels);
    }
}
