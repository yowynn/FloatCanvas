using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class Round2UiTests
{
    internal static void Run(Action<string,bool> check,string group)
    {
        _ = Application.Current ?? new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        AppTheme.Apply("light");
        if(group=="colors")
        {
            var transparent=ColorPickerPanel.CreateSwatch(0);
            check("transparent palette has a checkerboard behind its color",transparent.Children.Count==2 && transparent.Children[0] is Border {Background:DrawingBrush});
            var swatch=new ColorSwatchButton(0x80112233);
            check("palette tiles cannot receive an automatic keyboard selection",!swatch.Focusable && swatch.FocusVisualStyle is null);
            var button=new Button();
            button.Style=(Style)Application.Current!.FindResource(typeof(Button));
            button.Measure(new(100,34));
            check("application button has no system focus ring",button.FocusVisualStyle is null);
            check("custom eyedropper cursor is created at exactly 32 pixels",GetIconInfo(EyedropperCursor.Handle,out var info)&&!info.Icon&&info.X==3&&info.Y==29);
            try {GetObject(info.Color,Marshal.SizeOf<NativeBitmap>(),out var bitmap);check("cursor size is independent of the system cursor scale",bitmap.Width==32&&bitmap.Height==32);}
            finally{DeleteObject(info.Color);DeleteObject(info.Mask);}
            if(Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_PREVIEW_DIR") is { } output)
            {Directory.CreateDirectory(output);SaveBitmap(EyedropperCursor.Preview(),Path.Combine(output,"eyedropper.png"));}
        }
        if(group=="welcome")
        {
            foreach(string language in new[]{"zh-CN","en"})
            {
                L.Initialize(language);
                int projects=0;
                var view=new WelcomeView(HotkeyBinding.DefaultToggle,HotkeyBinding.DefaultHide,()=>{},openProject:()=>projects++);
                view.Measure(new(382,double.PositiveInfinity));view.Arrange(new(0,0,382,view.DesiredSize.Height));view.UpdateLayout();
                var label=Descendants(view).OfType<TextBlock>().Single(t=>t.Text==L.Get("Acceptance2.Welcome.ToggleMode"));
                check(language+" welcome mode title remains on one line",label.ActualHeight<24);
                check(language+" welcome attribution is a clickable button",Descendants(view).OfType<Button>().Any(b=>b.Content is TextBlock t&&t.Text.StartsWith("By ")));
                view.ProjectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(language+" attribution requests the project homepage",projects==1);
                if(Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_PREVIEW_DIR") is { } output)
                {
                    Directory.CreateDirectory(output);var surface=new Border {Width=WelcomeView.PreferredWidth,Background=(Brush)Application.Current!.Resources["CanvasSurface"],Child=view};
                    surface.Measure(new(WelcomeView.PreferredWidth,double.PositiveInfinity));surface.Arrange(new(0,0,WelcomeView.PreferredWidth,surface.DesiredSize.Height));surface.UpdateLayout();
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)surface.ActualWidth,(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                    SaveBitmap(bitmap,Path.Combine(output,"welcome-"+language+".png"));
                }
            }
            L.Initialize("zh-CN");
        }
        if(group=="pins")
        {
            var session=new EditorSession(new());
            var inspector=new InspectorWindow(()=>session);
            inspector.PrepareForShow();
            try
            {
                inspector.ApplyTemplate();
                var pin=(Button)typeof(InspectorWindow).GetField("pin",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(inspector)!;
                var close=(Button)inspector.Template.FindName("Close",inspector);
                check("pin and close share size padding and margins",pin.Width==close.Width && pin.Height==close.Height && pin.Margin==close.Margin && pin.Padding==close.Padding);
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("pinned caption uses orange icon and outline",pin.BorderThickness==new Thickness(1)&&pin.Foreground==Application.Current!.Resources["CanvasHiddenText"]&&pin.BorderBrush==Application.Current!.Resources["CanvasHiddenBorder"]);
                var icon=(System.Windows.Shapes.Path)((Canvas)((Viewbox)pin.Content).Child).Children[0];
                check("pinned caption uses the eye toggle's active background and actual glyph stroke",pin.Background==Application.Current!.Resources["CanvasHiddenSurface"]&&icon.Stroke==Application.Current!.Resources["CanvasHiddenText"]);
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("unpin restores the complete neutral button appearance",pin.Background==Application.Current!.Resources["CanvasButton"]&&icon.Stroke==Application.Current!.Resources["CanvasText"]&&pin.BorderBrush==System.Windows.Media.Brushes.Transparent);
            }
            finally{inspector.Close();}
        }
        if(group=="logo")
        {
            check("floating logo is on in a fresh profile",new UserSettings().ShowPassThroughLauncher);
            var legacy=JsonSerializer.Deserialize<UserSettings>("""{"ShowPassThroughLauncher":false,"Theme":"dark"}""")!;
            check("old default-off profiles adopt the new logo default",legacy.ShowPassThroughLauncher&&legacy.Theme=="dark");
            var saved=new UserSettings {ShowPassThroughLauncher=false};
            check("explicit new logo preference remains off across saves",!JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(saved))!.ShowPassThroughLauncher);
        }
        if(group=="text")
        {
            using var renderer=new FloatCanvas.Rendering.SceneRenderer();
            var session=new EditorSession(new());
            using var edit=new InlineTextEdit(session,null,new(100,100),new(),renderer);
            int changes=0;edit.Changed+=()=>changes++;
            edit.Pointer(new(110,110),false,false);
            check("idle pointer movement does not restart the caret blink",changes==0);
            check("text frame has space around its first caret",edit.Contains(new(98,98)));
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static void SaveBitmap(System.Windows.Media.Imaging.BitmapSource bitmap,string path)
    {var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);}
    [StructLayout(LayoutKind.Sequential)]private struct IconInfo{[MarshalAs(UnmanagedType.Bool)]public bool Icon;public uint X,Y;public nint Mask,Color;}
    [StructLayout(LayoutKind.Sequential)]private struct NativeBitmap{public int Type,Width,Height,WidthBytes;public ushort Planes,BitsPixel;public nint Bits;}
    [DllImport("user32.dll")]private static extern bool GetIconInfo(nint handle,out IconInfo info);
    [DllImport("gdi32.dll")]private static extern int GetObject(nint handle,int length,out NativeBitmap bitmap);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint handle);
}
