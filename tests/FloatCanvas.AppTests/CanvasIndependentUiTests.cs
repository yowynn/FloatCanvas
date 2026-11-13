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
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasIndependentUiTests
{
    internal static void Run(Action<string, bool> check, string? evidenceDirectory = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        ViewedRectification(check);
        string language = L.Preference;
        foreach (string locale in new[] { "zh-CN", "en" })
        {
            L.Initialize(locale);
            var canvas = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1000, 700)) };
            canvas.Projection.Enabled = true; CanvasGeometryMigration.UpgradeLegacy(canvas);
            var matrix = canvas.Projection.Matrix; double spacing = canvas.Grid.PlaneCellSize!.Value;
            var window = new CanvasOptionsTestWindow(CanvasPresetLibrary.OfficialOnly());
            try
            {
                int counts = 0; window.GridCountsChanged += (_, _) => counts++;
                window.GridSizeChanged += size => { canvas.Grid.PlaneCellSize = size; window.Refresh(canvas); window.PrepareForShow(); };
                window.GridOriginChanged += origin => { canvas.Grid.PlaneOrigin = origin; window.Refresh(canvas); window.PrepareForShow(); };
                window.Refresh(canvas); window.PrepareForShow();
                check(locale + " independent plane and origin controls replace count-based geometry", Field<StackPanel>(window, "planeFields").Visibility == Visibility.Visible &&
                    Field<StackPanel>(window, "originFields").Visibility == Visibility.Visible && Field<Grid>(window, "countPair").Visibility == Visibility.Collapsed);
                var grid = Field<NumericValueField>(window, "gridSize"); grid.Input.Text = "33.125"; Enter(grid.Input);
                check(locale + " perspective grid edit changes only independent spacing", canvas.Grid.PlaneCellSize == 33.125 && canvas.Projection.Matrix == matrix);
                var hiddenColumns = Field<NumericValueField>(window, "columns"); hiddenColumns.Input.Text = "19"; Enter(hiddenColumns.Input);
                check(locale + " hidden legacy count controls cannot publish geometry edits", counts == 0 && canvas.Projection.Matrix == matrix);
                check(locale + " target aspect presets are confined to rectification", !Descendants(window).OfType<ComboBox>().Any());
                var originX = Field<NumericValueField>(window, "gridOriginX"); originX.Input.Text = "123.75"; Enter(originX.Input);
                check(locale + " independent grid origin is editable", canvas.Grid.PlaneOrigin!.Value.X == 123.75);
                if (evidenceDirectory is not null)
                {
                    SaveContent(window, Path.Combine(evidenceDirectory, "canvas-geometry-" + locale + ".png"));
                    Descendants(window).OfType<ScrollViewer>().First().ScrollToVerticalOffset(440);
                    SaveContent(window, Path.Combine(evidenceDirectory, "canvas-grid-" + locale + ".png"));
                }
                var invalidWidth = Field<NumericValueField>(window, "planeWidth"); invalidWidth.Input.Text = "invalid"; Enter(invalidWidth.Input);
                check(locale + " invalid plane size remains visibly explained", Field<TextBlock>(window.Panel, "status").Text == "⚠ " + L.Get("Canvas.InvalidPlaneSize"));
                canvas.Projection.Enabled = false; var dormant = canvas.Projection.Matrix;
                CanvasGridScaleController.ApplyGridSize(canvas, new(0, 0, 1000, 700), 87.5);
                check(locale + " ordinary grid edits preserve dormant plane and perspective spacing", canvas.Projection.Matrix == dormant && canvas.Grid.PlaneCellSize == 33.125 && canvas.Grid.FlatCellSize == 87.5);
                check(locale + " new canvas controls remain hidden-window test objects", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
            }
            finally { window.CloseForDispose(); }
            PreviewInput(check, locale);
            DialogStandards(check, locale, evidenceDirectory);
            PresetRoundtrip(check, locale);
        }
        L.Initialize(language);
    }
    private static void ViewedRectification(Action<string, bool> check)
    {
        var projection = new CanvasProjectionSettings { Enabled = true, PlaneBounds = new(0, 0, 64, 96),
            Matrix = new(1, .1, 20, .02, 1, 10, .0008, .0004, 1) };
        projection.Quad = new[] { new PointD(0, 0), new(64, 0), new(64, 96), new(0, 96) }.Select(projection.Matrix.TransformPoint).ToList();
        var viewing = new CanvasViewTransform(2, new(150, 100));
        var viewed = CanvasRectificationPreviewWindow.ViewingProjection(projection, viewing);
        var bounds = viewed.Matrix.TransformBounds(projection.PlaneBounds.Value);
        var scene = new SceneDocument { Viewport = new(0, 0, 400, 400), Canvas = new() { Projection = projection },
            Elements = [new() { Kind = ElementKind.Rectangle, Width = 64, Height = 96,
                Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } }] };
        var selection = new CaptureSelection(CaptureShape.PolygonLasso, CaptureSource.CanvasOnly, bounds, viewed.Quad, 0, 0);
        var raster = SnapshotImageCommand.Render(scene, [], selection, null, [scene.Viewport], viewing: viewing);
        var window = new CanvasRectificationPreviewWindow(null, raster, projection, viewing);
        try
        {
            var image = (BitmapSource)Field<Image>(window, "rectified").Source;
            byte[] center = new byte[4]; image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), center, 4, 0);
            check("viewed rectification samples the displayed perspective pixels through the viewing matrix", center[2] > 240 && center[3] > 240 && center[0] < 10);
            var width = Field<NumericValueField>(window, "width"); width.BeginGesture(); width.Input.Text = "80"; width.EndGesture(true);
            check("viewed rectification resizing retains authored quad and excludes the editor camera", window.Projection.Quad.SequenceEqual(projection.Quad) &&
                window.Projection.PlaneBounds!.Value.Width == 80 && projection.PlaneBounds.Value.Width == 64);
            var matrix = CanvasRectificationPreviewWindow.ViewingProjection(window.Projection, viewing).Matrix;
            var capture = SnapshotImageCommand.CreateElement(new(), raster, matrix);
            var placement = capture.ProjectivePlacement ?? Projective2D.FromAffine(capture.Transform);
            check("viewed rectification inserted capture returns to its displayed screen position exactly once", matrix.Compose(placement).TransformPoint(default).DistanceTo(new(raster.Bounds.X, raster.Bounds.Y)) < 1e-6);
            Descendants(window).OfType<ComboBox>().Single().SelectedIndex = 1;
            var sized = window.Projection.PlaneBounds!.Value;
            check("target ratio presets remain functional inside rectification", Math.Abs(sized.Width / sized.Height - 210d / 297) < 1e-9);
            Descendants(window).OfType<Button>().Single(button => Equals(button.Content, L.Get("Canvas.SwapOrientation"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("orientation swap remains local to rectification preview", Math.Abs(window.Projection.PlaneBounds!.Value.Width - sized.Height) < 1e-9 && projection.PlaneBounds.Value.Width == 64);
        }
        finally { window.Close(); }
    }

    private static void DialogStandards(Action<string, bool> check, string locale, string? evidenceDirectory)
    {
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme); string prefix = locale + "/" + theme + " ";
            var canvas = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 800, 600)) };
            canvas.Projection.Enabled = true; CanvasGeometryMigration.UpgradeLegacy(canvas);
            CanvasSettings? lastPreview = null;
            var calibration = new CanvasGridCalibrationWindow(null!, canvas, new RectD(.1, .1, .2, .2), new CanvasGridCalibrationDraft(), value => lastPreview = value);
            var calibrationBody = (FrameworkElement)((ScrollViewer)calibration.Content).Content;
            calibrationBody.Measure(new(418, 400)); calibrationBody.Arrange(new Rect(0, 0, 418, 400)); calibrationBody.UpdateLayout();
            var numeric = Descendants(calibrationBody).OfType<NumericValueField>().ToArray();
            check(prefix + "grid calibration shares window insets numeric row sizes and column-row icons", calibrationBody.Margin.Left == WindowLayout.ContentInset + PropertyChangeRegion.HighlightInset &&
                numeric.All(field => field.Height == PropertyPanelLayout.ControlHeight) && numeric.Take(2).Select(field => ((NumericPropertyIcon)field.DragAnchor.Child).Kind).SequenceEqual(new[] { NumericPropertyKind.Columns, NumericPropertyKind.Rows }));
            check(prefix + "grid calibration exposes consistent default and cancel actions", Descendants(calibrationBody).OfType<Button>().Count(button => button.IsDefault) == 1 &&
                Descendants(calibrationBody).OfType<Button>().Count(button => button.IsCancel) == 1 && Descendants(calibrationBody).OfType<Button>().All(button =>
                    button.MinHeight == PropertyPanelLayout.ControlHeight || button.Height == PropertyPanelLayout.ControlHeight ||
                    numeric.Any(field => field.IsAncestorOf(button)) && button.ActualHeight <= PropertyPanelLayout.ControlHeight / 2));
            if (evidenceDirectory is not null)
                foreach (double scale in new[] { 1d, 1.5d, 2d })
                    SaveScaledContent(calibration, Path.Combine(evidenceDirectory, $"sample-dialog-{locale}-{theme}-{scale:0.0}.png"), 480, 800, scale);
            double beforeSpacing = lastPreview!.Grid.PlaneCellSize!.Value;
            numeric[0].BeginGesture(); numeric[0].Input.Text = "2";
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(prefix + "grid calibration numeric gestures preview before release", numeric[0].IsInteracting && lastPreview!.Grid.PlaneCellSize != beforeSpacing);
            numeric[0].EndGesture(false);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(prefix + "canceling grid calibration numeric gestures restores the previous preview", lastPreview!.Grid.PlaneCellSize == beforeSpacing);
            calibration.Close();
            var settings = new CanvasOptionsTestWindow(CanvasPresetLibrary.OfficialOnly()); settings.Refresh(canvas); settings.PrepareForShow();
            var planeWidth = Field<NumericValueField>(settings, "planeWidth"); var planeHeight = Field<NumericValueField>(settings, "planeHeight");
            check(prefix + "canvas dimensions share the historical size pair and aspect chain", ReferenceEquals(planeWidth.Parent, planeHeight.Parent) &&
                ReferenceEquals(Field<AspectRatioButton>(settings, "aspectLock").Parent, planeWidth.Parent));
            var settingsBody = (FrameworkElement)settings.Content;
            settingsBody.Measure(new(settings.MinWidth, 850)); settingsBody.Arrange(new Rect(0, 0, settings.MinWidth, 850)); settingsBody.UpdateLayout();
            var authoring = Field<StackPanel>(settings, "authoringControls");
            var titles = authoring.Children.OfType<TextBlock>().ToArray();
            check(prefix + "canvas properties use two peer sections with preset heading typography", titles.Select(title => title.Text).SequenceEqual(new[] {
                L.Get("Canvas.Section.BackgroundGrid"), L.Get("Canvas.Section.PerspectiveGrid") }) && titles.All(title => title.FontSize == PropertyPanelLayout.SectionFontSize));
            check(prefix + "dynamic properties have no enclosing frame or inset", ReferenceEquals(Field<PropertyChangeRegion>(settings, "projectionOptions").Parent, authoring));
            var modeLabel = Descendants(settings).OfType<PropertyLabel>().Single(label => label.Text == L.Get("Canvas.Mode"));
            var gridLabel = Descendants(settings).OfType<PropertyLabel>().Single(label => label.Text == L.Get("Canvas.GridSize"));
            check(prefix + "canvas type and grid size labels share the same left edge", Math.Abs(modeLabel.TranslatePoint(default, settingsBody).X - gridLabel.TranslatePoint(default, settingsBody).X) < .1);
            var actions = Descendants(authoring).OfType<Button>().Where(button => button.Content is StackPanel).ToArray();
            check(prefix + "canvas actions have icons and the standard control height", actions.Length >= 5 && actions.All(button => button.Height == PropertyPanelLayout.ControlHeight && ((StackPanel)button.Content).Children[0] is Viewbox));
            var fourPoints = Field<Button>(settings, "fourPoints"); var methods = (Grid)fourPoints.Parent;
            var methodButtons = methods.Children.OfType<Button>().ToArray();
            check(prefix + "perspective methods divide the full responsive action width equally", Math.Abs(methodButtons[0].ActualWidth - methodButtons[1].ActualWidth) < .1 && Math.Abs(methods.ActualWidth - authoring.ActualWidth) < .1);
            check(prefix + "reference opacity belongs to the shared caption", ReferenceEquals(Field<ToolbarOpacitySlider>(settings, "guideOpacity").Parent, settings.Options.Header.Actions));
            if (evidenceDirectory is not null)
                foreach (double scale in new[] { 1d, 1.5d, 2d })
                {
                    SaveScaledContent(settings, Path.Combine(evidenceDirectory, $"settings-min-{locale}-{theme}-{scale:0.0}.png"), settings.MinWidth, 850, scale);
                    canvas.Projection.Enabled = false; settings.Refresh(canvas); settings.PrepareForShow();
                    SaveScaledContent(settings, Path.Combine(evidenceDirectory, $"settings-flat-{locale}-{theme}-{scale:0.0}.png"), settings.MinWidth, 850, scale);
                    canvas.Projection.Enabled = true; settings.Refresh(canvas); settings.PrepareForShow();
                }
            settings.CloseForDispose();
            var keyboard = new KeyboardLayoutEditorWindow(null); var fields = Field<Dictionary<string, NumericValueField>>(keyboard, "coordinates");
            check(prefix + "keyboard layout numbers use the same coordinate and dimension icons as other property panels", fields.Select(pair => ((NumericPropertyIcon)pair.Value.DragAnchor.Child).Kind)
                .SequenceEqual(new[] { NumericPropertyKind.X, NumericPropertyKind.Y, NumericPropertyKind.Width, NumericPropertyKind.Height }) &&
                ReferenceEquals(Field<CheckBox>(keyboard, "enabled").Style, keyboard.FindResource("CanvasPropertySwitch")));
            keyboard.Close();
        }
        AppTheme.Apply("light");
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
            var height = Field<NumericValueField>(preview, "height");
            check(locale + " rectification uses shared insets a standard ratio editor and a spaced dimension pair", ((FrameworkElement)preview.Content).Margin.Left == WindowLayout.ContentInset &&
                Descendants(preview).OfType<ComboBox>().Single().Height == PropertyPanelLayout.ControlHeight && ReferenceEquals(width.Parent, height.Parent) &&
                ((Grid)width.Parent).ColumnDefinitions[1].Width.Value == PropertyPanelLayout.PairGap && !Descendants(preview).OfType<GroupBox>().Any());
            foreach (string key in new[] { "Canvas.ApplyCanvasSize", "Canvas.InsertRectifiedCapture" })
            {
                width.Input.Text = "invalid";
                var button = Descendants(preview).OfType<Button>().Single(b => Equals(b.Content, L.Get(key)));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(locale + " invalid preview dimensions block " + key, !preview.Applied && !preview.InsertScreenshot &&
                    preview.Projection.PlaneBounds == projection.PlaneBounds && Field<TextBlock>(preview, "status").Text == L.Get("Canvas.InvalidPlaneSize"));
            }
            width.SetModelValue(64); height.SetModelValue(96); width.BeginGesture(); width.Input.Text = "80";
            check(locale + "rectification numeric gestures update the bounded preview before release", width.IsInteracting && preview.Projection.PlaneBounds!.Value.Width == 80);
            width.EndGesture(false);
            check(locale + "rectification canceled gestures restore the previous plane without applying", preview.Projection.PlaneBounds!.Value.Width == 64 && !preview.Applied);
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
    private static void SaveScaledContent(Window window, string path, double width, double height, double scale)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = (FrameworkElement)window.Content;
        content.Measure(new(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)window.FindResource("CanvasSurface"), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
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
    private static T Field<T>(object owner, string name) => (T)CanvasOptionsTestWindow.Unwrap(owner).GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(CanvasOptionsTestWindow.Unwrap(owner))!;
    private static void Enter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    private sealed class DetachedSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
