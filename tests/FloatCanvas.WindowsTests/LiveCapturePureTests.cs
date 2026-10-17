using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.WindowsTests;

internal static class LiveCapturePureTests
{
    internal static void Run(Action<bool, string> Check)
    {
        MonitorInfo Monitor(string name,PixelBounds b)=>new(name,b,b,144,144,false);
        var targets=LiveDesktopCapture.BuildTargets(new[]{Monitor("RIGHT",new(0,-20,5,4)),Monitor("LEFT",new(-10,-20,6,4))},new PixelBounds[]{new(-8,-21,10,3),new(-7,-19,1,3),new(2,-19,1,2),new(100,100,2,2),new(0,0,0,0)});
        Check(targets.Length==2,"only two involved monitor unions are captured");
        Check(targets[0].Monitor.DeviceName=="LEFT"&&targets[0].Region==new PixelBounds(-8,-20,4,4),"negative-origin left ROI clips and unions correctly");
        Check(targets[1].Monitor.DeviceName=="RIGHT"&&targets[1].Region==new PixelBounds(0,-20,3,3),"right ROI uses its own physical coordinate slice");
        Check(LiveDesktopCapture.BuildTargets(new[]{Monitor("A",new(0,0,5,5))},new[]{new PixelBounds(5,0,4,4)}).Length==0,"touching right edge does not count as an intersection");
        Check(LiveDesktopCapture.BuildTargets(Array.Empty<MonitorInfo>(),new[]{new PixelBounds(0,0,4,4)}).Length==0,"no monitors means suspended capture");
        foreach(var bad in new[]{new PixelBounds(int.MaxValue,0,2,2),new PixelBounds(0,0,-1,2)}){
         bool rejected=false;try{LiveDesktopCapture.BuildTargets(new[]{Monitor("A",new(0,0,5,5))},new[]{bad});}catch(ArgumentException){rejected=true;}Check(rejected,"invalid ROI is rejected before GPU work");}
        bool duplicate=false;try{LiveDesktopCapture.BuildTargets(new[]{Monitor("a",new(0,0,5,5)),Monitor("A",new(5,0,5,5))},Array.Empty<PixelBounds>());}catch(ArgumentException){duplicate=true;}Check(duplicate,"duplicate device names are case insensitive");
        var originals=new uint[]{0x00100001,0x00200002,0x00300003,0x00400004,0x00500005,0x00600006};
        var rotated=new Dictionary<int,(int W,int H,int[] Values)>{[0]=(3,2,new[]{1,2,3,4,5,6}),[90]=(2,3,new[]{4,1,5,2,6,3}),[180]=(3,2,new[]{6,5,4,3,2,1}),[270]=(2,3,new[]{3,6,2,5,1,4})};
        foreach(var (angle,expected) in rotated){
         var output=new PixelBounds(-600,300,expected.W,expected.H);
         for(int top=0;top<expected.H;top++)for(int left=0;left<expected.W;left++)for(int height=1;height<=expected.H-top;height++)for(int width=1;width<=expected.W-left;width++){
          var roi=new PixelBounds(output.Left+left,output.Top+top,width,height);
          var source=LiveDesktopCapture.SourceRegion(output,roi,3,2,angle);
          Check(source.Left>=0&&source.Top>=0&&source.Right<=3&&source.Bottom<=2,"rotated source crop fits the unrotated GPU texture");
          int pitch=source.Width*4+8;var bytes=new byte[pitch*source.Height];Array.Fill(bytes,(byte)0xEE);
          for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++)BitConverter.GetBytes(originals[(y+source.Top)*3+x+source.Left]).CopyTo(bytes,y*pitch+x*4);
          nint pointer=Marshal.AllocHGlobal(bytes.Length);try{
           Marshal.Copy(bytes,0,pointer,bytes.Length);
           using var bitmap=LiveDesktopCapture.CopyRotatedRoi(pointer,pitch,source.Width,source.Height,roi,angle,CancellationToken.None);
           Check(bitmap.Width==width&&bitmap.Height==height&&bitmap.ColorType==SKColorType.Bgra8888,"ROI has exact dimensions and raw BGRA storage");
           for(int y=0;y<height;y++)for(int x=0;x<width;x++){
            var pixel=bitmap.GetPixel(x,y);int id=expected.Values[(y+top)*expected.W+x+left];
            Check(pixel.Blue==id&&pixel.Red==id*16&&pixel.Alpha==255,"ROI pixels match the explicit rotated desktop, including padded rows");
           }
          }finally{Marshal.FreeHGlobal(pointer);}
         }
        }
        // Exercise cancellation and validation only against owned synthetic bytes.
        var one=Marshal.AllocHGlobal(4);try{
         bool cancelled=false;using var cancel=new CancellationTokenSource();cancel.Cancel();
         try{using var unused=LiveDesktopCapture.CopyRotatedRoi(one,4,1,1,new(0,0,1,1),0,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"ROI copying observes cancellation");
         bool shortPitch=false;try{using var unused=LiveDesktopCapture.CopyRotatedRoi(one,3,1,1,new(0,0,1,1),0,CancellationToken.None);}catch(ArgumentException){shortPitch=true;}Check(shortPitch,"short stride is rejected before reading pixels");
        }finally{Marshal.FreeHGlobal(one);}
        // An empty-target worker never creates DXGI or calls desktop acquisition.
        using var service=new LiveDesktopCapture();int notifications=0;service.FrameReady+=()=>notifications++;
        Check(service.UpdateTargets(Array.Empty<MonitorInfo>(),Array.Empty<PixelBounds>(),0)==0,"unchanged empty configuration preserves its version");
        Check(!service.TryGetLatest(out _),"an idle worker cannot return a frame");
        var shareSource=new SKBitmap(1,1,SKColorType.Bgra8888,SKAlphaType.Opaque);shareSource.Erase(SKColors.Green);shareSource.SetImmutable();using(var shared=LiveDesktopCapture.SharePixels(shareSource)){Check(shared.GetPixels()==shareSource.GetPixels()&&shared.IsImmutable,"static outputs share immutable pixel storage without a deep copy");shareSource.Dispose();Check(shared.GetPixel(0,0)==SKColors.Green,"independent bitmap wrapper owns a retained native pixel reference");}
        var owned=new SKBitmap(1,1,SKColorType.Bgra8888,SKAlphaType.Opaque);owned.Erase(SKColors.Crimson);
        var frame=new LiveDesktopFrame(42,new[]{new LiveDesktopOutputFrame("synthetic",new(1,2,1,1),owned,100,0,false)});
        typeof(LiveDesktopCapture).GetField("_latest",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(service,frame);
        Check(service.TryGetLatest(out var claimed)&&ReferenceEquals(claimed,frame),"TryGetLatest transfers exclusive frame ownership");
        Check(!service.TryGetLatest(out _),"a claimed frame is never returned twice");
        Check(frame.TargetVersion==42&&frame.GeneratedAtTimestamp>0&&frame.Outputs[0].LastPresentTimestamp==100,"assembly time is distinct from actual output presentation time");
        owned.SetImmutable();using var image=SKImage.FromBitmap(owned);frame.Dispose();frame.Dispose();
        using var canvasBitmap=new SKBitmap(1,1);using(var canvas=new SKCanvas(canvasBitmap))canvas.DrawImage(image,0,0,new SKSamplingOptions(SKFilterMode.Nearest));
        Check(canvasBitmap.GetPixel(0,0)==SKColors.Crimson,"SKImage retains immutable pixels after the owning frame is disposed");
        var pendingBitmap=new SKBitmap(1,1);var pending=new LiveDesktopFrame(0,new[]{new LiveDesktopOutputFrame("synthetic",new(0,0,1,1),pendingBitmap,0,0,false)});
        typeof(LiveDesktopCapture).GetField("_latest",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(service,pending);
        Check(service.UpdateTargets(Array.Empty<MonitorInfo>(),Array.Empty<PixelBounds>(),Stopwatch.GetTimestamp())==1&&pendingBitmap.Handle==0,"changing the epoch invalidates unclaimed pixels atomically");
        service.Dispose();Check(service.Completion.Wait(TimeSpan.FromSeconds(2)),"idle worker shutdown is bounded and completes cleanup");
        Check(notifications==0,"pure checks never capture or publish desktop pixels");
        bool disposed=false;try{service.TryGetLatest(out _);}catch(ObjectDisposedException){disposed=true;}Check(disposed,"closed capture rejects further frame access");


    }
}
