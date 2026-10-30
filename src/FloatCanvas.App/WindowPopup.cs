using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>A transient owned panel with normal caption, focus and modal ownership.</summary>
internal sealed class WindowPopup(UIElement child, UIElement target, string title, double offset) : FrameworkElement
{
    private Window? window, owner;
    private int suspensionDepth;
    internal UIElement Child => child;
    internal bool StaysOpen { get; set; }
    internal bool TargetPressToggles { get; set; }
    internal double VerticalOffset { get; set; } = offset;
    internal event EventHandler? Opened, Closed;
    internal event KeyEventHandler? EscapePressed;
    internal bool IsOpen
    {
        get => window is not null;
        set { if (value) Open(); else if (window is { } current) SessionWindowCoordinator.Close(current); }
    }

    internal IDisposable Suspend()
    {
        suspensionDepth++;
        var suspended = window;
        suspended?.Hide();
        return new Suspension(() =>
        {
            if (--suspensionDepth != 0 || suspended is null || !ReferenceEquals(window, suspended) || owner?.IsVisible != true) return;
            SessionWindowCoordinator.Show(suspended, activate: true);
        });
    }

    private sealed class Suspension(Action resume) : IDisposable
    {
        private Action? action = resume;
        public void Dispose() { var current = action; action = null; current?.Invoke(); }
    }

    private void Open()
    {
        if (window is not null) return;
        owner = Window.GetWindow(target) ?? throw new InvalidOperationException("A panel requires its source window.");
        var shown = new Window { Title = title, Content = child, Owner = owner, ShowInTaskbar = false, Topmost = true,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = owner.IsActive, Icon = AppIcon.WindowImage };
        // Theme resources are application-scoped. A source dictionary also contains live
        // caption controls; sharing it reparents the owner's Pin/Help actions into this window.
        AppTheme.EnsureDialogPalette(shown);
        window = shown;
        AppWindowChrome.Apply(shown);
        child.Measure(new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
        var point = target.PointToScreen(new Point(0, target.RenderSize.Height));
        double scale = VisualTreeHelper.GetDpi(target).DpiScaleY;
        point.Y += VerticalOffset * scale;
        var work = System.Windows.Forms.Screen.FromPoint(new((int)point.X, (int)point.Y)).WorkingArea;
        shown.MaxHeight = System.Math.Max(120, work.Height / scale - 16);
        point.X = System.Math.Clamp(point.X, work.Left, System.Math.Max(work.Left, work.Right - child.DesiredSize.Width * scale - 2));
        point.Y = System.Math.Clamp(point.Y, work.Top, System.Math.Max(work.Top, work.Bottom - (child.DesiredSize.Height + 34) * scale));
        shown.SourceInitialized += (_, _) => OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(shown).Handle, new(point.X, point.Y));
        shown.Closed += OnClosed;
        // Bubble after content cancellation, including when focus is on the caption or window root.
        shown.KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Key.Escape) return;
            EscapePressed?.Invoke(this, e);
            if (!e.Handled) { e.Handled = true; IsOpen = false; }
        };
        shown.Deactivated += (_, _) => shown.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(window, shown) && suspensionDepth == 0 && !StaysOpen && !shown.IsActive && !shown.IsKeyboardFocusWithin &&
                !Application.Current.Windows.Cast<Window>().Any(candidate => candidate.IsActive && IsOwnedBy(candidate, shown)))
                CloseOutside();
        });
        owner.IsVisibleChanged += OwnerVisibilityChanged;
        InputManager.Current.PreProcessInput += BeforeInput;
        // Finish initial session placement before the source-button anchor runs. Creating the
        // HWND inside Show's placement pass would let that outer pass overwrite this anchor.
        _ = new WindowInteropHelper(shown).EnsureHandle();
        SessionWindowCoordinator.Show(shown, activate: owner.IsActive || SessionWindowCoordinator.Find(owner) is not null);
        shown.UpdateLayout();
        AppWindowChrome.EnsureVisibleBounds(shown);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (sender is not Window closed || !ReferenceEquals(window, closed)) return;
        window = null; closed.Content = null;
        InputManager.Current.PreProcessInput -= BeforeInput;
        if (owner is not null) owner.IsVisibleChanged -= OwnerVisibilityChanged;
        owner = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    { if (owner?.IsVisible == false) { if (suspensionDepth != 0) window?.Hide(); else IsOpen = false; } }

    private void BeforeInput(object sender, PreProcessInputEventArgs args)
    {
        if (suspensionDepth != 0 || PopupChildDialog.StaysOpenForOutsidePress(this,StaysOpen) || window is null || args.StagingItem.Input is not MouseButtonEventArgs mouse || mouse.ButtonState != MouseButtonState.Pressed) return;
        var origin = mouse.OriginalSource as DependencyObject ?? mouse.MouseDevice.Target as DependencyObject ?? Mouse.Captured as DependencyObject;
        if (origin is null || BelongsTo(origin, window) || TargetPressToggles && BelongsTo(origin, target)) return;
        CloseOutside();
    }

    private void CloseOutside()
    {
        using var intent = SessionWindowCoordinator.Find(owner)?.DirectTransfer(WindowDismissReason.ExternalFocus);
        IsOpen = false;
    }

    private static bool IsOwnedBy(Window candidate, Window ancestor)
    {
        for (Window? current = candidate; current is not null; current = current.Owner)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static bool BelongsTo(DependencyObject origin, DependencyObject ancestor)
    {
        for (DependencyObject? current = origin; current is not null; current = current switch
        {
            Window value => value.Owner,
            ContextMenu menu => menu.PlacementTarget,
            ComboBoxItem item => ItemsControl.ItemsControlFromItemContainer(item),
            OverlayPopup popup => popup.PlacementTarget,
            _ => LogicalTreeHelper.GetParent(current) ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null)
        })
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
