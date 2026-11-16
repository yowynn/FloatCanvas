using FloatCanvas.Core;
using Controls = System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private OverlayContextMenu _toolbarMenu = null!;
    private Controls.MenuItem _toolbarExpansionMenuItem = null!;
    private readonly List<Controls.MenuItem> _cropMenuItems = [];

    private OverlayContextMenu CreateLauncherMenu()
    {
        var menu = new OverlayContextMenu();
        _toolbarExpansionMenuItem = new Controls.MenuItem { InputGestureText = "Tab" };
        _toolbarExpansionMenuItem.Click += (_, _) => RunUiAction(ToggleToolbarExpanded);
        RefreshToolbarExpansionMenuItem();
        menu.Items.Add(_toolbarExpansionMenuItem);
        var pass = new Controls.MenuItem { Header = L.Get("ToolbarV2.PassThrough"), InputGestureText = FormatHotkey(_settings.Toggle), Icon = CommandGlyphs.Create(CommandGlyphs.PassThrough) };
        pass.Click += (_, _) => RunUiAction(() => SetEditing(false)); menu.Items.Add(pass);
        menu.Opened+=(_,_)=>pass.InputGestureText=FormatHotkey(_settings.Toggle);
        menu.Items.Add(new Controls.Separator());
        menu.Opened += (_, _) => { RefreshToolbarExpansionMenuItem(); RefreshMenuBindings(menu); };
        Controls.MenuItem Group(string key, string geometry)
        {
            var group = new Controls.MenuItem { Header = L.Get(key), Icon = CommandGlyphs.Create(geometry) }; menu.Items.Add(group); return group;
        }
        void Item(Controls.MenuItem group, string label, string geometry, Action action, string shortcut = "", string? command = null)
        {
            var registered=EditorCommandCatalog.All.FirstOrDefault(c=>c.Id==shortcut);
            string Binding()=>shortcut=="Global.Hide"?FormatHotkey(_settings.Hide):registered is null?shortcut:EditorCommandCatalog.Hint(shortcut);
            var item = new Controls.MenuItem { Header = label, InputGestureText = Binding(), Icon = CommandGlyphs.Create(geometry) };
            item.Click += (_, _) => RunUiAction(()=> { if(registered is not null) ExecuteEditorCommand(registered); else action(); }); group.Items.Add(item);
            menu.Opened += (_,_) => item.InputGestureText=Binding();
            if (command is not null)
            {
                Controls.ToolTipService.SetShowOnDisabled(item, true);
                menu.Opened += (_, _) => { item.IsEnabled = CanExecuteUiCommand(command); item.ToolTip = UiCommandDisabledHint(command); };
            }
        }
        var file = Group("ToolbarV2.File", CommandGlyphs.File);
        Item(file, L.Get("Feedback4.Toolbar.NewBlankCanvas"), CommandGlyphs.NewCanvas, () => _ = StartNewCanvasAsync(), "File.New");
        Item(file, L.Get("ToolbarV2.Open"), CommandGlyphs.Open, () => _ = RunCommand(OpenProjectAsync), "File.Open");
        Item(file, L.Get("ToolbarV2.Save"), CommandGlyphs.Save, () => _ = RunCommand(() => SaveProjectAsync()), "File.Save");
        Item(file, L.Get("ToolbarV2.SaveAs"), CommandGlyphs.SaveAs, () => _ = RunCommand(() => SaveProjectAsync(true)), "File.SaveAs");
        Item(file, L.Get("Menu.ExportImage"), CommandGlyphs.Export, () => _ = RunCommand(() => ExportAsync()));
        Item(file, L.Get("Menu.ExportTransparent"), CommandGlyphs.ExportTransparent, () => _ = RunCommand(() => ExportAsync(true)));
        Item(file, L.Get("Desktop.RecoveryHistory"), CommandGlyphs.History, () => _ = RunCommand(RestoreWorkspaceHistoryAsync));
        var edit = Group("ToolbarV2.Edit", CommandGlyphs.EditObjects);
        Item(edit, L.Get("ToolbarV2.Undo"), CommandGlyphs.Undo, Undo, "Edit.Undo", "undo");
        Item(edit, L.Get("ToolbarV2.Redo"), CommandGlyphs.Redo, Redo, "Edit.Redo", "redo");
        edit.Items.Add(new Controls.Separator());
        Item(edit, L.Get("ToolbarV2.Copy"), CommandGlyphs.Copy, CopySelection, "Edit.Copy", "copy");
        Item(edit, L.Get("ToolbarV2.Paste"), CommandGlyphs.Paste, () => _ = RunCommand(PasteAsync), "Edit.Paste");
        Item(edit, L.Get("ToolbarV2.Duplicate"), CommandGlyphs.Duplicate, () => { _editor.Cancel(); Active.DuplicateSelection(); }, "Edit.Duplicate", "duplicate");
        Item(edit, L.Get("Layers.Group"), CommandGlyphs.Group, () => { _editor.Cancel(); Active.GroupSelection(); }, "Edit.Group", "group");
        Item(edit, L.Get("Layers.Ungroup"), CommandGlyphs.Ungroup, () => { _editor.Cancel(); Active.UngroupSelection(); }, "Edit.Ungroup", "ungroup");
        Item(edit, L.Get("ToolbarV2.Delete"), CommandGlyphs.Delete, () => { _editor.Cancel(); Active.DeleteSelection(); }, "Edit.Delete", "delete");
        var drawing = Group("ToolbarV2.DrawingTools", CommandGlyphs.DrawingTools);
        foreach (var tool in new[] { EditorTool.Select, EditorTool.Eraser, EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Rectangle,
            EditorTool.Ellipse, EditorTool.Text, EditorTool.Number, EditorTool.Symbol, EditorTool.Emoji }) drawing.Items.Add(ToolMenuItem(tool));
        var images = Group("ToolbarV2.ImageTools", CommandGlyphs.ImageTools);
        Item(images, L.Get("Toolbar.Image"), CommandGlyphs.Image, () => _ = RunCommand(() => ImportImagesAsync()), "Image.Import");
        Item(images, L.Get("Menu.ReplaceImage"), CommandGlyphs.ReplaceImage, () => _ = RunCommand(() => ImportImagesAsync(null, true)), command: "replace-image");
        images.Items.Add(new Controls.Separator());
        foreach (var tool in new[] { EditorTool.Crop, EditorTool.Blur, EditorTool.Pixelate }) images.Items.Add(ToolMenuItem(tool));
        menu.Items.Add(CreateWindowSourcesMenu());
        var presentation = Group("Presentation.Menu", CommandGlyphs.PresentationTools);
        presentation.Items.Add(ToolMenuItem(EditorTool.Presentation));
        presentation.Items.Add(ToolMenuItem(EditorTool.PointerPresentation));
        var pointerToggle = new Controls.MenuItem { Header = L.Get("Presentation.Global.PointerEnabled"),
            Icon = CommandGlyphs.Create(CommandGlyphs.PresentationPointer), IsCheckable = true };
        pointerToggle.Click += (_, _) => RunUiAction(TogglePresentationPointer); presentation.Items.Add(pointerToggle);
        menu.Opened += (_, _) => pointerToggle.IsChecked = _settings.PresentationGlobal.Pointer.Enabled;
        var canvas = Group("ToolbarV2.Canvas", CommandGlyphs.CanvasTools);
        canvas.Items.Add(ToolMenuItem(EditorTool.CaptureRegion));
        canvas.Items.Add(ToolMenuItem(EditorTool.Canvas));
        var billboard=new Controls.MenuItem {Header=L.Get("Billboard.Toggle"),Icon=CommandGlyphs.Create(CommandGlyphs.Placement(_editor.BillboardMode)),IsCheckable=true,Tag="billboard"};
        billboard.Click+=(_,_)=>RunUiAction(ToggleBillboardMode);canvas.Items.Add(billboard);
        var fixedDepth = new Controls.MenuItem { Header = L.Get("Billboard.FixedDepthWorkflow"),
            Icon = CommandGlyphs.Create(CommandGlyphs.Billboard), IsCheckable = true };
        fixedDepth.Click += (_, _) => RunUiAction(() =>
        {
            FinishInlineText();
            _editor.BillboardWorkflow = _editor.BillboardWorkflow == BillboardCreationWorkflow.FixedDepthLine
                ? BillboardCreationWorkflow.ScreenFirst : BillboardCreationWorkflow.FixedDepthLine;
            _settings.BillboardWorkflow = _editor.BillboardWorkflow;
            _settings.Save(); RefreshBillboardState(); RequestRender();
        });
        canvas.Items.Add(fixedDepth);
        menu.Opened+=(_,_)=>
        {
            RefreshBillboardState();billboard.IsEnabled=Active.ActiveRootCanvas==RootCanvasKind.Main&&Active.Document.Canvas.Projection.Enabled&&!DocumentOperationBusy;
            billboard.IsChecked=_editor.BillboardMode;billboard.Icon=CommandGlyphs.Create(CommandGlyphs.Placement(_editor.BillboardMode));
            fixedDepth.IsChecked = _editor.BillboardWorkflow == BillboardCreationWorkflow.FixedDepthLine;
            fixedDepth.IsEnabled = !DocumentOperationBusy;
        };
        Item(canvas, L.Get("Desktop.ToggleVisibility"), CommandGlyphs.Visibility, ToggleCanvasVisibility, "Global.Hide");
        Item(canvas, L.Get("Menu.MoveIntoView"), CommandGlyphs.MoveIntoView, MoveIntoView);
        Item(canvas, L.Get("History.ClearCurrent"), CommandGlyphs.ClearCanvas, () => _ = RunCommand(ClearCurrentCanvasAsync), "Edit.Clear");
        var windows = Group("ToolbarV2.Windows", CommandGlyphs.Windows);
        Item(windows, L.Get("ToolbarV2.Properties"), CommandGlyphs.Properties, ToggleInspector, "Selection.Properties"); Item(windows, L.Get("ToolbarV2.Layers"), CommandGlyphs.Layers, ToggleLayers, "Panel.Layers");
        Item(windows, L.Get("ToolOptions.Title"), CommandGlyphs.ToolOptions, OpenCurrentToolOptions, "Panel.Tools");
        Item(windows, L.Get("Stamp.LibraryTitle"), CommandGlyphs.Stamp, ShowStampLibrary, "Stamp.Library");
        Item(edit, L.Get("Feedback3.Toolbar.NewStamp"), CommandGlyphs.NewStamp, SaveStampSelection, "Stamp.Create", "save-stamp");
        menu.Items.Add(new Controls.Separator());
        var floatingLogo = new Controls.MenuItem();
        floatingLogo.Click+=(_,_)=>RunUiAction(ToggleFloatingLogo); menu.Items.Add(floatingLogo);
        void RefreshLogo()
        {
            floatingLogo.Header = L.Get(_settings.ShowPassThroughLauncher ? "Menu.HideFloatingLogo" : "Menu.ShowFloatingLogo");
            floatingLogo.Icon = CommandGlyphs.Create(_settings.ShowPassThroughLauncher ? CommandGlyphs.HideFloatingLogo : CommandGlyphs.FloatingLogo);
        }
        RefreshLogo(); menu.Opened += (_, _) => RefreshLogo();
        var settings = new Controls.MenuItem { Header = L.Get("Menu.Settings"), InputGestureText = EditorCommandCatalog.Hint("Panel.Settings"), Icon = CommandGlyphs.Create(CommandGlyphs.Settings) };
        settings.Click += (_, _) => RunUiAction(ShowHotkeySettings); menu.Items.Add(settings);
        menu.Opened+=(_,_)=>settings.InputGestureText=EditorCommandCatalog.Hint("Panel.Settings");
        var help = Group("Audit.Help", CommandGlyphs.Help);
        Item(help, L.Get("Welcome.Open"), CommandGlyphs.Welcome, () => OpenWelcome(true));
        Item(help, L.Get("Audit.UserGuide"), CommandGlyphs.Guide, () => ShowHelp(false));
        Item(help, L.Get("Settings.Shortcuts"), CommandGlyphs.Shortcuts, () => ShowHelp(true));
        help.Items.Add(new Controls.Separator());
        Item(help,L.Get("Menu.OpenInstallDirectory"),CommandGlyphs.InstallDirectory,()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppContext.BaseDirectory){UseShellExecute=true}));
        Item(help,L.Get("Menu.OpenUserDataDirectory"),CommandGlyphs.UserDataDirectory,()=>{System.IO.Directory.CreateDirectory(ApplicationPaths.ProfileDirectory);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ApplicationPaths.ProfileDirectory){UseShellExecute=true});});
        help.Items.Add(new Controls.Separator());
        Item(help, L.Get("Audit.ReportIssue"), CommandGlyphs.ReportIssue, ShowReportIssue);
        var updates = new Controls.MenuItem { Header = L.Get("Updates.Title"), Icon = CommandGlyphs.Create(CommandGlyphs.Updates) };
        updates.Click += (_, _) => RunUiAction(ShowUpdates); menu.Items.Add(updates);
        menu.Items.Add(new Controls.Separator());
        var exit = new Controls.MenuItem { Header = L.Get("Desktop.Exit"), Icon = CommandGlyphs.Create(CommandGlyphs.Exit) };
        exit.Click += (_, _) => _ = ExitAfterSnapshotAsync("LauncherMenu"); menu.Items.Add(exit);
        return menu;
    }

    private void RefreshToolbarExpansionMenuItem()
    {
        _toolbarExpansionMenuItem.Header = L.Get(_settings.ToolbarExpanded ? "Toolbar.Collapse" : "Toolbar.Expand");
        _toolbarExpansionMenuItem.Icon = CommandGlyphs.Create(_settings.ToolbarExpanded ? CommandGlyphs.CollapseToolbar : CommandGlyphs.ExpandToolbar);
    }

    private static void RefreshMenuBindings(Controls.ItemsControl root)
    {
        foreach(var item in root.Items.OfType<Controls.MenuItem>())
        {
            if(item.Tag is EditorTool tool) item.InputGestureText=ToolShortcutCatalog.Hint(tool);
            RefreshMenuBindings(item);
        }
    }

    private Controls.MenuItem ToolMenuItem(EditorTool tool)
    {
        string shortcut = ToolShortcutCatalog.Hint(tool);
        var item = new Controls.MenuItem { Header = ToolName(tool), Tag = tool, InputGestureText = shortcut, Icon = CommandGlyphs.Create(CommandGlyphs.Tool(tool)) };
        item.ToolTip = L.Get("ToolOptions.Title") + " · " + EditorCommandCatalog.Hint("Panel.Tools");
        if (tool == EditorTool.Crop)
        {
            item.IsEnabled = _editor.CanCropSelection;
            item.ToolTip = L.Get("Editor.CropImageHint") + "\n" + item.ToolTip; Controls.ToolTipService.SetShowOnDisabled(item, true);
            _cropMenuItems.Add(item);
        }
        item.Click += (_, _) => RunUiAction(() => ActivateTool(tool)); return item;
    }
}
