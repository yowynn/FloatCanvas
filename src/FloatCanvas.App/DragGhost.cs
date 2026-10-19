using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>A non-activating, click-through visual that follows the pointer beyond its source window.</summary>
internal sealed class DragGhost : IDisposable
{
    private readonly Window window;
    private readonly Image image;
    private readonly BitmapSource bitmap;
    private Point position;
    private nint handle;
    private bool disposed;

    internal DragGhost(FrameworkElement source,BitmapSource bitmap,Point position)
    {
        this.bitmap=bitmap;this.position=position;
        image=new Image {Source=bitmap,IsHitTestVisible=false,Focusable=false,Stretch=Stretch.Fill};
        window=new Window {Content=image,Owner=Window.GetWindow(source),WindowStyle=WindowStyle.None,AllowsTransparency=true,
            Background=Brushes.Transparent,Opacity=.6,ShowInTaskbar=false,ShowActivated=false,Focusable=false,
            IsHitTestVisible=false,Topmost=true,ResizeMode=ResizeMode.NoResize,SizeToContent=SizeToContent.WidthAndHeight};
        window.SourceInitialized+=(_,_)=>
        {
            handle=new WindowInteropHelper(window).Handle;
            SetWindowLongPtr(handle,-20,GetWindowLongPtr(handle,-20)|(nint)0x080000A0); // NOACTIVATE, TOOLWINDOW, TRANSPARENT
            HwndSource.FromHwnd(handle)!.AddHook(Hook);Update();
        };
        window.Closed+=(_,_)=>disposed=true;
        try{window.Show();Update();}
        catch{Dispose();throw;}
    }
    internal void Move(Point point){position=point;Update();}
    private void Update()
    {
        if(disposed||handle==0)return;
        var dpi=VisualTreeHelper.GetDpi(window);
        double width=bitmap.PixelWidth/dpi.DpiScaleX,height=bitmap.PixelHeight/dpi.DpiScaleY;
        if(image.Width!=width)image.Width=width;
        if(image.Height!=height)image.Height=height;
        OverlayCoordinates.PositionToolWindow(handle,new(position.X,position.Y));
    }
    private nint Hook(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x84){handled=true;return -1;}
        if(message==0x2E0)window.Dispatcher.BeginInvoke(DispatcherPriority.Render,Update);
        return 0;
    }
    public void Dispose(){if(disposed)return;disposed=true;window.Close();}
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint window,int index,nint value);
}
