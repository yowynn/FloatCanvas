using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PresentationKeyboardPrivacyGate? _presentationPrivacy;

    partial void ObservePresentationKeyboardPrivacy(PresentationObservedBatch batch)
    {
        if (_presentationRuntime?.ObservingKeyboard == true)
            (_presentationPrivacy ??= new(_app.Dispatcher, _presentationRuntime)).Observe();
    }

    private void RefreshPresentationPrivacy()
    {
        if (_presentationRuntime?.ObservingKeyboard == true)
            (_presentationPrivacy ??= new(_app.Dispatcher, _presentationRuntime)).Refresh();
        else _presentationPrivacy?.Refresh();
    }
}
