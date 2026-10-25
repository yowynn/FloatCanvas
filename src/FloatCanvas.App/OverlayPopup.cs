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
    protected override IEnumerator LogicalChildren => Child is null?Array.Empty<object>().GetEnumerator():new object[]{Child}.GetEnumerator();
    protected override Size MeasureOverride(Size availableSize)=>default;
    protected override DependencyObject GetUIParentCore()=>base.GetUIParentCore()??PlacementTarget!;
    private void UpdateOpen()
    {
        if(IsOpen && window is null && Child is { } child)
        {
            var target=PlacementTarget??TemplatedParent as UIElement;
            window=new OverlayPopupWindow(child,target,Placement,HorizontalOffset,VerticalOffset,()=>SetCurrentValue(IsOpenProperty,false),CustomPopupPlacementCallback,()=>!StaysOpen,IsHitTestVisible,TargetPressToggles);
            window.Show(); Opened?.Invoke(this,EventArgs.Empty);
        }
        else if(!IsOpen && window is { } old)
        { window=null; old.Dispose(); Closed?.Invoke(this,EventArgs.Empty); }
    }
}

/// <summary>Thread-scoped ownership for all interactive overlay windows and popups.</summary>
internal sealed class OverlayWindowScope : IDisposable
{
    private static readonly DependencyProperty PreparedProperty=DependencyProperty.RegisterAttached("Prepared",typeof(bool),typeof(OverlayWindowScope),new PropertyMetadata(false));
    [ThreadStatic] internal static OverlayWindowScope? Current;
    internal nint Owner { get; }
    private readonly Func<bool> inputActive;
    internal bool IsInputActive=>inputActive();
    private readonly List<OverlayPopupWindow> windows=[];
    internal OverlayWindowScope(nint owner,Func<bool>? inputActive=null)
    { if(Current is not null) throw new InvalidOperationException("An overlay session already owns this thread."); Owner=owner;this.inputActive=inputActive??(()=>true);Current=this;OverlayTransientControls.Initialize(); }
    internal static void Prepare(Window window)
    {
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
    internal bool IsTopmost(OverlayPopupWindow window)=>windows.LastOrDefault()==window;
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
    private readonly HwndSource source;
    private readonly UIElement content;
    private readonly Action close;
    private readonly UIElement? target;
    private readonly OverlayWindowScope scope;
    private readonly Func<bool> closeOnOutside;
    private readonly bool targetPressToggles;
    private bool returnFocus=true;
    private bool disposed;
    internal OverlayPopupWindow(UIElement child,UIElement? target,PlacementMode placement,double offsetX,double offsetY,Action close,CustomPopupPlacementCallback? custom=null,Func<bool>? closeOnOutside=null,bool hitTestVisible=true,bool targetPressToggles=false)
    {
        scope=OverlayWindowScope.Current??throw new InvalidOperationException("No active system overlay window owner.");
        this.close=close;this.target=target;
        this.targetPressToggles=targetPressToggles;
        this.closeOnOutside=closeOnOutside??(()=>true);
        content=child;
        child.Measure(new Size(SystemParameters.PrimaryScreenWidth*.75,SystemParameters.PrimaryScreenHeight*.75));
        var size=child.DesiredSize;
        Point position=target?.PointToScreen(new Point())??CursorPosition();
        double scale=target is Visual visual?VisualTreeHelper.GetDpi(visual).DpiScaleX:1;
        if(placement==PlacementMode.Bottom && target is FrameworkElement element) position.Y+=element.ActualHeight*scale;
        if(placement==PlacementMode.Right && target is FrameworkElement right) position.X+=right.ActualWidth*scale;
        if(placement==PlacementMode.MousePoint) position=CursorPosition();
        if(placement==PlacementMode.Custom && custom is not null && target is FrameworkElement anchor)
        {
            var choices=custom(size,anchor.RenderSize,new Point(offsetX,offsetY));
            if(choices.Length!=0) position=anchor.PointToScreen(choices[0].Point);
        }
        var area=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)position.X,(int)position.Y)).WorkingArea;
        position.X=Math.Clamp(position.X,area.Left,Math.Max(area.Left,area.Right-size.Width*scale));
        position.Y=Math.Clamp(position.Y,area.Top,Math.Max(area.Top,area.Bottom-size.Height*scale));
        source=new HwndSource(new HwndSourceParameters("FloatCanvas.Popup")
        {
            ParentWindow=scope.Owner,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000088|(hitTestVisible?0:0x20),
            UsesPerPixelOpacity=true,PositionX=(int)Math.Round(position.X+offsetX*scale),PositionY=(int)Math.Round(position.Y+offsetY*scale),
            Width=Math.Max(1,(int)Math.Ceiling(size.Width*scale)),Height=Math.Max(1,(int)Math.Ceiling(size.Height*scale))
        });
        source.CompositionTarget.BackgroundColor=Colors.Transparent;
        source.RootVisual=child;
        child.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent,new KeyboardFocusChangedEventHandler(OverlayWindowScope.ConfigureInputMethod),true);
        source.SizeToContent=SizeToContent.WidthAndHeight;
        source.AddHook(NoActivate);
        if(!hitTestVisible)source.AddHook((nint hwnd,int message,nint wparam,nint lparam,ref bool handled)=>
            {if(message==0x84){handled=true;return -1;}return 0;});
        scope.Add(this);
        InputManager.Current.PreProcessInput+=BeforeInput;
    }
    internal void Show()=>ShowWindow(source.Handle,4);
    internal void Focus(UIElement element)
    {SetActiveWindow(source.Handle);SetFocus(source.Handle);element.Focus();Keyboard.Focus(element);}
    private void BeforeInput(object sender,PreProcessInputEventArgs args)
    {
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
    private static bool BelongsTo(DependencyObject source,DependencyObject root)
    {
        for(var current=source;current is not null;current=current switch
            {ContextMenu menu=>menu.PlacementTarget,OverlayPopup popup=>popup.PlacementTarget??popup.TemplatedParent,
                _=>LogicalTreeHelper.GetParent(current)??(current is Visual?VisualTreeHelper.GetParent(current):null)})
            if(ReferenceEquals(current,root)) return true;
        return false;
    }
    internal void Close()=>close();
    public void Dispose()
    {
        if(disposed) return;disposed=true;
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
}

internal sealed class OverlayContextMenu : ContextMenu
{
    private OverlayPopupWindow? window;
    static OverlayContextMenu()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement),ContextMenuService.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((sender,e)=>
            {
                if(e.Handled || OverlayWindowScope.Current is null || sender is not FrameworkElement root ||
                    !ReferenceEquals(PresentationSource.FromVisual(root)?.RootVisual,root))return;
                for(DependencyObject? source=e.OriginalSource as DependencyObject;source is not null;
                    source=LogicalTreeHelper.GetParent(source)??(source is Visual?VisualTreeHelper.GetParent(source):null))
                    if(source is FrameworkElement {ContextMenu:OverlayContextMenu menu} element)
                    {e.Handled=true;menu.PlacementTarget=element;menu.IsOpen=true;return;}
            }));
    }
    internal static void SetOpen(ContextMenu menu,bool open)
    {if(menu is OverlayContextMenu hosted)hosted.IsOpen=open;else menu.IsOpen=open;}
    internal OverlayContextMenu()
    {
        SetResourceReference(StyleProperty,typeof(ContextMenu));
        AddHandler(MenuItem.ClickEvent,new RoutedEventHandler((_,e)=>
        {if(e.OriginalSource is MenuItem {HasItems:false,StaysOpenOnClick:false}) IsOpen=false;}));
    }
    public new bool IsOpen
    {
        get=>OverlayWindowScope.Current is null?base.IsOpen:window is not null;
        set
        {
            if(OverlayWindowScope.Current is null){base.IsOpen=value;return;}
            if(value && window is null)
            {
                window=new OverlayPopupWindow(this,PlacementTarget,Placement,HorizontalOffset,VerticalOffset,()=>IsOpen=false,CustomPopupPlacementCallback);
                window.Show();RaiseEvent(new RoutedEventArgs(OpenedEvent,this));
                if(Items.OfType<MenuItem>().FirstOrDefault(item=>item.IsEnabled) is { } first) window.Focus(first);
            }
            else if(!value && window is { } old)
            {window=null;foreach(var item in Items.OfType<MenuItem>()) CloseSubmenus(item);old.Dispose();RaiseEvent(new RoutedEventArgs(ClosedEvent,this));}
        }
    }
    private static void CloseSubmenus(MenuItem item)
    {foreach(var child in item.Items.OfType<MenuItem>()) CloseSubmenus(child);item.IsSubmenuOpen=false;}
}
