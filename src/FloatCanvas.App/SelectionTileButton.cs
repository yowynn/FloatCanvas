using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A button that applies a preset and exposes its separate selected state.</summary>
internal sealed class SelectionTileButton : Button
{
    public static readonly DependencyProperty IsModifiedProperty = DependencyProperty.Register(nameof(IsModified), typeof(bool), typeof(SelectionTileButton), new PropertyMetadata(false));
    public bool IsModified { get => (bool)GetValue(IsModifiedProperty); private set => SetValue(IsModifiedProperty, value); }
    internal bool Selected { get; private set; }
    public SelectionTileButton() => SetResourceReference(TemplateProperty, "CanvasSelectionTileButtonTemplate");
    internal void SetSelected(bool selected, bool modified = false)
    {
        bool old = Selected; Selected = selected;
        IsModified = selected && modified;
        AutomationProperties.SetItemStatus(this, selected ? L.Get(modified ? "Audit.SelectedModified" : "Audit.Selected") : "");
        SetResourceReference(BorderBrushProperty, selected ? "CanvasAccent" : "CanvasBorder");
        if (old != selected && UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, old, selected);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);
    private sealed class Peer(SelectionTileButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
    {
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.SelectionItem ? this : base.GetPattern(pattern);
        public bool IsSelected => owner.Selected;
        public IRawElementProviderSimple? SelectionContainer => null;
        public void Select() => owner.RaiseEvent(new RoutedEventArgs(ClickEvent));
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { }
    }
}
