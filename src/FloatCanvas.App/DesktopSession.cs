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
    private readonly AssetLibrary _assets = new();
    private readonly SceneRenderer _renderer = new();
    private readonly OverlaySceneCache _overlayCache;
    private readonly EditorController _editor;
    private DispatcherOperation? _renderOperation;
    private EditorSession _live;
    private EditorSession? _capture;
    private PointD _captureOrigin;
    private InspectorWindow? _inspector;
    private Window? _textEditor;
    private EditorSession? _textEditorSession;
    private ComboBox _toolPicker = null!;
    private Slider _opacitySlider = null!;
    private System.Windows.Controls.Button _passThroughButton = null!;
    private TextBlock? _welcomeShortcuts;
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
        _overlayCache = new(_renderer);
        AppTheme.Apply(_settings.Theme);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        _host = testHost ?? new OverlayHost(ApplicationPaths.RequestedMonitorScope ??
            (_settings.PrimaryScreenOnly ? OverlayMonitorScope.PrimaryScreen : OverlayMonitorScope.AllScreens));
        var keyboardBridge = new PrivateKeyboardBridge();
        _host.EnableKeyboardIsolation((packet, complete) => keyboardBridge.Process(packet, complete));
        System.Windows.Input.InputManager.Current.PreProcessInput += OnWpfInputProgress;
        var bounds = _host.Monitors.Select(m => new RectD(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height)).Aggregate(RectD.Union);
        var document = new SceneDocument { Viewport = bounds, DisplayLayout = _host.Monitors.Select(m => new DisplayLayoutHint
        { DeviceName = m.DeviceName, Bounds = new(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height), DpiX = m.DpiX, DpiY = m.DpiY }).ToList() };
        _live = new(document, _assets);
        _editor = new(_live);
        _editor.Changed += () => { RequestRender(); ScheduleRecovery(); };
        _editor.Message += UpdateStatus;
        _editor.EditText += ShowTextEditor;
        _editor.CaptureRegionSelected += FinishCaptureRegion;
        _host.Draw += Draw;
        _host.Pointer += e => { if (!DocumentOperationBusy) _editor.Pointer(e); };
        _host.KeyPressed += OnKey;
        _host.ToggleRequested += () => { if (_host.IsEditing || !DocumentOperationBusy) SetEditing(!_host.IsEditing); };
        _host.HideRequested += () => { if (_host.IsEditing || !DocumentOperationBusy) { SetEditing(false); _host.SetVisible(!_host.IsVisible); } };
        _host.MonitorsChanged += OnMonitorsChanged;
        _host.Error += ex => _app.Dispatcher.BeginInvoke(() => ShowError(ex.Message));
        _toolbar = CreateToolbar();
        _host.EditingFocusLost += () =>
        {
            // Do not gate focus release on an asynchronous document operation.
            _editor.Cancel(); _textEditor?.Hide();
            if (Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
            _toolbar.Hide(); _inspector?.Hide(); RequestRender();
            UpdateStatus(L.Get("Desktop.FocusLost"));
        };
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = L.Get("App.NamePrefix") + FormatHotkey(_settings.Toggle), Visible = true };
        _host.KeyboardIsolationReleased += reason =>
        {
            if (reason == KeyboardLeaseEndReason.ForegroundLost) return;
            var message = reason is KeyboardLeaseEndReason.SystemEscape or KeyboardLeaseEndReason.EmergencyEscape
                ? L.Get("Desktop.InputReleased")
                : L.Get("Desktop.InputInterrupted");
            UpdateStatus(message);
            _tray.ShowBalloonTip(4000, L.Get("Desktop.InputReleasedTitle"), message, Forms.ToolTipIcon.Info);
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.Get("Desktop.ToggleEditing"), null, (_, _) => RunUiAction(() => SetEditing(!_host.IsEditing)));
        menu.Items.Add(L.Get("Desktop.ToggleVisibility"), null, (_, _) => RunUiAction(() => { SetEditing(false); _host.SetVisible(!_host.IsVisible); }));
        menu.Items.Add(L.Get("Desktop.OpenProject"), null, async (_, _) => await RunCommand(OpenProjectAsync));
        menu.Items.Add(L.Get("Desktop.HotkeySettings"), null, (_, _) => RunUiAction(ShowHotkeySettings));
        menu.Items.Add(L.Get("Desktop.Exit"), null, async (_, _) => await RunCommand(RequestExitAsync));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RunUiAction(() => SetEditing(!_host.IsEditing));
    }

    public async void Start(string[] args)
    {
        try
        {
            await InitializeDocumentsAsync(!args.Contains("--smoke") || args.Contains("--recover"));
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
                    ResourcesComplete = _live.Document.ReferencedAssets.Concat(_capture?.Document.ReferencedAssets ?? []).All(id => _assets.TryGet(id, out _))
                }, new JsonSerializerOptions { WriteIndented = true }));
                _app.Shutdown();
            };
            _host.Render(); timer.Start(); return;
        }
        ShowWelcome();
    }

    private void ShowWelcome()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = L.Get("App.Name"), FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.Get("Welcome.Tagline"), Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 20) });
        var shortcuts = new TextBlock { Text = L.Format("Welcome.Shortcuts", FormatHotkey(_settings.Toggle), FormatHotkey(_settings.Hide)), LineHeight = 26 };
        _welcomeShortcuts = shortcuts;
        panel.Children.Add(shortcuts);
        panel.Children.Add(new TextBlock { Text = L.Get("Welcome.Description"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 18) });
        var welcome = new Window { Title = L.Get("Welcome.Title"), Content = panel, Width = 430, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, FontSize = 14 };
        panel.Children.Add(Button(L.Get("Welcome.Start"), () => { welcome.Close(); SetEditing(true); }));
        welcome.Closed += (_, _) => { if (ReferenceEquals(_welcomeShortcuts, shortcuts)) _welcomeShortcuts = null; };
        welcome.Show();
    }

    private Window CreateToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = L.Get("App.Name"), FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
        row.Children.Add(title);
        _toolPicker = new ComboBox { Width = 130, MinHeight = 34, Margin = new Thickness(2), VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var option in ToolOptions) _toolPicker.Items.Add(option);
        _toolPicker.DisplayMemberPath = nameof(ToolOption.Label);
        _toolPicker.SelectedIndex = 1;
        _toolPicker.SelectionChanged += (_, _) =>
        {
            if (_syncingControls || DocumentOperationBusy) return;
            if (_toolPicker.SelectedItem is ToolOption { Tool: EditorTool.CaptureRegion } && Active.Document.Kind != DocumentKind.FrozenCapture)
            { UpdateStatus(L.Get("Desktop.CaptureFirst")); RequestRender(); return; }
            if (_toolPicker.SelectedItem is ToolOption option) { _editor.Tool = option.Tool; UpdateStatus(option.Label); }
        };
        row.Children.Add(_toolPicker);
        foreach (var (label, color) in new[] { (L.Get("Color.Blue"), 0xFF2383E2u), (L.Get("Color.Red"), 0xFFEF4444u), (L.Get("Color.Yellow"), 0xFFFFC53Du), (L.Get("Color.White"), 0xFFFFFFFFu), (L.Get("Color.Black"), 0xFF222222u) })
        {
            var b = Button(label, () => _editor.Style.Stroke = color);
            b.MinWidth = 30; b.Padding = new Thickness(4); b.Foreground = new SolidColorBrush(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
            if (label == L.Get("Color.White")) b.Background = Brushes.DimGray;
            if (color == 0xFF222222u) b.Background = Brushes.Gainsboro;
            row.Children.Add(b);
        }
        var width = new Slider { Minimum = 1, Maximum = 40, Value = 4, Width = 75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), ToolTip = L.Get("Toolbar.StrokeWidth") };
        width.ValueChanged += (_, _) => _editor.Style.StrokeWidth = width.Value;
        row.Children.Add(width);
        row.Children.Add(ToolButton(L.Get("Toolbar.Undo"), "M6,3 L2,7 L6,11 M2,7 L10,7 Q14,7 14,12", Undo));
        row.Children.Add(ToolButton(L.Get("Toolbar.Redo"), "M10,3 L14,7 L10,11 M14,7 L6,7 Q2,7 2,12", Redo));
        row.Children.Add(ToolButton(L.Get("Toolbar.Image"), "M2,2 L14,2 L14,14 L2,14 Z M2,12 L6,8 L9,11 L11,9 L14,12 M10,5 L10.1,5", () => _ = RunCommand(() => ImportImagesAsync())));
        row.Children.Add(ToolButton(L.Get("Toolbar.Capture"), "M1,6 L1,2 L5,2 M11,2 L15,2 L15,6 M15,10 L15,14 L11,14 M5,14 L1,14 L1,10 M4,5 L12,5 L12,11 L4,11 Z", () => _ = RunCommand(() => StartCaptureAsync(true))));
        row.Children.Add(ToolButton(L.Get("Toolbar.SaveProject"), "M2,1 L11,1 L14,4 L14,15 L2,15 Z M5,1 L5,6 L10,6 L10,1 M5,15 L5,10 L11,10 L11,15", () => _ = RunCommand(() => SaveProjectAsync())));
        row.Children.Add(ToolButton(L.Get("Toolbar.Inspector"), "M1,3 L15,3 M1,8 L15,8 M1,13 L15,13 M5,1 L5,5 M11,6 L11,10 M6,11 L6,15", ToggleInspector));
        _passThroughButton = ToolButton("", "M4,1 L4,13 L7,10 L10,15 L12,14 L9,9 L14,9 Z", () => SetEditing(false));
        RefreshHotkeyHints(_passThroughButton, null, _settings.Toggle, _settings.Hide);
        row.Children.Add(_passThroughButton);
        var more = ToolButton(L.Get("Toolbar.More"), "M3,7 L3,9 M8,7 L8,9 M13,7 L13,9", () => { });
        var menu = new ContextMenu();
        void Item(string name, Action action) { var item = new MenuItem { Header = name }; item.Click += (_, _) => RunUiAction(action); menu.Items.Add(item); }
        Item(L.Get("Menu.OpenProject"), () => _ = RunCommand(OpenProjectAsync));
        Item(L.Get("Menu.SaveAs"), () => _ = RunCommand(() => SaveProjectAsync(true)));
        Item(L.Get("Menu.ExportImage"), () => _ = RunCommand(() => ExportAsync()));
        Item(L.Get("Menu.ExportTransparent"), () => _ = RunCommand(() => ExportAsync(true)));
        Item(L.Get("Menu.Copy"), () => CopySelection());
        Item(L.Get("Menu.Paste"), () => _ = RunCommand(PasteAsync));
        Item(L.Get("Menu.ImageDrop"), () => RunUiAction(BeginImageDrop));
        Item(L.Get("Menu.ReplaceImage"), () => _ = RunCommand(() => ImportImagesAsync(null, true)));
        Item(_host.Scope == OverlayMonitorScope.PrimaryScreen ? L.Get("Menu.CapturePrimary") : L.Get("Menu.CaptureAll"), () => _ = RunCommand(() => StartCaptureAsync(false)));
        Item(L.Get("Menu.ReturnLive"), ReturnToLive); Item(L.Get("Menu.ResumeCapture"), ResumeCapture);
        Item(L.Get("Menu.MoveIntoView"), MoveIntoView);
        Item(L.Get("Menu.Clear"), () => { _editor.Cancel(); Active.Execute(L.Get("History.Clear"), d => d.Elements.Clear()); });
        Item(L.Get("Menu.Settings"), ShowHotkeySettings);
        Item(L.Get("Desktop.Exit"), () => _ = RunCommand(RequestExitAsync));
        more.ContextMenu = menu; more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
        row.Children.Add(more);
        var opacity = _opacitySlider = new Slider { Minimum = 0, Maximum = 1, Value = 1, Width = 80, VerticalAlignment = VerticalAlignment.Center, ToolTip = L.Get("Toolbar.Opacity"), Margin = new Thickness(8, 0, 6, 0) };
        opacity.PreviewMouseLeftButtonDown += (_, _) => { _editor.Cancel(); Active.BeginTransaction(L.Get("History.LayerOpacity")); };
        opacity.ValueChanged += (_, _) =>
        {
            if (_syncingControls || DocumentOperationBusy) return;
            if (Active.IsTransactionActive) { Active.Document.LayerOpacity = opacity.Value; Active.NotifyChanged(); }
            else Active.Execute(L.Get("History.LayerOpacity"), d => d.LayerOpacity = opacity.Value);
        };
        opacity.PreviewMouseLeftButtonUp += (_, _) => { if (Active.IsTransactionActive) Active.Commit(); };
        opacity.LostMouseCapture += (_, _) => { if (Active.IsTransactionActive) Active.Commit(); };
        row.Children.Add(opacity);
        _status.Text = L.Get("Toolbar.Freehand"); _status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); _status.VerticalAlignment = VerticalAlignment.Center; _status.MaxWidth = 160;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(_status);
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(247, 247, 245)), BorderBrush = new SolidColorBrush(Color.FromRgb(215, 215, 212)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = row };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        border.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        var window = new Window { Title = L.Get("Toolbar.Title"), WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = true, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Content = border, FontSize = 14,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI") };
        title.MouseLeftButtonDown += (_, _) => { window.DragMove(); SaveToolbarPosition(); };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox) return;
            var key = System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key);
            if (_toolPicker.IsDropDownOpen)
            {
                if (key == 0x1B) { _toolPicker.IsDropDownOpen = false; e.Handled = true; }
                return;
            }
            if (key is 0x1B or 0x2E or 0x56 or 0x42 or 0x4C or 0x41 or 0x52 or 0x4F or 0x54 or 0x4E or 0x45 ||
                (OverlayHost.CurrentModifiers & OverlayModifiers.Control) != 0) { OnKey(key); e.Handled = true; }
        };
        window.Closing += (_, e) => { if (!_disposed) { e.Cancel = true; RunUiAction(() => SetEditing(false)); } };
        return window;
    }

    private sealed record ToolOption(EditorTool Tool, string Key)
    {
        public string Label => L.Get(Key);
    }
    private static readonly ToolOption[] ToolOptions = [new(EditorTool.Select,"Tool.Select"),new(EditorTool.Freehand,"Tool.Freehand"),new(EditorTool.Highlighter,"Tool.Highlighter"),
        new(EditorTool.Line,"Tool.Line"),new(EditorTool.Arrow,"Tool.Arrow"),new(EditorTool.DoubleArrow,"Tool.DoubleArrow"),new(EditorTool.Rectangle,"Tool.Rectangle"),new(EditorTool.RoundedRectangle,"Tool.RoundedRectangle"),
        new(EditorTool.Ellipse,"Tool.Ellipse"),new(EditorTool.Triangle,"Tool.Triangle"),new(EditorTool.Text,"Tool.Text"),new(EditorTool.Number,"Tool.Number"),new(EditorTool.Eraser,"Tool.Eraser"),
        new(EditorTool.Crop,"Tool.Crop"),new(EditorTool.Blur,"Tool.Blur"),new(EditorTool.Pixelate,"Tool.Pixelate"),new(EditorTool.Redact,"Tool.Redact"),new(EditorTool.CaptureRegion,"Tool.CaptureRegion")];
    private System.Windows.Controls.Button Button(string label, Action action)
    {
        var button = new System.Windows.Controls.Button { Content = label, MinHeight = 34, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = label };
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
    private bool SetEditing(bool editing)
    {
        CloseImageDrop();
        bool wasEditing = _host.IsEditing;
        _editor.Cancel();
        if (!editing) _textEditor?.Close();
        else if (_textEditor is not null && !ReferenceEquals(_textEditorSession, Active)) _textEditor.Hide();
        if (!editing && Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
        if (editing) _host.SetVisible(true);
        if (editing && !_host.TryEnterEditing(out var failure))
        {
            // Never leave an opaque frozen screenshot over a desktop that is receiving input.
            ReturnToLive();
            _toolbar.Hide(); _inspector?.Hide(); _textEditor?.Hide();
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
            if (_inspectorWanted) { _inspector?.Show(); _inspector?.Refresh(); }
            if (_textEditor is not null && ReferenceEquals(_textEditorSession, Active))
            { _textEditor.Show(); _textEditor.Activate(); }
            if (!wasEditing) UpdateStatus(L.Get("Desktop.EditModePrefix") + (ToolOptions.FirstOrDefault(x => x.Tool == _editor.Tool)?.Label ?? L.Get("Desktop.SelectTool")));
        }
        else { _toolbar.Hide(); _inspector?.Hide(); }
        RequestRender();
        return true;
    }
    private void RequestRender()
    {
        if (_disposed) return;
        if (_renderingFrame) { _renderAgain = true; return; }
        if (IsRenderPending) return;
        // Coalesce a batch of changes without adding a fixed timer delay to every pointer update.
        // BeginInvoke also preserves the application's DispatcherUnhandledException reporting path.
        _renderOperation = _app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderFrame));
    }
    private void RenderFrame()
    {
        _renderOperation = null;
        if (_disposed) return;
        _renderingFrame = true;
        try
        {
            _host.Render();
            _syncingControls = true;
            try
            {
                if (ToolOptions.FirstOrDefault(x => x.Tool == _editor.Tool) is { } selectedTool && !ReferenceEquals(_toolPicker.SelectedItem, selectedTool))
                    _toolPicker.SelectedItem = selectedTool;
                if (!Active.IsTransactionActive) _opacitySlider.Value = Active.Document.LayerOpacity;
            }
            finally { _syncingControls = false; }
            if (!Active.IsTransactionActive) _inspector?.Refresh();
        }
        finally
        {
            _renderingFrame = false;
            if (_renderAgain) { _renderAgain = false; RequestRender(); }
        }
    }
    private void UpdateStatus(string message) { _status.Text = message; _status.ToolTip = message; }
    private void OnWpfInputProgress(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (!_disposed) _host.NotifyInputProgress();
    }
    private void SelectTool(EditorTool tool)
    {
        _editor.Tool = tool;
        _toolPicker.SelectedItem = ToolOptions.FirstOrDefault(x => x.Tool == tool);
        UpdateStatus(ToolOptions.FirstOrDefault(x => x.Tool == tool)?.Label ?? tool.ToString());
    }
    private void OnKey(int key)
    {
        if (DocumentOperationBusy) return;
        var ctrl = (OverlayHost.CurrentModifiers & OverlayModifiers.Control) != 0;
        var shift = (OverlayHost.CurrentModifiers & OverlayModifiers.Shift) != 0;
        if (key == 0x1B) { if (Active.IsTransactionActive || _editor.Marquee != null) _editor.Cancel(); else SetEditing(false); return; }
        if (ctrl)
        {
            _editor.Cancel();
            switch (key)
            {
                case 0x5A: if (shift) Redo(); else Undo(); break;
                case 0x59: Redo(); break;
                case 0x41: Active.SelectAll(); SelectTool(EditorTool.Select); break;
                case 0x43: CopySelection(); break;
                case 0x56: _ = RunCommand(PasteAsync); break;
                case 0x44: Active.DuplicateSelection(); break;
                case 0x53: _ = RunCommand(() => SaveProjectAsync(shift)); break;
                case 0x4F: _ = RunCommand(OpenProjectAsync); break;
            }
            return;
        }
        if (key == 0x2E) { _editor.Cancel(); Active.DeleteSelection(); return; }
        if (key is >= 0x25 and <= 0x28)
        {
            _editor.Cancel(); var amount = shift ? 10 : 1;
            Active.TransformSelection(Affine2D.Translation(key == 0x25 ? -amount : key == 0x27 ? amount : 0, key == 0x26 ? -amount : key == 0x28 ? amount : 0)); return;
        }
        var tool = key switch { 0x56 => EditorTool.Select,0x42 => EditorTool.Freehand,0x4C => EditorTool.Line,0x41 => EditorTool.Arrow,0x52 => EditorTool.Rectangle,
            0x4F => EditorTool.Ellipse,0x54 => EditorTool.Text,0x4E => EditorTool.Number,0x45 => EditorTool.Eraser,_ => (EditorTool?)null };
        if (tool != null) SelectTool(tool.Value);
    }
    private void Undo() { _editor.Cancel(); Active.Undo(); }
    private void Redo() { _editor.Cancel(); Active.Redo(); }
    private void Draw(SKSurface surface)
    {
        var canvas = surface.Canvas;
        canvas.Save(); canvas.Translate((float)_editor.ScreenOrigin.X,(float)_editor.ScreenOrigin.Y);
        var preview = _editor.RasterPreview;
        _overlayCache.DrawOnClearedRasterTarget(surface,Active.Document,_assets,Active.SceneRevision,Active.TransactionId,
            preview.First,preview.Last,preview.BypassCache);
        if (_host.IsEditing)
        {
            using var paint = new SKPaint { Color = new SKColor(35,131,226), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            if (_editor.Tool == EditorTool.Select && Active.SelectionBounds() is { } b)
            {
                canvas.DrawRect((float)b.X,(float)b.Y,(float)b.Width,(float)b.Height,paint);
                foreach(var p in EditorController.Corners(b)) canvas.DrawRect((float)p.X-5,(float)p.Y-5,10,10,paint);
                canvas.DrawLine((float)b.Center.X,(float)b.Top,(float)b.Center.X,(float)b.Top-28,paint);
                canvas.DrawCircle((float)b.Center.X,(float)b.Top-28,6,paint);
                if(Active.SelectedElements().ToArray() is [var line] && line.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow)
                    foreach(var p in line.Points.Select(line.Transform.TransformPoint)) canvas.DrawCircle((float)p.X,(float)p.Y,6,paint);
            }
            if (_editor.EffectPreviewPolygon is { Count: > 0 } polygon)
            {
                using var path = new SKPathBuilder(); path.MoveTo((float)polygon[0].X,(float)polygon[0].Y);
                foreach(var p in polygon.Skip(1)) path.LineTo((float)p.X,(float)p.Y);
                path.Close(); using var shape = path.Detach(); canvas.DrawPath(shape,paint);
            }
            else if(_editor.Marquee is {} r) canvas.DrawRect((float)r.X,(float)r.Y,(float)r.Width,(float)r.Height,paint);
        }
        canvas.Restore();
    }
    private void ToggleInspector()
    {
        _editor.Cancel(); _inspectorWanted = !_inspectorWanted;
        if (_inspector == null)
        {
            _inspector = new InspectorWindow(() => Active, () => !DocumentOperationBusy) { Owner = _toolbar };
            _inspector.CommandKey += OnKey;
            _inspector.Dismissed += () => { _inspectorWanted = false; if (_host.IsEditing) _toolbar.Activate(); };
        }
        if (_inspectorWanted)
        {
            var monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new(_toolbar.PointToScreen(new(0, 0)).X, _toolbar.PointToScreen(new(0, 0)).Y))) ?? _host.PrimaryMonitor;
            _inspector.MaxHeight = Math.Max(_inspector.MinHeight, monitor.WorkArea.Height * 96 / monitor.DpiY - 24);
            _inspector.Refresh(); _inspector.Show();
        }
        else _inspector.Hide();
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
            if (!TextEditCommand.TryCommit(session, elementId, elementKind, input.Text, _editor.Style, point, _renderer, out var failure))
            { error.Text = failure; input.Focus(); return; }
            dialog.Close();
        }
        dialog.PreviewKeyDown += (_,e)=>
        {
            if(e.Key==System.Windows.Input.Key.Escape) { dialog.Close(); e.Handled=true; }
            if(e.Key==System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)!=0) { Commit(); e.Handled=true; }
        };
        dialog.Closed += (_,_)=> { _textEditor=null; _textEditorSession=null; if(!_disposed && _host.IsEditing) _toolbar.Activate(); };
        _textEditor=dialog; _textEditorSession=session; dialog.Show(); input.Focus(); input.SelectAll();
    }
    private void OnMonitorsChanged()
    {
        CloseImageDrop();
        _editor.Cancel();
        var bounds = _host.Monitors.Select(m=>new RectD(m.Bounds.Left,m.Bounds.Top,m.Bounds.Width,m.Bounds.Height)).Aggregate(RectD.Union);
        _live.Document.Viewport=bounds;
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
        var x=_settings.ToolbarX ?? m.WorkArea.Left + Math.Max(8,(m.WorkArea.Width-_toolbar.ActualWidth*m.DpiX/96)/2);
        var y=_settings.ToolbarY ?? m.WorkArea.Top+12;
        var target = _host.Monitors.FirstOrDefault(a=>a.WorkArea.Contains(new OverlayPoint(x,y))) ?? m;
        x = Math.Clamp(x, target.WorkArea.Left, Math.Max(target.WorkArea.Left, target.WorkArea.Right - _toolbar.ActualWidth * target.DpiX / 96));
        y = Math.Clamp(y, target.WorkArea.Top, Math.Max(target.WorkArea.Top, target.WorkArea.Bottom - _toolbar.ActualHeight * target.DpiY / 96));
        OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(_toolbar).Handle,new(x,y)); _toolbarPlaced=true;
    }
    private void SaveToolbarPosition()
    {
        var p=_toolbar.PointToScreen(new System.Windows.Point(0,0));
        _settings.ToolbarX=p.X; _settings.ToolbarY=p.Y;
        try { _settings.Save(); } catch(Exception ex) { UpdateStatus(L.Get("Desktop.ToolbarSaveFailed")+ex.Message); }
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
        _editor.Cancel();
        try { await action(); } catch(OperationCanceledException) { UpdateStatus(L.Get("Desktop.Cancelled")); } catch(Exception ex) { ShowError(ex.Message); }
    }
    private void RunUiAction(Action action)
    {
        if (DocumentOperationBusy) { UpdateStatus(L.Get("Desktop.Busy")); return; }
        action();
    }
    private void UpdateInteractionAvailability()
    {
        bool enabled = !DocumentOperationBusy;
        if (_toolbar is not null) _toolbar.IsEnabled = enabled;
        if (_inspector is not null) _inspector.IsEnabled = enabled;
        if (_textEditor is not null) _textEditor.IsEnabled = enabled;
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
            ? previous : _toolPicker;
        System.Windows.Input.Keyboard.Focus(target);
    }
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetToolbarForegroundWindow();
    private void ShowError(string error) => System.Windows.MessageBox.Show(_toolbar,error,L.Get("App.Name"),MessageBoxButton.OK,MessageBoxImage.Warning);

    private void OnSystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme == "system" && !_disposed)
            _app.Dispatcher.BeginInvoke(() => { if (!_disposed) AppTheme.Apply("system"); });
    }

    private static string FormatHotkey(HotkeyBinding binding)
    {
        var parts = new List<string>();
        if ((binding.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & 1) != 0) parts.Add("Alt");
        if ((binding.Modifiers & 4) != 0) parts.Add("Shift");
        if ((binding.Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)binding.VirtualKey).ToString());
        return string.Join("+", parts);
    }

    internal static void RefreshHotkeyHints(System.Windows.Controls.Button passThrough, TextBlock? welcomeShortcuts,
        HotkeyBinding toggle, HotkeyBinding hide)
    {
        string toggleText = FormatHotkey(toggle);
        string label = L.Format("Toolbar.PassThrough", toggleText);
        passThrough.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(passThrough, label);
        if (welcomeShortcuts is not null)
            welcomeShortcuts.Text = L.Format("Welcome.Shortcuts", toggleText, FormatHotkey(hide));
    }

    private void ShowHotkeySettings()
    {
        SetEditing(false);
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
            try
            {
                _host.RegisterHotkeys(toggle, hide);
                _settings.Toggle = toggle;
                _settings.Hide = hide;
                _settings.Theme = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system";
                _settings.PrimaryScreenOnly = screenScope.SelectedIndex == 0;
                _settings.Language = language.SelectedIndex == 1 ? "zh-CN" : language.SelectedIndex == 2 ? "en" : "auto";
                try { _settings.Save(); }
                catch
                {
                    _settings.Toggle = oldToggle;
                    _settings.Hide = oldHide;
                    _settings.Theme = oldTheme;
                    _settings.PrimaryScreenOnly = oldPrimaryScreenOnly;
                    _settings.Language = oldLanguage;
                    _host.RegisterHotkeys(oldToggle, oldHide);
                    throw;
                }
                RefreshHotkeyHints(_passThroughButton, _welcomeShortcuts, _settings.Toggle, _settings.Hide);
                _tray.Text = L.Get("App.NamePrefix") + FormatHotkey(toggle) + L.Get("Desktop.TrayModeSuffix");
                AppTheme.Apply(_settings.Theme);
                window.Close();
                UpdateStatus(oldLanguage != _settings.Language ? L.Get("Settings.LanguageSaved") :
                    oldPrimaryScreenOnly == _settings.PrimaryScreenOnly ? L.Get("Settings.Saved") :
                    ApplicationPaths.RequestedMonitorScope.HasValue ? L.Get("Settings.SavedScopeOverride") : L.Get("Settings.SavedScopeRestart"));
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }));
        window.Show();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseImageDrop();
        System.Windows.Input.InputManager.Current.PreProcessInput -= OnWpfInputProgress;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        StopDocumentTimers(); _renderOperation?.Abort(); _renderOperation = null; _renderAgain = false;
        _tray.Visible = false; var trayIcon = _tray.Icon; _tray.Dispose(); trayIcon?.Dispose();
        _textEditor?.Close();
        _host.Dispose(); _overlayCache.Dispose(); _renderer.Dispose();
        _inspector?.Close(); _toolbar.Close();
    }
}
