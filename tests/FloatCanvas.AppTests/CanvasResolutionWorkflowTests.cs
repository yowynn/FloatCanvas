using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasResolutionWorkflowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (PresentationDesktopAvailability.IsCurrentInputDesktop()) throw new InvalidOperationException("Use --isolated-ui-desktop --canvas-resolution-workflow-only.");
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        string root = Path.Combine(AppContext.BaseDirectory, "canvas-resolution-workflow", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile")]);
        foreach (var choice in Enum.GetValues<CanvasResolutionChoice>()) Case(choice);
        void Case(CanvasResolutionChoice choice)
        {
            var screen = new PixelBounds(-24000, -22000, 1920, 1080);
            Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("conversion-private", screen, screen, 96, 96, true)];
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
            using var desktop = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
            var editor = Field<EditorController>(desktop, "_editor");
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true); typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
            MarkSaved(editor.Session); Call("CurrentGroupCanvasView");
            var previous = editor.Session; string oldDocument = JsonSerializer.Serialize(previous.Document);
            var source = new SceneDocument { Viewport = new(0, 0, 3840, 2160), AuthoringReference = new(new(0, 0, 3840, 2160), new(0, 0, 3840, 2160)),
                Elements = [new() { Kind = ElementKind.Rectangle, Width = 400, Height = 200, Transform = Affine2D.Translation(1700, 900) }] };
            string path = Path.Combine(root, choice + ".floatcanvas"); var store = new ProjectStore(); Await(store.SaveAsync(path, source, new()));
            byte[] originalBytes = File.ReadAllBytes(path); int dialogs = 0;
            using var driver = new DialogDriver(choice, () => dialogs++, check, Path.Combine(root, choice + "-compatibility.png"));
            Await((Task)Call("OpenProjectPathCoreAsync", path, false)!);
            check(choice + " compatibility decision uses the real default-preserve dialog once", dialogs == 1);
            check(choice + " opening leaves the original file bytes untouched", File.ReadAllBytes(path).SequenceEqual(originalBytes));
            if (choice == CanvasResolutionChoice.Cancel)
            {
                check("cancel returns before replacing or publishing the active document", ReferenceEquals(editor.Session, previous) && oldDocument == JsonSerializer.Serialize(previous.Document));
                return;
            }
            var opened = editor.Session;
            var width = ElementGeometry.Bounds(opened.Document.Elements.Single(), includeStroke: false).Width;
            check(choice + " uses the selected authored pixel scale", Math.Abs(width - (choice == CanvasResolutionChoice.Convert ? 200 : 400)) < 1e-7);
            if (choice == CanvasResolutionChoice.Preserve)
            {
                check("preserve records the original 4K frame despite a smaller mutable viewport", opened.Document.AuthoringReference!.Frame.Width == 3840 && opened.Document.Viewport.Width == 1920);
                driver.Dispose();
                using (var preserveCurrent = new DialogDriver(CanvasResolutionChoice.Preserve, () => dialogs++, check))
                    Await((Task)Call("ConvertCurrentCanvasResolutionAsync")!);
                check("explicit current-project conversion can preserve without replacing its session", ReferenceEquals(editor.Session, opened) && opened.Document.ResourceId == source.ResourceId);
                using (var convertCurrent = new DialogDriver(CanvasResolutionChoice.Convert, () => dialogs++, check))
                    Await((Task)Call("ConvertCurrentCanvasResolutionAsync")!);
                var converted = editor.Session; var convertedState = Call("FileState", converted)!;
                check("explicit current-project conversion creates an unbound target-scale copy without rewriting the saved source", dialogs == 3 &&
                    !ReferenceEquals(converted, opened) && converted.Document.ResourceId != source.ResourceId &&
                    Math.Abs(ElementGeometry.Bounds(converted.Document.Elements.Single(), includeStroke: false).Width - 200) < 1e-7 &&
                    convertedState.GetType().GetField("Path")!.GetValue(convertedState) is null && File.ReadAllBytes(path).SequenceEqual(originalBytes));
                return;
            }
            var state = Call("FileState", opened)!;
            check("converted working copy routes its first ordinary save to Save As", state.GetType().GetField("Path")!.GetValue(state) is null &&
                (long)state.GetType().GetField("SavedContentVersion")!.GetValue(state)! == -1 &&
                ((string)state.GetType().GetField("SuggestedSaveAsPath")!.GetValue(state)!).EndsWith("1920x1080.floatcanvas", StringComparison.Ordinal));
            check("converted working copy has its own resource identity and stable target reference", opened.Document.ResourceId != source.ResourceId && opened.Document.AuthoringReference!.Frame == new RectD(0, 0, 1920, 1080));
            var toolbar = Field<Window>(desktop, "_toolbar");
            toolbar.ShowActivated = false; toolbar.Left = screen.Left; toolbar.Top = screen.Top; toolbar.Show(); toolbar.UpdateLayout();
            string copy = Path.Combine(root, "saved-copy.floatcanvas");
            using (var cancelSave = new NativeSaveDriver(copy, cancel: true))
            {
                var save = (Task<bool>)Call("SaveSessionAsync", opened, false)!; Await(save); cancelSave.Complete();
                check("cancelling first native Save As keeps the converted copy unbound and the source untouched", !save.Result &&
                    state.GetType().GetField("Path")!.GetValue(state) is null && !File.Exists(copy) && File.ReadAllBytes(path).SequenceEqual(originalBytes));
            }
            string beforeSaveKey = (string)Call("CanvasViewingKey")!;
            using (var saveDriver = new NativeSaveDriver(copy))
            {
                var save = (Task<bool>)Call("SaveSessionAsync", opened, false)!; Await(save); saveDriver.Complete();
                check("first ordinary save completes the actual native Save As and binds only the new copy", save.Result &&
                    Equals(state.GetType().GetField("Path")!.GetValue(state), copy) && File.Exists(copy) &&
                    beforeSaveKey != (string)Call("CanvasViewingKey")! && File.ReadAllBytes(path).SequenceEqual(originalBytes));
            }
            Await((Task)Call("OpenProjectPathCoreAsync", copy, false)!);
            check("saved converted copy reopens without a second compatibility prompt or scale", dialogs == 1 &&
                Math.Abs(ElementGeometry.Bounds(editor.Session.Document.Elements.Single(), includeStroke: false).Width - 200) < 1e-7 && File.ReadAllBytes(path).SequenceEqual(originalBytes));

            object? Call(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, arguments);
            void MarkSaved(EditorSession session)
            { var state = Call("FileState", session)!; state.GetType().GetField("SavedContentVersion")!.SetValue(state, session.ContentVersion); }
        }
        Console.WriteLine("CANVAS_RESOLUTION_WORKFLOW " + root);
    }
    private sealed class DialogDriver : IDisposable
    {
        private readonly DispatcherTimer timer;
        internal DialogDriver(CanvasResolutionChoice choice, Action observed, Action<string, bool> check, string? screenshot = null)
        {
            timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.Title == L.Get("Canvas.ResolutionTitle"));
                if (dialog is null) return;
                if (screenshot is not null) { SaveWindow(dialog, screenshot); screenshot = null; }
                var buttons = ((StackPanel)((DockPanel)dialog.Content).Children[0]).Children.Cast<Button>().ToArray();
                check("compatibility dialog defaults to preserving dimensions", buttons[0].IsDefault && !buttons[1].IsDefault);
                observed(); buttons[choice == CanvasResolutionChoice.Preserve ? 0 : choice == CanvasResolutionChoice.Convert ? 1 : 2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
        }
        public void Dispose() => timer.Stop();
    }
    internal static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var output = File.Create(path); encoder.Save(output);
    }
    internal sealed class NativeSaveDriver : IDisposable
    {
        private readonly DispatcherTimer timer;
        private Task? operation;
        internal NativeSaveDriver(string path, bool cancel = false)
        {
            path = Path.GetFullPath(path);
            timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                nint dialog = HistoryRestoreRoutingFixture.FindOwnNativeDialog();
                if (dialog == 0) return;
                timer.Stop();
                operation = Task.Run(() =>
                {
                    try
                    {
                        if (cancel) PostMessage(dialog, 0x10, 0, 0);
                        else HistoryRestoreRoutingFixture.FillOwnSaveDialog(dialog, path, postCommand: true);
                    }
                    catch { PostMessage(dialog, 0x10, 0, 0); throw; }
                });
            };
            timer.Start();
        }
        internal void Complete() => (operation ?? throw new InvalidOperationException("Native Save As did not open.")).GetAwaiter().GetResult();
        public void Dispose() => timer.Stop();
        [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
    }
    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Canvas resolution workflow did not complete.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Yield();
        }
        task.GetAwaiter().GetResult();
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
}
