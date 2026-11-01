using FloatCanvas.Core;
using System.Windows.Controls;

namespace FloatCanvas.App;

internal sealed partial class InspectorWindow
{
    private readonly WindowSourceControls windowSources = new();
    private Func<IReadOnlyList<Guid>, WindowSourcesUiState>? windowSourcesProvider;
    private EditorSession? windowSourcesOwner;
    private SceneDocument? windowSourcesDocument;
    private long windowSourcesContent, windowSourcesScope;
    private Guid[] windowSourcesSelection = [];
    internal event Action<IReadOnlyList<Guid>, bool>? WindowSourcesPauseRequested;
    internal event Action<Guid>? WindowSourceRebindRequested;
    internal event Action<Guid>? WindowSourceCropRequested;
    internal event Action<IReadOnlyList<Guid>>? WindowSourcesFreezeRequested;

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
