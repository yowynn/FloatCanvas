using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

internal static class PrefabOwnerPreviewTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current; string language = L.Preference;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Draft(check, output); Sources(check); BoundsAndPlaceholders(check); Scheduling(check); }
        finally { L.Initialize(language); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static EditorSession Owner()
    {
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30,
            Transform = Affine2D.Translation(30, 40), Style = new() { Fill = 0xffdf3333, StrokeWidth = 0 } };
        var owner = new EditorSession(new() { Elements = [element], Viewport = new(0, 0, 500, 300) }); owner.SetSelection([element.Id]); return owner;
    }
    private static void Draft(Action<string, bool> check, string? output)
    {
        var owner = Owner(); var created = owner.CreatePrefabFromSelection("Preview"); owner.PlacePrefab(created.DefinitionId, new(Affine2D.Translation(220, 100), 1));
        var draft = owner.BeginPrefabEdit(created.DefinitionId); var context = new PrefabEditContext(owner, draft, default, EditorTool.Select);
        string unchanged = JsonSerializer.Serialize(owner.Document); int history = owner.UndoCount, assets = owner.Assets.Count;
        var before = PrefabPreviewScheduler.Render(PrefabPreviewComposer.Capture(context, owner.Assets), CancellationToken.None);
        draft.Session.BeginTransaction("live sample"); draft.Session.Document.Elements.Single().Width = 95;
        draft.Session.Document.Elements.Single().Style.Fill = 0xff2288dd; draft.Session.NotifyChanged();
        var captured = PrefabPreviewComposer.Capture(context, owner.Assets);
        var preview = PrefabPreviewScheduler.Render(captured, CancellationToken.None);
        check("preview composes an active definition gesture into both host instances", draft.Session.IsTransactionActive && SceneEvaluation.Build(captured.Document).Leaves.Count == 2 &&
            SceneEvaluation.Build(captured.Document).Leaves.All(leaf => leaf.RenderElement.Width == 95));
        check("all-instance preview changes pixels without publishing owner undo or assets", Pixels(before.Image) != Pixels(preview.Image) && JsonSerializer.Serialize(owner.Document) == unchanged && owner.UndoCount == history && owner.Assets.Count == assets);
        draft.Session.Document.Elements.Single().Width = 120; draft.Session.NotifyChanged();
        check("captured preview is detached from later live source mutation", SceneEvaluation.Build(captured.Document).Leaves.All(leaf => leaf.RenderElement.Width == 95));
        draft.Session.Cancel(); draft.Session.SetSelection(draft.Session.Document.Elements.Select(element => element.Id));
        var nested = draft.Session.CreatePrefabFromSelection("Nested"); var innerDraft = draft.Session.BeginPrefabEdit(nested.DefinitionId);
        var inner = new PrefabEditContext(draft.Session, innerDraft, default, EditorTool.Select, context);
        innerDraft.Session.BeginTransaction("inner sample"); innerDraft.Session.Document.Elements.Single().Width = 82; innerDraft.Session.NotifyChanged();
        var nestedInput = PrefabPreviewComposer.Capture(inner, owner.Assets);
        check("preview propagates an uncommitted nested draft through every parent context", SceneEvaluation.Build(nestedInput.Document).Leaves.Count == 2 && SceneEvaluation.Build(nestedInput.Document).Leaves.All(leaf => leaf.RenderElement.Width == 82));
        check("nested preview leaves its root owner exactly unchanged", JsonSerializer.Serialize(owner.Document) == unchanged && owner.UndoCount == history);
        innerDraft.Session.Commit();
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            foreach (string locale in new[] { "zh-CN", "en" }) foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(locale); AppTheme.Apply(theme); var layers = new LayersWindow(() => innerDraft.Session);
                layers.SetEditContext("Preview");
                layers.SetGroupContext(new("Nested", GroupStorageKind.Embedded, null, GroupCanvasMode.RootProject, true)); layers.PrepareForShow();
                var body = (FrameworkElement)layers.Content; body.Measure(new(440, 620)); body.Arrange(new Rect(0, 0, 440, 620)); body.UpdateLayout();
                var bitmap = new RenderTargetBitmap(440, 620, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(body);
                Save(bitmap, Path.Combine(output, $"all-instances-{locale}-{theme}.png")); SessionWindowCoordinator.Close(layers);
            }
        }
        innerDraft.Session.Cancel();
    }
    private static void Sources(Action<string, bool> check)
    {
        foreach (bool prefab in new[] { false, true })
        {
            var source = Owner(); var sourceBox = source.Document.Elements.Single();
            var created = source.CreatePrefabFromSelection("Source"); var package = source.ExportPrefab(created.DefinitionId);
            var owner = new EditorSession(new() { Viewport = new(0, 0, 500, 300) });
            var metadata = new ExternalPrefabSource { Locator = prefab ? "C:/not-read/source.floatprefab" : "C:/not-read/source.floatcanvas",
                SourceResourceId = prefab ? package.ResourceId : source.Document.ResourceId, EntryKind = prefab ? PrefabSourceKind.PrefabDefinition : PrefabSourceKind.ProjectContent,
                EntryDefinitionId = prefab ? package.EntryDefinitionId : null, LastAcceptedFileSha256 = new string('a', 64) };
            SceneDocument edited; PointD origin;
            if (prefab)
            {
                var entry = package.Definitions.Single(definition => definition.Id == package.EntryDefinitionId); var graph = SceneGraphClone.CloneGraph(entry.Graph).Graph;
                edited = new() { ResourceId = package.ResourceId, Elements = graph.Elements, Groups = graph.Groups, Instances = graph.Instances, RootOrder = graph.RootOrder,
                    Definitions = package.Definitions.Select(definition => definition.Clone()).ToList(), Viewport = new(0, 0, 500, 300) }; origin = entry.Pivot;
            }
            else
            {
                // Preserve an ordinary source project instead of saving a normalized definition as its source.
                edited = new() { Elements = [sourceBox.Clone()], Viewport = new(0, 0, 500, 300) }; edited.ResourceId = metadata.SourceResourceId;
                (package, origin) = PrefabLinkResolver.ProjectContent(new(edited, source.Assets), "Source");
            }
            metadata.ImportOrigin = origin; var imported = owner.ImportPrefab(package, source.Assets, LayerPlacement.Identity, metadata);
            owner.PlacePrefab(imported.DefinitionId, new(Affine2D.Translation(200, 90), 1));
            var session = new EditorSession(edited, owner.Assets);
            var context = new PrefabEditContext(owner, session, imported.DefinitionId, owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId).Revision,
                default, EditorTool.Select, sourcePath: metadata.Locator, sourceEntryDefinitionId: prefab ? package.EntryDefinitionId : null);
            string prior = JsonSerializer.Serialize(owner.Document); int undo = owner.UndoCount;
            session.BeginTransaction("source sample"); session.Document.Elements.Single().Width = 73; session.NotifyChanged();
            var foreground = new CanvasElement { Name = "Preview-only foreground", Kind = ElementKind.Rectangle, Width = 40, Height = 30 };
            new SceneGraph(session.Document).InsertElement(foreground, rootCanvas: RootCanvasKind.Foreground); session.NotifyChanged();
            var input = PrefabPreviewComposer.Capture(context, owner.Assets);
            check((prefab ? "prefab" : "project") + " source preview uses current Main content and excludes auxiliary roots without reading its locator", SceneEvaluation.Build(input.Document).Leaves.Count == 2 && SceneEvaluation.Build(input.Document).Leaves.All(leaf => leaf.RenderElement.Width == 73));
            check((prefab ? "prefab" : "project") + " source preview does not publish its candidate", JsonSerializer.Serialize(owner.Document) == prior && owner.UndoCount == undo);
            session.Cancel();
        }
    }
    private static void BoundsAndPlaceholders(Action<string, bool> check)
    {
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 60,
            Transform = Affine2D.Translation(-100, -50), Style = new() { Fill = 0xffff0000, StrokeWidth = 0 } };
        var live = new CanvasElement { Kind = ElementKind.LiveBlur, Width = 100, Height = 70, Transform = Affine2D.Translation(100, 70),
            Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 12 }] };
        var document = new SceneDocument { Viewport = new(-100, -50, 2000, 1400), Elements = [rectangle, live] };
        string prior = JsonSerializer.Serialize(document);
        var frame = PrefabPreviewScheduler.Render(new(document, [], default), CancellationToken.None);
        check("preview output stays at most 1000 by 700 pixels for a large negative-origin viewport", frame.Image.PixelWidth == 1000 && frame.Image.PixelHeight == 700);
        var pixel = new byte[4]; frame.Image.CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0);
        check("bounded preview honors the source viewport origin and authored artwork", pixel[2] > 240 && pixel[1] < 20 && pixel[0] < 20);
        check("live effects become static placeholders without touching the source graph", frame.HasLiveEffectPlaceholder && live.Kind == ElementKind.LiveBlur && live.Effects.Count == 1 && JsonSerializer.Serialize(document) == prior);
    }
    private static void Scheduling(Action<string, bool> check)
    {
        var input = new PrefabPreviewInput(Owner().Document.Clone(), [], default);
        using (var scheduler = new PrefabPreviewScheduler(Dispatcher.CurrentDispatcher, interval: TimeSpan.FromMilliseconds(20)))
        {
            int frames = 0; scheduler.FrameReady += _ => frames++; var identity = new object();
            for (int revision = 0; revision < 40; revision++) scheduler.Request(identity, revision, () => input);
            Wait(() => frames == 1);
            check("many live samples coalesce into one snapshot and one bounded worker", scheduler.CaptureCount == 1 && scheduler.RenderCount == 1 && scheduler.MaximumConcurrentWorkers == 1);
        }
        using var release = new ManualResetEventSlim(); int active = 0, maximum = 0, published = 0;
        using var blocked = new PrefabPreviewScheduler(Dispatcher.CurrentDispatcher, (sample, _) =>
        {
            int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count); release.Wait();
            try { return PrefabPreviewScheduler.Render(sample, CancellationToken.None); } finally { Interlocked.Decrement(ref active); }
        }, TimeSpan.FromMilliseconds(20));
        blocked.FrameReady += _ => published++; var first = new object(); blocked.Request(first, 1, () => input);
        Wait(() => blocked.IsRunning); for (int revision = 2; revision < 30; revision++) blocked.Request(first, revision, () => input);
        blocked.Cancel(); var second = new object(); blocked.Request(second, 1, () => input); release.Set(); Wait(() => published == 1 && !blocked.IsRunning);
        check("context cancellation rejects a late frame and never overlaps render workers", published == 1 && blocked.RenderCount == 2 && blocked.CaptureCount == 2 && maximum == 1);
        using var releaseDisposed = new ManualResetEventSlim(); int afterDispose = 0;
        var closing = new PrefabPreviewScheduler(Dispatcher.CurrentDispatcher, (sample, _) => { releaseDisposed.Wait(); return PrefabPreviewScheduler.Render(sample, CancellationToken.None); }, TimeSpan.FromMilliseconds(20));
        closing.FrameReady += _ => afterDispose++; closing.Request(new object(), 1, () => input); Wait(() => closing.IsRunning); closing.Dispose(); releaseDisposed.Set(); Wait(() => !closing.IsRunning);
        check("closing a preview prevents publication from an already running render", afterDispose == 0);
    }
    private static string Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }
    private static void Save(BitmapSource image, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Wait(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); try { if (!complete()) Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!complete()) throw new TimeoutException("Prefab preview did not complete.");
    }
}
