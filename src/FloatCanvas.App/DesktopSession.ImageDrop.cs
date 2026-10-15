using L = FloatCanvas.Localization.AppText;
using System.Windows;
using System.Windows.Interop;
using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private ImageDropRequest? _imageDropRequest;
    private DropImportWindow? _imageDropWindow;

    private void BeginImageDrop()
    {
        if (DocumentOperationBusy) return;
        var target = Active; var placement = VisibleDocumentArea(); var monitor = CurrentMonitor();
        SetEditing(false);
        var request = new ImageDropRequest(target, placement, _documentLifetime.Token);
        var window = new DropImportWindow(L.Get(target.Document.Kind == DocumentKind.FrozenCapture ? "ImageDrop.Capture" : "ImageDrop.Live"));
        _imageDropRequest = request; _imageDropWindow = window;
        window.FilesDropped += paths => _ = ImportDroppedImagesAsync(request, window, paths);
        window.ResumeRequested += () => ResumeImageDropTarget(request, window);
        window.Closed += (_, _) => { if (ReferenceEquals(_imageDropRequest, request)) CloseImageDrop(false); };
        window.Loaded += (_, _) => OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(window).Handle,
            new(monitor.WorkArea.Left + Math.Max(8, (monitor.WorkArea.Width - window.ActualWidth * monitor.DpiX / 96) / 2), monitor.WorkArea.Top + 72));
        window.Show();
    }

    private void CloseImageDrop(bool closeWindow = true)
    {
        var request = _imageDropRequest; var window = _imageDropWindow;
        _imageDropRequest = null; _imageDropWindow = null;
        request?.Cancel();
        if (closeWindow) window?.Close();
        if (request is { IsImporting: false }) request.Dispose();
    }

    private async Task ImportDroppedImagesAsync(ImageDropRequest request, DropImportWindow window, string[] paths)
    {
        if (DocumentOperationBusy) { window.ShowFailure(L.Get("Desktop.Busy")); return; }
        if (_disposed || !request.IsCurrent(_imageDropRequest, _live, _capture)) { window.ShowFailure(L.Get("ImageDrop.TargetChanged")); return; }
        request.IsImporting = true; _documentBusy = true; window.SetBusy();
        try
        {
            var images = await ImageImportCommand.NormalizeFilesAsync(paths, request.Token);
            if (_disposed || _documentStopped || !request.TryCommit(_imageDropRequest, _live, _capture, images))
            {
                if (!_disposed && ReferenceEquals(_imageDropRequest, request)) window.ShowFailure(L.Get("ImageDrop.TargetChanged"));
                return;
            }
            if (ReferenceEquals(Active, request.Target)) _editor.Tool = EditorTool.Select;
            // ReplaceSession detached Changed from an inactive capture; it still needs recovery.
            ScheduleRecovery(); RequestRender(); window.Complete(images.Count);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_imageDropRequest, request)) window.ShowFailure(L.Format("ImageDrop.Failed", ex.Message));
        }
        finally
        {
            request.IsImporting = false;
            if (_disposed) _documentBusyValue = false;
            else { _documentBusy = false; PruneDocumentResources(); }
            if (!ReferenceEquals(_imageDropRequest, request)) request.Dispose();
        }
    }

    private void ResumeImageDropTarget(ImageDropRequest request, DropImportWindow window)
    {
        if (DocumentOperationBusy) return;
        if (!request.IsCurrent(_imageDropRequest, _live, _capture)) { window.ShowFailure(L.Get("ImageDrop.TargetChanged")); return; }
        var target = request.Target;
        CloseImageDrop();
        if (ReferenceEquals(target, _capture)) ResumeCapture();
        else { ReturnToLive(); SetEditing(true); }
    }
}
