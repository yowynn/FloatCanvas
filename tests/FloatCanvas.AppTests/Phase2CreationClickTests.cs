using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Phase2CreationClickTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host)
    {
        var editor = (EditorController)typeof(DesktopSession).GetField("_editor", Private)!.GetValue(session)!;
        int requested = 0;
        editor.PropertiesRequested += _ => requested++;
        host.SetVisible(true);
        var bounds = host.Monitors.Single().Bounds;
        var point = new OverlayPoint(bounds.Left + 180, bounds.Top + 140);
        editor.Tool = EditorTool.Number;
        Send(OverlayPointerKind.Down, 1000); Send(OverlayPointerKind.Up, 1010);
        Send(OverlayPointerKind.DoubleClick, 1100); Send(OverlayPointerKind.Up, 1110);
        check("a creation click cannot seed a properties double click", editor.Session.Document.Elements.Count == 1 && requested == 0);
        editor.Session.Execute("clear click fixture", document => document.Elements.Clear()); editor.Session.SetSelection([]);
        Send(OverlayPointerKind.Cancel, 2000);
        Send(OverlayPointerKind.Down, 2100, OverlayModifiers.Control); Send(OverlayPointerKind.Up, 2110, OverlayModifiers.Control);
        Send(OverlayPointerKind.DoubleClick, 2200, OverlayModifiers.Control); Send(OverlayPointerKind.Up, 2210, OverlayModifiers.Control);
        check("Ctrl double click creates two numbered layers without opening properties", editor.Session.Document.Elements.Count == 2 && requested == 0);
        Send(OverlayPointerKind.Cancel, 3000);
        editor.Tool = EditorTool.Select; editor.Session.SelectAll();
        Send(OverlayPointerKind.Down, 3100); Send(OverlayPointerKind.Up, 3110);
        Send(OverlayPointerKind.DoubleClick, 3200); Send(OverlayPointerKind.Up, 3210);
        check("a fresh double click on existing content still requests properties", requested == 1);

        void Send(OverlayPointerKind kind, uint time, OverlayModifiers modifiers = OverlayModifiers.None) =>
            typeof(DesktopSession).GetMethod("HandleCanvasPointer", Private)!.Invoke(session,
                [new OverlayPointerEvent(kind, point, OverlayMouseButton.Left, modifiers) { Time = time }]);
    }
}
