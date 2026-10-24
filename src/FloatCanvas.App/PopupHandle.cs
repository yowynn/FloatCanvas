using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Uses the desktop Popup or the system overlay presenter behind the same picker lifecycle.</summary>
internal sealed class PopupHandle
{
    private static readonly ConditionalWeakTable<Popup,PopupHandle> Handles=new();
    internal static PopupHandle For(Popup popup)=>Handles.GetValue(popup,source=>new(source));
    private readonly Popup desktop;
    private readonly OverlayPopup? overlay;
    internal PopupHandle(Popup desktop)
    {
        this.desktop=desktop;
        if(OverlayWindowScope.Current is null) return;
        var child=desktop.Child;desktop.Child=null;
        overlay=new OverlayPopup {Child=child,PlacementTarget=desktop.PlacementTarget,Placement=desktop.Placement,
            HorizontalOffset=desktop.HorizontalOffset,VerticalOffset=desktop.VerticalOffset,StaysOpen=desktop.StaysOpen,
            IsHitTestVisible=desktop.IsHitTestVisible,Focusable=desktop.Focusable,
            AllowsTransparency=desktop.AllowsTransparency,CustomPopupPlacementCallback=desktop.CustomPopupPlacementCallback};
        if(desktop.PlacementTarget is FrameworkElement target)
        {
            overlay.DataContext=desktop.DataContext??target.DataContext;
            if(Window.GetWindow(target) is { } owner)overlay.Resources.MergedDictionaries.Add(owner.Resources);
        }
    }
    internal object Key=>(object?)overlay??desktop;
    internal UIElement? Child=>overlay?.Child??desktop.Child;
    internal bool IsOpen {get=>overlay?.IsOpen??desktop.IsOpen;set{if(overlay is not null)overlay.IsOpen=value;else desktop.IsOpen=value;}}
    internal bool StaysOpen {get=>overlay?.StaysOpen??desktop.StaysOpen;set{if(overlay is not null)overlay.StaysOpen=value;else desktop.StaysOpen=value;}}
    internal bool TargetPressToggles {set {if(overlay is not null)overlay.TargetPressToggles=value;}}
    internal double VerticalOffset {get=>overlay?.VerticalOffset??desktop.VerticalOffset;set{if(overlay is not null)overlay.VerticalOffset=value;else desktop.VerticalOffset=value;}}
    internal CustomPopupPlacementCallback? CustomPopupPlacementCallback
    {get=>overlay?.CustomPopupPlacementCallback??desktop.CustomPopupPlacementCallback;set{if(overlay is not null)overlay.CustomPopupPlacementCallback=value;else desktop.CustomPopupPlacementCallback=value;}}
    internal void OnOpened(EventHandler handler){if(overlay is not null)overlay.Opened+=handler;else desktop.Opened+=handler;}
    internal void OnClosed(EventHandler handler){if(overlay is not null)overlay.Closed+=handler;else desktop.Closed+=handler;}
    internal bool MoveTo(Point screen,UIElement child)
    {
        if(!IsOpen) return false;
        if(overlay is null && desktop.PlacementTarget is Visual target)
        {
            var point=target.PointFromScreen(screen);
            // RelativePoint keeps a manually placed picker at its chosen position
            // when its content later grows or shrinks (for example an error message).
            desktop.Placement=PlacementMode.RelativePoint;
            desktop.HorizontalOffset=point.X;desktop.VerticalOffset=point.Y;
            return true;
        }
        return PopupDrag.MoveNative(child,screen);
    }
    internal static bool IsOpenFor(object popup)=>popup switch {Popup normal=>normal.IsOpen,OverlayPopup hosted=>hosted.IsOpen,_=>false};
    internal static bool StaysOpenFor(object popup)=>popup switch {Popup normal=>normal.StaysOpen,OverlayPopup hosted=>hosted.StaysOpen,_=>false};
    internal static void Retain(object popup,bool value){if(popup is Popup normal)normal.StaysOpen=value;else if(popup is OverlayPopup hosted)hosted.StaysOpen=value;}
}
