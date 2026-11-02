using FloatCanvas.Core;
using System.Windows.Controls;
using System.Windows.Automation;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class InspectorWindow
{
    private readonly WindowSourceControls windowSources = new();
    private readonly SegmentedChoice windowSourceFit = new();
    private Func<IReadOnlyList<Guid>, WindowSourcesUiState>? windowSourcesProvider;
    private EditorSession? windowSourcesOwner;
    private SceneDocument? windowSourcesDocument;
    private long windowSourcesContent, windowSourcesScope;
    private Guid[] windowSourcesSelection = [];
    internal event Action<IReadOnlyList<Guid>, bool>? WindowSourcesPauseRequested;
    internal event Action<Guid>? WindowSourceRebindRequested;
    internal event Action<Guid>? WindowSourceCropRequested;
    internal event Action<IReadOnlyList<Guid>>? WindowSourcesFreezeRequested;

    private void InitializeWindowSourceFitControl()
    {
        foreach (var fit in Enum.GetValues<WindowSourceFit>())
            windowSourceFit.Add(L.Get("WindowSource.Fit." + fit), fit, CommandGlyphs.Create(fit == WindowSourceFit.Contain
                ? "M1,2 H15 V14 H1 Z M2,5 H14 V11 H2 Z" : "M1,2 H15 V14 H1 Z M4,5 L1,2 M12,5 L15,2 M4,11 L1,14 M12,11 L15,14"));
        AutomationProperties.SetName(windowSourceFit, L.Get("WindowSource.Fit"));
        var row = PropertyPanelLayout.Row(new PropertyLabel(L.Get("WindowSource.Fit")), windowSourceFit);
        fieldContainers.Add("WindowSourceFit", row); propertyPanel.Children.Add(row);
        windowSourceFit.SelectionChanged += (_, _) => { if (!refreshing && windowSourceFit.SelectedValue is WindowSourceFit) ChangeField("WindowSourceFit"); };
    }

    internal void SetWindowSourcesUi(Func<IReadOnlyList<Guid>, WindowSourcesUiState>? provider)
    { windowSourcesProvider = provider; RefreshWindowSourcesUi(); }

    internal void RefreshWindowSourcesUi()
    {
        var owner = getSession(); windowSourcesOwner = owner; windowSourcesDocument = owner.Document;
        windowSourcesContent = owner.ContentVersion; windowSourcesScope = owner.EditScopeVersion;
        windowSourcesSelection = owner.Selection.ToArray();
        windowSources.Refresh(windowSourcesProvider?.Invoke(Array.AsReadOnly(windowSourcesSelection)) ?? new());
    }

    private void InitializeWindowSourcesControls(StackPanel body)
    {
        body.Children.Insert(body.Children.IndexOf(propertyPanel), windowSources);
        windowSources.Requested += action =>
        {
            var owner = getSession();
            if (!ReferenceEquals(owner, windowSourcesOwner) || !ReferenceEquals(owner.Document, windowSourcesDocument) ||
                owner.ContentVersion != windowSourcesContent || owner.EditScopeVersion != windowSourcesScope ||
                !owner.Selection.SetEquals(windowSourcesSelection)) return;
            var selected = Array.AsReadOnly(windowSourcesSelection.ToArray());
            if (action is WindowSourceAction.Pause or WindowSourceAction.Resume)
            { WindowSourcesPauseRequested?.Invoke(selected, action == WindowSourceAction.Pause); return; }
            if (!canMutate() || owner.IsTransactionActive) return;
            if (action == WindowSourceAction.Freeze) WindowSourcesFreezeRequested?.Invoke(selected);
            else if (windowSources.State.EditableImageId is { } id)
            {
                if (action == WindowSourceAction.Rebind) WindowSourceRebindRequested?.Invoke(id);
                else if (action == WindowSourceAction.Crop) WindowSourceCropRequested?.Invoke(id);
            }
        };
    }
}
