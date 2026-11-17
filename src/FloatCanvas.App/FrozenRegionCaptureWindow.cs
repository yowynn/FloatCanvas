using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>A frozen-screen selector. Its private controller never changes the active editor/tool.</summary>
internal sealed class FrozenRegionCaptureWindow : Window
{
    private readonly CaptureSurface surface;
    internal EditorController Controller { get; }
    internal CaptureSelection? Selection { get; private set; }
    internal FrozenRegionCaptureWindow(CapturedDesktop desktop,SceneDocument scene,ToolParameters parameters,
        CanvasMapping mapping,PointD origin,bool billboard,BillboardCreationWorkflow workflow)
    {
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;
        Background=Brushes.Black;Cursor=Cursors.Cross;
        var controller=new EditorController(new EditorSession(scene.Clone())) { BillboardMode=billboard,BillboardWorkflow=workflow };
        Controller=controller;
        controller.ApplyParameters(parameters);controller.SetCanvasView(mapping,origin,new object());
        controller.ResumePresentedPicking();
        surface=new CaptureSurface(desktop,controller,origin);Content=surface;
        controller.CaptureSelectionCompleted+=selection=>{Selection=selection;if(IsVisible)DialogResult=true;};
        SourceInitialized+=(_,_)=>SetWindowPos(new WindowInteropHelper(this).Handle,new nint(-1),desktop.Bounds.Left,desktop.Bounds.Top,desktop.Bounds.Width,desktop.Bounds.Height,0x0040);
        Loaded+=(_,_)=>{Activate();surface.Focus();};
        PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Escape){DialogResult=false;e.Handled=true;}
            else if(controller.HandleCaptureKey(KeyInterop.VirtualKeyFromKey(e.Key)))e.Handled=true;
        };
        PreviewMouseRightButtonDown+=(_,e)=>{DialogResult=false;e.Handled=true;};
        Closed+=(_,_)=>{surface.ReleaseMouseCapture();controller.Cancel();};
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int cx,int cy,uint flags);

    private sealed class CaptureSurface : FrameworkElement
    {
        private readonly CapturedDesktop desktop;
        private readonly EditorController editor;
        private readonly PointD origin;
        private readonly BitmapImage image=new();
        internal CaptureSurface(CapturedDesktop desktop,EditorController editor,PointD origin)
        {
            this.desktop=desktop;this.editor=editor;this.origin=origin;Focusable=true;
            using var stream=new MemoryStream(desktop.PngBytes);
            image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();
            editor.Changed+=InvalidateVisual;
            MouseLeftButtonDown+=(_,e)=>{CaptureMouse();Send(e.ClickCount==2?OverlayPointerKind.DoubleClick:OverlayPointerKind.Down,e);e.Handled=true;};
            MouseMove+=(_,e)=>Send(OverlayPointerKind.Move,e);
            MouseLeftButtonUp+=(_,e)=>{Send(OverlayPointerKind.Up,e);ReleaseMouseCapture();e.Handled=true;};
        }
        private void Send(OverlayPointerKind kind,MouseEventArgs e)
        {
            if(ActualWidth<=0||ActualHeight<=0)return;
            var p=e.GetPosition(this);
            var mods=OverlayModifiers.None;
            if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))mods|=OverlayModifiers.Shift;
            if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control))mods|=OverlayModifiers.Control;
            if(Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))mods|=OverlayModifiers.Alt;
            editor.Pointer(new(kind,new(desktop.Bounds.Left+p.X*desktop.Bounds.Width/ActualWidth,desktop.Bounds.Top+p.Y*desktop.Bounds.Height/ActualHeight),
                kind==OverlayPointerKind.Move?OverlayMouseButton.None:OverlayMouseButton.Left,mods));
        }
        protected override void OnRender(DrawingContext dc)
        {
            var bounds=new Rect(0,0,ActualWidth,ActualHeight);
            dc.DrawImage(image,bounds);dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(100,0,0,0)),null,bounds);
            Point Map(PointD p)=>new((p.X+origin.X-desktop.Bounds.Left)*ActualWidth/desktop.Bounds.Width,(p.Y+origin.Y-desktop.Bounds.Top)*ActualHeight/desktop.Bounds.Height);
            if(editor.VisibleBillboardDepthLine is {} depth && depth.TryClip(new(desktop.Bounds.Left-origin.X,desktop.Bounds.Top-origin.Y,desktop.Bounds.Width,desktop.Bounds.Height),out var first,out var last))
                dc.DrawLine(new Pen(Brushes.Orange,1.5),Map(first),Map(last));
            if(editor.CapturePreview is not {} selection)return;
            Geometry shape;
            if(selection.Points.Count>0)
            {
                var path=new StreamGeometry { FillRule=FillRule.EvenOdd };
                using(var context=path.Open()) {context.BeginFigure(Map(selection.Points[0]),true,true);context.PolyLineTo(selection.Points.Skip(1).Select(Map).ToArray(),true,false);}
                shape=path;
            }
            else
            {
                var rect=new Rect(Map(new(selection.Bounds.Left,selection.Bounds.Top)),Map(new(selection.Bounds.Right,selection.Bounds.Bottom)));
                shape=selection.Shape==CaptureShape.Ellipse?new EllipseGeometry(rect):new RectangleGeometry(rect);
            }
            dc.PushClip(shape);dc.DrawImage(image,bounds);dc.Pop();
            dc.DrawGeometry(null,new Pen(Brushes.Black,3),shape);dc.DrawGeometry(null,new Pen(Brushes.White,1),shape);
        }
    }
}
