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

/// <summary>Explicit, atomic property edits and accessible layer controls for the current session.</summary>
internal sealed class InspectorWindow : Wpf.Window
{
    private readonly Func<EditorSession> getSession;
    private readonly Func<bool> canMutate;
    private readonly Dictionary<string, Controls.TextBox> fields = [];
    private readonly Dictionary<string, Wpf.FrameworkElement> fieldContainers = [];
    private readonly HashSet<string> dirtyFields = [];
    private readonly Dictionary<Guid, LayerRow> layerRows = [];
    private readonly Controls.StackPanel propertyPanel = new();
    private readonly Controls.TextBlock selectionLabel = new() { TextWrapping = Wpf.TextWrapping.Wrap };
    private readonly Controls.TextBlock status = new() { TextWrapping = Wpf.TextWrapping.Wrap, MinHeight = 36 };
    private readonly Controls.ListBox layers = new() { MinHeight = 100, MaxHeight = 260, SelectionMode = Controls.SelectionMode.Extended };
    private readonly Controls.CheckBox keepAspect = new() { Content = L.Get("Inspector.KeepAspect"), MinHeight = 30 };
    private readonly Controls.ComboBox alignment = new() { MinHeight = 34, Margin = new(0, 4, 5, 4) };
    private readonly Controls.ComboBox arrowStyle = new() { MinHeight = 34, Margin = new(0, 4, 5, 4) };
    private EditorSession? formSession;
    private EditorSession? layerSession;
    private Guid? formElementId;
    private string? formFingerprint;
    private bool refreshing;

    public event Action? Dismissed;
    public event Action<int>? CommandKey;

    public InspectorWindow(Func<EditorSession> getSession, Func<bool>? canMutate = null)
    {
        this.getSession = getSession;
        this.canMutate = canMutate ?? (() => true);
        Title = L.Get("Inspector.Title");
        Width = 320; Height = 760; MinWidth = 300; MinHeight = 420;
        FontSize = 14; FontFamily = new Media.FontFamily("Microsoft YaHei UI");
        Topmost = true; ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, Wpf.SystemColors.WindowBrushKey);
        SetResourceReference(ForegroundProperty, Wpf.SystemColors.WindowTextBrushKey);

        var body = new Controls.StackPanel { Margin = new Wpf.Thickness(14) };
        Content = new Controls.ScrollViewer { Content = body, VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto };
        body.Children.Add(new Controls.TextBlock { Text = L.Get("Inspector.Heading"), FontSize = 18, FontWeight = Wpf.FontWeights.SemiBold, Margin = new(0, 0, 0, 10) });
        body.Children.Add(selectionLabel);
        body.Children.Add(new Controls.TextBlock { Text = L.Get("Inspector.GeometryHint"), TextWrapping = Wpf.TextWrapping.Wrap,
            Opacity = .65, FontSize = 12, Margin = new(0, 6, 0, 8) });
        body.Children.Add(propertyPanel);
        propertyPanel.Children.Add(FieldPair("X", "X", "Y", "Y"));
        propertyPanel.Children.Add(FieldPair("W", L.Get("Inspector.WidthShort"), "H", L.Get("Inspector.HeightShort")));
        propertyPanel.Children.Add(FieldPair("Angle", L.Get("Inspector.AngleDegrees"), "StrokeWidth", L.Get("Inspector.StrokeWidth")));
        foreach (var label in new[] { L.Get("Inspector.ArrowFilledTriangle"), L.Get("Inspector.ArrowOpen"), L.Get("Inspector.ArrowDiamond") }) arrowStyle.Items.Add(label);
        Automation.AutomationProperties.SetName(arrowStyle, L.Get("Inspector.ArrowStyle"));
        arrowStyle.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            dirtyFields.Add("ArrowHeadStyle"); SetStatus(L.Get("Inspector.DraftRetained"), false);
        };
        var arrowRow = new Controls.StackPanel();
        arrowRow.Children.Add(new Controls.TextBlock { Text = L.Get("Inspector.ArrowStyle"), FontSize = 12, Margin = new(0, 4, 0, 2) }); arrowRow.Children.Add(arrowStyle);
        fieldContainers.Add("ArrowHeadStyle", arrowRow); propertyPanel.Children.Add(arrowRow);
        propertyPanel.Children.Add(FieldPair("ArrowHeadSize", L.Get("Inspector.ArrowHeadSize"), null, null));
        var arrowHint = new Controls.TextBlock { Text = L.Get("Inspector.ArrowHeadSizeHint"), FontSize = 11, Opacity = .65 };
        fieldContainers.Add("ArrowHint", arrowHint); propertyPanel.Children.Add(arrowHint);
        propertyPanel.Children.Add(keepAspect);
        propertyPanel.Children.Add(FieldPair("Stroke", L.Get("Inspector.Stroke"), null, null));
        propertyPanel.Children.Add(FieldPair("Fill", L.Get("Inspector.Fill"), null, null));
        var colorHint = new Controls.TextBlock { Text = L.Get("Inspector.ColorHint"), FontSize = 11, Opacity = .65, Margin = new(0, 2, 0, 6) };
        fieldContainers.Add("ColorHint", colorHint); propertyPanel.Children.Add(colorHint);
        propertyPanel.Children.Add(FieldPair("Opacity", L.Get("Inspector.OpacityPercent"), "FontSize", L.Get("Inspector.FontSize")));
        propertyPanel.Children.Add(LabeledField("FontFamily", L.Get("Inspector.FontFamily")));
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
        propertyPanel.Children.Add(FieldPair("Number", L.Get("Editor.ElementKind.Number"), null, null));
        var applyRow = new Controls.WrapPanel { Margin = new(0, 10, 0, 4) };
        var apply = Button(L.Get("Inspector.Apply"), Apply);
        apply.MinWidth = 110;
        applyRow.Children.Add(apply);
        applyRow.Children.Add(Button(L.Get("Inspector.ReloadSelection"), () =>
        {
            if (!this.canMutate()) { SetStatus(L.Get("Inspector.BusyDraftRetained"), true); return; }
            LoadSelection(true);
        }));
        body.Children.Add(applyRow);
        status.Margin = new(0, 4, 0, 10);
        body.Children.Add(status);
        body.Children.Add(new Controls.Separator { Margin = new(0, 4, 0, 10) });
        var layerPanel = new Controls.StackPanel();
        var layerExpander = new Controls.Expander
        {
            Header = new Controls.TextBlock { Text = L.Get("Inspector.Layers"), FontWeight = Wpf.FontWeights.SemiBold, MinHeight = 30,
                VerticalAlignment = Wpf.VerticalAlignment.Center },
            Content = layerPanel, IsExpanded = true, HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch
        };
        layerExpander.SetResourceReference(ForegroundProperty, Wpf.SystemColors.WindowTextBrushKey);
        Automation.AutomationProperties.SetName(layerExpander, L.Get("Inspector.Layers"));
        body.Children.Add(layerExpander);
        layerPanel.Children.Add(new Controls.TextBlock { Text = L.Get("Inspector.LayersHint"), FontSize = 12, Opacity = .65,
            TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        Automation.AutomationProperties.SetName(layers, L.Get("Inspector.LayerList"));
        layerPanel.Children.Add(layers);
        var layerActions = new Controls.WrapPanel { Margin = new(0, 8, 0, 0) };
        layerActions.Children.Add(Button(L.Get("Inspector.BringForward"), () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Forward))));
        layerActions.Children.Add(Button(L.Get("Inspector.SendBackward"), () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Backward))));
        layerActions.Children.Add(Button(L.Get("Inspector.BringToFront"), () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Front))));
        layerActions.Children.Add(Button(L.Get("Inspector.SendToBack"), () => RunLayerCommand(s => s.ReorderSelection(LayerOrder.Back))));
        layerActions.Children.Add(Button(L.Get("Inspector.Duplicate"), () => RunLayerCommand(s => s.DuplicateSelection())));
        layerActions.Children.Add(Button(L.Get("Inspector.Delete"), () => RunLayerCommand(s => s.DeleteSelection())));
        layerPanel.Children.Add(layerActions);
        layers.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            try
            {
                if (!this.canMutate())
                {
                    SetStatus(L.Get("Inspector.BusySelectionRetained"), true);
                    RefreshLayers(this.getSession());
                    return;
                }
                var session = this.getSession();
                if (session.IsTransactionActive) { SetStatus(L.Get("Inspector.FinishBeforeLayerSelection"), true); Refresh(); return; }
                session.SetSelection(layers.SelectedItems.Cast<Controls.ListBoxItem>().Select(item => (Guid)item.Tag));
                Refresh();
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        Closing += (_, e) => { e.Cancel = true; Hide(); Dismissed?.Invoke(); };
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Input.Key.System ? e.SystemKey : e.Key;
            if (key == Input.Key.Escape) { HandleEscape(); e.Handled = true; return; }
            bool textInput = Input.Keyboard.FocusedElement is Controls.TextBox || IsTextInput(e.OriginalSource as Wpf.DependencyObject);
            if (RouteCommandKey(Input.KeyInterop.VirtualKeyFromKey(key), (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0, textInput)) e.Handled = true;
        };
        Refresh();
    }

    /// <summary>Safe during high-frequency scene notifications; preserves unsubmitted editor text.</summary>
    public void Refresh()
    {
        if (!canMutate()) return;
        var session = getSession();
        if (session.IsTransactionActive) return;
        RefreshLayers(session);
        LoadSelection(false);
    }

    private void LoadSelection(bool discardDraft)
    {
        var session = getSession();
        var selected = session.SelectedElements().ToArray();
        var element = selected.Length == 1 ? selected[0] : null;
        var sameElement = ReferenceEquals(formSession, session) && formElementId == element?.Id;
        if (!discardDraft && (dirtyFields.Count > 0 || propertyPanel.IsKeyboardFocusWithin))
        {
            if (!sameElement)
            {
                selectionLabel.Text = L.Get("Inspector.SelectionChangedDraftRetained");
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
            foreach (var box in fields.Values) box.Text = "";
            if (element is null) { SetStatus(L.Get("Inspector.SelectHiddenOrLocked"), false); return; }
            var bounds = ElementGeometry.Bounds(element);
            var size = GeometricSize(element);
            SetField("X", bounds.Left); SetField("Y", bounds.Top); SetField("W", size.X); SetField("H", size.Y);
            SetField("Angle", Angle(element.Transform)); SetField("StrokeWidth", element.Style.StrokeWidth);
            arrowStyle.SelectedIndex = (int)element.Style.ArrowHeadStyle; SetField("ArrowHeadSize", element.Style.ArrowHeadSize);
            fields["Stroke"].Text = $"#{element.Style.Stroke:X8}";
            fields["Fill"].Text = $"#{element.Style.Fill:X8}";
            SetField("Opacity", element.Style.Opacity * 100); SetField("FontSize", element.Style.FontSize);
            fields["Text"].Text = element.Text;
            fields["Text"].IsEnabled = element.Kind == ElementKind.Text;
            fields["Number"].Text = element.Number.ToString(CultureInfo.InvariantCulture);
            fields["Number"].IsEnabled = element.Kind == ElementKind.Number;
            fields["FontSize"].IsEnabled = element.Kind is ElementKind.Text or ElementKind.Number;
            fields["FontFamily"].Text = element.Style.FontFamily;
            fields["FontFamily"].IsEnabled = element.Kind is ElementKind.Text or ElementKind.Number;
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
            if (dirtyFields.Contains("Stroke")) edited.Style.Stroke = Color("Stroke", L.Get("Inspector.Stroke"));
            if (dirtyFields.Contains("Fill")) edited.Style.Fill = Color("Fill", L.Get("Inspector.Fill"));
            if (dirtyFields.Contains("StrokeWidth")) edited.Style.StrokeWidth = Number("StrokeWidth", L.Get("Inspector.StrokeWidth"), 0, 512);
            if (source.Kind is ElementKind.Arrow or ElementKind.DoubleArrow)
            {
                if (dirtyFields.Contains("ArrowHeadStyle"))
                {
                    if (arrowStyle.SelectedIndex is < 0 or > 2) throw new InvalidOperationException(L.Get("Inspector.InvalidArrowStyle"));
                    edited.Style.ArrowHeadStyle = (ArrowHeadStyle)arrowStyle.SelectedIndex;
                }
                if (dirtyFields.Contains("ArrowHeadSize")) edited.Style.ArrowHeadSize = Number("ArrowHeadSize", L.Get("Inspector.ArrowSize"), 0, 512);
            }
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
                edited.Height = Math.Max(1, renderer.MeasureText(edited.Text, edited.Style, edited.Width).Height);
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
                edited.Transform = edited.Transform.Compose(Affine2D.Around(ElementGeometry.LocalBounds(edited).Center, Affine2D.Scale(sx, sy)));
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
                element.Height = edited.Height;
            }, L.Get("Inspector.History.EditProperties"));
            dirtyFields.Clear();
            LoadSelection(true);
            SetStatus(L.Get("Inspector.PropertiesApplied"), false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void RefreshLayers(EditorSession session)
    {
        refreshing = true;
        try
        {
            var ordered = session.Document.Elements.AsEnumerable().Reverse().ToArray();
            var ids = ordered.Select(e => e.Id).ToArray();
            var displayed = layers.Items.Cast<Controls.ListBoxItem>().Select(item => (Guid)item.Tag).ToArray();
            if (!ReferenceEquals(layerSession, session) || !ids.SequenceEqual(displayed))
            {
                layerSession = session; layerRows.Clear(); layers.Items.Clear();
                foreach (var element in ordered)
                {
                    var id = element.Id;
                    var row = new Controls.Grid { MinHeight = 34, Margin = new(2, 0, 2, 0) };
                    row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(30) });
                    row.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(30) });
                    var label = new Controls.TextBlock { VerticalAlignment = Wpf.VerticalAlignment.Center,
                        TextTrimming = Wpf.TextTrimming.CharacterEllipsis, MinWidth = 80, MaxWidth = 174 };
                    var visible = new Controls.CheckBox { VerticalAlignment = Wpf.VerticalAlignment.Center, ToolTip = L.Get("Inspector.ShowLayer") };
                    var locked = new Controls.CheckBox { VerticalAlignment = Wpf.VerticalAlignment.Center, ToolTip = L.Get("Inspector.LockLayer") };
                    Controls.Grid.SetColumn(visible, 1); Controls.Grid.SetColumn(locked, 2);
                    row.Children.Add(label); row.Children.Add(visible); row.Children.Add(locked);
                    var item = new Controls.ListBoxItem { Content = row, Tag = id, HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch,
                        Padding = new Wpf.Thickness(2) };
                    layerRows.Add(id, new(item, label, visible, locked)); layers.Items.Add(item);
                    visible.Checked += (_, _) => SetLayerFlag(id, true, true);
                    visible.Unchecked += (_, _) => SetLayerFlag(id, true, false);
                    locked.Checked += (_, _) => SetLayerFlag(id, false, true);
                    locked.Unchecked += (_, _) => SetLayerFlag(id, false, false);
                }
            }
            foreach (var element in ordered)
            {
                var row = layerRows[element.Id];
                row.Label.Text = ElementName(element);
                row.Visible.IsChecked = element.Visible; row.Locked.IsChecked = element.Locked;
                row.Item.IsSelected = session.Selection.Contains(element.Id);
                Automation.AutomationProperties.SetName(row.Item, ElementName(element));
                Automation.AutomationProperties.SetName(row.Visible, L.Format("Inspector.ShowElement", ElementName(element)));
                Automation.AutomationProperties.SetName(row.Locked, L.Format("Inspector.LockElement", ElementName(element)));
            }
        }
        finally { refreshing = false; }
    }

    private void SetLayerFlag(Guid id, bool visibility, bool value)
    {
        if (refreshing) return;
        try
        {
            if (!canMutate()) throw new InvalidOperationException(L.Get("Inspector.BusyLayersRetained"));
            var session = getSession();
            if (!ReferenceEquals(session, layerSession)) throw new InvalidOperationException(L.Get("Inspector.DocumentChanged"));
            if (session.IsTransactionActive) throw new InvalidOperationException(L.Get("Inspector.FinishCanvasAction"));
            session.Execute(visibility ? value ? L.Get("Inspector.ShowLayer") : L.Get("Inspector.HideLayer") : value ? L.Get("Inspector.LockLayer") : L.Get("Inspector.UnlockLayer"), document =>
            {
                var element = document.Elements.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
                if (visibility) element.Visible = value; else element.Locked = value;
            });
            Refresh();
        }
        catch (Exception ex) { SetStatus(ex.Message, true); RefreshLayers(getSession()); }
    }

    private void RunLayerCommand(Action<EditorSession> operation)
    {
        try
        {
            if (!canMutate()) throw new InvalidOperationException(L.Get("Inspector.BusyLayersRetained"));
            var session = getSession();
            if (session.IsTransactionActive) throw new InvalidOperationException(L.Get("Inspector.FinishCanvasAction"));
            if (!session.SelectedElements().Any(e => !e.Locked)) throw new InvalidOperationException(L.Get("Inspector.SelectUnlockedLayer"));
            operation(session);
            Refresh();
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
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
            var field = Field(key, label);
            Controls.Grid.SetColumn(field, 1); cell.Children.Add(field);
        }
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
        bool image = element?.Kind == ElementKind.Image, text = element?.Kind == ElementKind.Text, number = element?.Kind == ElementKind.Number;
        foreach (var (key, container) in fieldContainers)
        {
            bool visible = key switch
            {
                "Stroke" or "ColorHint" => !image,
                "Fill" => !image && !text,
                "StrokeWidth" => !image && !text && !number,
                "FontFamily" or "FontSize" => text || number,
                "Text" or "TextAlignment" => text,
                "Number" => number,
                "ArrowHeadStyle" or "ArrowHeadSize" or "ArrowHint" => element?.Kind is ElementKind.Arrow or ElementKind.DoubleArrow,
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

    private static Controls.Button Button(string label, Action action)
    {
        var button = new Controls.Button { Content = label, MinHeight = 34, Padding = new Wpf.Thickness(10, 3, 10, 3), Margin = new Wpf.Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => action();
        return button;
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

    private uint Color(string key, string label)
    {
        var text = fields[key].Text.Trim().TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        if (text.Length != 8 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
            throw new InvalidOperationException(L.Format("Inspector.ColorFormat", label));
        return color;
    }

    private void SetStatus(string text, bool error)
    {
        status.Text = text;
        if (error) status.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(190, 60, 55));
        else status.SetResourceReference(Controls.TextBlock.ForegroundProperty, Wpf.SystemColors.GrayTextBrushKey);
    }

    private void HandleEscape()
    {
        if (alignment.IsDropDownOpen) { alignment.IsDropDownOpen = false; return; }
        if (arrowStyle.IsDropDownOpen) { arrowStyle.IsDropDownOpen = false; return; }
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
    private static string ElementName(CanvasElement element)
    {
        var name = element.Kind switch
        {
            ElementKind.Freehand => L.Get("Editor.ElementKind.Freehand"), ElementKind.Highlighter => L.Get("Editor.ElementKind.Highlighter"), ElementKind.Line => L.Get("Editor.ElementKind.Line"), ElementKind.Arrow => L.Get("Editor.ElementKind.Arrow"),
            ElementKind.DoubleArrow => L.Get("Editor.ElementKind.DoubleArrow"), ElementKind.Rectangle => L.Get("Editor.ElementKind.Rectangle"), ElementKind.RoundedRectangle => L.Get("Editor.ElementKind.RoundedRectangle"),
            ElementKind.Ellipse => L.Get("Editor.ElementKind.Ellipse"), ElementKind.Triangle => L.Get("Editor.ElementKind.Triangle"), ElementKind.Text => L.Get("Editor.ElementKind.Text"), ElementKind.Number => L.Get("Editor.ElementKind.Number"), ElementKind.Image => L.Get("Editor.ElementKind.Image"), _ => L.Get("Editor.ElementKind.Unknown")
        };
        if (element.Kind == ElementKind.Number) return L.Format("Inspector.NumberName", name, element.Number);
        if (element.Kind != ElementKind.Text) return name;
        var text = element.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return L.Format("Inspector.TextName", name, text.Length > 18 ? text[..18] + "…" : text);
    }

    private sealed record LayerRow(Controls.ListBoxItem Item, Controls.TextBlock Label, Controls.CheckBox Visible, Controls.CheckBox Locked);
}
