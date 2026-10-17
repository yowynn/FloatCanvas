using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.Windows;
using FloatCanvas.Core;
using Primitives = System.Windows.Controls.Primitives;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private Window _toolOptionsWindow = null!;
    private bool IsToolOptionsOpen => _toolOptionsWindow?.IsVisible == true;

    private void CreateToolOptionsPopup(Window toolbar)
    {
        _toolOptionsPanel = new(_editor, _toolPresets, _colorLibrary, _toolStateStore);
        _toolOptionsFrame = new Border { Child = _toolOptionsPanel };
        _toolOptionsWindow = new Window { Content = _toolOptionsFrame, Width = ToolOptionsPanel.PreferredWidth + 2,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Icon = AppIcon.WindowImage };
        AppWindowChrome.Apply(_toolOptionsWindow);
        _windowLayout.Track(_toolOptionsWindow);
        _toolOptionsPin = ActionIconButton.Create(PanelActionIcon.Pin, L.Get("ToolbarV2.PinOptions"), () =>
        {
            _toolOptionsPinned = !_toolOptionsPinned;
            UpdateToolOptionsPin();
        });
        _toolOptionsPin.Width = 28; _toolOptionsPin.Height = 28;
        _toolOptionsPin.Style = (Style)_app.FindResource("CanvasCaptionButton");
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_toolOptionsPin, true);
        AppWindowChrome.SetHeaderActions(_toolOptionsWindow, _toolOptionsPin);
        UpdateToolOptionsPin();
        _toolOptionsWindow.Closing += (_, e) =>
        {
            if (_disposed) return;
            e.Cancel = true;
            CloseToolOptions();
        };
        ToolOptionsKeyboardRoute.Attach(_toolOptionsWindow, () =>
        {
            if (_toolOptionsPanel.CloseColorPicker()) { _toolOptionsWindow.Activate(); return true; }
            if (!_toolOptionsPinned) { CloseToolOptions(); return true; }
            return false;
        }, () => _toolOptionsPanel.IsColorPickerOpen, OnKey);
        _toolOptionsWindow.Deactivated += (_, _) =>
            toolbar.Dispatcher.BeginInvoke(DispatcherPriority.Input, CheckToolOptionsFocus);
        _toolOptionsPanel.ParametersChanged += () =>
        {
            if (_settings.NumberStart != _editor.NumberStart || _settings.NumberStep != _editor.NumberStep)
            {
                _settings.NumberStart = _editor.NumberStart; _settings.NumberStep = _editor.NumberStep;
                try { _settings.Save(); } catch (System.IO.IOException ex) { UpdateStatus(ex.Message); }
            }
            RefreshToolbarState(); RequestRender();
        };
        _toolOptionsPanel.EyedropperRequested += field => { CloseToolOptions(); BeginEyedropper(field); };
        var pointerRoute = new ToolOptionsPointerRoute(() => IsToolOptionsOpen && !_toolOptionsPinned,
            source => IsOptionsInput(source) || IsToolbarInput(source), CloseToolOptions, _toolOptionsWindow, toolbar);
        Action<OverlayPointerEvent> canvasPointer = e =>
        {
            if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && !_toolOptionsPinned) CloseToolOptions();
        };
        _host.Pointer += canvasPointer;
        toolbar.Closed += (_, _) =>
        {
            pointerRoute.Dispose(); _host.Pointer -= canvasPointer;
            _toolOptionsWindow.Close();
        };
    }

    private void UpdateToolOptionsPin()
    {
        _toolOptionsPin.SetResourceReference(Control.BackgroundProperty, "CanvasButton");
        _toolOptionsPin.SetResourceReference(Control.ForegroundProperty, _toolOptionsPinned ? "CanvasAccent" : "CanvasText");
        if (_toolOptionsPin.Content is FrameworkElement glyph)
        {
            glyph.RenderTransformOrigin = new Point(.5, .5);
            glyph.RenderTransform = new RotateTransform(_toolOptionsPinned ? -35 : 0);
        }
        string label = L.Get(_toolOptionsPinned ? "ToolbarV2.UnpinOptions" : "ToolbarV2.PinOptions");
        _toolOptionsPin.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(_toolOptionsPin, label);
    }

    private void OpenToolOptions()
    {
        bool alreadyOpen = IsToolOptionsOpen;
        _toolOptionsPanel.Refresh();
        _toolOptionsWindow.Title = ToolName(_editor.Tool);
        new WindowInteropHelper(_toolbar).EnsureHandle();
        _toolOptionsWindow.Owner ??= _toolbar;
        ConstrainToolOptions();
        _toolOptionsWindow.Show();
        _toolOptionsWindow.UpdateLayout();
        if (!alreadyOpen) PositionToolOptions();
        _toolOptionsWindow.Activate();
    }

    private void CloseToolOptions()
    {
        if (!IsToolOptionsOpen) return;
        _toolOptionsPanel.CloseColorPicker();
        bool active = _toolOptionsWindow.IsActive;
        if (active && _host.IsEditing && _toolbar.IsVisible) _toolbar.Activate();
        _toolOptionsWindow.Hide();
    }

    private void AnchorToolOptions()
    {
        if (!IsToolOptionsOpen) return;
        _toolOptionsWindow.Title = ToolName(_editor.Tool);
        ConstrainToolOptions();
        AppWindowChrome.EnsureVisibleBounds(_toolOptionsWindow);
    }

    private void ConstrainToolOptions()
    {
        var monitor = _host.PrimaryMonitor;
        if (IsToolOptionsOpen)
        {
            var p = _toolOptionsWindow.PointToScreen(new(0, 0));
            monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new(p.X,p.Y))) ?? monitor;
        }
        double scale = monitor.DpiY / 96d;
        _toolOptionsPanel.MaxHeight = Math.Clamp(monitor.WorkArea.Height / scale - 64, 100, 740);
        double available = monitor.WorkArea.Width * 96d / monitor.DpiX - 24;
        _toolOptionsPanel.Width = Math.Min(ToolOptionsPanel.PreferredWidth, Math.Max(280, available - 2));
        _toolOptionsPanel.MinWidth = Math.Min(480, _toolOptionsPanel.Width);
        _toolOptionsWindow.Width = _toolOptionsPanel.Width + 2;
    }

    private void PositionToolOptions()
    {
        var origin = _toolbar.PointToScreen(new(0,0));
        var cursor = System.Windows.Forms.Cursor.Position;
        var point = _settings.ToolbarExpanded ? new OverlayPoint(origin.X,origin.Y) : new(cursor.X,cursor.Y);
        var monitor = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(point)) ?? _host.PrimaryMonitor;
        double sx=monitor.DpiX/96d, sy=monitor.DpiY/96d;
        var work = monitor.WorkArea;
        double width=_toolOptionsWindow.ActualWidth*sx, height=_toolOptionsWindow.ActualHeight*sy;
        double x, y;
        if (_settings.ToolbarExpanded)
        {
            var button = _toolbarToolButtons.GetValueOrDefault(ButtonFamily(_editor.Tool)) ?? _toolbarLauncher;
            var center=button.PointToScreen(new(button.ActualWidth/2,0));
            x=center.X-width/2;
            double below=origin.Y+_toolbar.ActualHeight*sy+8*sy;
            y=below+height<=work.Bottom ? below : origin.Y-height-8*sy;
        }
        else { x=cursor.X+12*sx; y=cursor.Y+12*sy; }
        OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(_toolOptionsWindow).Handle,
            new(Math.Clamp(x,work.Left,Math.Max(work.Left,work.Right-width)),
                Math.Clamp(y,work.Top,Math.Max(work.Top,work.Bottom-height))));
    }

    private static Primitives.CustomPopupPlacement[] LauncherMenuPlacements(Size popup, Size target, Point offset) =>
    [new(new Point(0,target.Height+4),Primitives.PopupPrimaryAxis.Horizontal),
     new(new Point(0,-popup.Height-4),Primitives.PopupPrimaryAxis.Horizontal)];

    // Retained as the initial-placement geometry contract; live windows do not follow the bar.
    internal static Primitives.CustomPopupPlacement[] OptionsPlacements(Size popup, Size toolbar, double toolCenterX, Rect workArea)
    {
        double x=Math.Clamp(toolCenterX-popup.Width/2,workArea.Left,Math.Max(workArea.Left,workArea.Right-popup.Width));
        var below=new Primitives.CustomPopupPlacement(new(x,toolbar.Height+8),Primitives.PopupPrimaryAxis.Horizontal);
        var above=new Primitives.CustomPopupPlacement(new(x,-popup.Height-8),Primitives.PopupPrimaryAxis.Horizontal);
        return workArea.Bottom-toolbar.Height-8>=popup.Height || workArea.Bottom-toolbar.Height-8>=-workArea.Top-8 ? [below,above] : [above,below];
    }
    private void RepositionToolOptions() { } // Toolbar movement/collapse never moves an already opened panel.
    private void RaiseToolOptionsPopup()
    {
        if (!_disposed && IsToolOptionsOpen && new WindowInteropHelper(_toolOptionsWindow).Handle is var h && h!=0)
            OverlayCoordinates.RaiseToolWindow(h);
    }
    private void CheckToolOptionsFocus()
    {
        if (IsToolOptionsOpen && !_toolOptionsPinned && !_toolOptionsWindow.IsActive && !HasActiveOptionsDialog() &&
            !_toolOptionsPanel.IsColorPickerOpen && !IsOptionsInput(Keyboard.FocusedElement as DependencyObject) &&
            !IsOptionsInput(Mouse.DirectlyOver as DependencyObject) && !_toolbar.IsActive) CloseToolOptions();
    }
    private bool HasActiveOptionsDialog() => _app.Windows.Cast<Window>().Any(window =>
    {
        if (!window.IsActive) return false;
        for (var owner = window.Owner; owner is not null; owner = owner.Owner)
            if (ReferenceEquals(owner, _toolOptionsWindow)) return true;
        return false;
    });
    private bool IsToolbarInput(DependencyObject? source) => Ancestors(source).Any(v => ReferenceEquals(v,_toolbar));
    private bool IsOptionsInput(DependencyObject? source) => Ancestors(source).Any(v =>
        ReferenceEquals(v,_toolOptionsFrame) || ReferenceEquals(v,_toolOptionsWindow) ||
        v is ColorPickerPanel && _toolOptionsPanel.OwnsColorPickerInput(v) ||
        v is Window window && IsOwnedOptionsWindow(window));
    private bool IsOwnedOptionsWindow(Window window)
    {
        for (var owner=window.Owner;owner is not null;owner=owner.Owner)
            if (ReferenceEquals(owner,_toolOptionsWindow)) return true;
        return false;
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? source)
    {
        var visited=new HashSet<DependencyObject>();
        while(source is not null && visited.Add(source))
        {
            yield return source;
            source=source switch
            {
                ComboBoxItem item=>ItemsControl.ItemsControlFromItemContainer(item),
                ContextMenu menu=>menu.PlacementTarget,
                _=>source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)??LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source)
            };
        }
    }
}
