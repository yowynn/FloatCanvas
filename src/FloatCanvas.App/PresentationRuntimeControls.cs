using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record PresentationRuntimeUiState(bool Armed, bool Observing, bool CanBake, string? StatusKey = null);

/// <summary>Observation status only. Key layers run automatically; pointer effects have their own tool switch.</summary>
internal sealed class PresentationRuntimeControls : StackPanel
{
    private readonly TextBlock status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) };
    internal event Action? ToggleRequested { add { } remove { } }
    internal event Action<Guid?>? BakeRequested { add { } remove { } }
    internal PresentationRuntimeUiState State { get; private set; } = new(false, false, false);

    internal PresentationRuntimeControls()
    {
        Children.Add(status);
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        Refresh(null);
    }

    internal void Refresh(Func<Guid?, PresentationRuntimeUiState>? provider, Guid? nodeId = null)
    {
        State = provider?.Invoke(nodeId) ?? new(false, false, false);
        status.Text = L.Get(State.StatusKey ?? (State.Observing ? "Presentation.Status.Running" : "Presentation.Status.Stopped"));
    }
}
