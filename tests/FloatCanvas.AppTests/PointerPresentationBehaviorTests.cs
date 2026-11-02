using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class PointerPresentationBehaviorTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static void Run(Action<string, bool> check)
    {
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 80, Transform = Affine2D.Translation(60, 60) };
        var session = new EditorSession(new() { Elements = [element], Viewport = new(0, 0, 400, 300) }); session.SetSelection([element.Id]);
        var editor = new EditorController(session) { BackgroundPickingEnabled = false };
        editor.ApplyParameters(new() { Tool = EditorTool.PointerPresentation });
        var desktop = Desktop(editor); string before = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        Send(OverlayPointerKind.Down, OverlayMouseButton.Middle, new(110, 100));
        Send(OverlayPointerKind.Move, OverlayMouseButton.Middle, new(140, 125));
        Send(OverlayPointerKind.Up, OverlayMouseButton.Middle, new(140, 125));
        check("actual canvas entry keeps mouse-presentation middle drag passive and preserves author history",
            JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo && !session.IsTransactionActive && !editor.IsTemporaryMove);

        int properties = 0, text = 0, capture = 0, effects = 0;
        editor.PropertiesRequested += _ => properties++; editor.EditText += (_, _) => text++;
        editor.CaptureSelectionCompleted += _ => capture++; editor.DesktopEffectRequested += (_, _, _, _) => effects++;
        foreach (var modifiers in Enumerable.Range(0, 8).Select(value => (OverlayModifiers)value))
        foreach (var button in new[] { OverlayMouseButton.Left, OverlayMouseButton.Right, OverlayMouseButton.Middle })
        {
            foreach (var kind in new[] { OverlayPointerKind.Down, OverlayPointerKind.DoubleClick, OverlayPointerKind.Move, OverlayPointerKind.Up })
                Call(desktop, "HandleCanvasPointer", new OverlayPointerEvent(kind, new(130, 120), button, modifiers));
            Call(desktop, "HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Wheel, new(130, 120), button, modifiers, 120));
        }
        Call(desktop, "HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Wheel, new(130, 120), OverlayMouseButton.None, OverlayModifiers.Control, -120));
        Call(desktop, "HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Cancel, new(130, 120), OverlayMouseButton.None, OverlayModifiers.None));
        check("canvas left right middle double-click drag and wheel only preview under every ordinary modifier",
            JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo && session.Selection.SetEquals([element.Id]) &&
            !session.IsTransactionActive && !editor.IsTemporaryMove && editor.Tool == EditorTool.PointerPresentation && properties + text + capture + effects == 0);
        check("mouse presentation rejects direct temporary-selection entry as well as the desktop route",
            editor.HandleTemporaryMovePointer(new(OverlayPointerKind.Down, new(110, 100), OverlayMouseButton.Middle, OverlayModifiers.None)) && !editor.IsTemporaryMove && !session.IsTransactionActive);
        session.BeginTransaction("foreign operation"); long foreign = session.TransactionId;
        Send(OverlayPointerKind.Down, OverlayMouseButton.Left, new(110, 100));
        Send(OverlayPointerKind.Cancel, OverlayMouseButton.None, new(110, 100));
        check("passive pointer cancellation cannot cancel another operation's transaction", session.IsTransactionActive && session.TransactionId == foreign && JsonSerializer.Serialize(session.Document) == before);
        session.Cancel();

        editor.Tool = EditorTool.Select;
        editor.HandleTemporaryMovePointer(new(OverlayPointerKind.Down, new(110, 100), OverlayMouseButton.Middle, OverlayModifiers.None));
        editor.HandleTemporaryMovePointer(new(OverlayPointerKind.Move, new(140, 125), OverlayMouseButton.Middle, OverlayModifiers.None));
        check("ordinary tools still enter the existing middle selection transaction", editor.IsTemporaryMove && session.IsTransactionActive);
        editor.Tool = EditorTool.PointerPresentation;
        check("switching into mouse presentation cancels an existing middle drag without clearing selection",
            JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo && !session.IsTransactionActive && !editor.IsTemporaryMove && session.Selection.SetEquals([element.Id]));
        Send(OverlayPointerKind.Up, OverlayMouseButton.Middle, new(140, 125));
        check("late release from the old middle gesture cannot commit into the mouse tool", JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo);
        // Only the pending recipient identity is needed by Move. Avoid constructing
        // application resources or sampling a real desktop in this managed route check.
        Set(desktop, "_eyedropper", RuntimeHelpers.GetUninitializedObject(typeof(ColorField))); Set(desktop, "_disposed", true);
        Send(OverlayPointerKind.Move, OverlayMouseButton.None, new(270, 250));
        check("explicit property eyedropper still receives canvas preview input while the mouse tool is passive",
            (OverlayPoint)typeof(DesktopSession).GetField("_eyedropperPoint", Hidden)!.GetValue(desktop)! == new OverlayPoint(270, 250) && JsonSerializer.Serialize(session.Document) == before);
        Set(desktop, "_documentBusyValue", true); Send(OverlayPointerKind.Move, OverlayMouseButton.None, new(280, 260));
        check("property eyedropper retains the ordinary document-operation busy gate", (OverlayPoint)typeof(DesktopSession).GetField("_eyedropperPoint", Hidden)!.GetValue(desktop)! == new OverlayPoint(270, 250));
        Set(desktop, "_documentBusyValue", false);
        Set(desktop, "_eyedropper", null); Set(desktop, "_disposed", false);
        Visuals(check, desktop, editor);
        Toolbar(check, desktop, editor);
        editor.Tool = EditorTool.Freehand;
        editor.HandleTemporaryMovePointer(new(OverlayPointerKind.Down, new(110, 100), OverlayMouseButton.Middle, OverlayModifiers.None));
        check("leaving mouse presentation restores ordinary middle selection rules", editor.IsTemporaryMove && session.IsTransactionActive);
        editor.Cancel();

        void Send(OverlayPointerKind kind, OverlayMouseButton button, OverlayPoint point) =>
            Call(desktop, "HandleCanvasPointer", new OverlayPointerEvent(kind, point, button, OverlayModifiers.None));
    }

    private static DesktopSession Desktop(EditorController editor)
    {
        // Managed-only fixture: no constructor, native HWND, input hooks or foreground.
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
        Set(host, "<IsVisible>k__BackingField", true);
        Set(host, "_monitors", (IReadOnlyList<MonitorInfo>)[new("synthetic", new(0, 0, 400, 300), new(0, 0, 400, 300), 96, 96, true)]);
        Set(desktop, "_editor", editor); Set(desktop, "_host", host);
        Set(desktop, "_canvasCalibration", new CanvasCalibrationController());
        Set(desktop, "_canvasGridCalibrationPoints", new List<PointD>());
        Set(desktop, "_canvasPlacementWindows", Array.Empty<System.Windows.Window>());
        Set(desktop, "_updatingHiddenCanvas", true);
        return desktop;
    }
    private static void Visuals(Action<string, bool> check, DesktopSession desktop, EditorController editor)
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        using var paint = new SKPaint { Color = SKColors.Blue, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        editor.Tool = EditorTool.Select; surface.Canvas.Clear(SKColors.Transparent);
        Call(desktop, "DrawSelectionFrame", surface.Canvas, paint, OverlayModifiers.None);
        using var selection = SKBitmap.FromImage(surface.Snapshot());
        check("the actual selection-frame painter produces handles for an ordinary selected tool", selection.Pixels.Any(pixel => pixel.Alpha > 0));
        editor.Tool = EditorTool.PointerPresentation; surface.Canvas.Clear(SKColors.Transparent);
        Call(desktop, "DrawSelectionFrame", surface.Canvas, paint, OverlayModifiers.None);
        using var passive = SKBitmap.FromImage(surface.Snapshot());
        check("mouse presentation hides actual selection-frame pixels while retaining selection IDs", passive.Pixels.All(pixel => pixel.Alpha == 0) && editor.Session.Selection.Count == 1 && !editor.ShouldShowSelection(OverlayModifiers.None));
        check("mouse presentation publishes no author-adorners damage regions", !(bool)Call(desktop, "CanvasControlsVisible")! && ((IReadOnlyList<PixelBounds>)Call(desktop, "CanvasControlRegions")!).Count == 0);
        Set(desktop, "_pointerPresentationSettings", new PresentationPointerSettings { Enabled = true, CursorEnabled = true, CursorColor = 0xFFFF0000, CursorRadiusPixels = 12, ClicksEnabled = true });
        Set(desktop, "_pointerPresentationFrame", new PresentationFrame(1, 1, TimeSpan.FromSeconds(1), cursorScreen: new(300, 210), clicks: [new(new(300, 210), PresentationMouseButton.Left, TimeSpan.FromSeconds(1))]));
        Call(desktop, "DrawPresentationPointer", surface.Canvas);
        using var pointer = SKBitmap.FromImage(surface.Snapshot());
        check("passive canvas interaction still draws the existing global cursor and click overlay", pointer.GetPixel(300, 210).Alpha > 0 && pointer.Pixels.Count(pixel => pixel.Alpha > 0) > 100);
    }
    private static void Toolbar(Action<string, bool> check, DesktopSession desktop, EditorController editor)
    {
        var buttons = new Dictionary<EditorTool, Button> { [EditorTool.Select] = new(), [EditorTool.Freehand] = new(), [EditorTool.Blur] = new(), [EditorTool.Canvas] = new() };
        Set(desktop, "_toolbarToolButtons", buttons); Set(desktop, "_toolbarButtonMemory", new Dictionary<EditorTool, EditorTool>());
        Set(desktop, "_toolbarUpperRow", System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ToolbarUpperRow)));
        Set(desktop, "_extraToolButtons", new Dictionary<EditorTool, Button>());
        editor.Tool = EditorTool.PointerPresentation; Call(desktop, "RefreshToolbarToolSelection");
        check("the real toolbar buttons select only More when mouse presentation is active", buttons.Where(pair => AutomationProperties.GetItemStatus(pair.Value).Length > 0).Select(pair => pair.Key).SequenceEqual([EditorTool.Blur]));
        editor.Tool = EditorTool.Select; Call(desktop, "RefreshToolbarToolSelection");
        check("switching back selects only the ordinary tool and clears mouse-tool toolbar selection", buttons.Where(pair => AutomationProperties.GetItemStatus(pair.Value).Length > 0).Select(pair => pair.Key).SequenceEqual([EditorTool.Select]));
    }
    private static object? Call(object owner, string method, params object[] values)
    {
        try { return owner.GetType().GetMethod(method, Hidden)!.Invoke(owner, values); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Set(object owner, string field, object? value) => owner.GetType().GetField(field, Hidden)!.SetValue(owner, value);
}
