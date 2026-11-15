using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

/// <summary>Detached update UI and fake HTTP only; no visible windows, live GitHub or installed files.</summary>
internal static class AppUpdateWindowTests
{
    // Interactive acceptance fixture only; the shipping application has no mock-feed switch.
    internal static void ShowPreview(string[] args)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        bool english = args.Contains("en");
        L.Initialize(english ? "en" : "zh-CN"); AppTheme.Apply(args.Contains("dark") ? "dark" : "light");
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CHANGELOG.md"));
        string next = source.Split("## [Unreleased]", StringSplitOptions.None)[1].Split("\n## [", StringSplitOptions.None)[0];
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(ReleaseMetadata.Replace("0.3.0", "0.4.0"))!.AsArray();
        metadata[0]!["body"] = next;
        foreach (string version in new[] { "0.3.2", "0.3.1", "0.3.0" })
        {
            string notes = source.Split("## [" + version + "]", StringSplitOptions.None)[1];
            notes = notes[(notes.IndexOf('\n') + 1)..].Split("\n## [", StringSplitOptions.None)[0];
            metadata.Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { tag_name = "v" + version, body = notes })));
        }
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Task.FromResult(JsonResponse(metadata.ToJsonString()));
            throw new HttpRequestException(english
                ? "Simulated download failure. Use Download manually to visit the official releases page."
                : "已模拟下载失败。请点击“手动下载安装包”前往官方发布页。");
        }));
        using var client = new GitHubUpdateClient(http);
        var window = new AppUpdateWindow("0.2.1", client, _ => throw new InvalidOperationException(english
                ? "This preview does not install updates." : "此预览不会安装更新。"),
            true, Path.Combine(Path.GetTempPath(), "FloatCanvas-update-preview"),
            checkInstallDirectory: () => args.Contains("blocked")
                ? (english ? "Package metadata is missing (simulated)." : "缺少安装包元数据（模拟场景）。") : null,
            openLink: uri =>
            {
                // The target release is simulated. Send users to real published downloads instead of a nonexistent tag.
                Uri destination = uri == GitHubUpdateClient.ReleaseUrl("0.4.0") ? GitHubUpdateClient.ReleasesUrl : uri;
                _ = Process.Start(new ProcessStartInfo(destination.AbsoluteUri) { UseShellExecute = true });
            });
        window.Title += english ? " · Preview (simulated releases)" : " · 预览（模拟版本）";
        window.ManualDownloadButton.ToolTip = english
            ? "Open the official releases page in your browser. The simulated v0.4.0 is not a published download."
            : "在浏览器中打开官方发布列表。模拟的 v0.4.0 不代表已发布的下载包。";
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var priorContext = SynchronizationContext.Current;
        var priorLanguage = L.Preference;
        var initialWindows = app.Windows.Cast<Window>().ToArray();
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_UPDATE_UI_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-updates-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            RegularWindowDuringOverlayScope(output, check);
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(ReleaseMetadata))));
                using var client = new GitHubUpdateClient(http);
                var links = new List<Uri>();
                var window = new AppUpdateWindow("0.2.0-preview.1", client, _ => Task.CompletedTask, true, Path.Combine(output, "cache"), autoCheck: false, openLink: links.Add);
                try
                {
                    Await(window.CheckAsync());
                    string context = language + " " + theme;
                    check(context + " update check enables installing and shows latest version", window.InstallButton.IsEnabled && window.LatestVersionText.Text == "v0.3.0" && window.StatusText.Text == L.Get("Updates.Available"));
                    check(context + " release notes use a classified single-language document with source versions",
                        window.NotesText.Text.Contains(L.Get("Updates.Category.feature")) && window.NotesText.Text.Contains("**v0.3.0**") &&
                        window.NotesText.Document.Blocks.Count > 0 &&
                        (language == "en" ? !window.NotesText.Text.Contains("独立画布") : !window.NotesText.Text.Contains("Independent canvases")));
                    var firstItem = window.NotesText.Document.Blocks.OfType<System.Windows.Documents.List>().First().ListItems.FirstListItem;
                    var versionLabel = ((System.Windows.Documents.Paragraph)firstItem.Blocks.LastBlock).Inlines.OfType<System.Windows.Documents.Bold>().Last();
                    check(context + " version attribution is visually secondary", versionLabel.FontSize == 12 && versionLabel.FontWeight == FontWeights.Normal);
                    check(context + " repository link is always available and points to this project", window.RepositoryLink.NavigateUri?.AbsoluteUri == "https://github.com/yowynn/FloatCanvas");
                    window.ChangelogButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(context + " full changelog action opens the GitHub changelog file", links.Single().AbsoluteUri == "https://github.com/yowynn/FloatCanvas/blob/main/CHANGELOG.md" && Equals(window.ChangelogButton.Content, L.Get("Updates.MoreLogs")));
                    check(context + " update title identifies an ordinary application window", window.Title == WindowTitles.Format(L.Get("Updates.Title"), true) && !window.Topmost && window.ShowInTaskbar);
                    check(context + " update panel uses shared floating chrome without native min/max controls", window.Template is not null && window.WindowStyle == WindowStyle.None && window.ResizeMode == ResizeMode.CanResize);
                    var body = (FrameworkElement)window.Content;
                    window.Content = null;
                    var host = new Border { Width = 498, Height = 450, Child = body };
                    host.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
                    host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
                    host.Measure(new(498, 450)); host.Arrange(new(0, 0, 498, 450)); host.UpdateLayout();
                    check(context + " controls fit the compact update surface", window.NotesText.ActualHeight >= 90 && window.InstallButton.ActualWidth > 70 && window.CheckButton.ActualWidth > 70 && body.ActualWidth < 498);
                    check(context + " changelog action is visible beside the notes heading", window.ChangelogButton.ActualWidth > 70 && window.ChangelogButton.TranslatePoint(new Point(), body).Y < window.NotesText.TranslatePoint(new Point(), body).Y);
                    var pixels = new RenderTargetBitmap(498, 450, 96, 96, PixelFormats.Pbgra32); pixels.Render(host);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
                    using (var file = File.Create(Path.Combine(output, "updates-" + language + "-" + theme + ".png"))) encoder.Save(file);
                    host.Child = null; window.Content = body;
                }
                finally { window.Close(); }
            }
            L.Initialize("zh-CN"); AppTheme.Apply("light");
            NoUpdateAndError(output, check);
            ManualFallbacks(output, check);
            FullHistoryPreview(output, check);
            DirectoryRepairRecheck(output, check);
            PreviewChannelAndCurrentNotes(output, check);
            SingleCheckAndClose(output, check);
            DownloadCancellation(output, check);
            PreparationAndCommit(output, check);
            MachineSetupUpdates(output, check);
            check("update UI fixtures leave no additional application windows", app.Windows.Cast<Window>().SequenceEqual(initialWindows));
        }
        finally
        {
            foreach (var window in app.Windows.Cast<Window>().Where(w => !initialWindows.Contains(w)).ToArray()) window.Close();
            L.Initialize(priorLanguage); AppTheme.Apply("light"); SynchronizationContext.SetSynchronizationContext(priorContext);
        }
    }

    private static void FullHistoryPreview(string output, Action<string, bool> check)
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CHANGELOG.md"));
        string unreleased = source.Split("## [Unreleased]", StringSplitOptions.None)[1].Split("\n## [", StringSplitOptions.None)[0];
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(ReleaseMetadata.Replace("0.3.0", "0.4.0"))!.AsArray();
        metadata[0]!["body"] = unreleased;
        metadata.Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new {
            tag_name = "v0.3.2", body = "- 修复旧版跨版本更新。\n- Fix upgrades from older versions." })));
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(language); AppTheme.Apply(theme);
            using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(metadata.ToJsonString()))));
            using var client = new GitHubUpdateClient(http);
            var window = new AppUpdateWindow("0.2.1", client, _ => Task.CompletedTask, true, output, autoCheck: false);
            try
            {
                Await(window.CheckAsync());
                var body = (FrameworkElement)window.Content; window.Content = null;
                var host = new Border { Width = 638, Height = 580, Child = body };
                host.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
                host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
                host.Measure(new(638, 580)); host.Arrange(new(0, 0, 638, 580)); host.UpdateLayout();
                check(language + " " + theme + " full history keeps scroll space and manual action inside the surface",
                    window.InstallButton.IsEnabled && window.NotesText.ActualHeight > 260 &&
                    window.ManualDownloadButton.TranslatePoint(new Point(0, window.ManualDownloadButton.ActualHeight), host).Y <= 580);
                var pixels = new RenderTargetBitmap(638, 580, 96, 96, PixelFormats.Pbgra32); pixels.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
                using (var file = File.Create(Path.Combine(output, "history-" + language + "-" + theme + ".png"))) encoder.Save(file);
                host.Child = null; window.Content = body;
            }
            finally { window.Close(); }
        }
        L.Initialize("zh-CN"); AppTheme.Apply("light");
    }

    private static void ManualFallbacks(string output, Action<string, bool> check)
    {
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (string scenario in new[] { "missing-manifest", "http-failure", "download-failure", "no-package" })
        {
            L.Initialize(language);
            var links = new List<Uri>();
            string metadata = scenario == "no-package"
                ? JsonSerializer.Serialize(new[] { new { tag_name = "v0.4.0", body = Notes, assets = Array.Empty<object>() } })
                : ReleaseMetadata;
            using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(
                scenario == "http-failure" || request.RequestUri!.Host != "api.github.com"
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : JsonResponse(metadata))));
            using var client = new GitHubUpdateClient(http);
            var window = new AppUpdateWindow("0.2.0", client, _ => throw new Exception("Must not install"),
                true, output, autoCheck: false, openLink: links.Add,
                checkInstallDirectory: () => scenario == "missing-manifest" ? "Missing PACKAGE-INFO.json" : null);
            try
            {
                Await(window.CheckAsync());
                if (scenario == "download-failure") Await(window.DownloadUpdateAsync());
                window.ManualDownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Uri expected = scenario == "http-failure" ? GitHubUpdateClient.ReleasesUrl :
                    GitHubUpdateClient.ReleaseUrl(scenario == "no-package" ? "0.4.0" : "0.3.0");
                check(language + " " + scenario + " retains a working official manual download and instructions",
                    links.Single() == expected && window.ManualDownloadButton.IsEnabled &&
                    window.ManualUpdateHint.Visibility == Visibility.Visible && window.ManualUpdateHint.Text.Contains("ZIP"));
                if (scenario == "missing-manifest" || scenario == "no-package")
                    check(scenario + " blocks automatic installation without hiding the newer release", !window.InstallButton.IsEnabled &&
                        window.LatestVersionText.Text != "v0.2.0");
                if (scenario == "missing-manifest")
                    RenderCurrentNotes(window, Path.Combine(output, "updates-manual-" + language + ".png"));
            }
            finally { window.Close(); }
        }
        L.Initialize("zh-CN");
    }

    private static void DirectoryRepairRecheck(string output, Action<string, bool> check)
    {
        foreach (string language in new[] { "zh-CN", "en" })
        {
            L.Initialize(language);
            using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(ReleaseMetadata))));
            using var client = new GitHubUpdateClient(http);
            string? issue = L.Format("Updates.IncompletePackage", "DEPENDENCIES.cdx.json");
            int applications = 0;
            var window = new AppUpdateWindow("0.2.1", client, _ => { applications++; return Task.CompletedTask; }, false,
                Path.Combine(output, "directory-repair-" + language), autoCheck: false, checkInstallDirectory: () => issue);
            try
            {
                Await(window.CheckAsync());
                check(language + " unavailable update identifies the missing package file", !window.InstallButton.IsEnabled &&
                    TextBlocks((DependencyObject)window.Content).Any(text => text.Text.Contains("DEPENDENCIES.cdx.json", StringComparison.Ordinal)));
                issue = null;
                Await(window.CheckAsync());
                check(language + " rechecking after a directory repair enables the existing update window", window.InstallButton.IsEnabled);
                issue = L.Format("Updates.IncompletePackage", "Docs/Release-Trust.md");
                Await(window.DownloadUpdateAsync()); window.PrepareForShow();
                check(language + " install revalidates package files before downloading or applying", applications == 0 && !window.InstallButton.IsEnabled &&
                    TextBlocks((DependencyObject)window.Content).Any(text => text.Text.Contains("Docs/Release-Trust.md", StringComparison.Ordinal)));
            }
            finally { window.Close(); }
        }

        static IEnumerable<TextBlock> TextBlocks(DependencyObject parent)
        {
            if (parent is TextBlock block) yield return block;
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                foreach (var text in TextBlocks(child)) yield return text;
        }
    }

    private static void MachineSetupUpdates(string output, Action<string, bool> check)
    {
        byte[] bytes = "MZdetached setup fixture"u8.ToArray();
        foreach (bool hasInstaller in new[] { false, true })
        foreach (string language in new[] { "zh-CN", "en" })
        {
            L.Initialize(language);
            string name = "FloatCanvas-0.3.0-win-x64" + (hasInstaller ? "-Setup.exe" : ".zip");
            string metadata = JsonSerializer.Serialize(new[] { new
            {
                tag_name = "v0.3.0", html_url = "https://github.com/yowynn/FloatCanvas/releases/tag/v0.3.0", body = "Release notes",
                assets = new[] { new { name, state = "uploaded", size = bytes.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), browser_download_url = "https://github.com/yowynn/FloatCanvas/releases/download/v0.3.0/" + name } }
            } });
            using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? JsonResponse(metadata) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            using var client = new GitHubUpdateClient(http);
            int zipCalls = 0, setupCalls = 0;
            var links = new List<Uri>();
            var window = new AppUpdateWindow("0.2.0", client, _ => { zipCalls++; return Task.CompletedTask; }, true,
                Path.Combine(output, "setup-cache"), autoCheck: false, openLink: links.Add,
                applyInstallerUpdate: (prepared, token, commit) =>
                {
                    token.ThrowIfCancellationRequested(); setupCalls++; commit();
                    check(language + " machine handoff receives the verified Setup only", prepared.Version == "0.3.0" && File.ReadAllBytes(prepared.InstallerPath).SequenceEqual(bytes));
                    return Task.CompletedTask;
                });
            try
            {
                Await(window.CheckAsync());
                check(language + " machine update describes installation scope and available action", window.DetailText.Text == L.Get(hasInstaller ? "InstallerUpdates.AllUsers" : "InstallerUpdates.NoSetup") && Equals(window.InstallButton.Content, L.Get(hasInstaller ? "InstallerUpdates.Install" : "InstallerUpdates.OpenRelease")));
                Await(window.DownloadUpdateAsync());
                check(language + " machine updates never invoke ZIP replacement", zipCalls == 0 && setupCalls == (hasInstaller ? 1 : 0));
                check(language + " missing Setup offers the exact release instead of claiming up to date", hasInstaller ? links.Count == 0 : links.Single().AbsoluteUri == "https://github.com/yowynn/FloatCanvas/releases/tag/v0.3.0");
            }
            finally { window.Close(); }
        }
    }

    private static void RegularWindowDuringOverlayScope(string output, Action<string, bool> check)
    {
        using var owner = new HwndSource(new HwndSourceParameters("FloatCanvas.UpdateWindowPolicy")
        { Width = 10, Height = 10, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000) });
        using var scope = new OverlayWindowScope(owner.Handle, () => false);
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse("[]"))));
        using var client = new GitHubUpdateClient(http);
        var window = new AppUpdateWindow("0.2.1", client, _ => Task.CompletedTask, true, output, autoCheck: false);
        try
        {
            Console.WriteLine($"UPDATE_WINDOW_POLICY topmost={window.Topmost} taskbar={window.ShowInTaskbar} transparent={window.AllowsTransparency} regular={OverlayWindowScope.IsRegularWindow(window)} nativeOwner={new WindowInteropHelper(window).Owner}");
            check("updates are ordinary taskbar windows even while an overlay scope exists", !window.Topmost && window.ShowInTaskbar && OverlayWindowScope.IsRegularWindow(window));
            check("update construction does not inherit overlay ownership or input policy", !window.AllowsTransparency && window.ShowActivated && new WindowInteropHelper(window).Owner == 0);
        }
        finally { window.Close(); }
    }

    private static void PreviewChannelAndCurrentNotes(string output, Action<string, bool> check)
    {
        L.Initialize("en");
        string metadata = JsonSerializer.Serialize(new[] {
            new { tag_name = "v0.2.0-preview.1", draft = false, prerelease = true, body = "Current preview notes", assets = Array.Empty<object>(), html_url = "https://github.com/yowynn/FloatCanvas/releases/tag/v0.2.0-preview.1" },
            new { tag_name = "v0.4.0-preview.1", draft = false, prerelease = true, body = "Next preview notes", assets = new object[] { new { name = "FloatCanvas-0.4.0-preview.1-win-x64.zip", state = "uploaded", size = 1024, digest = "sha256:" + new string('a', 64), browser_download_url = "https://github.com/yowynn/FloatCanvas/releases/download/v0.4.0-preview.1/FloatCanvas-0.4.0-preview.1-win-x64.zip" } }, html_url = "https://github.com/yowynn/FloatCanvas/releases/tag/v0.4.0-preview.1" }
        });
        int requests = 0;
        using var http = new HttpClient(new FakeHandler((_, _) => { requests++; return Task.FromResult(JsonResponse(metadata)); }));
        using var client = new GitHubUpdateClient(http);
        var persisted = new List<bool>();
        var window = new AppUpdateWindow("0.2.0-preview.1", client, _ => Task.CompletedTask, true, output, autoCheck: false, savePreviewPreference: persisted.Add);
        try
        {
            Await(window.CheckAsync());
            check("update window defaults to stable only while showing current preview notes", window.PreviewUpdates.IsChecked == false && !window.InstallButton.IsEnabled && window.NotesText.Text.Contains("Current preview notes") && window.StatusText.Text == L.Get("Updates.UpToDate"));
            RenderCurrentNotes(window, Path.Combine(output, "updates-current-notes.png"));
            window.PreviewUpdates.IsChecked = true; PumpUntil(() => !window.IsBusy);
            check("enabling previews saves the choice and refreshes the update offer", persisted.SequenceEqual(new[] { true }) && window.InstallButton.IsEnabled && window.LatestVersionText.Text == "v0.4.0-preview.1" && window.NotesText.Text.Contains("Next preview notes"));
            window.PreviewUpdates.IsChecked = false; PumpUntil(() => !window.IsBusy);
            check("disabling previews removes the preview offer and restores current notes", persisted.SequenceEqual(new[] { true, false }) && !window.InstallButton.IsEnabled && window.NotesText.Text.Contains("Current preview notes"));
        }
        finally { window.Close(); }
        var failing = new AppUpdateWindow("0.2.0-preview.1", client, _ => Task.CompletedTask, true, output, autoCheck: false, savePreviewPreference: _ => throw new IOException("fixture save failed"));
        try
        {
            int before = requests;
            failing.PreviewUpdates.IsChecked = true;
            failing.PrepareForShow();
            check("failed preview preference save restores the switch without starting a check", failing.PreviewUpdates.IsChecked == false && !failing.IsBusy && requests == before && failing.StatusText.Text == L.Get("Updates.PreferenceFailed"));
        }
        finally { failing.Close(); }
        var retained = new AppUpdateWindow("0.2.0-preview.1", client, _ => Task.CompletedTask, true, output, autoCheck: false, includePrereleases: true);
        try
        {
            Await(retained.CheckAsync());
            check("reopening with saved preview preference offers previews immediately", retained.PreviewUpdates.IsChecked == true && retained.InstallButton.IsEnabled && retained.LatestVersionText.Text == "v0.4.0-preview.1");
        }
        finally { retained.Close(); }
    }

    private static void RenderCurrentNotes(AppUpdateWindow window, string path)
    {
        var body = (FrameworkElement)window.Content; window.Content = null;
        var surface = new Border { Width = 498, Height = 450, Child = body };
        surface.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        surface.Measure(new Size(498, 450)); surface.Arrange(new Rect(0, 0, 498, 450)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(498, 450, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(path)) encoder.Save(file);
        surface.Child = null; window.Content = body; Console.WriteLine("UPDATE_RENDER " + path);
    }

    private static void NoUpdateAndError(string output, Action<string, bool> check)
    {
        using var noReleaseHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse("[]"))));
        using var noReleaseClient = new GitHubUpdateClient(noReleaseHttp);
        var none = new AppUpdateWindow("0.2.0-preview.1", noReleaseClient, _ => Task.CompletedTask, true, output, autoCheck: false);
        try
        {
            Await(none.CheckAsync());
            check("no newer release leaves update disabled and check available", !none.InstallButton.IsEnabled && none.CheckButton.IsEnabled && none.StatusText.Text == L.Get("Updates.UpToDate"));
            check("full changelog stays available without a newer release", none.ChangelogButton.IsEnabled && none.ChangelogButton.Visibility == Visibility.Visible);
            check("repository stays available when no newer release exists", none.RepositoryLink.Parent is not null);
        }
        finally { none.Close(); }
        using var failedHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var failedClient = new GitHubUpdateClient(failedHttp);
        var failure = new AppUpdateWindow("0.2.0-preview.1", failedClient, _ => Task.CompletedTask, true, output, autoCheck: false);
        try
        {
            Await(failure.CheckAsync());
            check("HTTP error is shown without escaping the update task", failure.StatusText.Text == L.Get("Updates.CheckFailed") && failure.DetailText.Text.Contains("503") && !failure.InstallButton.IsEnabled && failure.CheckButton.IsEnabled);
            check("full changelog stays available when checking fails", failure.ChangelogButton.IsEnabled && failure.ChangelogButton.Visibility == Visibility.Visible);
            check("repository stays available when checking fails", failure.RepositoryLink.Parent is not null);
        }
        finally { failure.Close(); }
        using var devHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(ReleaseMetadata))));
        using var devClient = new GitHubUpdateClient(devHttp);
        var development = new AppUpdateWindow("0.2.0-preview.1", devClient, _ => throw new InvalidOperationException("Should not replace a development build"), false, output, autoCheck: false);
        try
        {
            Await(development.CheckAsync()); Await(development.DownloadUpdateAsync());
            check("development runs still check but cannot install over the running build", development.StatusText.Text == L.Get("Updates.Available") && !development.InstallButton.IsEnabled);
        }
        finally { development.Close(); }
    }

    private static void SingleCheckAndClose(string output, Action<string, bool> check)
    {
        int requests = 0;
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new FakeHandler((_, token) => { requests++; return response.Task.WaitAsync(token); }));
        using var client = new GitHubUpdateClient(http);
        var window = new AppUpdateWindow("0.2.0-preview.1", client, _ => Task.CompletedTask, true, output, autoCheck: false);
        try
        {
            Task first = window.CheckAsync(); Await(window.CheckAsync());
            check("only one update check runs at a time", requests == 1 && window.IsBusy && !window.CheckButton.IsEnabled && !window.InstallButton.IsEnabled && !window.PreviewUpdates.IsEnabled);
            response.SetResult(JsonResponse(ReleaseMetadata)); Await(first);
            check("successful check restores buttons and hides cancellation", window.CheckButton.IsEnabled && window.InstallButton.IsEnabled && window.PreviewUpdates.IsEnabled && window.CancelButton.Visibility == Visibility.Collapsed && !window.IsBusy);
        }
        finally { window.Close(); }
        bool cancelled = false;
        using var closeHttp = new HttpClient(new FakeHandler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return JsonResponse("[]");
        }));
        using var closeClient = new GitHubUpdateClient(closeHttp);
        var closing = new AppUpdateWindow("0.2.0-preview.1", closeClient, _ => Task.CompletedTask, true, output);
        _ = closing.CheckAsync(); closing.PrepareForShow();
        var status = closing.StatusText.Text;
        closing.Close(); PumpUntil(() => !closing.IsBusy);
        check("closing a checking panel cancels HTTP and suppresses later UI changes", cancelled && closing.StatusText.Text == status && !closing.IsBusy);
    }

    private static void DownloadCancellation(string output, Action<string, bool> check)
    {
        int downloads = 0; bool cancelled = false, applied = false;
        using var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return JsonResponse(ReleaseMetadata);
            downloads++;
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var client = new GitHubUpdateClient(http);
        var window = new AppUpdateWindow("0.2.0-preview.1", client, _ => { applied = true; return Task.CompletedTask; }, true, Path.Combine(output, "cancel-cache"), autoCheck: false);
        try
        {
            Await(window.CheckAsync());
            Task task = window.DownloadUpdateAsync();
            window.PrepareForShow();
            check("download takes over the panel actions with visible progress", downloads == 1 && window.IsBusy && !window.InstallButton.IsEnabled && !window.CheckButton.IsEnabled && window.CancelButton.Visibility == Visibility.Visible && window.DownloadProgress.Visibility == Visibility.Visible);
            Await(window.DownloadUpdateAsync());
            check("repeated update actions do not start a second download", downloads == 1);
            window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Await(task);
            check("cancel download preserves application and allows a retry", cancelled && !applied && !window.IsBusy && window.InstallButton.IsEnabled && window.StatusText.Text == L.Get("Updates.Cancelled"));
            check("cancel leaves the project link visible and removes progress UI", window.RepositoryLink.Parent is not null && window.DownloadProgress.Visibility == Visibility.Collapsed);
        }
        finally { window.Close(); }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static void PreparationAndCommit(string output, Action<string, bool> check)
    {
        // Drive the real metadata/download validation with an in-memory release;
        // preparation itself is gated so the test observes the exact cancellation boundary.
        byte[] package;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                foreach (string name in new[] { "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "PACKAGE-INFO.json" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry("FloatCanvas-0.3.0-win-x64/" + name).Open());
                    writer.Write(name == "PACKAGE-INFO.json" ? JsonSerializer.Serialize(new { Product = "浮绘 FloatCanvas", Version = "0.3.0", RuntimeIdentifier = "win-x64", Configuration = "Release", SelfContained = false, DistributionTrust = "Unsigned" }) : "fixture");
                }
            }
            package = stream.ToArray();
        }
        string metadata = ReleaseMetadata.Replace("\"size\":1024", "\"size\":" + package.Length).Replace(new string('a', 64), Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());
        using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? JsonResponse(metadata) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) })));
        using var client = new GitHubUpdateClient(http);
        int oldApplyCalls = 0, commits = 0, exits = 0;
        foreach (bool cancelBeforeCommit in new[] { true, false })
        {
            var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var committedWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken observed = default; bool entered = false;
            var window = new AppUpdateWindow("0.2.0-preview.1", client, _ => { oldApplyCalls++; return Task.CompletedTask; }, true,
                Path.Combine(output, cancelBeforeCommit ? "prepare-cancel" : "prepare-commit"), autoCheck: false,
                prepareAndApplyUpdate: async (_, token, beginCommit) =>
                {
                    observed = token; entered = true;
                    await preparation.Task.WaitAsync(token);
                    beginCommit(); commits++;
                    await committedWork.Task.WaitAsync(token);
                    exits++;
                });
            try
            {
                Await(window.CheckAsync()); Task update = window.DownloadUpdateAsync(); PumpUntil(() => entered || update.IsCompleted);
                check("update preparation begins after a validated download", entered && !update.IsCompleted && window.IsBusy);
                bool responded = false; _ = window.Dispatcher.BeginInvoke(() => responded = true, DispatcherPriority.Input); PumpUntil(() => responded);
                check("slow update preparation keeps Dispatcher and cancellation responsive", responded && window.CancelButton.IsEnabled && window.CancelButton.Visibility == Visibility.Visible);
                if (cancelBeforeCommit)
                {
                    window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Await(update);
                    check("cancelling preparation cancels its token without commit or legacy apply", observed.IsCancellationRequested && commits == 0 && oldApplyCalls == 0 && exits == 0 && window.StatusText.Text == L.Get("Updates.Cancelled"));
                }
                else
                {
                    preparation.SetResult(); PumpUntil(() => commits == 1);
                    check("commit disables cancellation and presents restart state", !window.CancelButton.IsEnabled && window.StatusText.Text == L.Get("Updates.Restarting"));
                    window.Close();
                    check("closing a committed update does not cancel its shutdown work", !observed.IsCancellationRequested);
                    committedWork.SetResult(); Await(update);
                    check("committed application exit work completes after the update panel closes", exits == 1 && commits == 1 && oldApplyCalls == 0);
                }
            }
            finally { preparation.TrySetResult(); committedWork.TrySetResult(); window.Close(); }
        }
    }
    private const string Notes = ReleaseNotesTests.Notes;
    private static readonly string ReleaseMetadata = JsonSerializer.Serialize(new[] { new
    {
        tag_name = "v0.3.0", draft = false, prerelease = false, body = Notes,
        html_url = "https://github.com/yowynn/FloatCanvas/releases/tag/v0.3.0",
        assets = new[] { new { name = "FloatCanvas-0.3.0-win-x64.zip", state = "uploaded", size = 1024, digest = "sha256:" + new string('a', 64), browser_download_url = "https://github.com/yowynn/FloatCanvas/releases/download/v0.3.0/FloatCanvas-0.3.0-win-x64.zip" } }
    } });

    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> completed)
    {
        var frame = new DispatcherFrame(); var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (completed() || watch.Elapsed > TimeSpan.FromSeconds(12)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!completed()) throw new TimeoutException("The detached update UI fixture did not complete.");
        // Display assertions explicitly synchronize detached views; runtime callbacks
        // keep hidden update windows unchanged until their next display boundary.
        foreach (var window in Application.Current.Windows.OfType<AppUpdateWindow>().ToArray()) window.PrepareForShow();
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
