using System.IO;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using System.Diagnostics;
using System.Windows.Threading;
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
    private readonly Controls.Image preview = new() { Stretch = Media.Stretch.Uniform, HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center };
    private readonly Controls.TextBlock message = new() { Text = L.Get("Stamp.Loading"), TextWrapping = Wpf.TextWrapping.Wrap, HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center, MaxWidth = 160 };
    private readonly Controls.TextBlock scale = new() { FontSize = 11, Margin = new Wpf.Thickness(0, 7, 0, 0), HorizontalAlignment = Wpf.HorizontalAlignment.Center };
    private CancellationTokenSource? loading;
    private long request;
    private PreviewPlayback? playback;
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer animation = new(DispatcherPriority.Background);

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
        animation.Tick += (_, _) => AdvanceAnimation();
        UpdateScale(new Wpf.DpiScale(1, 1));
    }

    private async void OnOpened(object sender, Wpf.RoutedEventArgs args)
    {
        CancelLoading();
        UpdateScale(Media.VisualTreeHelper.GetDpi(this));
        if (!IsCurrent(library, entry)) { IsOpen = false; return; }
        long version = ++request;
        loading = new CancellationTokenSource(); var token = loading.Token;
        message.Text = L.Get("Stamp.Loading"); message.Visibility = Wpf.Visibility.Visible;
        try
        {
            var prepared = await LoadPlaybackAsync(library, entry, token);
            if (token.IsCancellationRequested || version != request || !IsOpen) { prepared?.Dispose(); return; }
            playback=prepared;
            var bitmap=playback?.Render(TimeSpan.Zero);
            if (token.IsCancellationRequested || version != request || !IsOpen) return;
            if (bitmap is null) { IsOpen = false; return; }
            preview.Source = bitmap; message.Visibility = Wpf.Visibility.Collapsed;
            UpdateScale(Media.VisualTreeHelper.GetDpi(this));
            clock.Restart(); ScheduleAnimation();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { if (!token.IsCancellationRequested && version == request && IsOpen) message.Text = ex.Message; }
    }

    private void CancelLoading()
    {
        request++; loading?.Cancel(); loading?.Dispose(); loading = null;
        animation.Stop(); clock.Reset(); playback?.Dispose(); playback=null;
    }

    private void AdvanceAnimation()
    {
        animation.Stop();
        if(!IsOpen || !IsCurrent(library,entry) || playback is null) {IsOpen=false;CancelLoading();return;}
        try
        {
            preview.Source=playback.Render(clock.Elapsed);
            ScheduleAnimation();
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or FormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {CancelLoading();message.Text=ex.Message;message.Visibility=Wpf.Visibility.Visible;}
    }

    private void ScheduleAnimation()
    {
        if(playback?.NextDelay(clock.Elapsed) is not { } delay)return;
        animation.Interval=TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds,16,1000));animation.Start();
    }

    private void UpdateScale(Wpf.DpiScale dpi)
    {
        // Captured live-effect thumbnails can have a different aspect from the stamp's
        // plane-space dimensions. Fit the actual pixels, then report scale against the nominal stamp.
        var bitmap = preview.Source as Imaging.BitmapSource;
        var fit = Geometry(bitmap?.PixelWidth ?? entry.Width, bitmap?.PixelHeight ?? entry.Height, dpi);
        preview.Width = fit.WidthDip; preview.Height = fit.HeightDip;
        double scaleX = fit.WidthDip * dpi.DpiScaleX / entry.Width;
        double scaleY = fit.HeightDip * dpi.DpiScaleY / entry.Height;
        scale.Text = Math.Abs(scaleX - scaleY) < .00001
            ? L.Format("Acceptance2.StampPreview.Scale", scaleX * 100)
            : L.Format("Acceptance2.StampPreview.ScaleAxes", scaleX * 100, scaleY * 100);
    }

    internal static (double WidthDip, double HeightDip, double ScaleX, double ScaleY) Geometry(double width, double height, Wpf.DpiScale dpi)
    {
        double fit = PreviewSize / Math.Max(width, height);
        return (width * fit, height * fit, fit * dpi.DpiScaleX, fit * dpi.DpiScaleY);
    }

    internal static bool IsCurrent(StampLibrary library, StampEntry expected) => library.Items.Any(item => item.Id == expected.Id && item.Revision == expected.Revision);

    internal static async Task<Imaging.BitmapSource?> GetPreviewAsync(StampLibrary library, StampEntry expected, CancellationToken token = default)
    {
        using var playback=await LoadPlaybackAsync(library,expected,token).ConfigureAwait(false);
        return playback?.Render(TimeSpan.Zero);
    }

    internal static async Task<Imaging.BitmapSource?> GetThumbnailAsync(StampLibrary library, StampEntry expected, CancellationToken token = default)
    {
        var bytes=await library.GetThumbnailAsync(expected.Id,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return IsCurrent(library,expected)?Decode(bytes):null;
    }

    private static async Task<PreviewPlayback?> LoadPlaybackAsync(StampLibrary library, StampEntry expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(library, expected)) return null;
        var stamp = await library.LoadStampAsync(expected.Id, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(library, expected)) return null;
        // Dynamic regions have no desktop pixels in their editable source. Reuse
        // the preview captured when saving rather than displaying a gray fallback.
        if(SceneRenderer.HasLiveEffects(stamp.Document))
            return new(await GetThumbnailAsync(library,expected,token).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        return new(stamp,expected);
    }

    internal sealed class PreviewPlayback : IDisposable
    {
        private readonly SceneDocument? document;
        private readonly AssetLibrary? assets;
        private readonly SceneRenderer? renderer;
        private readonly Imaging.BitmapSource? still;

        internal PreviewPlayback(Imaging.BitmapSource? still) => this.still=still;
        internal PreviewPlayback(LoadedProject stamp,StampEntry expected)
        {
            var document = stamp.Document.Clone();
            double factor = 320 / Math.Max(document.Viewport.Width, document.Viewport.Height);
            var transform = Affine2D.Scale(factor, factor).Compose(Affine2D.Translation(-document.Viewport.X, -document.Viewport.Y));
            foreach (var element in document.Elements)
            {
                BillboardGeometry.ApplyPlaneTransform(element, transform);
                if (element.IsBillboard) element.BillboardHeight *= factor;
            }
            if (document.Background is { } background) background.Transform = transform.Compose(background.Transform);
            document.Viewport = new(0, 0, Math.Max(1, expected.Width * factor), Math.Max(1, expected.Height * factor));
            this.document=document;assets=stamp.Assets;renderer=new SceneRenderer();
        }
        internal Imaging.BitmapSource? Render(TimeSpan time)
        {
            if(renderer is null)return still;
            renderer.AnimationTime=time;
            return Decode(renderer.ExportPng(document!,assets!,transparent:true));
        }
        internal TimeSpan? NextDelay(TimeSpan time)=>renderer?.GetNextAnimationDelay(document!,assets!,time);
        public void Dispose()=>renderer?.Dispose();
    }

    private static Imaging.BitmapSource Decode(byte[] bytes)
    {
        _ = FloatCanvas.Rendering.ImageDecodeBudget.ReadPixelCount(bytes);
            using var stream = new MemoryStream(bytes);
        var bitmap = new Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
}
