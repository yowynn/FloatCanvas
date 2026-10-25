using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>An icon drag anchor, text entry and a common-range rail share one frame.</summary>
internal sealed class NumericValueField : UserControl
{
    private readonly double minimum, maximum, step;
    private readonly bool integerOnly;
    private readonly List<NumericScrubber> scrubbers = [];
    private NumericPropertyKind anchorKind;
    internal Border DragAnchor { get; }
    internal NumericScrubber? AnchorScrubber { get; }
    private readonly TextBlock outside = new() { FontSize = 11, IsHitTestVisible = false, Margin = new(4, 0, 0, 0) };
    private readonly TextBlock suffix = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
        Width = 26, TextAlignment = TextAlignment.Center, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private bool synchronizing, invalidDraft, hasPlaceholder, railGesture, endingGesture;
    private double value, gestureStart;
    private Window? owner;
    internal TextBox Input { get; }
    internal Slider Slider { get; }
    internal string Unit
    {
        get => suffix.Text;
        set
        {
            suffix.Text = value; suffix.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            Input.Padding = new(7, 0, string.IsNullOrEmpty(value) ? 7 : 3, 0);
            AutomationProperties.SetHelpText(Input, value);
            RefreshRange(this.value);
        }
    }
    internal bool IsInteracting { get; private set; }
    internal Func<bool>? CanBeginGesture { get; set; }
    internal event Action<double>? ValueChanged;
    internal event Action? InvalidInput;
    internal event Action? SliderValueCommitted;
    internal event Action? GestureStarted;
    internal event Action<bool>? GestureCompleted;

    internal NumericValueField(double minimum, double maximum, double? sliderMinimum = null, double? sliderMaximum = null, double step = 1, bool integerOnly = false)
        : this(new TextBox(), minimum, maximum, sliderMinimum, sliderMaximum, step, integerOnly) { }

    internal NumericValueField(TextBox input, double minimum, double maximum, double? sliderMinimum = null, double? sliderMaximum = null, double step = 1, bool integerOnly = false, bool customAnchorGesture = false)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum || step <= 0 || !double.IsFinite(step)) throw new ArgumentOutOfRangeException(nameof(minimum));
        this.minimum = minimum; this.maximum = maximum; this.step = step; this.integerOnly = integerOnly; Input = input;
        Focusable = false; Height = MinHeight = PropertyPanelLayout.ControlHeight;
        Margin = Input.Margin; Input.Margin = new(0); Input.MinHeight = 0;
        Input.Padding = new(7, 0, 7, 0); Input.BorderThickness = new(0);
        Input.Background = Brushes.Transparent; Input.VerticalContentAlignment = VerticalAlignment.Center;
        Input.TextAlignment = TextAlignment.Right;
        if (OverlayWindowScope.Current is not null) InputMethod.SetIsInputMethodEnabled(Input, false);
        Slider = new Slider { Minimum = Math.Clamp(sliderMinimum ?? minimum, minimum, maximum),
            Maximum = Math.Clamp(sliderMaximum ?? maximum, minimum, maximum), SmallChange = step, LargeChange = step * 10,
            // We own click-to-position and drag as one transaction. WPF's
            // move-to-point class handler otherwise consumes presses on empty
            // track before our preview handler can capture the gesture.
            TickFrequency = step, IsSnapToTickEnabled = integerOnly, IsMoveToPointEnabled = false, MinHeight = 0, Margin = new(6, 0, 6, 0) };
        if (Slider.Maximum < Slider.Minimum) throw new ArgumentOutOfRangeException(nameof(sliderMaximum));
        Slider.SetResourceReference(StyleProperty, "CanvasNumericSlider");
        string label = AutomationProperties.GetName(Input);
        if (!string.IsNullOrWhiteSpace(label)) AutomationProperties.SetName(Slider, label);
        var panel = new Grid(); panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = new(12) });
        var editor = new Grid(); editor.ColumnDefinitions.Add(new() { Width = new(24) }); editor.ColumnDefinitions.Add(new()); editor.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        DragAnchor = new Border { Width = 24, Background = Brushes.Transparent, CornerRadius = new(5, 0, 0, 0) };
        var anchorStyle = new Style(typeof(Border));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CanvasHover"))); anchorStyle.Triggers.Add(hover);
        DragAnchor.Style = anchorStyle;
        SetScrubAnchor(NumericPropertyKind.Number, string.IsNullOrWhiteSpace(label) ? L.Get("Numeric.Value") : label);
        if (!customAnchorGesture) { AnchorScrubber = new NumericScrubber(DragAnchor, BeginGesture, delta => Publish(gestureStart + delta * step), EndGesture); scrubbers.Add(AnchorScrubber); }
        suffix.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        editor.Children.Add(DragAnchor); Grid.SetColumn(Input, 1); editor.Children.Add(Input); Grid.SetColumn(suffix, 2); editor.Children.Add(suffix);
        panel.Children.Add(editor); Grid.SetRow(Slider, 1); panel.Children.Add(Slider);
        outside.SetResourceReference(TextBlock.ForegroundProperty, "CanvasAccent"); Grid.SetRow(outside, 1); outside.FontSize = 9; outside.Margin = new(0); panel.Children.Add(outside);
        var frame = new Border { Child = panel };
        frame.SetResourceReference(StyleProperty, "CanvasNumericFrame"); Content = frame;
        Input.TextChanged += (_, _) =>
        {
            if (synchronizing) return;
            hasPlaceholder = false;
            if (!TryGetValue(out double next)) { invalidDraft = true; InvalidInput?.Invoke(); return; }
            bool corrected = invalidDraft; invalidDraft = false;
            synchronizing = true;
            try { Slider.Value = Math.Clamp(next, Slider.Minimum, Slider.Maximum); }
            finally { synchronizing = false; }
            RefreshRange(next);
            if (value == next && !corrected) return;
            value = next; ValueChanged?.Invoke(next);
        };
        Slider.ValueChanged += (_, _) =>
        {
            if (synchronizing) return;
            Publish(Slider.Value);
            if (!IsInteracting) SliderValueCommitted?.Invoke();
        };
        Slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            Slider.Focus();
            if (!BeginGesture()) { e.Handled = true; return; }
            railGesture = true;
            if (!Slider.CaptureMouse()) EndGesture(false);
            else UpdateRail(e.GetPosition(Slider).X);
            e.Handled = true;
        };
        Slider.PreviewMouseMove += (_, e) =>
        {
            if (!railGesture) return;
            if (e.LeftButton != MouseButtonState.Pressed) EndGesture(false);
            else UpdateRail(e.GetPosition(Slider).X);
            e.Handled = true;
        };
        Slider.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!railGesture) return;
            UpdateRail(e.GetPosition(Slider).X); EndGesture(true); e.Handled = true;
        };
        Slider.LostMouseCapture += (_, _) => { if (railGesture) EndGesture(false); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && CancelGesture()) e.Handled = true; };
        Unloaded += (_, _) => CancelGesture();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelGesture(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelGesture(); };
        value = TryGetValue(out double initial) ? initial : minimum;
        synchronizing = true;
        try { Slider.Value = Math.Clamp(value, Slider.Minimum, Slider.Maximum); }
        finally { synchronizing = false; }
        RefreshRange(value);
    }

    internal void SetLabel(FrameworkElement label)
    {
        if (label is TextBlock caption) SetScrubAnchor(anchorKind, caption.Text);
        AutomationProperties.SetLabeledBy(Input, label); AutomationProperties.SetLabeledBy(Slider, label);
    }

    // Mixed selections and automatic values are valid states, unlike an invalid
    // typed draft. Retain the representative value as their drag starting point.
    internal void ShowPlaceholder(string text)
    {
        synchronizing = true;
        try { Input.Text = text; invalidDraft = false; hasPlaceholder = true; }
        finally { synchronizing = false; }
    }

    internal void SetScrubAnchor(NumericPropertyKind kind, string name)
    {
        anchorKind = kind;
        DragAnchor.Child = new NumericPropertyIcon(kind);
        DragAnchor.ToolTip = name + "\n" + L.Get("Inspector.DragNumeric");
        AutomationProperties.SetName(DragAnchor, name);
        AutomationProperties.SetHelpText(DragAnchor, L.Get("Inspector.DragNumeric"));
        ToolTipService.SetInitialShowDelay(DragAnchor, 650);
    }

    // The same lifecycle is used by embedded icons, rails, and detached checks.
    internal bool BeginGesture()
    {
        if (IsInteracting || !IsEnabled || Visibility != Visibility.Visible || Input.IsReadOnly || CanBeginGesture?.Invoke() == false) return false;
        if (!TryGetValue(out gestureStart))
        {
            if (!hasPlaceholder) return false;
            gestureStart = value;
        }
        IsInteracting = true; owner = Window.GetWindow(this);
        if (owner is not null) owner.Deactivated += OnDeactivated;
        GestureStarted?.Invoke(); return true;
    }

    internal void UpdateRail(double x)
    {
        if (!IsInteracting || !double.IsFinite(x)) return;
        double fraction = Math.Clamp((x - 4) / Math.Max(1, Slider.ActualWidth - 8), 0, 1);
        Publish(Slider.Minimum + (Slider.Maximum - Slider.Minimum) * fraction);
    }

    internal void EndGesture(bool commit)
    {
        if (!IsInteracting || endingGesture) return;
        endingGesture = true;
        railGesture = false;
        if (owner is not null) owner.Deactivated -= OnDeactivated;
        owner = null;
        try { if (!commit) Publish(gestureStart); }
        finally
        {
            IsInteracting = false;
            if (Slider.IsMouseCaptured) Slider.ReleaseMouseCapture();
            endingGesture = false;
        }
        GestureCompleted?.Invoke(commit);
        if (commit && value != gestureStart) SliderValueCommitted?.Invoke();
    }

    internal bool CancelGesture()
    {
        bool active = IsInteracting;
        foreach (var scrubber in scrubbers) scrubber.End(false);
        EndGesture(false); return active;
    }

    internal static bool TryCancelForInput(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is NumericValueField field && field.CancelGesture()) return true;
        return false;
    }

    private void OnDeactivated(object? sender, EventArgs e) => CancelGesture();

    private void Publish(double next)
    {
        if (!double.IsFinite(next)) return;
        next = Math.Clamp(Math.Round(Math.Clamp(next, minimum, maximum), integerOnly || Slider.IsSnapToTickEnabled ? 0 : 3), minimum, maximum);
        bool changed = value != next || invalidDraft || hasPlaceholder;
        invalidDraft = hasPlaceholder = false; value = next;
        synchronizing = true;
        try { Slider.Value = Math.Clamp(next, Slider.Minimum, Slider.Maximum); Input.Text = Format(next); }
        finally { synchronizing = false; }
        RefreshRange(next);
        if (changed) ValueChanged?.Invoke(next);
    }

    private void RefreshRange(double next)
    {
        outside.Text = next < Slider.Minimum ? "‹" : next > Slider.Maximum ? "›" : "";
        Slider.ToolTip = $"{Format(Slider.Minimum)} – {Format(Slider.Maximum)}{(Unit.Length == 0 ? "" : " " + Unit)}";
        AutomationProperties.SetItemStatus(Slider, outside.Text.Length == 0 ? "" : Format(next));
    }

    private static string Format(double next) => next.ToString(next != 0 && Math.Abs(next) < .001 ? "G" : "0.###", CultureInfo.CurrentCulture);

    internal double Value
    {
        get => value;
        set
        {
            if (!double.IsFinite(value) || value < minimum || value > maximum || (integerOnly && value != Math.Truncate(value))) throw new ArgumentOutOfRangeException(nameof(value));
            synchronizing = true;
            try { invalidDraft = hasPlaceholder = false; this.value = value; Input.Text = Format(value); Slider.Value = Math.Clamp(value, Slider.Minimum, Slider.Maximum); }
            finally { synchronizing = false; }
            RefreshRange(value);
        }
    }

    internal bool TryGetValue(out double result) =>
        (double.TryParse(Input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out result) || double.TryParse(Input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) &&
        double.IsFinite(result) && result >= minimum && result <= maximum && (!integerOnly || result == Math.Truncate(result));
}
