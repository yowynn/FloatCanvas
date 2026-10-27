using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>
/// Opt-in WPF staging regression: routes controlled mouse events exclusively to this fixture's
/// own controls. Never moves the desktop cursor, injects system input, enters editing or changes
/// the running application. Production popup handlers and WPF target resolution remain intact.
/// </summary>
internal static class ToolOptionsInputFixture
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --tool-options-input-only in a fresh process.");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvasOptionsInput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        L.Initialize("zh-CN"); AppTheme.Apply("light");
        var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        var toolbarTarget = new TextBlock { Text = "Isolated tool options input fixture" };
        var toolButton = new Button { Content = "Rectangle", Width = 28, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(200, 0, 0, 0) };
        var toolbarBody = new Grid(); toolbarBody.Children.Add(toolbarTarget); var launcher = new ToolbarLauncher { HorizontalAlignment = HorizontalAlignment.Left }; toolbarBody.Children.Add(launcher); toolbarBody.Children.Add(toolButton);
        var toolbar = new Window { Width = 520, Height = 100, Left = 500, Top = 180, ShowActivated = false,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Content = toolbarBody };
        var editor = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
        Set("_app", app); Set("_settings", new UserSettings()); Set("_windowLayout", new SessionWindowLayout());
        Set("_editor", editor); Set("_toolPresets", new ToolPresetLibrary(Path.Combine(folder, "presets.json")));
        Set("_colorLibrary", new ColorLibrary(Path.Combine(folder, "colors.json")));
        var fixtureHost = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
        var monitors = (IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        typeof(OverlayHost).GetField("_monitors", PrivateInstance)!.SetValue(fixtureHost, monitors);
        Set("_host", fixtureHost); Set("_toolbar", toolbar); Set("_toolbarLauncher", launcher);
        Set("_toolbarToolButtons", new Dictionary<EditorTool, Button> { [EditorTool.Rectangle] = toolButton });
        nint toolbarHandle = 0, popupHandle = 0;
        try
        {
            Invoke("CreateToolOptionsPopup", toolbar); toolbar.Show(); Pump();
            toolbarHandle = new WindowInteropHelper(toolbar).Handle;
            var popup = Get<Window>(session, "_toolOptionsWindow");
            Invoke("OpenToolOptions"); Pump();
            var frame = Get<Border>(session, "_toolOptionsFrame");
            popupHandle = ((HwndSource)PresentationSource.FromVisual(frame)).Handle;
            var toolbarBottom = toolbar.PointToScreen(new Point(0, toolbar.ActualHeight));
            var frameTop = popup.PointToScreen(new Point());
            double dpiScale = VisualTreeHelper.GetDpi(toolbar).DpiScaleY;
            check("native options window initially clears the complete toolbar by eight DIP", Math.Abs(frameTop.Y - toolbarBottom.Y - 8 * dpiScale) <= 2);
            double firstX = frameTop.X; toolbar.Left += 31; Invoke("RepositionToolOptions"); Pump();
            check("an already opened options window does not follow toolbar movement", Math.Abs(popup.PointToScreen(new Point()).X - firstX) <= 2);
            double movedLeft = popup.Left + 24, movedTop = popup.Top + 17; popup.Left = movedLeft; popup.Top = movedTop; Pump();
            Invoke("AnchorToolOptions"); Pump();
            check("options refresh preserves an independently dragged window position", Math.Abs(popup.Left - movedLeft) <= 1 && Math.Abs(popup.Top - movedTop) <= 1);
            var monitor = monitors.First(m => m.WorkArea.Contains(new(toolbar.PointToScreen(new Point()).X, toolbar.PointToScreen(new Point()).Y)));
            toolbar.Top = monitor.WorkArea.Bottom / dpiScale - toolbar.ActualHeight - 2;
            Invoke("CloseToolOptions"); Invoke("OpenToolOptions"); Pump();
            var frameBottom = popup.PointToScreen(new Point(0, popup.ActualHeight));
            check("a newly opened options window switches above a toolbar near the work area bottom", Math.Abs(toolbar.PointToScreen(new Point()).Y - frameBottom.Y - 8 * dpiScale) <= 2);
            toolbar.Top = 180; Invoke("CloseToolOptions"); Invoke("OpenToolOptions"); Pump();
            var panel = Get<ToolOptionsPanel>(session, "_toolOptionsPanel");
            string? previews = Environment.GetEnvironmentVariable("FLOATCANVAS_OPTIONS_PREVIEW_DIR");
            if (!string.IsNullOrEmpty(previews))
            {
                Directory.CreateDirectory(previews);
                foreach (string theme in new[] { "light", "dark" })
                {
                    AppTheme.Apply(theme);
                    foreach (var tool in new[] { EditorTool.Line, EditorTool.Rectangle, EditorTool.Text, EditorTool.Emoji })
                    {
                        editor.Tool = tool; panel.Refresh(); Pump(); frame.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth * 1.5), (int)Math.Ceiling(frame.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                        bitmap.Render(frame); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(previews, $"options-{theme}-{tool}.png")); encoder.Save(output);
                    }
                }
                editor.Tool = EditorTool.Rectangle; panel.Refresh(); AppTheme.Apply("light"); Pump();
            }

            var boxes = Get<Dictionary<string, TextBox>>(panel, "numbers");
            var box = boxes["StrokeWidth"];
            int innerDown = 0, unresolved = 0;
            box.PreviewMouseDown += (_, _) => innerDown++;
            PreProcessInputEventHandler trace = (_, e) =>
            {
                if (e.StagingItem.Input is MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed, OriginalSource: null }) unresolved++;
            };
            InputManager.Current.PreProcessInput += trace;
            try { Drive(box); }
            finally { InputManager.Current.PreProcessInput -= trace; }
            check("real WPF staging initially has an unresolved target for an internal press", unresolved > 0);
            check("first internal textbox press reaches the control without dismissing options", popup.IsVisible && innerDown == 1);
            check("production input ownership recognizes the numeric field after routing", (bool)Invoke("IsOptionsInput", box)!);

            var combo = Get<Dictionary<string, SegmentedChoice>>(panel, "pickers")["StrokePattern"];
            var selectedItem = combo.Items.Single(item => Equals(item.Tag, editor.Style.StrokePattern));
            check("segmented choices belong to their originating options control", (bool)Invoke("IsOptionsInput", combo)!);
            check("segmented radio options share the same options input scope", (bool)Invoke("IsOptionsInput", selectedItem)!);
            Drive(selectedItem);
            check("segmented option press does not dismiss its parent options popup", popup.IsVisible);

            var foreground = Get<ColorField>(panel, "foreground");
            Get<Button>(foreground, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var colorPopup = Get<Popup>(foreground, "popup");
            var color = Get<ColorPickerPanel>(foreground, "picker");
            var hex = Get<TextBox>(color, "hex");
            check("the options color picker opened in its own child popup", colorPopup.IsOpen);
            Drive(hex);
            check("custom color input keeps both the child picker and parent options open", colorPopup.IsOpen && popup.IsVisible);
            check("production focus ownership recognizes the child picker input", (bool)Invoke("IsOptionsInput", hex)!);
            foreground.ClosePicker();

            Drive(toolbarTarget);
            check("toolbar presses preserve the options so reselect and pin actions can run", popup.IsVisible);
            var outside = new Border { Child = new TextBlock { Text = "Outside the toolbar and options" } };
            Drive((TextBlock)outside.Child);
            check("a press outside toolbar and options closes unpinned options", !popup.IsVisible);
            Invoke("OpenToolOptions"); Pump(); Set("_toolOptionsPinned", true); Drive((TextBlock)outside.Child);
            check("a press outside preserves explicitly pinned options", popup.IsVisible);
            Set("_toolOptionsPinned", false);
            var unrelatedPicker = new ColorPickerPanel(ColorRole.Foreground, new ColorLibrary(Path.Combine(folder, "other-colors.json")));
            Drive(Get<TextBox>(unrelatedPicker, "hex"));
            check("an unrelated color picker is not mistaken for this options popup", !popup.IsVisible);

            Invoke("OpenToolOptions"); Pump();
            var host = Get<OverlayHost>(session, "_host");
            var canvas = (Action<OverlayPointerEvent>)typeof(OverlayHost).GetField("Pointer", PrivateInstance)!.GetValue(host)!;
            canvas(new(OverlayPointerKind.Down, new(0, 0), OverlayMouseButton.Left, OverlayModifiers.None));
            check("a native canvas press closes unpinned options", !popup.IsVisible);
            Invoke("OpenToolOptions"); Pump(); Set("_toolOptionsPinned", true);
            canvas(new(OverlayPointerKind.Down, new(0, 0), OverlayMouseButton.Left, OverlayModifiers.None));
            check("a native canvas press keeps pinned options", popup.IsVisible);
        }
        finally
        {
            Get<Window>(session, "_toolOptionsWindow").Hide();
            Set("_disposed", true); toolbar.Close(); Pump();
            string cleanupRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FloatCanvasOptionsInput")) + Path.DirectorySeparatorChar;
            string cleanupTarget = Path.GetFullPath(folder);
            if (!cleanupTarget.StartsWith(cleanupRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected options fixture profile");
            Directory.Delete(cleanupTarget, recursive: true);
        }
        check("the isolated fixture destroys both of its native surfaces", !IsWindow(toolbarHandle) && !IsWindow(popupHandle));
        void Set(string name, object value) => typeof(DesktopSession).GetField(name, PrivateInstance)!.SetValue(session, value);
        object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, args);
    }

    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static void Drive(IInputElement target)
    {
        var device = ControlledMouse(target);
        try
        {
            InputManager.Current.ProcessInput(new MouseButtonEventArgs(device, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Pump();
        }
        finally { ReleaseControlledMouse(device); }
    }

    internal static void ReleaseControlledMouse(MouseDevice device)
    {
        // MouseDevice's constructor subscribes every instance to the shared InputManager.
        // Its PostProcessInput promotes every preview event using that instance, without
        // checking the incoming device. Leaving a mock attached replays future presses
        // against its stale Target and can steal a later popup's capture.
        // https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/MouseDevice.cs
        var manager = InputManager.Current;
        Delegate Handler(string name, Type type) => typeof(MouseDevice).GetMethod(name, PrivateInstance)!.CreateDelegate(type, device);
        manager.PreProcessInput -= (PreProcessInputEventHandler)Handler("PreProcessInput", typeof(PreProcessInputEventHandler));
        manager.PreNotifyInput -= (NotifyInputEventHandler)Handler("PreNotifyInput", typeof(NotifyInputEventHandler));
        manager.PostProcessInput -= (ProcessInputEventHandler)Handler("PostProcessInput", typeof(ProcessInputEventHandler));
        manager.HitTestInvalidatedAsync -= (EventHandler)Handler("OnHitTestInvalidatedAsync", typeof(EventHandler));
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    // MouseDevice has an internal abstract platform-state method. A fixture-only dynamic subclass
    // supplies Pressed without calling SendInput/SetCursorPos or touching desktop mouse state.
    private static readonly Lazy<Type> MouseType = new(() =>
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FloatCanvasOptionsControlledInput"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!, ["PresentationCore"]));
        var module = assembly.DefineDynamicModule("ControlledInput");
        var type = module.DefineType("ControlledMouse", TypeAttributes.Public, typeof(MouseDevice));
        var target = type.DefineField("target", typeof(IInputElement), FieldAttributes.Public);
        var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(InputManager), typeof(IInputElement)]);
        var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(MouseDevice).GetConstructors(PrivateInstance).Single());
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stfld, target); il.Emit(OpCodes.Ret);
        var method = typeof(MouseDevice).GetMethod("GetButtonStateFromSystem", PrivateInstance)!;
        var getter = type.DefineMethod(method.Name, MethodAttributes.Assembly | MethodAttributes.Virtual, typeof(MouseButtonState), [typeof(MouseButton)]);
        il = getter.GetILGenerator(); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret); type.DefineMethodOverride(getter, method);
        method = typeof(MouseDevice).GetProperty("Target")!.GetMethod!;
        getter = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName, typeof(IInputElement), Type.EmptyTypes);
        il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, target); il.Emit(OpCodes.Ret); type.DefineMethodOverride(getter, method);
        return type.CreateType()!;
    });
    private static MouseDevice ControlledMouse(IInputElement target) => (MouseDevice)Activator.CreateInstance(MouseType.Value, InputManager.Current, target)!;
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}
