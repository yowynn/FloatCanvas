using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Real WPF popup content in an explicitly owned system-band WPF window.
/// No default Popup HWND is created, moved between bands or captured as pixels.</summary>
[ContentProperty(nameof(Child))]
public sealed class OverlayPopup : FrameworkElement
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen),typeof(bool),typeof(OverlayPopup),
        new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,(d,e)=>((OverlayPopup)d).UpdateOpen()));
    public static readonly DependencyProperty ChildProperty = DependencyProperty.Register(nameof(Child),typeof(UIElement),typeof(OverlayPopup),
        new PropertyMetadata(null,(d,e)=> {var popup=(OverlayPopup)d; if(e.OldValue is UIElement old) popup.RemoveLogicalChild(old); if(e.NewValue is UIElement next) popup.AddLogicalChild(next);}));
    public static readonly DependencyProperty PlacementTargetProperty=DependencyProperty.Register(nameof(PlacementTarget),typeof(UIElement),typeof(OverlayPopup));
    public bool IsOpen { get=>(bool)GetValue(IsOpenProperty); set=>SetValue(IsOpenProperty,value); }
    public UIElement? Child { get=>(UIElement?)GetValue(ChildProperty); set=>SetValue(ChildProperty,value); }
    public UIElement? PlacementTarget { get=>(UIElement?)GetValue(PlacementTargetProperty); set=>SetValue(PlacementTargetProperty,value); }
    public PlacementMode Placement { get; set; } = PlacementMode.Bottom;
    public double HorizontalOffset { get; set; }
    public double VerticalOffset { get; set; }
    public bool StaysOpen { get; set; } = true;
    internal bool TargetPressToggles { get; set; }
    public bool AllowsTransparency { get; set; }
    public PopupAnimation PopupAnimation { get; set; }
    public CustomPopupPlacementCallback? CustomPopupPlacementCallback { get; set; }
    public event EventHandler? Opened,Closed;
    private OverlayPopupWindow? window;
    internal void Close(bool returnFocus = true)
    {
        if (window is { } current) current.Close(returnFocus);
        else SetCurrentValue(IsOpenProperty, false);
    }
    protected override IEnumerator LogicalChildren => Child is null?Array.Empty<object>().GetEnumerator():new object[]{Child}.GetEnumerator();
    protected override Size MeasureOverride(Size availableSize)=>default;
    protected override DependencyObject GetUIParentCore()=>base.GetUIParentCore()??PlacementTarget!;
    private void UpdateOpen()
    {
        if(IsOpen && window is null && Child is { } child)
        {
            var target=PlacementTarget??TemplatedParent as UIElement;
            window=new OverlayPopupWindow(child,target,Placement,HorizontalOffset,VerticalOffset,()=>SetCurrentValue(IsOpenProperty,false),CustomPopupPlacementCallback,()=>!PopupChildDialog.StaysOpenForOutsidePress(this,StaysOpen),IsHitTestVisible,TargetPressToggles);
            window.Show(); Opened?.Invoke(this,EventArgs.Empty);
        }
        else if(!IsOpen && window is { } old)
        { window=null; old.Dispose(); Closed?.Invoke(this,EventArgs.Empty); }
    }
}

/// <summary>Thread-scoped ownership for all interactive overlay windows and popups.</summary>
internal sealed class OverlayWindowScope : IDisposable
{
    private static readonly DependencyProperty RegularWindowProperty = DependencyProperty.RegisterAttached("RegularWindow", typeof(bool), typeof(OverlayWindowScope), new PropertyMetadata(false));
    internal static void UseRegularWindow(Window window)
    {
        window.SetValue(RegularWindowProperty, true);
        if(window.Owner is { } owner && !IsRegularWindow(owner) && SessionWindowCoordinator.Find(owner) is not null) window.Owner=null;
        window.Topmost=false; window.ShowActivated=true; WindowTitles.Apply(window); AppTheme.UseRegularControls(window);
    }
    internal static bool IsRegularWindow(Window window) => (bool)window.GetValue(RegularWindowProperty);
    private static readonly DependencyProperty PreparedProperty=DependencyProperty.RegisterAttached("Prepared",typeof(bool),typeof(OverlayWindowScope),new PropertyMetadata(false));
    [ThreadStatic] internal static OverlayWindowScope? Current;
    internal nint Owner { get; }
    private readonly Func<bool> inputActive;
    internal bool IsInputActive=>inputActive();
    private readonly List<OverlayPopupWindow> windows=[];
    internal OverlayWindowScope(nint owner,Func<bool>? inputActive=null,bool installAsCurrent=true)
    {
        if(installAsCurrent && Current is not null) throw new InvalidOperationException("An overlay session already owns this thread.");
        Owner=owner;this.inputActive=inputActive??(()=>true);
        if(installAsCurrent){Current=this;OverlayTransientControls.Initialize();}
    }
    internal static void Prepare(Window window)
    {
        WindowFocusLifecycle.Attach(window);
        AppInputPolicy.AttachKeyboardSource(window);
        if (window.Owner is { } owner && IsRegularWindow(owner)) UseRegularWindow(window);
        if (IsRegularWindow(window)) { UseRegularWindow(window); return; }
        if(Current is not { } scope) return;
        window.ShowActivated=!scope.IsInputActive;
        var interop=new WindowInteropHelper(window);
        if(interop.Handle!=0)SetNativePolicy(interop.Handle,scope.IsInputActive);
        if((bool)window.GetValue(PreparedProperty)) return;
        window.SetValue(PreparedProperty,true);
        InputMethod.SetIsInputMethodEnabled(window,false);
        window.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent,new KeyboardFocusChangedEventHandler(ConfigureInputMethod),true);
        window.PreviewMouseDown+=(_,e)=>
        {
            if(!scope.IsInputActive || e.OriginalSource is not DependencyObject source || EditorShortcutPolicy.OwnsLocalEditingKeys(source))return;
            for(DependencyObject? current=source;current is not null && !ReferenceEquals(current,window);
                current=current is Visual?VisualTreeHelper.GetParent(current)??LogicalTreeHelper.GetParent(current):LogicalTreeHelper.GetParent(current))
                if(current is ButtonBase or MenuBase or MenuItem || current is Control {Focusable:true} and not ScrollViewer)return;
            // Clicking noninteractive chrome/space ends local editing. Clearing
            // keyboard focus alone would leave TSF's previous text document active.
            FocusSurface(window);
        };
        if(interop.Handle!=0) return;
        // Use the same per-pixel WPF presentation as the toolbar and menus. An
        // opaque redirected top-level surface can displace a DXGI exclusive owner.
        window.WindowStyle=WindowStyle.None;
        window.AllowsTransparency=true;
        if(window.Owner is null) interop.Owner=scope.Owner;
        window.SourceInitialized+=(_,_)=>
        {
            // WM_MOUSEACTIVATE only covers clicks. The native style also prevents
            // implicit activation while WPF shows or focuses an auxiliary window.
            SetNativePolicy(interop.Handle,scope.IsInputActive);
            HwndSource.FromHwnd(interop.Handle)?.AddHook(
            (nint hwnd,int message,nint wparam,nint lparam,ref bool handled)=>
            {
                if(message==0xA1 && (wparam==2 || wparam is >=10 and <=17) && scope.IsInputActive)FocusSurface(window);
                if(message==0xA1 && (wparam==2 || wparam is >=10 and <=17) && OverlayWindowDrag.TryStart(window,resizeEdge:(int)wparam)){handled=true;return 0;}
                return NoActivate(hwnd,message,wparam,lparam,ref handled);
            });
        };
    }
    private static nint NoActivate(nint hwnd,int message,nint wparam,nint lparam,ref bool handled)
    {if(message==0x21 && Current?.IsInputActive==true){handled=true;return 3;}return 0;}
    internal static void ConfigureInputMethod(object sender,KeyboardFocusChangedEventArgs args)
    {
        // IsInputMethodEnabled does not inherit. A ScrollViewer may take focus
        // after PreviewMouseDown, so apply the policy to the final focus target.
        // Actual editors retain their own policy (including numeric IME opt-out).
        if(args.NewFocus is DependencyObject target and not TextBoxBase and not PasswordBox)
            InputMethod.SetIsInputMethodEnabled(target,false);
    }
    private static void FocusSurface(Window window)
    {
        // Window.Focus restores its last focused child. Use the existing outer
        // template surface as the non-text command target instead.
        if(VisualTreeHelper.GetChildrenCount(window)==0 || VisualTreeHelper.GetChild(window,0) is not UIElement surface)return;
        surface.Focusable=true;
        InputMethod.SetIsInputMethodEnabled(surface,false);
        Keyboard.Focus(surface);
    }
    internal void Add(OverlayPopupWindow window)=>windows.Add(window);
    internal void Remove(OverlayPopupWindow window)=>windows.Remove(window);
    internal bool IsTopmost(OverlayPopupWindow window)=>windows.LastOrDefault(candidate=>candidate.IsInteractive)==window;
    internal void ClosePopups(){foreach(var window in windows.ToArray()) window.Close();}
    public void Dispose()
    {ClosePopups();if(Current==this) Current=null;}
    private static void SetNativePolicy(nint window,bool editing)
    {
        nint style=GetWindowLongPtrW(window,-20);
        SetWindowLongPtrW(window,-20,editing?style|0x08000000:style&~0x08000000);
        SetPropW(window,"NonRudeHWND",1);
    }
    [DllImport("user32.dll")]private static extern nint GetWindowLongPtrW(nint window,int index);
    [DllImport("user32.dll")]private static extern nint SetWindowLongPtrW(nint window,int index,nint value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool SetPropW(nint window,string name,nint value);
}

internal sealed class OverlayPopupWindow : IDisposable
{
    [ThreadStatic] private static List<OverlayPopupWindow>? openWindows;
    internal static Size MeasurementLimit => new(SystemParameters.PrimaryScreenWidth*.75,SystemParameters.PrimaryScreenHeight*.75);
    private readonly HwndSource source;
    private readonly UIElement content;
    private readonly Action close;
    private readonly UIElement? target;
    private readonly OverlayWindowScope scope;
    private readonly Func<bool> closeOnOutside;
    private readonly bool targetPressToggles;
    internal bool IsInteractive { get; }
    private bool returnFocus=true;
    private bool disposed;
    internal OverlayPopupWindow(UIElement child,UIElement? target,PlacementMode placement,double offsetX,double offsetY,Action close,CustomPopupPlacementCallback? custom=null,Func<bool>? closeOnOutside=null,bool hitTestVisible=true,bool targetPressToggles=false,OverlayWindowScope? ownerScope=null,bool returnFocus=true,Size? measuredSize=null)
    {
        scope=ownerScope??OverlayWindowScope.Current??throw new InvalidOperationException("No active system overlay window owner.");
        this.close=close;this.target=target;
        this.returnFocus=returnFocus;
        this.targetPressToggles=targetPressToggles;
        IsInteractive=hitTestVisible;
        this.closeOnOutside=closeOnOutside??(()=>true);
        content=child;
        if (child is FrameworkElement root) AppTheme.UsePopupPalette(root);
        // A caller handling delayed hints can measure first, validate its anchor
        // after layout callbacks, then avoid measuring again during creation.
        if(measuredSize is null)child.Measure(MeasurementLimit);
        var size=measuredSize??child.DesiredSize;
        Point position=target?.PointToScreen(new Point())??CursorPosition();
        Point monitorAnchor=target is FrameworkElement monitorTarget
            ? monitorTarget.PointToScreen(new(monitorTarget.ActualWidth/2,monitorTarget.ActualHeight/2)) : position;
        var dpi=target is Visual visual?VisualTreeHelper.GetDpi(visual):new DpiScale(1,1);
        double scaleX=dpi.DpiScaleX,scaleY=dpi.DpiScaleY;
        if(placement==PlacementMode.Bottom && target is FrameworkElement element) position.Y+=element.ActualHeight*scaleY;
        if(placement==PlacementMode.Right && target is FrameworkElement right) position.X+=right.ActualWidth*scaleX;
        if(placement==PlacementMode.MousePoint) position=CursorPosition();
        Point[] candidates=[position];
        if(placement==PlacementMode.Custom && custom is not null && target is FrameworkElement anchor)
        {
            var choices=custom(size,anchor.RenderSize,new Point(offsetX,offsetY));
            if(choices.Length!=0) candidates=choices.Select(choice=>anchor.PointToScreen(choice.Point)).ToArray();
        }
        if(placement==PlacementMode.MousePoint) monitorAnchor=position;
        var area=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)monitorAnchor.X,(int)monitorAnchor.Y)).WorkingArea;
        int width=Math.Max(1,(int)Math.Ceiling(size.Width*scaleX)),height=Math.Max(1,(int)Math.Ceiling(size.Height*scaleY));
        position=OverlayPopupPlacement.Place(candidates,new Size(width,height),new Rect(area.Left,area.Top,area.Width,area.Height),new Vector(offsetX*scaleX,offsetY*scaleY));
        source=new HwndSource(new HwndSourceParameters("FloatCanvas.Popup")
        {
            ParentWindow=scope.Owner,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000088|(hitTestVisible?0:0x20),
            UsesPerPixelOpacity=true,PositionX=(int)Math.Round(position.X),PositionY=(int)Math.Round(position.Y),
            Width=width,Height=height
        });
        source.CompositionTarget.BackgroundColor=Colors.Transparent;
        source.RootVisual=child;
        child.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent,new KeyboardFocusChangedEventHandler(OverlayWindowScope.ConfigureInputMethod),true);
        source.SizeToContent=SizeToContent.WidthAndHeight;
        source.AddHook(NoActivate);
        AppInputPolicy.AttachKeyboardSource(source);
        if(!hitTestVisible)source.AddHook((nint hwnd,int message,nint wparam,nint lparam,ref bool handled)=>
            {if(message==0x84){handled=true;return -1;}return 0;});
        scope.Add(this);
        (openWindows??=[]).Add(this);
        InputManager.Current.PreProcessInput+=BeforeInput;
    }
    internal void Show(){ShowWindow(source.Handle,4);RaiseOpenPopups();}
    internal static void RaiseOpenPopups()
    {
        // Popup HWNDs are not Application.Windows. Keep their creation order
        // above the first-level panels without promoting their toolbar owner.
        foreach(var popup in openWindows?.ToArray()??[])
            if(!popup.disposed && IsWindowVisible(popup.source.Handle))
            {
                RaiseWindow(popup.source.Handle);
                if(popup.content is OverlayContextMenu menu)menu.RaiseNativeSubmenus();
            }
    }
    internal static void RaiseWindow(nint handle)
    {
        if(IsWindowVisible(handle))OverlayCoordinates.RaiseToolWindow(handle);
    }
    internal void Focus(UIElement element)
    {SetActiveWindow(source.Handle);SetFocus(source.Handle);element.Focus();Keyboard.Focus(element);}
    private void BeforeInput(object sender,PreProcessInputEventArgs args)
    {
        if(!IsInteractive)
        {
            // Hover hints disappear with the gesture but never consume it or
            // hide an interactive popup from the Escape dismissal order.
            if(args.StagingItem.Input is KeyEventArgs {Key:Key.Escape,RoutedEvent:var hintKey} && hintKey==Keyboard.PreviewKeyDownEvent ||
                args.StagingItem.Input is MouseButtonEventArgs {ButtonState:MouseButtonState.Pressed})close();
            return;
        }
        // At PreProcessInput WPF has not assigned Source yet. Use the same
        // device target it will use when routing the pending mouse event.
        if(closeOnOutside() && args.StagingItem.Input is MouseButtonEventArgs mouse && mouse.ButtonState==MouseButtonState.Pressed &&
            (mouse.OriginalSource as DependencyObject??mouse.MouseDevice.Target as DependencyObject??Mouse.Captured as DependencyObject) is { } origin &&
            !BelongsTo(origin,content) && (!targetPressToggles || target is null || !BelongsTo(origin,target)))
        {returnFocus=false;close();}
        if(scope.IsTopmost(this) && !PopupChildDialog.HasRetainedDialog(content) &&
            args.StagingItem.Input is KeyEventArgs {Handled:false,RoutedEvent:var routed,Key:Key.Escape} key && routed==Keyboard.PreviewKeyDownEvent)
        {
            var keyOrigin = key.OriginalSource as DependencyObject ?? key.KeyboardDevice.Target as DependencyObject ?? Mouse.Captured as DependencyObject;
            if (keyOrigin is null || !BelongsTo(keyOrigin, content) || !NumericValueField.TryCancelForInput(keyOrigin)) close();
            key.Handled=true;
        }
    }
    internal static bool BelongsTo(DependencyObject source,DependencyObject root)
    {
        for(var current=source;current is not null;current=current switch
            {ContextMenu menu=>menu.PlacementTarget,OverlayPopup popup=>popup.PlacementTarget??popup.TemplatedParent,
                _=>LogicalTreeHelper.GetParent(current)??(current is Visual?VisualTreeHelper.GetParent(current):null)})
            if(ReferenceEquals(current,root)) return true;
        return false;
    }
    internal void Close(bool returnFocus=true){this.returnFocus&=returnFocus;close();}
    public void Dispose()
    {
        if(disposed) return;disposed=true;
        openWindows?.Remove(this);
        InputManager.Current.PreProcessInput-=BeforeInput;
        scope.Remove(this);content.RemoveHandler(Keyboard.PreviewGotKeyboardFocusEvent,new KeyboardFocusChangedEventHandler(OverlayWindowScope.ConfigureInputMethod));
        source.RootVisual=null;source.Dispose();
        if(returnFocus && target is not null && PresentationSource.FromVisual(target) is HwndSource targetSource)
        {
            target.Dispatcher.BeginInvoke(DispatcherPriority.Input,()=>
            {
                if(OverlayWindowScope.Current!=scope || !scope.IsInputActive || !target.IsVisible || targetSource.IsDisposed) return;
                nint focused=GetFocus();
                if(focused!=0 && focused!=targetSource.Handle && focused!=scope.Owner) return;
                // Destroying an owned menu can restore the canvas HWND before
                // its source control. That automatic fallback is not a new destination.
                // Preserve an explicitly selected control in any other HWND.
                if(Keyboard.FocusedElement is DependencyObject focusedElement &&
                    !BelongsTo(focusedElement,target) && !BelongsTo(focusedElement,content) &&
                    !(focused==scope.Owner && focusedElement is Visual ownerVisual &&
                        (PresentationSource.FromVisual(ownerVisual) as HwndSource)?.Handle==scope.Owner)) return;
                SetActiveWindow(GetAncestor(targetSource.Handle,2));SetFocus(targetSource.Handle);target.Focus();Keyboard.Focus(target);
            });
        }
    }
    private static nint NoActivate(nint hwnd,int message,nint wparam,nint lparam,ref bool handled)
    {if(message==0x21){handled=true;return 3;}return 0;}
    private static Point CursorPosition(){GetCursorPos(out var point);return new(point.X,point.Y);}
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint{public int X,Y;}
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
}

internal sealed class OverlayContextMenu : ContextMenu
{
    internal void RaiseNativeSubmenus()
    {
        void Raise(MenuItem item)
        {
            if(!item.IsSubmenuOpen)return;
            if(item.Template?.FindName("PART_Popup",item) is Popup {IsOpen:true,Child:{ } child} && PresentationSource.FromVisual(child) is HwndSource source)
                OverlayPopupWindow.RaiseWindow(source.Handle);
            foreach(var nested in item.Items.OfType<MenuItem>())Raise(nested);
        }
        foreach(var item in Items.OfType<MenuItem>())Raise(item);
    }
    [ThreadStatic] private static HashSet<OverlayContextMenu>? openMenus;
    internal static bool OwnsKeyboardFocus=>openMenus?.Any(menu=>menu.IsKeyboardFocusWithin)==true;
    internal static void CloseAll(){foreach(var menu in openMenus?.ToArray()??[])menu.window?.Close(returnFocus:false);}
    internal bool TargetPressToggles { get; set; }
    private OverlayPopupWindow? window;
    private OverlayWindowScope? ordinaryScope;
    private bool ignoreOpeningRightRelease;
    private Window? sourceWindow;
    private IDisposable? parentRetention;
    private WinEventProcedure? foregroundProcedure;
    private nint foregroundHook;
    static OverlayContextMenu()
    {
        OverlayTransientControls.Initialize();
        EventManager.RegisterClassHandler(typeof(FrameworkElement),ContextMenuService.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((sender,e)=>
            {
                if(e.Handled || sender is not FrameworkElement root ||
                    !ReferenceEquals(PresentationSource.FromVisual(root)?.RootVisual,root))return;
                for(DependencyObject? source=e.OriginalSource as DependencyObject;source is not null;
                    source=LogicalTreeHelper.GetParent(source)??(source is Visual?VisualTreeHelper.GetParent(source):null))
                    if(source is FrameworkElement {ContextMenu:OverlayContextMenu menu} element)
                    {e.Handled=true;menu.PlacementTarget=element;menu.IsOpen=true;return;}
            }));
        EventManager.RegisterClassHandler(typeof(MenuItem),MenuItem.ClickEvent,new RoutedEventHandler((sender,e)=>
        {
            if(!ReferenceEquals(sender,e.OriginalSource) || sender is not MenuItem {HasItems:false,StaysOpenOnClick:false} item)return;
            // Close the hosted root before the command can enter a modal loop.
            // A handler on the root only runs after the item's Click handler returns.
            for(ItemsControl? owner=ItemsControl.ItemsControlFromItemContainer(item);owner is not null;owner=ItemsControl.ItemsControlFromItemContainer(owner))
                if(owner is OverlayContextMenu menu){menu.IsOpen=false;return;}
        }),true);
    }
    internal static void SetOpen(ContextMenu menu,bool open)
    {if(menu is OverlayContextMenu hosted)hosted.IsOpen=open;else menu.IsOpen=open;}
    internal OverlayContextMenu()
    {
        SetResourceReference(StyleProperty,typeof(ContextMenu));
        // Outside presses belong to their real target; closing the menu must not
        // require a capture-only click before that target can react.
        StaysOpen=true;
        Opened += (_, _) =>
        {
            (openMenus??=[]).Add(this);
            sourceWindow=PlacementTarget is { } placement?Window.GetWindow(placement):null;
            if(sourceWindow is not null)sourceWindow.IsVisibleChanged+=SourceVisibilityChanged;
            if (PlacementTarget is { } target) parentRetention ??= PopupChildDialog.RetainFor(target,transientMenu:true);
            WatchForeground();
        };
        Closed += (_, _) =>
        {
            openMenus?.Remove(this);
            if(foregroundHook!=0){UnhookWinEvent(foregroundHook);foregroundHook=0;}
            foregroundProcedure=null;
            if(sourceWindow is not null)sourceWindow.IsVisibleChanged-=SourceVisibilityChanged;
            sourceWindow=null;
            var retention = parentRetention; parentRetention = null;
            // The popup's input-priority focus return runs before its parent is
            // eligible for outside-focus dismissal again.
            if (retention is not null) Dispatcher.BeginInvoke(DispatcherPriority.Input, retention.Dispose);
        };
    }
    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        ignoreOpeningRightRelease=false;
        base.OnPreviewMouseRightButtonDown(e);
    }
    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        // Screen-edge placement can put an item under the opening press. Its
        // release belongs to the launcher, not to that newly displayed item.
        if(ignoreOpeningRightRelease){ignoreOpeningRightRelease=false;e.Handled=true;return;}
        base.OnPreviewMouseRightButtonUp(e);
    }
    private void WatchForeground()
    {
        if(window is not { } opened || foregroundHook!=0)return;
        var origin=PlacementTarget is { } target?PresentationSource.FromVisual(target) as HwndSource:null;
        nint initial=GetForegroundWindow();uint openedAt=unchecked((uint)Environment.TickCount);
        foregroundProcedure=(_,_,hwnd,_,_,_,eventTime)=>
        {
            if(hwnd==0 || !ReferenceEquals(window,opened) || Dispatcher.HasShutdownStarted || unchecked((int)(eventTime-openedAt))<0 ||
                hwnd==origin?.Handle || hwnd==initial && unchecked((int)(eventTime-openedAt))<=0)return;
            var source=HwndSource.FromHwnd(hwnd);
            if(source is {IsDisposed:false} && source.Dispatcher.CheckAccess() && source.RootVisual is DependencyObject root && ContainsMenu(root))return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input,()=>
            {
                if(ReferenceEquals(window,opened))opened?.Close(returnFocus:false);
            });
        };
        foregroundHook=SetWinEventHook(3,3,0,foregroundProcedure,0,0,0);
    }
    private bool ContainsMenu(DependencyObject root)
    {
        if(OverlayPopupWindow.BelongsTo(root,this))return true;
        if(root is Window)return false;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(ContainsMenu(VisualTreeHelper.GetChild(root,i)))return true;
        return false;
    }
    private delegate void WinEventProcedure(nint hook,uint eventId,nint hwnd,int objectId,int childId,uint threadId,uint eventTime);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first,uint last,nint module,WinEventProcedure procedure,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private void SourceVisibilityChanged(object sender,DependencyPropertyChangedEventArgs args)
    {if(sourceWindow?.IsVisible==false)IsOpen=false;}
    public new bool IsOpen
    {
        get=>window is not null;
        set
        {
            if(value && window is null)
            {
                ignoreOpeningRightRelease=Mouse.RightButton==MouseButtonState.Pressed;
                var scope=OverlayWindowScope.Current;
                if(scope is null || PlacementTarget is { } target && Window.GetWindow(target) is { } owner && OverlayWindowScope.IsRegularWindow(owner))
                {
                    var source=PlacementTarget is { } anchor?PresentationSource.FromVisual(anchor) as HwndSource:null;
                    scope=ordinaryScope=new OverlayWindowScope(source?.Handle??0,()=>false,installAsCurrent:false);
                }
                window=new OverlayPopupWindow(this,PlacementTarget,Placement,HorizontalOffset,VerticalOffset,()=>IsOpen=false,CustomPopupPlacementCallback,targetPressToggles:TargetPressToggles,ownerScope:scope);
                window.Show();RaiseEvent(new RoutedEventArgs(OpenedEvent,this));
                if(Items.OfType<MenuItem>().FirstOrDefault(item=>item.IsEnabled) is { } first) window.Focus(first);
                if(Mouse.Captured is DependencyObject captured && OverlayPopupWindow.BelongsTo(captured,this))Mouse.Capture(null);
            }
            else if(!value && window is { } old)
            {window=null;foreach(var item in Items.OfType<MenuItem>()) CloseSubmenus(item);old.Dispose();ordinaryScope?.Dispose();ordinaryScope=null;RaiseEvent(new RoutedEventArgs(ClosedEvent,this));}
        }
    }
    private static void CloseSubmenus(MenuItem item)
    {foreach(var child in item.Items.OfType<MenuItem>()) CloseSubmenus(child);item.IsSubmenuOpen=false;}
}
