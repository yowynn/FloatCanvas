using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PropertiesClickSequenceTests
{
    internal static void Run(Action<string, bool> check)
    {
        var sequence = new PropertiesClickSequence(500, 8, 8);
        var session = new EditorSession(new SceneDocument(), new AssetLibrary());
        session.Document.Elements.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 200, Height = 150,
            Transform = Affine2D.Translation(100, 100), Style = new() { Fill = 0xFF2383E2 } });
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        int requested = 0;
        editor.PropertiesRequested += _ => requested++;
        void Send(OverlayPointerKind kind, uint time, bool dismissed = false)
        {
            var pointer = Event(kind, time);
            editor.PointerForCanvas(pointer, sequence.Observe(pointer, dismissed));
        }
        Send(OverlayPointerKind.Down, 100); Send(OverlayPointerKind.Up, 120);
        Send(OverlayPointerKind.DoubleClick, 200); Send(OverlayPointerKind.Up, 220);
        check("first real canvas click pair opens properties", requested == 1);
        Send(OverlayPointerKind.Down, 300, dismissed: true); Send(OverlayPointerKind.Up, 320);
        Send(OverlayPointerKind.DoubleClick, 400); Send(OverlayPointerKind.Up, 420);
        check("dismissal click cannot pair with the next same-position single click", requested == 1);
        Send(OverlayPointerKind.Down, 500); Send(OverlayPointerKind.Up, 520);
        check("two fresh clicks reopen properties even across the OS double/down boundary", requested == 2);

        sequence.Reset();
        check("an unpaired native double message cannot alone reopen properties", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 1000)));
        sequence.Observe(Event(OverlayPointerKind.Up, 1010));
        check("a later fresh completed pair still opens properties", sequence.Observe(Event(OverlayPointerKind.Down, 1100)));
        sequence.Observe(Event(OverlayPointerKind.Up, 1110));
        check("a consumed double click cannot seed another properties pair", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 1200)));

        sequence.Reset(); Complete(2000);
        check("properties pairing honors physical horizontal double-click bounds", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 2100, new(155, 150))));
        sequence.Reset(); Complete(2000);
        check("properties pairing honors physical vertical double-click bounds", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 2100, new(150, 155))));
        sequence.Reset(); Complete(2000);
        check("properties pairing expires despite a double-click label", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 2501)));
        sequence.Reset(); Complete(2000);
        check("reordered timestamps do not form a properties pair", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 1999)));
        sequence.Reset(); Complete(uint.MaxValue - 100);
        check("properties pairing tolerates native message-time wraparound", sequence.Observe(Event(OverlayPointerKind.Down, 100)));

        sequence.Reset();
        sequence.Observe(Event(OverlayPointerKind.Down, 3000));
        sequence.Observe(Event(OverlayPointerKind.Move, 3010, new(170, 170)));
        sequence.Observe(Event(OverlayPointerKind.Up, 3020));
        check("a drag cannot seed a properties double click", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 3100)));
        sequence.Reset(); Complete(4000);
        sequence.Observe(Event(OverlayPointerKind.Down, 4050) with { Button = OverlayMouseButton.Right });
        check("another button breaks a pending properties pair", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 4100)));
        sequence.Reset(); Complete(5000);
        sequence.Observe(Event(OverlayPointerKind.Cancel, 5050));
        check("cancellation breaks a pending properties pair", !sequence.Observe(Event(OverlayPointerKind.DoubleClick, 5100)));
        check("explicit untimed controller fixtures retain double-click semantics", sequence.Observe(new(OverlayPointerKind.DoubleClick, new(150, 150), OverlayMouseButton.Left, OverlayModifiers.None)));

        editor.Tool = EditorTool.Text;
        int textEdits = 0; editor.EditText += (_, _) => textEdits++;
        editor.PointerForCanvas(Event(OverlayPointerKind.DoubleClick, 6000), false);
        check("property permission does not remove the original Text double-click action", textEdits == 1);

        void Complete(uint time)
        { sequence.Observe(Event(OverlayPointerKind.Down, time)); sequence.Observe(Event(OverlayPointerKind.Up, unchecked(time + 10))); }
    }

    private static OverlayPointerEvent Event(OverlayPointerKind kind, uint time, OverlayPoint? point = null) =>
        new(kind, point ?? new(150, 150), OverlayMouseButton.Left, OverlayModifiers.None) { Time = time };
}
