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
        menu.Items.Add(new Controls.Separator());
        menu.Opened += (_, _) => RefreshToolbarExpansionMenuItem();
        Controls.MenuItem Group(string key, string geometry)
        {
            var group = new Controls.MenuItem { Header = L.Get(key), Icon = CommandGlyphs.Create(geometry) }; menu.Items.Add(group); return group;
        }
        void Item(Controls.MenuItem group, string label, string geometry, Action action, string shortcut = "", string? command = null)
        {
            var item = new Controls.MenuItem { Header = label, InputGestureText = shortcut, Icon = CommandGlyphs.Create(geometry) };
            item.Click += (_, _) => RunUiAction(action); group.Items.Add(item);
            if (command is not null)
            {
                Controls.ToolTipService.SetShowOnDisabled(item, true);
                menu.Opened += (_, _) => { item.IsEnabled = CanExecuteUiCommand(command); item.ToolTip = UiCommandDisabledHint(command); };
            }
        }
        var file = Group("ToolbarV2.File", CommandGlyphs.File);
        Item(file, L.Get("Feedback4.Toolbar.NewBlankCanvas"), CommandGlyphs.NewCanvas, () => _ = _newCanvasButton!.ActivateAsync(), "Ctrl+N");
        Item(file, L.Get("ToolbarV2.Open"), CommandGlyphs.Open, () => _ = RunCommand(OpenProjectAsync), "Ctrl+O");
        Item(file, L.Get("ToolbarV2.Save"), CommandGlyphs.Save, () => _ = RunCommand(() => SaveProjectAsync()), "Ctrl+S");
        Item(file, L.Get("ToolbarV2.SaveAs"), CommandGlyphs.SaveAs, () => _ = RunCommand(() => SaveProjectAsync(true)), "Ctrl+Shift+S");
        Item(file, L.Get("Menu.ExportImage"), CommandGlyphs.Export, () => _ = RunCommand(() => ExportAsync()));
        Item(file, L.Get("Menu.ExportTransparent"), CommandGlyphs.ExportTransparent, () => _ = RunCommand(() => ExportAsync(true)));
        Item(file, L.Get("Desktop.RecoveryHistory"), CommandGlyphs.History, () => _ = RunCommand(RestoreWorkspaceHistoryAsync));
        var edit = Group("ToolbarV2.Edit", CommandGlyphs.EditObjects);
        Item(edit, L.Get("ToolbarV2.Undo"), CommandGlyphs.Undo, Undo, "Ctrl+Z", "undo");
        Item(edit, L.Get("ToolbarV2.Redo"), CommandGlyphs.Redo, Redo, "Ctrl+Y", "redo");
        edit.Items.Add(new Controls.Separator());
        Item(edit, L.Get("ToolbarV2.Copy"), CommandGlyphs.Copy, CopySelection, "Ctrl+C", "copy");
        Item(edit, L.Get("ToolbarV2.Paste"), CommandGlyphs.Paste, () => _ = RunCommand(PasteAsync), "Ctrl+V");
        Item(edit, L.Get("ToolbarV2.Duplicate"), CommandGlyphs.Duplicate, () => { _editor.Cancel(); Active.DuplicateSelection(); }, "Ctrl+D", "duplicate");
        Item(edit, L.Get("Layers.Group"), CommandGlyphs.Group, () => { _editor.Cancel(); Active.GroupSelection(); }, "Ctrl+G", "group");
        Item(edit, L.Get("Layers.Ungroup"), CommandGlyphs.Ungroup, () => { _editor.Cancel(); Active.UngroupSelection(); }, "Ctrl+Shift+G", "ungroup");
        Item(edit, L.Get("ToolbarV2.Delete"), CommandGlyphs.Delete, () => { _editor.Cancel(); Active.DeleteSelection(); }, "Delete", "delete");
        var drawing = Group("ToolbarV2.DrawingTools", CommandGlyphs.DrawingTools);
        foreach (var tool in new[] { EditorTool.Select, EditorTool.Eraser, EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Rectangle,
            EditorTool.Ellipse, EditorTool.Text, EditorTool.Number, EditorTool.Symbol, EditorTool.Emoji }) drawing.Items.Add(ToolMenuItem(tool));
        var images = Group("ToolbarV2.ImageTools", CommandGlyphs.ImageTools);
        Item(images, L.Get("Toolbar.Image"), CommandGlyphs.Image, () => _ = RunCommand(() => ImportImagesAsync()), "I");
        Item(images, L.Get("Menu.ReplaceImage"), CommandGlyphs.ReplaceImage, () => _ = RunCommand(() => ImportImagesAsync(null, true)), command: "replace-image");
        images.Items.Add(new Controls.Separator());
        foreach (var tool in new[] { EditorTool.Crop, EditorTool.Blur, EditorTool.Pixelate }) images.Items.Add(ToolMenuItem(tool));
        var canvas = Group("ToolbarV2.Canvas", CommandGlyphs.CanvasTools);
        canvas.Items.Add(ToolMenuItem(EditorTool.CaptureRegion));
        canvas.Items.Add(ToolMenuItem(EditorTool.Canvas));
        var billboard=new Controls.MenuItem {Header=L.Get("Billboard.Toggle"),Icon=CommandGlyphs.Create(CommandGlyphs.Placement(_editor.BillboardMode)),IsCheckable=true,Tag="billboard"};
        billboard.Click+=(_,_)=>RunUiAction(ToggleBillboardMode);canvas.Items.Add(billboard);
        menu.Opened+=(_,_)=>
        {
            RefreshBillboardState();billboard.IsEnabled=Active.Document.Canvas.Projection.Enabled&&!DocumentOperationBusy;
            billboard.IsChecked=_editor.BillboardMode;billboard.Icon=CommandGlyphs.Create(CommandGlyphs.Placement(_editor.BillboardMode));
        };
        Item(canvas, L.Get("Desktop.ToggleVisibility"), CommandGlyphs.Visibility, ToggleCanvasVisibility, FormatHotkey(_settings.Hide));
        Item(canvas, L.Get("Menu.MoveIntoView"), CommandGlyphs.MoveIntoView, MoveIntoView);
        Item(canvas, L.Get("History.ClearCurrent"), CommandGlyphs.ClearCanvas, () => _ = RunCommand(ClearCurrentCanvasAsync), "Ctrl+Backspace / Ctrl+Delete");
        var windows = Group("ToolbarV2.Windows", CommandGlyphs.Windows);
        Item(windows, L.Get("ToolbarV2.Properties"), CommandGlyphs.Properties, ToggleInspector, "Enter"); Item(windows, L.Get("ToolbarV2.Layers"), CommandGlyphs.Layers, ToggleLayers, "Ctrl+L");
        Item(windows, L.Get("ToolOptions.Title"), CommandGlyphs.ToolOptions, OpenCurrentToolOptions, "Ctrl+T");
        Item(windows, L.Get("Stamp.LibraryTitle"), CommandGlyphs.Stamp, ShowStampLibrary, "-");
        Item(edit, L.Get("Feedback3.Toolbar.NewStamp"), CommandGlyphs.NewStamp, SaveStampSelection, "+", "save-stamp");
        menu.Items.Add(new Controls.Separator());
        var welcome = new Controls.MenuItem {Header=L.Get("Welcome.Open"),Icon=CommandGlyphs.Create(CommandGlyphs.Welcome)};
        welcome.Click+=(_,_)=>RunUiAction(()=>OpenWelcome(true)); menu.Items.Add(welcome);
        var floatingLogo = new Controls.MenuItem {Header=L.Get("Settings.PassThroughLauncher"),Icon=CommandGlyphs.Create(CommandGlyphs.FloatingLogo),IsCheckable=true};
        floatingLogo.Click+=(_,_)=>RunUiAction(ToggleFloatingLogo); menu.Items.Add(floatingLogo);
        menu.Opened+=(_,_)=>floatingLogo.IsChecked=_settings.ShowPassThroughLauncher;
        var settings = new Controls.MenuItem { Header = L.Get("Menu.Settings"), InputGestureText = "Ctrl+,", Icon = CommandGlyphs.Create(CommandGlyphs.Settings) };
        settings.Click += (_, _) => RunUiAction(ShowHotkeySettings); menu.Items.Add(settings);
        var updates = new Controls.MenuItem { Header = L.Get("Updates.Title"), Icon = CommandGlyphs.Create(CommandGlyphs.Updates) };
        updates.Click += (_, _) => RunUiAction(ShowUpdates); menu.Items.Add(updates);
        var system = Group("Menu.SystemDiagnostics",CommandGlyphs.SystemDiagnostics);
        Item(system, L.Get("Engineering.ExportDiagnostics"), CommandGlyphs.DiagnosticExport, () => DiagnosticExportWindow.ShowFor(_toolbar, UsesEnhancedOverlay));
        var help = Group("Audit.Help", CommandGlyphs.Help);
        Item(help, L.Get("Audit.UserGuide"), CommandGlyphs.Guide, AppHelp.OpenGuide);
        Item(help, L.Get("Settings.Shortcuts"), CommandGlyphs.Shortcuts, AppHelp.OpenShortcuts);
        Item(help, L.Get("Audit.ReportIssue"), CommandGlyphs.ReportIssue, AppHelp.ReportIssue);
        Item(system,L.Get("Menu.OpenInstallDirectory"),CommandGlyphs.InstallDirectory,()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppContext.BaseDirectory){UseShellExecute=true}));
        Item(system,L.Get("Menu.OpenUserDataDirectory"),CommandGlyphs.UserDataDirectory,()=>{System.IO.Directory.CreateDirectory(ApplicationPaths.ProfileDirectory);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ApplicationPaths.ProfileDirectory){UseShellExecute=true});});
        var pass = new Controls.MenuItem { Header = L.Get("ToolbarV2.PassThrough"), InputGestureText = FormatHotkey(_settings.Toggle), Icon = CommandGlyphs.Create(CommandGlyphs.PassThrough) };
        pass.Click += (_, _) => RunUiAction(() => SetEditing(false)); menu.Items.Add(pass);
        var exit = new Controls.MenuItem { Header = L.Get("Desktop.Exit"), Icon = CommandGlyphs.Create(CommandGlyphs.Exit) };
        exit.Click += (_, _) => _ = ExitAfterSnapshotAsync(); menu.Items.Add(exit);
        return menu;
    }

    private void RefreshToolbarExpansionMenuItem()
    {
        _toolbarExpansionMenuItem.Header = L.Get(_settings.ToolbarExpanded ? "Toolbar.Collapse" : "Toolbar.Expand");
        _toolbarExpansionMenuItem.Icon = CommandGlyphs.Create(_settings.ToolbarExpanded ? CommandGlyphs.CollapseToolbar : CommandGlyphs.ExpandToolbar);
    }

    private Controls.MenuItem ToolMenuItem(EditorTool tool)
    {
        string shortcut = ToolShortcutCatalog.Hint(tool);
        var item = new Controls.MenuItem { Header = ToolName(tool), Tag = tool, InputGestureText = shortcut, Icon = CommandGlyphs.Create(CommandGlyphs.Tool(tool)) };
        if (tool != EditorTool.Select) item.ToolTip = L.Get("ToolOptions.Title") + " · Ctrl+T";
        if (tool == EditorTool.Crop)
        {
            item.IsEnabled = _editor.CanCropSelection;
            item.ToolTip = L.Get("Editor.CropImageHint") + "\n" + item.ToolTip; Controls.ToolTipService.SetShowOnDisabled(item, true);
            _cropMenuItems.Add(item);
        }
        item.Click += (_, _) => RunUiAction(() => ActivateTool(tool)); return item;
    }
}
