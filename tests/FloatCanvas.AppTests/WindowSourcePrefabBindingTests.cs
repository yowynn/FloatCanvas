using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

internal static class WindowSourcePrefabBindingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal)) throw new InvalidOperationException("Use --isolated-ui-desktop --window-source-prefab-binding-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh test process.");
        string root = Path.Combine(output ?? AppContext.BaseDirectory, "prefab-window-bindings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys(); new UserSettings { Theme = "light", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2, PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-24000, -24000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("prefab-window-binding", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false; Field<Window>(desktop, "_toolbar").Opacity = 0;
        ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(host))?.Dispose(); typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(host, null);
        var fixture = new Fixture(desktop, root, check);
        fixture.EmbeddedAndReentry(); fixture.Nested(); fixture.DiscardAndStale();
        fixture.SourceFile(project: false); fixture.SourceFile(project: true); fixture.PendingSourceGuards();
        Console.WriteLine("WINDOW_PREFAB_BINDING_EVIDENCE " + root);
    }

    private sealed class Fixture(DesktopSession desktop, string root, Action<string, bool> check)
    {
        private readonly EditorController editor = Field<EditorController>(desktop, "_editor");
        private readonly WindowSourceRuntime runtime = Field<WindowSourceRuntime>(desktop, "_windowRuntime");
        private EditorSession Owner => Field<EditorSession>(desktop, "_live");
        private PrefabEditContext Context => Field<PrefabEditContext>(desktop, "_prefabEditContext");
        private int PendingCount => ((ICollection)Field<object>(desktop, "_windowSourceHandoffs")).Count;
        private object Token(EditorSession owner) => Invoke("WindowOwnerToken", owner)!;
        private SourceNodeAddress? Registered(object owner, Guid intent)
        {
            var bindings = (IDictionary)typeof(WindowSourceRuntime).GetField("bindings", Hidden)!.GetValue(runtime)!;
            var entry = bindings[(owner, intent)];
            return entry is null ? null : ((FloatCanvas.Rendering.WindowSourceKey)entry.GetType().GetField("Source", Hidden)!.GetValue(entry)!).Source;
        }
        private object? Invoke(string name, params object?[] args) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, args);
        private void Reset()
        {
            if (typeof(DesktopSession).GetField("_prefabEditContext", Hidden)!.GetValue(desktop) is not null) throw new InvalidOperationException("Test left a prefab context open.");
            Invoke("ForgetAllWindowSourceBindings");
            Owner.Execute("reset binding fixture", d => { d.Elements.Clear(); d.Groups.Clear(); d.Instances.Clear(); d.Definitions.Clear(); d.RootOrder = null; });
        }
        private PrefabCreation Seed(string name)
        {
            var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 24, Style = new() { StrokeWidth = 0 } };
            Owner.Add(leaf); Owner.SetSelection([leaf.Id]); return Owner.CreatePrefabFromSelection(name);
        }
        private (CanvasElement Image, FakeSource Source, object Token) Bind(EditorSession session)
        {
            string asset = session.Assets.Add(Png());
            var image = new CanvasElement { Kind = ElementKind.Image, Width = 12, Height = 8, AssetId = asset, Transform = Affine2D.Translation(4, 4),
                WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "Fake window" } };
            session.Add(image); var source = new FakeSource(); var token = Token(session);
            runtime.Bind(token, session.Document.ResourceId, new(null, image.Id), image.WindowSource.BindingIntent, source);
            return (image, source, token);
        }
        private SourceNodeAddress Published(Guid intent) => Owner.Document.Definitions.SelectMany(d => d.Graph.Elements.Where(e => e.WindowSource?.BindingIntent == intent)
            .Select(e => new SourceNodeAddress(d.Id, e.Id))).First();

        internal void EmbeddedAndReentry()
        {
            Reset(); var created = Seed("shared window"); Owner.PlacePrefab(created.DefinitionId, new(Affine2D.Translation(120, 0), 1));
            Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!); var context = Context;
            var bound = Bind(context.Session); Guid intent = bound.Image.WindowSource!.BindingIntent;
            check("definition draft binding is explicit and not yet registered in its owner", runtime.State(bound.Token, intent).Connected && !runtime.State(Token(Owner), intent).Connected);
            check("actual embedded CompleteApply accepts the draft graph", Await((Task<bool>)Invoke("CompleteCurrentPrefabAsync")!));
            var address = Published(intent); var ownerToken = Token(Owner);
            Console.WriteLine("WINDOW_HANDOFF_STATE " + System.Text.Json.JsonSerializer.Serialize(new { ParentConnected = runtime.State(ownerToken, intent).Connected,
                DraftConnected = runtime.State(bound.Token, intent).Connected, ActualSource = Registered(ownerToken, intent), ExpectedSource = address,
                Demanded = runtime.IsSourceDemanded(address, intent), bound.Source.DisposeCount, runtime.BindingCount }));
            check("successful definition publication hands draft-root binding to the actual definition source address", runtime.State(ownerToken, intent).Connected &&
                !runtime.State(bound.Token, intent).Connected && Registered(ownerToken, intent) == address && bound.Source.DisposeCount == 0);
            check("two instances of one definition keep one capture service registry entry", Owner.Document.Instances.Count == 2 && runtime.BindingCount == 1);
            Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!); var next = Context; var nextToken = Token(next.Session);
            check("new definition draft with equal ResourceId never automatically inherits its parent's native service", next.Session.Document.ResourceId == Owner.Document.ResourceId &&
                !ReferenceEquals(nextToken, ownerToken) && !runtime.State(nextToken, intent).Connected && runtime.State(ownerToken, intent).Connected);
            Invoke("ReturnFromPrefabContext", next);
            check("leaving an unbound fresh draft preserves the parent registry without a second service", runtime.BindingCount == 1 && bound.Source.DisposeCount == 0);
        }
        internal void Nested()
        {
            Reset(); var outer = Seed("outer"); Await((Task)Invoke("EditPrefabDefinitionAsync", outer.DefinitionId)!); var outerContext = Context;
            outerContext.Session.SetSelection(outerContext.Session.Document.Elements.Select(e => e.Id)); var child = outerContext.Session.CreatePrefabFromSelection("inner");
            Await((Task)Invoke("EditPrefabDefinitionAsync", child.DefinitionId)!); var childContext = Context; var bound = Bind(childContext.Session); Guid intent = bound.Image.WindowSource!.BindingIntent;
            check("actual child definition publication succeeds", Await((Task<bool>)Invoke("CompleteCurrentPrefabAsync")!));
            check("child service transfers to parent draft without leaking into the main owner", ReferenceEquals(editor.Session, outerContext.Session) &&
                runtime.State(Token(outerContext.Session), intent).Connected && !runtime.State(Token(Owner), intent).Connected && !runtime.State(bound.Token, intent).Connected && bound.Source.DisposeCount == 0);
            check("actual outer definition publication succeeds", Await((Task<bool>)Invoke("CompleteCurrentPrefabAsync")!));
            check("nested publication transfers the same service again to the main canonical source", ReferenceEquals(editor.Session, Owner) && runtime.State(Token(Owner), intent).Connected &&
                !runtime.State(Token(outerContext.Session), intent).Connected && runtime.BindingCount == 1 && Registered(Token(Owner), intent) == Published(intent) && bound.Source.DisposeCount == 0);
        }
        internal void DiscardAndStale()
        {
            Reset(); var created = Seed("discard"); Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!); var discarded = Context; var discard = Bind(discarded.Session);
            Invoke("ReturnFromPrefabContext", discarded);
            check("discarding an unpublished draft closes its service and creates no parent binding", discard.Source.DisposeCount == 1 && runtime.BindingCount == 0 && !runtime.State(Token(Owner), discard.Image.WindowSource!.BindingIntent).Connected);
            Await((Task)Invoke("EditPrefabDefinitionAsync", created.DefinitionId)!); var staleContext = Context; var stale = Bind(staleContext.Session); Guid intent = stale.Image.WindowSource!.BindingIntent;
            Owner.ExecuteSettings("owner changed during detached edit", d => d.Canvas.Grid.PlaneCellSize = 11);
            bool failed = false; try { Await((Task<bool>)Invoke("CompleteCurrentPrefabAsync")!); } catch (InvalidOperationException) { failed = true; }
            check("stale Core publication cannot transfer a draft service or claim success", failed && ReferenceEquals(editor.Session, staleContext.Session) &&
                runtime.State(stale.Token, intent).Connected && !runtime.State(Token(Owner), intent).Connected && PendingCount == 0);
            Invoke("ReturnFromPrefabContext", staleContext);
            check("discard after a failed publication closes the still-owned conflicted draft service", stale.Source.DisposeCount == 1 && runtime.BindingCount == 0);
        }
        internal void SourceFile(bool project)
        {
            Reset(); string path = Path.Combine(root, project ? "window-source.floatcanvas" : "window-source.floatprefab");
            var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 24, Style = new() { StrokeWidth = 0 } };
            if (project) Await(new ProjectStore().SaveAsync(path, new() { Elements = [leaf], Viewport = new(0, 0, 320, 180) }, Owner.Assets));
            else { var entry = new PrefabDefinition { Graph = new() { Elements = [leaf] } }; Await(new PrefabStore().SaveAsync(path, new() { EntryDefinitionId = entry.Id, Definitions = [entry] }, Owner.Assets)); }
            Await((Task)Invoke("ImportPrefabFileAsync", true, path)!); var definition = Owner.Document.Definitions.Single(d => d.Source?.IsLinked == true);
            Await((Task)Invoke("EditPrefabSourceAsync", definition.Id)!); var context = Context; var bound = Bind(context.Session); Guid intent = bound.Image.WindowSource!.BindingIntent;
            check("source editor creates an isolated explicit service owner project=" + project, context.IsSource && runtime.State(bound.Token, intent).Connected && !runtime.State(Token(Owner), intent).Connected);
            check("real source Save plus Refresh completes project=" + project, Await((Task<bool>)Invoke("CompleteCurrentPrefabAsync")!));
            Console.WriteLine("WINDOW_SOURCE_FILE_HANDOFF " + System.Text.Json.JsonSerializer.Serialize(new { Project = project,
                AcceptedResource = Owner.Document.Definitions.Single(d => d.Id == definition.Id).Source!.SourceResourceId,
                DraftResource = context.Session.Document.ResourceId, AcceptedSha = Owner.Document.Definitions.Single(d => d.Id == definition.Id).Source!.LastAcceptedFileSha256,
                DiskSha = Await(ProjectFileVersion.ReadAsync(path)).Sha256, PendingCount,
                ParentConnected = runtime.State(Token(Owner), intent).Connected, SourceConnected = runtime.State(bound.Token, intent).Connected,
                RealAddresses = Owner.Document.Definitions.SelectMany(d => d.Graph.Elements.Where(e => e.WindowSource?.BindingIntent == intent).Select(e => new SourceNodeAddress(d.Id, e.Id))).ToArray() }));
            Wait(() => runtime.State(Token(Owner), intent).Connected);
            var accepted = Owner.Document.Definitions.Single(d => d.Id == definition.Id).Source!; var disk = Await(ProjectFileVersion.ReadAsync(path));
            check("source handoff follows actual accepted file SHA and published intent project=" + project, accepted.LastAcceptedFileSha256 == disk!.Sha256 &&
                runtime.State(Token(Owner), intent).Connected && !runtime.State(bound.Token, intent).Connected && Registered(Token(Owner), intent) == Published(intent) && bound.Source.DisposeCount == 0 && PendingCount == 0);
        }
        internal void PendingSourceGuards()
        {
            Reset(); string path = Path.Combine(root, "pending.floatprefab"); var entry = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 40, Height = 24 }] } };
            var package = new PrefabPackage { Definitions = [entry], EntryDefinitionId = entry.Id }; Await(new PrefabStore().SaveAsync(path, package, Owner.Assets));
            Await((Task)Invoke("ImportPrefabFileAsync", true, path)!); var definition = Owner.Document.Definitions.Single(d => d.Source?.IsLinked == true); string sha = Await(ProjectFileVersion.ReadAsync(path)).Sha256 ?? throw new InvalidDataException("Missing test source file.");
            var (bound, context) = QueuePending(); Guid intent = bound.Image.WindowSource!.BindingIntent;
            check("returning while source refresh is pending retains only the explicit source token", runtime.State(bound.Token, intent).Connected && !runtime.State(Token(Owner), intent).Connected && PendingCount == 1 && bound.Source.DisposeCount == 0);
            Owner.Execute("wrong accepted source sha", d => d.Definitions.Single(x => x.Id == definition.Id).Source!.LastAcceptedFileSha256 = new string('0', 64)); Invoke("CompleteWindowSourceHandoffs", Owner);
            check("pending native binding cannot cross a wrong accepted file SHA", PendingCount == 1 && !runtime.State(Token(Owner), intent).Connected && runtime.State(bound.Token, intent).Connected);
            Owner.Execute("accepted source sha without new content", d => d.Definitions.Single(x => x.Id == definition.Id).Source!.LastAcceptedFileSha256 = sha); Invoke("CompleteWindowSourceHandoffs", Owner);
            check("matching file SHA alone does not hand off an unpublished binding intent", PendingCount == 1 && !runtime.State(Token(Owner), intent).Connected);
            var published = bound.Image.Clone(newId: true);
            Owner.Execute("actual refreshed source graph", d => new SceneGraph(d.Definitions.Single(x => x.Id == definition.Id).Graph).InsertElement(published, null));
            Invoke("CompleteWindowSourceHandoffs", Owner);
            check("accepted real graph resolves remapped node ID by explicit intent before native transfer", PendingCount == 0 && runtime.State(Token(Owner), intent).Connected &&
                Registered(Token(Owner), intent) == new SourceNodeAddress(definition.Id, published.Id) && published.Id != bound.Image.Id && !runtime.State(bound.Token, intent).Connected && bound.Source.DisposeCount == 0);
            var (late, _) = QueuePending(); Guid lateIntent = late.Image.WindowSource!.BindingIntent;
            Invoke("ForgetWindowSourceBindings", Owner);
            Owner.Execute("later loaded graph contains old pending intent", d => new SceneGraph(d.Definitions.Single(x => x.Id == definition.Id).Graph).InsertElement(late.Image.Clone(newId: true), null));
            Invoke("CompleteWindowSourceHandoffs", Owner);
            check("explicit owner lifetime Forget rejects pending source handoff even with matching resource and content", PendingCount == 0 && late.Source.DisposeCount == 1 && !runtime.State(Token(Owner), lateIntent).Connected);

            ((CanvasElement Image, FakeSource Source, object Token) Bound, PrefabEditContext Context) QueuePending()
            {
                var graph = SceneGraphClone.CloneGraph(entry.Graph).Graph;
                var document = new SceneDocument { ResourceId = package.ResourceId, Elements = graph.Elements, Groups = graph.Groups, Instances = graph.Instances,
                    RootOrder = graph.RootOrder, Definitions = [entry.Clone()], Viewport = Owner.Document.Viewport };
                var sourceSession = new EditorSession(document, Owner.Assets);
                var ctx = new PrefabEditContext(Owner, sourceSession, definition.Id, Owner.Document.Definitions.Single(x => x.Id == definition.Id).Revision,
                    editor.ScreenOrigin, editor.Tool, null, path, entry.Id);
                typeof(DesktopSession).GetField("_prefabEditContext", Hidden)!.SetValue(desktop, ctx); Invoke("SwitchPrefabContext", sourceSession);
                var file = Invoke("FileState", sourceSession)!; file.GetType().GetField("Path")!.SetValue(file, path); file.GetType().GetField("DiskVersion")!.SetValue(file, new ProjectFileVersion(sha));
                var capture = Bind(sourceSession); var handoff = Invoke("CaptureWindowSourceHandoff", ctx)!; Invoke("QueueWindowSourceHandoff", handoff); Invoke("ReturnFromPrefabContext", ctx);
                return (capture, ctx);
            }
        }
    }
    private sealed class FakeSource : IWindowCaptureSource
    {
        private static long streams;
        public WindowCaptureTarget Target { get; } = new(1, 1, 1, 1, "Synthetic only", "test", "Fake"); public long StreamId { get; } = Interlocked.Increment(ref streams); public long ConnectionVersion => 1;
        public WindowCaptureStatus Status => WindowCaptureStatus.Live; public Exception? LastError => null; public Task Completion => Task.CompletedTask; public Task FirstResult => Task.CompletedTask;
        public event Action? FrameReady { add { } remove { } } public event Action? StateChanged { add { } remove { } }
        internal int DisposeCount; public void SetActive(bool enabled) { } public void Resume() { }
        public bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame) { frame = null; return false; }
        public void Dispose() => DisposeCount++;
    }
    private static byte[] Png() { using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.CadetBlue); using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray(); }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Await(Task task) { while (!task.IsCompleted) Pump(); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> done) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!done()) { if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Expected fake binding was not handed off."); Pump(); } }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2) { if (!RegisterHotKey(0, 0x5b01, 7, key)) continue; try { if (!RegisterHotKey(0, 0x5b02, 7, key + 1)) continue; UnregisterHotKey(0, 0x5b02); return (new(7, key), new(7, key + 1)); } finally { UnregisterHotKey(0, 0x5b01); } }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    private static string DesktopName() { var value = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, value, value.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return value.ToString(); }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
