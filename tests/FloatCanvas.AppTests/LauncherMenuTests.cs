using System.Reflection;
using System.Runtime.CompilerServices;
using FloatCanvas.App;
using FloatCanvas.Core;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

internal static class LauncherMenuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(language); AppTheme.Apply(theme);
            var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
            var settings = new UserSettings();
            Set(session, "_settings", settings);
            Set(session, "_editor", new EditorController(new EditorSession()));
            Set(session, "_cropMenuItems", new List<Controls.MenuItem>());
            var menu = (Controls.ContextMenu)typeof(DesktopSession).GetMethod("CreateLauncherMenu", Private)!.Invoke(session, null)!;
            var entries = Descendants(menu).ToArray();
            var artwork = entries.Select(item => VisualDescendants((Wpf.DependencyObject)item.Icon)
                .OfType<System.Windows.Shapes.Path>().Single().Data.ToString()).ToArray();
            check(language + " every distinct main-menu command and category has its own artwork", artwork.Distinct().Count() == artwork.Length);
            var help=entries.Single(item=>Equals(item.Header,L.Get("Audit.Help")));
            check(language+" "+theme+" Help includes welcome, guides, folders and report issue",help.Items.OfType<Controls.MenuItem>().Select(item=>item.Header).SequenceEqual(new[]{L.Get("Welcome.Open"),L.Get("Audit.UserGuide"),L.Get("Settings.Shortcuts"),L.Get("Menu.OpenInstallDirectory"),L.Get("Menu.OpenUserDataDirectory"),L.Get("Audit.ReportIssue")}));
            string label = language + " " + theme;
            var expansion = (Controls.MenuItem)menu.Items[0];
            check(label + " the first main-menu item offers collapse with a separate Tab hint", Equals(expansion.Header, L.Get("Toolbar.Collapse")) &&
                expansion.InputGestureText == "Tab" && expansion.Items.Count == 0);
            settings.ToolbarExpanded = false;
            typeof(DesktopSession).GetMethod("RefreshToolbarExpansionMenuItem", Private)!.Invoke(session, null);
            check(label + " a collapsed toolbar offers expansion through the same first menu item", ReferenceEquals(menu.Items[0], expansion) &&
                Equals(expansion.Header, L.Get("Toolbar.Expand")) && expansion.InputGestureText == "Tab");
            settings.ToolbarExpanded = true;
            typeof(DesktopSession).GetMethod("RefreshToolbarExpansionMenuItem", Private)!.Invoke(session, null);
            check(label + " menu has one settings entry and no duplicate shortcut visibility toggle",
                entries.Count(item => Equals(item.Header, L.Get("Menu.Settings"))) == 1 &&
                !entries.Any(item => Equals(item.Header, L.Get("Feedback3.Toolbar.ShowShortcutHints"))));
            check(label + " settings entry uses the general settings name", L.Get("Menu.Settings") == (language == "en" ? "Settings" : "设置"));
            var tools = entries.Where(item => item.Tag is EditorTool).Select(item => (EditorTool)item.Tag).ToArray();
            check(label + " tools match the active toolbar families without retired solid cover",
                tools.SequenceEqual(new[] { EditorTool.Select, EditorTool.Eraser, EditorTool.Freehand, EditorTool.Highlighter,
                    EditorTool.Line, EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Text, EditorTool.Number,
                    EditorTool.Symbol, EditorTool.Emoji, EditorTool.Crop, EditorTool.Blur, EditorTool.Pixelate, EditorTool.CaptureRegion, EditorTool.Canvas }));
            var canvasTool = entries.Single(item => Equals(item.Tag, EditorTool.Canvas));
            check(label + " Canvas is one ordinary tool command with its reserved C shortcut", Equals(canvasTool.Header, L.Get("Canvas.Title")) &&
                canvasTool.InputGestureText == "C" && ToolShortcutCatalog.TryGetTool(0x43, out var canvas) && canvas == EditorTool.Canvas);
            check(label + " legacy screenshot draft commands are absent", !entries.Any(item =>
                Equals(item.Header, "返回实时画布") || Equals(item.Header, "Return to live canvas") ||
                Equals(item.Header, "继续上一张截图") || Equals(item.Header, "Resume capture draft")));
            check(label + " project and image file commands remain available", new[] { "ToolbarV2.Open", "ToolbarV2.Save", "ToolbarV2.SaveAs", "Menu.ExportImage", "Menu.ExportTransparent", "Toolbar.Image" }
                .All(key => entries.Any(item => Equals(item.Header, L.Get(key)))));
            check(label + " canvas settings and stamp creation match toolbar actions", new[] { "Canvas.Title", "Feedback3.Toolbar.NewStamp", "Stamp.LibraryTitle", "Desktop.ToggleVisibility" }
                .All(key => entries.Any(item => Equals(item.Header, L.Get(key)))));
            var stack = new Controls.StackPanel { Width = 320 };
            Controls.Grid.SetIsSharedSizeScope(stack, true);
            var shortItem = new Controls.MenuItem { Header = "Short", InputGestureText = "S", Width = 320 };
            var longItem = new Controls.MenuItem { Header = "Long", InputGestureText = "Ctrl+Shift+S", Width = 320 };
            stack.Children.Add(shortItem); stack.Children.Add(longItem);
            stack.Measure(new Wpf.Size(320, 200)); stack.Arrange(new Wpf.Rect(0, 0, 320, stack.DesiredSize.Height)); stack.UpdateLayout();
            var shortHint = VisualDescendants(shortItem).OfType<Controls.TextBlock>().Single(text => text.Text == "S");
            var longHint = VisualDescendants(longItem).OfType<Controls.TextBlock>().Single(text => text.Text == "Ctrl+Shift+S");
            double Right(Controls.TextBlock hint, Controls.MenuItem owner) => hint.TranslatePoint(new Wpf.Point(hint.ActualWidth, 0), owner).X;
            check(label + " shortcut strings share a right edge across different lengths", shortHint.HorizontalAlignment == Wpf.HorizontalAlignment.Right &&
                longHint.HorizontalAlignment == Wpf.HorizontalAlignment.Right && Math.Abs(Right(shortHint, shortItem) - Right(longHint, longItem)) < .01);
        }
        L.Initialize("zh-CN");
    }

    private static void Set(object target, string field, object value) => typeof(DesktopSession).GetField(field, Private)!.SetValue(target, value);
    private static IEnumerable<Controls.MenuItem> Descendants(Controls.ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<Controls.MenuItem>())
        {
            yield return item;
            foreach (var child in Descendants(item)) yield return child;
        }
    }
    private static IEnumerable<Wpf.DependencyObject> VisualDescendants(Wpf.DependencyObject parent)
    {
        for (int i = 0; i < Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Media.VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
}
