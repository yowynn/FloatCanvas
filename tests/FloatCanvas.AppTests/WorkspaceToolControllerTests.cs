using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class WorkspaceToolControllerTests
{
    internal static void Run(Action<string, bool> check)
    {
        SymbolsAndEmoji(check);
        AspectConstraints(check);
        ParameterIsolation(check);
        GroupSelection(check);
        GeneralEffects(check);
    }

    private static void SymbolsAndEmoji(Action<string, bool> check)
    {
        foreach (var symbol in Enum.GetValues<SymbolKind>())
        {
            var session = new EditorSession();
            var controller = new EditorController(session) { Tool = EditorTool.Symbol, Symbol = symbol };
            controller.Style.Fill = 0xFF66AAEE;
            Gesture(controller, new(70, 80), new(130, 120));
            var element = session.Document.Elements.Single();
            check("symbol tool preserves selected geometry and style: " + symbol, element.Kind == ElementKind.Symbol && element.Symbol == symbol &&
                element.Width == 60 && element.Height == 40 && element.Style.Fill == 0xFF66AAEE && element.Transform == Affine2D.Translation(70, 80));
            check("symbol creation is one undoable gesture: " + symbol, session.UndoCount == 1 && !session.IsTransactionActive &&
                session.Undo() && session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Single().Symbol == symbol);
        }
        var emojiSession = new EditorSession();
        var emoji = new EditorController(emojiSession) { Tool = EditorTool.Emoji, Emoji = "👩‍🎨", ScreenOrigin = new(-300, 100) };
        emoji.Style.FontSize = 18;
        Gesture(emoji, new(-250, 160), new(-250, 160));
        var sticker = emojiSession.Document.Elements.Single();
        var emojiMetrics = EmojiGlyphs.Measure("👩‍🎨", 18);
        check("emoji click inserts the full Unicode sequence at document coordinates", sticker.Kind == ElementKind.Text && sticker.Text == "👩‍🎨" &&
            sticker.Transform == Affine2D.Translation(50 - emojiMetrics.Width / 2, 60 - emojiMetrics.Height / 2) && sticker.Style.FontFamily == "Segoe UI Emoji");
        check("emoji click respects explicit size and independent tool style", sticker.Style.FontSize == 18 && sticker.Width == emojiMetrics.Width && sticker.Height == emojiMetrics.Height && emoji.Style.FontSize == 18);
        check("emoji click adds one editable object without a leftover gesture", emojiSession.UndoCount == 1 && !emojiSession.IsTransactionActive &&
            emoji.Marquee is null && emojiSession.Undo() && emojiSession.Document.Elements.Count == 0 && emojiSession.Redo());
    }

    private static void AspectConstraints(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.Ellipse })
        {
            foreach (bool reverse in new[] { false, true })
            {
                var session = new EditorSession();
                var controller = new EditorController(session) { Tool = tool, ConstrainAspect = true };
                Gesture(controller, new(100, 100), reverse ? new(40, 80) : new(160, 120));
                var element = session.Document.Elements.Single();
                check($"{tool} constrained drawing keeps equal dimensions in {(reverse ? "reverse" : "forward")} drags", element.Width == 60 && element.Height == 60 &&
                    element.Transform == Affine2D.Translation(reverse ? 40 : 100, reverse ? 40 : 100) && session.UndoCount == 1 && !session.IsTransactionActive);
            }
            var unconstrained = new EditorSession();
            var free = new EditorController(unconstrained) { Tool = tool };
            Gesture(free, new(20, 30), new(100, 70));
            check(tool + " remains unconstrained when its option and Shift are off", unconstrained.Document.Elements.Single().Width == 80 && unconstrained.Document.Elements.Single().Height == 40);
            var shifted = new EditorSession();
            Gesture(new(shifted) { Tool = tool }, new(20, 30), new(100, 70), OverlayModifiers.Shift);
            check(tool + " supports the temporary Shift aspect constraint", shifted.Document.Elements.Single().Width == 80 && shifted.Document.Elements.Single().Height == 80);
        }
    }

    private static void ParameterIsolation(Action<string, bool> check)
    {
        var session = new EditorSession();
        var controller = new EditorController(session);
        var parameters = new ToolParameters { Tool = EditorTool.Symbol, Symbol = SymbolKind.Heart, CornerRadius = 8,
            ConstrainAspect = true, EffectStrength = 21, Emoji = "🎨", Style = new() { Stroke = 0xAA112233, Fill = 0x66445566, StrokeWidth = 9 } };
        controller.ApplyParameters(parameters);
        parameters.Style.Stroke = 0xFFFFFFFF; parameters.Style.StrokeWidth = 2; parameters.Symbol = SymbolKind.Cross;
        check("applying tool parameters owns an independent copy of style and scalar values", controller.Tool == EditorTool.Symbol && controller.Symbol == SymbolKind.Heart &&
            controller.CornerRadius == 8 && controller.ConstrainAspect && controller.EffectStrength == 21 && controller.Emoji == "🎨" && controller.Style.Stroke == 0xAA112233 && controller.Style.StrokeWidth == 9);
        var readback = controller.Parameters; readback.Style.Fill = 0; readback.EffectStrength = 2;
        check("reading tool parameters cannot mutate the active tool by alias", controller.Style.Fill == 0x66445566 && controller.EffectStrength == 21);

        controller.ApplyParameters(new() { Tool = EditorTool.Rectangle, Style = new() { StrokeWidth = 5, Fill = 0xFF336699 } });
        Send(controller, OverlayPointerKind.Down, new(10, 20));
        Send(controller, OverlayPointerKind.Move, new(60, 80));
        string before = JsonSerializer.Serialize(session.Document), settingsBefore = JsonSerializer.Serialize(controller.Parameters);
        long transaction = session.TransactionId;
        var invalid = controller.Parameters; invalid.Tool = EditorTool.Ellipse; invalid.Style.Opacity = double.NaN;
        bool refused = false;
        try { controller.ApplyParameters(invalid); } catch (ArgumentException) { refused = true; }
        check("invalid parameters are rejected without cancelling an in-progress gesture", refused && session.IsTransactionActive && session.TransactionId == transaction &&
            JsonSerializer.Serialize(session.Document) == before && session.UndoCount == 0);
        check("invalid parameters leave the selected tool and its settings unchanged", JsonSerializer.Serialize(controller.Parameters) == settingsBefore);
        Send(controller, OverlayPointerKind.Up, new(80, 100));
        check("drawing can finish normally after invalid parameters are rejected", session.UndoCount == 1 && !session.IsTransactionActive &&
            session.Document.Elements.Single() is { Kind: ElementKind.Rectangle, Width: 70, Height: 80, Style.StrokeWidth: 5 });
    }

    private static void GroupSelection(Action<string, bool> check)
    {
        var group = new CanvasGroup { Name = "editable stamp" };
        var first = Box(100, 100, group.Id);
        var second = Box(250, 100, group.Id);
        var session = new EditorSession(new() { Groups = [group], Elements = [first, second] });
        var controller = new EditorController(session) { Tool = EditorTool.Select };
        Gesture(controller, new(140, 130), new(140, 130));
        check("clicking a group member selects the complete logical group", session.Selection.SetEquals([first.Id, second.Id]) && session.UndoCount == 0 && !session.IsTransactionActive);
        Gesture(controller, new(140, 130), new(160, 145));
        check("dragging a selected group moves all members in one transaction", session.Document.Elements.All(element => element.Transform.OffsetY == 115) &&
            session.Document.Elements[0].Transform.OffsetX == 120 && session.Document.Elements[1].Transform.OffsetX == 270 && session.UndoCount == 1);
        Gesture(controller, new(160, 145), new(160, 145), OverlayModifiers.Alt);
        check("Alt-click chooses one editable child without ungrouping or adding history", session.Selection.SetEquals([first.Id]) && session.Document.Groups.Count == 1 && session.UndoCount == 1);
        Gesture(controller, new(160, 145), new(170, 165), OverlayModifiers.Alt);
        check("Alt-drag moves only that child while preserving group membership", session.Document.Elements[0].Transform == Affine2D.Translation(130, 135) &&
            session.Document.Elements[1].Transform == Affine2D.Translation(270, 115) && session.Document.Elements.All(element => element.GroupId == group.Id) && session.UndoCount == 2);
        Gesture(controller, new(170, 165), new(170, 165));
        check("a normal click after child editing returns to selecting the complete group", session.Selection.SetEquals([first.Id, second.Id]) && !session.IsTransactionActive);
    }

    private static void GeneralEffects(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Blur, EditorTool.Pixelate })
        {
            foreach (int imageCount in new[] { 0, 1, 2 })
            {
                var images = Enumerable.Range(0, imageCount).Select(index => new CanvasElement { Kind = ElementKind.Image, Width = 120, Height = 100,
                    AssetId = new string((char)('a' + index), 64), Transform = Affine2D.Translation(index * 160, 10) }).ToList();
                var session = new EditorSession(new() { Elements = images });
                session.SetSelection(images.Select(image => image.Id));
                var controller = new EditorController(session) { Tool = tool, ScreenOrigin = new(-200, 120), EffectStrength = 23 };
                controller.Style.Stroke = 0xAA123456;
                var requests = new List<(RectD Region, ImageEffectKind Kind, double Amount, uint Color, bool TransactionActive)>();
                controller.DesktopEffectRequested += (region, kind, amount, color) => requests.Add((region, kind, amount, color, session.IsTransactionActive));
                string before = JsonSerializer.Serialize(session.Document);
                Send(controller, OverlayPointerKind.Down, new(-100, 190));
                Send(controller, OverlayPointerKind.Move, new(-170, 140));
                check($"{tool} previews a general canvas region with {imageCount} selected images", controller.Marquee == new RectD(30, 20, 70, 50) && !session.IsTransactionActive);
                Send(controller, OverlayPointerKind.Up, new(-170, 140));
                check($"{tool} dispatches normalized document bounds, strength and color with {imageCount} selected images", requests is [{ Region: var region, Kind: var kind, Amount: 23, Color: 0xAA123456, TransactionActive: false }] &&
                    region == new RectD(30, 20, 70, 50) && kind.ToString() == tool.ToString());
                check($"{tool} leaves document history and gesture state clean before asynchronous desktop processing with {imageCount} selected images", session.UndoCount == 0 && !session.IsTransactionActive &&
                    controller.Marquee is null && controller.EffectPreviewPolygon is null && JsonSerializer.Serialize(session.Document) == before && session.Selection.Count == imageCount);
                Gesture(controller, new(40, 40), new(41, 41));
                check($"{tool} ignores too-small general regions with {imageCount} selected images", requests.Count == 1 && !session.IsTransactionActive && controller.Marquee is null);
                Send(controller, OverlayPointerKind.Down, new(100, 100));
                Send(controller, OverlayPointerKind.Move, new(150, 150)); controller.Cancel();
                Send(controller, OverlayPointerKind.Up, new(150, 150));
                check($"{tool} cancels a general region without dispatching or leaving capture state with {imageCount} selected images", requests.Count == 1 && controller.Marquee is null && !session.IsTransactionActive);
            }
            var locked = new CanvasElement { Kind = ElementKind.Image, Width = 120, Height = 100, Locked = true, AssetId = new string('c', 64) };
            var lockedSession = new EditorSession(new() { Elements = [locked] }); lockedSession.SetSelection([locked.Id]);
            var lockedController = new EditorController(lockedSession) { Tool = tool };
            int emitted = 0; var messages = new List<string>();
            lockedController.DesktopEffectRequested += (_, _, _, _) => emitted++;
            lockedController.Message += messages.Add;
            string lockedBefore = JsonSerializer.Serialize(lockedSession.Document);
            Gesture(lockedController, new(20, 20), new(80, 70));
            check(tool + " targets the live desktop region independently of a selected locked image",
                emitted == 1 && messages.Count == 0 &&
                JsonSerializer.Serialize(lockedSession.Document) == lockedBefore && lockedSession.UndoCount == 0 && !lockedSession.IsTransactionActive);
        }
    }

    private static CanvasElement Box(double x, double y, Guid group) => new() { Kind = ElementKind.Rectangle, Width = 80, Height = 60,
        GroupId = group, Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF336699 } };
    private static void Gesture(EditorController controller, PointD from, PointD to, OverlayModifiers modifiers = OverlayModifiers.None)
    {
        Send(controller, OverlayPointerKind.Down, from, modifiers); Send(controller, OverlayPointerKind.Move, to, modifiers); Send(controller, OverlayPointerKind.Up, to, modifiers);
    }
    private static void Send(EditorController controller, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        controller.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
}
