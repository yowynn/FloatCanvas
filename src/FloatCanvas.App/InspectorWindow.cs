using System.Globalization;
using System.Text.Json;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Rendering;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Automation = System.Windows.Automation;

namespace FloatCanvas.App;

/// <summary>Atomic property edits for the current selection; layers live in their own window.</summary>
internal sealed class InspectorWindow : Wpf.Window
{
    private readonly Func<EditorSession> getSession;
    private readonly Func<bool> canMutate;
    private readonly Dictionary<string, Controls.TextBox> fields = [];
    private readonly Dictionary<string, NumericValueField> numericFields = [];
    private readonly Dictionary<string, ColorField> colorFields = [];
    private readonly Dictionary<string, Wpf.FrameworkElement> fieldContainers = [];
    private readonly HashSet<string> dirtyFields = [];
    private readonly Controls.StackPanel propertyPanel = new();
    private readonly Controls.TextBlock selectionLabel = new() { TextWrapping = Wpf.TextWrapping.Wrap, FontWeight = Wpf.FontWeights.SemiBold };
    private readonly Controls.TextBlock status = new() { TextWrapping = Wpf.TextWrapping.Wrap, MinHeight = 28, FontSize = 11 };
    private readonly Controls.Border textSection;
    private readonly Controls.CheckBox keepAspect = new() { Content = L.Get("Inspector.KeepAspect"), MinHeight = 30 };
    private readonly Controls.ComboBox fontPicker = ToolOptionPreviews.FontPicker();
    private readonly Controls.ComboBox alignment = new() { MinHeight = 34, Margin = new(0, 4, 5, 4) };
    private readonly Controls.ComboBox arrowStyle = new() { MinHeight = 34, Margin = new(0, 4, 5, 4) };
    private readonly Controls.ComboBox startArrow = new() { MinHeight = 34 };
    private readonly Controls.ComboBox endArrow = new() { MinHeight = 34 };
    private readonly Controls.ComboBox strokePattern = new() { MinHeight = 34 };
    private readonly List<NumericScrubber> scrubbers = [];
    private EditorSession? formSession;
    private Guid? formElementId;
    private string? formFingerprint;
    private bool refreshing = true;
    private EditorSession? scrubSession;
    private CanvasElement? scrubSource;
    private double scrubStartValue;
    private long scrubTransaction;

    public event Action? Dismissed;
    public event Action<int>? CommandKey;
    public event Action<ColorField>? EyedropperRequested;

    public InspectorWindow(Func<EditorSession> getSession, Func<bool>? canMutate = null, ColorLibrary? colorLibrary = null)
    {
        this.getSession = getSession;
        this.canMutate = canMutate ?? (() => true);
        Title = L.Get("Inspector.Heading");
        Width = 340; Height = 700; MinWidth = 300; MinHeight = 420;
        FontSize = 14; FontFamily = new Media.FontFamily("Microsoft YaHei UI");
        Topmost = true; ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, Wpf.SystemColors.WindowBrushKey);
        SetResourceReference(ForegroundProperty, Wpf.SystemColors.WindowTextBrushKey);

        var layout = new Controls.Grid();
        layout.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        Content = layout;
        var body = new Controls.StackPanel { Margin = new Wpf.Thickness(12, 4, 12, 8) };
        var scroll = new Controls.ScrollViewer { Content = body, VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Controls.ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll);
        selectionLabel.FontSize = 12;
        selectionLabel.Margin = new(0, 2, 0, 8);
        body.Children.Add(selectionLabel);
        body.Children.Add(propertyPanel);
        propertyPanel.Children.Add(SectionHeading("Inspector.GeometrySection", L.Get("Inspector.GeometryHint"), true));
        propertyPanel.Children.Add(FieldPair("X", "X", "Y", "Y"));
        propertyPanel.Children.Add(FieldPair("W", L.Get("Inspector.WidthShort"), "H", L.Get("Inspector.HeightShort")));
        propertyPanel.Children.Add(FieldPair("Angle", L.Get("Inspector.AngleDegrees"), null, null));
        propertyPanel.Children.Add(keepAspect);
        propertyPanel.Children.Add(SectionHeading("Inspector.AppearanceSection"));
        propertyPanel.Children.Add(FieldPair("StrokeWidth", L.Get("Inspector.StrokeWidth"), "CornerRadius", L.Get("Inspector.CornerRadius")));
        foreach (var (label, value) in new[] { (L.Get("Inspector.ArrowFilledTriangle"), ArrowHeadStyle.FilledTriangle), (L.Get("Inspector.ArrowOpen"), ArrowHeadStyle.Open), (L.Get("Inspector.ArrowDiamond"), ArrowHeadStyle.Diamond), (L.Get("Inspector.ArrowNone"), ArrowHeadStyle.None) })
            arrowStyle.Items.Add(new Controls.ComboBoxItem { Content = ToolOptionPreviews.Choice(label, value) });
        Automation.AutomationProperties.SetName(arrowStyle, L.Get("Inspector.ArrowStyle"));
        arrowStyle.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            dirtyFields.Add("ArrowHeadStyle"); SetStatus(L.Get("Inspector.DraftRetained"), false);
        };
        var arrowRow = new Controls.StackPanel();
        arrowRow.Children.Add(new Controls.TextBlock { Text = L.Get("Inspector.ArrowStyle"), FontSize = 12, Margin = new(0, 4, 0, 2) }); arrowRow.Children.Add(arrowStyle);
        fieldContainers.Add("ArrowHeadStyle", arrowRow); propertyPanel.Children.Add(arrowRow);
        AddPicker("StartArrowHead", L.Get("Inspector.StartArrow"), startArrow,
            [L.Get("Inspector.ArrowFilledTriangle"), L.Get("Inspector.ArrowOpen"), L.Get("Inspector.ArrowDiamond"), L.Get("Inspector.ArrowNone")]);
        AddPicker("EndArrowHead", L.Get("Inspector.EndArrow"), endArrow,
            [L.Get("Inspector.ArrowFilledTriangle"), L.Get("Inspector.ArrowOpen"), L.Get("Inspector.ArrowDiamond"), L.Get("Inspector.ArrowNone")]);
        AddPicker("StrokePattern", L.Get("Inspector.StrokePattern"), strokePattern,
            [L.Get("Inspector.PatternSolid"), L.Get("Inspector.PatternDashed"), L.Get("Inspector.PatternDotted")]);
        propertyPanel.Children.Add(FieldPair("ArrowHeadSize", L.Get("Inspector.ArrowHeadSize"), null, null));
        var arrowHint = new Controls.TextBlock { Text = L.Get("Inspector.ArrowHeadSizeHint"), FontSize = 11, Opacity = .65 };
        fieldContainers.Add("ArrowHint", arrowHint); propertyPanel.Children.Add(arrowHint);
        var colors = new Controls.StackPanel();
        var strokeColor = CreateColorField("Stroke", L.Get("Inspector.Stroke"), ColorRole.Foreground, colorLibrary);
        strokeColor.Margin = new(0, 4, 0, 4);
        colors.Children.Add(strokeColor);
        var fillColor = CreateColorField("Fill", L.Get("Inspector.Fill"), ColorRole.Background, colorLibrary);
        colors.Children.Add(fillColor);
        propertyPanel.Children.Add(colors);
        var colorHint = new Controls.TextBlock { Text = L.Get("Inspector.ColorHint"), FontSize = 11, Opacity = .65, Margin = new(0, 2, 0, 6) };
        fieldContainers.Add("ColorHint", colorHint); propertyPanel.Children.Add(colorHint);
        propertyPanel.Children.Add(FieldPair("Opacity", L.Get("Inspector.OpacityPercent"), null, null));
        textSection = SectionHeading("Inspector.TextSection");
        propertyPanel.Children.Add(textSection);
        propertyPanel.Children.Add(FieldPair("FontSize", L.Get("Inspector.FontSize"), "Number", L.Get("Editor.ElementKind.Number")));
        var fontRow = new Controls.StackPanel { Margin = new(0, 4, 0, 4) };
        fontRow.Children.Add(new Controls.TextBlock { Text = L.Get("Inspector.FontFamily"), FontSize = 12, Margin = new(0, 0, 0, 3) });
        var fontValue = Field("FontFamily", L.Get("Inspector.FontFamilyName"));
        fontPicker.SelectionChanged += (_, _) => { if (!refreshing && fontPicker.SelectedItem is Controls.ComboBoxItem item && item.Tag is string name) fontValue.Text = name; };
        fontValue.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(fontValue.Text)) ToolOptionPreviews.SelectFont(fontPicker, fontValue.Text); };
        Automation.AutomationProperties.SetName(fontPicker, L.Get("Inspector.FontFamily"));
        fontRow.Children.Add(fontPicker); fieldContainers.Add("FontFamily", fontRow); propertyPanel.Children.Add(fontRow);
        foreach (var label in new[] { L.Get("Inspector.AlignLeft"), L.Get("Inspector.AlignCenter"), L.Get("Inspector.AlignRight") }) alignment.Items.Add(label);
        Automation.AutomationProperties.SetName(alignment, L.Get("Inspector.TextAlignment"));
        alignment.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            dirtyFields.Add("TextAlignment"); SetStatus(L.Get("Inspector.DraftRetained"), false);
        };
        propertyPanel.Children.Add(alignment);
        fieldContainers.Add("TextAlignment", alignment);
        propertyPanel.Children.Add(LabeledField("Text", L.Get("Inspector.TextContent")));
        var text = fields["Text"];
        text.AcceptsReturn = true; text.TextWrapping = Wpf.TextWrapping.Wrap;
        text.Height = 76; text.VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto;
        var footer = new Controls.StackPanel();
        var footerBorder = new Controls.Border { Child = footer, BorderThickness = new(0, 1, 0, 0), Padding = new(12, 8, 26, 8) };
        footerBorder.SetResourceReference(Controls.Border.BorderBrushProperty, "CanvasBorder");
        Controls.Grid.SetRow(footerBorder, 1);
        layout.Children.Add(footerBorder);
        var applyRow = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        var apply = ActionIconButton.Create(PanelActionIcon.Apply, L.Get("Inspector.Apply"), Apply, "Ctrl+Enter");
        applyRow.Children.Add(apply);
        var reload = ActionIconButton.Create(PanelActionIcon.Refresh, L.Get("Inspector.ReloadSelection"), () =>
        {
            if (!this.canMutate()) { SetStatus(L.Get("Inspector.BusyDraftRetained"), true); return; }
            LoadSelection(true);
        });
        applyRow.Children.Add(reload);
        footer.Children.Add(applyRow);
        status.Margin = new(0, 6, 0, 0);
        footer.Children.Add(status);
        Closing += (_, e) => { e.Cancel = true; CancelScrub(); CloseColorPickers(); Hide(); Dismissed?.Invoke(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseColorPickers(); };
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Input.Key.System ? e.SystemKey : e.Key;
            if (key == Input.Key.Escape) { HandleEscape(); e.Handled = true; return; }
            if (key == Input.Key.Enter && (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0) { Apply(); e.Handled = true; return; }
            bool textInput = Input.Keyboard.FocusedElement is Controls.TextBox || IsTextInput(e.OriginalSource as Wpf.DependencyObject);
            if (RouteCommandKey(Input.KeyInterop.VirtualKeyFromKey(key), (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0, textInput)) e.Handled = true;
        };
        AppWindowChrome.Apply(this); Icon = AppIcon.WindowImage;
        refreshing = false;
        Refresh();
    }

    /// <summary>Safe during high-frequency scene notifications; preserves unsubmitted editor text.</summary>
    public void Refresh()
    {
        if (!canMutate()) return;
        var session = getSession();
        if (session.IsTransactionActive) return;
        LoadSelection(false);
    }

    private void LoadSelection(bool discardDraft)
    {
        var session = getSession();
        var selected = session.SelectedElements().ToArray();
        var element = selected.Length == 1 ? selected[0] : null;
        var sameElement = ReferenceEquals(formSession, session) && formElementId == element?.Id;
        if (!discardDraft && (dirtyFields.Count > 0 ||
            propertyPanel.IsKeyboardFocusWithin && IsTextInput(Input.Keyboard.FocusedElement as Wpf.DependencyObject)))
        {
            if (!sameElement)
            {
                selectionLabel.Text = L.Get("Inspector.SelectionChangedDraftRetained");
                selectionLabel.Visibility = Wpf.Visibility.Visible;
                SetStatus(L.Get("Inspector.ReturnOrReload"), false);
            }
            return;
        }
        var fingerprint = element is null ? null : JsonSerializer.Serialize(element);
        if (!discardDraft && sameElement && fingerprint == formFingerprint) return;
        refreshing = true;
        try
        {
            formSession = session; formElementId = element?.Id; formFingerprint = fingerprint;
            dirtyFields.Clear();
            propertyPanel.IsEnabled = element is { Locked: false };
            SetPropertyVisibility(element);
            selectionLabel.Text = element is null ? selected.Length > 1 ? L.Format("Inspector.MultipleSelection", selected.Length) : L.Get("Inspector.SelectElement")
                : L.Format("Inspector.ElementSelection", ElementName(element), element.Locked ? L.Get("Inspector.LockedSuffix") : "", !element.Visible ? L.Get("Inspector.HiddenSuffix") : "");
            selectionLabel.Visibility = element is null ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            AppWindowChrome.SetHeaderDetail(this, element is null ? "" : selectionLabel.Text);
            foreach (var box in fields.Values) box.Text = "";
            colorFields["Stroke"].Value = element?.Style.Stroke ?? 0xFF2383E2;
            colorFields["Fill"].Value = element?.Style.Fill ?? 0;
            if (element is null) { SetStatus(L.Get("Inspector.SelectHiddenOrLocked"), false); return; }
            var bounds = ElementGeometry.Bounds(element);
            var size = GeometricSize(element);
            SetField("X", bounds.Left); SetField("Y", bounds.Top); SetField("W", size.X); SetField("H", size.Y);
            SetField("Angle", Angle(element.Transform)); SetField("StrokeWidth", element.Style.StrokeWidth);
            arrowStyle.SelectedIndex = (int)element.Style.ArrowHeadStyle; SetField("ArrowHeadSize", element.Style.ArrowHeadSize);
            startArrow.SelectedIndex = (int)ElementGeometry.ArrowStyle(element, reverse: true);
            endArrow.SelectedIndex = (int)ElementGeometry.ArrowStyle(element);
            strokePattern.SelectedIndex = (int)element.Style.StrokePattern;
            SetField("CornerRadius", ElementGeometry.CornerRadius(element));
            SetField("Opacity", element.Style.Opacity * 100); SetField("FontSize", element.Style.FontSize);
            fields["Text"].Text = element.Text;
            fields["Text"].IsEnabled = element.Kind == ElementKind.Text;
            fields["Number"].Text = element.Number.ToString(CultureInfo.InvariantCulture);
            fields["Number"].IsEnabled = element.Kind == ElementKind.Number;
            fields["FontSize"].IsEnabled = element.Kind is ElementKind.Text or ElementKind.Number;
            fields["FontFamily"].Text = element.Style.FontFamily;
            fields["FontFamily"].IsEnabled = fontPicker.IsEnabled = element.Kind is ElementKind.Text or ElementKind.Number;
            alignment.IsEnabled = element.Kind == ElementKind.Text;
            alignment.SelectedIndex = (int)element.Style.TextAlignment;
            keepAspect.IsChecked = element.Kind is ElementKind.Image or ElementKind.Text or ElementKind.Number;
            SetStatus(element.Locked ? L.Get("Inspector.UnlockToEdit") : L.Get("Inspector.ApplyHint"), false);
        }
        finally { refreshing = false; }
    }

    private void Apply()
    {
        try
        {
            if (!canMutate()) throw new InvalidOperationException(L.Get("Inspector.BusyDraftRetained"));
            var session = getSession();
            var selected = session.SelectedElements().ToArray();
            if (!ReferenceEquals(session, formSession) || selected.Length != 1 || selected[0].Id != formElementId)
                throw new InvalidOperationException(L.Get("Inspector.SelectionChanged"));
            var source = selected[0];
            if (source.Locked) throw new InvalidOperationException(L.Get("Inspector.ElementLocked"));
            if (session.IsTransactionActive) throw new InvalidOperationException(L.Get("Inspector.FinishDrawing"));
            if (dirtyFields.Count == 0) { SetStatus(L.Get("Inspector.NoChanges"), false); return; }
            if (JsonSerializer.Serialize(source) != formFingerprint)
                throw new InvalidOperationException(L.Get("Inspector.ElementChanged"));
            var edited = source.Clone();
            if (dirtyFields.Contains("Stroke")) edited.Style.Stroke = colorFields["Stroke"].Value;
            if (dirtyFields.Contains("Fill")) edited.Style.Fill = colorFields["Fill"].Value;
            if (dirtyFields.Contains("StrokeWidth")) edited.Style.StrokeWidth = Number("StrokeWidth", L.Get("Inspector.StrokeWidth"), 0, 512);
            if (source.Kind is ElementKind.Arrow or ElementKind.DoubleArrow)
            {
                if (dirtyFields.Contains("ArrowHeadStyle"))
                {
                    if (arrowStyle.SelectedIndex is < 0 or > 3) throw new InvalidOperationException(L.Get("Inspector.InvalidArrowStyle"));
                    edited.Style.ArrowHeadStyle = (ArrowHeadStyle)arrowStyle.SelectedIndex;
                }
            }
            if (source.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow)
            {
                if (dirtyFields.Contains("StartArrowHead")) edited.Style.StartArrowHead = (ArrowHeadStyle)startArrow.SelectedIndex;
                if (dirtyFields.Contains("EndArrowHead")) edited.Style.EndArrowHead = (ArrowHeadStyle)endArrow.SelectedIndex;
                if (dirtyFields.Contains("ArrowHeadSize")) edited.Style.ArrowHeadSize = Number("ArrowHeadSize", L.Get("Inspector.ArrowSize"), 0, 512);
            }
            if (dirtyFields.Contains("StrokePattern")) edited.Style.StrokePattern = (StrokePattern)strokePattern.SelectedIndex;
            if (dirtyFields.Contains("CornerRadius")) edited.CornerRadius = Number("CornerRadius", L.Get("Inspector.CornerRadius"), 0, 10000000);
            if (dirtyFields.Contains("Opacity")) edited.Style.Opacity = Number("Opacity", L.Get("Inspector.Opacity"), 0, 100) / 100;
            if (dirtyFields.Contains("FontSize") && source.Kind is ElementKind.Text or ElementKind.Number)
                edited.Style.FontSize = Number("FontSize", L.Get("Inspector.FontSize"), 1, 1024);
            if (dirtyFields.Contains("FontFamily") && source.Kind is ElementKind.Text or ElementKind.Number)
            {
                var family = fields["FontFamily"].Text.Trim();
                if (family.Length == 0 || family.Length > 200) throw new InvalidOperationException(L.Get("Inspector.InvalidFontFamily"));
                edited.Style.FontFamily = family;
            }
            if (dirtyFields.Contains("TextAlignment") && source.Kind == ElementKind.Text && alignment.SelectedIndex is >= 0 and <= 2)
                edited.Style.TextAlignment = (FloatCanvas.Core.TextAlignment)alignment.SelectedIndex;
            if (dirtyFields.Contains("Text") && source.Kind == ElementKind.Text) edited.Text = fields["Text"].Text;
            if (source.Kind == ElementKind.Text && dirtyFields.Overlaps(["Text", "FontFamily", "FontSize"]))
            {
                using var renderer = new SceneRenderer();
                var metrics = renderer.MeasureText(edited.Text, edited.Style, edited.Width);
                edited.Height = Math.Max(1, metrics.Height);
                if (EmojiGlyphs.IsEmoji(edited.Text, edited.Style)) edited.Width = metrics.Width;
            }
            if (source.Kind == ElementKind.Number && dirtyFields.Contains("FontSize"))
            {
                double diameter = Math.Max(12, edited.Style.FontSize / .58);
                edited.Transform = edited.Transform.Compose(Affine2D.Translation((edited.Width - diameter) / 2, (edited.Height - diameter) / 2));
                edited.Width = edited.Height = diameter;
            }
            if (dirtyFields.Contains("Number") && source.Kind == ElementKind.Number)
            {
                if (!int.TryParse(fields["Number"].Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value < 1 || value > 1_000_000)
                    throw new InvalidOperationException(L.Get("Inspector.InvalidNumber"));
                edited.Number = value;
            }
            if (dirtyFields.Contains("Angle"))
            {
                var requested = Number("Angle", L.Get("Inspector.Angle"), -360000, 360000);
                var center = ElementGeometry.Bounds(edited).Center;
                edited.Transform = Affine2D.Around(center, Affine2D.Rotation(requested - Angle(edited.Transform))).Compose(edited.Transform);
            }
            var size = GeometricSize(edited);
            var resizeX = dirtyFields.Contains("W"); var resizeY = dirtyFields.Contains("H");
            if (resizeX || resizeY)
            {
                if ((resizeX && size.X < 1e-9) || (resizeY && size.Y < 1e-9))
                    throw new InvalidOperationException(L.Get("Inspector.ZeroSize"));
                double sx = resizeX ? Number("W", L.Get("Inspector.Width"), .01, 10000000) / size.X : 1;
                double sy = resizeY ? Number("H", L.Get("Inspector.Height"), .01, 10000000) / size.Y : 1;
                if (keepAspect.IsChecked == true)
                {
                    if (resizeX && resizeY && Math.Abs(sx - sy) > Math.Max(sx, sy) * .005)
                        throw new InvalidOperationException(L.Get("Inspector.AspectMismatch"));
                    sx = sy = resizeX ? sx : sy;
                }
                if (ElementGeometry.IsBoxShape(edited.Kind)) ElementGeometry.ResizeShapeGeometry(edited, sx, sy);
                else edited.Transform = edited.Transform.Compose(Affine2D.Around(ElementGeometry.LocalBounds(edited).Center, Affine2D.Scale(sx, sy)));
            }
            var bounds = ElementGeometry.Bounds(edited);
            double dx = dirtyFields.Contains("X") ? Number("X", "X", -10000000, 10000000) - bounds.Left : 0;
            double dy = dirtyFields.Contains("Y") ? Number("Y", "Y", -10000000, 10000000) - bounds.Top : 0;
            edited.Transform = Affine2D.Translation(dx, dy).Compose(edited.Transform);
            session.UpdateSelection(element =>
            {
                element.Transform = edited.Transform;
                element.Style = edited.Style.Clone();
                element.Text = edited.Text; element.Number = edited.Number;
                element.Width = edited.Width; element.Height = edited.Height;
                element.CornerRadius = edited.CornerRadius;
            }, L.Get("Inspector.History.EditProperties"));
            dirtyFields.Clear();
            LoadSelection(true);
            SetStatus(L.Get("Inspector.PropertiesApplied"), false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }


    private static Controls.Border SectionHeading(string key, string? hint = null, bool first = false)
    {
        var label = new Controls.TextBlock { Text = L.Get(key), FontSize = 11, FontWeight = Wpf.FontWeights.SemiBold };
        label.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted");
        var section = new Controls.Border { Child = label, Margin = new(0, first ? 0 : 10, 0, 5),
            Padding = new(0, first ? 2 : 9, 0, 0), BorderThickness = new(0, first ? 0 : 1, 0, 0) };
        section.SetResourceReference(Controls.Border.BorderBrushProperty, "CanvasBorder");
        if (hint is not null)
        {
            section.ToolTip = hint;
            Automation.AutomationProperties.SetHelpText(section, hint);
        }
        return section;
    }

    private Controls.Grid FieldPair(string first, string firstLabel, string? second, string? secondLabel)
    {
        var grid = new Controls.Grid { Margin = new(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        Add(first, firstLabel, 0);
        if (second != null) Add(second, secondLabel!, 1);
        return grid;

        void Add(string key, string label, int column)
        {
            var cell = new Controls.Grid();
            cell.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(58) });
            cell.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
            Controls.Grid.SetColumn(cell, column); if (second == null) Controls.Grid.SetColumnSpan(cell, 2);
            grid.Children.Add(cell); fieldContainers.Add(key, cell);
            var caption = new Controls.TextBlock { Text = label, FontSize = 12, VerticalAlignment = Wpf.VerticalAlignment.Center };
            cell.Children.Add(caption);
            if (key is "X" or "Y" or "W" or "H" or "Angle" or "StrokeWidth" or "CornerRadius")
            {
                caption.ToolTip = L.Get("Inspector.DragNumeric");
                Automation.AutomationProperties.SetName(caption, label);
                Automation.AutomationProperties.SetHelpText(caption, L.Get("Inspector.DragNumeric"));
                scrubbers.Add(new NumericScrubber(caption, () => BeginScrub(key), delta => UpdateScrub(key, delta), FinishScrub));
            }
            var field = Field(key, label);
            var limits = key switch
            {
                "X" or "Y" => (-10000000d, 10000000d, -8192d, 8192d, 1d),
                "W" or "H" => (0d, 10000000d, 0d, 1000d, 1d),
                "Angle" => (-360000d, 360000d, -180d, 180d, 1d),
                "StrokeWidth" => (0d, 512d, 0d, 50d, .1d),
                "CornerRadius" => (0d, 10000000d, 0d, 256d, 1d),
                "ArrowHeadSize" => (0d, 512d, 0d, 128d, 1d),
                "Opacity" => (0d, 100d, 0d, 100d, 1d),
                "FontSize" => (1d, 4096d, 8d, 144d, 1d),
                _ => (1d, 1000000d, 1d, 100d, 1d)
            };
            var numeric = new NumericValueField(field, limits.Item1, limits.Item2, limits.Item3, limits.Item4, limits.Item5);
            if (key == "Number") numeric.Slider.IsSnapToTickEnabled = true;
            Automation.AutomationProperties.SetName(numeric.Slider, label);
            numericFields.Add(key, numeric);
            if (key == "StrokeWidth")
            {
                var previewColumn = new Controls.StackPanel(); previewColumn.Children.Add(numeric); previewColumn.Children.Add(ToolOptionPreviews.StrokeWidth(numeric));
                Controls.Grid.SetColumn(previewColumn, 1); cell.Children.Add(previewColumn);
            }
            else { Controls.Grid.SetColumn(numeric, 1); cell.Children.Add(numeric); }
        }
    }

    private void AddPicker(string key, string label, Controls.ComboBox picker, string[] options)
    {
        for (int index = 0; index < options.Length; index++)
        {
            object value = key == "StrokePattern" ? (StrokePattern)index : (ArrowHeadStyle)index;
            picker.Items.Add(new Controls.ComboBoxItem { Content = ToolOptionPreviews.Choice(options[index], value) });
        }
        Automation.AutomationProperties.SetName(picker, label);
        picker.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            dirtyFields.Add(key); SetStatus(L.Get("Inspector.DraftRetained"), false);
        };
        var row = new Controls.StackPanel { Margin = new(0, 4, 0, 4) };
        row.Children.Add(new Controls.TextBlock { Text = label, FontSize = 12, Margin = new(0, 0, 0, 3) });
        row.Children.Add(picker); fieldContainers.Add(key, row); propertyPanel.Children.Add(row);
    }

    private Controls.StackPanel CreateColorField(string key, string label, ColorRole role, ColorLibrary? library)
    {
        var row = new Controls.StackPanel { Margin = new(0, 4, 0, 4) };
        var caption = new Controls.TextBlock { Text = label, FontSize = 12, Margin = new(0, 0, 0, 3) };
        var field = new ColorField(role, library);
        Automation.AutomationProperties.SetLabeledBy(field, caption);
        field.ValueChanged += _ =>
        {
            if (refreshing) return;
            dirtyFields.Add(key); SetStatus(L.Get("Inspector.DraftRetained"), false);
        };
        field.EyedropperRequested += () => EyedropperRequested?.Invoke(field);
        row.Children.Add(caption); row.Children.Add(field);
        colorFields.Add(key, field); fieldContainers.Add(key, row);
        return row;
    }

    private void CloseColorPickers()
    {
        foreach (var field in colorFields.Values) field.ClosePicker();
    }

    private bool BeginScrub(string key)
    {
        if (!canMutate() || dirtyFields.Count > 0) return false;
        var session = getSession();
        var selected = session.SelectedElements().ToArray();
        if (session.IsTransactionActive || !ReferenceEquals(session, formSession) || selected.Length != 1 ||
            selected[0].Id != formElementId || selected[0].Locked || JsonSerializer.Serialize(selected[0]) != formFingerprint) return false;
        var source = selected[0];
        var size = GeometricSize(source); var bounds = ElementGeometry.Bounds(source);
        if (key == "W" && size.X < 1e-9 || key == "H" && size.Y < 1e-9) return false;
        scrubStartValue = key switch
        {
            "X" => bounds.Left, "Y" => bounds.Top, "W" => size.X, "H" => size.Y,
            "Angle" => Angle(source.Transform), "StrokeWidth" => source.Style.StrokeWidth,
            "CornerRadius" => ElementGeometry.CornerRadius(source), _ => 0
        };
        scrubSource = source.Clone(); scrubSession = session;
        session.BeginTransaction(L.Get("Inspector.History.EditProperties")); scrubTransaction = session.TransactionId;
        return true;
    }

    private void UpdateScrub(string key, double delta)
    {
        var session = scrubSession; var source = scrubSource;
        if (session is null || source is null) return;
        if (!canMutate() || !ReferenceEquals(session, getSession()) || !session.IsTransactionActive || session.TransactionId != scrubTransaction)
        { CancelScrub(); return; }
        var target = session.Document.Elements.FirstOrDefault(e => e.Id == source.Id);
        if (target is null) { CancelScrub(); return; }
        double value = scrubStartValue + delta * (key == "StrokeWidth" ? .1 : 1);
        value = key switch
        {
            "W" or "H" => Math.Clamp(value, .01, 10000000), "StrokeWidth" => Math.Clamp(value, 0, 512),
            "CornerRadius" => Math.Clamp(value, 0, 10000000), "Angle" => Math.Clamp(value, -360000, 360000),
            _ => Math.Clamp(value, -10000000, 10000000)
        };
        var edited = source.Clone();
        switch (key)
        {
            case "X": edited.Transform = Affine2D.Translation(value - ElementGeometry.Bounds(source).Left, 0).Compose(source.Transform); break;
            case "Y": edited.Transform = Affine2D.Translation(0, value - ElementGeometry.Bounds(source).Top).Compose(source.Transform); break;
            case "Angle": edited.Transform = Affine2D.Around(ElementGeometry.Bounds(source).Center, Affine2D.Rotation(value - Angle(source.Transform))).Compose(source.Transform); break;
            case "W": case "H":
                var size = GeometricSize(source); double factor = value / (key == "W" ? size.X : size.Y);
                double sx = key == "W" || keepAspect.IsChecked == true ? factor : 1;
                double sy = key == "H" || keepAspect.IsChecked == true ? factor : 1;
                if (ElementGeometry.IsBoxShape(edited.Kind)) ElementGeometry.ResizeShapeGeometry(edited, sx, sy);
                else edited.Transform = source.Transform.Compose(Affine2D.Around(ElementGeometry.LocalBounds(source).Center, Affine2D.Scale(sx, sy))); break;
            case "StrokeWidth": edited.Style.StrokeWidth = value; break;
            case "CornerRadius": edited.CornerRadius = value; break;
        }
        target.Transform = edited.Transform; target.Style = edited.Style; target.CornerRadius = edited.CornerRadius;
        target.Width = edited.Width; target.Height = edited.Height;
        session.NotifyChanged();
        refreshing = true;
        try { SetField(key, value); }
        finally { refreshing = false; }
    }

    private void FinishScrub(bool commit)
    {
        var session = scrubSession;
        scrubSession = null; scrubSource = null;
        if (session is null) return;
        if (session.IsTransactionActive && session.TransactionId == scrubTransaction)
        {
            if (commit && canMutate() && ReferenceEquals(session, getSession())) session.Commit(); else session.Cancel();
        }
        LoadSelection(true);
    }

    private void CancelScrub()
    {
        foreach (var scrubber in scrubbers) scrubber.End(false);
        FinishScrub(false);
    }

    private Controls.StackPanel LabeledField(string key, string label)
    {
        var row = new Controls.StackPanel { Margin = new(0, 6, 0, 0) };
        row.Children.Add(new Controls.TextBlock { Text = label, FontSize = 12, Margin = new(0, 0, 0, 4) });
        row.Children.Add(Field(key, key == "FontFamily" ? L.Get("Inspector.FontFamilyName") : label)); fieldContainers.Add(key, row); return row;
    }

    private void SetPropertyVisibility(CanvasElement? element)
    {
        propertyPanel.Visibility = element is null ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        bool image = element?.Kind is ElementKind.Image or ElementKind.LivePixelate, text = element?.Kind == ElementKind.Text, number = element?.Kind == ElementKind.Number;
        textSection.Visibility = text || number ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        foreach (var (key, container) in fieldContainers)
        {
            bool visible = key switch
            {
                "Stroke" or "ColorHint" => !image,
                "Fill" => !image && !text,
                "StrokeWidth" => !image && !text && !number,
                "StrokePattern" => !image && !text && !number,
                "CornerRadius" => element?.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle,
                "FontFamily" or "FontSize" => text || number,
                "Text" or "TextAlignment" => text,
                "Number" => number,
                "ArrowHeadStyle" => element?.Kind is ElementKind.Arrow or ElementKind.DoubleArrow,
                "ArrowHeadSize" or "ArrowHint" => element?.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow,
                "StartArrowHead" or "EndArrowHead" => element?.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow,
                _ => true
            };
            container.Visibility = visible ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        }
        foreach (var row in propertyPanel.Children.OfType<Controls.Grid>())
            row.Visibility = row.Children.OfType<Wpf.FrameworkElement>().Any(child => child.Visibility == Wpf.Visibility.Visible) ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        ((Controls.TextBlock)fieldContainers["ColorHint"]).Text = text ? L.Get("Inspector.TextColorHint") : L.Get("Inspector.ColorHint");
    }

    private Controls.TextBox Field(string key, string label)
    {
        var box = new Controls.TextBox { MinHeight = 34, VerticalContentAlignment = Wpf.VerticalAlignment.Center,
            Padding = new Wpf.Thickness(5, 2, 5, 2), Margin = new Wpf.Thickness(0, 0, 5, 0) };
        Automation.AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) =>
        {
            if (refreshing) return;
            dirtyFields.Add(key);
            SetStatus(L.Get("Inspector.DraftRetained"), false);
        };
        fields.Add(key, box);
        return box;
    }

    private void SetField(string key, double value) => fields[key].Text = value.ToString("0.###", CultureInfo.CurrentCulture);
    private double Number(string key, string label, double min, double max)
    {
        if ((!double.TryParse(fields[key].Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
             !double.TryParse(fields[key].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
            !double.IsFinite(value) || value < min || value > max)
            throw new InvalidOperationException(L.Format("Inspector.NumberRange", label, min, max));
        return value;
    }

    private void SetStatus(string text, bool error)
    {
        status.Text = text;
        if (error) status.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(190, 60, 55));
        else status.SetResourceReference(Controls.TextBlock.ForegroundProperty, Wpf.SystemColors.GrayTextBrushKey);
    }

    private void HandleEscape()
    {
        if (scrubSession is not null) { CancelScrub(); return; }
        foreach (var field in colorFields.Values) if (field.TryClosePicker()) return;
        if (alignment.IsDropDownOpen) { alignment.IsDropDownOpen = false; return; }
        if (arrowStyle.IsDropDownOpen) { arrowStyle.IsDropDownOpen = false; return; }
        foreach (var picker in new[] { startArrow, endArrow, strokePattern })
            if (picker.IsDropDownOpen) { picker.IsDropDownOpen = false; return; }
        Hide(); Dismissed?.Invoke();
    }
    private bool RouteCommandKey(int key, bool control, bool textInput)
    {
        bool projectCommand = control && key is 0x53 or 0x4F;
        bool objectCommand = !textInput && (control && key is 0x5A or 0x59 or 0x41 or 0x43 or 0x56 or 0x44 || !control && key == 0x2E);
        if (!projectCommand && !objectCommand) return false;
        if (!canMutate()) { SetStatus(L.Get("Inspector.BusyDraftRetained"), true); return true; }
        CommandKey?.Invoke(key); return true;
    }
    private static bool IsTextInput(Wpf.DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Controls.TextBox) return true;
            source = source is Media.Visual || source is System.Windows.Media.Media3D.Visual3D ? Media.VisualTreeHelper.GetParent(source) : Wpf.LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private static PointD GeometricSize(CanvasElement element)
    {
        var local = ElementGeometry.LocalBounds(element); var t = element.Transform;
        return new(local.Width * Math.Sqrt(t.M11 * t.M11 + t.M21 * t.M21), local.Height * Math.Sqrt(t.M12 * t.M12 + t.M22 * t.M22));
    }

    private static double Angle(Affine2D transform) => Math.Atan2(transform.M21, transform.M11) * 180 / Math.PI;
    internal static string ElementName(CanvasElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Name)) return element.Name;
        var name = element.Kind switch
        {
            ElementKind.Freehand => L.Get("Editor.ElementKind.Freehand"), ElementKind.Highlighter => L.Get("Editor.ElementKind.Highlighter"), ElementKind.Line => L.Get("Editor.ElementKind.Line"), ElementKind.Arrow => L.Get("Editor.ElementKind.Arrow"),
            ElementKind.DoubleArrow => L.Get("Editor.ElementKind.DoubleArrow"), ElementKind.Rectangle => L.Get("Editor.ElementKind.Rectangle"), ElementKind.RoundedRectangle => L.Get("Editor.ElementKind.RoundedRectangle"),
            ElementKind.Ellipse => L.Get("Editor.ElementKind.Ellipse"), ElementKind.Triangle => L.Get("Editor.ElementKind.Triangle"), ElementKind.Symbol => L.Get("Editor.ElementKind.Symbol"), ElementKind.Text => L.Get("Editor.ElementKind.Text"), ElementKind.Number => L.Get("Editor.ElementKind.Number"), ElementKind.Image => L.Get("Editor.ElementKind.Image"), ElementKind.LivePixelate => L.Get("Feedback4.Effect.LivePixelate"), _ => L.Get("Editor.ElementKind.Unknown")
        };
        if (element.Kind == ElementKind.Number) return L.Format("Inspector.NumberName", name, element.Number);
        if (element.Kind != ElementKind.Text) return name;
        var text = element.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return L.Format("Inspector.TextName", name, text.Length > 18 ? text[..18] + "…" : text);
    }

}
