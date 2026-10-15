using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private const string ObjectClipboardFormat = "FloatCanvas.Objects.v1";
    private readonly ProjectStore _projectStore = new();
    private readonly RecoveryStore _recoveryStore = new(Path.Combine(ApplicationPaths.ProfileDirectory, "recovery"));
    private readonly CaptureService _documentCaptureService = new();
    private readonly CancellationTokenSource _documentLifetime = new();
    private readonly Dictionary<EditorSession, DocumentFileState> _documentFiles = [];
    private DispatcherTimer? _documentRecoveryTimer;
    private Task _documentRecoveryTask = Task.CompletedTask;
    private string? _documentRecoveryFingerprint;
    private string? _documentFrozenCaptureWarning;
    private bool _documentBusyValue, _documentExitRequestedValue, _documentStopped, _documentRecoveryWritesBlocked;
    private bool _documentBusy
    {
        get => _documentBusyValue;
        set { if (_documentBusyValue == value) return; _documentBusyValue = value; UpdateInteractionAvailability(); }
    }
    private bool _documentExitRequested
    {
        get => _documentExitRequestedValue;
        set { if (_documentExitRequestedValue == value) return; _documentExitRequestedValue = value; UpdateInteractionAvailability(); }
    }
    private EditorTool _documentLiveTool = EditorTool.Freehand;
    private bool DocumentOperationBusy => _documentBusy || _documentExitRequested;
    private sealed class DocumentFileState { public string? Path; public string? SavedFingerprint; }
    private sealed class ClipboardObjects
    {
        public int Version { get; set; } = 1;
        public SceneDocument Document { get; set; } = new();
        public List<ClipboardAsset> Assets { get; set; } = [];
    }
    private sealed class ClipboardAsset
    {
        public string Id { get; set; } = "";
        public string MediaType { get; set; } = "image/png";
        public byte[] Bytes { get; set; } = [];
    }

    private async Task InitializeDocumentsAsync(bool recover = true)
    {
        _documentBusy = true;
        try
        {
            bool restoredWorkspace = false;
            if (recover)
            {
                RecoveredWorkspace? restored = null;
                try { restored = await _recoveryStore.LoadAsync(_documentLifetime.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try
                    {
                        string location = await PreserveRecoveryGenerationsAsync();
                        ShowError(L.Get("Recovery.LoadFailedArchived") + location + "\n\n" + ex.Message);
                    }
                    catch (Exception archiveError) when (archiveError is not OperationCanceledException)
                    {
                        ShowError(L.Get("Recovery.LoadFailedPreserved") + ex.Message + "\n" + archiveError.Message);
                    }
                }
                if (restored is not null)
                {
                    MergeAssets(restored.Assets);
                    _live = new(restored.State.LiveDocument, _assets);
                    _capture = restored.State.CaptureDocument is { } draft ? new(draft, _assets) : null;
                    _captureOrigin = restored.State.CaptureOrigin;
                    _documentRecoveryFingerprint = WorkspaceFingerprint(restored.State);
                    restoredWorkspace = true;
                    if (restored.FromBackup)
                    {
                        try
                        {
                            string location = await PreserveRecoveryGenerationsAsync();
                            UpdateStatus(L.Get("Recovery.PreviousLoaded") + location);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { ShowError(L.Get("Recovery.PreviousArchiveFailed") + ex.Message); }
                    }
                }
            }
            _editor.ReplaceSession(_live, new(0, 0));
            if (EnsureCaptureVisible()) ScheduleRecovery();
            if (!restoredWorkspace) FileState(_live).SavedFingerprint = Fingerprint(_live.Document);
            _host.SetEditing(false);
            _toolbar.Hide(); _inspector?.Hide(); RequestRender();
        }
        finally { _documentBusy = false; }
    }
    private async Task<string> PreserveRecoveryGenerationsAsync()
    {
        _documentRecoveryWritesBlocked = true;
        var archived = await _recoveryStore.ArchiveExistingAsync(_documentLifetime.Token);
        _documentRecoveryWritesBlocked = false;
        return archived.Count == 0 ? _recoveryStore.DirectoryPath : Path.GetDirectoryName(archived[0])!;
    }

    private void ScheduleRecovery()
    {
        if (_documentStopped || _documentExitRequested || _documentRecoveryWritesBlocked) return;
        _documentRecoveryTimer ??= CreateRecoveryTimer();
        // Keep the first pending commit's deadline even while further small edits arrive.
        if (!_documentRecoveryTimer.IsEnabled) _documentRecoveryTimer.Start();
    }
    private DispatcherTimer CreateRecoveryTimer()
    {
        // One bounded UI snapshot per deadline must not starve behind continuous render work.
        // Encoding/writing still runs asynchronously in RecoveryStore.
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try { await FlushRecoveryAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { UpdateStatus(L.Get("Recovery.SaveFailed") + ex.Message); }
        };
        return timer;
    }
    private async Task FlushRecoveryAsync()
    {
        _documentRecoveryTimer?.Stop();
        await _documentRecoveryTask;
        if (_documentStopped) return;
        if (_documentRecoveryWritesBlocked) await PreserveRecoveryGenerationsAsync();
        PruneDocumentResources();
        var snapshot = SnapshotWorkspace();
        string fingerprint = WorkspaceFingerprint(snapshot);
        if (fingerprint == _documentRecoveryFingerprint) return;
        _documentRecoveryTask = _recoveryStore.SaveAsync(snapshot, _assets, _documentLifetime.Token);
        try { await _documentRecoveryTask; _documentRecoveryFingerprint = fingerprint; }
        finally { if (_documentRecoveryTask.IsCompleted) _documentRecoveryTask = Task.CompletedTask; }
    }
    private async Task RequestExitAsync()
    {
        if (_documentExitRequested || _documentBusy) return;
        _documentExitRequested = true; _editor.Cancel();
        try { await FlushRecoveryAsync(); _app.Shutdown(); }
        catch (Exception ex)
        {
            _documentExitRequested = false;
            ShowError(L.Get("Recovery.ExitSaveFailed") + ex.Message);
        }
    }
    private void StopDocumentTimers()
    {
        _documentStopped = true; _documentRecoveryTimer?.Stop();
        _documentLifetime.Cancel(); _documentCaptureService.Dispose();
    }
    private WorkspaceState SnapshotWorkspace() => new()
    {
        LiveDocument = _live.CreateCommittedSnapshot(), CaptureDocument = _capture?.CreateCommittedSnapshot(), CaptureOrigin = _captureOrigin
    };
    private static string Fingerprint(SceneDocument document) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document)));
    private static string WorkspaceFingerprint(WorkspaceState workspace) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(workspace)));
    private DocumentFileState FileState(EditorSession session)
    {
        if (!_documentFiles.TryGetValue(session, out var state)) _documentFiles.Add(session, state = new());
        return state;
    }
    private bool IsDirty(EditorSession session) => FileState(session).SavedFingerprint != Fingerprint(session.Document);
    private void PruneDocumentResources()
    {
        if (_documentBusy || _live.IsTransactionActive || _capture?.IsTransactionActive == true) return;
        // Save/export workers capture immutable AssetResource references before dispatching.
        // Removing a library entry cannot invalidate a snapshot already owned by those workers.
        _assets.Prune(_live.ReferencedAssetsIncludingHistory().Concat(_capture?.ReferencedAssetsIncludingHistory() ?? []));
        foreach (var session in _documentFiles.Keys.Where(s => !ReferenceEquals(s, _live) && !ReferenceEquals(s, _capture)).ToArray())
            _documentFiles.Remove(session);
    }
    private void MergeAssets(AssetLibrary source)
    {
        foreach (var resource in source.Resources)
            if (!_assets.TryGet(resource.Id, out _)) _assets.Add(resource.Data.ToArray(), resource.MediaType);
    }
    private AssetResource[] ResourceSnapshot(SceneDocument document) => document.ReferencedAssets.Select(_assets.Get).ToArray();
    private static AssetLibrary ResourceLibrary(IEnumerable<AssetResource> resources)
    {
        var assets = new AssetLibrary();
        foreach (var resource in resources) assets.Add(resource.Data.ToArray(), resource.MediaType);
        return assets;
    }

    private async Task OpenProjectAsync()
    {
        if (DocumentOperationBusy) return;
        _documentBusy = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = L.Get("Project.OpenTitle"), Filter = L.Get("Project.Filter"), CheckFileExists = true };
            if (dialog.ShowDialog(_toolbar) != true) return;
            _editor.Cancel(); var previous = Active;
            if (!await ConfirmReplaceAsync(previous, L.Get("Project.ReplaceCurrent"))) return;
            string previousContent = Fingerprint(previous.Document);
            long focusVersion = _host.ForegroundLossVersion;
            var loaded = await _projectStore.LoadAsync(dialog.FileName, _documentLifetime.Token);
            if (!ReferenceEquals(Active, previous) || Fingerprint(previous.Document) != previousContent)
            { UpdateStatus(L.Get("Project.OpenChanged")); return; }
            MergeAssets(loaded.Assets);
            var session = new EditorSession(loaded.Document, _assets);
            if (loaded.Document.Kind == DocumentKind.FrozenCapture)
            {
                if (_capture is not null && !ReferenceEquals(previous, _capture) && !await ConfirmReplaceAsync(_capture, L.Get("Project.ReplaceCapture"))) return;
                if (ReferenceEquals(previous, _live)) _documentLiveTool = _editor.Tool;
                if (_capture is not null) _documentFiles.Remove(_capture);
                _capture = session; _captureOrigin = CenteredOrigin(loaded.Document.Viewport);
                _documentFrozenCaptureWarning = null;
                _editor.ReplaceSession(session, _captureOrigin);
            }
            else
            {
                if (!ReferenceEquals(previous, _live) && !await ConfirmReplaceAsync(_live, L.Get("Project.ReplaceLive"))) return;
                _documentFiles.Remove(_live);
                _live = session; _editor.ReplaceSession(session, new(0, 0)); _editor.Tool = _documentLiveTool;
                OnMonitorsChanged();
            }
            _documentFiles[session] = new() { Path = dialog.FileName, SavedFingerprint = Fingerprint(session.Document) };
            bool editing;
            if (focusVersion != _host.ForegroundLossVersion)
            { SetEditing(false); editing = false; UpdateStatus(L.Get("Project.OpenedPassThrough")); }
            else editing = SetEditing(true);
            ScheduleRecovery();
            if (editing) UpdateStatus(L.Get("Project.OpenedPrefix") + Path.GetFileName(dialog.FileName));
            RequestRender();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Project.OpenFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); }
    }
    private async Task SaveProjectAsync(bool saveAs = false)
    {
        if (DocumentOperationBusy) return;
        _editor.Cancel(); _documentBusy = true;
        try { await SaveSessionAsync(Active, saveAs); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Project.SaveFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); }
    }
    private async Task<bool> SaveSessionAsync(EditorSession session, bool saveAs)
    {
        var state = FileState(session); string? path = state.Path;
        if (saveAs || path is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.Get("Project.SaveTitle"), Filter = L.Get("Project.Filter"), DefaultExt = ".floatcanvas", AddExtension = true,
                FileName = path is null ? L.Get("Project.FilePrefix") + DateTime.Now.ToString("yyyyMMdd-HHmmss") : Path.GetFileName(path)
            };
            if (dialog.ShowDialog(_toolbar) != true) return false;
            path = dialog.FileName;
        }
        var snapshot = session.Document.Clone(); string fingerprint = Fingerprint(snapshot);
        await _projectStore.SaveAsync(path, snapshot, _assets, _documentLifetime.Token);
        state.Path = path; state.SavedFingerprint = fingerprint;
        UpdateStatus(L.Get("Project.SavedPrefix") + Path.GetFileName(path)); return true;
    }
    private async Task<bool> ConfirmReplaceAsync(EditorSession session, string description)
    {
        if (!IsDirty(session)) return true;
        var result = Wpf.MessageBox.Show(_toolbar, description + L.Get("Project.ReplaceQuestion"), L.Get("App.Name"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && await SaveSessionAsync(session, true);
    }

    private async Task ExportAsync(bool transparent = false)
    {
        if (DocumentOperationBusy) return;
        _editor.Cancel(); var session = Active;
        _documentBusy = true;
        try
        {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = transparent ? L.Get("Export.TransparentTitle") : L.Get("Export.CompositeTitle"),
            Filter = transparent ? L.Get("Export.PngFilter") : L.Get("Export.ImageFilter"),
            DefaultExt = ".png", AddExtension = true, FileName = L.Get("Project.FilePrefix") + DateTime.Now.ToString("yyyyMMdd-HHmmss")
        };
        if (dialog.ShowDialog(_toolbar) != true) return;
        var snapshot = session.Document.Clone(); var resources = ResourceSnapshot(snapshot).ToList();
            string? captureWarning = snapshot.Kind == DocumentKind.FrozenCapture ? _documentFrozenCaptureWarning : null;
            if (!transparent && snapshot.Kind == DocumentKind.LiveOverlay)
            {
                var capture = await CaptureCleanDesktopAsync();
                captureWarning = CaptureWarning(capture);
                var temporaryAssets = new AssetLibrary(); string id = temporaryAssets.Add(capture.PngBytes);
                resources.Add(temporaryAssets.Get(id));
                snapshot.Background = Background(capture, id);
            }
            bool jpeg = !transparent && Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".jpg" or ".jpeg";
            byte[] result = await Task.Run(() =>
            {
                var assets = ResourceLibrary(resources); using var renderer = new SceneRenderer();
                return jpeg ? renderer.ExportJpeg(snapshot, assets) : renderer.ExportPng(snapshot, assets, transparent);
            }, _documentLifetime.Token);
            await WriteExportAsync(dialog.FileName, result, _documentLifetime.Token);
            UpdateStatus(WithCaptureWarning(L.Get("Export.SavedPrefix") + Path.GetFileName(dialog.FileName), transparent ? null : captureWarning));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Export.Failed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); RequestRender(); }
    }
    private static async Task WriteExportAsync(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void CopySelection() => _ = CopySelectionAsync();
    private async Task CopySelectionAsync()
    {
        if (DocumentOperationBusy) return;
        _editor.Cancel(); var session = Active;
        var selected = session.SelectedElements().Where(e => e.Visible).Select(e => e.Clone()).ToList();
        bool objects = selected.Count > 0;
        var snapshot = objects ? new SceneDocument { Elements = selected, Viewport = selected.Select(e => ElementGeometry.Bounds(e)).Aggregate(RectD.Union), LayerOpacity = session.Document.LayerOpacity } : session.Document.Clone();
        // Dots and horizontal lines still need a nonzero raster clipboard size.
        if (objects) snapshot.Viewport = snapshot.Viewport.Inflate(1);
        var resources = ResourceSnapshot(snapshot).ToList();
        _documentBusy = true;
        try
        {
            string? captureWarning = !objects && snapshot.Kind == DocumentKind.FrozenCapture ? _documentFrozenCaptureWarning : null;
            if (!objects && snapshot.Kind == DocumentKind.LiveOverlay)
            {
                var capture = await CaptureCleanDesktopAsync(); var assets = new AssetLibrary(); string id = assets.Add(capture.PngBytes);
                captureWarning = CaptureWarning(capture);
                resources.Add(assets.Get(id)); snapshot.Background = Background(capture, id);
            }
            var encoded = await Task.Run(() =>
            {
                var assets = ResourceLibrary(resources); using var renderer = new SceneRenderer();
                byte[] png = renderer.ExportPng(snapshot, assets, objects);
                string? internalObjects = objects ? JsonSerializer.Serialize(new ClipboardObjects
                {
                    Document = snapshot, Assets = resources.Select(r => new ClipboardAsset { Id = r.Id, MediaType = r.MediaType, Bytes = r.Data.ToArray() }).ToList()
                }) : null;
                return (png, internalObjects);
            }, _documentLifetime.Token);
            var data = new Wpf.DataObject();
            using var pngStream = new MemoryStream(encoded.png, writable: false);
            var decoder = new PngBitmapDecoder(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = decoder.Frames[0]; bitmap.Freeze();
            data.SetData("PNG", new MemoryStream(encoded.png, writable: false));
            data.SetData(Wpf.DataFormats.Bitmap, bitmap);
            if (encoded.internalObjects is not null) data.SetData(ObjectClipboardFormat, encoded.internalObjects);
            Wpf.Clipboard.SetDataObject(data, copy: true);
            UpdateStatus(WithCaptureWarning(objects ? L.Get("Clipboard.CopiedObjects") : L.Get("Clipboard.CopiedImage"), captureWarning));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Clipboard.CopyFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); }
    }

    private async Task PasteAsync()
    {
        if (DocumentOperationBusy) return;
        _editor.Cancel(); var target = Active;
        _documentBusy = true;
        try
        {
            var data = Wpf.Clipboard.GetDataObject();
            if (data is null) { UpdateStatus(L.Get("Clipboard.Empty")); return; }
            if (data.GetDataPresent(ObjectClipboardFormat))
            {
                string? json = data.GetData(ObjectClipboardFormat) as string;
                if (json is null || json.Length > 256 * 1024 * 1024) throw new InvalidDataException(L.Get("Clipboard.InvalidObjects"));
                var payload = await Task.Run(() =>
                {
                    var objects = JsonSerializer.Deserialize<ClipboardObjects>(json) ?? throw new InvalidDataException(L.Get("Clipboard.NullObjects"));
                    if (objects.Version != 1 || objects.Assets is null) throw new InvalidDataException(L.Get("Clipboard.InvalidVersion"));
                    SceneValidation.Validate(objects.Document);
                    var assets = new AssetLibrary();
                    foreach (var asset in objects.Assets)
                        if (asset.Bytes is null || assets.Add(asset.Bytes, asset.MediaType) != asset.Id) throw new InvalidDataException(L.Get("Clipboard.InvalidAsset"));
                    foreach (string id in objects.Document.ReferencedAssets) _ = assets.Get(id);
                    return (objects.Document, assets);
                }, _documentLifetime.Token);
                if (!ReferenceEquals(Active, target)) return;
                MergeAssets(payload.assets);
                var clones = payload.Document.Elements.Select(e => e.Clone(true)).ToArray();
                target.Execute(L.Get("History.Paste"), d =>
                {
                    target.Selection.Clear();
                    foreach (var clone in clones) { clone.Locked = false; clone.Transform = Affine2D.Translation(16, 16).Compose(clone.Transform); d.Elements.Add(clone); target.Selection.Add(clone.Id); if (clone.Kind == ElementKind.Number) d.NextNumber = Math.Max(d.NextNumber, clone.Number + 1); }
                    BringSelectionIntoView(target);
                });
                _editor.Tool = EditorTool.Select; return;
            }
            byte[]? bytes = ReadClipboardPng(data);
            if (bytes is not null)
            {
                var normalized = await Task.Run(() => AssetImport.Normalize(bytes), _documentLifetime.Token);
                if (ReferenceEquals(Active, target)) { InsertImages(target, [normalized], false); _editor.Tool = EditorTool.Select; }
            }
            else if (data.GetDataPresent(Wpf.DataFormats.FileDrop) && data.GetData(Wpf.DataFormats.FileDrop) is string[] paths)
            {
                _documentBusy = false; await ImportImagesAsync(paths);
            }
            else UpdateStatus(L.Get("Clipboard.Empty"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Clipboard.PasteFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); }
    }
    private static byte[]? ReadClipboardPng(Wpf.IDataObject data)
    {
        if (data.GetDataPresent("PNG"))
        {
            object? value = data.GetData("PNG");
            if (value is byte[] bytes) return bytes;
            if (value is Stream stream) { using var copy = new MemoryStream(); if (stream.CanSeek) stream.Position = 0; stream.CopyTo(copy); return copy.ToArray(); }
        }
        BitmapSource? bitmap = data.GetData(Wpf.DataFormats.Bitmap, autoConvert: true) as BitmapSource;
        if (bitmap is null && Wpf.Clipboard.ContainsImage()) bitmap = Wpf.Clipboard.GetImage();
        if (bitmap is null) return null;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    private async Task ImportImagesAsync(string[]? paths = null, bool replaceSelected = false)
    {
        if (DocumentOperationBusy) return;
        _editor.Cancel(); var target = Active;
        _documentBusy = true;
        try
        {
        if (paths is null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = replaceSelected ? L.Get("Image.ReplaceTitle") : L.Get("Image.ImportTitle"), Filter = L.Get("Image.Filter"), Multiselect = !replaceSelected };
            if (dialog.ShowDialog(_toolbar) != true) return; paths = dialog.FileNames;
        }
        if (paths.Length == 0) return;
        if (replaceSelected && target.SelectedElements().Count(e => e.Kind == ElementKind.Image && !e.Locked) != 1)
        { UpdateStatus(L.Get("Image.SelectFirst")); return; }
        var selectedIds = target.Selection.ToHashSet();
            var normalized = await Task.Run(async () =>
            {
                var images = new List<NormalizedImage>();
                foreach (string path in paths) images.Add(AssetImport.Normalize(await File.ReadAllBytesAsync(path, _documentLifetime.Token)));
                return images;
            }, _documentLifetime.Token);
            if (!ReferenceEquals(Active, target) || replaceSelected && !target.Selection.SetEquals(selectedIds))
            { UpdateStatus(L.Get("Image.ImportChanged")); return; }
            InsertImages(target, normalized, replaceSelected); _editor.Tool = EditorTool.Select;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Image.ImportFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); }
    }
    private void InsertImages(EditorSession target, IReadOnlyList<NormalizedImage> images, bool replace)
    {
        var resources = images.Select(image => (Image: image, Id: _assets.Add(image.PngBytes))).ToArray();
        var visible = VisibleDocumentArea();
        target.Execute(replace ? L.Get("History.ReplaceImage") : L.Get("Image.ImportTitle"), d =>
        {
            if (replace)
            {
                var image = target.SelectedElements().Single(e => e.Kind == ElementKind.Image && !e.Locked);
                image.AssetId = resources[0].Id; // Preserve object frame, crop and normalized effects.
                return;
            }
            target.Selection.Clear(); int index = 0;
            foreach (var resource in resources)
            {
                double scale = Math.Min(1, Math.Min(visible.Width * .8 / resource.Image.Width, visible.Height * .8 / resource.Image.Height));
                double width = resource.Image.Width * scale, height = resource.Image.Height * scale;
                var element = new CanvasElement { Kind = ElementKind.Image, AssetId = resource.Id, Width = width, Height = height,
                    Transform = Affine2D.Translation(visible.Center.X - width / 2 + index * 20, visible.Center.Y - height / 2 + index * 20) };
                d.Elements.Add(element); target.Selection.Add(element.Id); index++;
            }
        });
        UpdateStatus(replace ? L.Get("Image.Replaced") : L.Format("Image.Imported", images.Count));
    }

    private async Task StartCaptureAsync(bool region)
    {
        if (DocumentOperationBusy) return;
        _documentBusy = true;
        try
        {
            _editor.Cancel();
            if (_capture is not null && !await ConfirmReplaceAsync(_capture, L.Get("Capture.ReplaceDraft"))) return;
            ReturnToLive();
            _documentLiveTool = _editor.Tool;
            var source = _live.Document.Clone();
            long focusVersion = _host.ForegroundLossVersion;
            var captured = await CaptureCleanDesktopAsync();
            string assetId = _assets.Add(captured.PngBytes);
            var b = captured.Bounds; var bounds = new RectD(b.Left, b.Top, b.Width, b.Height);
            var snapshot = source.CreateCapture(bounds, assetId);
            if (_capture is not null) _documentFiles.Remove(_capture);
            _capture = new(snapshot, _assets); _captureOrigin = new(b.Left, b.Top);
            _documentFrozenCaptureWarning = CaptureWarning(captured);
            _editor.ReplaceSession(_capture, _captureOrigin); _editor.Tool = region ? EditorTool.CaptureRegion : EditorTool.Select;
            bool editing;
            if (focusVersion != _host.ForegroundLossVersion)
            {
                SetEditing(false); editing = false;
                UpdateStatus(L.Get("Capture.FocusLost"));
            }
            else editing = SetEditing(true);
            ScheduleRecovery(); RequestRender();
            if (editing) UpdateStatus(WithCaptureWarning(region ? L.Get("Capture.SelectRegion") :
                _host.Scope == OverlayMonitorScope.PrimaryScreen ? L.Get("Capture.PrimaryFrozen") : L.Get("Capture.AllFrozen"), _documentFrozenCaptureWarning));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Capture.Failed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); }
    }
    private void FinishCaptureRegion(RectD region)
    {
        if (_capture is null || !ReferenceEquals(Active, _capture)) return;
        var bounds = _capture.Document.Viewport.Intersect(region.Normalize());
        if (bounds.Width < 1 || bounds.Height < 1) return;
        _capture.Execute(L.Get("History.CaptureRegion"), d =>
        {
            d.Viewport = bounds;
            d.Elements.RemoveAll(element => !ElementGeometry.Bounds(element).Intersects(bounds));
        });
        _editor.Tool = EditorTool.Select; RequestRender(); UpdateStatus(WithCaptureWarning(L.Get("Capture.RegionSelected"), _documentFrozenCaptureWarning));
    }
    private void ReturnToLive()
    {
        if (ReferenceEquals(Active, _live)) return;
        _editor.Cancel(); _editor.ReplaceSession(_live, new(0, 0)); _editor.Tool = _documentLiveTool;
        ScheduleRecovery(); RequestRender(); UpdateStatus(L.Get("Capture.ReturnedLive"));
    }
    private void ResumeCapture()
    {
        if (_documentBusy || _capture is null) { UpdateStatus(L.Get("Capture.NoDraft")); return; }
        if (ReferenceEquals(Active, _live)) _documentLiveTool = _editor.Tool;
        _editor.Cancel();
        if (EnsureCaptureVisible()) ScheduleRecovery();
        _editor.ReplaceSession(_capture, _captureOrigin); _editor.Tool = EditorTool.Select;
        bool editing = SetEditing(true); RequestRender();
        if (editing) UpdateStatus(WithCaptureWarning(L.Get("Capture.Resumed"), _documentFrozenCaptureWarning));
    }
    private async Task<CapturedDesktop> CaptureCleanDesktopAsync()
    {
        // WPF Popup/ContextMenu/ToolTip HWNDs are not members of Application.Windows.
        // Include their presentation sources so transient editor chrome cannot enter a capture.
        var ownWindows = PresentationSource.CurrentSources.OfType<HwndSource>()
            .Where(source => source.RootVisual is UIElement { IsVisible: true }).Select(source => source.Handle)
            .Concat(_app.Windows.Cast<Window>().Where(w => w.IsVisible).Select(w => new WindowInteropHelper(w).Handle))
            .Where(h => h != 0).Distinct().ToArray();
        using var scope = _host.HideForCapture(ownWindows);
        var captured = await _documentCaptureService.CaptureAsync(_host.Monitors, scope.NotBeforeTimestamp, _documentLifetime.Token);
        if (CaptureWarning(captured) is { } warning) UpdateStatus(warning);
        return captured;
    }
    private static string? CaptureWarning(CapturedDesktop capture) => capture.Outputs.Any(output => output.ProtectedContentMaskedOut)
        ? L.Get("Capture.ProtectedRegions") : null;
    private static string WithCaptureWarning(string message, string? warning) => warning is null ? message : message + " " + warning;
    private static CanvasElement Background(CapturedDesktop capture, string assetId) => new()
    {
        Kind = ElementKind.Image, AssetId = assetId, Width = capture.Bounds.Width, Height = capture.Bounds.Height,
        Transform = Affine2D.Translation(capture.Bounds.Left, capture.Bounds.Top), Locked = true
    };
    private MonitorInfo CurrentMonitor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        return _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new(cursor.X, cursor.Y))) ?? _host.PrimaryMonitor;
    }
    private RectD VisibleDocumentArea()
    {
        var b = CurrentMonitor().WorkArea;
        var visible = new RectD(b.Left - _editor.ScreenOrigin.X, b.Top - _editor.ScreenOrigin.Y, b.Width, b.Height).Intersect(Active.Document.Viewport);
        return visible.IsEmpty ? Active.Document.Viewport : visible;
    }
    private PointD CenteredOrigin(RectD viewport)
    {
        return CaptureViewportPlacement.CenterOrigin(viewport, CurrentMonitor().Bounds);
    }
    private bool EnsureCaptureVisible()
    {
        if (_capture is null) return false;
        var origin = CaptureViewportPlacement.ResolveOrigin(_capture.Document.Viewport, _captureOrigin, _host.Monitors);
        if (origin == _captureOrigin) return false;
        _captureOrigin = origin;
        if (ReferenceEquals(Active, _capture)) _editor.ScreenOrigin = origin;
        return true;
    }
    private void BringSelectionIntoView(EditorSession session)
    {
        if (session.SelectionBounds() is not { } bounds) return;
        var visible = VisibleDocumentArea();
        if (bounds.Intersects(visible)) return;
        var delta = Affine2D.Translation(visible.Center.X - bounds.Center.X, visible.Center.Y - bounds.Center.Y);
        foreach (var element in session.SelectedElements()) element.Transform = delta.Compose(element.Transform);
    }
}
