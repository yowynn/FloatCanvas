using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record WindowSourcesUiState(int SourceCount = 0, string? DisplayName = null,
    string StatusKey = "WindowSource.Status.Disconnected", string? ReasonKey = null, bool CanAdd = false,
    bool CanPause = false, bool CanResume = false, bool CanRebind = false, bool CanCrop = false,
    bool CanFreeze = false, Guid? EditableImageId = null, int OccurrenceCount = 0, long StatusRevision = 0);

internal enum WindowSourceAction { Pause, Resume, Rebind, Crop, Freeze }

/// <summary>Immutable status and commands only. No source document or capture service is owned by this view.</summary>
internal sealed class WindowSourceControls : StackPanel
{
    private readonly TextBlock name = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 4, 0, 6) };
    private readonly Dictionary<WindowSourceAction, Button> actions = [];
    internal WindowSourcesUiState State { get; private set; } = new();
    internal event Action<WindowSourceAction>? Requested;

    internal WindowSourceControls()
    {
        Children.Add(name); Children.Add(status); status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        Children.Add(PropertyPanelLayout.Pair(Action(WindowSourceAction.Pause), Action(WindowSourceAction.Resume)));
        Children.Add(PropertyPanelLayout.Pair(Action(WindowSourceAction.Rebind), Action(WindowSourceAction.Crop)));
        Children.Add(Action(WindowSourceAction.Freeze));
        Refresh(new());
    }

    internal void Refresh(WindowSourcesUiState state)
    {
        State = state; Visibility = state.SourceCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        name.Text = state.SourceCount == 1 ? state.DisplayName ?? L.Get("WindowSource.DefaultName") : L.Format("WindowSource.SourceCount", state.SourceCount);
        status.Text = L.Get(state.StatusKey) + (state.OccurrenceCount > 1 ? "\n" + L.Format("WindowSource.SharedCount", state.OccurrenceCount) : "") +
            (state.ReasonKey is { Length: > 0 } reason ? "\n" + L.Get(reason) : "");
        actions[WindowSourceAction.Pause].IsEnabled = state.CanPause;
        actions[WindowSourceAction.Resume].IsEnabled = state.CanResume;
        actions[WindowSourceAction.Rebind].IsEnabled = state.CanRebind && state.EditableImageId is not null;
        actions[WindowSourceAction.Crop].IsEnabled = state.CanCrop && state.EditableImageId is not null;
        actions[WindowSourceAction.Freeze].IsEnabled = state.CanFreeze;
    }

    private Button Action(WindowSourceAction action)
    {
        string label = L.Get("WindowSource." + action);
        var button = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            MinHeight = PropertyPanelLayout.ControlHeight, Margin = PropertyPanelLayout.RowMargin, Padding = new(6, 4, 6, 4) };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => { if (button.IsEnabled) Requested?.Invoke(action); };
        actions.Add(action, button); return button;
    }
}
