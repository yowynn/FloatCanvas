using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

internal static class PrefabUiTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current; string language = L.Preference;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = Path.Combine(output ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-prefab-ui"), "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                L.Initialize(locale);
                foreach (string theme in new[] { "light", "dark" }) { AppTheme.Apply(theme); Rows(check, locale, theme, root); }
            }
            Contexts(check); Await(Stamps(check, root)); Await(Links(check, root));
        }
        finally { L.Initialize(language); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    private static EditorSession Source()
    {
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 25, Transform = Affine2D.Translation(20, 30), Style = new() { StrokeWidth = 0, Fill = 0xffff8844 } };
        var session = new EditorSession(new() { Elements = [element], Viewport = new(0, 0, 500, 360) }); session.SetSelection([element.Id]); return session;
    }
    private static void Rows(Action<string, bool> check, string locale, string theme, string root)
    {
        var session = Source(); var created = session.CreatePrefabFromSelection(locale == "zh-CN" ? "徽章" : "Badge");
        var owner = session; bool allowed = true; var layers = new LayersWindow(() => session, () => allowed); layers.PrepareForShow();
        string prefix = locale + "/" + theme + " ";
        check(prefix + "instance is one real row with no duplicated source leaves", layers.VisibleNodes is [var row] && row.IsInstance && !row.IsGroup && session.Document.Elements.Count == 0);
        check(prefix + "instance stays a single current-level row with a dedicated edit arrow", layers.VisibleNodes.Count == 1 &&
            LayerTreeRowPresenter.Find(layers.Container(created.InstanceId))!.EnterButton.Visibility == Visibility.Visible);
        Guid? edit = null; layers.EditPrefabRequested += id => edit = id; layers.EnterGroup(created.InstanceId);
        check(prefix + "instance Enter routes explicit definition editing without a fake host scope", edit == created.InstanceId && session.EditScopeId is null);
        check(prefix + "instance name can start F2 renaming", layers.BeginRename(created.InstanceId));
        layers.FindNode(created.InstanceId)!.DraftName = "Instance A"; layers.HandleRenameKey(Key.Enter); layers.PrepareForShow();
        check(prefix + "renaming an instance leaves its definition name intact", session.Document.Instances.Single().Name == "Instance A" && session.Document.Definitions.Single().Name != "Instance A");
        var presenter = LayerTreeRowPresenter.Find(layers.Container(created.InstanceId))!;
        presenter.VisibleButton.IsChecked = false; presenter.VisibleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        layers.PrepareForShow(); check(prefix + "instance visibility is its own node state", !session.Document.Instances.Single().Visible && session.Document.Definitions.Single().Graph.Elements.Single().Visible);
        layers.SetLayerFlag(created.InstanceId, true, true);
        var resources = new PrefabResourcesWindow(() => session, () => allowed);
        resources.Execute = request =>
        {
            if (request.Action == PrefabResourceAction.Place) request.Owner.PlacePrefab(request.DefinitionId!.Value, new(Affine2D.Translation(120, 60), 1));
            else if (request.Action == PrefabResourceAction.Edit)
            {
                var draft = request.Owner.BeginPrefabEdit(request.DefinitionId!.Value);
                draft.Session.Execute("color", document => document.Elements.Single().Style.Fill = 0xff6688cc);
                request.Owner.ApplyPrefabEdit(request.Owner.PreparePrefabEdit(draft));
            }
            return Task.CompletedTask;
        };
        resources.Refresh(); resources.Select(created.DefinitionId); Await(resources.PerformAsync(PrefabResourceAction.Place));
        check(prefix + "resource action places another instance sharing the same definition", session.Document.Instances.Count == 2 && session.Document.Definitions.Count == 1 && session.Document.Elements.Count == 0);
        Await(resources.PerformAsync(PrefabResourceAction.Edit));
        check(prefix + "resource edit action updates all rendered occurrences", SceneEvaluation.Build(session.Document).Leaves.Count == 2 && SceneEvaluation.Build(session.Document).Leaves.All(leaf => leaf.RenderElement.Style.Fill == 0xff6688cc));
        allowed = false; int history = session.UndoCount; Await(resources.PerformAsync(PrefabResourceAction.Place));
        check(prefix + "resource mutation respects the document busy guard", session.UndoCount == history); allowed = true;
        session = Source(); Await(resources.PerformAsync(PrefabResourceAction.Place));
        check(prefix + "stale resource rows cannot target a replacement session", session.Document.Instances.Count == 0 && owner.Document.Instances.Count == 2);
        session = owner; resources.Refresh(); resources.Select(created.DefinitionId);
        session.Document.Definitions.Single().Source = new() { SourceResourceId = Guid.NewGuid(), EntryKind = PrefabSourceKind.PrefabDefinition };
        resources.Refresh(); layers.PrepareForShow();
        check(prefix + "portable origin metadata does not masquerade as an external link", !resources.Rows.Single().Linked && layers.VisibleNodes.All(row => !row.IsLinked));
        Save((FrameworkElement)resources.Content, Path.Combine(root, $"resources-{locale}-{theme}.png"), 440, 500);
        Save((FrameworkElement)layers.Content, Path.Combine(root, $"layers-{locale}-{theme}.png"), 370, 360);
        SessionWindowCoordinator.Close(resources); SessionWindowCoordinator.Close(layers);
    }
    private static void Contexts(Action<string, bool> check)
    {
        var owner = Source(); var outer = owner.CreatePrefabFromSelection("Outer");
        var draft = owner.BeginPrefabEdit(outer.DefinitionId); var context = new PrefabEditContext(owner, draft, new(90, 110), EditorTool.Rectangle);
        check("definition context uses real separate session and shared immutable asset library", !ReferenceEquals(context.Session, owner) && ReferenceEquals(context.Session.Assets, owner.Assets));
        draft.Session.SetSelection(draft.Session.Document.Elements.Select(element => element.Id));
        var nested = draft.Session.CreatePrefabFromSelection("Nested"); var innerDraft = draft.Session.BeginPrefabEdit(nested.DefinitionId);
        var inner = new PrefabEditContext(draft.Session, innerDraft, default, EditorTool.Select, context);
        check("nested context retains the precise owner and return chain", ReferenceEquals(inner.Parent, context) && ReferenceEquals(inner.Owner, context.Session) && inner.Chain().Count() == 2);
        check("definition contexts cannot recursively reopen the same source definition", inner.Contains(owner, outer.DefinitionId));
        innerDraft.Session.Execute("resize", document => document.Elements.Single().Width = 72);
        inner.Owner.ApplyPrefabEdit(inner.Owner.PreparePrefabEdit(innerDraft));
        owner.ApplyPrefabEdit(owner.PreparePrefabEdit(draft));
        check("completed nested edits publish through each owner in one outer history step", owner.UndoCount == 2 && SceneEvaluation.Build(owner.Document).Leaves.Single().RenderElement.Width == 72);
        owner.Undo(); check("one outer undo restores the whole shared artwork", SceneEvaluation.Build(owner.Document).Leaves.Single().RenderElement.Width == 40);
    }
    private static async Task Stamps(Action<string, bool> check, string root)
    {
        string profile = Path.Combine(root, "stamp-profile"); Directory.CreateDirectory(Path.Combine(profile, "stamps"));
        string catalog = Path.Combine(profile, "stamps", "library.json");
        const string legacy = "{\"Version\":1,\"Items\":[],\"Pins\":[null,null,null,null,null,null,null,null,null,null],\"Order\":[]}";
        await File.WriteAllTextAsync(catalog, legacy); var library = new StampLibrary(profile); await library.LoadAsync();
        check("reading a legacy stamp catalog does not rewrite it", await File.ReadAllTextAsync(catalog) == legacy);
        var source = Source(); var entry = await library.SaveSelectionAsync(source, "Shared", StampSaveMode.SharedPrefab);
        var loaded = await library.LoadStampAsync(entry.Id); var target = new EditorSession();
        StampLibrary.Place(loaded, target, new(100, 80)); StampLibrary.Place(loaded, target, new(200, 80));
        check("shared stamp stores an explicit typed prefab and imports content once", loaded.SharedPrefab is not null && target.Document.Definitions.Count == 1 && target.Document.Instances.Count == 2 && target.Document.Elements.Count == 0);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(catalog))) check("first shared save upgrades the catalog to version 2", json.RootElement.GetProperty("Version").GetInt32() == 2);
        var definitionId = target.Document.Definitions.Single().Id; var draft = target.BeginPrefabEdit(definitionId);
        draft.Session.Execute("local definition", document => document.Elements.Single().Width = 75); target.ApplyPrefabEdit(target.PreparePrefabEdit(draft));
        StampLibrary.Place(loaded, target, new(300, 80));
        check("further placements reuse the project's edited shared definition", target.Document.Definitions.Count == 1 && SceneEvaluation.Build(target.Document).Leaves.All(leaf => leaf.RenderElement.Width == 75));
        target.SetSelection([target.Document.Instances[0].Id]);
        var independent = await library.SaveSelectionAsync(target, "Independent", StampSaveMode.EditableGroup);
        var independentLoad = await library.LoadStampAsync(independent.Id); var copies = new EditorSession();
        StampLibrary.Place(independentLoad, copies, new(30, 40)); StampLibrary.Place(independentLoad, copies, new(150, 40));
        check("legacy editable-group saving from an instance makes independent ordinary copies", independentLoad.SharedPrefab is null && copies.Document.Instances.Count == 0 && copies.Document.Elements.Count == 2);
        var reloaded = new StampLibrary(profile); await reloaded.LoadAsync();
        check("catalog reopen preserves shared versus independent placement modes", (await reloaded.LoadStampAsync(entry.Id)).SharedPrefab is not null && (await reloaded.LoadStampAsync(independent.Id)).SharedPrefab is null);
        var published = await library.SaveSelectionAsync(source, "Shared revision", StampSaveMode.SharedPrefab, entry.Id);
        var latest = await library.LoadStampAsync(published.Id); StampLibrary.Place(latest, target, new(400, 80));
        check("overwriting a library prefab does not silently replace the project's previous definition", target.Document.Definitions.Count == 2 && target.Document.Definitions.Any(definition => definition.Id == definitionId));
    }
    private static async Task Links(Action<string, bool> check, string root)
    {
        var source = Source(); source.Document.Canvas.Projection.Columns = 9;
        string path = Path.Combine(root, "linked.floatcanvas"); var store = new ProjectStore(); await store.SaveAsync(path, source.Document, source.Assets);
        var owner = new EditorSession(); var resolver = new PrefabLinkResolver();
        var candidate = await resolver.PrepareAsync(owner, path, Path.Combine(root, "owner.floatcanvas"));
        check("link preparation is read-only and retains the original source camera", owner.Document.Definitions.Count == 0 && candidate.SourceProject!.Document.Canvas.Projection.Columns == 9 && candidate.Source.IsLinked && candidate.Source.IsRelative);
        var imported = owner.ImportPrefab(candidate.Package, candidate.Assets, LayerPlacement.Identity, candidate.Source);
        owner.PlacePrefab(imported.DefinitionId, new(Affine2D.Translation(150, 0), 1));
        check("one linked source drives multiple real instance nodes", owner.Document.Instances.Count == 2 && owner.Document.Definitions.Count == 1);
        var scope = owner.CreateEmptyGroup("Scope"); var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resolver.BeforeReadAsync = ct => delay.Task.WaitAsync(ct); long beforeScope = owner.SceneRevision;
        var pendingScope = resolver.PrepareAsync(owner, path, Path.Combine(root, "owner.floatcanvas"), imported.DefinitionId);
        owner.EnterScope(scope); owner.LeaveScope(); delay.SetResult(); var staleScope = await pendingScope; resolver.BeforeReadAsync = null;
        check("delayed source read cannot revive after leaving and returning to the same scope", owner.SceneRevision == beforeScope && !PrefabLinkResolver.IsCurrent(staleScope, owner));
        source.Document.Elements.Single().Width = 92; await store.SaveAsync(path, source.Document, source.Assets);
        check("checking a modified source does not apply it", await resolver.CheckAsync(owner.Document.Definitions.Single().Source!, Path.Combine(root, "owner.floatcanvas")) == PrefabLinkStatus.Changed && SceneEvaluation.Build(owner.Document).Leaves.All(leaf => leaf.RenderElement.Width == 40));
        var refresh = await resolver.PrepareAsync(owner, path, Path.Combine(root, "owner.floatcanvas"), imported.DefinitionId);
        var plan = owner.PreparePrefabRefresh(imported.DefinitionId, refresh.Package, refresh.Assets, refresh.Source); owner.ApplyPrefabEdit(plan);
        check("one accepted refresh updates every instance", SceneEvaluation.Build(owner.Document).Leaves.All(leaf => leaf.RenderElement.Width == 92));
        owner.Undo(); check("refresh undo restores snapshots without rewriting the source file", SceneEvaluation.Build(owner.Document).Leaves.All(leaf => leaf.RenderElement.Width == 40) && (await store.LoadAsync(path)).Document.Elements.Single().Width == 92);
        owner.Redo(); string moved = Path.Combine(root, "moved.floatcanvas"); File.Move(path, moved);
        check("missing source keeps the complete offline snapshot", await resolver.CheckAsync(owner.Document.Definitions.Single().Source!, Path.Combine(root, "owner.floatcanvas")) == PrefabLinkStatus.Missing && SceneEvaluation.Build(owner.Document).Leaves.Count == 2);
        var relink = await resolver.PrepareAsync(owner, moved, Path.Combine(root, "owner.floatcanvas"), imported.DefinitionId);
        check("relinking recognizes persistent source identity at a new path", relink.Source.SourceResourceId == candidate.Source.SourceResourceId && relink.Source.Locator != candidate.Source.Locator);
        owner.RenameLayer(owner.Document.Instances[0].Id, "Changed while reading");
        check("an async link candidate cannot publish after owner content changes", !PrefabLinkResolver.IsCurrent(relink, owner));
        source.Document.ResourceId = owner.Document.ResourceId; await store.SaveAsync(path, source.Document, source.Assets);
        bool cycle = false; try { await resolver.PrepareAsync(owner, path, null); } catch (InvalidOperationException) { cycle = true; }
        check("linking a resource back to its owner is rejected", cycle);
    }
    private static void Save(FrameworkElement element, string path, double width, double height)
    {
        element.Measure(new(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}
