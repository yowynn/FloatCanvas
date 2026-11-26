using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasOptionsRefinementTests
{
    internal static void Run(Action<string, bool> check, string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        VerifyPreview(check);
        foreach (var language in new[] { "zh-CN", "en" })
        foreach (var theme in new[] { "light", "dark" })
        {
            L.Initialize(language); AppTheme.Apply(theme);
            string prefix = language + "-" + theme;
            var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1000, 700)) };
            settings.Projection.Enabled = true; CanvasProjectionGeometry.InitializePlane(settings);
            var window = new CanvasOptionsTestWindow(CanvasPresetLibrary.OfficialOnly()) { Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Options.TargetLabel = L.Get("RootCanvas.Main"); window.Refresh(settings); window.PrepareForShow(); window.Show(); Pump();
                var depth = Field<CaptionModeSwitch>(window, "depthPriorityOcclusion");
                var opacity = Field<ToolbarOpacitySlider>(window, "guideOpacity");
                var help = (Button)window.Template.FindName("Help", window); var close = (Button)window.Template.FindName("Close", window);
                double X(FrameworkElement element) => element.TranslatePoint(default, window).X;
                check(prefix + " caption order is depth, opacity, pin, help, close", X(depth) < X(opacity) && X(opacity) < X(window.Pin) && X(window.Pin) < X(help) && X(help) < X(close));
                check(prefix + " preview controls each occupy two caption slots", depth.Width == 2 * help.Width + 6 && opacity.Width == depth.Width && opacity.Height == help.Height);
                check(prefix + " caption action gaps are uniform", Math.Abs(X(opacity) - X(depth) - depth.ActualWidth - 6) < 1 && Math.Abs(X(window.Pin) - X(opacity) - opacity.ActualWidth - 6) < 1);
                var source = Field<NumericValueField>(window, "gridOriginX"); var target = Field<NumericValueField>(window, "gridOriginY");
                var pair = (Grid)source.Parent; var align = pair.Children.OfType<Button>().Single();
                check(prefix + " grid origin embeds one named icon action between X and Y", ReferenceEquals(target.Parent, pair) && Grid.GetColumn(align) == 1 && align.Content is Viewbox && align.Width == PropertyPanelLayout.ControlHeight && AutomationProperties.GetName(align) == L.Get("Canvas.GridOriginToPin"));
                check(prefix + " origin action has the normal property border and field height", ReferenceEquals(align.Style, align.FindResource("CanvasPropertyIconButton")) && align.BorderThickness == new Thickness(1) && align.ActualHeight == source.ActualHeight);
                var region = Field<PropertyChangeRegion>(window, "projectionOptions"); var body = (StackPanel)region.Child;
                var order = body.Children.Cast<FrameworkElement>().ToList();
                check(prefix + " geometry, pin, calibration, grid, origin, rectification, reset follow the agreed order",
                    order.IndexOf(Field<StackPanel>(window, "planeFields")) < order.IndexOf(Field<FrameworkElement>(window, "pinRow")) &&
                    order.IndexOf(Field<FrameworkElement>(window, "pinRow")) < order.IndexOf(Field<FrameworkElement>(window, "projectionGridCalibration")) &&
                    order.IndexOf(Field<FrameworkElement>(window, "projectionGridCalibration")) < order.IndexOf(Field<PropertyChangeRegion>(window, "gridCountsRegion")) &&
                    order.IndexOf(Field<StackPanel>(window, "originFields")) < order.IndexOf(Field<Button>(window, "rectificationPreview")) &&
                    ReferenceEquals(order.Last(), ((FrameworkElement)Field<Button>(window, "reset").Parent)));
                int aligned = 0; window.Options.GridOriginToPinRequested += () => aligned++; align.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(prefix + " embedded origin action invokes the existing command once", aligned == 1);
                window.SetAuthoringAllowed(false);
                check(prefix + " readonly authoring still permits preview controls", depth.IsEnabled && opacity.IsEnabled && !align.IsEnabled);
                window.SetAuthoringAllowed(true);
                foreach (bool perspective in new[] { true, false })
                {
                    settings.Projection.Enabled = perspective; window.Refresh(settings); window.PrepareForShow(); Pump();
                    check(prefix + " flat/perspective preserves caption positions " + perspective, depth.Visibility == Visibility.Visible && depth.IsEnabled == perspective);
                    foreach (double width in new[] { 640d, 699d })
                    foreach (double scale in new[] { 1d, 1.5, 2d })
                    {
                        window.Width = width; VisualTreeHelper.SetRootDpi(window, new(scale, scale)); window.UpdateLayout(); Pump();
                        check(prefix + " caption stays within client width " + width + "/" + scale + "/" + perspective,
                            X(close) + close.ActualWidth <= window.ActualWidth && X(depth) > 100 && depth.ActualHeight >= 27);
                        Save(window, Path.Combine(output, $"options-{prefix}-{width}-{scale:0.0}-{perspective}.png"), scale);
                    }
                }
                settings.Projection.Enabled = true; window.Refresh(settings); window.PrepareForShow();
                window.Options.RefreshPreviewControls(true, .45); Pump();
                Save(window, Path.Combine(output, $"options-{prefix}-depth.png"), 2);
                window.Options.RefreshGuideOpacity(1); opacity.Focus();
                opacity.StartPointerGesture(new(55, 14), () => true); opacity.Value = .3;
                opacity.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                check(prefix + " Escape cancels only the opacity gesture", !opacity.IsPointerInteracting && opacity.Value == 1 && window.IsVisible);
                VerifyDialog(check, window, settings, prefix, output);
            }
            finally { window.CloseForDispose(); }
        }
        L.Initialize("zh-CN"); AppTheme.Apply("light");
    }

    private static void VerifyDialog(Action<string, bool> check, Window owner, CanvasSettings settings, string prefix, string output)
    {
        CanvasSettings? preview = null; var draft = new CanvasGridCalibrationDraft { Columns = "4", Rows = "5" };
        var dialog = new CanvasGridCalibrationWindow(owner, settings, new(.1, .1, .4, .5), draft, value => preview = value) { ShowActivated = false };
        try
        {
            dialog.Show(); Pump();
            var numbers = Descendants(dialog).OfType<NumericValueField>().ToArray();
            var boundary = Descendants(dialog).OfType<CheckBox>().Single();
            var region = Descendants(dialog).OfType<PropertyChangeRegion>().Single();
            var candidate = (StackPanel)((StackPanel)region.Child).Children[0];
            var apply = Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, L.Get("Canvas.ApplyPlane")));
            var help = (Button)dialog.Template.FindName("Help", dialog);
            check(prefix + " calibration opens with a valid preview and common help", preview is not null && apply.IsEnabled && help.Visibility == Visibility.Visible);
            check(prefix + " calibration removes persistent explanatory paragraphs", !Descendants(dialog).OfType<TextBlock>().Any(text => text.Text == L.Get("Canvas.GridCalibrationSpans") || text.Text == L.Get("Canvas.GridCandidateHelp") || text.Text == L.Get("Canvas.GridCalibrationEffect")));
            foreach (bool constrained in new[] { false, true })
            {
                boundary.IsChecked = constrained; Pump();
                check(prefix + " boundary option animates the changing candidates " + constrained, candidate.Visibility == (constrained ? Visibility.Visible : Visibility.Collapsed) && region.CueStrength > 0 && preview is not null);
                region.Reset(); Pump();
                foreach (double scale in new[] { 1d, 1.5, 2d })
                {
                    VisualTreeHelper.SetRootDpi(dialog, new(scale, scale)); dialog.UpdateLayout(); Pump();
                    Save(dialog, Path.Combine(output, $"calibration-{prefix}-{scale:0.0}-{constrained}.png"), scale);
                }
            }
            numbers[0].Input.Text = "invalid"; Pump();
            check(prefix + " invalid input clears the candidate and disables Apply", preview is null && !apply.IsEnabled);
            numbers[0].Input.Text = "4"; Pump();
            check(prefix + " valid input immediately restores preview", preview is not null && apply.IsEnabled);
            help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check(prefix + " calibration help closes through the common local Escape path", AppWindowChrome.TryCloseHelp(dialog) && dialog.IsVisible);
            var adjust = Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, L.Get("Canvas.AdjustGridSample")));
            adjust.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(prefix + " adjusting retains sample counts without applying", dialog.AdjustRequested && dialog.Result is null && draft.Columns == "4" && draft.Rows == "5");
        }
        finally { dialog.Close(); }
    }

    private static void VerifyPreview(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 400, 300) };
        scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1);
        scene.Canvas.Projection.Quad = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)];
        var mapping = CanvasMapping.Create(scene);
        CanvasElement Billboard(double y, uint color)
        {
            var anchor = new PointD(150 * (1 + .001 * y), y); mapping.TryGetDepthScale(anchor, out double scale);
            return new() { Kind = ElementKind.Rectangle, IsBillboard = true, Width = 160, Height = 160, BillboardAnchor = anchor,
                BillboardHeight = (mapping.ToScreen(anchor).Y - 150) / scale, Style = new() { Fill = color, StrokeWidth = 0 } };
        }
        var near = Billboard(100, 0xFFFF0000); var far = Billboard(400, 0xFF0000FF); scene.Elements = [near, far];
        var session = new EditorSession(scene); var editor = new EditorController(session) { Tool = EditorTool.Select };
        string saved = JsonSerializer.Serialize(session.CreateCommittedSnapshot());
        var appearance = new CanvasPreviewAppearance(); var freezer = new CanvasPreviewScene();
        var frozen = freezer.Capture(scene, session.PreviewRevision, session.PreviewElementsRevision, 0, -1, -1, false);
        using var renderer = new CanvasPreviewRenderer();
        CanvasPreviewContext? previous = null; var monitors = new object();
        foreach (bool depth in new[] { false, true, false })
        {
            appearance.SetDepth(depth); var context = new CanvasPreviewContext(session, monitors, default, false, false, false, DepthPriorityOcclusion: depth);
            var evaluation = freezer.EvaluationFor(frozen, appearance.Resolve(frozen.Canvas));
            using var frame = renderer.Render(new() { Context = context, Document = frozen, FrozenEvaluation = evaluation, SceneIdentity = scene,
                Resources = [], Outputs = [new(0, 0, 400, 300)], ContentRevision = session.PreviewRevision });
            using var bitmap = PreviewTestPixels.Read(frame.Pixels);
            check("runtime depth renders the requested foreground " + depth, bitmap.GetPixel(165, 145) == (depth ? SKColors.Red : SKColors.Blue));
            session.SetSelection([]);
            editor.ApplyPresentedScene(frame.PickingDocument, frame.PickingImages, frame.AnimationTime, frame.PickingResources, session.PreviewRevision, evaluation: frame.PickingEvaluation);
            editor.Pointer(new(OverlayPointerKind.Down, new(165, 145), OverlayMouseButton.Left, OverlayModifiers.None));
            editor.Pointer(new(OverlayPointerKind.Up, new(165, 145), OverlayMouseButton.Left, OverlayModifiers.None));
            check("picking follows the presented depth mode " + depth, session.Selection.SetEquals([depth ? near.Id : far.Id]));
            check("depth preview never dirties authored or recovery content " + depth, session.UndoCount == 0 && JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == saved);
            if (previous is not null) check("opposite depth frames cannot satisfy a new presentation context", !previous.HasSameContent(context));
            previous = context;
        }
        check("unchanged preview reuses evaluation without re-cloning scene nodes", ReferenceEquals(freezer.EvaluationFor(frozen, appearance.Resolve(frozen.Canvas)), freezer.EvaluationFor(frozen, appearance.Resolve(frozen.Canvas))) && freezer.FullBuildCount == 1);
        editor.DetachSession();
    }

    private static T Field<T>(object owner, string name) => (T)CanvasOptionsTestWindow.Unwrap(owner).GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(CanvasOptionsTestWindow.Unwrap(owner))!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>())
        { yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Save(Window window, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
