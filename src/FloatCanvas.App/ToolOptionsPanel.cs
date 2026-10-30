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
    internal static double PreferredWidth => PropertyPanelLayout.ContentWidth + PropertyPanelLayout.Padding * 4 + 1 + 196 + WindowLayout.ScrollBarWidth;
    private readonly EditorController controller;
    private readonly Dictionary<string, TextBox> numbers = [];
    private readonly Dictionary<string, NumericValueField> numericFields = [];
    private readonly Dictionary<string, SegmentedChoice> pickers = [];
    private readonly Dictionary<string, FrameworkElement> rows = [];
    private readonly Dictionary<string, TextBlock> captions = [];
    private readonly ColorField foreground, background;
    private readonly AspectRatioButton aspect = new();
    private readonly CheckBox aspectSwitch = new();
    private readonly SegmentedChoice linkColors = SegmentedChoicePreviews.ColorStyle();
    private (EditorTool Tool, SymbolKind Symbol)? colorStyleContext;
    private readonly ComboBox fontFamily = ToolOptionPreviews.FontPicker();
    private readonly Grid parameterGrid = new();
    private readonly PropertyChangeRegion linkedColorsRegion;
    private readonly ToolPresetGallery presets;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock heading = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly TextBlock noParameters = new() { Text = L.Get("ToolOptions.NoParameters"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly UniformGrid symbolGallery = new() { Columns = 6 };
    private readonly UniformGrid emojiGallery = new() { Columns = 6 };
    private bool refreshing, refreshPending = true;
    private EditorTool? fontContextTool;
    private EditorSession? fontContextSession;

    public event Action? ParametersChanged;
    public event Action<ColorField>? EyedropperRequested;

    public ToolOptionsPanel(EditorController controller, ToolPresetLibrary library, ColorLibrary colors, ToolStateStore? stateStore = null)
    {
        this.controller = controller;
        PropertyPanelLayout.Scope(this);
        Width = PreferredWidth; MinWidth = PropertyPanelLayout.TwoColumnMinimumWidth - 2; MaxHeight = 620;
        SetResourceReference(BackgroundProperty, "CanvasSurface"); SetResourceReference(ForegroundProperty, "CanvasText");
        var body = new StackPanel(); body.Children.Add(heading);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        parameterGrid.ColumnDefinitions.Add(new()); parameterGrid.ColumnDefinitions.Add(new() { Width = new(PropertyPanelLayout.PairGap) }); parameterGrid.ColumnDefinitions.Add(new());
        parameterGrid.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutParameters(); };
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
        var colorRows = new StackPanel();
        foreach (string key in new[] { "Foreground", "Background" })
        { parameterGrid.Children.Remove(rows[key]); colorRows.Children.Add(rows[key]); }
        linkedColorsRegion = new(colorRows); rows.Add("Colors", linkedColorsRegion); parameterGrid.Children.Add(linkedColorsRegion);
        foreground.ValueChanged += value => Change(p => { if (p.Tool == EditorTool.CaptureRegion) p.CaptureBorderColor = value; else p.Style.Stroke = value; });
        background.ValueChanged += value => Change(p => p.Style.Fill = value);
        foreground.EyedropperRequested += () => EyedropperRequested?.Invoke(foreground);
        background.EyedropperRequested += () => EyedropperRequested?.Invoke(background);
        Number(parameterGrid, "StrokeWidth", L.Get("Inspector.StrokeWidth"), (p, v) => p.Style.StrokeWidth = v);
        Number(parameterGrid, "Opacity", L.Get("Inspector.Opacity"), (p, v) => p.Style.Opacity = v / 100);
        Number(parameterGrid, "FontSize", L.Get("Inspector.FontSize"), (p, v) => p.Style.FontSize = v);
        Number(parameterGrid, "NumberStart", L.Get("ToolOptions.NumberStart"), (p, v) => p.NumberStart = (int)v);
        Number(parameterGrid, "NumberStep", L.Get("ToolOptions.NumberStep"), (p, v) => p.NumberStep = (int)v);
        Number(parameterGrid, "NextNumber", L.Get("ToolOptions.NextNumber"), (_, _) => { });
        Number(parameterGrid, "DefaultWidth", L.Get("Inspector.Width"), (p, v) => p.DefaultWidth = v);
        Number(parameterGrid, "DefaultHeight", L.Get("Inspector.Height"), (p, v) => p.DefaultHeight = v);
        AddRow(parameterGrid, "FontFamily", L.Get("Inspector.FontFamily"), fontFamily);
        Picker(parameterGrid, "TextAlignment", L.Get("Inspector.TextAlignment"),
            [(L.Get("Inspector.AlignLeft"), FloatCanvas.Core.TextAlignment.Left), (L.Get("Inspector.AlignCenter"), FloatCanvas.Core.TextAlignment.Center),
             (L.Get("Inspector.AlignRight"), FloatCanvas.Core.TextAlignment.Right)], (p, v) => p.Style.TextAlignment = (FloatCanvas.Core.TextAlignment)v);
        ((FontPickerControl)fontFamily).FontCommitted += name =>
        {
            if (!refreshing) Change(p => p.Style.FontFamily = name);
        };
        Picker(parameterGrid, "StartArrowHead", L.Get("Inspector.StartArrow"), ArrowOptions(), (p, v) => p.Style.StartArrowHead = (ArrowHeadStyle)v);
        Picker(parameterGrid, "EndArrowHead", L.Get("Inspector.EndArrow"), ArrowOptions(), (p, v) => p.Style.EndArrowHead = (ArrowHeadStyle)v);
        Picker(parameterGrid, "StrokePattern", L.Get("Inspector.StrokePattern"),
            [(L.Get("Inspector.PatternSolid"), StrokePattern.Solid), (L.Get("Inspector.PatternDashed"), StrokePattern.Dashed), (L.Get("Inspector.PatternDotted"), StrokePattern.Dotted)],
            (p, v) => p.Style.StrokePattern = (StrokePattern)v);
        Number(parameterGrid, "ArrowHeadSize", L.Get("Inspector.ArrowHeadSize"), (p, v) => p.Style.ArrowHeadSize = v);
        Number(parameterGrid, "CornerRadius", L.Get("Inspector.CornerRadius"), (p, v) => p.CornerRadius = v);
        Number(parameterGrid, "EffectStrength", L.Get("ToolOptions.EffectStrength"), (p, v) => p.EffectStrength = v);
        Picker(parameterGrid, "CaptureShape", L.Get("Snapshot.Shape"),
            Enum.GetValues<CaptureShape>().Select(value => (L.Get("Snapshot.Shape." + value), (object)value)).ToArray(),
            (p, value) => p.CaptureShape = (CaptureShape)value);
        Picker(parameterGrid, "CaptureSource", L.Get("Snapshot.Source"),
            Enum.GetValues<CaptureSource>().Select(value => (L.Get("Snapshot.Source." + value), (object)value)).ToArray(),
            (p, value) => p.CaptureSource = (CaptureSource)value);
        Number(parameterGrid, "CaptureBorderWidth", L.Get("Snapshot.BorderWidth"), (p, value) => p.CaptureBorderWidth = value);
        var size = PropertyPanelLayout.Row(new PropertyLabel(L.Get("Property.Size")), PropertyPanelLayout.Pair(rows["DefaultWidth"], rows["DefaultHeight"], aspect));
        rows.Add("Size", size); parameterGrid.Children.Add(size);
        var aspectRow = PropertyPanelLayout.BooleanRow(L.Get("Inspector.KeepAspect"), aspectSwitch);
        rows.Add("Aspect", aspectRow); parameterGrid.Children.Add(aspectRow);
        aspect.Click += (_, _) => Change(p => p.ConstrainAspect = aspect.IsChecked == true);
        aspectSwitch.Click += (_, _) => Change(p => p.ConstrainAspect = aspectSwitch.IsChecked == true);
        var linkRow = PropertyPanelLayout.Row(new PropertyLabel(L.Get("Color.Style")), linkColors);
        AutomationProperties.SetName(linkColors, L.Get("Color.Style"));
        rows.Add("LinkColors", linkRow); parameterGrid.Children.Add(linkRow);
        linkColors.SelectionChanged += (_, _) =>
        {
            if (refreshing || linkColors.SelectedValue is not bool linked) return;
            linkedColorsRegion.Change(() => { Change(p => p.Style.LinkColors = linked); Refresh(); });
        };
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
        Content = PropertyChangeRegion.ReserveHighlightSpace(new ScrollViewer { Margin = WindowLayout.ContentMargin(), Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        void Conceal()
        { refreshPending=true;foreach(var field in numericFields.Values)field.CancelGesture();foreground.ClosePicker();background.ClosePicker(); }
        Unloaded += (_, _) => Conceal();
        IsVisibleChanged += (_, _) => { if(IsVisible) {if(refreshPending)RefreshNow();} else Conceal(); };
        Refresh();
    }

    public void Refresh()
    {
        refreshPending=true;
        if(IsVisible)RefreshNow();
    }

    internal void PrepareForShow()
    { refreshPending=true;RefreshNow(); }

    private void RefreshNow()
    {
        if (refreshing) return;
        // A numeric preview is already reflected in its field. The toolbar echoes
        // ParametersChanged synchronously; rebuilding here cancels our own capture.
        if (publishingNumericTool == controller.Tool && numericFields.Values.Any(field => field.IsInteracting)) return;
        refreshing = true;
        try
        {
            foreach (var field in numericFields.Values) field.CancelGesture();
            status.Text = "";
            var p = controller.Parameters; var tool = p.Tool;
            foreach (var (key, field) in numericFields)
            {
                var domain = NumericParameterDomain.ForTool(key, tool);
                field.SetSliderRange(domain.RailMinimum, domain.RailMaximum);
            }
            if (fontContextTool != tool || !ReferenceEquals(fontContextSession, controller.Session))
            {
                linkedColorsRegion.Reset();
                ((FontPickerControl)fontFamily).ResetModelContext();
                fontContextTool = tool; fontContextSession = controller.Session;
            }
            heading.Text = L.Get("ToolDescription." + ToolPresetLibrary.Family(tool));
            bool line = tool is EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow;
            bool shape = tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Triangle or EditorTool.Symbol;
            bool stroke = line || shape || tool is EditorTool.Freehand or EditorTool.Highlighter;
            bool text = tool is EditorTool.Text or EditorTool.Number or EditorTool.Emoji;
            bool paint = stroke || tool is EditorTool.Text or EditorTool.Number;
            bool capture = tool == EditorTool.CaptureRegion;
            bool boxRegion=tool is EditorTool.Crop or EditorTool.Blur or EditorTool.Pixelate;
            paint |= capture;
            Show("Foreground", paint); Show("Background", shape && !p.Style.LinkColors || tool == EditorTool.Number); Show("LinkColors", shape);
            Show("Colors", paint);
            foreground.Visibility = paint ? Visibility.Visible : Visibility.Collapsed;
            background.Visibility = shape && !p.Style.LinkColors || tool == EditorTool.Number ? Visibility.Visible : Visibility.Collapsed;
            Show("StrokeWidth", stroke); Show("Opacity", tool is EditorTool.Emoji or EditorTool.Image);
            Show("FontSize", text); Show("FontFamily", tool is EditorTool.Text or EditorTool.Number);
            Show("NumberStart", tool == EditorTool.Number); Show("NumberStep", tool == EditorTool.Number); Show("NextNumber", tool == EditorTool.Number);
            Show("DefaultWidth", shape); Show("DefaultHeight", shape);
            Show("StartArrowHead", line); Show("EndArrowHead", line); Show("ArrowHeadSize", line); Show("StrokePattern", stroke);
            Show("CornerRadius", tool is EditorTool.Rectangle or EditorTool.RoundedRectangle);
            Show("Size", shape); Show("Aspect", !shape && boxRegion);
            Show("EffectStrength", tool is EditorTool.Blur or EditorTool.Pixelate);
            Show("TextAlignment", tool == EditorTool.Text);
            Show("Symbols", tool == EditorTool.Symbol); Show("Emoji", tool == EditorTool.Emoji);
            Show("CaptureShape", capture); Show("CaptureSource", capture); Show("CaptureBorderWidth", capture);
            noParameters.Visibility = paint || text || boxRegion || tool == EditorTool.Image ? Visibility.Collapsed : Visibility.Visible;
            bool colorEcho = foreground.IsNotifyingOwner || background.IsNotifyingOwner;
            if (IsApplicable("Foreground"))
            {
                uint value = capture ? p.CaptureBorderColor : p.Style.Stroke;
                if (colorEcho) foreground.Value = value; else foreground.SynchronizeModel(value);
                foreground.PrepareForShow();
            }
            if (IsApplicable("Background"))
            {
                if (colorEcho) background.Value = p.Style.Fill; else background.SynchronizeModel(p.Style.Fill);
                background.PrepareForShow();
            }
            if (IsApplicable("Size")) aspect.IsChecked = p.ConstrainAspect;
            if (IsApplicable("FontFamily")) ToolOptionPreviews.SelectFont(fontFamily, p.Style.FontFamily);
            if (IsApplicable("Aspect")) aspectSwitch.IsChecked = p.ConstrainAspect;
            if (IsApplicable("LinkColors"))
            {
                var context = (tool, p.Symbol);
                if (colorStyleContext != context) { SegmentedChoicePreviews.UpdateColorStyle(linkColors, tool, p.Symbol); colorStyleContext = context; }
                linkColors.SelectedValue = p.Style.LinkColors;
            }
            if (IsApplicable("Foreground")) captions["Foreground"].Text = L.Get(capture ? "Snapshot.BorderColor" : tool == EditorTool.Number ? "Color.NumberShape" : shape ? p.Style.LinkColors ? "Color.Unified" : "Color.Outline" : "Color.Foreground");
            if (IsApplicable("Background")) captions["Background"].Text = L.Get(tool == EditorTool.Number ? "Color.NumberText" : "Color.Interior");
            SetPicker("CaptureShape", p.CaptureShape); SetPicker("CaptureSource", p.CaptureSource); SetNumber("CaptureBorderWidth", p.CaptureBorderWidth);
            SetNumber("StrokeWidth", tool == EditorTool.Highlighter ? Math.Max(16, p.Style.StrokeWidth) : p.Style.StrokeWidth); SetNumber("Opacity", p.Style.Opacity * 100); SetNumber("FontSize", p.Style.FontSize);
            SetNumber("EffectStrength", p.EffectStrength); SetNumber("CornerRadius", p.CornerRadius ?? 0); SetNumber("ArrowHeadSize", p.Style.ArrowHeadSize);
            SetNumber("DefaultWidth", p.DefaultWidth); SetNumber("DefaultHeight", p.DefaultHeight);
            SetNumber("NumberStart", p.NumberStart); SetNumber("NumberStep", p.NumberStep); SetNumber("NextNumber", controller.Session.Document.NextNumber);
            if (tool == EditorTool.RoundedRectangle && p.CornerRadius is null) numericFields["CornerRadius"].ShowPlaceholder("—");
            SetPicker("StartArrowHead", p.Style.StartArrowHead ?? (tool == EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None));
            SetPicker("EndArrowHead", p.Style.EndArrowHead ?? (tool is EditorTool.Arrow or EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None));
            SetPicker("StrokePattern", p.Style.StrokePattern);
            SetPicker("TextAlignment", p.Style.TextAlignment);
            if (IsApplicable("Symbols")) HighlightGallery(symbolGallery, p.Symbol);
            if (IsApplicable("Emoji")) HighlightGallery(emojiGallery, p.Emoji);
            if (IsApplicable("FontSize")) captions["FontSize"].Text = L.Get(tool is EditorTool.Emoji or EditorTool.Number ? "ToolOptions.Size" : "Inspector.FontSize");
            presets.PrepareForShow();
            LayoutParameters();
            refreshPending=false;
        }
        finally { refreshing = false; }
    }

    // Every row is always visible together with its applicable peer; no accordion state to manage.
    private void LayoutParameters()
    {
        parameterGrid.RowDefinitions.Clear();
        string[][] groups = [ ["Size"], ["Aspect"], ["CaptureShape"], ["CaptureSource"], ["LinkColors"], ["Colors"], ["CaptureBorderWidth"], ["StrokeWidth"], ["CornerRadius"], ["Opacity"],
            ["FontFamily"], ["FontSize"], ["TextAlignment"], ["NumberStart"], ["NumberStep"], ["NextNumber"], ["StartArrowHead"], ["EndArrowHead"],
            ["StrokePattern"], ["ArrowHeadSize"], ["EffectStrength"], ["Symbols"], ["Emoji"] ];
        foreach (var group in groups)
        {
            var visible = group.Select(key => rows[key]).Where(row => row.Visibility == Visibility.Visible).ToArray();
            if (visible.Length == 0) continue;
            int rowIndex = parameterGrid.RowDefinitions.Count; parameterGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int column = 0; column < visible.Length; column++)
            {
                var row = visible[column]; Grid.SetRow(row, rowIndex); Grid.SetColumn(row, column);
                Grid.SetColumnSpan(row, 3);
                if (row is not PropertyChangeRegion) row.Margin = new Thickness(0, 4, 0, 4);
            }
        }
    }
    internal bool IsColorPickerOpen => foreground.IsPickerOpen || background.IsPickerOpen;
    internal bool CloseColorPicker() => foreground.TryClosePicker() || background.TryClosePicker();
    internal bool OwnsColorPickerInput(DependencyObject? source) => foreground.OwnsPickerInput(source) || background.OwnsPickerInput(source);
    internal void RefreshPresets()
    { if(IsVisible)presets.Refresh();else refreshPending=true; }
    internal void RefreshDocumentState()
    {
        if(!IsVisible){refreshPending=true;return;}
        if (controller.Tool != EditorTool.Number || controller.Session.IsTransactionActive || numericFields["NextNumber"].IsKeyboardFocusWithin) return;
        SetNumber("NextNumber", controller.Session.Document.NextNumber);
    }

    private EditorTool? publishingNumericTool;

    private void Change(Action<ToolParameters> update, bool numericPreview = false, bool? followNumberStart = null)
    {
        if (refreshing) return;
        try
        {
            var next = controller.Parameters; update(next);
            if (!next.IsValid()) { Error(L.Get("ToolOptions.InvalidNumber")); return; }
            controller.ApplyParameters(next, followNumberStart); status.Text = "";
            // The active field owns its draft. Refresh only the paired value so a
            // live ratio update cannot tear down capture or move the text caret.
            if (next.ConstrainAspect)
            {
                if (!numericFields["DefaultWidth"].IsInteracting && !numericFields["DefaultWidth"].Input.IsKeyboardFocusWithin) SetNumber("DefaultWidth", next.DefaultWidth);
                if (!numericFields["DefaultHeight"].IsInteracting && !numericFields["DefaultHeight"].Input.IsKeyboardFocusWithin) SetNumber("DefaultHeight", next.DefaultHeight);
            }
            if (IsVisible && IsApplicable("Symbols")) HighlightGallery(symbolGallery, next.Symbol);
            if (IsVisible && IsApplicable("Emoji")) HighlightGallery(emojiGallery, next.Emoji);
            var previous = publishingNumericTool;
            if (numericPreview) publishingNumericTool = next.Tool;
            try { ParametersChanged?.Invoke(); }
            finally { publishingNumericTool = previous; }
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private void Error(string message) { status.Text = "⚠ " + message; status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); }
    private void Show(string key, bool visible) => rows[key].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private bool IsApplicable(string key) => rows[key].Visibility == Visibility.Visible;
    private void SetNumber(string key, double value)
    { if ((refreshing||IsVisible) && IsApplicable(key)) numericFields[key].Value = value; }
    private void SetPicker(string key, object value)
    { if ((refreshing||IsVisible) && IsApplicable(key)) pickers[key].SelectedValue = value; }
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
        string shortKey = key switch { "NumberStart" => "Property.NumberStart", "NumberStep" => "Property.NumberStep", "NextNumber" => "Property.NextNumber", "CaptureBorderWidth" => "Property.BorderWidth", _ => "" };
        var caption = new PropertyLabel(shortKey.Length == 0 ? label : L.Get(shortKey)) { ToolTip = label };
        captions.Add(key, caption);
        if (control is NumericValueField anchored)
            anchored.SetScrubAnchor(NumericPropertyIcon.ForProperty(key), label);
        if (key is "DefaultWidth" or "DefaultHeight")
        {
            rows.Add(key, control); AutomationProperties.SetName(control, label);
            var dimension = (NumericValueField)control; AutomationProperties.SetName(dimension.Input, label); AutomationProperties.SetName(dimension.Slider, label);
            return;
        }
        Panel row;
        if (control is not UniformGrid)
        {
            row = PropertyPanelLayout.Row(caption, control);
            if (control is NumericValueField number) number.SetLabel(caption);
        }
        else { var stack = new StackPanel(); stack.Children.Add(caption); stack.Children.Add(control); row = stack; }
        row.Margin = new(0, 4, 0, 4);
        AutomationProperties.SetName(control, label); rows.Add(key, row); body.Children.Add(row);
        if (control is NumericValueField numeric) { AutomationProperties.SetName(numeric.Input, label); AutomationProperties.SetName(numeric.Slider, label); }
    }
    private void Number(Panel body, string key, string label, Action<ToolParameters, double> set)
    {
        var domain = NumericParameterDomain.ForTool(key);
        var field = new NumericValueField(domain.Minimum, domain.Maximum, domain.RailMinimum, domain.RailMaximum, domain.Step, domain.Integer)
        { Unit = key switch { "Opacity" => "%", "NumberStart" or "NextNumber" => "#", "NumberStep" => L.Get("Numeric.Unit.Step"), _ => "px" } };
        if (key is "NumberStart" or "NumberStep" or "NextNumber") field.Slider.IsSnapToTickEnabled = true;
        var box = field.Input; numbers.Add(key, box); numericFields.Add(key, field); AddRow(body, key, label, field);
        box.LostKeyboardFocus += (_, _) => { if (!refreshing && !numericFields.Values.Any(number => number.IsInteracting)) Refresh(); };
        EditorSession? counterSession = null; long counterTransaction = 0;
        ToolParameters? original = null;
        bool? followNumberStart = null;
        EditorSession? numberStartSession = null;
        SceneDocument? numberStartDocument = null;
        field.CanBeginGesture = () => key != "NextNumber" || !controller.Session.IsTransactionActive;
        field.GestureStarted += () =>
        {
            original = controller.Parameters;
            if (key == "NumberStart")
            {
                numberStartSession = controller.Session; numberStartDocument = controller.Session.Document;
                followNumberStart = numberStartDocument.NextNumber == original.NumberStart;
            }
            if (key != "NextNumber") return;
            counterSession = controller.Session; counterSession.BeginTransaction(L.Get("History.NumberCounter")); counterTransaction = counterSession.TransactionId;
        };
        field.GestureCompleted += commit =>
        {
            if (counterSession is { } session && session.IsTransactionActive && session.TransactionId == counterTransaction)
            { if (commit && ReferenceEquals(session, controller.Session)) session.Commit(); else session.Cancel(); }
            counterSession = null;
            if (!commit && key != "NextNumber" && original is { } previous && controller.Tool == previous.Tool &&
                (key != "NumberStart" || ReferenceEquals(numberStartSession, controller.Session) && ReferenceEquals(numberStartDocument, controller.Session.Document)))
            { controller.ApplyParameters(previous, followNumberStart); ParametersChanged?.Invoke(); }
            original = null;
            followNumberStart = null;
            numberStartSession = null;
            numberStartDocument = null;
            if (!refreshing) Refresh();
        };
        field.ValueChanged += value =>
        {
            if (refreshing) return;
            if (key == "NumberStart" && field.IsInteracting &&
                (controller.Tool != EditorTool.Number || !ReferenceEquals(numberStartSession, controller.Session) ||
                    !ReferenceEquals(numberStartDocument, controller.Session.Document))) return;
            if (key is "NumberStart" or "NumberStep" or "NextNumber" && value != Math.Truncate(value)) { Error(L.Get("ToolOptions.InvalidNumber")); return; }
            if (key == "NextNumber")
            {
                if (field.IsInteracting)
                {
                    if (!ReferenceEquals(counterSession, controller.Session) || !controller.Session.IsTransactionActive || controller.Session.TransactionId != counterTransaction) return;
                    controller.Session.Document.NextNumber = (int)value; controller.Session.NotifyChanged();
                }
                else controller.Session.Execute(L.Get("History.NumberCounter"), document => document.NextNumber = (int)value);
                status.Text = "";
                ParametersChanged?.Invoke(); return;
            }
            if (key == "StrokeWidth" && controller.Tool == EditorTool.Highlighter && value < 16) { Error(L.Get("ToolOptions.InvalidNumber")); return; }
            Change(p =>
            {
                if (p.ConstrainAspect && key is "DefaultWidth" or "DefaultHeight")
                {
                    // Reject invalid paired sizes atomically through IsValid;
                    // silently clamping only one dimension would break the ratio.
                    double factor = value / (key == "DefaultWidth" ? p.DefaultWidth : p.DefaultHeight);
                    p.DefaultWidth *= factor; p.DefaultHeight *= factor;
                }
                set(p, value);
            }, field.IsInteracting, field.IsInteracting ? followNumberStart : null);
        };
        field.InvalidInput += () => { if (!refreshing) Error(L.Get("ToolOptions.InvalidNumber")); };
    }
    private void Picker(Panel body, string key, string label, (string Name, object Value)[] options, Action<ToolParameters, object> set)
    {
        var picker = new SegmentedChoice();
        foreach (var option in options) SegmentedChoicePreviews.Add(picker, option.Name, option.Value);
        AutomationProperties.SetName(picker, label);
        pickers.Add(key, picker); AddRow(body, key, label, picker);
        picker.SelectionChanged += (_, _) => { if (!refreshing && picker.SelectedValue is { } value) Change(p => set(p, value)); };
    }

    private static (string, object)[] ArrowOptions() =>
        [(L.Get("Inspector.ArrowFilledTriangle"), ArrowHeadStyle.FilledTriangle), (L.Get("Inspector.ArrowOpen"), ArrowHeadStyle.Open),
         (L.Get("Inspector.ArrowDiamond"), ArrowHeadStyle.Diamond), (L.Get("Inspector.ArrowNone"), ArrowHeadStyle.None)];
}
