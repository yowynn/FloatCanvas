using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatCanvas.App;

internal static class EyedropperCursor
{
    private static nint cursor;
    internal static nint Handle => cursor != 0 ? cursor : cursor = Create();
    internal static BitmapSource Preview()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var tube = Geometry.Parse("M5,22 L19,8 L24,13 L10,27 L3,29 Z");
            drawing.DrawGeometry(Brushes.White,new Pen(Brushes.White,4),tube);
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(198,241,250)),new Pen(new SolidColorBrush(Color.FromRgb(34,52,73)),1.6),tube);
            drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(27,157,202)),3),new Point(7,23),new Point(16,14));
            var grip=Geometry.Parse("M16,7 L21,2 Q23,0 25,2 L30,7 Q32,9 30,11 L25,16 Z");
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(113,81,198)),new Pen(Brushes.White,3),grip);
            drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(34,52,73)),1.5),new Point(16,7),new Point(25,16));
        }
        var bitmap = new RenderTargetBitmap(32,32,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        bitmap.Freeze(); return bitmap;
    }
    private static nint Create()
    {
        var bitmap=Preview();
        var pixels = new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
        byte[] bytes = new byte[32*32*4]; pixels.CopyPixels(bytes,32*4,0);
        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)3); writer.Write((ushort)29);
            writer.Write(40); writer.Write(32); writer.Write(64); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(bytes.Length+128); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for(int y=31;y>=0;y--)writer.Write(bytes,y*32*4,32*4);
            for(int y=31;y>=0;y--)for(int block=0;block<4;block++)
            {
                byte mask=0;
                for(int bit=0;bit<8;bit++)if(bytes[(y*32+block*8+bit)*4+3]==0)mask|=(byte)(0x80>>bit);
                writer.Write(mask);
            }
        }
        byte[] resource=file.ToArray();
        var handle=CreateIconFromResourceEx(resource,(uint)resource.Length,false,0x00030000,32,32,0);
        if(handle==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern nint CreateIconFromResourceEx(byte[] data,uint length,[MarshalAs(UnmanagedType.Bool)]bool icon,uint version,int width,int height,uint flags);
}
