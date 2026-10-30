using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ColorCopyFeedbackLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("dark");
        output ??= Path.Combine(ApplicationPaths.ProfileDirectory, "copy-feedback-lifecycle");
        Directory.CreateDirectory(output);
        var results = new List<(string Name, bool Passed)>();
        foreach (bool enhanced in new[] { false, true })
        foreach (bool inspector in new[] { false, true })
        foreach (bool picker in new[] { false, true })
        foreach (bool pointer in new[] { false, true }) MenuCopy(enhanced, inspector, picker, pointer, output, (name, passed) => results.Add((name, passed)));
        foreach (var (name, passed) in results) check(name, passed);
    }

    private static void MenuCopy(bool enhanced, bool useInspector, bool usePicker, bool pointer, string output, Action<string, bool> check)
    {
        string name = (enhanced ? "enhanced-" : "ordinary-") + (useInspector ? "inspector-" : "field-") + (usePicker ? "secondary" : "primary") + (pointer ? "-pointer" : "-command");
        Window? canvas = null;
        OverlayWindowScope? overlay = null;
        if (enhanced)
        {
            canvas = new Window { Width = 100, Height = 100, Left = 20, Top = 20, ShowInTaskbar = false, ShowActivated = false };
            canvas.Show();
            overlay = new OverlayWindowScope(new WindowInteropHelper(canvas).Handle, () => true);
        }
        var library = new ColorLibrary(Path.Combine(output, name + "-colors.json"));
        ColorField field;
        Window owner;
        if (useInspector)
        {
            var session = new EditorSession();
            session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60,
                Style = new() { Stroke = 0x80112233, Fill = 0x40667788 } });
            var inspector = new InspectorWindow(() => session, colorLibrary: library) { Left = 100, Top = 100, ShowInTaskbar = false };
            owner = inspector; field = Field<Dictionary<string, ColorField>>(inspector, "colorFields")["Stroke"];
        }
        else
        {
            field = new ColorField(ColorRole.Foreground, library) { Width = 360 };
            owner = new Window { Content = field, Width = 600, Height = 200, Left = 100, Top = 100,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        }
        owner.Topmost = true;
        if (enhanced) OverlayWindowScope.Prepare(owner);
        else OverlayWindowScope.UseRegularWindow(owner);
        ColorClipboardMenu? commands = null;
        try
        {
            owner.Show(); Pump(40);
            FrameworkElement target = Field<Button>(field, "button"); commands = Field<ColorClipboardMenu>(field, "clipboard");
            if (usePicker)
            {
                field.OpenPicker(); Pump(40);
                var panel = Field<ColorPickerPanel>(field, "picker");
                target = Field<Border>(panel, "preview"); commands = Field<ColorClipboardMenu>(panel, "clipboard");
            }
            string? copied = null;
            typeof(ColorClipboardMenu).GetField("writeText", Private)!.SetValue(commands, (Action<string>)(text => copied = text));
            var feedback = Field<ColorCopyFeedback>(commands, "feedback");
            var menu = commands.Menu;
            var sourceWindow = Window.GetWindow(target)!;
            nint sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
            SetActiveWindow(sourceHandle); SetFocus(sourceHandle); sourceWindow.Activate(); target.Focus(); Pump(40);
            check(name + " starts from its active source window", sourceWindow.IsActive);
            var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), Private, null, [target, true], null)!;
            opening.RoutedEvent = ContextMenuService.ContextMenuOpeningEvent; target.RaiseEvent(opening); Pump(30);
            var copy = (MenuItem)menu.Items[1]; copy.IsSubmenuOpen = true; Pump(30);
            var leaf = (MenuItem)copy.Items[1];
            bool shownAtCopy = false;
            commands.Copied += () => { shownAtCopy = feedback.IsOpen; Trace("copied"); };
            if (pointer) PointerClick(leaf);
            else typeof(MenuItem).GetMethod("OnClick", Private)!.Invoke(leaf, null);
            Trace("after-command");
            PumpUntil(() => copied is not null, 1000);
            Pump(250); Trace("after-dispatcher-close");
            check(name + " closes the real copy submenu and root menu", !menu.IsOpen && !copy.IsSubmenuOpen && copied == ColorLibrary.ToHtml(field.Value));
            check(name + " keeps the copy receipt visible after menu closure", shownAtCopy && FeedbackVisible(feedback) && target.IsVisible);
            if (FeedbackVisible(feedback) && target.IsVisible)
            {
                SetWindowPos(sourceHandle, -1, 0, 0, 0, 0, 0x13);
                check(name + " keeps the copy receipt above its source when that source is raised", AboveSource(feedback, sourceHandle));
            }
            Pump(1000); Trace("after-expiry");
            check(name + " expires the copy receipt after its intended short lifetime", !feedback.IsOpen);

            void Trace(string phase)
            {
                File.AppendAllText(Path.Combine(output, name + ".log"), $"{DateTime.UtcNow:O} {phase}: copied={copied}; feedback={feedback.IsOpen}; feedbackNativeVisible={FeedbackVisible(feedback)}; targetVisible={target.IsVisible}; targetLoaded={target.IsLoaded}; sourceActive={sourceWindow.IsActive}; menu={menu.IsOpen}; submenu={copy.IsSubmenuOpen}; picker={field.IsPickerOpen}; focus={Keyboard.FocusedElement?.GetType().Name}\n");
            }
        }
        finally
        {
            if (commands is not null) { commands.Menu.IsOpen = false; Field<ColorCopyFeedback>(commands, "feedback").Close(); }
            field.ClosePicker(); owner.Close(); Pump(30);
            overlay?.Dispose(); canvas?.Close();
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static bool FeedbackVisible(ColorCopyFeedback feedback)
    {
        if (!feedback.IsOpen) return false;
        var popup = Field<OverlayPopupWindow>(feedback, "window");
        var source = Field<HwndSource>(popup, "source");
        return !source.IsDisposed && source.Handle != 0 && IsWindow(source.Handle) && IsWindowVisible(source.Handle);
    }
    private static bool AboveSource(ColorCopyFeedback feedback, nint source)
    {
        if (!FeedbackVisible(feedback)) return false;
        var popupSource = Field<HwndSource>(Field<OverlayPopupWindow>(feedback, "window"), "source");
        for (nint candidate = GetWindow(popupSource.Handle, 3); candidate != 0; candidate = GetWindow(candidate, 3))
            if (candidate == source) return false;
        return true;
    }
    private static readonly Lazy<Type> MouseType = new(() =>
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FloatCanvas.CopyLifecycleMouse"), AssemblyBuilderAccess.Run);
        var attribute = typeof(ColorCopyFeedbackLifecycleTests).Assembly.GetType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute")!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(attribute.GetConstructor([typeof(string)])!, ["PresentationCore"]));
        var type = assembly.DefineDynamicModule("Mouse").DefineType("CopyMouse", TypeAttributes.Public, typeof(MouseDevice));
        var target = type.DefineField("target", typeof(IInputElement), FieldAttributes.Public);
        var state = type.DefineField("state", typeof(MouseButtonState), FieldAttributes.Public);
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(InputManager), typeof(IInputElement)]);
        var il = constructor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(MouseDevice).GetConstructors(Private).Single());
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stfld, target); il.Emit(OpCodes.Ret);
        var method = typeof(MouseDevice).GetMethod("GetButtonStateFromSystem", Private)!;
        var getter = type.DefineMethod(method.Name, MethodAttributes.Assembly | MethodAttributes.Virtual, typeof(MouseButtonState), [typeof(MouseButton)]);
        il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, state); il.Emit(OpCodes.Ret); type.DefineMethodOverride(getter, method);
        method = typeof(MouseDevice).GetProperty("Target")!.GetMethod!;
        getter = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName, typeof(IInputElement), Type.EmptyTypes);
        il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, target); il.Emit(OpCodes.Ret); type.DefineMethodOverride(getter, method);
        return type.CreateType()!;
    });
    private static void PointerClick(FrameworkElement target)
    {
        var source = (HwndSource)PresentationSource.FromVisual(target);
        var point = source.CompositionTarget.TransformToDevice.Transform(target.TranslatePoint(new(target.ActualWidth / 2, target.ActualHeight / 2), (UIElement)source.RootVisual));
        var mouse = (MouseDevice)Activator.CreateInstance(MouseType.Value, InputManager.Current, target)!;
        try
        {
            typeof(MouseDevice).GetField("_inputSource", Private)!.SetValue(mouse, source);
            typeof(MouseDevice).GetField("_lastPosition", Private)!.SetValue(mouse, point);
            MouseType.Value.GetField("state")!.SetValue(mouse, MouseButtonState.Pressed);
            InputManager.Current.ProcessInput(new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            MouseType.Value.GetField("state")!.SetValue(mouse, MouseButtonState.Released);
            InputManager.Current.ProcessInput(new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        }
        finally
        {
            Delegate Handler(string name, Type type) => typeof(MouseDevice).GetMethod(name, Private)!.CreateDelegate(type, mouse);
            InputManager.Current.PreProcessInput -= (PreProcessInputEventHandler)Handler("PreProcessInput", typeof(PreProcessInputEventHandler));
            InputManager.Current.PreNotifyInput -= (NotifyInputEventHandler)Handler("PreNotifyInput", typeof(NotifyInputEventHandler));
            InputManager.Current.PostProcessInput -= (ProcessInputEventHandler)Handler("PostProcessInput", typeof(ProcessInputEventHandler));
            InputManager.Current.HitTestInvalidatedAsync -= (EventHandler)Handler("OnHitTestInvalidatedAsync", typeof(EventHandler));
        }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> condition, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline) Pump(10);
    }
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
