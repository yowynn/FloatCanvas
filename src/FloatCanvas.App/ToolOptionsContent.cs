using System.Windows;

namespace FloatCanvas.App;

internal sealed record ToolOptionsHeader(string Title, string Target = "", string HelpTitle = "", string HelpText = "", FrameworkElement? Actions = null);

internal interface IToolOptionsContent
{
    FrameworkElement Parameters { get; }
    FrameworkElement Presets { get; }
    ToolOptionsHeader Header { get; }
    void PrepareForShow();
    void CancelEditing();
}

/// <summary>Tool-specific editors provide content; the host owns windows and layout.</summary>
internal sealed class ToolOptionsContent(FrameworkElement parameters, FrameworkElement presets,
    Func<ToolOptionsHeader> header, Action prepare, Action cancel) : IToolOptionsContent
{
    public FrameworkElement Parameters => parameters;
    public FrameworkElement Presets => presets;
    public ToolOptionsHeader Header => header();
    public void PrepareForShow() => prepare();
    public void CancelEditing() => cancel();
}
