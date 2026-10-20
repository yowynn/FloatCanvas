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
            window.CommandKey += OnKey;
            window.BackgroundChanged += color => ChangeCanvas(c => c.BackgroundColor = color);
            window.ProjectionChanged += SetCanvasProjection;
            window.GridCountsChanged += SetCanvasGridCounts;
            window.GridSizeLockChanged += SetCanvasGridSizeLock;
            window.CalibrationToggled += () =>
            {
                if (_canvasCalibration.IsActive) EndCanvasCalibration(); else BeginCanvasCalibration();
            };
            window.ResetDisplayRequested += ResetCanvasProjectionOnDisplay;
            window.FourPointsRequested += BeginCanvasFourPoints;
            window.GridVisibilityChanged += visible => ChangeCanvas(c =>
            {
                EnsureCanvasReference(c); c.Grid.Visible = visible;
            });
            window.GridColorChanged += color => ChangeCanvas(c => c.Grid.Color = color);
            window.GridWidthChanged += width => ChangeCanvas(c => c.Grid.Width = width);
            window.GridPatternChanged += pattern => ChangeCanvas(c => c.Grid.Pattern = pattern);
            window.PresetApplied += ApplyCanvasPreset;
            window.Dismissed += () => EndCanvasCalibration();
            _canvasCalibration.Changed += () =>
            {
                window.RefreshGridCounts(Active.Document.Canvas.Projection);
                RequestRender(); ScheduleRecovery();
            };
            _canvasCalibration.Error += error => UpdateStatus(L.Get(_canvasCalibration.IsPlacing ? error : "Canvas.DragBoundary"));
        }
        RefreshCanvasReferenceMonitors();
        _canvasSettingsWindow.Refresh(Active.Document.Canvas, _canvasCalibration.IsActive);
        ShowAppWindow(_canvasSettingsWindow, true);
    }

    private RectD CanvasReferenceViewport()
    {
        try { NormalizeCanvasResetDisplay(_settings, _host.Monitors); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { UpdateStatus(ex.Message); }
        return ResolveCanvasReferenceBounds(_host.Monitors, _settings.CanvasReferenceDisplay,
            default, _editor.ScreenOrigin, Active.Document.Viewport);
    }

    private void RefreshCanvasReferenceMonitors()
    {
        try { NormalizeCanvasResetDisplay(_settings, _host.Monitors); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { UpdateStatus(ex.Message); }
        _canvasSettingsWindow?.SetReferenceMonitors(_host.Monitors, _settings.CanvasReferenceDisplay);
    }

    private void ResetCanvasProjectionOnDisplay(string? device)
    {
        if (DocumentOperationBusy || !_host.IsVisible || !_host.IsEditing) return;
        if (_host.Monitors.Count <= 1 || !_host.Monitors.Any(m => m.DeviceName == device)) device = null;
        try { RememberCanvasReferenceDisplay(_settings, device); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { UpdateStatus(ex.Message); RefreshCanvasReferenceMonitors(); return; }
        ResetCanvasProjection();
        RefreshCanvasReferenceMonitors();
    }

    internal static void NormalizeCanvasResetDisplay(UserSettings settings, IReadOnlyList<MonitorInfo> monitors)
    {
        if (settings.CanvasReferenceDisplay is not null && (monitors.Count <= 1 || !monitors.Any(m => m.DeviceName == settings.CanvasReferenceDisplay)))
            RememberCanvasReferenceDisplay(settings, null);
    }

    internal static void RememberCanvasReferenceDisplay(UserSettings settings, string? device)
    {
        if (device is not null) ArgumentException.ThrowIfNullOrWhiteSpace(device);
        if (settings.CanvasReferenceDisplay == device) return;
        var previous = settings.CanvasReferenceDisplay;
        settings.CanvasReferenceDisplay = device;
        try { settings.Save(); }
        catch { settings.CanvasReferenceDisplay = previous; throw; }
    }

    internal static RectD ResolveCanvasReferenceBounds(IReadOnlyList<MonitorInfo> monitors, string? selectedDevice,
        PointD fallback, PointD sourceOrigin, RectD viewport)
    {
        var monitor = SelectCanvasReferenceMonitor(monitors, selectedDevice, fallback);
        var bounds = new RectD(monitor.Bounds.Left - sourceOrigin.X, monitor.Bounds.Top - sourceOrigin.Y,
            monitor.Bounds.Width, monitor.Bounds.Height).Intersect(viewport);
        return bounds.IsEmpty ? viewport : bounds;
    }

    private static MonitorInfo SelectCanvasReferenceMonitor(IReadOnlyList<MonitorInfo> monitors, string? selectedDevice, PointD fallback) =>
        monitors.FirstOrDefault(m => m.DeviceName == selectedDevice) ??
        monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

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
        RefreshBillboardState();
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
        if (Active.Document.Canvas.Projection.LockedGridCellSize is not null) return;
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

    private void SetCanvasGridSizeLock(bool locked)
    {
        if (locked == (Active.Document.Canvas.Projection.LockedGridCellSize is not null)) return;
        ChangeCanvas(settings =>
        {
            EnsureCanvasReference(settings);
            settings.Projection.LockedGridCellSize = locked ? settings.Projection.CellSize : null;
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
        UpdateStatus(L.Get("Canvas.MultiMonitorHelp"));
        RequestRender();
    }

    private void EndCanvasCalibration(bool hidePanel = false)
    {
        _calibrationSession = null;
        _canvasCalibration.End();
        if (hidePanel && _canvasSettingsWindow is not null) SessionWindowCoordinator.Hide(_canvasSettingsWindow);
    }

    private void BeginCanvasFourPoints()
    {
        if(DocumentOperationBusy||!_host.IsVisible||!_host.IsEditing)return;
        _editor.Cancel();CloseImageDrop();CancelSnapshotCapture("CanvasFourPoints");CancelLivePreparation();
        _calibrationSession=Active;
        _canvasCalibration.BeginPlacement(Active);
        UpdateStatus(L.Get("Canvas.FourPointsHint"));RequestRender();
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
        else if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left) _canvasCalibration.Down(point);
        else if (e.Kind == OverlayPointerKind.Move) _canvasCalibration.Move(point, e.Modifiers);
        else if (e.Kind == OverlayPointerKind.Up && e.Button == OverlayMouseButton.Left)
        { _canvasCalibration.Move(point, e.Modifiers); _canvasCalibration.Up(); }
        return true;
    }

    private void DrawCanvasCalibration(SKCanvas canvas)
    {
        if (!_canvasCalibration.IsActive) return;
        if(_canvasCalibration.IsPlacing){DrawCanvasFourPoints(canvas);return;}
        var settings = Active.Document.Canvas.Projection;
        if (settings.Quad.Count != 4) return;
        using var line = new SKPaint { Color = SKColors.Black.WithAlpha(210), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
        DrawScreenPolygon(canvas, settings.Quad, line);
        line.Color = _canvasCalibration.IsDragConstrained ? new(190, 120, 0) : new(35,131,226); line.StrokeWidth = 1.5f;
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
        var center = settings.Quad.Aggregate(new PointD(), (sum, point) => sum + point) * .25;
        float x = (float)center.X, y = (float)center.Y;
        float halfSize = (float)CanvasCalibrationController.CenterHandleHalfSize;
        canvas.DrawRoundRect(x - halfSize, y - halfSize, halfSize * 2, halfSize * 2, 6, 6, fill);
        canvas.DrawRoundRect(x - halfSize, y - halfSize, halfSize * 2, halfSize * 2, 6, 6, line);
        canvas.DrawLine(x - 10, y, x + 10, y, line); canvas.DrawLine(x, y - 10, x, y + 10, line);
        foreach (int sign in new[] { -1, 1 })
        {
            canvas.DrawLine(x + sign * 10, y, x + sign * 6, y - 4, line); canvas.DrawLine(x + sign * 10, y, x + sign * 6, y + 4, line);
            canvas.DrawLine(x, y + sign * 10, x - 4, y + sign * 6, line); canvas.DrawLine(x, y + sign * 10, x + 4, y + sign * 6, line);
        }
        DrawCanvasOriginMarker(canvas, settings.Quad[0], Active.Document.Viewport, settings.LockedGridCellSize is not null);
    }

    internal static void DrawCanvasOriginMarker(SKCanvas canvas, PointD origin, RectD viewport, bool locked)
    {
        // Q0 retains its identity even when another corner becomes the visually upper-left one.
        var color = locked ? new SKColor(210, 139, 63) : new SKColor(35, 131, 226);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawCircle((float)origin.X, (float)origin.Y, 3, paint);
        using var typeface = SKTypeface.FromFamilyName(L.CurrentLanguage == "zh-CN" ? "Microsoft YaHei UI" : "Segoe UI");
        using var font = new SKFont(typeface, 12);
        string label = L.Get("Canvas.OriginMarker");
        float width = font.MeasureText(label) + 12, height = 22;
        float x = (float)Math.Clamp(origin.X + 13, viewport.Left + 2, Math.Max(viewport.Left + 2, viewport.Right - width - 2));
        float y = (float)Math.Clamp(origin.Y + 13, viewport.Top + 2, Math.Max(viewport.Top + 2, viewport.Bottom - height - 2));
        paint.Color = new SKColor(22, 28, 36, 235);
        canvas.DrawRoundRect(x, y, width, height, 5, 5, paint);
        paint.Color = color;
        canvas.DrawText(label, x + 6, y + (height - font.Metrics.Ascent - font.Metrics.Descent) / 2, SKTextAlign.Left, font, paint);
    }

    private void DrawCanvasFourPoints(SKCanvas canvas)
    {
        var points=_canvasCalibration.PlacementPreview;
        if(points.Count==0)return;
        using var line=new SKPaint {Color=new(35,131,226),StrokeWidth=2,IsAntialias=true,Style=SKPaintStyle.Stroke};
        using var fill=new SKPaint {Color=SKColors.White,IsAntialias=true};
        IReadOnlyList<PointD> contour=points;
        PointD? previewOrigin=null;
        if(points.Count==4&&CanvasCalibration.TryOrderCorners(points,out var quad))
        {
            contour=quad;
            var projection=Active.Document.Canvas.Projection;
            if(CanvasCalibration.TryCalibrate(projection,quad,Active.Document.Viewport,out var preview,out _))
            {
                line.StrokeWidth=1;line.Color=new(35,131,226,170);
                double columns=preview.GridColumns,rows=preview.GridRows;
                void GridLine(PointD a,PointD b)
                {
                    a=preview.Matrix.TransformPoint(preview.Origin+a*preview.GridCellSize);
                    b=preview.Matrix.TransformPoint(preview.Origin+b*preview.GridCellSize);
                    canvas.DrawLine((float)a.X,(float)a.Y,(float)b.X,(float)b.Y,line);
                }
                double columnStride=1,rowStride=1;
                while(columns/columnStride>2048)columnStride*=2;
                while(rows/rowStride>2048)rowStride*=2;
                for(double column=columnStride;column<columns;column+=columnStride)GridLine(new(column,0),new(column,rows));
                for(double row=rowStride;row<rows;row+=rowStride)GridLine(new(0,row),new(columns,row));
                previewOrigin=quad[0];
                line.StrokeWidth=2;line.Color=new(35,131,226);
            }
        }
        if(contour.Count==2)canvas.DrawLine((float)contour[0].X,(float)contour[0].Y,(float)contour[1].X,(float)contour[1].Y,line);
        else if(contour.Count>2)DrawScreenPolygon(canvas,contour,line);
        foreach(var point in points)
        {canvas.DrawCircle((float)point.X,(float)point.Y,5,fill);canvas.DrawCircle((float)point.X,(float)point.Y,5,line);}
        if(previewOrigin is { } origin)
            DrawCanvasOriginMarker(canvas,origin,Active.Document.Viewport,Active.Document.Canvas.Projection.LockedGridCellSize is not null);
    }

    private static void DrawScreenPolygon(SKCanvas canvas, IReadOnlyList<PointD> points, SKPaint paint)
    {
        if (points.Count == 0) return;
        using var builder = new SKPathBuilder(); builder.MoveTo((float)points[0].X,(float)points[0].Y);
        foreach (var point in points.Skip(1)) builder.LineTo((float)point.X,(float)point.Y);
        builder.Close(); using var path = builder.Detach(); canvas.DrawPath(path,paint);
    }
}
