using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private Button? _canvasNavigationButton;
    private OverlayContextMenu? _canvasNavigationMenu;
    private MenuItem? _navigationApply, _navigationConvert;
    private (bool Open, bool Changed, double Zoom)? _navigationAppearance;
    private const string NavigationGlyph = "M2,2 H6 M2,2 V6 M14,2 H10 M14,2 V6 M2,14 H6 M2,14 V10 M14,14 H10 M14,14 V10 M5,8 H11 M8,5 V11";
    private const string ResetNavigationGlyph = "M3,6 A6,6 0 1 1 3,11 M3,2 V6 H7";

    private Button CreateCanvasNavigationButton()
    {
        var button = ToolbarIcon(L.Get("Canvas.Navigation"), NavigationGlyph, ToggleCanvasNavigationMenu);
        button.Tag = "canvas-navigation";
        _canvasNavigationButton = button;
        var menu = _canvasNavigationMenu = new OverlayContextMenu { PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, TargetPressToggles = true };
        foreach (var (command, label) in new[] {
            (CanvasViewingCommand.Reset, "Canvas.ResetView"),
            (CanvasViewingCommand.OriginalSize, "Canvas.View100"),
            (CanvasViewingCommand.CenterContent, "Canvas.CenterContent"),
            (CanvasViewingCommand.FitContent, "Canvas.FitContent"),
            (CanvasViewingCommand.FullCanvas, "Canvas.FullCanvas") })
        {
            var item = new MenuItem { Header = L.Get(label) };
            item.Click += (_, _) => ExecuteCanvasViewing(command);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        _navigationApply = new MenuItem { Header = L.Get("Canvas.ApplyView"), ToolTip = L.Get("Canvas.ApplyViewHint") };
        _navigationApply.Click += (_, _) => ApplyCanvasViewing(); menu.Items.Add(_navigationApply);
        _navigationConvert = new MenuItem { Header = L.Get("Canvas.ConvertProjectResolution") };
        _navigationConvert.Click += (_, _) => _ = RunCommand(ConvertCurrentCanvasResolutionAsync); menu.Items.Add(_navigationConvert);
        void OnInput(object sender, PreProcessInputEventArgs args)
        {
            if (!menu.IsOpen || args.StagingItem.Input is not MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed, ChangedButton: MouseButton.Left } mouse ||
                !new Rect(button.RenderSize).Contains(mouse.GetPosition(button))) return;
            mouse.Handled = true;
            menu.IsOpen = false;
            ExecuteCanvasViewing(CanvasViewingCommand.Reset);
        }
        menu.Opened += (_, _) => { InputManager.Current.PreProcessInput += OnInput; RefreshCanvasNavigationButton(); };
        menu.Closed += (_, _) => { InputManager.Current.PreProcessInput -= OnInput; RefreshCanvasNavigationButton(); };
        button.Unloaded += (_, _) => menu.IsOpen = false;
        RefreshCanvasNavigationButton();
        return button;
    }

    private void ToggleCanvasNavigationMenu()
    {
        if (_canvasNavigationMenu is not { } menu) return;
        if (menu.IsOpen) { menu.IsOpen = false; ExecuteCanvasViewing(CanvasViewingCommand.Reset); return; }
        RefreshCanvasNavigationButton(); menu.IsOpen = true;
    }

    private void RefreshCanvasNavigationButton()
    {
        if (_canvasNavigationButton is not { } button) return;
        bool open = _canvasNavigationMenu?.IsOpen == true;
        bool changed = _canvasView != CanvasViewTransform.Identity;
        if (_navigationAppearance != (open, changed, _canvasView.Zoom))
        {
        _navigationAppearance = (open, changed, _canvasView.Zoom);
        button.Content = CommandGlyphs.Create(open ? ResetNavigationGlyph : NavigationGlyph);
        button.SetResourceReference(Control.ForegroundProperty, changed ? "CanvasHiddenText" : "CanvasText");
        if (button.Content is Viewbox { Child: Canvas canvas })
            foreach (var path in canvas.Children.OfType<System.Windows.Shapes.Path>())
                path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, changed ? "CanvasHiddenText" : "CanvasText");
        button.ToolTip = L.Get(open ? "Canvas.ResetView" : "Canvas.Navigation") + " · " + L.Format("Canvas.ViewScale", _canvasView.Zoom * 100);
        System.Windows.Automation.AutomationProperties.SetName(button, L.Get(open ? "Canvas.ResetView" : "Canvas.Navigation"));
        }
        if (_navigationApply is not null) _navigationApply.IsEnabled = CanApplyCanvasViewing;
        if (_navigationConvert is not null) _navigationConvert.IsEnabled = !DocumentOperationBusy && !Active.IsTransactionActive &&
            ReferenceEquals(Active, _live) && !HasPrefabEditContext && Active.EditScopeId is null && !Active.IsReadOnly &&
            CanvasResolutionConversion.NeedsConversion(Active.Document, ConversionTargetScreen);
    }
}
