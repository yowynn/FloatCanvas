using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Personal presentation controls. Edits produce owned values and never touch an EditorSession.</summary>
internal sealed class PresentationGlobalSettingsPanel : StackPanel
{
    private sealed record Entry(FrameworkElement Control, Action<object> Display);
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Dictionary<string, FrameworkElement> rows = [];
    private readonly Dictionary<string, PropertyChangeRegion> conditions = [];
    private Panel target;
    internal Action<string>? ReportError { get; set; }
    private readonly TextBlock error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private PresentationGlobalSettings sample = new();
    private bool refreshing;
    private readonly CaptionModeSwitch pointerMaster = new(PointerIcon);
    internal FrameworkElement? HeaderActions { get; }
    internal event Action<PresentationGlobalSettings>? ValueChanged;
    internal event Action<ColorField>? EyedropperRequested;

    internal PresentationGlobalSettingsPanel(bool pointerEffects = true)
    {
        target = this;
        if (!pointerEffects)
        {
            Heading("Presentation.Global.Keyboard");
            Choice("KeyboardScope", "Presentation.Global.KeyboardScope", Enum.GetValues<PresentationKeyboardScope>()
                .Select(value => (L.Get("Presentation.Scope." + value), (object)value)));
            Hint("Presentation.Global.KeyboardHint");
        }
        else
        {
            HeaderActions = pointerMaster;
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(pointerMaster, true);
            pointerMaster.Click += (_, _) => Commit("PointerEnabled", !sample.Pointer.Enabled);
            entries.Add("PointerEnabled", new(pointerMaster, value => pointerMaster.Synchronize((bool)value, IsEnabled,
                L.Get("Presentation.Global.PointerEnabled"), L.Get((bool)value ? "Presentation.Global.PointerOn" : "Presentation.Global.PointerOff"),
                L.Get((bool)value ? "Presentation.Global.SwitchPointerOff" : "Presentation.Global.SwitchPointerOn"))));
            PointerSection("Presentation.Module.Cursor", true);
            Check("CursorEnabled", "Presentation.Module.Cursor");
            Conditional("CursorEnabled", () =>
            {
                Color("CursorColor", "Presentation.Global.CursorColor");
                Number("CursorRadiusPixels", "Presentation.CursorRadius", 2, 128, 2, 64, "px");
            });
            PointerSection("Presentation.Module.Clicks");
            Check("ClicksEnabled", "Presentation.Module.Clicks");
            Conditional("ClicksEnabled", () =>
            {
                Click("LeftClick", "Presentation.Global.LeftClick");
                Click("RightClick", "Presentation.Global.RightClick");
                Click("MiddleClick", "Presentation.Global.MiddleClick");
            });
            PointerSection("Presentation.Module.Trail");
            Check("TrailEnabled", "Presentation.Module.Trail");
            Conditional("TrailEnabled", () =>
            {
                Color("TrailColor", "Presentation.Global.TrailColor");
                Number("TrailWidthPixels", "Presentation.TrailWidth", 1, 64, 1, 20, "px");
                Number("TrailMilliseconds", "Presentation.TrailDuration", 100, 5000, 100, 2000, "ms");
            });
        }
        error.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); Children.Add(error);
        Refresh(new(), false);
    }

    internal void Refresh(PresentationGlobalSettings value, bool canEdit = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var region in conditions.Values) region.Reset();
        sample = value.Clone(); refreshing = true;
        try
        {
            IsEnabled = canEdit;
            foreach (var (key, entry) in entries)
            {
                if ((entry.Control is not CaptionModeSwitch && entry.Control.IsKeyboardFocusWithin) || entry.Control is NumericValueField { IsInteracting: true } ||
                    entry.Control is ColorField { IsInteracting: true }) continue;
                entry.Display(Get(sample, key));
                if (entry.Control is ColorField color) color.PrepareForShow();
            }
            RefreshRows();
        }
        finally { refreshing = false; }
    }

    private void Commit(string key, object value)
    {
        if (refreshing || !IsEnabled || !entries[key].Control.IsEnabled) return;
        var candidate = sample.Clone(); Set(candidate, key, value);
        if (!candidate.IsValid()) { Invalid(); return; }
        void Update()
        {
            sample = candidate; RefreshRows(); error.Visibility = Visibility.Collapsed; error.Text = ""; ReportError?.Invoke("");
            ValueChanged?.Invoke(candidate.Clone());
        }
        string conditionKey = key.EndsWith(".Shape", StringComparison.Ordinal) ? key[..key.IndexOf('.')] + ".Enabled" : key;
        if (conditions.TryGetValue(conditionKey, out var region)) region.Change(Update); else Update();
    }

    private void RefreshRows()
    {
        foreach (var (key, row) in rows)
        {
            row.IsEnabled = FieldActive(key);
            row.Visibility = FieldActive(key) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PointerSection(string key, bool first = false)
    {
        target.Children.Add(ToolOptionsLayout.Section(key, first));
    }

    private static FrameworkElement PointerIcon(bool enabled, string foreground)
    {
        var icon = (Viewbox)CommandGlyphs.Create(enabled ? CommandGlyphs.PresentationPointer :
            "M5,1 L5,13 L8,10 L10,15 L12,14 L10,9 L14,9 Z M1,1 L15,15");
        foreach (var path in ((Canvas)icon.Child).Children.OfType<System.Windows.Shapes.Path>())
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, foreground);
        return icon;
    }

    private bool FieldActive(string key)
    {
        if (key is "KeyboardScope" or "PointerEnabled") return true;
        if (key is "CursorEnabled" or "ClicksEnabled" or "TrailEnabled") return true;
        if (key.StartsWith("Cursor", StringComparison.Ordinal)) return sample.Pointer.CursorEnabled;
        if (key.StartsWith("Trail", StringComparison.Ordinal)) return sample.Pointer.TrailEnabled;
        int separator = key.IndexOf('.');
        var click = ClickSettings(sample.Pointer, key[..separator]);
        return sample.Pointer.ClicksEnabled && (key.EndsWith(".Enabled", StringComparison.Ordinal) || click.Enabled) &&
            (!key.EndsWith(".StrokeWidthPixels", StringComparison.Ordinal) || click.Shape == PresentationClickShape.Ring);
    }

    private void Conditional(string toggleKey, Action build)
    {
        var parent = target; var body = new StackPanel();
        var region = new PropertyChangeRegion(body); conditions.Add(toggleKey, region);
        target = body; build(); target = parent; parent.Children.Add(region);
    }

    private void Click(string prefix, string label)
    {
        Check(prefix + ".Enabled", label);
        Conditional(prefix + ".Enabled", () =>
        {
            Color(prefix + ".Color", "Presentation.Global.ClickColor");
            Choice(prefix + ".Shape", "Presentation.Global.ClickShape", Enum.GetValues<PresentationClickShape>()
                .Select(value => (L.Get("Presentation.Shape." + value), (object)value)));
            Number(prefix + ".RadiusPixels", "Presentation.Global.ClickRadius", 2, 128, 2, 80, "px");
            Number(prefix + ".StrokeWidthPixels", "Presentation.Global.ClickWidth", .5, 32, .5, 12, "px", .5);
            Number(prefix + ".DurationMilliseconds", "Presentation.RippleDuration", 100, 2000, 100, 1000, "ms");
        });
    }

    private void Heading(string key)
    {
        var title = PropertyPanelLayout.SectionTitle(L.Get(key)); title.Margin = new(0, 13, 0, 5); target.Children.Add(title);
    }
    private void Hint(string key)
    {
        var text = new TextBlock { Text = L.Get(key), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 6) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); target.Children.Add(text);
    }
    private void Check(string key, string labelKey)
    {
        var control = new CheckBox();
        control.Click += (_, _) => Commit(key, control.IsChecked == true);
        var row = PropertyPanelLayout.BooleanRow(L.Get(labelKey), control);
        Add(key, control, row, value => control.IsChecked = (bool)value);
    }
    private void Choice(string key, string labelKey, IEnumerable<(string Label, object Value)> options)
    {
        var control = new SegmentedChoice();
        foreach (var (label, value) in options)
            control.Add(label, value, ChoiceIcon(value));
        control.SelectionChanged += (_, _) => { if (control.SelectedValue is { } value) Commit(key, value); };
        AutomationProperties.SetName(control, L.Get(labelKey));
        Add(key, control, Row(labelKey, control), value => control.SelectedValue = value);
    }
    private static FrameworkElement ChoiceIcon(object value)
    {
        if (value is PresentationClickShape shape)
        {
            var stage = new Canvas { Width = 18, Height = 18 };
            var disc = new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, StrokeThickness = 1.5 };
            disc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText");
            if (shape == PresentationClickShape.Disc) disc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "CanvasText");
            Canvas.SetLeft(disc, 2.5); Canvas.SetTop(disc, 2.5); stage.Children.Add(disc); return stage;
        }
        return CommandGlyphs.Create(value is PresentationKeyboardScope.OutsideFloatCanvas ? CommandGlyphs.Exit :
            value is PresentationKeyboardScope.FloatCanvasOnly ? CommandGlyphs.FloatingLogo : CommandGlyphs.Windows);
    }
    private void Color(string key, string labelKey)
    {
        var control = new ColorField(ColorRole.Foreground) { CanBeginGesture = () => IsEnabled };
        control.ValueChanged += value => Commit(key, value);
        control.EyedropperRequested += () => EyedropperRequested?.Invoke(control);
        AutomationProperties.SetName(control, L.Get(labelKey));
        Add(key, control, Row(labelKey, control), value => control.Value = (uint)value);
    }
    private void Number(string key, string labelKey, double min, double max, double railMin, double railMax, string unit, double step = 1)
    {
        var input = new TextBox(); AutomationProperties.SetName(input, L.Get(labelKey));
        var control = new NumericValueField(input, min, max, railMin, railMax, step) { Unit = unit, KeyboardStep = NumericKeyboardSteps.ForProperty(key), CanBeginGesture = () => IsEnabled };
        var kind = key is "CursorRadiusPixels" || key.EndsWith(".RadiusPixels", StringComparison.Ordinal) ? NumericPropertyKind.Length :
            key is "TrailWidthPixels" || key.EndsWith(".StrokeWidthPixels", StringComparison.Ordinal) ? NumericPropertyKind.StrokeWidth : NumericPropertyKind.Number;
        control.SetScrubAnchor(kind, L.Get(labelKey));
        AutomationProperties.SetName(control, L.Get(labelKey));
        control.ValueChanged += value => Commit(key, value); control.InvalidInput += Invalid;
        Add(key, control, Row(labelKey, control), value => control.Value = (double)value);
    }
    private static FrameworkElement Row(string labelKey, FrameworkElement control)
        => PropertyPanelLayout.Row(new PropertyLabel(L.Get(labelKey)), control);
    private void Add(string key, FrameworkElement control, FrameworkElement row, Action<object> display)
    {
        rows.Add(key, row); entries.Add(key, new(control, display)); target.Children.Add(row);
    }
    private IEnumerable<ColorField> Colors => entries.Values.Select(entry => entry.Control).OfType<ColorField>();
    internal bool IsColorPickerOpen => Colors.Any(color => color.IsPickerOpen);
    internal bool TryClosePicker() => Colors.Any(field => field.TryClosePicker());
    internal void ClosePickers() { foreach (var field in Colors) field.ClosePicker(); }
    internal bool OwnsColorPickerInput(DependencyObject? source) => Colors.Any(field => field.OwnsPickerInput(source));
    private void Invalid()
    { if (!refreshing) { if (ReportError is { } report) report(L.Get("Presentation.InvalidValue")); else { error.Text = L.Get("Presentation.InvalidValue"); error.Visibility = Visibility.Visible; } } }

    private static PresentationClickStyle ClickSettings(PresentationPointerSettings value, string prefix) => prefix switch
    {
        "LeftClick" => value.LeftClick, "RightClick" => value.RightClick, "MiddleClick" => value.MiddleClick,
        _ => throw new ArgumentOutOfRangeException(nameof(prefix))
    };
    private static object Get(PresentationGlobalSettings value, string key)
    {
        var pointer = value.Pointer; int separator = key.IndexOf('.');
        if (separator >= 0)
        {
            var click = ClickSettings(pointer, key[..separator]);
            return key[(separator + 1)..] switch
            {
                "Enabled" => click.Enabled, "Color" => click.Color, "Shape" => click.Shape,
                "RadiusPixels" => click.RadiusPixels, "StrokeWidthPixels" => click.StrokeWidthPixels,
                "DurationMilliseconds" => click.DurationMilliseconds, "Opacity" => click.Opacity * 100,
                _ => throw new ArgumentOutOfRangeException(nameof(key))
            };
        }
        return key switch
        {
            "KeyboardScope" => value.KeyboardScope,
            "PointerEnabled" => pointer.Enabled,
            "CursorEnabled" => pointer.CursorEnabled, "ClicksEnabled" => pointer.ClicksEnabled, "TrailEnabled" => pointer.TrailEnabled,
            "CursorColor" => pointer.CursorColor, "CursorRadiusPixels" => pointer.CursorRadiusPixels, "CursorOpacity" => pointer.CursorOpacity * 100,
            "TrailColor" => pointer.TrailColor, "TrailWidthPixels" => pointer.TrailWidthPixels,
            "TrailMilliseconds" => pointer.TrailMilliseconds, "TrailOpacity" => pointer.TrailOpacity * 100,
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
    }
    private static void Set(PresentationGlobalSettings value, string key, object next)
    {
        var pointer = value.Pointer; int separator = key.IndexOf('.');
        if (separator >= 0)
        {
            var click = ClickSettings(pointer, key[..separator]);
            switch (key[(separator + 1)..])
            {
                case "Enabled": click.Enabled = (bool)next; break;
                case "Color": click.Color = (uint)next; break;
                case "Shape": click.Shape = (PresentationClickShape)next; break;
                case "RadiusPixels": click.RadiusPixels = (double)next; break;
                case "StrokeWidthPixels": click.StrokeWidthPixels = (double)next; break;
                case "DurationMilliseconds": click.DurationMilliseconds = (double)next; break;
                case "Opacity": click.Opacity = (double)next / 100; break;
                default: throw new ArgumentOutOfRangeException(nameof(key));
            }
            return;
        }
        switch (key)
        {
            case "KeyboardScope": value.KeyboardScope = (PresentationKeyboardScope)next; break;
            case "PointerEnabled": pointer.Enabled = (bool)next; break;
            case "CursorEnabled": pointer.CursorEnabled = (bool)next; break;
            case "ClicksEnabled": pointer.ClicksEnabled = (bool)next; break;
            case "TrailEnabled": pointer.TrailEnabled = (bool)next; break;
            case "CursorColor": pointer.CursorColor = (uint)next; break;
            case "CursorRadiusPixels": pointer.CursorRadiusPixels = (double)next; break;
            case "CursorOpacity": pointer.CursorOpacity = (double)next / 100; break;
            case "TrailColor": pointer.TrailColor = (uint)next; break;
            case "TrailWidthPixels": pointer.TrailWidthPixels = (double)next; break;
            case "TrailMilliseconds": pointer.TrailMilliseconds = (double)next; break;
            case "TrailOpacity": pointer.TrailOpacity = (double)next / 100; break;
            default: throw new ArgumentOutOfRangeException(nameof(key));
        }
    }
}
