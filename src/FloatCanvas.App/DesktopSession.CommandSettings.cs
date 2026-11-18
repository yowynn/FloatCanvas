using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private Window? _commandWindow;
    private List<string> _shortcutBindingNotices = [];
    private void ShowCommandSearch(bool configure = false)
    {
        if (_commandWindow is { IsVisible: true }) { ShowAppWindow(_commandWindow,true); return; }
        var draft = EditorCommandCatalog.Overrides.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
        var window = new Window { Title = configure ? L.Get("Shortcuts.Title") : L.Get("Shortcuts.Search"),
            Width = configure ? 1040 : 680, Height = configure ? 660 : 560, MinWidth = configure ? 760 : 480,
            MinHeight = configure ? 440 : 360, Owner = _settingsWindow is { IsVisible:true } ? _settingsWindow : null, ShowInTaskbar = false };
        _commandWindow = window;
        EditorShortcutPolicy.CommandWindows.Add(window);
        var panel = new DockPanel { Margin = WindowLayout.ContentMargin() };
        var searchLabel=new TextBlock {Text=L.Get(configure ? "Shortcuts.TableSearch" : "Shortcuts.Search"),Margin=new Thickness(0,0,0,4)};
        DockPanel.SetDock(searchLabel,Dock.Top);panel.Children.Add(searchLabel);
        var search = new TextBox { MinHeight = 32, Margin = new Thickness(0,0,0,8) };
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        search.Margin = new Thickness(0);
        var findKey = new Button { Content = L.Get("Shortcuts.FindKey"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
        if (configure) { DockPanel.SetDock(findKey, Dock.Right); searchRow.Children.Add(findKey); }
        searchRow.Children.Add(search); DockPanel.SetDock(searchRow,Dock.Top); panel.Children.Add(searchRow);
        var scope=new TextBlock {Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Scope"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        DockPanel.SetDock(scope,Dock.Top);panel.Children.Add(scope);
        var areas = Enum.GetValues<ShortcutSettingsTable.Area>();
        var sources = Enum.GetValues<ShortcutSettingsTable.Source>();
        ComboBox Filter(string label, IEnumerable<string> values)
        {
            var filter = new ComboBox { MinWidth = 130, Margin = new Thickness(0, 0, 8, 0) };
            filter.Items.Add(L.Get(label));
            foreach (var value in values) filter.Items.Add(value);
            filter.SelectedIndex = 0;
            System.Windows.Automation.AutomationProperties.SetName(filter, L.Get(label));
            return filter;
        }
        var areaFilter = Filter("Shortcuts.AllAreas", areas.Select(ShortcutSettingsTable.AreaLabel));
        var sourceFilter = Filter("Shortcuts.AllSources", sources.Select(ShortcutSettingsTable.SourceLabel).Append(L.Get("Shortcuts.Unbound")));
        if (configure)
        {
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            filters.Children.Add(areaFilter); filters.Children.Add(sourceFilter);
            DockPanel.SetDock(filters, Dock.Top); panel.Children.Add(filters);
            var header = new Border
            {
                Child = ShortcutSettingsTable.Cells(new[] { "Shortcuts.AreaColumn", "Shortcuts.CommandColumn", "Settings.Shortcuts",
                    "Shortcuts.WhenColumn", "Shortcuts.SourceColumn" }.Select(key => L.Get(key)).ToArray(), header: true),
                Padding = new Thickness(7, 7, 7 + WindowLayout.ScrollBarWidth, 7)
            };
            header.SetResourceReference(Border.BackgroundProperty, "CanvasInput");
            DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        }
        var footer = new StackPanel { Margin = new Thickness(0,10,0,0) };
        DockPanel.SetDock(footer,Dock.Bottom); panel.Children.Add(footer);
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        details.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        if (configure) footer.Children.Add(details);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(new ScrollViewer { Content = status, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var actions = new WrapPanel(); footer.Children.Add(actions);
        var list = new ListBox(); panel.Children.Add(list);
        if (configure)
        {
            list.Padding = new Thickness(0);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
        }
        System.Windows.Automation.AutomationProperties.SetName(search, searchLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(list, FloatCanvas.Localization.AppText.Get("Shortcuts.Title"));
        EditorCommand? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as EditorCommand;
        CommandChord? recordedKey = null;
        var editButtons = new List<Button>();
        void UpdateSelection()
        {
            if (!configure) return;
            foreach (var button in editButtons) button.IsEnabled = Selected() is { Configurable: true };
            details.Text = (list.SelectedItem as ListBoxItem)?.ToolTip as string ?? L.Get("Shortcuts.NoResults");
        }
        void Refresh()
        {
            string? selected = Selected()?.Id;
            string? selectedLabel = (list.SelectedItem as ListBoxItem)?.ToolTip as string;
            list.Items.Clear();
            if (configure)
            {
                foreach (var entry in ShortcutSettingsTable.Rows(draft).OrderBy(row => row.Area))
                {
                    if (areaFilter.SelectedIndex > 0 && entry.Area != areas[areaFilter.SelectedIndex - 1] ||
                        sourceFilter.SelectedIndex > 0 && (sourceFilter.SelectedIndex <= sources.Length
                            ? entry.Source != sources[sourceFilter.SelectedIndex - 1] : entry.Command is null || entry.Keys.Length != 0) ||
                        !entry.Matches(search.Text, recordedKey)) continue;
                    string summary = $"{entry.Name}  ·  {entry.Binding}    {entry.Context}";
                    var row = new ListBoxItem
                    {
                        Tag = entry.Command, ToolTip = summary,
                        Content = ShortcutSettingsTable.Cells([ShortcutSettingsTable.AreaLabel(entry.Area), entry.Name, entry.Binding,
                            entry.Context, ShortcutSettingsTable.SourceLabel(entry.Source)]),
                        Padding = new Thickness(6, 6, 6, 6)
                    };
                    System.Windows.Automation.AutomationProperties.SetName(row, summary);
                    list.Items.Add(row);
                    if (entry.Command is { } command ? command.Id == selected : selected is null && summary == selectedLabel)
                        list.SelectedItem = row;
                }
                if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
                UpdateSelection();
                return;
            }
            foreach (var command in EditorCommandCatalog.All)
            {
                var keys = draft.GetValueOrDefault(command.Id,command.Defaults);
                string binding = string.Join(" / ",keys.Select(k=>k.ToString()));
                if(binding.Length==0) binding=FloatCanvas.Localization.AppText.Get("Shortcuts.Unbound");
                string label = command.Name + "    " + binding + (draft.ContainsKey(command.Id) ? FloatCanvas.Localization.AppText.Get("Shortcuts.Custom") : "") +
                    (!configure && !CanExecuteEditorCommand(command) ? FloatCanvas.Localization.AppText.Get("Shortcuts.Unavailable") : "");
                if (!string.IsNullOrWhiteSpace(search.Text) && !(label+command.Id).Contains(search.Text,StringComparison.CurrentCultureIgnoreCase)) continue;
                var row = new ListBoxItem { Content=label, Tag=command, Padding=new Thickness(6),
                    ToolTip=configure?null:EditorCommandDisabledReason(command) };
                list.Items.Add(row); if (command.Id==selected) list.SelectedItem=row;
            }
            if (list.SelectedIndex<0 && list.Items.Count>0) list.SelectedIndex=0;
        }
        void ActionButton(string name, Action action, bool editsSelection = false)
        {
            var button = new Button { Content=name, Margin=new Thickness(0,8,8,0), Padding=new Thickness(10,5,10,5) };
            button.Click+=(_,_)=>RunUiAction(action); actions.Children.Add(button);
            if (editsSelection) editButtons.Add(button);
        }
        bool recording = false, findingKey = false, wasSuspended = false; int recordingSlot = 0; EditorCommand? recordingCommand = null;
        void BeginRecording(bool searchByKey)
        {
            if (recording) return;
            EditorShortcutPolicy.RecordingWindows.Add(window);
            recording = true; findingKey = searchByKey;
            status.Text=L.Get(searchByKey ? "Shortcuts.FindKeyHint" : "Shortcuts.RecordHint");
            wasSuspended=_host.ApplicationHotkeysSuspended; _host.SuspendApplicationHotkeys(true); window.Focus();
        }
        void Record(int slot)
        {
            if (recording) return;
            if (Selected() is not { Configurable:true } command) { status.Text=L.Get("Shortcuts.Source.Fixed"); return; }
            recordingSlot=slot; recordingCommand=command;
            BeginRecording(searchByKey: false);
        }
        void CancelRecording()
        {
            if (!recording) return;
            recording=false; EditorShortcutPolicy.RecordingWindows.Remove(window);
            _host.SuspendApplicationHotkeys(wasSuspended);
            status.Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Cancelled");
        }
        void RunSelected()
        {
            if (Selected() is not { } command || !CanExecuteEditorCommand(command)) return;
            SessionWindowCoordinator.Close(window); RunUiAction(()=>ExecuteEditorCommand(command));
        }
        if (configure)
        {
            status.Text = string.Join("\n", _shortcutBindingNotices);
            findKey.Click += (_, _) => BeginRecording(searchByKey: true);
            ActionButton(L.Get("Shortcuts.KeyboardPreview"), () =>
            {
                if (recording) CancelRecording();
                var entries = ShortcutSettingsTable.Rows(draft).SelectMany(row =>
                    row.Keys.Select(chord => new ShortcutKeyboardPreviewWindow.Entry(chord, row.Name, row.Context))).ToList();
                var global = GlobalBindings(_settings.Toggle, _settings.Hide, _settings.GlobalCommandBindings);
                foreach (var (id, label) in new[] { ("Global.Toggle", "Settings.ToggleHotkey"), ("Global.Hide", "Settings.HideHotkey") }.Concat(OptionalGlobalCommands))
                {
                    var binding = global.GetValueOrDefault(id);
                    if (binding.VirtualKey != 0)
                        entries.Add(new(new((int)binding.VirtualKey, (OverlayModifiers)binding.Modifiers), L.Get(label), L.Get("Settings.GlobalShortcuts")));
                }
                entries.Add(new(new(0x1B, OverlayModifiers.Control | OverlayModifiers.Alt), L.Get("Shortcuts.KeyboardPreviewEmergency"), L.Get("Settings.GlobalShortcuts")));
                var preview = new ShortcutKeyboardPreviewWindow(entries, window);
                ConfigureAppWindow(preview, dialog: true, regular: !_host.IsEditing);
                preview.ShowInTaskbar = false;
                SessionWindowCoordinator.ShowModal(preview);
            });
            ActionButton(L.Get("Shortcuts.RecordPrimary"),()=>Record(0), editsSelection: true);
            ActionButton(L.Get("Shortcuts.RecordAlternate"),()=>Record(1), editsSelection: true);
            ActionButton(L.Get("Shortcuts.Clear"),()=>{ if (Selected() is { Configurable:true } c) draft[c.Id]=[]; Refresh(); }, editsSelection: true);
            ActionButton(L.Get("Shortcuts.Reset"),()=>{ if (Selected() is { Configurable:true } c) draft.Remove(c.Id); Refresh(); }, editsSelection: true);
            ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.ResetAll"),()=>{draft.Clear();Refresh();});
            ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.Export"),()=>{
                var dialog = new Microsoft.Win32.SaveFileDialog { Filter=FloatCanvas.Localization.AppText.Get("Shortcuts.FileFilter"),FileName="FloatCanvas-shortcuts.json" };
                if (SessionWindowCoordinator.ShowNativeModal(window,()=>dialog.ShowDialog(window))==true) File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(draft,new JsonSerializerOptions {WriteIndented=true}));
            });
            ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.Import"),()=>{
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter=FloatCanvas.Localization.AppText.Get("Shortcuts.FileFilter") };
                if (SessionWindowCoordinator.ShowNativeModal(window,()=>dialog.ShowDialog(window))!=true) return;
                var incoming = JsonSerializer.Deserialize<Dictionary<string,CommandChord[]>>(File.ReadAllText(dialog.FileName)) ?? [];
                foreach (var id in incoming.Keys.Where(EditorCommandCatalog.IsRetired).ToArray()) incoming.Remove(id);
                if (ValidateCommandBindings(incoming) is {} error) {status.Text=error;return;}
                draft=incoming;Refresh();status.Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Imported");
            });
            ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.Save"),()=>{
                if (ValidateCommandBindings(draft) is {} error) { status.Text=error;return; }
                var before=_settings.CommandBindings; _settings.CommandBindings=draft;
                try { _settings.Save(); } catch { _settings.CommandBindings=before; throw; }
                EditorCommandCatalog.Overrides=draft; _shortcutBindingNotices.Clear(); RefreshToolShortcutHints(); SessionWindowCoordinator.Close(window);
            });
        }
        else { ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.Execute"),RunSelected); ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.Configure"),()=>{SessionWindowCoordinator.Close(window);ShowCommandSearch(true);}); }
        window.PreviewKeyDown += (_,e)=>{
            var key=e.Key==Key.System?e.SystemKey:e.Key;
            if (recording)
            {
                e.Handled=true;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
                recording=false; EditorShortcutPolicy.RecordingWindows.Remove(window); _host.SuspendApplicationHotkeys(wasSuspended);
                if (key==Key.Escape) {status.Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Cancelled");return;}
                var chord=new CommandChord(KeyInterop.VirtualKeyFromKey(key),OverlayHost.CurrentModifiers);
                if (findingKey)
                {
                    search.Text = chord.ToString();
                    recordedKey = chord;
                    status.Text = "";
                    Refresh();
                    search.Focus();
                    search.SelectAll();
                    return;
                }
                var old=draft.GetValueOrDefault(recordingCommand!.Id,recordingCommand!.Defaults);
                var next=old.ToList();
                if (recordingSlot < next.Count) next[recordingSlot]=chord; else next.Add(chord);
                var candidate=new Dictionary<string,CommandChord[]>(draft) {[recordingCommand!.Id]=next.ToArray()};
                if (ValidateCommandBindings(candidate) is {} error) {status.Text=error;return;}
                draft=candidate;status.Text="";Refresh();return;
            }
            if(e.Key==Key.Escape)
            {
                e.Handled=true;
                if (areaFilter.IsDropDownOpen) areaFilter.IsDropDownOpen = false;
                else if (sourceFilter.IsDropDownOpen) sourceFilter.IsDropDownOpen = false;
                else SessionWindowCoordinator.Close(window);
            }
            else if(!configure && e.Key==Key.Enter) {e.Handled=true;RunSelected();}
            else if(e.Key==Key.Down && search.IsKeyboardFocusWithin) {e.Handled=true;list.Focus();}
        };
        list.MouseDoubleClick += (_, e) =>
        {
            if (!configure) RunSelected();
            else if (EditorShortcutPolicy.Ancestors(e.OriginalSource as DependencyObject).OfType<ListBoxItem>().Any()) Record(0);
        };
        list.SelectionChanged += (_, _) => UpdateSelection();
        search.TextChanged += (_, _) => { recordedKey = null; Refresh(); };
        areaFilter.SelectionChanged += (_, _) => Refresh();
        sourceFilter.SelectionChanged += (_, _) => Refresh();
        window.Content=panel; ConfigureAppWindow(window, regular: !_host.IsEditing);
        window.Deactivated+=(_,_)=>CancelRecording();
        window.IsVisibleChanged+=(_,_)=>{if(!window.IsVisible)CancelRecording();};
        window.Closed+=(_,_)=>{EditorShortcutPolicy.CommandWindows.Remove(window);EditorShortcutPolicy.RecordingWindows.Remove(window);if(recording)_host.SuspendApplicationHotkeys(wasSuspended);if(ReferenceEquals(_commandWindow,window))_commandWindow=null;};
        Refresh();ShowAppWindow(window,true);search.Focus();
    }
}
