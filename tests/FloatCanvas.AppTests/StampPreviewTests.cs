using System.IO;
using System.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Threading = System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

/// <summary>Production slot controls and actual bounded stamp rendering, without opening a tooltip HWND.</summary>
internal static class StampPreviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        var app = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current;
        string language = L.Preference;
        SynchronizationContext.SetSynchronizationContext(new Threading.DispatcherSynchronizationContext(Threading.Dispatcher.CurrentDispatcher));
        try
        {
            string directory = Environment.GetEnvironmentVariable("FLOATCANVAS_STAMP_PREVIEW_TEST_DIR") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-StampPreview", Guid.NewGuid().ToString("N"));
            var library = new StampLibrary(Path.Combine(directory,"profile-"+Guid.NewGuid().ToString("N")));
            var saved = Task.Run(async () =>
            {
                await library.LoadAsync();
                var session = new EditorSession(new SceneDocument { Elements = [new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 80, Style = new ElementStyle { StrokeWidth = 0, Fill = 0xFFEA4C50 } }] });
                session.SetSelection(session.Document.Elements.Select(element => element.Id));
                var entry = await library.SaveSelectionAsync(session,"Preview specimen");
                await library.SaveSelectionAsync(session,"Second preview specimen");
                var bitmap = await StampPreviewTooltip.GetPreviewAsync(library,entry);
                return (session,entry,bitmap);
            }).GetAwaiter().GetResult();
            check("hover rendering generates a frozen 320px image from the actual stamp project", saved.bitmap is { IsFrozen:true,PixelWidth:320,PixelHeight:160 });
            byte[] pixels = new byte[320*160*4]; saved.bitmap!.CopyPixels(pixels,320*4,0);
            int center = (80*320+160)*4;
            check("large preview contains actual original artwork pixels", pixels[center+2] > 220 && pixels[center+3] > 250 && pixels[center+1] < 100);
            var animated=Task.Run(async()=>
            {
                var source=new EditorSession();
                byte[] gif=Convert.FromBase64String("R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs=");
                var id=source.Assets.Add(gif,"image/gif");
                source.Document.Elements.Add(new CanvasElement {Kind=ElementKind.Image,Width=30,Height=10,AssetId=id});source.SelectAll();
                var entry=await library.SaveSelectionAsync(source,"Animated preview specimen");
                int slot=library.Pins.ToList().FindIndex(id=>id==entry.Id);if(slot>=0)await library.PinAsync(slot,null);
                return(entry,stamp:await library.LoadStampAsync(entry.Id));
            }).GetAwaiter().GetResult();
            using(var playback=new StampPreviewTooltip.PreviewPlayback(animated.stamp,animated.entry))
            {
                var first=playback.Render(TimeSpan.Zero)!;var second=playback.Render(TimeSpan.FromMilliseconds(150))!;
                byte[] a=new byte[first.PixelWidth*first.PixelHeight*4],b=new byte[a.Length];first.CopyPixels(a,first.PixelWidth*4,0);second.CopyPixels(b,second.PixelWidth*4,0);
                check("large hover preview selects distinct actual GIF frames using its elapsed clock",!a.SequenceEqual(b)&&playback.NextDelay(TimeSpan.FromMilliseconds(150)) is { } next&&next>TimeSpan.Zero);
                var thumbnail=Task.Run(()=>StampPreviewTooltip.GetThumbnailAsync(library,animated.entry)).GetAwaiter().GetResult();
                check("small stamp thumbnail stays a frozen static bitmap",thumbnail is {IsFrozen:true});
            }
            ProjectedFallbackAspect(library, check);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool cancelledRead = false;
            try { Task.Run(() => StampPreviewTooltip.GetPreviewAsync(library,saved.entry,cancelled.Token)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) {cancelledRead=true;}
            check("cancelled hover cannot start a preview read", cancelledRead);
            foreach (double dpi in new[] {1d,1.5,2d})
            {
                var wide = StampPreviewTooltip.Geometry(160,80,new Wpf.DpiScale(dpi,dpi));
                check(dpi+" wide preview aspect fits a 176 DIP square", wide.WidthDip==176 && wide.HeightDip==88);
                check(dpi+" scale uses actual physical pixels per document pixel", Math.Abs(wide.ScaleX - 176*dpi/160) < .000001 && Math.Abs(wide.ScaleY - 88*dpi/80) < .000001);
                var tall = StampPreviewTooltip.Geometry(80,160,new Wpf.DpiScale(dpi,dpi));
                check(dpi+" tall preview preserves the original aspect ratio", tall.WidthDip==88 && tall.HeightDip==176);
            }
            var independentDpi = StampPreviewTooltip.Geometry(160,80,new Wpf.DpiScale(1.5,2));
            check("unequal DPI scales remain truthful on both axes", Math.Abs(independentDpi.ScaleX-1.65)<.000001 && Math.Abs(independentDpi.ScaleY-2.2)<.000001);
            var clicks = new List<Guid>();
            foreach (string theme in new[] {"light","dark"})
            {
                AppTheme.Apply(theme);
                var shelf = new StampShelfControl(library,clicks.Add);
                var row = (Controls.StackPanel)shelf.Content;
                row.SetResourceReference(Controls.Panel.BackgroundProperty,"CanvasSurface");
                row.Measure(new Wpf.Size(300,26)); row.Arrange(new Wpf.Rect(0,0,300,26)); row.UpdateLayout();
                check(theme+" shelf has exactly ten touching 30x26 cells", row.Children.Count==10 && row.Children.Cast<Controls.Button>().All(button=>button.Width==30 && button.Height==26 && button.MinWidth==30 && button.MinHeight==26 && button.Margin==new Wpf.Thickness(0)));
                check(theme+" slot markers read 1 through 0 at the lower left of each frame", row.Children.Cast<Controls.Button>().Select(button=>((Controls.Grid)button.Content).Children.OfType<Controls.TextBlock>().Single()).Select(number=>number.Text).SequenceEqual(new[]{"1","2","3","4","5","6","7","8","9","0"}));
                var empty = (Controls.Button)row.Children[2]; int before = clicks.Count;
                empty.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent,empty));
                var emptyContent = (Controls.Grid)empty.Content;
                var emptyFrame=emptyContent.Children.OfType<Controls.Border>().Single();
                var emptyLegend=emptyContent.Children.OfType<Controls.TextBlock>().Single();
                var legendBottomCenter=emptyLegend.TranslatePoint(new(emptyLegend.ActualWidth/2,emptyLegend.ActualHeight),emptyFrame);
                check(theme+" stamp frame remains 26 DIP and its legend straddles the left edge with bottoms aligned",
                    emptyFrame is {Width:26,Height:26,Margin:var frameMargin}&&frameMargin==new Wpf.Thickness(0)&&
                    emptyLegend.HorizontalAlignment==Wpf.HorizontalAlignment.Left&&emptyLegend.VerticalAlignment==Wpf.VerticalAlignment.Bottom&&
                    Math.Abs(legendBottomCenter.X)<.01&&Math.Abs(legendBottomCenter.Y-emptyFrame.ActualHeight)<.01);
                check(theme+" empty slots retain hover and readable number without invoking selection", empty.IsEnabled && empty.ToolTip is string && clicks.Count==before && emptyContent.Children.OfType<Controls.TextBlock>().Single().Opacity==1 && emptyContent.Children.OfType<Controls.Border>().Single().Opacity<.5);
                var bound = (Controls.Button)row.Children[0];
                var boundContent = (Controls.Grid)bound.Content;
                var boundFrame = boundContent.Children.OfType<Controls.Border>().Single();
                var slotPixels = Raster(row);
                var surfaceColor = ((Media.SolidColorBrush)app.Resources["CanvasSurface"]).Color;
                var emptyEdge = FrameEdge(slotPixels, emptyFrame, row);
                var boundEdge = FrameEdge(slotPixels, boundFrame, row);
                double emptyContrast = Contrast(emptyEdge, surfaceColor), boundContrast = Contrast(boundEdge, surfaceColor);
                check(theme+" empty slot outline remains faint but discernible against its actual toolbar surface", emptyContrast is >= 1.08 and <= 1.3);
                check(theme+" an assigned slot has a moderately stronger neutral outline than an empty slot", boundContrast is >= 1.5 and <= 2.3 && boundContrast > emptyContrast + .25);
                bound.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent,bound));
                check(theme+" bound slot invokes only its assigned stamp", clicks.Count==before+1 && clicks[^1]==saved.entry.Id);
                var tooltip = (StampPreviewTooltip)bound.ToolTip;
                var body = (Controls.StackPanel)tooltip.Content;
                var stage = body.Children.OfType<Controls.Grid>().Single();
                check(theme+" bound tooltip contains name shortcut and a fixed large image stage", body.Children.OfType<Controls.TextBlock>().Any(text=>text.Text==saved.entry.Name) && body.Children.OfType<Controls.TextBlock>().Any(text=>text.Text.Contains("1")) && stage.Width==176 && stage.Height==176 && stage.Children.OfType<Controls.Image>().Single().Source is null);
                check(theme+" previews remain lazy before a hover opens", stage.Children.OfType<Controls.Image>().Single().Source is null);
                shelf.SetActive(saved.entry.Id);
                var selected = (Controls.Grid)((Controls.Button)row.Children[0]).Content;
                PumpUntil(() => row.Children.Cast<Controls.Button>().Take(2).All(button=>((Controls.Grid)button.Content).Children.OfType<Controls.Image>().Single().Source is not null));
                check(theme+" shelf thumbnail is decoded without opening a large preview", selected.Children.OfType<Controls.Image>().Single().Source is not null);
                check(theme+" selected stamp uses the same accent outline as tool selection", ((Media.SolidColorBrush)selected.Children.OfType<Controls.Border>().Single().BorderBrush).Color == ((Media.SolidColorBrush)app.Resources["CanvasAccent"]).Color);
                row.Measure(new Wpf.Size(300,26)); row.Arrange(new Wpf.Rect(0,0,300,26)); row.UpdateLayout();
                var selectedFrame=selected.Children.OfType<Controls.Border>().Single();
                double selectedContrast=Contrast(FrameEdge(Raster(row),selectedFrame,row),surfaceColor);
                Console.WriteLine($"SLOT CONTRAST {theme}: empty={emptyContrast:F3}, assigned={boundContrast:F3}, selected={selectedContrast:F3}");
                check(theme+" selected blue frame remains opaque and clearly stronger than the assigned neutral frame",selectedFrame.Opacity==1 && selectedContrast>boundContrast+1);
                check(theme+" visual content stays centered inside each 30px column", row.Children.Cast<Controls.Button>().All(button=>((Controls.Grid)button.Content).Width==26 && button.ActualWidth==30 && button.ActualHeight==26));
                // Render the production tooltip control detached, supplying the already
                // verified bitmap; this is not evidence of a native hover or popup DPI.
                stage.Children.OfType<Controls.Image>().Single().Source=saved.bitmap;
                foreach(var label in stage.Children.OfType<Controls.TextBlock>())label.Visibility=Wpf.Visibility.Collapsed;
                tooltip.Measure(new Wpf.Size(double.PositiveInfinity,double.PositiveInfinity));
                tooltip.Arrange(new Wpf.Rect(new Wpf.Point(),tooltip.DesiredSize));tooltip.UpdateLayout();
                check(theme+" derived preview tooltip keeps the application's themed surface", ((Media.SolidColorBrush)tooltip.Background).Color==((Media.SolidColorBrush)app.Resources["CanvasInput"]).Color && tooltip.Template is not null);
                string? previewDirectory = Environment.GetEnvironmentVariable("FLOATCANVAS_STAMP_PREVIEW_TEST_DIR");
                if (!string.IsNullOrWhiteSpace(previewDirectory))
                {
                    Directory.CreateDirectory(previewDirectory);
                    var picture = new Imaging.RenderTargetBitmap(600,52,192,192,Media.PixelFormats.Pbgra32);picture.Render(row);
                    var encoder = new Imaging.PngBitmapEncoder();encoder.Frames.Add(Imaging.BitmapFrame.Create(picture));
                    using var stream=File.Create(Path.Combine(previewDirectory,"shelf-"+theme+".png"));encoder.Save(stream);
                    var tooltipPixels=new Imaging.RenderTargetBitmap((int)Math.Ceiling(tooltip.ActualWidth),(int)Math.Ceiling(tooltip.ActualHeight),96,96,Media.PixelFormats.Pbgra32);tooltipPixels.Render(tooltip);
                    var tooltipEncoder=new Imaging.PngBitmapEncoder();tooltipEncoder.Frames.Add(Imaging.BitmapFrame.Create(tooltipPixels));
                    using var tooltipStream=File.Create(Path.Combine(previewDirectory,"tooltip-"+theme+".png"));tooltipEncoder.Save(tooltipStream);
                }
            }
            var replacement = Task.Run(()=>library.SaveSelectionAsync(saved.session,"Replacement specimen",replaceId:saved.entry.Id)).GetAwaiter().GetResult();
            var stale = Task.Run(()=>StampPreviewTooltip.GetPreviewAsync(library,saved.entry)).GetAwaiter().GetResult();
            check("stale revision is rejected before its old artwork can be shown", replacement.Revision!=saved.entry.Revision && !StampPreviewTooltip.IsCurrent(library,saved.entry) && stale is null);
            check("preview tests show no application or tooltip windows",app.Windows.Cast<Wpf.Window>().All(window=>!window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle==0));
        }
        finally {SynchronizationContext.SetSynchronizationContext(context);L.Initialize(language);}
    }

    private static void ProjectedFallbackAspect(StampLibrary library, Action<string, bool> check)
    {
        var saved = Task.Run(async () =>
        {
            var document = new SceneDocument { Viewport = new(0,0,640,480), Elements = [new() { Kind = ElementKind.LivePixelate,
                Width = 80, Height = 60, Transform = Affine2D.Translation(150,150), Style = new() {StrokeWidth = 0}, Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(0,0,1,1), Amount = 20 }] }] };
            var initial = CanvasCalibration.CreateInitial(document.Viewport);
            if (!CanvasCalibration.TryCalibrate(initial,initial.Quad.Select(p=>new PointD(p.X,p.Y*.5)).ToArray(),document.Viewport,out var projection,out _)) throw new InvalidOperationException("Invalid test perspective");
            document.Canvas.Projection = projection;
            var session = new EditorSession(document); session.SelectAll();
            var entry = await library.SaveSelectionAsync(session,"Projected live-effect aspect",renderContext:new(document.Clone(),session.Assets));
            var bitmap = (await StampPreviewTooltip.GetPreviewAsync(library,entry))!;
            int slot = library.Pins.ToList().FindIndex(id => id == entry.Id); if (slot >= 0) await library.PinAsync(slot,null);
            return (entry,bitmap);
        }).GetAwaiter().GetResult();
        check("projected live-effect fixture reaches a saved thumbnail with a different pixel aspect", Math.Abs(saved.bitmap.PixelWidth/(double)saved.bitmap.PixelHeight-saved.entry.Width/saved.entry.Height) > .2);
        var tooltip = StampPreviewTooltip.Create(library,saved.entry,"");
        var stage = ((Controls.StackPanel)tooltip.Content).Children.OfType<Controls.Grid>().Single();
        stage.Children.OfType<Controls.Image>().Single().Source = saved.bitmap;
        typeof(StampPreviewTooltip).GetMethod("UpdateScale",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(tooltip,[new Wpf.DpiScale(1,1)]);
        foreach (var label in stage.Children.OfType<Controls.TextBlock>()) label.Visibility = Wpf.Visibility.Collapsed;
        stage.Measure(new Wpf.Size(176,176));stage.Arrange(new Wpf.Rect(0,0,176,176));stage.UpdateLayout();
        static double PixelAspect(Imaging.BitmapSource bitmap)
        {
            var converted = new Imaging.FormatConvertedBitmap(bitmap,Media.PixelFormats.Bgra32,null,0);
            int w=converted.PixelWidth,h=converted.PixelHeight;byte[] pixels=new byte[w*h*4]; converted.CopyPixels(pixels,w*4,0);
            int left=w,right=-1,top=h,bottom=-1;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(pixels[(y*w+x)*4+3]>128){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            return (right-left+1d)/(bottom-top+1d);
        }
        var sourceAspect=PixelAspect(saved.bitmap);var displayedAspect=PixelAspect(Raster(stage));
        Console.WriteLine($"Projected thumbnail source aspect={sourceAspect:F4}, actual tooltip pixels={displayedAspect:F4}, logical stamp={saved.entry.Width/saved.entry.Height:F4}");
        check("the actual tooltip preserves saved preview pixel aspect even when logical stamp dimensions differ",Math.Abs(displayedAspect/sourceAspect-1)<.04);
    }

    private static Imaging.RenderTargetBitmap Raster(Wpf.FrameworkElement element)
    {
        var picture = new Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 2),
            (int)Math.Ceiling(element.ActualHeight * 2),192,192,Media.PixelFormats.Pbgra32);
        picture.Render(element); return picture;
    }

    private static Media.Color FrameEdge(Imaging.BitmapSource picture, Controls.Border frame, Wpf.UIElement row)
    {
        var point = frame.TranslatePoint(new Wpf.Point(frame.ActualWidth/2,.5),row);
        byte[] pixel = new byte[4];
        picture.CopyPixels(new Wpf.Int32Rect((int)(point.X*2),(int)(point.Y*2),1,1),pixel,4,0);
        return Media.Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);
    }

    private static double Contrast(Media.Color first, Media.Color second)
    {
        static double Channel(byte value) { double s = value/255d; return s <= .04045 ? s/12.92 : Math.Pow((s+.055)/1.055,2.4); }
        static double Luminance(Media.Color color) => .2126*Channel(color.R)+.7152*Channel(color.G)+.0722*Channel(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }

    private static void PumpUntil(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new Threading.DispatcherFrame(); var watch=System.Diagnostics.Stopwatch.StartNew();
        var timer = new Threading.DispatcherTimer(Threading.DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick += (_,_)=>{if(completed() || watch.Elapsed>TimeSpan.FromSeconds(5))frame.Continue=false;};
        timer.Start();try{Threading.Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        if(!completed())throw new TimeoutException("The actual slot thumbnail did not finish loading.");
    }
}
