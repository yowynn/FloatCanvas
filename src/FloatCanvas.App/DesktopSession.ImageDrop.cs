using L = FloatCanvas.Localization.AppText;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using OleDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private ImageDropRequest? _imageDropRequest;
    private DispatcherTimer? _imageDropTimer;
    private PointD _imageDropOrigin;
    private long? _imageDropReleasedAt;

    private void ToggleEditing()
    {
        if (_host.IsReceivingDrop) { CloseImageDrop(); RequestRender(); return; }
        if (_host.IsEditing) { SetEditing(false); return; }
        if (DocumentOperationBusy) return;
        if (OverlayHost.IsPrimaryMouseButtonDown) BeginCanvasDropReception();
        else SetEditing(true);
    }

    private void BeginCanvasDropReception()
    {
        if (!CanAddCanvasContent()) return;
        CloseImageDrop(); _editor.Cancel();
        _imageDropOrigin = _editor.ScreenOrigin;
        _imageDropRequest = new(Active, VisibleDocumentArea(), _documentLifetime.Token);
        _toolbar.Hide(); _inspector?.Hide(); _textEditor?.Hide();
        // The source owns the mouse and its OLE loop until Drop returns. Showing a
        // target must not activate a window or acquire the ordinary keyboard lease.
        if (!_host.TryStartDropReception(out var failure))
        {
            CloseImageDrop(); UpdateStatus(failure ?? L.Get("ImageDrop.Unsupported")); return;
        }
        _imageDropReleasedAt = null;
        _imageDropTimer = new DispatcherTimer(DispatcherPriority.Background, _app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _imageDropTimer.Tick += OnImageDropTick;
        _imageDropTimer.Start();
        RequestRender();
    }

    private void OnImageDropTick(object? sender, EventArgs e)
    {
        if (_disposed || !_host.IsReceivingDrop || _imageDropRequest is not { IsImporting: false } request ||
            !request.IsCurrent(_imageDropRequest, _live, _capture))
        { CloseImageDrop(); RequestRender(); return; }
        if (GetDropKeyState(0x1B) < 0) { CloseImageDrop(); RequestRender(); return; }
        if (OverlayHost.IsPrimaryMouseButtonDown) { _imageDropReleasedAt = null; return; }
        _imageDropReleasedAt ??= Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_imageDropReleasedAt.Value);
        // A timer can observe the physical release before OLE delivers Drop. Give
        // that callback precedence; also release an abandoned/unsupported drag.
        if (elapsed < TimeSpan.FromMilliseconds(_host.IsOleDragOver ? 1000 : 250)) return;
        CloseImageDrop(); RequestRender();
    }

    private bool SupportsCanvasImageDrop(OleDataObject data) => !_disposed && _host.IsVisible && !DocumentOperationBusy &&
        (_host.IsEditing || _host.IsReceivingDrop) && ImageDropDataReader.Supports(data);

    private bool ReceiveCanvasImageDrop(OleDataObject data, OverlayPoint point)
    {
        if (_disposed || !_host.IsVisible || DocumentOperationBusy || (!_host.IsEditing && !_host.IsReceivingDrop)) return false;
        _editor.Cancel();
        var request = _imageDropRequest;
        if (request is null)
        {
            _imageDropOrigin = _editor.ScreenOrigin;
            request = _imageDropRequest = new(Active, VisibleDocumentArea(), _documentLifetime.Token);
        }
        if (!request.IsCurrent(_imageDropRequest, _live, _capture) ||
            !request.TrySetDropPoint(new(point.X, point.Y), _imageDropOrigin)) return false;
        StopImageDropTimer();
        request.IsImporting = true; _documentBusy = true;
        nint sourceForeground = GetToolbarForegroundWindow();
        try
        {
            // Copy delayed/virtual OLE data while the source still owns its medium.
            // No COM data object, stream or borrowed native handle crosses await.
            var payload = ImageDropDataReader.Read(data);
            if (!request.IsCurrent(_imageDropRequest, _live, _capture))
            { FinishImageDrop(request); return false; }
            _app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _ = ImportCanvasDropAsync(request, payload, sourceForeground)));
            return true;
        }
        catch (Exception ex)
        {
            FinishImageDrop(request);
            // Never show a modal window or activate the editor from an OLE callback.
            _app.Dispatcher.BeginInvoke(() => ReportImageDropFailure(ex.Message));
            return false;
        }
    }

    private async Task ImportCanvasDropAsync(ImageDropRequest request, ImageDropPayload payload, nint sourceForeground)
    {
        try
        {
            if (_disposed || !_host.IsVisible || !request.IsCurrent(_imageDropRequest, _live, _capture) ||
                GetToolbarForegroundWindow() != sourceForeground) return;
            _host.EndDropReception();
            // This runs only after the native Drop callback has returned to OLE.
            if (!SetEditing(true, preserveImageDrop: true)) { request.Cancel(); return; }
            UpdateStatus(L.Get("ImageDrop.Importing"));
            var images = await NormalizeCanvasDropAsync(payload, request.Token);
            if (_disposed || _documentStopped || !_host.IsVisible || !request.TryCommit(_imageDropRequest, _live, _capture, images)) return;
            if (ReferenceEquals(Active, request.Target)) _editor.Tool = EditorTool.Select;
            ScheduleRecovery(); RequestRender(); UpdateStatus(L.Format("ImageDrop.Imported", images.Count));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && ReferenceEquals(_imageDropRequest, request)) ReportImageDropFailure(ex.Message); }
        finally { FinishImageDrop(request); }
    }

    internal static Task<IReadOnlyList<NormalizedImage>> NormalizeCanvasDropAsync(ImageDropPayload payload, CancellationToken token) => Task.Run(async () =>
    {
        const long maximumImageBytes = 64L * 1024 * 1024, maximumTotalBytes = 128L * 1024 * 1024;
        var images = new List<NormalizedImage>(); long total = 0;
        foreach (string path in payload.FilePaths)
        {
            token.ThrowIfCancellationRequested();
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            if (source.Length > maximumImageBytes || source.Length > maximumTotalBytes - total)
                throw new InvalidDataException(L.Get("ImageDrop.Unsupported"));
            using var copy = new MemoryStream(); var buffer = new byte[65536];
            int read;
            while ((read = await source.ReadAsync(buffer, token)) != 0)
            {
                if (read > maximumImageBytes - copy.Length || read > maximumTotalBytes - total)
                    throw new InvalidDataException(L.Get("ImageDrop.Unsupported"));
                copy.Write(buffer, 0, read); total += read;
            }
            images.Add(AssetImport.Normalize(copy.ToArray()));
        }
        foreach (var bytes in payload.EncodedImages)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.LongLength > maximumImageBytes || bytes.LongLength > maximumTotalBytes - total)
                throw new InvalidDataException(L.Get("ImageDrop.Unsupported"));
            total += bytes.LongLength; images.Add(AssetImport.Normalize(bytes));
        }
        token.ThrowIfCancellationRequested();
        return (IReadOnlyList<NormalizedImage>)images;
    }, token);

    private void FinishImageDrop(ImageDropRequest request)
    {
        request.IsImporting = false;
        if (ReferenceEquals(_imageDropRequest, request)) CloseImageDrop();
        else request.Dispose();
        if (_disposed) _documentBusyValue = false;
        else { _documentBusy = false; PruneDocumentResources(); RequestRender(); }
    }

    private void CloseImageDrop()
    {
        StopImageDropTimer();
        var request = _imageDropRequest; _imageDropRequest = null;
        request?.Cancel();
        _host.EndDropReception();
        if (request is { IsImporting: false }) request.Dispose();
    }

    private void StopImageDropTimer()
    {
        if (_imageDropTimer is not null) { _imageDropTimer.Stop(); _imageDropTimer.Tick -= OnImageDropTick; _imageDropTimer = null; }
        _imageDropReleasedAt = null;
    }

    private void ReportImageDropFailure(string reason)
    {
        if (_disposed) return;
        string message = L.Format("ImageDrop.Failed", reason);
        UpdateStatus(message);
        if (!_host.IsEditing) _tray.ShowBalloonTip(4000, L.Get("App.Name"), message, System.Windows.Forms.ToolTipIcon.Warning);
        RequestRender();
    }

    private void DrawImageDropHint(SKCanvas canvas)
    {
        var monitor = _host.PrimaryMonitor;
        float scale = monitor.DpiX / 96f;
        using var typeface = SKTypeface.FromFamilyName(L.CurrentLanguage == "zh-CN" ? "Microsoft YaHei UI" : "Segoe UI");
        using var font = new SKFont(typeface, 14 * scale);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(30, 30, 32, 240) };
        string message = L.Format("ImageDrop.Hint", FormatHotkey(_settings.Toggle));
        float width = font.MeasureText(message) + 32 * scale, height = 42 * scale;
        float x = (float)(monitor.WorkArea.Left + monitor.WorkArea.Width / 2) - width / 2;
        float y = (float)monitor.WorkArea.Bottom - height - 24 * scale;
        canvas.DrawRoundRect(x, y, width, height, 8 * scale, 8 * scale, paint);
        paint.Color = SKColors.White;
        canvas.DrawText(message, x + 16 * scale, y + 27 * scale, SKTextAlign.Left, font, paint);
    }

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetDropKeyState(int key);
}
