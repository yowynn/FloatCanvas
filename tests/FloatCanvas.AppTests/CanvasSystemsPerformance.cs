using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CanvasSystemsPerformance
{
    private sealed record Sample(double Ms, long Bytes);
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        var results = new List<object>();
        var definition = new PrefabDefinition { Graph = new() { Elements = [new() { Width = 20, Height = 20 }] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id };
        var host = new EditorSession(new() { Elements = Enumerable.Range(0, 10000).Select(_ => new CanvasElement { Width = 20, Height = 20 }).ToList(),
            Definitions = [definition], Instances = [instance] });
        host.Document.RootOrder = host.Document.Elements.Select(e => e.Id).Append(instance.Id).ToList();
        var draft = host.BeginPrefabEdit(definition.Id);
        var context = new PrefabEditContext(host, draft, default, EditorTool.Select) { EnteredInstanceId = instance.Id };
        var preview = new GroupCanvasScene();
        var view = preview.Capture(draft.Session, context, GroupCanvasMode.RootProject, default, host.Assets);
        results.Add(new { Count = 10000, Case = "host-preview-cache-hit", Samples = Measure(() =>
        {
            if (!ReferenceEquals(view, preview.Capture(draft.Session, context, GroupCanvasMode.RootProject, default, host.Assets)))
                throw new Exception("Unchanged host preview rebuilt");
        }) });
        foreach (int count in new[] { 500, 1000, 2000 })
        {
            var session = new EditorSession(new() { Elements = Enumerable.Range(0, count).Select(_ => new CanvasElement { Width = 10, Height = 10 }).ToList() });
            session.SetSelection(session.Document.Elements.Select(e => e.Id));
            var layers = new LayersWindow(() => session); layers.Show(); Pump();
            bool visible = true;
            results.Add(new { Count = count, Case = "batch-flags", Samples = Measure(() => { visible = !visible; session.SetNodesFlagDetailed(session.Selection, true, visible); }) });
            results.Add(new { Count = count, Case = "unchanged-selection-refresh", Samples = Measure(layers.PrepareForShow) });
            var cancelDrag = typeof(LayersWindow).GetMethod("CancelDrag", BindingFlags.NonPublic | BindingFlags.Instance)!;
            results.Add(new { Count = count, Case = "drag-start", Samples = Measure(() => { if (!layers.BeginDrag(session.Document.Elements[0].Id)) throw new Exception("Drag rejected"); cancelDrag.Invoke(layers, null); }) });
            layers.Close();
            var groups = Enumerable.Range(0, count).Select(i => new CanvasGroup { Name = "Group " + i, OrderMode = GroupOrderMode.AtomicBlock }).ToList();
            var children = groups.Select(g => new CanvasElement { GroupId = g.Id, Width = 10, Height = 10 }).ToList();
            for (int i = 0; i < count; i++) groups[i].ChildOrder = [children[i].Id];
            var moving = new CanvasElement { Width = 10, Height = 10 }; children.Add(moving);
            session = new(new() { Groups = groups, Elements = children, RootOrder = groups.Select(g => g.Id).Append(moving.Id).ToList() });
            session.SetSelection([moving.Id]);
            layers = new(() => session); layers.Show(); Pump();
            var opens = new List<Sample>(); var searches = new List<Sample>();
            for (int i = 0; i < 35; i++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var dialog = Application.Current!.Windows.OfType<Window>().Single(w => ReferenceEquals(w.Owner, layers));
                    dialog.UpdateLayout();
                    var open = new Sample(clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
                    if (i >= 5) opens.Add(open);
                    var list = Find<ListBox>(dialog).Single(); var model = (LayerMoveTargets)list.DataContext;
                    // Exercise the same model and layout as the debounce callback, without its waiting time.
                    allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
                    model.Filter("Group 9"); dialog.UpdateLayout(); model.Filter("Group"); dialog.UpdateLayout();
                    if (i >= 5) searches.Add(new(clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated));
                    if (Find<ListBoxItem>(list).Count() > 40) throw new Exception("Destination list lost virtualization");
                    dialog.Close();
                }));
                typeof(LayersWindow).GetMethod("ShowMoveDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(layers, null);
            }
            layers.Close();
            results.Add(new { Count = count, Case = "picker-open-layout", Samples = opens });
            results.Add(new { Count = count, Case = "picker-search-two-queries-layout", Samples = searches });
        }
        // Container count rather than elapsed time is the assertion for the large virtualized list.
        var large = new EditorSession(new() { Elements = Enumerable.Range(0, 50000).Select(_ => new CanvasElement { Width = 2, Height = 2 }).ToList() });
        var window = new LayersWindow(() => large); window.Show(); window.UpdateLayout(); Pump();
        int realized = Find<ListBoxItem>(window).Count(); window.Close();
        if (realized > 60) throw new Exception("50,000-layer list lost virtualization");
        File.WriteAllText(Path.Combine(output, "lists.json"), JsonSerializer.Serialize(new { Warmups = 5, Samples = 30, LargeListRealized = realized, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static List<Sample> Measure(Action action)
    {
        var samples = new List<Sample>();
        for (int i = 0; i < 35; i++)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew(); action();
            if (i >= 5) samples.Add(new(watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes));
        }
        return samples;
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    internal static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
