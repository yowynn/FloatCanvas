using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Production position callbacks on owned, non-activating HWNDs; no editing/input lease.</summary>
internal static class LauncherPositionTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        new UserSettings {ShowFloatingLogo=false}.Save();
        var host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,[OverlayMonitorScope.PrimaryScreen,false],null)!;
        using var session=new DesktopSession(app,host);
        Field<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
        var settings=Field<UserSettings>(session,"_settings");var toolbar=Field<Window>(session,"_toolbar");
        var monitor=host.PrimaryMonitor;
        settings.ToolbarX=monitor.Bounds.Left+30;settings.ToolbarY=monitor.Bounds.Top+150;
        settings.ShowPassThroughLauncher=true;Invoke(session,"RefreshPassThroughLauncher");
        var passive=Field<PassThroughLauncherWindow>(session,"_passThroughLauncher");passive.Hide();
        var surface=(FrameworkElement)toolbar.Content;surface.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        foreach(bool atRight in new[]{false,true})
        {
            double x=atRight?monitor.Bounds.Right-94*monitor.DpiX/96d-2:monitor.Bounds.Left+30;
            var requested=new OverlayPoint(x,monitor.Bounds.Top+150);
            var before=new Point(passive.Left,passive.Top);
            OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(passive).Handle,requested);Pump();
            Invoke(passive,"CompleteDrag",before);
            check($"passive drag {(atRight?"at right edge":"at ordinary position")} updates the shared physical coordinates",
                Near(new Point(settings.ToolbarX!.Value,settings.ToolbarY!.Value),passive.PointToScreen(new Point())));
            Invoke(session,"PlaceToolbar");Pump();
            nint foreground=GetForegroundWindow();
            toolbar.ShowActivated=false;toolbar.Show();Pump();
            var fitted=toolbar.PointToScreen(new Point());
            toolbar.Hide();
            check("showing the fitted test toolbar never takes foreground",GetForegroundWindow()==foreground);
            double width=surface.DesiredSize.Width*monitor.DpiX/96d;
            check($"editing placement {(atRight?"at right edge":"at ordinary position")} fits its complete available width",
                fitted.X>=monitor.Bounds.Left-1&&fitted.X+Math.Min(width,monitor.Bounds.Width)<=monitor.Bounds.Right+1);
            check("editing placement updates the same canonical origin",Near(fitted,new Point(settings.ToolbarX!.Value,settings.ToolbarY!.Value)));
            Invoke(session,"RefreshPassThroughLauncher");passive.Hide();Pump();
            check("returning to the passive view keeps the adapted origin",Near(fitted,passive.PointToScreen(new Point())));
            if(!atRight&&requested.X+width<=monitor.Bounds.Right)check("ordinary mode changes preserve the dragged position",Near(fitted,new Point(requested.X,requested.Y)));
        }
        var final=toolbar.PointToScreen(new Point());
        OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(toolbar).Handle,new(final.X+12,final.Y+10));Pump();
        Invoke(session,"SaveToolbarPosition");Invoke(session,"RefreshPassThroughLauncher");passive.Hide();Pump();
        check("editing drags feed the same passive position",Near(toolbar.PointToScreen(new Point()),passive.PointToScreen(new Point())));
        check("position verification never enters editing and leaves the toolbar hidden",!host.IsEditing&&!toolbar.IsVisible);
        check("both native modes retain the same DIP artwork size",passive.Width==ToolbarLauncher.ArtworkSize&&passive.Height==ToolbarLauncher.ArtworkSize&&passive.Launcher.LayoutTransform.Value.IsIdentity);
    }
    private static bool Near(Point a,Point b)=>(a-b).Length<=1.5;
    private static T Field<T>(object source,string name)=>(T)source.GetType().GetField(name,Private)!.GetValue(source)!;
    private static void Invoke(object source,string name,params object[] args)=>source.GetType().GetMethod(name,Private)!.Invoke(source,args);
    private static void Pump()
    {
        var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}
