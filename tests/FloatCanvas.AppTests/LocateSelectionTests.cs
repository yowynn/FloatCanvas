using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class LocateSelectionTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                string prefix = locale + " " + theme + " locate: ";
                VerifyCrossLevelCycle(check, prefix);
                var elements = Enumerable.Range(0, 300).Select(i => new CanvasElement { Name = "Layer " + i, Kind = ElementKind.Rectangle, Width = 10, Height = 10 }).ToArray();
                var session = new EditorSession(new() { Elements = [.. elements] }); bool allowed = true;
                var window = new LayersWindow(() => session, () => allowed);
                using var host = new GroupCanvasUiTests.NativeHost(window, 280, 350);
                window.PrepareForShow(); host.Layout();
                var button = (Button)typeof(LayersWindow).GetField("locateSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                check(prefix + "hidden without selection", button.Visibility == Visibility.Collapsed);
                var ids = new[] { elements[5].Id, elements[140].Id, elements[295].Id };
                session.SetSelection(ids); window.PrepareForShow(); host.Layout();
                Point captionPosition = button.TransformToAncestor(host.Root).Transform(new Point());
                int undo = session.UndoCount; long revision = session.SceneRevision;
                var expected = window.VisibleNodes.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToArray();
                for (int i = 0; i < 7; i++)
                {
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
                    check(prefix + "cycle scrolls virtualized row " + i, IsVisible(expected[i % expected.Length]));
                    check(prefix + "keeps selection and history " + i, session.Selection.SetEquals(ids) && session.UndoCount == undo && session.SceneRevision == revision);
                }
                var titleActions = (StackPanel)window.Resources["CanvasWindowActions"];
                check(prefix + "locate occupies the caption slot immediately before mode", ReferenceEquals(button.Parent, titleActions) &&
                    titleActions.Children.Count == 2 && titleActions.Children[0] == button && titleActions.Children[1] is CanvasModeSwitch);
                allowed = false; window.PrepareForShow(); check(prefix + "busy guard disables locate", !button.IsEnabled);
                allowed = true; session.SetSelection([elements[20].Id]); window.PrepareForShow(); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
                check(prefix + "selection change resets cycle", IsVisible(elements[20].Id));
                session.SetSelection([]); window.PrepareForShow(); check(prefix + "clearing selection hides action", button.Visibility == Visibility.Collapsed);

                session.SetSelection([elements[3].Id]); Guid group = session.GroupSelection("Parent")!.Value;
                session.SetSelection([elements[3].Id, elements[0].Id]); window.PrepareForShow();
                var selection = session.Selection.ToArray(); undo = session.UndoCount;
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
                check(prefix + "descendant enters parent without losing mixed selection", session.EditScopeId == group && IsVisible(elements[3].Id) && session.Selection.SetEquals(selection));
                check(prefix + "entering a group keeps the caption locate slot fixed", button.TransformToAncestor(host.Root).Transform(new Point()) == captionPosition &&
                    button.Width == 28 && button.Height == 28 && ReferenceEquals(button.Style, window.FindResource("CanvasCaptionButton")));
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
                check(prefix + "cycle can return to root selection", session.EditScopeId is null && IsVisible(elements[0].Id) && session.Selection.SetEquals(selection) && session.UndoCount == undo);
                session.SetNodeLocked(group, true); session.SetSelection([elements[3].Id]); window.PrepareForShow(); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
                check(prefix + "locked group remains browsable", session.EditScopeId == group && IsVisible(elements[3].Id));
                window.SetGroupContext(new("Parent", GroupStorageKind.Ordinary, null, GroupCanvasMode.RootProject, true, Temporary: () => { }, Export: () => { }));
                host.Layout();
                if (output is not null)
                {
                    System.IO.Directory.CreateDirectory(output);
                    foreach (double scale in new[] { 1d, 1.5, 2d })
                    {
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(280 * scale), (int)(350 * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(host.Root);
                        using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"locate-{locale}-{theme}-{scale}.png"));
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(stream);
                    }
                }

                bool IsVisible(Guid id)
                {
                    var container = window.Container(id);
                    if (container is null || PresentationSource.FromVisual(container) is null) return false;
                    var list = (ListBox)typeof(LayersWindow).GetField("layers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var bounds = container.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
                    return bounds.Top >= -1 && bounds.Bottom <= list.ActualHeight + 1;
                }
            }
        }
        finally { L.Initialize(language); }
    }

    private static void VerifyCrossLevelCycle(Action<string, bool> check, string prefix)
    {
        var a = new CanvasGroup { Name = "A", OrderMode = GroupOrderMode.AtomicBlock };
        var inner = new CanvasGroup { Name = "Inner", ParentId = a.Id, OrderMode = GroupOrderMode.AtomicBlock };
        var b = new CanvasGroup { Name = "B", OrderMode = GroupOrderMode.AtomicBlock };
        CanvasElement Layer(string name, int x, Guid? parent = null) => new()
        {
            Name = name, Kind = ElementKind.Rectangle, Width = 10, Height = 10,
            Transform = Affine2D.Translation(x, 10), GroupId = parent,
            Style = new() { Fill = 0xFF408020, StrokeWidth = 0 }
        };
        var root = Layer("Root", 100);
        var aLow = Layer("A low", 200, a.Id);
        var deep = Layer("Deep", 300, inner.Id);
        var aHigh = Layer("A high", 400, a.Id);
        var bLow = Layer("B low", 500, b.Id);
        var bHigh = Layer("B high", 600, b.Id);
        a.ChildOrder = [aLow.Id, inner.Id, aHigh.Id];
        inner.ChildOrder = [deep.Id]; b.ChildOrder = [bLow.Id, bHigh.Id];
        var session = new EditorSession(new()
        {
            Elements = [root, aLow, deep, aHigh, bLow, bHigh], Groups = [a, inner, b],
            RootOrder = [root.Id, b.Id, a.Id]
        });
        var window = new LayersWindow(() => session);
        using var host = new GroupCanvasUiTests.NativeHost(window, 280, 350);
        window.PrepareForShow(); host.Layout();
        var button = (Button)typeof(LayersWindow).GetField("locateSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var list = (ListBox)typeof(LayersWindow).GetField("layers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        foreach (int x in new[] { 300, 100, 500, 400, 600 })
        {
            var position = new FloatCanvas.Windows.OverlayPoint(x + 5, 15);
            const FloatCanvas.Windows.OverlayModifiers modifiers = FloatCanvas.Windows.OverlayModifiers.Control | FloatCanvas.Windows.OverlayModifiers.Shift;
            editor.Pointer(new(FloatCanvas.Windows.OverlayPointerKind.Down, position, FloatCanvas.Windows.OverlayMouseButton.Left, modifiers));
            editor.Pointer(new(FloatCanvas.Windows.OverlayPointerKind.Up, position, FloatCanvas.Windows.OverlayMouseButton.Left, modifiers));
        }
        Guid[] expected = [aHigh.Id, deep.Id, bHigh.Id, bLow.Id, root.Id];
        Guid?[] scopes = [a.Id, inner.Id, b.Id, b.Id, null];
        check(prefix + "individual additive hits retain all levels without navigation", session.EditScopeId is null && session.Selection.SetEquals(expected));
        editor.SelectContextTarget(new(305, 15));
        check(prefix + "right-clicking a selected leaf keeps the exact cross-level selection", session.Selection.SetEquals(expected));
        int undo = session.UndoCount; long revision = session.SceneRevision;
        // Production refreshes synchronously during SetEditScope and SetSelection.
        // The temporary empty selection must not reset the locate cycle.
        EventHandler refresh = (_, _) => window.PrepareForShow();
        session.Changed += refresh;
        try
        {
            window.PrepareForShow(); host.Layout();
            for (int i = 0; i < expected.Length * 2; i++)
            {
                int index = i % expected.Length;
                long scopeVersion = session.EditScopeVersion;
                Guid? previousScope = session.EditScopeId;
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
                var container = window.Container(expected[index]);
                bool visible = container is not null && PresentationSource.FromVisual(container) is not null;
                if (visible)
                {
                    var bounds = container!.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
                    visible = bounds.Top >= -1 && bounds.Bottom <= list.ActualHeight + 1;
                }
                check(prefix + "cross-level depth-first cycle " + i, session.EditScopeId == scopes[index] && visible);
                check(prefix + "cross-level cycle preserves full selection and history " + i,
                    session.Selection.SetEquals(expected) && session.UndoCount == undo && session.SceneRevision == revision);
                if (previousScope == scopes[index])
                    check(prefix + "same-parent locate only scrolls " + i, session.EditScopeVersion == scopeVersion);
            }
            session.SetSelection(expected.Where(id => id != aHigh.Id));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); host.Layout();
            check(prefix + "cross-level selection change restarts from first remaining layer",
                session.EditScopeId == inner.Id && window.Container(deep.Id) is not null &&
                session.Selection.SetEquals(expected.Where(id => id != aHigh.Id)));
        }
        finally { session.Changed -= refresh; }
    }
}
