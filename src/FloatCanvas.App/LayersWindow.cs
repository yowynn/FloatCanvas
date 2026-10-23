using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Automation = System.Windows.Automation;

namespace FloatCanvas.App;

/// <summary>Independent layer ordering, visibility and grouping with atomic history operations.</summary>
internal sealed class LayersWindow : Wpf.Window
{
    private readonly Func<EditorSession> getSession;
    private readonly Func<bool> canMutate;
    private readonly Controls.ListBox layers = new() { SelectionMode = Controls.SelectionMode.Extended, BorderThickness = new(0) };
    private readonly Controls.TextBlock status = new() { TextWrapping = Wpf.TextWrapping.Wrap, FontSize = 12 };
    private readonly Controls.TextBlock empty = new() { Text = L.Get("Layers.Empty"), TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(12) };
    private readonly Controls.CheckBox depthPriorityOcclusion = new()
    {
        Content = L.Get("Layers.DepthPriorityOcclusion"), ToolTip = L.Get("Layers.DepthPriorityOcclusionHint"),
        Margin = new(2, 4, 0, 10), Visibility = Wpf.Visibility.Collapsed
    };
    private readonly Dictionary<Guid, LayerRow> rows = [];
    private EditorSession? displayedSession, dragSession;
    private Guid[]? dragIds;
    private Guid? dropTarget;
    private bool dropBefore, refreshing;
    private long dragRevision;
    private Guid? dragSource;
    private Wpf.Point dragStart, dragStartScreen;
    private HashSet<Wpf.FrameworkElement>? dragRows;
    private TileDragFeedback? dragFeedback;
    private EditorSession? renameSession;
    private Guid? renameId;
    private string? renameOriginal;
    private bool renameComposing;

    internal bool IsRenaming => renameId is not null;

    public event Action? Dismissed;
    public event Action<int>? CommandKey;

    public LayersWindow(Func<EditorSession> getSession, Func<bool>? canMutate = null)
    {
        this.getSession = getSession; this.canMutate = canMutate ?? (() => true);
        Title = L.Get("Layers.Title"); Width = 320; Height = 520; MinWidth = 260; MinHeight = 260;
        Topmost = true; ShowInTaskbar = true; Icon = AppIcon.WindowImage;
        var body = new Controls.Grid { Margin = new(12, 4, 12, 12) };
        body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, Wpf.GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        var footer = new Controls.DockPanel { Margin = new(0, 6, 0, 0) };
        var listSite = new Controls.Grid(); listSite.Children.Add(layers); listSite.Children.Add(empty);
        Automation.AutomationProperties.SetName(depthPriorityOcclusion, L.Get("Layers.DepthPriorityOcclusion"));
        body.Children.Add(depthPriorityOcclusion);
        Controls.Grid.SetRow(listSite, 1);
        body.Children.Add(listSite);
        Automation.AutomationProperties.SetName(layers, L.Get("Inspector.LayerList"));
        status.Margin = new(0, 3, 20, 0); status.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted");
        footer.Children.Add(status);
        Controls.Grid.SetRow(footer, 2); body.Children.Add(footer); Content = body;
        AppWindowChrome.Apply(this);
        AppWindowChrome.SetHelp(this, L.Get("Layers.Help"), L.Get("Layers.HelpWithRename"));
        layers.SelectionChanged += (_, _) => SelectRows();
        depthPriorityOcclusion.Click += (_, _) => SetDepthPriorityOcclusion(depthPriorityOcclusion.IsChecked == true);
        Closing += (_, e) => { e.Cancel = true; CommitRename(); CancelDrag(); SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); };
        Deactivated += (_, _) => { if (SessionWindowCoordinator.Find(this)?.IsInputMethodSwitchPending != true) { CommitRename(); CancelDrag(); } };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CommitRename(); CancelDrag(); } };
        AppInputPolicy.AttachToolbarToggle(this, () => CommandKey?.Invoke(0x09), () => !IsRenaming && this.canMutate());
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Input.Key.System ? e.SystemKey : e.Key;
            var modifiers = Input.Keyboard.Modifiers;
            var source = e.OriginalSource as Wpf.DependencyObject;
            if (e.Handled || (modifiers & (Input.ModifierKeys.Alt | Input.ModifierKeys.Windows)) != 0 || EditorShortcutPolicy.OwnsMenu(source)) return;
            // TextBox owns editing keys and the IME until this rename has finished.
            if (IsRenaming)
            {
                int projectKey = Input.KeyInterop.VirtualKeyFromKey(key);
                if (!renameComposing && EditorShortcutPolicy.IsProjectCommand(projectKey, (FloatCanvas.Windows.OverlayModifiers)modifiers))
                {
                    e.Handled = true;
                    if (!e.IsRepeat && this.canMutate()) { CommitRename(); CommandKey?.Invoke(projectKey); }
                }
                return;
            }
            if (key == Input.Key.F2 && modifiers == Input.ModifierKeys.None && layers.SelectedItems.Count == 1)
            { e.Handled = true; if (!e.IsRepeat) BeginRename((Guid)((Controls.ListBoxItem)layers.SelectedItem).Tag); return; }
            if (key == Input.Key.Escape)
            {
                e.Handled = true;
                if (!e.IsRepeat && !AppWindowChrome.TryCloseHelp(this)) { if (dragIds is not null) CancelDrag(); else { SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); } }
                return;
            }
            bool ctrl = (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0;
            bool localInput = EditorShortcutPolicy.OwnsLocalEditingKeys(source) || EditorShortcutPolicy.OwnsLocalEditingKeys(Input.Keyboard.FocusedElement as Wpf.DependencyObject);
            if (ctrl && key == Input.Key.G && !localInput)
            {
                e.Handled = true;
                if (!e.IsRepeat) RunLayerCommand(s => { if ((modifiers & Input.ModifierKeys.Shift) != 0) s.UngroupSelection(); else s.GroupSelection(); });
                return;
            }
            int command = Input.KeyInterop.VirtualKeyFromKey(key);
            if (!EditorShortcutPolicy.ShouldRoute(command, modifiers, source, preserveListNavigation: true, localInput: localInput)) return;
            e.Handled = true;
            if (!e.IsRepeat || EditorShortcutPolicy.AllowsRepeat(command, (FloatCanvas.Windows.OverlayModifiers)modifiers)) RouteCommandKey(command, ctrl);
        };
        Refresh();
    }

    public void Refresh()
    {
        if (!canMutate() || dragIds is not null) return;
        var session = getSession(); if (session.IsTransactionActive) return;
        RefreshRows(session);
    }

    private void RefreshRows(EditorSession session)
    {
        refreshing = true;
        try
        {
            depthPriorityOcclusion.Visibility = session.Document.Canvas.Projection.Enabled ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            depthPriorityOcclusion.IsChecked = session.Document.Canvas.DepthPriorityOcclusion;
            var ordered = session.Document.Elements.AsEnumerable().Reverse().ToArray();
            if (renameId is { } editing && (!ReferenceEquals(renameSession, session) || !ordered.Any(e => e.Id == editing))) CancelRename();
            if (!ReferenceEquals(displayedSession, session) || !ordered.Select(e => e.Id).SequenceEqual(layers.Items.Cast<Controls.ListBoxItem>().Select(i => (Guid)i.Tag)))
            {
                CancelRename();
                displayedSession = session; rows.Clear(); layers.Items.Clear();
                foreach (var element in ordered) AddRow(element);
            }
            foreach (var element in ordered)
            {
                var row = rows[element.Id]; string name = InspectorWindow.ElementName(element);
                row.Label.Text = element.GroupId is not null ? "▦  " + name : name;
                row.Label.ToolTip = name + "\n" + L.Get("Layers.RenameHint");
                row.Item.IsSelected = session.Selection.Contains(element.Id);
                if (row.PaintedVisible != element.Visible) { row.Visible.Content = IconPath(element.Visible ? Eye : EyeOff); row.PaintedVisible = element.Visible; }
                if (row.PaintedLocked != element.Locked) { row.Locked.Content = IconPath(element.Locked ? Lock : Unlock); row.PaintedLocked = element.Locked; }
                row.Visible.IsChecked = element.Visible; row.Locked.IsChecked = element.Locked;
                row.Visible.Opacity = element.Visible ? 1 : .55; row.Locked.Opacity = element.Locked ? 1 : .55;
                Automation.AutomationProperties.SetName(row.Item, name);
                string visibility = L.Get(element.Visible ? "Inspector.HideLayer" : "Inspector.ShowLayer") + " " + name;
                string locking = L.Get(element.Locked ? "Inspector.UnlockLayer" : "Inspector.LockLayer") + " " + name;
                Automation.AutomationProperties.SetName(row.Visible, visibility); row.Visible.ToolTip = visibility;
                Automation.AutomationProperties.SetName(row.Locked, locking); row.Locked.ToolTip = locking;
            }
            empty.Visibility = ordered.Length == 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        }
        finally { refreshing = false; }
    }

    private void AddRow(CanvasElement element)
    {
        Guid id = element.Id;
        var row = new Controls.Grid { MinHeight = 36 };
        row.ColumnDefinitions.Add(new() { Width = new(26) });
        row.ColumnDefinitions.Add(new() { Width = new(1, Wpf.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(30) }); row.ColumnDefinitions.Add(new() { Width = new(30) });
        var handle = new Primitives.Thumb { Width = 24, Cursor = Input.Cursors.SizeNS, ToolTip = L.Get("Layers.Drag"), Template = DragTemplate };
        Automation.AutomationProperties.SetName(handle, L.Get("Layers.Drag"));
        var label = new Controls.TextBlock { VerticalAlignment = Wpf.VerticalAlignment.Center, TextTrimming = Wpf.TextTrimming.CharacterEllipsis, Margin = new(3, 0, 4, 0), Background = Media.Brushes.Transparent };
        var editor = new Controls.TextBox { Visibility = Wpf.Visibility.Collapsed, VerticalContentAlignment = Wpf.VerticalAlignment.Center,
            Margin = new(0, 2, 3, 2), Padding = new(3, 0, 3, 0), MaxLength = EditorSession.MaximumLayerNameLength };
        Automation.AutomationProperties.SetName(editor, L.Get("Layers.RenameInput"));
        var visible = StateButton(); var locked = StateButton();
        Controls.Grid.SetColumn(label, 1); Controls.Grid.SetColumn(editor, 1); Controls.Grid.SetColumn(visible, 2); Controls.Grid.SetColumn(locked, 3);
        row.Children.Add(handle); row.Children.Add(label); row.Children.Add(editor); row.Children.Add(visible); row.Children.Add(locked);
        var item = new Controls.ListBoxItem { Tag = id, Content = row, HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch, Padding = new(2) };
        label.PreviewMouseLeftButtonDown += (_, e) => { if (item.IsSelected && BeginRename(id)) e.Handled = true; };
        editor.PreviewKeyDown += (_, e) => { if (HandleRenameKey(e.Key)) e.Handled = true; };
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (renameId == id && !editor.IsKeyboardFocusWithin && SessionWindowCoordinator.Find(this)?.IsInputMethodSwitchPending != true) CommitRename();
        };
        Input.TextCompositionManager.AddPreviewTextInputStartHandler(editor, (_, _) => renameComposing = true);
        Input.TextCompositionManager.AddPreviewTextInputUpdateHandler(editor, (_, _) => renameComposing = true);
        Input.TextCompositionManager.AddPreviewTextInputHandler(editor, (_, _) => renameComposing = false);
        item.ContextMenu = CreateContextMenu(id);
        item.PreviewMouseRightButtonDown += (_, _) => { if (!item.IsSelected && canMutate() && !getSession().IsTransactionActive) getSession().SetSelection([id]); Refresh(); };
        visible.Click += (_, _) => SetLayerFlag(id, true, visible.IsChecked == true);
        locked.Click += (_, _) => SetLayerFlag(id, false, locked.IsChecked == true);
        handle.DragStarted += (_, e) =>
        {
            if (!BeginDrag(id)) handle.CancelDrag();
            else
            {
                dragStart=handle.TranslatePoint(new Wpf.Point(e.HorizontalOffset,e.VerticalOffset),layers);
                dragStartScreen=layers.PointToScreen(dragStart);
            }
        };
        handle.DragDelta += (_, _) => UpdateDropTarget(Input.Mouse.GetPosition(layers));
        handle.DragCompleted += (_, e) =>
        {
            // Thumb completion reports screen pixels; convert the screen endpoint back to list DIPs.
            if(!e.Canceled)UpdateDropTarget(layers.PointFromScreen(dragStartScreen+new Wpf.Vector(e.HorizontalChange,e.VerticalChange)));
            FinishDrag(!e.Canceled);
        };
        rows.Add(id, new(item, label, editor, visible, locked, handle)); layers.Items.Add(item);
    }

    private OverlayContextMenu CreateContextMenu(Guid id)
    {
        var menu = new OverlayContextMenu();
        var rename = new Controls.MenuItem { Header = L.Get("Layers.Rename"), InputGestureText = "F2", Icon = IconPath("M 2,11 L 2,14 L 5,14 L 13,6 L 10,3 Z M 9,4 L 12,7") };
        rename.Click += (_, _) => BeginRename(id); menu.Items.Add(rename);
        menu.Items.Add(new Controls.Separator());
        void Add(string key, string icon, string shortcut, Action<EditorSession> action)
        {
            var command = new Controls.MenuItem { Header = L.Get(key), Icon = IconPath(icon), InputGestureText = shortcut };
            command.Click += (_, _) => RunLayerCommand(action); menu.Items.Add(command);
        }
        Add("Inspector.Duplicate", "M 1,1 L 10,1 L 10,10 M 5,5 L 14,5 L 14,14 L 5,14 Z", "Ctrl+D", s => s.DuplicateSelection());
        Add("Inspector.Delete", "M 3,4 L 13,4 M 5,4 L 5,14 L 11,14 L 11,4 M 6,1 L 10,1", "Delete", s => s.DeleteSelection());
        menu.Items.Add(new Controls.Separator());
        Add("Inspector.BringToFront", "M 3,9 L 8,4 L 13,9 M 8,4 L 8,14 M 2,1 L 14,1", "", s => s.ReorderSelection(LayerOrder.Front));
        Add("Inspector.SendToBack", "M 3,7 L 8,12 L 13,7 M 8,2 L 8,12 M 2,15 L 14,15", "", s => s.ReorderSelection(LayerOrder.Back));
        menu.Items.Add(new Controls.Separator());
        Add("Layers.Group", "M 1,1 L 8,1 L 8,8 L 1,8 Z M 6,6 L 14,6 L 14,14 L 6,14 Z", "Ctrl+G", s => s.GroupSelection());
        Add("Layers.Ungroup", "M 1,1 L 6,1 L 6,6 L 1,6 Z M 9,9 L 14,9 L 14,14 L 9,14 Z", "Ctrl+Shift+G", s => s.UngroupSelection());
        menu.Opened += (_, _) =>
        {
            if (!getSession().Selection.Contains(id) && canMutate() && !getSession().IsTransactionActive) getSession().SetSelection([id]);
            bool allowed = canMutate() && !getSession().IsTransactionActive && getSession().SelectedElements().Any(e => !e.Locked);
            foreach (var command in menu.Items.OfType<Controls.MenuItem>()) command.IsEnabled = allowed;
            rename.IsEnabled = canMutate() && !getSession().IsTransactionActive;
        };
        return menu;
    }

    private void SelectRows()
    {
        if (refreshing) return;
        var session = getSession();
        if (!canMutate() || session.IsTransactionActive || !ReferenceEquals(session, displayedSession))
        { status.Text = L.Get("Inspector.BusySelectionRetained"); RefreshRows(session); return; }
        session.SetSelection(layers.SelectedItems.Cast<Controls.ListBoxItem>().Select(i => (Guid)i.Tag));
        Refresh();
    }

    private void SetLayerFlag(Guid id, bool visibility, bool value)
    {
        if (refreshing) return;
        try
        {
            var session = getSession();
            if (!canMutate() || !ReferenceEquals(session, displayedSession) || session.IsTransactionActive) throw new InvalidOperationException(L.Get("Inspector.BusyLayersRetained"));
            session.Execute(L.Get(visibility ? value ? "Inspector.ShowLayer" : "Inspector.HideLayer" : value ? "Inspector.LockLayer" : "Inspector.UnlockLayer"), document =>
            {
                var element = document.Elements.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
                if (visibility) element.Visible = value; else element.Locked = value;
            });
        }
        catch (Exception ex) { status.Text = ex.Message; }
        RefreshRows(getSession());
    }

    private void SetDepthPriorityOcclusion(bool value)
    {
        if (refreshing) return;
        try
        {
            var session = getSession();
            if (!canMutate() || !ReferenceEquals(session, displayedSession) || session.IsTransactionActive)
                throw new InvalidOperationException(L.Get("Inspector.BusyLayersRetained"));
            if (session.Document.Canvas.Projection.Enabled && session.Document.Canvas.DepthPriorityOcclusion != value)
                session.Execute(L.Get("Layers.DepthPriorityOcclusion"), document => document.Canvas.DepthPriorityOcclusion = value);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        RefreshRows(getSession());
    }

    private void RunLayerCommand(Action<EditorSession> operation)
    {
        try
        {
            var session = getSession();
            if (!canMutate() || session.IsTransactionActive || !ReferenceEquals(session, displayedSession)) throw new InvalidOperationException(L.Get("Inspector.BusyLayersRetained"));
            if (!session.SelectedElements().Any(e => !e.Locked)) return;
            operation(session); Refresh();
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private bool BeginDrag(Guid id)
    {
        CommitRename();
        var session = getSession();
        if (!canMutate() || session.IsTransactionActive || !ReferenceEquals(session, displayedSession)) return false;
        var source = session.Document.Elements.FirstOrDefault(e => e.Id == id);
        if (source is null || source.Locked) return false;
        if (!session.Selection.Contains(id)) session.SetSelection(session.SelectionUnitIds(id));
        dragSession = session; dragIds = session.SelectedElements().Where(e => !e.Locked).Select(e => e.Id).ToArray(); dragRevision = session.SceneRevision;
        dragSource=id;
        dragRows=dragIds.Select(value=>(Wpf.FrameworkElement)rows[value].Item).ToHashSet();
        return dragIds.Length > 0;
    }

    private void UpdateDropTarget(Wpf.Point point)
    {
        if (dragIds is null) return;
        if(!canMutate()||!ReferenceEquals(dragSession,getSession())||dragSession.IsTransactionActive||dragSession.SceneRevision!=dragRevision)
        {CancelDrag();Refresh();return;}
        ClearDropIndicator();
        if(dragFeedback is null&&Math.Abs(point.X-dragStart.X)<=Wpf.SystemParameters.MinimumHorizontalDragDistance&&
            Math.Abs(point.Y-dragStart.Y)<=Wpf.SystemParameters.MinimumVerticalDragDistance)return;
        try
        {
            if(dragFeedback is null&&dragSource is { } source)
            {
                var created=TileDragFeedback.Create(rows[source].Item,layers,dragStart,dragRows);
                if(dragIds is null||dragSource!=source||!rows[source].Item.IsVisible){created?.Dispose();return;}
                dragFeedback=created;
            }
            dragFeedback?.Move(point);
            var drop=TileDragReorder.ResolveDropCore(layers,layers.Items.Cast<Wpf.FrameworkElement>(),point,vertical:true,moving:dragRows);
            if(drop is null)return;
            dropTarget=(Guid)drop.Value.Tile.Tag;dropBefore=!drop.Value.After;
            dragFeedback?.ShowInsertion(drop.Value.Marker);
        }
        catch{CancelDrag();throw;}
    }

    private void FinishDrag(bool commit)
    {
        var session = dragSession; var ids = dragIds; var target = dropTarget; bool before = dropBefore;
        CancelDrag();
        if (!commit || session is null || ids is null || target is null || !canMutate() || !ReferenceEquals(session, getSession()) ||
            session.IsTransactionActive || session.SceneRevision != dragRevision) {Refresh();return;}
        ReorderLayers(ids, target.Value, before); Refresh();
    }

    private void ReorderLayers(Guid[] ids, Guid targetId, bool before)
    {
        var session = getSession();
        if (!canMutate() || session.IsTransactionActive) return;
        var selected = session.Document.Elements.Where(e => ids.Contains(e.Id)).ToArray();
        if (selected.Length != ids.Length || selected.Any(e => e.Locked) || ids.Contains(targetId)) return;
        var remaining = session.Document.Elements.Where(e => !ids.Contains(e.Id)).ToList();
        int index = remaining.FindIndex(e => e.Id == targetId); if (index < 0) return;
        remaining.InsertRange(index + (before ? 1 : 0), selected);
        if (remaining.Select(e => e.Id).SequenceEqual(session.Document.Elements.Select(e => e.Id))) return;
        session.Execute(L.Get("History.ReorderElements"), document => document.Elements = remaining);
    }

    private void ClearDropIndicator()
    {
        dragFeedback?.ClearTarget();
        dropTarget = null;
    }
    private void CancelDrag()
    {
        ClearDropIndicator(); dragIds = null; dragSession = null;dragSource=null;dragRows=null;
        dragFeedback?.Dispose();dragFeedback=null;
        foreach (var row in rows.Values) if (row.Handle.IsDragging) row.Handle.CancelDrag();
    }
    private bool RouteCommandKey(int key, bool control)
    {
        if (!CanRouteCommandKey(key, control)) return false;
        if (canMutate()) CommandKey?.Invoke(key); return true;
    }
    private bool CanRouteCommandKey(int key, bool control) => !IsRenaming &&
        EditorShortcutPolicy.ShouldRoute(key, control ? Input.ModifierKeys.Control : Input.ModifierKeys.None, null, preserveListNavigation: true);

    private bool BeginRename(Guid id)
    {
        if (renameId == id) return true;
        CommitRename();
        var session = getSession();
        if (!canMutate() || session.IsTransactionActive || !ReferenceEquals(session, displayedSession) || !rows.TryGetValue(id, out var row)) return false;
        var element = session.Document.Elements.FirstOrDefault(e => e.Id == id);
        if (element is null) return false;
        renameSession = session; renameId = id; renameOriginal = element.Name; renameComposing = false;
        row.Editor.Text = element.Name ?? InspectorWindow.ElementName(element);
        row.Label.Visibility = Wpf.Visibility.Collapsed; row.Editor.Visibility = Wpf.Visibility.Visible;
        row.Editor.SelectAll();
        if (IsVisible) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        { if (renameId == id) { row.Editor.Focus(); row.Editor.SelectAll(); } }));
        return true;
    }

    private bool HandleRenameKey(Input.Key key)
    {
        // Composition-confirming Enter/Esc belongs to the IME, not to the layer command.
        if (!IsRenaming || renameComposing) return false;
        if (key == Input.Key.Enter) { CommitRename(); return true; }
        if (key == Input.Key.Escape) { CancelRename(); return true; }
        return false;
    }

    private void CommitRename()
    {
        if (renameId is not { } id || renameSession is not { } session || !rows.TryGetValue(id, out var row)) return;
        string name = row.Editor.Text; string? original = renameOriginal;
        CancelRename();
        try
        {
            if (!canMutate() || session.IsTransactionActive || !ReferenceEquals(session, getSession())) return;
            var element = session.Document.Elements.FirstOrDefault(e => e.Id == id);
            // Do not overwrite a replacement document or an external edit made while typing.
            if (element is null || element.Name != original) return;
            if (original is null && name == InspectorWindow.ElementName(element)) return;
            session.RenameElement(id, name);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        Refresh();
    }

    private void CancelRename()
    {
        var id = renameId; renameId = null; renameSession = null; renameOriginal = null; renameComposing = false;
        if (id is { } value && rows.TryGetValue(value, out var row))
        { row.Editor.Visibility = Wpf.Visibility.Collapsed; row.Label.Visibility = Wpf.Visibility.Visible; }
    }

    private static Primitives.ToggleButton StateButton()
    {
        var button = new Primitives.ToggleButton { Width = 28, Height = 30, Padding = new(5), Template = ToggleTemplate };
        button.SetResourceReference(Primitives.ToggleButton.ForegroundProperty, "CanvasText"); return button;
    }
    private static System.Windows.Shapes.Path IconPath(string data)
    {
        var path = new System.Windows.Shapes.Path { Data = Media.Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = 1.4,
            StrokeStartLineCap = Media.PenLineCap.Round, StrokeEndLineCap = Media.PenLineCap.Round, Stretch = Media.Stretch.Uniform };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "CanvasText"); return path;
    }
    private static readonly Controls.ControlTemplate ToggleTemplate = (Controls.ControlTemplate)Wpf.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
          <Border x:Name="Chrome" Background="Transparent" CornerRadius="5" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasButtonHover}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/><Setter TargetName="Chrome" Property="BorderThickness" Value="1"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    private static readonly Controls.ControlTemplate DragTemplate = (Controls.ControlTemplate)Wpf.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
          <Border Background="Transparent"><Path Data="M 8,10 L 8,11 M 13,10 L 13,11 M 8,16 L 8,17 M 13,16 L 13,17 M 8,22 L 8,23 M 13,22 L 13,23" Stroke="{DynamicResource CanvasMuted}" StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/></Border>
        </ControlTemplate>
        """);
    private const string Eye = "M 1,8 C 5,2 11,2 15,8 C 11,14 5,14 1,8 Z M 8,5 A 3,3 0 1 1 8,11 A 3,3 0 1 1 8,5";
    private const string EyeOff = "M 1,8 C 5,2 11,2 15,8 C 11,14 5,14 1,8 M 2,2 L 14,14";
    private const string Lock = "M 4,7 L 4,4 A 4,4 0 0 1 12,4 L 12,7 M 3,7 L 13,7 L 13,15 L 3,15 Z M 8,10 L 8,12";
    private const string Unlock = "M 6,7 L 6,4 A 4,4 0 0 1 14,4 M 3,7 L 13,7 L 13,15 L 3,15 Z M 8,10 L 8,12";
    private sealed record LayerRow(Controls.ListBoxItem Item, Controls.TextBlock Label, Controls.TextBox Editor, Primitives.ToggleButton Visible, Primitives.ToggleButton Locked, Primitives.Thumb Handle)
    {
        public bool? PaintedVisible { get; set; }
        public bool? PaintedLocked { get; set; }
    }
}
