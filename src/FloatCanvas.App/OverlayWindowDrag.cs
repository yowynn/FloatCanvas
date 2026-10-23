using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Moves a delegated-input window without entering Win32's foreground caption-move loop.</summary>
internal static class OverlayWindowDrag
{
    internal static bool TryStart(Window window,Action? completed=null,int resizeEdge=2)
    {
        if(OverlayWindowScope.Current is null || Mouse.LeftButton!=MouseButtonState.Pressed) return false;
        nint handle=new WindowInteropHelper(window).Handle;
        if(handle==0 || !GetWindowRect(handle,out var initial) || !GetCursorPos(out var origin)) return false;
        bool ended=false;
        var dpi=VisualTreeHelper.GetDpi(window);
        void Finish()
        {
            if(ended)return;ended=true;
            window.PreviewMouseMove-=Move;window.PreviewMouseLeftButtonUp-=Up;
            window.LostMouseCapture-=Lost;window.Closed-=Closed;
            if(Mouse.Captured==window)Mouse.Capture(null);
            completed?.Invoke();
        }
        void Move(object sender,MouseEventArgs args)
        {
            if(args.LeftButton!=MouseButtonState.Pressed){Finish();return;}
            if(GetCursorPos(out var point))
            {
                int dx=point.X-origin.X,dy=point.Y-origin.Y;
                if(resizeEdge==2) SetWindowPos(handle,0,initial.Left+dx,initial.Top+dy,0,0,0x215);
                else
                {
                    int left=initial.Left,top=initial.Top,right=initial.Right,bottom=initial.Bottom;
                    if(resizeEdge is 10 or 13 or 16)left+=dx;
                    if(resizeEdge is 11 or 14 or 17)right+=dx;
                    if(resizeEdge is 12 or 13 or 14)top+=dy;
                    if(resizeEdge is 15 or 16 or 17)bottom+=dy;
                    int minWidth=(int)Math.Ceiling(window.MinWidth*dpi.DpiScaleX),minHeight=(int)Math.Ceiling(window.MinHeight*dpi.DpiScaleY);
                    if(right-left<minWidth){if(resizeEdge is 10 or 13 or 16)left=right-minWidth;else right=left+minWidth;}
                    if(bottom-top<minHeight){if(resizeEdge is 12 or 13 or 14)top=bottom-minHeight;else bottom=top+minHeight;}
                    SetWindowPos(handle,0,left,top,Math.Max(1,right-left),Math.Max(1,bottom-top),0x214);
                }
            }
            args.Handled=true;
        }
        void Up(object sender,MouseButtonEventArgs args){Finish();args.Handled=true;}
        void Lost(object sender,MouseEventArgs args)=>Finish();
        void Closed(object? sender,EventArgs args)=>Finish();
        window.PreviewMouseMove+=Move;window.PreviewMouseLeftButtonUp+=Up;window.LostMouseCapture+=Lost;window.Closed+=Closed;
        if(!Mouse.Capture(window,CaptureMode.SubTree)){Finish();return false;}
        return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
}
