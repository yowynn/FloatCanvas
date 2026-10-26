using System.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class WindowTitles
{
    internal static string Format(string title, bool regular)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        string name = L.Get("App.Name");
        string text = title.Trim();
        if (text.StartsWith(name, StringComparison.Ordinal))
        {
            string suffix = text[name.Length..].TrimStart();
            if (suffix.StartsWith('·')) text = suffix[1..].TrimStart();
        }
        if (text == name) text = L.Get("Window.Notice");
        return regular ? L.Format("Window.RegularTitle", name, text) : text;
    }

    internal static void Apply(Window window)
    {
        bool regular = false;
        for (var owner = window; owner is not null; owner = owner.Owner)
            if (OverlayWindowScope.IsRegularWindow(owner)) { regular = true; break; }
        window.Title = Format(window.Title, regular);
    }
}
