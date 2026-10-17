using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Windows;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

/// <summary>Focused startup content, persistent-preference boundaries and geometry checks. No HWNDs or hooks.</summary>
internal static class WelcomeSessionTests
{
    internal static void Run(Action<string, bool> check)
    {
        var app = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        string preference = L.Preference;
        try
        {
            check("fresh toggle is Ctrl+backtick without Alt", HotkeyBinding.DefaultToggle == new HotkeyBinding(2, 0xC0));
            check("fresh hide is Ctrl+Shift+backtick", HotkeyBinding.DefaultHide == new HotkeyBinding(6, 0xC0));
            foreach (var pair in new (uint Key, string Label)[] { (0xC0,"`"), (0x31,"1"), (0xBA,";"), (0xBB,"="), (0xDB,"["), (0xDC,"\\"), (0xDE,"'"), (0x08,"Backspace"), (0x21,"Page Up"), (0x6B,"Num +") })
                check("readable key cap " + pair.Key, HotkeyDisplay.KeyName(pair.Key) == pair.Label);
            var settings = JsonSerializer.Deserialize<UserSettings>("""{"Toggle":{"Modifiers":3,"VirtualKey":70},"Hide":{"Modifiers":7,"VirtualKey":70},"ToolbarX":123,"ToolbarY":456,"ToolbarExpanded":false,"Theme":"dark","Language":"en","PrimaryScreenOnly":false}""")!;
            check("existing custom hotkeys survive without silent migration", settings.Toggle == new HotkeyBinding(3,70) && settings.Hide == new HotkeyBinding(7,70));
            check("restarting discards saved UI position and expansion", settings.ToolbarX is null && settings.ToolbarY is null && settings.ToolbarExpanded);
            settings.ToolbarX = 15; settings.ToolbarY = 35; settings.ToolbarExpanded = false;
            string saved = JsonSerializer.Serialize(settings);
            check("UI state remains usable within a process but is never serialized", settings.ToolbarX == 15 && !settings.ToolbarExpanded && !saved.Contains("ToolbarX") && !saved.Contains("ToolbarY") && !saved.Contains("ToolbarExpanded"));
            var reloaded = JsonSerializer.Deserialize<UserSettings>(saved)!;
            check("persistent user preferences survive UI reset", reloaded.Theme == "dark" && reloaded.Language == "en" && !reloaded.PrimaryScreenOnly && reloaded.Toggle == settings.Toggle);
            foreach (var reason in Enum.GetValues<KeyboardLeaseEndReason>())
            {
                bool expected = reason is KeyboardLeaseEndReason.HeartbeatExpired or KeyboardLeaseEndReason.QueueOverflow or KeyboardLeaseEndReason.NotificationFailed or KeyboardLeaseEndReason.HookFailure;
                check("keyboard release notification policy " + reason, DesktopSession.ShouldNotifyKeyboardRelease(reason) == expected);
            }
            foreach (uint dpi in new uint[] {96,144,192})
            {
                var monitor = new MonitorInfo("top-taskbar",new(-1920,0,1920,1080),new(-1920,48,1920,1032),dpi,dpi,true);
                var origin = SessionWindowLayout.ToolbarOrigin(monitor,680,100,null,null);
                var leftTaskbar=monitor with {WorkArea=new(-1872,0,1872,1080)};
                var leftOrigin=SessionWindowLayout.ToolbarOrigin(leftTaskbar,680,100,null,null);
                check(dpi + " default toolbar centers on the physical screen even with a left taskbar", Math.Abs(origin.X + 680*dpi/192d - (-960)) < .001 && Math.Abs(leftOrigin.X+680*dpi/192d-(-960))<.001);
                check(dpi + " crown starts at the physical screen top even with a top taskbar", origin.Y == monitor.Bounds.Top && leftOrigin.Y==monitor.Bounds.Top);
                var moved = SessionWindowLayout.ToolbarOrigin(monitor,680,100,-1800,200);
                check(dpi + " valid in-process toolbar position survives", moved == new OverlayPoint(-1800,200));
                var clamped = SessionWindowLayout.ToolbarOrigin(monitor,680,100,10000,-1000);
                var bottomTaskbar=monitor with {WorkArea=new(-1920,0,1920,1032)};
                var bottomOrigin=SessionWindowLayout.ToolbarOrigin(bottomTaskbar,680,100,-10000,10000);
                check(dpi + " saved geometry keeps the mascot on screen and avoids a bottom taskbar", clamped.Y == monitor.Bounds.Top && clamped.X + 680*dpi/96d <= monitor.Bounds.Right + .001 && bottomOrigin.X==monitor.Bounds.Left && bottomOrigin.Y+100*dpi/96d<=bottomTaskbar.WorkArea.Bottom+.001);
            }
            foreach (string language in new[] {"zh-CN","en"})
            foreach (string theme in new[] {"light","dark"})
            {
                L.Initialize(language); AppTheme.Apply(theme);
                int updateRequests = 0;
                var view = new WelcomeView(HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, () => {}, () => updateRequests++);
                var host = new Controls.Border {Width=430, Child=view};
                host.SetResourceReference(Controls.Border.BackgroundProperty,"CanvasSurface");
                host.Measure(new Wpf.Size(430,double.PositiveInfinity)); host.Arrange(new Wpf.Rect(0,0,430,host.DesiredSize.Height)); host.UpdateLayout();
                check(language + theme + " code shortcuts are not text inputs", view.ToggleShortcut.Text == "Ctrl+`" && view.HideShortcut.Text == "Ctrl+Shift+`" && !Descendants(view).OfType<Controls.TextBox>().Any());
                check(language + theme + " primary action includes the current shortcut", view.StartButton.Content?.ToString() == L.Format("Acceptance2.Welcome.Start","Ctrl+`"));
                check(language + theme + " shortcut decoration has no input border", view.ToggleShortcut.Parent is Controls.Border code && code.BorderThickness == new Wpf.Thickness(0) && code.CornerRadius == new Wpf.CornerRadius(4));
                check(language + theme + " footer shows version and exact attribution with color emoji artwork", Descendants(view).OfType<Controls.TextBlock>().Any(text=>text.Text.StartsWith("v0.")) &&
                    Descendants(view).OfType<Controls.TextBlock>().Any(text=>text.Text.StartsWith("By 𝗛𝗘𝗣𝗧𝗔𝗬𝗢 with ")) &&
                    Descendants(view).OfType<Controls.Image>().Any(image => ReferenceEquals(image.Source, EmojiArtwork.Image("❤️")) && image.Width == 12));
                view.VersionButton.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
                check(language + theme + " version opens updates once without taking footer space", updateRequests == 1 && view.VersionButton.Height == 18 && view.VersionButton.HorizontalAlignment == Wpf.HorizontalAlignment.Left);
                view.StartButton.ApplyTemplate();
                var chrome = (Controls.Border)view.StartButton.Template.FindName("Chrome",view.StartButton);
                check(language + theme + " start action paints blue instead of transparent", ((Media.SolidColorBrush)chrome.Background).Color == ((Media.SolidColorBrush)app.Resources["CanvasPrimary"]).Color);
                check(language + theme + " primary normal hover and pressed are distinct", Color(app,"CanvasPrimary") != Color(app,"CanvasPrimaryHover") && Color(app,"CanvasPrimaryHover") != Color(app,"CanvasPrimaryPressed") && view.StartButton.BorderThickness == new Wpf.Thickness(0));
                var ordinary = new Controls.Button {Content="Save",Width=100,Height=36};
                ordinary.Measure(new(100,36)); ordinary.Arrange(new(0,0,100,36)); ordinary.ApplyTemplate();
                var ordinaryChrome = (Controls.Border)ordinary.Template.FindName("Chrome",ordinary);
                check(language + theme + " ordinary buttons have visible no-stroke surfaces", ((Media.SolidColorBrush)ordinaryChrome.Background).Color != Color(app,"CanvasSurface") && ordinary.BorderThickness == new Wpf.Thickness(0));
                var pixels = new Imaging.RenderTargetBitmap(430,(int)Math.Ceiling(host.ActualHeight),96,96,Media.PixelFormats.Pbgra32); pixels.Render(host);
                check(language + theme + " welcome content fits its measured compact surface", host.ActualHeight > 220 && host.ActualHeight < 450 && view.ActualWidth > 350);
                check(language + theme + " welcome omits the redundant startup paragraph", !Descendants(view).OfType<Controls.TextBlock>().Any(text => text.Text == L.Get("Acceptance2.Welcome.Description")) &&
                    Descendants(view).OfType<Controls.TextBlock>().Any(text => text.Text == L.Get("Acceptance2.Welcome.ToggleVisibility")));
                string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_WELCOME_PREVIEW_DIR");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); var png = new Imaging.PngBitmapEncoder(); png.Frames.Add(Imaging.BitmapFrame.Create(pixels));
                    using var stream = File.Create(Path.Combine(output,$"welcome-{language}-{theme}.png")); png.Save(stream);
                }
                view.Refresh(new(6,0x47),new(6,0x48));
                check(language + theme + " settings refresh both hints and start action", view.ToggleShortcut.Text == "Ctrl+Shift+G" && view.HideShortcut.Text == "Ctrl+Shift+H" && view.StartButton.Content?.ToString()?.Contains("Ctrl+Shift+G") == true);
            }
            check("welcome checks create no visible or native windows", app.Windows.Cast<Wpf.Window>().All(window=>!window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0));
        }
        finally {L.Initialize(preference);}
    }

    private static Media.Color Color(Wpf.Application app,string key) => ((Media.SolidColorBrush)app.Resources[key]).Color;
    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        for (int i=0;i<Media.VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=Media.VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
}
