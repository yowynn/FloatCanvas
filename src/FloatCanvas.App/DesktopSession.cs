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
    private readonly OverlayWindowScope? _overlayWindows;
    private readonly Window _toolbar;
    private readonly Forms.NotifyIcon _tray;
    private readonly TrayLauncherGestures _trayClicks;
    private readonly TextBlock _status = new();
    private readonly UserSettings _settings;
    private readonly ToolPresetLibrary _toolPresets;
    private readonly ColorLibrary _colorLibrary;
    private readonly Func<EditorSession> _presetHistorySession;
    private readonly StampLibrary _stampLibrary;
    private readonly AssetLibrary _assets = new();
    private readonly SceneRenderer _renderer = new();
    private readonly CanvasPreviewPump _canvasPreview;
    private readonly IDisposable? _escapeTrace;
    private readonly DispatcherResponsivenessMonitor? _dispatcherResponsiveness;
    private readonly EditorController _editor;
    private DispatcherOperation? _renderOperation;
    private EditorSession _live;
    private PointD _liveOrigin;
    private EditorSession? _capture;
    private PointD _captureOrigin;
    private InspectorWindow? _inspector;
    private LayersWindow? _layers;
    private bool _layersWanted;
    private Slider _opacitySlider = null!;
    private System.Windows.Controls.Button _passThroughButton = null!;
    private TextBlock? _welcomeShortcuts;
    private TextBlock? _welcomeHideShortcut;
    private Window? _welcomeWindow;
    private WelcomeView? _welcomeView;
    private bool _welcomeShown;
    private bool _welcomeManual;
    private readonly SessionWindowLayout _windowLayout = new();
    private readonly SessionWindowCoordinator? _windowCoordinator;
    private readonly SessionKeyboardRouter? _keyboardRouter;
    private EditorSession? _keyboardSession;
    private SceneDocument? _keyboardDocument;
    [DllImport("user32.dll", EntryPoint = "GetFocus")] private static extern nint KeyboardFocusHandle();
    private bool _syncingControls;
    private bool _disposed;
    private bool _inspectorWanted;
    private bool _renderingFrame, _renderAgain;
    private EditorSession Active => _editor.Session;
    private bool IsRenderPending => !_disposed && (_renderingFrame || _renderAgain ||
        _renderOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing);

    public DesktopSession(System.Windows.Application app) : this(app, null) { }

    // Internal fixture seam: a real native host may present a bounded tile inside the primary screen.
    // Normal startup always constructs its host from the saved scope and real monitor enumeration.
    internal DesktopSession(System.Windows.Application app, OverlayHost? testHost,
        OverlayBackendProtection? protection = null, DesktopFallbackState? fallbackState = null)
    {
        _app = app;
        _backendProtection = protection;
        _fallbackState = fallbackState;
        if (fallbackState is not null) _assets = fallbackState.Assets;
        _settings = UserSettings.Load();
        L.Initialize(_settings.Language);
        _presetHistorySession = () => Active;
        _toolPresets = new() { HistorySession = _presetHistorySession };
        _toolStateStore = new(legacyKeyboardScope: _settings.PresentationGlobal.KeyboardScope);
        _toolbarToolMemory = _toolStateStore.Tools;
        _toolbarButtonMemory = _toolStateStore.Buttons;
        _toolStateStore.Changed += ScheduleToolStateSave;
        if (_toolStateStore.LegacyKeyboardScopeMigrated) ScheduleToolStateSave();
        _colorLibrary = ColorLibrary.Shared;
        _colorLibrary.HistorySession = _presetHistorySession;
        _stampLibrary = new(ApplicationPaths.ProfileDirectory);
        AppTheme.Apply(_settings.Theme);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        _host = testHost ?? new OverlayHost(ApplicationPaths.RequestedMonitorScope ?? MonitorScopeControl.Saved(_settings).Scope,
            ApplicationPaths.RequestedMonitorScope.HasValue ? null : _settings.MonitorDeviceName);
        if(testHost is null)
        {
            string safetyJournal = Path.Combine(ApplicationPaths.ProfileDirectory, "overlay-input-safety.log");
            if (protection is null) _host.EnableSystemInput(safetyJournal);
            else protection.TryEnable(() => _host.EnableSystemInput(
                protection.AllowsAutomaticFallback ? safetyJournal : null, protection.AllowsAutomaticFallback));
        }
        if(_host.UsesSystemInput) {_overlayWindows=new(_host.SystemWindowOwner,()=>_host.IsEditing);AppTheme.Apply(_settings.Theme);}
        _diagnostics.Write("Input.Backend",new {SystemDelegation=_host.UsesSystemInput,OwnerWindow=_host.SystemWindowOwner.ToInt64(),Mode=protection?.Mode.ToString()});
        if(protection?.Warning is { } warning) _diagnostics.Write("Input.BackendWarning",new {Reason=warning});
        _host.ApplyRenderPreference(_settings.PreferGpuAcceleration);
        _host.RenderBackendChanged += () => _diagnostics.Write("Rendering.Backend", new
            { Backend = _host.RenderBackend.ToString(), _host.GpuDetails, _host.LastGpuFailure });
        _host.CanBeginCanvasGesture = () => !_disposed && !_presentationUncertain && !DocumentOperationBusy && !ComponentDispatcher.IsThreadModal;
        var keyboardBridge = new PrivateKeyboardBridge(handle => _host.Diagnostics.Any(pair => pair.InputHandle == handle),
            () => _host.UsesSystemInput && _host.HasEditingInput && _host.IsKeyboardIsolationActive);
        _host.EnableKeyboardIsolation((packet, complete) =>
        {
            if (packet.VirtualKey == 0x20 && !packet.IsLocalBalance)
                _keyboardRouter?.Space(!packet.IsKeyUp, packet.IsRepeat, packet.Modifiers,
                    System.Windows.Input.Keyboard.FocusedElement as DependencyObject,
                    canvas: _host.Diagnostics.Any(pair => pair.InputHandle == KeyboardFocusHandle()));
            if(packet.VirtualKey is 0x10 or 0xA0 or 0xA1&&!packet.IsLocalBalance)
                _editor?.ModifiersChanged(packet.Modifiers,packet.PointerPosition,packet.Time,packet.PointerLeftDown);
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
        document.Canvas = CreateDefaultCanvasSettings(document.Viewport);
        var reference = _host.PrimaryMonitor.Bounds;
        var authoringFrame = new RectD(reference.Left, reference.Top, reference.Width, reference.Height);
        document.AuthoringReference = new(authoringFrame, authoringFrame);
        _live = new(document, _assets);
        FileState(_live).SavedContentVersion = _live.ContentVersion;
        FileState(_live).SavedFingerprint = Fingerprint(document);
        _editor = new(_live) { BillboardWorkflow = _settings.BillboardWorkflow };
        _editor.BackgroundPickingEnabled = true;
        if (_toolbarToolMemory.TryGetValue(EditorTool.Freehand, out var savedPen)) _editor.ApplyParameters(savedPen);
        _toolbarToolMemory.TryGetValue(EditorTool.Number, out var savedNumber);
        _editor.NumberStart = Math.Clamp(savedNumber?.NumberStart ?? _settings.NumberStart,-999999,999999);
        _editor.NumberStep = Math.Clamp(savedNumber?.NumberStep ?? _settings.NumberStep,-999999,999999);
        _settings.NumberStart = _editor.NumberStart; _settings.NumberStep = _editor.NumberStep;
        document.NextNumber = _editor.NumberStart;
        _editor.Changed += OnEditorChanged;
        _editor.SceneCommitted += (owner, scope) =>
        {
            if (ReferenceEquals(owner, Active.Document)) return;
            var current = CurrentPreviewContext();
            if (Active.EditScopeVersion == scope && _presentedCanvasContext is { } shown &&
                ReferenceEquals(shown.Session, current.Session) && ReferenceEquals(shown.DocumentEpoch, owner) &&
                ReferenceEquals(shown.Monitors, current.Monitors) && shown.Hidden == current.Hidden && !current.Hidden &&
                shown.Origin == current.Origin && shown.View == current.View)
                _sceneExitPresentation = (shown, current);
            RequestRender();
        };
        _editor.ScreenBillboardCommitted += () =>
        {
            InvalidateScreenDraftPreview();
            if (_presentedCanvasContext is { } shown && ReferenceEquals(shown.Session, Active) &&
                ReferenceEquals(shown.DocumentEpoch, Active.Document) && shown.HasSameScene(CurrentPreviewContext()))
                _sceneExitPresentation = (shown, CurrentPreviewContext());
            RequestRender();
        };
        _editor.ScreenBillboardCancelled += (before, scope) =>
        {
            InvalidateScreenDraftPreview(); var context = CurrentPreviewContext();
            if (Active.EditScopeVersion != scope || _presentedCanvasContext is not { } shown ||
                !ReferenceEquals(shown.DocumentEpoch, before) || !ReferenceEquals(shown.Session, Active) || shown.Hidden != context.Hidden ||
                shown.View != context.View || shown.Origin != context.Origin) return;
            _sceneExitPresentation = (shown, context);
            if (_lastBaseFrame is { } basis && ReferenceEquals(basis.Context.DocumentEpoch, before) && basis.Context.HasSameContent(shown))
            {
                _cancelledDraftFrame?.Dispose();
                _cancelledDraftFrame = basis.Retain() with { Context = context, IsTransitionFallback = true };
            }
            RequestRender();
        };
        _editor.Message += UpdateStatus;
        _editor.EditText += ShowTextEditor;
        _editor.PropertiesRequested += ShowInspectorNear;
        _editor.PrefabEditRequested += id => _ = RunCommand(() => EditPrefabInstanceAsync(id));
        _editor.Cancelling += () => FinishInlineText();
        _editor.CaptureSelectionCompleted += BeginSnapshotCapture;
        _host.CaptureDiagnostic += OnCaptureDiagnostic;
        _canvasPreview = new(_app.Dispatcher, CaptureCanvasPreview, captureAsync: CaptureCanvasPreviewAsync);
        _canvasPreview.FrameReady += PresentCanvasPreview;
        _canvasPreview.RequestFailed += CanvasPreviewRequestFailed;
        _host.Draw += Draw;
        _host.FrameRendering += BeginCanvasSubmission;
        _host.FrameSubmitted += CompleteCanvasSubmission;
        _host.CaptureToolVisuals = CaptureScreenshotTools;
        _host.SystemScreenshotFinished += RestoreScreenshotTools;
        _host.Pointer += pointer=>
        {
            _canvasInputSequence++;
            if(_overlayWindows is not null && pointer.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)
            {
                using var transfer=_windowCoordinator?.DirectTransfer(WindowDismissReason.CanvasPress);
                _overlayWindows.ClosePopups();
            }
            HandleCanvasPointer(pointer);
        };
        _host.PointerLeft += ClearProjectionHover;
        _host.PointerLeft += StopCanvasEdgePan;
        _editor.Cancelling += () => { FinishKeyboardNudge(); _keyboardRouter?.CancelGesture(); CancelCanvasNavigationGesture(); };
        _editor.DesktopEffectRequested += (region, kind, amount, color) => _ = RunCommand(() => AddDesktopEffectAsync(region, kind, amount, color));
        _host.KeyInput += OnCanvasKeyInput;
        _host.KeyReleased += key => _keyboardRouter?.Release(key);
        _host.ModifierKeysChanged += (modifiers,point,time)=>
        {
            _editor.ModifiersChanged(modifiers,point,time);
            RequestRender();
        };
        _host.ToggleRequested += ToggleEditing;
        _host.HideRequested += ToggleCanvasVisibility;
        _host.CommandHotkeyRequested += ExecuteGlobalCommand;
        _host.EnableImageDrops(SupportsCanvasImageDrop, ReceiveCanvasImageDrop);
        _host.MonitorsChanged += OnMonitorsChanged;
        _host.Error += ex =>
        {
            _diagnostics.Write("Overlay.Error", exception: ex);
            if (TryRequestDesktopFallback(ex)) return;
            _app.Dispatcher.BeginInvoke(() => ShowError(ex.Message));
        };
        EditorCommandCatalog.Overrides = EditorCommandCatalog.ReconcileSavedBindings(_settings.CommandBindings,
            GlobalBindings(_settings.Toggle, _settings.Hide, _settings.GlobalCommandBindings)
                .Where(binding => binding.Value.VirtualKey != 0)
                .Select(binding => KeyValuePair.Create(new CommandChord((int)binding.Value.VirtualKey, (OverlayModifiers)binding.Value.Modifiers),
                    L.Get(binding.Key switch
                    {
                        "Global.Toggle" => "ToolbarV2.PassThrough",
                        "Global.Hide" => "Desktop.ToggleVisibility",
                        _ => "Shortcuts." + binding.Key
                    }))), out _shortcutBindingNotices);
        _toolbar = CreateToolbarV2();
        _windowCoordinator = new(_host, _toolbar) { HistoryCommand = OnKey };
        _keyboardSession = Active;
        _keyboardDocument = Active.Document;
        _keyboardRouter = new(() => !_disposed && _host.IsEditing && _host.HasEditingInput,
            _windowCoordinator.OwnsKeyboardSource, () => _inlineText is not null, OnKey,
            held =>
            {
                if (held) FinishKeyboardNudge();
                if (_canvasNavigationSpaceHeld && !held) CancelCanvasNavigationGesture();
                _canvasNavigationSpaceHeld = held;
                UpdateBrushTipDamage();
            });
        _keyboardRouter.CommandInput = OnCommandInput;
        _keyboardRouter.KeyReleased = ReleaseEditorKey;
        _keyboardRouter.Cancelled = () => { FinishKeyboardNudge(); CancelBorrowedTool(); };
        _windowCoordinator.ModalEntering += () => { _keyboardRouter.Cancel(); CancelCanvasNavigationGesture(); };
        _windowCoordinator.Register(_toolOptionsWindow, () => _toolOptionsPinned, DismissToolOptions);
        InitializeCanvasTool();
        _windowLayout.BindRoot(_toolbar);
        RefreshHotkeyHints(_passThroughButton, null, _settings.Toggle, _settings.Hide);
        _host.InputActivated += ()=>{ClearSystemInputEntryHint();if(_host.UsesSystemInput)ShowEditingWindows();RaiseVisibleToolWindows();};
        _host.EditingFocusLost += () =>
        {
            _keyboardRouter?.Cancel();
            CloseEditingDialogs();
            _overlayWindows?.ClosePopups();
            ClearProjectionHover();
            _windowCoordinator?.SuspendEditingWindows();
            EndCanvasCalibration(restoreWindows: false);
            CancelSnapshotCapture("ExternalFocus", userInitiated: false);
            CompleteGlobalCapture(inserted:false,restoreMode:false);
            using var destination = _windowCoordinator?.DirectTransfer(WindowDismissReason.ExternalFocus);
            // Do not gate focus release on an asynchronous document operation.
            CloseImageDrop();
            _editor.Cancel(); _stampWindow?.Hide(); EndEyedropper(restore: false);
            if (!HasPrefabEditContext && Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
            _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); CloseToolOptions(); RequestRender();
            UpdatePresentationRuntime();
            RefreshPassThroughLauncher();
            UpdateStatus(L.Get("Desktop.FocusLost"));
        };
        _tray = new Forms.NotifyIcon { Icon = AppIcon.CreateTrayIcon(!_host.IsVisible),
            Text = L.Get("App.NamePrefix") + FormatHotkey(_settings.Toggle), Visible = true };
        _host.KeyboardIsolationReleased += reason =>
        {
            _diagnostics.Write("Input.IsolationReleased", new { Reason = reason, _host.LastRenderMilliseconds, _host.LastCompletedRenderBackend });
            if (!ShouldNotifyKeyboardRelease(reason)) return;
            var message = L.Get("Desktop.InputInterrupted");
            UpdateStatus(message);
            _tray.ShowBalloonTip(4000, L.Get("Desktop.InputReleasedTitle"), message, Forms.ToolTipIcon.Info);
        };
        _host.SystemInputUnavailable+=()=>
        {
            if (_backendProtection?.AllowsAutomaticFallback == true) { RequestDesktopFallback("System input acquisition was interrupted or timed out."); return; }
            var message=L.Get("Desktop.InputInterrupted");UpdateStatus(message);
            _tray.ShowBalloonTip(4000,L.Get("Desktop.InputReleasedTitle"),message,Forms.ToolTipIcon.Info);
        };
        _host.SystemInputEntryBlocked += ShowSystemInputEntryBlocked;
        var menu = new Forms.ContextMenuStrip { MinimumSize = new System.Drawing.Size(340, 0) };
        menu.Items.Add(CommandGlyphs.TrayItem(L.Get("Desktop.ToggleEditing"), CommandGlyphs.EditObjects, (_, _) =>
            _app.Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleEditing)));
        menu.Items.Add(CommandGlyphs.TrayItem(L.Get("Desktop.ToggleVisibility"), CommandGlyphs.Visibility, (_, _) => ToggleCanvasVisibility()));
        menu.Items.Add(CommandGlyphs.TrayItem(L.Get("Desktop.OpenProject"), CommandGlyphs.Open, async (_, _) => await RunCommand(OpenProjectAsync)));
        menu.Items.Add(CommandGlyphs.TrayItem(L.Get("Desktop.RecoveryHistory"), CommandGlyphs.History, async (_, _) => await RunCommand(RestoreWorkspaceHistoryAsync)));
        menu.Items.Add(CommandGlyphs.TrayItem(L.Get("Desktop.HotkeySettings"), CommandGlyphs.Settings, (_, _) => RunUiAction(ShowHotkeySettings)));
        if (_host.UsesSystemInput && protection?.AllowsAutomaticFallback == true)
            menu.Items.Add(CommandGlyphs.TrayItem(L.Get("OverlaySafety.DesktopCommand"), CommandGlyphs.Windows,
                (_, _) => RequestDesktopFallback("User selected ordinary desktop mode.")));
        menu.Items.Add(CommandGlyphs.TrayItem(L.Get("Desktop.Exit"), CommandGlyphs.Exit, async (_, _) => await ExitAfterSnapshotAsync("TrayMenu")));
        menu.Items.Insert(4, CommandGlyphs.TrayItem(L.Get("Welcome.Open"), CommandGlyphs.Welcome, (_, _) => RunUiAction(() => OpenWelcome(true))));
        var floatingLogo = CommandGlyphs.TrayItem(L.Get("Settings.PassThroughLauncher"), CommandGlyphs.FloatingLogo, (_, _) => RunUiAction(ToggleFloatingLogo));
        menu.Items.Insert(5, floatingLogo);
        menu.Opening += (_, _) => floatingLogo.Checked = _settings.ShowPassThroughLauncher;
        _tray.ContextMenuStrip = menu;
        _trayClicks = new(_tray, () => RunUiAction(() => SetEditing(true)), () => RunUiAction(ToggleCanvasVisibility));
        menu.Opening += (_, _) =>
        {
            _trayClicks.Cancel();
            ((Forms.ToolStripMenuItem)menu.Items[0]).ShortcutKeyDisplayString = FormatHotkey(_settings.Toggle);
            ((Forms.ToolStripMenuItem)menu.Items[1]).ShortcutKeyDisplayString = FormatHotkey(_settings.Hide);
            ((Forms.ToolStripMenuItem)menu.Items[2]).ShortcutKeyDisplayString = "Ctrl+O";
            menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == L.Get("Desktop.HotkeySettings")).ShortcutKeyDisplayString = "Ctrl+,";
        };
        _escapeTrace = EscapeInputTrace.Start(_diagnostics, CaptureEscapeState);
        _dispatcherResponsiveness = new(_app.Dispatcher, _diagnostics);
        InitializeWindowSources();
        InitializePresentation();
    }

    private object CaptureEscapeState() => new
    {
        Editing = _host.IsEditing,
        CanvasVisible = _host.IsVisible,
        Enhanced = _host.UsesSystemInput,
        EditingInput = _host.HasEditingInput,
        Tool = _editor.Tool.ToString(),
        ToolbarVisible = _toolbar.IsVisible,
        ToolbarExpanded = _settings.ToolbarExpanded,
        MainMenuOpen = _toolbarMenu.IsOpen,
        MoreToolsExpanded = _toolbarUpperRow.IsExpanded,
        ToolOptionsOpen = IsToolOptionsOpen,
        ToolOptionsPinned = _toolOptionsPinned,
        ToolColorOpen = _toolOptionsPanel.IsColorPickerOpen,
        CanvasSettingsOpen = IsCanvasSettingsOpen,
        Transaction = Active.IsTransactionActive,
        Marquee = _editor.Marquee is not null,
        NewCanvasConfirmation = _newCanvasButton?.IsConfirming == true,
        SnapshotPending = _snapshotCaptureRequest is not null,
        InlineText = _inlineText is not null,
        Eyedropper = _eyedropper is not null,
        DocumentBusy = DocumentOperationBusy
    };

    public async void Start(string[] args)
    {
        _smokeStartup = args.Contains("--smoke");
        _shellIntegrationAllowed = ProjectLaunchRequest.AllowsRegistration(args);
        _startupPending = true;
        _quietStartup = _fallbackState is not null || args.Contains("--startup");
        string? requestedProject = _fallbackState is null ? ProjectLaunchRequest.Parse(args) : null;
        if (requestedProject is not null) QueueProjectOpen(requestedProject, enterEditing: true);
        try
        {
            if (_fallbackState is { } restored) RestoreFallbackWorkspace(restored);
            else await InitializeDocumentsAsync(args.Contains("--recover"));
            try { await _stampLibrary.LoadAsync(_documentLifetime.Token); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException)
            {
                UpdateStatus(ex.Message);
                _tray.ShowBalloonTip(5000, L.Get("Stamp.LibraryTitle"), ex.Message, Forms.ToolTipIcon.Warning);
            }
            OnMonitorsChanged();
            _host.RegisterCommandHotkeys(GlobalBindings(_settings.Toggle, _settings.Hide, _settings.GlobalCommandBindings));
            NotifyDesktopFallback();
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
                    SystemDelegation = _host.UsesSystemInput,
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

    private void ShowWelcome() => OpenWelcome(false);
    private void OpenWelcome(bool manuallyOpened)
    {
        if (!manuallyOpened && _host.IsEditing) return;
        if (_host.IsEditing) SetEditingCore(false, preserveImageDrop: false, restorePreviousForeground: false);
        if (_welcomeWindow is { } existing)
        {
            if (manuallyOpened) { _welcomeManual=true; ShowWelcomeWindow(existing, true); }
            return;
        }
        if (!manuallyOpened && (_welcomeShown || _host.IsEditing)) return;
        _welcomeShown = true;
        _welcomeManual=manuallyOpened;
        var view = new WelcomeView(_settings.Toggle, _settings.Hide, () => RunUiAction(() => SetEditing(true)), ShowUpdates,
            () => RunUiAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GitHubUpdateClient.ProjectUrl.AbsoluteUri) {UseShellExecute=true})),
            () => _ = RunCommand(RestoreWorkspaceHistoryAsync), RecoveryHistory);
        _welcomeView = view; _welcomeShortcuts = view.ToggleShortcut; _welcomeHideShortcut = view.HideShortcut;
        var welcome = new Window { Title = L.Get("Welcome.Title"), Content = view, Width = WelcomeView.PreferredWidth, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, FontSize = 14 };
        _welcomeWindow = welcome;
        welcome.Closed += (_, _) => { _welcomeWindow = null; _welcomeView = null; _welcomeShortcuts = null; _welcomeHideShortcut = null; };
        ConfigureAppWindow(welcome, true, regular: true);
        // The first-run surface is a normal discoverable entry while the toolbar is hidden.
        welcome.ShowInTaskbar = true;
        ShowWelcomeWindow(welcome, manuallyOpened);
    }

    private sealed record ToolOption(EditorTool Tool, string Key)
    {
        public string Label => L.Get(Key);
    }
    private static readonly ToolOption[] ToolOptions = [new(EditorTool.Select,"Tool.Select"),new(EditorTool.Freehand,"Tool.Freehand"),new(EditorTool.Highlighter,"Tool.Highlighter"),
        new(EditorTool.Line,"Tool.Line"),new(EditorTool.Arrow,"Tool.Arrow"),new(EditorTool.DoubleArrow,"Tool.DoubleArrow"),new(EditorTool.Rectangle,"Tool.Rectangle"),new(EditorTool.RoundedRectangle,"Tool.RoundedRectangle"),
        new(EditorTool.Ellipse,"Tool.Ellipse"),new(EditorTool.Symbol,"Tool.Symbol"),new(EditorTool.Emoji,"Tool.Emoji"),new(EditorTool.Text,"Tool.Text"),new(EditorTool.Number,"Tool.Number"),new(EditorTool.Eraser,"Tool.Eraser"),
        new(EditorTool.Crop,"Tool.Crop"),new(EditorTool.Blur,"Tool.Blur"),new(EditorTool.Pixelate,"Tool.Pixelate"),new(EditorTool.CaptureRegion,"Tool.CaptureRegion"),new(EditorTool.Canvas,"Canvas.Title")];
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
        => SetEditingCore(editing, preserveImageDrop, restorePreviousForeground: true);

    private bool SetEditingCore(bool editing, bool preserveImageDrop, bool restorePreviousForeground)
    {
        if (editing && _desktopFallbackPending) return false;
        if(!editing) CompleteGlobalCapture(inserted:false,restoreMode:false);
        ClearProjectionHover();
        if (!editing) { CloseEditingDialogs(); _windowCoordinator?.SuspendEditingWindows(); EndCanvasCalibration(restoreWindows: false); }
        if (!editing) { _keyboardRouter?.Cancel(); CancelCanvasNavigationGesture(); }
        CancelSnapshotCapture(editing ? "ExplicitEntry" : "ModeExit");
        using var destination = editing ? null : _windowCoordinator?.DirectTransfer(WindowDismissReason.ModeExit);
        _newCanvasButton?.CancelConfirmation();
        // Close the startup window before acquiring the input lease so native close
        // focus restoration cannot interrupt a newly acquired editing session.
        if (editing) CloseRegularWindows();
        if (!preserveImageDrop) CloseImageDrop();
        bool wasEditing = _host.IsEditing;
        _inspector?.CompletePendingEdit();
        _editor.Cancel();
        if (!editing && !HasPrefabEditContext && Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
        if (editing && !_host.TryEnterEditing(out var failure))
        {
            // Never leave an opaque frozen screenshot over a desktop that is receiving input.
            if (!HasPrefabEditContext) ReturnToLive(); else _host.SetVisible(false);
            _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); CloseToolOptions();
            UpdateStatus(failure!);
            _tray.ShowBalloonTip(4000, L.Get("Desktop.EnterEditFailed"), failure!, Forms.ToolTipIcon.Info);
            RequestRender();
            RefreshPassThroughLauncher();
            if (_backendProtection is not null) RequestDesktopFallback(failure ?? "Input acquisition failed.");
            return false;
        }
        if (!editing) {_overlayWindows?.ClosePopups();_host.ExitEditing(restorePreviousForeground);}
        if (editing)
        {
            ShowEditingWindows();
            if (!wasEditing) UpdateStatus(L.Get("Desktop.EditModePrefix") + (ToolOptions.FirstOrDefault(x => x.Tool == _editor.Tool)?.Label ?? L.Get("Desktop.SelectTool")));
        }
        else { _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); _stampWindow?.Hide(); CloseToolOptions(); EndEyedropper(restore: false); }
        RefreshPassThroughLauncher();
        SynchronizeCanvasTool();
        UpdateHiddenCanvasState();
        UpdatePresentationRuntime();
        RequestRender();
        return true;
    }
    private void ShowEditingWindows()
    {
        if(!_host.IsEditing || (_host.UsesSystemInput && !_host.HasEditingInput))return;
        // The passive mascot may have moved against the right edge. Fit the full
        // toolbar before showing it, and keep the fitted point as the shared origin.
        PlaceToolbar();
        SessionWindowCoordinator.Show(_toolbar);
        RefreshPassThroughLauncher();
        // Native canvas activation can occur on every placement/sample press. Keep
        // the captured windows concealed until that complete interaction restores them.
        if (_canvasPlacementSession is not null || _eyedropper is not null) return;
        if(_inspectorWanted && _inspector is not null){_inspector.PrepareForShow();ShowAppWindow(_inspector);}
        if(_layersWanted && _layers is not null){_layers.PrepareForShow();ShowAppWindow(_layers);}
        _windowCoordinator?.ResumeEditingWindows();
    }
    private void RaiseVisibleToolWindows()
    {
        var windows = _app.Windows.Cast<Window>();
        foreach (var window in _windowCoordinator?.OrderForRaise(windows) ?? windows)
            if (window is { IsVisible: true } && new WindowInteropHelper(window).Handle is var handle && handle != 0)
                OverlayCoordinates.RaiseToolWindow(handle);
        OverlayPopupWindow.RaiseOpenPopups();
    }
    private void OnEditorChanged()
    {
        using var sceneRead = SceneGraph.BeginReadScope();
        if (_changingPrefabContext) return;
        if (!ReferenceEquals(_keyboardSession, Active) || !ReferenceEquals(_keyboardDocument, Active.Document))
        {
            _keyboardSession = Active; _keyboardDocument = Active.Document;
            _keyboardRouter?.Cancel();
            CancelCanvasNavigationGesture();
        }
        ApplyPointerToolPreferences();
        ValidatePendingWindowInsertion();
        InvalidateScreenDraftPreview();
        UpdatePresentationRuntime();
        RefreshPresentationRuntimeUi();
        UpdateWindowSourceRuntime();
        RefreshWindowSourcesUi();
        OnPrefabEditorChanged();
        RefreshEditingScopeIfChanged();
        UpdateHiddenCanvasState();
        if (_eyedropper is null) _host.SetPointerCursor(_editor.IsPointerBlocked ? Forms.Cursors.No.Handle : 0);
        ValidateCanvasCalibrationSession();
        if (_liveEffectPreparation is { } pending && pending.Tool != _editor.Tool) CancelLivePreparation();
        RequestRender(); ScheduleRecovery();
    }

    private void RequestRender()
    {
        // Session replacement raises Changed before the prefab context stack is
        // switched. The transition schedules its complete frame after both agree.
        if (_changingPrefabContext) return;
        SynchronizeLiveDisplayLayout();
        // Observe transitions when requested, even if A -> B -> A changes are
        // coalesced before the next draw. An old A frame must not regain its epoch.
        if (_canvasPreview is not null) _ = CurrentPreviewContext();
        _pixelsOnlyRender = false;
        if (_disposed) return;
        if (_renderingFrame) { _renderAgain = true; return; }
        if (IsRenderPending)
        {
            // An edit can arrive while a background-only refresh is queued.
            // Keep edit feedback at the normal render priority.
            if (_renderOperation is { Status: DispatcherOperationStatus.Pending } pending && pending.Priority < DispatcherPriority.Render)
                pending.Priority = DispatcherPriority.Render;
            return;
        }
        // Coalesce a batch of changes without adding a fixed timer delay to every pointer update.
        // BeginInvoke also preserves the application's DispatcherUnhandledException reporting path.
        _renderOperation = _app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderFrame));
    }
    private void RequestPixelRender(IEnumerable<PixelBounds>? dirtyRegions = null)
    {
        // Animation and live background frames change pixels, not controls or document
        // state. Let pending input run before these continuously arriving frames.
        // A queued full edit render always takes precedence over this shortcut.
        if (_disposed || _presentationUncertain) return;
        _pixelDirtyRegions.AddRange(dirtyRegions ?? _host.Monitors.Select(monitor => monitor.Bounds));
        if (IsRenderPending) return;
        _pixelsOnlyRender = true;
        _renderOperation = _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RenderFrame));
    }
    private void RenderFrame()
    {
        using var sceneRead = SceneGraph.BeginReadScope();
        _renderOperation = null;
        if (_disposed) return;
        _renderingFrame = true;
        try
        {
            bool pixelsOnly = _pixelsOnlyRender;
            bool presentationPixels = _presentationPixelsDirty || _windowPixelsDirty; _presentationPixelsDirty = false; _windowPixelsDirty = false;
            bool pixelInvalidation = _pixelDirtyRegions.Count > 0;
            var dirtyRegions = pixelInvalidation ? _pixelDirtyRegions.ToArray() : null;
            _pixelDirtyRegions.Clear();
            _pixelsOnlyRender = false;
            PrepareAnimationFrame();
            if (!pixelsOnly) SyncLiveEffectsCapture();
            RequestCanvasPreview(pixelsOnly || pixelInvalidation || presentationPixels,
                dirtyRegions ?? (presentationPixels ? Array.Empty<PixelBounds>() : null));
            // Heavy scene computation, GPU filtering and readback happen on the preview worker.
            // The UI only copies completed CPU tiles and draws current handles.
            _host.SetGpuWorkload(false);
            PresentCanvasControls(dirtyRegions);
            // A native caption loop can delay control timers even while frames
            // complete. Successful UI work is progress; a blocked render is not.
            _host.NotifyInputProgress();
            ScheduleAnimationFrame();
            if (pixelsOnly) return;
            _syncingControls = true;
            try
            {
                RefreshToolbarState();
                if (!Active.IsTransactionActive) _opacitySlider.Value = CurrentCanvasLayerOpacity;
            }
            finally { _syncingControls = false; }
            if (!Active.IsTransactionActive)
            {
                if(_inspector is {IsVisible:true})_inspector.Refresh();
                if(_layers is {IsVisible:true})_layers.Refresh();
                RefreshPrefabWindows();
                if(_canvasToolOptions is {IsVisible:true})RefreshCanvasSettingsTarget();
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
        if (e.StagingItem.Input is System.Windows.Input.MouseButtonEventArgs
            { ButtonState: System.Windows.Input.MouseButtonState.Pressed } or System.Windows.Input.MouseWheelEventArgs)
            FinishKeyboardNudge();
        if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs) ClearProjectionHover();
        if (e.StagingItem.Input is System.Windows.Input.MouseButtonEventArgs { ButtonState: System.Windows.Input.MouseButtonState.Pressed } mouse)
        {
            var source=mouse.OriginalSource as DependencyObject??mouse.MouseDevice.Target as DependencyObject;
            if(_inlineText is not null&&source is not null&&_canvasTextInput?.Owns(source)!=true)FinishInlineText();
            _windowCoordinator?.ObservePointerPress(mouse);
        }
        if (e.StagingItem.Input is System.Windows.Input.KeyEventArgs { Key: System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl })
            RequestRender();
        if(e.StagingItem.Input is System.Windows.Input.KeyEventArgs {Key:System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift} shiftKey)
            _editor.ModifiersChanged(OverlayHost.CurrentModifiers,time:unchecked((uint)shiftKey.Timestamp));
    }
    private void OnWpfPointerFocus(object sender, System.Windows.Input.ProcessInputEventArgs e)
    {
        if (_disposed || !_host.IsEditing || e.StagingItem.Input is not System.Windows.Input.MouseButtonEventArgs
            { ButtonState: System.Windows.Input.MouseButtonState.Pressed, OriginalSource: DependencyObject source }) return;
        if (_newCanvasButton is { IsConfirming: true } clear && !Ancestors(source).Contains(clear)) clear.CancelConfirmation();
        // WPF menus own their focus/capture lifetime until the mouse release invokes
        // the item. Moving native focus into that Popup can cancel the pending click.
        // The press can open a root menu before PostProcessInput runs. Its
        // original toolbar source must not take focus back from that new menu.
        if (!ShouldSynchronizePointerFocus(source) || OverlayContextMenu.OwnsKeyboardFocus) return;
        // A native canvas drag owns its whole gesture, even when it crosses a panel.
        nint capture = _host.CaptureHandle;
        if (capture != 0 && _host.Diagnostics.Any(pair => pair.InputHandle == capture)) return;
        if(_host.UsesSystemInput && PresentationSource.FromDependencyObject(source) is HwndSource realSource)
        {
            var logicalFocus=System.Windows.Input.Keyboard.FocusedElement;
            if(_host.TryFocusEditingWindow(realSource.Handle) && logicalFocus is DependencyObject focused &&
                ReferenceEquals(PresentationSource.FromDependencyObject(focused),realSource)) System.Windows.Input.Keyboard.Focus(logicalFocus);
            return;
        }
        PrivateKeyboardBridge.SynchronizePointerFocus(source);
    }
    internal static bool ShouldSynchronizePointerFocus(DependencyObject source) =>
        !Ancestors(source).Any(value => value is MenuItem or System.Windows.Controls.Primitives.MenuBase);
    private void SelectTool(EditorTool tool)
    {
        ActivateTool(tool);
    }
    private void OnCanvasKeyInput(OverlayKeyEvent key)
    {
        _keyboardRouter?.CanvasKey(key);
    }
    private void OnKey(int key)
    {
        var modifiers = _commandModifiers ?? OverlayHost.CurrentModifiers;
        if (SessionKeyboardRouter.IsModal) return;
        if (HandleCanvasSampleKey(key, modifiers)) return;
        var command = EditorCommandCatalog.Resolve(key, modifiers);
        if (command is null) return;
        if (FinishKeyboardNudge(cancel: command.Id == "Interaction.Cancel") && command.Id == "Interaction.Cancel") return;
        if (command.Id == "Interaction.Cancel" &&
            (_opacitySlider is ToolbarOpacitySlider opacity && opacity.CancelPointerGesture() || CancelCanvasNavigationWithEscape())) return;
        if (_commandModifiers is null && command.Tool is not null)
        { OnCommandInput(new(key,modifiers,false)); return; }
        if (key == 0x09 && modifiers == OverlayModifiers.None)
        {
            if (_inlineText is null && _layers?.IsRenaming != true) ToggleToolbarExpanded();
            return;
        }
        if(_inlineText is not null && modifiers == OverlayModifiers.None && key is 0x1B or 0x0D)
        {FinishInlineText(key==0x0D,selectEdited:key==0x0D);return;}
        if (modifiers == OverlayModifiers.None && key == 0x1B && _canvasRectificationRequest is not null) { CancelCanvasRectification("Escape"); return; }
        if (modifiers == OverlayModifiers.None && key == 0x1B && _editor.Tool == EditorTool.Canvas &&
            (_canvasCalibration.IsDragging || _canvasGridScale.IsDragging || _canvasCalibration.IsPlacing ||
                IsCanvasSettingsOpen && !_toolOptionsPinned))
        {
            if (_canvasCalibration.IsDragging) _canvasCalibration.CancelDrag();
            else if (_canvasGridScale.IsDragging) _canvasGridScale.Cancel();
            else if (_canvasCalibration.IsPlacing) CancelCanvasFourPoints();
            else DismissCanvasSettings();
            RequestRender(); return;
        }
        if (modifiers == OverlayModifiers.None && key == 0x1B && _snapshotCaptureRequest is not null) { CancelSnapshotCapture("Escape"); return; }
        if (modifiers == OverlayModifiers.None && key == 0x1B && _globalCapture is not null) {CompleteGlobalCapture(inserted:false);return;}
        if (_newCanvasButton is { IsConfirming: true } clear && command.Id != "File.New")
        {
            clear.CancelConfirmation();
            if (key == 0x1B) return;
        }
        if (DocumentOperationBusy || _layers?.IsRenaming == true) return;
        _inspector?.CompletePendingEdit();
        if (modifiers == OverlayModifiers.None && _editor.HandleCaptureKey(key)) return;
        if (_commandModifiers is not null && command.Id.StartsWith("Move.", StringComparison.Ordinal))
        { BeginKeyboardNudge(command, new(key, modifiers)); return; }
        ExecuteEditorCommand(command);
    }
    private void RunSafeSelectionCommand(Action command)
    {
        try { command(); }
        catch (InvalidOperationException error) when (error.Message == L.Get("Perspective.InputOutsidePlane"))
        { UpdateStatus(error.Message); }
    }
    private void Undo() => ReplayHistory(redo: false);
    private void Redo() => ReplayHistory(redo: true);
    private void ReplayHistory(bool redo)
    {
        _inspector?.CompletePendingEdit(); _canvasGridScale?.Cancel(); _canvasCalibration.CancelDrag(); _editor.Cancel();
        try
        {
            if (redo) Active.Redo(); else Active.Undo();
            SynchronizeCanvasTool();
            _toolOptionsPanel.RefreshPresets();
            RefreshCanvasSettingsTarget();
            foreach (var window in new Window?[] { _toolOptionsWindow, _inspector })
                if (window is { IsVisible: true }) RefreshColorPresets(window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { UpdateStatus(ex.Message); }
    }
    private static void RefreshColorPresets(DependencyObject root)
    {
        if (root is ColorField field) { field.RefreshPresets(); return; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshColorPresets(VisualTreeHelper.GetChild(root, i));
    }
    private void Draw(SKSurface surface)
    {
        using var sceneRead = SceneGraph.BeginReadScope();
        var canvas = surface.Canvas;
        var context = CurrentPreviewContext();
        bool current = _submittingCanvasFrame is { } frame && CanPresentCanvasFrame(frame, context);
        if (current)
            _submittingCanvasFrame!.Pixels.Draw(canvas);
        else
        {
            // A rebuilt HWND was cleared without current content. Old retained
            // pixels cannot be a damage baseline for this new display surface.
            _presentedPixels?.Dispose(); _presentedPixels = null;
            RequestCanvasPreview(false, null);
        }
        canvas.Save(); canvas.Translate((float)_editor.CanvasOrigin.X,(float)_editor.CanvasOrigin.Y);
        if (_host.IsEditing && _hasEditableCanvasOccurrence && !Active.IsReadOnly && _editor.InteractionTool != EditorTool.PointerPresentation)
        {
            DrawPresentationLayerGuides(canvas);
            DrawProjectionHover(canvas);
            using var paint = new SKPaint { Color = new SKColor(35,131,226), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            DrawSelectionFrame(canvas, paint, _host.InteractionModifiers);
            if (_editor.CapturePreview is { } capturePreview) DrawCapturePreview(canvas, capturePreview);
            else if (_editor.EffectScreenPreview is { Count: > 0 } polygon) DrawScreenPolygon(canvas, polygon, paint);
            else if (_editor.MarqueeScreenPolygon is { Count: > 0 } marquee) DrawScreenPolygon(canvas, marquee, paint);
            DrawBillboardGuides(canvas);
            DrawCanvasCalibration(canvas);
            DrawInlineText(canvas);
            DrawBrushTip(canvas);
        }
        canvas.Restore();
        DrawCanvasViewingBadge(canvas);
        DrawPresentationPointer(canvas);
        if (_host.IsEditing) DrawEyedropperPreview(canvas);
        if (_host.IsReceivingDrop) DrawImageDropHint(canvas);
        _presentedCanvasContext = current ? context : null;
        _canvasControlsWereVisible = CanvasControlsVisible();
    }

    private void DrawSelectionFrame(SKCanvas canvas, SKPaint paint, OverlayModifiers modifiers)
    {
        if (_inlineText is not null || _canvasCalibration.IsActive || !_editor.ShouldShowSelection(modifiers) ||
            _editor.SelectionScreenCorners is not { Count: 4 } corners) return;
        DrawScreenPolygon(canvas, corners, paint);
        foreach(var p in corners) canvas.DrawRect((float)p.X-5,(float)p.Y-5,10,10,paint);
        if (EditorController.RotationAnchor(corners) is { } anchor && EditorController.RotationHandle(corners) is { } handle)
        {
            canvas.DrawLine((float)anchor.X,(float)anchor.Y,(float)handle.X,(float)handle.Y,paint);
            canvas.DrawCircle((float)handle.X,(float)handle.Y,6,paint);
        }
        foreach(var p in _editor.SelectionScreenEndpoints) canvas.DrawCircle((float)p.X,(float)p.Y,6,paint);
    }
    private void ToggleInspector()
        => ToggleInspectorAt(null);

    private void ToggleInspectorAt(PointD? near)
    {
        _editor.Cancel(); _inspectorWanted = !_inspectorWanted;
        if (_inspector == null)
        {
            _inspector = new InspectorWindow(() => Active, () => !DocumentOperationBusy && _host.IsVisible && !Active.IsReadOnly, hasSharedCardContext: () => _editor.HasSharedCardContext) { Owner = _toolbar };
            ConfigurePresentationUi(_inspector);
            ConfigureWindowSourcesUi(_inspector);
            ConfigureAppWindow(_inspector);
            _inspector.CommandKey += OnKey;
            _inspector.EyedropperRequested += BeginEyedropper;
            _inspector.PrefabEditRequested += id => _ = RunCommand(() => EditPrefabInstanceAsync(id));
            _inspector.Dismissed += () => _inspectorWanted = false;
            _windowCoordinator?.Register(_inspector,()=>_inspector.IsPinned,reason=>_inspector.Dismiss(reason));
        }
        if (_inspectorWanted)
        {
            var monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new(_toolbar.PointToScreen(new(0, 0)).X, _toolbar.PointToScreen(new(0, 0)).Y))) ?? _host.PrimaryMonitor;
            if (_inspector.WindowState == WindowState.Normal)
                _inspector.Height = Math.Min(_inspector.Height, Math.Max(_inspector.MinHeight, monitor.WorkArea.Height * 96 / monitor.DpiY - 24));
            _inspector.PrepareForShow();
            if (near is { } point) ShowAnchoredToolWindow(_inspector, false, () => PositionInspectorNear(point));
            else ShowAppWindow(_inspector, true);
        }
        else _inspector.Dismiss();
    }
    private void ShowInspectorNear(PointD point)
    {
        if(_inspector?.IsVisible==true)return;
        ToggleInspectorAt(point);
    }
    private void PositionInspectorNear(PointD point)
    {
        if(_inspector is null)return;
        _inspector.UpdateLayout();
        var screen=point+_editor.CanvasOrigin;
        var monitor=_host.Monitors.FirstOrDefault(item=>item.Bounds.Contains(new(screen.X,screen.Y)))??_host.PrimaryMonitor;
        double sx=monitor.DpiX/96d,sy=monitor.DpiY/96d;
        var work=monitor.WorkArea;
        OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(_inspector).Handle,new(
            Math.Clamp(screen.X+12*sx,work.Left,Math.Max(work.Left,work.Right-_inspector.ActualWidth*sx)),
            Math.Clamp(screen.Y+12*sy,work.Top,Math.Max(work.Top,work.Bottom-_inspector.ActualHeight*sy))));
    }
    private void OnMonitorsChanged()
    {
        ClearProjectionHover();
        EndCanvasCalibration(hidePanel: true);
        CancelSnapshotCapture("MonitorsChanged");
        StopLiveEffectsCapture();
        CloseImageDrop();
        _editor.Cancel();
        SynchronizeLiveDisplayLayout();
        if (_live.Document.Canvas.Projection.Enabled && !CanvasCalibration.IsValidProjection(_live.Document.Canvas.Projection.Matrix))
        {
            _live.Document.Canvas.Projection.Enabled = false;
            UpdateStatus(L.Get("Canvas.UnstableProjection"));
        }
        EnsureCaptureVisible();
        SynchronizeFlatGridOrigin();
        RefreshCanvasReferenceMonitors();
        _live.NotifyDisplayLayoutChanged();
        if(_host.IsEditing) PlaceToolbar();
        RefreshPassThroughLauncher();
        RequestRender(); ScheduleRecovery();
    }
    private SceneDocument? _liveLayoutDocument;
    private object? _liveLayoutMonitors;
    private void SynchronizeLiveDisplayLayout()
    {
        if (_live is null || _host is null || _host.Monitors.Count == 0) return;
        var document = _live.Document;
        document.AuthoringReference ??= CanvasAuthoringReference.FromSavedDocument(document);
        if (ReferenceEquals(_liveLayoutDocument, document) && ReferenceEquals(_liveLayoutMonitors, _host.Monitors))
        {
            if (ReferenceEquals(_editor?.Session, _live)) _editor.ScreenOrigin = _liveOrigin;
            return;
        }
        var reference = _host.PrimaryMonitor.Bounds;
        var savedCenter = document.AuthoringReference.ReferenceScreenBounds.Center;
        _liveOrigin = new PointD(reference.Left + reference.Width / 2d, reference.Top + reference.Height / 2d) - savedCenter;
        RectD Local(MonitorInfo monitor) => new(monitor.Bounds.Left - _liveOrigin.X, monitor.Bounds.Top - _liveOrigin.Y,
            monitor.Bounds.Width, monitor.Bounds.Height);
        document.Canvas = document.Canvas.ResolveReference(document.Viewport);
        document.Viewport = _host.Monitors.Select(Local).Aggregate(RectD.Union);
        document.DisplayLayout = _host.Monitors.Select(m => new DisplayLayoutHint { DeviceName = m.DeviceName,
            Bounds = Local(m), DpiX = m.DpiX, DpiY = m.DpiY }).ToList();
        if (ReferenceEquals(_editor?.Session, _live)) _editor.ScreenOrigin = _liveOrigin;
        _liveLayoutDocument = document; _liveLayoutMonitors = _host.Monitors;
    }
    private void PlaceToolbar()
    {
        var m=_host.PrimaryMonitor;
        var surface = (FrameworkElement)_toolbar.Content;
        surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // A saved position may legally straddle adjacent active screens. The
        // native bounds hook constrains it against their union using the actual frame size.
        var origin = _settings.ToolbarX is { } savedX && _settings.ToolbarY is { } savedY
            ? new OverlayPoint(savedX, savedY)
            : SessionWindowLayout.ToolbarOrigin(m, surface.DesiredSize.Width, surface.DesiredSize.Height, null, null);
        var handle = new WindowInteropHelper(_toolbar).EnsureHandle();
        OverlayCoordinates.PositionToolWindow(handle,origin);
        SaveToolbarPosition();
    }
    private void SaveToolbarPosition()
    {
        if (!SessionWindowLayout.TryOrigin(_toolbar, out var origin)) return;
        _settings.ToolbarX=origin.X; _settings.ToolbarY=origin.Y;
    }
    private void MoveIntoView()
    {
        if (DocumentOperationBusy) return;
        _editor.Cancel();
        MoveIntoViewCommand.Execute(Active, _host.Monitors.Select(m => (new RectD(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height),
            new RectD(m.WorkArea.Left, m.WorkArea.Top, m.WorkArea.Width, m.WorkArea.Height))), _editor.ScreenOrigin);
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
        finally { SynchronizeCanvasTool(); }
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
        if (!enabled) FinishKeyboardNudge();
        if (_toolbar is not null) _toolbar.IsEnabled = enabled;
        if (_inspector is not null) _inspector.IsEnabled = enabled;
        if (_layers is not null) _layers.IsEnabled = enabled;
        if (_toolOptionsWindow is not null)
        {
            if (!enabled) _toolOptionsPanel.CancelEditing();
            _toolOptionsWindow.IsEnabled = enabled;
        }
        if (_editor is not null) { RefreshHistoryCommandState(); SynchronizeCanvasTool(); UpdateWindowSourceRuntime(); RefreshWindowSourcesUi(); }
        // Document operations may replace presentation layers while observation is
        // suspended. Reconcile on both busy edges, including pass-through opens
        // where no subsequent editing/input event can restart the observer.
        UpdatePresentationRuntime();
        if (enabled) _windowCoordinator?.RestoreKeyboardDestination();
        if (enabled && _toolbar is not null && !_disposed) RequestRender();
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

    private static string FormatHotkey(HotkeyBinding binding) => binding.VirtualKey==0?L.Get("Shortcuts.Unbound"):HotkeyDisplay.Format(binding);

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

    private Window? _settingsWindow;
    private void ShowHotkeySettings()
    {
        if (_settingsWindow?.IsVisible == true) return;
        _inspector?.CompletePendingEdit();
        _editor.Cancel();
        var toggle = _settings.Toggle;
        var hide = _settings.Hide;
        var globalDraft = new Dictionary<string, HotkeyBinding>(_settings.GlobalCommandBindings);
        var window = new AppSettingsWindow(regular: !_host.IsEditing)
        {
            MaxHeight = Math.Max(240, _host.PrimaryMonitor.WorkArea.Height * 96 / _host.PrimaryMonitor.DpiY - 24),
            MaxWidth = Math.Max(360, _host.PrimaryMonitor.WorkArea.Width * 96 / _host.PrimaryMonitor.DpiX - 24)
        };
        _settingsWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null; };
        var general = window.AddSection(L.Get("Settings.General"), L.Get("Settings.GeneralHint"));
        var canvas = window.AddSection(L.Get("Settings.CanvasHistory"), L.Get("Settings.CanvasHistoryHint"));
        var shortcuts = window.AddSection(L.Get("Settings.Shortcuts"), L.Get("Settings.ShortcutsHint"));
        var internalShortcuts = AppSettingsWindow.AddGroup(shortcuts, L.Get("Settings.InternalShortcuts"));
        internalShortcuts.Children.Add(SettingsAction(L.Get("Shortcuts.ConfigureCommands"), () => ShowCommandSearch(configure: true)));
        var globalShortcuts = AppSettingsWindow.AddGroup(shortcuts, L.Get("Settings.GlobalShortcuts"));
        var system = window.AddSection(L.Get("Settings.System"), L.Get("Settings.SystemHint"));
        var error = window.Error;
        System.Windows.Controls.Button SettingsAction(string label, Action action)
        {
            var button = new System.Windows.Controls.Button { Content = label, MinHeight = 34 };
            button.Click += (_, _) => RunUiAction(action);
            return button;
        }
        System.Windows.Controls.TextBox Recorder(string label, HotkeyBinding initial, Action<HotkeyBinding> change)
        {
            var box = new System.Windows.Controls.TextBox { Text = FormatHotkey(initial), IsReadOnly = true, Padding = new Thickness(8, 0, 8, 0), MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
            // WM_HOTKEY consumes registered combinations before WPF sees them.
            box.GotKeyboardFocus += (_, _) => _host.SuspendApplicationHotkeys(true);
            box.LostKeyboardFocus += (_, _) => { if (!_host.IsEditing) _host.SuspendApplicationHotkeys(false); };
            box.AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
            {
                var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt
                    or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift)
                { e.Handled = true; return; }
                if (key == System.Windows.Input.Key.Escape) { e.Handled = true; window.Focus(); return; }
                if (key == System.Windows.Input.Key.Back && OverlayHost.CurrentModifiers == OverlayModifiers.None)
                { change(default); box.Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Unbound"); e.Handled=true;return; }
                var modifiers = OverlayHost.CurrentModifiers;
                if (modifiers == OverlayModifiers.None) { error.Text = L.Get("Settings.ModifiersRequired"); e.Handled = true; return; }
                var binding = new HotkeyBinding((uint)modifiers, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key));
                try { KeyboardInputIsolation.ValidateHotkey(binding); }
                catch (ArgumentException ex) { error.Text = ex.Message; e.Handled = true; return; }
                change(binding);
                box.Text = FormatHotkey(binding);
                error.Text = "";
                e.Handled = true;
            }), handledEventsToo: true);
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            globalShortcuts.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(label), box));
            return box;
        }
        Recorder(L.Get("Settings.ToggleHotkey"), toggle, value => toggle = value);
        Recorder(L.Get("Settings.HideHotkey"), hide, value => hide = value);
        foreach(var command in OptionalGlobalCommands)
            Recorder(L.Get(command.Name),globalDraft.GetValueOrDefault(command.Id),value=>globalDraft[command.Id]=value);
        var globalHint = new TextBlock
        {
            Text = L.Get("Shortcuts.GlobalHint"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), FontSize = 12
        };
        globalHint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        globalShortcuts.Children.Add(globalHint);
        var theme = SegmentedChoicePreviews.Appearance(_settings.Theme);
        AppSettingsWindow.AddSetting(general, L.Get("Settings.Theme"), theme);
        var shortcutHints = new CheckBox { Content = L.Get("Feedback3.Toolbar.ShowShortcutHints"),
            IsChecked = _settings.ShowToolShortcutHints, MinHeight = 28 };
        AppSettingsWindow.AddSetting(general, L.Get("Feedback3.Toolbar.ShowShortcutHints"), shortcutHints);
        var passThroughLauncher = new CheckBox { IsChecked = _settings.ShowPassThroughLauncher, MinHeight = 28 };
        AppSettingsWindow.AddSetting(general, L.Get("Settings.PassThroughLauncher"), passThroughLauncher, L.Get("Settings.PassThroughLauncherHint"));
        WindowsShellIntegration? shell = null;
        bool oldStartup = false;
        if (_shellIntegrationAllowed)
        {
            try { shell = WindowsShellIntegration.Current(); oldStartup = shell.StartupEnabled; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { error.Text = ex.Message; }
        }
        var startup = new CheckBox { Content = L.Get("Shell.StartWithWindows"), IsChecked = oldStartup,
            IsEnabled = shell is not null, MinHeight = 28 };
        ToolTipService.SetShowOnDisabled(startup, true);
        startup.ToolTip = L.Get(shell is null ? "Shell.TestProfileHint" : "Shell.StartupHint");
        AppSettingsWindow.AddSetting(system, L.Get("Shell.StartWithWindows"), startup);
        var overlayMode = SegmentedChoicePreviews.OverlayMode(OverlayBackendModes.Parse(_settings.OverlayMode));
        AppSettingsWindow.AddSetting(system, L.Get("OverlaySafety.ModeSetting"), overlayMode,
            L.Get("OverlaySafety.ModeHint") + "\n" + L.Format("OverlaySafety.CurrentMode", L.Get(_host.UsesSystemInput ? "OverlaySafety.ModeEnhanced" : "OverlaySafety.ModeDesktop")));
        var preferGpu = AddRenderSettings(system);
        var registrationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(registrationStatus, L.Get("Shell.RegisterProjectType"));
        var registerFiles = SettingsAction(L.Get("Shell.AssociateProjectFiles"), () =>
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
        registerFiles.ToolTip = L.Get("Shell.AssociateProjectFilesHint")
            + (shell is null ? "\n" + L.Get("Shell.TestProfileHint") : string.Empty);
        var fileRegistration = AppSettingsWindow.AddGroup(system, L.Get("Shell.RegisterProjectType"));
        fileRegistration.Children.Add(registerFiles); fileRegistration.Children.Add(registrationStatus);
        var language = SegmentedChoicePreviews.Language(_settings.Language);
        AppSettingsWindow.AddSetting(general, L.Get("Settings.Language"), language, L.Get("Settings.LanguageRestartHint"));
        var screenScope = new OverlayComboBox { MinHeight = 34 };
        var pendingScope = new MonitorScopeSelection(screenScope, MonitorScopeControl.Saved(_settings));
        pendingScope.Refresh(_host.AvailableMonitors);
        void RefreshScreenScope() => pendingScope.Refresh(_host.AvailableMonitors);
        _host.MonitorsChanged += RefreshScreenScope;
        window.Closed += (_, _) => _host.MonitorsChanged -= RefreshScreenScope;
        AppSettingsWindow.AddSetting(canvas, L.Get("Settings.Scope"), screenScope,
            ApplicationPaths.RequestedMonitorScope.HasValue ? L.Get("Settings.ScopeOverrideHint") : null);
        var preserveCanvas = new System.Windows.Controls.CheckBox { IsChecked = _settings.PreserveCanvasSettingsOnNewCanvas };
        AppSettingsWindow.AddSetting(canvas, L.Get("Settings.PreserveCanvasSettings"), preserveCanvas, L.Get("Settings.PreserveCanvasSettingsHint"));
        var historyField = new NumericValueField(0, 1000, sliderMaximum: 200, integerOnly: true) { Value = _settings.HistoryVersionLimit, Unit = L.Get("Numeric.Unit.Version") };
        var historyLimit = historyField.Input;
        AppSettingsWindow.AddSetting(canvas, L.Get("History.Limit"), historyField, L.Get("History.LimitHint"));
        bool RestartRequired() => L.NormalizePreference(_settings.Language) != (language.SelectedIndex == 1 ? "zh-CN" : language.SelectedIndex == 2 ? "en" : "auto") ||
            OverlayBackendModes.Parse(_settings.OverlayMode) != (OverlayBackendMode)overlayMode.SelectedIndex;
        void RefreshSaveLabel() => window.SaveButton.Content = L.Get(RestartRequired() ? "Settings.SaveRestart" : "Settings.Save");
        language.SelectionChanged += (_, _) => RefreshSaveLabel();
        overlayMode.SelectionChanged += (_, _) => RefreshSaveLabel();
        window.SaveButton.Click += async (_, _) =>
        {
            if (!int.TryParse(historyLimit.Text, out int maximumHistory) || maximumHistory is < 0 or > 1000)
            { error.Text = L.Get("History.InvalidLimit"); window.SelectSection(canvas); historyLimit.Focus(); return; }
            var oldHistoryLimit = _settings.HistoryVersionLimit;
            var oldPreserveCanvas = _settings.PreserveCanvasSettingsOnNewCanvas;
            var oldPassThroughLauncher = _settings.ShowPassThroughLauncher;
            var oldPreviewUpdates = _settings.IncludePrereleaseUpdates;
            var oldToggle = _settings.Toggle;
            var oldHide = _settings.Hide;
            var oldGlobalBindings=_settings.GlobalCommandBindings;
            var oldTheme = _settings.Theme;
            var oldPrimaryScreenOnly = _settings.PrimaryScreenOnly;
            var oldMonitorDevice = _settings.MonitorDeviceName;
            var oldLanguage = _settings.Language;
            var oldShortcutHints = _settings.ShowToolShortcutHints;
            var oldPreferGpu = _settings.PreferGpuAcceleration;
            var oldOverlayMode = _settings.OverlayMode;
            bool restart = RestartRequired();
            try
            {
                var pendingGlobals=GlobalBindings(toggle,hide,globalDraft);
                foreach(var binding in pendingGlobals.Values.Where(b=>b.VirtualKey!=0))
                    if(EditorCommandCatalog.Resolve((int)binding.VirtualKey,(OverlayModifiers)binding.Modifiers) is {} conflict)
                        throw new ArgumentException(FormatHotkey(binding)+": "+conflict.Name);
                _host.RegisterCommandHotkeys(pendingGlobals);
                _settings.GlobalCommandBindings=globalDraft;
                _settings.HistoryVersionLimit = maximumHistory;
                _settings.PreserveCanvasSettingsOnNewCanvas = preserveCanvas.IsChecked == true;
                _settings.ShowPassThroughLauncher = passThroughLauncher.IsChecked == true;
                _settings.Toggle = toggle;
                _settings.Hide = hide;
                _settings.Theme = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system";
                var monitorChoice = pendingScope.Pending;
                _settings.PrimaryScreenOnly = monitorChoice.Scope == OverlayMonitorScope.PrimaryScreen;
                _settings.MonitorDeviceName = monitorChoice.DeviceName;
                _settings.Language = language.SelectedIndex == 1 ? "zh-CN" : language.SelectedIndex == 2 ? "en" : "auto";
                _settings.ShowToolShortcutHints = shortcutHints.IsChecked == true;
                _settings.PreferGpuAcceleration = preferGpu.IsChecked == true;
                _settings.OverlayMode = OverlayBackendModes.Serialize((OverlayBackendMode)overlayMode.SelectedIndex);
                bool startupChanged = false, settingsSaved = false;
                try
                {
                    if (shell is not null && (startup.IsChecked == true) != oldStartup)
                    {
                        shell.SetStartup(startup.IsChecked == true);
                        startupChanged = true;
                    }
                    _settings.Save();
                    settingsSaved = true;
                    ApplyMonitorScopeSetting();
                }
                catch (Exception failure)
                {
                    _settings.HistoryVersionLimit = oldHistoryLimit;
                    _settings.PreserveCanvasSettingsOnNewCanvas = oldPreserveCanvas;
                    _settings.ShowPassThroughLauncher = oldPassThroughLauncher;
                    _settings.IncludePrereleaseUpdates = oldPreviewUpdates;
                    _settings.Toggle = oldToggle;
                    _settings.Hide = oldHide;
                    _settings.GlobalCommandBindings=oldGlobalBindings;
                    _settings.Theme = oldTheme;
                    _settings.PrimaryScreenOnly = oldPrimaryScreenOnly;
                    _settings.MonitorDeviceName = oldMonitorDevice;
                    _settings.Language = oldLanguage;
                    _settings.ShowToolShortcutHints = oldShortcutHints;
                    _settings.PreferGpuAcceleration = oldPreferGpu;
                    _settings.OverlayMode = oldOverlayMode;
                    RefreshPassThroughLauncher();
                    try
                    {
                        _host.RegisterCommandHotkeys(GlobalBindings(oldToggle,oldHide,oldGlobalBindings));
                        if (settingsSaved)
                        {
                            try { _settings.Save(); }
                            catch (Exception rollbackFailure) { throw new AggregateException(failure, rollbackFailure); }
                        }
                    }
                    finally { if (startupChanged) shell!.SetStartup(oldStartup); }
                    throw;
                }
                await RecoveryHistory.TrimAsync(_documentLifetime.Token);
                RefreshHotkeyHints(_passThroughButton, _welcomeShortcuts, _settings.Toggle, _settings.Hide, _welcomeHideShortcut);
                _welcomeView?.Refresh(_settings.Toggle, _settings.Hide);
                RefreshToolShortcutHints();
                _host.ApplyRenderPreference(_settings.PreferGpuAcceleration);
                RequestRender();
                _tray.Text = L.Get("App.NamePrefix") + FormatHotkey(toggle) + L.Get("Desktop.TrayModeSuffix");
                AppTheme.Apply(_settings.Theme);
                SessionWindowCoordinator.Close(window);
                RefreshPassThroughLauncher();
                _ = CompleteStartupAsync();
                if (_host.IsEditing) RaiseVisibleToolWindows();
                UpdateStatus(oldLanguage != _settings.Language ? L.Get("Settings.LanguageSaved") :
                    oldOverlayMode != _settings.OverlayMode ? L.Get("OverlaySafety.ModeSaved") :
                    oldPrimaryScreenOnly == _settings.PrimaryScreenOnly && oldMonitorDevice == _settings.MonitorDeviceName ? L.Get("Settings.Saved") :
                    ApplicationPaths.RequestedMonitorScope.HasValue ? L.Get("Settings.SavedScopeOverride") : L.Get("Settings.SavedScopeApplied"));
                if (restart) await RestartApplicationAsync();
            }
            catch (Exception ex) { if (window.IsVisible) error.Text = ex.Message; else ShowError(ex.Message); }
        };
        window.Closed += (_, _) =>
        {
            try { _host.SuspendApplicationHotkeys(false); }
            catch (Exception ex) { UpdateStatus(ex.Message); }
        };
        ShowSessionDialog(window);
    }

    internal void ApplyMonitorScopeSetting() => _host.SetMonitorScope(ApplicationPaths.RequestedMonitorScope ??
        MonitorScopeControl.Saved(_settings).Scope, ApplicationPaths.RequestedMonitorScope.HasValue ? null : _settings.MonitorDeviceName);

    public void Dispose()
    {
        ForgetBorrowedTool();
        if (_disposed) return;
        FinishKeyboardNudge();
        _presentationPrivacy?.Dispose();
        _host.PresentationInputObserved -= OnPresentationHookInput;
        _host.ConfigurePresentationInputTap(false, false, 0);
        _presentationRuntime?.Dispose();
        DisposeWindowSources();
        DisposePrefabWindows();
        LogExitStage("Dispose.Begin");
        _dispatcherResponsiveness?.Dispose();
        _host.FrameRendering -= BeginCanvasSubmission;
        _host.FrameSubmitted -= CompleteCanvasSubmission;
        _submittingCanvasFrame?.Dispose(); _submittingCanvasFrame = null;
        _escapeTrace?.Dispose();
        LogExitStage("Dispose.RegularWindows.Begin");
        CloseEditingDialogs(); CloseRegularWindows();
        EndEyedropper(restore: false);
        LogExitStage("Dispose.ToolState.Begin");
        try { FlushToolState(commitDraft: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UpdateStatus(ex.Message); }
        _disposed = true;
        if (ReferenceEquals(_colorLibrary.HistorySession, _presetHistorySession)) _colorLibrary.HistorySession = null;
        LogExitStage("Dispose.Canvas.Begin");
        EndCanvasCalibration(hidePanel: true);
        _canvasToolOptions?.CancelEditing();
        LogExitStage("Dispose.WindowCoordinator.Begin");
        _keyboardRouter?.Dispose();
        _windowCoordinator?.Dispose();
        LogExitStage("Dispose.OverlayWindows.Begin");
        _overlayWindows?.Dispose();
        _toolStateSaveTimer?.Stop();
        StopCanvasEdgePan();
        if (_canvasViewHistorySession is not null) _canvasViewHistorySession.CanvasViewRestored -= OnCanvasViewHistoryRestored;
        RememberCanvasViewing();
        _canvasViewSaveTimer?.Stop();
        SaveCanvasViewingAsync().GetAwaiter().GetResult();
        _animationTimer?.Stop();
        _animationClock.Stop();
        LogExitStage("Dispose.LiveCapture.Begin");
        StopLiveEffectsCapture();
        CloseImageDrop();
        System.Windows.Input.InputManager.Current.PreProcessInput -= OnWpfInputProgress;
        System.Windows.Input.InputManager.Current.PostProcessInput -= OnWpfPointerFocus;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        LogExitStage("Dispose.DocumentTimers.Begin");
        StopDocumentTimers(); _renderOperation?.Abort(); _renderOperation = null; _renderAgain = false;
        LogExitStage("Dispose.Tray.Begin");
        _trayClicks.Dispose();
        _tray.Visible = false; var trayIcon = _tray.Icon; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); trayIcon?.Dispose();
        LogExitStage("Dispose.ToolWindows.Begin");
        _welcomeWindow?.Close();
        _updatesWindow?.Close();
        _passThroughLauncher?.Close();
        FinishInlineText();
        _inspector?.Close(); _layers?.Close(); _stampWindow?.Close(); _toolbar.Close();
        _editor.DetachSession();
        _presentOperation?.Abort(); _presentedPixels?.Dispose(); _presentedPixels = null;
        _lastBaseFrame?.Dispose(); _cancelledDraftFrame?.Dispose();
        LogExitStage("Dispose.Preview.Begin");
        _canvasPreview.Dispose();
        LogExitStage("Dispose.OverlayHost.Begin");
        _host.Dispose();
        LogExitStage("Dispose.Renderer.Begin");
        _renderer.Dispose();
        LogExitStage("Dispose.Complete");
    }
}
