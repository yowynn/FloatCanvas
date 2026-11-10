using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;

/// <summary>The real resource-panel import command on an undisplayed, isolated Windows desktop.</summary>
internal static class LinkedContentDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal) || PresentationDesktopAvailability.IsCurrentInputDesktop())
            throw new InvalidOperationException("Use --isolated-ui-desktop --linked-content-desktop-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private child process.");
        string root = Path.Combine(output ?? Path.GetTempPath(), "linked-content-desktop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys(); new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-24000, -23000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("linked-content-private", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false; Field<Window>(desktop, "_toolbar").Opacity = 0;
        ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(host))?.Dispose(); typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(host, null);
        var editor = Field<EditorController>(desktop, "_editor"); var owner = editor.Session;
        var projects = new ProjectStore(); var source = new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 50, Height = 24 }], Viewport = new(0, 0, 320, 180) };
        string pathA = Path.Combine(root, "Resource original.floatcanvas"), pathB = Path.Combine(root, "Resource independent copy.floatcanvas");
        Await(projects.SaveAsync(pathA, source, owner.Assets));
        int history = owner.UndoCount; Await(Import(pathA));
        Guid definitionA = owner.Document.Instances.Single().DefinitionId;
        check("resource-panel Link creates a named path-linked snapshot in one history entry", owner.UndoCount == history + 1 &&
            owner.Document.Instances.Single().Name == "Resource original" && Path.GetFullPath(owner.Document.Definitions.Single(item => item.Id == definitionA).Source!.Locator!) == Path.GetFullPath(pathA));
        source.Elements.Single().Width = 91; Await(projects.SaveAsync(pathA, source, owner.Assets));
        string beforeRepeat = JsonSerializer.Serialize(owner.Document); history = owner.UndoCount;
        Await(Import(pathA));
        Console.WriteLine("RESOURCE_REPEAT_WIDTHS " + string.Join(',', SceneEvaluation.Build(owner.Document).Leaves.Select(leaf => leaf.RenderElement.Width)));
        check("resource-panel re-link accepts changed source bytes for both old and new occurrences", owner.Document.Instances.Count == 2 &&
            owner.Document.Instances.All(instance => instance.DefinitionId == definitionA) && SceneEvaluation.Build(owner.Document).Leaves.All(leaf => leaf.RenderElement.Width == 91));
        check("resource-panel refresh plus new occurrence is exactly one undo", owner.UndoCount == history + 1 && owner.Undo() && JsonSerializer.Serialize(owner.Document) == beforeRepeat && owner.Redo());
        source.Elements.Single().Width = 123; Await(projects.SaveAsync(pathB, source, owner.Assets));
        history = owner.UndoCount; Await(Import(pathB));
        Guid definitionB = owner.Document.Instances.Last().DefinitionId;
        check("different files with the same resource identity retain independent external bindings", definitionB != definitionA &&
            owner.Document.Definitions.Single(item => item.Id == definitionA).Source!.Locator == Path.GetFullPath(pathA) &&
            owner.Document.Definitions.Single(item => item.Id == definitionB).Source!.Locator == Path.GetFullPath(pathB) &&
            SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(owner.Document.Instances.Last().Id).Single().RenderElement.Width == 123 && owner.UndoCount == history + 1);
        Directory.CreateDirectory(Path.Combine(root, "canonical"));
        Await(Import(Path.Combine(root, "canonical", "..", Path.GetFileName(pathB))));
        check("canonical spellings of the same path share their accepted linked definition", owner.Document.Instances.Count(instance => instance.DefinitionId == definitionB) == 2 && owner.Document.Definitions.Count == 2);
        Guid offlineInstance = owner.Document.Instances.First(instance => instance.DefinitionId == definitionA).Id;
        string offlinePath = pathA + ".offline"; File.Move(pathA, offlinePath);
        string offlineOwner = JsonSerializer.Serialize(owner.Document); Guid acceptedRevision = owner.Document.Definitions.Single(item => item.Id == definitionA).Revision;
        Exception? enterError = null;
        try { Await((Task)Call("EditPrefabInstanceAsync", offlineInstance)!); }
        catch (Exception error) { enterError = error; }
        Console.WriteLine("OFFLINE_ENTER_RESULT " + (enterError is null ? "entered" : enterError.GetType().Name + ": " + enterError.Message));
        check("the real layer Enter route can browse an offline link's accepted internal snapshot", enterError is null && !ReferenceEquals(editor.Session, owner) &&
            SceneEvaluation.Build(editor.Session.Document).Leaves.Single().RenderElement.Width == 91);
        var offlineContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        var readOnly = editor.Session; string readOnlyBefore = JsonSerializer.Serialize(readOnly.Document);
        int readOnlyAssets = readOnly.Assets.Count, readOnlyHistory = readOnly.UndoCount, externalRuns = 0;
        RejectReadOnly("Add", () => readOnly.Add(new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 }));
        RejectReadOnly("RenameLayer", () => readOnly.RenameLayer(readOnly.Document.Elements.Single().Id, "Cannot rename cached source"));
        RejectReadOnly("ExecuteSettings", () => readOnly.ExecuteSettings("cached setting", document => document.Canvas.BackgroundColor = 0xffff0000));
        RejectReadOnly("ExecuteExternal", () => readOnly.ExecuteExternal("cached resource", () => externalRuns++, () => { }, () => { }));
        check("read-only resource transactions never invoke an external mutation callback", externalRuns == 0);
        Await(Import(pathB)); Await((Task)Call("ImportPrefabFileAsync", false, pathB)!);
        check("direct linked and embedded import methods cannot bypass offline read-only eligibility", JsonSerializer.Serialize(readOnly.Document) == readOnlyBefore &&
            readOnly.UndoCount == readOnlyHistory && readOnly.Assets.Count == readOnlyAssets);
        bool handledSave = Await((Task<bool>)Call("TryHandlePrefabSaveAsync", false)!);
        check("Ctrl S in an offline cache is handled without publishing or recreating the source", handledSave && ReferenceEquals(editor.Session, readOnly) &&
            JsonSerializer.Serialize(owner.Document) == offlineOwner && !File.Exists(pathA));
        var recovery = new WorkspaceState { LiveDocument = owner.Document.Clone() }; Call("AddPrefabRecoveryState", recovery);
        var recoveryStore = new RecoveryStore(Path.Combine(root, "offline-recovery")); Await(recoveryStore.SaveAsync(recovery, owner.Assets));
        var recovered = Await(recoveryStore.LoadAsync());
        check("recovery persists the owner without turning a read-only cache into an editable pending draft", recovery.PendingPrefabEdit is null && recovered?.State.PendingPrefabEdit is null &&
            recovered is not null && JsonSerializer.Serialize(recovered.State.LiveDocument) == offlineOwner && !File.Exists(pathA));
        Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        using (var snapshot = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!)
            check("offline internal browsing can preview the root without accepting a new revision", SceneEvaluation.Build(snapshot.Document).OccurrencesOfHostNode(offlineInstance).Single().RenderElement.Width == 91 &&
                JsonSerializer.Serialize(owner.Document) == offlineOwner && owner.Document.Definitions.Single(item => item.Id == definitionA).Revision == acceptedRevision && !File.Exists(pathA));
        history = owner.UndoCount;
        check("completing an offline cache returns without publishing a new accepted definition", Await((Task<bool>)Call("CompleteCurrentPrefabAsync")!) && ReferenceEquals(editor.Session, owner) &&
            owner.UndoCount == history && JsonSerializer.Serialize(owner.Document) == offlineOwner && !File.Exists(pathA));
        File.WriteAllText(pathA, "corrupted project fixture");
        Await((Task)Call("EditPrefabInstanceAsync", offlineInstance)!);
        var corruptContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        check("corrupted source opens its valid accepted snapshot with a format reason", corruptContext.IsOfflineSnapshot && corruptContext.IsReadOnlyView && corruptContext.SnapshotReason == "LayerExperience.SnapshotFormat" && JsonSerializer.Serialize(owner.Document) == offlineOwner);
        Await((Task<bool>)Call("CompleteCurrentPrefabAsync")!);
        Await(projects.SaveAsync(pathA, source, owner.Assets));
        using (var archive = System.IO.Compression.ZipFile.Open(pathA, System.IO.Compression.ZipArchiveMode.Update))
        {
            var manifest = archive.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonObject json;
            using (var input = manifest.Open()) json = System.Text.Json.Nodes.JsonNode.Parse(input)!.AsObject();
            json["version"] = 99999; manifest.Delete();
            using var outputManifest = archive.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(outputManifest, json);
        }
        Await((Task)Call("EditPrefabInstanceAsync", offlineInstance)!);
        corruptContext = Field<PrefabEditContext>(desktop,"_prefabEditContext");
        check("unsupported source version uses its saved snapshot with a format reason", corruptContext.IsOfflineSnapshot && corruptContext.SnapshotReason == "LayerExperience.SnapshotFormat" && JsonSerializer.Serialize(owner.Document) == offlineOwner);
        Await(projects.SaveAsync(pathA, source, owner.Assets));
        Await((Task)Call("EditPrefabSourceCoreAsync", definitionA, offlineInstance, corruptContext)!);
        var retriedContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        check("retry reads the recovered source without publishing the saved snapshot", retriedContext.ContentOrigin == PrefabContentOrigin.ExternalSource && retriedContext.Parent == corruptContext.Parent && JsonSerializer.Serialize(owner.Document) == offlineOwner);
        Await((Task<bool>)Call("CompleteCurrentPrefabAsync")!);
        var resolver = Field<PrefabLinkResolver>(desktop, "_prefabLinkResolver");
        foreach (var failure in new Exception[] { new UnauthorizedAccessException("access fixture"), new IOException("read fixture") })
        {
            resolver.BeforeReadAsync = _ => Task.FromException(failure);
            try
            {
                Await((Task)Call("EditPrefabInstanceAsync", offlineInstance)!);
                var failedContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
                check(failure.GetType().Name + " uses read-only snapshot without writing owner", failedContext.IsOfflineSnapshot && failedContext.SnapshotReason == "LayerExperience.SnapshotRead" && JsonSerializer.Serialize(owner.Document) == offlineOwner);
                Await((Task<bool>)Call("CompleteCurrentPrefabAsync")!);
            }
            finally { resolver.BeforeReadAsync = null; }
        }
        foreach (var failure in new Exception[] { new OperationCanceledException("cancel fixture"), new InvalidOperationException("identity fixture") })
        {
            resolver.BeforeReadAsync = _ => Task.FromException(failure); bool rejected = false;
            try { Await((Task)Call("EditPrefabInstanceAsync", offlineInstance)!); } catch (Exception) { rejected = true; }
            finally { resolver.BeforeReadAsync = null; }
            check(failure.GetType().Name + " does not silently open a snapshot", rejected && ReferenceEquals(editor.Session, owner) && JsonSerializer.Serialize(owner.Document) == offlineOwner);
        }
        File.Delete(pathA);
        Await((Task)Call("EditPrefabInstanceAsync", offlineInstance)!); offlineContext = Field<PrefabEditContext>(desktop, "_prefabEditContext");
        history = owner.UndoCount;
        Await((Task)Call("DetachEditingGroupAsync", offlineContext)!);
        check("offline internal browsing can detach its accepted contents without recreating the source file", ReferenceEquals(editor.Session, owner) &&
            new SceneGraph(owner.Document).FindGroup(offlineInstance) is not null && owner.EditScopeId == offlineInstance && owner.UndoCount == history + 1 && !File.Exists(pathA) &&
            owner.Document.Definitions.Single(item => item.Id == definitionA).Revision == acceptedRevision && owner.Document.Instances.Any(instance => instance.DefinitionId == definitionA));
        owner.SetEditScope(null);
        var nestedDefinition = new PrefabDefinition { Name = "Nested locked browser", Graph = new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 }] } };
        var nestedInstance = new PrefabInstance { DefinitionId = nestedDefinition.Id };
        var lockedDefinition = new PrefabDefinition { Name = "Locked browser", Graph = new() { Instances = [nestedInstance], RootOrder = [nestedInstance.Id] } };
        owner.Document.Definitions.Add(nestedDefinition); owner.Document.Definitions.Add(lockedDefinition);
        var lockedInstance = new SceneGraph(owner.Document).InsertInstance(new() { DefinitionId = lockedDefinition.Id }); owner.SetNodeLocked(lockedInstance.Id,true);
        string beforeLockedBrowse = JsonSerializer.Serialize(owner.Document); history = owner.UndoCount;
        Await((Task)Call("EditPrefabInstanceAsync", lockedInstance.Id)!);
        var lockedContext = Field<PrefabEditContext>(desktop,"_prefabEditContext");
        check("locked shared instance opens a read-only browsing session", lockedContext.LockedBrowsing && editor.Session.IsReadOnly);
        var nestedId = editor.Session.Document.Instances.Single().Id;
        Await((Task)Call("EditPrefabInstanceAsync", nestedId)!);
        check("nested shared browsing inherits locked read-only protection", Field<PrefabEditContext>(desktop,"_prefabEditContext").LockedBrowsing && editor.Session.IsReadOnly);
        Await((Task<bool>)Call("CompleteCurrentPrefabAsync")!); Await((Task<bool>)Call("CompleteCurrentPrefabAsync")!);
        check("leaving locked shared browsing publishes no owner change or history", ReferenceEquals(editor.Session,owner) && owner.UndoCount == history && JsonSerializer.Serialize(owner.Document) == beforeLockedBrowse);
        var lockedScopeGroup = owner.CreatePopulatedGroup("Locked resource browser"); owner.SetNodeLocked(lockedScopeGroup,true); owner.SetEditScope(lockedScopeGroup);
        history = owner.UndoCount;
        Await((Task)Call("EditPrefabDefinitionAsync", nestedDefinition.Id)!);
        check("resource editor inside a locked ordinary scope also opens read-only", editor.Session.IsReadOnly && Field<PrefabEditContext>(desktop,"_prefabEditContext").LockedBrowsing);
        Await((Task<bool>)Call("CompleteCurrentPrefabAsync")!);
        check("resource browsing restores the locked ordinary scope without publishing", ReferenceEquals(editor.Session,owner) && owner.EditScopeId == lockedScopeGroup && owner.UndoCount == history);
        check("isolated resource lifecycle never exposes overlay HWNDs or editing input", host.Diagnostics.All(pair => pair.Bounds == bounds) && !host.HasEditingInput);
        Console.WriteLine("LINKED_CONTENT_DESKTOP_EVIDENCE " + root);

        Task Import(string path) => (Task)typeof(DesktopSession).GetMethod("ImportPrefabFileAsync", Hidden)!.Invoke(desktop, [true, path])!;
        object? Call(string name, params object?[] arguments) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, arguments);
        void RejectReadOnly(string name, Action operation)
        {
            bool rejected = false; try { operation(); } catch (InvalidOperationException) { rejected = true; }
            check("offline cached session rejects " + name + " without changing author data assets or history", rejected && readOnly.IsReadOnly &&
                JsonSerializer.Serialize(readOnly.Document) == readOnlyBefore && JsonSerializer.Serialize(owner.Document) == offlineOwner &&
                readOnly.Assets.Count == readOnlyAssets && readOnly.UndoCount == readOnlyHistory && !readOnly.IsTransactionActive);
        }
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
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5a01, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5a02, 7, key + 1)) continue; UnregisterHotKey(0, 0x5a02); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5a01); }
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
