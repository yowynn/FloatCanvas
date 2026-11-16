using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Authored keyboard display fields. UI mode choices map to the existing three display styles.</summary>
internal sealed class PresentationSettingsPanel : StackPanel
{
    private sealed record Entry(FrameworkElement Control, Action<object?> Display);
    private sealed record RestoredLogStyles(IReadOnlyDictionary<Guid, PresentationDisplayStyle> Values);
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Dictionary<string, FrameworkElement> rows = [];
    private readonly Dictionary<string, object?> editedValues = [];
    private readonly Dictionary<Guid, PresentationDisplayStyle> rememberedLogStyles = [];
    private readonly SegmentedChoice displayMode = new(), logStyle = new();
    private readonly StackPanel recordOptions = new(), keyboardOptions = new();
    private readonly PropertyChangeRegion modeOptions;
    internal Action<string>? ReportError { get; set; }
    private readonly TextBlock error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private PresentationSettings sample = new();
    private Guid[] sourceIds = [];
    private PresentationDisplayStyle? shownMode;
    private object? context;
    private bool refreshing;
    internal event Action<string>? ValueChanged;
    internal event Action? GestureStarted;
    internal event Action<bool>? GestureCompleted;
    internal Func<bool>? CanBeginGesture { get; set; }
    internal bool IsInteracting => entries.Values.Select(entry => entry.Control).OfType<NumericValueField>().Any(numeric => numeric.IsInteracting);

    internal PresentationSettingsPanel()
    {
        displayMode.Add(L.Get("Presentation.Mode.Log"), PresentationDisplayStyle.Log, StyleIcon(PresentationDisplayStyle.Log));
        displayMode.Add(L.Get("Presentation.Mode.Keyboard"), PresentationDisplayStyle.Keyboard, StyleIcon(PresentationDisplayStyle.Keyboard));
        Add(this, "DisplayStyle", "Presentation.Mode.Label", displayMode, _ => { });
        logStyle.Add(L.Get("Presentation.LogStyle.Log"), PresentationDisplayStyle.Log, StyleIcon(PresentationDisplayStyle.Log));
        logStyle.Add(L.Get("Presentation.LogStyle.Keycaps"), PresentationDisplayStyle.Keycaps, StyleIcon(PresentationDisplayStyle.Keycaps));
        Add(recordOptions, "LogStyle", "Presentation.LogStyle.Label", logStyle, _ => { });
        Choice(recordOptions, "KeyDisplay", "Presentation.KeyDisplay", Enum.GetValues<PresentationKeyDisplayMode>().Select(value => (L.Get("Presentation.KeyDisplay." + value), (object)value)));
        Number(recordOptions, "RecentChordLimit", "Presentation.RecentChordLimit", 1, PresentationLimits.MaximumChords, 1, PresentationLimits.MaximumChords, integer: true);
        Number(recordOptions, "KeyHoldMilliseconds", "Presentation.KeyHold", 100, 5000, 100, 2000, "ms");
        var layout = new Button { Content = L.Get("Presentation.Layout.Edit"), MinHeight = PropertyPanelLayout.ControlHeight };
        AutomationProperties.SetName(layout, L.Get("Presentation.Layout.Edit")); layout.Click += (_, _) => OpenLayoutEditor();
        Add(keyboardOptions, "KeyboardLayout", "Presentation.Layout.Label", layout, _ => { });
        var options = new StackPanel(); options.Children.Add(recordOptions); options.Children.Add(keyboardOptions);
        modeOptions = new(options); Children.Add(modeOptions);
        Choice(this, "KeyboardScope", "Presentation.KeyboardScope", Enum.GetValues<PresentationKeyboardScope>().Select(value => (L.Get("Presentation.Scope." + value), (object)value)));
        Choice(this, "KeyPanelAlignment", "Presentation.Alignment", Enum.GetValues<PresentationPanelAlignment>().Select(value => (L.Get("Presentation.Alignment." + value), (object)value)));
        var hint = new TextBlock { Text = L.Get("Presentation.RegionHint"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 4) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); Children.Add(hint);
        error.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); Children.Add(error);
        displayMode.SelectionChanged += (_, _) =>
        {
            if (refreshing || displayMode.SelectedValue is not PresentationDisplayStyle mode) return;
            object value = mode == PresentationDisplayStyle.Keyboard ? mode : new RestoredLogStyles(sourceIds.ToDictionary(id => id, id => rememberedLogStyles.GetValueOrDefault(id, PresentationDisplayStyle.Log)));
            modeOptions.Change(() => Commit("DisplayStyle", value));
        };
        logStyle.SelectionChanged += (_, _) =>
        {
            if (refreshing || shownMode != PresentationDisplayStyle.Log || logStyle.SelectedValue is not PresentationDisplayStyle style) return;
            foreach (var id in sourceIds) rememberedLogStyles[id] = style;
            Commit("DisplayStyle", style);
        };
    }

    private static FrameworkElement StyleIcon(PresentationDisplayStyle style)
    {
        var parameters = new ToolParameters { Tool = EditorTool.Presentation, Presentation = new() { DisplayStyle = style } };
        parameters.Style.Foreground = 0xff4285d4; parameters.Style.Background = 0x224285d4;
        return (FrameworkElement)ToolParameterIcon.Create(parameters, 18);
    }
    internal void Refresh(IEnumerable<(Guid Id, PresentationSettings Value)> values, object modelContext, bool canEdit = true)
    {
        var sources = values.ToArray(); if (sources.Length == 0) return;
        bool changedContext = !Equals(context, modelContext);
        if (changedContext) { CancelGestures(); rememberedLogStyles.Clear(); }
        context = modelContext; sourceIds = sources.Select(source => source.Id).ToArray();
        var selectedIds = sourceIds.ToHashSet(); foreach (var id in rememberedLogStyles.Keys.Where(id => !selectedIds.Contains(id)).ToArray()) rememberedLogStyles.Remove(id);
        foreach (var source in sources)
            if (source.Value.DisplayStyle != PresentationDisplayStyle.Keyboard) rememberedLogStyles[source.Id] = source.Value.DisplayStyle;
            else if (!rememberedLogStyles.ContainsKey(source.Id)) rememberedLogStyles[source.Id] = PresentationDisplayStyle.Log;
        var settings = sources.Select(source => source.Value).ToArray(); refreshing = true;
        try
        {
            sample = settings[0].Clone(); editedValues.Clear(); IsEnabled = canEdit;
            if (changedContext) { error.Text = ""; error.Visibility = Visibility.Collapsed; modeOptions.Reset(); }
            foreach (var (key, entry) in entries)
            {
                if (key is "DisplayStyle" or "LogStyle") continue;
                if (!changedContext && (entry.Control.IsKeyboardFocusWithin || entry.Control is NumericValueField { IsInteracting: true })) continue;
                object? value = Get(settings[0], key);
                entry.Display(settings.Skip(1).Any(setting => !Equals(Get(setting, key), value)) ? null : value);
            }
            SetChoices(settings.Select(setting => setting.DisplayStyle).ToArray());
        }
        finally { refreshing = false; }
    }
    private void SetChoices(PresentationDisplayStyle[] styles)
    {
        bool previous = refreshing; refreshing = true;
        try
        {
            var modes = styles.Select(style => style == PresentationDisplayStyle.Keyboard ? PresentationDisplayStyle.Keyboard : PresentationDisplayStyle.Log).Distinct().ToArray();
            shownMode = modes.Length == 1 ? modes[0] : null; displayMode.SelectedValue = shownMode;
            var recalled = sourceIds.Select(id => rememberedLogStyles.GetValueOrDefault(id, PresentationDisplayStyle.Log)).Distinct().ToArray();
            logStyle.SelectedValue = recalled.Length == 1 ? recalled[0] : null;
            recordOptions.Visibility = shownMode == PresentationDisplayStyle.Log ? Visibility.Visible : Visibility.Collapsed;
            keyboardOptions.Visibility = shownMode == PresentationDisplayStyle.Keyboard ? Visibility.Visible : Visibility.Collapsed;
            rows["KeyPanelAlignment"].Visibility = shownMode == PresentationDisplayStyle.Keyboard ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { refreshing = previous; }
    }
    internal void ApplyEdit(PresentationSettings target, string key, Guid sourceId = default)
    { if (editedValues.TryGetValue(key, out var value)) Set(target, key, value, sourceId); }
    internal bool CancelGestures()
    { bool active = IsInteracting; foreach (var field in entries.Values.Select(entry => entry.Control).OfType<NumericValueField>()) field.CancelGesture(); return active; }
    private void Commit(string key, object? value)
    {
        if (refreshing || !IsEnabled || sourceIds.Length == 0) return;
        var candidate = sample.Clone(); Set(candidate, key, value, sourceIds.FirstOrDefault());
        if (!candidate.IsValid()) { Invalid(); return; }
        sample = candidate; editedValues[key] = value; error.Text = ""; error.Visibility = Visibility.Collapsed; ReportError?.Invoke("");
        if (key == "DisplayStyle")
        {
            var styles = value is RestoredLogStyles recalled ? sourceIds.Select(id => recalled.Values.GetValueOrDefault(id, PresentationDisplayStyle.Log)).ToArray() : sourceIds.Select(_ => (PresentationDisplayStyle)value!).ToArray();
            foreach (var (id, style) in sourceIds.Zip(styles)) if (style != PresentationDisplayStyle.Keyboard) rememberedLogStyles[id] = style;
            SetChoices(styles);
        }
        ValueChanged?.Invoke(key);
    }
    private void Invalid() { if (!refreshing) { if (ReportError is { } report) report(L.Get("Presentation.InvalidValue")); else { error.Text = L.Get("Presentation.InvalidValue"); error.Visibility = Visibility.Visible; } } }
    private void OpenLayoutEditor()
    {
        if (!IsEnabled || CanBeginGesture?.Invoke() == false) return;
        var expectedContext = context;
        var window = new KeyboardLayoutEditorWindow(sample.KeyboardLayout, Window.GetWindow(this));
        if (SessionWindowCoordinator.ShowModal(window) != true || !IsEnabled || !Equals(context, expectedContext)) return;
        Commit("KeyboardLayout", window.ResultLayout); GestureCompleted?.Invoke(true);
    }
    private void Choice(Panel parent, string key, string labelKey, IEnumerable<(string Label, object Value)> options)
    {
        var values = options.ToArray();
        if (values.Length <= 5)
        {
            var choice = new SegmentedChoice();
            foreach (var (label, value) in values)
                choice.Add(label, value, CommandGlyphs.Create(value switch
                {
                    PresentationKeyboardScope.AllApplications => "M3,2 H6 Q7,2 7,3 V6 Q7,7 6,7 H3 Q2,7 2,6 V3 Q2,2 3,2 Z M11,2 H14 Q15,2 15,3 V6 Q15,7 14,7 H11 Q10,7 10,6 V3 Q10,2 11,2 Z M3,10 H6 Q7,10 7,11 V14 Q7,15 6,15 H3 Q2,15 2,14 V11 Q2,10 3,10 Z M11,10 H14 Q15,10 15,11 V14 Q15,15 14,15 H11 Q10,15 10,14 V11 Q10,10 11,10 Z",
                    PresentationKeyboardScope.OutsideFloatCanvas => "M7,2 H3 Q2,2 2,3 V13 Q2,14 3,14 H7 M5,8 H15 M11,4 L15,8 L11,12",
                    PresentationKeyboardScope.FloatCanvasOnly => "M2,4 L4,12 H12 L14,4 L10,7 L8,2 L6,7 Z M4,14 H12",
                    PresentationKeyDisplayMode.ShortcutChords => CommandGlyphs.Shortcuts,
                    _ => CommandGlyphs.Presentation
                }));
            choice.SelectionChanged += (_, _) => { if (choice.SelectedValue is { } value) Commit(key, value); };
            Add(parent, key, labelKey, choice, value => choice.SelectedValue = value); return;
        }
        var control = new ComboBox { SelectedValuePath = "Tag", MinHeight = PropertyPanelLayout.ControlHeight };
        foreach (var (label, value) in values) control.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        control.SelectionChanged += (_, _) => { if (control.SelectedValue is { } value) Commit(key, value); };
        Add(parent, key, labelKey, control, value => control.SelectedValue = value);
    }
    private void Number(Panel parent, string key, string labelKey, double min, double max, double railMin, double railMax, string unit = "", bool integer = false)
    {
        var input = new TextBox(); AutomationProperties.SetName(input, L.Get(labelKey));
        var control = new NumericValueField(input, min, max, railMin, railMax, integerOnly: integer) { Unit = unit, KeyboardStep = NumericKeyboardSteps.ForProperty(key), CanBeginGesture = () => IsEnabled && CanBeginGesture?.Invoke() != false };
        control.SetScrubAnchor(key == "KeyPanelMaximumWidth" ? NumericPropertyKind.Width : NumericPropertyKind.Number, L.Get(labelKey));
        control.ValueChanged += value => Commit(key, integer ? (object)(int)value : value); control.InvalidInput += Invalid;
        control.GestureStarted += () => GestureStarted?.Invoke(); control.GestureCompleted += value => GestureCompleted?.Invoke(value);
        Add(parent, key, labelKey, control, value => { if (value is null) control.ShowPlaceholder("—"); else control.Value = Convert.ToDouble(value); });
    }
    private void Add(Panel parent, string key, string labelKey, FrameworkElement control, Action<object?> display)
    {
        AutomationProperties.SetName(control, L.Get(labelKey)); entries.Add(key, new(control, display));
        var row = PropertyPanelLayout.Row(new PropertyLabel(L.Get(labelKey)), control);
        rows.Add(key, row); parent.Children.Add(row);
    }
    private static object? Get(PresentationSettings value, string key) => key switch
    {
        "KeyboardLayout" => value.KeyboardLayout, "KeyDisplay" => value.KeyDisplay, "KeyboardScope" => value.KeyboardScope, "KeyPanelAlignment" => value.KeyPanelAlignment,
        "RecentChordLimit" => value.RecentChordLimit, "KeyHoldMilliseconds" => value.KeyHoldMilliseconds, "KeyPanelMaximumWidth" => value.KeyPanelMaximumWidth,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
    private static void Set(PresentationSettings target, string key, object? value, Guid sourceId)
    {
        switch (key)
        {
            case "DisplayStyle": target.DisplayStyle = value is RestoredLogStyles restored ? restored.Values.GetValueOrDefault(sourceId, PresentationDisplayStyle.Log) : (PresentationDisplayStyle)value!; break;
            case "KeyboardLayout": target.KeyboardLayout = (value as PresentationKeyboardLayout)?.Clone(); break;
            case "KeyDisplay": target.KeyDisplay = (PresentationKeyDisplayMode)value!; break;
            case "KeyboardScope": target.KeyboardScope = (PresentationKeyboardScope)value!; break;
            case "KeyPanelAlignment": target.KeyPanelAlignment = (PresentationPanelAlignment)value!; break;
            case "RecentChordLimit": target.RecentChordLimit = (int)value!; break;
            case "KeyHoldMilliseconds": target.KeyHoldMilliseconds = (double)value!; break;
            case "KeyPanelMaximumWidth": target.KeyPanelMaximumWidth = (double)value!; break;
            default: throw new ArgumentOutOfRangeException(nameof(key));
        }
    }
}
