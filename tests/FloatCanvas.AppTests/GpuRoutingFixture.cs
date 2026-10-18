using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

/// <summary>Opt-in real App/Host GPU routing in a bounded own canvas. No desktop
/// capture, injected keyboard/mouse input, or user profile is used.</summary>
internal static class GpuRoutingFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string,bool> check,DesktopSession session,OverlayHost host,EditorController editor,
        Window toolbar,string run,string scenario,Action<string> snapshot)
    {
        if(scenario!="gpu-native")throw new ArgumentException("Unknown GPU routing scenario: "+scenario);
        var monitor=host.Diagnostics.Single();
        check("GPU routing keeps its real bounded canvas and keyboard lease",monitor.Bounds.Width==640 && monitor.Bounds.Height==360 && host.IsEditing && host.IsKeyboardIsolationActive);
        var settings=Field<UserSettings>(session,"_settings");
        check("production App startup loads the default enabled GPU preference",settings.PreferGpuAcceleration && UserSettings.Load().PreferGpuAcceleration);
        // Like the existing settings fixture, keep unchanged shortcut values in the
        // already-configured branch without registering any temporary system hotkey.
        Set(host,"_toggleHotkey",(HotkeyBinding?)settings.Toggle);
        Set(host,"_hideHotkey",(HotkeyBinding?)settings.Hide);
        editor.Cancel();
        byte[] png;
        using(var bitmap=new SKBitmap(160,80))
        {
            bitmap.Erase(SKColors.Red);
            using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);png=encoded.ToArray();
        }
        string asset=editor.Session.Assets.Add(png);
        var image=new CanvasElement {Kind=ElementKind.Image,AssetId=asset,Width=240,Height=120,
            Transform=Affine2D.Translation(monitor.Bounds.Left+80,monitor.Bounds.Top+90),Style=new(){StrokeWidth=0},
            Effects=[new(){Kind=ImageEffectKind.Blur,Bounds=new(0,0,1,1),Amount=8}]};
        var renderer=Field<SceneRenderer>(session,"_renderer");
        long gpuIntermediateBefore=renderer.GpuIntermediateSurfaceCount;
        var frames=new List<object>();var backends=new List<object>();
        int gpuCallbacks=0,cpuCallbacks=0,focusLoss=0,leaseEnds=0,injectedFaults=0,backendNotificationFaults=0;
        bool injectFault=false,gpuRedObserved=false,cpuFallbackRedObserved=false;OverlayFrameTiming? fallbackFrame=null;
        long epoch=host.EditingSessionVersion;
        Action lost=()=>focusLoss++;
        Action<KeyboardLeaseEndReason> leaseEnded=_=>leaseEnds++;
        Action backendChanged=()=>backends.Add(new {At=DateTimeOffset.Now,Backend=host.RenderBackend.ToString(),host.LastGpuFailure,host.GpuDetails});
        Action throwingBackendHandler=()=>{backendNotificationFaults++;throw new InvalidOperationException("injected backend observer fault");};
        Action<OverlayFrameTiming> submitted=frame=>{if(injectedFaults>0 && fallbackFrame is null)fallbackFrame=frame;};
        Action<SKSurface> observe=surface=>
        {
            bool gpu=surface.Context is not null;
            if(gpu)gpuCallbacks++;else cpuCallbacks++;
            if(gpu && injectFault)
            {
                injectFault=false;injectedFaults++;
                throw new InvalidOperationException("injected GPU fault");
            }
            using var rendered=surface.Snapshot();using var pixels=SKBitmap.FromImage(rendered);
            var color=pixels.GetPixel(200,150);
            bool red=color.Alpha>240 && color.Red>220 && color.Green<30 && color.Blue<30;
            gpuRedObserved|=gpu && red;cpuFallbackRedObserved|=!gpu && injectedFaults>0 && red;
            frames.Add(new {At=DateTimeOffset.Now,GpuContext=gpu,Red=color.Red,Green=color.Green,Blue=color.Blue,Alpha=color.Alpha,
                Epoch=host.EditingSessionVersion,host.IsEditing,Lease=host.IsKeyboardIsolationActive,host.GpuFrameCount,host.CpuFrameCount,
                renderer.GpuIntermediateSurfaceCount,renderer.RasterIntermediateSurfaceCount});
            string path=gpu?"own-gpu-canvas.png":injectedFaults>0?"own-cpu-fallback-canvas.png":"own-cpu-canvas.png";
            if(!File.Exists(Path.Combine(run,path))){using var encoded=rendered.Encode(SKEncodedImageFormat.Png,100);File.WriteAllBytes(Path.Combine(run,path),encoded.ToArray());}
        };
        host.Draw+=observe;host.RenderBackendChanged+=backendChanged;host.EditingFocusLost+=lost;
        host.KeyboardIsolationReleased+=leaseEnded;host.FrameSubmitted+=submitted;
        try
        {
            editor.Session.Execute("GPU fixture image blur",document=>{document.Elements.Clear();document.Elements.Add(image);});
            editor.Session.SetSelection([]);Invoke(session,"RequestRender");
            Wait(()=>host.RenderBackend==OverlayRenderBackend.Gpu && gpuCallbacks>0,"actual GPU image-filter frame");
            check("actual App frame uses a hardware GPU main canvas and filter intermediates",host.RenderBackend==OverlayRenderBackend.Gpu &&
                !string.IsNullOrWhiteSpace(host.GpuDetails) && gpuRedObserved && renderer.GpuIntermediateSurfaceCount>gpuIntermediateBefore);
            SaveWindow(toolbar,Path.Combine(run,"production-toolbar-gpu.png"));snapshot("gpu-image-filter-active");

            editor.Session.Execute("remove fixture image effects",document=>document.Elements.Single().Effects.Clear());
            Invoke(session,"RequestRender");Wait(()=>host.RenderBackend==OverlayRenderBackend.Cpu,"adaptive light-canvas CPU frame");
            check("removing image filters returns the production light canvas to CPU",host.RenderBackend==OverlayRenderBackend.Cpu && settings.PreferGpuAcceleration);
            check("restoring the filter uses only the normal edit undo",editor.Session.Undo());
            Invoke(session,"RequestRender");Wait(()=>host.RenderBackend==OverlayRenderBackend.Gpu,"restored GPU image filter");

            editor.Session.Execute("make filtered fixture layer transparent",document=>document.LayerOpacity=0);
            Invoke(session,"RequestRender");Wait(()=>host.RenderBackend==OverlayRenderBackend.Cpu,"transparent filtered layer CPU frame");
            bool transparentCpu=host.RenderBackend==OverlayRenderBackend.Cpu;
            bool opacityRestored=editor.Session.Undo();
            Invoke(session,"RequestRender");Wait(()=>host.RenderBackend==OverlayRenderBackend.Gpu,"restored opaque GPU filter frame");
            check("a transparent filtered layer uses CPU and undoing opacity restores GPU",transparentCpu && opacityRestored &&
                editor.Session.Document.LayerOpacity==1 && host.RenderBackend==OverlayRenderBackend.Gpu);

            editor.Session.Execute("move filtered fixture outside its viewport",document=>document.Elements.Single().Transform=
                Affine2D.Translation(document.Viewport.Right+300,document.Viewport.Bottom+300));
            Invoke(session,"RequestRender");Wait(()=>host.RenderBackend==OverlayRenderBackend.Cpu,"offscreen filtered image CPU frame");
            bool offscreenCpu=host.RenderBackend==OverlayRenderBackend.Cpu;
            bool positionRestored=editor.Session.Undo();
            Invoke(session,"RequestRender");Wait(()=>host.RenderBackend==OverlayRenderBackend.Gpu,"restored visible GPU filter frame");
            check("an offscreen filtered image uses CPU and undoing its move restores GPU",offscreenCpu && positionRestored &&
                host.RenderBackend==OverlayRenderBackend.Gpu);

            var first=OpenSettings();var prefer=GpuCheckBox(first);
            check("the actual settings window presents default GPU preference checked",prefer.IsChecked==true);
            SaveWindow(first,Path.Combine(run,"production-settings-gpu.png"));
            prefer.IsChecked=false;SaveSettings(first);
            Wait(()=>host.RenderBackend==OverlayRenderBackend.Cpu,"saved CPU preference frame");
            check("saving CPU preference applies immediately and persists in the isolated profile",!settings.PreferGpuAcceleration && !UserSettings.Load().PreferGpuAcceleration && host.RenderBackend==OverlayRenderBackend.Cpu);
            var second=OpenSettings();prefer=GpuCheckBox(second);
            check("reopening actual settings retains the saved CPU preference",prefer.IsChecked==false);
            SaveWindow(second,Path.Combine(run,"production-settings-cpu.png"));
            prefer.IsChecked=true;SaveSettings(second);
            Wait(()=>host.RenderBackend==OverlayRenderBackend.Gpu,"saved GPU preference frame");
            check("saving GPU preference restores real GPU rendering without restarting",settings.PreferGpuAcceleration && UserSettings.Load().PreferGpuAcceleration && host.RenderBackend==OverlayRenderBackend.Gpu);
            check("internal settings handoffs preserve the same edit epoch and active lease",host.IsEditing && host.IsKeyboardIsolationActive && host.EditingSessionVersion==epoch && focusLoss==0 && leaseEnds==0 && toolbar.IsVisible);

            string originalDocument=JsonSerializer.Serialize(editor.Session.Document);
            int undo=editor.Session.UndoCount,redo=editor.Session.RedoCount;
            var selected=editor.Session.Selection.ToHashSet();
            long gpuCount=host.GpuFrameCount,cpuCount=host.CpuFrameCount;
            int cpuCallbacksBefore=cpuCallbacks;
            host.RenderBackendChanged+=throwingBackendHandler;
            injectFault=true;Invoke(session,"RequestRender");
            Wait(()=>fallbackFrame is not null,"same-call CPU fallback after injected GPU draw failure");
            check("one failed GPU Draw is repainted on CPU within the same successful host Render",injectedFaults==1 && fallbackFrame is {Succeeded:true} &&
                host.RenderBackend==OverlayRenderBackend.Cpu && cpuFallbackRedObserved && host.GpuFrameCount==gpuCount && host.CpuFrameCount==cpuCount+1 && cpuCallbacks==cpuCallbacksBefore+1 &&
                backendNotificationFaults==1 && host.LastGpuFailure?.Contains("injected GPU fault",StringComparison.Ordinal)==true);
            int afterFaultGpuCallbacks=gpuCallbacks;
            for(int i=0;i<3;i++)
            {
                long before=host.CpuFrameCount;Invoke(session,"RequestRender");Wait(()=>host.CpuFrameCount>before,"subsequent CPU fallback frame");
            }
            check("failed GPU state is not retried every frame or replaced by an error dialog",gpuCallbacks==afterFaultGpuCallbacks && injectedFaults==1 &&
                Application.Current.Windows.Cast<Window>().Where(window=>window.IsVisible).All(window=>ReferenceEquals(window,toolbar)));
            check("GPU fallback preserves document, selection and undo/redo histories",JsonSerializer.Serialize(editor.Session.Document)==originalDocument &&
                editor.Session.UndoCount==undo && editor.Session.RedoCount==redo && editor.Session.Selection.SetEquals(selected));
            check("GPU fallback preserves editing, visibility, foreground ownership and keyboard lease",host.IsEditing && host.IsVisible && toolbar.IsVisible &&
                host.IsKeyboardIsolationActive && host.EditingSessionVersion==epoch && focusLoss==0 && leaseEnds==0 && IsOwnForeground());
            SaveWindow(toolbar,Path.Combine(run,"production-toolbar-cpu-fallback.png"));snapshot("gpu-fault-continued-on-cpu");
        }
        finally
        {
            host.Draw-=observe;host.RenderBackendChanged-=backendChanged;host.EditingFocusLost-=lost;
            host.RenderBackendChanged-=throwingBackendHandler;
            host.KeyboardIsolationReleased-=leaseEnded;host.FrameSubmitted-=submitted;
            File.WriteAllText(Path.Combine(run,"gpu-routing-metrics.json"),JsonSerializer.Serialize(new
            {
                Surface="Own bounded production overlay and WPF settings only; no desktop capture",NativeBounds=monitor.Bounds,
                GpuCallbacks=gpuCallbacks,CpuCallbacks=cpuCallbacks,GpuRedObserved=gpuRedObserved,CpuFallbackRedObserved=cpuFallbackRedObserved,InjectedGpuFaults=injectedFaults,InjectedBackendObserverFaults=backendNotificationFaults,FocusLosses=focusLoss,LeaseEnds=leaseEnds,
                InitialEpoch=epoch,FinalEpoch=host.EditingSessionVersion,Backend=host.RenderBackend.ToString(),host.LastGpuFailure,Frames=frames,BackendChanges=backends
            },new JsonSerializerOptions {WriteIndented=true}));
        }

        Window OpenSettings()
        {
            Invoke(session,"ShowHotkeySettings");
            Wait(()=>Application.Current.Windows.Cast<Window>().Any(window=>window.Title==L.Get("Settings.Title")&&window.IsVisible),"actual owned settings window");
            return Application.Current.Windows.Cast<Window>().Single(window=>window.Title==L.Get("Settings.Title")&&window.IsVisible);
        }
        void SaveSettings(Window window)
        {
            Descendants(window).OfType<Button>().Single(button=>Equals(button.Content,L.Get("Settings.Save"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(()=>!window.IsVisible,"actual settings save and close");
        }
    }

    private static CheckBox GpuCheckBox(Window window)=>Descendants(window).OfType<CheckBox>().Single(box=>AutomationProperties.GetName(box)==L.Get("Settings.PreferGpuAcceleration"));
    private static void SaveWindow(Window window,string path)
    {
        window.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(window);
        var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX)),Math.Max(1,(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY)),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var descendant in Descendants(child))yield return descendant;}
    }
    private static void Wait(Func<bool> condition,string operation)
    {
        long start=Stopwatch.GetTimestamp();
        while(!condition() && Stopwatch.GetElapsedTime(start).TotalSeconds<8)Pump(20);
        if(!condition())throw new TimeoutException(operation);
    }
    private static void Pump(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>frame.Continue=false;try{timer.Start();Dispatcher.PushFrame(frame);}finally{timer.Stop();}
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Private)!.SetValue(target,value);
    private static object? Invoke(object target,string name,params object[] arguments)=>target.GetType().GetMethod(name,Private)!.Invoke(target,arguments);
    private static bool IsOwnForeground()=>GetWindowThreadProcessId(GetForegroundWindow(),out uint process)!=0 && process==Environment.ProcessId;
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
}
