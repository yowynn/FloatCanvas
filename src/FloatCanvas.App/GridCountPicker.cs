using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A local count shortcut; hovering never publishes a calibration candidate.</summary>
internal sealed class GridCountPicker : Button
{
    private const int Side = 9;
    private readonly PopupHandle popup;
    private readonly TextBlock caption = new() { Margin = new(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
    private readonly Button[,] cells = new Button[Side, Side];
    private readonly Func<(int Columns, int Rows)?> readCounts;
    private SessionWindowCoordinator? session;
    private bool suppressClick;
    private int suppressedPress;
    internal bool IsPickerOpen => popup.IsOpen;
    internal UIElement PickerContent => popup.Child!;
    internal event Action<int, int>? CountsSelected;

    internal GridCountPicker(Func<(int Columns, int Rows)?> readCounts)
    {
        this.readCounts = readCounts;
        Width = Height = PropertyPanelLayout.ControlHeight; MinWidth = MinHeight = 0;
        Margin = new(4, 0, 4, 0); Padding = new(0);
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        Content = CommandGlyphs.Create("M2,2 H14 V14 H2 Z M6,2 V14 M10,2 V14 M2,6 H14 M2,10 H14");
        ToolTip = L.Get("Canvas.PickGridCounts"); AutomationProperties.SetName(this, (string)ToolTip);
        SetResourceReference(StyleProperty, "CanvasPropertyIconButton");
        var panel = new StackPanel(); panel.Children.Add(caption);
        var grid = new UniformGrid { Columns = Side, Rows = Side }; panel.Children.Add(grid);
        for (int y = 1; y <= Side; y++)
        for (int x = 1; x <= Side; x++)
        {
            int columns = x, rows = y;
            var cell = new Button { Width = 20, Height = 20, MinWidth = 0, MinHeight = 0,
                Margin = new(1), Padding = new(0), Tag = (x, y) };
            cell.Resources["CanvasButtonCornerRadius"] = new CornerRadius(2);
            AutomationProperties.SetName(cell, L.Format("Canvas.GridCountChoice", x, y));
            cell.MouseEnter += (_, _) => Highlight(columns, rows);
            cell.GotKeyboardFocus += (_, _) => Highlight(columns, rows);
            cell.Click += (_, e) =>
            {
                e.Handled = true; ClosePicker(); CountsSelected?.Invoke(columns, rows); Focus();
            };
            cell.PreviewKeyDown += (_, e) =>
            {
                int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                if (dx == 0 && dy == 0 || Keyboard.Modifiers != ModifierKeys.None) return;
                cells[Math.Clamp(columns + dx, 1, Side) - 1, Math.Clamp(rows + dy, 1, Side) - 1].Focus(); e.Handled = true;
            };
            cells[x - 1, y - 1] = cell; grid.Children.Add(cell);
        }
        grid.MouseLeave += (_, _) => HighlightSelection();
        var hint = new TextBlock { Text = L.Get("Canvas.PickGridCountsHint"), Margin = new(0, 8, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = Side * 22 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); panel.Children.Add(hint);
        var frame = new Border { Child = panel, Padding = new(12), BorderThickness = new(1), CornerRadius = new(6) };
        frame.SetResourceReference(Border.BackgroundProperty, "CanvasSurface"); frame.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        popup = new(new Popup { PlacementTarget = this, Placement = PlacementMode.Custom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None, Child = frame,
            CustomPopupPlacementCallback = (size, target, _) => [
                new(new((target.Width - size.Width) / 2, target.Height + 4), PopupPrimaryAxis.Horizontal),
                new(new((target.Width - size.Width) / 2, -size.Height - 4), PopupPrimaryAxis.Horizontal)] }) { TargetPressToggles = true };
        frame.PreviewKeyDown += HandleEscape;
        PreviewKeyDown += (_, e) => { suppressClick = false; HandleEscape(this, e); };
        popup.OnOpened((_, _) =>
        {
            HighlightSelection(); session = SessionWindowCoordinator.Find(Window.GetWindow(this)); session?.RegisterPopup(popup.Key, ClosePicker);
            var selected = readCounts();
            int x = selected is { Columns: >= 1 and <= Side } value ? value.Columns : 1;
            int y = selected is { Rows: >= 1 and <= Side } other ? other.Rows : 1;
            foreach (var cell in cells) cell.IsTabStop = ReferenceEquals(cell, cells[x - 1, y - 1]);
            cells[x - 1, y - 1].Focus();
            HighlightSelection();
        });
        popup.OnClosed((_, _) =>
        {
            suppressClick = Mouse.LeftButton == MouseButtonState.Pressed && IsVisible && new Rect(new Point(), RenderSize).Contains(Mouse.GetPosition(this));
            if (suppressClick) suppressedPress = GetMessageTime();
            session?.UnregisterPopup(popup.Key); session = null;
        });
        PreviewMouseLeftButtonDown += (_, e) => { if (e.Timestamp != suppressedPress) suppressClick = false; };
        PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => suppressClick = false);
        Click += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, this)) return;
            if (suppressClick) { suppressClick = false; return; }
            popup.IsOpen = !popup.IsOpen;
        };
        Unloaded += (_, _) => ClosePicker();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClosePicker(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) ClosePicker(); };
    }

    internal void ClosePicker() => popup.IsOpen = false;
    internal bool TryClosePicker()
    {
        if (!popup.IsOpen) return false;
        ClosePicker(); if (IsVisible && IsEnabled) Focus(); return true;
    }
    private void HandleEscape(object sender, KeyEventArgs e)
    { if (!e.Handled && e.Key == Key.Escape && TryClosePicker()) e.Handled = true; }
    private void HighlightSelection()
    {
        var selected = readCounts(); Highlight(selected?.Columns ?? 0, selected?.Rows ?? 0);
    }
    private void Highlight(int columns, int rows)
    {
        caption.Text = columns > 0 && rows > 0 ? L.Format("Canvas.GridCountChoice", columns, rows) : L.Get("Canvas.GridSampleSection");
        var selected = readCounts(); bool withinPicker = columns is >= 1 and <= Side && rows is >= 1 and <= Side;
        for (int y = 1; y <= Side; y++)
        for (int x = 1; x <= Side; x++)
        {
            var cell = cells[x - 1, y - 1]; bool lit = withinPicker && x <= columns && y <= rows;
            cell.SetResourceReference(BackgroundProperty, lit ? "CanvasPressed" : "CanvasInput");
            cell.SetResourceReference(BorderBrushProperty, lit ? "CanvasAccent" : "CanvasBorder");
            AutomationProperties.SetItemStatus(cell, selected == (x, y) ? L.Get("Canvas.GridCountSelected") : "");
        }
    }
    [DllImport("user32.dll")] private static extern int GetMessageTime();
}
