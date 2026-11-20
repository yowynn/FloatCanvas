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

/// <summary>One configuration surface; each tool content owns its editing target and reusable recipes.</summary>
internal sealed partial class ToolOptionsPanel : UserControl
{
    internal static double PreferredWidth => PropertyPanelLayout.ContentWidth + PropertyPanelLayout.Padding * 4 + 1 + 196 + WindowLayout.ScrollBarWidth;
    private readonly EditorController controller;
    private readonly Dictionary<string, TextBox> numbers = [];
    private readonly Dictionary<string, NumericValueField> numericFields = [];
    private readonly Dictionary<string, SegmentedChoice> pickers = [];
    private readonly Dictionary<string, FrameworkElement> rows = [];
    private readonly Dictionary<string, TextBlock> captions = [];
    private readonly ColorField foreground, background;
    private readonly CaptionModeSwitch freehandSmoothing = new(FreehandSmoothingIcon);
    private readonly AspectRatioButton aspect = new();
    private readonly CheckBox aspectSwitch = new();
    private readonly SegmentedChoice linkColors = SegmentedChoicePreviews.ColorStyle();
    private (EditorTool Tool, SymbolKind Symbol)? colorStyleContext;
    private readonly ComboBox fontFamily = ToolOptionPreviews.FontPicker();
    private readonly Grid parameterGrid = new();
    private readonly Grid parameterColumns = new();
    private readonly PropertyChangeRegion linkedColorsRegion;
    private readonly StackPanel colorRows = new();
    private readonly PropertyChangeRegion highlighterAngleRegion;
    private readonly ToolPresetGallery presets;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock heading = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly TextBlock noParameters = new() { Text = L.Get("ToolOptions.NoParameters"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly UniformGrid symbolGallery = new() { Columns = 6 };
    private readonly UniformGrid emojiGallery = new() { Columns = 6 };
    private bool refreshing, refreshPending = true;
    private EditorTool? fontContextTool;
    private EditorSession? fontContextSession;

    private readonly ScrollViewer parameterScroll = ToolOptionsLayout.ScrollWithReservedGutter();
    private readonly Border presetHost = new();
    private readonly ToolOptionsContent standardContent;
    private IToolOptionsContent? activeContent;
    private bool switchingContent;
    private double initialMaximumHeight = 620;
    internal double InitialMaximumHeight
    {
        get => initialMaximumHeight;
        set { if (initialMaximumHeight == value) return; initialMaximumHeight = value; InvalidateMeasure(); }
    }
    private CanvasToolOptions? canvasContent;
    internal Func<CanvasToolOptions>? CanvasContentFactory { get; set; }
    internal event Action? HeaderChanged;
    internal ToolOptionsHeader Header => activeContent?.Header ?? new(ToolDisplayName.Get(controller.Tool));
    internal void SetCanvasContent(CanvasToolOptions content)
    { canvasContent = content; content.Failed += Error; content.HeaderChanged += () => HeaderChanged?.Invoke(); }
    internal void CancelEditing()
    { CloseTransientEditors(); activeContent?.CancelEditing(); }
    private void CancelStandardEditing()
    {
        foreach (var field in numericFields.Values) field.CancelGesture();
        presentationSettings.CancelGestures(); foreground.ClosePicker(); background.ClosePicker(); presentationGlobalSettings.ClosePickers();
    }
    internal void ClearStatus() { status.Text = ""; status.Visibility = Visibility.Collapsed; }
    internal void SynchronizeFreehandSmoothing()
    {
        bool enabled = controller.SmoothFreehand;
        freehandSmoothing.Synchronize(enabled, true, L.Get("Tool.Freehand.Smoothing"),
            L.Get(enabled ? "Tool.Freehand.SmoothingOn" : "Tool.Freehand.SmoothingOff"),
            L.Get(enabled ? "Tool.Freehand.DisableSmoothing" : "Tool.Freehand.EnableSmoothing"));
    }

    private static FrameworkElement FreehandSmoothingIcon(bool enabled, string foreground)
    {
        var icon = (Viewbox)CommandGlyphs.Create(enabled ? "M2,12 C6,12 9,3 14,3" : "M2,12 L6,12 L10,3 L14,3");
        foreach (var path in ((Canvas)icon.Child).Children.OfType<System.Windows.Shapes.Path>())
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, foreground);
        return icon;
    }

    private void ActivateContent(IToolOptionsContent content)
    {
        if (ReferenceEquals(activeContent, content)) return;
        switchingContent = true;
        try
        {
            CloseTransientEditors(); activeContent?.CancelEditing();
            parameterScroll.Content = null; presetHost.Child = null;
            activeContent = content; content.Parameters.Margin = ToolOptionsLayout.ParameterMargin; parameterScroll.Content = content.Parameters; presetHost.Child = content.Presets;
            ClearStatus();
        }
        finally { switchingContent = false; }
    }
    private void CloseTransientEditors()
    {
        ((FontPickerControl)fontFamily).CancelDraft(returnFocus: false);
        if (activeContent is null) return;
        Close(activeContent.Parameters); Close(activeContent.Presets);
        static void Close(DependencyObject node)
        {
            if (node is FrameworkElement element && element.ContextMenu is { } menu) menu.IsOpen = false;
            if (node is ComboBox combo) combo.IsDropDownOpen = false;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToArray()) Close(child);
        }
    }

    public event Action? ParametersChanged;
    public event Action<ColorField>? EyedropperRequested;
    internal event Action? FreehandSmoothingRequested;

    public ToolOptionsPanel(EditorController controller, ToolPresetLibrary library, ColorLibrary colors, ToolStateStore? stateStore = null)
    {
        this.controller = controller;
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(freehandSmoothing, true);
        freehandSmoothing.Click += (_, _) => FreehandSmoothingRequested?.Invoke();
        SynchronizeFreehandSmoothing();
        PropertyPanelLayout.Scope(this);
        Width = double.NaN; MinWidth = PropertyPanelLayout.TwoColumnMinimumWidth - 2;
        SetResourceReference(BackgroundProperty, "CanvasSurface"); SetResourceReference(ForegroundProperty, "CanvasText");
        var body = new StackPanel { Margin = ToolOptionsLayout.ParameterMargin };
        var columns = parameterColumns;
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        parameterGrid.ColumnDefinitions.Add(new()); parameterGrid.ColumnDefinitions.Add(new() { Width = new(PropertyPanelLayout.PairGap) }); parameterGrid.ColumnDefinitions.Add(new());
        parameterGrid.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutParameters(); };
        var parameters = new StackPanel(); parameters.Children.Add(noParameters); parameters.Children.Add(parameterGrid);
        var recipeBorder = new Border { BorderThickness = new(1, 0, 0, 0), Padding = new(WindowLayout.ContentInset, 0, 0, 0) };
        recipeBorder.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        presets = new ToolPresetGallery(controller, library, stateStore);
        presets.Applied += () => { Refresh(); ParametersChanged?.Invoke(); };
        presets.Failed += Error;
        foreground = new(ColorRole.Foreground, colors); background = new(ColorRole.Background, colors);
        AddRow(parameterGrid, "Foreground", L.Get("Color.Unified"), foreground);
        AddRow(parameterGrid, "Background", L.Get("Color.Interior"), background);
        foreach (string key in new[] { "Foreground", "Background" })
        { parameterGrid.Children.Remove(rows[key]); colorRows.Children.Add(rows[key]); }
        linkedColorsRegion = new(colorRows); rows.Add("Colors", linkedColorsRegion); parameterGrid.Children.Add(linkedColorsRegion);
        foreground.ValueChanged += value => Change(p => p.Style.PrimaryColor = value);
        background.ValueChanged += value => Change(p => p.Style.SecondaryColor = value);
        foreground.EyedropperRequested += () => EyedropperRequested?.Invoke(foreground);
        background.EyedropperRequested += () => EyedropperRequested?.Invoke(background);
        Number(parameterGrid, "StrokeWidth", L.Get("Inspector.StrokeWidth"), (p, v) => p.Style.StrokeWidth = v);
        Picker(parameterGrid, "HighlighterTip", L.Get("Color.Style"),
            [(L.Get("Highlighter.TipRound"), HighlighterTip.Round), (L.Get("Highlighter.TipFlat"), HighlighterTip.Flat)],
            (p, v) => p.Style.HighlighterTip = (HighlighterTip)v);
        Number(parameterGrid, "HighlighterTipAngle", L.Get("Highlighter.TipAngle"), (p, v) => p.Style.HighlighterTipAngle = v);
        var angleRow = rows["HighlighterTipAngle"];
        parameterGrid.Children.Remove(angleRow);
        angleRow.Margin = PropertyPanelLayout.RowMargin;
        highlighterAngleRegion = new(new StackPanel { Children = { angleRow } });
        rows.Add("HighlighterAngleRegion", highlighterAngleRegion);
        parameterGrid.Children.Add(highlighterAngleRegion);
        Number(parameterGrid, "HighlighterInkContrast", L.Get("Highlighter.InkContrast"), (p, v) => p.Style.HighlighterInkContrast = v / 100);
        Number(parameterGrid, "Opacity", L.Get("Inspector.Opacity"), (p, v) => p.Style.Opacity = v / 100);
        Number(parameterGrid, "FontSize", L.Get("Inspector.FontSize"), (p, v) => p.Style.FontSize = v);
        Number(parameterGrid, "NumberStart", L.Get("ToolOptions.NumberStart"), (p, v) => p.NumberStart = (int)v);
        Number(parameterGrid, "NumberStep", L.Get("ToolOptions.NumberStep"), (p, v) => p.NumberStep = (int)v);
        InitializeNumberReset();
        Number(parameterGrid, "NextNumber", L.Get("ToolOptions.NextNumber"), (_, _) => { }, resetNextNumber);
        AddNumberFormatRow();
        Number(parameterGrid, "DefaultWidth", L.Get("Inspector.Width"), (p, v) => p.DefaultWidth = v);
        Number(parameterGrid, "DefaultHeight", L.Get("Inspector.Height"), (p, v) => p.DefaultHeight = v);
        AddRow(parameterGrid, "FontFamily", L.Get("Inspector.FontFamily"), fontFamily);
        Picker(parameterGrid, "TextAlignment", L.Get("Inspector.TextAlignment"),
            [(L.Get("Inspector.AlignLeft"), FloatCanvas.Core.TextAlignment.Left), (L.Get("Inspector.AlignCenter"), FloatCanvas.Core.TextAlignment.Center),
             (L.Get("Inspector.AlignRight"), FloatCanvas.Core.TextAlignment.Right)], (p, v) => p.Style.TextAlignment = (FloatCanvas.Core.TextAlignment)v);
        Picker(parameterGrid, "StyleVariant", L.Get("Color.Style"),
            AppearanceRegistry.VariantsFor(ElementKind.Number).Select(variant => (L.Get(variant.LabelKey), (object)variant.Id)).ToArray(),
            (p, value) => ElementAppearance.SetVariant(p.Style, p.AppearanceKind!.Value, (string)value));
        SegmentedChoicePreviews.UpdateColorStyle(pickers["StyleVariant"], EditorTool.Number);
        ((FontPickerControl)fontFamily).FontCommitted += name =>
        {
            if (!refreshing) Change(p => p.Style.FontFamily = name);
        };
        Picker(parameterGrid, "LinePathMode", L.Get("Color.Style"),
            [(L.Get("Line.Straight"), LinePathMode.Straight), (L.Get("Line.Arc"), LinePathMode.Arc)],
            (p, v) => p.Style.LineArcBulge = (LinePathMode)v == LinePathMode.Arc ? ElementGeometry.DefaultLineArcBulge : null);
        Picker(parameterGrid, "StartArrowHead", L.Get("Inspector.StartArrow"), ArrowOptions(), (p, v) => p.Style.StartArrowHead = (ArrowHeadStyle)v);
        Picker(parameterGrid, "EndArrowHead", L.Get("Inspector.EndArrow"), ArrowOptions(), (p, v) => p.Style.EndArrowHead = (ArrowHeadStyle)v);
        Picker(parameterGrid, "StrokePattern", L.Get("Inspector.StrokePattern"),
            [(L.Get("Inspector.PatternSolid"), StrokePattern.Solid), (L.Get("Inspector.PatternDashed"), StrokePattern.Dashed),
             (L.Get("Inspector.PatternDotted"), StrokePattern.Dotted), (L.Get("Inspector.PatternDashDot"), StrokePattern.DashDot)],
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
            if (refreshing || linkColors.SelectedValue is not string variant) return;
            Change(p => p.Style.Variant = variant); Refresh();
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
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        InitializePresentationParameters();
        // All tool pages share these two viewports and the fixed status row.
        body.Children.Add(heading); body.Children.Add(parameters);
        standardContent = new(body, presets, () => new ToolOptionsHeader(ToolDisplayName.Get(controller.Tool),
            Target: controller.Tool == EditorTool.Presentation ? presentationRuntime.StatusText : "",
            Actions: controller.Tool switch
            {
                EditorTool.Freehand => freehandSmoothing,
                EditorTool.PointerPresentation => presentationGlobalSettings.HeaderActions,
                _ => null
            }), RefreshStandard, CancelStandardEditing);
        parameterScroll.Content = body;
        presetHost.Child = presets; recipeBorder.Child = presetHost;
        columns.Children.Add(parameterScroll); Grid.SetColumn(recipeBorder, 1); columns.Children.Add(recipeBorder);
        var layout = new DockPanel { Margin = WindowLayout.ContentMargin() };
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status); layout.Children.Add(columns); Content = layout;
        ClearStatus(); activeContent = standardContent;
        void Conceal() { refreshPending = true; CancelEditing(); }
        Unloaded += (_, _) => Conceal();
        IsVisibleChanged += (_, _) => { if (IsVisible) { if (refreshPending) RefreshNow(); } else Conceal(); };
        Refresh();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Bound the initial content-sized window, not its resizable content.
        // A MaxHeight on this panel centers it inside a taller manual window.
        if (double.IsPositiveInfinity(constraint.Height) || Window.GetWindow(this)?.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight)
            constraint.Height = Math.Min(constraint.Height, InitialMaximumHeight);
        return base.MeasureOverride(constraint);
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
        if (switchingContent) { refreshPending = true; return; }
        if (controller.Tool == EditorTool.Canvas && canvasContent is null)
        {
            CanvasContentFactory?.Invoke();
            if (canvasContent is null) { refreshPending = true; return; }
        }
        IToolOptionsContent next = controller.Tool == EditorTool.Canvas && canvasContent is not null ? canvasContent : standardContent;
        ActivateContent(next); ClearStatus(); next.PrepareForShow(); refreshPending = false; HeaderChanged?.Invoke();
    }

    private void RefreshStandard()
    {
        if (refreshing) return;
        // A numeric preview is already reflected in its field. The toolbar echoes
        // ParametersChanged synchronously; rebuilding here cancels our own capture.
        if (publishingNumericTool == controller.Tool && numericFields.Values.Any(field => field.IsInteracting)) return;
        refreshing = true;
        try
        {
            if (IsVisible && fontContextTool == controller.Tool && ReferenceEquals(fontContextSession, controller.Session))
                PropertyChangeRegion.ChangeTogether(Synchronize, linkedColorsRegion, highlighterAngleRegion);
            else Synchronize();
        }
        finally { refreshing = false; }

        void Synchronize()
        {
            foreach (var field in numericFields.Values) field.CancelGesture();
            ClearStatus();
            var p = controller.Parameters; var tool = p.Tool;
            p.NormalizeAppearance();
            var appearance = p.AppearanceKind is { } appearanceKind ? AppearanceRegistry.Find(appearanceKind, p.Style.Variant) : null;
            foreach (var (key, field) in numericFields)
            {
                var domain = NumericParameterDomain.ForTool(key, tool);
                field.SetInputRange(domain.Minimum, domain.Maximum);
                field.SetSliderRange(domain.RailMinimum, domain.RailMaximum);
                field.KeyboardStep = NumericKeyboardSteps.ForProperty(key, tool);
            }
            if (fontContextTool != tool || !ReferenceEquals(fontContextSession, controller.Session))
            {
                linkedColorsRegion.Reset();
                highlighterAngleRegion.Reset();
                ((FontPickerControl)fontFamily).ResetModelContext();
                fontContextTool = tool; fontContextSession = controller.Session;
            }
            heading.Text = L.Get("ToolDescription." + ToolPresetLibrary.Family(tool));
            bool line = tool is EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow;
            bool shape = tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Triangle or EditorTool.Symbol;
            bool stroke = line || shape || tool is EditorTool.Freehand or EditorTool.Highlighter;
            bool presentation = tool == EditorTool.Presentation;
            bool text = tool is EditorTool.Text or EditorTool.Number or EditorTool.Emoji || presentation;
            bool paint = stroke || tool is EditorTool.Text or EditorTool.Number || presentation;
            bool capture = tool == EditorTool.CaptureRegion;
            bool boxRegion=tool is EditorTool.Crop or EditorTool.Blur or EditorTool.Pixelate;
            paint |= capture;
            bool shapeStroke = shape && ElementAppearance.HasShapeStroke(p.Style);
            bool secondary = appearance?.BackgroundActive == true;
            Show("Foreground", paint && (capture || appearance?.ForegroundActive == true)); Show("Background", secondary); Show("LinkColors", shape);
            Show("StyleVariant", tool is EditorTool.Number or EditorTool.Text);
            Show("Colors", paint);
            foreground.Visibility = paint ? Visibility.Visible : Visibility.Collapsed;
            background.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
            GroupStyleStrokeRows(shape);
            foreground.SetPaletteContext(ToolPresetLibrary.Family(tool)); background.SetPaletteContext(ToolPresetLibrary.Family(tool));
            Show("StrokeWidth", shape ? shapeStroke : stroke); Show("Opacity", false);
            Show("HighlighterTip", tool == EditorTool.Highlighter);
            Show("HighlighterInkContrast", tool == EditorTool.Highlighter);
            Show("HighlighterAngleRegion", tool == EditorTool.Highlighter);
            Show("HighlighterTipAngle", tool == EditorTool.Highlighter && p.Style.HighlighterTip == HighlighterTip.Flat);
            Show("FontSize", text); Show("FontFamily", tool is EditorTool.Text or EditorTool.Number || presentation);
            RefreshNumberSequence(p);
            Show("NumberStart", tool == EditorTool.Number); Show("NumberStep", tool == EditorTool.Number); Show("NextNumber", tool == EditorTool.Number);
            Show("DefaultWidth", shape); Show("DefaultHeight", shape);
            Show("StartArrowHead", line); Show("EndArrowHead", line); Show("ArrowHeadSize", line); Show("StrokePattern", shape ? shapeStroke : stroke);
            Show("LinePathMode", line);
            Show("CornerRadius", tool is EditorTool.Rectangle or EditorTool.RoundedRectangle);
            Show("Size", shape); Show("Aspect", !shape && boxRegion && !presentation);
            Show("EffectStrength", tool is EditorTool.Blur or EditorTool.Pixelate);
            Show("TextAlignment", tool == EditorTool.Text);
            Show("Symbols", tool == EditorTool.Symbol); Show("Emoji", tool == EditorTool.Emoji);
            Show("CaptureShape", capture); Show("CaptureSource", capture); Show("CaptureBorderWidth", capture);
            noParameters.Visibility = paint || text || boxRegion || tool is EditorTool.PointerPresentation ? Visibility.Collapsed : Visibility.Visible;
            RefreshPresentationParameters(p);
            bool colorEcho = foreground.IsNotifyingOwner || background.IsNotifyingOwner;
            if (IsApplicable("Foreground"))
            {
                uint value = p.Style.PrimaryColor;
                if (colorEcho) foreground.Value = value; else foreground.SynchronizeModel(value);
                foreground.PrepareForShow();
            }
            if (IsApplicable("Background"))
            {
                if (colorEcho) background.Value = p.Style.Background; else background.SynchronizeModel(p.Style.Background);
                background.PrepareForShow();
            }
            if (IsApplicable("Size")) aspect.IsChecked = p.ConstrainAspect;
            if (IsApplicable("FontFamily")) ToolOptionPreviews.SelectFont(fontFamily, p.Style.FontFamily);
            if (IsApplicable("Aspect")) aspectSwitch.IsChecked = p.ConstrainAspect;
            if (IsApplicable("LinkColors"))
            {
                var context = (tool, p.Symbol);
                if (colorStyleContext != context) { SegmentedChoicePreviews.UpdateColorStyle(linkColors, tool, p.Symbol); colorStyleContext = context; }
                linkColors.SelectedValue = p.Style.Variant;
            }
            if (IsApplicable("StyleVariant"))
            {
                SegmentedChoicePreviews.SetStyleFamily(pickers["StyleVariant"], p.AppearanceKind!.Value);
                SetPicker("StyleVariant", p.Style.Variant!);
            }
            if (IsApplicable("Foreground"))
                SetColorLabel("Foreground", foreground, capture ? "Snapshot.BorderColor" :
                    p.AppearanceKind is { } primaryKind ? AppearanceRegistry.ColorLabel(primaryKind, p.Style, false) : "Color.Unified");
            if (IsApplicable("Background"))
                SetColorLabel("Background", background, AppearanceRegistry.ColorLabel(p.AppearanceKind!.Value, p.Style, true));
            SetPicker("CaptureShape", p.CaptureShape); SetPicker("CaptureSource", p.CaptureSource); SetNumber("CaptureBorderWidth", p.CaptureBorderWidth);
            SetNumber("StrokeWidth", tool == EditorTool.Highlighter ? Math.Max(16, p.Style.StrokeWidth) : p.Style.StrokeWidth); SetNumber("Opacity", p.Style.Opacity * 100); SetNumber("FontSize", p.Style.FontSize);
            SetNumber("EffectStrength", p.EffectStrength); SetNumber("CornerRadius", p.CornerRadius ?? 0); SetNumber("ArrowHeadSize", p.Style.ArrowHeadSize);
            SetNumber("DefaultWidth", p.DefaultWidth); SetNumber("DefaultHeight", p.DefaultHeight);
            SetNumber("NumberStart", p.NumberStart); SetNumber("NumberStep", p.NumberStep); SetNumber("NextNumber", controller.Session.Document.NextNumber);
            if (tool == EditorTool.RoundedRectangle && p.CornerRadius is null) numericFields["CornerRadius"].ShowPlaceholder("—");
            SetPicker("StartArrowHead", p.Style.StartArrowHead ?? (tool == EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None));
            SetPicker("EndArrowHead", p.Style.EndArrowHead ?? (tool is EditorTool.Arrow or EditorTool.DoubleArrow ? p.Style.ArrowHeadStyle : ArrowHeadStyle.None));
            SetPicker("StrokePattern", p.Style.StrokePattern);
            SetPicker("LinePathMode", p.Style.LineArcBulge.HasValue ? LinePathMode.Arc : LinePathMode.Straight);
            SetPicker("HighlighterTip", p.Style.HighlighterTip);
            SetNumber("HighlighterTipAngle", p.Style.HighlighterTipAngle);
            SetNumber("HighlighterInkContrast", 100 * p.Style.HighlighterInkContrast);
            SetPicker("TextAlignment", p.Style.TextAlignment);
            if (IsApplicable("Symbols")) HighlightGallery(symbolGallery, p.Symbol);
            if (IsApplicable("Emoji")) HighlightGallery(emojiGallery, p.Emoji);
            if (IsApplicable("FontSize")) captions["FontSize"].Text = L.Get(tool is EditorTool.Emoji or EditorTool.Number ? "ToolOptions.Size" : "Inspector.FontSize");
            presets.PrepareForShow();
            LayoutParameters();
            refreshPending=false;
        }
    }

    private void GroupStyleStrokeRows(bool grouped)
    {
        Panel target = grouped ? colorRows : parameterGrid;
        foreach (var key in new[] { "StrokeWidth", "StrokePattern" })
        {
            var row = rows[key];
            if (ReferenceEquals(row.Parent, target)) continue;
            ((Panel)row.Parent).Children.Remove(row);
            target.Children.Add(row); row.Margin = PropertyPanelLayout.RowMargin;
        }
    }

    private void LayoutParameters()
    {
        foreach (var section in parameterGrid.Children.OfType<FrameworkElement>().Where(item => Equals(item.Tag, "ToolSection")).ToArray()) parameterGrid.Children.Remove(section);
        parameterGrid.RowDefinitions.Clear();
        string[][] groups = [ ["PresentationRuntime"], ["Aspect"], ["CaptureShape"], ["CaptureSource"], ["LinkColors"], ["StyleVariant"], ["LinePathMode"],
            ["HighlighterTip"], ["HighlighterAngleRegion"], ["HighlighterInkContrast"], ["Colors"], ["CaptureBorderWidth"], ["StrokeWidth"], ["CornerRadius"], ["Opacity"],
            ["FontFamily"], ["FontSize"], ["NumberFormat"], ["TextAlignment"], ["NumberStart"], ["NumberStep"], ["NextNumber"], ["StartArrowHead"], ["EndArrowHead"],
            ["StrokePattern"], ["ArrowHeadSize"], ["EffectStrength"], ["Symbols"], ["Size"], ["Emoji"], ["PresentationSettings"], ["PointerPresentationSettings"] ];
        if (controller.Tool == EditorTool.Presentation)
            groups = [["PresentationSettings"], ["Colors"], ["Opacity"], ["FontFamily"], ["FontSize"], ["Size"]];
        foreach (var group in groups)
        {
            var visible = group.Select(key => rows[key]).Where(row => row.Visibility == Visibility.Visible && row.Parent == parameterGrid).ToArray();
            if (visible.Length == 0) continue;
            if (controller.Tool == EditorTool.Presentation && group[0] is "PresentationSettings" or "Colors" or "PresentationRuntime")
            {
                int sectionRow = parameterGrid.RowDefinitions.Count; parameterGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var section = ToolOptionsLayout.Section(group[0] switch { "PresentationSettings" => "ToolOptions.Section.Display", "Colors" => "ToolOptions.Section.Appearance", _ => "ToolOptions.Section.Runtime" }, sectionRow == 0);
                section.Tag = "ToolSection"; Grid.SetRow(section, sectionRow); Grid.SetColumnSpan(section, 3); parameterGrid.Children.Add(section);
            }
            int rowIndex = parameterGrid.RowDefinitions.Count; parameterGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int column = 0; column < visible.Length; column++)
            {
                var row = visible[column]; Grid.SetRow(row, rowIndex); Grid.SetColumn(row, column);
                Grid.SetColumnSpan(row, 3);
                if (row is not PropertyChangeRegion) row.Margin = PropertyPanelLayout.RowMargin;
            }
        }
    }
    internal bool IsColorPickerOpen => canvasContent?.IsColorPickerOpen == true || foreground.IsPickerOpen || background.IsPickerOpen || presentationGlobalSettings.IsColorPickerOpen;
    internal bool CloseColorPicker() => canvasContent?.TryCloseColorPicker() == true || foreground.TryClosePicker() || background.TryClosePicker() || presentationGlobalSettings.TryClosePicker();
    internal bool OwnsColorPickerInput(DependencyObject? source) => canvasContent?.OwnsColorPickerInput(source) == true || foreground.OwnsPickerInput(source) || background.OwnsPickerInput(source) || presentationGlobalSettings.OwnsColorPickerInput(source);
    internal void RefreshPresets()
    { if (controller.Tool == EditorTool.Canvas) canvasContent?.PrepareForShow(); else if(IsVisible)presets.Refresh();else refreshPending=true; }
    internal void RefreshDocumentState()
    {
        if(!IsVisible){refreshPending=true;return;}
        if (controller.Tool != EditorTool.Number) return;
        RefreshNumberReset();
        if (controller.Session.IsTransactionActive || numericFields["NextNumber"].IsKeyboardFocusWithin) return;
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
            controller.ApplyParameters(next, followNumberStart); ClearStatus();
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

    internal void Error(string message) { if (string.IsNullOrEmpty(message)) { ClearStatus(); return; } status.Visibility = Visibility.Visible; status.Text = "⚠ " + message; status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); }
    private void Show(string key, bool visible) => rows[key].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private void SetColorLabel(string key, ColorField field, string resource) => field.SetColorLabel(captions[key], resource);
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
    private void AddRow(Panel body, string key, string label, FrameworkElement control, Button? trailingAction = null)
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
            row = PropertyPanelLayout.Row(caption, trailingAction is null ? control : PropertyPanelLayout.WithTrailingAction(control, trailingAction));
            if (control is NumericValueField number) number.SetLabel(caption);
        }
        else { var stack = new StackPanel(); stack.Children.Add(caption); stack.Children.Add(control); row = stack; }
        row.Margin = new(0, 4, 0, 4);
        AutomationProperties.SetAutomationId(row, key);
        AutomationProperties.SetName(control, label); rows.Add(key, row); body.Children.Add(row);
        if (control is NumericValueField numeric) { AutomationProperties.SetName(numeric.Input, label); AutomationProperties.SetName(numeric.Slider, label); }
    }
    private void Number(Panel body, string key, string label, Action<ToolParameters, double> set, Button? trailingAction = null)
    {
        var domain = NumericParameterDomain.ForTool(key);
        var field = new NumericValueField(domain.Minimum, domain.Maximum, domain.RailMinimum, domain.RailMaximum, domain.Step, domain.Integer)
        { Unit = key switch { "HighlighterTipAngle" => "°", "Opacity" or "HighlighterInkContrast" => "%", "NumberStart" or "NextNumber" => "#", "NumberStep" => L.Get("Numeric.Unit.Step"), _ => "px" } };
        if (key is "NumberStart" or "NumberStep" or "NextNumber") field.Slider.IsSnapToTickEnabled = true;
        var box = field.Input; numbers.Add(key, box); numericFields.Add(key, field); AddRow(body, key, label, field, trailingAction);
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
                else SetNextNumber((int)value);
                ClearStatus();
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
        picker.SelectionChanged += (_, _) =>
        {
            if (refreshing || picker.SelectedValue is not { } value) return;
            Change(p => set(p, value));
            if (key is "HighlighterTip" or "StyleVariant") Refresh();
        };
    }

    private static (string, object)[] ArrowOptions() =>
        [(L.Get("Inspector.ArrowFilledTriangle"), ArrowHeadStyle.FilledTriangle), (L.Get("Inspector.ArrowOpen"), ArrowHeadStyle.Open),
         (L.Get("Inspector.ArrowDiamond"), ArrowHeadStyle.Diamond), (L.Get("Inspector.ArrowNone"), ArrowHeadStyle.None)];
}
