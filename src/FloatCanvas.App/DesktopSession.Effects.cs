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
            CapturedDesktop? screenCapture = null;
            if(hasLiveEffects && snapshot.Kind==DocumentKind.LiveOverlay)
            {
                var captured=await CaptureCleanDesktopAsync();
                if (!CanFinishEffect(request)) throw new OperationCanceledException("Stamp render context is no longer current.");
                screenCapture = captured;
            }
            return new(snapshot,ResourceLibrary(resources), animationTime, screenCapture);
        }
        finally {_documentBusy=false;RequestRender();}
    }
    private Task AddDesktopEffectAsync(RectD region, ImageEffectKind kind, double amount, uint color) =>
        AddDesktopEffectCoreAsync(region, kind, amount, color, CaptureCleanDesktopAsync);

    private async Task AddDesktopEffectCoreAsync(RectD region, ImageEffectKind kind, double amount, uint color,
        Func<Task<CapturedDesktop>> captureDesktop)
    {
        if (kind is not (ImageEffectKind.Blur or ImageEffectKind.Pixelate)) return;
        var target = Active;
        var request = BeginEffectRender(target);
        var animationTime = _renderer.AnimationTime;
        if (region.IsEmpty) return;
        var mapping = CanvasMapping.Create(target.Document);
        // Snapshot this transient choice before the controller ends the input gesture or an
        // asynchronous desktop capture yields. The region is already logical screen/s here.
        var billboardDepth = _editor.ActiveBillboardDepthLine;
        var regionToScreen = billboardDepth is { } selectedDepth
            ? Projective2D.FromAffine(Affine2D.Scale(selectedDepth.Scale, selectedDepth.Scale)) : mapping.PlaneToScreen;
        if (!regionToScreen.TryTransformBounds(region, out var projectedRegion) ||
            projectedRegion.Intersect(target.Document.Viewport).IsEmpty) return;
        bool desktopFilter = kind is ImageEffectKind.Pixelate or ImageEffectKind.Blur && target.Document.Kind == DocumentKind.LiveOverlay;
        if (desktopFilter && _host.Capabilities.SupportsCaptureExclusion)
        {
            var effect = new CanvasElement { Kind = kind == ImageEffectKind.Blur ? ElementKind.LiveBlur : ElementKind.LivePixelate, Width = region.Width, Height = region.Height,
                Transform = Affine2D.Translation(region.X, region.Y), Style = new() { StrokeWidth = 0 },
                Effects = [new ImageEffect { Kind = kind, Amount = amount, Bounds = new(0,0,1,1) }] };
            if (billboardDepth is { } depth && !BillboardGeometry.TryPlaceFromScreen(effect, mapping, depth,
                regionToScreen.Compose(ElementGeometry.LocalToPlane(effect))))
            { UpdateStatus(L.Get("Billboard.InvalidDepth")); return; }
            if (!await PrepareLiveEffectAsync(request, effect) || !CanFinishEffect(request)) return;
            target.Add(effect);
            UpdateStatus(L.Get("Effect.LiveReady"));
            return;
        }
        _documentBusy = true;
        try
        {
            var snapshot = target.CreateCommittedSnapshot(); snapshot.LayerOpacity = 1;
            var resources = ResourceSnapshot(snapshot).ToList();
            var capture = await captureDesktop();
            if (!CanFinishEffect(request)) return;
            var selection = new CaptureSelection(CaptureShape.PolygonLasso, CaptureSource.BackgroundAndCanvas,
                projectedRegion, EditorController.Corners(region).Select(regionToScreen.TransformPoint).ToArray(), 0, 0);
            var raster = await Task.Run(() =>
            {
                using var backdrop = CreateScreenBackdrop(capture);
                using var renderer = new SceneRenderer { AnimationTime = animationTime, ScreenBackdrop = backdrop };
                return renderer.ExportSelectionPng(snapshot, ResourceLibrary(resources), selection, includeBackground: true);
            }, _documentLifetime.Token);
            if (!CanFinishEffect(request) || !CanAddCanvasContent()) return;
            var image = billboardDepth is { } depth
                ? SnapshotImageCommand.CreateBillboardElement(_assets, raster, mapping, depth)
                : SnapshotImageCommand.CreateElement(_assets, raster, mapping.PlaneToScreen);
            image.Effects = [new ImageEffect { Kind = kind, Amount = amount, Color = color, Bounds = new(0, 0, 1, 1) }];
            target.Add(image);
            UpdateStatus(L.Get(desktopFilter ? kind == ImageEffectKind.Blur ? "Effect.LegacyBlurFrozen" : "Effect.LegacyPixelateFrozen" : "Effect.RegionFrozen"));
        }
        finally { _documentBusy = false; }
    }

    private void BeginEyedropper(ColorField field)
    {
        var cursorHandle = EyedropperCursor.Handle;
        EndEyedropper(restore: false);
        _editor.Cancel(); _eyedropper = field;
        _eyedropperWindows = _app.Windows.Cast<System.Windows.Window>().Where(window => window.IsVisible && !ReferenceEquals(window, _toolbar)).ToArray();
        _eyedropperOwner = System.Windows.Window.GetWindow(field);
        using (var transition = _host.BeginInternalWindowTransition())
            foreach (var window in _eyedropperWindows) SessionWindowCoordinator.Hide(window);
        _host.SetPointerCursor(cursorHandle);
        var cursor = System.Windows.Forms.Cursor.Position;
        _eyedropperPoint = new(cursor.X,cursor.Y);
        _eyedropperTimer ??= new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _eyedropperTimer.Tick -= PreviewEyedropper;
        _eyedropperTimer.Tick += PreviewEyedropper;
        _eyedropperTimer.Start();
        RequestRender();
        UpdateStatus(L.Get("Color.SampleHint"));
    }

    private System.Windows.Window[] _eyedropperWindows = [];
    private System.Windows.Window? _eyedropperOwner;
    private System.Windows.Threading.DispatcherTimer? _eyedropperTimer;
    private OverlayPoint _eyedropperPoint;
    private uint? _eyedropperPreview;
    private bool _eyedropperPicked;

    private bool HandleEyedropperPointer(OverlayPointerEvent pointer)
    {
        if (_eyedropper is not { } field) return false;
        _eyedropperPoint = pointer.Position;
        if (pointer.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            if (pointer.Button == OverlayMouseButton.Left)
                _ = RunCommand(() => SampleDesktopColorAsync(field, pointer.Position));
            else if (pointer.Button == OverlayMouseButton.Right)
            { _eyedropperTimer?.Stop(); _eyedropperPicked = true; UpdateStatus(L.Get("Color.SampleCancelled")); }
        }
        else if (pointer.Kind is OverlayPointerKind.Up or OverlayPointerKind.Cancel && _eyedropperPicked) EndEyedropper();
        RequestRender();
        return true;
    }

    private void PreviewEyedropper(object? sender, EventArgs args)
    {
        if (_eyedropper is null || _documentBusy) return;
        _eyedropperPreview = DesktopPixelSampler.Sample(_eyedropperPoint);
        RequestRender();
    }

    private void EndEyedropper(bool restore = true)
    {
        var field = _eyedropper; _eyedropper = null;
        _eyedropperTimer?.Stop(); _eyedropperPreview = null; _eyedropperPicked = false;
        _host.SetPointerCursor(0);
        var windows = _eyedropperWindows; _eyedropperWindows = [];
        var owner = _eyedropperOwner; _eyedropperOwner = null;
        if (restore && field is not null && !_disposed && _host.IsEditing && _host.IsVisible)
        {
            foreach (var window in windows) SessionWindowCoordinator.Show(window);
            if (owner is { IsVisible: true, IsEnabled: true }) owner.Activate();
            if (field.IsVisible) field.OpenPicker(focus: true);
        }
        RequestRender();
    }

    private void DrawEyedropperPreview(SKCanvas canvas)
    {
        if (_eyedropper is null) return;
        var monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(_eyedropperPoint)) ?? _host.PrimaryMonitor;
        float scale = monitor.DpiX / 96f;
        float width = 126*scale, height = 40*scale;
        float x = (float)Math.Clamp(_eyedropperPoint.X + 20*scale, monitor.Bounds.Left, monitor.Bounds.Right-width);
        float y = (float)Math.Clamp(_eyedropperPoint.Y + 20*scale, monitor.Bounds.Top, monitor.Bounds.Bottom-height);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(35,35,38,245) };
        canvas.DrawRoundRect(x,y,width,height,8*scale,8*scale,paint);
        paint.Color = _eyedropperPreview is { } color ? new SKColor(color) : SKColors.Transparent;
        canvas.DrawCircle(x+20*scale,y+20*scale,11*scale,paint);
        paint.Color=SKColors.White; paint.Style=SKPaintStyle.Stroke; paint.StrokeWidth=scale;
        canvas.DrawCircle(x+20*scale,y+20*scale,11*scale,paint);
        paint.Style=SKPaintStyle.Fill;
        using var font = new SKFont(SKTypeface.Default,12*scale);
        canvas.DrawText(_eyedropperPreview is { } current ? ColorLibrary.ToHtml(current,false) : "…",x+38*scale,y+25*scale,SKTextAlign.Left,font,paint);
    }

    private Task SampleDesktopColorAsync(ColorField field, OverlayPoint position)
    {
        _eyedropperTimer?.Stop();
        try
        {
            // Sample the same displayed pixel as the live bubble. A second capture and
            // export can differ from the screen and unnecessarily reads a whole desktop.
            uint? color = DesktopPixelSampler.Sample(position);
            if (color.HasValue && ReferenceEquals(_eyedropper, field))
            { field.SetUserValue(color.Value); UpdateStatus(L.Get("Color.Sampled")); }
            else UpdateStatus(L.Get("Color.SampleCancelled"));
        }
        // The complete original canvas press must finish before restoring popups;
        // other pointer subscribers still dismiss transient panels on this Down.
        finally { _eyedropperPicked = true; }
        return Task.CompletedTask;
    }
}
