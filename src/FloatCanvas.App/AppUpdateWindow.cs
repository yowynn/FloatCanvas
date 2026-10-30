using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Checks and downloads releases; the session owns installing and disposing the client.</summary>
internal sealed class AppUpdateWindow : Window
{
    private readonly string currentVersion, cacheDirectory;
    private readonly GitHubUpdateClient client;
    private readonly Func<PreparedUpdate, Task> applyUpdate;
    private readonly Func<PreparedUpdate, CancellationToken, Action, Task>? prepareAndApplyUpdate;
    private readonly Func<PreparedInstallerUpdate, CancellationToken, Action, Task>? applyInstallerUpdate;
    private readonly bool canInstall;
    private readonly Action<bool>? savePreviewPreference;
    private readonly Action<Uri> openLink;
    private bool restoringPreviewPreference;
    private CancellationTokenSource? operation;
    private AvailableUpdate? available;
    private bool closed, applying;
    private readonly Dictionary<string, Action> pendingDisplay = [];
    private string cancelCaption = "";
    private bool autoCheckPending;
    private volatile bool displayVisible;
    private sealed record PendingProgress(CancellationTokenSource Source, UpdateProgress Progress);
    private PendingProgress? latestProgress;
    private int progressQueued;
    internal TextBlock StatusText { get; } = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    internal TextBlock DetailText { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 5, 0, 0) };
    internal TextBlock LatestVersionText { get; } = new() { Text = "—", HorizontalAlignment = HorizontalAlignment.Right };
    internal MarkdownView NotesText { get; } = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MinHeight = 90, Margin = new(0, 6, 0, 0)
    };
    internal Button CheckButton { get; } = new() { MinHeight = 34, Padding = new(12, 6, 12, 6) };
    internal Button ChangelogButton { get; } = new() { MinHeight = 28, Padding = new(8, 3, 8, 3), FontSize = 12 };
    internal Button InstallButton { get; } = new() { MinHeight = 34, Padding = new(16, 6, 16, 6), IsEnabled = false };
    internal Button CancelButton { get; } = new() { MinHeight = 34, Padding = new(12, 6, 12, 6), Visibility = Visibility.Collapsed };
    internal ProgressBar DownloadProgress { get; } = new() { Height = 5, Minimum = 0, Maximum = 100, Margin = new(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    internal Hyperlink RepositoryLink { get; } = new() { NavigateUri = GitHubUpdateClient.ProjectUrl };
    internal CheckBox PreviewUpdates { get; } = new() { Margin = new(0, 12, 0, 0) };
    internal bool IsBusy => operation is not null;

    internal AppUpdateWindow(string currentVersion, GitHubUpdateClient client,
        Func<PreparedUpdate, Task> applyUpdate, bool canInstall, string? cacheDirectory = null, bool autoCheck = true,
        bool includePrereleases = false, Action<bool>? savePreviewPreference = null,
        Func<PreparedUpdate, CancellationToken, Action, Task>? prepareAndApplyUpdate = null, Action<Uri>? openLink = null,
        Func<PreparedInstallerUpdate, CancellationToken, Action, Task>? applyInstallerUpdate = null)
    {
        this.currentVersion = currentVersion; this.client = client; this.applyUpdate = applyUpdate; this.canInstall = canInstall;
        this.prepareAndApplyUpdate = prepareAndApplyUpdate;
        this.applyInstallerUpdate = applyInstallerUpdate;
        this.savePreviewPreference = savePreviewPreference;
        this.openLink = openLink ?? (uri => { _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); });
        PreviewUpdates.IsChecked = includePrereleases;
        PreviewUpdates.Content = L.Get("Updates.IncludePreviews"); PreviewUpdates.ToolTip = L.Get("Updates.PreviewHint");
        AutomationProperties.SetName(PreviewUpdates, L.Get("Updates.IncludePreviews"));
        PreviewUpdates.Checked += OnPreviewPreferenceChanged; PreviewUpdates.Unchecked += OnPreviewPreferenceChanged;
        this.cacheDirectory = cacheDirectory ?? Path.Combine(ApplicationPaths.ProfileDirectory, "updates", "downloads");
        Title = L.Get("Updates.Title"); Width = 500; Height = 510; MinWidth = 410; MinHeight = 390; MaxHeight = 650;
        ShowInTaskbar = true; Icon = AppIcon.WindowImage; ResizeMode = ResizeMode.CanResize;
        OverlayWindowScope.UseRegularWindow(this);
        CheckButton.Content = L.Get("Updates.CheckAgain"); InstallButton.Content = L.Get("Updates.Install");
        ChangelogButton.Content = L.Get("Updates.MoreLogs");
        InstallButton.SetResourceReference(StyleProperty, "CanvasPrimaryButton");
        DetailText.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        NotesText.Text = L.Get("Updates.NoNotes");
        StatusText.Text = L.Get("Updates.Checking");
        AutomationProperties.SetName(NotesText, L.Get("Updates.ReleaseNotes"));
        AutomationProperties.SetName(DownloadProgress, L.Get("Updates.Downloading"));
        Content = CreateContent(); AppWindowChrome.Apply(this);
        CheckButton.Click += (_, _) => _ = CheckAsync();
        InstallButton.Click += (_, _) => _ = DownloadUpdateAsync();
        CancelButton.Click += (_, _) => operation?.Cancel();
        RepositoryLink.RequestNavigate += (_, e) => { OpenProjectLink(GitHubUpdateClient.ProjectUrl); e.Handled = true; };
        ChangelogButton.Click += (_, _) => OpenProjectLink(GitHubUpdateClient.ChangelogUrl);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (IsBusy && !applying) operation?.Cancel(); else if (!applying) Close();
            e.Handled = true;
        };
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; pendingDisplay.Clear(); if (!applying) operation?.Cancel(); };
        autoCheckPending = autoCheck;
        if (autoCheck) Loaded += OnFirstLoaded;
        IsVisibleChanged += (_, _) =>
        {
            displayVisible = IsVisible;
            if (!IsVisible) return;
            PrepareForShow();
            ApplyLatestProgress();
            if (IsLoaded && autoCheckPending) OnFirstLoaded(this, new RoutedEventArgs());
        };
    }

    private FrameworkElement CreateContent()
    {
        var body = new Grid { Margin = WindowLayout.ContentMargin(6, 18) };
        for (int i = 0; i < 10; i++) body.RowDefinitions.Add(new() { Height = i == 5 ? new(1, GridUnitType.Star) : GridLength.Auto });
        var versions = new Grid { Margin = new(0, 0, 0, 17) };
        versions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        versions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var current = new StackPanel();
        var latest = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        current.Children.Add(Muted(L.Get("Updates.CurrentVersion")));
        current.Children.Add(new TextBlock { Text = "v" + currentVersion, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new(0, 3, 0, 0) });
        latest.Children.Add(Muted(L.Get("Updates.LatestVersion")));
        LatestVersionText.FontSize = 19; LatestVersionText.FontWeight = FontWeights.SemiBold; LatestVersionText.Margin = new(0, 3, 0, 0);
        latest.Children.Add(LatestVersionText); Grid.SetColumn(latest, 1); versions.Children.Add(current); versions.Children.Add(latest);
        Add(versions, 0); Add(StatusText, 1); Add(DetailText, 2);
        Add(PropertyPanelLayout.BooleanRow(L.Get("Updates.IncludePreviews"), PreviewUpdates), 3);
        var notesHeading = new Grid { Margin = new(0, 14, 0, 0) };
        notesHeading.ColumnDefinitions.Add(new()); notesHeading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        notesHeading.Children.Add(new TextBlock { Text = L.Get("Updates.ReleaseNotes"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(ChangelogButton, 1); notesHeading.Children.Add(ChangelogButton); Add(notesHeading, 4);
        Add(NotesText, 5); Add(DownloadProgress, 6);
        if (!canInstall)
        {
            var development = Muted(L.Get("Updates.Development")); development.TextWrapping = TextWrapping.Wrap; development.Margin = new(0, 12, 0, 0);
            Add(development, 7);
        }
        var actions = new Grid { Margin = new(0, 16, 0, 0) };
        actions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        CheckButton.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(CheckButton);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        CancelButton.Margin = new(0, 0, 8, 0); right.Children.Add(CancelButton); right.Children.Add(InstallButton);
        Grid.SetColumn(right, 1); actions.Children.Add(right); Add(actions, 8);
        RepositoryLink.Inlines.Add(L.Get("Updates.Repository")); RepositoryLink.SetResourceReference(Hyperlink.ForegroundProperty, "CanvasAccent");
        var repository = new TextBlock { FontSize = 12, Margin = new(0, 16, 0, 0) }; repository.Inlines.Add(RepositoryLink); Add(repository, 9);
        return body;

        void Add(UIElement element, int row) { Grid.SetRow(element, row); body.Children.Add(element); }
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); return block;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || !autoCheckPending) return;
        autoCheckPending = false;
        Loaded -= OnFirstLoaded;
        _ = CheckAsync();
    }

    internal async Task CheckAsync()
    {
        if (closed || IsBusy) return;
        using var cancellation = BeginOperation(false);
        available = null; UpdateDisplay("version", () => LatestVersionText.Text = "—");
        UpdateDisplay("notes", () => NotesText.Text = L.Get("Updates.NoNotes"));
        SetStatus(L.Get("Updates.Checking")); SetDetail("");
        try
        {
            var result = await client.CheckAsync(currentVersion, cancellation.Token, PreviewUpdates.IsChecked == true, preferInstaller: applyInstallerUpdate is not null);
            if (closed) return;
            available = result.Update;
            if (applyInstallerUpdate is not null && available is not null)
                SetDetail(L.Get(available.IsInstaller ? "InstallerUpdates.AllUsers" : "InstallerUpdates.NoSetup"));
            SetStatus(L.Get(available is null ? "Updates.UpToDate" : "Updates.Available"));
            string version = "v" + (available?.Version ?? currentVersion);
            UpdateDisplay("version", () => LatestVersionText.Text = version);
            string? notes = available?.Notes ?? result.CurrentNotes;
            UpdateDisplay("notes", () => NotesText.Text = string.IsNullOrWhiteSpace(notes) ? L.Get("Updates.NoNotes") : notes);
        }
        catch (OperationCanceledException) { if (!closed) SetStatus(L.Get("Updates.Cancelled")); }
        catch (Exception ex) { if (!closed) ShowFailure("Updates.CheckFailed", ex); }
        finally { EndOperation(cancellation); }
    }

    private void OnPreviewPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (closed || restoringPreviewPreference) return;
        bool enabled = PreviewUpdates.IsChecked == true;
        try { savePreviewPreference?.Invoke(enabled); }
        catch (Exception ex)
        {
            restoringPreviewPreference = true;
            try { PreviewUpdates.IsChecked = !enabled; }
            finally { restoringPreviewPreference = false; }
            ShowFailure("Updates.PreferenceFailed", ex); return;
        }
        _ = CheckAsync();
    }

    internal async Task DownloadUpdateAsync()
    {
        if (closed || IsBusy || !canInstall || available is null) return;
        var update = available;
        if (applyInstallerUpdate is not null && !update.IsInstaller) { OpenProjectLink(update.ReleaseUrl); return; }
        using var cancellation = BeginOperation(true);
        SetStatus(L.Get("Updates.Downloading")); SetDetail("");
        try
        {
            var progress = new DownloadProgressReporter(this, cancellation);
            if (applyInstallerUpdate is not null)
            {
                var installer = await client.DownloadInstallerAsync(update, cacheDirectory, progress, cancellation.Token);
                if (closed || cancellation.IsCancellationRequested) return;
                SetStatus(L.Get("InstallerUpdates.Opening"));
                await applyInstallerUpdate(installer, cancellation.Token, () =>
                {
                    cancellation.Token.ThrowIfCancellationRequested(); applying = true;
                    UpdateDisplay("controls", PaintOperationControls); SetStatus(L.Get("InstallerUpdates.Opening"));
                });
                return;
            }
            var prepared = await client.DownloadAsync(update, cacheDirectory, progress, cancellation.Token);
            if (closed || cancellation.IsCancellationRequested) return;
            SetStatus(L.Get("Engineering.UpdatesPreparing"));
            void Commit() { cancellation.Token.ThrowIfCancellationRequested(); applying = true; UpdateDisplay("controls", PaintOperationControls); SetStatus(L.Get("Updates.Restarting")); }
            if (prepareAndApplyUpdate is not null) await prepareAndApplyUpdate(prepared, cancellation.Token, Commit);
            else { Commit(); await applyUpdate(prepared); }
        }
        catch (OperationCanceledException) { if (!closed) SetStatus(L.Get("Updates.Cancelled")); }
        catch (Exception ex) { if (!closed) ShowFailure(applying ? "Updates.InstallFailed" : "Updates.DownloadFailed", ex); }
        finally { applying = false; EndOperation(cancellation); }
    }

    private CancellationTokenSource BeginOperation(bool downloading)
    {
        operation = new CancellationTokenSource();
        cancelCaption = L.Get(downloading ? "Updates.Cancel" : "Updates.CancelCheck");
        UpdateDisplay("controls", PaintOperationControls);
        UpdateDisplay("progress", () => { if (IsBusy) { DownloadProgress.Value = 0; DownloadProgress.IsIndeterminate = true; } });
        return operation;
    }

    private void EndOperation(CancellationTokenSource source)
    {
        if (!ReferenceEquals(operation, source)) return;
        operation = null;
        if (closed) return;
        UpdateDisplay("controls", PaintOperationControls);
    }

    private void PaintOperationControls()
    {
        bool busy = IsBusy;
        InstallButton.Content = L.Get(applyInstallerUpdate is null ? "Updates.Install" : available is { IsInstaller: false } ? "InstallerUpdates.OpenRelease" : "InstallerUpdates.Install");
        CheckButton.IsEnabled = !busy; InstallButton.IsEnabled = !busy && canInstall && available is not null;
        PreviewUpdates.IsEnabled = !busy;
        CancelButton.Visibility = DownloadProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) { CancelButton.Content = cancelCaption; CancelButton.IsEnabled = !applying; }
    }

    private void UpdateDisplay(string key, Action update)
    {
        if (closed) return;
        pendingDisplay[key] = update;
        if (IsVisible) PrepareForShow();
    }

    internal void PrepareForShow()
    {
        if (closed) return;
        var updates = pendingDisplay.Values.ToArray(); pendingDisplay.Clear();
        foreach (var update in updates) update();
    }

    private void SetStatus(string message) => UpdateDisplay("status", () => StatusText.Text = message);
    private void SetDetail(string message) => UpdateDisplay("detail", () => DetailText.Text = message);
    private void ShowFailure(string key, Exception error) { SetStatus(L.Get(key)); SetDetail(error.Message); }

    private void ApplyLatestProgress()
    {
        var pending = Volatile.Read(ref latestProgress);
        if (!IsVisible || closed || pending is null || !ReferenceEquals(operation, pending.Source) || pending.Source.IsCancellationRequested) return;
        var value = pending.Progress;
        UpdateDisplay("progress", () =>
        {
            if (!IsBusy) return;
            DownloadProgress.IsIndeterminate = value.TotalBytes <= 0;
            if (value.TotalBytes > 0) DownloadProgress.Value = Math.Clamp(value.ReceivedBytes * 100d / value.TotalBytes, 0, 100);
        });
        if (value.TotalBytes > 0) SetStatus(L.Format("Updates.DownloadProgress", (int)Math.Clamp(value.ReceivedBytes * 100d / value.TotalBytes, 0, 100)));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Closing during a download cancels it; installation belongs to the application shutdown flow.
        if (!applying) operation?.Cancel();
    }

    private void OpenProjectLink(Uri uri)
    {
        try { openLink(uri); }
        catch (Exception ex) { if (!closed) ShowFailure("Updates.OpenLinkFailed", ex); }
    }

    private sealed class DownloadProgressReporter(AppUpdateWindow owner, CancellationTokenSource source) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value)
        {
            if (owner.Dispatcher.HasShutdownStarted) return;
            Volatile.Write(ref owner.latestProgress, new(source, value));
            if (!owner.displayVisible || Interlocked.Exchange(ref owner.progressQueued, 1) != 0) return;
            _ = owner.Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref owner.progressQueued, 0);
                owner.ApplyLatestProgress();
            });
        }
    }
}
