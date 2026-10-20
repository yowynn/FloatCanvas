using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Opt-in, human-started production hover preview in one bounded tile and its own temporary profile.</summary>
internal static class ProjectionHoverRuntimeFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --projection-hover-runtime-preview in a fresh process.");
        string output = Path.GetFullPath(Environment.GetEnvironmentVariable("FLOATCANVAS_HOVER_OUTPUT") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvasHoverPreview", Guid.NewGuid().ToString("N")));
        string profile = Path.Combine(output, "profile");
        Directory.CreateDirectory(profile);
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Theme = "light", Language = "zh-CN", PreferGpuAcceleration = false,
            ShowFloatingLogo = false, PrimaryScreenOnly = true, Toggle = new(3, 0x78), Hide = new(7, 0x78) }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var frame = new DispatcherFrame();
        DesktopSession? session = null; OverlayHost? host = null; Window? backdrop = null;
        bool started = false, ready = false, timedOut = false;
        Exception? failure = null;
        var timeout = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { timedOut = true; timeout.Stop(); frame.Continue = false; };
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!).First(monitor => monitor.IsPrimary);
            var tile = new PixelBounds(primary.WorkArea.Left + 24, primary.WorkArea.Top + 24, 640, 360);
            if (tile.Right > primary.WorkArea.Right || tile.Bottom > primary.WorkArea.Bottom)
                throw new InvalidOperationException("The preview needs room for its bounded 640 x 360 tile.");
            Func<IReadOnlyList<MonitorInfo>> monitors = () => [primary with { Bounds = tile, WorkArea = primary.WorkArea }];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
            // Do not call Start: hotkey registration and startup recovery belong to the real application entry.
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var toolbar = Field<Window>(session, "_toolbar");
            toolbar.ShowActivated = false; toolbar.WindowStartupLocation = WindowStartupLocation.Manual;
            toolbar.Title = "FloatCanvas Hover Preview Toolbar";
            Set(session, "_toolbarPlaced", true);
            var editor = Field<EditorController>(session, "_editor");
            editor.Tool = EditorTool.Rectangle;
            var document = editor.Session.Document;
            var matrix = Projective2D.FromAffine(Affine2D.Translation(tile.Left + 220, tile.Top))
                .Compose(new(1, 0, 0, 0, -1, 200, 0, -.01, 1));
            document.Canvas = new() { BackgroundColor = 0xB8F1F5F9, Grid = new() { Visible = true, Color = 0x70526468, Width = 1 },
                Projection = new() { Enabled = true, Columns = 8, Rows = 6, CellSize = 10, Origin = new(0, 0), Matrix = matrix,
                    Quad = new PointD[] { new(0, 0), new(80, 0), new(80, 60), new(0, 60) }.Select(matrix.TransformPoint).ToList() } };
            SceneValidation.Validate(document);
            var mapping = CanvasMapping.Create(document);
            check("hover preview has a real screen horizon and editable ground in its bounded tile", !mapping.IsScreenSafe(new(tile.Left + 320, tile.Top + 50)) &&
                mapping.IsScreenSafe(new(tile.Left + 320, tile.Top + 180)) && !Field<CanvasCalibrationController>(session, "_canvasCalibration").IsActive);
            new RecoveryStore(Path.Combine(profile, "recovery")).SaveAsync(new() { LiveDocument = document.Clone() }, editor.Session.Assets).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "initial-scene.json"), JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));

            var start = new Button { Content = "开始 90 秒透视悬停预览", MinWidth = 220, MinHeight = 38, Margin = new Thickness(0, 18, 0, 0) };
            var instructions = new StackPanel { Margin = new Thickness(28), VerticalAlignment = VerticalAlignment.Center };
            instructions.Children.Add(new TextBlock { Text = "独立 640 × 360 画布预览", FontSize = 20, FontWeight = FontWeights.SemiBold });
            instructions.Children.Add(new TextBlock { Text = "点击开始后，在画布上方禁区与下方网格之间移动，再移到旁边工具条。\n仅使用本预览窗口；90 秒后自动关闭。",
                TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 12, 0, 0) });
            instructions.Children.Add(start);
            var backdropBrush = new SolidColorBrush(Color.FromRgb(249, 250, 251));
            backdrop = new Window { Title = "FloatCanvas Projection Hover Preview", Content = instructions, Background = backdropBrush,
                Foreground = Brushes.Black, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true,
                ShowInTaskbar = true, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = tile.Left * 96d / primary.DpiX, Top = tile.Top * 96d / primary.DpiY,
                Width = tile.Width * 96d / primary.DpiX, Height = tile.Height * 96d / primary.DpiY };
            backdrop.Closed += (_, _) => frame.Continue = false;
            host.EditingFocusLost += () =>
            {
                Console.WriteLine("PREVIEW_STATE editing=false reason=external-focus"); Console.Out.Flush();
                if (started) frame.Continue = false;
            };
            bool? lastUnsafe = null;
            void ReportHover(string reason)
            {
                bool unsafeHover = (bool)Invoke(session, "ShouldShowProjectionUnsafeHover")!;
                if (lastUnsafe == unsafeHover) return;
                lastUnsafe = unsafeHover;
                Console.WriteLine($"PREVIEW_HOVER unsafe={unsafeHover} reason={reason} editing={host.IsEditing} elements={editor.Session.Document.Elements.Count}");
                Console.Out.Flush();
                SaveCanvas(unsafeHover ? "canvas-unsafe.png" : reason == "PointerLeft" ? "canvas-cleared-on-leave.png" : "canvas-safe.png");
            }
            void SaveCanvas(string name)
            {
                // Render only this product canvas onto a plain white surface. No desktop capture.
                using var surface = SKSurface.Create(new SKImageInfo(tile.Width, tile.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(SKColors.White);
                surface.Canvas.Translate(-tile.Left, -tile.Top);
                Invoke(session, "Draw", surface);
                using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(output, name), png.ToArray());
            }
            host.Pointer += pointer => ReportHover(pointer.Kind.ToString());
            host.PointerLeft += () => ReportHover("PointerLeft");
            start.Click += (_, _) =>
            {
                if (started) return;
                started = true; start.IsEnabled = false;
                // Continue only from this human click. Production SetEditing performs its normal
                // own-window focus handoff; this fixture never forces an external foreground away.
                app.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                {
                    try
                    {
                        backdrop.Content = new Border { Background = backdropBrush };
                        // Once the human has activated it, keep the white backdrop in the
                        // normal band so overlay z-order repair cannot raise it over canvas ink.
                        backdrop.Topmost = false;
                        toolbar.Show(); toolbar.UpdateLayout();
                        PlaceToolbar(toolbar, primary, tile);
                        host.SetVisible(true);
                        bool entered = (bool)Invoke(session, "SetEditing", true, false)!;
                        if (!entered) throw new InvalidOperationException("The human-started preview could not enter its own editing surface.");
                        Invoke(session, "RequestRender");
                        Invoke(session, "RefreshToolbarState"); toolbar.UpdateLayout();
                        SaveToolbar(toolbar, primary, Path.Combine(output, "toolbar.png"));
                        SaveCanvas("canvas-ready.png");
                        ready = true;
                        var toolbarOrigin = toolbar.PointToScreen(new Point());
                        Console.WriteLine($"PREVIEW_READY title=FloatCanvas Projection Hover Preview pid={Environment.ProcessId} input={host.Diagnostics.Single().InputHandle:X} " +
                            $"bounds={tile.Left},{tile.Top},{tile.Width},{tile.Height} horizonY={tile.Top + 100} safetyY={tile.Top + 110} " +
                            $"toolbar={toolbarOrigin.X},{toolbarOrigin.Y},{toolbar.ActualWidth * primary.DpiX / 96d},{toolbar.ActualHeight * primary.DpiY / 96d} " +
                            $"editing={host.IsEditing} calibration=false registerHotkeys=false profile={profile} timeoutSeconds=90");
                        Console.Out.Flush();
                        Console.WriteLine("PROFILE " + profile); Console.Out.Flush();
                    }
                    catch (Exception error) { failure = error; frame.Continue = false; }
                });
            };
            backdrop.Show();
            Console.WriteLine($"PREVIEW_START_REQUIRED title={backdrop.Title} hwnd={new WindowInteropHelper(backdrop).Handle:X} " +
                $"bounds={tile.Left},{tile.Top},{tile.Width},{tile.Height} profile={profile} registerHotkeys=false timeoutSeconds=90");
            Console.Out.Flush();
            timeout.Start(); Dispatcher.PushFrame(frame);
            if (failure is not null) throw failure;
            check("hover preview stayed within one bounded canvas tile", host.Diagnostics.Count == 1 && host.Diagnostics[0].Bounds == tile);
            Console.WriteLine($"PREVIEW_FINISHED ready={ready} timedOut={timedOut} profile={profile}"); Console.Out.Flush();
        }
        finally
        {
            timeout.Stop();
            host?.ExitEditing(restorePreviousForeground: false);
            if (session is not null) session.Dispose(); else host?.Dispose();
            backdrop?.Close();
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.Shutdown();
        }
    }

    private static void PlaceToolbar(Window toolbar, MonitorInfo monitor, PixelBounds tile)
    {
        double width = toolbar.ActualWidth * monitor.DpiX / 96d, height = toolbar.ActualHeight * monitor.DpiY / 96d;
        double left, top;
        if (tile.Right + 20 + width <= monitor.WorkArea.Right)
        { left = tile.Right + 20; top = tile.Top; }
        else if (tile.Bottom + 20 + height <= monitor.WorkArea.Bottom)
        { left = monitor.WorkArea.Left + 24; top = tile.Bottom + 20; }
        else throw new InvalidOperationException("The preview needs room outside its tile for the production toolbar.");
        toolbar.Left = left * 96d / monitor.DpiX; toolbar.Top = top * 96d / monitor.DpiY;
    }
    private static void SaveToolbar(Window toolbar, MonitorInfo monitor, string path)
    {
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(toolbar.ActualWidth * monitor.DpiX / 96d)),
            Math.Max(1, (int)Math.Ceiling(toolbar.ActualHeight * monitor.DpiY / 96d)), monitor.DpiX, monitor.DpiY, PixelFormats.Pbgra32);
        image.Render(toolbar);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
}
