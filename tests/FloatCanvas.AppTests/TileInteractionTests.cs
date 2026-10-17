using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Real arranged WPF tiles and controlled local input; never moves the desktop pointer or sends OS input.</summary>
internal static class TileInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-tiles-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var controller = new EditorController(new EditorSession()); var library = new ToolPresetLibrary(Path.Combine(folder, "presets.json"));
        var first = library.Save("A", controller.Parameters); var second = library.Save("B", controller.Parameters);
        var gallery = new ToolPresetGallery(controller, library) { Width = 200 }; FocusManager.SetIsFocusScope(gallery, true);
        var window = new Window { Content = gallery, Width = 230, Height = 300, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Right - 250, Top = SystemParameters.WorkArea.Bottom - 320 };
        try
        {
            window.Show(); Pump(); var source = (HwndSource)PresentationSource.FromVisual(gallery);
            var buttons = gallery.UserTiles.Children.OfType<Button>().ToArray();
            var firstButton = buttons[0]; var secondButton = buttons[1];
            Point At(Button button, double fraction) => button.TranslatePoint(new Point(button.ActualWidth * fraction, button.ActualHeight / 2), gallery.UserTiles);
            var left = TileDragReorder.ResolveDrop(gallery.UserTiles, At(secondButton, .25));
            var right = TileDragReorder.ResolveDrop(gallery.UserTiles, At(secondButton, .75));
            check("arranged tile left and right halves resolve to before and after", left is { After: false } && right is { After: true } && left.Value.Button == secondButton && right.Value.Button == secondButton);
            var lastTop = secondButton.TranslatePoint(new Point(), gallery.UserTiles);
            var blank = TileDragReorder.ResolveDrop(gallery.UserTiles, new Point(10, lastTop.Y + secondButton.ActualHeight + 24));
            check("blank space after the last tile resolves to the list end", blank is { After: true } && blank.Value.Button == secondButton);

            int applied = 0; gallery.Applied += () => applied++;
            var mouse = ControlledMouse(firstButton); SetPosition(mouse, source, gallery.UserTiles, At(firstButton, .5));
            var sample = new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left);
            check("controlled WPF event positions use the fixture tile coordinates", (sample.GetPosition(gallery.UserTiles) - At(firstButton, .5)).Length < .1);
            SendButton(mouse, Mouse.PreviewMouseDownEvent, true);
            SetPosition(mouse, source, gallery.UserTiles, At(secondButton, .75));
            InputManager.Current.ProcessInput(new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
            SendButton(mouse, Mouse.PreviewMouseUpEvent, false); Pump();
            check("actual WPF drag events move the first tile after the second", library.User.Select(p => p.Id).SequenceEqual(new[] { second.Id, first.Id }));
            check("drag release never applies or selects its source recipe", applied == 0 && gallery.SelectedId.StartsWith("official."));

            firstButton = gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, first.Id));
            secondButton = gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, second.Id));
            mouse = ControlledMouse(secondButton); SetPosition(mouse, source, gallery.UserTiles, At(secondButton, .5)); SendButton(mouse, Mouse.PreviewMouseDownEvent, true);
            var tail = firstButton.TranslatePoint(new Point(10, firstButton.ActualHeight + 24), gallery.UserTiles);
            SetPosition(mouse, source, gallery.UserTiles, tail); InputManager.Current.ProcessInput(new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
            SendButton(mouse, Mouse.PreviewMouseUpEvent, false); Pump();
            check("actual WPF drag events accept blank tail space", library.User.Select(p => p.Id).SequenceEqual(new[] { first.Id, second.Id }) && applied == 0);

            firstButton = gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, first.Id));
            secondButton = gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, second.Id));
            mouse = ControlledMouse(secondButton); SetPosition(mouse, source, gallery.UserTiles, At(secondButton, .5)); SendButton(mouse, Mouse.PreviewMouseDownEvent, true);
            SetPosition(mouse, source, gallery.UserTiles, At(firstButton, .25)); InputManager.Current.ProcessInput(new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
            SendButton(mouse, Mouse.PreviewMouseUpEvent, false); Pump();
            check("actual WPF drag events move a tile before the target", library.User.Select(p => p.Id).SequenceEqual(new[] { second.Id, first.Id }) && applied == 0);
            var rightButton = gallery.UserTiles.Children.OfType<Button>().First(); int rightEvents = 0;
            rightButton.ContextMenuOpening += (_, e) => e.Handled = true;
            rightButton.AddHandler(UIElement.PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler((_, _) => rightEvents++), true);
            rightButton.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler((_, _) => rightEvents++), true);
            mouse = ControlledMouse(rightButton); SetPosition(mouse, source, gallery.UserTiles, At(rightButton, .5));
            SendButton(mouse, Mouse.PreviewMouseDownEvent, true, MouseButton.Right); SendButton(mouse, Mouse.PreviewMouseUpEvent, false, MouseButton.Right); Pump();
            check("actual right-button events retain the context menu without applying a recipe", rightEvents == 2 && rightButton.ContextMenu.Items.Count == 3 && applied == 0 && gallery.SelectedId.StartsWith("official."));

            firstButton = gallery.UserTiles.Children.OfType<Button>().First(); FocusManager.SetFocusedElement(gallery, firstButton);
            firstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("click refresh keeps the same focused tile and applies once", ReferenceEquals(gallery.UserTiles.Children.OfType<Button>().First(), firstButton) && ReferenceEquals(FocusManager.GetFocusedElement(gallery), firstButton) && applied == 1);
            controller.Style.StrokeWidth = 14; gallery.Refresh();
            check("parameter refresh preserves the focused recipe control", ReferenceEquals(FocusManager.GetFocusedElement(gallery), firstButton) && gallery.UserTiles.Children.Contains(firstButton));

            for (int i = 0; i < 35; i++) library.Save("Extra " + i, controller.Parameters);
            gallery.Refresh(); var last = library.User.Last(); gallery.Apply(last.Id); Pump();
            var scroll = (ScrollViewer)typeof(ToolPresetGallery).GetField("userScroll", Private)!.GetValue(gallery)!;
            var lastButton = gallery.UserTiles.Children.OfType<Button>().Single(button => Equals(button.Tag, last.Id));
            var viewport = FindVisual<ScrollContentPresenter>(scroll)!; var top = lastButton.TranslatePoint(new Point(), viewport);
            check("applying a long-list recipe scrolls its selected tile into view", scroll.VerticalOffset > 0 && top.Y >= -.5 && top.Y + lastButton.ActualHeight <= viewport.ActualHeight + .5);
            check("drops below the visible scroll viewport are ignored", TileDragReorder.ResolveDrop(gallery.UserTiles, viewport.TranslatePoint(new Point(5, viewport.ActualHeight + 10), gallery.UserTiles)) is null);
            FocusManager.SetFocusedElement(gallery, lastButton);
            lastButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump();
            check("Delete remains available after selection and focuses the official fallback", library.User.All(p => p.Id != last.Id) && gallery.SelectedId == library.OfficialFor(controller.Tool).First().Id && ReferenceEquals(FocusManager.GetFocusedElement(gallery), gallery.OfficialTiles.Children.OfType<Button>().First()));
            SwatchFocus(folder, check);
        }
        finally { Mouse.Capture(null); window.Close(); Pump(); Directory.Delete(folder, true); }
    }
    private static void SwatchFocus(string folder, Action<string, bool> check)
    {
        var library = new ColorLibrary(Path.Combine(folder, "colors.json")); var saved = library.Save("Original", 0xFF112233);
        var picker = new ColorPickerPanel(ColorRole.Foreground, library); FocusManager.SetIsFocusScope(picker, true);
        var pinned = (Panel)typeof(ColorPickerPanel).GetField("pinned", Private)!.GetValue(picker)!;
        var tile = pinned.Children.OfType<Button>().Single(); FocusManager.SetFocusedElement(picker, tile);
        library.Rename(saved.Id, "Renamed"); library.Update(saved.Id, 0xFF445566); picker.RefreshCollections();
        check("renaming and overwriting a swatch preserve its focused button", ReferenceEquals(pinned.Children.OfType<Button>().Single(), tile) && ReferenceEquals(FocusManager.GetFocusedElement(picker), tile));
        tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("a reused swatch button chooses its latest saved color", picker.TryGetDraft(out uint color) && color == 0xFF445566 && tile.ToolTip is string tip && tip.StartsWith("Renamed"));
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    { if (root is T value) return value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found; return null; }
    private static void SetPosition(MouseDevice mouse, HwndSource source, UIElement relative, Point point)
    {
        var rootPoint = relative.TranslatePoint(point, (UIElement)source.RootVisual);
        typeof(MouseDevice).GetField("_inputSource", Private)!.SetValue(mouse, source);
        typeof(MouseDevice).GetField("_lastPosition", Private)!.SetValue(mouse, source.CompositionTarget.TransformToDevice.Transform(rootPoint));
    }
    private static void SendButton(MouseDevice mouse, RoutedEvent routed, bool pressed, MouseButton changed = MouseButton.Left)
    {
        mouse.GetType().GetField("pressed")!.SetValue(mouse, pressed ? 1 : 0);
        InputManager.Current.ProcessInput(new MouseButtonEventArgs(mouse, Environment.TickCount, changed) { RoutedEvent = routed });
    }
    private static readonly Lazy<Type> MouseType = new(() =>
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FloatCanvasTilesControlledInput"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!, ["PresentationCore"]));
        var type = assembly.DefineDynamicModule("Input").DefineType("TileMouse", TypeAttributes.Public, typeof(MouseDevice));
        var target = type.DefineField("target", typeof(IInputElement), FieldAttributes.Public); var state = type.DefineField("pressed", typeof(int), FieldAttributes.Public);
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(InputManager), typeof(IInputElement)]); var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, typeof(MouseDevice).GetConstructors(Private).Single()); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stfld, target); il.Emit(OpCodes.Ret);
        var original = typeof(MouseDevice).GetMethod("GetButtonStateFromSystem", Private)!;
        var method = type.DefineMethod(original.Name, MethodAttributes.Assembly | MethodAttributes.Virtual, typeof(MouseButtonState), [typeof(MouseButton)]); il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, state); il.Emit(OpCodes.Ret); type.DefineMethodOverride(method, original);
        original = typeof(MouseDevice).GetProperty("Target")!.GetMethod!;
        method = type.DefineMethod(original.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName, typeof(IInputElement), Type.EmptyTypes); il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, target); il.Emit(OpCodes.Ret); type.DefineMethodOverride(method, original);
        return type.CreateType()!;
    });
    private static MouseDevice ControlledMouse(IInputElement target) => (MouseDevice)Activator.CreateInstance(MouseType.Value, InputManager.Current, target)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
