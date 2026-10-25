using System.Windows;

namespace FloatCanvas.App;

/// <summary>Content edges shared by framed windows and popup panels, excluding the 1 DIP window border.</summary>
internal static class WindowLayout
{
    internal const double ContentInset = 12;
    // Matches the ScrollBar style in AppTheme; do not reserve the OS scrollbar metric.
    internal const double ScrollBarWidth = 12;
    internal static Thickness ContentMargin(double top = ContentInset, double bottom = ContentInset)
        => new(ContentInset, top, ContentInset, bottom);
}
