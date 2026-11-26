using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class WindowSourceCropGeometry
{
    internal static RectD Fit(double width, double height, double sourceWidth, double sourceHeight)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return default;
        double scale = Math.Min(width / sourceWidth, height / sourceHeight);
        return new((width - sourceWidth * scale) / 2, (height - sourceHeight * scale) / 2, sourceWidth * scale, sourceHeight * scale);
    }
    internal static bool TryDrag(RectD image, PointD first, PointD last, out RectD crop)
    {
        crop = default;
        if (image.IsEmpty || !double.IsFinite(image.X) || !double.IsFinite(image.Y) || !double.IsFinite(image.Width) || !double.IsFinite(image.Height) ||
            !double.IsFinite(first.X) || !double.IsFinite(first.Y) || !double.IsFinite(last.X) || !double.IsFinite(last.Y) ||
            first.X < image.Left || first.X > image.Right || first.Y < image.Top || first.Y > image.Bottom) return false;
        last = new(Math.Clamp(last.X, image.Left, image.Right), Math.Clamp(last.Y, image.Top, image.Bottom));
        var region = RectD.FromPoints(first, last);
        if (region.Width < 1 || region.Height < 1) return false;
        crop = new((region.X - image.X) / image.Width, (region.Y - image.Y) / image.Height, region.Width / image.Width, region.Height / image.Height);
        return true;
    }
    internal static bool IsValid(RectD crop) => double.IsFinite(crop.X) && double.IsFinite(crop.Y) && double.IsFinite(crop.Width) && double.IsFinite(crop.Height) &&
        crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0 && crop.Right <= 1 + 1e-12 && crop.Bottom <= 1 + 1e-12;
}

/// <summary>Edits one normalized ImageCrop from an owned full-source PNG. No capture, source mutation or asset insertion occurs here.</summary>
internal sealed class WindowSourceCropWindow : Window
{
    private readonly Image image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Canvas overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly System.Windows.Shapes.Rectangle selection = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly BitmapImage bitmap;
    private readonly Button apply = new() { Content = L.Get("WindowSource.ApplyCrop"), MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 96 };
    private PointD? dragStart;
    private RectD crop;
    internal bool Accepted { get; private set; }
    internal RectD? NormalizedCrop => crop == new RectD(0, 0, 1, 1) ? null : crop;

    internal WindowSourceCropWindow(byte[] fullSourcePreviewPng, RectD? initialCrop = null) : this(Decode(fullSourcePreviewPng), ValidateCrop(initialCrop)) { }
    private static BitmapImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length > 32 * 1024 * 1024 || ImageDecodeBudget.ReadPixelCount(png) > 8_000_000) throw new InvalidDataException(L.Get("WindowSource.InvalidPreview"));
        using var input = new MemoryStream(png.ToArray(), false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
    }
    private static RectD ValidateCrop(RectD? value)
    { var result = value ?? new(0, 0, 1, 1); return WindowSourceCropGeometry.IsValid(result) ? result : throw new ArgumentOutOfRangeException(nameof(value)); }
    private WindowSourceCropWindow(BitmapImage previewImage, RectD initialCrop)
    {
        bitmap = previewImage; crop = initialCrop;
        Title = L.Get("WindowSource.CropTitle"); Width = 820; Height = 600; MinWidth = 320; MinHeight = 320;
        ShowInTaskbar = false; Topmost = true; Icon = AppIcon.WindowImage; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin(16) };
        var hint = new TextBlock { Text = L.Get("WindowSource.CropHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        DockPanel.SetDock(hint, Dock.Top); body.Children.Add(hint);
        var buttons = new StackPanel { Margin = new(0, 10, 0, 0) };
        var reset = new Button { Content = L.Get("WindowSource.ResetCrop"), MinHeight = PropertyPanelLayout.ControlHeight,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 6), Padding = new(8, 4, 8, 4) };
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true, MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 96, Margin = new(0, 0, 8, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(cancel); actions.Children.Add(apply);
        buttons.Children.Add(reset); buttons.Children.Add(actions);
        DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
        image.Source = bitmap; var preview = new Grid { ClipToBounds = true }; preview.SetResourceReference(Panel.BackgroundProperty, "CanvasButton");
        preview.Children.Add(image); preview.Children.Add(overlay); overlay.Children.Add(selection); body.Children.Add(preview); Content = body;
        selection.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasAccent");
        selection.Fill = new SolidColorBrush(Color.FromArgb(35, 32, 160, 255));
        AutomationProperties.SetName(overlay, L.Get("WindowSource.CropTitle"));
        reset.Click += (_, _) => SetCrop(null); cancel.Click += (_, _) => SessionWindowCoordinator.Close(this);
        apply.Click += (_, _) => { if (WindowSourceCropGeometry.IsValid(crop)) { Accepted = true; SessionWindowCoordinator.Close(this); } };
        AppInputPolicy.AttachFormSubmit(this, apply);
        overlay.SizeChanged += (_, _) => RefreshSelection();
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            var point = e.GetPosition(overlay); var bodyRect = ImageRect();
            if (point.X < bodyRect.Left || point.X > bodyRect.Right || point.Y < bodyRect.Top || point.Y > bodyRect.Bottom) return;
            dragStart = new(point.X, point.Y); overlay.CaptureMouse(); e.Handled = true;
        };
        overlay.MouseMove += (_, e) => { if (dragStart is { } start && e.LeftButton == MouseButtonState.Pressed) UpdateDrag(start, e.GetPosition(overlay)); };
        overlay.MouseLeftButtonUp += (_, e) => { if (dragStart is { } start) { UpdateDrag(start, e.GetPosition(overlay)); dragStart = null; overlay.ReleaseMouseCapture(); e.Handled = true; } };
        overlay.LostMouseCapture += (_, _) => dragStart = null;
        Closed += (_, _) => { dragStart = null; overlay.ReleaseMouseCapture(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SessionWindowCoordinator.Close(this); e.Handled = true; } };
        AppTheme.EnsureDialogPalette(this); AppWindowChrome.ApplyDialog(this);
    }
    internal void SetCrop(RectD? value)
    { var next = value ?? new(0, 0, 1, 1); if (!WindowSourceCropGeometry.IsValid(next)) throw new ArgumentOutOfRangeException(nameof(value)); crop = next; RefreshSelection(); }
    private RectD ImageRect() => WindowSourceCropGeometry.Fit(overlay.ActualWidth, overlay.ActualHeight, bitmap.PixelWidth, bitmap.PixelHeight);
    private void UpdateDrag(PointD first, Point last)
    { if (WindowSourceCropGeometry.TryDrag(ImageRect(), first, new(last.X, last.Y), out var next)) { crop = next; RefreshSelection(); } }
    private void RefreshSelection()
    {
        var imageRect = ImageRect(); Canvas.SetLeft(selection, imageRect.X + crop.X * imageRect.Width); Canvas.SetTop(selection, imageRect.Y + crop.Y * imageRect.Height);
        selection.Width = crop.Width * imageRect.Width; selection.Height = crop.Height * imageRect.Height;
    }
}
