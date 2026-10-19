using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;

/// <summary>Arranged local drag routes; no desktop pointer movement or OS input injection.</summary>
internal static class TileDragReorderTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        var body = new DockPanel { Margin = new Thickness(12) };
        var slot = new Button { Width = 48, Height = 48, Content = "1", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(slot, Dock.Top); body.Children.Add(slot);
        var gallery = new PixelTilePanel(3, 36, 4) { VerticalAlignment = VerticalAlignment.Top };
        var first = new Button { Content = "A", Tag = "A", ToolTip = "A" };
        var second = new Button { Content = "B", Tag = "B" };
        gallery.Children.Add(first); gallery.Children.Add(second);
        var scroll = new ScrollViewer { Content = gallery, Height = 140, VerticalAlignment = VerticalAlignment.Top };
        body.Children.Add(scroll);
        var window = new Window { Content = new AdornerDecorator { Child = body }, Width = 260, Height = 300,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Right - 280, Top = SystemParameters.WorkArea.Bottom - 320 };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) e.Handled = true; };
        int reordered = 0, pinned = 0, clicks = 0; bool throwOnDrop = false;
        TileDragReorder.Attach(first, gallery, (_, _) => { reordered++; if (throwOnDrop) throw new InvalidOperationException("fixture drop failure"); },
            () => [new(slot, () => pinned++)]);
        first.PreviewMouseLeftButtonUp += (_, e) => { if (!e.Handled) clicks++; };
        try
        {
            window.Show(); Pump(); var source = (HwndSource)PresentationSource.FromVisual(gallery);
            var mouse = ControlledMouse(first);
            Point At(FrameworkElement element, double fraction = .5) => element.TranslatePoint(new Point(element.ActualWidth * fraction, element.ActualHeight / 2), gallery);
            void Press() { Position(mouse, source, gallery, At(first)); Button(first, mouse, true); }
            void Move(Point point)
            {
                Position(mouse, source, gallery, point);
                first.RaiseEvent(new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                // Native capture can refresh MouseDevice's position cache from the real pointer.
                Position(mouse, source, gallery, point);
            }
            void Release() => Button(first, mouse, false);
            TileDragFeedback? Feedback() => Layer(gallery).GetAdorners(gallery)?.OfType<TileDragFeedback>().SingleOrDefault();
            void Start() { Press(); Move(At(second, .75)); }
            void Restored(string reason) => check(reason, Feedback() is null && Math.Abs(first.Opacity - 1) < .001 && ToolTipService.GetIsEnabled(first) && !first.IsMouseCaptured);

            Press(); Move(At(first) + new Vector(SystemParameters.MinimumHorizontalDragDistance / 2, 0));
            check("tile press below the existing threshold has no drag visual", Feedback() is null && first.Opacity == 1);
            Release(); check("an ordinary tile release remains available to its click handler", clicks == 1 && reordered == 0);

            Start(); var feedback = Feedback();
            check("dragging adds one input-transparent ghost and dims only its source", feedback is { IsHitTestVisible: false, Focusable: false } && first.Opacity == .35 && !ToolTipService.GetIsEnabled(first) && second.Opacity == 1);
            check("right-half drag target shows an after insertion line", feedback?.TargetBounds is { } after && Math.Abs(after.X - (At(second, 1).X)) < .1 && after.Width == 0 && !feedback.IsExternalTarget);
            var beforeGhost = feedback!.GhostBounds;
            Move(At(second, .25));
            check("the same ghost follows the pointer and clears the marker at its unchanged original gap", ReferenceEquals(feedback, Feedback()) && Math.Abs(feedback.GhostBounds.X - beforeGhost.X + second.ActualWidth / 2) < .1 &&
                feedback.TargetBounds is null);
            SavePreview(window);
            Move(new Point(5, 170));
            check("leaving the visible scroll viewport clears the insertion marker", feedback.TargetBounds is null);
            Release(); Restored("outside drop removes ghost, capture, tooltip override and source dimming");
            check("outside drag release never leaks into ordinary tile activation", reordered == 0 && clicks == 1);

            Start(); window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Restored("Escape cancels feedback even when the host handles the key first"); Release();
            check("Escape then mouse release never reorders or activates the tile", reordered == 0 && clicks == 1);

            Start(); Mouse.Capture(null); Restored("losing mouse capture cancels the drag visual"); Release();
            check("capture loss then release never activates a tile", reordered == 0 && clicks == 1);

            Start(); typeof(Window).GetMethod("OnDeactivated", Private)!.Invoke(window, [EventArgs.Empty]);
            Restored("window deactivation cancels local drag feedback"); Release();
            check("deactivation then release never activates a tile", reordered == 0 && clicks == 1);

            Start(); gallery.Children.Remove(first); Pump();
            Restored("unloading the source removes the adorner and restores its visual state");
            gallery.Children.Insert(0, first); Pump(); Release();
            check("unload then release does not run a stale drop or click callback", reordered == 0 && clicks == 1);

            Start(); Move(At(slot));
            check("cross-container slot hover shows a bounded destination outline", Feedback() is { IsExternalTarget: true, TargetBounds: { } external } && external.Width == slot.ActualWidth && external.Height == slot.ActualHeight);
            Release(); Restored("cross-container drop cleans feedback before its callback");
            check("slot drop invokes exactly one external callback and no reorder or ordinary click", pinned == 1 && reordered == 0 && clicks == 1);

            throwOnDrop = true; Start(); bool threw = false;
            try { Release(); } catch (InvalidOperationException ex) when (ex.Message == "fixture drop failure") { threw = true; }
            Restored("a throwing drop callback leaves no adorner or input capture");
            check("drop callback exceptions stay visible after visual cleanup", threw && reordered == 1 && clicks == 1);
            throwOnDrop = false; Start(); Release();
            check("the tile can be dragged again after a failed drop callback", reordered == 2 && clicks == 1 && Feedback() is null);

            ItemsControlRoute(window, check);
            PopupRoute(window, check);
        }
        finally { Mouse.Capture(null); window.Close(); Pump(); }
    }

    private static void PopupRoute(Window window, Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-popup-drag-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var library = new ColorLibrary(Path.Combine(folder, "colors.json"));
        var firstColor = library.Save("A", 0xFF112233); library.Save("B", 0xFF445566);
        var field = new ColorField(ColorRole.Foreground, library);
        var popup = (Popup)typeof(ColorField).GetField("popup", Private)!.GetValue(field)!;
        try
        {
            window.Content = field; Pump(); popup.IsOpen = true; Pump();
            var picker = (ColorPickerPanel)typeof(ColorField).GetField("picker", Private)!.GetValue(field)!;
            var gallery = (Panel)typeof(ColorPickerPanel).GetField("pinned", Private)!.GetValue(picker)!;
            var first = gallery.Children.OfType<Button>().First(); var second = gallery.Children.OfType<Button>().Last();
            var layer = TileDragFeedback.GetLayer(gallery);
            check("the production color Popup supplies an adorner layer for saved swatches", layer is not null && gallery.Children.Count == 2);
            var source = (HwndSource)PresentationSource.FromVisual(gallery); var root = (UIElement)source.RootVisual;
            root.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) e.Handled = true; };
            var mouse = ControlledMouse(first);
            void Start()
            {
                Position(mouse, source, gallery, first.TranslatePoint(new Point(10, 10), gallery)); Button(first, mouse, true);
                var point = second.TranslatePoint(new Point(25, 10), gallery); Position(mouse, source, gallery, point);
                first.RaiseEvent(new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                Position(mouse, source, gallery, point);
            }
            Start();
            check("saved colors in the real Popup show the ghost and insertion line", layer!.GetAdorners(gallery)?.OfType<TileDragFeedback>().SingleOrDefault() is { TargetBounds: not null } && first.Opacity == .35);
            first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Button(first, mouse, false);
            check("Popup Escape cleans feedback even if its root handled the key first", layer.GetAdorners(gallery) is null && first.Opacity == 1 && library.Swatches.First().Id == firstColor.Id);
            Start(); popup.IsOpen = false; Pump();
            check("closing the color Popup removes its ghost and capture without a drop", layer.GetAdorners(gallery) is null && first.Opacity == 1 && !first.IsMouseCaptured && library.Swatches.First().Id == firstColor.Id);
        }
        finally { popup.IsOpen = false; Pump(); Directory.Delete(folder, true); }
    }

    private static void ItemsControlRoute(Window window, Action<string, bool> check)
    {
        var body = new DockPanel { Margin = new Thickness(12) };
        var slot = new Button { Width = 48, Height = 48, Content = "1", HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(slot, Dock.Top); body.Children.Add(slot);
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        var first = new ListBoxItem { Content = "A", Width = 60, Height = 60 };
        var second = new ListBoxItem { Content = "B", Width = 60, Height = 60 };
        list.Items.Add(first); list.Items.Add(second); body.Children.Add(list);
        int reorders = 0, pins = 0, ordinaryReleases = 0, activations = 0;
        FrameworkElement? destination = null; bool after = false;
        TileDragReorder.Attach(first, list, (target, insertAfter) => { reorders++; destination = target; after = insertAfter; }, () => [new(slot, () => pins++)], () => activations++);
        TileDragReorder.Attach(second, list, (_, _) => { }, clicked: () => activations++);
        first.PreviewMouseLeftButtonUp += (_, e) => { if (!e.Handled) ordinaryReleases++; };
        window.Content = new AdornerDecorator { Child = body }; Pump();
        var source = (HwndSource)PresentationSource.FromVisual(list); var mouse = ControlledMouse(first);
        void At(FrameworkElement element, double fraction = .5) => Position(mouse, source, list, element.TranslatePoint(new Point(element.ActualWidth * fraction, element.ActualHeight / 2), list));
        void Start(FrameworkElement target, double fraction = .5)
        {
            At(first); Button(first, mouse, true); At(target, fraction);
            first.RaiseEvent(new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
            At(target, fraction);
        }
        Start(second, .75); Button(first, mouse, false);
        check("ListBox item drag uses its arranged container and suppresses ordinary release", reorders == 1 && ReferenceEquals(destination, second) && after && ordinaryReleases == 0);
        Start(slot); Button(first, mouse, false);
        check("ListBox item can pin to a slot once without reordering or ordinary activation", pins == 1 && reorders == 1 && ordinaryReleases == 0);
        Start(second); window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Button(first, mouse, false);
        check("cancelled ListBox drag suppresses the later ordinary release", pins == 1 && reorders == 1 && ordinaryReleases == 0 && activations == 0 && Layer(list).GetAdorners(list) is null);
        Start(second); window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        var secondMouse = ControlledMouse(second);
        Position(secondMouse, source, list, second.TranslatePoint(new Point(20, 20), list)); Button(second, secondMouse, false);
        check("cancelled A drag released over B never activates B without its own press", activations == 0 && pins == 1 && reorders == 1);
        Button(second, secondMouse, true); Button(second, secondMouse, false);
        check("a matching ListBox tile press and inside release activates exactly once", activations == 1);
        Button(second, secondMouse, true); Position(secondMouse, source, list, new Point(5, 230)); Button(second, secondMouse, false);
        check("an ordinary release outside the pressed tile never activates it", activations == 1);

        // Use the actual ListBox notification/capture path, then send the first
        // move to its parent route rather than forcing a move onto the source tile.
        // WPF's parent auto-scroll consults the physical Mouse.LeftButton. The
        // fixture supplies its own pressed device, so suppress that unrelated
        // bubble handler while preserving the complete preview input route.
        MouseEventHandler stopNativeAutoScroll = (_, e) => e.Handled = true;
        list.AddHandler(Mouse.PreviewMouseMoveEvent, stopNativeAutoScroll, true);
        Point intendedPoint = first.TranslatePoint(new Point(20, 20), list);
        // Capture synchronously refreshes a controlled device's position cache
        // from the physical pointer. Keep this fixture's own pointer stable.
        MouseEventHandler holdControlledPosition = (_, e) => { if (ReferenceEquals(e.MouseDevice, mouse)) Position(mouse, source, list, intendedPoint); };
        window.AddHandler(Mouse.PreviewMouseMoveEvent, holdControlledPosition, true);
        try
        {
            // The fixture is intentionally not activated; make the same WPF
            // notification directly, then arm the helper after native capture's
            // position-cache refresh, without injecting a physical button press.
            typeof(ListBox).GetMethod("NotifyListItemClicked", Private)!.Invoke(list, [first, MouseButton.Left]);
            check("the WPF ListBox item notification selects the source and captures its parent", first.IsSelected && ReferenceEquals(Mouse.Captured, list));
            At(first); Button(first, mouse, true);
            intendedPoint = slot.TranslatePoint(new Point(slot.ActualWidth / 2, slot.ActualHeight / 2), list);
            mouse.GetType().GetField("target")!.SetValue(mouse, list); At(slot);
            list.RaiseEvent(new MouseEventArgs(mouse, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
            check("a first move beyond the source tile starts dragging through the captured parent route", first.IsMouseCaptured && Layer(list).GetAdorners(list)?.OfType<TileDragFeedback>().SingleOrDefault() is { IsExternalTarget: true, TargetBounds: not null });
            mouse.GetType().GetField("target")!.SetValue(mouse, first); At(slot); mouse.GetType().GetField("pressed")!.SetValue(mouse, 0);
            Button(first, mouse, false);
            check("the captured-parent first-move route drops externally once and fully cleans capture", pins == 2 && reorders == 1 && activations == 1 && !first.IsMouseCaptured && Layer(list).GetAdorners(list) is null);
        }
        finally { window.RemoveHandler(Mouse.PreviewMouseMoveEvent, holdControlledPosition); list.RemoveHandler(Mouse.PreviewMouseMoveEvent, stopNativeAutoScroll); Mouse.Capture(null); }
    }

    private static AdornerLayer Layer(FrameworkElement element)=>TileDragFeedback.GetLayer(element)??throw new InvalidOperationException("The drag fixture has no root adorner layer.");
    private static void SavePreview(Window window)
    {
        string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_TILE_DRAG_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(output, "tile-drag-feedback.png")); encoder.Save(stream);
    }
    private static void Position(MouseDevice mouse, HwndSource source, UIElement relative, Point point)
    {
        var root = relative.TranslatePoint(point, (UIElement)source.RootVisual);
        typeof(MouseDevice).GetField("_inputSource", Private)!.SetValue(mouse, source);
        typeof(MouseDevice).GetField("_lastPosition", Private)!.SetValue(mouse, source.CompositionTarget.TransformToDevice.Transform(root));
    }
    private static void Button(FrameworkElement tile, MouseDevice mouse, bool pressed)
    {
        mouse.GetType().GetField("pressed")!.SetValue(mouse, pressed ? 1 : 0);
        tile.RaiseEvent(new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = pressed ? UIElement.PreviewMouseLeftButtonDownEvent : UIElement.PreviewMouseLeftButtonUpEvent });
    }
    private static readonly Lazy<Type> MouseType = new(() =>
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FloatCanvasDragControlledInput"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!, ["PresentationCore"]));
        var type = assembly.DefineDynamicModule("Input").DefineType("DragMouse", TypeAttributes.Public, typeof(MouseDevice));
        var target = type.DefineField("target", typeof(IInputElement), FieldAttributes.Public); var state = type.DefineField("pressed", typeof(int), FieldAttributes.Public);
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(InputManager), typeof(IInputElement)]); var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, typeof(MouseDevice).GetConstructors(Private).Single()); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stfld, target); il.Emit(OpCodes.Ret);
        var original = typeof(MouseDevice).GetMethod("GetButtonStateFromSystem", Private)!;
        var method = type.DefineMethod(original.Name, MethodAttributes.Assembly | MethodAttributes.Virtual, typeof(MouseButtonState), [typeof(MouseButton)]); il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, state); il.Emit(OpCodes.Ret); type.DefineMethodOverride(method, original);
        original = typeof(MouseDevice).GetProperty("Target")!.GetMethod!;
        method = type.DefineMethod(original.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName, typeof(IInputElement), Type.EmptyTypes); il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, target); il.Emit(OpCodes.Ret); type.DefineMethodOverride(method, original);
        return type.CreateType()!;
    });
    private static MouseDevice ControlledMouse(IInputElement tile) => (MouseDevice)Activator.CreateInstance(MouseType.Value, InputManager.Current, tile)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
