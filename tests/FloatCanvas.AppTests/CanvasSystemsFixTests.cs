using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

internal static class CanvasSystemsFixTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var group = new CanvasGroup { Name = "Parent", OrderMode = GroupOrderMode.AtomicBlock };
        var child = new CanvasElement { GroupId = group.Id, Width = 20, Height = 20 };
        var other = new CanvasElement { Width = 20, Height = 20 };
        group.ChildOrder = [child.Id];
        var session = new EditorSession(new() { Groups = [group], Elements = [child, other], RootOrder = [group.Id, other.Id] });
        session.SetSelection([child.Id, other.Id]);
        var window = new LayersWindow(() => session); window.PrepareForShow();
        var menu = window.CreateContextMenu(other.Id);
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "Layers.HideSelected")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check("cross-level menu hides the entire selection and records one undo", session.Document.Elements.All(e => !e.Visible) && session.UndoCount == 1);
        session.Undo();
        group = session.Document.Groups.Single(); group.Locked = true; session.NotifyChanged(); session.SetSelection([group.Id]);
        var panel = new NodeTransformPanel(() => session, () => true); panel.Refresh();
        var enter = panel.Children.OfType<Button>().Single(b => Equals(b.Content, L.Get("GroupTransform.Enter")));
        check("inspector allows entering a locked group for browsing", enter.IsEnabled);
        enter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("inspector entry retains locked content protection", session.EditScopeId == group.Id && !session.CanInsertInEditScope);
        window.Close();
        Targets(check); Calibration(check); Rectification(check); Incremental(check);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed.TotalSeconds < 10) { Pump(); Thread.Sleep(1); }
        if (!ready()) throw new Exception("Timed out waiting for UI candidate validation");
    }
    private static void Targets(Action<string, bool> check)
    {
        var groups = Enumerable.Range(0, 1000).Select(i => new CanvasGroup { Name = "Group " + i, OrderMode = GroupOrderMode.AtomicBlock }).ToList();
        var leaves = groups.Select(g => new CanvasElement { GroupId = g.Id, Width = 10, Height = 10 }).ToList();
        for (int i = 0; i < groups.Count; i++) groups[i].ChildOrder = [leaves[i].Id];
        var moving = new CanvasElement { Width = 10, Height = 10 }; leaves.Add(moving);
        var session = new EditorSession(new() { Groups = groups, Elements = leaves, RootOrder = groups.Select(g => g.Id).Append(moving.Id).ToList() });
        session.SetSelection([moving.Id]);
        var model = new LayerMoveTargets(session, session.AnalyzeLayerOperation(LayerOperation.Move), false);
        var list = model.CreateList(); var window = new Window { Content = list, Width = 420, Height = 480 };
        window.Show(); Pump(); window.UpdateLayout();
        check("destination list realizes only visible recycled containers", model.Visible.Count == 1003 && Descendants<ListBoxItem>(list).Count() < 40);
        var expanders = Descendants<LayerMoveTargetPresenter>(list).SelectMany(p => Descendants<Button>(p)).ToArray();
        check("destination expanders fit their rows and leaf rows reserve an empty slot", expanders.All(b => b.ActualHeight <= 22) &&
            expanders.Any(b => b.Visibility == Visibility.Hidden));
        var item = Descendants<ListBoxItem>(list).First();
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(item);
        check("destination item automation exposes full path and hierarchy help", peer.GetName() == ((LayerMoveTargets.Row)item.DataContext).Path &&
            peer.GetHelpText() == ((LayerMoveTargets.Row)item.DataContext).DepthHint);
        var first = model.Visible[1];
        model.Filter("Group 999"); window.UpdateLayout();
        check("destination search retains necessary root and matching group", model.Visible.Count == 2 && model.Visible.Last().Id == groups.Last().Id);
        model.Filter(""); window.UpdateLayout();
        check("clearing destination search restores stable row identities", model.Visible.Count == 1003 && ReferenceEquals(first, model.Visible[1]));
        window.Close();
        for (int i = 1; i < 60; i++) { groups[i].ParentId = groups[i - 1].Id; groups[i - 1].ChildOrder!.Add(groups[i].Id); }
        session.Document.RootOrder = groups.Where(g => g.ParentId is null).Select(g => g.Id).Append(moving.Id).ToList();
        session.NotifyChanged();
        model = new(session, session.AnalyzeLayerOperation(LayerOperation.Move), false);
        model.Filter("Group 59");
        check("deep destination search retains its complete ancestor path", model.Visible.Single(r => r.Id == groups[59].Id).Depth == 60 &&
            groups.Take(60).All(g => model.Visible.Any(r => r.Id == g.Id)));
    }
    private static void Calibration(Action<string, bool> check)
    {
        var owner = new Window { Width = 100, Height = 100 }; owner.Show();
        var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 800, 600)) };
        settings.Projection.Enabled = true;
        var requests = new List<TaskCompletionSource<CanvasCalibrationCheck>>();
        CanvasSettings? preview = null; bool reject = true;
        var draft = new CanvasGridCalibrationDraft();
        var window = new CanvasGridCalibrationWindow(owner, settings, new(.1, .1, .2, .25), draft, value => preview = value,
            (_, _) => { var pending = new TaskCompletionSource<CanvasCalibrationCheck>(); requests.Add(pending); return pending.Task; },
            _ => reject ? "busy" : null);
        window.Show(); Pump();
        var columns = Descendants<NumericValueField>(window).First();
        columns.Input.Text = "4"; Pump();
        requests[0].SetResult(new(0)); Pump();
        check("older candidate validation cannot publish a stale preview", preview is null && window.State == CanvasGridCalibrationWindow.CandidateState.Checking);
        requests.Last().SetResult(new(2)); Pump();
        var apply = Descendants<Button>(window).Single(b => Equals(b.Content, L.Get("Canvas.ApplyPlane")));
        check("unsafe candidate retains sample inputs and disables apply", !apply.IsEnabled && draft.Columns == "4" && preview is null && window.IsVisible);
        columns.Input.Text = "5"; Pump(); requests.Last().SetResult(new(0)); Pump();
        check("safe current candidate enables preview and apply", preview is not null && apply.IsEnabled);
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("rejected final commit retains dialog parameters", window.IsVisible && window.Result is null && draft.Columns == "5");
        reject = false; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("only successful final commit closes calibration", !window.IsVisible && window.Result is not null);
        owner.Close();
        var initial = CanvasCalibration.CreateInitial(new(0, 0, 800, 600));
        CanvasCalibration.TryCalibrate(initial, [new(100, 100), new(400, 100), new(550, 500), new(0, 500)], new(0, 0, 800, 600), out var calibrated, out _);
        settings = new() { Projection = calibrated! }; settings.Projection.Enabled = true;
        bool fit = CanvasGridCalibration.TryFitRectangle(settings, new(.1, .1, 4d / 30, .5), 4, 5, true, null, null, out var candidate, out _);
        bool unsafeFound = false;
        foreach (double y in new[] { -1000d, 0, 100, 400, 600, 800, 1000, 1200, 2000 })
        {
            var element = new CanvasElement { Name = "Existing tower", Width = 100, Height = 80, Transform = Affine2D.Translation(100, y) };
            var document = new SceneDocument { Canvas = settings, Viewport = new(0, 0, 800, 600), Elements = [element] };
            if (!CanvasMapping.Create(document).IsElementSafe(element)) continue;
            var safety = new CanvasCalibrationSafety(document.Clone());
            if (fit && safety.Check(candidate!.Settings).GetAwaiter().GetResult() is { AffectedCount: > 0 } result)
            {
                unsafeFound = true;
                check("unsafe fit identifies the existing object and its original visible bounds " + y,
                    result.Issues.Count == 1 && result.Issues[0].Id == element.Id && result.Issues[0].Name == "Existing tower" &&
                    result.Issues[0].OriginalBounds is { Width: > 0, Height: > 0 });
                var definition = new PrefabDefinition { Name = "Library tower", Graph = new() { Elements = [new() { Width = 100, Height = 80 }] } };
                var instance = new PrefabInstance { DefinitionId = definition.Id, Transform = element.Transform };
                var shared = new SceneDocument { Canvas = settings, Viewport = document.Viewport, Definitions = [definition], Instances = [instance], RootOrder = [instance.Id] };
                var sharedResult = new CanvasCalibrationSafety(shared.Clone()).Check(candidate.Settings).GetAwaiter().GetResult();
                check("unsafe shared instance uses the same definition-name fallback as the layers panel " + y,
                    sharedResult.Issues.Count == 1 && sharedResult.Issues[0].Id == instance.Id && sharedResult.Issues[0].Name == "Library tower");
            }
        }
        check("4 by 5 fit is checked against actual existing content before preview", fit && unsafeFound);
    }
    private static void Rectification(Action<string, bool> check)
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(320, 240)); surface.Canvas.Clear(SkiaSharp.SKColors.Blue);
        using var image = surface.Snapshot(); using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var projection = CanvasCalibration.CreateInitial(new(0, 0, 320, 240)); projection.Enabled = true;
        var window = new CanvasRectificationPreviewWindow(null, new(png.ToArray(), new(0, 0, 320, 240), true), projection);
        window.Show(); Pump();
        var combo = Descendants<ComboBox>(window).Single();
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => i.Tag is CanvasAspectPreset { Key: "Canvas.AspectA4", Aspect: < 1 });
        var swap = Descendants<Button>(window).Single(b => Equals(b.Content, L.Get("Canvas.SwapOrientation")));
        swap.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        double aspect = ((CanvasAspectPreset)((ComboBoxItem)combo.SelectedItem).Tag).Aspect!.Value;
        var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(window.Projection);
        check("rectification ratio label tracks landscape swap", aspect > 1 && Math.Abs(aspect - bounds.Width / bounds.Height) < 1e-4);
        swap.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("rectification second swap restores portrait label", ((CanvasAspectPreset)((ComboBoxItem)combo.SelectedItem).Tag).Aspect < 1);
        window.Close();
    }
    private static void Incremental(Action<string, bool> check)
    {
        var element = new CanvasElement { Width = 30, Height = 40 };
        var definition = new PrefabDefinition { Graph = new() { Elements = [element] } };
        var doc = new SceneDocument { Definitions = [definition], Instances = Enumerable.Range(0, 10).Select(i => new PrefabInstance { DefinitionId = definition.Id,
            Billboard = new(new(15, 20), 20), Transform = Affine2D.Translation(i * 40, 100) }).ToList() };
        doc.Viewport = new(0, 0, 480, 300); doc.Canvas.Projection = CanvasCalibration.CreateInitial(doc.Viewport); doc.Canvas.Projection.Enabled = true;
        doc.RootOrder = doc.Instances.Select(i => i.Id).ToList();
        var session = new EditorSession(doc); var freezer = new CanvasPreviewScene();
        SceneDocument Capture()
        {
            session.TryGetPreviewChanges(freezer.CapturedRevision, out var elements, out var containers);
            return freezer.Capture(doc, session.PreviewRevision, session.PreviewElementsRevision, session.TransactionId, -1, -1, true,
                freezer.CapturedRevision < 0 ? null : elements, definitionsRevision: session.PreviewDefinitionsRevision, changedContainers: containers);
        }
        var original = freezer.EvaluateFrozen(Capture());
        for (int i = 0; i < 4; i++)
        {
            var instance = doc.Instances[i]; instance.Transform = instance.Transform with { OffsetY = 120 + i };
            session.NotifyContainerPlacementChanged([instance.Id]);
            var next = freezer.EvaluateFrozen(Capture()); var full = SceneEvaluation.Build(doc);
            check("incremental placement matches complete world evaluation " + i,
                JsonSerializer.Serialize(next.RenderDocument) == JsonSerializer.Serialize(full.RenderDocument));
            check("unchanged shared instance retains immutable render geometry " + i, ReferenceEquals(original.Leaves[9], next.Leaves[9]));
            check("incremental occurrence maps use current geometry " + i, ReferenceEquals(next.Leaves[i], next.FindOccurrence(next.Leaves[i].RuntimeElementId)) &&
                ReferenceEquals(next.Leaves[i], next.OccurrencesOfHostNode(instance.Id).Single()));
            using var renderer = new FloatCanvas.Rendering.SceneRenderer();
            check("incremental card pixels equal full rendering " + i, renderer.ExportPng(next.RenderDocument, session.Assets, true)
                .SequenceEqual(renderer.ExportPng(full.RenderDocument, session.Assets, true)));
        }
        doc.Instances[0].Transform = Affine2D.Translation(30, 140); session.NotifyContainerPlacementChanged([doc.Instances[0].Id]); Capture();
        doc.Instances[1].Transform = Affine2D.Translation(60, 150); session.NotifyContainerPlacementChanged([doc.Instances[1].Id]);
        var accumulated = freezer.EvaluateFrozen(Capture());
        check("unconsumed placement revisions accumulate before world evaluation", JsonSerializer.Serialize(accumulated.RenderDocument) == JsonSerializer.Serialize(SceneEvaluation.Build(doc).RenderDocument));
        definition.Graph.Elements[0].Width = 45; session.NotifyChanged();
        var replaced = freezer.EvaluateFrozen(Capture());
        check("unknown definition edits rebuild instead of reusing stale instance geometry", JsonSerializer.Serialize(replaced.RenderDocument) == JsonSerializer.Serialize(SceneEvaluation.Build(doc).RenderDocument) && !ReferenceEquals(accumulated.Leaves[9], replaced.Leaves[9]));
        session.SetSelection([doc.Instances[0].Id]); session.BeginTransaction("move");
        var transform = session.CaptureSelectionTransform();
        check("shared card gesture safety checks affected occurrence", session.TryApplySelectionTransform(transform, Affine2D.Translation(4, 2)));
        session.Cancel();
    }

    internal static void Performance(string project, string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var loaded = Task.Run(() => new FloatCanvas.Storage.ProjectStore().LoadAsync(project)).GetAwaiter().GetResult();
        var session = new EditorSession(loaded.Document, loaded.Assets);
        var group = session.Document.Groups.First(g => g.Name.Contains("中央天文塔"));
        var child = session.Document.Elements.First(e => e.GroupId == group.Id && !e.Locked).Id;
        var window = new LayersWindow(() => session); window.Show(); window.UpdateLayout();
        var samples = new List<object>();
        using var worker = new CanvasPreviewRenderer(); var freezer = new CanvasPreviewScene();
        var viewport = session.Document.Viewport;
        var outputBounds = new FloatCanvas.Windows.PixelBounds[] { new(0, 0, (int)Math.Ceiling(viewport.Width), (int)Math.Ceiling(viewport.Height)) };
        var context = new CanvasPreviewContext(session, outputBounds, new(-viewport.Left, -viewport.Top), false, false, false);
        CanvasPreviewSnapshot Snapshot()
        {
            var frozen = freezer.Capture(session.Document, session.PreviewRevision, session.PreviewElementsRevision,
                session.TransactionId, -1, -1, true, definitionsRevision: session.PreviewDefinitionsRevision);
            return new() { Context = context, SceneIdentity = session, Document = frozen, SourceDocument = frozen,
                FrozenEvaluation = freezer.EvaluationFor(frozen), Resources = loaded.Assets.Resources.ToArray(), Outputs = outputBounds,
                Revision = session.PreviewRevision, ContentRevision = session.ContentVersion, GraphChanged = freezer.GraphChanged };
        }
        using (var initial = Snapshot()) using (var frame = worker.Render(initial)) { }
        for (int n = 0; n < 35; n++)
        {
            var initialPause = GC.GetTotalPauseDuration();
            long bytes = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            var plan = session.PrepareLayerMove([child], RootCanvasKind.Main, null); double prepare = watch.Elapsed.TotalMilliseconds;
            session.CommitLayerMove(plan); double commit = watch.Elapsed.TotalMilliseconds - prepare;
            window.PrepareForShow(); double total = watch.Elapsed.TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            double commandGcPause = (GC.GetTotalPauseDuration() - initialPause).TotalMilliseconds;
            long frameBytes = GC.GetAllocatedBytesForCurrentThread();
            using var snapshot = Snapshot(); double captured = watch.Elapsed.TotalMilliseconds;
            using var frame = worker.Render(snapshot);
            if (frame.PickingEvaluation?.FindElement(child) is null || frame.ContentRevision != session.ContentVersion)
                throw new Exception("Presented frame and picking data differ from the committed move");
            double frameReady = watch.Elapsed.TotalMilliseconds;
            long frameAllocated = GC.GetAllocatedBytesForCurrentThread() - frameBytes;
            initialPause = GC.GetTotalPauseDuration();
            bytes = GC.GetAllocatedBytesForCurrentThread(); watch.Restart(); session.Undo();
            double undoCore = watch.Elapsed.TotalMilliseconds; window.PrepareForShow();
            double undo = watch.Elapsed.TotalMilliseconds; long undoBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            double undoGcPause = (GC.GetTotalPauseDuration() - initialPause).TotalMilliseconds;
            using (var restored = Snapshot()) using (var restoredFrame = worker.Render(restored)) { }
            if (n >= 5) samples.Add(new { PrepareMs = prepare, CommitMs = commit, RefreshMs = total - prepare - commit,
                TotalMs = total, AllocatedBytes = allocated, UndoMs = undo, UndoAllocatedBytes = undoBytes,
                UndoCoreMs = undoCore, UndoRefreshMs = undo - undoCore,
                FrozenCaptureReadyMs = captured, CorrectOffscreenFrameAndPickingReadyMs = frameReady, WorkerRenderMs = frame.RenderMilliseconds,
                CommandGcPauseMs = commandGcPause, UndoGcPauseMs = undoGcPause, FrameAllocatedBytes = frameAllocated });
        }
        window.Hide(); File.WriteAllText(Path.Combine(output, "tidal-move.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        var components = new Dictionary<string, List<object>>();
        void Component(string name, Action action)
        {
            var records = new List<object>();
            for (int n = 0; n < 35; n++)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew(); action();
                if (n >= 5) records.Add(new { Ms = clock.Elapsed.TotalMilliseconds, Bytes = GC.GetAllocatedBytesForCurrentThread() - bytes });
            }
            components[name] = records;
        }
        Component("one-history-snapshot", () => session.Document.Clone());
        Component("full-capacity-check", () => SceneLimits.ValidateCapacity(session.Document));
        Component("one-scene-index", () => new SceneGraph(session.Document));
        File.WriteAllText(Path.Combine(output, "tidal-components.json"), JsonSerializer.Serialize(components));
        Console.WriteLine("TIDAL_MOVE samples=" + samples.Count + " (production preview worker and matching picking data; physical presentation latency is not measured)");
    }
}
