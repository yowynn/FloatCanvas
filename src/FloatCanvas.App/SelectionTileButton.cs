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
    internal bool Selected { get; private set; }
    internal void SetSelected(bool selected, bool modified = false)
    {
        bool old = Selected; Selected = selected;
        AutomationProperties.SetItemStatus(this, selected ? L.Get(modified ? "Audit.SelectedModified" : "Audit.Selected") : "");
        SetResourceReference(BorderBrushProperty, selected ? "CanvasAccent" : "CanvasBorder");
        if (Content is Grid { Tag: "selection-content" } previous) { var original = previous.Children[0]; previous.Children.RemoveAt(0); Content = original; }
        if (selected && Content is UIElement child)
        {
            Content = null;
            if (child is FrameworkElement { Parent: Panel parent }) parent.Children.Remove(child);
            var grid = new Grid { Tag = "selection-content" }; grid.Children.Add(child);
            var mark = new TextBlock { Text = modified ? "✓*" : "✓", FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false };
            mark.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText");
            grid.Children.Add(mark); Content = grid;
        }
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
