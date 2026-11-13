using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

internal static class FlatCanvasOptionsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Runtime(check,output); Markers(check,output);
        foreach(string language in new[]{"zh-CN","en"})
        foreach(string theme in new[]{"light","dark"})
        {
            L.Initialize(language);AppTheme.Apply(theme);
            var settings=new CanvasSettings { Grid = new() { FlatOrigin = new(600,400), PlaneOrigin = new(37,51) } };
            var window=new CanvasOptionsTestWindow(CanvasPresetLibrary.OfficialOnly()) {ShowActivated=false,ShowInTaskbar=false,Left=-12000,Top=-12000};
            window.GridVisibilityChanged += value=>{settings.Grid.Visible=value;window.Refresh(settings);};
            window.Options.GridOriginChanged += value=>{settings.Grid.FlatOrigin=value;window.Refresh(settings);};
            try
            {
                window.Refresh(settings);window.PrepareForShow();window.Show();Pump(30);
                string prefix=language+"/"+theme;
                var origin=Field<StackPanel>(window.Options,"originFields");var x=Field<NumericValueField>(window.Options,"gridOriginX");
                var y=Field<NumericValueField>(window.Options,"gridOriginY");
                var labels=Field<StackPanel>(window.Options,"authoringControls").Children.OfType<TextBlock>().Select(item=>item.Text);
                check(prefix+" canvas has two correctly ordered peer sections",labels.SequenceEqual(new[]{L.Get("Canvas.Section.BackgroundGrid"),L.Get("Canvas.Section.PerspectiveGrid")}));
                check(prefix+" flat canvas exposes origin XY without perspective sampling",origin.IsVisible && x.Value==600 && y.Value==400 && !Field<FrameworkElement>(window.Options,"projectionGridCalibration").IsVisible);
                x.Input.Text="125";
                check(prefix+" flat origin edit does not change stored perspective origin",settings.Grid.FlatOrigin==new PointD(125,400) && settings.Grid.PlaneOrigin==new PointD(37,51));
                var grid=Field<CheckBox>(window.Options,"gridVisible");var appearance=Field<PropertyChangeRegion>(window.Options,"gridAppearanceRegion");
                var color=Field<ColorField>(window.Options,"gridColor");
                check(prefix+" disabled visible grid hides appearance but preserves placement",!color.IsVisible && origin.IsVisible && Field<NumericValueField>(window.Options,"gridSize").IsVisible);
                foreach(bool visible in new[]{true,false,true})
                {
                    grid.IsChecked=visible;grid.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(40);
                    check(prefix+" grid appearance changes with one cue "+visible,color.IsVisible==visible && appearance.CueStrength>0 && origin.IsVisible);
                    Pump(260);
                    // An isolated desktop can deliver the animation's first frame late.
                    // Observe its completion, rather than assuming timer time is frame time.
                    for(int frame=0;frame<25 && !double.IsNaN(appearance.Height);frame++) Pump(20);
                    window.UpdateLayout();
                    Console.WriteLine($"LAYOUT {prefix} visible={visible} actual={appearance.ActualHeight} height={appearance.Height} desired={appearance.Child.DesiredSize.Height}");
                    if(!visible)check(prefix+" hidden appearance has no empty reserved region",appearance.ActualHeight==0);
                }
                foreach(bool visible in new[]{false,true})
                foreach(double scale in new[]{1d,1.5,2d})
                {
                    settings.Grid.Visible=visible;window.Refresh(settings);window.PrepareForShow();appearance.Reset();window.Width=640;
                    VisualTreeHelper.SetRootDpi(window,new(scale,scale));window.UpdateLayout();Pump(20);
                    Save(window,Path.Combine(output,$"flat-{language}-{theme}-{visible}-{scale:0.0}.png"),scale);
                }
            }
            finally{window.CloseForDispose();}
        }
        L.Initialize("zh-CN");AppTheme.Apply("light");
    }

    private static void Runtime(Action<string,bool> check,string output)
    {
        string profile=ApplicationPaths.ProfileDirectory;var scope=ApplicationPaths.RequestedMonitorScope;
        string scratch=Path.Combine(Path.GetTempPath(),"FloatCanvas-flat-"+Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile",scratch,"--all-screens"]);
        new UserSettings{Theme="light",Language="zh-CN",ShowFloatingLogo=false}.Save();
        Func<IReadOnlyList<MonitorInfo>> monitors=()=>[
            new("primary",new(0,0,1200,800),new(0,0,1200,760),96,96,true),
            new("left",new(-800,0,800,600),new(-800,0,800,560),96,96,false)];
        using var host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Private,null,[OverlayMonitorScope.AllScreens,monitors,false],null)!;
        using var desktop=new DesktopSession(Application.Current,host);
        try
        {
            Field<System.Windows.Forms.NotifyIcon>(desktop,"_tray").Visible=false;
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsVisible))!.SetValue(host,true);
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host,true);
            var editor=Field<EditorController>(desktop,"_editor");editor.Tool=EditorTool.Canvas;
            editor.Session.Add(new CanvasElement { Kind=ElementKind.Rectangle, Transform=Affine2D.Translation(73,91), Width=123, Height=78 });
            string layers=JsonSerializer.Serialize(editor.Session.Document.Elements);
            Call(desktop,"CurrentGroupCanvasView");
            var options=(CanvasToolOptions)Call(desktop,"EnsureCanvasToolOptions")!;options.PrepareForShow();
            var source=editor.Session.Document.Canvas;source.Grid.PlaneOrigin=new(37,51);
            string projection=JsonSerializer.Serialize(source.Projection);
            int history=editor.Session.UndoCount;
            var x=Field<NumericValueField>(options,"gridOriginX");var y=Field<NumericValueField>(options,"gridOriginY");
            var savedOrigin=source.Grid.FlatOrigin; source.Grid.FlatOrigin=null;
            Call(desktop,"RefreshCanvasSettingsTarget"); options.PrepareForShow();
            check("unresolved flat origin displays its actual viewport reference without a document edit",x.Value==editor.Session.Document.Viewport.Center.X && y.Value==editor.Session.Document.Viewport.Center.Y && source.Grid.FlatOrigin is null && editor.Session.UndoCount==history);
            source.Grid.FlatOrigin=savedOrigin; Call(desktop,"RefreshCanvasSettingsTarget"); options.PrepareForShow();
            x.Input.Text="125";
            check("production flat origin edit changes only flat placement in one undo",source.Grid.FlatOrigin!.Value.X==125 && source.Grid.PlaneOrigin==new PointD(37,51) && JsonSerializer.Serialize(source.Projection)==projection && editor.Session.UndoCount==history+1);
            editor.Session.Undo();check("flat origin edit undo restores previous position",editor.Session.Document.Canvas.Grid.FlatOrigin!.Value.X!=125);
            editor.Session.Redo();Call(desktop,"RefreshCanvasSettingsTarget");options.PrepareForShow();
            var camera=new CanvasViewTransform(1.25,new(42,-28));Set(desktop,"_canvasView",camera);
            var before=editor.Session.Document.Canvas.Grid.FlatOrigin;history=editor.Session.UndoCount;
            var align=((Grid)x.Parent).Children.OfType<Button>().Single();align.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var defaultPin=new PointD(600,400)-editor.CanvasOrigin; var expectedPin=camera.ToView(defaultPin);
            var scene=editor.Session.Document;var mapping=CanvasMapping.CreateViewing(scene,camera);
            check("align to flat pin follows the viewed reference center after zoom and pan",mapping.ToScreen(mapping.GridOrigin).DistanceTo(expectedPin)<1e-8 && scene.Canvas.Grid.FlatOrigin==defaultPin);
            check("flat alignment preserves perspective data and is one undo",scene.Canvas.Grid.PlaneOrigin==new PointD(37,51) && JsonSerializer.Serialize(scene.Canvas.Projection)==projection && editor.Session.UndoCount==history+1);
            check("flat origin and pin alignment preserve authored layer coordinates",JsonSerializer.Serialize(scene.Elements)==layers);
            editor.Session.Undo();check("flat alignment undo restores the custom origin",editor.Session.Document.Canvas.Grid.FlatOrigin==before);
            editor.Tool=EditorTool.Select;using var bitmap=new SKBitmap(2000,800);using var canvas=new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            Call(desktop,"DrawCanvasCalibration",canvas);
            check("flat reference markers stay hidden for layer selection tools",bitmap.GetPixel(1400,400).Alpha==0);
            editor.Tool=EditorTool.Canvas;Call(desktop,"DrawCanvasCalibration",canvas);
            check("canvas tool paints its flat pin at the transformed reference center",bitmap.GetPixel((int)expectedPin.X,(int)expectedPin.Y).Blue>150 && bitmap.GetPixel((int)defaultPin.X,(int)defaultPin.Y).Alpha==0);
            Guides(desktop,editor,host,check,output);
            Call(desktop,"ExecuteCanvasViewing",CanvasViewingCommand.Reset); canvas.Clear(SKColors.Transparent); Call(desktop,"DrawCanvasCalibration",canvas);
            check("resetting the view returns the flat pin to the default center",Field<CanvasViewTransform>(desktop,"_canvasView")==CanvasViewTransform.Identity && bitmap.GetPixel((int)defaultPin.X,(int)defaultPin.Y).Blue>150);
            Call(desktop,"ChangeCanvasViewing",camera,false); history=editor.Session.UndoCount;
            Call(desktop,"ApplyCanvasViewing"); canvas.Clear(SKColors.Transparent); Call(desktop,"DrawCanvasCalibration",canvas);
            check("applying the view returns the flat pin to the default center",editor.Session.UndoCount==history+1 && Field<CanvasViewTransform>(desktop,"_canvasView")==CanvasViewTransform.Identity && bitmap.GetPixel((int)defaultPin.X,(int)defaultPin.Y).Blue>150);
            editor.Session.Undo(); canvas.Clear(SKColors.Transparent); Call(desktop,"DrawCanvasCalibration",canvas);
            var restored=Field<CanvasViewTransform>(desktop,"_canvasView");
            check("undoing view application restores the transformed pin",restored.Zoom==camera.Zoom && restored.ToView(defaultPin).DistanceTo(expectedPin)<1e-8 && bitmap.GetPixel((int)expectedPin.X,(int)expectedPin.Y).Blue>150);
            check("flat fixture never activates overlay input isolation",!host.IsKeyboardIsolationActive);
        }
        finally
        {
            typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host,false);desktop.Dispose();
            var args=new List<string>{"--profile",profile};if(scope is {} value)args.Add(value==OverlayMonitorScope.PrimaryScreen?"--primary-screen":"--all-screens");ApplicationPaths.Configure(args.ToArray());
        }
    }

    private static void Guides(DesktopSession desktop,EditorController editor,OverlayHost host,Action<string,bool> check,string output)
    {
        string before=JsonSerializer.Serialize(editor.Session.Document); int history=editor.Session.UndoCount;
        var document=new SceneDocument { Viewport=new(0,0,640,400), Canvas=new() { Grid=new() { FlatCellSize=40, FlatOrigin=new(160,120), Visible=false } } };
        using var renderer=new CanvasPreviewRenderer();
        SKBitmap Draw()
        {
            var context=(CanvasPreviewContext)Call(desktop,"CurrentPreviewContext")!;
            using var snapshot=new CanvasPreviewSnapshot { Context=context with { Origin=default }, SceneIdentity=document,
                Document=document, Resources=[], Outputs=[new(0,0,640,400)] };
            using var frame=renderer.Render(snapshot); return PreviewTestPixels.Read(frame.Pixels);
        }
        long Alpha(SKBitmap pixels)=>pixels.Bytes.Where((_,index)=>index%4==3).Sum(value=>(long)value);
        using var full=Draw();long fullInk=Alpha(full);
        check("flat Canvas tool renders reference grid even when authored grid is hidden",fullInk>10000);
        using(var image=SKImage.FromBitmap(full))using(var data=image.Encode(SKEncodedImageFormat.Png,100))using(var stream=File.Create(Path.Combine(output,"flat-reference-grid.png")))data.SaveTo(stream);
        Set(desktop,"_canvasGuideOpacity",.35);using(var dim=Draw())check("flat reference grid follows caption opacity",Alpha(dim)>0 && Alpha(dim)<fullInk/2);
        Set(desktop,"_canvasGuideOpacity",0d);using(var hidden=Draw())check("zero reference opacity hides the flat guide grid",Alpha(hidden)==0);
        Set(desktop,"_canvasGuideOpacity",1d);editor.Tool=EditorTool.Select;
        using(var hidden=Draw())check("switching away from Canvas removes flat reference grid",Alpha(hidden)==0);
        editor.Tool=EditorTool.Canvas;typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host,false);
        using(var hidden=Draw())check("leaving edit mode removes flat reference grid",Alpha(hidden)==0);
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host,true);
        check("reference grid and opacity leave authored data and history unchanged",JsonSerializer.Serialize(editor.Session.Document)==before && editor.Session.UndoCount==history);
    }

    private static void Markers(Action<string,bool> check,string output)
    {
        var settings=new CanvasSettings{Grid=new(){FlatOrigin=new(125,70)}};
        using var bitmap=new SKBitmap(640,400);using var canvas=new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        DesktopSession.DrawFlatCanvasReferenceMarkers(canvas,settings,CanvasViewTransform.Identity,new(0,0,640,400),new(320,200));
        check("flat document origin is marked at 0,0 independently of grid placement",bitmap.GetPixel(0,0).Green>100 && bitmap.GetPixel(125,70).Alpha==0);
        check("flat pin is marked at primary center independently of grid placement",bitmap.GetPixel(320,200)==new SKColor(35,131,226));
        using(var image=SKImage.FromBitmap(bitmap))using(var data=image.Encode(SKEncodedImageFormat.Png,100))using(var stream=File.Create(Path.Combine(output,"flat-markers.png")))data.SaveTo(stream);
    }
    private static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Private)!.GetValue(owner)!;
    private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Private)!.SetValue(owner,value);
    private static object? Call(object owner,string name,params object[] args)=>owner.GetType().GetMethod(name,Private)!.Invoke(owner,args);
    private static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    private static void Save(Window window,string path,double scale){var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*scale),(int)Math.Ceiling(window.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);}
}
