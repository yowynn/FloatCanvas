using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L=FloatCanvas.Localization.AppText;

internal static class Round2StampRuntimeFixture
{
    internal static void Run(Action<string,bool> check,DesktopSession session,Window toolbar,EditorController editor)
    {
        var library=Field<StampLibrary>(session,"_stampLibrary");
        var shelf=Field<StampShelfControl>(session,"_stampShelf");Await(library.LoadAsync());
        editor.Session.Add(new(){Kind=ElementKind.Rectangle,Width=28,Height=20});
        editor.Session.SetSelection(editor.Session.Document.Elements.Select(e=>e.Id));
        var entry=Await(library.SaveSelectionAsync(editor.Session,"Slot original"));
        Await(library.PinAsync(0,entry.Id));Await(library.PinAsync(1,entry.Id));Pump();
        ContextMenu Menu(int slot)
        {
            var button=((StackPanel)shelf.Content).Children.OfType<Button>().ElementAt(slot);
            var args=(ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,[button,true],null)!;
            args.RoutedEvent=ContextMenuService.ContextMenuOpeningEvent;button.RaiseEvent(args);Pump();
            return button.ContextMenu;
        }
        var initial=Menu(1);
        check("toolbar stamp slot exposes exactly rename, overwrite, delete and unpin maintenance actions",initial.IsOpen&&initial.Items.OfType<MenuItem>().Select(item=>item.Tag).SequenceEqual(new[]{"Stamp.Rename","Stamp.Overwrite","Stamp.Delete","Stamp.Unpin"}));
        check("stamp slot menu has semantic icons without redundant use actions",initial.Items.OfType<MenuItem>().All(item=>item.Icon is FrameworkElement)&&!initial.Items.OfType<MenuItem>().Any(item=>Equals(item.Tag,"Stamp.Use")||Equals(item.Tag,"Stamp.UseSource")));
        string? output=Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_PREVIEW_DIR");
        if(output is not null)Render((FrameworkElement)((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(initial)).RootVisual,Path.Combine(output,"stamp-slot-menu.png"));
        initial.IsOpen=false;
        bool Act(string key,string response,string? name=null)
        {
            var menu=Menu(1);var item=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Tag,key));menu.IsOpen=false;
            bool prompted=false;int ticks=0;
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Owner==toolbar&&w.Content is StackPanel);
                if(dialog?.Content is not StackPanel panel)return;
                if(++ticks>250){dialog.Close();timer.Stop();return;}
                if(name is not null&&panel.Children.OfType<TextBox>().FirstOrDefault() is { } input)input.Text=name;
                var button=panel.Children.OfType<StackPanel>().SelectMany(row=>row.Children.OfType<Button>()).FirstOrDefault(b=>Equals(b.Content,L.Get(response)));
                if(button is null)return;prompted=true;timer.Stop();
                typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(button,null);
            };
            timer.Start();try{item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}finally{timer.Stop();}
            WaitUntil(()=>!Field<bool>(Field<StampLibraryWindow>(session,"_stampWindow"),"busy"));return prompted;
        }
        check("slot rename uses an owned modal and persists Unicode name without changing pins",Act("Stamp.Rename","Stamp.Save","重命名 / renamed")&&library.Items.Single().Name=="重命名 / renamed"&&library.Pins[0]==entry.Id&&library.Pins[1]==entry.Id);
        check("slot overwrite can be cancelled without changing the saved stamp",Act("Stamp.Overwrite","Stamp.Cancel")&&library.Items.Single().Name=="重命名 / renamed");
        check("slot delete cancel preserves the stamp and all bindings",Act("Stamp.Delete","Stamp.Cancel")&&library.Items.Any(e=>e.Id==entry.Id)&&library.Pins[1]==entry.Id);
        var unpin=Menu(1);unpin.IsOpen=false;unpin.Items.OfType<MenuItem>().Single(item=>Equals(item.Tag,"Stamp.Unpin")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WaitUntil(()=>library.Pins[1] is null);
        check("toolbar unpin removes the clicked slot and preserves another pin to the same stamp",library.Pins[0]==entry.Id&&library.Pins[1] is null&&library.Items.Any(e=>e.Id==entry.Id));
        Await(library.PinAsync(1,entry.Id));Pump();
        check("confirmed slot delete removes its catalog entry and all pins",Act("Stamp.Delete","Stamp.Confirm")&&!library.Items.Any(e=>e.Id==entry.Id)&&library.Pins.All(id=>id!=entry.Id));
        check("slot actions do not open the library window",!Field<StampLibraryWindow>(session,"_stampWindow").IsVisible);
        BillboardInsertionRoutes(check,session,editor,library);
    }

    private static void BillboardInsertionRoutes(Action<string,bool> check,DesktopSession session,EditorController editor,StampLibrary library)
    {
        editor.Session.Document.Canvas.Projection = new() { Enabled=true, Matrix=new(1,.08,0,0,1,0,0,.0003,1),
            Quad=[new(0,0),new(800,0),new(600,400),new(0,400)] };
        editor.Session.SetSelection([]); editor.BillboardMode=true;
        using var surface=SKSurface.Create(new SKImageInfo(40,30));surface.Canvas.Clear(SKColors.Coral);
        using var image=surface.Snapshot();using var encoded=image.Encode(SKEncodedImageFormat.Png,100);
        var png=encoded.ToArray();var normalized=new NormalizedImage(png,40,30);
        int originalCount=editor.Session.Document.Elements.Count,originalAssets=editor.Session.Assets.Count;
        bool QueueImage() => (bool)typeof(DesktopSession).GetMethod("InsertImages",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(session,[editor.Session,new NormalizedImage[]{normalized},false])!;
        void Pointer(OverlayPointerKind kind,PointD p)
        {
            var physical=p+editor.ScreenOrigin;
            typeof(DesktopSession).GetMethod("HandleCanvasPointer",BindingFlags.Instance|BindingFlags.NonPublic)!
                .Invoke(session,[new OverlayPointerEvent(kind,new(physical.X,physical.Y),OverlayMouseButton.Left,OverlayModifiers.None,0)]);
        }
        void Click(PointD point){Pointer(OverlayPointerKind.Down,point);Pointer(OverlayPointerKind.Up,point);Pump();}
        check("desktop fresh image import queues depth selection without creating assets or history",QueueImage()&&editor.HasPendingBillboardInsertion&&
            editor.Session.Document.Elements.Count==originalCount&&editor.Session.Assets.Count==originalAssets);
        Click(new(250,250));
        check("desktop image import first click fixes depth without inserting the image",editor.ActiveBillboardDepthLine is not null&&editor.Session.Document.Elements.Count==originalCount);
        Click(new(300,170));
        var inserted=editor.Session.Document.Elements.Last();
        check("desktop image import second click inserts the original pixels as one billboard",editor.Session.Document.Elements.Count==originalCount+1&&inserted.IsBillboard&&
            editor.Session.Assets.Get(inserted.AssetId!).Data.Span.SequenceEqual(png)&&editor.Tool==EditorTool.Select&&!editor.HasPendingBillboardInsertion);
        int countAfter=editor.Session.Document.Elements.Count,assetsAfter=editor.Session.Assets.Count;
        QueueImage();Click(new(230,260));editor.Tool=EditorTool.Rectangle;
        check("switching tools cancels pending imported pixels without an empty object or asset",!editor.HasPendingBillboardInsertion&&editor.ActiveBillboardDepthLine is null&&
            editor.Session.Document.Elements.Count==countAfter&&editor.Session.Assets.Count==assetsAfter);
        var source=new EditorSession(new(){Elements=[new(){Kind=ElementKind.Rectangle,Width=40,Height=30,Style=new(){Fill=0xFFFF8844,StrokeWidth=0}}]});source.SelectAll();
        var entry=Await(library.SaveSelectionAsync(source,"Raster billboard",StampSaveMode.RasterMerge));
        var loaded=Await(library.LoadStampAsync(entry.Id));
        void Set(string field,object? value)=>session.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(session,value);
        Set("_selectedStampId",(Guid?)entry.Id);Set("_selectedStamp",loaded);Set("_selectedStampRevision",entry.Revision);
        Set("_selectedStampUsesSource",false);Set("_selectedStampFromSlot",false);Set("_stampSelectionPending",false);
        editor.Tool=EditorTool.Stamp;
        Click(new(260,260));
        check("raster stamp uses the same first depth click without inserting prematurely",editor.Session.Document.Elements.Count==countAfter&&editor.ActiveBillboardDepthLine is not null);
        Click(new(340,150));
        check("raster stamp pointer route inserts a fresh billboard using its saved PNG",editor.Session.Document.Elements.Count==countAfter+1&&editor.Session.Document.Elements.Last().IsBillboard&&
            editor.Session.Assets.Get(editor.Session.Document.Elements.Last().AssetId!).Data.Span.SequenceEqual(loaded.Assets.Get(loaded.Document.Elements.Single().AssetId!).Data.Span));
        editor.Cancel();editor.Tool=EditorTool.Select;editor.BillboardMode=false;
    }
    internal static void Render(FrameworkElement element,string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame=new DispatcherFrame();var settle=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        settle.Tick+=(_,_)=>frame.Continue=false;settle.Start();try{Dispatcher.PushFrame(frame);}finally{settle.Stop();}
        element.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(element);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(element.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        var visual=new DrawingVisual();using(var draw=visual.RenderOpen())draw.DrawRectangle(new VisualBrush(element),null,new Rect(0,0,element.ActualWidth,element.ActualHeight));
        bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void WaitUntil(Func<bool> ready){var deadline=DateTime.UtcNow.AddSeconds(10);while(!ready()&&DateTime.UtcNow<deadline){Pump();Thread.Sleep(5);}if(!ready())throw new TimeoutException("Stamp action did not finish.");Pump();}
    private static T Await<T>(Task<T> task){WaitUntil(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
    private static void Await(Task task){WaitUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
}
