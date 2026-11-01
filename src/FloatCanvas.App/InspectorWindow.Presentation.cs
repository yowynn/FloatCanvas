using FloatCanvas.Core;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.Localization;

namespace FloatCanvas.App;

internal sealed partial class InspectorWindow
{
    private readonly PresentationSettingsPanel presentationSettings = new();
    private readonly PresentationRuntimeControls presentationRuntime = new();
    private Func<Guid?, PresentationRuntimeUiState>? presentationRuntimeProvider;
    private bool presentationSourceSelection;
    private EditorSession? presentationSettingsSession;
    private SceneDocument? presentationSettingsDocument;
    private long presentationSettingsScopeVersion;
    private bool HasCurrentPresentationSettingsContext => ReferenceEquals(getSession(), presentationSettingsSession) &&
        ReferenceEquals(getSession().Document, presentationSettingsDocument) && getSession().EditScopeVersion == presentationSettingsScopeVersion;
    internal event Action? PresentationRuntimeToggleRequested;
    internal event Action<Guid?>? PresentationTrailBakeRequested;

    internal void SetPresentationRuntimeUi(Func<Guid?, PresentationRuntimeUiState>? provider)
    { presentationRuntimeProvider = provider; RefreshPresentationRuntime(); }
    internal void RefreshPresentationRuntimeUi() => RefreshPresentationRuntime();

    private void InitializePresentationControls(StackPanel body)
    {
        var settingsSection = new StackPanel(); settingsSection.Children.Add(SectionHeading("Presentation.Settings")); settingsSection.Children.Add(presentationSettings);
        propertyPanel.Children.Insert(0, settingsSection); fieldContainers.Add("PresentationSettings", settingsSection);
        body.Children.Insert(body.Children.IndexOf(propertyPanel), presentationRuntime);
        presentationSettings.ValueChanged += key =>
        {
            if (refreshing) return;
            var session = getSession();
            if (!ReferenceEquals(session, presentationSettingsSession) || !ReferenceEquals(session.Document, presentationSettingsDocument) || session.EditScopeVersion != presentationSettingsScopeVersion)
            { SetStatus(AppText.Get("Inspector.SelectionChanged"), true); return; }
            ChangeField("Presentation." + key);
        };
        presentationSettings.CanBeginGesture = () =>
        { CompleteEdit(); return HasCurrentPresentationSettingsContext && !invalidDraft && canMutate() && !getSession().IsTransactionActive; };
        presentationSettings.GestureCompleted += commit =>
        {
            if (commit) { CompleteEdit(); return; }
            editTimer.Stop(); var previous = editSession; editSession = null;
            if (previous is not null && ReferenceEquals(previous, getSession()) && previous.IsTransactionActive && previous.TransactionId == editTransaction)
            {
                applying = true;
                try { previous.Cancel(); }
                finally { applying = false; }
            }
            LoadSelection(true);
        };
        presentationRuntime.ToggleRequested += () => PresentationRuntimeToggleRequested?.Invoke();
        presentationRuntime.BakeRequested += id => PresentationTrailBakeRequested?.Invoke(id);
        RefreshPresentationRuntime();
    }
    private void RefreshPresentationSettings(CanvasElement[] selected)
    {
        if (selected.Length == 0 || selected.Any(element => element.Kind != ElementKind.Presentation)) return;
        var session = getSession();
        var graph = new SceneGraph(session.Document);
        presentationSettingsSession = session; presentationSettingsDocument = session.Document; presentationSettingsScopeVersion = session.EditScopeVersion;
        presentationSettings.Refresh(selected.Select(element => (element.Id, element.Presentation ?? new PresentationSettings())),
            (session, session.EditScopeVersion, string.Join(",", session.Selection.Order())), canMutate() && selected.All(element => !graph.IsLocked(element.Id)));
    }
    private void RefreshPresentationRuntime()
    {
        presentationRuntime.Refresh(presentationRuntimeProvider);
        presentationRuntime.Visibility = presentationSourceSelection ? Visibility.Visible : Visibility.Collapsed;
    }
}
