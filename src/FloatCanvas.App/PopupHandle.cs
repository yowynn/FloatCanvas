using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Runtime.CompilerServices;

namespace FloatCanvas.App;

/// <summary>Shares a transient lifecycle across regular owned panels and native popup presenters.</summary>
internal sealed class PopupHandle
{
    private static readonly ConditionalWeakTable<Popup,PopupHandle> Handles=new();
    internal static PopupHandle For(Popup popup)=>Handles.GetValue(popup,source=>new(source));
    private readonly Popup desktop;
    private readonly OverlayPopup? overlay;
    private readonly WindowPopup? panel;
    internal PopupHandle(Popup desktop, string? windowTitle = null)
    {
        this.desktop=desktop;
        if (windowTitle is not null)
        {
            var content = desktop.Child!; desktop.Child = null;
            panel = new WindowPopup(content, desktop.PlacementTarget, windowTitle, desktop.VerticalOffset) { StaysOpen = desktop.StaysOpen };
            return;
        }
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
    internal object Key=>(object?)panel??overlay??(object)desktop;
    internal UIElement? Child=>panel?.Child??overlay?.Child??desktop.Child;
    internal bool IsOpen {get=>panel?.IsOpen??overlay?.IsOpen??desktop.IsOpen;set{if(panel is not null)panel.IsOpen=value;else if(overlay is not null)overlay.IsOpen=value;else desktop.IsOpen=value;}}
    internal bool StaysOpen {get=>panel?.StaysOpen??overlay?.StaysOpen??desktop.StaysOpen;set{if(panel is not null)panel.StaysOpen=value;else if(overlay is not null)overlay.StaysOpen=value;else desktop.StaysOpen=value;}}
    internal bool TargetPressToggles {set {if(panel is not null)panel.TargetPressToggles=value;else if(overlay is not null)overlay.TargetPressToggles=value;}}
    internal double VerticalOffset {get=>panel?.VerticalOffset??overlay?.VerticalOffset??desktop.VerticalOffset;set{if(panel is not null)panel.VerticalOffset=value;else if(overlay is not null)overlay.VerticalOffset=value;else desktop.VerticalOffset=value;}}
    internal CustomPopupPlacementCallback? CustomPopupPlacementCallback
    {get=>overlay?.CustomPopupPlacementCallback??desktop.CustomPopupPlacementCallback;set{if(overlay is not null)overlay.CustomPopupPlacementCallback=value;else desktop.CustomPopupPlacementCallback=value;}}
    internal void OnOpened(EventHandler handler){if(panel is not null)panel.Opened+=handler;else if(overlay is not null)overlay.Opened+=handler;else desktop.Opened+=handler;}
    internal void OnClosed(EventHandler handler){if(panel is not null)panel.Closed+=handler;else if(overlay is not null)overlay.Closed+=handler;else desktop.Closed+=handler;}
    internal IDisposable? Suspend() => panel?.Suspend();
    internal static bool IsOpenFor(object popup)=>popup switch {WindowPopup window=>window.IsOpen,Popup normal=>normal.IsOpen,OverlayPopup hosted=>hosted.IsOpen,_=>false};
    internal static bool StaysOpenFor(object popup)=>popup switch {WindowPopup window=>window.StaysOpen,Popup normal=>normal.StaysOpen,OverlayPopup hosted=>hosted.StaysOpen,_=>false};
    internal static void Retain(object popup,bool value){if(popup is WindowPopup window)window.StaysOpen=value;else if(popup is Popup normal)normal.StaysOpen=value;else if(popup is OverlayPopup hosted)hosted.StaysOpen=value;}
}
