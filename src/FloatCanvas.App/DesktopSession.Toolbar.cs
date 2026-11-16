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
    private readonly List<(Controls.Button Button, string Id, string Label)> _toolbarCommandHints=[];
    private ToolbarLauncher _toolbarLauncher = null!;
    private StampShelfControl _stampShelf = null!;
    private Controls.Border _toolbarExpandedContent = null!;
    private ToolbarUpperRow _toolbarUpperRow = null!;
    private readonly Dictionary<EditorTool, Controls.Button> _extraToolButtons = [];
    private Controls.Button? _canvasVisibilityButton;
    private static readonly Geometry VisibleCanvasEye = Geometry.Parse("M1,8 Q8,-1 15,8 Q8,17 1,8 Z M8,5 A3,3 0 1 1 7.99,5");
    private static readonly Geometry HiddenCanvasEye = Geometry.Parse("M1,5.5 Q8,12.5 15,5.5 M3.5,7.5 L2,10 M8,9 L8,12 M12.5,7.5 L14,10");
    private readonly Dictionary<EditorTool, Controls.Button> _toolbarToolButtons = [];
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
    private NewCanvasButton? _newCanvasButton;
    private BoundFileIndicator? _boundFileIndicator;
    private RootCanvasToolbarHeader? _rootCanvasHeader;
    private Controls.Button? _stampLibraryButton;
    private Controls.ContentPresenter? _stampLibraryGlyph;
    private object? _stampLibraryDefaultGlyph;
    private StampEntry? _toolbarLibraryStamp;
    private bool? _mascotCanvasVisible;
    private bool? _mascotEditing;
    private Controls.Button? _canvasStateButton;
    private (uint Background, bool Perspective, bool GridVisible, uint GridColor, double GridWidth, StrokePattern GridPattern)? _canvasPreviewFingerprint;
    private const string MoreCollapsedGlyph = "M2,8 H14 V12 Q14,13 13,13 H3 Q2,13 2,12 Z M2,8 L4,3 H12 L14,8 M6,10.5 H10";
    private const string MoreExpandedGlyph = "M2,9 H14 V13 Q14,14 13,14 H3 Q2,14 2,13 Z M6,11.5 H10 M2,2 H14 V5 H2 Z M6,2 V5 M10,2 V5";
    private Controls.Button? _billboardButton;
    private readonly Dictionary<string, Controls.Button> _commandStateButtons = [];

    private Window CreateToolbarV2()
    {
        _toolbarLauncher = new ToolbarLauncher();
        var content = new Controls.Grid();
        content.Resources["CanvasButtonCornerRadius"]=new CornerRadius(ToolbarUpperRow.CornerRadiusValue);
        content.RowDefinitions.Add(new Controls.RowDefinition { Height = new GridLength(26) });
        // Match the two-DIP inner padding above and below the rows.
        content.RowDefinitions.Add(new Controls.RowDefinition { Height = new GridLength(2) });
        content.RowDefinitions.Add(new Controls.RowDefinition { Height = new GridLength(26) });
        for (int i = 0; i < 33; i++) content.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new GridLength(i is 2 or 13 or 17 or 21 or 24 ? 10 : 30) });
        int toolColumn = 0;
        void Cell(UIElement element, int column, int row = 0, int span = 1)
        { Controls.Grid.SetColumn(element, column); Controls.Grid.SetRow(element, row==0?0:2); Controls.Grid.SetColumnSpan(element, span); content.Children.Add(element); }
        void Tool(EditorTool tool, string label)
        {
            string shortcut=ToolShortcutCatalog.Hint(tool);
            // A toolbar revisit restores its family's working state. Remembered
            // legacy aliases must not act like an explicit recipe selection.
            var button = ToolbarIcon(label, CommandGlyphs.Tool(tool), () => ActivateTool(ToolFamily(ButtonFamily(_editor.Tool) == tool ? _editor.Tool : _toolbarButtonMemory.GetValueOrDefault(tool, tool))), shortcut);
            if(shortcut.Length>0)AddShortcutHint(button,shortcut,tool);
            button.Tag = "tool:" + tool; _toolbarToolButtons[tool] = button; Cell(button, toolColumn++);
        }
        Tool(EditorTool.Select, L.Get("ToolbarV2.Select"));
        Tool(EditorTool.Eraser, L.Get("Preset.Eraser"));
        for(int row=0;row<2;row++)Cell(Separator(),2,row);
        toolColumn=3;
        Tool(EditorTool.Freehand, L.Get("Preset.Freehand"));
        Tool(EditorTool.Highlighter, L.Get("Preset.Highlighter"));
        Tool(EditorTool.Line, L.Get("ToolbarV2.Lines"));
        Tool(EditorTool.Rectangle, L.Get("ToolbarV2.Rectangles"));
        Tool(EditorTool.Ellipse, L.Get("ToolbarV2.Ellipses"));
        Tool(EditorTool.Text, L.Get("Preset.Text"));
        Tool(EditorTool.Number,L.Get("Preset.Number"));
        Tool(EditorTool.Symbol, L.Get("Tool.Symbol"));
        Tool(EditorTool.Emoji, L.Get("Tool.Emoji"));
        var more=ToolbarIcon(L.Get("Feedback4.Toolbar.More"),MoreCollapsedGlyph,OpenMoreTools,"M");
        AddShortcutHint(more,"M",EditorTool.Blur);more.Tag="more";Cell(more,toolColumn++);
        foreach(var tool in new[]{EditorTool.Blur,EditorTool.Pixelate,EditorTool.Presentation,EditorTool.PointerPresentation})
        {
            var button = ToolbarIcon(ToolName(tool), CommandGlyphs.Tool(tool), () => ActivateTool(tool));
            button.Tag = "extra-tool:" + tool; _extraToolButtons.Add(tool, button);
        }
        _toolbarToolButtons[EditorTool.Blur]=more;
        for (int row = 0; row < 2; row++)
            Cell(Separator(),13,row);
        toolColumn = 14;
        void Action(string tag, string label, string geometry, Action action, string shortcut = "")
        { var button = ToolbarIcon(label, geometry, action, shortcut); button.Tag = tag; _commandStateButtons[tag] = button; Cell(button, toolColumn++); }
        Action("canvas", L.Get("Canvas.Title"), CommandGlyphs.Canvas, () => ActivateTool(EditorTool.Canvas), ToolShortcutCatalog.Hint(EditorTool.Canvas));
        _canvasStateButton = content.Children.OfType<Controls.Button>().Last();
        _toolbarToolButtons[EditorTool.Canvas] = _canvasStateButton;
        AddShortcutHint(_canvasStateButton, ToolShortcutCatalog.Hint(EditorTool.Canvas), EditorTool.Canvas);
        RefreshCanvasStateGlyph();
        _billboardButton=ToolbarIcon(L.Get("Billboard.Toggle"),CommandGlyphs.Placement(_editor.BillboardMode),ToggleBillboardMode);
        _billboardButton.Tag="billboard";Cell(_billboardButton,toolColumn++);
        Controls.ToolTipService.SetShowOnDisabled(_billboardButton,true);
        RefreshBillboardState();
        Cell(CreateCanvasNavigationButton(), toolColumn++);
        Cell(Separator(), toolColumn++);
        Action("import", L.Get("Toolbar.Image"), CommandGlyphs.Image, () => _ = RunCommand(() => ImportImagesAsync()), "I");
        AddShortcutHint(content.Children.OfType<Controls.Button>().Last(), "I", EditorTool.Image);
        Action("capture", L.Get("Acceptance2.Toolbar.Capture"), "M1,6 L1,2 L5,2 M11,2 L15,2 L15,6 M15,10 L15,14 L11,14 M5,14 L1,14 L1,10 M4,5 L12,5 L12,11 L4,11 Z", () => ActivateTool(EditorTool.CaptureRegion), "P");
        var captureButton = content.Children.OfType<Controls.Button>().Last();
        _toolbarToolButtons[EditorTool.CaptureRegion] = captureButton;
        AddShortcutHint(captureButton, "P", EditorTool.CaptureRegion);
        Action("crop", L.Get("Tool.Crop"), "M4,1 L4,11 Q4,12 5,12 L15,12 M1,4 L11,4 Q12,4 12,5 L12,15", () => ActivateTool(EditorTool.Crop));
        _toolbarToolButtons[EditorTool.Crop] = content.Children.OfType<Controls.Button>().Last();
        _toolbarToolButtons[EditorTool.Crop].IsEnabled = _editor.CanCropSelection;
        Controls.ToolTipService.SetShowOnDisabled(_toolbarToolButtons[EditorTool.Crop], true);
        Cell(Separator(), toolColumn++);
        Action("properties", L.Get("ToolbarV2.Properties"), "M1,3 L15,3 M1,8 L15,8 M1,13 L15,13 M5,1 L5,5 M11,6 L11,10 M6,11 L6,15", ToggleInspector, "Enter");
        Action("layers", L.Get("ToolbarV2.Layers"), "M1,5 L8,1 L15,5 L8,9 Z M1,9 L8,13 L15,9 M1,13 L8,17 L15,13", OpenLayers, "Panel.Layers");
        Cell(Separator(), toolColumn++);
        Action("undo", L.Get("ToolbarV2.Undo"), "M6,3 L2,7 L6,11 M2,7 L10,7 Q14,7 14,13", Undo, "Edit.Undo");
        Action("redo", L.Get("ToolbarV2.Redo"), "M10,3 L14,7 L10,11 M14,7 L6,7 Q2,7 2,13", Redo, "Edit.Redo");
        _newCanvasButton = new NewCanvasButton(() => !_disposed && !DocumentOperationBusy, () => RunCommand(ReplaceCanvasAsync), NeedsNewCanvasConfirmationAsync);
        Cell(_newCanvasButton, toolColumn++);
        var clearCurrent=ToolbarIcon(L.Get("History.ClearCurrent"),CommandGlyphs.ClearCanvas,()=>_ = RunCommand(ClearCurrentCanvasAsync),"Edit.Clear");
        clearCurrent.Tag="clear";clearCurrent.SetResourceReference(Controls.Control.StyleProperty,"CanvasDangerButton");
        clearCurrent.ClearValue(Controls.Control.BackgroundProperty);
        var clearGlyph=(System.Windows.Shapes.Path)((Controls.Canvas)((Controls.Viewbox)clearCurrent.Content).Child).Children[0];
        clearGlyph.Stroke=Brushes.White;Cell(clearCurrent,toolColumn++);
        _opacitySlider=CreateToolbarOpacitySlider();Cell(_opacitySlider,toolColumn,0,2);toolColumn+=2;
        Action("visibility",L.Get("Desktop.ToggleVisibility"),"M1,8 Q8,-1 15,8 Q8,17 1,8 Z M8,5 A3,3 0 1 1 7.99,5",ToggleCanvasVisibility);
        _canvasVisibilityButton = content.Children.OfType<Controls.Button>().Last();
        RefreshCanvasVisibilityButton();
        _passThroughButton = ToolbarIcon(L.Get("Acceptance2.Toolbar.Done"), CommandGlyphs.PassThrough, () => SetEditing(false), FormatHotkey(_settings.Toggle));
        _passThroughButton.Tag = "done"; Cell(_passThroughButton, toolColumn++);
        var manage=ToolbarIcon(L.Get("Stamp.LibraryTitle"),"M6,8.5 V7 C6,5.5 4.8,4.7 4.8,3.2 C4.8,.6 11.2,.6 11.2,3.2 C11.2,4.7 10,5.5 10,7 V8.5 M6,8.5 H10 Q12,8.5 12,11 M6,8.5 Q4,8.5 4,11 M1.5,11 H14.5 V13.5 H1.5 Z M3,15.5 H13",ShowStampLibrary,"Stamp.Library");
        AddShortcutHint(manage,"",lowerRow:true);manage.Tag="manage-stamps";Cell(manage,0,1);
        _stampLibraryButton=manage;
        _stampLibraryGlyph=((Controls.Grid)manage.Content).Children.OfType<Controls.ContentPresenter>().Single();
        _stampLibraryDefaultGlyph=_stampLibraryGlyph.Content;
        var save=ToolbarIcon(L.Get("Feedback3.Toolbar.NewStamp"),"M4.5,8.5 V7 C4.5,5.5 3.5,4.7 3.5,3.2 C3.5,.6 8.5,.6 8.5,3.2 C8.5,4.7 7.5,5.5 7.5,7 V8.5 M4.5,8.5 H7.5 Q9.5,8.5 9.5,11 M4.5,8.5 Q2.5,8.5 2.5,11 M1,11 H11 V13.5 H1 Z M2,15.5 H10 M12.5,2.5 V7.5 M10,5 H15",SaveStampSelection,"Stamp.Create");
        AddShortcutHint(save,"",lowerRow:true);save.Tag="save-stamp";Cell(save,1,1);
        _stampShelf = new(_stampLibrary, SelectStampSlot); Cell(_stampShelf, 3, 1, 10);
        _stampShelf.EntryMenuFactory=(id,slot)=>
        {
            EnsureStampWindow();return _stampWindow!.CreateToolbarMenu(id,slot,_toolbar);
        };
        _status.Text = L.Get("Preset.Freehand"); _toolbarStatusDisplay = new(_status); Cell(_toolbarStatusDisplay, 14, 1, 19);
        RefreshToolShortcutHints();
        _toolbarExpandedContent = Surface(content, ToolbarUpperRow.OuterCornerRadius, new Thickness(20, 2, 4, 2));
        _toolbarExpandedContent.Visibility = _settings.ToolbarExpanded ? Visibility.Visible : Visibility.Collapsed;
        _toolbarExpandedContent.SetResourceReference(UIElement.EffectProperty,"ToolbarEdgeEffect");
        var shell = _toolbarLauncher.CreateSurface(_toolbarExpandedContent);
        _boundFileIndicator=new BoundFileIndicator(()=>_ = RunCommand(RevertActiveFileAsync),LocateActiveFile,DetachActiveFile,
            CanRevertActiveFile,()=>!DocumentOperationBusy);
        _rootCanvasHeader = new(_boundFileIndicator, () => SwitchRootCanvas(RootCanvasKind.Main),
            () => !DocumentOperationBusy && _host.IsVisible && !Active.IsTransactionActive && _prefabEditContext is null);
        _toolbarUpperRow = new(content, _rootCanvasHeader, _extraToolButtons.Values.ToArray(), _newCanvasButton);
        _toolbarUpperRow.ExpansionChanged += () => { RefreshMoreToolPreviews(); RefreshToolbarToolSelection(); RepositionToolOptions(); };
        _toolbarUpperRow.Visibility = _toolbarExpandedContent.Visibility;
        shell.Children.Add(_toolbarUpperRow);
        _toolbarUpperRow.AttachSurface(shell, _toolbarExpandedContent, _toolbarLauncher);
        shell.LayoutUpdated+=(_,_)=>
        {
            if(_toolbarExpandedContent.Visibility!=Visibility.Visible)return;
            var contentLeft = content.TranslatePoint(new Point(), shell).X;
            var upperMargin = new Thickness(contentLeft, 3, 0, 0);
            if (_toolbarUpperRow.Margin != upperMargin) _toolbarUpperRow.Margin = upperMargin;
            if (_toolbarUpperRow.Width != content.ActualWidth) _toolbarUpperRow.Width = content.ActualWidth;
        };
        var window = new Window { Title = L.Get("Toolbar.Title"), Content = shell, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true, ShowInTaskbar = true, Icon = _host.IsVisible?AppIcon.WindowImage:AppIcon.HiddenWindowImage, FontSize = 14, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            UseLayoutRounding = true, SnapsToDevicePixels = true };
        OverlayWindowScope.Prepare(window);
        ToolbarWindowBounds.Attach(window, () => _host.Monitors);
        _toolbarMenu = CreateLauncherMenu();
        _toolbarMenu.TargetPressToggles = true;
        // A WPF menu captures the mouse. Inspect coordinates before outside-click
        // dismissal so the launcher press cannot become a second delayed open.
        void LauncherMenuInput(object sender, PreProcessInputEventArgs args)
        {
            if (!_toolbarMenu.IsOpen || args.StagingItem.Input is not MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed } mouse) return;
            var point = mouse.GetPosition(_toolbarLauncher);
            if (!new Rect(_toolbarLauncher.RenderSize).Contains(point)) return;
            if (mouse.ChangedButton == MouseButton.Left)
            {
                _toolbarLauncher.DismissMenuPress(point);
                mouse.Handled = true;
            }
            else if (mouse.ChangedButton == MouseButton.Right) mouse.Handled = true;
        }
        _toolbarMenu.Opened += (_, _) => InputManager.Current.PreProcessInput += LauncherMenuInput;
        _toolbarMenu.Closed += (_, _) => InputManager.Current.PreProcessInput -= LauncherMenuInput;
        _toolbarMenu.PlacementTarget = _toolbarLauncher;
        _toolbarMenu.Placement = Primitives.PlacementMode.Custom;
        _toolbarMenu.CustomPopupPlacementCallback = LauncherMenuPlacements;
        _toolbarLauncher.CloseOpenMenu = () =>
        {
            if (!_toolbarMenu.IsOpen) return false;
            _toolbarMenu.IsOpen = false; return true;
        };
        _toolbarLauncher.MenuRequested += () => { if (!_toolOptionsPinned) CloseToolOptions(); _toolbarMenu.IsOpen = true; };
        _toolbarLauncher.ToggleVisibilityRequested += () => RunUiAction(ToggleCanvasVisibility);
        _toolbarLauncher.DragRequested += () =>
        {
            _toolbarMenu.IsOpen = false;
            if(OverlayWindowDrag.TryStart(window,()=>{SaveToolbarPosition();RepositionToolOptions();}))return;
            if (Mouse.LeftButton == MouseButtonState.Pressed) window.DragMove();
            SaveToolbarPosition(); RepositionToolOptions();
        };
        CreateToolOptionsPopup(window);
        Action libraryChanged = () => window.Dispatcher.BeginInvoke(() => { ValidateSelectedStamp(); RefreshToolbarState(); });
        _stampLibrary.Changed += libraryChanged;
        window.Closed += (_, _) => _stampLibrary.Changed -= libraryChanged;
        window.LocationChanged += (_, _) => { if (ReferenceEquals(_toolbar, window) && window.IsVisible) SaveToolbarPosition(); RepositionToolOptions(); RefreshPassThroughLauncher(); };
        window.SizeChanged += (_, _) => RepositionToolOptions();
        window.Deactivated += (_, _) => { _newCanvasButton.CancelConfirmation(); QueueToolOptionsFocusCheck(); };
        window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) { _newCanvasButton.CancelConfirmation(); CloseToolOptions(); _toolbarMenu.IsOpen = false; _toolbarLauncher.CancelPendingGestures(); } };
        AppInputPolicy.AttachToolbarToggle(window, () => OnKey(0x09));
        window.PreviewKeyDown += (_, e) =>
        {
            if(e.Handled)return;
            if (e.OriginalSource is ToolbarLauncher && e.Key == Key.Apps) return;
            var key = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
            if (!EditorShortcutPolicy.ShouldRoute(key, Keyboard.Modifiers, e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            if (!AppInputPolicy.IsRepeat(e) || EditorShortcutPolicy.AllowsRepeat(key, (OverlayModifiers)Keyboard.Modifiers)) OnKey(key);
        };
        window.Closing += (_, e) => { if (!_disposed) { e.Cancel = true; RunUiAction(() => SetEditing(false)); } };
        return window;
    }

    private void ToggleToolbarExpanded()
    {
        if (_disposed || DocumentOperationBusy || !_host.IsEditing || !_toolbar.IsVisible) return;
        _settings.ToolbarExpanded = !_settings.ToolbarExpanded;
        _toolbarExpandedContent.Visibility = _settings.ToolbarExpanded ? Visibility.Visible : Visibility.Collapsed;
        _toolbarUpperRow.Visibility = _toolbarExpandedContent.Visibility;
        RefreshToolbarExpansionMenuItem();
        RefreshBoundFileIndicator();
        _rootCanvasHeader?.Refresh(Active.ActiveRootCanvas);
        RefreshEditingScope();
        if (_settings.ToolbarExpanded)
            _toolbar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_toolbar.IsVisible) { PlaceToolbar(); SaveToolbarPosition(); } });
    }

    private void OpenMoreTools()
    {
        if(DocumentOperationBusy || !_host.IsEditing || !_toolbar.IsVisible)return;
        if(!_toolOptionsPinned)CloseToolOptions();
        RefreshMoreToolPreviews();
        if (!_settings.ToolbarExpanded) ToggleToolbarExpanded();
        _toolbarUpperRow.SetExpanded(!_toolbarUpperRow.IsExpanded);
    }

    private void ActivateTool(EditorTool tool)
    {
        CancelBorrowedTool();
        ForgetBorrowedTool();
        ActivateToolCore(tool,showOptions:true);
    }

    private void ActivateToolCore(EditorTool tool,bool showOptions)
    {
        if (DocumentOperationBusy) return;
        if(tool!=EditorTool.CaptureRegion) CompleteGlobalCapture(inserted:false,restoreMode:false);
        if (tool == EditorTool.Canvas && !CanEditDisplayedCanvas)
        { UpdateStatus(L.Get("GroupCanvas.EditPreviewInIndependent")); return; }
        if (tool == EditorTool.Crop && _editor.TryRedirectWindowSourceCrop()) return;
        bool wasOpen = IsToolOptionsOpen || _canvasPlacementWindows.Contains(_toolOptionsWindow) &&
            ReferenceEquals(_canvasPlacementSession, Active) && _canvasPlacementTarget?.IsCurrent == true &&
            _host.EditingSessionVersion == _canvasPlacementEditingVersion;
        if (tool != _editor.Tool) EndCanvasCalibration(hidePanel: _canvasPlacementSession is not null);
        if (tool == EditorTool.Crop && !_editor.CanCropSelection) return;
        if (tool == EditorTool.CaptureRegion && Active.Document.Kind == DocumentKind.FrozenCapture) ReturnToLive();
        var family = ToolFamily(tool); var currentFamily = ToolFamily(_editor.Tool);
        bool same = currentFamily == family && (tool is not (EditorTool.Arrow or EditorTool.DoubleArrow or EditorTool.RoundedRectangle or EditorTool.Triangle) || AliasMatches(tool));
        if (!same)
        {
            SwitchToolState(tool, applyAlias: true);
        }
        SynchronizeCanvasTool();
        UpdateStatus(ToolName(_editor.Tool)); RefreshToolbarState();
        if (!_host.IsEditing) { CloseToolOptions(); return; }
        if(!showOptions)return;
        bool open = ShouldOpenToolOptions(same, wasOpen);
        if (open) OpenToolOptions(); else CloseToolOptions();
    }

    internal static bool ShouldOpenToolOptions(bool sameTool, bool wasOpen) => sameTool != wasOpen;

    private bool AliasMatches(EditorTool requested)
    {
        var p = _editor.Parameters;
        var start = p.Style.StartArrowHead ?? (_editor.Tool == EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None);
        var end = p.Style.EndArrowHead ?? (_editor.Tool is EditorTool.Arrow or EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None);
        return requested switch { EditorTool.Arrow => start == ArrowHeadStyle.None && end != ArrowHeadStyle.None,
            EditorTool.DoubleArrow => start != ArrowHeadStyle.None && end != ArrowHeadStyle.None,
            EditorTool.RoundedRectangle => (p.CornerRadius ?? (_editor.Tool == EditorTool.RoundedRectangle ? 1 : 0)) > 0,
            EditorTool.Triangle => p.Symbol==SymbolKind.Triangle, _ => true };
    }
    private void RefreshCanvasStateGlyph()
    {
        if (_canvasStateButton is null) return;
        var canvas = CurrentCanvasSettings;
        var fingerprint = (canvas.BackgroundColor, canvas.Projection.Enabled, canvas.Grid.Visible, canvas.Grid.Color, canvas.Grid.Width, canvas.Grid.Pattern);
        if (_canvasPreviewFingerprint == fingerprint) return;
        _canvasPreviewFingerprint = fingerprint;
        var parameters = new CanvasPresetParameters { BackgroundColor = canvas.BackgroundColor, ProjectionEnabled = canvas.Projection.Enabled,
            GridVisible = canvas.Grid.Visible, GridColor = canvas.Grid.Color, GridWidth = canvas.Grid.Width, GridPattern = canvas.Grid.Pattern };
        var content = new CanvasParameterIcon(parameters, 16);
        if (_toolbarGlyphHosts.TryGetValue(EditorTool.Canvas, out var presenter)) presenter.Content = content;
        else _canvasStateButton.Content = content;
    }
    private void RefreshHistoryCommandState()
    {
        foreach (var (key, button) in _commandStateButtons.Where(pair => pair.Key is "undo" or "redo"))
        {
            button.IsEnabled = CanExecuteUiCommand(key);
            string label = L.Get(key == "undo" ? "ToolbarV2.Undo" : "ToolbarV2.Redo") + (" · " + EditorCommandCatalog.Hint(key == "undo" ? "Edit.Undo" : "Edit.Redo"));
            string? operation = key == "undo" ? Active.UndoLabel : Active.RedoLabel;
            if (button.IsEnabled && operation is not null) label += "\n" + operation;
            button.ToolTip = button.IsEnabled ? label : label + "\n" + UiCommandDisabledHint(key);
            Controls.ToolTipService.SetShowOnDisabled(button, true);
        }
    }
    private void RefreshToolbarState()
    {
        RefreshBoundFileIndicator();
        _rootCanvasHeader?.Refresh(Active.ActiveRootCanvas);
        RefreshEditingScope();
        RefreshCanvasVisibilityButton();
        RefreshCanvasStateGlyph();
        RefreshBillboardState();
        if (_toolbarToolButtons.Count == 0) return;
        RefreshHistoryCommandState();
        ValidateSelectedStamp();
        _toolbarStatusDisplay?.Refresh();
        // Crop is a dedicated entry with no remembered aliases. Keep legacy shared
        // button cache keys compatible while its working parameters persist normally.
        RefreshToolbarToolSelection();
        foreach (var item in _cropMenuItems) item.IsEnabled = _editor.CanCropSelection;
        _syncingControls = true;
        try { if (!Active.IsTransactionActive) _opacitySlider.Value = CurrentCanvasLayerOpacity; _opacitySlider.IsEnabled = CanEditDisplayedCanvas && !Active.IsReadOnly; }
        finally { _syncingControls = false; }
        var parameters = _editor.Parameters;
        string fingerprint = JsonSerializer.Serialize(parameters);
        if (_toolbarParameterFingerprint != fingerprint)
        {
            if (parameters.Tool != EditorTool.Canvas) _toolbarToolMemory[ToolFamily(parameters.Tool)] = parameters;
            _toolbarParameterFingerprint = fingerprint;
            if (parameters.Tool != EditorTool.Canvas && IsToolOptionsOpen) _toolOptionsPanel.Refresh();
            foreach(var(tool,presenter)in _toolbarGlyphHosts)
            {
                if(tool == EditorTool.Canvas)continue;
                if(tool == EditorTool.Blur && ButtonFamily(_editor.Tool)!=EditorTool.Blur)
                {
                    presenter.Content=CommandGlyphs.Create(MoreCollapsedGlyph);
                    continue;
                }
                var own=ToolbarPreviewParameters(tool == EditorTool.Blur ? _toolbarButtonMemory.GetValueOrDefault(EditorTool.Blur, EditorTool.Blur) : tool);
                presenter.Content=ToolParameterIcon.Create(own);
            }
            RefreshMoreToolPreviews();
            _toolStateStore?.MarkChanged();
        }
        if (IsToolOptionsOpen && _toolbarObservedTool != _editor.Tool) AnchorToolOptions();
        _toolbarObservedTool = _editor.Tool;
        Guid? activeStamp = _editor.Tool == EditorTool.Stamp ? _selectedStampId : null;
        Guid? slotStamp = _selectedStampFromSlot ? activeStamp : null;
        if (_toolbarActiveStamp != slotStamp) { _toolbarActiveStamp = slotStamp; _stampShelf.SetActive(slotStamp); }
        RefreshLibraryStamp(activeStamp is not null && !_selectedStampFromSlot ? _stampLibrary.Items.FirstOrDefault(entry=>entry.Id==activeStamp) : null);
        if (!_host.IsEditing || !_toolbar.IsVisible) CloseToolOptions();
    }

    private ToolParameters ToolbarPreviewParameters(EditorTool tool) =>
        _toolbarToolMemory.TryGetValue(ToolFamily(tool), out var saved) ? saved :
        _toolPresets.OfficialFor(tool).First().Parameters;

    private void RefreshToolbarToolSelection()
    {
        if (_editor.Tool != EditorTool.Crop && _toolbarToolButtons.ContainsKey(ButtonFamily(_editor.Tool))) _toolbarButtonMemory[ButtonFamily(_editor.Tool)] = _editor.Tool;
        foreach (var (tool, button) in _toolbarToolButtons)
        {
            if (tool == EditorTool.Crop) button.IsEnabled = _editor.CanCropSelection;
            bool selected = tool == EditorTool.Blur ? _toolbarUpperRow.IsExpanded || ButtonFamily(_editor.Tool) == tool : ButtonFamily(_editor.Tool) == tool;
            RefreshToolTooltip(tool, button, selected);
            if (tool == EditorTool.Blur)
            {
                string binding=EditorCommandCatalog.Hint("Tools.More");
                button.ToolTip = L.Get(_toolbarUpperRow.IsExpanded ? "Toolbar.MoreCollapse" : "Toolbar.MoreExpand") + (binding.Length>0?" · "+binding:"");
            }
            if(selected)button.SetResourceReference(Controls.Control.BorderBrushProperty,"CanvasAccent");else button.BorderBrush=Brushes.Transparent;
            button.SetResourceReference(Controls.Control.BackgroundProperty, selected ? "CanvasHover" : "CanvasSurface");
            if (tool == EditorTool.Blur && _toolbarUpperRow.IsExpanded)
            {
                button.SetResourceReference(Controls.Control.BackgroundProperty, "CanvasHiddenSurface");
                button.SetResourceReference(Controls.Control.BorderBrushProperty, "CanvasHiddenBorder");
            }
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? L.Get("ToolbarV2.Selected") : "");
        }
        foreach (var (tool, button) in _extraToolButtons)
        {
            bool selected = ToolFamily(_editor.Tool) == tool;
            RefreshToolTooltip(tool, button, selected);
            if (selected) button.SetResourceReference(Controls.Control.BorderBrushProperty, "CanvasAccent"); else button.BorderBrush = Brushes.Transparent;
            button.SetResourceReference(Controls.Control.BackgroundProperty, selected ? "CanvasHover" : "CanvasSurface");
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? L.Get("ToolbarV2.Selected") : "");
        }
    }

    private static void RefreshToolTooltip(EditorTool tool, Controls.Button button, bool selected)
    {
        string name = System.Windows.Automation.AutomationProperties.GetName(button);
        string shortcut = System.Windows.Automation.AutomationProperties.GetAcceleratorKey(button);
        string text = shortcut.Length == 0 ? name : name + " · " + shortcut;
        if (selected)
        {
            // M toggles the extra tool row; ordinary tool keys revisit their panel.
            string hint = shortcut.Length > 0 && tool != EditorTool.Blur
                ? L.Format("Toolbar.ToolOptionsWithKeyHint", EditorCommandCatalog.Hint("Panel.Tools"), shortcut)
                : L.Format("Toolbar.ToolOptionsHint", EditorCommandCatalog.Hint("Panel.Tools"));
            if (tool == EditorTool.Select) text += "\n" + L.Get("Toolbar.SelectionHint");
            text += "\n" + hint;
        }
        if (!Equals(button.ToolTip, text)) button.ToolTip = text;
    }

    private void RefreshMoreToolPreviews()
    {
        foreach (var (tool, button) in _extraToolButtons) button.Content = ToolParameterIcon.Create(ToolbarPreviewParameters(tool));
        if (_toolbarGlyphHosts.TryGetValue(EditorTool.Blur, out var presenter))
            presenter.Content = _toolbarUpperRow.IsExpanded ? CommandGlyphs.Create(MoreExpandedGlyph) :
                ButtonFamily(_editor.Tool) == EditorTool.Blur ? ToolParameterIcon.Create(ToolbarPreviewParameters(_editor.Tool)) :
                CommandGlyphs.Create(MoreCollapsedGlyph);
    }

    private void RefreshCanvasVisibilityButton()
    {
        if (_canvasVisibilityButton is not { Content: Controls.Viewbox { Child: Controls.Canvas icon } } button ||
            icon.Children.OfType<System.Windows.Shapes.Path>().FirstOrDefault() is not { } glyph) return;
        bool visible = _host.IsVisible;
        RefreshMascotVisibility(visible);
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
    private static EditorTool ButtonFamily(EditorTool tool) => ToolFamily(tool) switch { EditorTool.Pixelate or EditorTool.Presentation or EditorTool.PointerPresentation => EditorTool.Blur,
        EditorTool.Triangle => EditorTool.Symbol, var family => family };
    private static string ToolName(EditorTool tool) => ToolDisplayName.Get(tool);
    private Controls.Button ToolbarIcon(string label, string geometry, Action action, string shortcut = "")
    {
        var command=EditorCommandCatalog.All.FirstOrDefault(c=>c.Id==shortcut);
        var button = ToolButton(label, geometry, command is null ? action : ()=>ExecuteEditorCommand(command));
        if(command is not null) { _toolbarCommandHints.Add((button,command.Id,label)); shortcut=EditorCommandCatalog.Hint(command.Id); }
        // Keep the frame pixel-aligned, but allow its centered artwork to sit
        // between pixels when the frame and glyph have different pixel parity.
        var glyphStyle = new Style(typeof(Controls.ContentPresenter));
        glyphStyle.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, false));
        button.Resources.Add(typeof(Controls.ContentPresenter), glyphStyle);
        button.MinWidth = button.Width = 26; button.MinHeight = button.Height = 26;
        button.FocusVisualStyle=null; Controls.ToolTipService.SetShowsToolTipOnKeyboardFocus(button,false);
        button.Margin = new Thickness(2, 0, 2, 0); button.BorderThickness = new Thickness(1); button.BorderBrush=Brushes.Transparent;button.Padding = new Thickness(3);
        button.Content = CommandGlyphs.Create(geometry);
        button.ToolTip = shortcut.Length == 0 ? label : label + " · " + shortcut;
        if(shortcut.Length>0)System.Windows.Automation.AutomationProperties.SetAcceleratorKey(button,shortcut);
        Controls.ToolTipService.SetInitialShowDelay(button, 650); Controls.ToolTipService.SetBetweenShowDelay(button, 300); Controls.ToolTipService.SetShowDuration(button, 7000);
        return button;
    }
    private void AddShortcutHint(Controls.Button button,string shortcut,EditorTool? tool=null,bool lowerRow=false)
    {
        var content=ToolbarShortcutHint.CreateButtonContent(button,shortcut,lowerRow);
        if(tool is { } key)_toolbarGlyphHosts[key]=content.Glyph;
        if (tool is {} hintedTool) content.Hint.Tag = (hintedTool, button);
        _toolbarShortcutHintLabels.Add(content.Hint);button.Content=content.Content;
    }
    private void RefreshToolShortcutHints()
    {
        foreach(var hint in _toolbarShortcutHintLabels)
        {
            if(hint.Tag is ValueTuple<EditorTool,Controls.Button> target)
            {
                string binding=Equals(target.Item2.Tag,"more")?EditorCommandCatalog.Hint("Tools.More"):ToolShortcutCatalog.Hint(target.Item1);
                hint.Text=binding.Length==1?binding:"";
                target.Item2.ToolTip=(Equals(target.Item2.Tag,"more")?L.Get("Feedback4.Toolbar.More"):ToolName(target.Item1))+(binding.Length>0?" · "+binding:"");
                System.Windows.Automation.AutomationProperties.SetAcceleratorKey(target.Item2,binding);
            }
            hint.Visibility=_settings.ShowToolShortcutHints?Visibility.Visible:Visibility.Collapsed;
        }
        foreach(var (button,id,label) in _toolbarCommandHints)
        {
            string binding=EditorCommandCatalog.Hint(id);
            button.ToolTip=label+(binding.Length>0?" · "+binding:"");
            System.Windows.Automation.AutomationProperties.SetAcceleratorKey(button,binding);
        }
        foreach(var (tool,button) in _extraToolButtons)
        {
            System.Windows.Automation.AutomationProperties.SetAcceleratorKey(button,ToolShortcutCatalog.Hint(tool));
            RefreshToolTooltip(tool,button,ToolFamily(_editor.Tool)==tool);
        }
        RefreshLibraryStamp(_toolbarLibraryStamp);
        _stampShelf?.Refresh();
        _stampShelf?.SetShortcutHintsVisible(_settings.ShowToolShortcutHints);
    }
    private static Controls.Border Separator()
    {var separator=new Controls.Border {Width=1,Margin=new Thickness(4,4,4,4)};separator.SetResourceReference(Controls.Border.BackgroundProperty,"CanvasBorder");return separator;}
    private Controls.Slider CreateToolbarOpacitySlider()
    {
        var opacity=new ToolbarOpacitySlider {Margin=new Thickness(2,0,2,0),VerticalAlignment=VerticalAlignment.Center,ToolTip=L.Get("Feedback3.Toolbar.Opacity")+" · "+EditorCommandCatalog.Hint("Canvas.OpacityUp")+" / "+EditorCommandCatalog.Hint("Canvas.OpacityDown")};
        opacity.FocusVisualStyle=null; Controls.ToolTipService.SetShowsToolTipOnKeyboardFocus(opacity,false);
        System.Windows.Automation.AutomationProperties.SetName(opacity,L.Get("Feedback3.Toolbar.Opacity"));
        void RefreshOpacityTooltip() => opacity.ToolTip = L.Get("Feedback3.Toolbar.Opacity") + $" · {opacity.Value * 100:0.#}% · "+EditorCommandCatalog.Hint("Canvas.OpacityUp")+" / "+EditorCommandCatalog.Hint("Canvas.OpacityDown");
        RefreshOpacityTooltip();
        opacity.ToolTipOpening+=(_,_)=>RefreshOpacityTooltip();
        EditorSession? pointerSession=null; CanvasSettingsTarget? pointerTarget=null; long pointerTransaction=0;
        bool OwnsPointerTransaction()=>pointerSession is {IsTransactionActive:true}&&ReferenceEquals(pointerSession,Active)&&pointerSession.TransactionId==pointerTransaction&&pointerTarget?.IsCurrent==true;
        opacity.PointerGestureStarted+=()=>{pointerSession=null;pointerTarget=null;if(!DocumentOperationBusy&&CanEditDisplayedCanvas&&!Active.IsReadOnly){_editor.Cancel();pointerSession=Active;pointerTarget=CaptureCanvasSettingsTarget();pointerSession.BeginTransaction(L.Get("History.LayerOpacity"));pointerTransaction=pointerSession.TransactionId;}};
        opacity.ValueChanged+=(_,_)=>
        {
            RefreshOpacityTooltip();if(_syncingControls||DocumentOperationBusy||!CanEditDisplayedCanvas||Active.IsReadOnly)return;
            if(opacity.IsPointerInteracting&&!OwnsPointerTransaction())
            {
                // Escape or a document command can end this gesture's transaction.
                // Late pointer input must not turn the cancelled preview into new edits.
                opacity.EndPointerGesture();
                _syncingControls=true;try{opacity.Value=CurrentCanvasLayerOpacity;}finally{_syncingControls=false;}
                return;
            }
            if(opacity.IsPointerInteracting)pointerTarget!.ChangeOpacity(opacity.Value);else ChangeCurrentCanvasOpacity(opacity.Value);
        };
        opacity.PointerGestureCompleted+=()=>{if(OwnsPointerTransaction())pointerSession!.CommitSettings();else if(pointerSession is {IsTransactionActive:true}&&pointerSession.TransactionId==pointerTransaction)pointerSession.Cancel();pointerSession=null;pointerTarget=null;};return opacity;
    }
    private static Controls.Border Surface(UIElement child, double radius, Thickness padding)
    {
        var border = new Controls.Border { Child = child, CornerRadius = new CornerRadius(radius), Padding = padding, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasSurface"); border.SetResourceReference(Controls.Border.BorderBrushProperty, "CanvasBorder"); return border;
    }

}
