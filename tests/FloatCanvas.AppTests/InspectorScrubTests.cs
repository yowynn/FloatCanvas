using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using Controls = System.Windows.Controls;

internal static class InspectorScrubTests
{
    public static void Run(Action<string, bool> assert)
    {
        foreach (string key in new[] { "X", "Y", "W", "H", "Angle", "StrokeWidth", "CornerRadius" })
        {
            var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(20, 30) });
            var inspector = new InspectorWindow(() => session);
            var numericFields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
            string originalField = numericFields[key].Text;
            string before = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
            assert(key + " numeric drag starts a single transaction", (bool)Invoke(inspector, "BeginScrub", key)! && session.IsTransactionActive);
            Invoke(inspector, "UpdateScrub", key, 5d); Invoke(inspector, "UpdateScrub", key, 12d);
            assert(key + " numeric drag previews without adding history", session.IsTransactionActive && session.UndoCount == undo && JsonSerializer.Serialize(session.Document) != before);
            Invoke(inspector, "FinishScrub", true);
            string committedField = numericFields[key].Text;
            assert(key + " numeric drag commits once and one undo restores its full gesture", !session.IsTransactionActive && session.UndoCount == undo + 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before);
            inspector.Refresh();
            assert(key + " undo refresh restores its displayed numeric value without explicit reload", numericFields[key].Text == originalField);
            assert(key + " committed numeric drag can be redone", session.Redo());
            inspector.Refresh();
            assert(key + " redo refresh restores its displayed numeric value", numericFields[key].Text == committedField);
            session.Undo(); inspector.Refresh();
            assert(key + " undo refresh leaves no stale form fingerprint blocking the next drag", (bool)Invoke(inspector, "BeginScrub", key)!);
            Invoke(inspector, "UpdateScrub", key, -8d); Invoke(inspector, "FinishScrub", false);
            assert(key + " numeric cancellation restores geometry and history", !session.IsTransactionActive && session.UndoCount == undo && JsonSerializer.Serialize(session.Document) == before);
        }
        bool allowed = true;
        var guarded = new EditorSession(); guarded.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 50 });
        var window = new InspectorWindow(() => guarded, () => allowed);
        var fields = (Dictionary<string, Controls.TextBox>)Field(window, "fields");
        var containers = (Dictionary<string, System.Windows.FrameworkElement>)Field(window, "fieldContainers");
        var caption = ((Controls.Grid)containers["X"]).Children.OfType<Controls.TextBlock>().Single();
        var colors = (Dictionary<string, ColorField>)Field(window, "colorFields");
        var colorButton = (Controls.Button)Field(colors["Stroke"], "button");
        assert("focusable numeric labels and property buttons are not text editors", caption.Focusable && colorButton.Focusable &&
            !InspectorWindow.OwnsLocalEditingKeys(caption) && !InspectorWindow.OwnsLocalEditingKeys(colorButton));
        assert("numeric text fields remain text inputs for draft and IME protection", InspectorWindow.OwnsLocalEditingKeys(fields["X"]));

        Invoke(window, "BeginScrub", "X"); Invoke(window, "UpdateScrub", "X", 20d); Invoke(window, "FinishScrub", true);
        fields["X"].Text = "123.456";
        assert("valid numeric typing previews the document immediately", Math.Abs(ElementGeometry.Bounds(guarded.Document.Elements.Single()).Left - 123.456) < 1e-8);
        Invoke(window, "Apply");
        assert("committed numeric typing has its own undo operation", guarded.Undo());
        window.Refresh();
        assert("undo automatically refreshes the numeric input", fields["X"].Text != "123.456");
        assert("committed numeric typing can redo", guarded.Redo());
        window.Refresh();
        assert("redo automatically refreshes the numeric input", fields["X"].Text == "123.456");
        Invoke(window, "LoadSelection", true);
        fields["X"].Text = "draft";
        assert("numeric drag preserves an existing unapplied draft", !(bool)Invoke(window, "BeginScrub", "X")! && fields["X"].Text == "draft" && !guarded.IsTransactionActive);
        Invoke(window, "LoadSelection", true); allowed = false;
        assert("busy numeric label cannot begin a transaction", !(bool)Invoke(window, "BeginScrub", "X")! && !guarded.IsTransactionActive);
        allowed = true; string original = JsonSerializer.Serialize(guarded.Document); int history = guarded.UndoCount;
        Invoke(window, "BeginScrub", "X"); Invoke(window, "UpdateScrub", "X", 10d); allowed = false; Invoke(window, "FinishScrub", true);
        assert("numeric commit interrupted by busy state rolls back", JsonSerializer.Serialize(guarded.Document) == original && guarded.UndoCount == history && !guarded.IsTransactionActive);
        allowed = true; Invoke(window, "BeginScrub", "X"); Invoke(window, "FinishScrub", true);
        assert("click without dragging creates no undo step", guarded.UndoCount == history);

        var endpoints = new EditorSession(); endpoints.Add(new() { Kind = ElementKind.Line, Points = [new(0, 0), new(100, 30)] });
        var lineInspector = new InspectorWindow(() => endpoints);
        ((Controls.ComboBox)Field(lineInspector, "startArrow")).SelectedIndex = (int)ArrowHeadStyle.Diamond;
        ((Controls.ComboBox)Field(lineInspector, "endArrow")).SelectedIndex = (int)ArrowHeadStyle.Open;
        ((Controls.ComboBox)Field(lineInspector, "strokePattern")).SelectedIndex = (int)StrokePattern.Dashed;
        ((Dictionary<string, Controls.TextBox>)Field(lineInspector, "fields"))["ArrowHeadSize"].Text = "24";
        history = endpoints.UndoCount; Invoke(lineInspector, "Apply");
        var edited = endpoints.Document.Elements.Single();
        assert("independent line arrow endpoints, size and stroke pattern apply together", edited.Style.StartArrowHead == ArrowHeadStyle.Diamond && edited.Style.EndArrowHead == ArrowHeadStyle.Open &&
            edited.Style.ArrowHeadSize == 24 && edited.Style.StrokePattern == StrokePattern.Dashed && endpoints.UndoCount == history + 1);
        endpoints.Undo();
        assert("line arrow size and endpoint edits share one undo operation", endpoints.Document.Elements.Single().Style.ArrowHeadSize == 0 &&
            endpoints.Document.Elements.Single().Style.StartArrowHead is null && endpoints.Document.Elements.Single().Style.EndArrowHead is null);
        assert("numeric property checks remain detached from native desktop", new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !window.IsVisible);
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
