using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FloatCanvas.App;

/// <summary>Move a picker from its title using screen pixels, without activating its native owner.</summary>
internal static class PopupDrag
{
    internal static void Attach(FrameworkElement grip,PopupHandle popup)
    {
        grip.SetValue(FrameworkElement.CursorProperty,Cursors.SizeAll);
        grip.PreviewMouseLeftButtonDown+=(_,press)=>
        {
            if(press.ButtonState!=MouseButtonState.Pressed || PresentationSource.FromVisual(grip) is not HwndSource source ||
                !GetWindowRect(source.Handle,out var bounds)) return;
            var mouse=press.MouseDevice;
            Point origin=grip.PointToScreen(press.GetPosition(grip)),windowOrigin=new(bounds.Left,bounds.Top);
            bool staysOpen=popup.StaysOpen,ended=false;
            // Desktop Popup normally owns subtree capture. Temporarily retain it
            // while the title takes capture, then restore its outside-click policy.
            popup.StaysOpen=true;
            void Finish()
            {
                if(ended)return;ended=true;
                grip.PreviewMouseMove-=Move;grip.PreviewMouseLeftButtonUp-=Up;
                grip.LostMouseCapture-=Lost;grip.Unloaded-=Unloaded;
                if(mouse.Captured==grip)mouse.Capture(null);
                popup.StaysOpen=staysOpen;
            }
            void Move(object sender,MouseEventArgs args)
            {
                if(args.MouseDevice!=mouse)return;
                if(args.LeftButton!=MouseButtonState.Pressed){Finish();return;}
                var current=grip.PointToScreen(args.GetPosition(grip));
                popup.MoveTo(windowOrigin+(current-origin),grip);
                args.Handled=true;
            }
            void Up(object sender,MouseButtonEventArgs args){Finish();args.Handled=true;}
            void Lost(object sender,MouseEventArgs args)=>Finish();
            void Unloaded(object sender,RoutedEventArgs args)=>Finish();
            grip.PreviewMouseMove+=Move;grip.PreviewMouseLeftButtonUp+=Up;
            grip.LostMouseCapture+=Lost;grip.Unloaded+=Unloaded;
            if(!mouse.Capture(grip,CaptureMode.Element)){Finish();return;}
            press.Handled=true;
        };
    }

    internal static bool MoveNative(UIElement child,Point screen) =>
        PresentationSource.FromVisual(child) is HwndSource source && !source.IsDisposed &&
        SetWindowPos(source.Handle,0,(int)System.Math.Round(screen.X),(int)System.Math.Round(screen.Y),0,0,0x215);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle,out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle,nint after,int x,int y,int width,int height,uint flags);
}
