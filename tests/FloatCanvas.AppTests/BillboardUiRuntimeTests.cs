using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Production toolbar and Inspector controls hosted in hidden native windows, with routed edits and rendered evidence.</summary>
internal static class BillboardUiRuntimeTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        if(Application.Current is not null)throw new InvalidOperationException("Run --billboard-ui-runtime-only in a fresh process.");
        string output=Environment.GetEnvironmentVariable("FLOATCANVAS_BILLBOARD_UI_OUTPUT")??Path.Combine(Path.GetTempPath(),"FloatCanvas-billboard-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);ApplicationPaths.Configure(["--profile",Path.Combine(output,"profile"),"--primary-screen"]);
        new UserSettings {Theme="light",Language="zh-CN"}.Save();
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        OverlayHost? host=null;DesktopSession? desktop=null;Window? toolbar=null;InspectorWindow? inspector=null;
        FrameworkElement? toolbarContent=null;FrameworkElement? inspectorContent=null;
        nint toolbarHandle=0,inspectorHandle=0;nint[] overlayHandles=[];
        try
        {
            host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,[OverlayMonitorScope.PrimaryScreen,false],null)!;
            desktop=new DesktopSession(app,host);Field<System.Windows.Forms.NotifyIcon>(desktop,"_tray").Visible=false;
            // This fixture edits controls in hidden HWNDs. Keep document edits from
            // exercising the independent hidden-canvas auto-reveal behavior.
            typeof(DesktopSession).GetField("_updatingHiddenCanvas",Private)!.SetValue(desktop,true);
            overlayHandles=host.Diagnostics.SelectMany(pair=>new[]{pair.InputHandle,pair.DisplayHandle}).Append(host.ControlHandle).ToArray();
            toolbar=Field<Window>(desktop,"_toolbar");toolbarContent=(FrameworkElement)toolbar.Content;toolbar.Content=null;
            var editor=Field<EditorController>(desktop,"_editor");var session=editor.Session;
            var projection=CanvasCalibration.CreateInitial(session.Document.Viewport);
            projection.Enabled=true;projection.Matrix=new(1,0,0,0,1,0,0,.0005,1);
            projection.Quad=projection.Quad.Select(projection.Matrix.TransformPoint).ToList();session.Document.Canvas.Projection=projection;
            session.Add(new(){Kind=ElementKind.Rectangle,Width=100,Height=60,Transform=Affine2D.Translation(200,220),Style=new(){StrokeWidth=5,Fill=0x40E03E3E}});
            session.SetSelection([session.Document.Elements.Single().Id]);
            inspector=new InspectorWindow(()=>session); inspector.PrepareForShow();inspectorContent=(FrameworkElement)inspector.Content;inspector.Content=null;
            var toolbarRoot=new Border {Child=toolbarContent};var inspectorRoot=new Border {Width=340,Height=700,Child=inspectorContent};
            inspectorRoot.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,inspector.FontFamily);
            inspectorRoot.SetValue(System.Windows.Documents.TextElement.FontSizeProperty,inspector.FontSize);
            inspectorRoot.SetResourceReference(Border.BackgroundProperty,SystemColors.WindowBrushKey);
            inspectorRoot.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,SystemColors.WindowTextBrushKey);
            using(var toolbarSource=new HwndSource(new HwndSourceParameters("FloatCanvas billboard toolbar UI fixture"){Width=1600,Height=180,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000080}))
            using(var inspectorSource=new HwndSource(new HwndSourceParameters("FloatCanvas billboard Inspector UI fixture"){Width=340,Height=700,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000080}))
            {
                toolbarHandle=toolbarSource.Handle;inspectorHandle=inspectorSource.Handle;
                toolbarSource.RootVisual=toolbarRoot;inspectorSource.RootVisual=inspectorRoot;
                Layout(toolbarRoot);Layout(inspectorRoot);
                check("billboard production controls attach to real hidden native presentation sources",IsWindow(toolbarHandle)&&IsWindow(inspectorHandle)&&!IsWindowVisible(toolbarHandle)&&!IsWindowVisible(inspectorHandle)&&PresentationSource.FromVisual(inspectorContent) is HwndSource);
                Invoke(desktop,"RefreshToolbarState");Layout(toolbarRoot);
                var toggle=Field<Button>(desktop,"_billboardButton");
                var surfaceArtwork=Descendants(toggle).OfType<System.Windows.Shapes.Path>().Single().Data.ToString();
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Layout(toolbarRoot);
                check("native-hosted toolbar changes its actual placement artwork and highlights billboard mode",editor.BillboardMode&&toggle.IsEnabled&&toggle.Background==app.Resources["CanvasHiddenSurface"]&&Descendants(toggle).OfType<System.Windows.Shapes.Path>().Single().Data.ToString()!=surfaceArtwork);
                session.SetSelection([session.Document.Elements.Single().Id]); inspector.PrepareForShow();
                var placement=Field<SegmentedChoice>(inspector,"placement");placement.SelectedIndex=1;inspector.CompletePendingEdit();inspector.PrepareForShow();Pump();Layout(inspectorRoot);
                var fields=Field<Dictionary<string,TextBox>>(inspector,"fields");var element=session.Document.Elements.Single();
                fields["BillboardHeight"].Text="45";inspector.CompletePendingEdit();inspector.PrepareForShow();
                fields["Angle"].Text="30";inspector.CompletePendingEdit();inspector.PrepareForShow();
                var mapping=CanvasMapping.Create(session.Document);var anchor=element.BillboardAnchor;
                var center=mapping.ElementToScreen(element).TransformPoint(ElementGeometry.LocalBounds(element).Center);
                fields["W"].Text="200";inspector.CompletePendingEdit();inspector.PrepareForShow();Pump();Layout(inspectorRoot);
                check("native Inspector edits preserve the fixed billboard center and ground anchor during logical resize",element.IsBillboard&&element.Width==200&&element.Height==60&&element.BillboardHeight==45&&element.BillboardAnchor==anchor&&mapping.ElementToScreen(element).TransformPoint(ElementGeometry.LocalBounds(element).Center).DistanceTo(center)<1e-8&&element.Style.StrokeWidth==5);
                check("native Inspector own-angle edit stays independent of ground placement",Math.Abs(Math.Atan2(element.Transform.M21,element.Transform.M11)*180/Math.PI-30)<1e-8);
                foreach(string theme in new[]{"light","dark"})
                {
                    AppTheme.Apply(theme);Invoke(desktop,"RefreshToolbarState");Pump();Layout(toolbarRoot);Layout(inspectorRoot);
                    // A detached hidden root does not receive Window resource invalidations.
                    // Recreate the production Inspector at this theme, as a fresh user window would be created.
                    inspectorRoot.Child=null;inspector.Content=inspectorContent;inspector.Close();
                    session.SetSelection([element.Id]);
                    inspector=new InspectorWindow(()=>session); inspector.PrepareForShow();inspectorContent=(FrameworkElement)inspector.Content;inspector.Content=null;
                    inspectorRoot.Child=inspectorContent;
                    inspectorRoot.Background=(Brush)app.Resources["CanvasSurface"];
                    inspectorRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty,app.Resources["CanvasText"]);
                    Layout(inspectorRoot);
                    Render(toolbarRoot,Path.Combine(output,"toolbar-billboard-"+theme+".png"));
                    var scroll=Descendants(inspectorRoot).OfType<ScrollViewer>().First();scroll.ScrollToBottom();Pump();Layout(inspectorRoot);
                    Render(inspectorRoot,Path.Combine(output,"inspector-billboard-"+theme+".png"));
                    check(theme+" native billboard toolbar keeps its active icon border and background",toggle.Background==app.Resources["CanvasHiddenSurface"]&&toggle.BorderBrush==app.Resources["CanvasHiddenBorder"]&&Descendants(toggle).OfType<System.Windows.Shapes.Path>().Single().Stroke==app.Resources["CanvasHiddenText"]);
                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Layout(toolbarRoot);
                    Render(toolbarRoot,Path.Combine(output,"toolbar-surface-"+theme+".png"));
                    check(theme+" native toolbar restores its distinct surface artwork and ordinary background",!editor.BillboardMode&&Descendants(toggle).OfType<System.Windows.Shapes.Path>().Single().Data.ToString()==surfaceArtwork&&toggle.Background==app.Resources["CanvasButton"]);
                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Layout(toolbarRoot);
                }
                session.Document.Canvas.Projection.Enabled=false;session.NotifyChanged();Invoke(desktop,"RefreshToolbarState");inspector.PrepareForShow();Pump();
                var containers=Field<Dictionary<string,FrameworkElement>>(inspector,"fieldContainers");
                check("native controls restore the surface artwork on perspective shutdown while retaining layer metadata",!editor.BillboardMode&&!toggle.IsEnabled&&Descendants(toggle).OfType<System.Windows.Shapes.Path>().Single().Data.ToString()==surfaceArtwork&&containers["Placement"].Visibility==Visibility.Collapsed&&element.IsBillboard&&element.BillboardHeight==45&&element.BillboardAnchor==anchor);
                check("native billboard UI evidence never exposes a fixture or canvas window",!host.IsEditing&&!host.IsVisible&&app.Windows.Cast<Window>().All(window=>!window.IsVisible)&&!IsWindowVisible(toolbarHandle)&&!IsWindowVisible(inspectorHandle));
                toolbarSource.RootVisual=null;inspectorSource.RootVisual=null;toolbarRoot.Child=null;inspectorRoot.Child=null;
            }
        }
        finally
        {
            if(inspector is not null){inspector.Content=inspectorContent;inspector.Close();}
            if(toolbar is not null)toolbar.Content=toolbarContent;
            desktop?.Dispose();if(desktop is null)host?.Dispose();
            SynchronizationContext.SetSynchronizationContext(context);app.Shutdown();
        }
        check("native billboard UI fixtures release all of their native handles",!IsWindow(toolbarHandle)&&!IsWindow(inspectorHandle)&&overlayHandles.All(handle=>!IsWindow(handle)));
        Console.WriteLine("BILLBOARD_UI_NATIVE_RENDER "+output);
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static void Invoke(object target,string name)=>target.GetType().GetMethod(name,Private)!.Invoke(target,null);
    private static void Layout(FrameworkElement root){root.Measure(new Size(1800,900));root.Arrange(new Rect(root.DesiredSize));root.UpdateLayout();}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var item in Descendants(child))yield return item;}}
    private static void Render(FrameworkElement root,string path)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindowVisible(nint window);
}
