using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private Func<Task>? _addWindowSourceCommand = null;
    private Func<Guid, Task>? _rebindWindowSourceCommand = null, _cropWindowSourceCommand = null;
    private Action<IReadOnlyList<Guid>, bool>? _setWindowSourcesPausedCommand = null;
    private Func<IReadOnlyList<Guid>, Task>? _freezeWindowSourcesCommand = null;
    private Func<IReadOnlyList<Guid>, WindowSourcesUiState>? _windowSourcesUiProvider = null;
    private WindowSourcesUiState? _shownWindowSourcesState;
    private Guid[] _shownWindowSourcesSelection = [];

    private void ConfigureWindowSourcesUi(InspectorWindow panel)
    {
        panel.SetWindowSourcesUi(ids => _windowSourcesUiProvider?.Invoke(ids) ?? new());
        panel.WindowSourceRebindRequested += id => { if (_rebindWindowSourceCommand is { } command) _ = RunCommand(() => command(id)); };
        panel.WindowSourceCropRequested += id => { if (_cropWindowSourceCommand is { } command) _ = RunCommand(() => command(id)); };
        panel.WindowSourcesFreezeRequested += ids => { if (_freezeWindowSourcesCommand is { } command) _ = RunCommand(() => command(ids)); };
        panel.WindowSourcesPauseRequested += (ids, paused) => { if (_setWindowSourcesPausedCommand is { } command) RunUiAction(() => command(ids, paused)); };
    }
    private void RefreshWindowSourcesUi()
    {
        var selection = Active.Selection.ToArray(); var state = _windowSourcesUiProvider?.Invoke(Array.AsReadOnly(selection)) ?? new();
        if (state == _shownWindowSourcesState && selection.Order().SequenceEqual(_shownWindowSourcesSelection.Order())) return;
        _shownWindowSourcesState = state; _shownWindowSourcesSelection = selection;
        _inspector?.RefreshWindowSourcesUi();
    }
    private MenuItem CreateWindowSourcesMenu()
    {
        var menu = new MenuItem { Header = L.Get("WindowSource.Menu"), Icon = CommandGlyphs.Create(CommandGlyphs.WindowSources) };
        var actions = new Dictionary<string, MenuItem>();
        void Add(string action, string glyph, Action execute)
        {
            var item = new MenuItem { Header = L.Get("WindowSource." + action), Icon = CommandGlyphs.Create(glyph), IsEnabled = false };
            item.Click += (_, _) => { if (item.IsEnabled) execute(); }; actions.Add(action, item); menu.Items.Add(item);
        }
        Guid[] Selection() => Active.Selection.ToArray();
        WindowSourcesUiState State() => _windowSourcesUiProvider?.Invoke(Array.AsReadOnly(Selection())) ?? new();
        Add("Add", CommandGlyphs.WindowSourceAdd, () => { if (_addWindowSourceCommand is { } command) _ = RunCommand(command); });
        Add("Pause", CommandGlyphs.WindowSourcePause, () => { if (_setWindowSourcesPausedCommand is { } command) RunUiAction(() => command(Selection(), true)); });
        Add("Resume", CommandGlyphs.WindowSourceResume, () => { if (_setWindowSourcesPausedCommand is { } command) RunUiAction(() => command(Selection(), false)); });
        menu.Items.Add(new Separator());
        Add("Rebind", CommandGlyphs.WindowSourceRebind, () => { if (State().EditableImageId is { } id && _rebindWindowSourceCommand is { } command) _ = RunCommand(() => command(id)); });
        Add("Crop", CommandGlyphs.WindowSourceCrop, () => { if (State().EditableImageId is { } id && _cropWindowSourceCommand is { } command) _ = RunCommand(() => command(id)); });
        Add("Freeze", CommandGlyphs.WindowSourceFreeze, () => { if (_freezeWindowSourcesCommand is { } command) _ = RunCommand(() => command(Selection())); });
        menu.SubmenuOpened += (_, _) =>
        {
            var state = State();
            actions["Add"].IsEnabled = state.CanAdd && _addWindowSourceCommand is not null;
            actions["Pause"].IsEnabled = state.CanPause && _setWindowSourcesPausedCommand is not null;
            actions["Resume"].IsEnabled = state.CanResume && _setWindowSourcesPausedCommand is not null;
            actions["Rebind"].IsEnabled = state.CanRebind && state.EditableImageId is not null && _rebindWindowSourceCommand is not null;
            actions["Crop"].IsEnabled = state.CanCrop && state.EditableImageId is not null && _cropWindowSourceCommand is not null;
            actions["Freeze"].IsEnabled = state.CanFreeze && _freezeWindowSourcesCommand is not null;
            foreach (var item in actions.Values) item.ToolTip = state.ReasonKey is { } reason ? L.Get(reason) : null;
        };
        return menu;
    }
}
