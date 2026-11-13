using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Hints belong to a live hover, not to retained focus or an open trigger's child surface.</summary>
internal static class TooltipLifecycle
{
    private static bool initialized;
    private static readonly Dictionary<ToolTip, Action> open = [];
    private static readonly DependencyProperty SuppressionProperty = DependencyProperty.RegisterAttached("Suppression", typeof(int), typeof(TooltipLifecycle), new PropertyMetadata(0));
    private static readonly DependencyProperty LeaseProperty = DependencyProperty.RegisterAttached("Lease", typeof(IDisposable), typeof(TooltipLifecycle));
    private static readonly DependencyProperty WatchingComboProperty = DependencyProperty.RegisterAttached("WatchingCombo", typeof(bool), typeof(TooltipLifecycle), new PropertyMetadata(false));

    internal static void Initialize()
    {
        if (initialized) return; initialized = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((sender, e) =>
        {
            if (ReferenceEquals(sender, e.OriginalSource) && sender is FrameworkElement target && !CanShow(target)) e.Handled = true;
        }));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipClosingEvent, new ToolTipEventHandler((sender, e) =>
        {
            if (ReferenceEquals(sender, e.OriginalSource) && sender is FrameworkElement target) CloseFor(target);
        }), true);
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnOpened));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent, new RoutedEventHandler((sender, _) =>
        { if (sender is ToolTip tip && open.Remove(tip, out var detach)) detach(); }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) =>
        { if (sender is ContextMenu menu) Hold(menu, menu.PlacementTarget); }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((sender, _) => Release((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var combo = (ComboBox)sender;
            if ((bool)combo.GetValue(WatchingComboProperty)) return;
            combo.SetValue(WatchingComboProperty, true);
            combo.DropDownOpened += (_, _) => Hold(combo, combo);
            combo.DropDownClosed += (_, _) => Release(combo);
            combo.Unloaded += (_, _) => Release(combo);
            if (combo.IsDropDownOpen) Hold(combo, combo);
        }));
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed } or KeyEventArgs { IsDown: true })
                foreach (var tip in open.Keys.ToArray()) OverlayTransientControls.CloseHint(tip);
        };
    }

    internal static bool CanShow(FrameworkElement target) => target.IsVisible && target.IsMouseOver &&
        PresentationSource.FromVisual(target) is not null && ToolTipService.GetIsEnabled(target) &&
        (target.IsEnabled || ToolTipService.GetShowOnDisabled(target)) && (int)target.GetValue(SuppressionProperty) == 0;

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip || tip.PlacementTarget is not FrameworkElement target) return;
        if (!CanShow(target)) { OverlayTransientControls.CloseHint(tip); return; }
        if (open.ContainsKey(tip)) return;
        var owner = Window.GetWindow(target);
        void Close() => OverlayTransientControls.CloseHint(tip);
        void Leave(object sender, MouseEventArgs e) => Close();
        void Unload(object sender, RoutedEventArgs e) => Close();
        void Availability(object sender, DependencyPropertyChangedEventArgs e) { if (!CanShow(target)) Close(); }
        void Deactivate(object? sender, EventArgs e) => Close();
        target.MouseLeave += Leave; target.Unloaded += Unload;
        target.IsVisibleChanged += Availability; target.IsEnabledChanged += Availability;
        if (owner is not null) owner.Deactivated += Deactivate;
        open.Add(tip, () =>
        {
            target.MouseLeave -= Leave; target.Unloaded -= Unload;
            target.IsVisibleChanged -= Availability; target.IsEnabledChanged -= Availability;
            if (owner is not null) owner.Deactivated -= Deactivate;
        });
    }

    private static void CloseFor(FrameworkElement target)
    {
        foreach (var tip in open.Keys.Where(t => ReferenceEquals(t.PlacementTarget, target)).ToArray()) OverlayTransientControls.CloseHint(tip);
        // Also cover explicit tips opened before the shared service was initialized.
        if (target.ToolTip is ToolTip own && OverlayTransientControls.IsHintOpen(own)) OverlayTransientControls.CloseHint(own);
    }

    internal static IDisposable Suppress(UIElement? anchor)
    {
        Initialize();
        var targets = new List<FrameworkElement>();
        for (DependencyObject? current = anchor; current is not null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement target) continue;
            target.SetValue(SuppressionProperty, (int)target.GetValue(SuppressionProperty) + 1); targets.Add(target);
        }
        // Establish all guards before Closed callbacks can try to open another hint.
        foreach (var target in targets) CloseFor(target);
        return new Lease(targets);
    }

    private sealed class Lease(List<FrameworkElement> targets) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var target in targets) target.SetValue(SuppressionProperty, Math.Max(0, (int)target.GetValue(SuppressionProperty) - 1));
            targets.Clear();
        }
    }
    private static void Hold(DependencyObject surface, UIElement? target)
    {
        if (surface.GetValue(LeaseProperty) is IDisposable) return;
        surface.SetValue(LeaseProperty, Suppress(target));
    }
    private static void Release(DependencyObject surface)
    {
        var lease = surface.GetValue(LeaseProperty) as IDisposable; surface.ClearValue(LeaseProperty); lease?.Dispose();
    }
}
