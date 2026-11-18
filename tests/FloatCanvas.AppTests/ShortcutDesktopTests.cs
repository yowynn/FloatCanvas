using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L=FloatCanvas.Localization.AppText;

internal static class ShortcutDesktopTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        var app=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Func<IReadOnlyList<MonitorInfo>> screens=()=>[new("shortcuts",new(0,0,640,480),new(0,0,640,440),96,96,true)];
        using var host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Hidden,null,[OverlayMonitorScope.AllScreens,screens,false],null)!;
        using var desktop=new DesktopSession(app,host);
        T Field<T>(string name)=>(T)typeof(DesktopSession).GetField(name,Hidden)!.GetValue(desktop)!;
        object? Call(string name,params object?[] args)=>typeof(DesktopSession).GetMethod(name,Hidden)!.Invoke(desktop,args);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible=false;
        var editor=Field<EditorController>("_editor");
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host,true);
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host,true);
        editor.BackgroundPickingEnabled=false;
        editor.Tool=EditorTool.Rectangle;
        Call("CurrentGroupCanvasView"); editor.ResumePresentedPicking();
        editor.Pointer(new(OverlayPointerKind.Down,new(20,20),OverlayMouseButton.Left,0));
        check("idempotent entry fixture owns a creation",editor.Session.IsTransactionActive);
        Call("ExecuteGlobalCommand","Global.Enter");
        check("retired entry command cannot affect the active gesture",editor.Session.IsTransactionActive);
        editor.Cancel();
        BorrowedToolTests.Run(check,desktop,editor);
        var parametersBeforeOptions=editor.Parameters.Clone();
        var toolbar=Field<Window>("_toolbar"); toolbar.Show(); toolbar.UpdateLayout();
        var commandLegends = Field<List<TextBlock>>("_toolbarShortcutHintLabels");
        check("stamp buttons show their visible character shortcuts", commandLegends.Single(label => Equals(label.Tag, "Stamp.Library")).Text == "~" &&
            commandLegends.Single(label => Equals(label.Tag, "Stamp.Create")).Text == "+");
        foreach(var tool in new[]{EditorTool.Select,EditorTool.Freehand,EditorTool.Canvas})
        {
            Call("CloseToolOptions"); editor.Tool=tool;
            Call("OnCommandInput",new OverlayKeyEvent(0x73,OverlayModifiers.None,false));
            var options=Field<Window>("_toolOptionsWindow");
            check("F4 opens current tool properties for "+tool,options.IsVisible && editor.Tool==tool);
            Call("OnCommandInput",new OverlayKeyEvent(0x73,OverlayModifiers.None,false));
            check("repeated F4 keeps tool properties open for "+tool,options.IsVisible && editor.Tool==tool);
            var menu=(MenuItem)Call("ToolMenuItem",tool)!;
            check("tool menu advertises the common properties shortcut for "+tool,menu.ToolTip?.ToString()?.Contains(EditorCommandCatalog.Hint("Panel.Tools"))==true);
        }
        Call("CloseToolOptions"); toolbar.Hide(); editor.ApplyParameters(parametersBeforeOptions);
        editor.Tool = EditorTool.Stamp;
        var toolOptionsCommand = EditorCommandCatalog.All.Single(command => command.Id == "Panel.Tools");
        Call("OnCommandInput", new OverlayKeyEvent(0x73, OverlayModifiers.None, false));
        check("stamp placement has no tool options window or runnable options command",
            !Field<Window>("_toolOptionsWindow").IsVisible && Call("CanExecuteEditorCommand", toolOptionsCommand) is false);
        var bindings=EditorCommandCatalog.Overrides;
        try
        {
            EditorCommandCatalog.Overrides=new()
            {
                ["Tools.More"]=[new(0x51)], ["Tool.Blur"]=[new(0x4B)],
                ["Tool.Freehand"]=[new(0x42,OverlayModifiers.Alt)], ["Stamp.Library"]=[new(0x76)]
            };
            editor.Tool=EditorTool.Select;
            Call("RefreshToolShortcutHints");Call("RefreshToolbarState");
            var tools=Field<Dictionary<EditorTool,Button>>("_toolbarToolButtons");
            var extra=Field<Dictionary<EditorTool,Button>>("_extraToolButtons");
            check("selection tooltip advertises tool properties",tools[EditorTool.Select].ToolTip?.ToString()?.Contains(EditorCommandCatalog.Hint("Panel.Tools"))==true);
            check("More tooltip follows its remapped binding after state refresh",tools[EditorTool.Blur].ToolTip?.ToString()?.EndsWith(" · Q")==true);
            check("extra tool tooltip exposes its newly assigned binding",extra[EditorTool.Blur].ToolTip?.ToString()?.Contains(" · K")==true);
            check("multi-key tool binding remains readable in tooltip",tools[EditorTool.Freehand].ToolTip?.ToString()?.Contains("Alt+B")==true);
            check("stamp library tooltip uses remapped binding",Field<Button>("_stampLibraryButton").ToolTip?.ToString()?.EndsWith(" · F7")==true);
            var stampBadge = commandLegends.Single(label => Equals(label.Tag, "Stamp.Library"));
            check("a multi-character stamp binding hides the entire legend", stampBadge.Text == "" && stampBadge.Visibility == Visibility.Collapsed);

            EditorCommandCatalog.Overrides["Stamp.Library"] = [new(0xC0), new(0xBD)];
            EditorCommandCatalog.Overrides["Tool.Blur"] = [new(0x76), new(0x4B)];
            EditorCommandCatalog.Overrides["Stamp.Slot0"] = [new(0x77), new(0x31)];
            Call("RefreshToolShortcutHints");
            Call("RefreshMoreToolPreviews");
            TextBlock Badge(Button button) => ((Grid)button.Content).Children.OfType<TextBlock>().Single();
            var blurBadge = Badge(extra[EditorTool.Blur]);
            var mainBadge = Badge(tools[EditorTool.Select]);
            check("stamp library uses its first eligible binding and retains both bindings in the tooltip",
                stampBadge.Text == "~" && stampBadge.Visibility == Visibility.Visible &&
                Field<Button>("_stampLibraryButton").ToolTip?.ToString()?.EndsWith(" · ~ / -") == true);
            check("expanded More tools use the main row position and fall back to an eligible alternate",
                blurBadge.Text == "K" && blurBadge.Visibility == Visibility.Visible &&
                blurBadge.HorizontalAlignment == HorizontalAlignment.Right && blurBadge.VerticalAlignment == VerticalAlignment.Top &&
                blurBadge.RenderTransform.Value == mainBadge.RenderTransform.Value && Badge(tools[EditorTool.Blur]).Text == "Q");
            check("an unbound extra tool has no badge or mask", Badge(extra[EditorTool.Pixelate]).Visibility == Visibility.Collapsed);
            var shelf = Field<StampShelfControl>("_stampShelf");
            shelf.PrepareForShow();
            var slotBadge = Badge(((StackPanel)shelf.Content).Children.OfType<Button>().First());
            check("the stamp slot uses its eligible alternate in the lower row",
                slotBadge.Text == "1" && slotBadge.Visibility == Visibility.Visible && slotBadge.VerticalAlignment == VerticalAlignment.Bottom);
            if (Environment.GetEnvironmentVariable("FLOATCANVAS_SHORTCUT_SETTINGS_PREVIEW") is { } output)
            {
                var upper = Field<ToolbarUpperRow>("_toolbarUpperRow");
                toolbar.Show();
                upper.SetExpanded(true);
                toolbar.UpdateLayout();
                check("assigned More shortcut is visible in the actual expanded toolbar", blurBadge.IsVisible);
                Preview4SettingsRuntimeFixture.Render(toolbar, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, "toolbar-badges.png"));
                upper.SetExpanded(false);
                toolbar.Hide();
            }
        }
        finally {EditorCommandCatalog.Overrides=bindings;Call("RefreshToolShortcutHints");}
        check("command search explains an unavailable crop",Call("EditorCommandDisabledReason",EditorCommandCatalog.All.Single(c=>c.Tool==EditorTool.Crop)) as string==L.Get("Audit.ImageRequirement"));
        var pen=editor.Parameters;pen.Tool=EditorTool.Freehand;pen.Style.StrokeWidth=7;editor.ApplyParameters(pen);
        // FrozenGlobalCaptureTests covers selection, cancellation and insertion without switching tools.
        var settings = Field<UserSettings>("_settings");
        bool pointerEnabled = settings.PresentationGlobal.Pointer.Enabled;
        Call("ExecuteGlobalCommand", "Global.Pointer");
        check("global pointer command toggles the visible master setting", settings.PresentationGlobal.Pointer.Enabled != pointerEnabled);
        Call("ExecuteGlobalCommand", "Global.Pointer");
        check("global pointer command restores the same master setting", settings.PresentationGlobal.Pointer.Enabled == pointerEnabled);
        // Native registration on the isolated desktop, without injecting input.
        var original=new Dictionary<string,HotkeyBinding> { ["Global.Capture"]=new(3,0x79) };
        host.RegisterCommandHotkeys(original);
        using var peer=new HwndSource(new HwndSourceParameters("shortcut conflict fixture") {Width=1,Height=1,WindowStyle=0});
        check("reserve conflicting chord on the isolated desktop",RegisterHotKey(peer.Handle,91,3|0x4000,0x7A));
        try
        {
            bool rejected=false;
            try { host.RegisterCommandHotkeys(new Dictionary<string,HotkeyBinding> { ["Global.Capture"]=new(3,0x7A) }); }
            catch(System.ComponentModel.Win32Exception) { rejected=true; }
            var actual=(Dictionary<string,HotkeyBinding>)typeof(OverlayHost).GetField("_commandHotkeys",Hidden)!.GetValue(host)!;
            check("registration failure restores the complete prior collection",rejected && actual.Count==1 && actual["Global.Capture"]==original["Global.Capture"]);
        }
        finally {UnregisterHotKey(peer.Handle,91);}
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, false);
        Call("ShowHotkeySettings");
        var settingsWindow = Field<AppSettingsWindow>("_settingsWindow");
        var settingsPages = (Grid)typeof(AppSettingsWindow).GetField("pages", Hidden)!.GetValue(settingsWindow)!;
        var pages = settingsPages.Children.OfType<ScrollViewer>().Select(page => (StackPanel)page.Content).ToArray();
        IEnumerable<StackPanel> Cards(StackPanel page) => page.Children.OfType<Border>().Select(card => card.Child).OfType<StackPanel>();
        var shortcutsPage = pages.Single(page => Cards(page).Any(card => card.Children.OfType<TextBlock>().Any(title => title.Text == L.Get("Settings.InternalShortcuts"))));
        var internalCard = Cards(shortcutsPage).Single(card => card.Children.OfType<TextBlock>().Any(title => title.Text == L.Get("Settings.InternalShortcuts")));
        var globalCard = Cards(shortcutsPage).Single(card => card.Children.OfType<TextBlock>().Any(title => title.Text == L.Get("Settings.GlobalShortcuts")));
        var configureShortcuts = internalCard.Children.OfType<Button>().Single(button => Equals(button.Content, L.Get("Shortcuts.ConfigureCommands")));
        var associateFiles = pages.SelectMany(Cards).SelectMany(card => card.Children.OfType<Button>())
            .Single(button => Equals(button.Content, L.Get("Shell.AssociateProjectFiles")));
        settingsWindow.SelectSection(shortcutsPage);
        check("internal and global shortcuts have separate titled cards with four global recorders",
            Cards(shortcutsPage).Count() == 2 &&
            globalCard.Children.OfType<Grid>().Count(row => row.Children.OfType<TextBox>().Any()) == 4);
        check("shortcut and project file actions use the standard outlined button style",
            new[] { configureShortcuts, associateFiles }.All(button => button.BorderThickness == new Thickness(1) &&
                Equals(button.Background, settingsWindow.FindResource("CanvasInput"))));
        string? preview = Environment.GetEnvironmentVariable("FLOATCANVAS_SHORTCUT_SETTINGS_PREVIEW");
        if (preview is not null)
        {
            settingsWindow.UpdateLayout();
            Preview4SettingsRuntimeFixture.Render(settingsWindow, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(preview)!, "shortcut-entry.png"));
        }
        configureShortcuts.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var window=Field<Window>("_commandWindow");
        check("the internal-shortcuts button in Settings opens the table", window.IsVisible && ReferenceEquals(window.Owner, settingsWindow));
        var panel=(DockPanel)window.Content;
        var footer=panel.Children.OfType<StackPanel>().Single(child => DockPanel.GetDock(child) == Dock.Bottom);
        var actions=footer.Children.OfType<WrapPanel>().Single();
        var list=panel.Children.OfType<ListBox>().Single();
        check("command picker list never forwards canvas shortcuts",!EditorShortcutPolicy.ShouldRoute(0x56,ModifierKeys.None,list));
        var searchRow = panel.Children.OfType<DockPanel>().Single();
        var search = searchRow.Children.OfType<TextBox>().Single();
        var findByKey = searchRow.Children.OfType<Button>().Single(button => Equals(button.Content, L.Get("Shortcuts.FindKey")));
        var filters = panel.Children.OfType<StackPanel>().Single(child => child.Orientation == Orientation.Horizontal);
        var areaFilter = (ComboBox)filters.Children[0];
        var sourceFilter = (ComboBox)filters.Children[1];
        IEnumerable<ListBoxItem> Rows() => list.Items.OfType<ListBoxItem>();
        string Cell(ListBoxItem row, int index) => ((TextBlock)((Grid)row.Content).Children[index]).Text;
        check("shortcut settings render area, command, binding, condition and source", Rows().All(row => ((Grid)row.Content).Children.Count == 5));
        areaFilter.SelectedIndex = (int)ShortcutSettingsTable.Area.Stamps + 1;
        sourceFilter.SelectedIndex = (int)ShortcutSettingsTable.Source.Fixed + 1;
        check("area and source filters intersect and keep fixed stamp-library keys visible", Rows().Count() == 5 &&
            Rows().All(row => Cell(row, 0) == ShortcutSettingsTable.AreaLabel(ShortcutSettingsTable.Area.Stamps) &&
                Cell(row, 4) == ShortcutSettingsTable.SourceLabel(ShortcutSettingsTable.Source.Fixed)));
        check("fixed shortcuts cannot be recorded", actions.Children.OfType<Button>().Single(button => Equals(button.Content, L.Get("Shortcuts.RecordPrimary"))).IsEnabled == false);
        areaFilter.SelectedIndex = 0; sourceFilter.SelectedIndex = 0;
        search.Text = L.Get("Shortcuts.When.CanvasOpacity");
        check("search text matches conditions", Rows().Count() == 2 && Rows().All(row => row.Tag is EditorCommand { Id: "Canvas.OpacityUp" or "Canvas.OpacityDown" }));
        findByKey.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using (new ThreadModifierScope(ModifierKeys.None))
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.F2) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        check("recording a search key finds both F2 contexts without changing bindings or leaving hotkeys suspended",
            search.Text == "F2" && Rows().Count() == 2 && Rows().Any(row => row.Tag is EditorCommand { Id: "Selection.Rename" }) &&
            Rows().Any(row => Cell(row, 1) == L.Get("Stamp.Rename")) && !host.ApplicationHotkeysSuspended && EditorCommandCatalog.Hint("Selection.Rename") == "F2");
        search.Clear();
        list.SelectedItem=list.Items.Cast<ListBoxItem>().Single(item=>item.Tag is EditorCommand { Id: "Edit.Copy" });
        window.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        preview ??= "B:/FloatCanvas-shortcuts-20261116/shortcut-settings.png";
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(preview)!);
        using(var output=System.IO.File.Create(preview)) encoder.Save(output);
        void Click(string key)=>actions.Children.OfType<Button>().Single(b=>Equals(b.Content,L.Get(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click("Shortcuts.RecordPrimary");
        using (new ThreadModifierScope(ModifierKeys.None))
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Q) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Exception? previewFailure = null;
        app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            var keyboardPreview = app.Windows.OfType<ShortcutKeyboardPreviewWindow>().Single();
            try
            {
                var frame = (DockPanel)keyboardPreview.Content;
                var keyboard = (Canvas)((Viewbox)frame.Children.OfType<Border>().Single().Child).Child;
                Button KeyCap(int virtualKey) => keyboard.Children.OfType<Button>().First(button => Equals(button.Tag, virtualKey));
                var modifiers = frame.Children.OfType<StackPanel>().Single().Children.OfType<CheckBox>().ToArray();
                CheckBox Modifier(string name) => modifiers.Single(toggle => Equals(toggle.Content, name));
                check("keyboard preview uses the full-sized keyboard and shows the unsaved Q binding",
                    keyboardPreview.IsVisible && ReferenceEquals(keyboardPreview.Owner, window) && keyboard.Children.Count == 104 &&
                    KeyCap(0x51).ToolTip?.ToString()?.Contains(L.Get("Shortcuts.Command.Edit.Copy")) == true && KeyCap(0x08).ToolTip is not null);
                KeyCap(0x71).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var details = (StackPanel)frame.Children.OfType<ScrollViewer>().Single().Content;
                check("selecting a key shows both scoped actions on F2", details.Children.OfType<TextBlock>().Count() == 3);
                Modifier("Ctrl").IsChecked = true;
                check("Ctrl preview highlights the global edit toggle separately", KeyCap(0xC0).ToolTip?.ToString()?.Contains(L.Get("Settings.GlobalShortcuts")) == true);
                Modifier("Shift").IsChecked = true;
                check("Ctrl Shift preview highlights clear on Backspace instead of removing a screenshot vertex",
                    KeyCap(0x08).ToolTip?.ToString()?.Contains(L.Get("Shortcuts.Command.Edit.Clear")) == true);
                if (Environment.GetEnvironmentVariable("FLOATCANVAS_SHORTCUT_SETTINGS_PREVIEW") is { } outputPath)
                {
                    KeyCap(0x08).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Preview4SettingsRuntimeFixture.Render(keyboardPreview, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(outputPath)!, "keyboard-shortcuts-ctrl-shift.png"));
                    Modifier("Ctrl").IsChecked = Modifier("Shift").IsChecked = false;
                    KeyCap(0x71).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Preview4SettingsRuntimeFixture.Render(keyboardPreview, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(outputPath)!, "keyboard-shortcuts.png"));
                }
            }
            catch (Exception ex) { previewFailure = ex; }
            finally { keyboardPreview.Close(); }
        });
        Click("Shortcuts.KeyboardPreview");
        if (previewFailure is not null) throw previewFailure;
        check("closing the keyboard preview leaves saved shortcuts unchanged", EditorCommandCatalog.Hint("Edit.Copy") == "Ctrl+C");
        Click("Shortcuts.Reset");
        Click("Shortcuts.RecordPrimary"); Click("Shortcuts.RecordAlternate");
        check("reentering shortcut recording retains one suspended scope",host.ApplicationHotkeysSuspended);
        var source=PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Escape) {RoutedEvent=Keyboard.PreviewKeyDownEvent});
        check("cancelling repeated recording restores global bindings",!host.ApplicationHotkeysSuspended);
        Click("Shortcuts.RecordPrimary");window.Hide();
        check("hiding recorder releases hotkeys and input ownership",!host.ApplicationHotkeysSuspended && !EditorShortcutPolicy.RecordingWindows.Contains(window));
        window.Close();
        settingsWindow.Close();
        typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
        host.RegisterCommandHotkeys(new Dictionary<string,HotkeyBinding>());

        editor.Tool = EditorTool.Select;
        var state = editor.Session;
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20 };
        state.Add(element);
        state.SetSelection([element.Id]);
        int undoBefore = state.UndoCount;
        double X() => state.SelectionBounds()!.Value.Left;
        double start = X();
        void CommandKey(int code, OverlayModifiers modifiers = OverlayModifiers.None, bool repeat = false) =>
            Call("OnCommandInput", new OverlayKeyEvent(code, modifiers, repeat));
        var router = Field<SessionKeyboardRouter>("_keyboardRouter");
        CommandKey(0x27);
        var timer = Field<System.Windows.Threading.DispatcherTimer>("_keyboardNudgeTimer");
        var nudge = Field<object>("_keyboardNudge");
        check("nudge moves immediately and waits before the first repeat", X() == start + 1 && timer.Interval.TotalMilliseconds >= 300);
        CommandKey(0x27, repeat: true);
        check("native repeat cannot add a movement between nudge timer ticks", X() == start + 1);
        var intervals = new List<double> { timer.Interval.TotalMilliseconds };
        for (int index = 0; index < 8; index++)
        {
            Call("StepKeyboardNudge", nudge);
            intervals.Add(timer.Interval.TotalMilliseconds);
        }
        check("nudge cadence accelerates to a steady rate without changing the step",
            intervals.Take(7).Zip(intervals.Skip(1).Take(6)).All(pair => pair.First > pair.Second) &&
            intervals.Skip(6).Distinct().Count() == 1 && X() == start + 9);
        router.Release(0x27);
        check("key release commits a held movement as one undo step", !timer.IsEnabled && !state.IsTransactionActive && state.UndoCount == undoBefore + 1);
        CommandKey(0x5A, OverlayModifiers.Control);
        CommandKey(0x5A, OverlayModifiers.Control, repeat: true);
        check("held undo rolls back only the nudge, preserving earlier content", state.UndoCount == undoBefore && X() == start);
        CommandKey(0x59, OverlayModifiers.Control);
        CommandKey(0x59, OverlayModifiers.Control, repeat: true);
        check("redo restores the whole held movement", X() == start + 9);
        CommandKey(0x27, OverlayModifiers.Shift);
        check("Shift nudges by ten document pixels", X() == start + 19);
        CommandKey(0x1B);
        router.Release(0x27);
        check("Escape restores the pre-nudge position without a history entry", X() == start + 9 && state.UndoCount == undoBefore + 1 && !timer.IsEnabled);

        const OverlayModifiers clearModifiers = OverlayModifiers.Control | OverlayModifiers.Shift;
        void WaitForClear()
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (CallBusy() && DateTime.UtcNow < until)
            {
                app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(5);
            }
        }
        bool CallBusy() => (bool)typeof(DesktopSession).GetProperty("DocumentOperationBusy", Hidden)!.GetValue(desktop)!;
        CommandKey(0x08, clearModifiers);
        WaitForClear();
        check("pressing the new clear chord clears editable content and keeps the canvas session",
            !CallBusy() && ReferenceEquals(editor.Session, state) && state.Document.Elements.Count == 0);
        check("the clear shortcut can be undone once", state.Undo() && state.Document.Elements.Count == 1);
        CommandKey(0x08, clearModifiers, repeat: true);
        WaitForClear();
        check("held clear ignores repeats even after drawable content is restored", !CallBusy() && state.Document.Elements.Count == 1);
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window,int id);
}
