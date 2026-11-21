using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Runtime.InteropServices;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Shared color input. Programmatic synchronization stays silent; user choices notify the owner.</summary>
internal sealed class ColorField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(uint), typeof(ColorField),
        new FrameworkPropertyMetadata(ColorLibrary.ForegroundRed, (sender, _) => ((ColorField)sender).SynchronizeValue()));
    private readonly ColorLibrary library;
    private readonly ColorSwatchButton[] quickColors = new ColorSwatchButton[10];
    private readonly ColorSwatchButton switchPalette = new(0);
    private readonly HashSet<object> alternatePalettes = [];
    private object paletteContext = "";
    internal ColorRole DisplayedPalette => alternatePalettes.Contains(paletteContext)
        ? (Role == ColorRole.Foreground ? ColorRole.Background : ColorRole.Foreground) : Role;
    private readonly System.Windows.Controls.Button button = new() { MinHeight = 34, Padding = new Thickness(6, 4, 6, 4) };
    private readonly Grid swatch = new() { IsHitTestVisible = false };
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10, FontWeight = FontWeights.SemiBold };
    private readonly PopupHandle popupDisplay;
    private readonly ColorPickerPanel picker;
    private readonly ColorClipboardMenu clipboard;
    private string colorLabel = L.Get("Color.Unified");
    private bool pickerSessionOpen, draftCommitted, pickerSessionChanged, isMixed;
    private bool suppressTriggerClick;
    private int suppressedPressTime;
    private Point? opacityPress;
    private uint? opacityDragStart;
    private int pickerSuspensions;
    private bool valueDirty = true;
    private bool publishingValue;

    internal bool IsMixed
    {
        get => isMixed;
        set { if (isMixed == value) return; isMixed = value; RefreshValue(); }
    }

    internal bool IsPickerOpen => popupDisplay.IsOpen;
    internal int? OpacityDragDelta { get; private set; }
    internal bool IsInteracting => opacityDragStart.HasValue || picker.IsInteracting;
    internal bool IsSynchronizingModel { get; private set; }
    internal bool IsNotifyingOwner { get; private set; }
    internal DependencyObject PickerRoot => popupDisplay.Child!;
    public ColorRole Role { get; }
    public uint Value { get => (uint)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public event Action<uint>? ValueChanged;
    public event Action? EyedropperRequested;
    internal event Action? PickerClosed;
    internal Func<bool>? CanBeginGesture { get; set; }
    internal event Action? GestureStarted;
    internal event Action<bool>? GestureCompleted;

    public ColorField(ColorRole role, ColorLibrary? library = null)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        Role = role; this.library = library ?? ColorLibrary.Shared;
        Height = MinHeight = button.Height = button.MinHeight = PropertyPanelLayout.ControlHeight;
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var face = new Grid(); face.Children.Add(swatch); face.Children.Add(label); button.Content = face;
        swatch.SizeChanged += (_, _) =>
        {
            if (button.Template?.FindName("Chrome", button) is not Border chrome) return;
            ColorPickerPanel.ClipSwatch(swatch, chrome);
        };
        button.Padding = new(0); button.BorderThickness = new(1); button.SetResourceReference(BorderBrushProperty, "CanvasBorder");
        button.MinWidth = 66; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
        row.Children.Add(button);
        var palette = new PixelTilePanel(6, (PropertyPanelLayout.ControlHeight - 2) / 2) { Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(palette, 1); row.Children.Add(palette);
        for (int i = 0; i < quickColors.Length; i++)
        {
            var tile = quickColors[i] = new(0);
            tile.Click += (_, _) => SetUserValue((uint)tile.Tag);
            palette.Children.Add(tile);
            if (i == 4)
            {
                var clear = new ColorSwatchButton(0) { Content = ActionIconButton.Glyph(PanelActionIcon.Transparent, 13) };
                clear.ToolTip = L.Get("Color.SetTransparent"); AutomationProperties.SetName(clear, (string)clear.ToolTip);
                clear.Click += (_, _) => SetUserValue(0);
                palette.Children.Add(clear);
            }
        }
        switchPalette.Content = ActionIconButton.Glyph(PanelActionIcon.SwitchPalette, 13);
        switchPalette.Click += (_, _) =>
        {
            if (!alternatePalettes.Remove(paletteContext)) alternatePalettes.Add(paletteContext);
            RefreshQuickColors();
        };
        palette.Children.Add(switchPalette);
        RefreshQuickColors();
        Content = row;
        picker = new ColorPickerPanel(role, this.library);
        clipboard = new ColorClipboardMenu(button, () => Value, SetUserValue, message =>
        {
            button.ToolTip = message; AutomationProperties.SetHelpText(this, message);
        }, allowTransparent: true);
        picker.CanBeginGesture = () => CanBeginGesture?.Invoke() != false;
        picker.GestureStarted += () => GestureStarted?.Invoke();
        picker.GestureCompleted += commit => GestureCompleted?.Invoke(commit);
        picker.DialogOwner = () => Window.GetWindow(picker) ?? Window.GetWindow(this);
        picker.DragHandle.Visibility = Visibility.Collapsed;
        var border = new Border { Child = new System.Windows.Documents.AdornerDecorator { Child = picker } };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasSurface"); border.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None, Child = border, HorizontalOffset = 0, VerticalOffset = 4 };
        popupDisplay=new(popup, colorLabel) { TargetPressToggles = true };
        if (popupDisplay.Key is WindowPopup window) window.EscapePressed += (_, e) => HandlePickerEscape(e);
        PopupChildDialog.Attach(border, popupDisplay.Key);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && (NumericValueField.TryCancelForInput(e.OriginalSource as DependencyObject) || TryClosePicker())) e.Handled = true;
        };
        border.PreviewKeyDown += (_, e) =>
        {
            if (SessionWindowCoordinator.Find(Window.GetWindow(this))?.HistoryCommand is { } command)
                PresetHistoryKeyboardRoute.Handle(e, Keyboard.Modifiers, command);
            HandlePickerEscape(e);
        };
        SessionWindowCoordinator? popupSession = null;
        popupDisplay.OnOpened((_, _) =>
        {
            pickerSessionOpen = true; draftCommitted = false; pickerSessionChanged = false;
            picker.BeginSession(Value); picker.RefreshCollections();
            popupSession = SessionWindowCoordinator.Find(Window.GetWindow(this));
            popupSession?.RegisterPopup(popupDisplay.Key, ClosePicker);
        });
        popupDisplay.OnClosed((_, _) =>
        {
            // Desktop Popup may auto-close on the trigger's mouse-down before
            // Button raises Click on mouse-up. That same click must not reopen it.
            suppressTriggerClick = Mouse.LeftButton == MouseButtonState.Pressed && button.IsVisible &&
                new Rect(new Point(), button.RenderSize).Contains(Mouse.GetPosition(button));
            if (suppressTriggerClick) suppressedPressTime = GetMessageTime();
            CommitPickerDraft(); pickerSessionOpen = false;
            popupSession?.UnregisterPopup(popupDisplay.Key); popupSession = null;
            PickerClosed?.Invoke();
        });
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.Timestamp != suppressedPressTime) suppressTriggerClick = false;
            if (!popupDisplay.IsOpen && !suppressTriggerClick)
                opacityPress = e.GetPosition(button);
        };
        button.PreviewMouseMove += (_, e) =>
        {
            if (button.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
                UpdateOpacityPreview(e.GetPosition(button), e.Timestamp);
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (button.IsMouseCaptured) UpdateOpacityPreview(e.GetPosition(button), e.Timestamp);
            EndOpacityDrag(commit: true);
            ClearTriggerSuppressionAfterInput();
        };
        button.LostMouseCapture += (_, _) =>
        {
            EndOpacityDrag(commit: false);
            ClearTriggerSuppressionAfterInput();
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && opacityDragStart.HasValue)
            {
                e.Handled = TryClosePicker();
                return;
            }
            if (!opacityPress.HasValue) suppressTriggerClick = false;
        };
        button.Click += (_, e) =>
        {
            // OverlayPopup's logical parent is this placement target. A nested
            // swatch/save button must not toggle its owner's picker on bubbling.
            if (!ReferenceEquals(e.OriginalSource, button)) return;
            if (suppressTriggerClick) { suppressTriggerClick = false; return; }
            if (popupDisplay.IsOpen) ClosePicker(); else OpenPicker();
        };
        picker.ColorChosen += color =>
        {
            // An explicit choice can intentionally unify a mixed selection even
            // when it equals the first layer's color. Plain popup closure cannot.
            ApplyPickerValue(color); ClosePicker();
            if (MayReturnFocus && button.IsVisible && button.IsEnabled) button.Focus();
        };
        picker.DraftChanged += color => { if (pickerSessionOpen) ApplyPickerValue(color); };
        picker.EyedropperRequested += () => EyedropperRequested?.Invoke();
        Unloaded += (_, _) => ClosePicker();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) PrepareForShow();
            else ClosePicker();
        };
        Loaded += (_, _) => { if (IsVisible) PrepareForShow(); };
        if (role == ColorRole.Background) Value = 0;
        RefreshValue();
    }

    private void UpdateOpacityPreview(Point position, int timestamp)
    {
        if (opacityPress is not Point press) return;
        double dx = position.X - press.X;
        double horizontal = Math.Abs(dx);
        double vertical = Math.Abs(position.Y - press.Y);
        double threshold = Math.Max(5, SystemParameters.MinimumHorizontalDragDistance);
        if (!suppressTriggerClick)
        {
            if (horizontal < threshold && vertical < Math.Max(5, SystemParameters.MinimumVerticalDragDistance)) return;
            // A deliberate move is no longer a click. Only a horizontal move edits alpha.
            suppressTriggerClick = true;
            suppressedPressTime = timestamp;
            if (horizontal < threshold || horizontal < vertical) { opacityPress = null; return; }
        }

        const double dipsPerPercent = 2;
        double distance = Math.Sign(dx) * Math.Max(0, horizontal - threshold);
        int delta = (int)Math.Round(distance * 255 / (100 * dipsPerPercent));
        uint start = opacityDragStart ?? Value;
        uint color = AdjustAlpha(start, delta);
        if ((!IsMixed && color == Value) || delta == (OpacityDragDelta ?? 0)) return;
        if (!opacityDragStart.HasValue)
        {
            if (CanBeginGesture?.Invoke() == false) { opacityPress = null; return; }
            opacityDragStart = start;
            OpacityDragDelta = 0;
            GestureStarted?.Invoke();
        }
        PublishOpacityPreview(color, delta);
    }
    internal static uint AdjustAlpha(uint color, int delta)
    {
        uint alpha = (uint)Math.Clamp((int)(color >> 24) + delta, 0, 255);
        return (color & 0x00FFFFFF) | (alpha << 24);
    }

    private void PublishOpacityPreview(uint color, int delta)
    {
        OpacityDragDelta = delta;
        // Alpha drags use the owner's live edit path without adding color history.
        SetPublishedValue(color);
        NotifyOwner(color);
    }

    internal bool EndOpacityDrag(bool commit, bool restore = true)
    {
        opacityPress = null;
        if (opacityDragStart is not uint start) return false;
        opacityDragStart = null;
        try
        {
            if (!commit && restore && OpacityDragDelta != 0) PublishOpacityPreview(start, 0);
        }
        finally
        {
            try { GestureCompleted?.Invoke(commit); }
            finally { OpacityDragDelta = null; }
        }
        return true;
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
        bool changed = IsMixed || Value != value;
        IsMixed = false;
        SetPublishedValue(value);
        if (changed) NotifyOwner(value);
        if (changed) Remember(value);
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
    internal void RefreshPresets() { if (IsPickerOpen) picker.RefreshCollections(); }
    internal void SetPaletteContext(object context)
    {
        if (Equals(paletteContext, context)) return;
        var previous = DisplayedPalette;
        paletteContext = context;
        if (previous != DisplayedPalette) RefreshQuickColors();
    }
    private void RefreshQuickColors()
    {
        var palette = ColorLibrary.Palette(DisplayedPalette);
        for (int i = 0; i < quickColors.Length; i++)
        {
            var tile = quickColors[i]; var color = palette[i];
            tile.Content = ColorPickerPanel.CreateSwatch(color.Value); tile.Tag = color.Value;
            tile.ToolTip = color.Name; AutomationProperties.SetName(tile, color.Name);
        }
        var label = L.Get(DisplayedPalette == ColorRole.Foreground ? "Color.UseBackgroundPalette" : "Color.UseForegroundPalette");
        switchPalette.ToolTip = label; AutomationProperties.SetName(switchPalette, label);
    }
    private void ClearTriggerSuppressionAfterInput()
    {
        int press = suppressedPressTime;
        // Button releases capture before raising its mouse-up Click.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        { if (suppressedPressTime == press) suppressTriggerClick = false; });
    }
    private void CommitPickerDraft()
    {
        if (!pickerSessionOpen || draftCommitted) return;
        draftCommitted = true;
        picker.Run(() =>
        {
            picker.EndGesture(true);
            if (picker.TryGetDraft(out uint color) && (!IsMixed || color != Value)) ApplyPickerValue(color);
            if (pickerSessionChanged) Remember(Value);
        });
    }
    private void ApplyPickerValue(uint color)
    {
        if (!IsMixed && Value == color) return;
        pickerSessionChanged = true; IsMixed = false; SetPublishedValue(color);
        NotifyOwner(color);
    }
    private void NotifyOwner(uint color)
    {
        IsNotifyingOwner = true;
        try { ValueChanged?.Invoke(color); }
        finally { IsNotifyingOwner = false; }
    }
    internal void SynchronizeModel(uint color)
    {
        if (Value != color) EndOpacityDrag(commit: false, restore: false);
        IsSynchronizingModel = true;
        try { picker.SynchronizeExternalValue(color); pickerSessionChanged = false; Value = color; }
        finally { IsSynchronizingModel = false; }
    }
    private void SetPublishedValue(uint value)
    {
        publishingValue = true;
        try { Value = value; }
        finally { publishingValue = false; }
    }
    private void SynchronizeValue()
    {
        if (!publishingValue && !IsSynchronizingModel)
        {
            // Owner echo of our exact value never changes the dependency property.
            // A different value is an external undo, preset or document replacement.
            EndOpacityDrag(commit: false, restore: false);
            IsSynchronizingModel = true;
            try { picker?.SynchronizeExternalValue(Value); }
            finally { IsSynchronizingModel = false; }
            pickerSessionChanged = false;
        }
        RefreshValue();
    }
    private void Remember(uint color)
    {
        picker.Run(() => library.Remember(color));
        if (picker.ErrorMessage is { Length: > 0 } message)
        {
            button.ToolTip = message;
            AutomationProperties.SetHelpText(this, message);
        }
    }
    internal void OpenPicker(bool focus = false)
    {
        if (!IsVisible) return;
        picker.Value = Value; picker.RefreshCollections(); picker.PrepareForShow();
        popupDisplay.IsOpen = true;
        if (focus) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        { if (popupDisplay.IsOpen) picker.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); });
    }
    public void ClosePicker()
    {
        if (pickerSuspensions != 0) return;
        EndOpacityDrag(commit: false);
        CommitPickerDraft();
        popupDisplay.IsOpen = false;
    }
    internal PickerSuspension SuspendPicker()
    {
        pickerSuspensions++;
        var retention = PopupChildDialog.RetainFor(picker);
        var suspension = popupDisplay.Suspend();
        return new PickerSuspension(restore =>
        {
            pickerSuspensions--;
            if (!restore) popupDisplay.IsOpen = false;
            suspension?.Dispose();
            retention?.Dispose();
        });
    }
    internal sealed class PickerSuspension(Action<bool> release) : IDisposable
    {
        private Action<bool>? action = release;
        internal void Complete(bool restore) { var current = action; action = null; current?.Invoke(restore); }
        public void Dispose() => Complete(restore: true);
    }
    public bool TryClosePicker()
    {
        if (EndOpacityDrag(commit: false))
        {
            button.ReleaseMouseCapture();
            return true;
        }
        if (!popupDisplay.IsOpen && !pickerSessionOpen) return false;
        ClosePicker(); if (MayReturnFocus) button.Focus(); return true;
    }
    private void HandlePickerEscape(KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape) return;
        if (NumericValueField.TryCancelForInput(e.OriginalSource as DependencyObject)) { e.Handled = true; return; }
        if (picker.IsInteracting)
        {
            bool cancelledEdit = picker.HasCancellableGesture;
            picker.EndGesture(false);
            if (cancelledEdit) { e.Handled = true; return; }
        }
        if (TryClosePicker()) e.Handled = true;
    }
    private bool MayReturnFocus => SessionWindowCoordinator.Find(Window.GetWindow(this))?.IsDirectTransfer != true;
    private void RefreshValue()
    {
        valueDirty = true;
        if (IsVisible) PrepareForShow();
    }
    internal void PrepareForShow()
    {
        if (!valueDirty) return;
        valueDirty = false;
        uint color = Value;
        swatch.Children.Clear(); swatch.Children.Add(ColorPickerPanel.CreateSwatch(color));
        double alpha = (color >> 24) / 255d;
        double luminance = alpha * (.2126 * ((color >> 16) & 255) + .7152 * ((color >> 8) & 255) + .0722 * (color & 255)) + (1 - alpha) * 220;
        label.Foreground = luminance < 140 ? Brushes.White : Brushes.Black;
        label.Text = IsMixed ? "—" : (color >> 24) == 0 ? L.Get("Color.Transparent") : ColorLibrary.ToHtml(color, (color >> 24) != 255);
        UpdateColorName();
    }

    internal void SetColorLabel(TextBlock label, string resource)
    {
        string caption = L.Get(resource);
        if (AutomationProperties.GetAutomationId(label) == resource && label.Text == caption) return;
        AutomationProperties.SetAutomationId(label, resource);
        label.Text = caption;
        string hint = resource == "Color.BadgeInk" ? L.Get("Color.BadgeInkHint") : caption;
        label.ToolTip = hint;
        AutomationProperties.SetHelpText(button, hint);
        SetColorLabel(caption);
    }

    internal void SetColorLabel(string caption)
    {
        if (colorLabel == caption) return;
        colorLabel = caption;
        if (popupDisplay.Key is WindowPopup window) window.Title = caption;
        UpdateColorName();
    }

    private void UpdateColorName()
    {
        string name = colorLabel + " " + (IsMixed ? L.Get("Feedback4.Inspector.Mixed") : ColorLibrary.ToHtml(Value));
        button.ToolTip = name; AutomationProperties.SetName(button, name); AutomationProperties.SetName(this, name);
    }
    [DllImport("user32.dll")] private static extern int GetMessageTime();
}
