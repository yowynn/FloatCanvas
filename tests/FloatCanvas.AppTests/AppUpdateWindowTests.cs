using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

/// <summary>Detached update UI and fake HTTP only; no visible windows, live GitHub or installed files.</summary>
internal static class AppUpdateWindowTests
{
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
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(ReleaseMetadata))));
                using var client = new GitHubUpdateClient(http);
                var window = new AppUpdateWindow("0.2.0-preview.1", client, _ => Task.CompletedTask, true, Path.Combine(output, "cache"), autoCheck: false);
                try
                {
                    Await(window.CheckAsync());
                    string context = language + " " + theme;
                    check(context + " update check enables installing and shows latest version", window.InstallButton.IsEnabled && window.LatestVersionText.Text == "v0.3.0" && window.StatusText.Text == L.Get("Updates.Available"));
                    check(context + " release notes remain literal read-only text", window.NotesText.IsReadOnly && window.NotesText.Text == Notes && !window.NotesText.IsReadOnlyCaretVisible);
                    check(context + " repository link is always available and points to this project", window.RepositoryLink.NavigateUri?.AbsoluteUri == "https://github.com/yowynn/FloatCanvas");
                    check(context + " update panel uses shared floating chrome without native min/max controls", window.Template is not null && window.WindowStyle == WindowStyle.None && window.ResizeMode == ResizeMode.CanResize);
                    var body = (FrameworkElement)window.Content;
                    window.Content = null;
                    var host = new Border { Width = 498, Height = 450, Child = body };
                    host.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
                    host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
                    host.Measure(new(498, 450)); host.Arrange(new(0, 0, 498, 450)); host.UpdateLayout();
                    check(context + " controls fit the compact update surface", window.NotesText.ActualHeight >= 90 && window.InstallButton.ActualWidth > 70 && window.CheckButton.ActualWidth > 70 && body.ActualWidth < 498);
                    var pixels = new RenderTargetBitmap(498, 450, 96, 96, PixelFormats.Pbgra32); pixels.Render(host);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
                    using (var file = File.Create(Path.Combine(output, "updates-" + language + "-" + theme + ".png"))) encoder.Save(file);
                    host.Child = null; window.Content = body;
                }
                finally { window.Close(); }
            }
            L.Initialize("zh-CN"); AppTheme.Apply("light");
            NoUpdateAndError(output, check);
            SingleCheckAndClose(output, check);
            DownloadCancellation(output, check);
            check("update UI fixtures leave no additional application windows", app.Windows.Cast<Window>().SequenceEqual(initialWindows));
        }
        finally
        {
            foreach (var window in app.Windows.Cast<Window>().Where(w => !initialWindows.Contains(w)).ToArray()) window.Close();
            L.Initialize(priorLanguage); AppTheme.Apply("light"); SynchronizationContext.SetSynchronizationContext(priorContext);
        }
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
            check("only one update check runs at a time", requests == 1 && window.IsBusy && !window.CheckButton.IsEnabled && !window.InstallButton.IsEnabled);
            response.SetResult(JsonResponse(ReleaseMetadata)); Await(first);
            check("successful check restores buttons and hides cancellation", window.CheckButton.IsEnabled && window.InstallButton.IsEnabled && window.CancelButton.Visibility == Visibility.Collapsed && !window.IsBusy);
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
        closing.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
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
    private const string Notes = "## Clearer palettes\n<script>never execute this</script>\n[Project](https://github.com/yowynn/FloatCanvas)";
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
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
