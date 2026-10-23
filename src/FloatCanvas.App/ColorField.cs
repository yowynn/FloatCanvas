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
    private readonly PopupHandle popupDisplay;
    private readonly ColorPickerPanel picker;
    private bool pickerSessionOpen, draftCommitted, pickerSessionChanged, isMixed;

    internal bool IsMixed
    {
        get => isMixed;
        set { if (isMixed == value) return; isMixed = value; RefreshValue(); }
    }

    internal bool IsPickerOpen => popupDisplay.IsOpen;
    internal DependencyObject PickerRoot => popup.Child;
    public ColorRole Role { get; }
    public uint Value { get => (uint)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public event Action<uint>? ValueChanged;
    public event Action? EyedropperRequested;
    internal event Action? PickerClosed;

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
        var border = new Border { Child = new System.Windows.Documents.AdornerDecorator { Child = picker }, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasSurface"); border.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None, Child = border, HorizontalOffset = 0, VerticalOffset = 4 };
        popupDisplay=new(popup);
        PopupChildDialog.Attach(border, popupDisplay.Key);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && TryClosePicker()) e.Handled = true; };
        border.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            ClosePicker(); button.Focus(); e.Handled = true;
        };
        SessionWindowCoordinator? popupSession = null;
        popupDisplay.OnOpened((_, _) =>
        {
            pickerSessionOpen = true; draftCommitted = false; pickerSessionChanged = false;
            picker.Value = Value; picker.RefreshCollections();
            popupSession = SessionWindowCoordinator.Find(Window.GetWindow(this));
            popupSession?.RegisterPopup(popupDisplay.Key, ClosePicker);
        });
        popupDisplay.OnClosed((_, _) =>
        {
            CommitPickerDraft(); pickerSessionOpen = false;
            popupSession?.UnregisterPopup(popupDisplay.Key); popupSession = null;
            PickerClosed?.Invoke();
        });
        button.Click += (_, _) => { if (popupDisplay.IsOpen) ClosePicker(); else popupDisplay.IsOpen = true; };
        picker.ColorChosen += color =>
        {
            // An explicit choice can intentionally unify a mixed selection even
            // when it equals the first layer's color. Plain popup closure cannot.
            ApplyPickerValue(color); ClosePicker();
            if (MayReturnFocus && button.IsVisible && button.IsEnabled) button.Focus();
        };
        picker.DraftChanged += color => { if (pickerSessionOpen) ApplyPickerValue(color); };
        picker.EyedropperRequested += () => { ClosePicker(); EyedropperRequested?.Invoke(); };
        Unloaded += (_, _) => ClosePicker();
        if (role == ColorRole.Background) Value = 0;
        RefreshValue();
    }

    public void SetUserValue(uint value)
    {
        if (popupDisplay.IsOpen)
        {
            bool returnToButton = picker.IsKeyboardFocusWithin;
            picker.Value = value; ApplyPickerValue(value); ClosePicker();
            if (returnToButton && MayReturnFocus && button.IsVisible && button.IsEnabled) button.Focus();
            return;
        }
        if (Value != value) library.Remember(value);
        bool changed = IsMixed || Value != value;
        IsMixed = false;
        Value = value;
        if (changed) ValueChanged?.Invoke(value);
    }
    internal bool OwnsPickerInput(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, picker) || ReferenceEquals(current, popupDisplay.Child)) return true;
            if (current is ContextMenu menu && menu.PlacementTarget is { } target) { if (OwnsPickerInput(target)) return true; }
        }
        return false;
    }
    private void CommitPickerDraft()
    {
        if (!pickerSessionOpen || draftCommitted) return;
        draftCommitted = true;
        picker.Run(() =>
        {
            if (picker.TryGetDraft(out uint color) && (!IsMixed || color != Value)) ApplyPickerValue(color);
            if (pickerSessionChanged) library.Remember(Value);
        });
    }
    private void ApplyPickerValue(uint color)
    {
        if (!IsMixed && Value == color) return;
        pickerSessionChanged = true; IsMixed = false; Value = color;
        ValueChanged?.Invoke(color);
    }
    internal void OpenPicker(bool focus = false)
    {
        popupDisplay.IsOpen = true;
        if (focus) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        { if (popupDisplay.IsOpen) picker.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); });
    }
    public void ClosePicker()
    {
        CommitPickerDraft();
        popupDisplay.IsOpen = false;
    }
    public bool TryClosePicker()
    {
        if (!popupDisplay.IsOpen && !pickerSessionOpen) return false;
        ClosePicker(); if (MayReturnFocus) button.Focus(); return true;
    }
    private bool MayReturnFocus => SessionWindowCoordinator.Find(Window.GetWindow(this))?.IsDirectTransfer != true;
    private void RefreshValue()
    {
        swatch.Children.Clear(); swatch.Children.Add(ColorPickerPanel.CreateSwatch(Value));
        double alpha = (Value >> 24) / 255d;
        double luminance = alpha * (.2126 * ((Value >> 16) & 255) + .7152 * ((Value >> 8) & 255) + .0722 * (Value & 255)) + (1 - alpha) * 220;
        label.Foreground = luminance < 140 ? Brushes.White : Brushes.Black;
        label.Text = IsMixed ? "—" : (Value >> 24) == 0 ? L.Get("Color.Transparent") : ColorLibrary.ToHtml(Value, (Value >> 24) != 255);
        string name = L.Get(Role == ColorRole.Foreground ? "Color.Foreground" : "Color.Background") + " " + (IsMixed ? L.Get("Feedback4.Inspector.Mixed") : ColorLibrary.ToHtml(Value));
        button.ToolTip = name; AutomationProperties.SetName(button, name); AutomationProperties.SetName(this, name);
    }
}
