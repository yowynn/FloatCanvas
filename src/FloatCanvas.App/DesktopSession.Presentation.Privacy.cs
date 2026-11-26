using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PresentationKeyboardPrivacyGate? _presentationPrivacy;

    partial void ObservePresentationKeyboardPrivacy(PresentationObservedBatch batch)
    {
        if (_presentationRuntime?.ObservingKeyboard == true)
            PresentationPrivacy(_presentationRuntime).Observe();
    }

    private void RefreshPresentationPrivacy()
    {
        if (_presentationRuntime?.ObservingKeyboard == true)
            PresentationPrivacy(_presentationRuntime).Refresh();
        else _presentationPrivacy?.Refresh();
    }

    private PresentationKeyboardPrivacyGate PresentationPrivacy(PresentationRuntimeBroker runtime) =>
        _presentationPrivacy ??= new(_app.Dispatcher, runtime, isCanvasInput: foreground =>
            _host.HasEditingInput && !_host.UsesSystemInput && _host.Diagnostics.Any(window => window.InputHandle == foreground));
}
