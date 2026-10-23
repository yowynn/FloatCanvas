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
            assert($"{frame.PixelWidth}px ICO preserves the full source canvas with proportional, transparent artwork", frame.PixelWidth == frame.PixelHeight &&
                SameCanvasPlacement(master, frame) && artwork.Visible && artwork.TransparentCorners && proportional);
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
        var hidden=AppIcon.HiddenToolbarImage;
        var hiddenArtwork=MeasureArtwork(hidden);
        Console.WriteLine($"Mascot artwork bounds: open={masterArtwork.Width}x{masterArtwork.Height}/{master.PixelWidth}; closed={hiddenArtwork.Width}x{hiddenArtwork.Height}/{hidden.PixelWidth}");
        assert("closed-eye mascot keeps the same high-resolution transparent square dimensions as the open master",hidden.PixelWidth>=1024 && hidden.PixelWidth==master.PixelWidth && hidden.PixelHeight==master.PixelHeight && hidden.PixelWidth==hidden.PixelHeight && hidden.IsFrozen && hiddenArtwork.Visible && hiddenArtwork.TransparentCorners);
        VerifyStatePair(master, hidden, decoder, assert);
        assert("closed-eye toolbar shares its complete frozen master",ReferenceEquals(hidden,AppIcon.HiddenToolbarImage));
        assert("hidden taskbar icon uses a frozen largest frame",AppIcon.HiddenWindowImage is {PixelWidth:256,IsFrozen:true});
        using var hiddenIconStream=Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.Hidden.ico",UriKind.Relative))!.Stream;
        var hiddenDecoder=new Imaging.IconBitmapDecoder(hiddenIconStream,Imaging.BitmapCreateOptions.PreservePixelFormat,Imaging.BitmapCacheOption.OnLoad);
        assert("closed-eye ICO embeds all seven Windows sizes preserving the full source canvas position",hiddenDecoder.Frames.Select(frame=>frame.PixelWidth).Order().SequenceEqual([16,24,32,48,64,128,256]) && hiddenDecoder.Frames.All(frame=>
        {
            var artwork=MeasureArtwork(frame);
            var visible=MeasureArtwork(frame,128);var masterVisible=MeasureArtwork(hidden,128);
            double scale=(double)frame.PixelWidth/hidden.PixelWidth;
            return artwork.Visible&&artwork.TransparentCorners&&Math.Abs(visible.Left-masterVisible.Left*scale)<=2&&Math.Abs(visible.Top-masterVisible.Top*scale)<=2&&
                Math.Abs(visible.Width-masterVisible.Width*scale)<=3&&Math.Abs(visible.Height-masterVisible.Height*scale)<=3;
        }));
        using var hiddenTray=AppIcon.CreateTrayIcon(true);
        assert("hidden tray icon owns a native handle independently",hiddenTray.Handle!=0&&hiddenTray.Handle!=secondTray.Handle);
        assert("application assembly identifies Heptayo as company", typeof(AppIcon).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company == "Heptayo");
        VerifyPassThroughPair(assert);
    }

    private static void VerifyPassThroughPair(Action<string, bool> assert)
    {
        var open = AppIcon.PassThroughToolbarImage; var closed = AppIcon.HiddenPassThroughToolbarImage;
        assert("held-crown states share a transparent square canvas", open.PixelWidth == closed.PixelWidth && open.PixelHeight == closed.PixelHeight &&
            open.PixelWidth == open.PixelHeight && MeasureArtwork(open).TransparentCorners && MeasureArtwork(closed).TransparentCorners);
        var mask = EyeMask(open.PixelWidth, open.PixelHeight);
        var diff = StateDifferences(open, closed, mask);
        assert("held-crown visibility states change only eyes, retaining every other BGRA and alpha byte", diff.Changed > 0 && diff.Outside == 0 && diff.Alpha == 0);
        assert("held-crown states use the original master's canvas and pixel registration", open.PixelWidth == AppIcon.ToolbarImage.PixelWidth && open.PixelHeight == AppIcon.ToolbarImage.PixelHeight &&
            open.DpiX == AppIcon.ToolbarImage.DpiX && open.DpiY == AppIcon.ToolbarImage.DpiY);
        var gestureMask = new bool[mask.Length];
        for (int y = 0; y < open.PixelHeight; y++) for (int x = 0; x < open.PixelWidth; x++)
            gestureMask[y * open.PixelWidth + x] = x >= 530 && y <= 535 || x >= 245 && x <= 985 && y >= 645 && y <= 1110;
        var gestureDiff = StateDifferences(AppIcon.ToolbarImage, open, gestureMask);
        Console.WriteLine($"Held-crown pose diff: {gestureDiff}");
        assert("new pose preserves all original BGRA pixels outside crown, hands and pen restoration areas", gestureDiff.Changed > 0 && gestureDiff.Outside == 0);
        var oldPixels = Pixels(AppIcon.ToolbarImage); var oldClosedPixels = Pixels(AppIcon.HiddenToolbarImage); var newPixels = Pixels(open);
        var capEdges = Enumerable.Range(460, 51).Select(y => Enumerable.Range(940, 121).Last(x => newPixels[(y * open.PixelWidth + x) * 4 + 3] >= 128)).ToArray();
        assert("restored head crown boundary joins the original side hair without an alpha step", capEdges.Zip(capEdges.Skip(1)).All(pair => Math.Abs(pair.First - pair.Second) <= 2));
        assert("held-crown open eyes retain the original eye artwork exactly", Enumerable.Range(0, mask.Length).Where(i => !oldPixels.AsSpan(i * 4, 4).SequenceEqual(oldClosedPixels.AsSpan(i * 4, 4))).All(i =>
            oldPixels.AsSpan(i * 4, 4).SequenceEqual(newPixels.AsSpan(i * 4, 4))));
        VerifyProtectedDetails(AppIcon.ToolbarImage, open, assert);
        using var openIconStream = Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.PassThrough.ico", UriKind.Relative))!.Stream;
        using var closedIconStream = Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.PassThrough.Hidden.ico", UriKind.Relative))!.Stream;
        var openIcons = new Imaging.IconBitmapDecoder(openIconStream, Imaging.BitmapCreateOptions.PreservePixelFormat, Imaging.BitmapCacheOption.OnLoad);
        var closedIcons = new Imaging.IconBitmapDecoder(closedIconStream, Imaging.BitmapCreateOptions.PreservePixelFormat, Imaging.BitmapCacheOption.OnLoad);
        assert("held-crown ICO pair includes all seven matching Windows sizes", openIcons.Frames.Select(f => f.PixelWidth).Order().SequenceEqual([16,24,32,48,64,128,256]) &&
            closedIcons.Frames.Select(f => f.PixelWidth).Order().SequenceEqual([16,24,32,48,64,128,256]));
        foreach (var frame in openIcons.Frames)
        {
            var counterpart = closedIcons.Frames.Single(f => f.PixelWidth == frame.PixelWidth);
            var iconDiff = StateDifferences(frame, counterpart, ScaleEyeMask(mask, open.PixelWidth, open.PixelHeight, frame.PixelWidth, 2));
            assert($"held-crown {frame.PixelWidth}px ICOs change only filtered eye pixels", iconDiff.Alpha == 0 && iconDiff.Outside == 0 &&
                SameCanvasPlacement(open, frame) && SameCanvasPlacement(closed, counterpart));
        }
        VerifyLauncherRendering(mask, open.PixelWidth, open.PixelHeight, assert, editing: false);
        var launcher = new ToolbarLauncher(editing: false);
        var image = (System.Windows.Controls.Image)launcher.Content;
        assert("pass-through launcher displays the held-crown open state", ReferenceEquals(image.Source, open));
        launcher.SetCanvasVisible(false);
        assert("pass-through visibility swaps only the image source", ReferenceEquals(image, launcher.Content) && ReferenceEquals(image.Source, closed));
        foreach (bool hidden in new[] { false, true })
        {
            using var icon = AppIcon.CreateTrayIcon(hidden, passThrough: true);
            assert("held-crown tray resource is available for hidden=" + hidden, icon.Handle != 0);
        }
    }

    private static void VerifyStatePair(Imaging.BitmapSource open, Imaging.BitmapSource hidden,
        Imaging.IconBitmapDecoder openIcons, Action<string, bool> assert)
    {
        int width = open.PixelWidth, height = open.PixelHeight;
        assert("mascot states use the identical source canvas", hidden.PixelWidth == width && hidden.PixelHeight == height);
        Console.WriteLine($"Mascot source metadata: open DPI={open.DpiX:0.#####}x{open.DpiY:0.#####}, format={open.Format}, ColorContexts={ColorContexts(open)}; hidden DPI={hidden.DpiX:0.#####}x{hidden.DpiY:0.#####}, format={hidden.Format}, ColorContexts={ColorContexts(hidden)}");
        var eyeMask = EyeMask(width, height);
        var diff = StateDifferences(open, hidden, eyeMask);
        Console.WriteLine($"Mascot master state diff: changed={diff.Changed}; alpha={diff.Alpha}; outside eyes BGRA={diff.Outside}; transparent RGB outside eyes={diff.TransparentRgb}");
        assert("closed-eye state contains an actual eye-local pixel edit", diff.Changed > 0);
        assert("closed-eye master preserves every original alpha byte", diff.Alpha == 0);
        VerifyProtectedDetails(open, hidden, assert);
        assert("closed-eye master preserves every BGRA byte outside the two eye regions, including transparent RGB", diff.Outside == 0);

        using var stream = Wpf.Application.GetResourceStream(new Uri("/FloatCanvas;component/Assets/FloatCanvas.Hidden.ico", UriKind.Relative))!.Stream;
        var hiddenIcons = new Imaging.IconBitmapDecoder(stream, Imaging.BitmapCreateOptions.PreservePixelFormat, Imaging.BitmapCacheOption.OnLoad);
        foreach (var frame in openIcons.Frames)
        {
            int size = frame.PixelWidth;
            var hiddenFrame = hiddenIcons.Frames.Single(candidate => candidate.PixelWidth == size);
            // Both states must use the same full-canvas sampler. Bicubic reduction can
            // spread changed eye RGB to at most two neighboring destination pixels.
            var iconMask = ScaleEyeMask(eyeMask, width, height, size, 2);
            var iconDiff = StateDifferences(frame, hiddenFrame, iconMask);
            Console.WriteLine($"Mascot {size}px ICO state diff: alpha={iconDiff.Alpha}; outside filtered eye support BGRA={iconDiff.Outside}");
            assert($"{size}px state ICOs retain identical alpha and all pixels outside filtered eye support", iconDiff.Alpha == 0 && iconDiff.Outside == 0);
        }
        VerifyLauncherRendering(eyeMask, width, height, assert);
    }

    private static void VerifyProtectedDetails(Imaging.BitmapSource open, Imaging.BitmapSource hidden, Action<string, bool> assert)
    {
        var a = Pixels(open); var b = Pixels(hidden);
        (string Name, (int X, int Y)[] Polygon)[] details =
        [
            ("left blush", [(388,663),(400,654),(415,659),(454,678),(446,700),(400,690)]),
            ("right blush", [(689,792),(704,782),(756,798),(750,822),(701,816)]),
            ("mouth", [(530,715),(625,715),(625,770),(530,770)])
        ];
        foreach (var (name, polygon) in details)
        {
            int differences = 0;
            for (int y = 0; y < open.PixelHeight; y++) for (int x = 0; x < open.PixelWidth; x++)
            {
                if (!InsidePolygon((x + .5) * 1210d / open.PixelWidth, (y + .5) * 1210d / open.PixelHeight, polygon, false)) continue;
                int offset = (y * open.PixelWidth + x) * 4;
                if (!a.AsSpan(offset, 4).SequenceEqual(b.AsSpan(offset, 4))) differences++;
            }
            Console.WriteLine($"Mascot protected {name} BGRA differences={differences}");
            assert($"closed-eye master preserves the original {name} pixels exactly", differences == 0);
        }
    }

    private static string ColorContexts(Imaging.BitmapSource image)
    {
        var contexts = (image as Imaging.BitmapFrame)?.ColorContexts;
        return $"{contexts?.Count ?? 0}:" + string.Join(";", contexts?.Select(context => context.ProfileUri?.ToString() ?? "embedded") ?? []);
    }

    private static void VerifyLauncherRendering(bool[] masterMask, int width, int height, Action<string, bool> assert, bool editing = true)
    {
        var launcher = new ToolbarLauncher(editing);
        var image = (System.Windows.Controls.Image)launcher.Content;
        var surface = new System.Windows.Controls.Grid { Width = 100, Height = 100 };
        surface.Children.Add(launcher);
        foreach (var dpi in new[] { 96d, 144d, 192d })
        foreach (var background in new[] { System.Windows.Media.Brushes.White, System.Windows.Media.Brushes.Black })
        {
            surface.Background = background;
            launcher.SetCanvasVisible(true);
            var before = Render();
            var desired = launcher.DesiredSize; var rendered = image.RenderSize;
            launcher.SetCanvasVisible(false);
            var after = Render();
            int size = before.PixelWidth;
            var diff = StateDifferences(before, after, ScaleEyeMask(masterMask, width, height, size, 2));
            Console.WriteLine($"Mascot same-Image WPF render {size}px/{dpi}DPI/{background}: alpha={diff.Alpha}; outside filtered eyes BGRA={diff.Outside}; layout={image.RenderSize}");
            assert($"{dpi}DPI launcher source swap preserves layout and all rendered pixels outside eye-filter support on {background}",
                ReferenceEquals(image, launcher.Content) && desired == launcher.DesiredSize && rendered == image.RenderSize && diff.Alpha == 0 && diff.Outside == 0);

            Imaging.RenderTargetBitmap Render()
            {
                surface.Measure(new Wpf.Size(100, 100));
                surface.Arrange(new Wpf.Rect(0, 0, 100, 100)); surface.UpdateLayout();
                int pixels = (int)Math.Round(dpi * 100 / 96);
                var bitmap = new Imaging.RenderTargetBitmap(pixels, pixels, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(surface); bitmap.Freeze(); return bitmap;
            }
        }
    }

    private static bool[] EyeMask(int width, int height)
    {
        // Explicit local eye outlines in the adopted 1210-square master. Neither
        // polygon reaches the crown, mouth, gloves, pen or pillow.
        (int X, int Y)[] left = [(374,533),(412,516),(451,499),(505,502),(553,530),(577,580),(573,641),(546,681),(480,697),(416,674),(384,620)];
        (int X, int Y)[] right = [(681,674),(711,637),(760,620),(809,637),(840,678),(865,732),(862,780),(822,807),(755,807),(701,780),(675,733)];
        var mask = new bool[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double mx = (x + .5) * 1210d / width, my = (y + .5) * 1210d / height;
            mask[y * width + x] = y * 1210d / height <= 693 && InsidePolygon(mx, my, left) ||
                x * 1210d / width <= 861 && y * 1210d / height <= 802 && InsidePolygon(mx, my, right);
        }
        return mask;
    }

    private static bool InsidePolygon(double x, double y, (int X, int Y)[] polygon, bool includeRasterBoundary = true)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            // Include the one-pixel rasterized boundary of each explicit polygon.
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            if (includeRasterBoundary && Math.Abs(x - a.X - t * dx) <= .75 && Math.Abs(y - a.Y - t * dy) <= .75) return true;
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static bool[] ScaleEyeMask(bool[] master, int width, int height, int size, int support)
    {
        var result = new bool[size * size];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (!master[y * width + x]) continue;
            int tx = x * size / width, ty = y * size / height;
            for (int yy = Math.Max(0, ty - support); yy <= Math.Min(size - 1, ty + support); yy++)
                for (int xx = Math.Max(0, tx - support); xx <= Math.Min(size - 1, tx + support); xx++) result[yy * size + xx] = true;
        }
        return result;
    }

    private static (int Alpha, int Outside, int TransparentRgb, int Changed) StateDifferences(Imaging.BitmapSource open, Imaging.BitmapSource hidden, bool[] allowed)
    {
        byte[] a = Pixels(open), b = Pixels(hidden);
        int alpha = 0, outside = 0, transparent = 0, changed = 0;
        for (int pixel = 0, offset = 0; pixel < allowed.Length; pixel++, offset += 4)
        {
            if (a[offset + 3] != b[offset + 3]) alpha++;
            if (a.AsSpan(offset, 4).SequenceEqual(b.AsSpan(offset, 4))) continue;
            changed++;
            if (allowed[pixel]) continue;
            outside++;
            if (a[offset + 3] == 0 && b[offset + 3] == 0) transparent++;
        }
        return (alpha, outside, transparent, changed);
    }

    private static byte[] Pixels(Imaging.BitmapSource image)
    {
        var converted = new Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static bool SameCanvasPlacement(Imaging.BitmapSource master, Imaging.BitmapSource frame)
    {
        var original = MeasureArtwork(master, 128); var actual = MeasureArtwork(frame, 128);
        double scale = (double)frame.PixelWidth / master.PixelWidth;
        return Math.Abs(actual.Left - original.Left * scale) <= 2 && Math.Abs(actual.Top - original.Top * scale) <= 2 &&
            Math.Abs(actual.Width - original.Width * scale) <= 3 && Math.Abs(actual.Height - original.Height * scale) <= 3;
    }

    private static bool FillsAxis(Imaging.BitmapSource image, ArtworkBounds artwork) =>
        artwork.Width == image.PixelWidth || artwork.Height == image.PixelHeight;

    private static ArtworkBounds MeasureArtwork(Imaging.BitmapSource image,byte alphaThreshold=1)
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
                if (alpha < alphaThreshold) continue;
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
