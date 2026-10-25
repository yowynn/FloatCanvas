using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly CanvasCalibrationController _canvasCalibration = new();
    private readonly CanvasGridScaleController _canvasGridScale = new();
    private CanvasSettingsWindow? _canvasSettingsWindow;
    private EditorSession? _calibrationSession;
    private bool _canvasSafetyPromptOpen;
    private bool _synchronizingCanvasTool;
    private SceneDocument? _canvasOriginDocument;
    private bool IsCanvasSettingsOpen => _canvasSettingsWindow?.IsVisible == true;

    private void InitializeCanvasTool()
    {
        _canvasCalibration.Changed += () =>
        {
            if (_canvasCalibration.IsPlacing) _canvasDraftRevision++;
            _canvasSettingsWindow?.RefreshGridCounts(Active.Document.Canvas.Projection);
            RequestRender(); ScheduleRecovery();
        };
        _canvasCalibration.Error += error => UpdateStatus(L.Get(error == "Canvas.RelocateFailed" || _canvasCalibration.IsPlacing ? error : "Canvas.DragBoundary"));
        _canvasCalibration.ResolutionRequired += QueueCanvasSafetyResolution;
        SynchronizeFlatGridOrigin();
    }

    private void ToggleCanvasSettings() => ActivateTool(EditorTool.Canvas);

    private void OpenCanvasSettings()
    {
        if (DocumentOperationBusy || !_host.IsEditing || _editor.Tool != EditorTool.Canvas) return;
        bool alreadyOpen = IsCanvasSettingsOpen;
        SynchronizeFlatGridOrigin();
        CloseToolOptions();
        if (_canvasSettingsWindow is null)
        {
            var window = _canvasSettingsWindow = new(historySession: () => Active) { Owner = _toolbar };
            ConfigureAppWindow(window);
            _windowCoordinator?.Register(window, () => window.IsPinned, DismissCanvasSettings);
            window.CommandKey += OnKey;
            window.BackgroundChanged += color => ChangeCanvas(c => c.BackgroundColor = color);
            window.ProjectionChanged += SetCanvasProjection;
            window.DepthPriorityOcclusionChanged += enabled =>
            {
                ApplyCanvasDepthPriorityOcclusion(Active, window.DisplayedSettings, enabled,
                    !DocumentOperationBusy && _host.IsEditing, ChangeCanvas);
                window.Refresh(Active.Document.Canvas, _canvasCalibration.IsActive);
            };
            window.GridCountsChanged += SetCanvasGridCounts;
            window.GridSizeChanged += SetCanvasGridSize;
            window.GridSizeLockChanged += SetCanvasGridSizeLock;
            window.CalibrationToggled += () =>
            {
                if (_canvasCalibration.IsActive) ActivateTool(EditorTool.Select); else BeginCanvasCalibration();
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
            window.Deactivated += (_, _) => QueueCanvasSettingsFocusCheck();
            var route = new ToolOptionsPointerRoute(() => IsCanvasSettingsOpen && !window.IsPinned,
                source => IsCanvasSettingsInput(source) || IsToolbarInput(source),
                () => DismissCanvasSettings(WindowDismissReason.ToolbarPress), window, _toolbar);
            window.Closed += (_, _) => route.Dispose();
        }
        RefreshCanvasReferenceMonitors();
        _canvasSettingsWindow.Refresh(Active.Document.Canvas, _canvasCalibration.IsActive);
        ShowAppWindow(_canvasSettingsWindow, true);
        _canvasSettingsWindow.UpdateLayout();
        if (!alreadyOpen) PositionToolOptions(_canvasSettingsWindow, _canvasStateButton);
    }

    private void DismissCanvasSettings(WindowDismissReason reason = WindowDismissReason.Close)
    {
        if (!IsCanvasSettingsOpen) return;
        using var intent = reason == WindowDismissReason.Close ? null : _windowCoordinator?.DirectTransfer(reason);
        _canvasSettingsWindow!.ClosePickers();
        if (reason == WindowDismissReason.Close) _canvasSettingsWindow.ResetPin();
        SessionWindowCoordinator.Hide(_canvasSettingsWindow, reason);
    }

    private bool IsCanvasSettingsInput(DependencyObject? source) => _canvasSettingsWindow is { } settings &&
        Ancestors(source).Any(value => ReferenceEquals(value, settings) || settings.OwnsColorPickerInput(value) ||
            value is Window window && OwnedBy(window, settings));

    private static bool OwnedBy(Window window, Window owner)
    {
        for (var parent = window.Owner; parent is not null; parent = parent.Owner)
            if (ReferenceEquals(parent, owner)) return true;
        return false;
    }

    private void QueueCanvasSettingsFocusCheck()
    {
        if (_disposed || !_host.IsEditing) return;
        long version = _host.EditingSessionVersion;
        _app.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_disposed || !_host.IsEditing || _host.EditingSessionVersion != version || _host.IsInputMethodSwitchPending ||
                _canvasSettingsWindow is not { IsVisible: true, IsPinned: false, IsActive: false } settings) return;
            if (!settings.IsColorPickerOpen && !IsCanvasSettingsInput(Keyboard.FocusedElement as DependencyObject) &&
                !IsCanvasSettingsInput(Mouse.DirectlyOver as DependencyObject) && !_toolbar.IsActive &&
                !_app.Windows.Cast<Window>().Any(window => window.IsActive && OwnedBy(window, settings))) DismissCanvasSettings();
        });
    }

    private RectD PrimaryCanvasReferenceViewport()
    {
        var bounds = _host.PrimaryMonitor.Bounds;
        return new(bounds.Left - _editor.ScreenOrigin.X, bounds.Top - _editor.ScreenOrigin.Y, bounds.Width, bounds.Height);
    }

    private void SynchronizeFlatGridOrigin()
    {
        if (_host.Monitors.Count == 0) return;
        var primary = _host.PrimaryMonitor.Bounds;
        Active.Document.Canvas.Grid.FlatOrigin = new PointD(primary.Left + primary.Width / 2d, primary.Top + primary.Height / 2d) - _editor.ScreenOrigin;
        Active.Document.Canvas.Grid.FlatCellSize ??= CanvasGridGeometry.ResolveCellSize(Active.Document.Canvas, Active.Document.Viewport);
        _canvasOriginDocument = Active.Document;
    }

    private RectD CanvasReferenceViewport()
    {
        return PrimaryCanvasReferenceViewport();
    }

    private void RefreshCanvasReferenceMonitors()
    {
        _canvasSettingsWindow?.SetReferenceMonitors(_host.Monitors, _settings.CanvasReferenceDisplay);
    }

    private void ResetCanvasProjectionOnDisplay(string? device)
    {
        if (DocumentOperationBusy || !_host.IsEditing) return;
        RememberCanvasReferenceDisplay(_settings, device);
        ResetCanvasProjection(ResolveCanvasReferenceBounds(_host.Monitors, device, default, _editor.ScreenOrigin, Active.Document.Viewport));
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
        var old = settings.Projection;
        double size = CanvasGridGeometry.ResolveCellSize(settings, CanvasReferenceViewport());
        settings.Projection = CanvasGridGeometry.CreateFlatReference(CanvasReferenceViewport(), old.Columns, old.Rows, size);
        settings.Projection.Enabled = old.Enabled;
        settings.Projection.LockedGridCellSize = old.LockedGridCellSize;
    }

    private void ChangeCanvas(Action<CanvasSettings> change)
    {
        if (DocumentOperationBusy || !_host.IsEditing) return;
        _canvasGridScale.Cancel(); _canvasCalibration.CancelDrag(); _editor.Cancel();
        CancelSnapshotCapture("CanvasSettingsChanged"); CancelLivePreparation();
        Active.ExecuteSettings(L.Get("Canvas.Title"), document => change(document.Canvas));
        SynchronizeFlatGridOrigin(); SynchronizeCanvasTool();
        RefreshBillboardState();
        RequestRender();
    }

    internal static void ApplyCanvasDepthPriorityOcclusion(EditorSession session, CanvasSettings? displayed, bool enabled,
        bool canMutate, Action<Action<CanvasSettings>> change)
    {
        if (!canMutate || session.IsTransactionActive || !ReferenceEquals(session.Document.Canvas, displayed) ||
            !session.Document.Canvas.Projection.Enabled || session.Document.Canvas.DepthPriorityOcclusion == enabled) return;
        change(settings => settings.DepthPriorityOcclusion = enabled);
    }

    private void SetCanvasProjection(bool enabled)
    {
        if (!enabled) EndCanvasCalibration();
        ChangeCanvas(settings =>
        {
            EnsureCanvasReference(settings);
            if (enabled && !CanvasCalibration.IsValidProjection(settings.Projection.Matrix))
            { UpdateStatus(L.Get("Canvas.UnstableProjection")); return; }
            settings.Projection.Enabled = enabled;
        });
    }

    private void SetCanvasGridSize(double size)
    {
        if (Active.Document.Canvas.Projection.Enabled || !double.IsFinite(size) || size is < 1 or > CanvasGridGeometry.MaximumCellSize) return;
        if (CanvasGridGeometry.ResolveCellSize(Active.Document.Canvas, CanvasReferenceViewport()) == size) return;
        ChangeCanvas(settings => CanvasGridScaleController.ApplyGridSize(settings, CanvasReferenceViewport(), size));
    }

    private void ApplyCanvasPreset(CanvasPresetParameters parameters)
    {
        if (DocumentOperationBusy || !_host.IsEditing) return;
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
            settings.DepthPriorityOcclusion = preset.DepthPriorityOcclusion;
            settings.Grid = preset.Grid;
            settings.Projection = preset.Projection;
        });
    }

    private void SetCanvasGridCounts(int columns, int rows)
    {
        if (!Active.Document.Canvas.Projection.Enabled || Active.Document.Canvas.Projection.LockedGridCellSize is not null) return;
        ChangeCanvas(settings =>
        {
            var original = settings.Projection;
            if (original.Columns == columns && original.Rows == rows) return;
            if (original.Quad.Count == 0)
            {
                settings.Projection = CanvasCalibration.CreateInitial(CanvasReferenceViewport(), columns, rows);
                settings.Grid.FlatCellSize = settings.Projection.GridCellSize;
                return;
            }
            var draft = original.Clone(); draft.Columns = columns; draft.Rows = rows;
            if (CanvasCalibration.TryCalibrate(draft, original.Quad, Active.Document.Viewport, out var calibrated, out var error))
            { calibrated.Enabled = original.Enabled; settings.Projection = calibrated; settings.Grid.FlatCellSize = calibrated.GridCellSize; }
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
            settings.Grid.FlatCellSize = settings.Projection.GridCellSize;
        });
    }

    private void ResetCanvasProjection(RectD? reference = null)
    {
        try { ChangeCanvas(settings =>
        {
            settings.Projection = CreateResetCanvasProjection(settings, reference ?? CanvasReferenceViewport());
            settings.Grid.FlatCellSize = settings.Projection.GridCellSize;
        }); }
        catch (ArgumentOutOfRangeException) { UpdateStatus(L.Get("Canvas.InvalidGrid")); }
    }

    internal static CanvasProjectionSettings CreateResetCanvasProjection(CanvasSettings settings, RectD reference)
    {
        var old = settings.Projection;
        double size = settings.Grid.FlatCellSize ?? CanvasGridGeometry.ResolveCellSize(settings, reference);
        var reset = CanvasGridGeometry.CreateFlatReference(reference, old.Columns, old.Rows, size);
        reset.Enabled = old.Enabled;
        if (old.LockedGridCellSize.HasValue) reset.LockedGridCellSize = size;
        return reset;
    }

    private void BeginCanvasCalibration()
    {
        if (DocumentOperationBusy || !_host.IsEditing) return;
        _editor.Cancel(); CloseImageDrop(); CancelSnapshotCapture("CanvasCalibration"); CancelLivePreparation();
        SetCanvasProjection(true);
        if (!Active.Document.Canvas.Projection.Enabled) return;
        if (_editor.Tool != EditorTool.Canvas) SwitchToolState(EditorTool.Canvas);
        SynchronizeCanvasTool();
        UpdateStatus(L.Get("Canvas.MultiMonitorHelp"));
        RequestRender();
    }

    private void EndCanvasCalibration(bool hidePanel = false)
    {
        _canvasGridScale?.Cancel();
        _calibrationSession = null;
        if (_canvasCalibration.IsActive) _canvasCalibration.End();
        if (hidePanel) DismissCanvasSettings();
    }

    private void BeginCanvasFourPoints()
    {
        if(DocumentOperationBusy||!_host.IsEditing)return;
        _editor.Cancel();CloseImageDrop();CancelSnapshotCapture("CanvasFourPoints");CancelLivePreparation();
        if (_editor.Tool != EditorTool.Canvas) SwitchToolState(EditorTool.Canvas);
        _calibrationSession=Active;
        _canvasCalibration.BeginPlacement(Active);
        UpdateStatus(L.Get("Canvas.FourPointsHint"));RequestRender();
    }

    private void ValidateCanvasCalibrationSession()
    {
        if (!ReferenceEquals(_canvasOriginDocument, Active.Document)) SynchronizeFlatGridOrigin();
        if (_calibrationSession is not null && !ReferenceEquals(_calibrationSession, Active)) EndCanvasCalibration(hidePanel: true);
        SynchronizeCanvasTool();
    }

    private void SynchronizeCanvasTool()
    {
        if (_synchronizingCanvasTool) return;
        _synchronizingCanvasTool = true;
        try
        {
            bool selected = _editor.Tool == EditorTool.Canvas && _host.IsEditing &&
                !_disposed && !_editor.IsTemporaryMove && (!DocumentOperationBusy || _canvasCalibration.IsAwaitingResolution);
            if (!selected)
            {
                _canvasGridScale?.Cancel();
                if (_canvasCalibration.IsActive) EndCanvasCalibration();
                return;
            }
            if (Active.Document.Canvas.Projection.Enabled || _canvasCalibration.IsPlacing)
            {
                _canvasGridScale?.Cancel();
                if (!_canvasCalibration.IsActive || !ReferenceEquals(_calibrationSession, Active))
                {
                    _calibrationSession = Active;
                    _canvasCalibration.Begin(Active);
                }
            }
            else if (_canvasCalibration.IsActive) EndCanvasCalibration();
        }
        finally { _synchronizingCanvasTool = false; }
    }

    private bool HandleCanvasToolPointer(OverlayPointerEvent e)
    {
        if (_editor.Tool != EditorTool.Canvas) return false;
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            using var destination = _windowCoordinator?.DirectTransfer(WindowDismissReason.CanvasPress);
            DismissPanelsForCanvasPress();
        }
        if (e.Kind == OverlayPointerKind.Down && e.Button == OverlayMouseButton.Right)
        {
            _canvasGridScale.Cancel(); _canvasCalibration.CancelDrag();
            ActivateTool(EditorTool.Select); return true;
        }
        SynchronizeCanvasTool();
        if (HandleCalibrationPointer(e)) return true;
        var point = new PointD(e.Position.X - _editor.ScreenOrigin.X, e.Position.Y - _editor.ScreenOrigin.Y);
        if (e.Kind == OverlayPointerKind.Cancel) _canvasGridScale.Cancel();
        else if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
            _canvasGridScale.Down(Active, point, PrimaryCanvasReferenceViewport(), hitExistingContent: _host.IsVisible);
        else if (e.Kind == OverlayPointerKind.Move) _canvasGridScale.Move(point);
        else if (e.Kind == OverlayPointerKind.Up && e.Button == OverlayMouseButton.Left)
        { _canvasGridScale.Move(point); _canvasGridScale.Up(); }
        return true;
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

    private void QueueCanvasSafetyResolution()
    {
        var expected = Active; long transaction = expected.TransactionId;
        // Let the native mouse-up release capture before transferring focus to the dialog.
        _toolbar.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
        {
            if (_canvasSafetyPromptOpen || !_canvasCalibration.IsAwaitingResolution ||
                !ReferenceEquals(expected, Active) || expected.TransactionId != transaction || !expected.IsTransactionActive) return;
            _canvasSafetyPromptOpen = true;
            try
            {
                bool canRelocate = CanvasSafetyResolution.TryRelocate(expected.Document.Clone(), _canvasCalibration.AffectedLayerIds);
                var dialog = CanvasSafetyDialog.Create(_canvasSettingsWindow is { IsVisible: true } ? _canvasSettingsWindow : _toolbar,
                    _canvasCalibration.AffectedLayerIds.Count, canRelocate);
                SessionWindowCoordinator.ShowModal(dialog.Window);
                if (!_canvasCalibration.Resolve(dialog.Result()) && _canvasCalibration.IsAwaitingResolution)
                    _canvasCalibration.Resolve(CanvasSafetyAction.Cancel);
            }
            catch (Exception ex)
            {
                // A dialog/focus failure must never implicitly apply a destructive resolution.
                _canvasCalibration.CancelDrag(); UpdateStatus(ex.Message);
            }
            finally { _canvasSafetyPromptOpen = false; RequestRender(); ScheduleRecovery(); }
        }));
    }

    private void DrawCanvasCalibration(SKCanvas canvas)
    {
        if (!_canvasCalibration.IsActive) return;
        DrawCanvasSafetyBoundary(canvas);
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
        var center = CanvasGridGeometry.ReferenceCenter(settings);
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
        DrawCanvasOriginMarker(canvas, center, Active.Document.Viewport, settings.LockedGridCellSize is not null);
    }

    private void DrawCanvasSafetyBoundary(SKCanvas canvas)
    {
        var mapping = CanvasMapping.Create(Active.Document);
        var inverse = mapping.ScreenToPlane;
        double normal = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
        if (normal < 1e-15) return;
        using var dash = SKPathEffect.CreateDash([6, 5], 0);
        using var paint = new SKPaint { Color = new SKColor(210, 139, 63, 190), StrokeWidth = 1.5f,
            IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = dash };
        var polygon = mapping.SafeScreenPolygon;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if (Math.Abs(inverse.Denominator(a) / normal - CanvasProjectionSafety.HorizonMargin) > .01 ||
                Math.Abs(inverse.Denominator(b) / normal - CanvasProjectionSafety.HorizonMargin) > .01) continue;
            canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, paint);
        }
    }

    internal static void DrawCanvasOriginMarker(SKCanvas canvas, PointD origin, RectD viewport, bool locked)
    {
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
                // The grid is rendered from a detached snapshot on the preview worker.
                // Only the current contour, anchors and origin remain in this UI draw.
                previewOrigin=CanvasGridGeometry.ReferenceCenter(preview);
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
