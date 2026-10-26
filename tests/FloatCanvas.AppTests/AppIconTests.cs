using System.Reflection;
using System.Security.Cryptography;
using FloatCanvas.App;
using Wpf = System.Windows;
using Imaging = System.Windows.Media.Imaging;

internal static class AppIconTests
{
    private static readonly int[] Sizes = [16,24,32,48,64,128,256];
    public static void Run(Action<string, bool> assert)
    {
        (string File,string Hash,Imaging.BitmapSource Image)[] masters =
        [
            ("FloatCanvas.png","84BDDB3E890BDB5A13F47261CAD5AD76FB7FFCC2B6726F070D85BFDC1F592778",AppIcon.ToolbarImage),
            ("FloatCanvas.Hidden.png","0C987FF44B2F3F7A9A0F4604E55562E4DBAFB492F7C36065CBDFF9FA3538E544",AppIcon.HiddenToolbarImage),
            ("FloatCanvas.PassThrough.png","FAE380141E603EE310B548203786FA1180961E3F43AEDE68BA7B0DCE54E55202",AppIcon.PassThroughToolbarImage),
            ("FloatCanvas.PassThrough.Hidden.png","CEEC1518A1FE6B23F10948E9971ADD16F6D6C1B7FF18762165FC6773AE296B16",AppIcon.HiddenPassThroughToolbarImage)
        ];
        foreach(var (file,hash,image) in masters)
        {
            using var source=Resource(file);
            assert(file+" is the approved v8 source without re-encoding or cropping",Convert.ToHexString(SHA256.HashData(source))==hash);
            assert(file+" keeps its frozen 1024 RGBA canvas and transparent margin",image is {PixelWidth:1024,PixelHeight:1024,IsFrozen:true}&&image.Format.BitsPerPixel==32&&Bounds(image)==(2,2,1021,1019));
        }
        var eyes=Mask((322,430,482,571),(566,514,713,657));
        foreach(bool editing in new[]{true,false})
        {
            var diff=Differences(AppIcon.LauncherImage(true,editing),AppIcon.LauncherImage(false,editing),eyes);
            assert($"mode {editing} visibility changes only eyes and keeps every alpha byte",diff.Changed>0&&diff.Outside==0&&diff.Alpha==0);
            VerifyRendering(assert,eyes,editing);
            VerifyDownsampling(assert,editing);
        }
        foreach(bool visible in new[]{true,false})
        {
            var diff=Differences(AppIcon.LauncherImage(visible,true),AppIcon.LauncherImage(visible,false),Mask((290,380,760,690)));
            assert($"visibility {visible} mode change is confined to glasses with a shared silhouette",diff.Changed>0&&diff.Outside==0&&diff.Alpha==0);
        }
        foreach(bool hidden in new[]{false,true})
        {
            string suffix=hidden?".Hidden":"";var shell=Icons("FloatCanvas"+suffix+".ico");var passive=Icons("FloatCanvas.PassThrough"+suffix+".ico");
            var master=hidden?AppIcon.HiddenPassThroughToolbarImage:AppIcon.PassThroughToolbarImage;
            assert($"shell state {hidden} embeds all seven Windows sizes",shell.Frames.Select(f=>f.PixelWidth).Order().SequenceEqual(Sizes));
            foreach(var frame in shell.Frames)
                assert($"shell {hidden}/{frame.PixelWidth}px uses the no-glasses frame and original placement",Pixels(frame).SequenceEqual(Pixels(passive.Frames.Single(f=>f.PixelWidth==frame.PixelWidth)))&&SamePlacement(master,frame));
            var window=hidden?AppIcon.HiddenWindowImage:AppIcon.WindowImage;
            assert($"window state {hidden} uses a frozen no-glasses 256px frame",window.IsFrozen&&window.PixelWidth==256&&Pixels(window).SequenceEqual(Pixels(passive.Frames.MaxBy(f=>f.PixelWidth)!)));
            using var actualIcon=AppIcon.CreateTrayIcon(hidden);using var stream=Resource("FloatCanvas.PassThrough"+suffix+".ico");
            using var expectedIcon=new System.Drawing.Icon(stream,System.Windows.Forms.SystemInformation.SmallIconSize);
            using var actual=actualIcon.ToBitmap();using var expected=expectedIcon.ToBitmap();
            assert($"tray state {hidden} uses no glasses",actual.Size==expected.Size&&Enumerable.Range(0,actual.Width).All(x=>Enumerable.Range(0,actual.Height).All(y=>actual.GetPixel(x,y)==expected.GetPixel(x,y))));
        }
        var openIcons=Icons("FloatCanvas.ico");var closedIcons=Icons("FloatCanvas.Hidden.ico");
        foreach(var frame in openIcons.Frames)
        {
            var diff=Differences(frame,closedIcons.Frames.Single(f=>f.PixelWidth==frame.PixelWidth),ScaleMask(eyes,frame.PixelWidth));
            assert($"{frame.PixelWidth}px shell visibility retains alpha and pixels outside filtered eyes",diff.Alpha==0&&diff.Outside==0);
        }
        using var first=AppIcon.CreateTrayIcon();using var second=AppIcon.CreateTrayIcon();first.Dispose();
        assert("tray callers own independent native icon handles",second.Handle!=0);
        assert("application assembly identifies Heptayo as company",typeof(AppIcon).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company=="Heptayo");
    }
    private static void VerifyRendering(Action<string,bool> assert,bool[] eyes,bool editing)
    {
        var launcher=new ToolbarLauncher(editing);var image=(System.Windows.Controls.Image)launcher.Content;double size=ToolbarLauncher.ArtworkSize;
        var surface=new System.Windows.Controls.Grid {Width=size,Height=size};surface.Children.Add(launcher);
        foreach(double dpi in new[]{96d,144d,192d})foreach(var background in new[]{System.Windows.Media.Brushes.White,System.Windows.Media.Brushes.Black})
        {
            surface.Background=background;launcher.SetCanvasVisible(true);var before=Render();var desired=launcher.DesiredSize;var rendered=image.RenderSize;
            launcher.SetCanvasVisible(false);var after=Render();var diff=Differences(before,after,ScaleMask(eyes,before.PixelWidth));
            assert($"mode {editing}/{dpi} DPI source swap keeps layout and non-eye pixels",ReferenceEquals(image,launcher.Content)&&desired==launcher.DesiredSize&&rendered==image.RenderSize&&diff.Alpha==0&&diff.Outside==0);
            Imaging.RenderTargetBitmap Render()
            {
                surface.Measure(new Wpf.Size(size,size));surface.Arrange(new Wpf.Rect(0,0,size,size));surface.UpdateLayout();int pixels=(int)Math.Round(dpi*size/96);
                var bitmap=new Imaging.RenderTargetBitmap(pixels,pixels,dpi,dpi,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(surface);return bitmap;
            }
        }
    }
    private static void VerifyDownsampling(Action<string,bool> assert,bool editing)
    {
        var launcher=new ToolbarLauncher(editing);
        foreach(bool visible in new[]{true,false})foreach(double scale in new[]{1d,1.25,1.5,2d})
        {
            launcher.SetCanvasVisible(visible);
            var source=(Imaging.BitmapSource)((System.Windows.Controls.Image)launcher.Content).Source;
            var premultiplied=new Imaging.FormatConvertedBitmap(source,System.Windows.Media.PixelFormats.Pbgra32,null,0);
            var original=new byte[source.PixelWidth*source.PixelHeight*4];premultiplied.CopyPixels(original,source.PixelWidth*4,0);
            double extent=ToolbarLauncher.ArtworkSize*scale,step=source.PixelWidth/extent;int size=(int)Math.Ceiling(extent);
            launcher.Measure(new Wpf.Size(ToolbarLauncher.ArtworkSize,ToolbarLauncher.ArtworkSize));
            launcher.Arrange(new Wpf.Rect(0,0,ToolbarLauncher.ArtworkSize,ToolbarLauncher.ArtworkSize));launcher.UpdateLayout();
            var rendered=new Imaging.RenderTargetBitmap(size,size,96*scale,96*scale,System.Windows.Media.PixelFormats.Pbgra32);rendered.Render(launcher);
            var actual=new byte[size*size*4];rendered.CopyPixels(actual,size*4,0);
            double error=0;int channels=0;
            // Integrate source pixel coverage independently. Linear point sampling loses
            // edge coverage when this 1024px artwork is reduced to a small launcher.
            // Only the pillow's antialiased contour contributes to this signal.
            for(int y=(int)(size*.80);y<size;y++)for(int x=0;x<size;x++)
            {
                double x0=x*step,x1=(x+1)*step,y0=y*step,y1=(y+1)*step;double[] expected=new double[4];
                for(int yy=(int)y0;yy<Math.Ceiling(y1)&&yy<source.PixelHeight;yy++)
                    for(int xx=(int)x0;xx<Math.Ceiling(x1)&&xx<source.PixelWidth;xx++)
                    {
                        double weight=(Math.Min(xx+1,x1)-Math.Max(xx,x0))*(Math.Min(yy+1,y1)-Math.Max(yy,y0));
                        for(int c=0;c<4;c++)expected[c]+=original[(yy*source.PixelWidth+xx)*4+c]*weight;
                    }
                for(int c=0;c<4;c++)expected[c]=Math.Round(expected[c]/(step*step));
                if(expected[3]<=5||expected[3]>=250)continue;
                for(int c=0;c<4;c++){error+=Math.Abs(actual[(y*size+x)*4+c]-expected[c]);channels++;}
            }
            double meanError=error/Math.Max(channels,1);
            assert($"mode {editing}/visible {visible}/{scale*100:0}% pillow contour preserves area coverage ({meanError:F2})",channels>0&&meanError<14);
        }
    }
    private static System.IO.Stream Resource(string file)=>Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/"+file,UriKind.Relative))!.Stream;
    private static Imaging.IconBitmapDecoder Icons(string file){using var stream=Resource(file);return new(stream,Imaging.BitmapCreateOptions.PreservePixelFormat,Imaging.BitmapCacheOption.OnLoad);}
    private static bool[] Mask(params (int Left,int Top,int Right,int Bottom)[] areas)
    {
        // Eye bounds include the original folds; surrounding mouth, hair and props stay protected.
        var result=new bool[1024*1024];foreach(var a in areas)for(int y=a.Top;y<=a.Bottom;y++)for(int x=a.Left;x<=a.Right;x++)result[y*1024+x]=true;return result;
    }
    private static bool[] ScaleMask(bool[] master,int size)
    {
        var result=new bool[size*size];for(int y=0;y<1024;y++)for(int x=0;x<1024;x++)if(master[y*1024+x])
        {
            int tx=x*size/1024,ty=y*size/1024;for(int yy=Math.Max(0,ty-2);yy<=Math.Min(size-1,ty+2);yy++)for(int xx=Math.Max(0,tx-2);xx<=Math.Min(size-1,tx+2);xx++)result[yy*size+xx]=true;
        }
        return result;
    }
    private static (int Alpha,int Outside,int Changed) Differences(Imaging.BitmapSource first,Imaging.BitmapSource second,bool[] allowed)
    {
        byte[] a=Pixels(first),b=Pixels(second);int alpha=0,outside=0,changed=0;for(int i=0;i<allowed.Length;i++)
        {
            int offset=i*4;if(a[offset+3]!=b[offset+3])alpha++;if(a.AsSpan(offset,4).SequenceEqual(b.AsSpan(offset,4)))continue;changed++;if(!allowed[i])outside++;
        }
        return(alpha,outside,changed);
    }
    private static byte[] Pixels(Imaging.BitmapSource image)
    {
        var bitmap=new Imaging.FormatConvertedBitmap(image,System.Windows.Media.PixelFormats.Bgra32,null,0);var bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return bytes;
    }
    private static bool SamePlacement(Imaging.BitmapSource master,Imaging.BitmapSource frame)
    {
        var a=Bounds(master,128);var b=Bounds(frame,128);double s=(double)frame.PixelWidth/master.PixelWidth;return Math.Abs(b.Left-a.Left*s)<=2&&Math.Abs(b.Top-a.Top*s)<=2&&Math.Abs(b.Width-a.Width*s)<=3&&Math.Abs(b.Height-a.Height*s)<=3;
    }
    private static (int Left,int Top,int Width,int Height) Bounds(Imaging.BitmapSource image,byte threshold=1)
    {
        byte[] bytes=Pixels(image);int left=image.PixelWidth,top=image.PixelHeight,right=-1,bottom=-1;for(int y=0;y<image.PixelHeight;y++)for(int x=0;x<image.PixelWidth;x++)if(bytes[(y*image.PixelWidth+x)*4+3]>=threshold){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}return(left,top,Math.Max(0,right-left+1),Math.Max(0,bottom-top+1));
    }
}
