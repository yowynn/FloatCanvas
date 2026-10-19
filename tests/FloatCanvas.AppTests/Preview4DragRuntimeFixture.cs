using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class Preview4DragRuntimeFixture
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        var app=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};AppTheme.Apply("light");
        var a=new CanvasElement {Name="A",Kind=ElementKind.Rectangle,Width=50,Height=30};
        var b=a.Clone(true);b.Name="B";var c=a.Clone(true);c.Name="C";
        var session=new EditorSession(new SceneDocument {Elements=[a,b,c]});session.SetSelection([a.Id,b.Id]);
        var window=new LayersWindow(()=>session) {Width=340,Height=420,ShowInTaskbar=false,ShowActivated=false};
        nint foreground=GetForegroundWindow();
        try
        {
            window.Show();Pump();var list=Field<ListBox>(window,"layers");
            Thumb? active=null;Point start=new();
            void Begin()
            {
                var item=list.Items.Cast<ListBoxItem>().Single(row=>(Guid)row.Tag==a.Id);
                active=((Grid)item.Content).Children.OfType<Thumb>().Single();
                start=active.TranslatePoint(new(12,18),list);
                active.RaiseEvent(new DragStartedEventArgs(12,18) {RoutedEvent=Thumb.DragStartedEvent});
            }
            void Move(Point point){Invoke("UpdateDropTarget",point);Pump();}
            void Complete(Point point,bool cancelled=false)
            {active!.RaiseEvent(new DragCompletedEventArgs(point.X-start.X,point.Y-start.Y,cancelled) {RoutedEvent=Thumb.DragCompletedEvent});Pump();}
            Point Front()=>list.Items.Cast<ListBoxItem>().First().TranslatePoint(new(40,6),list);
            Begin();Move(Front());
            var feedback=Field<TileDragFeedback>(window,"dragFeedback");
            var ghost=Field<DragGhost>(feedback,"ghost");var visual=Field<Window>(ghost,"window");
            nint handle=new WindowInteropHelper(visual).Handle;
            check("layer drag shows a shared horizontal insertion line",feedback.TargetBounds is {Height:0,Width:>0});
            check("multi-layer drag dims exactly the selected rows",list.Items.Cast<ListBoxItem>().All(row=>Math.Abs(row.Opacity-((Guid)row.Tag==c.Id?1:.35))<.001));
            check("the floating drag image is visible at sixty-percent opacity without taking foreground",visual.IsVisible&&visual.Opacity==.6&&!visual.IsActive&&!visual.IsHitTestVisible&&GetForegroundWindow()==foreground);
            check("the floating image has non-activating and transparent native styles",(GetWindowLongPtr(handle,-20)&(nint)0x08000020)==(nint)0x08000020&&SendMessage(handle,0x84,0,0)==-1);
            Save(window,"layers");Save(visual,"ghost");
            Move(new(-130,120));GetWindowRect(handle,out var ghostRect);GetWindowRect(new WindowInteropHelper(window).Handle,out var ownerRect);
            check("the mouse-following image remains visible outside its source window",visual.IsVisible&&ghostRect.Left<ownerRect.Left&&feedback.TargetBounds is null);
            var source=(HwndSource)PresentationSource.FromVisual(window);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Escape) {RoutedEvent=Keyboard.PreviewKeyDownEvent});Pump();
            check("Escape closes the floating image and restores all selected rows",!visual.IsVisible&&Field<TileDragFeedback?>(window,"dragFeedback") is null&&list.Items.Cast<ListBoxItem>().All(row=>row.Opacity==1)&&session.UndoCount==0);
            Begin();var point=Front();Move(point);Complete(point);
            check("a multi-layer drop changes the real order in one undo step",session.Document.Elements.Select(element=>element.Id).SequenceEqual([c.Id,a.Id,b.Id])&&session.UndoCount==1);
            check("drag completion removes every floating drag window",!app.Windows.Cast<Window>().Any(item=>item.Content is Image&&item.WindowStyle==WindowStyle.None));
            session.Undo();window.Refresh();Pump();
            Begin();Move(Front());session.Add(new() {Name="D",Kind=ElementKind.Rectangle,Width=10,Height=10});int undo=session.UndoCount;
            Complete(Front());
            check("a document change during dragging aborts reordering and refreshes the real rows",session.UndoCount==undo&&session.Document.Elements.Count==4&&list.Items.Count==4&&Field<TileDragFeedback?>(window,"dragFeedback") is null);
            return;
            object? Invoke(string name,params object[] args)=>typeof(LayersWindow).GetMethod(name,Private)!.Invoke(window,args);
        }
        finally{window.Close();Pump();}
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Save(Window window,string name)
    {
        var dpi=VisualTreeHelper.GetDpi(window);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path=Path.Combine(Path.GetTempPath(),"FloatCanvas-drag-"+name+"-"+Guid.NewGuid().ToString("N")+".png");
        using(var file=File.Create(path))encoder.Save(file);Console.WriteLine("DRAG_RENDER "+name+" "+path);
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint handle,out NativeRect rect);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nuint wParam,nint lParam);
}
