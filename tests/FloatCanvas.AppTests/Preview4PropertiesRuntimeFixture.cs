using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Preview4PropertiesRuntimeFixture
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,Window toolbar,PixelBounds bounds,Action<uint,bool> key)
    {
        var editor=Get<EditorController>(session,"_editor");editor.Tool=EditorTool.Select;
        editor.Session.Execute("Property fixture",document=>document.Elements.Clear());
        var shape=new CanvasElement {Kind=ElementKind.Rectangle,Width=100,Height=50,Style=new() {Fill=0xFF2383E2},Transform=Affine2D.Translation(bounds.Left+100,bounds.Top+100)};
        var text=new CanvasElement {Kind=ElementKind.Text,Text="Properties 中文",Width=180,Height=60,Transform=Affine2D.Translation(bounds.Left+280,bounds.Top+180)};
        editor.Session.Add(shape);editor.Session.Add(text);
        var route=Get<Action<OverlayPointerEvent>>(host,"Pointer");
        PointD at=new(bounds.Left+150,bounds.Top+125);
        Pointer(OverlayPointerKind.DoubleClick,at);
        var inspector=Get<InspectorWindow>(session,"_inspector");
        check("properties native: double click opens the selected layer panel",inspector.IsVisible&&inspector.IsActive&&editor.Session.Selection.SetEquals([shape.Id]));
        var initial=Rect(inspector);
        var monitor=host.Monitors.Single();double dpi=monitor.DpiX/96d;
        double expectedX=Math.Clamp(at.X+12*dpi,bounds.Left,Math.Max(bounds.Left,bounds.Right-inspector.ActualWidth*dpi));
        check("properties native: a hidden panel opens next to the pointer within the work area",Math.Abs(initial.Left-expectedX)<3);
        var pin=Get<Button>(inspector,"pin");pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pointer(OverlayPointerKind.Down,new(bounds.Left+10,bounds.Top+10));Pointer(OverlayPointerKind.Up,new(bounds.Left+10,bounds.Top+10));
        check("properties native: pinned panel remains visible across a canvas press",inspector.IsVisible&&inspector.IsPinned);
        Pointer(OverlayPointerKind.DoubleClick,new(bounds.Left+300,bounds.Top+195));
        check("properties native: an already visible pinned panel retains its position",inspector.IsVisible&&Rect(inspector).Left==initial.Left&&Rect(inspector).Top==initial.Top&&editor.Session.Selection.SetEquals([text.Id]));
        var rendered=VisualTreeHelper.GetDpi(inspector);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(inspector.ActualWidth*rendered.DpiScaleX),(int)Math.Ceiling(inspector.ActualHeight*rendered.DpiScaleY),rendered.PixelsPerInchX,rendered.PixelsPerInchY,PixelFormats.Pbgra32);
        bitmap.Render(inspector);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string imagePath=Path.Combine(Path.GetTempPath(),"FloatCanvas-properties-"+Guid.NewGuid().ToString("N")+".png");
        using(var output=File.Create(imagePath))encoder.Save(output);
        Console.WriteLine("PROPERTY_PANEL_RENDER "+imagePath);
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SessionWindowCoordinator.Show(inspector,true);Pump();
        var child=new Window {Owner=inspector,Title="Property child fixture",Width=220,Height=100,ShowInTaskbar=false,Topmost=true};
        try
        {
            SessionWindowCoordinator.Show(child,true);Pump();
            check("properties native: an active owned child does not dismiss the unpinned panel",child.IsActive&&inspector.IsVisible&&!inspector.IsPinned);
        }
        finally{SessionWindowCoordinator.Close(child);Pump();}
        SessionWindowCoordinator.Show(inspector,true);Pump();
        var colors=Get<Dictionary<string,ColorField>>(inspector,"colorFields");colors["Stroke"].OpenPicker(true);Pump();
        check("properties native: the nested color picker keeps its parent property panel",colors["Stroke"].IsPickerOpen&&inspector.IsVisible);
        colors["Stroke"].ClosePicker();SessionWindowCoordinator.Show(inspector,true);Pump();
        SessionWindowCoordinator.Show(toolbar,true);toolbar.Focus();Pump();
        check("properties native: true focus transfer to another panel dismisses and resets pin",!inspector.IsVisible&&!inspector.IsPinned&&!Get<bool>(session,"_inspectorWanted"));
        editor.Session.SetSelection([shape.Id,text.Id]);host.TryFocusEditingCanvas();Pump();key(0x0D,false);key(0x0D,true);Pump();
        check("properties native: Enter on multiple layers opens their properties without changing selection",inspector.IsVisible&&editor.Session.Selection.SetEquals([shape.Id,text.Id]));
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));inspector.Close();Pump();
        check("properties native: closing a pinned panel clears both visibility and pin state",!inspector.IsVisible&&!inspector.IsPinned&&!Get<bool>(session,"_inspectorWanted"));

        void Pointer(OverlayPointerKind kind,PointD point)
        {route(new(kind,new(point.X,point.Y),OverlayMouseButton.Left,OverlayModifiers.None));Pump();}
    }
    private static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static NativeRect Rect(Window window){GetWindowRect(new WindowInteropHelper(window).Handle,out var rect);return rect;}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint handle,out NativeRect rect);
}
