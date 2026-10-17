using System.Reflection;
using FloatCanvas.App;
using Wpf = System.Windows;
using Imaging = System.Windows.Media.Imaging;

internal static class AppIconTests
{
    public static void Run(Action<string, bool> assert)
    {
        using var stream = Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.ico", UriKind.Relative))!.Stream;
        var decoder = new Imaging.IconBitmapDecoder(stream, Imaging.BitmapCreateOptions.PreservePixelFormat, Imaging.BitmapCacheOption.OnLoad);
        assert("application ICO embeds all seven Windows sizes", decoder.Frames.Select(frame => frame.PixelWidth).Order().SequenceEqual([16, 24, 32, 48, 64, 128, 256]));

        using var pngStream = Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.png", UriKind.Relative))!.Stream;
        var master = Imaging.BitmapFrame.Create(pngStream, Imaging.BitmapCreateOptions.PreservePixelFormat, Imaging.BitmapCacheOption.OnLoad);
        var masterArtwork = MeasureArtwork(master);
        assert("adopted transparent PNG retains a high-resolution square canvas", master.PixelWidth >= 1024 && master.PixelWidth == master.PixelHeight && master.Format.BitsPerPixel == 32);
        assert("PNG artwork fills at least one canvas axis without an added safety margin", FillsAxis(master, masterArtwork) && masterArtwork.Visible && masterArtwork.TransparentCorners);
        var artworkAspect = (double)masterArtwork.Width / masterArtwork.Height;
        foreach (var frame in decoder.Frames)
        {
            var artwork = MeasureArtwork(frame);
            // Resampling can discard or spread faint contour pixels by up to two pixels per edge.
            // The destination remains uniformly scaled; alpha bounds need a four-pixel allowance.
            bool proportional = Math.Abs(artwork.Width - artwork.Height * artworkAspect) <= 4;
            assert($"{frame.PixelWidth}px ICO fills a canvas axis with proportional, transparent artwork", frame.PixelWidth == frame.PixelHeight &&
                FillsAxis(frame, artwork) && artwork.Visible && artwork.TransparentCorners && proportional);
        }

        var windowImage = AppIcon.WindowImage;
        assert("window icon uses the largest embedded frame and can be shared safely", windowImage.PixelWidth == 256 && windowImage.PixelHeight == 256 &&
            windowImage.IsFrozen && ReferenceEquals(windowImage, AppIcon.WindowImage));
        using var firstTray = AppIcon.CreateTrayIcon();
        using var secondTray = AppIcon.CreateTrayIcon();
        firstTray.Dispose();
        using var trayBitmap = secondTray.ToBitmap();
        assert("tray icon loads from application resources and owns an independent native handle", trayBitmap.Width >= 16 && trayBitmap.Height >= 16 && secondTray.Handle != 0);

        var toolbarImage = AppIcon.ToolbarImage;
        assert("toolbar shares the complete, frozen PNG canvas without an additional crop", toolbarImage.PixelWidth == master.PixelWidth && toolbarImage.PixelHeight == master.PixelHeight &&
            toolbarImage.IsFrozen && ReferenceEquals(toolbarImage, AppIcon.ToolbarImage) && MeasureArtwork(toolbarImage) == masterArtwork);
        assert("application assembly identifies Heptayo as company", typeof(AppIcon).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company == "Heptayo");
    }

    private static bool FillsAxis(Imaging.BitmapSource image, ArtworkBounds artwork) =>
        artwork.Width == image.PixelWidth || artwork.Height == image.PixelHeight;

    private static ArtworkBounds MeasureArtwork(Imaging.BitmapSource image)
    {
        var pixels = new Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var bytes = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
        pixels.CopyPixels(bytes, pixels.PixelWidth * 4, 0);
        int left = pixels.PixelWidth, top = pixels.PixelHeight, right = -1, bottom = -1;
        bool visible = false;
        for (int y = 0; y < pixels.PixelHeight; y++)
            for (int x = 0; x < pixels.PixelWidth; x++)
            {
                byte alpha = bytes[(y * pixels.PixelWidth + x) * 4 + 3];
                if (alpha == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                visible |= alpha > 128;
            }
        bool transparentCorners = bytes[3] == 0 && bytes[(pixels.PixelWidth - 1) * 4 + 3] == 0 &&
            bytes[((pixels.PixelHeight - 1) * pixels.PixelWidth) * 4 + 3] == 0 && bytes[^1] == 0;
        return new ArtworkBounds(left, top, Math.Max(0, right - left + 1), Math.Max(0, bottom - top + 1), visible, transparentCorners);
    }

    private readonly record struct ArtworkBounds(int Left, int Top, int Width, int Height, bool Visible, bool TransparentCorners);
}
