using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FloatCanvas.App;

internal static class AppIcon
{
    private static readonly Uri ResourceUri = new("/FloatCanvas;component/Assets/FloatCanvas.ico", UriKind.Relative);

    public static BitmapSource WindowImage { get; } = LoadWindowImage();
    public static BitmapSource ToolbarImage { get; } = LoadToolbarImage();
    public static BitmapSource HiddenToolbarImage { get; } = LoadToolbarImage("FloatCanvas.Hidden.png");
    public static BitmapSource HiddenWindowImage { get; } = LoadWindowImage(true);

    // The caller owns the returned icon. Clone it so its resource stream can close immediately.
    public static Drawing.Icon CreateTrayIcon(bool hidden = false)
    {
        using var stream = OpenResource(hidden);
        using var icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Drawing.Icon)icon.Clone();
    }

    private static BitmapSource LoadWindowImage(bool hidden = false)
    {
        using var stream = OpenResource(hidden);
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames.MaxBy(frame => frame.PixelWidth)!;
        image.Freeze();
        return image;
    }

    private static BitmapSource LoadToolbarImage(string fileName = "FloatCanvas.png")
    {
        using var stream = Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/"+fileName, UriKind.Relative))?.Stream
            ?? throw new InvalidOperationException("The FloatCanvas toolbar artwork resource is missing.");
        var master = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        // The packaged PNG already fits the artwork's alpha bounds. Use the full
        // image here so toolbar, window and shell icons share the same framing.
        master.Freeze();
        return master;
    }

    private static Stream OpenResource(bool hidden = false) => Application.GetResourceStream(hidden ? new Uri("/FloatCanvas;component/Assets/FloatCanvas.Hidden.ico", UriKind.Relative) : ResourceUri)?.Stream
        ?? throw new InvalidOperationException("The FloatCanvas application icon resource is missing.");
}
