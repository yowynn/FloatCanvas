using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Real ColorField/WindowPopup sessions around the production sampling pointer route.</summary>
internal static class EyedropperWindowRestoreTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string originalProfile = ApplicationPaths.ProfileDirectory, language = L.Preference;
        var originalScope = ApplicationPaths.RequestedMonitorScope;
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-eyedropper-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ApplicationPaths.Configure(["--profile", folder, "--primary-screen"]);
        new UserSettings { Theme = "light", Language = "zh-CN" }.Save();
        try
        {
            foreach (bool cancel in new[] { false, true }) Completion(app, cancel, check);
            RepeatedBegin(app, check);
            OtherColorWindow(app, check);
            NoInventedPicker(app, check);
            ModeExit(app, check);
        }
        finally
        {
            var arguments = new List<string> { "--profile", originalProfile };
            if (originalScope is { } scope) arguments.Add(scope == OverlayMonitorScope.PrimaryScreen ? "--primary-screen" : "--all-screens");
            ApplicationPaths.Configure(arguments.ToArray()); L.Initialize(language);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Completion(Application app, bool cancel, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var color = fixture.OpenTarget();
        string label = cancel ? "cancelled" : "sampled";
        fixture.BeginFromPicker(); Pump();
        check(label + " sampling suspends the original color window and its parent without closing the picker", fixture.Target.IsPickerOpen &&
            !color.IsVisible && !fixture.Panel.IsVisible && !fixture.Layers.IsVisible && fixture.Toolbar.IsVisible);
        fixture.Invoke("ShowEditingWindows"); Pump();
        check(label + " sampling activation does not reopen wanted panels early", !fixture.Panel.IsVisible && !fixture.Layers.IsVisible && !color.IsVisible);
        fixture.Pointer(OverlayPointerKind.Down, cancel ? OverlayMouseButton.Right : OverlayMouseButton.Left);
        fixture.Invoke("ShowEditingWindows"); Pump();
        check(label + " sampling press keeps both popup levels hidden until release", !fixture.Panel.IsVisible && !fixture.Layers.IsVisible && !color.IsVisible);
        fixture.Pointer(OverlayPointerKind.Up, cancel ? OverlayMouseButton.Right : OverlayMouseButton.Left); Pump();
        check(label + " sampling returns the exact original parent and color window", fixture.Panel.IsVisible && fixture.Layers.IsVisible &&
            fixture.Target.IsPickerOpen && color.IsVisible && ReferenceEquals(Window.GetWindow(fixture.Target.PickerRoot), color));
        Pump();
        check(label + " restored color window survives queued focus and input checks", fixture.Target.IsPickerOpen && color.IsVisible && fixture.Panel.IsVisible);
        fixture.AssertNoInput(check);
    }

    private static void NoInventedPicker(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        check("sampling fixture starts without a hidden color window", !fixture.Target.IsPickerOpen);
        fixture.Invoke("BeginEyedropper", fixture.Target); Pump();
        fixture.Invoke("EndEyedropper", true); Pump();
        check("ending sampling does not create a color window that was not open beforehand", !fixture.Target.IsPickerOpen &&
            fixture.Panel.IsVisible && fixture.Layers.IsVisible);
        fixture.AssertNoInput(check);
    }

    private static void RepeatedBegin(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var color = fixture.OpenTarget(); fixture.BeginFromPicker();
        fixture.Invoke("BeginEyedropper", fixture.Target); Pump();
        check("reentering sampling retains the original suspended color session", fixture.Target.IsPickerOpen &&
            ReferenceEquals(Window.GetWindow(fixture.Target.PickerRoot), color) && !color.IsVisible && !fixture.Panel.IsVisible && !fixture.Layers.IsVisible);
        fixture.Invoke("EndEyedropper", true); Pump();
        check("ending a repeatedly entered sampler restores the original complete window set", fixture.Panel.IsVisible &&
            fixture.Layers.IsVisible && fixture.Target.IsPickerOpen && color.IsVisible && ReferenceEquals(Window.GetWindow(fixture.Target.PickerRoot), color));
        fixture.AssertNoInput(check);
    }

    private static void OtherColorWindow(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var other = Field<ColorField>(fixture.Panel, "gridColor");
        other.OpenPicker(); Pump();
        var color = Window.GetWindow(other.PickerRoot)!;
        fixture.Invoke("BeginEyedropper", fixture.Target); Pump();
        fixture.Invoke("EndEyedropper", true); Pump();
        check("sampling preserves another previously open secondary color window", other.IsPickerOpen && color.IsVisible &&
            ReferenceEquals(Window.GetWindow(other.PickerRoot), color) && fixture.Panel.IsVisible && !fixture.Target.IsPickerOpen);
        fixture.AssertNoInput(check);
    }

    private static void ModeExit(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var color = fixture.OpenTarget(); fixture.BeginFromPicker();
        fixture.Invoke("SetEditing", false, false); Pump();
        check("leaving edit mode during sampling never restores either color layer", !fixture.Host.IsEditing &&
            !fixture.Panel.IsVisible && !fixture.Layers.IsVisible && !fixture.Target.IsPickerOpen && !color.IsVisible);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(fixture.Host, true);
        fixture.Invoke("ShowEditingWindows"); Pump();
        check("reentering editing restores original pinned panels without reopening the discarded color popup", fixture.Panel.IsVisible &&
            fixture.Layers.IsVisible && fixture.Panel.IsPinned && !fixture.Target.IsPickerOpen && !color.IsVisible);
        fixture.AssertNoInput(check);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Application App;
        internal readonly DesktopSession Session;
        internal readonly OverlayHost Host;
        internal readonly Window Toolbar;
        internal readonly CanvasSettingsWindow Panel;
        internal readonly ColorField Target;
        internal readonly LayersWindow Layers;

        internal Fixture(Application app)
        {
            App = app;
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = bounds, WorkArea = bounds }];
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            Session = new DesktopSession(app, Host);
            Field<System.Windows.Forms.NotifyIcon>(Session, "_tray").Visible = false;
            var editor = Field<EditorController>(Session, "_editor"); editor.Tool = EditorTool.Canvas;
            Toolbar = Field<Window>(Session, "_toolbar"); Offscreen(Toolbar); Toolbar.Show();
            Panel = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly()) { Owner = Toolbar };
            Field<Button>(Panel, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Offscreen(Panel); typeof(DesktopSession).GetField("_canvasSettingsWindow", Private)!.SetValue(Session, Panel);
            Target = Field<ColorField>(Panel, "background");
            Target.EyedropperRequested += () => Invoke("BeginEyedropper", Target);
            Layers = new LayersWindow(() => editor.Session, () => true) { Owner = Toolbar };
            Offscreen(Layers); typeof(DesktopSession).GetField("_layers", Private)!.SetValue(Session, Layers);
            typeof(DesktopSession).GetField("_layersWanted", Private)!.SetValue(Session, true);
            var coordinator = Field<SessionWindowCoordinator>(Session, "_windowCoordinator");
            coordinator.Register(Panel, () => Panel.IsPinned); coordinator.Register(Layers, () => true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(Host, true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(Host, true);
            Panel.Refresh(editor.Session.Document.Canvas); Panel.PrepareForShow(); Panel.Show(); Layers.Show(); Pump();
        }

        internal Window OpenTarget()
        { Target.OpenPicker(); Pump(); return Window.GetWindow(Target.PickerRoot)!; }
        internal void BeginFromPicker()
        {
            var picker = Field<ColorPickerPanel>(Target, "picker");
            var button = Descendants(picker).OfType<Button>().Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == L.Get("Color.Eyedropper"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }
        internal void Pointer(OverlayPointerKind kind, OverlayMouseButton button) => Invoke("HandleCanvasPointer",
            new OverlayPointerEvent(kind, new(Host.PrimaryMonitor.Bounds.Left + 5, Host.PrimaryMonitor.Bounds.Top + 5), button, OverlayModifiers.None));
        internal object? Invoke(string method, params object[] arguments) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(Session, arguments);
        internal void AssertNoInput(Action<string, bool> check) => check("eyedropper fixture never acquires desktop input or shows overlay HWNDs",
            !Host.IsKeyboardIsolationActive && Host.Diagnostics.All(value => !IsWindowVisible(value.DisplayHandle) && !IsWindowVisible(value.InputHandle)));
        public void Dispose()
        {
            Invoke("EndEyedropper", false);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(Host, false);
            Session.Dispose(); Panel.CloseForDispose(); Pump();
        }
    }
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, Private)!.GetValue(source)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject source)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(source).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Offscreen(Window window)
    {
        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Opacity = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = desktop.Left - 10000; window.Top = desktop.Top - 10000;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
