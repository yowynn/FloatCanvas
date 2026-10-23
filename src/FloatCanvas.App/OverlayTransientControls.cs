using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>Framework-created text editing menus and hover hints use the same popup owner as application menus.</summary>
internal static class OverlayTransientControls
{
    private static bool initialized;
    private static OverlayPopupWindow? hint;
    internal static void Initialize()
    {
        if(initialized)return;initialized=true;
        EventManager.RegisterClassHandler(typeof(TextBox),ContextMenuService.ContextMenuOpeningEvent,new ContextMenuEventHandler(TextMenu));
        EventManager.RegisterClassHandler(typeof(RichTextBox),ContextMenuService.ContextMenuOpeningEvent,new ContextMenuEventHandler(TextMenu));
        EventManager.RegisterClassHandler(typeof(FrameworkElement),ToolTipService.ToolTipOpeningEvent,new ToolTipEventHandler(Hint));
    }
    private static void TextMenu(object sender,ContextMenuEventArgs args)
    {
        if(OverlayWindowScope.Current is null || sender is not FrameworkElement element || element.ContextMenu is not null)return;
        var menu=new OverlayContextMenu {PlacementTarget=element,Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint};
        foreach(var command in new RoutedUICommand[]{ApplicationCommands.Undo,ApplicationCommands.Cut,ApplicationCommands.Copy,ApplicationCommands.Paste,ApplicationCommands.SelectAll})
            menu.Items.Add(new MenuItem {Header=command.Text,Command=command,CommandTarget=element});
        args.Handled=true;menu.IsOpen=true;
    }
    private static void Hint(object sender,ToolTipEventArgs args)
    {
        if(OverlayWindowScope.Current is null || sender is not FrameworkElement {ToolTip: { } value} element)return;
        args.Handled=true;hint?.Close();
        var toolTip=value as ToolTip??new ToolTip {Content=value};
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ToolTipService.GetShowDuration(element))};
        OverlayPopupWindow? owned=null;
        void Close()
        {
            timer.Stop();element.MouseLeave-=Leave;
            owned?.Dispose();if(ReferenceEquals(hint,owned))hint=null;
        }
        void Leave(object sender,MouseEventArgs args)=>Close();
        owned=new OverlayPopupWindow(toolTip,element,System.Windows.Controls.Primitives.PlacementMode.Bottom,0,4,Close);
        hint=owned;element.MouseLeave+=Leave;timer.Tick+=(_,_)=>Close();owned.Show();timer.Start();
    }
}
