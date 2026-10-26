using System.Windows;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

internal static class WindowTitleTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string language = L.Preference;
        try
        {
            foreach (bool english in new[] { false, true })
            {
                L.Initialize(english ? "en" : "zh-CN"); AppTheme.Apply("light");
                foreach (var entry in new (string Key, string Name)[]
                {
                    ("Settings.Title", english ? "Settings" : "设置"),
                    ("Recovery.HistoryTitle", english ? "Workspace history" : "历史画布"),
                    ("Toolbar.Title", english ? "Toolbar" : "工具条"),
                    ("App.OperationFailed", english ? "Operation failed" : "操作失败"),
                    ("App.StartupFailed", english ? "Startup failed" : "启动失败")
                })
                foreach (bool regular in new[] { false, true })
                {
                    var window = new Window { Title = L.Get(entry.Key) };
                    try
                    {
                        if (regular) OverlayWindowScope.UseRegularWindow(window);
                        AppWindowChrome.ApplyDialog(window);
                        string expected = regular ? (english ? "FloatCanvas · " : "浮绘·") + entry.Name : entry.Name;
                        check($"{L.CurrentLanguage} {entry.Key} regular={regular} follows the title rule", window.Title == expected);
                        AppWindowChrome.ApplyDialog(window);
                        check($"{L.CurrentLanguage} {entry.Key} repeated chrome never duplicates the product name", window.Title == expected);
                    }
                    finally { window.Close(); }
                }
                var notice = AppDialog.Create(null, "fixture", L.Get("App.Name")).Window;
                try { check(L.CurrentLanguage + " ordinary generic messages have a named title", notice.Title == (english ? "FloatCanvas · Notice" : "浮绘·提示") && !notice.Topmost); }
                finally { notice.Close(); }
            }
        }
        finally { L.Initialize(language); }
    }
}
