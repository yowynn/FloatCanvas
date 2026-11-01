using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

/// <summary>Actual DesktopSession hooks run only on a separate, never displayed Windows desktop.</summary>
internal static class PrefabDesktopContextTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal)) throw new InvalidOperationException("Use --isolated-ui-desktop --prefab-desktop-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Run in a fresh child process.");
        string root = Path.Combine(output ?? Path.GetTempPath(), "prefab-desktop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "light", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2, PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-22000, -22000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("prefab-context", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var toolbar = Field<Window>(desktop, "_toolbar"); toolbar.Opacity = 0;
        ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(host))?.Dispose(); typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(host, null);
        var editor = Field<EditorController>(desktop, "_editor"); var owner = editor.Session;
        var box = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20,
            Transform = Affine2D.Translation(owner.Document.Viewport.Left + 30, owner.Document.Viewport.Top + 40), Style = new() { Fill = 0xff7799cc, StrokeWidth = 0 } };
        owner.Add(box); owner.SetSelection([box.Id]); var created = owner.CreatePrefabFromSelection("Badge");
        owner.ExecuteSettings("custom camera", document => { document.Canvas.Grid.FlatOrigin = new(13, 27); document.Canvas.Grid.FlatCellSize = 17; });
        string ownerCamera = JsonSerializer.Serialize(owner.Document.Canvas);
        int before = owner.UndoCount;
        Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!);
        var context = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        check("DesktopSession enters a detached definition session and retains its publishing owner", !ReferenceEquals(editor.Session, owner) && ReferenceEquals(context.Owner, owner));
        var layers = Field<LayersWindow>(desktop, "_layers");
        string untouchedOwner = JsonSerializer.Serialize(owner.Document);
        var draftSession = editor.Session; var draftRows = layers.VisibleNodes.Select(row => row.Id).ToArray();
        editor.Session.Execute("preview sample", document => document.Elements.Single().Width = 55);
        int draftHistory = draftSession.UndoCount;
        Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        using (var rootCanvas = (CanvasPreviewSnapshot)Invoke("CaptureCanvasPreview")!)
            check("definition edits appear in the actual root canvas snapshot", SceneEvaluation.Build(rootCanvas.Document).Leaves.Single().RenderElement.Width == 55 && rootCanvas.Document.ResourceId == owner.Document.ResourceId && rootCanvas.Context.Origin == context.ReturnOrigin);
        check("root canvas preview retains the current group list and detached editor", ReferenceEquals(editor.Session, draftSession) && draftRows.SequenceEqual(layers.VisibleNodes.Select(row => row.Id)) && layers.CurrentGroupContext is { Storage: GroupStorageKind.Embedded, IsEntered: true, Mode: GroupCanvasMode.RootProject });
        check("root canvas preview does not publish history or source content", owner.UndoCount == before && draftSession.UndoCount == draftHistory && JsonSerializer.Serialize(owner.Document) == untouchedOwner);
        string draftBeforePointer = JsonSerializer.Serialize(draftSession.Document);
        editor.Tool = EditorTool.Rectangle;
        Invoke("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Down, new(15, 15), OverlayMouseButton.Left, OverlayModifiers.None));
        Invoke("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Up, new(45, 35), OverlayMouseButton.Left, OverlayModifiers.None));
        check("pointer input before the current root frame is presented cannot mutate the retained draft", JsonSerializer.Serialize(draftSession.Document) == draftBeforePointer && draftSession.UndoCount == draftHistory && !draftSession.IsTransactionActive);
        editor.Tool = EditorTool.Select;
        var unresolvedSource = new ExternalPrefabSource { Locator = "unresolved/source.floatprefab", IsRelative = true };
        check("a recovered relative source without a save location remains visible", ((string)Invoke("GroupSourcePath", unresolvedSource, owner)!).Contains(unresolvedSource.Locator, StringComparison.Ordinal));
        Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        check("temporary canvas returns to the same editable draft without an extra preview window", ReferenceEquals(editor.Session, draftSession) && layers.CurrentGroupContext?.Mode == GroupCanvasMode.Temporary && app.Windows.OfType<Window>().All(window => window.GetType().Name != "PrefabPreviewWindow"));
        string added = owner.Assets.Add(Png());
        editor.Session.Add(new() { Kind = ElementKind.Image, AssetId = added, Width = 12, Height = 12, Visible = false });
        Invoke("PruneDocumentResources");
        check("normal resource pruning retains assets owned only by an unfinished draft", owner.Assets.TryGet(added, out _) && !owner.Document.ReferencedAssets.Contains(added));
        using (var answer = new Answer(app, "Prefab.Discard")) Await((Task)Invoke("LeaveCurrentPrefabAsync")!);
        check("discarding an actual definition context restores the owner without a host history change", ReferenceEquals(editor.Session, owner) && owner.UndoCount == before && !owner.Assets.TryGet(added, out _));
        check("returning from a discarded definition context retains the owner's camera", JsonSerializer.Serialize(owner.Document.Canvas) == ownerCamera);
        check("discarding the context returns the group toolbar to an unentered selection", layers.CurrentGroupContext?.IsEntered != true);

        Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!);
        var outer = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        editor.Session.SetSelection(editor.Session.Document.Elements.Select(element => element.Id));
        var nested = editor.Session.CreatePrefabFromSelection("Nested"); Await((Task)Invoke("EditPrefabDefinitionAsync", nested.DefinitionId)!);
        editor.Session.Execute("nested width", document => document.Elements.Single().Width = 88);
        var snapshot = (WorkspaceState)Invoke("SnapshotWorkspace")!;
        check("actual workspace snapshot includes both pending edit contexts and committed root content", PrefabDraftState.Enumerate(snapshot.PendingPrefabEdit).Count == 2 && SceneEvaluation.Build(snapshot.LiveDocument).Leaves.Single().RenderElement.Width == 30);
        var recovery = new RecoveryStore(Path.Combine(root, "recovery")); Await(recovery.SaveAsync(snapshot, owner.Assets));
        var loaded = Await(recovery.LoadAsync())!;
        Invoke("ReturnFromPrefabContext", Field<PrefabEditContext>(desktop, "_prefabEditContext"));
        Invoke("ReturnFromPrefabContext", Field<PrefabEditContext>(desktop, "_prefabEditContext"));
        Invoke("RestorePrefabRecoveryState", loaded.State.PendingPrefabEdit!);
        check("actual recovery hook resumes the inner real source graph and parent editing context", Field<PrefabEditContext>(desktop, "_prefabEditContext").Parent is not null && editor.Session.Document.Elements.Single().Width == 88);
        Await((Task)Invoke("CompleteCurrentPrefabAsync")!); Await((Task)Invoke("CompleteCurrentPrefabAsync")!);
        check("finishing recovered nested contexts publishes once to the root owner", ReferenceEquals(editor.Session, owner) && owner.UndoCount == before + 1 && SceneEvaluation.Build(owner.Document).Leaves.Single().RenderElement.Width == 88);

        var source = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 320, 240), Elements = [new() { Kind = ElementKind.Rectangle, Width = 18, Height = 21 }] };
        source.Canvas.Grid.PlaneCellSize = 37; source.Background = new() { Kind = ElementKind.Image, AssetId = owner.Assets.Add(Png()), Width = 320, Height = 240, Locked = true };
        string path = Path.Combine(root, "source.floatcanvas"); var projects = new ProjectStore(); Await(projects.SaveAsync(path, source, owner.Assets));
        string camera = JsonSerializer.Serialize(source.Canvas), background = JsonSerializer.Serialize(source.Background);
        var resolver = new PrefabLinkResolver(); var prepared = Await(resolver.PrepareAsync(owner, path, null));
        var imported = owner.ImportPrefab(prepared.Package, prepared.Assets, LayerPlacement.Identity, prepared.Source);
        Await((Task)Invoke("EditPrefabInstanceAsync", imported.InstanceId)!);
        var sourceContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        check("external group toolbar identifies its actual disk source", sourceContext.EnteredInstanceId == imported.InstanceId &&
            layers.CurrentGroupContext is { Storage: GroupStorageKind.ExternalFile, IsEntered: true } external && Path.GetFullPath(external.SourcePath!) == Path.GetFullPath(path) && external.Detach is not null);
        Console.WriteLine($"PREFAB_SOURCE_CONTEXT isSource={sourceContext.IsSource} kind={editor.Session.Document.Kind} camera={JsonSerializer.Serialize(editor.Session.Document.Canvas) == camera} background={JsonSerializer.Serialize(editor.Session.Document.Background) == background}");
        File.WriteAllText(Path.Combine(root, "source-original.json"), JsonSerializer.Serialize(source));
        File.WriteAllText(Path.Combine(root, "source-edit.json"), JsonSerializer.Serialize(editor.Session.Document));
        check("source editing opens the original full project including capture camera and background", sourceContext.IsSource && editor.Session.Document.Kind == DocumentKind.FrozenCapture && JsonSerializer.Serialize(editor.Session.Document.Canvas) == camera && JsonSerializer.Serialize(editor.Session.Document.Background) == background);
        editor.Session.Execute("source width", document => document.Elements.Single().Width = 66);
        Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        using (var rootCanvas = (CanvasPreviewSnapshot)Invoke("CaptureCanvasPreview")!)
            check("unsaved source edits appear on the root canvas with its original viewport", rootCanvas.Document.Viewport == owner.Document.Viewport &&
                SceneEvaluation.Build(rootCanvas.Document).OccurrencesOfHostNode(imported.InstanceId).Any(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle && leaf.RenderElement.Width == 66));
        check("root preview does not save the external file", Await(projects.LoadAsync(path)).Document.Elements.Single().Width == 18);
        Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        Await((Task)Invoke("TryHandlePrefabSaveAsync", false)!);
        var written = Await(projects.LoadAsync(path));
        check("source save routes to its original project rather than a pure definition payload", JsonSerializer.Serialize(written.Document.Canvas) == camera && JsonSerializer.Serialize(written.Document.Background) == background && written.Document.Elements.Single().Width == 66);
        Await((Task)Invoke("CompleteCurrentPrefabAsync")!);
        check("returning from source editing updates the owner's linked content", ReferenceEquals(editor.Session, owner) && SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(imported.InstanceId).Any(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle && leaf.RenderElement.Width == 66));

        source.Elements.Single().Width = 77; Await(projects.SaveAsync(path, source, owner.Assets));
        Await((Task)Invoke("OnPrefabSourceSavedAsync", path)!);
        check("source-save notification refreshes an idle opened owner", SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(imported.InstanceId).Any(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle && leaf.RenderElement.Width == 77));
        owner.BeginTransaction("busy"); source.Elements.Single().Width = 99; Await(projects.SaveAsync(path, source, owner.Assets));
        Await((Task)Invoke("OnPrefabSourceSavedAsync", path)!);
        check("source-save notification does not replace an owner during its gesture", SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(imported.InstanceId).Any(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle && leaf.RenderElement.Width == 77));
        owner.Cancel(); Await((Task)Invoke("CheckOpenedPrefabLinksAsync", owner)!);
        Wait(() => SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(imported.InstanceId).Any(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle && leaf.RenderElement.Width == 99));
        check("opened-link check accepts the queued safe update after the gesture ends", SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(imported.InstanceId).Any(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle && leaf.RenderElement.Width == 99));

        var prefabStore = new PrefabStore(); string prefabPath = Path.Combine(root, "source.floatprefab");
        byte[] oldImageBytes = Png(SKColors.LimeGreen); string oldImage = owner.Assets.Add(oldImageBytes);
        var imageLeaf = new CanvasElement { Kind = ElementKind.Image, AssetId = oldImage, Width = 16, Height = 18 };
        var imageDefinition = new PrefabDefinition { Name = "Image dependency", Graph = new() { Elements = [imageLeaf] } };
        var dependency = new PrefabInstance { DefinitionId = imageDefinition.Id, Transform = Affine2D.Translation(55, 0) };
        var packageBox = new CanvasElement { Kind = ElementKind.Rectangle, Width = 45, Height = 26 };
        var packageEntry = new PrefabDefinition { Name = "Source prefab", Pivot = new(9, 14), Graph = new() { Elements = [packageBox], Instances = [dependency], RootOrder = [dependency.Id, packageBox.Id] } };
        var sourcePackage = new PrefabPackage { EntryDefinitionId = packageEntry.Id, Definitions = [packageEntry, imageDefinition] };
        Guid sourceIdentity = sourcePackage.ResourceId, sourceEntry = sourcePackage.EntryDefinitionId; PointD sourcePivot = packageEntry.Pivot;
        Await(prefabStore.SaveAsync(prefabPath, sourcePackage, owner.Assets));
        var prefabCandidate = Await(resolver.PrepareAsync(owner, prefabPath, null));
        var linkedPrefab = owner.ImportPrefab(prefabCandidate.Package, prefabCandidate.Assets, LayerPlacement.Identity, prefabCandidate.Source);
        Guid secondPrefab = owner.PlacePrefab(linkedPrefab.DefinitionId, new(Affine2D.Translation(160, 90), 1));
        Await((Task)Invoke("EditPrefabSourceAsync", linkedPrefab.DefinitionId)!);
        var prefabContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        check("linked prefab enters its real source entry with the complete dependency table", prefabContext.IsPrefabSource && prefabContext.SourceEntryDefinitionId == sourceEntry &&
            editor.Session.Document.ResourceId == sourceIdentity && editor.Session.Document.Definitions.Count == 2 && editor.Session.Document.Instances.Single().DefinitionId == imageDefinition.Id);
        string newImage = owner.Assets.Add(Png(SKColors.Fuchsia));
        editor.Session.Execute("edit prefab source", document => document.Elements.Single().Width = 81);
        editor.Session.Add(new() { Kind = ElementKind.Image, AssetId = newImage, Width = 7, Height = 8, Transform = Affine2D.Translation(90, 0) });
        Invoke("PruneDocumentResources");
        check("editing a linked prefab retains original dependency image bytes alongside new draft resources", owner.Assets.Get(oldImage).Data.Span.SequenceEqual(oldImageBytes) && owner.Assets.TryGet(newImage, out _));
        Await((Task)Invoke("TryHandlePrefabSaveAsync", false)!);
        var savedPrefab = Await(prefabStore.LoadAsync(prefabPath));
        check("prefab source save preserves package identity entry pivot and definition closure", savedPrefab.Package.ResourceId == sourceIdentity && savedPrefab.Package.EntryDefinitionId == sourceEntry &&
            savedPrefab.Package.Definitions.Single(definition => definition.Id == sourceEntry).Pivot == sourcePivot && savedPrefab.Package.Definitions.Count == 2 &&
            savedPrefab.Package.Definitions.Any(definition => definition.Id == imageDefinition.Id) && savedPrefab.Package.ReferencedAssets.ToHashSet().SetEquals([oldImage, newImage]));
        check("prefab source package retains both original and newly added encoded image resources", savedPrefab.Assets.Get(oldImage).Data.Span.SequenceEqual(oldImageBytes) && savedPrefab.Assets.TryGet(newImage, out _));
        Await((Task)Invoke("CompleteCurrentPrefabAsync")!);
        var prefabWorld = SceneEvaluation.Build(owner.Document);
        check("finishing prefab source editing synchronizes every matching host instance", ReferenceEquals(editor.Session, owner) &&
            new[] { linkedPrefab.InstanceId, secondPrefab }.All(id => prefabWorld.OccurrencesOfHostNode(id).Count == 3 &&
                prefabWorld.OccurrencesOfHostNode(id).Single(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle).RenderElement.Width == 81));

        Await((Task)Invoke("EditPrefabSourceAsync", linkedPrefab.DefinitionId)!);
        prefabContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        editor.Session.Execute("unpublished local source edit", document => document.Elements.Single(element => element.Kind == ElementKind.Rectangle).Width = 111);
        var externalPrefab = Await(prefabStore.LoadAsync(prefabPath));
        externalPrefab.Package.Definitions.Single(definition => definition.Id == sourceEntry).Graph.Elements.Single(element => element.Kind == ElementKind.Rectangle).Width = 123;
        Await(prefabStore.SaveAsync(prefabPath, externalPrefab.Package, externalPrefab.Assets)); byte[] conflictVersion = File.ReadAllBytes(prefabPath);
        bool conflictSaved; int conflictDialogs;
        using (var answer = new Answer(app, "Project.KeepCurrent")) { conflictSaved = Await((Task<bool>)Invoke("SavePrefabSourceAsync", prefabContext, false)!); conflictDialogs = answer.Count; }
        check("prefab expected-file-version conflict does not overwrite the external generation", !conflictSaved && conflictDialogs == 1 && File.ReadAllBytes(prefabPath).SequenceEqual(conflictVersion));
        check("prefab save conflict retains the user's source edit and all referenced images", ReferenceEquals(editor.Session, prefabContext.Session) &&
            editor.Session.Document.Elements.Single(element => element.Kind == ElementKind.Rectangle).Width == 111 && owner.Assets.Get(oldImage).Data.Span.SequenceEqual(oldImageBytes));
        using (var answer = new Answer(app, "Prefab.Discard")) Await((Task)Invoke("LeaveCurrentPrefabAsync")!);

        var existingInstanceIds = owner.Document.Instances.Select(instance => instance.Id).ToHashSet(); Guid rootIdentity = owner.Document.ResourceId;
        host.SetVisible(false); int passiveBefore = owner.Document.Instances.Count;
        Await((Task)Invoke("OpenProjectPathCoreAsync", prefabPath, false)!);
        check("passive prefab file dispatch imports without replacing the existing root project", ReferenceEquals(editor.Session, owner) && owner.Document.ResourceId == rootIdentity &&
            owner.Document.Instances.Count == passiveBefore + 1 && existingInstanceIds.IsSubsetOf(owner.Document.Instances.Select(instance => instance.Id)));
        check("passive prefab file dispatch keeps pass-through mode and the imported dependency closure", !host.IsEditing && !host.IsKeyboardIsolationActive &&
            SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(owner.Selection.Single()).Any(leaf => leaf.RenderElement.AssetId == oldImage));
        int editingBefore = owner.Document.Instances.Count; long entryVersion = host.EditingSessionVersion;
        string statusBefore = Field<TextBlock>(desktop, "_status").Text;
        Await((Task)Invoke("OpenProjectPathCoreAsync", prefabPath, true)!);
        check("editing-request prefab dispatch uses the same additive import path", ReferenceEquals(editor.Session, owner) && owner.Document.Instances.Count == editingBefore + 1 && owner.Document.ResourceId == rootIdentity);
        check("editing-request prefab dispatch reports its isolated-desktop input result", host.EditingSessionVersion != entryVersion || host.IsEditing || Field<TextBlock>(desktop, "_status").Text != statusBefore);

        Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!);
        var conflictDraft = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        string draftImage = owner.Assets.Add(Png(SKColors.OrangeRed));
        conflictDraft.Session.Add(new() { Kind = ElementKind.Rectangle, Width = 77, Height = 19 });
        conflictDraft.Session.Add(new() { Kind = ElementKind.Image, Width = 13, Height = 14, AssetId = draftImage });
        owner.RenamePrefabDefinition(created.DefinitionId, "Owner changed while editing");
        string changedOwner = JsonSerializer.Serialize(owner.Document); int changedHistory = owner.UndoCount;
        string savedDraftPath = Path.Combine(root, "retained-conflict-draft.floatprefab");
        Await((Task)Invoke("ExportPrefabDraftAsync", conflictDraft, savedDraftPath)!);
        var retainedDraft = Await(prefabStore.LoadAsync(savedDraftPath));
        check("conflicted definition draft can be saved as an independent editable prefab", retainedDraft.Package.Definitions.Single(definition => definition.Id == retainedDraft.Package.EntryDefinitionId).Graph.Elements.Any(element => element.Width == 77) &&
            retainedDraft.Package.ReferencedAssets.Contains(draftImage) && retainedDraft.Assets.TryGet(draftImage, out _));
        check("draft Save As retains its editing context and never publishes the conflicted owner", ReferenceEquals(editor.Session, conflictDraft.Session) && owner.UndoCount == changedHistory && JsonSerializer.Serialize(owner.Document) == changedOwner);
        using (var answer = new Answer(app, "Prefab.Discard")) Await((Task)Invoke("LeaveCurrentPrefabAsync")!);
        OrdinaryAndDetach();
        check("desktop context fixture never exposes overlay HWNDs on the user's desktop", host.Diagnostics.All(pair => pair.Bounds == bounds) && !host.IsKeyboardIsolationActive);
        Console.WriteLine("PREFAB_DESKTOP_EVIDENCE " + root);
        object? Invoke(string method, params object?[] arguments) => typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, arguments);

        void OrdinaryAndDetach()
        {
            owner.SetEditScope(null);
            Guid ordinary = owner.CreateEmptyGroup("Ordinary badge"); owner.SetEditScope(ordinary);
            var child = new CanvasElement { Kind = ElementKind.Ellipse, Width = 14, Height = 11 };
            owner.Add(child); owner.SetEditScope(null); owner.SetSelection([ordinary]); Invoke("RefreshGroupCanvasContext");
            check("a selected ordinary group does not expose entered-group configuration", layers.CurrentGroupContext is null);
            layers.PrepareForShow(); layers.EnterGroup(ordinary); layers.PrepareForShow();
            var card = layers.CurrentGroupContext!;
            check("entering an ordinary group offers both editable views and direct prefab export", card is { Storage: GroupStorageKind.Ordinary, IsEntered: true, Temporary: not null, Root: not null, Export: not null, Detach: null });
            var childRows = layers.VisibleNodes.Select(row => row.Id).ToArray(); int history = owner.UndoCount;
            using (var isolated = (CanvasPreviewSnapshot)Invoke("CaptureCanvasPreview")!)
                check("ordinary temporary canvas renders only that group's actual children", SceneEvaluation.Build(isolated.Document).Leaves.Select(leaf => leaf.HostNodeId).SequenceEqual([child.Id]) && owner.EditScopeId == ordinary);
            Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject);
            using (var full = (CanvasPreviewSnapshot)Invoke("CaptureCanvasPreview")!)
                check("ordinary root view restores other artwork without changing its list", SceneEvaluation.Build(full.Document).Leaves.Count > 1 && childRows.SequenceEqual(layers.VisibleNodes.Select(row => row.Id)) && owner.EditScopeId == ordinary && owner.UndoCount == history);
            string exported = Path.Combine(root, "ordinary.floatprefab");
            Await((Task)Invoke("ExportGroupAsync", owner, ordinary, exported)!);
            var package = Await(prefabStore.LoadAsync(exported)).Package;
            check("ordinary group saves a named prefab without conversion or history", package.Definitions.Single(definition => definition.Id == package.EntryDefinitionId) is { Name: "Ordinary badge" } entry &&
                entry.Graph.Elements.Any(element => element.Id == child.Id) && new SceneGraph(owner.Document).FindGroup(ordinary) is not null && owner.UndoCount == history && owner.EditScopeId == ordinary);
            Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary);
            owner.BeginTransaction("foreign transaction"); long transaction = owner.TransactionId;
            Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject);
            check("view switching never cancels an unrelated transaction", owner.IsTransactionActive && owner.TransactionId == transaction && Field<GroupCanvasMode>(desktop, "_groupCanvasMode") == GroupCanvasMode.Temporary);
            owner.Cancel(); owner.SetEditScope(null);

            Guid sharedCopy = owner.PlacePrefab(created.DefinitionId, LayerPlacement.Identity, "Unlink this copy");
            Await((Task)Invoke("EditPrefabInstanceAsync", sharedCopy)!);
            var sharedContext = Field<PrefabEditContext>(desktop, "_prefabEditContext"); history = owner.UndoCount;
            Await((Task)Invoke("DetachEditingGroupAsync", sharedContext)!);
            check("unlinking an entered shared instance keeps browsing its ordinary group", ReferenceEquals(editor.Session, owner) && owner.EditScopeId == sharedCopy &&
                new SceneGraph(owner.Document).FindGroup(sharedCopy)?.Name == "Unlink this copy" && new SceneGraph(owner.Document).FindInstance(created.InstanceId) is not null && owner.UndoCount == history + 1 && layers.CurrentGroupContext?.Storage == GroupStorageKind.Ordinary);
            owner.SetEditScope(null); owner.Undo();
            check("unlinking one instance is reversible and leaves other copies shared", new SceneGraph(owner.Document).FindInstance(sharedCopy)?.DefinitionId == created.DefinitionId && new SceneGraph(owner.Document).FindInstance(created.InstanceId)?.DefinitionId == created.DefinitionId);

            Guid externalCopy = owner.PlacePrefab(imported.DefinitionId, LayerPlacement.Identity, "External copy");
            string diskBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            Await((Task)Invoke("EditPrefabInstanceAsync", externalCopy)!);
            var externalContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
            Await((Task)Invoke("DetachEditingGroupAsync", externalContext)!);
            check("unlinking an external group preserves its file and other file-linked instances", new SceneGraph(owner.Document).FindGroup(externalCopy) is not null && owner.EditScopeId == externalCopy &&
                owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId).Source?.IsLinked == true &&
                new SceneGraph(owner.Document).FindInstance(imported.InstanceId) is not null && diskBefore == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            owner.SetEditScope(null);
        }
    }
    private static byte[] Png(SKColor? color = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4)); surface.Canvas.Clear(color ?? SKColors.CornflowerBlue);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Hidden)!.GetValue(instance)!;
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext()); Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> complete)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) }; var frame = new DispatcherFrame(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        timer.Tick += (_, _) => { if (complete() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start(); try { if (!complete()) Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private sealed class Answer : IDisposable
    {
        private readonly DispatcherTimer timer;
        internal int Count { get; private set; }
        internal Answer(Application app, string key)
        {
            timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var button = app.Windows.OfType<Window>().Where(window => window.IsVisible).SelectMany(window => Descendants(window)).OfType<Button>().FirstOrDefault(button => Equals(button.Content, L.Get(key)));
                if (button is null) return; timer.Stop(); Count++; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            };
            timer.Start();
        }
        public void Dispose() => timer.Stop();
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var nested in Descendants(child)) yield return nested;
            }
        }
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5d01, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5d02, 7, key + 1)) continue; UnregisterHotKey(0, 0x5d02); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5d01); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    private static string DesktopName()
    {
        var name = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return name.ToString();
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
