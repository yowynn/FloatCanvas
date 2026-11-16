using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class PresentationInspectorPersistenceTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(AppContext.BaseDirectory, "presentation-inspector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var element = Layer("Segoe UI", 24, 40);
        var session = new EditorSession(new() { Elements = [element], Viewport = new(0, 0, 1200, 900) });
        var originalTransform = element.Transform;
        session.SetSelection([element.Id]);
        using (var edit = new Fixture(session, folder))
        {
            edit.Set("FontFamily", "Consolas");
            Console.WriteLine($"PRESENTATION_FONT family={session.Document.Elements[0].Style.FontFamily} size={session.Document.Elements[0].Style.FontSize}");
            check("presentation font family is committed to the layer rather than only its textbox", Current().Style.FontFamily == "Consolas");
            check("changing presentation font preserves the saved size", Current().Style.FontSize == 24);
            double priorWidth = Current().Width, priorHeight = Current().Height;
            var priorBottom = ElementGeometry.Bounds(Current(), false).Bottom;
            edit.Set("FontSize", "53");
            check("presentation size commits while retaining the edited family", Current().Style.FontSize == 53 && Current().Style.FontFamily == "Consolas");
            check("font size scales both dimensions and preserves the bottom anchor", Math.Abs(Current().Width / priorWidth - 53d / 24) < .001 && Math.Abs(Current().Height / priorHeight - 53d / 24) < .001 && Math.Abs(ElementGeometry.Bounds(Current(), false).Bottom - priorBottom) < .001);
            edit.Set("X", "175"); edit.Set("Y", "210");
            check("moving the presentation region retains both typography fields", Current().Style.FontFamily == "Consolas" && Current().Style.FontSize == 53 && Current().Transform != originalTransform);
            edit.Window.PrepareForShow();
            check("reloading the inspector retains committed typography and position", edit.Fields["FontFamily"].Text == "Consolas" && edit.Fields["FontSize"].Text == "53" && edit.Fields["X"].Text == "175" && edit.Fields["Y"].Text == "210");
            check("each committed presentation property edit has its own undo boundary", session.UndoCount == 4);
            check("undoing position preserves committed typography", session.Undo() && Current().Style.FontFamily == "Consolas" && Current().Style.FontSize == 53);
            check("redoing position preserves committed typography", session.Redo() && Current().Style.FontFamily == "Consolas" && Current().Style.FontSize == 53);
            edit.Window.PrepareForShow(); edit.Set("FontSize", "0");
            check("invalid presentation size cannot overwrite valid typography", Current().Style.FontSize == 53 && Current().Style.FontFamily == "Consolas");
        }
        string path = Path.Combine(folder, "typography.floatcanvas");
        Task.Run(() => new ProjectStore().SaveAsync(path, session.Document, session.Assets)).GetAwaiter().GetResult();
        var reopened = Task.Run(() => new ProjectStore().LoadAsync(path)).GetAwaiter().GetResult();
        check("presentation family and size survive a real project save and reopen", reopened.Document.Elements.Single().Style is { FontFamily: "Consolas", FontSize: 53 });

        var first = Layer("Consolas", 33, 20); var second = Layer("Segoe UI Emoji", 47, 450);
        var multiple = new EditorSession(new() { Elements = [first, second], Viewport = new(0, 0, 1200, 900) });
        multiple.SetSelection([first.Id, second.Id]);
        using (var edit = new Fixture(multiple, folder))
        {
            edit.Set("FontFamily", "Calibri");
            check("mixed presentation family editing preserves each layer's independent size", multiple.Document.Elements.All(e => e.Style.FontFamily == "Calibri") && multiple.Document.Elements.Select(e => e.Style.FontSize).SequenceEqual(new double[] { 33, 47 }));
            edit.Set("FontSize", "61");
            check("mixed presentation size editing preserves the committed family", multiple.Document.Elements.All(e => e.Style.FontFamily == "Calibri" && e.Style.FontSize == 61));
        }
        var burstLayer = Layer("Segoe UI", 25, 30);
        var burst = new EditorSession(new() { Elements = [burstLayer], Viewport = new(0, 0, 1200, 900) });
        burst.SetSelection([burstLayer.Id]);
        using (var edit = new Fixture(burst, folder))
        {
            edit.Fields["FontFamily"].Text = "Consolas";
            edit.Fields["FontSize"].Text = "44";
            edit.Fields["X"].Text = "135";
            check("rapid uncommitted family size and position edits preserve all three live model values", burst.IsTransactionActive &&
                burstLayer.Style is { FontFamily: "Consolas", FontSize: 44 } && burstLayer.Transform.OffsetX == 135);
            edit.Window.CompletePendingEdit();
            check("completing a rapid typography burst retains one coherent undo unit", !burst.IsTransactionActive && burst.UndoCount == 1);
            check("undoing a rapid typography burst restores its complete original state", burst.Undo() &&
                burst.Document.Elements.Single().Style is { FontFamily: "Segoe UI", FontSize: 25 } && burst.Document.Elements.Single().Transform.OffsetX == 30);
            check("redoing a rapid typography burst restores all committed values together", burst.Redo() &&
                burst.Document.Elements.Single().Style is { FontFamily: "Consolas", FontSize: 44 } && burst.Document.Elements.Single().Transform.OffsetX == 135);
        }
        var sizing = Layer("Segoe UI", 24, 300); sizing.ConstrainAspect = true;
        var sizingSession = new EditorSession(new() { Elements = [sizing], Viewport = new(0, 0, 1200, 900) });
        sizingSession.SetSelection([sizing.Id]);
        using (var edit = new Fixture(sizingSession, folder))
        {
            double originalHeight = sizing.Height, originalWidth = sizing.Width;
            edit.Set("H", (originalHeight * 1.5).ToString(System.Globalization.CultureInfo.CurrentCulture));
            check("locked layer height resize adjusts width and font size", Math.Abs(sizing.Height - originalHeight * 1.5) < .01 && Math.Abs(sizing.Width - originalWidth * 1.5) < .01 && Math.Abs(sizing.Style.FontSize - 36) < .01);
            var panel = (PresentationSettingsPanel)typeof(InspectorWindow).GetField("presentationSettings", Hidden)!.GetValue(edit.Window)!;
            var entries = (System.Collections.IDictionary)typeof(PresentationSettingsPanel).GetField("entries", Hidden)!.GetValue(panel)!;
            var entry = entries["RecentChordLimit"]!;
            var field = (NumericValueField)entry.GetType().GetProperty("Control")!.GetValue(entry)!;
            double heightBefore = sizing.Height, widthBefore = sizing.Width, fontBefore = sizing.Style.FontSize, bottomBefore = ElementGeometry.Bounds(sizing, false).Bottom;
            field.Input.Text = "6"; edit.Window.CompletePendingEdit();
            check("increasing record count expands the layer while retaining its bottom anchor", sizing.Presentation!.RecentChordLimit == 6 && sizing.Height > heightBefore && Math.Abs(sizing.Width - widthBefore) < .01 && sizing.Style.FontSize == fontBefore && Math.Abs(ElementGeometry.Bounds(sizing, false).Bottom - bottomBefore) < .01);
        }
        CanvasElement Current() => session.Document.Elements.Single();
    }
    private static CanvasElement Layer(string family, double size, double x) => new()
    {
        Kind = ElementKind.Presentation, Width = 300, Height = 160, Transform = Affine2D.Translation(x, 80),
        Presentation = new(), Style = new() { FontFamily = family, FontSize = size, StrokeWidth = 0, Foreground = 0xFFFFFFFF, Background = 0xFF202020 }
    };
    private sealed class Fixture : IDisposable
    {
        internal InspectorWindow Window { get; }
        internal Dictionary<string, TextBox> Fields => (Dictionary<string, TextBox>)typeof(InspectorWindow).GetField("fields", Hidden)!.GetValue(Window)!;
        internal Fixture(EditorSession session, string folder)
        {
            Window = new(() => session, colorLibrary: new ColorLibrary(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json")));
            Window.PrepareForShow();
        }
        internal void Set(string key, string value)
        {
            Window.PrepareForShow(); Fields[key].Text = value;
            typeof(InspectorWindow).GetMethod("Apply", Hidden)!.Invoke(Window, null);
        }
        public void Dispose() => Window.Close();
    }
}
