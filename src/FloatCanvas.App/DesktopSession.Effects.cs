using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private sealed record EffectRenderRequest(EditorSession Target, SceneDocument Document, long Revision, WindowSessionRequest Window);
    private EffectRenderRequest BeginEffectRender(EditorSession target) =>
        new(target, target.Document, target.SceneRevision, new(_host.EditingSessionVersion, _host.IsEditing, target.Document));
    private bool CanFinishEffect(EffectRenderRequest request) => !_disposed && !_documentStopped && !_documentExitRequested &&
        !_documentLifetime.IsCancellationRequested && ReferenceEquals(request.Target, Active) &&
        ReferenceEquals(request.Document, request.Target.Document) && request.Revision == request.Target.SceneRevision &&
        !request.Target.IsTransactionActive && request.Window.IsCurrent(_host.EditingSessionVersion, _host.IsEditing, Active.Document) &&
        (!request.Window.Editing || (_windowCoordinator?.CanContinue(request.Window, Active.Document) == true &&
            (!_host.IsKeyboardIsolationEnabled || _host.IsKeyboardIsolationActive)));

    private async Task<StampRenderContext?> PrepareStampRenderingAsync(EditorSession session)
    {
        var selection = session.CreateSelectionSnapshot();
        bool hasLiveEffects = SceneRenderer.HasLiveEffects(selection);
        if(!hasLiveEffects && !_renderer.HasAnimatedImages(selection, session.Assets))return null;
        var animationTime = _renderer.AnimationTime;
        var request = BeginEffectRender(session);
        _documentBusy=true;
        try
        {
            var snapshot=session.Document.Clone();
            var resources=ResourceSnapshot(snapshot).ToList();
            if(hasLiveEffects && snapshot.Kind==DocumentKind.LiveOverlay)
            {
                var captured=await CaptureCleanDesktopAsync();
                if (!CanFinishEffect(request)) throw new OperationCanceledException("Stamp render context is no longer current.");
                var assets=new AssetLibrary();string id=assets.Add(captured.PngBytes);
                resources.Add(assets.Get(id));snapshot.Background=Background(captured,id);
            }
            return new(snapshot,ResourceLibrary(resources), animationTime);
        }
        finally {_documentBusy=false;RequestRender();}
    }
    private Task AddDesktopEffectAsync(RectD region, ImageEffectKind kind, double amount, uint color) =>
        AddDesktopEffectCoreAsync(region, kind, amount, color, CaptureCleanDesktopAsync);

    private async Task AddDesktopEffectCoreAsync(RectD region, ImageEffectKind kind, double amount, uint color,
        Func<Task<CapturedDesktop>> captureDesktop)
    {
        var target = Active;
        var request = BeginEffectRender(target);
        var animationTime = _renderer.AnimationTime;
        region = region.Intersect(target.Document.Viewport);
        if (region.IsEmpty) return;
        bool desktopFilter = kind is ImageEffectKind.Pixelate or ImageEffectKind.Blur && target.Document.Kind == DocumentKind.LiveOverlay;
        if (desktopFilter && _host.Capabilities.SupportsCaptureExclusion)
        {
            var effect = new CanvasElement { Kind = kind == ImageEffectKind.Blur ? ElementKind.LiveBlur : ElementKind.LivePixelate, Width = region.Width, Height = region.Height,
                Transform = Affine2D.Translation(region.X, region.Y), Style = new() { StrokeWidth = 0 },
                Effects = [new ImageEffect { Kind = kind, Amount = amount, Bounds = new(0,0,1,1) }] };
            if (!await PrepareLiveEffectAsync(request, effect) || !CanFinishEffect(request)) return;
            target.Add(effect);
            UpdateStatus(L.Get("Effect.LiveReady"));
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
            var capture = await captureDesktop();
            if (!CanFinishEffect(request)) return;
            var backgroundAssets = new AssetLibrary(); string backgroundId = backgroundAssets.Add(capture.PngBytes);
            resources.Add(backgroundAssets.Get(backgroundId)); snapshot.Background = Background(capture, backgroundId);
            byte[] pixels = await Task.Run(() =>
            {
                using var renderer = new SceneRenderer { AnimationTime = animationTime };
                return renderer.ExportPng(snapshot, ResourceLibrary(resources));
            }, _documentLifetime.Token);
            if (!CanFinishEffect(request) || !CanAddCanvasContent()) return;
            string id = _assets.Add(pixels);
            target.Add(new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = region.Width, Height = region.Height,
                Transform = Affine2D.Translation(region.X, region.Y),
                Effects = [new ImageEffect { Kind = kind, Amount = amount, Color = color, Bounds = new(0, 0, 1, 1) }],
                Style = new() { StrokeWidth = 0 } });
            UpdateStatus(L.Get(desktopFilter ? kind == ImageEffectKind.Blur ? "Effect.LegacyBlurFrozen" : "Effect.LegacyPixelateFrozen" : "Effect.RegionFrozen"));
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
        var animationTime = _renderer.AnimationTime;
        var request = BeginEffectRender(Active);
        _documentBusy = true;
        try
        {
            var snapshot = request.Target.CreateCommittedSnapshot();
            var point = _editor.ToDocument(position); snapshot.Viewport = new(point.X, point.Y, 1, 1);
            var resources = ResourceSnapshot(snapshot).ToList();
            if (snapshot.Background is null)
            {
                var capture = await CaptureCleanDesktopAsync(); var assets = new AssetLibrary(); string id = assets.Add(capture.PngBytes);
                if (!CanFinishEffect(request)) return;
                resources.Add(assets.Get(id)); snapshot.Background = Background(capture, id);
            }
            uint color = await Task.Run(() =>
            {
                using var renderer = new SceneRenderer { AnimationTime = animationTime }; using var image = SKBitmap.Decode(renderer.ExportPng(snapshot, ResourceLibrary(resources)));
                var pixel = image.GetPixel(0, 0);
                return (uint)(pixel.Alpha << 24 | pixel.Red << 16 | pixel.Green << 8 | pixel.Blue);
            }, _documentLifetime.Token);
            if (!CanFinishEffect(request)) return;
            field.SetUserValue(color);
            UpdateStatus(L.Get("Color.Sampled"));
        }
        finally { _documentBusy = false; }
    }
}
