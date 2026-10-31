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
        var findings = new List<(string Name, bool Passed)>();
        foreach (string dimension in new[] { "W", "H" })
        {
            var session = new EditorSession();
            var locked = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100, ConstrainAspect = true };
            var free = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40, ConstrainAspect = false };
            session.Document.Elements.AddRange([locked, free]); session.SetSelection([locked.Id, free.Id]);
            var inspector = new InspectorWindow(() => session);
            using var surface = new InspectorTestSurface(inspector);
            var chain = Field<AspectRatioButton>(inspector, "keepAspect");
            var fields = Field<Dictionary<string, TextBox>>(inspector, "fields");
            Observe(dimension + " mixed-lock selection displays an indeterminate aspect chain", chain.IsThreeState && chain.IsChecked is null);
            string original = JsonSerializer.Serialize(session.Document.Elements);
            int undo = session.UndoCount;
            fields[dimension].Text = dimension == "W" ? "320" : "200";
            inspector.CompletePendingEdit();
            var afterLocked = session.Document.Elements.Single(e => e.Id == locked.Id);
            var afterFree = session.Document.Elements.Single(e => e.Id == free.Id);
            Console.WriteLine("ASPECT_INSPECTOR " + JsonSerializer.Serialize(new
            {
                Dimension = dimension, Before = original,
                After = session.Document.Elements.Select(e => new { e.Id, e.Width, e.Height, e.ConstrainAspect }),
                UndoBefore = undo, UndoAfter = session.UndoCount,
                Chain = chain.IsChecked, chain.IsThreeState
            }));
            Observe(dimension + " numeric edit preserves the locked layer's own ratio within a mixed selection",
                Near(afterLocked.Width, 320) && Near(afterLocked.Height, 200));
            Observe(dimension + " numeric edit leaves the unlocked layer's other dimension independent",
                dimension == "W" ? Near(afterFree.Width, 320) && Near(afterFree.Height, 40) : Near(afterFree.Width, 80) && Near(afterFree.Height, 200));
            Observe(dimension + " numeric edit retains both stored lock choices", afterLocked.ConstrainAspect == true && afterFree.ConstrainAspect == false);
            Observe(dimension + " multi-layer numeric edit commits one undo entry", session.UndoCount == undo + 1 && !session.IsTransactionActive);
            bool undone = session.Undo();
            Observe(dimension + " one undo restores both dimensions and both lock choices", undone && JsonSerializer.Serialize(session.Document.Elements) == original);
            bool redone = session.Redo();
            Observe(dimension + " redo reapplies the linked and independent dimensions", redone &&
                Near(session.Document.Elements.Single(e => e.Id == locked.Id).Width, 320) &&
                Near(session.Document.Elements.Single(e => e.Id == locked.Id).Height, 200));
        }
        Scrubs(Observe);
        UnifiedChoices(Observe);
        foreach (var finding in findings) check(finding.Name, finding.Passed);
        void Observe(string name, bool passed)
        {
            findings.Add((name, passed));
            Console.WriteLine($"ASPECT_INSPECTOR_FINDING {passed}: {name}");
        }
    }

    private static void Scrubs(Action<string, bool> observe)
    {
        foreach (string dimension in new[] { "W", "H" })
        foreach (bool commit in new[] { true, false })
        {
            var (session, locked, free) = Selection();
            var inspector = new InspectorWindow(() => session);
            using var surface = new InspectorTestSurface(inspector);
            string label = $"{dimension} mixed-lock numeric drag commit={commit}";
            string original = JsonSerializer.Serialize(session.Document.Elements);
            int undo = session.UndoCount;
            bool began = (bool)Invoke(inspector, "BeginScrub", dimension)!;
            observe(label + " starts the production multi-layer scrub", began && session.IsTransactionActive);
            if (!began) continue;
            Invoke(inspector, "UpdateScrub", dimension, 16d);
            Invoke(inspector, "UpdateScrub", dimension, 32d);
            var activeLocked = session.Document.Elements.Single(e => e.Id == locked.Id);
            var activeFree = session.Document.Elements.Single(e => e.Id == free.Id);
            Console.WriteLine("ASPECT_INSPECTOR_SCRUB " + JsonSerializer.Serialize(new
            {
                Dimension = dimension, Commit = commit,
                Preview = session.Document.Elements.Select(e => new { e.Width, e.Height, e.ConstrainAspect }),
                session.UndoCount, session.IsTransactionActive
            }));
            observe(label + " previews the locked layer's own ratio", dimension == "W"
                ? Near(activeLocked.Width, 192) && Near(activeLocked.Height, 120)
                : Near(activeLocked.Width, 211.2) && Near(activeLocked.Height, 132));
            observe(label + " previews only the requested axis on the unlocked layer", dimension == "W"
                ? Near(activeFree.Width, 112) && Near(activeFree.Height, 40)
                : Near(activeFree.Width, 80) && Near(activeFree.Height, 72));
            observe(label + " preserves both lock choices without preview history", activeLocked.ConstrainAspect == true &&
                activeFree.ConstrainAspect == false && session.UndoCount == undo);
            string preview = JsonSerializer.Serialize(session.Document.Elements);
            Invoke(inspector, "FinishScrub", commit);
            if (commit)
            {
                observe(label + " commits one complete gesture", !session.IsTransactionActive && session.UndoCount == undo + 1 &&
                    JsonSerializer.Serialize(session.Document.Elements) == preview);
                observe(label + " undo restores both layers and locks", session.Undo() && JsonSerializer.Serialize(session.Document.Elements) == original);
                observe(label + " redo restores both layer previews", session.Redo() && JsonSerializer.Serialize(session.Document.Elements) == preview);
            }
            else observe(label + " cancellation restores both layers and history", !session.IsTransactionActive && session.UndoCount == undo &&
                JsonSerializer.Serialize(session.Document.Elements) == original);
        }
    }

    private static void UnifiedChoices(Action<string, bool> observe)
    {
        foreach (string dimension in new[] { "W", "H" })
        foreach (bool constrained in new[] { true, false })
        {
            var (session, locked, free) = Selection();
            var inspector = new InspectorWindow(() => session);
            using var surface = new InspectorTestSurface(inspector);
            var chain = Field<AspectRatioButton>(inspector, "keepAspect");
            var fields = Field<Dictionary<string, TextBox>>(inspector, "fields");
            string label = $"{dimension} explicit multi-layer aspect choice={constrained}";
            string original = JsonSerializer.Serialize(session.Document.Elements);
            chain.IsChecked = constrained; chain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inspector.CompletePendingEdit();
            observe(label + " unifies both stored layer choices", session.Document.Elements.All(e => e.ConstrainAspect == constrained) &&
                !chain.IsThreeState && chain.IsChecked == constrained && session.UndoCount == 1);
            string unified = JsonSerializer.Serialize(session.Document.Elements);
            fields[dimension].Text = dimension == "W" ? "320" : "200";
            inspector.CompletePendingEdit();
            var actualLocked = session.Document.Elements.Single(e => e.Id == locked.Id);
            var actualFree = session.Document.Elements.Single(e => e.Id == free.Id);
            double lockedWidth = dimension == "W" || constrained ? 320 : 160;
            double lockedHeight = dimension == "H" || constrained ? 200 : 100;
            double freeWidth = dimension == "W" ? 320 : constrained ? 400 : 80;
            double freeHeight = dimension == "H" ? 200 : constrained ? 160 : 40;
            observe(label + " applies the unified choice to each layer's numeric resize", Near(actualLocked.Width, lockedWidth) &&
                Near(actualLocked.Height, lockedHeight) && Near(actualFree.Width, freeWidth) && Near(actualFree.Height, freeHeight));
            observe(label + " keeps choice and numeric edit as separate undo steps", session.UndoCount == 2 && !session.IsTransactionActive &&
                session.Undo() && JsonSerializer.Serialize(session.Document.Elements) == unified &&
                session.Undo() && JsonSerializer.Serialize(session.Document.Elements) == original);
        }
    }

    private static (EditorSession Session, CanvasElement Locked, CanvasElement Free) Selection()
    {
        var session = new EditorSession();
        var locked = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100, ConstrainAspect = true };
        var free = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40, ConstrainAspect = false };
        session.Document.Elements.AddRange([locked, free]); session.SetSelection([locked.Id, free.Id]);
        return (session, locked, free);
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-8;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static object? Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
}
