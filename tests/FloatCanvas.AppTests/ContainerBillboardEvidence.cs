using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class ContainerBillboardEvidence
{
    internal static void Ui(Action<string, bool> check, string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string languageBefore = L.Preference;
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(language); AppTheme.Apply(theme);
            var element = new CanvasElement { Width = 100, Height = 80 };
            var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [element.Id] }; element.GroupId = group.Id;
            var session = new EditorSession(new() { Elements = [element], Groups = [group], RootOrder = [group.Id] });
            session.SetSelection([group.Id]); session.SetContainerBillboard(group.Id, true);
            var panel = new NodeTransformPanel(() => session, () => true); panel.Refresh();
            var surface = new Border { Padding = new Thickness(12), Width = 420, Child = panel };
            surface.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
            surface.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
            surface.Measure(new Size(420, double.PositiveInfinity)); surface.Arrange(new Rect(surface.DesiredSize)); surface.UpdateLayout();
            var fields = (Dictionary<string, NumericValueField>)typeof(NodeTransformPanel).GetField("fields", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
            check(language + " " + theme + " card height uses ordinary property metrics", Math.Abs(fields["CardHeight"].ActualHeight - fields["X"].ActualHeight) < .1);
            foreach (double dpi in new[] { 1d, 1.5, 2d })
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * dpi), (int)Math.Ceiling(surface.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"card-properties-{language}-{theme}-{dpi}.png")); encoder.Save(file);
            }
        }
        L.Initialize(languageBefore);
    }

    internal static void Performance(Action<string, bool> check, string output)
    {
        Directory.CreateDirectory(output); var rows = new List<object>();
        foreach (var (leaves, copies, depth) in new[] { (1000, 10, 1), (500, 20, 32), (2000, 5, 60) })
        {
            var graph = new SceneNodeGraph(); Guid? parent = null;
            for (int i = 0; i < depth; i++)
            {
                var group = new CanvasGroup { ParentId = parent, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
                if (parent is { } id) graph.Groups.Last().ChildOrder!.Add(group.Id); else graph.RootOrder = [group.Id];
                graph.Groups.Add(group); parent = group.Id;
            }
            for (int i = 0; i < leaves; i++)
            {
                var element = new CanvasElement { Width = 8, Height = 8, GroupId = parent, Transform = Affine2D.Translation(i % 100 * 10, i / 100 * 10) };
                graph.Elements.Add(element); graph.Groups.Last().ChildOrder!.Add(element.Id);
            }
            var definition = new PrefabDefinition { Graph = graph };
            var doc = new SceneDocument { Definitions = [definition] };
            for (int i = 0; i < copies; i++) doc.Instances.Add(new() { DefinitionId = definition.Id, Billboard = new(new(500, 50), 50), Transform = Affine2D.Translation(i * 25, 300) });
            doc.RootOrder = doc.Instances.Select(item => item.Id).ToList();
            var session = new EditorSession(doc); var freezer = new CanvasPreviewScene();
            SceneDocument Capture()
            {
                bool precise = session.TryGetPreviewChanges(freezer.CapturedRevision, out var elements, out var containers);
                return freezer.Capture(doc, session.PreviewRevision, session.PreviewElementsRevision, session.TransactionId, -1, -1, true,
                    precise ? elements : null, definitionsRevision: session.PreviewDefinitionsRevision, changedContainers: precise ? containers : null);
            }
            var initial = Capture(); freezer.EvaluateFrozen(initial);
            var hit = Sample(() => freezer.EvaluateFrozen(initial), 100);
            long firstBuilds = freezer.DefinitionBuildCount;
            long firstSourceBuilds = freezer.FullBuildCount, firstClones = freezer.ClonedElementCount, firstWorldUpdates = freezer.EvaluationBuildCount;
            var moved = Sample(() => { doc.Instances[0].Transform = doc.Instances[0].Transform with { OffsetX = doc.Instances[0].Transform.OffsetX + 1 }; session.NotifyContainerPlacementChanged([doc.Instances[0].Id]); freezer.EvaluateFrozen(Capture()); }, 30);
            var moveBuilds = new { SharedDefinitionRebuilds = freezer.DefinitionBuildCount - firstBuilds,
                FullSourceRebuilds = freezer.FullBuildCount - firstSourceBuilds, HostElementClones = freezer.ClonedElementCount - firstClones,
                WorldUpdates = freezer.EvaluationBuildCount - firstWorldUpdates, IncludesWarmups = true };
            check($"{leaves}x{copies} depth{depth}: moving one instance does not rebuild shared geometry", freezer.DefinitionBuildCount == firstBuilds);
            session.SetSelection([doc.Instances[0].Id]); session.BeginTransaction("Gesture performance");
            var original = session.CaptureSelectionTransform(); int step = 0;
            var gesture = Sample(() => { if (!session.TryApplySelectionTransform(original, Affine2D.Translation(++step, 0))) throw new Exception("Unsafe benchmark gesture"); freezer.EvaluateFrozen(Capture()); }, 30);
            session.Cancel(); doc = session.Document;
            var uncached = Sample(() => { doc.Instances[0].Transform = doc.Instances[0].Transform with { OffsetX = doc.Instances[0].Transform.OffsetX + 1 }; session.NotifyChanged(); freezer.EvaluateFrozen(Capture()); }, 30);
            rows.Add(new { Leaves = leaves, Instances = copies, Depth = depth, CacheHit = hit, InstanceMove = moved, Gesture = gesture,
                InstanceMoveBuilds = moveBuilds, ConservativeInvalidation = uncached });
            Console.WriteLine("CONTAINER_PERF " + JsonSerializer.Serialize(rows.Last()));
        }
        File.WriteAllText(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(new { Method = "Release; 5 warmups, at least 30 serial samples; allocation is cumulative per operation, not peak memory. Conservative invalidation is a same-build comparison, not a historical binary.", Rows = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object Sample(Action action, int count)
    {
        for (int i = 0; i < 5; i++) action();
        var samples = new List<double>(); var allocations = new List<long>(); long allocated = 0;
        for (int i = 0; i < count; i++)
        {
            long startBytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            action(); double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes; allocated += bytes; allocations.Add(bytes); samples.Add(elapsed);
        }
        var sorted = samples.Order().ToArray(); return new { MedianMs = sorted[count / 2], P95Ms = sorted[(int)Math.Ceiling(count * .95) - 1], BytesPerOperation = allocated / count, MaxBytes = allocations.Max(), Samples = samples, Allocations = allocations };
    }
}
