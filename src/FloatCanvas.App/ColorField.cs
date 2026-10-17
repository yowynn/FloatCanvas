using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Shared color input. Programmatic synchronization stays silent; user choices notify the owner.</summary>
internal sealed class ColorField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(uint), typeof(ColorField),
        new FrameworkPropertyMetadata(ColorLibrary.ForegroundRed, (sender, _) => ((ColorField)sender).RefreshValue()));
    private readonly ColorLibrary library;
    private readonly System.Windows.Controls.Button button = new() { MinHeight = 34, Padding = new Thickness(6, 4, 6, 4) };
    private readonly Grid swatch = new() { IsHitTestVisible = false };
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10, FontWeight = FontWeights.SemiBold };
    private readonly Popup popup;
    private readonly ColorPickerPanel picker;

    internal bool IsPickerOpen => popup.IsOpen;
    internal DependencyObject PickerRoot => popup.Child;
    public ColorRole Role { get; }
    public uint Value { get => (uint)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public event Action<uint>? ValueChanged;
    public event Action? EyedropperRequested;

    public ColorField(ColorRole role, ColorLibrary? library = null)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        Role = role; this.library = library ?? ColorLibrary.Shared;
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var face = new Grid(); face.Children.Add(swatch); face.Children.Add(label); button.Content = face;
        swatch.SizeChanged += (_, _) =>
        {
            if (button.Template?.FindName("Chrome", button) is not Border chrome) return;
            var inset = swatch.TranslatePoint(new Point(), button);
            swatch.Clip = new RectangleGeometry(new Rect(0, 0, swatch.ActualWidth, swatch.ActualHeight),
                Math.Max(0, chrome.CornerRadius.TopLeft - inset.X), Math.Max(0, chrome.CornerRadius.TopLeft - inset.Y));
        };
        button.Padding = new(0); button.BorderThickness = new(1); button.SetResourceReference(BorderBrushProperty, "CanvasBorder");
        button.MinWidth = 66; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
        row.Children.Add(button);
        var palette = new PixelTilePanel(5, 17) { Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(palette, 1); row.Children.Add(palette);
        foreach (var color in ColorLibrary.Palette(role))
        {
            var tile = new ColorSwatchButton(color.Value) { ToolTip = color.Name };
            AutomationProperties.SetName(tile, color.Name); tile.Click += (_, _) => SetUserValue(color.Value); palette.Children.Add(tile);
        }
        Content = row;
        picker = new ColorPickerPanel(role, this.library) { DialogOwner = () => Window.GetWindow(this) };
        var border = new Border { Child = picker, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasSurface"); border.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None, Child = border, HorizontalOffset = 0, VerticalOffset = 4 };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && TryClosePicker()) e.Handled = true; };
        border.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            ClosePicker(); button.Focus(); e.Handled = true;
        };
        popup.Opened += (_, _) => { picker.Value = Value; picker.RefreshCollections(); };
        popup.Closed += (_, _) => picker.CommitDraft(SetUserValue);
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        picker.ColorChosen += _ => ClosePicker();
        picker.EyedropperRequested += () => { popup.IsOpen = false; EyedropperRequested?.Invoke(); };
        Unloaded += (_, _) => popup.IsOpen = false;
        if (role == ColorRole.Background) Value = 0;
        RefreshValue();
    }

    public void SetUserValue(uint value)
    {
        if (Value != value) library.Remember(value);
        bool returnToButton = popup.IsOpen && picker.IsKeyboardFocusWithin;
        bool changed = Value != value;
        Value = value; if (popup.IsOpen) { picker.Value = value; popup.IsOpen = false; }
        // A committed choice completes this picker interaction. Keep keyboard access
        // on its trigger, just as Escape does; closed/background updates stay silent.
        if (returnToButton && button.IsVisible && button.IsEnabled) button.Focus();
        if (changed) ValueChanged?.Invoke(value);
    }
    internal bool OwnsPickerInput(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, picker) || ReferenceEquals(current, popup.Child)) return true;
            if (current is ContextMenu menu && menu.PlacementTarget is { } target) { if (OwnsPickerInput(target)) return true; }
        }
        return false;
    }
    public void ClosePicker() => popup.IsOpen = false;
    public bool TryClosePicker()
    {
        if (!popup.IsOpen) return false;
        ClosePicker(); button.Focus(); return true;
    }
    private void RefreshValue()
    {
        swatch.Children.Clear(); swatch.Children.Add(ColorPickerPanel.CreateSwatch(Value));
        double alpha = (Value >> 24) / 255d;
        double luminance = alpha * (.2126 * ((Value >> 16) & 255) + .7152 * ((Value >> 8) & 255) + .0722 * (Value & 255)) + (1 - alpha) * 220;
        label.Foreground = luminance < 140 ? Brushes.White : Brushes.Black;
        label.Text = (Value >> 24) == 0 ? L.Get("Color.Transparent") : ColorLibrary.ToHtml(Value, (Value >> 24) != 255);
        string name = L.Get(Role == ColorRole.Foreground ? "Color.Foreground" : "Color.Background") + " " + ColorLibrary.ToHtml(Value);
        button.ToolTip = name; AutomationProperties.SetName(button, name); AutomationProperties.SetName(this, name);
    }
}
