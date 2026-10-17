using System.IO;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Lazy, revision-checked preview shared by pinned slots and the stamp inventory.</summary>
internal sealed class StampPreviewTooltip : Controls.ToolTip
{
    internal const double PreviewSize = 176;
    private readonly StampLibrary library;
    private readonly StampEntry entry;
    private readonly Controls.Image preview = new() { Stretch = Media.Stretch.Fill, HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center };
    private readonly Controls.TextBlock message = new() { Text = L.Get("Stamp.Loading"), TextWrapping = Wpf.TextWrapping.Wrap, HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center, MaxWidth = 160 };
    private readonly Controls.TextBlock scale = new() { FontSize = 11, Margin = new Wpf.Thickness(0, 7, 0, 0), HorizontalAlignment = Wpf.HorizontalAlignment.Center };
    private CancellationTokenSource? loading;
    private long request;
    private bool loaded;

    internal static StampPreviewTooltip Create(StampLibrary library, StampEntry entry, string shortcut) => new(library, entry, shortcut);

    private StampPreviewTooltip(StampLibrary library, StampEntry entry, string shortcut)
    {
        this.library = library; this.entry = entry;
        // Implicit styles use the concrete control type as their key; this subclass
        // must explicitly retain the application's standard ToolTip surface.
        SetResourceReference(Wpf.FrameworkElement.StyleProperty, typeof(Controls.ToolTip));
        var body = new Controls.StackPanel { Width = PreviewSize };
        body.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
        body.Children.Add(new Controls.TextBlock { Text = entry.Name, FontWeight = Wpf.FontWeights.SemiBold, TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(0, 0, 0, 3) });
        if (!string.IsNullOrWhiteSpace(shortcut))
        {
            var binding = new Controls.TextBlock { Text = L.Format("Acceptance2.StampPreview.Shortcut", shortcut), FontSize = 11, Margin = new(0, 0, 0, 8) };
            binding.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted"); body.Children.Add(binding);
        }
        var stage = new Controls.Grid { Width = PreviewSize, Height = PreviewSize };
        stage.Children.Add(preview); stage.Children.Add(message); body.Children.Add(stage);
        message.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted");
        scale.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted"); body.Children.Add(scale);
        Content = body;
        Opened += OnOpened;
        Closed += (_, _) => CancelLoading();
        UpdateScale(new Wpf.DpiScale(1, 1));
    }

    private async void OnOpened(object sender, Wpf.RoutedEventArgs args)
    {
        CancelLoading();
        UpdateScale(Media.VisualTreeHelper.GetDpi(this));
        if (!IsCurrent(library, entry)) { IsOpen = false; return; }
        if (loaded) return;
        long version = ++request;
        loading = new CancellationTokenSource(); var token = loading.Token;
        message.Text = L.Get("Stamp.Loading"); message.Visibility = Wpf.Visibility.Visible;
        try
        {
            var bitmap = await GetPreviewAsync(library, entry, token);
            if (token.IsCancellationRequested || version != request || !IsOpen) return;
            if (bitmap is null) { IsOpen = false; return; }
            preview.Source = bitmap; loaded = true; message.Visibility = Wpf.Visibility.Collapsed;
            UpdateScale(Media.VisualTreeHelper.GetDpi(this));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { if (!token.IsCancellationRequested && version == request && IsOpen) message.Text = ex.Message; }
    }

    private void CancelLoading()
    {
        request++; loading?.Cancel(); loading?.Dispose(); loading = null;
    }

    private void UpdateScale(Wpf.DpiScale dpi)
    {
        var fit = Geometry(entry.Width, entry.Height, dpi);
        preview.Width = fit.WidthDip; preview.Height = fit.HeightDip;
        scale.Text = Math.Abs(fit.ScaleX - fit.ScaleY) < .00001
            ? L.Format("Acceptance2.StampPreview.Scale", fit.ScaleX * 100)
            : L.Format("Acceptance2.StampPreview.ScaleAxes", fit.ScaleX * 100, fit.ScaleY * 100);
    }

    internal static (double WidthDip, double HeightDip, double ScaleX, double ScaleY) Geometry(double width, double height, Wpf.DpiScale dpi)
    {
        double fit = PreviewSize / Math.Max(width, height);
        return (width * fit, height * fit, fit * dpi.DpiScaleX, fit * dpi.DpiScaleY);
    }

    internal static bool IsCurrent(StampLibrary library, StampEntry expected) => library.Items.Any(item => item.Id == expected.Id && item.Revision == expected.Revision);

    internal static async Task<Imaging.BitmapSource?> GetPreviewAsync(StampLibrary library, StampEntry expected, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(library, expected)) return null;
        var stamp = await library.LoadStampAsync(expected.Id, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(library, expected)) return null;
        // Dynamic regions have no desktop pixels in their editable source. Reuse
        // the preview captured when saving rather than displaying a gray fallback.
        byte[] bytes = SceneRenderer.HasLiveEffects(stamp.Document)
            ? await library.GetThumbnailAsync(expected.Id, token).ConfigureAwait(false)
            : await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var document = stamp.Document.Clone();
            double factor = 320 / Math.Max(document.Viewport.Width, document.Viewport.Height);
            var transform = Affine2D.Scale(factor, factor).Compose(Affine2D.Translation(-document.Viewport.X, -document.Viewport.Y));
            foreach (var element in document.Elements) element.Transform = transform.Compose(element.Transform);
            if (document.Background is { } background) background.Transform = transform.Compose(background.Transform);
            document.Viewport = new(0, 0, Math.Max(1, expected.Width * factor), Math.Max(1, expected.Height * factor));
            using var renderer = new SceneRenderer();
            return renderer.ExportPng(document, stamp.Assets, transparent: true);
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(library, expected)) return null;
        using var stream = new MemoryStream(bytes);
        var bitmap = new Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
}
