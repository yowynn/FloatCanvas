using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>The single startup introduction. Detached content is also used for layout verification.</summary>
internal sealed class WelcomeView : StackPanel
{
    internal const double PreferredWidth = 480;
    internal TextBlock ToggleShortcut { get; } = ShortcutText();
    internal TextBlock HideShortcut { get; } = ShortcutText();
    internal Button StartButton { get; }
    internal Button VersionButton { get; }
    internal Button ProjectButton { get; }
    private (HotkeyBinding Toggle, HotkeyBinding Hide)? pendingShortcuts;

    internal WelcomeView(HotkeyBinding toggle, HotkeyBinding hide, Action start, Action? checkUpdates = null, Action? openProject = null, Action? restoreHistory = null, WorkspaceRecoveryHistory? history = null)
    {
        Margin = WindowLayout.ContentMargin(12, 20);
        SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "CanvasText");
        Children.Add(new TextBlock { Text = L.Get("App.Name"), FontSize = 26, FontWeight = FontWeights.SemiBold });
        var tagline = new TextBlock { Text = L.Get("Welcome.Tagline"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 19) };
        tagline.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); Children.Add(tagline);
        AddShortcut("Acceptance2.Welcome.ToggleMode", "Acceptance2.Welcome.ToggleModeDescription", ToggleShortcut);
        AddShortcut("Acceptance2.Welcome.ToggleVisibility", "Acceptance2.Welcome.ToggleVisibilityDescription", HideShortcut);
        StartButton = new Button { MinHeight = 38, Padding = new Thickness(12, 7, 12, 7), IsDefault = true };
        StartButton.SetResourceReference(FrameworkElement.StyleProperty, "CanvasPrimaryButton");
        StartButton.Click += (_, _) => start(); Children.Add(StartButton);
        if (restoreHistory is not null && history is not null)
        {
            var recoveryHint = new TextBlock { Text = L.Get("Audit.WelcomeRestoreHint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };
            recoveryHint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); Children.Add(recoveryHint);
            var resume = new Button { Visibility = Visibility.Collapsed, Margin = new(0, 8, 0, 0) };
            resume.Click += (_, _) => restoreHistory(); Children.Add(resume);
            CancellationTokenSource? recoveryRequest = null;
            async void LoadRecovery()
            {
                if (!IsVisible || recoveryRequest is not null) return;
                var request = recoveryRequest = new CancellationTokenSource();
                try
                {
                    var entries = await history.GetEntriesAsync(request.Token);
                    if (request.IsCancellationRequested || !IsVisible || !ReferenceEquals(recoveryRequest, request)) return;
                    if (entries.FirstOrDefault() is { } latest)
                    { resume.Content = L.Format("Audit.RecentHistory", latest.SavedAtUtc.ToLocalTime().ToString("g")); resume.Visibility = Visibility.Visible; }
                    else { resume.Visibility = Visibility.Collapsed; recoveryHint.Text = L.Get("Audit.WelcomeRestoreEmpty"); }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { if (!request.IsCancellationRequested && IsVisible) { resume.Content = L.Get("Recovery.HistoryTitle"); resume.Visibility = Visibility.Visible; } }
            }
            Loaded += (_, _) => LoadRecovery();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible && IsLoaded) LoadRecovery();
                else if (!IsVisible) { recoveryRequest?.Cancel(); recoveryRequest?.Dispose(); recoveryRequest = null; }
            };
            Unloaded += (_, _) => { recoveryRequest?.Cancel(); recoveryRequest?.Dispose(); recoveryRequest = null; };
        }
        Refresh(toggle, hide);
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        var version = typeof(DesktopSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        var versionText = new TextBlock { Text = "v" + version, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left };
        versionText.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        VersionButton = new Button { Content = versionText, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Height = 18, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = L.Get("Updates.Title") };
        VersionButton.SetResourceReference(FrameworkElement.StyleProperty, "CanvasCaptionButton");
        System.Windows.Automation.AutomationProperties.SetName(VersionButton, L.Get("Updates.Title") + " v" + version);
        VersionButton.Click += (_, _) => checkUpdates?.Invoke(); footer.Children.Add(VersionButton);
        var credits = new TextBlock { Text = "By 𝗛𝗘𝗣𝗧𝗔𝗬𝗢 with ", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        var heart = new Image { Source = EmojiArtwork.Image("❤️"), Width = 12, Height = 12, Stretch = Stretch.Uniform };
        System.Windows.Automation.AutomationProperties.SetName(heart, "❤️");
        credits.Inlines.Add(new System.Windows.Documents.InlineUIContainer(heart) { BaselineAlignment = BaselineAlignment.Center });
        credits.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        ProjectButton = new Button {Content=credits,MinWidth=0,MinHeight=0,Height=18,Padding=new Thickness(0),
            HorizontalAlignment=HorizontalAlignment.Right,Background=Brushes.Transparent,BorderThickness=new Thickness(0),
            Cursor=System.Windows.Input.Cursors.Hand,ToolTip=L.Get("Welcome.ProjectHome")};
        ProjectButton.SetResourceReference(FrameworkElement.StyleProperty,"CanvasCaptionButton");
        System.Windows.Automation.AutomationProperties.SetName(ProjectButton,L.Get("Welcome.ProjectHome"));
        ProjectButton.Click+=(_,_)=>openProject?.Invoke();
        footer.Children.Add(ProjectButton); Children.Add(footer);
        IsVisibleChanged += (_, _) => { if (IsVisible) PrepareForShow(); };
    }

    internal void Refresh(HotkeyBinding toggle, HotkeyBinding hide)
    {
        pendingShortcuts = (toggle, hide);
        if (IsVisible) PrepareForShow();
    }

    internal void PrepareForShow()
    {
        if (pendingShortcuts is not { } shortcuts) return;
        pendingShortcuts = null;
        var (toggle, hide) = shortcuts;
        ToggleShortcut.Text = HotkeyDisplay.Format(toggle); HideShortcut.Text = HotkeyDisplay.Format(hide);
        StartButton.Content = L.Format("Acceptance2.Welcome.Start", ToggleShortcut.Text);
    }

    private static TextBlock ShortcutText()
    {
        return CodeAppearance.Text();
    }

    private void AddShortcut(string titleKey, string descriptionKey, TextBlock shortcut)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 5) };
        var code = CodeAppearance.Frame(shortcut); code.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(code, Dock.Right); row.Children.Add(code);
        row.Children.Add(new TextBlock { Text = L.Get(titleKey), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(row);
        var explanation = new TextBlock { Text = L.Get(descriptionKey), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 15) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); Children.Add(explanation);
    }
}
