using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private async Task<StampRenderContext?> PrepareStampRenderingAsync(EditorSession session)
    {
        if(!SceneRenderer.HasLiveEffects(session.CreateSelectionSnapshot()))return null;
        _documentBusy=true;
        try
        {
            var snapshot=session.Document.Clone();
            var resources=ResourceSnapshot(snapshot).ToList();
            if(snapshot.Kind==DocumentKind.LiveOverlay)
            {
                var captured=await CaptureCleanDesktopAsync();
                var assets=new AssetLibrary();string id=assets.Add(captured.PngBytes);
                resources.Add(assets.Get(id));snapshot.Background=Background(captured,id);
            }
            return new(snapshot,ResourceLibrary(resources));
        }
        finally {_documentBusy=false;RequestRender();}
    }
    private async Task AddDesktopEffectAsync(RectD region, ImageEffectKind kind, double amount, uint color)
    {
        var target = Active;
        region = region.Intersect(target.Document.Viewport);
        if (region.IsEmpty) return;
        if (kind == ImageEffectKind.Pixelate && target.Document.Kind == DocumentKind.LiveOverlay)
        {
            target.Add(new CanvasElement { Kind = ElementKind.LivePixelate, Width = region.Width, Height = region.Height,
                Transform = Affine2D.Translation(region.X, region.Y), Style = new() { StrokeWidth = 0 },
                Effects = [new ImageEffect { Kind = ImageEffectKind.Pixelate, Amount = amount, Bounds = new(0,0,1,1) }] });
            return;
        }
        if (kind == ImageEffectKind.Redact)
        {
            target.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = region.Width, Height = region.Height,
                Transform = Affine2D.Translation(region.X, region.Y), Style = new() { Fill = color, StrokeWidth = 0 } });
            return;
        }
        _documentBusy = true;
        try
        {
            var snapshot = target.CreateCommittedSnapshot(); snapshot.Viewport = region; snapshot.LayerOpacity = 1;
            var resources = ResourceSnapshot(snapshot).ToList();
            var capture = await CaptureCleanDesktopAsync();
            var backgroundAssets = new AssetLibrary(); string backgroundId = backgroundAssets.Add(capture.PngBytes);
            resources.Add(backgroundAssets.Get(backgroundId)); snapshot.Background = Background(capture, backgroundId);
            byte[] pixels = await Task.Run(() =>
            {
                using var renderer = new SceneRenderer();
                return renderer.ExportPng(snapshot, ResourceLibrary(resources));
            }, _documentLifetime.Token);
            if (!ReferenceEquals(target, Active) || !CanAddCanvasContent()) return;
            string id = _assets.Add(pixels);
            target.Add(new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = region.Width, Height = region.Height,
                Transform = Affine2D.Translation(region.X, region.Y),
                Effects = [new ImageEffect { Kind = kind, Amount = amount, Color = color, Bounds = new(0, 0, 1, 1) }],
                Style = new() { StrokeWidth = 0 } });
            UpdateStatus(L.Get("Effect.RegionFrozen"));
        }
        finally { _documentBusy = false; }
    }

    private void BeginEyedropper(ColorField field)
    {
        CloseToolOptions();
        _editor.Cancel(); _eyedropper = field;
        UpdateStatus(L.Get("Color.SampleHint"));
    }

    private async Task SampleDesktopColorAsync(ColorField field, OverlayPoint position)
    {
        _documentBusy = true;
        try
        {
            var snapshot = Active.CreateCommittedSnapshot();
            var point = _editor.ToDocument(position); snapshot.Viewport = new(point.X, point.Y, 1, 1);
            var resources = ResourceSnapshot(snapshot).ToList();
            if (snapshot.Background is null)
            {
                var capture = await CaptureCleanDesktopAsync(); var assets = new AssetLibrary(); string id = assets.Add(capture.PngBytes);
                resources.Add(assets.Get(id)); snapshot.Background = Background(capture, id);
            }
            uint color = await Task.Run(() =>
            {
                using var renderer = new SceneRenderer(); using var image = SKBitmap.Decode(renderer.ExportPng(snapshot, ResourceLibrary(resources)));
                var pixel = image.GetPixel(0, 0);
                return (uint)(pixel.Alpha << 24 | pixel.Red << 16 | pixel.Green << 8 | pixel.Blue);
            }, _documentLifetime.Token);
            field.SetUserValue(color);
            UpdateStatus(L.Get("Color.Sampled"));
        }
        finally { _documentBusy = false; }
    }
}
