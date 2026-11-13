using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class CanvasSystemsUiEvidence
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string languageBefore = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                var group = new CanvasGroup { Name = "组合 / Nested artwork with a deliberately long name", OrderMode = GroupOrderMode.AtomicBlock };
                var child = new CanvasElement { Width = 20, Height = 20, GroupId = group.Id }; group.ChildOrder = [child.Id];
                var rootLayer = new CanvasElement { Name = "Other layer", Width = 20, Height = 20 };
                var session = new EditorSession(new() { Groups = [group], Elements = [child, rootLayer], RootOrder = [group.Id, rootLayer.Id] }); session.SetSelection([child.Id]);
                var owner = new LayersWindow(() => session); owner.Show();
                foreach (var size in new[] { new Size(300, 300), new Size(420, 1000) })
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var dialog = Application.Current!.Windows.OfType<Window>().Single(w => ReferenceEquals(w.Owner, owner));
                        dialog.Width = size.Width; dialog.Height = size.Height; dialog.UpdateLayout();
                        Save(dialog, $"picker-{language}-{theme}-{size.Height}");
                        var search = CanvasUiUxTests.Descendants<TextBox>(dialog).Single();
                        var list = CanvasUiUxTests.Descendants<ListBox>(dialog).Single();
                        search.Text = "Nested artwork"; WaitSearch(); Save(dialog, $"picker-search-{language}-{theme}-{size.Height}");
                        search.Text = "no matching destination"; WaitSearch(); Save(dialog, $"picker-empty-{language}-{theme}-{size.Height}"); dialog.Close();
                        void WaitSearch()
                        {
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            while (((LayerMoveTargets)list.DataContext).Search != search.Text && watch.Elapsed.TotalSeconds < 5)
                            { CanvasUiUxTests.Pump(); Thread.Sleep(5); }
                            dialog.UpdateLayout();
                        }
                    }));
                    typeof(LayersWindow).GetMethod("ShowMoveDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
                }
                var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 800, 600)) }; settings.Projection.Enabled = true;
                var draft = new CanvasGridCalibrationDraft { Columns = "4", Rows = "5" };
                var calibration = new CanvasGridCalibrationWindow(owner, settings, new(.1, .1, .4, .5), draft, _ => { });
                calibration.Show(); calibration.UpdateLayout();
                Save(calibration, $"calibration-{language}-{theme}");
                var total = CanvasUiUxTests.Descendants<TextBox>(calibration).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == L.Get("Canvas.TotalColumns"));
                total.Text = "11"; CanvasUiUxTests.Pump(); Save(calibration, $"calibration-manual-{language}-{theme}"); calibration.Close();
                var unsafeGrid = new CanvasGridCalibrationWindow(owner, settings, new(.1, .1, .4, .5), draft, _ => { },
                    (_, _) => Task.FromResult(new CanvasCalibrationCheck(2) { Issues = [
                        new(Guid.NewGuid(), "组合 A / Skyline", "主画布 › 组合 A / Skyline", new(50, 50, 100, 100)),
                        new(Guid.NewGuid(), "组合 B / Garden", "主画布 › 组合 B / Garden", new(200, 80, 70, 90))] }), highlight: _ => { });
                unsafeGrid.Show(); CanvasUiUxTests.Pump();
                var details = CanvasUiUxTests.Descendants<Expander>(unsafeGrid).Single(); details.IsExpanded = true;
                unsafeGrid.UpdateLayout(); CanvasUiUxTests.Descendants<ListBox>(details).Single().SelectedIndex = 0;
                unsafeGrid.UpdateLayout(); Save(unsafeGrid, $"calibration-unsafe-{language}-{theme}"); unsafeGrid.Close();
                using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(320, 240));
                surface.Canvas.Clear(SkiaSharp.SKColors.SteelBlue);
                using var bitmap = surface.Snapshot(); using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                var rectification = new CanvasRectificationPreviewWindow(owner, new(png.ToArray(), new(0, 0, 320, 240), true), settings.Projection);
                rectification.Show(); rectification.UpdateLayout(); Save(rectification, $"rectification-{language}-{theme}");
                rectification.Width = 600; rectification.Height = 420; rectification.UpdateLayout(); Save(rectification, $"rectification-min-{language}-{theme}"); rectification.Close();
                session.SetSelection([rootLayer.Id, child.Id]); owner.Refresh(); owner.UpdateLayout();
                Save(owner, $"selection-scope-{language}-{theme}"); owner.Close();
            }
        }
        finally { L.Initialize(languageBefore); }
        void Save(Window window, string name)
        {
            foreach (double dpi in new[] { 1d, 1.5, 2d })
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi), (int)Math.Ceiling(window.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{name}-{dpi}.png")); encoder.Save(file);
            }
        }
    }
}
