using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private ToolbarLauncher _toolbarLauncher = null!;
    private StampShelfControl _stampShelf = null!;
    private Controls.Border _toolbarExpandedContent = null!;
    private Controls.ContextMenu _toolbarMenu = null!;
    private Controls.ContextMenu _moreToolsMenu = null!;
    private Controls.Button? _canvasVisibilityButton;
    private static readonly Geometry VisibleCanvasEye = Geometry.Parse("M1,8 Q8,-1 15,8 Q8,17 1,8 Z M8,5 A3,3 0 1 1 7.99,5");
    private static readonly Geometry HiddenCanvasEye = Geometry.Parse("M1,5.5 Q8,12.5 15,5.5 M3.5,7.5 L2,10 M8,9 L8,12 M12.5,7.5 L14,10");
    private readonly Dictionary<EditorTool, Controls.Button> _toolbarToolButtons = [];
    private readonly List<Controls.MenuItem> _cropMenuItems = [];
    private readonly Dictionary<EditorTool, ToolParameters> _toolbarToolMemory = [];
    private readonly Dictionary<EditorTool, EditorTool> _toolbarButtonMemory = [];
    private ToolOptionsPanel _toolOptionsPanel = null!;
    private Controls.Border _toolOptionsFrame = null!;
    private Controls.Button _toolOptionsPin = null!;
    private bool _toolOptionsPinned;
    private string? _toolbarParameterFingerprint;
    private EditorTool _toolbarObservedTool = EditorTool.Freehand;
    private Guid? _toolbarActiveStamp;
    private ToolbarStatusDisplay _toolbarStatusDisplay = null!;
    private readonly List<Controls.TextBlock> _toolbarShortcutHintLabels=[];
    private readonly Dictionary<EditorTool,Controls.ContentPresenter> _toolbarGlyphHosts=[];
    private Controls.MenuItem? _toolbarShortcutHintsMenu;
    private ClearCanvasButton? _clearCanvasButton;

    private Window CreateToolbarV2()
    {
        _toolbarLauncher = new ToolbarLauncher();
        var content = new Controls.Grid();
        content.Resources["CanvasButtonCornerRadius"]=new CornerRadius(3);
        content.RowDefinitions.Add(new Controls.RowDefinition { Height = new GridLength(26) });
        // Match the two-DIP inner padding above and below the rows.
        content.RowDefinitions.Add(new Controls.RowDefinition { Height = new GridLength(2) });
        content.RowDefinitions.Add(new Controls.RowDefinition { Height = new GridLength(26) });
        for (int i = 0; i < 27; i++) content.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new GridLength(i is 2 or 13 ? 10 : 30) });
        int toolColumn = 0;
        void Cell(UIElement element, int column, int row = 0, int span = 1)
        { Controls.Grid.SetColumn(element, column); Controls.Grid.SetRow(element, row==0?0:2); Controls.Grid.SetColumnSpan(element, span); content.Children.Add(element); }
        void Tool(EditorTool tool, string label, string geometry)
        {
            string shortcut=ToolShortcutCatalog.Hint(tool);
            // A toolbar revisit restores its family's working state. Remembered
            // legacy aliases must not act like an explicit recipe selection.
            var button = ToolbarIcon(label, geometry, () => ActivateTool(ToolFamily(ButtonFamily(_editor.Tool) == tool ? _editor.Tool : _toolbarButtonMemory.GetValueOrDefault(tool, tool))), shortcut);
            if(shortcut.Length>0)AddShortcutHint(button,shortcut,tool);
            button.Padding=new Thickness(0);
            button.Tag = "tool:" + tool; _toolbarToolButtons[tool] = button; Cell(button, toolColumn++);
        }
        Tool(EditorTool.Select, L.Get("ToolbarV2.Select"), "M3,1 L3,15 L7,11 L10,16 L12,15 L9,10 L15,10 Z");
        Tool(EditorTool.Eraser, L.Get("Preset.Eraser"), "M1,10 L8,2 L15,9 L8,16 L6,16 Z M5,6 L12,13 M8,16 L16,16");
        for(int row=0;row<2;row++)Cell(Separator(),2,row);
        toolColumn=3;
        Tool(EditorTool.Freehand, L.Get("Preset.Freehand"), "M2,13 L3,9 L11,1 L15,5 L7,13 Z M3,9 L7,13 M10,2 L14,6");
        Tool(EditorTool.Highlighter, L.Get("Preset.Highlighter"), "M3,11 L10,2 L14,5 L8,14 L3,14 Z M1,16 L12,16");
        Tool(EditorTool.Line, L.Get("ToolbarV2.Lines"), "M2,14 L14,2");
        Tool(EditorTool.Rectangle, L.Get("ToolbarV2.Rectangles"), "M3,2 L13,2 Q15,2 15,4 L15,12 Q15,14 13,14 L3,14 Q1,14 1,12 L1,4 Q1,2 3,2 Z");
        Tool(EditorTool.Ellipse, L.Get("ToolbarV2.Ellipses"), "M1,8 C1,0 15,0 15,8 C15,16 1,16 1,8 Z");
        Tool(EditorTool.Text, L.Get("Preset.Text"), "M2,2 L14,2 M8,2 L8,15 M5,15 L11,15");
        Tool(EditorTool.Number,L.Get("Preset.Number"),"M8,1 A7,7 0 1 1 7.99,1 M6,6 L8,4 L8,12 M6,12 L10,12");
        Tool(EditorTool.Symbol, L.Get("Tool.Symbol"), "M8,1 L10,6 L15,6 L11,10 L13,15 L8,12 L3,15 L5,10 L1,6 L6,6 Z");
        Tool(EditorTool.Emoji, L.Get("Tool.Emoji"), "M8,1 A7,7 0 1 1 7.99,1 M5,6 L5.1,6 M11,6 L11.1,6 M4,9 Q8,15 12,9");
        var more=ToolbarIcon(L.Get("Feedback4.Toolbar.More"),"M3,8 L3.1,8 M8,8 L8.1,8 M13,8 L13.1,8",OpenMoreTools,"M");
        AddShortcutHint(more,"M");more.Padding=new Thickness(0);more.Tag="more";Cell(more,toolColumn++);
        _moreToolsMenu=new Controls.ContextMenu {PlacementTarget=more,Placement=Primitives.PlacementMode.Bottom};
        foreach(var tool in new[]{EditorTool.Blur,EditorTool.Pixelate})_moreToolsMenu.Items.Add(ToolMenuItem(tool));
        more.ContextMenu=_moreToolsMenu;
        _toolbarToolButtons[EditorTool.Blur]=more;
        for (int row = 0; row < 2; row++)
            Cell(Separator(),13,row);
        toolColumn = 14;
        void Action(string tag, string label, string geometry, Action action, string shortcut = "")
        { var button = ToolbarIcon(label, geometry, action, shortcut); button.Tag = tag; Cell(button, toolColumn++); }
        Action("undo", L.Get("ToolbarV2.Undo"), "M6,3 L2,7 L6,11 M2,7 L10,7 Q14,7 14,13", Undo, "Ctrl+Z");
        Action("redo", L.Get("ToolbarV2.Redo"), "M10,3 L14,7 L10,11 M14,7 L6,7 Q2,7 2,13", Redo, "Ctrl+Y");
        Action("import", L.Get("Toolbar.Image"), "M2,2 L14,2 L14,14 L2,14 Z M2,12 L6,8 L9,11 L11,9 L14,12 M10,5 L10.1,5", () => _ = RunCommand(() => ImportImagesAsync()));
        Action("capture", L.Get("Acceptance2.Toolbar.Capture"), "M1,6 L1,2 L5,2 M11,2 L15,2 L15,6 M15,10 L15,14 L11,14 M5,14 L1,14 L1,10 M4,5 L12,5 L12,11 L4,11 Z", () => ActivateTool(EditorTool.CaptureRegion));
        var captureButton = content.Children.OfType<Controls.Button>().Last();
        _toolbarToolButtons[EditorTool.CaptureRegion] = captureButton;
        captureButton.ToolTip = L.Get("Acceptance2.Toolbar.Capture") + "\n" + L.Get("Acceptance2.Toolbar.CaptureHint");
        Action("crop", L.Get("Tool.Crop"), "M4,1 L4,11 Q4,12 5,12 L15,12 M1,4 L11,4 Q12,4 12,5 L12,15", () => ActivateTool(EditorTool.Crop));
        _toolbarToolButtons[EditorTool.Crop] = content.Children.OfType<Controls.Button>().Last();
        _toolbarToolButtons[EditorTool.Crop].IsEnabled = _editor.CanCropSelection;
        Action("canvas", L.Get("Canvas.Title"), "M2,3 L14,3 L16,14 L0,14 Z M5,3 L4,14 M11,3 L12,14 M1,8 L15,8", ToggleCanvasSettings);
        Action("properties", L.Get("ToolbarV2.Properties"), "M1,3 L15,3 M1,8 L15,8 M1,13 L15,13 M5,1 L5,5 M11,6 L11,10 M6,11 L6,15", ToggleInspector);
        Action("layers", L.Get("ToolbarV2.Layers"), "M1,5 L8,1 L15,5 L8,9 Z M1,9 L8,13 L15,9 M1,13 L8,17 L15,13", ToggleLayers);
        _clearCanvasButton = new ClearCanvasButton(() => !DocumentOperationBusy, () => RunCommand(StartNewCanvasAsync));
        Cell(_clearCanvasButton, toolColumn++);
        _opacitySlider=CreateToolbarOpacitySlider();Cell(_opacitySlider,toolColumn,0,2);toolColumn+=2;
        Action("visibility",L.Get("Desktop.ToggleVisibility"),"M1,8 Q8,-1 15,8 Q8,17 1,8 Z M8,5 A3,3 0 1 1 7.99,5",ToggleCanvasVisibility);
        _canvasVisibilityButton = content.Children.OfType<Controls.Button>().Last();
        RefreshCanvasVisibilityButton();
        _passThroughButton = ToolbarIcon(L.Get("Acceptance2.Toolbar.Done"), "M2,8 L6,12 L14,3", () => SetEditing(false), FormatHotkey(_settings.Toggle));
        _passThroughButton.Tag = "done"; Cell(_passThroughButton, toolColumn++);
        var manage=ToolbarIcon(L.Get("Stamp.LibraryTitle"),"M3,11 L13,11 L14,14 L2,14 Z M5,11 L5,9 Q5,7 7,7 L6,3 Q6,1 8,1 Q10,1 10,3 L9,7 Q11,7 11,9 L11,11 M2,16 L14,16",ShowStampLibrary,"-");
        AddShortcutHint(manage,"-",lowerRow:true);manage.Padding=new Thickness(0);manage.Tag="manage-stamps";Cell(manage,0,1);
        var save=ToolbarIcon(L.Get("Feedback3.Toolbar.NewStamp"),"M2,12 L12,12 L13,15 L1,15 Z M4,12 L4,10 Q4,8 6,8 L5,4 Q5,2 7,2 Q9,2 9,4 L8,8 Q10,8 10,10 L10,12 M13,2 L13,8 M10,5 L16,5",SaveStampSelection,"+");
        AddShortcutHint(save,"+",lowerRow:true);save.Padding=new Thickness(0);save.Tag="save-stamp";Cell(save,1,1);
        _stampShelf = new(_stampLibrary, SelectStampSlot); Cell(_stampShelf, 3, 1, 10);
        _status.Text = L.Get("Preset.Freehand"); _toolbarStatusDisplay = new(_status); Cell(_toolbarStatusDisplay, 14, 1, 13);
        RefreshToolShortcutHints();
        _toolbarExpandedContent = Surface(content, 10, new Thickness(20, 2, 4, 2));
        _toolbarExpandedContent.Visibility = _settings.ToolbarExpanded ? Visibility.Visible : Visibility.Collapsed;
        _toolbarExpandedContent.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = .38, Color = Colors.Black };
        var shell = _toolbarLauncher.CreateSurface(_toolbarExpandedContent);
        var window = new Window { Title = L.Get("Toolbar.Title"), Content = shell, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true, ShowInTaskbar = true, Icon = AppIcon.WindowImage, FontSize = 14, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            UseLayoutRounding = true, SnapsToDevicePixels = true };
        _toolbarMenu = CreateLauncherMenu();
        _toolbarMenu.PlacementTarget = _toolbarLauncher;
        _toolbarMenu.Placement = Primitives.PlacementMode.Custom;
        _toolbarMenu.CustomPopupPlacementCallback = LauncherMenuPlacements;
        _toolbarMenu.Opened += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_toolbarMenu.IsOpen) _toolbarMenu.Items.OfType<Controls.MenuItem>().FirstOrDefault()?.Focus();
        });
        _toolbarLauncher.MenuRequested += () => { if (!_toolOptionsPinned) CloseToolOptions(); _toolbarMenu.IsOpen = true; };
        _toolbarLauncher.ToggleExpandedRequested += () =>
        {
            _settings.ToolbarExpanded = !_settings.ToolbarExpanded;
            _toolbarExpandedContent.Visibility = _settings.ToolbarExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (_settings.ToolbarExpanded)
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (window.IsVisible) { PlaceToolbar(); SaveToolbarPosition(); } });
        };
        _toolbarLauncher.DragRequested += () =>
        {
            _toolbarMenu.IsOpen = false;
            if (Mouse.LeftButton == MouseButtonState.Pressed) window.DragMove();
            SaveToolbarPosition(); RepositionToolOptions();
        };
        CreateToolOptionsPopup(window);
        Action libraryChanged = () => window.Dispatcher.BeginInvoke(() => { ValidateSelectedStamp(); RefreshToolbarState(); });
        _stampLibrary.Changed += libraryChanged;
        window.Closed += (_, _) => _stampLibrary.Changed -= libraryChanged;
        window.LocationChanged += (_, _) => RepositionToolOptions();
        window.SizeChanged += (_, _) => RepositionToolOptions();
        window.Deactivated += (_, _) => { _clearCanvasButton.CancelConfirmation(); QueueToolOptionsFocusCheck(); };
        window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) { _clearCanvasButton.CancelConfirmation(); CloseToolOptions(); _toolbarMenu.IsOpen = false; _moreToolsMenu.IsOpen=false; _toolbarLauncher.CancelPendingGestures(); } };
        window.PreviewKeyDown += (_, e) =>
        {
            if(IsOptionsInput(e.OriginalSource as DependencyObject))return;
            if (e.OriginalSource is Controls.TextBox || e.OriginalSource is Controls.Primitives.TextBoxBase || e.OriginalSource is ToolbarLauncher && e.Key is Key.Enter or Key.Space or Key.Apps) return;
            var key = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
            if (key is 0x1B or 0x2E or >= 0x25 and <= 0x28 or >= 0x30 and <= 0x39 || ToolShortcutCatalog.TryGetTool(key, out var routedTool) || ToolShortcutCatalog.IsReserved(key) ||
                (OverlayHost.CurrentModifiers & OverlayModifiers.Control) != 0) { e.Handled = true; OnKey(key); }
        };
        window.Closing += (_, e) => { if (!_disposed) { e.Cancel = true; RunUiAction(() => SetEditing(false)); } };
        return window;
    }

    private void OpenMoreTools()
    {
        if(DocumentOperationBusy || !_host.IsEditing || !_toolbar.IsVisible)return;
        if(!_toolOptionsPinned)CloseToolOptions();
        _moreToolsMenu.PlacementTarget=_settings.ToolbarExpanded?_toolbarToolButtons[EditorTool.Blur]:_toolbarLauncher;
        _moreToolsMenu.IsOpen=!_moreToolsMenu.IsOpen;
    }

    private Controls.ContextMenu CreateLauncherMenu()
    {
        var menu = new Controls.ContextMenu();
        Controls.MenuItem Group(string key, string geometry)
        {
            var group = new Controls.MenuItem { Header = L.Get(key), Icon = MenuGeometry(geometry) }; menu.Items.Add(group); return group;
        }
        void Item(Controls.MenuItem group, string label, Action action, string shortcut = "", string geometry = "M2,2 L14,2 L14,14 L2,14 Z")
        {
            var item = new Controls.MenuItem { Header = label, InputGestureText = shortcut, Icon = MenuGeometry(geometry) };
            item.Click += (_, _) => RunUiAction(action); group.Items.Add(item);
        }
        var file = Group("ToolbarV2.File", "M1,4 L7,4 L9,6 L15,6 L15,14 L1,14 Z");
        Item(file, L.Get("Canvas.New"), () => _ = RunCommand(CreateDefaultCanvasAsync));
        Item(file, L.Get("ToolbarV2.Open"), () => _ = RunCommand(OpenProjectAsync), "Ctrl+O");
        Item(file, L.Get("ToolbarV2.Save"), () => _ = RunCommand(() => SaveProjectAsync()), "Ctrl+S", "M2,1 L12,1 L15,4 L15,15 L2,15 Z M5,1 L5,6 L10,6 L10,1");
        Item(file, L.Get("ToolbarV2.SaveAs"), () => _ = RunCommand(() => SaveProjectAsync(true)), "Ctrl+Shift+S");
        Item(file, L.Get("Menu.ExportImage"), () => _ = RunCommand(() => ExportAsync()));
        Item(file, L.Get("Menu.ExportTransparent"), () => _ = RunCommand(() => ExportAsync(true)));
        Item(file, L.Get("Desktop.RecoveryHistory"), () => _ = RunCommand(RestoreWorkspaceHistoryAsync));
        var edit = Group("ToolbarV2.Edit", "M2,13 L3,9 L11,1 L15,5 L7,13 Z");
        Item(edit, L.Get("ToolbarV2.Undo"), Undo, "Ctrl+Z", "M6,3 L2,7 L6,11 M2,7 L10,7 Q14,7 14,13");
        Item(edit, L.Get("ToolbarV2.Redo"), Redo, "Ctrl+Y", "M10,3 L14,7 L10,11 M14,7 L6,7 Q2,7 2,13");
        edit.Items.Add(new Controls.Separator());
        Item(edit, L.Get("ToolbarV2.Copy"), CopySelection, "Ctrl+C");
        Item(edit, L.Get("ToolbarV2.Paste"), () => _ = RunCommand(PasteAsync), "Ctrl+V");
        Item(edit, L.Get("ToolbarV2.Duplicate"), () => { _editor.Cancel(); Active.DuplicateSelection(); }, "Ctrl+D");
        Item(edit, L.Get("Layers.Group"), () => { _editor.Cancel(); Active.GroupSelection(); }, "Ctrl+G");
        Item(edit, L.Get("Layers.Ungroup"), () => { _editor.Cancel(); Active.UngroupSelection(); }, "Ctrl+Shift+G");
        Item(edit, L.Get("ToolbarV2.Delete"), () => { _editor.Cancel(); Active.DeleteSelection(); }, "Delete");
        var drawing = Group("ToolbarV2.DrawingTools", "M8,1 L15,15 L1,15 Z");
        foreach (var tool in new[] { EditorTool.Select, EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Rectangle,
            EditorTool.Ellipse, EditorTool.Text, EditorTool.Number, EditorTool.Symbol, EditorTool.Emoji, EditorTool.Eraser }) drawing.Items.Add(ToolMenuItem(tool));
        var images = Group("ToolbarV2.ImageTools", "M2,2 L14,2 L14,14 L2,14 Z M2,12 L6,8 L10,12 L14,9");
        Item(images, L.Get("Toolbar.Image"), () => _ = RunCommand(() => ImportImagesAsync()));
        Item(images, L.Get("Menu.ReplaceImage"), () => _ = RunCommand(() => ImportImagesAsync(null, true)));
        images.Items.Add(new Controls.Separator());
        foreach (var tool in new[] { EditorTool.Crop, EditorTool.Blur, EditorTool.Pixelate, EditorTool.Redact }) images.Items.Add(ToolMenuItem(tool));
        var canvas = Group("ToolbarV2.Canvas", "M1,3 L15,3 L15,13 L1,13 Z M5,16 L11,16");
        canvas.Items.Add(ToolMenuItem(EditorTool.CaptureRegion));
        Item(canvas, L.Get("Menu.ReturnLive"), ReturnToLive); Item(canvas, L.Get("Menu.ResumeCapture"), ResumeCapture);
        Item(canvas, L.Get("Menu.MoveIntoView"), MoveIntoView);
        Item(canvas, L.Get("Feedback4.Toolbar.ClearCanvas"), ClearCanvas);
        var windows = Group("ToolbarV2.Windows", "M2,1 L14,1 L14,15 L2,15 Z M2,5 L14,5");
        Item(windows, L.Get("ToolbarV2.Properties"), ToggleInspector); Item(windows, L.Get("ToolbarV2.Layers"), ToggleLayers);
        Item(windows, L.Get("Stamp.LibraryTitle"), ShowStampLibrary);
        menu.Items.Add(new Controls.Separator());
        _toolbarShortcutHintsMenu=new Controls.MenuItem {Header=L.Get("Feedback3.Toolbar.ShowShortcutHints"),IsCheckable=true,IsChecked=_settings.ShowToolShortcutHints};
        _toolbarShortcutHintsMenu.Click+=(_,_)=>
        {
            bool previous=_settings.ShowToolShortcutHints;_settings.ShowToolShortcutHints=_toolbarShortcutHintsMenu.IsChecked;
            try{_settings.Save();}catch(Exception ex){_settings.ShowToolShortcutHints=previous;ShowError(ex.Message);}
            RefreshToolShortcutHints();
        };
        menu.Items.Add(_toolbarShortcutHintsMenu);
        var settings = new Controls.MenuItem { Header = L.Get("Menu.Settings"), Icon = MenuGeometry("M1,3 L15,3 M1,8 L15,8 M1,13 L15,13 M5,1 L5,5 M11,6 L11,10 M6,11 L6,15") };
        settings.Click += (_, _) => RunUiAction(ShowHotkeySettings); menu.Items.Add(settings);
        var updates = new Controls.MenuItem { Header = L.Get("Updates.Title") };
        updates.Click += (_, _) => RunUiAction(ShowUpdates); menu.Items.Add(updates);
        var pass = new Controls.MenuItem { Header = L.Get("ToolbarV2.PassThrough"), InputGestureText = FormatHotkey(_settings.Toggle), Icon = MenuGeometry("M4,1 L4,13 L7,10 L10,15 L12,14 L9,9 L14,9 Z") };
        pass.Click += (_, _) => RunUiAction(() => SetEditing(false)); menu.Items.Add(pass);
        var exit = new Controls.MenuItem { Header = L.Get("Desktop.Exit"), Icon = MenuGeometry("M9,2 L2,2 L2,14 L9,14 M7,8 L16,8 M12,4 L16,8 L12,12") };
        exit.Click += (_, _) => _ = ExitAfterSnapshotAsync(); menu.Items.Add(exit);
        return menu;
    }

    private void ActivateTool(EditorTool tool)
    {
        if (DocumentOperationBusy) return;
        EndCanvasCalibration();
        if (tool == EditorTool.Crop && !_editor.CanCropSelection) return;
        if (tool == EditorTool.CaptureRegion && Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
        var family = ToolFamily(tool); var currentFamily = ToolFamily(_editor.Tool);
        bool same = currentFamily == family && (tool is not (EditorTool.Arrow or EditorTool.DoubleArrow or EditorTool.RoundedRectangle) || AliasMatches(tool));
        bool wasOpen = IsToolOptionsOpen;
        if (!same)
        {
            SwitchToolState(tool, applyAlias: true);
        }
        UpdateStatus(ToolName(_editor.Tool)); RefreshToolbarState();
        if (!_host.IsEditing) { CloseToolOptions(); return; }
        if (ShouldOpenToolOptions(same, wasOpen)) OpenToolOptions(); else CloseToolOptions();
    }

    internal static bool ShouldOpenToolOptions(bool sameTool, bool wasOpen) => sameTool != wasOpen;

    private bool AliasMatches(EditorTool requested)
    {
        var p = _editor.Parameters;
        var start = p.Style.StartArrowHead ?? (_editor.Tool == EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None);
        var end = p.Style.EndArrowHead ?? (_editor.Tool is EditorTool.Arrow or EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None);
        return requested switch { EditorTool.Arrow => start == ArrowHeadStyle.None && end != ArrowHeadStyle.None,
            EditorTool.DoubleArrow => start != ArrowHeadStyle.None && end != ArrowHeadStyle.None,
            EditorTool.RoundedRectangle => (p.CornerRadius ?? (_editor.Tool == EditorTool.RoundedRectangle ? 1 : 0)) > 0, _ => true };
    }
    private void RefreshToolbarState()
    {
        RefreshCanvasVisibilityButton();
        if (_toolbarToolButtons.Count == 0) return;
        ValidateSelectedStamp();
        _toolbarStatusDisplay?.Refresh();
        // Crop is a dedicated entry with no remembered aliases. Keep legacy shared
        // button cache keys compatible while its working parameters persist normally.
        if (_editor.Tool != EditorTool.Crop && _toolbarToolButtons.ContainsKey(ButtonFamily(_editor.Tool))) _toolbarButtonMemory[ButtonFamily(_editor.Tool)] = _editor.Tool;
        foreach (var (tool, button) in _toolbarToolButtons)
        {
            if (tool == EditorTool.Crop) button.IsEnabled = _editor.CanCropSelection;
            bool selected = ButtonFamily(_editor.Tool) == tool;
            if(selected)button.SetResourceReference(Controls.Control.BorderBrushProperty,"CanvasAccent");else button.BorderBrush=Brushes.Transparent;
            button.SetResourceReference(Controls.Control.BackgroundProperty, selected ? "CanvasHover" : "CanvasSurface");
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? L.Get("ToolbarV2.Selected") : "");
        }
        foreach (var item in _cropMenuItems) item.IsEnabled = _editor.CanCropSelection;
        _syncingControls = true;
        try { if (!Active.IsTransactionActive) _opacitySlider.Value = Active.Document.LayerOpacity; }
        finally { _syncingControls = false; }
        var parameters = _editor.Parameters;
        string fingerprint = JsonSerializer.Serialize(parameters);
        if (_toolbarParameterFingerprint != fingerprint)
        {
            _toolbarToolMemory[ToolFamily(parameters.Tool)] = parameters;
            _toolbarParameterFingerprint = fingerprint;
            _toolOptionsPanel?.Refresh();
            foreach(var(tool,presenter)in _toolbarGlyphHosts)
            {
                if(tool is EditorTool.Select or EditorTool.Eraser)continue;
                var own=_toolbarToolMemory.TryGetValue(ToolFamily(tool),out var saved)?saved:new ToolParameters {Tool=tool};
                presenter.Content=ToolParameterIcon.Create(own);
            }
            _toolStateStore?.MarkChanged();
        }
        if (IsToolOptionsOpen && _toolbarObservedTool != _editor.Tool) AnchorToolOptions();
        _toolbarObservedTool = _editor.Tool;
        Guid? activeStamp = _editor.Tool == EditorTool.Stamp ? _selectedStampId : null;
        if (_toolbarActiveStamp != activeStamp) { _toolbarActiveStamp = activeStamp; _stampShelf.SetActive(activeStamp); }
        if (!_host.IsEditing || !_toolbar.IsVisible) CloseToolOptions();
    }

    private void RefreshCanvasVisibilityButton()
    {
        if (_canvasVisibilityButton is not { Content: Controls.Viewbox { Child: Controls.Canvas icon } } button ||
            icon.Children.OfType<System.Windows.Shapes.Path>().FirstOrDefault() is not { } glyph) return;
        bool visible = _host.IsVisible;
        glyph.Data = visible ? VisibleCanvasEye : HiddenCanvasEye;
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, visible ? "CanvasText" : "CanvasHiddenText");
        button.SetResourceReference(Controls.Control.BackgroundProperty, visible ? "CanvasButton" : "CanvasHiddenSurface");
        if (visible) button.BorderBrush = Brushes.Transparent;
        else button.SetResourceReference(Controls.Control.BorderBrushProperty, "CanvasHiddenBorder");
        string action = L.Get(visible ? "Toolbar.Canvas.Hide" : "Toolbar.Canvas.Show");
        string shortcut = FormatHotkey(_settings.Hide);
        button.ToolTip = action + " · " + shortcut;
        System.Windows.Automation.AutomationProperties.SetName(button, action);
        System.Windows.Automation.AutomationProperties.SetAcceleratorKey(button, shortcut);
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, L.Get(visible ? "Feedback4.Canvas.Visible" : "Feedback4.Canvas.Hidden"));
    }

    private static EditorTool ToolFamily(EditorTool tool) => ToolPresetLibrary.Family(tool);
    private static EditorTool ButtonFamily(EditorTool tool) => ToolFamily(tool) switch { EditorTool.Pixelate or EditorTool.Redact => EditorTool.Blur,
        EditorTool.Triangle => EditorTool.Symbol, var family => family };
    private static string ToolName(EditorTool tool) => L.Get(tool switch { EditorTool.Select => "ToolbarV2.Select", EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow => "ToolbarV2.Lines",
        EditorTool.Rectangle or EditorTool.RoundedRectangle => "ToolbarV2.Rectangles", EditorTool.Ellipse => "ToolbarV2.Ellipses", EditorTool.Symbol => "Tool.Symbol",
        EditorTool.Emoji => "Tool.Emoji", EditorTool.Stamp => "Tool.Stamp", EditorTool.Image => "Toolbar.Image", EditorTool.Crop => "Tool.Crop", EditorTool.CaptureRegion => "Tool.CaptureRegion", _ => "Preset." + tool });
    private Controls.MenuItem ToolMenuItem(EditorTool tool)
    {
        string shortcut = ToolShortcutCatalog.Hint(tool);
        var item = new Controls.MenuItem { Header = ToolName(tool), InputGestureText = shortcut, Icon = MenuGeometry("M2,2 L14,2 L14,14 L2,14 Z") };
        if (tool == EditorTool.Crop) { item.IsEnabled = _editor.CanCropSelection; _cropMenuItems.Add(item); }
        item.Click += (_, _) => RunUiAction(() => ActivateTool(tool)); return item;
    }
    private Controls.Button ToolbarIcon(string label, string geometry, Action action, string shortcut = "")
    {
        var button = ToolButton(label, geometry, action);
        button.MinWidth = button.Width = 26; button.MinHeight = button.Height = 26;
        button.FocusVisualStyle=null;Controls.ToolTipService.SetShowsToolTipOnKeyboardFocus(button,false);
        button.Margin = new Thickness(2, 0, 2, 0); button.BorderThickness = new Thickness(1); button.BorderBrush=Brushes.Transparent;button.Padding = new Thickness(3);
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new TranslateTransform(1.5, 1.5) };
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText");
        var bounds = new Controls.Canvas { Width = 20, Height = 20 }; bounds.Children.Add(glyph);
        button.Content = new Controls.Viewbox { Width = 16, Height = 16, Stretch = Stretch.Uniform, Child = bounds };
        button.ToolTip = shortcut.Length == 0 ? label : label + " · " + shortcut;
        if(shortcut.Length>0)System.Windows.Automation.AutomationProperties.SetAcceleratorKey(button,shortcut);
        Controls.ToolTipService.SetInitialShowDelay(button, 650); Controls.ToolTipService.SetBetweenShowDelay(button, 300); Controls.ToolTipService.SetShowDuration(button, 7000);
        return button;
    }
    private void AddShortcutHint(Controls.Button button,string shortcut,EditorTool? tool=null,bool lowerRow=false)
    {
        // Rounded borders leave a fractional inner size at mixed DPI. Let the
        // content fill that size so WPF does not clip the corner legend to a
        // fixed 24-DIP layout slot which can exceed the available space.
        button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.VerticalContentAlignment=VerticalAlignment.Stretch;
        var grid=new Controls.Grid();
        var presenter=new Controls.ContentPresenter {Content=button.Content,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};grid.Children.Add(presenter);
        if(tool is { } key)_toolbarGlyphHosts[key]=presenter;
        var hint=ToolbarShortcutHint.Create(shortcut,lowerRow);
        if(lowerRow)hint.RenderTransform=new TranslateTransform(-6,1);
        _toolbarShortcutHintLabels.Add(hint);grid.Children.Add(hint);button.Content=grid;
    }
    private void RefreshToolShortcutHints()
    {
        foreach(var hint in _toolbarShortcutHintLabels)hint.Visibility=_settings.ShowToolShortcutHints?Visibility.Visible:Visibility.Collapsed;
        _stampShelf?.SetShortcutHintsVisible(_settings.ShowToolShortcutHints);
        if(_toolbarShortcutHintsMenu is not null)_toolbarShortcutHintsMenu.IsChecked=_settings.ShowToolShortcutHints;
    }
    private static Controls.Border Separator()
    {var separator=new Controls.Border {Width=1,Margin=new Thickness(4,4,4,4)};separator.SetResourceReference(Controls.Border.BackgroundProperty,"CanvasBorder");return separator;}
    private void ClearCanvas()
    {if (_clearCanvasButton is not null) _ = _clearCanvasButton.ActivateAsync();}
    private Controls.Slider CreateToolbarOpacitySlider()
    {
        var opacity=new Controls.Slider {Minimum=0,Maximum=1,Value=1,Width=56,Height=26,Margin=new Thickness(2,0,2,0),VerticalAlignment=VerticalAlignment.Center,ToolTip=L.Get("Feedback3.Toolbar.Opacity")};
        opacity.FocusVisualStyle=null;Controls.ToolTipService.SetShowsToolTipOnKeyboardFocus(opacity,false);
        System.Windows.Automation.AutomationProperties.SetName(opacity,L.Get("Feedback3.Toolbar.Opacity"));
        opacity.PreviewMouseLeftButtonDown+=(_,_)=>{if(!DocumentOperationBusy){_editor.Cancel();Active.BeginTransaction(L.Get("History.LayerOpacity"));}};
        opacity.ValueChanged+=(_,_)=>
        {if(_syncingControls||DocumentOperationBusy)return;if(Active.IsTransactionActive){Active.Document.LayerOpacity=opacity.Value;Active.NotifyChanged();}else Active.Execute(L.Get("History.LayerOpacity"),document=>document.LayerOpacity=opacity.Value);};
        opacity.PreviewMouseLeftButtonUp+=(_,_)=>{if(Active.IsTransactionActive)Active.Commit();};
        opacity.LostMouseCapture+=(_,_)=>{if(Active.IsTransactionActive)Active.Commit();};return opacity;
    }
    private static Controls.Border Surface(UIElement child, double radius, Thickness padding)
    {
        var border = new Controls.Border { Child = child, CornerRadius = new CornerRadius(radius), Padding = padding, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasSurface"); border.SetResourceReference(Controls.Border.BorderBrushProperty, "CanvasBorder"); return border;
    }
    private static System.Windows.Shapes.Path MenuGeometry(string geometry)
    {
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 15, Height = 15, Stretch = Stretch.Uniform,
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText"); return path;
    }
}
