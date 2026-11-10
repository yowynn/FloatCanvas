using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Actual toolbar placement command on an undisplayed private desktop; no native file dialog or input is driven.</summary>
internal static class CanvasContentImportDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal) || PresentationDesktopAvailability.IsCurrentInputDesktop())
            throw new InvalidOperationException("Use --isolated-ui-desktop --canvas-content-import-desktop-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private child process.");
        string root = Path.Combine(output ?? AppContext.BaseDirectory, "canvas-placement-desktop-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys(); new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2, PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-24000, -23000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("canvas-placement-private", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false; Field<Window>(desktop, "_toolbar").Opacity = 0;
        ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(host))?.Dispose(); typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(host, null);
        var editor = Field<EditorController>(desktop, "_editor"); var owner = editor.Session;
        var store = new ProjectStore("0.4.0"); var assets = new AssetLibrary(); byte[] png = Png();
        string projectPath = Path.Combine(root, "Placed canvas.floatcanvas"), imagePath = Path.Combine(root, "image.png"); File.WriteAllBytes(imagePath, png);
        var source = new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 70, Height = 40 }], Viewport = new(0, 0, 320, 180) };
        Await(store.SaveAsync(projectPath, source, assets));

        Reset(); Guid scope = owner.CreatePopulatedGroup("Nested target"); owner.SetEditScope(scope); Pump(); Call("SynchronizeFlatGridOrigin");
        int undo = owner.UndoCount; Guid resource = owner.Document.ResourceId;
        check("project placement starts in ordinary pass-through mode without editing input", !host.IsEditing && !host.HasEditingInput);
        Await(Import([projectPath]));
        check("actual ImportImagesAsync places a linked project without replacing the current canvas", owner.Document.ResourceId == resource && owner.Document.Instances.Count == 1 && owner.Document.Instances.Single().ParentId == scope && owner.EditScopeId == scope);
        Console.WriteLine("CANVAS_PLACEMENT_DESKTOP_STATE " + JsonSerializer.Serialize(new { owner.Document.Instances.Single().Name, Source = owner.Document.Definitions.Single().Source, ProjectPath = projectPath, BeforeUndo = undo, AfterUndo = owner.UndoCount }));
        check("actual toolbar placement preserves source file name link and one Undo", owner.Document.Instances.Single().Name == "Placed canvas" && owner.Document.Definitions.Single().Source is { IsLinked: true, Locator: var locator } &&
            string.Equals(locator, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase) && owner.UndoCount == undo + 1);
        check("ordinary-mode placement does not acquire an editing lease or native window capture", !host.IsEditing && !host.HasEditingInput && Field<WindowSourceRuntime>(desktop, "_windowRuntime").BindingCount == 0);
        check("actual project placement is reversible without leaving the nested scope", owner.Undo() && owner.Document.Instances.Count == 0 && owner.EditScopeId == scope && owner.Redo() && owner.Document.Instances.Count == 1);
        var paths = new[] { projectPath, imagePath }; undo = owner.UndoCount;
        Await(Import(paths));
        check("actual toolbar command accepts mixed project image selections as one edit", owner.UndoCount == undo + 1 && owner.Selection.Count == 2 && owner.Document.Instances.Count == 2 && owner.Document.Elements.Count == 2);
        check("same linked file is shared by actual repeated toolbar placement", owner.Document.Definitions.Count == 1 && owner.Document.Instances.Select(instance => instance.DefinitionId).Distinct().Count() == 1);
        var language = FloatCanvas.Localization.AppText.Preference;
        foreach (string locale in new[] { "zh-CN", "en" })
        {
            FloatCanvas.Localization.AppText.Initialize(locale);
            check(locale + " import chooser advertises canvas and prefab formats while replace stays image-only", FloatCanvas.Localization.AppText.Get("Placement.Filter").Contains("*.floatcanvas") &&
                FloatCanvas.Localization.AppText.Get("Placement.Filter").Contains("*.floatprefab") && !FloatCanvas.Localization.AppText.Get("Image.Filter").Contains(".floatcanvas"));
        }
        FloatCanvas.Localization.AppText.Initialize(language);

        Reset(); Pending("same-resource document replacement", () => owner.ReplaceDocument(owner.Document.Clone()), () => !owner.IsTransactionActive);
        Reset(); scope = owner.CreatePopulatedGroup("Scope"); owner.SetEditScope(scope); Pump(); Call("SynchronizeFlatGridOrigin");
        Pending("scope A B A", () => { owner.SetEditScope(null); owner.SetEditScope(scope); }, () => owner.EditScopeId == scope);
        Reset();
        Pending("mode A B A", () =>
        {
            var focus = Field<object>(host, "_focusTransitions"); focus.GetType().GetMethod("BeginSession")!.Invoke(focus, null); focus.GetType().GetMethod("EndSession")!.Invoke(focus, null);
        }, () => !host.IsEditing && !host.HasEditingInput);
        Reset(); long foreignId = 0;
        Pending("foreign transaction", () => { owner.BeginTransaction("foreign edit"); foreignId = owner.TransactionId; }, () => owner.IsTransactionActive && owner.TransactionId == foreignId);
        owner.Cancel(); SetPrepare(CanvasContentImport.PrepareAsync);
        check("desktop import fixture leaves physical input and native source capture untouched", !PresentationDesktopAvailability.IsCurrentInputDesktop() && !host.IsKeyboardIsolationActive && !host.HasEditingInput && Field<WindowSourceRuntime>(desktop, "_windowRuntime").BindingCount == 0);
        Console.WriteLine("CANVAS_CONTENT_DESKTOP_EVIDENCE " + root);

        void Pending(string label, Action mutate, Func<bool> retained)
        {
            var ready = new TaskCompletionSource<PreparedCanvasContentBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPrepare(async (target, paths, images, placement, center, opacity, ownerPath, token) =>
            {
                var prepared = await CanvasContentImport.PrepareAsync(target, paths, images, placement, center, opacity, ownerPath, token);
                ready.SetResult(prepared); await release.Task; return prepared;
            });
            Task pending = Import([projectPath]); Await(ready.Task);
            int assetsBefore = owner.Assets.Count; mutate(); string afterMutation = JsonSerializer.Serialize(owner.Document); int undoBefore = owner.UndoCount;
            release.SetResult(); Await(pending); Pump();
            check(label + " rejects late prepared toolbar content without publishing an asset or Undo", JsonSerializer.Serialize(owner.Document) == afterMutation && owner.Assets.Count == assetsBefore && owner.UndoCount == undoBefore && owner.Document.Instances.Count == 0 && retained());
        }
        void Reset()
        {
            editor.Cancel(); owner.ReplaceDocument(new() { Viewport = new(0, 0, 640, 360) }); editor.BillboardMode = false; editor.Tool = EditorTool.Select;
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, false); SetPrepare(CanvasContentImport.PrepareAsync);
            Pump(); Call("SynchronizeFlatGridOrigin"); Call("PruneDocumentResources");
        }
        Task Import(string[] selected) => (Task)Call("ImportImagesAsync", selected, false)!;
        object? Call(string method, params object?[] args) => typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, args);
        void SetPrepare(CanvasContentPreparation prepare) => typeof(DesktopSession).GetField("_prepareCanvasContent", Hidden)!.SetValue(desktop, prepare);
    }
    private static byte[] Png() { using var bitmap = new SKBitmap(5, 4); bitmap.Erase(SKColors.CadetBlue); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray(); }
    private static T Field<T>(object owner, string field) => (T)owner.GetType().GetField(field, Hidden)!.GetValue(owner)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Await(Task task) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!task.IsCompleted) { if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Private placement task timed out."); Pump(); } task.GetAwaiter().GetResult(); }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2) { if (!RegisterHotKey(0, 0x5c01, 7, key)) continue; try { if (!RegisterHotKey(0, 0x5c02, 7, key + 1)) continue; UnregisterHotKey(0, 0x5c02); return (new(7, key), new(7, key + 1)); } finally { UnregisterHotKey(0, 0x5c01); } }
        throw new InvalidOperationException("No private fixture hotkeys are available.");
    }
    private static string DesktopName() { var name = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return name.ToString(); }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
