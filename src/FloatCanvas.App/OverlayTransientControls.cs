using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>Framework-created text editing menus and hover hints use the same popup owner as application menus.</summary>
internal static class OverlayTransientControls
{
    private static bool initialized;
    private static OverlayPopupWindow? hint;
    private static readonly DependencyProperty HostedCloseProperty = DependencyProperty.RegisterAttached("HostedClose", typeof(Action), typeof(OverlayTransientControls));
    internal static bool IsHintOpen(ToolTip toolTip) => toolTip.IsOpen || toolTip.GetValue(HostedCloseProperty) is Action;
    internal static void CloseHint(ToolTip toolTip)
    {
        if(toolTip.GetValue(HostedCloseProperty) is Action close) close();
        else toolTip.IsOpen=false;
    }
    internal static void Initialize()
    {
        if(initialized)return;initialized=true;
        EventManager.RegisterClassHandler(typeof(TextBox),ContextMenuService.ContextMenuOpeningEvent,new ContextMenuEventHandler(TextMenu));
        EventManager.RegisterClassHandler(typeof(RichTextBox),ContextMenuService.ContextMenuOpeningEvent,new ContextMenuEventHandler(TextMenu));
        EventManager.RegisterClassHandler(typeof(FrameworkElement),ToolTipService.ToolTipOpeningEvent,new ToolTipEventHandler(Hint));
    }
    private static void TextMenu(object sender,ContextMenuEventArgs args)
    {
        if(sender is not FrameworkElement element || element.ContextMenu is not null)return;
        var menu=new OverlayContextMenu {PlacementTarget=element,Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint};
        foreach(var command in new RoutedUICommand[]{ApplicationCommands.Undo,ApplicationCommands.Cut,ApplicationCommands.Copy,ApplicationCommands.Paste,ApplicationCommands.SelectAll})
            menu.Items.Add(new MenuItem {Header=command.Text,Command=command,CommandTarget=element});
        args.Handled=true;menu.IsOpen=true;
    }
    private static void Hint(object sender,ToolTipEventArgs args)
    {
        if(OverlayWindowScope.Current is not { } scope || sender is not FrameworkElement {ToolTip: { } value} element)return;
        if (Window.GetWindow(element) is { } owner && OverlayWindowScope.IsRegularWindow(owner)) return;
        args.Handled=true;
        var anchorSource=PresentationSource.FromVisual(element) as HwndSource;
        bool IsAnchored()=>ReferenceEquals(OverlayWindowScope.Current,scope)&&element.IsVisible&&
            anchorSource is {IsDisposed:false}&&ReferenceEquals(PresentationSource.FromVisual(element),anchorSource);
        if(!IsAnchored())return;
        hint?.Close();
        if(!IsAnchored())return;
        var toolTip=value as ToolTip??new ToolTip {Content=value};
        AppTheme.UsePopupPalette(toolTip);
        toolTip.Measure(OverlayPopupWindow.MeasurementLimit);
        if(!IsAnchored())return;
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ToolTipService.GetShowDuration(element))};
        OverlayPopupWindow? owned=null;
        bool closed=false,opened=false;
        void Close()
        {
            if(closed)return;closed=true;
            timer.Stop();element.MouseLeave-=Leave;element.Unloaded-=Unload;element.IsVisibleChanged-=VisibleChanged;
            toolTip.ClearValue(HostedCloseProperty);
            owned?.Dispose();if(ReferenceEquals(hint,owned))hint=null;
            if(opened)toolTip.RaiseEvent(new RoutedEventArgs(ToolTip.ClosedEvent,toolTip));
        }
        void Leave(object sender,MouseEventArgs args)=>Close();
        void Unload(object sender,RoutedEventArgs args)=>Close();
        void VisibleChanged(object sender,DependencyPropertyChangedEventArgs args){if(!element.IsVisible)Close();}
        owned=new OverlayPopupWindow(toolTip,element,System.Windows.Controls.Primitives.PlacementMode.Bottom,0,4,Close,hitTestVisible:false,ownerScope:scope,returnFocus:false,measuredSize:toolTip.DesiredSize);
        if(!IsAnchored()){owned.Dispose();return;}
        toolTip.PlacementTarget=element;
        toolTip.SetValue(HostedCloseProperty,(Action)Close);
        hint=owned;element.MouseLeave+=Leave;element.Unloaded+=Unload;element.IsVisibleChanged+=VisibleChanged;timer.Tick+=(_,_)=>Close();
        if(!IsAnchored()){Close();return;}
        owned.Show();
        if(closed)return;
        if(!IsAnchored()){Close();return;}
        opened=true;
        toolTip.RaiseEvent(new RoutedEventArgs(ToolTip.OpenedEvent,toolTip));
        if(!closed)timer.Start();
    }
}
