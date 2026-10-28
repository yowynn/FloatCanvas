using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Phase3CanvasEscapeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host)
    {
        var editor = Field<EditorController>(session, "_editor");
        var calibration = Field<CanvasCalibrationController>(session, "_canvasCalibration");
        var scale = Field<CanvasGridScaleController>(session, "_canvasGridScale");
        var editing = typeof(OverlayHost).GetField("<IsEditing>k__BackingField", Private)!;
        foreach (bool perspective in new[] { false, true })
        {
            editing.SetValue(host, true);
            editor.Tool = EditorTool.Canvas;
            editor.Session.Document.Canvas.Projection.Enabled = perspective;
            Invoke("SynchronizeCanvasTool");
            string before = JsonSerializer.Serialize(editor.Session.Document);
            Invoke("OnKey", 0x1B);
            check("idle Canvas Escape leaves editing directly for perspective=" + perspective, !host.IsEditing);
            check("Canvas Escape retains its selected tool instead of introducing Select", editor.Tool == EditorTool.Canvas);
            check("Canvas Escape leaves document settings unchanged", before == JsonSerializer.Serialize(editor.Session.Document));
        }
        editing.SetValue(host, true);
        editor.Session.Document.Canvas.Projection.Enabled = false;
        Invoke("SynchronizeCanvasTool");
        string original = JsonSerializer.Serialize(editor.Session.Document);
        var viewport = editor.Session.Document.Viewport;
        var point = viewport.Center + new PointD(100, 0);
        check("flat Canvas drag starts before Escape", scale.Down(editor.Session, point, viewport));
        scale.Move(point + new PointD(50, 0));
        Invoke("OnKey", 0x1B);
        check("Escape first cancels an unfinished grid drag while retaining Canvas editing", host.IsEditing && editor.Tool == EditorTool.Canvas &&
            !scale.IsDragging && !editor.Session.IsTransactionActive && original == JsonSerializer.Serialize(editor.Session.Document));
        calibration.BeginPlacement(editor.Session);
        Invoke("OnKey", 0x1B);
        check("Escape cancels unfinished four-point placement without changing tools", host.IsEditing && !calibration.IsPlacing && editor.Tool == EditorTool.Canvas);
        Invoke("OnKey", 0x1B);
        check("the next idle Escape leaves Canvas editing with no extra tool level", !host.IsEditing && editor.Tool == EditorTool.Canvas);

        object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, args);
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
}
