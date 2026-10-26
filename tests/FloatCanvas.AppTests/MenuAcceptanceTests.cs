using System.Reflection;
using System.Runtime.CompilerServices;
using FloatCanvas.App;
using FloatCanvas.Core;
using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

internal static class MenuAcceptanceTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        L.Initialize("zh-CN"); AppTheme.Apply("light");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        void Set(string name, object value) => typeof(DesktopSession).GetField(name, flags)!.SetValue(session, value);
        Set("_settings", new UserSettings());
        Set("_editor", new EditorController(new EditorSession()));
        Set("_cropMenuItems", new List<MenuItem>());
        var menu = (ContextMenu)typeof(DesktopSession).GetMethod("CreateLauncherMenu", flags)!.Invoke(session, null)!;
        check("main menu places pass-through immediately after toolbar expansion", menu.Items[1] is MenuItem item && Equals(item.Header, L.Get("ToolbarV2.PassThrough")));
        var help = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, L.Get("Audit.Help")));
        check("welcome is the first Help item", help.Items[0] is MenuItem welcome && Equals(welcome.Header, L.Get("Welcome.Open")));
        check("diagnostic menu is integrated into report issue", !menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, L.Get("Menu.SystemDiagnostics"))));
        var launcher = new ToolbarLauncher(); int menuRequests = 0;
        using var launcherSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("FloatCanvas.MenuAcceptanceLauncher")
        { Width = 94, Height = 94, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000) });
        launcherSource.RootVisual = launcher;
        launcher.MenuRequested += () => menuRequests++;
        var right = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
        typeof(ToolbarLauncher).GetMethod("OnPreviewMouseRightButtonDown", flags)!.Invoke(launcher, [right]);
        check("avatar right press opens the menu immediately", menuRequests == 1 && right.Handled);
        right.RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent;
        typeof(ToolbarLauncher).GetMethod("OnPreviewMouseRightButtonUp", flags)!.Invoke(launcher, [right]);
        check("avatar right release cannot queue a delayed second open", menuRequests == 1);
        bool closedMenu = false; launcher.CloseOpenMenu = () => closedMenu = true;
        var left = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(ToolbarLauncher).GetMethod("OnPreviewMouseLeftButtonDown", flags)!.Invoke(launcher, [left]);
        check("avatar left press closes an open menu without a pending delayed click", closedMenu && menuRequests == 1 && !((LauncherGestureState)typeof(ToolbarLauncher).GetField("gestures", flags)!.GetValue(launcher)!).HasPendingClick);
        check("main menu has the requested command groups and separators", menu.Items.Cast<object>().Select(item => item is MenuItem command ? command.Header : "---").SequenceEqual(new object[] {
            L.Get("Toolbar.Collapse"), L.Get("ToolbarV2.PassThrough"), "---", L.Get("ToolbarV2.File"), L.Get("ToolbarV2.Edit"), L.Get("ToolbarV2.DrawingTools"), L.Get("ToolbarV2.ImageTools"), L.Get("ToolbarV2.Canvas"), L.Get("ToolbarV2.Windows"), "---", L.Get("Menu.HideFloatingLogo"), L.Get("Menu.Settings"), L.Get("Audit.Help"), L.Get("Updates.Title"), "---", L.Get("Desktop.Exit") }));
        var markdown = new MarkdownView { Text = "# Heading\n**Bold** and `code`\n| Shortcut | Action |\n| --- | --- |\n| Ctrl+S | Save |\n```\n<untrusted/>\n```" };
        check("Markdown renders headings, emphasis, a real shortcut table and literal code", markdown.Document.Blocks.OfType<System.Windows.Documents.Table>().Single().RowGroups[0].Rows.Count == 2 &&
            markdown.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().Any(p => p.Inlines.OfType<System.Windows.Documents.Bold>().Any()) &&
            markdown.Document.Blocks.OfType<System.Windows.Documents.BlockUIContainer>().Single().Child is Border { Child: TextBox { Text: "<untrusted/>" } });
        var sharedCode = CodeAppearance.Selectable("Ctrl+`");
        check("Markdown keycaps reuse the welcome-page code font, colors, padding and corners", ((TextBlock)sharedCode.Child).FontFamily.Equals(CodeAppearance.Text().FontFamily) && sharedCode.CornerRadius == new CornerRadius(4) && sharedCode.Padding == new Thickness(6, 2, 6, 2));
        sharedCode.Measure(new Size(160, 80)); sharedCode.Arrange(new Rect(sharedCode.DesiredSize)); sharedCode.UpdateLayout();
        var keyText = (TextBlock)sharedCode.Child;
        check("keycap text has equal top and bottom padding", Math.Abs(keyText.TranslatePoint(new Point(), sharedCode).Y -
            (sharedCode.ActualHeight - keyText.TranslatePoint(new Point(0, keyText.ActualHeight), sharedCode).Y)) < .01);
        var copied = new List<string>(); bool openedAfterCopy = false;
        var feedback = new ReportIssueWindow(false, () => openedAfterCopy = copied.Count == 2, copied.Add);
        feedback.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        feedback.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("feedback copies environment information before opening the issue page", openedAfterCopy && copied.All(value => value == feedback.Summary && value.Contains("Runtime") && value.Contains("Displays")));
        check("feedback groups guidance, system and diagnostics into three collapsible sections", SettingsWindowFixture.Descendants(feedback).OfType<Expander>().Count() == 3);
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_MENU_PREVIEW") is { Length: > 0 } preview)
        {
            System.IO.Directory.CreateDirectory(preview);
            OverlayWindowScope.UseRegularWindow(feedback); AppWindowChrome.ApplyDialog(feedback);
            feedback.ShowActivated = false; feedback.Show(); feedback.UpdateLayout();
            Preview4SettingsRuntimeFixture.Render(feedback, System.IO.Path.Combine(preview, "feedback.png"));
            var guide = AppHelp.CreateTextWindow(L.Get("Settings.Shortcuts"), AppHelp.ReadGuide(true));
            OverlayWindowScope.UseRegularWindow(guide); AppWindowChrome.ApplyDialog(guide);
            guide.ShowActivated = false; guide.Show(); guide.UpdateLayout();
            Preview4SettingsRuntimeFixture.Render(guide, System.IO.Path.Combine(preview, "shortcuts.png")); guide.Close();
        }
        feedback.Close();
        Labels(96, "Width", "Opacity"); Labels(132, "Edit / pass-through", "Hide / show");

        void Labels(double maximum, string first, string second)
        {
            var panel = new StackPanel(); PropertyPanelLayout.Scope(panel, maximum);
            var rows = new[] { PropertyPanelLayout.Row(new PropertyLabel(first), new TextBox()), PropertyPanelLayout.Row(new PropertyLabel(second), new TextBox()) };
            foreach (var row in rows) panel.Children.Add(row);
            panel.Measure(new Size(440, 200)); panel.Arrange(new Rect(0, 0, 440, 200)); panel.UpdateLayout();
            double width = rows[0].ColumnDefinitions[0].ActualWidth;
            check($"panel cap {maximum} aligns labels using their content width", Math.Abs(width - rows[1].ColumnDefinitions[0].ActualWidth) < 1 && width <= maximum + PropertyPanelLayout.LabelGap && width < maximum + PropertyPanelLayout.LabelGap - 1);
        }
    }
}
