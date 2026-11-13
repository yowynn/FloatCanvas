using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasWorkflowRegressionTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void NewCanvas(Action<string, bool> check)
    {
        foreach (string entry in new[] { "toolbar", "menu", "shortcut" })
        {
            using var f = new Fixture();
            var before = f.Live; var item = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20 };
            before.Add(item);
            f.Trigger(entry);
            check(entry + " unbound new first arms only inline confirmation", f.NewButton.IsConfirming && f.Modals == 0 && ReferenceEquals(before, f.Live));
            f.Trigger(entry);
            check(entry + " unbound new confirms without a second modal", f.Modals == 0 && !f.NewButton.IsConfirming && !ReferenceEquals(before, f.Live));
            var archive = f.History.GetEntries().Single(e => !e.IsCurrent);
            check(entry + " new archives the previous unbound content", Await(f.History.LoadAsync(archive))!.State.LiveDocument.Elements.Any(e => e.Id == item.Id));
            f.Trigger(entry);
            check(entry + " untouched new canvas needs no confirmation", !f.NewButton.IsConfirming && f.Modals == 0 && f.History.GetEntries().Count(e => !e.IsCurrent) == 1);
        }
        foreach (string answer in new[] { "Dialog.Cancel", "Dialog.No", "Dialog.Yes" })
        {
            using var f = new Fixture();
            string path = f.CreateProject("bound");
            Await((Task)f.Call("OpenProjectPathAsync", path)!);
            var before = f.Live; before.Add(new() { Kind = ElementKind.Ellipse, Width = 11, Height = 12 });
            byte[] diskBefore = File.ReadAllBytes(path);
            f.Answer = answer; f.Trigger("toolbar");
            check(answer + " bound new has one save modal with standard labels", f.Modals == 1 && !f.NewButton.IsConfirming &&
                f.LastButtons.Contains(L.Get("Dialog.Yes")) && f.LastButtons.Contains(L.Get("Dialog.No")) && f.LastButtons.Contains(L.Get("Dialog.Cancel")));
            if (answer == "Dialog.Cancel")
                check("cancel preserves the bound canvas and its file", ReferenceEquals(before, f.Live) && diskBefore.SequenceEqual(File.ReadAllBytes(path)));
            else
            {
                check(answer + " continues to an unbound blank canvas", !ReferenceEquals(before, f.Live) && f.Live.Document.Elements.Count == 0 && f.BoundPath is null);
                check(answer + " writes the file only for Save", (answer == "Dialog.Yes") != diskBefore.SequenceEqual(File.ReadAllBytes(path)));
                var history = f.History.GetEntries().Where(e => !e.IsCurrent).ToArray();
                check(answer + " still archives unsaved canvas content", history.Any(e => Await(f.History.LoadAsync(e))!.State.LiveDocument.Elements.Count == 2));
            }
        }
        using (var f = new Fixture())
        {
            f.Live.Add(new() { Kind = ElementKind.Rectangle, Width = 30, Height = 20 }); var original = f.Live;
            string path = f.CreateProject("open-other");
            Await((Task)f.Call("OpenProjectPathAsync", path)!);
            check("opening another project retains save protection for unbound work", f.Modals == 1 && ReferenceEquals(original, f.Live));
            f.Answer = "Dialog.No";
            Await((Task)f.Call("OpenProjectPathAsync", path)!);
            check("Don't save opens the requested project and preserves prior content in history", f.Modals == 2 && f.BoundPath == path && f.History.GetEntries().Any(e => !e.IsCurrent));
        }
        using (var f = new Fixture())
        {
            f.Live.Add(new() { Kind = ElementKind.Rectangle, Width = 30, Height = 20 });
            f.Trigger("shortcut");
            using (new ThreadModifierScope(ModifierKeys.None)) f.Call("OnKey", 0x1B);
            check("Escape cancels new-canvas confirmation without replacing content", !f.NewButton.IsConfirming && f.Live.Document.Elements.Count == 1);
        }
        using (var f = new Fixture())
        {
            string path = f.CreateProject("clean-live"); Await((Task)f.Call("OpenProjectPathAsync", path)!);
            var capture = new EditorSession(f.Live.Document.Clone());
            typeof(DesktopSession).GetField("_capture", Hidden)!.SetValue(f.Session, capture);
            var state = f.Call("FileState", capture)!; state.GetType().GetField("SavedContentVersion")!.SetValue(state, capture.ContentVersion);
            capture.Add(new() { Kind = ElementKind.Ellipse, Width = 12, Height = 12 });
            f.Trigger("toolbar");
            check("a clean bound document does not suppress inline protection for unbound capture changes", f.NewButton.IsConfirming && f.Modals == 0);
        }
        using (var f = new Fixture())
        {
            var before = f.Live; var item = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20 }; before.Add(item);
            var store = Field<RecoveryStore>(f.Session, "_recoveryStore");
            Directory.CreateDirectory(store.DirectoryPath);
            string blocker = Path.Combine(store.DirectoryPath, "history"); File.WriteAllText(blocker, "fixture-owned archive blocker");
            bool rejected = false;
            try { Await((Task)f.Call("ReplaceCanvasAsync")!); }
            catch (IOException) { rejected = true; }
            check("failed archive never replaces the current canvas or loses artwork", rejected && ReferenceEquals(before, f.Live) && f.Live.Document.Elements.Any(e => e.Id == item.Id));
            File.Delete(blocker);
            f.Trigger("toolbar"); f.Trigger("toolbar");
            check("new-canvas confirmation remains usable after archive failure", !ReferenceEquals(before, f.Live) && f.Modals == 0 && f.History.GetEntries().Any(e => !e.IsCurrent));
        }
    }

    internal static void PresetSelection(Action<string, bool> check, string? evidence = null)
    {
        using var f = new Fixture();
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(f.Host, true);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(f.Host, true);
        var toolbar = Field<Window>(f.Session, "_toolbar"); toolbar.ShowActivated = false; toolbar.Show(); Pump();
        f.Call("ActivateTool", EditorTool.Canvas); f.Call("OpenToolOptions"); Pump();
        var content = Field<CanvasToolOptions>(f.Session, "_canvasToolOptions");
        var gallery = Field<CanvasPresetGallery>(content, "presets");
        foreach (string id in new[] { "official.canvas-whiteboard", "official.canvas-blackboard", "official.canvas-graph", "official.canvas-dots", "official.canvas-transparent" })
        {
            // Select the real production tile, then let the document-to-panel refresh complete.
            var tile = gallery.OfficialTiles.Children.OfType<SelectionTileButton>().Single(t => Equals(t.Tag, id));
            tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            f.Call("RefreshCanvasSettingsTarget"); Pump();
            check(id + " applies actual values without an immediate modified badge", !tile.IsModified && gallery.SelectedId == id &&
                Field<CanvasPresetLibrary>(gallery, "library").Official.Single(p => p.Id == id).Parameters.Matches(CanvasPresetParameters.From(f.Live.Document.Canvas)));
        }
        f.Call("ChangeCanvas", (Action<CanvasSettings>)(c => { c.BackgroundColor = 0xFF123456; c.Grid.Color = 0xFF789ABC; c.Grid.Width = 3; }));
        f.Call("RefreshCanvasSettingsTarget"); gallery.SaveNamed("Personal A"); string first = gallery.SelectedId;
        var expectedFirst = CanvasPresetParameters.From(f.Live.Document.Canvas);
        f.Call("ChangeCanvas", (Action<CanvasSettings>)(c => { c.BackgroundColor = 0xFF654321; c.Grid.Pattern = StrokePattern.Dashed; c.Grid.Width = 5; }));
        f.Call("RefreshCanvasSettingsTarget"); gallery.SaveNamed("Personal B"); string second = gallery.SelectedId;
        var reloaded = new CanvasPresetLibrary(Path.Combine(f.Folder, "canvas-presets.json"));
        byte[] presetFile = File.ReadAllBytes(Path.Combine(f.Folder, "canvas-presets.json"));
        check("personal canvas preset files retain their complete saved settings", reloaded.User.Count == 2 &&
            reloaded.User.Single(p => p.Id == first).Parameters.Matches(expectedFirst) && expectedFirst.Matches(reloaded.User.Single(p => p.Id == first).Parameters));
        foreach (string id in new[] { first, second, first })
        {
            gallery.Apply(id); Pump(); f.Call("RefreshCanvasSettingsTarget");
            var actual = CanvasPresetParameters.From(f.Live.Document.Canvas);
            check(id + " switching personal presets restores saved values and clears the badge", !SelectedTile().IsModified &&
                reloaded.User.Single(p => p.Id == id).Parameters.Matches(actual));
        }
        SaveEvidence("preset-applied");
        f.Call("ChangeCanvas", (Action<CanvasSettings>)(c => c.Grid.Width = 7)); f.Call("RefreshCanvasSettingsTarget");
        check("real edits still mark the selected canvas preset modified", SelectedTile().IsModified);
        SaveEvidence("preset-edited");
        f.Live.Undo(); f.Call("RefreshCanvasSettingsTarget");
        check("undo back to applied preset clears its modification badge", !SelectedTile().IsModified && f.Live.Document.Canvas.Grid.Width == 3);
        f.Live.Redo(); f.Call("RefreshCanvasSettingsTarget");
        check("redo restores the real modification badge", SelectedTile().IsModified && f.Live.Document.Canvas.Grid.Width == 7);
        gallery.Apply(first); Pump(); f.Call("RefreshCanvasSettingsTarget");
        check("reapplying the selected preset resets edits and clears its badge", !SelectedTile().IsModified && f.Live.Document.Canvas.Grid.Width == 3);
        f.Call("CloseToolOptions"); f.Call("OpenToolOptions"); Pump();
        check("reopening canvas options retains the clean applied preset state", !SelectedTile().IsModified && gallery.SelectedId == first);
        check("applying and editing canvas settings do not rewrite stored personal presets", presetFile.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Folder, "canvas-presets.json"))));
        SelectionTileButton SelectedTile() => gallery.UserTiles.Children.OfType<SelectionTileButton>().Single(t => Equals(t.Tag, gallery.SelectedId));
        void SaveEvidence(string name)
        {
            if (evidence is null) return;
            Directory.CreateDirectory(evidence); Pump();
            var window = Field<Window>(f.Session, "_toolOptionsWindow"); window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(evidence, name + ".png")); encoder.Save(file);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string profile = ApplicationPaths.ProfileDirectory;
        private readonly OverlayMonitorScope? scope = ApplicationPaths.RequestedMonitorScope;
        private readonly SynchronizationContext? context = SynchronizationContext.Current;
        private readonly string language = L.Preference;
        private readonly DispatcherTimer response;
        internal readonly string Folder;
        internal readonly OverlayHost Host;
        internal readonly DesktopSession Session;
        internal int Modals;
        internal string Answer = "Dialog.Cancel";
        internal string[] LastButtons = [];
        internal EditorSession Live => Field<EditorSession>(Session, "_live");
        internal NewCanvasButton NewButton => Field<NewCanvasButton>(Session, "_newCanvasButton");
        internal string? BoundPath => (string?)Call("FileState", Live)!.GetType().GetField("Path")!.GetValue(Call("FileState", Live));
        internal WorkspaceRecoveryHistory History => (WorkspaceRecoveryHistory)typeof(DesktopSession).GetProperty("RecoveryHistory", Hidden)!.GetValue(Session)!;
        internal Fixture()
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            Folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder); ApplicationPaths.Configure(["--profile", Folder, "--primary-screen"]);
            new UserSettings { Theme = "light", OverlayMode = "normal", ShowFloatingLogo = false }.Save(); L.Initialize("en");
            var screen = new MonitorInfo("workflow", new(-10000, -10000, 640, 360), new(-10000, -10000, 640, 360), 96, 96, true);
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
                [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => [screen]), false], null)!;
            Session = new DesktopSession(app, Host); Field<System.Windows.Forms.NotifyIcon>(Session, "_tray").Visible = false;
            var state = Call("FileState", Live)!; state.GetType().GetField("SavedContentVersion")!.SetValue(state, Live.ContentVersion);
            response = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            response.Tick += (_, _) =>
            {
                foreach (var w in app.Windows.OfType<Window>().Where(w => w.IsVisible).ToArray())
                {
                    var buttons = Descendants<Button>(w).ToArray();
                    var cancel = buttons.FirstOrDefault(b => Equals(b.Content, L.Get("Dialog.Cancel")));
                    if (cancel is null) continue;
                    Modals++; LastButtons = buttons.Select(b => b.Content?.ToString() ?? "").ToArray();
                    (buttons.FirstOrDefault(b => Equals(b.Content, L.Get(Answer))) ?? cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            response.Start();
        }
        internal string CreateProject(string name)
        {
            string path = Path.Combine(Folder, name + ".floatcanvas");
            var document = Live.Document.Clone();
            var item = new CanvasElement { Kind = ElementKind.Rectangle, Width = 21, Height = 22 };
            document.Elements = [item]; document.RootOrder = [item.Id];
            Await(new ProjectStore().SaveAsync(path, document, new AssetLibrary()));
            return path;
        }
        internal object? Call(string name, params object?[] args) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(Session, args);
        internal void Trigger(string entry)
        {
            if (entry == "toolbar") NewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (entry == "menu")
                Field<OverlayContextMenu>(Session, "_toolbarMenu").Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).Single(m => m.InputGestureText == "Ctrl+N").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else using (new ThreadModifierScope(ModifierKeys.Control)) Call("OnKey", 0x4E);
            Wait(() => !Field<bool>(NewButton, "executing") && !(bool)typeof(DesktopSession).GetProperty("DocumentOperationBusy", Hidden)!.GetValue(Session)!);
        }
        public void Dispose()
        {
            response.Stop(); Session.Dispose(); Host.Dispose();
            ApplicationPaths.Configure(scope is { } s ? ["--profile", profile, s == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens"] : ["--profile", profile]);
            L.Initialize(language); SynchronizationContext.SetSynchronizationContext(context);
        }
    }
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    { if (node is T t) yield return t; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var value in Descendants<T>(VisualTreeHelper.GetChild(node, i))) yield return value; }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> done)
    { var watch = Stopwatch.StartNew(); do { Pump(); if (done()) return; Thread.Sleep(1); } while (watch.Elapsed < TimeSpan.FromSeconds(20)); throw new TimeoutException("Canvas workflow did not settle"); }
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
}
