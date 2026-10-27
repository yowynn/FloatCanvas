using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PopupInteractionAcceptanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void RunSession(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar)
    {
        var failures = new List<Exception>();
        string output = Path.GetFullPath(Path.Combine(".artifacts", "acceptance-next", "popups")); Directory.CreateDirectory(output);
        object? Invoke(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, arguments);
        var tool = (Window)typeof(DesktopSession).GetField("_toolOptionsWindow", Private)!.GetValue(session)!;
        NativeRect? firstVisible = null;
        void ObserveSource(object? sender, EventArgs args)
        {
            var native = HwndSource.FromHwnd(new WindowInteropHelper(tool).Handle)!;
            native.AddHook((nint hwnd, int message, nint wparam, nint lparam, ref bool handled) =>
            {
                if(firstVisible is null && message is 0x47 or 0x18 && IsWindowVisible(hwnd) && GetWindowRect(hwnd,out var bounds))firstVisible=bounds;
                return 0;
            });
        }
        if(new WindowInteropHelper(tool).Handle != 0)ObserveSource(null,EventArgs.Empty); else tool.SourceInitialized+=ObserveSource;
        try
        {
            Invoke("ActivateTool",EditorTool.Rectangle);
            if(!tool.IsVisible)Invoke("OpenToolOptions"); Pump();
            GetWindowRect(new WindowInteropHelper(tool).Handle,out var final);
            Console.WriteLine($"FIRST_TOOL_POSITION first={firstVisible} final={final}");
            try { check("the first visible tool HWND already occupies its final anchored position", firstVisible is { } first && Math.Abs(first.Left-final.Left)<=1 && Math.Abs(first.Top-final.Top)<=1 && Math.Abs(first.Right-final.Right)<=1 && Math.Abs(first.Bottom-final.Bottom)<=1); }
            catch(Exception ex){failures.Add(ex);Console.WriteLine(ex);}
            Invoke("CloseToolOptions");
            var editor=(EditorController)typeof(DesktopSession).GetField("_editor",Private)!.GetValue(session)!;
            var options=(ToolOptionsPanel)typeof(DesktopSession).GetField("_toolOptionsPanel",Private)!.GetValue(session)!;
            var font=(FontPickerControl)typeof(ToolOptionsPanel).GetField("fontFamily",Private)!.GetValue(options)!;
            Invoke("ActivateTool",EditorTool.Text);Invoke("OpenToolOptions");Pump();
            font.IsDropDownOpen=true;Pump();
            font.SelectedItem=font.Items.OfType<FontPickerControl.FontChoice>().First(item=>item.Name=="Arial");
            font.IsDropDownOpen=false;WaitForFontSearch();
            check("font selection keeps the real tool options window open after the search delay",tool.IsVisible&&!font.IsDropDownOpen);
            Invoke("CloseToolOptions");
            var editorInput=(TextBox)font.Template.FindName("PART_EditableTextBox",font);
            editorInput.Text="Segoe";
            editor.Tool=EditorTool.Highlighter;Invoke("RefreshToolbarState");WaitForFontSearch();
            check("hidden tool switches cannot open a standalone font popup",font.IsLoaded&&!font.IsVisible&&!tool.IsVisible&&!font.IsDropDownOpen);
            Invoke("ActivateTool",EditorTool.Text);Invoke("OpenToolOptions");WaitForFontSearch();
            check("tool options reopen with current font state and no detached dropdown",tool.IsVisible&&!font.IsDropDownOpen&&
                font.Text==editor.Style.FontFamily&&ToolOptionPreviews.SelectedFontName(font)==editor.Style.FontFamily);
            Invoke("CloseToolOptions");
            var inspector=new InspectorWindow(()=>editor.Session){Owner=toolbar};
            typeof(DesktopSession).GetField("_inspector",Private)!.SetValue(session,inspector);
            Invoke("ConfigureAppWindow",inspector,false,false);
            NativeRect? inspectorFirst=null;
            inspector.SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(inspector).Handle)!.AddHook((nint hwnd,int message,nint wparam,nint lparam,ref bool handled)=>
            {
                if(inspectorFirst is null && message is 0x47 or 0x18 && IsWindowVisible(hwnd) && GetWindowRect(hwnd,out var bounds))inspectorFirst=bounds;
                return 0;
            });
            Invoke("ShowInspectorNear",new PointD(200,100));Pump();GetWindowRect(new WindowInteropHelper(inspector).Handle,out var inspectorFinal);
            Console.WriteLine($"FIRST_INSPECTOR_POSITION first={inspectorFirst} final={inspectorFinal}");
            try { check("the first visible inspector HWND already occupies its pointer anchor", inspectorFirst is { } initial && Math.Abs(initial.Left-inspectorFinal.Left)<=1 && Math.Abs(initial.Top-inspectorFinal.Top)<=1 && Math.Abs(initial.Right-inspectorFinal.Right)<=1 && Math.Abs(initial.Bottom-inspectorFinal.Bottom)<=1); }
            catch(Exception ex){failures.Add(ex);Console.WriteLine(ex);}
            Invoke("ToggleInspector");
            Invoke("ActivateTool",EditorTool.Canvas); Pump();
            Invoke("OpenCanvasSettings");
            var canvasBeforePump=(CanvasSettingsWindow)typeof(DesktopSession).GetField("_canvasSettingsWindow",Private)!.GetValue(session)!;
            Console.WriteLine($"CANVAS_OPEN_SYNC visible={canvasBeforePump.IsVisible} active={canvasBeforePump.IsActive} focus={Keyboard.FocusedElement?.GetType().Name}");
            Pump();
            var canvas=(CanvasSettingsWindow)typeof(DesktopSession).GetField("_canvasSettingsWindow",Private)!.GetValue(session)!;
            Console.WriteLine($"CANVAS_OPEN_PUMP visible={canvas.IsVisible} active={canvas.IsActive} focus={Keyboard.FocusedElement?.GetType().Name}");
            Preview4SettingsRuntimeFixture.Render(canvas,Path.Combine(output,"canvas-settings-actual.png"));
            Invoke("SetCanvasProjection",true); Pump();
            Preview4SettingsRuntimeFixture.Render(canvas,Path.Combine(output,"canvas-perspective-settings-actual.png"));
            foreach(string name in new[]{"background","gridColor"})
            {
                var field=(ColorField)typeof(CanvasSettingsWindow).GetField(name,Private)!.GetValue(canvas)!;
                field.OpenPicker(); Pump();
                var picker=(ColorPickerPanel)typeof(ColorField).GetField("picker",Private)!.GetValue(field)!;
                var sample=Descendants(picker).OfType<Button>().Single(button=>System.Windows.Automation.AutomationProperties.GetName(button)==FloatCanvas.Localization.AppText.Get("Color.Eyedropper"));
                sample.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("canvas "+name+" eyedropper reaches the shared sampling session", ReferenceEquals(typeof(DesktopSession).GetField("_eyedropper",Private)!.GetValue(session),field));
                Invoke("EndEyedropper",true);
                Console.WriteLine($"EYEDROPPER_RESTORE_SYNC field={name} canvas={canvas.IsVisible} picker={field.IsPickerOpen} editing={host.IsEditing}");
                Pump();
                Console.WriteLine($"EYEDROPPER_RESTORE_PUMP field={name} canvas={canvas.IsVisible} picker={field.IsPickerOpen} editing={host.IsEditing}");
                check("canvas "+name+" sampling cancellation restores its canvas and color windows", canvas.IsVisible && field.IsPickerOpen && host.IsEditing);
                field.ClosePicker(); Pump();
            }
        }
        finally { Invoke("EndEyedropper",false); if(tool.IsVisible)Invoke("CloseToolOptions"); }
        if(failures.Count!=0)throw new AggregateException(failures);

        static void WaitForFontSearch()
        {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(350)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);Pump();
        }
    }

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var failures = new List<Exception>();
        foreach (var test in new Action<Action<string, bool>>[] { DetachedHintLifecycle, ForegroundHookLifecycle, HoverLifecycle, PassiveHintEscape, HoverFocusRetention, CanvasMenuCloseFocusRetention, StampHoverLifecycle, NestedOutsidePress, OrdinaryMenuCapture })
        {
            try { test(check); }
            catch (Exception ex) { failures.Add(ex); Console.WriteLine(ex); }
        }
        SynchronizationContext.SetSynchronizationContext(context);
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static ToolTipEventArgs OpenHint(FrameworkElement target)
    {
        var args=(ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[true],null)!;
        target.RaiseEvent(args);return args;
    }

    private static void DetachedHintLifecycle(Action<string,bool> check)
    {
        var root=new StackPanel();var active=new Button {ToolTip=new ToolTip {Content="active"}};
        var stale=new Button {ToolTip=new ToolTip {Content="stale"}};root.Children.Add(active);root.Children.Add(stale);
        using var source=Source(root);using var scope=new OverlayWindowScope(source.Handle,()=>false);Pump();
        try
        {
            OpenHint(active);var activeTip=(ToolTip)active.ToolTip;
            root.Children.Remove(stale);Pump();
            var staleOpening=OpenHint(stale);
            check("a delayed hint for a detached trigger is cancelled without a screen-coordinate exception",staleOpening.Handled&&!OverlayTransientControls.IsHintOpen((ToolTip)stale.ToolTip));
            check("a stale hint request does not close another valid hint",OverlayTransientControls.IsHintOpen(activeTip));
            scope.ClosePopups();

            var next=new Button {ToolTip=new ToolTip {Content="next"}};root.Children.Add(next);Pump();
            int nextOpened=0,nextClosed=0;
            ((ToolTip)next.ToolTip).Opened+=(_,_)=>nextOpened++;((ToolTip)next.ToolTip).Closed+=(_,_)=>nextClosed++;
            activeTip.Closed+=(_,_)=>root.Children.Remove(next);
            OpenHint(active);OpenHint(next);
            check("closing an old hint rechecks the new trigger after synchronous Closed callbacks",nextOpened==0&&nextClosed==0&&!OverlayTransientControls.IsHintOpen((ToolTip)next.ToolTip));

            var measured=new Button();int measuredOpened=0,measuredClosed=0;
            var measuredTip=new DetachingToolTip {Content="measure",DuringMeasure=()=>root.Children.Remove(measured)};
            measuredTip.Opened+=(_,_)=>measuredOpened++;measuredTip.Closed+=(_,_)=>measuredClosed++;
            measured.ToolTip=measuredTip;root.Children.Add(measured);Pump();OpenHint(measured);
            check("hint layout detaching its anchor cancels before coordinate conversion or lifecycle notifications",measuredOpened==0&&measuredClosed==0&&!OverlayTransientControls.IsHintOpen(measuredTip));

            var removed=new Button {ToolTip=new ToolTip {Content="removed"}};root.Children.Add(removed);Pump();
            var removedTip=(ToolTip)removed.ToolTip;int removedClosed=0;removedTip.Closed+=(_,_)=>removedClosed++;
            OpenHint(removed);root.Children.Remove(removed);Pump();
            check("unloading an open hint trigger closes its hosted state once",removedClosed==1&&!OverlayTransientControls.IsHintOpen(removedTip));
            scope.ClosePopups();check("closing the scope after trigger removal does not duplicate Closed",removedClosed==1);

            var hidden=new Button {ToolTip=new ToolTip {Content="hidden"}};root.Children.Add(hidden);Pump();
            var hiddenTip=(ToolTip)hidden.ToolTip;int hiddenClosed=0;hiddenTip.Closed+=(_,_)=>hiddenClosed++;
            OpenHint(hidden);hidden.Visibility=Visibility.Collapsed;Pump();
            check("hiding an open hint trigger closes the hint even if it remains connected",hiddenClosed==1&&!OverlayTransientControls.IsHintOpen(hiddenTip));
        }
        finally {scope.ClosePopups();}
    }

    private sealed class DetachingToolTip:ToolTip
    {
        internal Action? DuringMeasure;
        protected override Size MeasureOverride(Size constraint)
        {
            var action=DuringMeasure;DuringMeasure=null;action?.Invoke();return base.MeasureOverride(constraint);
        }
    }

    private static void ForegroundHookLifecycle(Action<string,bool> check)
    {
        var menu=new OverlayContextMenu();
        nint Hook()=>(nint)typeof(OverlayContextMenu).GetField("foregroundHook",Private)!.GetValue(menu)!;
        try {menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));check("refreshing a closed menu does not install a native foreground listener",Hook()==0);}
        finally {menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));}
        var target=new Button {Content="menu",Width=100,Height=30};
        using var source=Source(target);menu.PlacementTarget=target;menu.Items.Add(new MenuItem {Header="command"});
        try
        {
            menu.IsOpen=true;Pump();nint hook=Hook();
            check("a real menu opening installs one foreground listener",hook!=0);
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            check("refreshing an open menu retains its single foreground listener",Hook()==hook);
        }
        finally {menu.IsOpen=false;}
        check("closing a refreshed menu releases its foreground listener",Hook()==0);
    }

    private static void StampHoverLifecycle(Action<string, bool> check)
    {
        string output = Path.GetFullPath(Path.Combine(".artifacts", "acceptance-next", "popups"));
        Directory.CreateDirectory(output);
        var library = new StampLibrary(Path.Combine(output, "hover-profile-" + Guid.NewGuid().ToString("N")));
        var entry = Task.Run(async () =>
        {
            await library.LoadAsync();
            var session = new EditorSession(new SceneDocument { Elements = [new CanvasElement { Kind = ElementKind.Rectangle, Width = 120, Height = 70, Style = new ElementStyle { Fill = 0xFFE53935, StrokeWidth = 0 } }] });
            session.SelectAll();
            return await library.SaveSelectionAsync(session, "Hosted hover specimen");
        }).GetAwaiter().GetResult();
        var trigger = new Button { Content = "Stamp", Width = 150, Height = 32 };
        using var source = Source(trigger);
        using var scope = new OverlayWindowScope(source.Handle, () => false);
        var tooltip = StampPreviewTooltip.Create(library, entry, "1"); trigger.ToolTip = tooltip;
        try
        {
            var args = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [true], null)!;
            trigger.RaiseEvent(args);
            var image = (Image)typeof(StampPreviewTooltip).GetField("preview", Private)!.GetValue(tooltip)!;
            PumpUntil(() => image.Source is not null, TimeSpan.FromSeconds(8));
            var message = (TextBlock)typeof(StampPreviewTooltip).GetField("message", Private)!.GetValue(tooltip)!;
            check("actual enhanced stamp hover completes artwork loading", OverlayTransientControls.IsHintOpen(tooltip) && image.Source is System.Windows.Media.Imaging.BitmapSource && message.Visibility == Visibility.Collapsed);
            Render(tooltip, Path.Combine(output, "stamp-hover-enhanced.png"));
            scope.ClosePopups();
            check("closing enhanced stamp hover cancels playback and its hosted state", !OverlayTransientControls.IsHintOpen(tooltip) && typeof(StampPreviewTooltip).GetField("playback", Private)!.GetValue(tooltip) is null);
        }
        finally { scope.ClosePopups(); }
    }

    private static void HoverLifecycle(Action<string, bool> check)
    {
        var trigger = new Button { Content = "Tooltip source", Width = 150, Height = 32 };
        using var source = Source(trigger);
        using var scope = new OverlayWindowScope(source.Handle, () => false);
        var tooltip = new ToolTip { Content = "Hosted tooltip" };
        int opened = 0, closed = 0;
        tooltip.Opened += (_, _) => opened++;
        tooltip.Closed += (_, _) => closed++;
        trigger.ToolTip = tooltip;
        try
        {
            var args = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [true], null)!;
            trigger.RaiseEvent(args);
            check("enhanced hover preserves the actual tooltip Opened lifecycle", opened == 1);
            scope.ClosePopups();
            check("closing the enhanced hint preserves the actual tooltip Closed lifecycle", closed == 1);
            scope.ClosePopups();
            check("a hosted tooltip closes exactly once", closed == 1);
        }
        finally { scope.ClosePopups(); }
    }

    private static void PassiveHintEscape(Action<string, bool> check)
    {
        var trigger = new Button { Content = "Canvas command surface", Width = 180, Height = 32 };
        using var source = Source(trigger);
        using var scope = new OverlayWindowScope(source.Handle, () => true);
        var tooltip = new ToolTip { Content = "Passive hover hint" };
        trigger.ToolTip = tooltip;
        int opened = 0, outerEscapes = 0;
        bool observed = false;
        tooltip.Opened += (_, _) => opened++;
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = trigger };
        ProcessInputEventHandler afterInput = (_, args) =>
        {
            if (!ReferenceEquals(args.StagingItem.Input, escape)) return;
            observed = true;
            if (!escape.Handled) outerEscapes++;
        };
        try
        {
            OpenHint(trigger);
            check("passive Escape fixture opens the real hosted tooltip lifecycle", opened == 1 && OverlayTransientControls.IsHintOpen(tooltip));
            InputManager.Current.PostProcessInput += afterInput;
            InputManager.Current.ProcessInput(escape);
            check("passive hint Escape travels through the WPF input pipeline", observed);
            Console.WriteLine($"PASSIVE_HINT_ESCAPE handled={escape.Handled} outer={outerEscapes} hintOpen={OverlayTransientControls.IsHintOpen(tooltip)}");
            check("a passive hover hint does not consume the canvas's first Escape", !escape.Handled && outerEscapes == 1);
            check("the same Escape dismisses its passive hint", !OverlayTransientControls.IsHintOpen(tooltip));

            var popup = new OverlayPopup { PlacementTarget = trigger, Child = new Button { Content = "Interactive popup" } };
            popup.IsOpen = true; OpenHint(trigger);
            check("a passive hint can coexist above a real interactive popup", popup.IsOpen && OverlayTransientControls.IsHintOpen(tooltip));
            observed = false; outerEscapes = 0;
            escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = trigger };
            InputManager.Current.ProcessInput(escape);
            check("a passive hint does not mask the real popup's Escape dismissal", observed && escape.Handled && !popup.IsOpen && outerEscapes == 0);
        }
        finally
        {
            InputManager.Current.PostProcessInput -= afterInput;
            scope.ClosePopups();
        }
    }

    private static void HoverFocusRetention(Action<string, bool> check)
    {
        var root=new Grid();
        var editor=new TextBox {Text="Existing edit",Width=160,Height=32,HorizontalAlignment=HorizontalAlignment.Left};
        var trigger=new Button {Content="Hover only",Width=150,Height=32,HorizontalAlignment=HorizontalAlignment.Right};
        root.Children.Add(editor);root.Children.Add(trigger);
        nint foreground=GetForegroundWindow();
        using var source=Source(root);
        // Show only this fixture's offscreen HWND without activating the user's
        // foreground app. WPF focus can then be exercised on real sibling controls.
        ShowWindow(source.Handle,4);
        using var scope=new OverlayWindowScope(source.Handle,()=>true);
        var tooltip=new ToolTip {Content="Does not own input"};trigger.ToolTip=tooltip;
        try
        {
            Keyboard.Focus(editor);
            check("offscreen hover fixture starts with its sibling textbox focused",ReferenceEquals(Keyboard.FocusedElement,editor));
            foreach(bool leave in new[]{true,false})
            {
                var opening=(ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[true],null)!;
                trigger.RaiseEvent(opening);
                check("opening a hosted tooltip preserves existing sibling text input",ReferenceEquals(Keyboard.FocusedElement,editor));
                if(leave)trigger.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount) {RoutedEvent=Mouse.MouseLeaveEvent});
                else scope.ClosePopups();
                Pump();
                check((leave?"leaving":"closing")+" a hosted tooltip preserves existing sibling text input",ReferenceEquals(Keyboard.FocusedElement,editor));
            }
            check("offscreen hover focus fixture leaves the user's foreground window unchanged",GetForegroundWindow()==foreground);
        }
        finally {scope.ClosePopups();Keyboard.ClearFocus();}
    }

    private static void CanvasMenuCloseFocusRetention(Action<string,bool> check)
    {
        var canvas=new TextBox {Text="Canvas input surface"};
        var trigger=new Button {Content="Toolbar menu"};
        nint foreground=GetForegroundWindow();
        using var canvasSource=Source(canvas);using var toolbarSource=Source(trigger);
        ShowWindow(canvasSource.Handle,4);ShowWindow(toolbarSource.Handle,4);
        using var scope=new OverlayWindowScope(canvasSource.Handle,()=>true);
        var menu=new OverlayContextMenu {PlacementTarget=trigger};menu.Items.Add(new MenuItem {Header="Command"});
        try
        {
            menu.IsOpen=true;
            Keyboard.Focus(canvas);
            check("native canvas close fixture starts with input on its canvas owner",ReferenceEquals(Keyboard.FocusedElement,canvas));
            // Native canvas input reaches the DesktopSession dismissal entry point
            // directly, without a WPF BeforeInput outside-press notification.
            OverlayContextMenu.CloseAll();Pump();
            check("native canvas menu dismissal preserves input on the canvas owner",!menu.IsOpen&&ReferenceEquals(Keyboard.FocusedElement,canvas));
            check("native canvas close fixture leaves the user's foreground window unchanged",GetForegroundWindow()==foreground);
        }
        finally {menu.IsOpen=false;Keyboard.ClearFocus();}
    }

    private static void NestedOutsidePress(Action<string, bool> check)
    {
        var owner = new Grid { Width = 600, Height = 300 };
        var trigger = new Button { Content = "Color", Width = 100, Height = 32, HorizontalAlignment = HorizontalAlignment.Left };
        var outside = new Button { Content = "Parent", Width = 100, Height = 32, HorizontalAlignment = HorizontalAlignment.Right };
        owner.Children.Add(trigger); owner.Children.Add(outside);
        using var source = Source(owner);
        using var scope = new OverlayWindowScope(source.Handle, () => false);
        var swatch = new Button { Content = "My swatch", Width = 160, Height = 40 };
        var content = new Border { Child = swatch, Background = Brushes.White };
        var panel = new OverlayPopup { Child = content, PlacementTarget = trigger, StaysOpen = false };
        PopupChildDialog.Attach(content, panel);
        var menu = new OverlayContextMenu { PlacementTarget = swatch };
        menu.Items.Add(new MenuItem { Header = "Rename" });
        int parentPresses = 0;
        outside.PreviewMouseDown += (_, _) => parentPresses++;
        try
        {
            panel.IsOpen = true; menu.IsOpen = true;
            check("nested menu fixture opens both popup levels", panel.IsOpen && menu.IsOpen);
            Press(outside);
            check("one parent press closes the menu and its unfocused color popup", !menu.IsOpen && !panel.IsOpen);
            check("outside dismissal preserves the same parent press", parentPresses == 1);
        }
        finally { menu.IsOpen = false; panel.IsOpen = false; Pump(); }
    }

    private static void OrdinaryMenuCapture(Action<string, bool> check)
    {
        var trigger = new Button { Content = "Menu", Width = 100, Height = 32 };
        using var source = Source(trigger);
        var menu = new OverlayContextMenu { PlacementTarget = trigger };
        var group = new MenuItem { Header = "Group" };
        group.Items.Add(new MenuItem { Header = "Command" }); menu.Items.Add(group);
        int presses = 0; trigger.PreviewMouseDown += (_, _) => presses++;
        try
        {
            menu.IsOpen = true; Pump();
            check("ordinary popup menus do not create a mouse-capture dismissal backdrop", menu.IsOpen && Mouse.Captured is null);
            group.IsSubmenuOpen = true; Pump();
            check("ordinary submenus stay available without capturing the next external press", menu.IsOpen && group.IsSubmenuOpen && Mouse.Captured is null);
            Press(trigger); Pump();
            check("one external press closes all ordinary menu levels and reaches its target", !menu.IsOpen && !group.IsSubmenuOpen && presses == 1);
        }
        finally { menu.IsOpen = false; Pump(); }
    }

    private static HwndSource Source(FrameworkElement root)
    {
        var source = new HwndSource(new HwndSourceParameters("FloatCanvas.PopupAcceptance")
        { PositionX = -10000, PositionY = -10000, Width = 600, Height = 300, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = root;
        root.Measure(new Size(600, 300)); root.Arrange(new Rect(0, 0, 600, 300)); root.UpdateLayout();
        return source;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }

    private static void Press(UIElement element) => typeof(ToolOptionsInputFixture).GetMethod("Drive", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [element]);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> complete, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while(!complete())
        {
            if(DateTime.UtcNow >= deadline) throw new TimeoutException("Hosted stamp hover did not finish loading.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
    }
    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight*dpi.DpiScaleY), 96*dpi.DpiScaleX, 96*dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativeRect
    {
        public readonly int Left,Top,Right,Bottom;
        public override string ToString()=>$"{Left},{Top},{Right},{Bottom}";
    }
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle,int command);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle,out NativeRect rectangle);
}
