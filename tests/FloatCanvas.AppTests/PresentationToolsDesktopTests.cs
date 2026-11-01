using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PresentationToolsDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) || !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use a fresh --isolated-ui-desktop process.");
        string folder = Path.Combine(AppContext.BaseDirectory, "presentation-tools-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", folder, "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var hotkeys = FreeHotkeys();
        var initial = new UserSettings { Theme = "dark", Language = "en", ShowFloatingLogo = false, PreferGpuAcceleration = false, Toggle = hotkeys.Item1, Hide = hotkeys.Item2 };
        initial.PresentationGlobal.Pointer.Enabled = true;
        initial.PresentationGlobal.Pointer.CursorColor = 0x804488cc;
        initial.Save();
        var bounds = new PixelBounds(-24000, -23000, 800, 700);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("presentation-tools", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var editor = Field<EditorController>(desktop, "_editor"); var session = editor.Session;
        var settings = Field<UserSettings>(desktop, "_settings");
        string document = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        Call("ActivateTool", EditorTool.PointerPresentation);
        check("the mouse demonstration is a separate formal tool using current global preferences", editor.Tool == EditorTool.PointerPresentation && editor.PointerPresentation is { Enabled: true, CursorColor: 0x804488cc });
        check("activating the mouse tool cannot write a canvas layer or undo entry", JsonSerializer.Serialize(session.Document) == document && session.UndoCount == undo);
        var next = editor.Parameters; next.PointerPresentation!.CursorRadiusPixels = 27; next.PointerPresentation.LeftClick.Color = 0x906633aa;
        editor.ApplyParameters(next);
        check("normal mouse tool parameter edits update global effects", settings.PresentationGlobal.Pointer.CursorRadiusPixels == 27 && settings.PresentationGlobal.Pointer.LeftClick.Color == 0x906633aa);
        var loaded = UserSettings.Load();
        check("mouse tool effect edits persist in the personal profile", loaded.PresentationGlobal.Pointer.CursorRadiusPixels == 27 && loaded.PresentationGlobal.Pointer.LeftClick.Color == 0x906633aa);
        Call("TogglePresentationPointer");
        check("the global quick switch synchronizes the mouse tool master without erasing styles", editor.PointerPresentation is { Enabled: false, CursorRadiusPixels: 27 } && !settings.PresentationGlobal.Pointer.Enabled);
        var library = Field<ToolPresetLibrary>(desktop, "_toolPresets");
        var recipe = library.OfficialFor(EditorTool.PointerPresentation).First();
        var applied = (ToolParameters)Call("ApplyToolbarPreset", recipe.Id, editor.Parameters)!;
        check("toolbar preset application preserves the current global mouse master", applied.Tool == EditorTool.PointerPresentation && applied.PointerPresentation?.Enabled == false);
        editor.ApplyParameters(applied);
        Call("ActivateTool", EditorTool.Presentation); Call("TogglePresentationPointer"); Call("ActivateTool", EditorTool.PointerPresentation);
        check("returning to the mouse tool uses the current master rather than stale cached tool state", editor.PointerPresentation?.Enabled == true && settings.PresentationGlobal.Pointer.Enabled);
        var panel = Field<ToolOptionsPanel>(desktop, "_toolOptionsPanel");
        var gallery = Field<ToolPresetGallery>(panel, "presets"); gallery.Apply(recipe.Id);
        check("the standard preset gallery preserves the enabled global mouse master", editor.PointerPresentation?.Enabled == true && settings.PresentationGlobal.Pointer.Enabled);
        check("mouse configuration preset selection and tool switching preserve the author scene and history", JsonSerializer.Serialize(session.Document) == document && session.UndoCount == undo);
        var original = Field<PresentationRuntimeBroker>(desktop, "_presentationRuntime"); original.Dispose();
        int starts = 0;
        var runtime = new PresentationRuntimeBroker(app.Dispatcher, () => { },
            (epoch, _, _, _) => { starts++; return Task.FromResult<IPresentationInputSource>(new Source(epoch)); });
        typeof(DesktopSession).GetField("_presentationRuntime", Hidden)!.SetValue(desktop, runtime);
        typeof(DesktopSession).GetField("_presentationDesktopAvailable", Hidden)!.SetValue(desktop, (Func<bool>)(() => true));
        host.SetVisible(true);
        var viewport = new RectD(bounds.Left - editor.ScreenOrigin.X, bounds.Top - editor.ScreenOrigin.Y, bounds.Width, bounds.Height);
        session.Execute("keyboard style demand", value =>
        {
            value.Viewport = viewport; value.Elements.Add(new() { Kind = ElementKind.Presentation, Width = 300, Height = 120,
                Transform = Affine2D.Translation(viewport.Left + 20, viewport.Top + 20),
                Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard, KeyDisplay = PresentationKeyDisplayMode.ShortcutChords } });
        });
        Call("InitializePresentation");
        check("live keyboard style requests all physical keys even when an old shortcut filter remains stored", Demand().Keyboard && Demand().AllPhysicalKeys && runtime.ObservingKeyboard);
        int keyboardStarts = starts;
        session.Execute("log style demand", value => value.Elements.Single().Presentation!.DisplayStyle = PresentationDisplayStyle.Log);
        check("log styles retain their selected shortcut filter without restarting input", Demand().Keyboard && !Demand().AllPhysicalKeys && starts == keyboardStarts);
        check("the integration fixture never observes the actual input desktop or acquires editing isolation", !PresentationDesktopAvailability.IsCurrentInputDesktop() && !host.IsKeyboardIsolationActive && !host.HasEditingInput);
        PresentationRuntimeDemand Demand() => Field<PresentationRuntimeDemand>(runtime, "demand");
        object? Call(string method, params object?[] args)
        {
            try { return typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
    private sealed class Source(long epoch) : IPresentationInputSource
    {
        public long Epoch => epoch;
        public bool IsRunning { get; private set; } = true;
        public Task Completion => Task.CompletedTask;
        public event Action? Available { add { } remove { } }
        public PresentationObservedBatch Drain() => new(epoch, [], null, false);
        public bool Stop() { IsRunning = false; return true; }
        public void Dispose() => Stop();
    }
    private static T Field<T>(object value, string field) => (T)value.GetType().GetField(field, Hidden)!.GetValue(value)!;
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5b41, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5b42, 7, key + 1)) continue; UnregisterHotKey(0, 0x5b42); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5b41); }
        }
        throw new InvalidOperationException("No free fixture hotkeys.");
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
