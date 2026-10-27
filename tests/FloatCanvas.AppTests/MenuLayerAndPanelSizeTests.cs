using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class MenuLayerAndPanelSizeTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,bool layout)
    {
        if(Application.Current is not null)throw new InvalidOperationException("Run this native fixture in a fresh process.");
        string output=Path.GetFullPath(".artifacts/menu-layer-min-size-20261027");Directory.CreateDirectory(output);
        string profile=Path.Combine(output,"profile-"+Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile",profile,"--primary-screen"]);
        new UserSettings {Theme="dark",Language="zh-CN",ShowFloatingLogo=false}.Save();
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        using var activation=new ActivationGuard();
        nint foreground=GetForegroundWindow();
        var results=new List<(string Name,bool Pass)>();
        void Observe(string name,bool pass){results.Add((name,pass));Console.WriteLine($"OBSERVE {name}: {pass}");}
        bool enhanced=Environment.GetEnvironmentVariable("FLOATCANVAS_LAYER_BACKEND")=="enhanced";
        OverlayHost? host=null;DesktopSession? session=null;CanvasSettingsWindow? canvas=null;Window? otherPanel=null;
        try
        {
            AppTheme.InitializeStartup();
            host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,[OverlayMonitorScope.PrimaryScreen,false],null)!;
            if(enhanced)host.EnableSystemInput();
            session=new DesktopSession(app,host);
            Field<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
            (Field<object?>(host,"_keyboardIsolation") as IDisposable)?.Dispose();
            typeof(OverlayHost).GetField("_keyboardIsolation",Private)!.SetValue(host,null);
            var toolbar=Field<Window>(session,"_toolbar");HidePixels(toolbar);toolbar.Show();Pump();
            var tool=Field<Window>(session,"_toolOptionsWindow");
            var panel=Field<ToolOptionsPanel>(session,"_toolOptionsPanel");
            canvas=new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly()){Owner=toolbar};HidePixels(canvas);
            Invoke(session,"ConfigureAppWindow",canvas,false,false);canvas.Show();Pump();
            if(layout)
            {
                Invoke(session,"ConstrainToolOptions");
                Observe("tool options use a useful shared minimum and allow resizing",tool.MinWidth>=640&&tool.MinHeight>=420&&tool.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip);
                Observe("canvas uses the same useful minimum",canvas.MinWidth>=640&&canvas.MinHeight>=420&&canvas.MinWidth==tool.MinWidth&&canvas.MinHeight==tool.MinHeight);
                var editor=Field<EditorController>(session,"_editor");editor.Tool=EditorTool.Rectangle;panel.PrepareForShow();
                HidePixels(tool);tool.Owner??=toolbar;tool.Show();Pump();
                foreach(var window in new[]{tool,(Window)canvas})
                {
                    window.SizeToContent=SizeToContent.Manual;
                    foreach(double width in new[]{640d,680d,820d})
                    {
                        window.Width=width;window.Height=420;Pump();
                        var gallery=window==tool?(FrameworkElement)Field<ToolPresetGallery>(panel,"presets"):Field<CanvasPresetGallery>(canvas,"presets");
                        var frame=window==tool?(Border)((Grid)gallery.Parent).Parent:(Border)((ScrollViewer)gallery.Parent).Parent;
                        var columns=(Grid)frame.Parent;
                        var fields=window==tool?Field<Grid>(panel,"parameterGrid"):(FrameworkElement)columns.Children[0];
                        var frameBounds=frame.TransformToAncestor(window).TransformBounds(new Rect(frame.RenderSize));
                        Console.WriteLine($"PANEL_MIN kind={window.GetType().Name} requested={width} actual={window.ActualWidth}x{window.ActualHeight} properties={fields.ActualWidth} presets={gallery.ActualWidth} row={Grid.GetRow(frame)} column={Grid.GetColumn(frame)}");
                        Observe(window.GetType().Name+" keeps usable side-by-side columns at "+width,Grid.GetRow(frame)==0&&Grid.GetColumn(frame)==1&&fields.ActualWidth>=340&&gallery.ActualWidth>=196&&frameBounds.Right<=window.ActualWidth+1&&frameBounds.Bottom<=window.ActualHeight+1);
                        if(width==640)Render(window,Path.Combine(output,window==tool?"tool-minimum.png":"canvas-minimum.png"));
                    }
                    window.Width=280;window.Height=240;Pump();
                    Observe(window.GetType().Name+" rejects destructive shrinking",window.ActualWidth>=640&&window.ActualHeight>=420);
                }
            }
            else
            {
                otherPanel=new Window {Owner=toolbar,Content=new TextBlock {Text="Another first-level panel"},Width=440,Height=420};HidePixels(otherPanel);
                Invoke(session,"ConfigureAppWindow",otherPanel,false,false);otherPanel.Show();Pump();
                var library=Field<StampLibrary>(session,"_stampLibrary");
                var entry=Task.Run(async()=>{await library.LoadAsync();var source=new EditorSession(new SceneDocument {Elements=[new(){Kind=ElementKind.Rectangle,Width=40,Height=30}]});source.SelectAll();return await library.SaveSelectionAsync(source,"Menu layering fixture");}).GetAwaiter().GetResult();
                Invoke(session,"EnsureStampWindow");
                var stamp=Field<StampLibraryWindow>(session,"_stampWindow");
                var slot=stamp.CreateToolbarMenu(entry.Id,0,toolbar);slot.PlacementTarget=Field<ToolbarLauncher>(session,"_toolbarLauncher");
                var main=Field<OverlayContextMenu>(session,"_toolbarMenu");main.PlacementTarget=slot.PlacementTarget;
                foreach(var (name,menu) in new[]{("main",main),("more",Field<OverlayContextMenu>(session,"_moreToolsMenu")),("stamp slot",slot)})
                {
                    menu.Opacity=0;Invoke(session,"RaiseVisibleToolWindows");Pump();
                    nint barHwnd=new WindowInteropHelper(toolbar).Handle,panelHwnd=new WindowInteropHelper(canvas).Handle;
                    Observe(name+" begins with the first-level panel above the toolbar",Rank(panelHwnd)<Rank(barHwnd));
                    menu.IsOpen=true;Pump();
                    nint menuHwnd=((HwndSource)PresentationSource.FromVisual(menu)).Handle;
                    Observe(name+" opens above the first-level panel",Rank(menuHwnd)<Rank(panelHwnd));
                    var group=menu.Items.OfType<MenuItem>().FirstOrDefault(item=>item.HasItems);
                    FrameworkElement? submenu=null;
                    if(group is not null)
                    {
                        group.ApplyTemplate();object popup=group.Template.FindName("PART_Popup",group);
                        submenu=(FrameworkElement)(popup is Popup regular?regular.Child:((OverlayPopup)popup).Child!);submenu.Opacity=0;
                        group.IsSubmenuOpen=true;Pump();
                    }
                    Invoke(session,"RaiseVisibleToolWindows");Pump();
                    Console.WriteLine($"MENU_LAYER backend={enhanced} menu={name} ranks menu={Rank(menuHwnd)} panel={Rank(panelHwnd)} toolbar={Rank(barHwnd)}");
                    Observe(name+" survives production panel restacking above all first-level windows",menu.IsOpen&&app.Windows.Cast<Window>().Where(window=>window.IsVisible&&!ReferenceEquals(window,toolbar)).All(window=>Rank(menuHwnd)<Rank(new WindowInteropHelper(window).Handle))&&Rank(panelHwnd)<Rank(barHwnd));
                    if(submenu is not null)
                    {
                        nint child=((HwndSource)PresentationSource.FromVisual(submenu)).Handle;
                        Observe(name+" keeps its submenu above both parent menu and panels",Rank(child)<Rank(menuHwnd)&&Rank(menuHwnd)<Rank(panelHwnd));
                    }
                    menu.IsOpen=false;Pump();
                }
            }
        }
        finally
        {
            otherPanel?.Close();canvas?.CloseForDispose();session?.Dispose();host?.Dispose();Pump();SynchronizationContext.SetSynchronizationContext(context);
        }
        Observe("native layering/layout fixture leaves the user's foreground unchanged",GetForegroundWindow()==foreground);
        foreach(var result in results)check(result.Name,result.Pass);
    }
    private static void HidePixels(Window window){var screen=System.Windows.Forms.SystemInformation.VirtualScreen;window.ShowActivated=false;window.Opacity=0;window.Left=screen.Left-10000;window.Top=screen.Top-10000;}
    private static int Rank(nint handle){nint current=GetTopWindow(0);for(int i=0;i<8192&&current!=0;i++,current=GetWindow(current,2))if(current==handle)return i;return int.MaxValue;}
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static object? Invoke(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Private)!.Invoke(target,args);
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Render(Window window,string path){var root=(FrameworkElement)VisualTreeHelper.GetChild(window,0);var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);}
    private sealed class ActivationGuard:IDisposable
    {
        private readonly HookProcedure procedure;private nint hook;
        internal ActivationGuard(){procedure=(code,w,l)=>code==5?1:CallNextHookEx(hook,code,w,l);hook=SetWindowsHookExW(5,procedure,0,GetCurrentThreadId());if(hook==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
        public void Dispose(){if(hook!=0){UnhookWindowsHookEx(hook);hook=0;}}
    }
    private delegate nint HookProcedure(int code,nint w,nint l);
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWindowsHookExW(int kind,HookProcedure procedure,nint module,uint thread);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nint w,nint l);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint window,uint relation);
}
