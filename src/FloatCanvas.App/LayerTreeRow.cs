using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Visible ownership row, without a WPF container until it reaches the viewport.</summary>
internal sealed class LayerTreeRow(LayersWindow owner, Guid id) : INotifyPropertyChanged
{
    internal LayersWindow Owner { get; } = owner;
    public Guid Id { get; } = id;
    public string? SourceName { get; private set; }
    public string Name { get; private set; } = "";
    public Guid? ParentId { get; private set; }
    public int Depth { get; private set; }
    public bool IsGroup { get; private set; }
    public bool IsInstance { get; private set; }
    public bool IsLinked { get; private set; }
    public bool IsLegacy { get; private set; }
    public ElementKind? Kind { get; private set; }
    public bool IsWindowSource { get; private set; }
    public bool Visible { get; private set; }
    public bool Locked { get; private set; }
    public bool EffectiveVisible { get; private set; }
    public bool EffectiveLocked { get; private set; }
    public string? HiddenAncestor { get; private set; }
    public bool AncestorLocked { get; private set; }
    private object? presentationStamp;
    private bool renaming;
    public bool Renaming { get => renaming; set { if (renaming == value) return; renaming = value; Changed(); } }
    public string DraftName { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(string? sourceName, string name, Guid? parent, int depth, bool group, bool legacy,
        bool visible, bool locked, bool effectiveVisible, bool effectiveLocked, bool ancestorLocked, bool instance = false, bool linked = false,
        ElementKind? kind = null, bool windowSource = false, string? hiddenAncestor = null)
    {
        object stamp = (L.Preference, Owner.IndependentPreview, Owner.CanWrite);
        if (Equals(presentationStamp, stamp) && SourceName == sourceName && Name == name && ParentId == parent && Depth == depth && IsGroup == group && IsLegacy == legacy &&
            Visible == visible && Locked == locked && EffectiveVisible == effectiveVisible && EffectiveLocked == effectiveLocked && AncestorLocked == ancestorLocked &&
            IsInstance == instance && IsLinked == linked && Kind == kind && IsWindowSource == windowSource && HiddenAncestor == hiddenAncestor) return;
        presentationStamp = stamp; HiddenAncestor = hiddenAncestor;
        SourceName = sourceName; Name = name; ParentId = parent; Depth = depth; IsGroup = group; IsLegacy = legacy;
        Visible = visible; Locked = locked; EffectiveVisible = effectiveVisible; EffectiveLocked = effectiveLocked; AncestorLocked = ancestorLocked;
        IsInstance = instance; IsLinked = linked; Kind = kind; IsWindowSource = windowSource;
        Changed();
    }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

internal sealed class LayerTreeListBox(LayersWindow owner) : ListBox
{
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is not ListBoxItem container || item is not LayerTreeRow row) return;
        container.Tag = row.Id; container.HorizontalContentAlignment = HorizontalAlignment.Stretch; container.Padding = new(2);
        container.ContextMenu = owner.CreateContextMenu(row.Id);
        container.PreviewMouseRightButtonDown += SelectForContext;
        container.PreviewMouseDoubleClick += EnterGroup;
        System.Windows.Automation.AutomationProperties.SetName(container, row.Name);
    }
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ListBoxItem container)
        {
            if (container.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
            container.PreviewMouseRightButtonDown -= SelectForContext; container.PreviewMouseDoubleClick -= EnterGroup;
            container.ContextMenu = null; container.Tag = null;
        }
        base.ClearContainerForItemOverride(element, item);
    }
    private void SelectForContext(object sender, MouseButtonEventArgs e)
    { if (sender is ListBoxItem { DataContext: LayerTreeRow row }) owner.SelectForContext(row.Id); }
    private void EnterGroup(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None) return;
        for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null && source != sender;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is ButtonBase or TextBoxBase or Thumb) return;
        if (sender is not ListBoxItem { DataContext: LayerTreeRow row } container || LayerTreeRowPresenter.Find(container) is not { } presenter) return;
        e.Handled = presenter.ActivateDoubleClick(e.GetPosition(presenter.Label));
    }
}

/// <summary>Recycled controls for direct children of the currently browsed layer scope.</summary>
internal sealed class LayerTreeRowPresenter : Grid
{
    internal TextBlock Label { get; } = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(3, 0, 3, 0), Background = Brushes.Transparent };
    internal TextBox Editor { get; } = new() { Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new(3, 0, 3, 0), MaxLength = FloatCanvas.Core.EditorSession.MaximumLayerNameLength };
    internal Thumb Handle { get; } = new() { Width = 20, Cursor = Cursors.SizeNS, Template = DragTemplate };
    internal Button EnterButton { get; } = new() { Width = 24, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0) };
    private readonly ContentControl kind = new() { Width = 16, Height = 16, Margin = new(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock legacy = new() { Text = "↕", Width = 16, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
    internal ToggleButton VisibleButton { get; } = StateButton();
    internal ToggleButton LockedButton { get; } = StateButton();
    private LayerTreeRow? row;
    private bool refreshing;
    public LayerTreeRowPresenter()
    {
        MinHeight = 36;
        foreach (double width in new[] { 20d, 24 }) ColumnDefinitions.Add(new() { Width = new(width) });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        foreach (double width in new[] { 16d, 24, 28, 28 }) ColumnDefinitions.Add(new() { Width = new(width) });
        void Add(UIElement item, int column) { SetColumn(item, column); Children.Add(item); }
        Add(Handle, 0); Add(kind, 1); Add(Label, 2); Add(Editor, 2); Add(legacy, 3); Add(EnterButton, 4); Add(VisibleButton, 5); Add(LockedButton, 6);
        EnterButton.Click += (_, _) => { if (row is { } current) current.Owner.EnterGroup(current.Id); };
        Label.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ActivateNameClick(e.GetPosition(Label))) e.Handled = true;
        };
        Editor.TextChanged += (_, _) => { if (!refreshing && row is { Renaming: true } current) current.DraftName = Editor.Text; };
        Editor.PreviewKeyDown += (_, e) => { if (row?.Owner.HandleRenameKey(e.Key) == true) e.Handled = true; };
        Editor.LostKeyboardFocus += (_, _) => { if (!Editor.IsKeyboardFocusWithin && row is { } current) current.Owner.RenameLostFocus(current.Id); };
        TextCompositionManager.AddPreviewTextInputStartHandler(Editor, (_, _) => row?.Owner.RenameComposition(true));
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Editor, (_, _) => row?.Owner.RenameComposition(true));
        TextCompositionManager.AddPreviewTextInputHandler(Editor, (_, _) => row?.Owner.RenameComposition(false));
        VisibleButton.Click += (_, _) => { if (row is { } current) current.Owner.SetLayerFlag(current.Id, true, VisibleButton.IsChecked == true); };
        LockedButton.Click += (_, _) => { if (row is { } current) current.Owner.SetLayerFlag(current.Id, false, LockedButton.IsChecked == true); };
        Handle.DragStarted += (_, e) => { if (row is { } current) current.Owner.StartDrag(current.Id, Handle, e.HorizontalOffset, e.VerticalOffset); };
        Handle.DragDelta += (_, _) => row?.Owner.UpdateDrag();
        Handle.DragCompleted += (_, e) => row?.Owner.CompleteDrag(e.HorizontalChange, e.VerticalChange, e.Canceled);
        DataContextChanged += (_, _) =>
        {
            if (row is not null) row.PropertyChanged -= RowChanged;
            row = DataContext as LayerTreeRow;
            if (row is not null) row.PropertyChanged += RowChanged;
            Update();
        };
    }
    private ItemsControl? FindList()
    {
        DependencyObject? current = this;
        while (current is not null) { if (current is ItemsControl list) return list; current = VisualTreeHelper.GetParent(current); }
        return null;
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e) => Update();
    private void Update()
    {
        if (row is null) return;
        refreshing = true;
        try
        {
            Margin = new(row.Depth * 16, 0, 0, 0);
            if (FindList() is { } list && ItemsControl.ContainerFromElement(list, this) is ListBoxItem container)
                System.Windows.Automation.AutomationProperties.SetName(container, row.Name);
            Label.Text = row.Name; Label.ToolTip = row.Name + "\n" + L.Get(row.IsInstance ? "Layers.InstanceHint" : row.IsGroup ? "Layers.EnterHint" : "Layers.RenameHint");
            Label.Visibility = row.Renaming ? Visibility.Collapsed : Visibility.Visible; Editor.Visibility = row.Renaming ? Visibility.Visible : Visibility.Collapsed;
            if (row.Renaming && Editor.Text != row.DraftName) { Editor.Text = row.DraftName; Editor.SelectAll(); }
            EnterButton.Visibility = row.IsGroup || row.IsInstance ? Visibility.Visible : Visibility.Hidden;
            var chevron = LayersWindow.IconPath("M5,3 L10,8 L5,13"); chevron.Width = chevron.Height = 10;
            EnterButton.Content = chevron; EnterButton.IsEnabled = true;
            EnterButton.ToolTip = L.Get(row.IsInstance ? "Prefab.EditDefinition" : "Layers.EnterGroup");
            kind.Content = LayerTypeIcon.Create(row.Kind, row.IsGroup, row.IsInstance, row.IsLinked, row.IsWindowSource);
            kind.ToolTip = row.IsInstance ? L.Get(row.IsLinked ? "Prefab.Linked" : "Prefab.DefaultName") : row.IsGroup ? L.Get("Layers.DefaultGroup") :
                row.IsWindowSource ? L.Get("WindowSource.Menu") : row.Kind == ElementKind.Presentation ? L.Get("Tool.Presentation") : row.Kind is { } type ? InspectorWindow.ElementName(new() { Kind = type }) : "";
            System.Windows.Automation.AutomationProperties.SetName(kind, kind.ToolTip?.ToString() ?? "");
            legacy.Visibility = row.IsLegacy ? Visibility.Visible : Visibility.Hidden; legacy.ToolTip = L.Get("Layers.LegacyOrder");
            var eye = new Grid(); eye.Children.Add(LayersWindow.IconPath(row.Visible ? LayersWindow.Eye : LayersWindow.EyeOff));
            if (row.HiddenAncestor is not null) eye.Children.Add(new TextBlock { Text = "↳", FontSize = 9, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            VisibleButton.Content = row.HiddenAncestor is null ? LayersWindow.IconPath(row.Visible ? LayersWindow.Eye : LayersWindow.EyeOff) : eye;
            LockedButton.Content = LayersWindow.IconPath(row.Locked || row.AncestorLocked ? LayersWindow.Lock : LayersWindow.Unlock);
            VisibleButton.IsChecked = row.Visible; LockedButton.IsChecked = row.Locked;
            VisibleButton.Opacity = row.EffectiveVisible ? 1 : .5; LockedButton.Opacity = row.EffectiveLocked ? 1 : .5;
            VisibleButton.IsEnabled = LockedButton.IsEnabled = row.Owner.CanWrite && !row.AncestorLocked;
            Handle.IsEnabled = row.Owner.CanWrite && !row.EffectiveLocked;
            VisibleButton.ToolTip = L.Get(row.Visible ? "Inspector.HideLayer" : "Inspector.ShowLayer") + " " + row.Name;
            if (row.HiddenAncestor is { } hidden) VisibleButton.ToolTip += "\n" + L.Format(row.Owner.IndependentPreview ? "LayerExperience.HiddenIndependent" : "LayerExperience.HiddenAncestor", hidden);
            LockedButton.ToolTip = L.Get(row.AncestorLocked ? "Layers.InheritedLock" : row.Locked ? "Inspector.UnlockLayer" : "Inspector.LockLayer") + " " + row.Name;
            foreach (var pair in new (DependencyObject Element, string Name)[] { (EnterButton, EnterButton.ToolTip.ToString()!),
                ((DependencyObject)VisibleButton, VisibleButton.ToolTip.ToString()!), ((DependencyObject)LockedButton, LockedButton.ToolTip.ToString()!),
                ((DependencyObject)Handle, L.Get("Layers.Drag")), ((DependencyObject)Editor, L.Get("Layers.RenameInput")) })
                System.Windows.Automation.AutomationProperties.SetName(pair.Element, pair.Name);
        }
        finally { refreshing = false; }
    }
    internal void FocusEditor() { Editor.Focus(); Editor.SelectAll(); }
    internal bool ActivateNameClick(Point point)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || row is not { } current || !LayerNameInkHit.Contains(Label, point) || FindList() is not { } list ||
            ItemsControl.ContainerFromElement(list, this) is not ListBoxItem { IsSelected: true }) return false;
        return current.Owner.BeginRename(current.Id);
    }
    internal bool ActivateDoubleClick(Point labelPoint)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || row is not { } current) return false;
        if (LayerNameInkHit.Contains(Label, labelPoint)) return current.Owner.BeginRename(current.Id);
        if (!current.IsGroup && !current.IsInstance) return false;
        current.Owner.EnterGroup(current.Id); return true;
    }
    internal static LayerTreeRowPresenter? Find(DependencyObject? root)
    {
        if (root is null) return null; if (root is LayerTreeRowPresenter presenter) return presenter;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static ToggleButton StateButton()
    {
        var button = new ToggleButton { Width = 28, Height = 30, Padding = new(5), Template = ToggleTemplate };
        button.SetResourceReference(Control.ForegroundProperty, "CanvasText"); return button;
    }
    private static readonly ControlTemplate DragTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
          <Border Background="Transparent"><Canvas Width="10" Height="16" HorizontalAlignment="Center" VerticalAlignment="Center">
            <Ellipse Width="2" Height="2" Canvas.Left="1" Canvas.Top="1" Fill="{DynamicResource CanvasMuted}"/>
            <Ellipse Width="2" Height="2" Canvas.Left="7" Canvas.Top="1" Fill="{DynamicResource CanvasMuted}"/>
            <Ellipse Width="2" Height="2" Canvas.Left="1" Canvas.Top="7" Fill="{DynamicResource CanvasMuted}"/>
            <Ellipse Width="2" Height="2" Canvas.Left="7" Canvas.Top="7" Fill="{DynamicResource CanvasMuted}"/>
            <Ellipse Width="2" Height="2" Canvas.Left="1" Canvas.Top="13" Fill="{DynamicResource CanvasMuted}"/>
            <Ellipse Width="2" Height="2" Canvas.Left="7" Canvas.Top="13" Fill="{DynamicResource CanvasMuted}"/>
          </Canvas></Border>
        </ControlTemplate>
        """);
    private static readonly ControlTemplate ToggleTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
          <Border x:Name="Chrome" Background="Transparent" CornerRadius="5" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasButtonHover}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/><Setter TargetName="Chrome" Property="BorderThickness" Value="1"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
}
