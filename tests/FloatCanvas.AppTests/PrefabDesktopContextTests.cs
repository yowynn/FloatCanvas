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
        ResourceStatusCache(desktop, owner, created.DefinitionId, check);
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
        Invoke("ReturnFromPrefabContext", Field<PrefabEditContext>(desktop, "_prefabEditContext"));
        check("discarding an actual definition context restores the owner without a host history change", ReferenceEquals(editor.Session, owner) && owner.UndoCount == before && !owner.Assets.TryGet(added, out _));
        check("returning from a discarded definition context retains the owner's camera", JsonSerializer.Serialize(owner.Document.Canvas) == ownerCamera);
        check("discarding the context returns the group toolbar to an unentered selection", layers.CurrentGroupContext?.IsEntered != true);

        Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!);
        var outer = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        editor.Session.SetSelection(editor.Session.Document.Elements.Select(element => element.Id));
        var nested = editor.Session.CreatePrefabFromSelection("Nested"); Await((Task)Invoke("EditPrefabDefinitionAsync", nested.DefinitionId)!);
        editor.Session.ChangeEditingSubprojectCanvas("local origin", settings => settings.Origin = new(13, -19));
        editor.Session.Execute("nested width", document => document.Elements.Single().Width = 88);
        var snapshot = (WorkspaceState)Invoke("SnapshotWorkspace")!;
        check("actual workspace snapshot includes both pending edit contexts and committed root content", PrefabDraftState.Enumerate(snapshot.PendingPrefabEdit).Count == 2 && SceneEvaluation.Build(snapshot.LiveDocument).Leaves.Single().RenderElement.Width == 30);
        var recovery = new RecoveryStore(Path.Combine(root, "recovery")); Await(recovery.SaveAsync(snapshot, owner.Assets));
        var loaded = Await(recovery.LoadAsync())!;
        Invoke("ReturnFromPrefabContext", Field<PrefabEditContext>(desktop, "_prefabEditContext"));
        Invoke("ReturnFromPrefabContext", Field<PrefabEditContext>(desktop, "_prefabEditContext"));
        Invoke("RestorePrefabRecoveryState", loaded.State.PendingPrefabEdit!);
        check("actual recovery hook resumes the inner real source graph and parent editing context", Field<PrefabEditContext>(desktop, "_prefabEditContext").Parent is not null && editor.Session.Document.Elements.Single().Width == 88);
        check("recovery restores the detached entry origin from its draft envelope", editor.Session.GetRootSubprojectCanvas().Origin == new PointD(13, -19));
        Await((Task)Invoke("CompleteCurrentPrefabAsync")!); Await((Task)Invoke("CompleteCurrentPrefabAsync")!);
        check("finishing recovered nested contexts publishes once to the root owner", ReferenceEquals(editor.Session, owner) && owner.UndoCount == before + 1 && SceneEvaluation.Build(owner.Document).Leaves.Single().RenderElement.Width == 88);

        var source = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 320, 240), Elements = [new() { Kind = ElementKind.Rectangle, Width = 18, Height = 21 }] };
        source.Canvas.Grid.PlaneCellSize = 37; source.Background = new() { Kind = ElementKind.Image, AssetId = owner.Assets.Add(Png()), Width = 320, Height = 240, Locked = true };
        var auxiliaryDefinition = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Ellipse, Width = 20, Height = 30 }] } };
        source.Definitions.Add(auxiliaryDefinition);
        var sourceGraph = new SceneGraph(source);
        sourceGraph.InsertInstance(new() { DefinitionId = auxiliaryDefinition.Id, Name = "foreground retained" }, rootCanvas: RootCanvasKind.Foreground);
        sourceGraph.InsertInstance(new() { DefinitionId = auxiliaryDefinition.Id, Name = "background retained" }, rootCanvas: RootCanvasKind.Background);
        string path = Path.Combine(root, "source.floatcanvas"); var projects = new ProjectStore(); Await(projects.SaveAsync(path, source, owner.Assets));
        string camera = JsonSerializer.Serialize(source.Canvas), background = JsonSerializer.Serialize(source.Background);
        var resolver = new PrefabLinkResolver(); var prepared = Await(resolver.PrepareAsync(owner, path, null));
        var imported = owner.ImportPrefab(prepared.Package, prepared.Assets, LayerPlacement.Identity, prepared.Source);
        Await((Task)Invoke("EditPrefabInstanceAsync", imported.InstanceId)!);
        var sourceContext = RestoreLegacyWritableSource();
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
        check("editing a linked full project and saving preserves all source root planes", written.Document.RootCanvasAssignments.Count == 2 && written.Document.Instances.Count == 2 &&
            written.Document.Instances.All(instance => SceneCanvasRoots.Resolve(written.Document, instance.Id) != RootCanvasKind.Main));
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
        packageEntry.PreviewCanvas = new() { Origin = new(29, -17), Viewport = new(-20, -30, 720, 480), LayerOpacity = .71,
            Background = new() { Kind = ElementKind.Image, AssetId = oldImage, Width = 720, Height = 480 } };
        packageEntry.PreviewCanvas.Canvas.BackgroundColor = 0xFF123678;
        var sourcePackage = new PrefabPackage { EntryDefinitionId = packageEntry.Id, Definitions = [packageEntry, imageDefinition] };
        Guid sourceIdentity = sourcePackage.ResourceId, sourceEntry = sourcePackage.EntryDefinitionId; PointD sourcePivot = packageEntry.Pivot;
        Await(prefabStore.SaveAsync(prefabPath, sourcePackage, owner.Assets));
        var prefabCandidate = Await(resolver.PrepareAsync(owner, prefabPath, null));
        var linkedPrefab = owner.ImportPrefab(prefabCandidate.Package, prefabCandidate.Assets, LayerPlacement.Identity, prefabCandidate.Source);
        Guid secondPrefab = owner.PlacePrefab(linkedPrefab.DefinitionId, new(Affine2D.Translation(160, 90), 1));
        Await((Task)Invoke("EditPrefabSourceAsync", linkedPrefab.DefinitionId)!);
        var prefabContext = RestoreLegacyWritableSource();
        check("linked prefab enters its real source entry with the complete dependency table", prefabContext.IsPrefabSource && prefabContext.SourceEntryDefinitionId == sourceEntry &&
            editor.Session.Document.ResourceId == sourceIdentity && editor.Session.Document.Definitions.Count == 2 && editor.Session.Document.Instances.Single().DefinitionId == imageDefinition.Id);
        check("linked prefab opens using its own preview canvas and origin", JsonSerializer.Serialize(editor.Session.GetRootSubprojectCanvas()) == JsonSerializer.Serialize(packageEntry.PreviewCanvas));
        editor.Session.ChangeEditingSubprojectCanvas("source preview origin", settings => settings.Origin = new(-41, 23));
        string newImage = owner.Assets.Add(Png(SKColors.Fuchsia));
        editor.Session.Execute("edit prefab source", document => document.Elements.Single().Width = 81);
        editor.Session.Add(new() { Kind = ElementKind.Image, AssetId = newImage, Width = 7, Height = 8, Transform = Affine2D.Translation(90, 0) });
        Invoke("PruneDocumentResources");
        check("editing a linked prefab retains original dependency image bytes alongside new draft resources", owner.Assets.Get(oldImage).Data.Span.SequenceEqual(oldImageBytes) && owner.Assets.TryGet(newImage, out _));
        Await((Task)Invoke("TryHandlePrefabSaveAsync", false)!);
        var savedPrefab = Await(prefabStore.LoadAsync(prefabPath));
        check("saving source writes its changed independent origin and preview background", savedPrefab.Package.Definitions.Single(definition => definition.Id == sourceEntry).PreviewCanvas is { Origin: var savedOrigin, Background.AssetId: var savedBackground } &&
            savedOrigin == new PointD(-41, 23) && savedBackground == oldImage);
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
        prefabContext = RestoreLegacyWritableSource();
        editor.Session.Execute("unpublished local source edit", document => document.Elements.Single(element => element.Kind == ElementKind.Rectangle).Width = 111);
        var externalPrefab = Await(prefabStore.LoadAsync(prefabPath));
        externalPrefab.Package.Definitions.Single(definition => definition.Id == sourceEntry).Graph.Elements.Single(element => element.Kind == ElementKind.Rectangle).Width = 123;
        Await(prefabStore.SaveAsync(prefabPath, externalPrefab.Package, externalPrefab.Assets)); byte[] conflictVersion = File.ReadAllBytes(prefabPath);
        bool conflictSaved; int conflictDialogs;
        using (var answer = new Answer(app, "Project.KeepCurrent")) { conflictSaved = Await((Task<bool>)Invoke("SavePrefabSourceAsync", prefabContext, false)!); conflictDialogs = answer.Count; }
        check("prefab expected-file-version conflict does not overwrite the external generation", !conflictSaved && conflictDialogs == 1 && File.ReadAllBytes(prefabPath).SequenceEqual(conflictVersion));
        check("prefab save conflict retains the user's source edit and all referenced images", ReferenceEquals(editor.Session, prefabContext.Session) &&
            editor.Session.Document.Elements.Single(element => element.Kind == ElementKind.Rectangle).Width == 111 && owner.Assets.Get(oldImage).Data.Span.SequenceEqual(oldImageBytes));
        Invoke("ReturnFromPrefabContext", Field<PrefabEditContext>(desktop, "_prefabEditContext"));

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
        Invoke("ReturnFromPrefabContext", Field<PrefabEditContext>(desktop, "_prefabEditContext"));
        OrdinaryAndDetach();
        SourceCopyPublication();
        check("desktop context fixture never exposes overlay HWNDs on the user's desktop", host.Diagnostics.All(pair => pair.Bounds == bounds) && !host.IsKeyboardIsolationActive);
        Console.WriteLine("PREFAB_DESKTOP_EVIDENCE " + root);
        object? Invoke(string method, params object?[] arguments) => typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, arguments);

        // New linked navigation is read-only. Retain lower-level CAS/save coverage
        // for writable source drafts restored from older recovery files.
        PrefabEditContext RestoreLegacyWritableSource()
        {
            var previous = Field<PrefabEditContext>(desktop, "_prefabEditContext");
            check("new external navigation opens a read-only source", previous.Session.IsReadOnly);
            bool rejected = false;
            try { previous.Session.Execute("forbidden external edit", d => d.Elements.Clear()); }
            catch (InvalidOperationException) { rejected = true; }
            check("external source rejects content mutations", rejected);
            var session = new EditorSession(previous.Session.Document.Clone(), owner.Assets,
                independentCanvasOrigin: previous.Session.GetRootSubprojectCanvas().Origin);
            var context = new PrefabEditContext(previous.Owner, session, previous.DefinitionId, previous.ExpectedRevision,
                previous.ReturnOrigin, previous.ReturnTool, previous.Parent, previous.SourcePath, previous.SourceEntryDefinitionId)
                { EnteredInstanceId = previous.EnteredInstanceId };
            var files = (System.Collections.IDictionary)typeof(DesktopSession).GetField("_documentFiles", Hidden)!.GetValue(desktop)!;
            files[session] = files[previous.Session];
            typeof(DesktopSession).GetField("_prefabEditContext", Hidden)!.SetValue(desktop, context);
            Invoke("SwitchPrefabContext", session);
            return context;
        }

        void SourceCopyPublication()
        {
            bool Busy() => (bool)typeof(DesktopSession).GetProperty("_documentBusy", Hidden)!.GetValue(desktop)!;
            void SetBusy(bool value) => typeof(DesktopSession).GetProperty("_documentBusy", Hidden)!.SetValue(desktop, value);
            Task<bool> Complete(Func<string?> chooseCopyPath) => (Task<bool>)Invoke("CompleteCurrentPrefabWithCopyPathAsync", chooseCopyPath)!;
            owner.ReplaceDocument(new SceneDocument { Viewport = new(0, 0, 640, 360) });
            string origin = Path.Combine(root, "cas-origin.floatprefab"), copy = Path.Combine(root, "cas-saved-copy.floatprefab");
            var sourceBox = new CanvasElement { Kind = ElementKind.Rectangle, Width = 245, Height = 140 };
            var sourceLabel = new CanvasElement { Kind = ElementKind.Text, Text = "Source", Width = 200, Height = 40 };
            var entry = new PrefabDefinition { Name = "CAS orange", Graph = new() { Elements = [sourceBox, sourceLabel] } };
            var package = new PrefabPackage { EntryDefinitionId = entry.Id, Definitions = [entry] };
            var store = new PrefabStore(); Await(store.SaveAsync(origin, package, owner.Assets));
            var candidate = Await(resolver.PrepareAsync(owner, origin, null));
            var imported = owner.ImportPrefab(candidate.Package, candidate.Assets, new(Affine2D.Translation(20, 20), 1), candidate.Source);
            Guid second = owner.PlacePrefab(imported.DefinitionId, new(Affine2D.Translation(370, 20), 1));
            Await((Task)Invoke("EditPrefabSourceAsync", imported.DefinitionId)!);
            var context = RestoreLegacyWritableSource();
            context.Session.Add(new() { Kind = ElementKind.Rectangle, Width = 128, Height = 74, Transform = Affine2D.Translation(93, 178) });
            string draft = JsonSerializer.Serialize(context.Session.Document);
            byte[] original = File.ReadAllBytes(origin);
            if (original[^22] != 0x50 || original[^21] != 0x4b || original[^2] != 0 || original[^1] != 0)
                throw new InvalidDataException("CAS fixture requires a generated ZIP without an existing comment.");
            byte[] comment = Encoding.UTF8.GetBytes("private desktop CAS comment only");
            byte[] external = original[..^2].Concat(BitConverter.GetBytes((ushort)comment.Length)).Concat(comment).ToArray();
            File.WriteAllBytes(origin, external);
            string conflictHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(external));
            bool canceled;
            using (var answer = new Answer(app, "Project.SaveCopy"))
                canceled = Await(Complete(() => null));
            check("CAS SaveCopy cancel retains the real three-node draft original source path and previous busy state", !canceled &&
                ReferenceEquals(editor.Session, context.Session) && Path.GetFullPath(context.SourcePath!) == Path.GetFullPath(origin) &&
                JsonSerializer.Serialize(context.Session.Document) == draft && !Busy() &&
                File.ReadAllBytes(origin).SequenceEqual(external) && !File.Exists(copy));
            SetBusy(true);
            try
            {
                using var answer = new Answer(app, "Project.SaveCopy");
                check("CAS SaveCopy cancellation preserves an already busy enclosing operation", !Await(Complete(() => null)) && Busy() &&
                    ReferenceEquals(editor.Session, context.Session) && JsonSerializer.Serialize(context.Session.Document) == draft);
            }
            finally { SetBusy(false); }
            bool failedChoice = false;
            try
            {
                using var answer = new Answer(app, "Project.SaveCopy");
                Await(Complete(() => throw new IOException("CAS fixture copy-path failure")));
            }
            catch (IOException ex) when (ex.Message == "CAS fixture copy-path failure") { failedChoice = true; }
            check("CAS SaveCopy failure releases busy state and retains its unsaved source draft", failedChoice && !Busy() &&
                ReferenceEquals(editor.Session, context.Session) && JsonSerializer.Serialize(context.Session.Document) == draft && File.ReadAllBytes(origin).SequenceEqual(external));
            string ownerBefore = JsonSerializer.Serialize(owner.Document); int undoBefore = owner.UndoCount;
            var copyRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backgroundRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool explicitHeld = false; int backgroundReads = 0; long explicitRevision = -1;
            var liveResolver = Field<PrefabLinkResolver>(desktop, "_prefabLinkResolver");
            var previousRead = liveResolver.BeforeReadAsync;
            Task<bool>? completing = null;
            liveResolver.BeforeReadAsync = _ =>
            {
                var callers = new System.Diagnostics.StackTrace().GetFrames().Select(frame => frame.GetMethod()?.DeclaringType?.Name + "." + frame.GetMethod()?.Name).ToArray();
                if (callers.Any(name => name.Contains("RefreshPrefabLinkAsync", StringComparison.Ordinal)))
                {
                    explicitHeld = true; explicitRevision = owner.SceneRevision; backgroundRead.TrySetResult(); return copyRead.Task;
                }
                if (callers.Any(name => name.Contains("CheckOpenedPrefabLinksAsync", StringComparison.Ordinal)))
                { backgroundReads++; return explicitHeld ? Task.CompletedTask : backgroundRead.Task; }
                throw new InvalidOperationException("Unexpected caller reached the CAS publication read gate.");
            };
            try
            {
                // A genuine old-source notification is queued while the source draft is retained.
                Await((Task)Invoke("OnPrefabSourceSavedAsync", origin)!);
                using var answer = new Answer(app, "Project.SaveCopy");
                completing = Complete(() => copy);
                Wait(() => explicitHeld);
                check("CAS publication repro holds the actual explicit copy Prepare after returning to its owner", explicitHeld &&
                    ReferenceEquals(editor.Session, owner) && context.SourcePath == copy);
                check("CAS SaveCopy writes a real three-node package before owner publication", Await(store.LoadAsync(copy)).Package.Definitions.Single(definition => definition.Id == entry.Id).Graph.Elements.Count == 3);
                Await((Task)Invoke("CheckOpenedPrefabLinksAsync", owner)!);
                var beforeRelease = owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId);
                check("source completion blocks the old background candidate until explicit publication", Busy() &&
                    owner.SceneRevision == explicitRevision && backgroundReads == 0 && beforeRelease.Graph.Elements.Count == 2);
                copyRead.TrySetResult();
                check("CAS copy publication completes successfully", Await(completing));
                string copyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(copy)));
                var published = owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId);
                check("CAS SaveCopy completion binds its new path revision hash and all three source nodes", Path.GetFullPath(published.Source!.Locator!) == Path.GetFullPath(copy) &&
                    published.Source.LastAcceptedFileSha256 == copyHash && published.Source.LastAcceptedContentRevision == Await(store.LoadAsync(copy)).Package.Definitions.Single(definition => definition.Id == entry.Id).Revision &&
                    published.Graph.Elements.Count == 3 && new[] { imported.InstanceId, second }.All(id => SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(id).Count == 3));
                Await((Task)Invoke("DrainPrefabSourcesAsync")!);
                Wait(() => !Field<bool>(desktop, "_drainingPrefabSources"));
                published = owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId);
                check("queued old-source notifications drain against the new binding without reverting it", !Field<bool>(desktop, "_drainingPrefabSources") &&
                    Field<HashSet<string>>(desktop, "_prefabPendingSources").Count == 0 && Path.GetFullPath(published.Source!.Locator!) == Path.GetFullPath(copy) &&
                    published.Source.LastAcceptedFileSha256 == copyHash && published.Graph.Elements.Count == 3 && owner.UndoCount == undoBefore + 1);
                check("CAS completion never replaces the external conflicting source generation", File.ReadAllBytes(origin).SequenceEqual(external) &&
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(origin))) == conflictHash);
                check("CAS copy publication is one undoable owner update and releases its busy gate", owner.UndoCount == undoBefore + 1 && !Busy() && owner.Undo() &&
                    JsonSerializer.Serialize(owner.Document) == ownerBefore);
            }
            finally
            {
                copyRead.TrySetResult(); backgroundRead.TrySetResult();
                liveResolver.BeforeReadAsync = previousRead;
                if (completing is not null) Await(completing);
            }
        }

        void OrdinaryAndDetach()
        {
            // The preceding passive-open fixture deliberately exited editing.
            // This section tests editing geometry without acquiring real input.
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host, true);
            owner.SetEditScope(null);
            Guid ordinary = owner.CreateEmptyGroup("Ordinary badge"); owner.SetEditScope(ordinary);
            var child = new CanvasElement { Kind = ElementKind.Ellipse, Width = 14, Height = 11 };
            owner.Add(child); owner.SetEditScope(null); owner.SetSelection([ordinary]); Invoke("RefreshGroupCanvasContext");
            check("a selected ordinary group exposes only the persistent root-view choice", layers.CurrentGroupContext is { IsRootContext: true, IsEntered: false, Export: null, Detach: null });
            layers.PrepareForShow(); layers.EnterGroup(ordinary); layers.PrepareForShow();
            var card = layers.CurrentGroupContext!;
            check("entering an ordinary group offers both editable views and direct prefab export", card is { Storage: GroupStorageKind.Ordinary, IsEntered: true, Temporary: not null, Root: not null, Export: not null, Detach: null });
            var childRows = layers.VisibleNodes.Select(row => row.Id).ToArray(); int history = owner.UndoCount;
            Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary);
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
            // The source changes after the owner accepted its offline generation.
            var retainedDefinition = JsonSerializer.Serialize(owner.Document.Definitions.Single(d => d.Id == imported.DefinitionId));
            source.Elements.Single().Width = 137;
            Await(projects.SaveAsync(path, source, owner.Assets));
            string diskBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            Await((Task)Invoke("EditPrefabInstanceAsync", externalCopy)!);
            var externalContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
            check("online source view sees the latest generation before detaching", externalContext.Session.Document.Elements.Single().Width == 137);
            string viewedCanvas = JsonSerializer.Serialize(externalContext.Session.GetRootSubprojectCanvas());
            history = owner.UndoCount;
            Await((Task)Invoke("DetachEditingGroupAsync", externalContext)!);
            check("detaching online source retains the content currently being viewed",
                SceneEvaluation.Build(owner.Document).Leaves.Any(leaf => leaf.RenderElement.Width == 137 &&
                    new SceneGraph(owner.Document).AncestorIds(leaf.HostNodeId).Contains(externalCopy)));
            check("detaching online source leaves the other linked instances on their accepted generation",
                JsonSerializer.Serialize(owner.Document.Definitions.Single(d => d.Id == imported.DefinitionId)) == retainedDefinition);
            check("detaching online source retains its independent canvas settings",
                JsonSerializer.Serialize(new SceneGraph(owner.Document).FindGroup(externalCopy)!.PreviewCanvas) == viewedCanvas);
            check("unlinking an external group preserves its file and other file-linked instances", new SceneGraph(owner.Document).FindGroup(externalCopy) is not null && owner.EditScopeId == externalCopy &&
                owner.Document.Definitions.Single(definition => definition.Id == imported.DefinitionId).Source?.IsLinked == true &&
                new SceneGraph(owner.Document).FindInstance(imported.InstanceId) is not null && diskBefore == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            owner.SetEditScope(null);
            check("detaching online source publishes exactly one undo step", owner.UndoCount == history + 1);
            owner.Undo();
            check("undo restores the original external instance and accepted source",
                new SceneGraph(owner.Document).FindInstance(externalCopy)?.DefinitionId == imported.DefinitionId &&
                JsonSerializer.Serialize(owner.Document.Definitions.Single(d => d.Id == imported.DefinitionId)) == retainedDefinition);
            owner.Redo();
            check("redo restores detached current content and its canvas",
                JsonSerializer.Serialize(new SceneGraph(owner.Document).FindGroup(externalCopy)!.PreviewCanvas) == viewedCanvas &&
                SceneEvaluation.Build(owner.Document).Leaves.Any(leaf => leaf.RenderElement.Width == 137 &&
                    new SceneGraph(owner.Document).AncestorIds(leaf.HostNodeId).Contains(externalCopy)));
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
    private static void ResourceStatusCache(DesktopSession desktop, EditorSession owner, Guid definitionId, Action<string, bool> check)
    {
        object? Invoke(string name, params object?[] values) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, values);
        var source = new ExternalPrefabSource { Locator = "missing-status-source.floatprefab", IsRelative = false,
            SourceResourceId = Guid.NewGuid(), EntryKind = PrefabSourceKind.PrefabDefinition, EntryDefinitionId = Guid.NewGuid(), LastAcceptedFileSha256 = new string('B', 64) };
        owner.Execute("Link fixture", document => document.Definitions.Single(definition => definition.Id == definitionId).Source = source.Clone());
        Invoke("ShowPrefabResources");
        var resources = Field<PrefabResourcesWindow>(desktop, "_prefabResources"); resources.Select(definitionId);
        var footer = Field<TextBlock>(resources, "status"); string missing = L.Get("Prefab.SourceMissing");
        Invoke("SetPrefabLinkStatus", owner, definitionId, "Prefab.SourceMissing"); resources.SetStatus(missing);
        check("actual Desktop resource provider shows the bound missing-source observation", resources.Rows.Single(row => row.Id == definitionId).Summary.Contains(missing, StringComparison.Ordinal));
        Await(resources.PerformAsync(PrefabResourceAction.Embed));
        check("actual Desktop embed command clears footer and external-link row status", footer.Text.Length == 0 && !resources.Rows.Single(row => row.Id == definitionId).Linked &&
            !resources.Rows.Single(row => row.Id == definitionId).Summary.Contains(missing, StringComparison.Ordinal));
        owner.Undo(); resources.Refresh();
        check("actual Desktop undo restores only the matching old link observation", resources.Rows.Single(row => row.Id == definitionId).Linked &&
            resources.Rows.Single(row => row.Id == definitionId).Summary.Contains(missing, StringComparison.Ordinal) && footer.Text.Length == 0);
        owner.Redo(); resources.Refresh();
        check("actual Desktop redo hides the preserved external-link observation", !resources.Rows.Single(row => row.Id == definitionId).Summary.Contains(missing, StringComparison.Ordinal));
        var relinked = source.Clone(); relinked.Locator = "different-status-source.floatprefab";
        owner.Execute("Relink fixture", document => document.Definitions.Single(definition => definition.Id == definitionId).Source = relinked);
        resources.Refresh();
        check("actual Desktop resource cache cannot label a new locator with an old error", resources.Rows.Single(row => row.Id == definitionId).Linked &&
            !resources.Rows.Single(row => row.Id == definitionId).Summary.Contains(missing, StringComparison.Ordinal));
        owner.Undo(); owner.Undo(); owner.Undo(); resources.Refresh();
        check("status fixture restores the original embedded source metadata", owner.Document.Definitions.Single(definition => definition.Id == definitionId).Source?.IsLinked != true);
        SessionWindowCoordinator.Hide(resources);
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
