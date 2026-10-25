using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class PanelReviewRegressionTests
{
    internal static void Run(Action<string, bool> check, string? group = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        if (group is null or "restore") RestoreVersions(check);
        if (group is null or "placeholders") PlaceholderGestures(check);
        if (group is null or "escape") ColorEscape(check);
    }

    private static void RestoreVersions(Action<string, bool> check)
    {
        var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 50 });
        var saved = session.Document.Clone(); long savedVersion = session.ContentVersion;
        session.Execute("B", doc => doc.Elements[0].Width = 200);
        session.Execute("A again", doc => doc.Elements[0].Width = 100);
        long draftVersion = session.ContentVersion; int count = session.UndoCount;
        session.RestoreDocument("Restore", saved, savedVersion);
        check("same-content restore retains its saved version and one undo step", session.ContentVersion == savedVersion && session.UndoCount == count + 1);
        check("undo same-content restore retains the previously unsaved version", session.Undo() && session.ContentVersion == draftVersion);
        check("undo to the original save point is clean", session.Undo() && session.Undo() && session.ContentVersion == savedVersion);
        check("redo same-content restore returns to the saved version", session.Redo() && session.Redo() && session.Redo() && session.ContentVersion == savedVersion);
        count = session.UndoCount;
        session.RestoreDocument("New external save point", saved);
        check("explicit restore without a known version is also undoable", session.UndoCount == count + 1 && session.ContentVersion != savedVersion && session.Undo() && session.ContentVersion == savedVersion);
    }

    private static void PlaceholderGestures(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var controller = new EditorController(new EditorSession()) { Tool = EditorTool.RoundedRectangle };
            var tools = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(folder, "tools.json")), new ColorLibrary(Path.Combine(folder, "colors.json")));
            var radius = Field<Dictionary<string, NumericValueField>>(tools, "numericFields")["CornerRadius"];
            Measure(radius, 180, 34);
            check("automatic corner radius has an explicit placeholder", controller.Parameters.CornerRadius is null && radius.Input.Text == "—");
            check("automatic corner radius can start a rail gesture", radius.BeginGesture());
            radius.UpdateRail(radius.Slider.ActualWidth / 2);
            check("automatic corner rail publishes an explicit value", controller.Parameters.CornerRadius > 0);
            radius.CancelGesture();
            check("cancelling automatic corner rail restores its automatic state", controller.Parameters.CornerRadius is null && radius.Input.Text == "—");
            check("automatic corner radius can start its icon gesture", radius.AnchorScrubber!.TryBegin(0));
            radius.AnchorScrubber.Update(60); radius.AnchorScrubber.End(true);
            check("automatic corner icon can commit a value", controller.Parameters.CornerRadius > 0);

            var session = new EditorSession();
            session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 50, Style = new() { Opacity = .5 } });
            session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 50, Style = new() { Opacity = .8 } });
            session.SelectAll(); var inspector = new InspectorWindow(() => session);
            var opacity = Field<Dictionary<string, NumericValueField>>(inspector, "numericFields")["Opacity"];
            Measure(opacity, 180, 34);
            check("mixed opacity can start its rail without typing first", opacity.Input.Text == "" && opacity.BeginGesture());
            opacity.UpdateRail(opacity.Slider.ActualWidth * .75); opacity.CancelGesture();
            check("mixed opacity rail cancellation restores individual values", session.Document.Elements.Select(e => e.Style.Opacity).SequenceEqual(new[] { .5, .8 }) && opacity.Input.Text == "");
            int undo = session.UndoCount;
            check("mixed opacity can start another rail gesture", opacity.BeginGesture());
            opacity.UpdateRail(opacity.Slider.ActualWidth / 2); opacity.EndGesture(true);
            check("clicking the representative mixed value still unifies the selection once", session.Document.Elements.All(e => e.Style.Opacity == .5) && session.UndoCount == undo + 1);
            session.Undo(); inspector.Refresh();
            check("mixed opacity icon can start without typing first", opacity.AnchorScrubber!.TryBegin(0));
            opacity.AnchorScrubber.Update(60); opacity.AnchorScrubber.End(false);
            check("mixed opacity icon cancellation restores individual values", session.Document.Elements.Select(e => e.Style.Opacity).SequenceEqual(new[] { .5, .8 }));
            var invalid = new NumericValueField(0, 100) { Value = 40 };
            invalid.ShowPlaceholder("—"); invalid.Input.Text = "invalid";
            check("invalid numeric drafts still cannot start a gesture", !invalid.BeginGesture());
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void ColorEscape(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-color-escape-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var color = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(folder, "colors.json")));
            var picker = Field<ColorPickerPanel>(color, "picker"); var root = (FrameworkElement)color.PickerRoot;
            Measure(root, 660, 500);
            var opacity = Field<NumericValueField>(picker, "alphaField"); opacity.Value = 50;
            check("color opacity starts an embedded rail gesture", opacity.BeginGesture());
            opacity.UpdateRail(opacity.Slider.ActualWidth * .9);
            var escape = Escape(opacity.Slider, new DetachedSource()); opacity.Slider.RaiseEvent(escape);
            check("color parent lets Escape cancel the numeric gesture first", escape.Handled && !opacity.IsInteracting && opacity.Value == 50);

            var owner = new Border { Child = new Button() };
            using var source = new HwndSource(new HwndSourceParameters("FloatCanvas numeric Escape") { Width = 500, Height = 200, WindowStyle = unchecked((int)0x80000000) });
            source.RootVisual = owner; Measure(owner, 500, 200);
            using var scope = new OverlayWindowScope(source.Handle, () => false);
            var numeric = new NumericValueField(0, 100) { Value = 40 }; int closed = 0;
            using var popup = new OverlayPopupWindow(numeric, owner, PlacementMode.Bottom, 0, 0, () => closed++);
            Measure(numeric, 180, 34); numeric.BeginGesture(); numeric.UpdateRail(100);
            InputManager.Current.ProcessInput(Escape(numeric.Slider, source));
            check("enhanced popup Escape cancels an active numeric gesture before dismissal", closed == 0 && !numeric.IsInteracting && numeric.Value == 40);
            InputManager.Current.ProcessInput(Escape(numeric.Slider, source));
            check("enhanced popup Escape dismisses normally after numeric cancellation", closed == 1);
            source.RootVisual = null;
        }
        finally { Directory.Delete(folder, true); }
    }

    private static KeyEventArgs Escape(UIElement source, PresentationSource presentation) => new(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = source };
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Measure(FrameworkElement element, double width, double height)
    { element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); }
    private sealed class DetachedSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
