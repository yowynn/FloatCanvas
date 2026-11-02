using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    internal sealed class PreparedWindowSource(IWindowCaptureSource capture, WindowSourceDescriptor descriptor,
        byte[] fullSourcePng, WindowCaptureSize contentSize, RectD? crop) : IDisposable
    {
        internal IWindowCaptureSource Capture { get; } = capture;
        internal WindowSourceDescriptor Descriptor { get; } = descriptor.Clone();
        internal byte[] Png { get; } = fullSourcePng.ToArray();
        internal WindowCaptureSize Content { get; } = contentSize;
        internal RectD? Crop { get; } = crop;
        internal bool Transferred;
        private bool disposed;
        internal bool IsCurrent => Capture.Target.IsCurrent && Capture.Status is not (WindowCaptureStatus.Closed or WindowCaptureStatus.Stopped or WindowCaptureStatus.Unavailable);
        public void Dispose() { if (disposed) return; disposed = true; if (!Transferred) Capture.Dispose(); }
    }
    private sealed record PendingWindowInsertion(PreparedWindowSource Source, WindowSourceOperationGuard Guard, EditorSession Owner,
        Action<BillboardDepthLine?, PointD> Placement);
    private PendingWindowInsertion? _pendingWindowInsertion;
    private Func<WindowSourceOperationGuard, RectD?, WindowSourceFit, CancellationToken, Task<PreparedWindowSource?>>? _prepareWindowSource;

    partial void InitializeWindowSourceCommands()
    {
        _prepareWindowSource = PickWindowSourceAsync;
        _addWindowSourceCommand = AddWindowSourceAsync;
        _rebindWindowSourceCommand = RebindWindowSourceAsync;
        _cropWindowSourceCommand = CropWindowSourceAsync;
        _freezeWindowSourcesCommand = FreezeWindowSourcesAsync;
    }
    private WindowSourceOperationGuard CaptureWindowOperation(bool selection = true) => new(Active, _editor,
        _host.IsEditing, _host.EditingSessionVersion, _prefabEditContext, _host.Monitors, WindowOwnerToken(Active), selection);
    private bool IsCurrentWindowOperation(WindowSourceOperationGuard guard) => !_disposed && !_documentStopped && !_documentExitRequested &&
        guard.IsCurrent(Active, _editor, _host.IsEditing, _host.EditingSessionVersion, _prefabEditContext, _host.Monitors, WindowOwnerToken(Active));

    private async Task<PreparedWindowSource?> PickWindowSourceAsync(WindowSourceOperationGuard guard, RectD? crop, WindowSourceFit fit, CancellationToken token)
    {
        var picker = new WindowSourcePickerWindow(cancellation => Task.Run<IReadOnlyList<WindowSourceCandidate>>(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            return WindowCaptureCatalog.Enumerate().Select(target => new WindowSourceCandidate(target, target.Title, target.ProcessName, target.WindowClass,
                WindowCaptureCatalog.IsMinimized(target) ? "WindowSource.Status.SuspendedMinimized" : null)).ToArray();
        }, cancellation)) { Owner = _toolbar };
        ConfigureAppWindow(picker, true, regular: !_host.IsEditing);
        _ = picker.RefreshCandidatesAsync(token);
        SessionWindowCoordinator.ShowModal(picker);
        if (!picker.Accepted || picker.SelectedCandidate?.Target is not WindowCaptureTarget target || !IsCurrentWindowOperation(guard) || !target.IsCurrent) return null;
        IWindowCaptureSource? capture = new LiveWindowCapture(target);
        try
        {
            await capture.FirstResult.WaitAsync(TimeSpan.FromSeconds(8), token);
            if (!IsCurrentWindowOperation(guard) || !target.IsCurrent || !capture.TryGetLatest(out var frame)) return null;
            byte[] png; WindowCaptureSize content;
            using (frame)
            {
                content = frame.ContentSize;
                png = await Task.Run(() =>
                {
                    using var image = SKImage.FromBitmap(frame.Bitmap);
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    return encoded?.ToArray() ?? throw new InvalidOperationException(L.Get("WindowSource.InvalidPreview"));
                }, token);
            }
            if (!IsCurrentWindowOperation(guard) || !target.IsCurrent) return null;
            var preview = new WindowSourceCropWindow(png, crop) { Owner = _toolbar };
            ConfigureAppWindow(preview, true, regular: !_host.IsEditing);
            SessionWindowCoordinator.ShowModal(preview);
            if (!preview.Accepted || !IsCurrentWindowOperation(guard) || !target.IsCurrent) return null;
            var descriptor = new WindowSourceDescriptor { BindingIntent = Guid.NewGuid(), TitleHint = target.Title,
                ProcessNameHint = target.ProcessName, WindowClassHint = target.WindowClass, Fit = fit };
            var result = new PreparedWindowSource(capture, descriptor, png, content, preview.NormalizedCrop);
            capture = null; return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        { _diagnostics.Write("WindowSource.PrepareFailed", exception: error); UpdateStatus(L.Get("WindowSource.Status.Unavailable")); return null; }
        finally { capture?.Dispose(); }
    }

    private async Task AddWindowSourceAsync()
    {
        if (DocumentOperationBusy || Active.IsTransactionActive || !Active.CanInsertInEditScope || _prepareWindowSource is null) return;
        var owner = Active; var guard = CaptureWindowOperation(selection: false); PreparedWindowSource? prepared = null; bool pending = false;
        _documentBusy = true;
        try
        {
            prepared = await _prepareWindowSource(guard, null, WindowSourceFit.Contain, _documentLifetime.Token);
            if (prepared is null || !IsCurrentWindowOperation(guard) || !prepared.IsCurrent) return;
            prepared.Capture.SetActive(false);
            var source = prepared;
            Action<BillboardDepthLine?, PointD> placement = (depth, center) =>
            {
                _pendingWindowInsertion = null;
                try { CommitWindowInsertion(owner, source, guard, billboard: true, depth, center); }
                finally { source.Dispose(); }
            };
            if (_editor.BeginBillboardPlacement(placement))
            { _pendingWindowInsertion = new(prepared, guard, owner, placement); pending = true; RequestRender(); }
            else CommitWindowInsertion(owner, prepared, guard, billboard: false, null, null);
        }
        finally { if (!pending) prepared?.Dispose(); _documentBusy = false; }
    }
    private void CommitWindowInsertion(EditorSession owner, PreparedWindowSource source, WindowSourceOperationGuard guard,
        bool billboard, BillboardDepthLine? depth, PointD? center)
    {
        if (!IsCurrentWindowOperation(guard) || !source.IsCurrent || _windowRuntime is null) return;
        var placement = billboard ? owner.Document.Viewport : VisibleDocumentArea();
        var element = WindowSourceInsertion.Prepare(owner, source.Descriptor, source.Content.Width, source.Content.Height, source.Crop,
            placement, billboard, depth, center, ImageInsertionOpacity(), planeCenter: billboard ? null : VisibleDocumentInsertionCenter(placement));
        AppearanceMigration.NormalizeElement(element);
        var staging = new AssetLibrary(); element.AssetId = staging.Add(source.Png, "image/png");
        var candidate = owner.Document.Clone(); new SceneGraph(candidate).InsertElement(element.Clone(), owner.EditScopeId, rootCanvas: owner.ActiveRootCanvas); SceneValidation.Validate(candidate);
        if (!IsCurrentWindowOperation(guard) || !source.IsCurrent) return;
        owner.Assets.Add(source.Png, "image/png");
        owner.Execute(L.Get("Image.ImportTitle"), document => new SceneGraph(document).InsertElement(element, owner.EditScopeId, rootCanvas: owner.ActiveRootCanvas));
        _windowRuntime.Bind(WindowOwnerToken(owner), owner.Document.ResourceId, new(null, element.Id), source.Descriptor.BindingIntent, source.Capture);
        source.Transferred = true; owner.SetSelection([element.Id]); SwitchToolState(EditorTool.Select);
        UpdateWindowSourceRuntime(); RefreshWindowSourcesUi(); ScheduleRecovery(); RequestRender();
    }
    private void ValidatePendingWindowInsertion()
    {
        if (_pendingWindowInsertion is not { } pending || _editor.HasPendingBillboardInsertion &&
            ReferenceEquals(Active, pending.Owner) && IsCurrentWindowOperation(pending.Guard) && _host.IsVisible) return;
        _pendingWindowInsertion = null;
        _editor.CancelBillboardPlacement(pending.Placement);
        pending.Source.Dispose();
    }
    private CanvasElement? EditableWindowImage(Guid id)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive || Active.Selection.Count != 1 || !Active.Selection.Contains(id)) return null;
        var graph = new SceneGraph(Active.Document); var image = graph.FindElement(id);
        return image?.WindowSource is not null && !graph.IsLocked(id) ? image : null;
    }
    private async Task RebindWindowSourceAsync(Guid id)
    {
        var image = EditableWindowImage(id); if (image?.WindowSource is not { } descriptor || _prepareWindowSource is null) return;
        var owner = Active; var guard = CaptureWindowOperation(); _documentBusy = true;
        try
        {
            using var prepared = await _prepareWindowSource(guard, image.ImageCrop, descriptor.Fit, _documentLifetime.Token);
            if (prepared is null || !IsCurrentWindowOperation(guard) || !prepared.IsCurrent || _windowRuntime is null) return;
            var staging = new AssetLibrary(); string asset = staging.Add(prepared.Png, "image/png");
            var candidate = owner.Document.Clone(); var replacement = candidate.Elements.Single(element => element.Id == id);
            replacement.WindowSource = prepared.Descriptor.Clone(); replacement.AssetId = asset; replacement.ImageCrop = prepared.Crop;
            SceneValidation.Validate(candidate);
            if (!IsCurrentWindowOperation(guard) || !prepared.IsCurrent) return;
            owner.Assets.Add(prepared.Png, "image/png");
            owner.Execute(L.Get("History.ReplaceImage"), document =>
            {
                var target = document.Elements.Single(element => element.Id == id);
                target.WindowSource = prepared.Descriptor.Clone(); target.AssetId = asset; target.ImageCrop = prepared.Crop;
            });
            _windowRuntime.Bind(WindowOwnerToken(owner), owner.Document.ResourceId, new(null, id), prepared.Descriptor.BindingIntent, prepared.Capture);
            prepared.Transferred = true; UpdateWindowSourceRuntime(); RefreshWindowSourcesUi(); ScheduleRecovery(); RequestRender();
        }
        finally { _documentBusy = false; }
    }
    private async Task CropWindowSourceAsync(Guid id)
    {
        var image = EditableWindowImage(id); if (image is null) return;
        var owner = Active; var guard = CaptureWindowOperation(); _documentBusy = true;
        try
        {
            byte[]? png = await WindowSourcePreviewPngAsync(owner, image, _documentLifetime.Token);
            if (png is null || !IsCurrentWindowOperation(guard)) return;
            var dialog = new WindowSourceCropWindow(png, image.ImageCrop) { Owner = _toolbar };
            ConfigureAppWindow(dialog, true, regular: !_host.IsEditing); SessionWindowCoordinator.ShowModal(dialog);
            if (!dialog.Accepted || !IsCurrentWindowOperation(guard)) return;
            owner.Execute(L.Get("Editor.History.CropImage"), document => document.Elements.Single(element => element.Id == id).ImageCrop = dialog.NormalizedCrop);
            ScheduleRecovery(); RequestRender();
        }
        finally { _documentBusy = false; }
    }
    private async Task<byte[]?> WindowSourcePreviewPngAsync(EditorSession owner, CanvasElement image, CancellationToken token)
    {
        if (image.WindowSource is { } source && TryRetainWindowSourceFrame(owner, new(null, image.Id), source.BindingIntent, out var frame))
        {
            using (frame)
                return await Task.Run(() => { using var png = frame!.Image.Encode(SKEncodedImageFormat.Png, 100); return png?.ToArray(); }, token);
        }
        if (image.AssetId is not { } asset) return null;
        var resource = owner.Assets.Get(asset);
        return await Task.Run(() =>
        {
            using var decoded = SKImage.FromEncodedData(resource.Data.ToArray()); using var png = decoded?.Encode(SKEncodedImageFormat.Png, 100); return png?.ToArray();
        }, token);
    }
    private async Task FreezeWindowSourcesAsync(IReadOnlyList<Guid> ids)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive) return;
        var owner = Active; var graph = new SceneGraph(owner.Document);
        var targets = ids.Select(graph.FindElement).Where(element => element?.WindowSource is not null).Cast<CanvasElement>().ToArray();
        if (targets.Length == 0 || targets.Any(element => graph.IsLocked(element.Id) || !HasWindowPixels(element))) return;
        var guard = CaptureWindowOperation(); var snapshot = owner.Document.Clone();
        var sourceResources = owner.Assets.Resources.ToArray();
        var captures = new List<(CanvasElement Element, WindowFrameLease? Frame)>();
        using var shown = FreezeWindowSourceCapture(owner);
        foreach (var target in targets)
        {
            var frame = shown?.RetainFrame(owner.Document.ResourceId, new(null, target.Id), target.WindowSource!.BindingIntent);
            if (frame is null) TryRetainWindowSourceFrame(owner, new(null, target.Id), target.WindowSource!.BindingIntent, out frame);
            captures.Add((snapshot.Elements.Single(element => element.Id == target.Id), frame));
        }
        _documentBusy = true;
        try
        {
            var materialized = await Task.Run(() =>
            {
                var assets = ResourceLibrary(sourceResources); var result = new List<(Guid Id, byte[] Png)>(); long bytes = 0;
                foreach (var capture in captures)
                {
                    _documentLifetime.Token.ThrowIfCancellationRequested(); var context = new object();
                    using var frames = capture.Frame is null ? null : WindowFrameSet.Capture(context, [capture.Frame]);
                    using var renderer = new SceneRenderer { WindowFrames = frames, WindowOwnerContext = context };
                    var body = renderer.PrepareFrozenWindowBody(snapshot, capture.Element, assets);
                    bytes = checked(bytes + body.PngBytes.Length); if (bytes > 128L * 1024 * 1024) throw new InvalidOperationException(L.Get("Export.BufferTooLarge"));
                    result.Add((capture.Element.Id, body.PngBytes));
                }
                return result;
            }, _documentLifetime.Token);
            if (!IsCurrentWindowOperation(guard)) return;
            var staging = new AssetLibrary(); var prepared = materialized.Select(item => (item.Id, item.Png, Asset: staging.Add(item.Png, "image/png"))).ToArray();
            var candidate = owner.Document.Clone(); foreach (var item in prepared) MakeStatic(candidate.Elements.Single(element => element.Id == item.Id), item.Asset);
            SceneValidation.Validate(candidate); if (!IsCurrentWindowOperation(guard)) return;
            foreach (var item in prepared) owner.Assets.Add(item.Png, "image/png");
            owner.Execute(L.Get("WindowSource.Freeze"), document => { foreach (var item in prepared) MakeStatic(document.Elements.Single(element => element.Id == item.Id), item.Asset); });
            ScheduleRecovery(); RequestRender(); RefreshWindowSourcesUi();
        }
        finally { foreach (var capture in captures) capture.Frame?.Dispose(); _documentBusy = false; }
        static void MakeStatic(CanvasElement element, string asset)
        { element.AssetId = asset; element.WindowSource = null; element.ImageCrop = null; element.ImageFrame = null; element.Effects.Clear(); }
    }
}
