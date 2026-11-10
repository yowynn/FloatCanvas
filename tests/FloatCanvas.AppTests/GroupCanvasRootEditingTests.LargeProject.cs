using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;

internal static partial class GroupCanvasRootEditingTests
{
    // Optional real-project acceptance fixture. The caller supplies an isolated
    // copy; neither the project nor the user's profile is written by this test.
    internal static void RunLargeProject(Action<string, bool> check, string project, string output)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal) || PresentationDesktopAvailability.IsCurrentInputDesktop())
            throw new InvalidOperationException("Use --isolated-ui-desktop --large-layer-project <copy> <output>.");
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private test process.");
        Directory.CreateDirectory(output);
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "light", Language = "zh-CN", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var loaded = new ProjectStore().LoadAsync(project).GetAwaiter().GetResult();
        using var fixture = new Fixture(app, output, new(-24000, -23000, 3840, 2160));
        foreach (var resource in loaded.Assets.Resources) fixture.Editor.Session.Assets.Add(resource);
        fixture.Reset(loaded.Document);
        var session = fixture.Editor.Session;
        var graph = new SceneGraph(session.Document);
        var scope = session.Document.Groups.FirstOrDefault(group => group.Name.Contains("中央天文塔", StringComparison.Ordinal)) ??
            session.Document.Groups.OrderByDescending(group => graph.EnumerateChildren(group.Id).Count).First();
        var window = new LayersWindow(() => session) { Width = 550, Height = 450 };
        Set(fixture.Desktop, "_layers", window); window.Show(); window.PrepareForShow();
        var list = Field<ListBox>(window, "layers");
        using (fixture.PresentNative("initial")) { }
        var samples = new List<object>();
        int history = session.UndoCount; long revision = session.PreviewRevision;
        Measure("root-groups", null, GroupCanvasMode.RootProject, true);
        Measure("largest-root-groups", null, GroupCanvasMode.RootProject, true, largest: true);
        Measure("entered-project", scope.Id, GroupCanvasMode.RootProject, false);
        Measure("entered-independent", scope.Id, GroupCanvasMode.Temporary, false);
        check("real-project selections do not edit content or history", session.UndoCount == history && session.PreviewRevision == revision);
        check("real-project selection keeps editing enabled", fixture.Host.IsEditing);
        File.WriteAllText(Path.Combine(output, "selection-samples.json"), JsonSerializer.Serialize(new
        {
            Elements = session.Document.Elements.Count, Groups = session.Document.Groups.Count,
            Instances = session.Document.Instances.Count, Samples = samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Hide();

        void Measure(string name, Guid? parent, GroupCanvasMode mode, bool groupsOnly, bool largest = false)
        {
            session.SetEditScope(parent); fixture.Call("SetGroupCanvasMode", mode); window.PrepareForShow();
            using (fixture.PresentNative(name + "-ready")) { }
            var candidates = window.VisibleNodes.Where(row => !row.EffectiveLocked && row.EffectiveVisible && (!groupsOnly || row.IsGroup));
            if (largest) candidates = candidates.OrderByDescending(row => graph.DescendantElementIds(row.Id).Count());
            var choices = candidates.Take(5).ToArray();
            if (choices.Length < 2) throw new InvalidOperationException("This acceptance fixture requires at least two selectable layers.");
            var times = new List<double>(); var allocations = new List<long>();
            // Warm the actual ListBox -> session -> desktop callbacks -> native
            // presentation route before collecting serial measurements.
            for (int sample = 0; sample < 25; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                list.SelectedItem = choices[sample % choices.Length];
                Pump();
                watch.Stop();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                check(name + " selection reaches session", session.Selection.SetEquals([choices[sample % choices.Length].Id]));
                if (sample >= 5)
                {
                    times.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(bytes);
                    samples.Add(new { Scenario = name, Sample = sample - 5, Milliseconds = watch.Elapsed.TotalMilliseconds, AllocatedBytes = bytes });
                }
            }
            var sorted = times.Order().ToArray(); double p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            Console.WriteLine($"LARGE_PROJECT scenario={name} nodes={session.Document.Elements.Count + session.Document.Groups.Count + session.Document.Instances.Count} medianMs={sorted[sorted.Length / 2]:F3} p95Ms={p95:F3} maxMs={sorted[^1]:F3} maxAllocatedMiB={allocations.Max() / 1048576d:F3}");
            check(name + " P95 selection responds within 200ms", p95 <= 200);
            check(name + " has no heartbeat-sized selection stalls", sorted[^1] < 500);
            check(name + " preserves editing context", session.EditScopeId == parent && fixture.Host.IsEditing);
        }
    }
}
