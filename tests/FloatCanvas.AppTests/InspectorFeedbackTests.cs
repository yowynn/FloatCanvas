using System.IO;
using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Automation = System.Windows.Automation.AutomationProperties;
using L = FloatCanvas.Localization.AppText;

internal static class InspectorFeedbackTests
{
    internal static void Run(Action<string, bool> check)
    {
        ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-Inspector3-" + Guid.NewGuid().ToString("N"))]);
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var library = new ColorLibrary(Path.Combine(Path.GetTempPath(), "FloatCanvas-Inspector3-" + Guid.NewGuid().ToString("N"), "colors.json"));
        foreach (string language in new[] { "zh-CN", "en" })
        {
            L.Initialize(language); AppTheme.Apply("light");
            var session = new EditorSession();
            var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60 };
            session.Add(element);
            var inspector = new InspectorWindow(() => session, colorLibrary: library); inspector.PrepareForShow();
            var numeric = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields");
            var fields = Field<Dictionary<string, Controls.TextBox>>(inspector, "fields");
            check(language + " property numeric fields have both precise inputs and sliders", new[] { "X", "Y", "W", "H", "Angle", "StrokeWidth", "Opacity", "CornerRadius", "FontSize", "ArrowHeadSize" }.All(key =>
                numeric.ContainsKey(key) && ReferenceEquals(numeric[key].Input, fields[key]) && !string.IsNullOrWhiteSpace(Automation.GetName(numeric[key].Slider))));
            int history = session.UndoCount;
            numeric["Opacity"].Slider.Value = 42;
            numeric["StrokeWidth"].Slider.Value = 8;
            numeric["X"].Input.Text = "32";
            check(language + " sliders and text immediately preview valid changes", numeric["Opacity"].Input.Text == "42" && numeric["X"].Slider.Value == 32 &&
                session.Document.Elements.Single().Style.Opacity == .42 && session.Document.Elements.Single().Style.StrokeWidth == 8 && session.UndoCount == history);
            check(language + " live property panel removes confirmation and refresh buttons", !Descendants(inspector).OfType<Controls.Button>().Any(b => Automation.GetName(b) == L.Get("Inspector.Apply") || Automation.GetName(b) == L.Get("Inspector.ReloadSelection")));
            inspector.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(inspector, []);
            var edited = session.Document.Elements.Single();
            Console.WriteLine($"Inspector applied: stroke={edited.Style.StrokeWidth}, opacity={edited.Style.Opacity}, x={ElementGeometry.Bounds(edited).Left}, undo={session.UndoCount}/{history}; status={Field<Controls.TextBlock>(inspector, "status").Text}");
            check(language + " one apply commits slider and typed values together", edited.Style.StrokeWidth == 8 && Math.Abs(edited.Style.Opacity - .42) < 1e-9 &&
                Math.Abs(ElementGeometry.Bounds(edited).Left - 32) < 1e-9 && session.UndoCount == history + 1);
            check(language + " property draft commit remains one undo", session.Undo() && session.Document.Elements.Single().Style.StrokeWidth == 4 && session.Document.Elements.Single().Style.Opacity == 1);
            inspector.Close();
        }
        L.Initialize("zh-CN");
        InspectorTests.Run(check);
        InspectorColorTests.Run(check);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>()) foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
