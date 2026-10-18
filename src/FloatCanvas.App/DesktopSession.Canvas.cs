using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly CanvasCalibrationController _canvasCalibration = new();
    private CanvasSettingsWindow? _canvasSettingsWindow;
    private EditorSession? _calibrationSession;

    private void ToggleCanvasSettings()
    {
        if (DocumentOperationBusy || !_host.IsEditing) return;
        if (_canvasSettingsWindow?.IsVisible == true) { EndCanvasCalibration(hidePanel: true); return; }
        _editor.Cancel(); CloseToolOptions();
        if (_canvasSettingsWindow is null)
        {
            var window = _canvasSettingsWindow = new() { Owner = _toolbar };
            ConfigureAppWindow(window);
            window.BackgroundChanged += color => ChangeCanvas(c => c.BackgroundColor = color);
            window.ProjectionChanged += SetCanvasProjection;
            window.GridCountsChanged += SetCanvasGridCounts;
            window.CalibrationToggled += () =>
            {
                if (_canvasCalibration.IsActive) EndCanvasCalibration(); else BeginCanvasCalibration();
            };
            window.ResetRequested += ResetCanvasProjection;
            window.GridVisibilityChanged += visible => ChangeCanvas(c =>
            {
                EnsureCanvasReference(c); c.Grid.Visible = visible;
            });
            window.GridColorChanged += color => ChangeCanvas(c => c.Grid.Color = color);
            window.GridWidthChanged += width => ChangeCanvas(c => c.Grid.Width = width);
            window.GridPatternChanged += pattern => ChangeCanvas(c => c.Grid.Pattern = pattern);
            window.PresetApplied += ApplyCanvasPreset;
            window.Dismissed += () => EndCanvasCalibration();
            _canvasCalibration.Changed += () => { RequestRender(); ScheduleRecovery(); };
            _canvasCalibration.Error += key => UpdateStatus(L.Get(key));
        }
        _canvasSettingsWindow.Refresh(Active.Document.Canvas, _canvasCalibration.IsActive);
        ShowAppWindow(_canvasSettingsWindow, true);
    }

    private RectD CanvasReferenceViewport()
    {
        var point = _toolbar.PointToScreen(new(0, 0));
        var monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new(point.X, point.Y))) ?? _host.PrimaryMonitor;
        var bounds = new RectD(monitor.Bounds.Left - _editor.ScreenOrigin.X, monitor.Bounds.Top - _editor.ScreenOrigin.Y,
            monitor.Bounds.Width, monitor.Bounds.Height).Intersect(Active.Document.Viewport);
        return bounds.IsEmpty ? Active.Document.Viewport : bounds;
    }

    private void EnsureCanvasReference(CanvasSettings settings)
    {
        if (settings.Projection.Quad.Count == 4) return;
        settings.Projection = CanvasCalibration.CreateInitial(CanvasReferenceViewport(), settings.Projection.Columns, settings.Projection.Rows);
    }

    private void ChangeCanvas(Action<CanvasSettings> change)
    {
        if (DocumentOperationBusy || !_host.IsVisible || !_host.IsEditing) return;
        _canvasCalibration.CancelDrag(); _editor.Cancel();
        CancelSnapshotCapture("CanvasSettingsChanged"); CancelLivePreparation();
        Active.Execute(L.Get("Canvas.Title"), document => change(document.Canvas));
        RequestRender();
    }

    private void SetCanvasProjection(bool enabled)
    {
        if (!enabled) EndCanvasCalibration();
        ChangeCanvas(settings =>
        {
            EnsureCanvasReference(settings);
            if (enabled && !CanvasCalibration.IsStableViewport(settings.Projection.Matrix, Active.Document.Viewport))
            { UpdateStatus(L.Get("Canvas.UnstableProjection")); return; }
            settings.Projection.Enabled = enabled;
        });
    }

    private void ApplyCanvasPreset(CanvasPresetParameters parameters)
    {
        if (DocumentOperationBusy || !_host.IsVisible || !_host.IsEditing) return;
        CanvasSettings preset;
        try
        {
            preset = CanvasPresetLibrary.Apply(parameters, Active.Document.Canvas,
                CanvasReferenceViewport(), Active.Document.Viewport);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or System.IO.InvalidDataException)
        {
            UpdateStatus(ex.Message);
            _canvasSettingsWindow?.Refresh(Active.Document.Canvas, _canvasCalibration.IsActive);
            return;
        }
        EndCanvasCalibration();
        ChangeCanvas(settings =>
        {
            settings.BackgroundColor = preset.BackgroundColor;
            settings.Grid = preset.Grid;
            settings.Projection = preset.Projection;
        });
    }

    private void SetCanvasGridCounts(int columns, int rows)
    {
        ChangeCanvas(settings =>
        {
            var original = settings.Projection;
            if (original.Columns == columns && original.Rows == rows) return;
            if (original.Quad.Count == 0)
            { settings.Projection = CanvasCalibration.CreateInitial(CanvasReferenceViewport(), columns, rows); return; }
            var draft = original.Clone(); draft.Columns = columns; draft.Rows = rows;
            if (CanvasCalibration.TryCalibrate(draft, original.Quad, Active.Document.Viewport, out var calibrated, out var error))
            { calibrated.Enabled = original.Enabled; settings.Projection = calibrated; }
            else UpdateStatus(L.Get(error!));
        });
    }

    private void ResetCanvasProjection()
    {
        ChangeCanvas(settings =>
        {
            var old = settings.Projection;
            settings.Projection = CanvasCalibration.CreateInitial(CanvasReferenceViewport(), old.Columns, old.Rows);
            settings.Projection.Enabled = old.Enabled;
        });
    }

    private void BeginCanvasCalibration()
    {
        if (DocumentOperationBusy || !_host.IsVisible || !_host.IsEditing) return;
        _editor.Cancel(); CloseImageDrop(); CancelSnapshotCapture("CanvasCalibration"); CancelLivePreparation();
        SetCanvasProjection(true);
        if (!Active.Document.Canvas.Projection.Enabled) return;
        _calibrationSession = Active;
        _canvasCalibration.Begin(Active);
        UpdateStatus(L.Get("Canvas.HelpText"));
        RequestRender();
    }

    private void EndCanvasCalibration(bool hidePanel = false)
    {
        _calibrationSession = null;
        _canvasCalibration.End();
        if (hidePanel && _canvasSettingsWindow is not null) SessionWindowCoordinator.Hide(_canvasSettingsWindow);
    }

    private void ValidateCanvasCalibrationSession()
    {
        if (_calibrationSession is not null && !ReferenceEquals(_calibrationSession, Active)) EndCanvasCalibration(hidePanel: true);
    }

    private bool HandleCalibrationPointer(OverlayPointerEvent e)
    {
        if (!_canvasCalibration.IsActive) return false;
        var point = new PointD(e.Position.X - _editor.ScreenOrigin.X, e.Position.Y - _editor.ScreenOrigin.Y);
        if (e.Kind == OverlayPointerKind.Cancel) _canvasCalibration.CancelDrag();
        else if (e.Kind == OverlayPointerKind.Down && e.Button == OverlayMouseButton.Left) _canvasCalibration.Down(point);
        else if (e.Kind == OverlayPointerKind.Move) _canvasCalibration.Move(point);
        else if (e.Kind == OverlayPointerKind.Up && e.Button == OverlayMouseButton.Left)
        { _canvasCalibration.Move(point); _canvasCalibration.Up(); }
        return true;
    }

    private void DrawCanvasCalibration(SKCanvas canvas)
    {
        if (!_canvasCalibration.IsActive) return;
        var settings = Active.Document.Canvas.Projection;
        if (settings.Quad.Count != 4) return;
        using var line = new SKPaint { Color = SKColors.Black.WithAlpha(210), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
        DrawScreenPolygon(canvas, settings.Quad, line);
        line.Color = new(35,131,226); line.StrokeWidth = 1.5f;
        DrawScreenPolygon(canvas, settings.Quad, line);
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        foreach (var p in settings.Quad)
        {
            canvas.DrawCircle((float)p.X, (float)p.Y, 7, fill);
            canvas.DrawCircle((float)p.X, (float)p.Y, 7, line);
        }
        for (int i = 0; i < 4; i++)
        {
            var p = (settings.Quad[i] + settings.Quad[(i+1)%4]) * .5;
            canvas.DrawRect((float)p.X-4,(float)p.Y-4,8,8,fill);
            canvas.DrawRect((float)p.X-4,(float)p.Y-4,8,8,line);
        }
    }

    private static void DrawScreenPolygon(SKCanvas canvas, IReadOnlyList<PointD> points, SKPaint paint)
    {
        if (points.Count == 0) return;
        using var builder = new SKPathBuilder(); builder.MoveTo((float)points[0].X,(float)points[0].Y);
        foreach (var point in points.Skip(1)) builder.LineTo((float)point.X,(float)point.Y);
        builder.Close(); using var path = builder.Detach(); canvas.DrawPath(path,paint);
    }
}
