using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

internal static class TextToolTransformTests
{
    internal static void Run(Action<string, bool> check)
    {
        var report = check; var failures = new List<string>();
        check = (name, passed) => { if (passed) report(name, true); else { failures.Add(name); Console.WriteLine("TEXT REGRESSION FAIL: " + name); } };
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string area in new[] { "corner", "border", "rotation", "interior" })
        {
            var text = Text(); var state = new EditorSession(new() { Elements = [text] }); state.SetSelection([text.Id]);
            var editor = new EditorController(state) { Tool = EditorTool.Text };
            int edits = 0; editor.EditText += (_, _) => edits++;
            var point = area switch { "corner" => editor.SelectionScreenCorners[2], "border" => new(190, 100),
                "rotation" => editor.SelectionScreenRotationHandle!.Value, _ => new PointD(190, 125) };
            Send(editor, OverlayPointerKind.Down, point);
            check("Text tool " + area + " prioritizes the selected frame instead of editing", edits == 0 && state.IsTransactionActive);
            Send(editor, OverlayPointerKind.Move, point + new PointD(35, 20)); Send(editor, OverlayPointerKind.Up, point + new PointD(35, 20));
            check("Text tool " + area + " transforms the selected text in one undo step", state.UndoCount == 1 && state.Document.Elements.Count == 1 && state.Selection.SetEquals([text.Id]) && state.Undo() && state.Document.Elements.Single().Transform == Affine2D.Translation(100, 100));
        }
        var doubleClicked = Text(); var doubleState = new EditorSession(new() { Elements = [doubleClicked] }); doubleState.SetSelection([doubleClicked.Id]);
        var doubleEditor = new EditorController(doubleState) { Tool = EditorTool.Text };
        CanvasElement? edited = null; int doubleEdits = 0;
        doubleEditor.EditText += (existing, _) => { edited = existing; doubleEdits++; };
        Send(doubleEditor, OverlayPointerKind.Down, new(190, 125)); Send(doubleEditor, OverlayPointerKind.Up, new(190, 125));
        Send(doubleEditor, OverlayPointerKind.DoubleClick, new(190, 125));
        check("a completed first click still permits double-click editing of selected text", doubleEdits == 1 && ReferenceEquals(edited, doubleClicked) &&
            !doubleState.IsTransactionActive && doubleState.UndoCount == 0 && doubleState.Document.Elements.Count == 1 && doubleState.Selection.SetEquals([doubleClicked.Id]));
        foreach (string placement in new[] { "rotated", "projected", "billboard" })
        {
            var text = Text(); var document = new SceneDocument { Viewport = new(0, 0, 1000, 800), Elements = [text] };
            if (placement == "rotated") text.Transform = text.Transform.Compose(Affine2D.Rotation(20));
            else
            {
                var projection = CanvasCalibration.CreateInitial(document.Viewport);
                check("projected text fixture calibrates", CanvasCalibration.TryCalibrate(projection,
                    [new(80, 60), new(920, 100), new(850, 740), new(110, 700)], document.Viewport, out projection, out _));
                document.Canvas.Projection = projection;
                if (placement == "billboard") { text.IsBillboard = true; text.BillboardAnchor = new(190, 150); }
            }
            var state = new EditorSession(document); state.SetSelection([text.Id]);
            var editor = new EditorController(state) { Tool = EditorTool.Text };
            int edits = 0; editor.EditText += (_, _) => edits++;
            var corners = editor.SelectionScreenCorners; var border = (corners[0] + corners[1]) * .5;
            Send(editor, OverlayPointerKind.Down, border); Send(editor, OverlayPointerKind.Move, border + new PointD(20, 15)); Send(editor, OverlayPointerKind.Up, border + new PointD(20, 15));
            check(placement + " Text selection border uses the shared screen transform route", edits == 0 && state.UndoCount == 1 && state.Selection.SetEquals([text.Id]));
        }
        var selected = Text(); var selectedState = new EditorSession(new() { Elements = [selected] }); selectedState.SetSelection([selected.Id]);
        var selectedEditor = new EditorController(selectedState) { Tool = EditorTool.Text };
        int newText = 0; selectedEditor.EditText += (existing, _) => { if(existing is null) newText++; };
        Send(selectedEditor, OverlayPointerKind.Down, new(100, 100), OverlayModifiers.Control);
        check("Ctrl still creates new text at a selected handle", newText == 1 && !selectedState.IsTransactionActive);
        using var renderer = new SceneRenderer();
        using (var edit = new InlineTextEdit(selectedState, selected, new(), selected.Style, renderer))
        {
            var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
            typeof(DesktopSession).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(desktop, selectedEditor);
            typeof(DesktopSession).GetField("_inlineText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(desktop, edit);
            var pointer = new OverlayPointerEvent(OverlayPointerKind.Down, new(190, 100), OverlayMouseButton.Left, OverlayModifiers.None);
            bool handled = (bool)typeof(DesktopSession).GetMethod("HandleInlineTextPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(desktop, [pointer])!;
            check("an active inline edit releases its frame border to the transform route", !handled && !edit.IsActive && !selectedState.IsTransactionActive);
        }
        foreach (var alignment in Enum.GetValues<FloatCanvas.Core.TextAlignment>())
        {
            var text = Text(); text.Text = "AAA BBB CCC"; text.Width = 70; text.Style.TextAlignment = alignment;
            text.Height = renderer.MeasureText(text.Text, text.Style, text.Width).Height;
            double originalSize = text.Style.FontSize;
            check("reflow fixture starts with wrapped text", renderer.MeasureText(text.Text, text.Style, text.Width).LineCount >= 3);
            var state = new EditorSession(new() { Elements = [text] }); state.SetSelection([text.Id]);
            var editor = new EditorController(state) { Tool = EditorTool.Select };
            var center = ElementGeometry.Bounds(text, false).Center; var corner = editor.SelectionScreenCorners[2];
            double wantedSize = text.Height / 1.35; var style = text.Style.Clone(); style.FontSize = wantedSize;
            double width = renderer.MeasureText(text.Text, style).Width + 8;
            var to = new PointD(center.X + width / 2, corner.Y);
            Send(editor, OverlayPointerKind.Down, corner); Send(editor, OverlayPointerKind.Move, to); Send(editor, OverlayPointerKind.Up, to);
            var measured = renderer.MeasureText(text.Text, text.Style, text.Width);
            check("widened " + alignment + " text grows to fill its one-line frame without old wrapped-line space", measured.LineCount == 1 && text.Style.FontSize > originalSize * 2.5 && measured.Height >= text.Height - .02 && measured.Width <= text.Width + .01 && state.UndoCount == 1);
            var enlarged = text.Style.FontSize;
            check("frame fitting preserves alignment and undoes font and wrapping together", text.Style.TextAlignment == alignment && state.Undo() && state.Document.Elements.Single().Style.FontSize == originalSize && state.Redo() && Math.Abs(state.Document.Elements.Single().Style.FontSize - enlarged) < .001);
        }
        var paragraphs = Text(); paragraphs.Text = "A\n\nB"; paragraphs.Height = 90;
        TextFrameLayout.Resize(paragraphs, paragraphs.Clone(), 2, 1, renderer);
        check("font fitting retains deliberate paragraph breaks and empty paragraphs", paragraphs.Text == "A\n\nB" && renderer.MeasureText(paragraphs.Text, paragraphs.Style, paragraphs.Width).LineCount == 3);
        report("text tool and frame regression has no failures (" + failures.Count + ")", failures.Count == 0);
    }
    private static CanvasElement Text() => new() { Kind = ElementKind.Text, Text = "Edit this text", Width = 180, Height = 50,
        Transform = Affine2D.Translation(100, 100), Style = new() { FontSize = 24 } };
    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
}
