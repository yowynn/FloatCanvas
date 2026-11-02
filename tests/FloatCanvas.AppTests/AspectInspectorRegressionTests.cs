using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class AspectInspectorRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        foreach (string dimension in new[] { "W", "H" })
        foreach (bool constrained in new[] { true, false })
        foreach (bool individually in new[] { true, false })
        {
            var (session, locked, free) = Selection(); session.TransformIndividually = individually;
            var inspector = new InspectorWindow(() => session); using var surface = new InspectorTestSurface(inspector);
            var panel = Field<NodeTransformPanel>(inspector, "nodeTransforms");
            var fields = Field<Dictionary<string, NumericValueField>>(panel, "fields");
            Field<AspectRatioButton>(panel, "aspect").IsChecked = constrained;
            string label = $"{dimension} selection aspect={constrained}, individual={individually}";
            string original = JsonSerializer.Serialize(session.Document.Elements);
            var before = session.SelectedWorldElements().ToDictionary(element => element.Id, element => ElementGeometry.Bounds(element));
            var combined = session.SelectionBounds()!.Value;
            double value = (dimension == "W" ? combined.Width : combined.Height) * 2;
            fields[dimension].Input.Text = value.ToString(CultureInfo.CurrentCulture);
            Invoke(panel, "Publish", dimension); inspector.CompletePendingEdit();
            var after = session.SelectedWorldElements().ToDictionary(element => element.Id, element => ElementGeometry.Bounds(element));
            double sx = dimension == "W" || constrained ? 2 : 1, sy = dimension == "H" || constrained ? 2 : 1;
            foreach (var id in new[] { locked.Id, free.Id })
            {
                check(label + " applies the same selected-axis scale to every member", Near(after[id].Width, before[id].Width * sx) && Near(after[id].Height, before[id].Height * sy));
                PointD expected = individually ? before[id].Center :
                    Affine2D.Around(combined.Center, Affine2D.Scale(sx, sy)).TransformPoint(before[id].Center);
                check(label + " uses the chosen whole-selection or individual pivot", after[id].Center.DistanceTo(expected) < 1e-8);
            }
            check(label + " keeps local dimensions and each layer's stored creation policy",
                session.Document.Elements.Single(e => e.Id == locked.Id) is { Width: 160, Height: 100, ConstrainAspect: true } &&
                session.Document.Elements.Single(e => e.Id == free.Id) is { Width: 80, Height: 40, ConstrainAspect: false });
            string preview = JsonSerializer.Serialize(session.Document.Elements);
            check(label + " commits one complete transform", session.UndoCount == 1 && !session.IsTransactionActive);
            check(label + " one undo restores both local placements and policies", session.Undo() && JsonSerializer.Serialize(session.Document.Elements) == original);
            check(label + " redo restores the complete transform", session.Redo() && JsonSerializer.Serialize(session.Document.Elements) == preview);
        }
        Scrubs(check);
        SingleLeafPolicies(check);
    }

    private static void Scrubs(Action<string, bool> check)
    {
        foreach (string dimension in new[] { "W", "H" })
        foreach (bool commit in new[] { true, false })
        {
            var (session, _, _) = Selection(); var inspector = new InspectorWindow(() => session);
            using var surface = new InspectorTestSurface(inspector);
            var panel = Field<NodeTransformPanel>(inspector, "nodeTransforms");
            var fields = Field<Dictionary<string, NumericValueField>>(panel, "fields");
            string label = $"{dimension} selection transform gesture commit={commit}";
            string original = JsonSerializer.Serialize(session.Document.Elements);
            var bounds = session.SelectionBounds()!.Value;
            double start = dimension == "W" ? bounds.Width : bounds.Height;
            check(label + " starts the production selection transform transaction", (bool)Invoke(panel, "BeginEdit")! && session.IsTransactionActive);
            fields[dimension].Input.Text = (start * 1.2).ToString(CultureInfo.CurrentCulture); Invoke(panel, "Publish", dimension);
            fields[dimension].Input.Text = (start * 1.4).ToString(CultureInfo.CurrentCulture); Invoke(panel, "Publish", dimension);
            var after = session.SelectionBounds()!.Value;
            check(label + " second sample is relative to the initial snapshot", Near(after.Width, bounds.Width * 1.4) && Near(after.Height, bounds.Height * 1.4) && session.UndoCount == 0);
            string preview = JsonSerializer.Serialize(session.Document.Elements);
            if (commit)
            {
                panel.CompletePendingEdit();
                check(label + " commits one history entry", session.UndoCount == 1 && !session.IsTransactionActive && JsonSerializer.Serialize(session.Document.Elements) == preview);
                check(label + " undo and redo preserve the exact local snapshots", session.Undo() && JsonSerializer.Serialize(session.Document.Elements) == original &&
                    session.Redo() && JsonSerializer.Serialize(session.Document.Elements) == preview);
            }
            else
            {
                panel.CancelEditing();
                check(label + " cancellation restores all members without history", session.UndoCount == 0 && !session.IsTransactionActive && JsonSerializer.Serialize(session.Document.Elements) == original);
            }
        }
    }

    private static void SingleLeafPolicies(Action<string, bool> check)
    {
        foreach (string dimension in new[] { "W", "H" })
        foreach (bool constrained in new[] { true, false })
        {
            var (session, locked, _) = Selection(); session.SetSelection([locked.Id]);
            var inspector = new InspectorWindow(() => session); using var surface = new InspectorTestSurface(inspector);
            var chain = Field<AspectRatioButton>(inspector, "keepAspect");
            chain.IsChecked = constrained; chain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); inspector.CompletePendingEdit();
            var fields = Field<Dictionary<string, TextBox>>(inspector, "fields");
            fields[dimension].Text = dimension == "W" ? "320" : "200"; inspector.CompletePendingEdit();
            var result = session.Document.Elements.Single(element => element.Id == locked.Id);
            check($"{dimension} single leaf retains its own persisted aspect policy={constrained}",
                result.ConstrainAspect == constrained && Near(result.Width, dimension == "W" || constrained ? 320 : 160) &&
                Near(result.Height, dimension == "H" || constrained ? 200 : 100));
        }
    }

    private static (EditorSession Session, CanvasElement Locked, CanvasElement Free) Selection()
    {
        var session = new EditorSession();
        var locked = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100, ConstrainAspect = true, Style = new() { StrokeWidth = 0 } };
        var free = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40, Transform = Affine2D.Translation(240, 50), ConstrainAspect = false, Style = new() { StrokeWidth = 0 } };
        session.Document.Elements.AddRange([locked, free]); session.SetSelection([locked.Id, free.Id]);
        return (session, locked, free);
    }
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-8;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static object? Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
}
