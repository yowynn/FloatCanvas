using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession : IDisposable
{
    private readonly System.Windows.Application _app;
    private readonly OverlayHost _host;
    private readonly Window _toolbar;
    private readonly Forms.NotifyIcon _tray;
    private readonly TextBlock _status = new();
    private readonly UserSettings _settings;
    private readonly ToolPresetLibrary _toolPresets;
    private readonly ColorLibrary _colorLibrary;
    private readonly StampLibrary _stampLibrary;
    private readonly AssetLibrary _assets = new();
    private readonly SceneRenderer _renderer = new();
    private readonly OverlaySceneCache _overlayCache;
    private readonly EditorController _editor;
    private DispatcherOperation? _renderOperation;
    private EditorSession _live;
    private EditorSession? _capture;
    private PointD _captureOrigin;
    private InspectorWindow? _inspector;
    private LayersWindow? _layers;
    private bool _layersWanted;
    private Window? _textEditor;
    private EditorSession? _textEditorSession;
    private Slider _opacitySlider = null!;
    private System.Windows.Controls.Button _passThroughButton = null!;
    private TextBlock? _welcomeShortcuts;
    private TextBlock? _welcomeHideShortcut;
    private Window? _welcomeWindow;
    private WelcomeView? _welcomeView;
    private bool _welcomeShown;
    private readonly SessionWindowLayout _windowLayout = new();
    private readonly SessionWindowCoordinator? _windowCoordinator;
    private bool _syncingControls;
    private bool _disposed;
    private bool _toolbarPlaced;
    private bool _inspectorWanted;
    private bool _renderingFrame, _renderAgain;
    private EditorSession Active => _editor.Session;
    private bool IsRenderPending => !_disposed && (_renderingFrame || _renderAgain ||
        _renderOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing);

    public DesktopSession(System.Windows.Application app) : this(app, null) { }

    // Internal fixture seam: a real native host may present a bounded tile inside the primary screen.
    // Normal startup always constructs its host from the saved scope and real monitor enumeration.
    internal DesktopSession(System.Windows.Application app, OverlayHost? testHost)
    {
        _app = app;
        _settings = UserSettings.Load();
        L.Initialize(_settings.Language);
        _toolPresets = new();
        _toolStateStore = new();
        _toolbarToolMemory = _toolStateStore.Tools;
        _toolbarButtonMemory = _toolStateStore.Buttons;
        _toolStateStore.Changed += ScheduleToolStateSave;
        _colorLibrary = ColorLibrary.Shared;
        _stampLibrary = new(ApplicationPaths.ProfileDirectory);
        _overlayCache = new(_renderer);
        AppTheme.Apply(_settings.Theme);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        _host = testHost ?? new OverlayHost(ApplicationPaths.RequestedMonitorScope ??
            (_settings.PrimaryScreenOnly ? OverlayMonitorScope.PrimaryScreen : OverlayMonitorScope.AllScreens));
        _host.ApplyRenderPreference(_settings.PreferGpuAcceleration);
        _host.RenderBackendChanged += () => _diagnostics.Write("Rendering.Backend", new
            { Backend = _host.RenderBackend.ToString(), _host.GpuDetails, _host.LastGpuFailure });
        _host.CanBeginCanvasGesture = () => !_disposed && _host.IsVisible && !DocumentOperationBusy && !ComponentDispatcher.IsThreadModal;
        var keyboardBridge = new PrivateKeyboardBridge(handle => _host.Diagnostics.Any(pair => pair.InputHandle == handle));
        _host.EnableKeyboardIsolation((packet, complete) =>
        {
            keyboardBridge.Process(packet, complete);
            // Native canvas HWNDs bypass WPF input staging, including Ctrl key-up.
            // Repaint after the bridge updates this thread's local modifier state.
            if (packet.VirtualKey is 0x11 or 0xA2 or 0xA3) RequestRender();
        });
        System.Windows.Input.InputManager.Current.PreProcessInput += OnWpfInputProgress;
        System.Windows.Input.InputManager.Current.PostProcessInput += OnWpfPointerFocus;
        var bounds = _host.Monitors.Select(m => new RectD(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height)).Aggregate(RectD.Union);
        var document = new SceneDocument { Viewport = bounds, DisplayLayout = _host.Monitors.Select(m => new DisplayLayoutHint
        { DeviceName = m.DeviceName, Bounds = new(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height), DpiX = m.DpiX, DpiY = m.DpiY }).ToList() };
        _live = new(document, _assets);
        _editor = new(_live);
        if (_toolbarToolMemory.TryGetValue(EditorTool.Freehand, out var savedPen)) _editor.ApplyParameters(savedPen);
        _toolbarToolMemory.TryGetValue(EditorTool.Number, out var savedNumber);
        _editor.NumberStart = Math.Clamp(savedNumber?.NumberStart ?? _settings.NumberStart,1,999999);
        _editor.NumberStep = Math.Clamp(savedNumber?.NumberStep ?? _settings.NumberStep,1,999999);
        _settings.NumberStart = _editor.NumberStart; _settings.NumberStep = _editor.NumberStep;
        document.NextNumber = _editor.NumberStart;
        _editor.Changed += () =>
        {
            ValidateCanvasCalibrationSession();
            if (_liveEffectPreparation is { } pending && pending.Tool != _editor.Tool) CancelLivePreparation();
            RequestRender(); ScheduleRecovery();
        };
        _editor.Message += UpdateStatus;
        _editor.EditText += ShowTextEditor;
        _editor.CaptureSelectionCompleted += BeginSnapshotCapture;
        _host.CaptureDiagnostic += OnCaptureDiagnostic;
        _host.Draw += Draw;
        _host.Pointer += HandleCanvasPointer;
        _editor.DesktopEffectRequested += (region, kind, amount, color) => _ = RunCommand(() => AddDesktopEffectAsync(region, kind, amount, color));
        _host.KeyPressed += OnKey;
        _host.ToggleRequested += ToggleEditing;
        _host.HideRequested += ToggleCanvasVisibility;
        _host.EnableImageDrops(SupportsCanvasImageDrop, ReceiveCanvasImageDrop);
        _host.MonitorsChanged += OnMonitorsChanged;
        _host.Error += ex =>
        {
            _diagnostics.Write("Overlay.Error", exception: ex);
            _app.Dispatcher.BeginInvoke(() => ShowError(ex.Message));
        };
        _toolbar = CreateToolbarV2();
        _windowCoordinator = new(_host, _toolbar);
        _windowCoordinator.Register(_toolOptionsWindow, () => _toolOptionsPinned, DismissToolOptions);
        _windowLayout.BindRoot(_toolbar);
        RefreshHotkeyHints(_passThroughButton, null, _settings.Toggle, _settings.Hide);
        _host.InputActivated += RaiseVisibleToolWindows;
        _host.EditingFocusLost += () =>
        {
            EndCanvasCalibration(hidePanel: true);
            CancelSnapshotCapture("ExternalFocus", userInitiated: false);
            using var destination = _windowCoordinator?.DirectTransfer(WindowDismissReason.ExternalFocus);
            // Do not gate focus release on an asynchronous document operation.
            CloseImageDrop();
            _editor.Cancel(); _textEditor?.Hide(); _stampWindow?.Hide(); _eyedropper = null;
            if (Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
            _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); CloseToolOptions(); RequestRender();
            UpdateStatus(L.Get("Desktop.FocusLost"));
        };
        _tray = new Forms.NotifyIcon { Icon = AppIcon.CreateTrayIcon(!_host.IsVisible),
            Text = L.Get("App.NamePrefix") + FormatHotkey(_settings.Toggle), Visible = true };
        _host.KeyboardIsolationReleased += reason =>
        {
            if (!ShouldNotifyKeyboardRelease(reason)) return;
            var message = L.Get("Desktop.InputInterrupted");
            UpdateStatus(message);
            _tray.ShowBalloonTip(4000, L.Get("Desktop.InputReleasedTitle"), message, Forms.ToolTipIcon.Info);
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.Get("Desktop.ToggleEditing"), null, (_, _) => ToggleEditing());
        menu.Items.Add(L.Get("Desktop.ToggleVisibility"), null, (_, _) => ToggleCanvasVisibility());
        menu.Items.Add(L.Get("Desktop.OpenProject"), null, async (_, _) => await RunCommand(OpenProjectAsync));
        menu.Items.Add(L.Get("Desktop.RecoveryHistory"), null, async (_, _) => await RunCommand(RestoreWorkspaceHistoryAsync));
        menu.Items.Add(L.Get("Desktop.HotkeySettings"), null, (_, _) => RunUiAction(ShowHotkeySettings));
        menu.Items.Add(L.Get("Desktop.Exit"), null, async (_, _) => await ExitAfterSnapshotAsync());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleEditing();
    }

    public async void Start(string[] args)
    {
        _shellIntegrationAllowed = ProjectLaunchRequest.AllowsRegistration(args);
        _startupPending = true;
        _quietStartup = args.Contains("--startup");
        string? requestedProject = ProjectLaunchRequest.Parse(args);
        if (requestedProject is not null) QueueProjectOpen(requestedProject);
        try
        {
            await InitializeDocumentsAsync(args.Contains("--recover"));
            try { await _stampLibrary.LoadAsync(_documentLifetime.Token); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException)
            {
                UpdateStatus(ex.Message);
                _tray.ShowBalloonTip(5000, L.Get("Stamp.LibraryTitle"), ex.Message, Forms.ToolTipIcon.Warning);
            }
            OnMonitorsChanged();
            _host.RegisterHotkeys(_settings.Toggle, _settings.Hide);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            if (args.Contains("--smoke")) throw;
            ShowError(ex.Message + L.Get("Desktop.ChooseOtherHotkey"));
            ShowHotkeySettings(); return;
        }
        if (args.Contains("--smoke"))
        {
            int durationIndex = Array.IndexOf(args, "--smoke-seconds");
            int seconds = durationIndex >= 0 && durationIndex + 1 < args.Length && int.TryParse(args[durationIndex + 1], out var requestedSeconds)
                ? Math.Clamp(requestedSeconds, 1, 60) : 2;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop(); Directory.CreateDirectory("artifacts/m1");
                File.WriteAllText("artifacts/m1/app-smoke.json", JsonSerializer.Serialize(new
                { Passed = !_host.IsEditing, Mode = "PassThrough", Process = Environment.ProcessId,
                    LanguagePreference = L.Preference, InterfaceLanguage = L.CurrentLanguage,
                    MonitorScope = _host.Scope.ToString(), _host.Monitors,
                    CapturedInput = _host.IsEditing, Timestamp = DateTimeOffset.Now, _live.Document.Viewport,
                    LiveElements = _live.Document.Elements.Select(e => new { e.Id, e.Kind, e.Transform, e.Text, e.Number, e.AssetId }).ToArray(),
                    CaptureElementCount = _capture?.Document.Elements.Count, AssetCount = _assets.Count,
                    ResourcesComplete = _live.Document.ReferencedAssets.Concat(_capture?.Document.ReferencedAssets ?? []).All(id => _assets.TryGet(id, out _)),
                    ToolParameters = _editor.Parameters, ToolWorkingStates = _toolbarToolMemory, ToolPresetSelections = _toolStateStore?.Selections
                }, new JsonSerializerOptions { WriteIndented = true }));
                _app.Shutdown();
            };
            _host.Render(); timer.Start(); return;
        }
        await CompleteStartupAsync();
    }

    private void ShowWelcome()
    {
        if (_welcomeShown || _host.IsEditing) return;
        _welcomeShown = true;
        var view = new WelcomeView(_settings.Toggle, _settings.Hide, () => RunUiAction(() => SetEditing(true)), ShowUpdates);
        _welcomeView = view; _welcomeShortcuts = view.ToggleShortcut; _welcomeHideShortcut = view.HideShortcut;
        var welcome = new Window { Title = L.Get("Welcome.Title"), Content = view, Width = 430, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, FontSize = 14 };
        _welcomeWindow = welcome;
        welcome.Closed += (_, _) => { _welcomeWindow = null; _welcomeView = null; _welcomeShortcuts = null; _welcomeHideShortcut = null; };
        ConfigureAppWindow(welcome, true);
        // The first-run surface is a normal discoverable entry while the toolbar is hidden.
        welcome.ShowInTaskbar = true;
        welcome.Show();
    }

    private sealed record ToolOption(EditorTool Tool, string Key)
    {
        public string Label => L.Get(Key);
    }
    private static readonly ToolOption[] ToolOptions = [new(EditorTool.Select,"Tool.Select"),new(EditorTool.Freehand,"Tool.Freehand"),new(EditorTool.Highlighter,"Tool.Highlighter"),
        new(EditorTool.Line,"Tool.Line"),new(EditorTool.Arrow,"Tool.Arrow"),new(EditorTool.DoubleArrow,"Tool.DoubleArrow"),new(EditorTool.Rectangle,"Tool.Rectangle"),new(EditorTool.RoundedRectangle,"Tool.RoundedRectangle"),
        new(EditorTool.Ellipse,"Tool.Ellipse"),new(EditorTool.Symbol,"Tool.Symbol"),new(EditorTool.Emoji,"Tool.Emoji"),new(EditorTool.Text,"Tool.Text"),new(EditorTool.Number,"Tool.Number"),new(EditorTool.Eraser,"Tool.Eraser"),
        new(EditorTool.Crop,"Tool.Crop"),new(EditorTool.Blur,"Tool.Blur"),new(EditorTool.Pixelate,"Tool.Pixelate"),new(EditorTool.CaptureRegion,"Tool.CaptureRegion")];
    private System.Windows.Controls.Button Button(string label, Action action)
    {
        var button = new System.Windows.Controls.Button { Content = label, MinHeight = 34, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2, 0, 2, 0),
            BorderThickness = new Thickness(0), ToolTip = label };
        button.SetResourceReference(Control.BackgroundProperty, "CanvasButton");
        button.Click += (_, _) => RunUiAction(action); return button;
    }
    private System.Windows.Controls.Button ToolButton(string label, string geometry, Action action)
    {
        var button = Button(label, action);
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 16, Height = 16,
            Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText");
        button.Content = icon; button.Width = 34; button.Padding = new Thickness(9);
        return button;
    }
    private bool SetEditing(bool editing, bool preserveImageDrop = false)
    {
        if (!editing) EndCanvasCalibration(hidePanel: true);
        CancelSnapshotCapture(editing ? "ExplicitEntry" : "ModeExit");
        using var destination = editing ? null : _windowCoordinator?.DirectTransfer(WindowDismissReason.ModeExit);
        _clearCanvasButton?.CancelConfirmation();
        // Close the startup window before acquiring the input lease so native close
        // focus restoration cannot interrupt a newly acquired editing session.
        if (editing) _welcomeWindow?.Close();
        if (!preserveImageDrop) CloseImageDrop();
        bool wasEditing = _host.IsEditing;
        _inspector?.CompletePendingEdit();
        _editor.Cancel();
        if (!editing) _textEditor?.Close();
        else if (_textEditor is not null && !ReferenceEquals(_textEditorSession, Active)) _textEditor.Hide();
        if (!editing && Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
        if (editing && !_host.TryEnterEditing(out var failure))
        {
            // Never leave an opaque frozen screenshot over a desktop that is receiving input.
            ReturnToLive();
            _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); _textEditor?.Hide(); CloseToolOptions();
            UpdateStatus(failure!);
            _tray.ShowBalloonTip(4000, L.Get("Desktop.EnterEditFailed"), failure!, Forms.ToolTipIcon.Info);
            RequestRender();
            return false;
        }
        if (!editing) _host.ExitEditing();
        if (editing)
        {
            _toolbar.Show();
            if (!_toolbarPlaced) PlaceToolbar();
            if (_inspectorWanted && _inspector is not null) { ShowAppWindow(_inspector); _inspector.Refresh(); }
            if (_layersWanted && _layers is not null) { ShowAppWindow(_layers); _layers.Refresh(); }
            if (_textEditor is not null && ReferenceEquals(_textEditorSession, Active))
            { _textEditor.Show(); _textEditor.Activate(); }
            if (!wasEditing) UpdateStatus(L.Get("Desktop.EditModePrefix") + (ToolOptions.FirstOrDefault(x => x.Tool == _editor.Tool)?.Label ?? L.Get("Desktop.SelectTool")));
        }
        else { _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); _stampWindow?.Hide(); CloseToolOptions(); _eyedropper = null; }
        RequestRender();
        return true;
    }
    private void RaiseVisibleToolWindows()
    {
        foreach (var window in _app.Windows.Cast<Window>().OrderBy(w => ReferenceEquals(w, _toolbar) ? 0 : 1))
            if (window is { IsVisible: true } && new WindowInteropHelper(window).Handle is var handle && handle != 0)
                OverlayCoordinates.RaiseToolWindow(handle);
        RaiseToolOptionsPopup();
    }
    private void RequestRender()
    {
        _pixelsOnlyRender = false;
        if (_disposed) return;
        if (_renderingFrame) { _renderAgain = true; return; }
        if (IsRenderPending) return;
        // Coalesce a batch of changes without adding a fixed timer delay to every pointer update.
        // BeginInvoke also preserves the application's DispatcherUnhandledException reporting path.
        _renderOperation = _app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderFrame));
    }
    private void RequestPixelRender()
    {
        // Animation and live background frames change pixels, not controls or document
        // state. A queued full edit render always takes precedence over this shortcut.
        if (_disposed || IsRenderPending) return;
        _pixelsOnlyRender = true;
        _renderOperation = _app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderFrame));
    }
    private void RenderFrame()
    {
        _renderOperation = null;
        if (_disposed) return;
        _renderingFrame = true;
        try
        {
            bool pixelsOnly = _pixelsOnlyRender;
            _pixelsOnlyRender = false;
            PrepareAnimationFrame();
            if (!pixelsOnly) SyncLiveEffectsCapture();
            // GPU readback is worthwhile for filter computation, while the raster cache
            // is faster for ordinary shapes and small animated images on the current host.
            var document = Active.Document;
            bool ImageFilter(CanvasElement element) => element.Kind == ElementKind.Image && element.Visible && element.Style.Opacity > 0 &&
                SceneRenderer.ProjectedBounds(document, element).Intersects(document.Viewport) &&
                element.Effects.Any(effect => effect.Kind is ImageEffectKind.Blur or ImageEffectKind.Pixelate);
            _host.SetGpuWorkload(SceneRenderer.HasLiveEffects(document) ||
                document.Background is { } background && ImageFilter(background) ||
                document.LayerOpacity > 0 && document.Elements.Any(ImageFilter));
            _host.Render();
            ScheduleAnimationFrame();
            if (pixelsOnly) return;
            _syncingControls = true;
            try
            {
                RefreshToolbarState();
                if (!Active.IsTransactionActive) _opacitySlider.Value = Active.Document.LayerOpacity;
            }
            finally { _syncingControls = false; }
            if (!Active.IsTransactionActive)
            {
                _inspector?.Refresh(); _layers?.Refresh();
                _canvasSettingsWindow?.Refresh(Active.Document.Canvas, _canvasCalibration.IsActive);
                if (IsToolOptionsOpen) _toolOptionsPanel.RefreshDocumentState();
            }
        }
        finally
        {
            _renderingFrame = false;
            if (_renderAgain) { _renderAgain = false; RequestRender(); }
        }
    }
    private void UpdateStatus(string message) { _status.Text = message; _toolbarStatusDisplay?.Refresh(); }
    private void OnWpfInputProgress(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (_disposed) return;
        _host.NotifyInputProgress();
        if (e.StagingItem.Input is System.Windows.Input.MouseButtonEventArgs { ButtonState: System.Windows.Input.MouseButtonState.Pressed } mouse)
            _windowCoordinator?.ObservePointerPress(mouse);
        if (e.StagingItem.Input is System.Windows.Input.KeyEventArgs { Key: System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl })
            RequestRender();
    }
    private void OnWpfPointerFocus(object sender, System.Windows.Input.ProcessInputEventArgs e)
    {
        if (_disposed || !_host.IsEditing || e.StagingItem.Input is not System.Windows.Input.MouseButtonEventArgs
            { ButtonState: System.Windows.Input.MouseButtonState.Pressed, OriginalSource: DependencyObject source }) return;
        if (_clearCanvasButton is { IsConfirming: true } clear && !Ancestors(source).Contains(clear)) clear.CancelConfirmation();
        // WPF menus own their focus/capture lifetime until the mouse release invokes
        // the item. Moving native focus into that Popup can cancel the pending click.
        if (!ShouldSynchronizePointerFocus(source)) return;
        // A native canvas drag owns its whole gesture, even when it crosses a panel.
        nint capture = _host.CaptureHandle;
        if (capture != 0 && _host.Diagnostics.Any(pair => pair.InputHandle == capture)) return;
        PrivateKeyboardBridge.SynchronizePointerFocus(source);
    }
    internal static bool ShouldSynchronizePointerFocus(DependencyObject source) =>
        !Ancestors(source).Any(value => value is MenuItem or System.Windows.Controls.Primitives.MenuBase);
    private void SelectTool(EditorTool tool)
    {
        ActivateTool(tool);
    }
    private void OnKey(int key)
    {
        if (key == 0x1B && _canvasCalibration.IsActive)
        { _canvasCalibration.Escape(); RequestRender(); return; }
        if (key == 0x1B && _snapshotCaptureRequest is not null) { CancelSnapshotCapture("Escape"); return; }
        if (_clearCanvasButton is { IsConfirming: true } clear)
        {
            clear.CancelConfirmation();
            if (key == 0x1B) return;
        }
        if (DocumentOperationBusy || _layers?.IsRenaming == true) return;
        _inspector?.CompletePendingEdit();
        if (_editor.HandleCaptureKey(key)) return;
        var ctrl = (OverlayHost.CurrentModifiers & OverlayModifiers.Control) != 0;
        var shift = (OverlayHost.CurrentModifiers & OverlayModifiers.Shift) != 0;
        if (key == 0x1B)
        {
            if (_eyedropper is not null) { _eyedropper = null; UpdateStatus(L.Get("Color.SampleCancelled")); }
            else if (_toolOptionsPanel.CloseColorPicker()) { }
            else if (IsToolOptionsOpen && !_toolOptionsPinned) CloseToolOptions();
            else if (Active.IsTransactionActive || _editor.Marquee != null) _editor.Cancel();
            else SetEditing(false);
            return;
        }
        if (ctrl)
        {
            // Modifiers (including a repeated or delayed Ctrl packet) are not editor
            // commands. They must not roll back a pointer transaction in progress.
            if (key is not (0x5A or 0x59 or 0x41 or 0x43 or 0x56 or 0x44 or 0x47 or 0x53 or 0x4F)) return;
            EndCanvasCalibration();
            _editor.Cancel();
            switch (key)
            {
                case 0x5A: if (shift) Redo(); else Undo(); break;
                case 0x59: Redo(); break;
                case 0x41: Active.SelectAll(); SelectTool(EditorTool.Select); break;
                case 0x43: CopySelection(); break;
                case 0x56: _ = RunCommand(PasteAsync); break;
                case 0x44: if (CanAddCanvasContent()) Active.DuplicateSelection(); break;
                case 0x47: if (shift) Active.UngroupSelection(); else Active.GroupSelection(); break;
                case 0x53: _ = RunCommand(() => SaveProjectAsync(shift)); break;
                case 0x4F: _ = RunCommand(OpenProjectAsync); break;
            }
            return;
        }
        if (key == ToolShortcutCatalog.MoreKey) { OpenMoreTools(); return; }
        if (key == ToolShortcutCatalog.StampLibraryKey) { ShowStampLibrary(); return; }
        if (key == ToolShortcutCatalog.NewStampKey) { SaveStampSelection(); return; }
        if (key is >= 0x30 and <= 0x39)
        {
            var slot = key == 0x30 ? 9 : key - 0x31;
            if (_stampLibrary.Pins[slot] is { } stamp) SelectStampSlot(stamp);
            return;
        }
        if (key == 0x2E) { _editor.Cancel(); Active.DeleteSelection(); return; }
        if (key is >= 0x25 and <= 0x28)
        {
            _editor.Cancel(); var amount = shift ? 10 : 1;
            Active.TransformSelection(Affine2D.Translation(key == 0x25 ? -amount : key == 0x27 ? amount : 0, key == 0x26 ? -amount : key == 0x28 ? amount : 0)); return;
        }
        if (ToolShortcutCatalog.IsReserved(key)) return;
        if (ToolShortcutCatalog.TryGetTool(key, out var tool)) SelectTool(tool);
    }
    private void Undo() { _inspector?.CompletePendingEdit(); _canvasCalibration.CancelDrag(); _editor.Cancel(); Active.Undo(); }
    private void Redo() { _inspector?.CompletePendingEdit(); _canvasCalibration.CancelDrag(); _editor.Cancel(); Active.Redo(); }
    private void Draw(SKSurface surface)
    {
        var canvas = surface.Canvas;
        canvas.Save(); canvas.Translate((float)_editor.ScreenOrigin.X,(float)_editor.ScreenOrigin.Y);
        var preview = _editor.RasterPreview;
        if (_host.IsEditing && _canvasCalibration.IsActive)
            _renderer.Draw(canvas, Active.Document, _assets, calibrationReference: true);
        else _overlayCache.DrawOnClearedRasterTarget(surface,Active.Document,_assets,Active.SceneRevision,Active.TransactionId,
            preview.First,preview.Last,preview.BypassCache || _canvasCalibration.IsDragging);
        if (_host.IsEditing)
        {
            using var paint = new SKPaint { Color = new SKColor(35,131,226), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            if (!_canvasCalibration.IsActive && _editor.ShouldShowSelection(_host.InteractionModifiers) && _editor.SelectionScreenCorners is { Count: 4 } corners)
            {
                DrawScreenPolygon(canvas, corners, paint);
                foreach(var p in corners) canvas.DrawRect((float)p.X-5,(float)p.Y-5,10,10,paint);
                if (_editor.SelectionScreenRotationAnchor is { } anchor && _editor.SelectionScreenRotationHandle is { } handle)
                {
                    canvas.DrawLine((float)anchor.X,(float)anchor.Y,(float)handle.X,(float)handle.Y,paint);
                    canvas.DrawCircle((float)handle.X,(float)handle.Y,6,paint);
                }
                foreach(var p in _editor.SelectionScreenEndpoints) canvas.DrawCircle((float)p.X,(float)p.Y,6,paint);
            }
            if (_editor.CapturePreview is { } capturePreview) DrawCapturePreview(canvas, capturePreview);
            else if (_editor.EffectScreenPreview is { Count: > 0 } polygon) DrawScreenPolygon(canvas, polygon, paint);
            else if (_editor.MarqueeScreenPolygon is { Count: > 0 } marquee) DrawScreenPolygon(canvas, marquee, paint);
            DrawCanvasCalibration(canvas);
        }
        canvas.Restore();
        if (_host.IsReceivingDrop) DrawImageDropHint(canvas);
    }
    private void ToggleInspector()
    {
        _editor.Cancel(); _inspectorWanted = !_inspectorWanted;
        if (_inspector == null)
        {
            _inspector = new InspectorWindow(() => Active, () => !DocumentOperationBusy) { Owner = _toolbar };
            ConfigureAppWindow(_inspector);
            _inspector.CommandKey += OnKey;
            _inspector.EyedropperRequested += BeginEyedropper;
            _inspector.Dismissed += () => _inspectorWanted = false;
        }
        if (_inspectorWanted)
        {
            var monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new(_toolbar.PointToScreen(new(0, 0)).X, _toolbar.PointToScreen(new(0, 0)).Y))) ?? _host.PrimaryMonitor;
            if (_inspector.WindowState == WindowState.Normal)
                _inspector.Height = Math.Min(_inspector.Height, Math.Max(_inspector.MinHeight, monitor.WorkArea.Height * 96 / monitor.DpiY - 24));
            _inspector.Refresh(); ShowAppWindow(_inspector, true);
        }
        else SessionWindowCoordinator.Hide(_inspector);
    }
    private void ShowTextEditor(CanvasElement? existing, PointD point)
    {
        _editor.Cancel(); _textEditor?.Close();
        var session = Active;
        var elementId = existing?.Id;
        var elementKind = existing?.Kind ?? ElementKind.Text;
        if (existing != null) session.SetSelection([existing.Id]);
        var panel = new StackPanel { Margin = new Thickness(16) };
        var input = new System.Windows.Controls.TextBox { Text = existing?.Kind == ElementKind.Number ? existing.Number.ToString() : existing?.Text ?? "", AcceptsReturn = existing?.Kind != ElementKind.Number,
            TextWrapping = TextWrapping.Wrap, MinHeight = existing?.Kind == ElementKind.Number ? 34 : 120, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 16 };
        panel.Children.Add(input);
        var dialog = new Window { Title = existing?.Kind == ElementKind.Number ? L.Get("Text.NumberTitle") : L.Get("Text.TextTitle"), Content = panel, Width = 420,
            SizeToContent = SizeToContent.Height, Topmost = true, Owner = _toolbar, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontSize = 14 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(error);
        panel.Children.Add(Button(L.Get("Text.Done"), () => Commit()));
        void Commit()
        {
            if (DocumentOperationBusy) { error.Text = L.Get("Text.Busy"); return; }
            if (!ReferenceEquals(session, Active)) { error.Text = L.Get("Text.SessionChanged"); return; }
            if (!CanAddCanvasContent()) { error.Text = L.Get("Feedback4.Canvas.Hidden"); return; }
            if (!TextEditCommand.TryCommit(session, elementId, elementKind, input.Text, _editor.Style, point, _renderer, out var failure))
            { error.Text = failure; input.Focus(); return; }
            SessionWindowCoordinator.Close(dialog);
        }
        dialog.PreviewKeyDown += (_,e)=>
        {
            if(e.Key==System.Windows.Input.Key.Escape) { SessionWindowCoordinator.Close(dialog); e.Handled=true; }
            if(e.Key==System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)!=0) { Commit(); e.Handled=true; }
        };
        dialog.Closed += (_,_)=> { _textEditor=null; _textEditorSession=null; };
        ConfigureAppWindow(dialog, true);
        _textEditor=dialog; _textEditorSession=session; ShowAppWindow(dialog, true); input.Focus(); input.SelectAll();
    }
    private void OnMonitorsChanged()
    {
        EndCanvasCalibration(hidePanel: true);
        StopLiveEffectsCapture();
        CloseImageDrop();
        _editor.Cancel();
        var bounds = _host.Monitors.Select(m=>new RectD(m.Bounds.Left,m.Bounds.Top,m.Bounds.Width,m.Bounds.Height)).Aggregate(RectD.Union);
        _live.Document.Viewport=bounds;
        if (_live.Document.Canvas.Projection.Enabled && !CanvasCalibration.IsStableViewport(_live.Document.Canvas.Projection.Matrix, bounds))
        {
            _live.Document.Canvas.Projection.Enabled = false;
            UpdateStatus(L.Get("Canvas.UnstableProjection"));
        }
        _live.Document.DisplayLayout = _host.Monitors.Select(m => new DisplayLayoutHint { DeviceName = m.DeviceName,
            Bounds = new(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height), DpiX = m.DpiX, DpiY = m.DpiY }).ToList();
        EnsureCaptureVisible();
        _overlayCache.Invalidate();
        _live.NotifyChanged();
        _toolbarPlaced=false;
        if(_host.IsEditing) PlaceToolbar();
        RequestRender(); ScheduleRecovery();
    }
    private void PlaceToolbar()
    {
        var m=_host.PrimaryMonitor;
        var target = _settings.ToolbarX is { } x && _settings.ToolbarY is { } y
            ? _host.Monitors.FirstOrDefault(a=>a.WorkArea.Contains(new OverlayPoint(x,y))) ?? m : m;
        // The whole transparent surface is centered, including the 100-DIP mascot. Its
        // crown touches the physical screen top, preserving screen-centered placement.
        var origin = SessionWindowLayout.ToolbarOrigin(target, _toolbar.ActualWidth, _toolbar.ActualHeight, _settings.ToolbarX, _settings.ToolbarY);
        OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(_toolbar).Handle,origin); _toolbarPlaced=true;
    }
    private void SaveToolbarPosition()
    {
        var p=_toolbar.PointToScreen(new System.Windows.Point(0,0));
        _settings.ToolbarX=p.X; _settings.ToolbarY=p.Y;
    }
    private void MoveIntoView()
    {
        _editor.Cancel();
        var work = _host.PrimaryMonitor.WorkArea;
        MoveIntoViewCommand.Execute(Active, new(work.Left, work.Top, work.Width, work.Height), _editor.ScreenOrigin);
    }
    private async Task RunCommand(Func<Task> action)
    {
        if(DocumentOperationBusy) { UpdateStatus(L.Get("Desktop.Busy")); return; }
        _inspector?.CompletePendingEdit();
        EndCanvasCalibration();
        _editor.Cancel();
        try { await action(); }
        catch(OperationCanceledException) { UpdateStatus(L.Get("Desktop.Cancelled")); }
        catch(Exception ex) { _diagnostics.Write("Command.Error", exception: ex); ShowError(ex.Message); }
    }
    private void RunUiAction(Action action)
    {
        if (DocumentOperationBusy) { UpdateStatus(L.Get("Desktop.Busy")); return; }
        _inspector?.CompletePendingEdit();
        action();
    }
    private void UpdateInteractionAvailability()
    {
        bool enabled = !DocumentOperationBusy;
        if (_toolbar is not null) _toolbar.IsEnabled = enabled;
        if (_inspector is not null) _inspector.IsEnabled = enabled;
        if (_textEditor is not null) _textEditor.IsEnabled = enabled;
        if (_layers is not null) _layers.IsEnabled = enabled;
        if (_canvasSettingsWindow is not null) _canvasSettingsWindow.IsEnabled = enabled;
        if (enabled) RestoreToolbarKeyboardFocus();
    }
    private void RestoreToolbarKeyboardFocus()
    {
        // Disabling the toolbar removes WPF keyboard focus. Native dialog ownership restores
        // its HWND, but enabling it does not restore the element receiving routed key events.
        // Repair only this already-foreground toolbar, synchronously; do not activate a window
        // or replace an existing text/IME/other control's keyboard focus.
        if (_disposed || !_host.IsEditing || !_host.IsKeyboardIsolationActive || _toolbar is null ||
            !_toolbar.IsVisible || !_toolbar.IsEnabled || System.Windows.Input.Keyboard.FocusedElement is not null) return;
        var handle = new WindowInteropHelper(_toolbar).Handle;
        if (handle == 0 || GetToolbarForegroundWindow() != handle) return;
        var previous = System.Windows.Input.FocusManager.GetFocusedElement(_toolbar) as UIElement;
        var target = previous is { Focusable: true, IsVisible: true, IsEnabled: true } && ReferenceEquals(Window.GetWindow(previous), _toolbar)
            ? previous : _toolbarLauncher;
        System.Windows.Input.Keyboard.Focus(target);
    }
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetToolbarForegroundWindow();
    private void ShowError(string error)
    {
        _diagnostics.Write("App.ErrorShown", new { Message = error });
        AppDialog.Show(_toolbar,error,L.Get("App.Name"),MessageBoxButton.OK,MessageBoxImage.Warning);
    }

    private void OnSystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme == "system" && !_disposed)
            _app.Dispatcher.BeginInvoke(() => { if (!_disposed) AppTheme.Apply("system"); });
    }

    private static string FormatHotkey(HotkeyBinding binding) => HotkeyDisplay.Format(binding);

    internal static bool ShouldNotifyKeyboardRelease(KeyboardLeaseEndReason reason) => reason is
        KeyboardLeaseEndReason.HeartbeatExpired or KeyboardLeaseEndReason.QueueOverflow or
        KeyboardLeaseEndReason.NotificationFailed or KeyboardLeaseEndReason.HookFailure;

    internal static void RefreshHotkeyHints(System.Windows.Controls.Button passThrough, TextBlock? welcomeShortcuts,
        HotkeyBinding toggle, HotkeyBinding hide, TextBlock? hideShortcut = null)
    {
        string toggleText = FormatHotkey(toggle);
        string label = L.Format("Toolbar.PassThrough", toggleText);
        passThrough.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(passThrough, label);
        if (welcomeShortcuts is not null)
            welcomeShortcuts.Text = hideShortcut is null ? L.Format("Welcome.Shortcuts", toggleText, FormatHotkey(hide)) : toggleText;
        if (hideShortcut is not null) hideShortcut.Text = FormatHotkey(hide);
    }

    private void ShowHotkeySettings()
    {
        _inspector?.CompletePendingEdit();
        _editor.Cancel();
        var toggle = _settings.Toggle;
        var hide = _settings.Hide;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = L.Get("Settings.Hotkeys"), FontSize = 18, FontWeight = FontWeights.SemiBold });
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
        System.Windows.Controls.TextBox Recorder(string label, HotkeyBinding initial, Action<HotkeyBinding> change)
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 6) });
            var box = new System.Windows.Controls.TextBox { Text = FormatHotkey(initial), IsReadOnly = true, Padding = new Thickness(8), MinHeight = 34 };
            box.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt
                    or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift) return;
                var modifiers = OverlayHost.CurrentModifiers;
                if (modifiers == OverlayModifiers.None) { error.Text = L.Get("Settings.ModifiersRequired"); e.Handled = true; return; }
                var binding = new HotkeyBinding((uint)modifiers, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key));
                try { KeyboardInputIsolation.ValidateHotkey(binding); }
                catch (ArgumentException ex) { error.Text = ex.Message; e.Handled = true; return; }
                change(binding);
                box.Text = FormatHotkey(binding);
                error.Text = "";
                e.Handled = true;
            };
            panel.Children.Add(box);
            return box;
        }
        Recorder(L.Get("Settings.ToggleHotkey"), toggle, value => toggle = value);
        Recorder(L.Get("Settings.HideHotkey"), hide, value => hide = value);
        panel.Children.Add(error);
        panel.Children.Add(new TextBlock { Text = L.Get("Settings.Theme"), Margin = new Thickness(0, 6, 0, 6) });
        var theme = new ComboBox { ItemsSource = new[] { L.Get("Settings.SystemTheme"), L.Get("Settings.LightTheme"), L.Get("Settings.DarkTheme") }, MinHeight = 34,
            SelectedIndex = _settings.Theme == "light" ? 1 : _settings.Theme == "dark" ? 2 : 0 };
        panel.Children.Add(theme);
        var shortcutHints = new CheckBox { Content = L.Get("Feedback3.Toolbar.ShowShortcutHints"),
            IsChecked = _settings.ShowToolShortcutHints, MinHeight = 28, Margin = new Thickness(0, 14, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(shortcutHints, L.Get("Feedback3.Toolbar.ShowShortcutHints"));
        panel.Children.Add(shortcutHints);
        WindowsShellIntegration? shell = null;
        bool oldStartup = false;
        if (_shellIntegrationAllowed)
        {
            try { shell = WindowsShellIntegration.Current(); oldStartup = shell.StartupEnabled; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { error.Text = ex.Message; }
        }
        var startup = new CheckBox { Content = L.Get("Shell.StartWithWindows"), IsChecked = oldStartup,
            IsEnabled = shell is not null, MinHeight = 28, Margin = new Thickness(0, 14, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(startup, L.Get("Shell.StartWithWindows"));
        ToolTipService.SetShowOnDisabled(startup, true);
        startup.ToolTip = L.Get(shell is null ? "Shell.TestProfileHint" : "Shell.StartupHint");
        panel.Children.Add(startup);
        panel.Children.Add(new TextBlock { Text = L.Get("Shell.StartupHint"), TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 6) });
        var registrationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(registrationStatus, L.Get("Shell.RegisterProjectType"));
        var registerFiles = Button(L.Get("Shell.RegisterProjectType"), () =>
        {
            try
            {
                shell!.RegisterProjectType();
                registrationStatus.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
                registrationStatus.Text = L.Get("Shell.Registered");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                registrationStatus.SetResourceReference(TextBlock.ForegroundProperty, "CanvasDanger");
                registrationStatus.Text = ex.Message;
            }
            registrationStatus.Visibility = Visibility.Visible;
        });
        registerFiles.IsEnabled = shell is not null;
        ToolTipService.SetShowOnDisabled(registerFiles, true);
        if (shell is null) registerFiles.ToolTip = L.Get("Shell.TestProfileHint");
        panel.Children.Add(registerFiles);
        panel.Children.Add(registrationStatus);
        var preferGpu = AddRenderSettings(panel);
        panel.Children.Add(new TextBlock { Text = L.Get("Settings.Language"), Margin = new Thickness(0, 18, 0, 6) });
        var language = new ComboBox { ItemsSource = new[] { L.Get("Settings.AutomaticLanguage"), L.Get("Settings.ChineseLanguage"), L.Get("Settings.EnglishLanguage") },
            MinHeight = 34, SelectedIndex = L.NormalizePreference(_settings.Language) == "zh-CN" ? 1 : L.NormalizePreference(_settings.Language) == "en" ? 2 : 0 };
        System.Windows.Automation.AutomationProperties.SetName(language, L.Get("Settings.Language"));
        panel.Children.Add(language);
        panel.Children.Add(new TextBlock { Text = L.Get("Settings.LanguageRestartHint"), TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 6) });
        panel.Children.Add(new TextBlock { Text = L.Get("Settings.Scope"), Margin = new Thickness(0, 18, 0, 6) });
        var screenScope = new ComboBox { ItemsSource = new[] { L.Get("Settings.PrimaryOnly"), L.Get("Settings.AllScreens") }, MinHeight = 34,
            SelectedIndex = _settings.PrimaryScreenOnly ? 0 : 1 };
        panel.Children.Add(screenScope);
        panel.Children.Add(new TextBlock { Text = L.Format("Settings.CurrentScope", _host.Scope == OverlayMonitorScope.PrimaryScreen ? L.Get("Settings.PrimaryOnly") : L.Get("Settings.AllScreens")) +
            (ApplicationPaths.RequestedMonitorScope.HasValue ? L.Get("Settings.ScopeOverrideHint") : L.Get("Settings.ScopeRestartHint")),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 6) });
        var window = new Window { Title = L.Get("Settings.Title"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Width = 460, MaxHeight = Math.Max(240, _host.PrimaryMonitor.WorkArea.Height * 96 / _host.PrimaryMonitor.DpiY - 24), SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, FontSize = 14 };
        panel.Children.Add(Button(L.Get("Settings.Save"), () =>
        {
            var oldToggle = _settings.Toggle;
            var oldHide = _settings.Hide;
            var oldTheme = _settings.Theme;
            var oldPrimaryScreenOnly = _settings.PrimaryScreenOnly;
            var oldLanguage = _settings.Language;
            var oldShortcutHints = _settings.ShowToolShortcutHints;
            var oldPreferGpu = _settings.PreferGpuAcceleration;
            try
            {
                _host.RegisterHotkeys(toggle, hide);
                _settings.Toggle = toggle;
                _settings.Hide = hide;
                _settings.Theme = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system";
                _settings.PrimaryScreenOnly = screenScope.SelectedIndex == 0;
                _settings.Language = language.SelectedIndex == 1 ? "zh-CN" : language.SelectedIndex == 2 ? "en" : "auto";
                _settings.ShowToolShortcutHints = shortcutHints.IsChecked == true;
                _settings.PreferGpuAcceleration = preferGpu.IsChecked == true;
                bool startupChanged = false;
                try
                {
                    if (shell is not null && (startup.IsChecked == true) != oldStartup)
                    {
                        shell.SetStartup(startup.IsChecked == true);
                        startupChanged = true;
                    }
                    _settings.Save();
                }
                catch
                {
                    _settings.Toggle = oldToggle;
                    _settings.Hide = oldHide;
                    _settings.Theme = oldTheme;
                    _settings.PrimaryScreenOnly = oldPrimaryScreenOnly;
                    _settings.Language = oldLanguage;
                    _settings.ShowToolShortcutHints = oldShortcutHints;
                    _settings.PreferGpuAcceleration = oldPreferGpu;
                    try { _host.RegisterHotkeys(oldToggle, oldHide); }
                    finally { if (startupChanged) shell!.SetStartup(oldStartup); }
                    throw;
                }
                RefreshHotkeyHints(_passThroughButton, _welcomeShortcuts, _settings.Toggle, _settings.Hide, _welcomeHideShortcut);
                _welcomeView?.Refresh(_settings.Toggle, _settings.Hide);
                RefreshToolShortcutHints();
                _host.ApplyRenderPreference(_settings.PreferGpuAcceleration);
                RequestRender();
                _tray.Text = L.Get("App.NamePrefix") + FormatHotkey(toggle) + L.Get("Desktop.TrayModeSuffix");
                AppTheme.Apply(_settings.Theme);
                SessionWindowCoordinator.Close(window);
                _ = CompleteStartupAsync();
                if (_host.IsEditing) RaiseVisibleToolWindows();
                UpdateStatus(oldLanguage != _settings.Language ? L.Get("Settings.LanguageSaved") :
                    oldPrimaryScreenOnly == _settings.PrimaryScreenOnly ? L.Get("Settings.Saved") :
                    ApplicationPaths.RequestedMonitorScope.HasValue ? L.Get("Settings.SavedScopeOverride") : L.Get("Settings.SavedScopeRestart"));
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }));
        ConfigureAppWindow(window, true);
        ShowAppWindow(window, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { FlushToolState(commitDraft: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UpdateStatus(ex.Message); }
        _disposed = true;
        EndCanvasCalibration(hidePanel: true);
        _canvasSettingsWindow?.CloseForDispose();
        _windowCoordinator?.Dispose();
        _toolStateSaveTimer?.Stop();
        _animationTimer?.Stop();
        _animationClock.Stop();
        StopLiveEffectsCapture();
        CloseImageDrop();
        System.Windows.Input.InputManager.Current.PreProcessInput -= OnWpfInputProgress;
        System.Windows.Input.InputManager.Current.PostProcessInput -= OnWpfPointerFocus;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        StopDocumentTimers(); _renderOperation?.Abort(); _renderOperation = null; _renderAgain = false;
        _tray.Visible = false; var trayIcon = _tray.Icon; _tray.Dispose(); trayIcon?.Dispose();
        _welcomeWindow?.Close();
        _updatesWindow?.Close();
        _textEditor?.Close();
        _host.Dispose(); _overlayCache.Dispose(); _renderer.Dispose();
        _inspector?.Close(); _layers?.Close(); _stampWindow?.Close(); _toolbar.Close();
    }
}
