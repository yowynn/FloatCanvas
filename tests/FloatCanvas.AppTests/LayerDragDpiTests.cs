using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Replay Thumb's screen-pixel completion contract in a hidden native Layers host.</summary>
internal static class LayerDragDpiTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --layer-drag-dpi-only in a fresh process.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var session = new EditorSession();
        var window = new LayersWindow(() => session);
        var content = (FrameworkElement)window.Content; window.Content = null;
        var root = new Border { Width = 320, Height = 520, Child = content };
        try
        {
            // No AdornerDecorator: target resolution still runs, while no drag ghost is shown.
            using var source = new HwndSource(new HwndSourceParameters("FloatCanvas layer drag DPI regression")
            { Width = 320, Height = 520, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 });
            source.RootVisual = root;
            try
            {
                Layout(root);
                var list = Field<ListBox>(window, "layers");
                var dpi = VisualTreeHelper.GetDpi(list);
                Console.WriteLine($"LAYER_DRAG_NATIVE_DPI {dpi.DpiScaleX},{dpi.DpiScaleY}");
                check("layer drag regression uses a hidden native presentation source", !IsWindowVisible(source.Handle) &&
                    !window.IsVisible && PresentationSource.FromVisual(list) == source);

                RunCase("first layer between third and fourth", [1], 1, 3, [2, 3, 1, 4]);
                RunCase("last layer moves upward into the first gap", [4], 4, 1, [1, 4, 2, 3]);
                RunCase("a non-first layer moves downward", [2], 2, 3, [1, 3, 2, 4]);
                RunCase("dragging an unselected group member retains group order", [1, 2], 1, 3, [3, 1, 2, 4], grouped: true);
                RunCase("disjoint selected layers retain their relative order", [1, 3], 1, 4, [2, 4, 1, 3]);
                RunCase("canceled completion retains the document", [1], 1, 3, [1, 2, 3, 4], canceled: true);
                RunCase("dropping into the existing slot adds no history", [1], 1, 1, [1, 2, 3, 4]);

                void RunCase(string name, int[] selected, int moving, int gap, int[] expected, bool grouped = false, bool canceled = false)
                {
                    var elements = Enumerable.Range(1, 4).Select(number => new CanvasElement
                    { Name = number.ToString(), Kind = ElementKind.Rectangle, Width = 30, Height = 30 }).ToArray();
                    session = new EditorSession(new SceneDocument { Elements = elements.Reverse().ToList() });
                    var selectedIds = selected.Select(number => elements[number - 1].Id).ToArray();
                    session.SetSelection(selectedIds);
                    Guid? group = grouped ? session.GroupSelection("Drag group") : null;
                    if (grouped) session.SetSelection([]);
                    window.Refresh(); Layout(root);
                    var rows = list.Items.Cast<ListBoxItem>().ToArray();
                    var item = rows.Single(row => (Guid)row.Tag == elements[moving - 1].Id);
                    var handle = ((Grid)item.Content).Children.OfType<Thumb>().Single();
                    var start = handle.TranslatePoint(new Point(12, 18), list);
                    var screenStart = list.PointToScreen(start);
                    double y = gap == rows.Length ? rows[^1].TranslatePoint(new Point(0, rows[^1].ActualHeight - 2), list).Y :
                        (rows[gap - 1].TranslatePoint(new Point(0, rows[gap - 1].ActualHeight), list).Y +
                         rows[gap].TranslatePoint(new Point(), list).Y) / 2;
                    var target = new Point(start.X, y);
                    var physicalChange = list.PointToScreen(target) - screenStart;
                    var logicalChange = target - start;
                    int undo = session.UndoCount;
                    handle.RaiseEvent(new DragStartedEventArgs(12, 18) { RoutedEvent = Thumb.DragStartedEvent });
                    Invoke(window, "UpdateDropTarget", target);
                    check(name + " uses physical screen delta at the actual native DPI", Math.Abs(physicalChange.Y - logicalChange.Y * dpi.DpiScaleY) <= 1);
                    if (moving == 1 && gap == 3 && !grouped)
                        check(name + " hovers before the fourth layer", Field<Guid?>(window, "dropTarget") == elements[3].Id && Field<bool>(window, "dropBefore"));
                    check(name + " resolves the drag without a visible ghost", Field<TileDragFeedback?>(window, "dragFeedback") is null &&
                        !app.Windows.Cast<Window>().Any(candidate => candidate.IsVisible));
                    handle.RaiseEvent(new DragCompletedEventArgs(physicalChange.X, physicalChange.Y, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
                    Layout(root);
                    var actual = session.Document.Elements.AsEnumerable().Reverse().Select(element => element.Name).ToArray();
                    bool changed = !expected.SequenceEqual([1, 2, 3, 4]);
                    check(name + " commits exactly the displayed target", actual.SequenceEqual(expected.Select(number => number.ToString())) &&
                        session.UndoCount == undo + (changed ? 1 : 0) && session.Selection.SetEquals(selectedIds));
                    if (group is { } groupId)
                        check(name + " preserves group identity", session.Document.Groups.Single().Id == groupId &&
                            session.Document.GroupElements(groupId).Select(element => element.Id).ToHashSet().SetEquals(selectedIds));
                    if (changed)
                    {
                        session.Undo(); window.Refresh(); Layout(root);
                        check(name + " undoes as one operation", session.Document.Elements.AsEnumerable().Reverse().Select(element => element.Name).SequenceEqual(["1", "2", "3", "4"]) && session.UndoCount == undo);
                        session.Redo(); window.Refresh(); Layout(root);
                        check(name + " redoes the same order", session.Document.Elements.AsEnumerable().Reverse().Select(element => element.Name).SequenceEqual(actual) && session.UndoCount == undo + 1);
                    }
                }
            }
            finally { Invoke(window, "CancelDrag"); root.Child = null; source.RootVisual = null; }
        }
        finally { window.Content = content; window.Close(); app.Shutdown(); }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(320, 520)); root.Arrange(new Rect(0, 0, 320, 520)); root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
