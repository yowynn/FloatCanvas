using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One accessible launcher with mutually exclusive primary, visibility and window-drag gestures.</summary>
internal sealed class ToolbarLauncher : System.Windows.Controls.Button
{
    internal const double ArtworkSize = 94;
    internal const double BodyTop = 32;
    internal const double BodyLeft = 44;
    private LauncherGestureState gestures = null!;
    private readonly DispatcherTimer clickTimer;
    private readonly bool editing;
    public event Action? MenuRequested;
    public event Action? ToggleVisibilityRequested;
    public event Action? DragRequested;
    internal Func<bool>? CloseOpenMenu { get; set; }

    public ToolbarLauncher(bool editing = true)
    {
        this.editing = editing;
        MinWidth = Width = ArtworkSize; MinHeight = Height = ArtworkSize; Padding = new Thickness(0); BorderThickness = new Thickness(0);
        Background = Brushes.Transparent; Focusable = !editing; Cursor = Cursors.Arrow;
        FocusVisualStyle = null;
        var hitSurface = new FrameworkElementFactory(typeof(Border));
        hitSurface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        hitSurface.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        Template = new ControlTemplate(typeof(Button))
        {
            VisualTree = hitSurface
        };
        Content = new Image { Source = AppIcon.LauncherImage(true, editing), Width = ArtworkSize, Height = ArtworkSize, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        ToolTip = L.Get("ToolbarV2.LauncherHint");
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, false);
        ToolTipService.SetInitialShowDelay(this, 650); ToolTipService.SetShowDuration(this, 12000);
        AutomationProperties.SetName(this, L.Get("App.Name"));
        clickTimer = new DispatcherTimer(DispatcherPriority.Input);
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); Execute(gestures.AdvanceTime(Environment.TickCount64)); ArmTimer(); };
        ResetMetrics();
        Unloaded += (_, _) => CancelPendingGestures();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelPendingGestures(); };
    }

    internal Grid CreateSurface(Border body)
    {
        // The crown/hair junction meets the 60-DIP bar's top; the pillow masks
        // both left corners. Extra inner padding preserves the first tool's position.
        body.Margin = new Thickness(BodyLeft, BodyTop, 3, 3);
        body.Padding = new Thickness(58, 2, 4, 2);
        body.VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        var surface = new Grid(); // No background: leave the space above tools transparent.
        surface.Children.Add(body);
        surface.Children.Add(this);
        Panel.SetZIndex(this, 1);
        return surface;
    }

    public void CancelPendingGestures()
    {
        clickTimer.Stop(); gestures.Cancel();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    internal void DismissMenuPress(Point position)
    {
        CloseOpenMenu?.Invoke();
        BeginLeftPress(position, suppressMenu: true);
    }

    internal void SetCanvasVisible(bool visible)
    {
        if(Content is Image artwork)artwork.Source=AppIcon.LauncherImage(visible, editing);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        bool closedMenu = CloseOpenMenu?.Invoke() == true;
        BeginLeftPress(e.GetPosition(this), closedMenu);
        e.Handled = true;
    }
    private void BeginLeftPress(Point position, bool suppressMenu)
    {
        if (!gestures.HasPendingClick && !gestures.IsPointerDown) ResetMetrics();
        if (!editing) Focus();
        var point = PointToScreen(position);
        Execute(gestures.PointerDown(point.X, point.Y, Environment.TickCount64, suppressMenu));
        CaptureMouse(); ArmTimer();
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var point = PointToScreen(e.GetPosition(this));
        var action = gestures.PointerUp(point.X, point.Y, Environment.TickCount64);
        if (IsMouseCaptured) ReleaseMouseCapture();
        Execute(action); ArmTimer(); e.Handled = true;
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!gestures.IsPointerDown || e.LeftButton != MouseButtonState.Pressed) return;
        var point = PointToScreen(e.GetPosition(this));
        Execute(gestures.PointerMove(point.X, point.Y, Environment.TickCount64));
        ArmTimer(); e.Handled = true;
    }
    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        OpenMenuImmediately(); e.Handled = true;
    }
    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e) => e.Handled = true;
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!editing && e.Key is Key.Enter or Key.Space || e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!e.IsRepeat) OpenMenuImmediately();
            e.Handled = true; return;
        }
        base.OnPreviewKeyDown(e);
    }
    protected override void OnClick() { base.OnClick(); OpenMenuImmediately(); }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        // Lost capture bubbles from the dismissed menu through its placement
        // target. That old menu capture must not cancel the new launcher press.
        if (ReferenceEquals(e.OriginalSource, this) && !IsMouseCaptured && (gestures.IsPointerDown || gestures.IsDragging))
        { gestures.Cancel(); clickTimer.Stop(); }
        base.OnLostMouseCapture(e);
    }
    private void OpenMenuImmediately() { CancelPendingGestures(); MenuRequested?.Invoke(); }
    private void Execute(LauncherGestureAction action)
    {
        switch (action)
        {
            case LauncherGestureAction.OpenMenu: MenuRequested?.Invoke(); break;
            case LauncherGestureAction.ToggleVisibility: ToggleVisibilityRequested?.Invoke(); break;
            case LauncherGestureAction.BeginDrag:
                clickTimer.Stop();
                if (IsMouseCaptured) ReleaseMouseCapture();
                try { DragRequested?.Invoke(); }
                finally { gestures.Cancel(); }
                break;
        }
    }
    private void ArmTimer()
    {
        clickTimer.Stop();
        if (gestures.PendingClickDeadlineMs is not { } deadline) return;
        clickTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64));
        clickTimer.Start();
    }
    private void ResetMetrics()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var doubleClick = Forms.SystemInformation.DoubleClickSize;
        gestures = new(Forms.SystemInformation.DoubleClickTime, SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX,
            SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY, doubleClick.Width, doubleClick.Height);
    }
}
