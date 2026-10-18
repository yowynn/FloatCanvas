using System.Windows;
using System.Windows.Media;
using FloatCanvas.Core;
using Controls = System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private Controls.ContextMenu _toolbarMenu = null!;
    private readonly List<Controls.MenuItem> _cropMenuItems = [];

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
        foreach (var tool in new[] { EditorTool.Select, EditorTool.Eraser, EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Rectangle,
            EditorTool.Ellipse, EditorTool.Text, EditorTool.Number, EditorTool.Symbol, EditorTool.Emoji }) drawing.Items.Add(ToolMenuItem(tool));
        var images = Group("ToolbarV2.ImageTools", "M2,2 L14,2 L14,14 L2,14 Z M2,12 L6,8 L10,12 L14,9");
        Item(images, L.Get("Toolbar.Image"), () => _ = RunCommand(() => ImportImagesAsync()));
        Item(images, L.Get("Menu.ReplaceImage"), () => _ = RunCommand(() => ImportImagesAsync(null, true)));
        images.Items.Add(new Controls.Separator());
        foreach (var tool in new[] { EditorTool.Crop, EditorTool.Blur, EditorTool.Pixelate }) images.Items.Add(ToolMenuItem(tool));
        var canvas = Group("ToolbarV2.Canvas", "M1,3 L15,3 L15,13 L1,13 Z M5,16 L11,16");
        canvas.Items.Add(ToolMenuItem(EditorTool.CaptureRegion));
        Item(canvas, L.Get("Canvas.Title"), ToggleCanvasSettings);
        Item(canvas, L.Get("Desktop.ToggleVisibility"), ToggleCanvasVisibility, FormatHotkey(_settings.Hide));
        Item(canvas, L.Get("Menu.MoveIntoView"), MoveIntoView);
        Item(canvas, L.Get("Feedback4.Toolbar.ClearCanvas"), ClearCanvas);
        var windows = Group("ToolbarV2.Windows", "M2,1 L14,1 L14,15 L2,15 Z M2,5 L14,5");
        Item(windows, L.Get("ToolbarV2.Properties"), ToggleInspector); Item(windows, L.Get("ToolbarV2.Layers"), ToggleLayers);
        Item(windows, L.Get("Stamp.LibraryTitle"), ShowStampLibrary, "-");
        Item(edit, L.Get("Feedback3.Toolbar.NewStamp"), SaveStampSelection, "+");
        menu.Items.Add(new Controls.Separator());
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

    private Controls.MenuItem ToolMenuItem(EditorTool tool)
    {
        string shortcut = ToolShortcutCatalog.Hint(tool);
        var item = new Controls.MenuItem { Header = ToolName(tool), Tag = tool, InputGestureText = shortcut, Icon = MenuGeometry("M2,2 L14,2 L14,14 L2,14 Z") };
        if (tool == EditorTool.Crop)
        {
            item.IsEnabled = _editor.CanCropSelection;
            item.ToolTip = L.Get("Editor.CropImageHint"); Controls.ToolTipService.SetShowOnDisabled(item, true);
            _cropMenuItems.Add(item);
        }
        item.Click += (_, _) => RunUiAction(() => ActivateTool(tool)); return item;
    }

    private static System.Windows.Shapes.Path MenuGeometry(string geometry)
    {
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 15, Height = 15, Stretch = Stretch.Uniform,
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText"); return path;
    }
}
