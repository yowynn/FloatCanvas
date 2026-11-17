using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private Window? _commandWindow;
    private void ShowCommandSearch(bool configure = false)
    {
        if (_commandWindow is { IsVisible: true }) { ShowAppWindow(_commandWindow,true); return; }
        var draft = EditorCommandCatalog.Overrides.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
        var window = new Window { Title = configure ? FloatCanvas.Localization.AppText.Get("Shortcuts.Title") : FloatCanvas.Localization.AppText.Get("Shortcuts.Search"), Width = 680, Height = 560,
            MinWidth = 480, MinHeight = 360, Owner = _settingsWindow is { IsVisible:true } ? _settingsWindow : null, ShowInTaskbar = false };
        _commandWindow = window;
        EditorShortcutPolicy.CommandWindows.Add(window);
        var panel = new DockPanel { Margin = WindowLayout.ContentMargin() };
        var searchLabel=new TextBlock {Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Search"),Margin=new Thickness(0,0,0,4)};
        DockPanel.SetDock(searchLabel,Dock.Top);panel.Children.Add(searchLabel);
        var search = new TextBox { MinHeight = 32, Margin = new Thickness(0,0,0,8) };
        DockPanel.SetDock(search,Dock.Top); panel.Children.Add(search);
        var scope=new TextBlock {Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Scope"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        DockPanel.SetDock(scope,Dock.Top);panel.Children.Add(scope);
        var footer = new StackPanel { Margin = new Thickness(0,10,0,0) };
        DockPanel.SetDock(footer,Dock.Bottom); panel.Children.Add(footer);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        var list = new ListBox(); panel.Children.Add(list);
        System.Windows.Automation.AutomationProperties.SetName(search, FloatCanvas.Localization.AppText.Get("Shortcuts.Search"));
        System.Windows.Automation.AutomationProperties.SetName(list, FloatCanvas.Localization.AppText.Get("Shortcuts.Title"));
        EditorCommand? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as EditorCommand;
        void Refresh()
        {
            string? selected = Selected()?.Id; list.Items.Clear();
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
        void ActionButton(string name, Action action)
        { var button = new Button { Content=name, Margin=new Thickness(0,8,8,0), Padding=new Thickness(10,5,10,5) }; button.Click+=(_,_)=>RunUiAction(action); actions.Children.Add(button); }
        bool recording = false, wasSuspended = false; int recordingSlot = 0; EditorCommand? recordingCommand = null;
        void Record(int slot)
        {
            if (recording) return;
            if (Selected() is not { Configurable:true } command) { status.Text=FloatCanvas.Localization.AppText.Get("Shortcuts.Fixed"); return; }
            EditorShortcutPolicy.RecordingWindows.Add(window);
            recording = true; recordingSlot=slot; recordingCommand=command;
            status.Text=FloatCanvas.Localization.AppText.Get("Shortcuts.RecordHint"); wasSuspended=_host.ApplicationHotkeysSuspended; _host.SuspendApplicationHotkeys(true); window.Focus();
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
            ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.RecordPrimary"),()=>Record(0)); ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.RecordAlternate"),()=>Record(1));
            ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.Clear"),()=>{ if (Selected() is { Configurable:true } c) draft[c.Id]=[]; Refresh(); });
            ActionButton(FloatCanvas.Localization.AppText.Get("Shortcuts.Reset"),()=>{ if (Selected() is {} c) draft.Remove(c.Id); Refresh(); });
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
                EditorCommandCatalog.Overrides=draft; RefreshToolShortcutHints(); SessionWindowCoordinator.Close(window);
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
                var old=draft.GetValueOrDefault(recordingCommand!.Id,recordingCommand!.Defaults);
                var next=old.ToList();
                var chord=new CommandChord(KeyInterop.VirtualKeyFromKey(key),OverlayHost.CurrentModifiers);
                if (recordingSlot < next.Count) next[recordingSlot]=chord; else next.Add(chord);
                var candidate=new Dictionary<string,CommandChord[]>(draft) {[recordingCommand!.Id]=next.ToArray()};
                if (ValidateCommandBindings(candidate) is {} error) {status.Text=error;return;}
                draft=candidate;status.Text="";Refresh();return;
            }
            if(e.Key==Key.Escape) {e.Handled=true;SessionWindowCoordinator.Close(window);}
            else if(!configure && e.Key==Key.Enter) {e.Handled=true;RunSelected();}
            else if(e.Key==Key.Down && search.IsKeyboardFocusWithin) {e.Handled=true;list.Focus();}
        };
        list.MouseDoubleClick+=(_,_)=>{if(!configure)RunSelected();}; search.TextChanged+=(_,_)=>Refresh();
        window.Content=panel; ConfigureAppWindow(window, regular: !_host.IsEditing);
        window.Deactivated+=(_,_)=>CancelRecording();
        window.IsVisibleChanged+=(_,_)=>{if(!window.IsVisible)CancelRecording();};
        window.Closed+=(_,_)=>{EditorShortcutPolicy.CommandWindows.Remove(window);EditorShortcutPolicy.RecordingWindows.Remove(window);if(recording)_host.SuspendApplicationHotkeys(wasSuspended);if(ReferenceEquals(_commandWindow,window))_commandWindow=null;};
        Refresh();ShowAppWindow(window,true);search.Focus();
    }
}
