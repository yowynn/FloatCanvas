using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class ToolOptionsPanel
{
    private readonly PresentationSettingsPanel presentationSettings = new();
    private readonly PresentationRuntimeControls presentationRuntime = new();
    private readonly PresentationGlobalSettingsPanel presentationGlobalSettings = new();
    private Func<Guid?, PresentationRuntimeUiState>? presentationRuntimeProvider;
    private Func<PresentationGlobalSettings>? presentationGlobalProvider;
    private ToolParameters? presentationGestureStart;
    private EditorSession? presentationGestureSession;
    internal event Action? PresentationRuntimeToggleRequested;
    internal event Action<Guid?>? PresentationTrailBakeRequested;

    internal void SetPresentationGlobalUi(Func<PresentationGlobalSettings>? provider)
    { presentationGlobalProvider = provider; RefreshPresentationGlobalUi(); }
    internal void RefreshPresentationGlobalUi()
    {
        var settings = presentationGlobalProvider?.Invoke() ?? new();
        if (controller.Tool == EditorTool.PointerPresentation)
        {
            settings.Pointer = (controller.Parameters.PointerPresentation ?? settings.Pointer).Clone();
            presentationGlobalSettings.Refresh(settings);
        }
        else presentationGlobalSettings.Refresh(settings, false);
    }

    internal void SetPresentationRuntimeUi(Func<Guid?, PresentationRuntimeUiState>? provider)
    { presentationRuntimeProvider = provider; presentationRuntime.Refresh(provider); }
    internal void RefreshPresentationRuntimeUi() => presentationRuntime.Refresh(presentationRuntimeProvider);

    private void InitializePresentationParameters()
    {
        presentationSettings.ReportError = Error; presentationGlobalSettings.ReportError = Error;
        foreach (var panel in new System.Windows.Controls.Panel[] { presentationSettings, presentationGlobalSettings, presentationRuntime })
            foreach (var text in panel.Children.OfType<System.Windows.Controls.TextBlock>()) text.FontSize = PropertyPanelLayout.LabelFontSize;
        rows.Add("PresentationSettings", presentationSettings); parameterGrid.Children.Add(presentationSettings);
        rows.Add("PresentationRuntime", presentationRuntime); parameterGrid.Children.Add(presentationRuntime);
        rows.Add("PointerPresentationSettings", presentationGlobalSettings); parameterGrid.Children.Add(presentationGlobalSettings);
        presentationSettings.ValueChanged += key => { if (!refreshing) Change(parameters => presentationSettings.ApplyEdit(parameters.Presentation ??= new(), key)); };
        presentationSettings.CanBeginGesture = () => !refreshing && controller.Tool == EditorTool.Presentation;
        presentationSettings.GestureStarted += () => { presentationGestureStart = controller.Parameters; presentationGestureSession = controller.Session; };
        presentationSettings.GestureCompleted += commit =>
        {
            if (!commit && presentationGestureStart is { } original && ReferenceEquals(presentationGestureSession, controller.Session) && controller.Tool == original.Tool)
            { controller.ApplyParameters(original); ParametersChanged?.Invoke(); }
            presentationGestureStart = null; presentationGestureSession = null;
        };
        presentationGlobalSettings.ValueChanged += value =>
        { if (!refreshing && controller.Tool == EditorTool.PointerPresentation) Change(parameters => parameters.PointerPresentation = value.Pointer.Clone()); };
        presentationGlobalSettings.EyedropperRequested += field => EyedropperRequested?.Invoke(field);
        presentationRuntime.ToggleRequested += () => PresentationRuntimeToggleRequested?.Invoke();
        presentationRuntime.BakeRequested += id => PresentationTrailBakeRequested?.Invoke(id);
    }

    private void RefreshPresentationParameters(ToolParameters parameters)
    {
        bool keyboard = parameters.Tool == EditorTool.Presentation;
        Show("PresentationSettings", keyboard); Show("PresentationRuntime", keyboard);
        Show("PointerPresentationSettings", parameters.Tool == EditorTool.PointerPresentation);
        RefreshPresentationGlobalUi();
        if (!keyboard) return;
        var keySettings = parameters.Presentation?.Clone() ?? new(); keySettings.Modules = PresentationModules.Keys;
        presentationSettings.Refresh([(Guid.Empty, keySettings)], (controller.Session, controller.Tool));
        presentationRuntime.Refresh(presentationRuntimeProvider);
    }
}
