using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Real native windows and the production anchored-open path, rendered
/// without visible pixels or physical input. Non-native DPI values exercise
/// visual layout invalidation only; they do not claim a physical monitor change.</summary>
internal static class CanvasLayoutRegressionTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        _=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        AppTheme.Apply("dark");
        string output=Environment.GetEnvironmentVariable("FLOATCANVAS_CANVAS_LAYOUT_DIR")??Path.GetFullPath(Path.Combine(".artifacts","acceptance-next","canvas-layout","current"));Directory.CreateDirectory(output);
        var results=new List<(string Name,bool Pass)>();
        var activationGuard=new ActivationGuard();
        try
        {
        foreach(bool layered in new[]{true,false})
        foreach(bool premeasure in new[]{true,false})
        {
            using var owner=new HwndSource(new HwndSourceParameters("Canvas layout owner") {Width=1,Height=1,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000080});
            using var scope=layered?new OverlayWindowScope(owner.Handle,()=>true):null;
            var window=new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly()) {ShowActivated=false,Opacity=0,IsHitTestVisible=false};
            var layout=new SessionWindowLayout();layout.Track(window);
            window.Refresh(new CanvasSettings {Projection=new(){Enabled=true}});
            double preferred=window.Width;
            try
            {
                new WindowInteropHelper(window).EnsureHandle();window.ApplyTemplate();
                Reopen();Snapshot("first-"+premeasure);
                Reopen();Snapshot("reopened-"+premeasure);
                foreach(double width in new[]{preferred-20,320d,900d,preferred})
                {
                    window.Width=width;Pump();Snapshot("width-"+width+"-"+premeasure);
                }
                window.Refresh(new CanvasSettings {Projection=new(){Enabled=false}});Pump();Snapshot("flat-"+premeasure);
                Reopen();Snapshot("flat-reopened-"+premeasure);
                window.Refresh(new CanvasSettings {Projection=new(){Enabled=true}});Pump();Snapshot("perspective-"+premeasure);
                Reopen();Snapshot("perspective-reopened-"+premeasure);
                for(int repeat=0;repeat<3;repeat++){Reopen();Snapshot("repeat-"+repeat+"-"+premeasure);}
                foreach(double dpi in new[]{1d,1.25,1.5,2})
                {
                    VisualTreeHelper.SetRootDpi(window,new DpiScale(dpi,dpi));Pump();
                    Reopen();Snapshot("visual-dpi-"+dpi+"-"+premeasure);
                }
                window.Width=320;
                foreach(double height in new[]{240d,420d,800d}){window.Height=height;Pump();Reopen();Snapshot("minimum-height-"+height+"-"+premeasure);}
                window.Width=820;window.Height=620;Pump();
                void Reopen()
                {
                    window.Hide();
                    window.PrepareForShow();
                    if(premeasure)
                    {
                        var session=(DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
                        typeof(DesktopSession).GetMethod("ShowAnchoredToolWindow",Private)!.Invoke(session,[window,false,(Action)(()=>{})]);
                    }
                    else window.Show();
                    Pump();
                }
                void Snapshot(string name)
                {
                    name="layered-"+layered+"-"+name;
                    var presets=(CanvasPresetGallery)typeof(CanvasSettingsWindow).GetField("presets",Private)!.GetValue(window)!;
                    var parent=(Border)((ScrollViewer)presets.Parent).Parent;var grid=(Grid)parent.Parent;
                    var fourPoints=(Button)typeof(CanvasSettingsWindow).GetField("fourPoints",Private)!.GetValue(window)!;
                    var slot=System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(parent);
                    var bounds=window.IsVisible?presets.TransformToAncestor(window).TransformBounds(new Rect(presets.RenderSize)):Rect.Empty;
                    Console.WriteLine($"CANVAS_LAYOUT name={name} requested={window.Width} actual={window.ActualWidth} desired={window.DesiredSize} gallery={presets.ActualWidth}x{presets.ActualHeight} row={Grid.GetRow(parent)} col={Grid.GetColumn(parent)} grid={grid.ActualWidth}x{grid.ActualHeight} columns={string.Join(',',grid.ColumnDefinitions.Select(c=>c.ActualWidth))} rows={string.Join(',',grid.RowDefinitions.Select(r=>r.ActualHeight))} slot={slot} bounds={bounds} actions={((Grid)fourPoints.Parent).ActualWidth}");
                    if(window.IsVisible)
                    {
                        var visibleBounds=parent.TransformToAncestor(window).TransformBounds(new Rect(parent.RenderSize));
                        bool fits=visibleBounds.Left>=-1&&visibleBounds.Top>=-1&&visibleBounds.Right<=window.ActualWidth+1&&visibleBounds.Bottom<=window.ActualHeight+1;
                        results.Add((name+" retains a visible preset viewport",parent.ActualWidth>=196&&parent.ActualHeight>=50&&fits));
                        results.Add((name+" respects requested size and usable minimum",Math.Abs(window.ActualWidth-Math.Max(window.MinWidth,window.Width))<=1&&Math.Abs(window.ActualHeight-Math.Max(window.MinHeight,window.Height))<=1));
                        results.Add((name+" keeps presets beside the properties at every supported size",Grid.GetColumn(parent)==1&&Grid.GetRow(parent)==0));
                        var visual=(FrameworkElement)VisualTreeHelper.GetChild(window,0);
                        var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(visual.ActualWidth)),Math.Max(1,(int)Math.Ceiling(visual.ActualHeight)),96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
                        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);
                    }
                }
            }
            finally {window.CloseForDispose();}
            var restored=new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly()) {ShowActivated=false,Opacity=0,IsHitTestVisible=false};
            try
            {
                layout.Track(restored);restored.Show();Pump();
                results.Add(("layered-"+layered+" anchored-"+premeasure+" restores the user's session size on a new HWND",Math.Abs(restored.ActualWidth-820)<=1&&Math.Abs(restored.ActualHeight-620)<=1));
            }
            finally{restored.CloseForDispose();}
        }
        foreach(var result in results)check(result.Name,result.Pass);
        }
        finally{activationGuard.Dispose();}
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    // Exercise the real anchored-open method without allowing its production
    // Activate call to interrupt the user's current foreground application.
    private sealed class ActivationGuard:IDisposable
    {
        private readonly HookProcedure procedure;
        private nint hook;
        internal ActivationGuard()
        {
            procedure=(code,wparam,lparam)=>code==5?1:CallNextHookEx(hook,code,wparam,lparam);
            hook=SetWindowsHookExW(5,procedure,0,GetCurrentThreadId());
            if(hook==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose(){if(hook!=0){UnhookWindowsHookEx(hook);hook=0;}}
    }
    private delegate nint HookProcedure(int code,nint wparam,nint lparam);
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWindowsHookExW(int kind,HookProcedure procedure,nint module,uint thread);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nint wparam,nint lparam);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
}
