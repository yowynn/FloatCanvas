using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class ThemeMenuRegressionTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        if(Application.Current is not null)throw new InvalidOperationException("Run menu theme regression in a fresh process.");
        bool overlay=Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_BACKEND")!="normal";
        string initial=Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_INITIAL")=="light"?"light":"dark";
        string opposite=initial=="light"?"dark":"light";
        string output=Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_OUTPUT")??System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FloatCanvas-ThemeRegression");
        Directory.CreateDirectory(output);
        string profile=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FloatCanvas-ThemeMenuRegression",overlay?"overlay":"normal");
        ApplicationPaths.Configure(["--profile",profile,"--primary-screen"]);
        new UserSettings {Theme=initial,Language=Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_LANGUAGE")??"en",ShowFloatingLogo=false}.Save();
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var context=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        using var activation=new ActivationGuard();
        var findings=new List<(string Name,bool Passed)>();var snapshots=new List<object>();
        OverlayHost? host=null;DesktopSession? session=null;OverlayContextMenu? menu=null;
        try
        {
            AppTheme.InitializeStartup();
            host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,[OverlayMonitorScope.PrimaryScreen,false],null)!;
            if(overlay)host.EnableSystemInput();
            session=new DesktopSession(app,host);
            Field<System.Windows.Forms.NotifyIcon>(session,"_tray").Visible=false;
            (Field<object?>(host,"_keyboardIsolation") as IDisposable)?.Dispose();
            typeof(OverlayHost).GetField("_keyboardIsolation",Private)!.SetValue(host,null);
            var toolbar=Field<Window>(session,"_toolbar");toolbar.Opacity=0;toolbar.Left=toolbar.Top=-12000;
            toolbar.ShowActivated=false;toolbar.Show();Pump();
            var body=Field<Border>(session,"_toolbarExpandedContent");
            menu=Field<OverlayContextMenu>(session,"_toolbarMenu");menu.Opacity=0;
            bool fileGroup=Environment.GetEnvironmentVariable("FLOATCANVAS_THEME_GROUP")=="file";
            var group=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,L.Get(fileGroup?"ToolbarV2.File":"Audit.Help")));
            var leaf=fileGroup?group.Items.OfType<MenuItem>().First():group.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,L.Get("Audit.UserGuide")));
            var mainItem=(MenuItem)menu.Items[0];
            Border? submenu=null;
            Open();
            foreach(string theme in new[]{initial,opposite,initial})
            {AppTheme.Apply(theme);Pump();Snapshot(theme,"open");}
            foreach(string theme in new[]{opposite,initial})
            {menu.IsOpen=false;Pump();AppTheme.Apply(theme);Pump();Open();Snapshot(theme,"reopened");}

            void Open()
            {
                menu.IsOpen=true;Pump();group.ApplyTemplate();
                object popup=group.Template.FindName("PART_Popup",group);
                submenu=(Border)(popup is Popup regular?regular.Child:((OverlayPopup)popup).Child!);
                submenu.Opacity=0;group.IsSubmenuOpen=true;Pump();
                Observe("production menu and submenu occupy real presentation sources",menu.IsOpen&&group.IsSubmenuOpen&&PresentationSource.FromVisual(menu) is HwndSource&&PresentationSource.FromVisual(submenu) is HwndSource);
            }
            void Snapshot(string theme,string phase)
            {
                var main=(Border)VisualTreeHelper.GetChild(menu,0);
                var mainText=Header(mainItem);var childText=Header(leaf);
                var mainIcon=Descendants((DependencyObject)mainItem.Icon).OfType<Shape>().First(value=>value.Stroke is not null);
                var childIcon=Descendants((DependencyObject)leaf.Icon).OfType<Shape>().First(value=>value.Stroke is not null);
                Color expectedInput=(Color)ColorConverter.ConvertFromString(theme=="dark"?"#2D323B":"#FFFFFF");
                Color expectedText=(Color)ColorConverter.ConvertFromString(theme=="dark"?"#E9ECF1":"#252B36");
                Color expectedSurface=(Color)ColorConverter.ConvertFromString(theme=="dark"?"#24272C":"#F8F9FB");
                Color? root=ColorOf(main.Background),sub=ColorOf(submenu!.Background),rootText=ColorOf(mainText?.Foreground),subText=ColorOf(childText?.Foreground),rootIcon=ColorOf(mainIcon.Stroke),subIcon=ColorOf(childIcon.Stroke);
                string name=theme+" "+phase;
                var inputResource=(Brush)app.Resources["CanvasInput"];var textResource=(Brush)app.Resources["CanvasText"];
                snapshots.Add(new{Theme=theme,Phase=phase,Overlay=overlay,Toolbar=ColorOf(body.Background)?.ToString(),Root=root?.ToString(),Submenu=sub?.ToString(),RootText=rootText?.ToString(),SubmenuText=subText?.ToString(),RootIcon=rootIcon?.ToString(),SubmenuIcon=subIcon?.ToString(),InputResource=BrushInfo(inputResource,inputResource),MenuBackground=BrushInfo(menu.Background,inputResource),TemplateBackground=BrushInfo(main.Background,inputResource),SubmenuBackground=BrushInfo(submenu.Background,inputResource),RootTextBrush=BrushInfo(mainText?.Foreground,textResource),SubmenuTextBrush=BrushInfo(childText?.Foreground,textResource),RootBackgroundSource=DependencyPropertyHelper.GetValueSource(main,Border.BackgroundProperty).BaseValueSource.ToString()});
                Console.WriteLine("MENU_THEME "+JsonSerializer.Serialize(snapshots[^1]));
                Observe(name+" actual toolbar surface follows theme",ColorOf(body.Background)==expectedSurface);
                Observe(name+" actual root Border background follows theme",root==expectedInput);
                Observe(name+" actual submenu Border background follows theme",sub==expectedInput);
                Observe(name+" realized root header follows theme",mainText is not null&&rootText==expectedText);
                Observe(name+" realized submenu header follows theme",childText is not null&&subText==expectedText);
                Observe(name+" realized root icon follows theme",rootIcon==expectedText);
                Observe(name+" realized submenu icon follows theme",subIcon==expectedText);
                var groupText=Header(group);
                var groupChrome=(Border)group.Template.FindName("Chrome",group);
                Color? groupBackground=ColorOf(groupChrome.Background);
                Observe(name+" open group header follows theme",ColorOf(groupText?.Foreground)==expectedText);
                Observe(name+" open group header remains readable",Contrast(ColorOf(groupText?.Foreground),groupBackground is {A:255}?groupBackground:root)>=4.5);
                Observe(name+" root text has readable contrast against its actual background",Contrast(rootText,root)>=4.5);
                Observe(name+" submenu text has readable contrast against its actual background",Contrast(subText,sub)>=4.5);
                Capture(menu,System.IO.Path.Combine(output,theme+"-"+phase+"-main.png"));
                Capture(submenu,System.IO.Path.Combine(output,theme+"-"+phase+"-submenu.png"));
            }
            void Observe(string name,bool value){findings.Add((name,value));Console.WriteLine($"MENU_THEME_FINDING {value}: {name}");}
        }
        finally
        {
            if(menu is not null)menu.IsOpen=false;
            session?.Dispose();if(session is null)host?.Dispose();
            foreach(var window in app.Windows.Cast<Window>().ToArray())window.Close();
            Pump();SynchronizationContext.SetSynchronizationContext(context);
            File.WriteAllText(System.IO.Path.Combine(output,overlay?"menu-theme-overlay.json":"menu-theme-normal.json"),JsonSerializer.Serialize(snapshots,new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown();
        }
        foreach(var finding in findings)check(finding.Name,finding.Passed);
    }
    private static TextBlock? Header(MenuItem item)=>Descendants(item).OfType<TextBlock>().FirstOrDefault(value=>value.Text==item.Header?.ToString());
    private static Color? ColorOf(Brush? brush)=>(brush as SolidColorBrush)?.Color;
    private static void Capture(FrameworkElement element,string path)
    {
        // Render the connected, laid-out tree while its own native host is hidden.
        // This never displays a test menu or changes another process's input.
        if(PresentationSource.FromVisual(element) is HwndSource source)ShowWindow(source.Handle,0);
        double opacity=element.Opacity;
        try
        {
            element.Opacity=1;element.UpdateLayout();
            var dpi=VisualTreeHelper.GetDpi(element);
            var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(element.ActualWidth*dpi.DpiScaleX)),Math.Max(1,(int)Math.Ceiling(element.ActualHeight*dpi.DpiScaleY)),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(path);encoder.Save(stream);
        }
        finally{element.Opacity=opacity;}
    }
    private static object BrushInfo(Brush? brush,Brush expected)
    {
        var binding=brush is SolidColorBrush solid?BindingOperations.GetBinding(solid,SolidColorBrush.ColorProperty):null;
        return new{Color=ColorOf(brush)?.ToString(),Identity=brush is null?0:RuntimeHelpers.GetHashCode(brush),SameAsApplicationResource=ReferenceEquals(brush,expected),Frozen=brush?.IsFrozen,BindingSourceType=binding?.Source?.GetType().FullName,BindingSourceIdentity=binding?.Source is{} source?RuntimeHelpers.GetHashCode(source):0};
    }
    private static double Contrast(Color? first,Color? second)
    {
        if(first is not{} a||second is not{} b)return 0;
        static double C(byte value){double x=value/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
        static double Luma(Color x)=>.2126*C(x.R)+.7152*C(x.G)+.0722*C(x.B);
        double x=Luma(a),y=Luma(b);return(Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
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
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
}
