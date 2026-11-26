using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

internal static class EmojiFontDownloadTests
{
    internal static void Run(Action<string, bool> check, string source)
    {
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_EMOJI_DOWNLOAD_TEST_DIR") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvas-EmojiDownload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string profile = Path.Combine(output, Guid.NewGuid().ToString("N"));
        EmojiFontDownloads.Configure([]);
        const string release = "https://github.com/yowynn/FloatCanvas/releases/download/v0.4.2/";
        check("the three optional fonts resolve to their fixed release assets",
            EmojiFontDownloads.DownloadUri(EmojiFontIds.NotoColor).AbsoluteUri == release + "Noto-COLRv1.ttf" &&
            EmojiFontDownloads.DownloadUri(EmojiFontIds.OpenMojiColor).AbsoluteUri == release + "OpenMoji-COLRv0.ttf" &&
            EmojiFontDownloads.DownloadUri(EmojiFontIds.BlobmojiColor).AbsoluteUri == release + "Blobmoji-COLRv1.ttf" &&
            !EmojiFontDownloads.CanDownload(EmojiFontIds.SystemSegoe));
        ApplicationPaths.Configure(["--profile", profile, EmojiFontDownloads.LocalSourceArgument, source]);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var host = new HwndSource(new HwndSourceParameters("FloatCanvas.EmojiDownloadTest")
        { Width = 8, Height = 8, PositionX = -20000, PositionY = -20000, WindowStyle = unchecked((int)0x80000000) });
        using var scope = new OverlayWindowScope(host.Handle, () => true);
        AppTheme.Apply("light");
        var picker = new EmojiFontPicker();
        var owner = new Window { Content = picker, Width = 310, Height = 70, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        OverlayWindowScope.Prepare(owner);
        owner.Show();
        try
        {
            string[] fonts = [EmojiFontIds.NotoColor, EmojiFontIds.OpenMojiColor, EmojiFontIds.BlobmojiColor];
            const string emoji = "😀", flag = "🇨🇳";
            check("the three app fonts are missing and Windows is available in the isolated profile",
                fonts.All(id => !EmojiFontSources.IsAvailable(id)) && EmojiGlyphs.CanRender(emoji, EmojiFontIds.SystemSegoe));
            string lockedFont = EmojiFontSources.PathFor(EmojiFontIds.BlobmojiColor);
            Directory.CreateDirectory(Path.GetDirectoryName(lockedFont)!);
            using (File.Create(lockedFont))
                check("the optional-font diagnostics tolerate an exclusively opened face",
                    !EmojiBoundsProfile.FingerprintMismatches().Any(mismatch => mismatch.FontId == EmojiFontIds.BlobmojiColor));
            File.Delete(lockedFont);
            var store = new ProjectStore();
            var document = new SceneDocument { Viewport = new(0, 0, 600, 180) };
            var assets = new AssetLibrary();
            foreach (var (font, index) in fonts.Select((id, index) => (id, index)))
                document.Elements.Add(Element(font, emoji, 24 + index * 124, 24));
            document.Elements.Add(Element(EmojiFontIds.NotoColor, flag, 396, 24));
            string path = Path.Combine(output, "表情字体回退测试.floatcanvas");
            store.SaveAsync(path, document, assets).GetAwaiter().GetResult();
            var reopened = store.LoadAsync(path).GetAwaiter().GetResult();
            check("opening a project with missing fonts retains its original font identifiers",
                reopened.Document.Elements.Select(element => element.Style.EmojiFontId).SequenceEqual(fonts.Append(EmojiFontIds.NotoColor)));
            using var renderer = new SceneRenderer();
            var rendered = renderer.ExportPng(reopened.Document, reopened.Assets, transparent: true, useAuthoredFrame: false);
            File.WriteAllBytes(Path.Combine(output, "fonts-missing.png"), rendered);
            check("the missing Google, OpenMoji and Blobmoji faces draw via Windows",
                fonts.All(id => !EmojiGlyphs.CanRender(emoji, id) && EmojiGlyphs.Measure(emoji, 96, id).MissingGlyphCount == 0));
            check("when every available face lacks the flag, the project draws a placeholder and remains renderable",
                EmojiGlyphs.Measure(flag, 96, EmojiFontIds.NotoColor).MissingGlyphCount > 0 &&
                HasVisiblePixels(rendered, 396, 24));
            picker.SetValue(EmojiFontIds.NotoColor);
            picker.SelectedIndex = 2;
            check("an option whose availability has not yet been checked cannot change the saved style",
                picker.Value == EmojiFontIds.NotoColor);
            picker.RefreshAvailability();
            check("an existing missing style remains selected in the inspector model", picker.Value == EmojiFontIds.NotoColor);
            picker.SetValue(EmojiFontIds.SystemSegoe);
            picker.IsDropDownOpen = true;
            picker.RefreshAvailability();
            var popup = picker.Template.FindName("PART_Popup", picker) as OverlayPopup;
            check("the style dropdown uses the interactive hosted popup",
                popup is { IsOpen: true, Child: not null });
            foreach (string font in fonts)
            {
                var item = picker.Items.Cast<ComboBoxItem>().Single(option => Equals(option.Tag, font));
                item.ApplyTemplate();
                var button = (Button)item.Template.FindName("Download", item);
                check(font + " displays a download action for the missing face", button is { IsEnabled: true } &&
                    Equals(button.Content, L.Get("Emoji.Font.Download")));
                picker.SelectedItem = item;
                check(font + " cannot change the current style until installed", picker.Value == EmojiFontIds.SystemSegoe);
                IInputElement DownloadHit()
                {
                    var content = popup!.Child!;
                    content.UpdateLayout();
                    var hit = content.InputHitTest(button!.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), content));
                    return hit is DependencyObject target && OverlayPopupWindow.BelongsTo(target, button)
                        ? hit : throw new InvalidOperationException("The rendered download button was not the pointer hit target.");
                }
                void ClickDownload()
                {
                    PopupOpeningGestureTests.Press(DownloadHit(), MouseButton.Left);
                    check(font + " mouse press leaves the popup and current selection in place",
                        picker.IsDropDownOpen && picker.Value == EmojiFontIds.SystemSegoe && button!.IsEnabled);
                    PopupOpeningGestureTests.Release(DownloadHit(), MouseButton.Left);
                }
                if (font == EmojiFontIds.NotoColor)
                {
                    EmojiFontDownloads.Configure([EmojiFontDownloads.LocalSourceArgument, new Uri(new Uri(source), "missing/").AbsoluteUri]);
                    try
                    {
                        ClickDownload();
                        PumpUntil(() => Equals(button!.Content, L.Get("Emoji.Font.DownloadFailed")));
                        check("a failed download leaves the font unavailable and offers a retry",
                            button!.IsEnabled && !File.Exists(EmojiFontSources.PathFor(font)) && picker.Value == EmojiFontIds.SystemSegoe);
                    }
                    finally { EmojiFontDownloads.Configure([EmojiFontDownloads.LocalSourceArgument, source]); }
                }
                else if (font == EmojiFontIds.OpenMojiColor)
                {
                    var installedGoogle = picker.Items.Cast<ComboBoxItem>().Single(option => Equals(option.Tag, EmojiFontIds.NotoColor));
                    PopupOpeningGestureTests.Press(DownloadHit(), MouseButton.Left);
                    PopupOpeningGestureTests.Release(installedGoogle, MouseButton.Left);
                    check("dragging away from Download cancels the click without choosing the row under the pointer",
                        picker.IsDropDownOpen && button!.IsEnabled && picker.Value == EmojiFontIds.SystemSegoe &&
                        !File.Exists(EmojiFontSources.PathFor(font)));
                }
                ClickDownload();
                PumpUntil(() => File.Exists(EmojiFontSources.PathFor(font)) &&
                    Equals(item.Content, L.Get(font == EmojiFontIds.NotoColor ? "Emoji.Font.Noto" :
                        font == EmojiFontIds.OpenMojiColor ? "Emoji.Font.OpenMoji" : "Emoji.Font.Blobmoji")));
                check(font + " installs the actual verified font and clears the unavailable cache",
                    EmojiFontSources.Sha256(font) == EmojiBoundsProfile.For(font).ExpectedSha256 && EmojiGlyphs.CanRender(emoji, font));
                check(font + " downloads without altering the selected style", picker.Value == EmojiFontIds.SystemSegoe);
            }
            check("the flag switches to the downloaded vector font without reopening the project",
                EmojiGlyphs.CanRender(flag, EmojiFontIds.NotoColor) && EmojiGlyphs.Measure(flag, 96, EmojiFontIds.NotoColor).MissingGlyphCount == 0);
            var downloadedStyle = picker.Items.Cast<ComboBoxItem>().Single(option => Equals(option.Tag, EmojiFontIds.OpenMojiColor));
            PopupOpeningGestureTests.Press(downloadedStyle, MouseButton.Left);
            PopupOpeningGestureTests.Release(downloadedStyle, MouseButton.Left);
            check("mouse clicks on an installed font still select the style and dismiss the popup",
                picker.Value == EmojiFontIds.OpenMojiColor && !picker.IsDropDownOpen);
            var installed = renderer.ExportPng(reopened.Document, reopened.Assets, transparent: true, useAuthoredFrame: false);
            File.WriteAllBytes(Path.Combine(output, "fonts-installed.png"), installed);
            check("the reopened project uses the installed faces while keeping the same style identifiers",
                !rendered.AsSpan().SequenceEqual(installed) &&
                reopened.Document.Elements.Select(element => element.Style.EmojiFontId).SequenceEqual(fonts.Append(EmojiFontIds.NotoColor)));
        }
        finally { owner.Close(); }
    }

    private static CanvasElement Element(string font, string emoji, int x, int y) => new()
    {
        Kind = ElementKind.Emoji, Width = 96, Height = 96, Text = emoji, Transform = Affine2D.Translation(x, y),
        Style = new() { EmojiFontId = font }
    };

    private static bool HasVisiblePixels(byte[] png, int left, int top)
    {
        using var bitmap = SKBitmap.Decode(png);
        return Enumerable.Range(top, 96).Any(y => Enumerable.Range(left, 96).Any(x => bitmap.GetPixel(x, y).Alpha > 0));
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!ready()) throw new TimeoutException("The actual font download did not finish.");
    }
}
