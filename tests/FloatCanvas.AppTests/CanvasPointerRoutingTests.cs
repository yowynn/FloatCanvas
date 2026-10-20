using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CanvasPointerRoutingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host)
    {
        var editor = Field<EditorController>(session, "_editor");
        var state = editor.Session;
        Field<InspectorWindow?>(session, "_inspector")?.Dismiss();
        state.Execute("pointer routing fixture", document => document.Elements.Clear());
        var item = new CanvasElement { Kind = ElementKind.Rectangle, Width = 200, Height = 150,
            Transform = Affine2D.Translation(100, 100), Style = new() { Fill = 0xFF2383E2 } };
        state.Add(item); state.SetSelection([item.Id]);
        EditorTool saved = editor.Tool; editor.Tool = EditorTool.Select;
        var point = new OverlayPoint(editor.ScreenOrigin.X + 150, editor.ScreenOrigin.Y + 150);
        uint time = unchecked((uint)Environment.TickCount);
        Send(OverlayPointerKind.Down); Send(OverlayPointerKind.Up);
        Send(OverlayPointerKind.DoubleClick); Send(OverlayPointerKind.Up);
        var inspector = Field<InspectorWindow?>(session, "_inspector");
        check("production canvas pointer route opens Inspector for the first complete click pair", inspector?.IsVisible == true);
        Send(OverlayPointerKind.Down); Send(OverlayPointerKind.Up);
        check("production canvas single press dismisses unpinned Inspector", inspector?.IsVisible == false);
        Send(OverlayPointerKind.DoubleClick); Send(OverlayPointerKind.Up);
        check("production same-position single press after dismissal cannot reopen Inspector", inspector?.IsVisible == false);
        Send(OverlayPointerKind.Down); Send(OverlayPointerKind.Up);
        check("production fresh second press reopens Inspector across native double/down pairing", inspector?.IsVisible == true);
        inspector?.Dismiss();

        editor.Tool = EditorTool.Stamp; state.SetSelection([item.Id]);
        int count = state.Document.Elements.Count;
        var before = state.Document.Elements.Single(element => element.Id == item.Id).Transform;
        Send(OverlayPointerKind.Down, OverlayMouseButton.Middle);
        check("production middle press starts temporary Move before the Stamp placement route", editor.IsTemporaryMove && editor.Tool == EditorTool.Stamp && state.Document.Elements.Count == count);
        Send(OverlayPointerKind.Move, OverlayMouseButton.Middle, new(point.X + 20, point.Y + 10));
        Send(OverlayPointerKind.Up, OverlayMouseButton.Middle, new(point.X + 20, point.Y + 10));
        check("production Stamp middle drag moves existing content without adding a stamp or changing tool", !editor.IsTemporaryMove && editor.Tool == EditorTool.Stamp &&
            state.Document.Elements.Count == count && state.Document.Elements.Single(element => element.Id == item.Id).Transform ==
                Affine2D.Translation(20,10).Compose(before));
        check("production pointer fixture preserves native editing", host.IsEditing);
        editor.Tool = saved;

        void Send(OverlayPointerKind kind, OverlayMouseButton button = OverlayMouseButton.Left, OverlayPoint? position = null)
        {
            var pointer = new OverlayPointerEvent(kind, position ?? point, button, OverlayModifiers.None) { Time = time = unchecked(time + 5) };
            typeof(DesktopSession).GetMethod("HandleCanvasPointer", Private)!.Invoke(session, [pointer]);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
}
