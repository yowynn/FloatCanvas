using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private StampLibraryWindow? _stampWindow;
    private LoadedProject? _selectedStamp;
    private Guid? _selectedStampId;
    private Guid _selectedStampRevision;
    private bool _selectedStampUsesSource;
    private bool _stampSelectionPending;
    private long _stampSelectionRequest;
    private StampPlacementRequestGuard? _stampPlacementGuard;
    private ColorField? _eyedropper;
    private bool _selectedStampFromSlot;
    private PropertiesClickSequence? _propertiesClicks;
    private EditorSession? _propertiesClickSession;
    private EditorTool _propertiesClickTool;
    private long _propertiesClickEditingSession;

    private StampPlacementRequestGuard StampRequestGuard
    {
        get
        {
            if (_stampPlacementGuard is not null) return _stampPlacementGuard;
            var guard = _stampPlacementGuard = new(_editor);
            _toolbar.IsVisibleChanged += (_, _) => { if (!_toolbar.IsVisible) guard.Invalidate(); };
            _toolbar.Closed += (_, _) => guard.Dispose();
            return guard;
        }
    }

    private void HandleCanvasPointer(OverlayPointerEvent e)
    {
        ObserveProjectionHover(e);
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
            _editor.ClickPlacementDragThreshold = ClickPlacementThresholdFor(_host.Monitors, e.Position);
        if (e.Kind == OverlayPointerKind.Cancel) _propertiesClicks?.Reset();
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick) _newCanvasButton?.CancelConfirmation();
        if (_canvasCalibration?.IsAwaitingResolution == true)
        {
            if (e.Kind == OverlayPointerKind.Cancel) _canvasCalibration.CancelDrag();
            else if (e.Kind == OverlayPointerKind.Up && e.Button == OverlayMouseButton.Left) _canvasCalibration.Up();
            _propertiesClicks?.Reset(); return;
        }
        if (DocumentOperationBusy || !_host.IsVisible) { _propertiesClicks?.Reset(); return; }
        if (e.Button == OverlayMouseButton.Middle && e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && !_editor.IsTemporaryMove)
        {
            using var middleDestination = _windowCoordinator?.DirectTransfer(WindowDismissReason.CanvasPress);
            FinishInlineText();
            _inspector?.CompletePendingEdit();
            EndCanvasCalibration();
            if (_eyedropper is not null) EndEyedropper(restore: false);
            DismissPanelsForCanvasPress();
        }
        if (_editor.HandleTemporaryMovePointer(e)) { SynchronizeCanvasTool(); _propertiesClicks?.Reset(); return; }
        if (HandleEyedropperPointer(e)) { _propertiesClicks?.Reset(); return; }
        if (HandleInlineTextPointer(e)) { _propertiesClicks?.Reset(); return; }
        bool dismissedProperties = e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick &&
            _inspector is { IsVisible: true, IsPinned: false };
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick) _inspector?.CompletePendingEdit();
        if (HandleCanvasToolPointer(e) || HandleCalibrationPointer(e)) { _propertiesClicks?.Reset(); return; }
        using var destination = e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick
            ? _windowCoordinator?.DirectTransfer(WindowDismissReason.CanvasPress) : null;
        if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
        {
            // Commit a color draft before this very gesture reads the tool parameters.
            // The clicked canvas remains the destination throughout the whole dismissal.
            DismissPanelsForCanvasPress();
        }
        _propertiesClicks ??= new((uint)System.Windows.Forms.SystemInformation.DoubleClickTime,
            System.Windows.Forms.SystemInformation.DoubleClickSize.Width, System.Windows.Forms.SystemInformation.DoubleClickSize.Height);
        if (!ReferenceEquals(_propertiesClickSession, Active) || _propertiesClickTool != _editor.Tool ||
            _propertiesClickEditingSession != _host.EditingSessionVersion) _propertiesClicks.Reset();
        _propertiesClickSession = Active; _propertiesClickTool = _editor.Tool;
        _propertiesClickEditingSession = _host.EditingSessionVersion;
        bool propertiesDoubleClick;
        if (_editor.Tool == EditorTool.Text || (e.Modifiers & OverlayModifiers.Control) != 0)
        { _propertiesClicks.Reset(); propertiesDoubleClick = false; }
        else propertiesDoubleClick = _propertiesClicks.Observe(e, dismissedProperties);
        ValidateSelectedStamp();
        if (_editor.Tool == EditorTool.Stamp && _editor.BillboardMode && !_selectedStampUsesSource &&
            _selectedStampId is { } billboardStampId &&
            _stampLibrary.Items.FirstOrDefault(item => item.Id == billboardStampId) is { Mode: StampSaveMode.RasterMerge } rasterEntry)
        {
            if (_selectedStamp is null || _stampSelectionPending) return;
            if (_selectedStampRevision != rasterEntry.Revision)
            { SelectStamp(billboardStampId, useSource: false, fromSlot: _selectedStampFromSlot); return; }
            if (!_editor.HasPendingBillboardInsertion)
            {
                var loaded = _selectedStamp; var target = Active; var document = target.Document;
                long revision = target.SceneRevision;
                _editor.BeginBillboardInsertion((depth, center) =>
                {
                    var current = _stampLibrary.Items.FirstOrDefault(item => item.Id == billboardStampId);
                    if (_disposed || _documentStopped || DocumentOperationBusy || !ReferenceEquals(Active, target) ||
                        !ReferenceEquals(target.Document, document) || target.SceneRevision != revision ||
                        current?.Revision != rasterEntry.Revision || _selectedStampId != billboardStampId || _selectedStampUsesSource) return;
                    PlaceStampSafely(() =>
                    {
                        StampLibrary.PlaceBillboardRaster(loaded, target, depth, center);
                        ScheduleRecovery(); RequestRender();
                    });
                });
            }
            _editor.PointerForCanvas(e, propertiesDoubleClick);
            return;
        }
        bool press = e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left;
        if (_editor.Tool == EditorTool.Stamp && press)
        {
            if (!_editor.TryToDocument(e.Position, out var stampPoint))
            { UpdateStatus(L.Get("Perspective.InputOutsidePlane")); return; }
            if (_stampSelectionPending) { UpdateStatus(L.Get("Stamp.Loading")); return; }
            if (_selectedStamp is null || _stampLibrary.Items.All(item => item.Id != _selectedStampId))
            { UpdateStatus(L.Get("Stamp.Missing")); return; }
            var current = _stampLibrary.Items.First(item => item.Id == _selectedStampId);
            if (current.Revision != _selectedStampRevision)
            {
                _editor.Cancel();
                var request = StampRequestGuard.Capture(++_stampSelectionRequest, current.Id, current.Revision, _selectedStampUsesSource, stampPoint);
                _ = RunCommand(() => ReloadStampAndPlaceAsync(request));
                return;
            }
            PlaceStampSafely(() => StampLibrary.Place(_selectedStamp, Active, stampPoint));
            return;
        }
        if (e.Kind == OverlayPointerKind.Down && e.Button == OverlayMouseButton.Right) SwitchToolState(EditorTool.Select);
        int elementCount = Active.Document.Elements.Count;
        _editor.PointerForCanvas(e, propertiesDoubleClick);
        if (Active.Document.Elements.Count > elementCount) _propertiesClicks.Reset();
    }

    private void PlaceStampSafely(Action placement) => RunUiAction(() =>
    {
        try { placement(); }
        catch (InvalidOperationException ex) when (ex.Message == L.Get("Perspective.InputOutsidePlane") ||
            ex.Message == L.Get("Perspective.SnapshotOutsidePlane"))
        { UpdateStatus(ex.Message); RequestRender(); }
    });

    private void DismissPanelsForCanvasPress()
    {
        _toolOptionsPanel?.CloseColorPicker();
        _canvasSettingsWindow?.ClosePickers();
        _windowCoordinator?.DismissTransient(WindowDismissReason.CanvasPress);
        if (_toolbarMenu is not null) _toolbarMenu.IsOpen = false;
        if (_moreToolsMenu is not null) _moreToolsMenu.IsOpen = false;
    }

    private async Task ReloadStampAndPlaceAsync(StampPlacementRequest request)
    {
        var loaded = request.UseSource
            ? await _stampLibrary.LoadSourceAsync(request.StampId, _documentLifetime.Token)
            : await _stampLibrary.LoadStampAsync(request.StampId, _documentLifetime.Token);
        var current = _stampLibrary.Items.FirstOrDefault(item => item.Id == request.StampId);
        if (current is null || !StampRequestGuard.CanPlace(request, _stampSelectionRequest, _selectedStampId, current.Revision,
            _host.IsEditing && _host.IsVisible, !_disposed && !_documentStopped && !DocumentOperationBusy)) return;
        _selectedStamp = loaded; _selectedStampRevision = current.Revision;
        StampLibrary.Place(loaded, request.Target, request.Position);
    }

    private void SelectStamp(Guid id) => SelectStamp(id, useSource: false, fromSlot: false);
    private void SelectStampSlot(Guid id) => SelectStamp(id, useSource: false, fromSlot: true);
    private void SelectStampSource(Guid id) => SelectStamp(id, useSource: true);
    private void SelectStamp(Guid id, bool useSource, bool fromSlot = false) => _ = RunCommand(async () =>
    {
        CloseToolOptions();
        var expected = _stampLibrary.Items.FirstOrDefault(item => item.Id == id);
        if (expected is null) { UpdateStatus(L.Get("Stamp.Missing")); return; }
        SwitchToolState(EditorTool.Stamp);
        _selectedStamp = null; _selectedStampId = id;
        _selectedStampFromSlot = fromSlot; _selectedStampUsesSource = useSource;
        var request = StampRequestGuard.Capture(++_stampSelectionRequest, id, expected.Revision, useSource, default);
        _stampSelectionPending = true; UpdateStatus(L.Get("Stamp.Loading"));
        LoadedProject loaded;
        try { loaded = useSource ? await _stampLibrary.LoadSourceAsync(id, _documentLifetime.Token) : await _stampLibrary.LoadStampAsync(id, _documentLifetime.Token); }
        finally { if (request.Token == _stampSelectionRequest) _stampSelectionPending = false; }
        var entry = _stampLibrary.Items.FirstOrDefault(item => item.Id == id);
        if (entry is null || !StampRequestGuard.CanContinue(request, _stampSelectionRequest, entry.Revision, !_disposed && !_documentStopped && !DocumentOperationBusy)) return;
        _selectedStamp = loaded; _selectedStampId = id; _selectedStampRevision = entry.Revision;
        _selectedStampUsesSource = useSource;
        _selectedStampFromSlot = fromSlot;
        SwitchToolState(EditorTool.Stamp);
        if (_stampWindow is not null) SessionWindowCoordinator.Hide(_stampWindow);
        if (!_host.IsEditing) SetEditing(true);
        UpdateStatus(useSource ? L.Get("Stamp.SourceSelected") + entry.Name : entry.Name);
        RequestRender();
    });

    private void ShowStampLibrary() => _ = RunCommand(async () =>
    {
        var request = new WindowSessionRequest(_host.EditingSessionVersion, _host.IsEditing, Active);
        await _stampLibrary.LoadAsync(_documentLifetime.Token);
        if (_disposed || _windowCoordinator?.CanContinue(request, Active) == false) return;
        EnsureStampWindow();
        ShowAppWindow(_stampWindow!, true);
    });

    private void SaveStampSelection() => _ = RunCommand(async () =>
    {
        if (Active.Selection.Count == 0) { UpdateStatus(L.Get("Stamp.NoSelection")); return; }
        var request = new WindowSessionRequest(_host.EditingSessionVersion, _host.IsEditing, Active);
        var selection = Active.Selection.ToHashSet();
        await _stampLibrary.LoadAsync(_documentLifetime.Token);
        if (_disposed || _windowCoordinator?.CanContinue(request, Active) == false || !selection.SetEquals(Active.Selection)) return;
        EnsureStampWindow();
        await _stampWindow!.SaveSelectionFromToolbarAsync(_toolbar);
    });

    private void EnsureStampWindow()
    {
        if (_stampWindow is null)
        {
            _stampWindow = new StampLibraryWindow(_stampLibrary, () => Active, SelectStamp, () => !DocumentOperationBusy, SelectStampSource);
            _stampWindow.PrepareRendering=PrepareStampRenderingAsync;
            _stampWindow.CommandKey += OnKey;
            ConfigureAppWindow(_stampWindow);
            _stampWindow.Closed += (_, _) => _stampWindow = null;
        }
    }

    private void ValidateSelectedStamp()
    {
        if (_editor.Tool != EditorTool.Stamp || _selectedStampId is not { } id) return;
        if (_stampLibrary.Items.Any(item => item.Id == id) && (!_selectedStampFromSlot || _stampLibrary.Pins.Contains(id))) return;
        _selectedStamp = null; _selectedStampId = null; _stampSelectionRequest++;
        SwitchToolState(EditorTool.Select);
    }

    private void OpenLayers()
    {
        if (_layers?.IsVisible == true) return;
        _layersWanted = false;
        ToggleLayers();
    }

    private void ToggleLayers()
    {
        _editor.Cancel(); _layersWanted = !_layersWanted;
        if (_layers is null)
        {
            _layers = new LayersWindow(() => Active, () => !DocumentOperationBusy);
            ConfigureAppWindow(_layers);
            _layers.CommandKey += OnKey;
            _windowCoordinator?.Register(_layers, () => true);
            _layers.Dismissed += () => _layersWanted = false;
        }
        if (_layersWanted) { _layers.Refresh(); ShowAppWindow(_layers, true); }
        else SessionWindowCoordinator.Hide(_layers);
    }
}
