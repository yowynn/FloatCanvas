using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class Preview4CanvasTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static void Run(Action<string, bool> check)
    {
        var viewport = new RectD(0, 0, 1920, 1080);
        var initial = CanvasCalibration.CreateInitial(viewport);
        var custom = initial.Clone();
        check("fixture calibration is valid", CanvasCalibration.TryCalibrate(custom,
            [new(300,200), new(1500,250), new(1400,850), new(400,800)], viewport, out custom, out _));
        var library = CanvasPresetLibrary.OfficialOnly();
        foreach (var preset in library.Official)
        {
            var result = library.Apply(preset.Id, new() { Projection = custom }, viewport, viewport);
            check(preset.Id + " resets the disabled perspective plane to the default screen geometry", !result.Projection.Enabled &&
                result.Projection.Quad.SequenceEqual(initial.Quad) && result.Projection.Matrix == Projective2D.Identity);
        }
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new CanvasSettingsWindow(library);
        try
        {
            var rows = Field<NumericValueField>(window, "rows");
            var columns = Field<NumericValueField>(window, "columns");
            check("rows and columns share a paired horizontal layout with rows on the left", rows.Parent is Grid rowSlot && columns.Parent is Grid columnSlot &&
                rowSlot.Parent is StackPanel rowPanel && columnSlot.Parent is StackPanel columnPanel &&
                rowPanel.Parent is Grid pair && ReferenceEquals(pair, columnPanel.Parent) && Grid.GetColumn(rowPanel) == 0 && Grid.GetColumn(columnPanel) == 1);
            check("automatic row and column counts occupy their corresponding paired slots", ReferenceEquals(rows.Parent, Field<TextBox>(window, "lockedRows").Parent) &&
                ReferenceEquals(columns.Parent, Field<TextBox>(window, "lockedColumns").Parent) && !ReferenceEquals(rows.Parent, columns.Parent));
            var actions = Field<FrameworkElement>(window, "perspectiveActions");
            window.Refresh(new(), false);
            check("disabled perspective hides adjust and reset actions", actions.Visibility == Visibility.Collapsed);
            window.Refresh(new() { Projection = custom }, false);
            check("enabled perspective shows adjust and reset actions", actions.Visibility == Visibility.Visible);
            MonitorInfo main = new("main", new(0,0,1920,1080), new(0,0,1920,1040),96,96,true);
            MonitorInfo pad = new("pad",new(1920,0,1280,720),new(1920,0,1280,680),120,120,false);
            window.SetReferenceMonitors([main,pad], "pad");
            var menu = Field<ContextMenu>(window,"resetMenu");
            check("reset targets the primary screen without a display selection split button", menu.Items.Count == 0 &&
                Field<Button>(window,"resetChoices").Visibility == Visibility.Collapsed && Equals(Field<Button>(window,"reset").Content, L.Get("Canvas.ResetOnDisplay")));
            string? requestedDevice = "unrequested";
            window.ResetDisplayRequested += device => requestedDevice = device;
            Field<Button>(window,"reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("a dormant secondary display preference cannot change the reset request", requestedDevice is null);
            window.SetReferenceMonitors([main], "pad");
            check("single display retains the same primary reset without a hidden menu", menu.Items.Count == 0 && Equals(Field<Button>(window,"reset").Content, L.Get("Canvas.ResetOnDisplay")));
            window.SetReferenceMonitors([main,pad], "missing");
            check("invalid old display choices leave the same primary reset", menu.Items.Count == 0 && Equals(Field<Button>(window,"reset").Content, L.Get("Canvas.ResetOnDisplay")));
            var resetSettings = new CanvasSettings { Projection = custom.Clone(), Grid = new() { FlatCellSize = 73.1, FlatOrigin = viewport.Center } };
            var reset = DesktopSession.CreateResetCanvasProjection(resetSettings, viewport);
            check("primary reset preserves the exact flat spacing and count pair at the primary center", reset.Enabled &&
                reset.Columns == custom.Columns && reset.Rows == custom.Rows && reset.CellSize == 73.1 &&
                reset.Matrix == Projective2D.Identity && CanvasGridGeometry.ReferenceCenter(reset).DistanceTo(viewport.Center) < 1e-10);
        }
        finally { window.CloseForDispose(); }
        CornerModifiers(check, viewport);
    }
    private static void CornerModifiers(Action<string,bool> check, RectD viewport)
    {
        var settings = CanvasCalibration.CreateInitial(viewport); settings.Enabled = true;
        var session = new EditorSession(new SceneDocument { Canvas = new() {Projection = settings} });
        var controller = new CanvasCalibrationController(); controller.Begin(session);
        var corner = settings.Quad[0];
        var move = typeof(CanvasCalibrationController).GetMethod("Move", [typeof(PointD),typeof(OverlayModifiers)]);
        check("corner dragging accepts modifier state from the actual pointer path", move is not null);
        void Move(PointD p, OverlayModifiers modifiers = OverlayModifiers.None) => move!.Invoke(controller,[p,modifiers]);
        controller.Down(corner); Move(corner + new PointD(5,50));
        check("corner X snaps to its adjacent vertical edge", Math.Abs(session.Document.Canvas.Projection.Quad[0].X - corner.X) < 1e-8);
        Move(corner + new PointD(5,50),OverlayModifiers.Control);
        check("Ctrl releases corner alignment snapping", session.Document.Canvas.Projection.Quad[0].DistanceTo(corner + new PointD(5,50)) < 1e-7);
        Move(corner + new PointD(45,70),OverlayModifiers.Shift | OverlayModifiers.Control);
        check("Shift constrains movement to the dominant axis from drag origin", session.Document.Canvas.Projection.Quad[0].DistanceTo(corner + new PointD(0,70)) < 1e-7);
        Move(corner + new PointD(90,20),OverlayModifiers.Shift | OverlayModifiers.Control);
        check("Shift can choose the horizontal drag-origin axis", session.Document.Canvas.Projection.Quad[0].DistanceTo(corner + new PointD(90,0)) < 1e-7);
        controller.Up();
        check("snapping and modifier changes remain one undoable calibration gesture", session.UndoCount == 1 && session.Undo() && session.Document.Canvas.Projection.Quad.SequenceEqual(settings.Quad));
    }
    private static T Field<T>(object owner,string name) => (T)owner.GetType().GetField(name,Private)!.GetValue(owner)!;
}
