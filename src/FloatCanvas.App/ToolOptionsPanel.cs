using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Input = System.Windows.Input;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Tool state and reusable recipes. Editing parameters never edits the current selection.</summary>
internal sealed class ToolOptionsPanel : UserControl
{
    internal const double PreferredWidth = 560;
    private readonly EditorController controller;
    private readonly Dictionary<string, TextBox> numbers = [];
    private readonly Dictionary<string, NumericValueField> numericFields = [];
    private readonly Dictionary<string, ComboBox> pickers = [];
    private readonly Dictionary<string, FrameworkElement> rows = [];
    private readonly ColorField foreground, background;
    private readonly CheckBox aspect = new() { Content = L.Get("Inspector.KeepAspect"), MinHeight = 30 };
    private readonly ComboBox fontFamily = ToolOptionPreviews.FontPicker();
    private readonly Grid parameterGrid = new();
    private readonly ToolPresetGallery presets;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock heading = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly TextBlock noParameters = new() { Text = L.Get("ToolOptions.NoParameters"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly UniformGrid symbolGallery = new() { Columns = 6 };
    private readonly UniformGrid emojiGallery = new() { Columns = 6 };
    private bool refreshing;

    public event Action? ParametersChanged;
    public event Action<ColorField>? EyedropperRequested;

    public ToolOptionsPanel(EditorController controller, ToolPresetLibrary library, ColorLibrary colors, ToolStateStore? stateStore = null)
    {
        this.controller = controller;
        Width = PreferredWidth; MinWidth = 480; MaxWidth = PreferredWidth; MaxHeight = 620;
        SetResourceReference(BackgroundProperty, "CanvasSurface"); SetResourceReference(ForegroundProperty, "CanvasText");
        var body = new StackPanel { Margin = new(12) }; body.Children.Add(heading);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        parameterGrid.ColumnDefinitions.Add(new()); parameterGrid.ColumnDefinitions.Add(new());
        var parameters = new StackPanel(); parameters.Children.Add(noParameters); parameters.Children.Add(parameterGrid);
        columns.Children.Add(parameters);
        var recipes = new Grid { Margin = new(12, 0, 0, 0) };
        var recipeBorder = new Border { BorderThickness = new(1, 0, 0, 0), Margin = new(12, 0, 0, 0), Child = recipes };
        recipeBorder.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        Grid.SetColumn(recipeBorder, 1); columns.Children.Add(recipeBorder); body.Children.Add(columns);
        presets = new ToolPresetGallery(controller, library, stateStore);
        presets.Applied += () => { Refresh(); ParametersChanged?.Invoke(); };
        presets.Failed += Error; recipes.Children.Add(presets);
        foreground = new(ColorRole.Foreground, colors); background = new(ColorRole.Background, colors);
        AddRow(parameterGrid, "Foreground", L.Get("Color.Foreground"), foreground); AddRow(parameterGrid, "Background", L.Get("Color.Background"), background);
        foreground.ValueChanged += value => Change(p => { if (p.Tool == EditorTool.CaptureRegion) p.CaptureBorderColor = value; else p.Style.Stroke = value; });
        background.ValueChanged += value => Change(p => p.Style.Fill = value);
        foreground.EyedropperRequested += () => EyedropperRequested?.Invoke(foreground);
        background.EyedropperRequested += () => EyedropperRequested?.Invoke(background);
        Number(parameterGrid, "StrokeWidth", L.Get("Inspector.StrokeWidth"), 0, 1024, (p, v) => p.Style.StrokeWidth = v);
        Number(parameterGrid, "Opacity", L.Get("Inspector.OpacityPercent"), 0, 100, (p, v) => p.Style.Opacity = v / 100);
        Number(parameterGrid, "FontSize", L.Get("Inspector.FontSize"), 1, 4096, (p, v) => p.Style.FontSize = v);
        Number(parameterGrid, "NumberStart", L.Get("ToolOptions.NumberStart"), 1, 999999, (p, v) => p.NumberStart = (int)v);
        Number(parameterGrid, "NumberStep", L.Get("ToolOptions.NumberStep"), 1, 999999, (p, v) => p.NumberStep = (int)v);
        Number(parameterGrid, "NextNumber", L.Get("ToolOptions.NextNumber"), 1, int.MaxValue, (_, _) => { });
        Number(parameterGrid, "DefaultWidth", L.Get("ToolOptions.DefaultWidth"), 1, 1000000, (p, v) => p.DefaultWidth = v);
        Number(parameterGrid, "DefaultHeight", L.Get("ToolOptions.DefaultHeight"), 1, 1000000, (p, v) => p.DefaultHeight = v);
        AddRow(parameterGrid, "FontFamily", L.Get("Inspector.FontFamily"), fontFamily);
        Picker(parameterGrid, "TextAlignment", L.Get("Inspector.TextAlignment"),
            [(L.Get("Inspector.AlignLeft"), FloatCanvas.Core.TextAlignment.Left), (L.Get("Inspector.AlignCenter"), FloatCanvas.Core.TextAlignment.Center),
             (L.Get("Inspector.AlignRight"), FloatCanvas.Core.TextAlignment.Right)], (p, v) => p.Style.TextAlignment = (FloatCanvas.Core.TextAlignment)v);
        fontFamily.SelectionChanged += (_, _) =>
        {
            if (!refreshing && fontFamily.SelectedItem is ComboBoxItem item && item.Tag is string name)
                Change(p => p.Style.FontFamily = name);
        };
        Picker(parameterGrid, "StartArrowHead", L.Get("Inspector.StartArrow"), ArrowOptions(), (p, v) => p.Style.StartArrowHead = (ArrowHeadStyle)v);
        Picker(parameterGrid, "EndArrowHead", L.Get("Inspector.EndArrow"), ArrowOptions(), (p, v) => p.Style.EndArrowHead = (ArrowHeadStyle)v);
        Picker(parameterGrid, "StrokePattern", L.Get("Inspector.StrokePattern"),
            [(L.Get("Inspector.PatternSolid"), StrokePattern.Solid), (L.Get("Inspector.PatternDashed"), StrokePattern.Dashed), (L.Get("Inspector.PatternDotted"), StrokePattern.Dotted)],
            (p, v) => p.Style.StrokePattern = (StrokePattern)v);
        Number(parameterGrid, "ArrowHeadSize", L.Get("Inspector.ArrowHeadSize"), 0, 512, (p, v) => p.Style.ArrowHeadSize = v);
        Number(parameterGrid, "CornerRadius", L.Get("Inspector.CornerRadius"), 0, 10000000, (p, v) => p.CornerRadius = v);
        Number(parameterGrid, "EffectStrength", L.Get("ToolOptions.EffectStrength"), .01, 1024, (p, v) => p.EffectStrength = v);
        Picker(parameterGrid, "CaptureShape", L.Get("Snapshot.Shape"),
            Enum.GetValues<CaptureShape>().Select(value => (L.Get("Snapshot.Shape." + value), (object)value)).ToArray(),
            (p, value) => p.CaptureShape = (CaptureShape)value);
        Picker(parameterGrid, "CaptureSource", L.Get("Snapshot.Source"),
            Enum.GetValues<CaptureSource>().Select(value => (L.Get("Snapshot.Source." + value), (object)value)).ToArray(),
            (p, value) => p.CaptureSource = (CaptureSource)value);
        Number(parameterGrid, "CaptureBorderWidth", L.Get("Snapshot.BorderWidth"), 0, 1024, (p, value) => p.CaptureBorderWidth = value);
        rows.Add("Aspect", aspect); parameterGrid.Children.Add(aspect);
        aspect.Click += (_, _) => Change(p => p.ConstrainAspect = aspect.IsChecked == true);
        foreach (var symbol in Enum.GetValues<SymbolKind>())
        {
            var points = SymbolGeometry.Outline(symbol, 16, 16);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open()) { context.BeginFigure(new(points[0].X, points[0].Y), true, true); context.PolyLineTo(points.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, false); }
            geometry.Freeze();
            var icon = new System.Windows.Shapes.Path { Data = geometry, Width = 16, Height = 16, Stretch = Stretch.Uniform };
            icon.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "CanvasText");
            var button = ActionButton(L.Get("Symbol." + symbol), () => Change(p => p.Symbol = symbol));
            button.Content = icon; button.Tag = symbol; symbolGallery.Children.Add(button);
        }
        AddRow(parameterGrid, "Symbols", L.Get("ToolOptions.Symbols"), symbolGallery);
        foreach (var emoji in EmojiArtwork.BuiltIn)
        {
            var button = ActionButton(emoji, () => Change(p => p.Emoji = emoji));
            button.Tag = emoji; button.Content = new Image { Source = EmojiArtwork.Image(emoji), Width = 24, Height = 24, Stretch = Stretch.Uniform }; emojiGallery.Children.Add(button);
        }
        AddRow(parameterGrid, "Emoji", L.Get("ToolOptions.Emoji"), emojiGallery);
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); body.Children.Add(status);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Unloaded += (_, _) => { foreground.ClosePicker(); background.ClosePicker();  };
        Refresh();
    }

    public void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var p = controller.Parameters; var tool = p.Tool;
            heading.Text = L.Get("ToolDescription." + ToolPresetLibrary.Family(tool));
            bool line = tool is EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow;
            bool shape = tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Triangle or EditorTool.Symbol;
            bool stroke = line || shape || tool is EditorTool.Freehand or EditorTool.Highlighter;
            bool text = tool is EditorTool.Text or EditorTool.Number or EditorTool.Emoji;
            bool paint = stroke || tool is EditorTool.Text or EditorTool.Number;
            bool capture = tool == EditorTool.CaptureRegion;
            paint |= capture;
            Show("Foreground", paint); Show("Background", shape || tool == EditorTool.Number);
            foreground.Visibility = paint ? Visibility.Visible : Visibility.Collapsed;
            background.Visibility = shape || tool == EditorTool.Number ? Visibility.Visible : Visibility.Collapsed;
            Show("StrokeWidth", stroke); Show("Opacity", tool is EditorTool.Emoji or EditorTool.Image);
            Show("FontSize", text); Show("FontFamily", tool is EditorTool.Text or EditorTool.Number);
            Show("NumberStart", tool == EditorTool.Number); Show("NumberStep", tool == EditorTool.Number); Show("NextNumber", tool == EditorTool.Number);
            Show("DefaultWidth", shape); Show("DefaultHeight", shape);
            Show("StartArrowHead", line); Show("EndArrowHead", line); Show("ArrowHeadSize", line); Show("StrokePattern", stroke);
            Show("CornerRadius", tool is EditorTool.Rectangle or EditorTool.RoundedRectangle); Show("Aspect", shape);
            Show("EffectStrength", tool is EditorTool.Blur or EditorTool.Pixelate);
            Show("TextAlignment", tool == EditorTool.Text);
            Show("Symbols", tool == EditorTool.Symbol); Show("Emoji", tool == EditorTool.Emoji);
            Show("CaptureShape", capture); Show("CaptureSource", capture); Show("CaptureBorderWidth", capture);
            noParameters.Visibility = paint || text || tool is EditorTool.Blur or EditorTool.Pixelate or EditorTool.Image ? Visibility.Collapsed : Visibility.Visible;
            foreground.Value = capture ? p.CaptureBorderColor : p.Style.Stroke; background.Value = p.Style.Fill; aspect.IsChecked = p.ConstrainAspect; ToolOptionPreviews.SelectFont(fontFamily, p.Style.FontFamily);
            ((TextBlock)((StackPanel)rows["Foreground"]).Children[0]).Text = L.Get(capture ? "Snapshot.BorderColor" : "Color.Foreground");
            SetPicker("CaptureShape", p.CaptureShape); SetPicker("CaptureSource", p.CaptureSource); SetNumber("CaptureBorderWidth", p.CaptureBorderWidth);
            SetNumber("StrokeWidth", tool == EditorTool.Highlighter ? Math.Max(16, p.Style.StrokeWidth) : p.Style.StrokeWidth); SetNumber("Opacity", p.Style.Opacity * 100); SetNumber("FontSize", p.Style.FontSize);
            SetNumber("EffectStrength", p.EffectStrength); SetNumber("CornerRadius", p.CornerRadius ?? 0); SetNumber("ArrowHeadSize", p.Style.ArrowHeadSize);
            SetNumber("DefaultWidth", p.DefaultWidth); SetNumber("DefaultHeight", p.DefaultHeight);
            SetNumber("NumberStart", p.NumberStart); SetNumber("NumberStep", p.NumberStep); SetNumber("NextNumber", controller.Session.Document.NextNumber);
            if (tool == EditorTool.RoundedRectangle && p.CornerRadius is null) numbers["CornerRadius"].Text = "—";
            SetPicker("StartArrowHead", p.Style.StartArrowHead ?? (tool == EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None));
            SetPicker("EndArrowHead", p.Style.EndArrowHead ?? (tool is EditorTool.Arrow or EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None));
            SetPicker("StrokePattern", p.Style.StrokePattern);
            SetPicker("TextAlignment", p.Style.TextAlignment);
            HighlightGallery(symbolGallery, p.Symbol); HighlightGallery(emojiGallery, p.Emoji);
            ((TextBlock)((StackPanel)rows["FontSize"]).Children[0]).Text = L.Get(tool == EditorTool.Emoji ? "ToolOptions.Size" : "Inspector.FontSize");
            presets.Refresh();
            LayoutParameters();
        }
        finally { refreshing = false; }
    }

    // Every row is always visible together with its applicable peer; no accordion state to manage.
    private void LayoutParameters()
    {
        parameterGrid.RowDefinitions.Clear();
        string[][] groups = [ ["CaptureShape"], ["CaptureSource"], ["Foreground"], ["CaptureBorderWidth"], ["Background"], ["StrokeWidth", "Opacity"],
            ["DefaultWidth", "DefaultHeight"], ["FontSize", "TextAlignment"], ["FontFamily"], ["NumberStart", "NumberStep"], ["NextNumber"], ["StartArrowHead", "EndArrowHead"],
            ["StrokePattern", "ArrowHeadSize"], ["CornerRadius", "Aspect"], ["EffectStrength"], ["Symbols"], ["Emoji"] ];
        foreach (var group in groups)
        {
            var visible = group.Select(key => rows[key]).Where(row => row.Visibility == Visibility.Visible).ToArray();
            if (visible.Length == 0) continue;
            int rowIndex = parameterGrid.RowDefinitions.Count; parameterGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int column = 0; column < visible.Length; column++)
            {
                var row = visible[column]; Grid.SetRow(row, rowIndex); Grid.SetColumn(row, column);
                Grid.SetColumnSpan(row, visible.Length == 1 ? 2 : 1);
                row.Margin = new Thickness(0, 4, visible.Length == 2 && column == 0 ? 10 : 0, 4);
                if (row == aspect) row.VerticalAlignment = VerticalAlignment.Bottom;
            }
        }
    }
    internal bool IsColorPickerOpen => foreground.IsPickerOpen || background.IsPickerOpen;
    internal bool CloseColorPicker() => foreground.TryClosePicker() || background.TryClosePicker();
    internal bool OwnsColorPickerInput(DependencyObject? source) => foreground.OwnsPickerInput(source) || background.OwnsPickerInput(source);
    internal void RefreshDocumentState()
    {
        if (controller.Tool != EditorTool.Number || controller.Session.IsTransactionActive || numericFields["NextNumber"].IsKeyboardFocusWithin) return;
        SetNumber("NextNumber", controller.Session.Document.NextNumber);
    }

    private void Change(Action<ToolParameters> update)
    {
        if (refreshing) return;
        try
        {
            var next = controller.Parameters; update(next);
            if (!next.IsValid()) { Error(L.Get("ToolOptions.InvalidNumber")); return; }
            controller.ApplyParameters(next); status.Text = "";
            HighlightGallery(symbolGallery, next.Symbol); HighlightGallery(emojiGallery, next.Emoji);
            ParametersChanged?.Invoke();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private void Error(string message) { status.Text = message; status.Foreground = new SolidColorBrush(Color.FromRgb(190, 60, 55)); }
    private void Show(string key, bool visible) => rows[key].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private void SetNumber(string key, double value) => numericFields[key].Value = value;
    private void SetPicker(string key, object value) => pickers[key].SelectedItem = pickers[key].Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
    private static void HighlightGallery(Panel gallery, object value)
    {
        foreach (var button in gallery.Children.OfType<Button>())
            button.SetResourceReference(BorderBrushProperty, Equals(button.Tag, value) ? "CanvasAccent" : "CanvasBorder");
    }
    private static Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, ToolTip = label, MinHeight = 34, MinWidth = 34, Margin = new(2), Padding = new(6, 3, 6, 3) };
        ToolTipService.SetInitialShowDelay(button, 650);
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    private void AddRow(Panel body, string key, string label, FrameworkElement control)
    {
        var row = new StackPanel { Margin = new(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new(0, 0, 0, 3) }); row.Children.Add(control);
        AutomationProperties.SetName(control, label); rows.Add(key, row); body.Children.Add(row);
        if (control is NumericValueField numeric) { AutomationProperties.SetName(numeric.Input, label); AutomationProperties.SetName(numeric.Slider, label); }
    }
    private void Number(Panel body, string key, string label, double min, double max, Action<ToolParameters, double> set)
    {
        double sliderMax = key switch { "StrokeWidth" => 64, "FontSize" => 512, "NumberStart" or "NumberStep" or "NextNumber" => 100, "DefaultWidth" or "DefaultHeight" => 600, "CornerRadius" => 100, "ArrowHeadSize" => 100, "EffectStrength" => 100, _ => max };
        var field = new NumericValueField(min, max, sliderMaximum: Math.Min(max, sliderMax), step: key == "EffectStrength" ? .1 : 1);
        if (key is "NumberStart" or "NumberStep" or "NextNumber") field.Slider.IsSnapToTickEnabled = true;
        var box = field.Input; numbers.Add(key, box); numericFields.Add(key, field); AddRow(body, key, label, field);
        if (key == "StrokeWidth") ((StackPanel)rows[key]).Children.Add(ToolOptionPreviews.StrokeWidth(field));
        field.ValueChanged += value =>
        {
            if (refreshing) return;
            if (key is "NumberStart" or "NumberStep" or "NextNumber" && value != Math.Truncate(value)) { Error(L.Get("ToolOptions.InvalidNumber")); return; }
            if (key == "NextNumber")
            {
                controller.Session.Execute(L.Get("History.NumberCounter"), document => document.NextNumber = (int)value);
                ParametersChanged?.Invoke(); return;
            }
            if (key == "StrokeWidth" && controller.Tool == EditorTool.Highlighter && value < 16) { Error(L.Get("ToolOptions.InvalidNumber")); return; }
            Change(p => set(p, value));
        };
        field.InvalidInput += () => { if (!refreshing) Error(L.Get("ToolOptions.InvalidNumber")); };
    }
    private void Picker(Panel body, string key, string label, (string Name, object Value)[] options, Action<ToolParameters, object> set)
    {
        var picker = new ComboBox { MinHeight = 32 };
        foreach (var option in options) picker.Items.Add(new ComboBoxItem { Content = ToolOptionPreviews.Choice(option.Name, option.Value), Tag = option.Value });
        pickers.Add(key, picker); AddRow(body, key, label, picker);
        picker.SelectionChanged += (_, _) => { if (!refreshing && picker.SelectedItem is ComboBoxItem item) Change(p => set(p, item.Tag)); };
    }

    private static (string, object)[] ArrowOptions() =>
        [(L.Get("Inspector.ArrowFilledTriangle"), ArrowHeadStyle.FilledTriangle), (L.Get("Inspector.ArrowOpen"), ArrowHeadStyle.Open),
         (L.Get("Inspector.ArrowDiamond"), ArrowHeadStyle.Diamond), (L.Get("Inspector.ArrowNone"), ArrowHeadStyle.None)];
}
