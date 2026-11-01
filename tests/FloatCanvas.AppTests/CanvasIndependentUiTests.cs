using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasIndependentUiTests
{
    internal static void Run(Action<string, bool> check, string? evidenceDirectory = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        string language = L.Preference;
        foreach (string locale in new[] { "zh-CN", "en" })
        {
            L.Initialize(locale);
            var canvas = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1000, 700)) };
            canvas.Projection.Enabled = true; CanvasGeometryMigration.UpgradeLegacy(canvas);
            var matrix = canvas.Projection.Matrix; double spacing = canvas.Grid.PlaneCellSize!.Value;
            var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
            try
            {
                int counts = 0; window.GridCountsChanged += (_, _) => counts++;
                window.GridSizeChanged += size => { canvas.Grid.PlaneCellSize = size; window.Refresh(canvas); window.PrepareForShow(); };
                window.PlaneAspectChanged += aspect =>
                {
                    if (CanvasProjectionGeometry.TrySetAspect(canvas.Projection, aspect, out var next, out _)) canvas.Projection = next;
                    window.Refresh(canvas); window.PrepareForShow();
                };
                window.GridOriginChanged += origin => { canvas.Grid.PlaneOrigin = origin; window.Refresh(canvas); window.PrepareForShow(); };
                window.Refresh(canvas); window.PrepareForShow();
                check(locale + " independent plane and origin controls replace count-based geometry", Field<StackPanel>(window, "planeFields").Visibility == Visibility.Visible &&
                    Field<StackPanel>(window, "originFields").Visibility == Visibility.Visible && Field<Grid>(window, "countPair").Visibility == Visibility.Collapsed);
                var grid = Field<NumericValueField>(window, "gridSize"); grid.Input.Text = "33.125"; Enter(grid.Input);
                check(locale + " perspective grid edit changes only independent spacing", canvas.Grid.PlaneCellSize == 33.125 && canvas.Projection.Matrix == matrix);
                var hiddenColumns = Field<NumericValueField>(window, "columns"); hiddenColumns.Input.Text = "19"; Enter(hiddenColumns.Input);
                check(locale + " hidden legacy count controls cannot publish geometry edits", counts == 0 && canvas.Projection.Matrix == matrix);
                Field<ComboBox>(window, "aspectPreset").SelectedIndex = 1;
                var bounds = canvas.Projection.PlaneBounds!.Value;
                check(locale + " A4 picker controls target ratio without touching density", Math.Abs(bounds.Width / bounds.Height - 210.0 / 297) < 1e-9 && canvas.Grid.PlaneCellSize == 33.125);
                var originX = Field<NumericValueField>(window, "gridOriginX"); originX.Input.Text = "123.75"; Enter(originX.Input);
                check(locale + " independent grid origin is editable", canvas.Grid.PlaneOrigin!.Value.X == 123.75);
                if (evidenceDirectory is not null)
                {
                    SaveContent(window, Path.Combine(evidenceDirectory, "canvas-geometry-" + locale + ".png"));
                    Descendants(window).OfType<ScrollViewer>().First().ScrollToVerticalOffset(440);
                    SaveContent(window, Path.Combine(evidenceDirectory, "canvas-grid-" + locale + ".png"));
                }
                var invalidWidth = Field<NumericValueField>(window, "planeWidth"); invalidWidth.Input.Text = "invalid"; Enter(invalidWidth.Input);
                check(locale + " invalid plane size remains visibly explained", Field<TextBlock>(window, "status").Text == L.Get("Canvas.InvalidPlaneSize"));
                canvas.Projection.Enabled = false; var dormant = canvas.Projection.Matrix;
                CanvasGridScaleController.ApplyGridSize(canvas, new(0, 0, 1000, 700), 87.5);
                check(locale + " ordinary grid edits preserve dormant plane and perspective spacing", canvas.Projection.Matrix == dormant && canvas.Grid.PlaneCellSize == 33.125 && canvas.Grid.FlatCellSize == 87.5);
                check(locale + " new canvas controls remain hidden-window test objects", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
            }
            finally { window.CloseForDispose(); }
            PreviewInput(check, locale);
            PresetRoundtrip(check, locale);
        }
        L.Initialize(language);
    }

    private static void PreviewInput(Action<string, bool> check, string locale)
    {
        using var surface = SKSurface.Create(new SKImageInfo(64, 96)); surface.Canvas.Clear(SKColors.CornflowerBlue);
        using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var source = new CaptureRasterResult(png.ToArray(), new(0, 0, 64, 96), true);
        var projection = new CanvasProjectionSettings { PlaneBounds = new RectD(0, 0, 64, 96), Origin = default,
            PinPlanePoint = new(0, 96), Quad = [new(0, 0), new(64, 0), new(64, 96), new(0, 96)], Enabled = true };
        var preview = new CanvasRectificationPreviewWindow(null, source, projection);
        try
        {
            var width = Field<NumericValueField>(preview, "width");
            foreach (string key in new[] { "Canvas.ApplyPlane", "Canvas.InsertRectifiedCapture" })
            {
                width.Input.Text = "invalid";
                var button = Descendants(preview).OfType<Button>().Single(b => Equals(b.Content, L.Get(key)));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(locale + " invalid preview dimensions block " + key, !preview.Applied && !preview.InsertScreenshot &&
                    preview.Projection.PlaneBounds == projection.PlaneBounds && Field<TextBlock>(preview, "status").Text == L.Get("Canvas.InvalidPlaneSize"));
            }
            check(locale + " preview cancellation and invalid input create no native window", !preview.IsVisible && new System.Windows.Interop.WindowInteropHelper(preview).Handle == 0);
            var assets = new AssetLibrary(); var captured = SnapshotImageCommand.CreateElement(assets, source, projection.Matrix);
            using var small = SKBitmap.Decode(CanvasRectificationRenderer.Render(source.PngBytes, source.Bounds, projection, 16));
            check(locale + " insertion retains full capture bytes independently of preview resolution", captured.Width == 64 && captured.Height == 96 &&
                assets.Get(captured.AssetId!).Data.Span.SequenceEqual(source.PngBytes) && small.Width < captured.Width);
        }
        finally { preview.Close(); }
    }
    private static void PresetRoundtrip(Action<string, bool> check, string locale)
    {
        var directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-geometry-preset-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var canvas = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1200, 900)), Grid = new() { FlatCellSize = 47.123, FlatOrigin = new(600, 450) } };
            canvas.Projection.Enabled = true; CanvasGeometryMigration.UpgradeLegacy(canvas);
            CanvasProjectionGeometry.TrySetAspect(canvas.Projection, 210.0 / 297, out var projection, out _); canvas.Projection = projection;
            canvas.Grid.PlaneCellSize = 23.125; canvas.Grid.PlaneOrigin = new(-32, 74);
            var library = new CanvasPresetLibrary(Path.Combine(directory, "canvas.json")); var saved = library.Save("A4", canvas);
            var restored = new CanvasPresetLibrary(Path.Combine(directory, "canvas.json")).Apply(saved.Id, new(), new(0, 0, 900, 700), new(0, 0, 1200, 900));
            check(locale + " v4 preset restores independent dimensions matrix density and phase", restored.Projection.PlaneBounds == canvas.Projection.PlaneBounds &&
                restored.Projection.Matrix == canvas.Projection.Matrix && restored.Grid.PlaneCellSize == 23.125 && restored.Grid.PlaneOrigin == canvas.Grid.PlaneOrigin && restored.Grid.FlatCellSize == 47.123);
            check(locale + " restored independent preset matches its saved identity", saved.Parameters.Matches(CanvasPresetParameters.From(restored)));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void SaveContent(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = (FrameworkElement)window.Content;
        content.Measure(new(740, 830)); content.Arrange(new Rect(0, 0, 740, 830)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(740, 830, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)window.FindResource("CanvasSurface"), null, new Rect(0, 0, 740, 830));
        bitmap.Render(background); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Enter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    private sealed class DetachedSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
