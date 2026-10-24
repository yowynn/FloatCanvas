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

/// <summary>Live property edits for the current selection; layers live in their own window.</summary>
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
    private readonly Controls.CheckBox linkColors = new() { Content = L.Get("Color.LinkColors"), MinHeight = 30 };
    private readonly Controls.ComboBox fontPicker = ToolOptionPreviews.FontPicker();
    private readonly Controls.ComboBox alignment = new() { MinHeight = 34, Margin = new(0, 4, 5, 4) };
    private readonly Controls.ComboBox arrowStyle = new() { MinHeight = 34, Margin = new(0, 4, 5, 4) };
    private readonly Controls.ComboBox startArrow = new() { MinHeight = 34 };
    private readonly Controls.ComboBox endArrow = new() { MinHeight = 34 };
    private readonly Controls.ComboBox strokePattern = new() { MinHeight = 34 };
    private readonly Controls.ComboBox placement = new() { MinHeight = 34 };
    private readonly List<NumericScrubber> scrubbers = [];
    private EditorSession? formSession;
    private Guid[] formSelection = [];
    private EditorSession? editSession;
    private long editTransaction;
    private bool applying;
    private readonly System.Windows.Threading.DispatcherTimer editTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string? formFingerprint;
    private bool? formProjectionEnabled;
    private bool refreshing = true;
    private bool invalidDraft; // Field validation can fail before an edit transaction exists.
    private EditorSession? scrubSession;
    private CanvasElement[] scrubSources = [];
    private double scrubStartValue;
    private long scrubTransaction;
    private Controls.TextBlock? scrubCaption;
    private bool scrubUnsafe;
    private readonly Controls.Button pin;
    internal bool IsPinned { get; private set; }

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
        propertyPanel.Children.Add(SectionHeading("Inspector.GeometrySection", L.Get("Inspector.GeometryHint")));
        propertyPanel.Children.Add(FieldPair("X", "X", "Y", "Y"));
        propertyPanel.Children.Add(FieldPair("W", L.Get("Inspector.WidthShort"), "H", L.Get("Inspector.HeightShort")));
        propertyPanel.Children.Add(FieldPair("Angle", L.Get("Inspector.AngleDegrees"), null, null));
        propertyPanel.Children.Add(keepAspect);
        fieldContainers.Add("ConstrainAspect",keepAspect);
        keepAspect.Click += (_,_) => ChangeField("ConstrainAspect");
        propertyPanel.Children.Add(SectionHeading("Inspector.AppearanceSection"));
        propertyPanel.Children.Add(FieldPair("StrokeWidth", L.Get("Inspector.StrokeWidth"), "CornerRadius", L.Get("Inspector.CornerRadius")));
        foreach (var (label, value) in new[] { (L.Get("Inspector.ArrowFilledTriangle"), ArrowHeadStyle.FilledTriangle), (L.Get("Inspector.ArrowOpen"), ArrowHeadStyle.Open), (L.Get("Inspector.ArrowDiamond"), ArrowHeadStyle.Diamond), (L.Get("Inspector.ArrowNone"), ArrowHeadStyle.None) })
            arrowStyle.Items.Add(new Controls.ComboBoxItem { Content = ToolOptionPreviews.Choice(label, value) });
        Automation.AutomationProperties.SetName(arrowStyle, L.Get("Inspector.ArrowStyle"));
        arrowStyle.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            ChangeField("ArrowHeadStyle");
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
        propertyPanel.Children.Add(linkColors); fieldContainers.Add("LinkColors", linkColors);
        linkColors.Click += (_, _) => ChangeField("LinkColors");
        var colorHint = new Controls.TextBlock { Text = L.Get("Inspector.ColorHint"), FontSize = 11, Opacity = .65, Margin = new(0, 2, 0, 6) };
        fieldContainers.Add("ColorHint", colorHint); propertyPanel.Children.Add(colorHint);
        propertyPanel.Children.Add(FieldPair("Opacity", L.Get("Inspector.OpacityPercent"), null, null));
        textSection = SectionHeading("Inspector.TextSection",first:true);
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
            ChangeField("TextAlignment");
        };
        propertyPanel.Children.Add(alignment);
        fieldContainers.Add("TextAlignment", alignment);
        propertyPanel.Children.Add(LabeledField("Text", L.Get("Inspector.TextContent")));
        var text = fields["Text"];
        text.AcceptsReturn = true; text.TextWrapping = Wpf.TextWrapping.Wrap;
        text.Height = 76; text.VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto;
        text.VerticalContentAlignment=Wpf.VerticalAlignment.Top; text.HorizontalContentAlignment=Wpf.HorizontalAlignment.Left;
        var geometryRows=propertyPanel.Children.Cast<Wpf.UIElement>().Take(5).ToArray();
        var textRows=propertyPanel.Children.Cast<Wpf.UIElement>().SkipWhile(row=>!ReferenceEquals(row,textSection)).ToArray();
        foreach(var row in geometryRows.Concat(textRows))propertyPanel.Children.Remove(row);
        foreach(var row in geometryRows)propertyPanel.Children.Add(row);
        propertyPanel.Children.Insert(0,textSection);
        propertyPanel.Children.Insert(1,fieldContainers["Text"]);
        int textIndex=2;
        foreach(var row in textRows.Where(row=>!ReferenceEquals(row,textSection)&&!ReferenceEquals(row,fieldContainers["Text"])))
            propertyPanel.Children.Insert(textIndex++,row);
        var placementSection=new Controls.StackPanel();
        placementSection.Children.Add(SectionHeading("Billboard.Placement"));
        placement.Items.Add(L.Get("Billboard.Surface"));placement.Items.Add(L.Get("Billboard.Name"));
        Automation.AutomationProperties.SetName(placement,L.Get("Billboard.Placement"));
        placement.SelectionChanged+=(_,_)=>{if(!refreshing)ChangeField("Placement");};
        placementSection.Children.Add(placement);fieldContainers.Add("Placement",placementSection);
        propertyPanel.Children.Add(placementSection);
        placementSection.Children.Add(FieldPair("BillboardHeight",L.Get("Billboard.Height"),null,null));
        placementSection.ToolTip=L.Get("Billboard.AnchorHint");
        var footer = new Controls.StackPanel();
        var footerBorder = new Controls.Border { Child = footer, BorderThickness = new(0, 1, 0, 0), Padding = new(12, 8, 26, 8) };
        footerBorder.SetResourceReference(Controls.Border.BorderBrushProperty, "CanvasBorder");
        Controls.Grid.SetRow(footerBorder, 1);
        layout.Children.Add(footerBorder);
        status.Margin = new(0, 6, 0, 0);
        footer.Children.Add(status);
        editTimer.Tick += (_, _) => { if (!numericFields.Values.Any(field => field.Slider.IsMouseCaptureWithin)) CompleteEdit(); };
        Deactivated += (_, _) => {CompleteEdit();QueueFocusCheck();};
        propertyPanel.LostKeyboardFocus += (_, _) => { if (!propertyPanel.IsKeyboardFocusWithin) CompleteEdit(); };
        Closing += (_, e) => { e.Cancel = true; CompleteEdit(); CancelScrub(); CloseColorPickers(); SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CompleteEdit(); CloseColorPickers();
            if (SessionWindowCoordinator.Find(this)?.IsSuspendingWindows == true && IsPinned) return;
            IsPinned=false;UpdatePin();Dismissed?.Invoke(); } };
        AppInputPolicy.AttachToolbarToggle(this, () => CommandKey?.Invoke(0x09), () => this.canMutate() && !colorFields.Values.Any(field => field.IsPickerOpen));
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Input.Key.System ? e.SystemKey : e.Key;
            var modifiers = Input.Keyboard.Modifiers;
            var source = e.OriginalSource as Wpf.DependencyObject;
            if (e.Handled || (modifiers & (Input.ModifierKeys.Alt | Input.ModifierKeys.Windows)) != 0 || EditorShortcutPolicy.OwnsMenu(source)) return;
            if (key == Input.Key.Escape) { e.Handled = true; if (!e.IsRepeat && !AppWindowChrome.TryCloseHelp(this)) HandleEscape(); return; }
            if (key == Input.Key.Enter && (modifiers & Input.ModifierKeys.Control) != 0) { e.Handled = true; if (!e.IsRepeat) Apply(); return; }
            bool textInput = OwnsLocalEditingKeys(Input.Keyboard.FocusedElement as Wpf.DependencyObject) ||
                OwnsLocalEditingKeys(e.OriginalSource as Wpf.DependencyObject);
            int command = Input.KeyInterop.VirtualKeyFromKey(key);
            bool control = (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0;
            if (!EditorShortcutPolicy.ShouldRoute(command, modifiers, source, localInput: textInput)) return;
            e.Handled = true;
            if (!e.IsRepeat || EditorShortcutPolicy.AllowsRepeat(command, (FloatCanvas.Windows.OverlayModifiers)modifiers)) RouteCommandKey(command, control, textInput);
        };
        AppWindowChrome.Apply(this); Icon = AppIcon.WindowImage;
        pin=ActionIconButton.Create(PanelActionIcon.Pin,L.Get("ToolbarV2.PinOptions"),()=>{IsPinned=!IsPinned;UpdatePin();});
        pin.Width=28;pin.Height=28;
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(pin,true);
        AppWindowChrome.SetHeaderActions(this,pin);UpdatePin();
        foreach(var color in colorFields.Values)color.PickerClosed+=QueueFocusCheck;
        foreach(var picker in new[] {fontPicker,alignment,arrowStyle,startArrow,endArrow,strokePattern,placement})picker.DropDownClosed+=(_,_)=>QueueFocusCheck();
        refreshing = false;
        Refresh();
    }

    internal void Dismiss(WindowDismissReason reason=WindowDismissReason.Close)
    {
        CompletePendingEdit();CancelScrub();IsPinned=false;UpdatePin();
        SessionWindowCoordinator.Hide(this,reason);
    }
    private void UpdatePin()
    {
        if(pin is null)return;
        ActionIconButton.UpdatePin(pin,IsPinned);
    }
    private void QueueFocusCheck()=>Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,()=>
    {
        if(SessionWindowCoordinator.Find(this)?.IsInputMethodSwitchPending==true)return;
        if(!IsVisible||IsPinned||IsActive||IsKeyboardFocusWithin||colorFields.Values.Any(color=>color.IsPickerOpen)||
            new[] {fontPicker,alignment,arrowStyle,startArrow,endArrow,strokePattern,placement}.Any(picker=>picker.IsDropDownOpen))return;
        if(Wpf.Application.Current.Windows.Cast<Wpf.Window>().Any(window=>window.IsActive&&IsChildWindow(window)))return;
        Dismiss(WindowDismissReason.ExternalFocus);
    });
    private bool IsChildWindow(Wpf.Window window)
    {
        for(var owner=window.Owner;owner is not null;owner=owner.Owner)if(ReferenceEquals(owner,this))return true;
        return false;
    }

    /// <summary>Selection notifications replace the form; in-progress valid edits stay visible.</summary>
    public void Refresh()
    {
        if (applying || scrubSession is not null) return;
        var session = getSession();
        if (!ReferenceEquals(session, formSession) || !session.Selection.SetEquals(formSelection))
        {
            CompleteEdit();
            LoadSelection(true);
            return;
        }
        if (session.IsTransactionActive) return;
        if (editSession is not null) { editSession = null; editTimer.Stop(); }
        LoadSelection(false);
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Refresh();

    private void LoadSelection(bool discardDraft)
    {
        var session = getSession();
        var selected = session.SelectedElements().ToArray();
        var element = selected.FirstOrDefault();
        var fingerprint = JsonSerializer.Serialize(selected);
        if (!discardDraft && ReferenceEquals(formSession, session) && fingerprint == formFingerprint && formProjectionEnabled==session.Document.Canvas.Projection.Enabled &&
            (!invalidDraft || (IsVisible && propertyPanel.IsKeyboardFocusWithin) || !canMutate())) return;
        refreshing = true;
        try
        {
            if (!ReferenceEquals(formSession, session))
            {
                if (formSession is not null) formSession.Changed -= OnSessionChanged;
                session.Changed += OnSessionChanged;
            }
            formSession = session; formSelection = selected.Select(e => e.Id).ToArray();
            formFingerprint = fingerprint;
            formProjectionEnabled=session.Document.Canvas.Projection.Enabled;
            dirtyFields.Clear();
            invalidDraft=false;
            propertyPanel.IsEnabled = selected.Length > 0 && selected.All(e => !e.Locked);
            SetPropertyVisibility(selected);
            selectionLabel.Text = selected.Length > 1 ? L.Format("Feedback4.Inspector.MultipleSelection", selected.Length) : element is null ? L.Get("Inspector.SelectElement")
                : L.Format("Inspector.ElementSelection", ElementName(element), element.Locked ? L.Get("Inspector.LockedSuffix") : "", !element.Visible ? L.Get("Inspector.HiddenSuffix") : "");
            selectionLabel.Visibility = selected.Length == 1 ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
            AppWindowChrome.SetHeaderDetail(this, selected.Length > 1 ? L.Get("Feedback4.Inspector.MultipleHeader") : selected.Length == 1 ? selectionLabel.Text : "");
            foreach (var box in fields.Values) box.Text = "";
            colorFields["Stroke"].Value = element is null ? ColorLibrary.ForegroundRed : (uint)PropertyValue(element, "Stroke")!;
            colorFields["Fill"].Value = element is null ? 0 : (uint)PropertyValue(element, "Fill")!;
            linkColors.IsThreeState = selected.Select(e => e.Style.LinkColors).Distinct().Count() > 1;
            linkColors.IsChecked = linkColors.IsThreeState ? null : element?.Style.LinkColors == true;
            foreach (var color in colorFields.Values) color.IsMixed = false;
            if (element is null) { SetStatus(L.Get("Inspector.SelectHiddenOrLocked"), false); return; }
            var bounds = ElementGeometry.Bounds(element);
            var size = GeometricSize(element);
            SetField("X", element.IsBillboard?element.BillboardAnchor.X:bounds.Left); SetField("Y", element.IsBillboard?element.BillboardAnchor.Y:bounds.Top); SetField("W", size.X); SetField("H", size.Y);
            SetField("BillboardHeight",element.BillboardHeight);
            placement.SelectedIndex=selected.Select(e=>e.IsBillboard).Distinct().Count()>1?-1:element.IsBillboard?1:0;
            SetField("Angle", ElementAngle(element)); SetField("StrokeWidth", element.Style.StrokeWidth);
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
            keepAspect.IsThreeState=selected.Select(ElementGeometry.PreservesAspect).Distinct().Count()>1;
            keepAspect.IsChecked=keepAspect.IsThreeState?null:ElementGeometry.PreservesAspect(element);

            if (selected.Length > 1)
            {
                foreach (var key in fields.Keys)
                    if (selected.Skip(1).Any(other => !Equals(PropertyValue(element, key), PropertyValue(other, key)))) fields[key].Text = "";
                if (selected.Skip(1).Any(other => other.Style.FontFamily != element.Style.FontFamily)) fontPicker.SelectedIndex = -1;
                foreach (var (key, picker) in new[] { ("ArrowHeadStyle", arrowStyle), ("StartArrowHead", startArrow), ("EndArrowHead", endArrow), ("StrokePattern", strokePattern), ("TextAlignment", alignment) })
                    if (selected.Skip(1).Any(other => !Equals(PropertyValue(element, key), PropertyValue(other, key)))) picker.SelectedIndex = -1;
                foreach (var (key, color) in colorFields)
                    color.IsMixed = selected.Skip(1).Any(other => !Equals(PropertyValue(element, key), PropertyValue(other, key)));
            }
            SetStatus(selected.Any(item => item.Locked) ? L.Format("Audit.LockedSelection", selected.Count(item => item.Locked)) : L.Get("Feedback4.Inspector.LiveHint"), false);
        }
        finally { refreshing = false; }
    }

    private void ChangeField(string key)
    {
        if (refreshing || !fieldContainers.TryGetValue(key, out var container) || container.Visibility != Wpf.Visibility.Visible) return;
        dirtyFields.Clear(); dirtyFields.Add(key);
        ApplyLive();
    }

    public void CompletePendingEdit()
    {
        CloseColorPickers();
        CompleteEdit();
    }

    private void CompleteEdit()
    {
        editTimer.Stop();
        var session = editSession;
        editSession = null;
        if (session is not null && session.IsTransactionActive && session.TransactionId == editTransaction)
        {
            applying = true;
            try { session.Commit(); }
            finally { applying = false; }
        }
        else if (!invalidDraft || !canMutate()) return;
        // Preserve the caret and intermediate input when completing an idle typing burst.
        if (!IsVisible || !propertyPanel.IsKeyboardFocusWithin) LoadSelection(true);
    }

    private void Apply()
    {
        ApplyLive();
        if (!invalidDraft) CompleteEdit();
    }

    private void ApplyLive()
    {
        bool validatingDraft=false;
        try
        {
            if (!canMutate()) throw new InvalidOperationException(L.Get("Inspector.BusyDraftRetained"));
            var session = getSession();
            var selected = session.SelectedElements().ToArray();
            if (!ReferenceEquals(session, formSession) || !session.Selection.SetEquals(formSelection) || selected.Length == 0)
                throw new InvalidOperationException(L.Get("Inspector.SelectionChanged"));
            if (selected.Any(e => e.Locked)) throw new InvalidOperationException(L.Get("Inspector.ElementLocked"));
            bool ownsTransaction = ReferenceEquals(editSession, session) && session.IsTransactionActive && session.TransactionId == editTransaction;
            if (session.IsTransactionActive && !ownsTransaction) throw new InvalidOperationException(L.Get("Inspector.FinishDrawing"));
            if (dirtyFields.Count == 0) return;
            if (JsonSerializer.Serialize(selected) != formFingerprint) throw new InvalidOperationException(L.Get("Inspector.ElementChanged"));
            var changedKey = dirtyFields.Single();
            validatingDraft=true;
            var changes = selected.Select(source => (Source: source, Edited: EditElement(source))).ToArray();
            var mapping = CanvasMapping.Create(session.Document);
            if (changes.Any(change => !mapping.IsElementSafe(change.Edited)))
                throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
            validatingDraft=false;invalidDraft=false;
            if (!ownsTransaction)
            {
                session.BeginTransaction(L.Get("Inspector.History.EditProperties"));
                editSession = session; editTransaction = session.TransactionId;
            }
            applying = true;
            try
            {
                foreach (var (source, edited) in changes)
                {
                    source.Transform = edited.Transform; source.Style = edited.Style;
                    source.Text = edited.Text; source.Number = edited.Number;
                    source.Width = edited.Width; source.Height = edited.Height; source.CornerRadius = edited.CornerRadius;
                    source.ConstrainAspect = edited.ConstrainAspect; source.TextFrameFixed = edited.TextFrameFixed;
                    source.NumberUsesForeground = edited.NumberUsesForeground;
                    source.IsBillboard=edited.IsBillboard;source.BillboardAnchor=edited.BillboardAnchor;source.BillboardHeight=edited.BillboardHeight;
                    source.ProjectivePlacement=edited.ProjectivePlacement;
                }
                formFingerprint = JsonSerializer.Serialize(selected);
                dirtyFields.Clear();
                SynchronizeOtherFields(selected, changedKey);
                session.NotifyChanged();
            }
            finally { applying = false; }
            editTimer.Stop(); editTimer.Start();
            SetStatus(L.Get("Inspector.PropertiesApplied"), false);
        }
        catch (Exception ex) { invalidDraft=validatingDraft;SetStatus(ex.Message, true); }
    }

    private void SynchronizeOtherFields(CanvasElement[] selected, string changedKey)
    {
        refreshing = true;
        try
        {
            if(changedKey=="ConstrainAspect")
            {keepAspect.IsThreeState=false;keepAspect.IsChecked=selected.All(ElementGeometry.PreservesAspect);}
            if(changedKey=="Placement")SetPropertyVisibility(selected);
            if(changedKey=="LinkColors")SetPropertyVisibility(selected);
            foreach (var (key, box) in fields)
            {
                if (key == changedKey) continue;
                var value = PropertyValue(selected[0], key);
                if (selected.Skip(1).Any(e => !Equals(PropertyValue(e, key), value))) { box.Text = ""; continue; }
                if (value is double number) box.Text = (key == "Opacity" ? number * 100 : number).ToString("0.###", CultureInfo.CurrentCulture);
                else box.Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
            }
        }
        finally { refreshing = false; }
    }

    private CanvasElement EditElement(CanvasElement source)
    {
        var edited = source.Clone();
        if(dirtyFields.Contains("Placement"))
        {
            if(!getSession().Document.Canvas.Projection.Enabled||placement.SelectedIndex is <0 or >1||
                !BillboardGeometry.TrySetBillboard(edited,placement.SelectedIndex==1,CanvasMapping.Create(getSession().Document)))
                throw new InvalidOperationException(L.Get("Billboard.ConversionFailed"));
        }
        if(dirtyFields.Contains("BillboardHeight"))edited.BillboardHeight=Number("BillboardHeight",L.Get("Billboard.Height"),-10000000,10000000);
        if(dirtyFields.Contains("ConstrainAspect"))edited.ConstrainAspect=keepAspect.IsChecked==true;
        if(dirtyFields.Contains("LinkColors"))edited.Style.LinkColors=linkColors.IsChecked==true;
        if (source.Kind == ElementKind.Number && !source.NumberUsesForeground && dirtyFields.Overlaps(["Stroke", "Fill"]))
        {
            edited.Style.Stroke = (uint)PropertyValue(source, "Stroke")!;
            edited.Style.Fill = (uint)PropertyValue(source, "Fill")!;
            edited.NumberUsesForeground = true;
        }
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
            if(edited.TextFrameFixed)TextFrameLayout.Fit(edited,edited.Style.FontSize,renderer);
            else
            {
                edited.Height = Math.Max(1, metrics.Height);
                if (EmojiGlyphs.IsEmoji(edited.Text, edited.Style)) edited.Width = metrics.Width;
            }
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
            edited.Transform = Affine2D.Around(center, Affine2D.Rotation(requested - ElementAngle(edited))).Compose(edited.Transform);
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
            ResizeElement(edited, sx, sy);
        }
        var bounds = ElementGeometry.Bounds(edited);
        double dx = dirtyFields.Contains("X") ? Number("X", "X", -10000000, 10000000) - (edited.IsBillboard?edited.BillboardAnchor.X:bounds.Left) : 0;
        double dy = dirtyFields.Contains("Y") ? Number("Y", "Y", -10000000, 10000000) - (edited.IsBillboard?edited.BillboardAnchor.Y:bounds.Top) : 0;
        if(edited.IsBillboard)edited.BillboardAnchor+=new PointD(dx,dy);
        else edited.Transform = Affine2D.Translation(dx, dy).Compose(edited.Transform);
        return edited;
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
                "X" or "Y" or "BillboardHeight" => (-10000000d, 10000000d, -8192d, 8192d, 1d),
                "W" or "H" => (0d, 10000000d, 0d, 1000d, 1d),
                "Angle" => (-360000d, 360000d, -180d, 180d, 1d),
                "StrokeWidth" => (0d, 512d, 0d, 50d, .1d),
                "CornerRadius" => (0d, 10000000d, 0d, 256d, 1d),
                "ArrowHeadSize" => (0d, 512d, 0d, 128d, 1d),
                "Opacity" => (0d, 100d, 0d, 100d, 1d),
                "FontSize" => (1d, 1024d, 8d, 144d, 1d),
                _ => (1d, 1000000d, 1d, 100d, 1d)
            };
            var numeric = new NumericValueField(field, limits.Item1, limits.Item2, limits.Item3, limits.Item4, limits.Item5);
            if (key == "Number") numeric.Slider.IsSnapToTickEnabled = true;
            Automation.AutomationProperties.SetName(numeric.Slider, label);
            numeric.Slider.LostMouseCapture += (_, _) => CompleteEdit();
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
            ChangeField(key);
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
            ChangeField(key);
        };
        field.EyedropperRequested += () => EyedropperRequested?.Invoke(field);
        field.PickerClosed += CompleteEdit;
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
        if (invalidDraft) return false;
        CompleteEdit();
        if (!canMutate() || dirtyFields.Count > 0) return false;
        var session = getSession();
        var selected = session.SelectedElements().ToArray();
        if (session.IsTransactionActive || !ReferenceEquals(session, formSession) || selected.Length == 0 ||
            !session.Selection.SetEquals(formSelection) || selected.Any(e => e.Locked) || JsonSerializer.Serialize(selected) != formFingerprint) return false;
        var source = selected[0];
        var size = GeometricSize(source); var bounds = ElementGeometry.Bounds(source);
        if (selected.Any(e => key == "W" && GeometricSize(e).X < 1e-9 || key == "H" && GeometricSize(e).Y < 1e-9)) return false;
        scrubStartValue = key switch
        {
            "X" => source.IsBillboard?source.BillboardAnchor.X:bounds.Left, "Y" => source.IsBillboard?source.BillboardAnchor.Y:bounds.Top, "W" => size.X, "H" => size.Y,
            "Angle" => ElementAngle(source), "StrokeWidth" => source.Style.StrokeWidth,
            "CornerRadius" => ElementGeometry.CornerRadius(source), _ => 0
        };
        scrubSources = selected.Select(e => e.Clone()).ToArray(); scrubSession = session;
        scrubCaption = ((Controls.Grid)fieldContainers[key]).Children.OfType<Controls.TextBlock>().Single();
        scrubUnsafe = false;
        session.BeginTransaction(L.Get("Inspector.History.EditProperties")); scrubTransaction = session.TransactionId;
        return true;
    }

    private void UpdateScrub(string key, double delta)
    {
        var session = scrubSession;
        if (session is null || scrubSources.Length == 0) return;
        if (!canMutate() || !ReferenceEquals(session, getSession()) || !session.IsTransactionActive || session.TransactionId != scrubTransaction)
        { CancelScrub(); return; }
        double displayValue = scrubStartValue;
        var changes = new List<(CanvasElement Target, CanvasElement Edited)>();
        foreach (var source in scrubSources)
        {
            var target = session.Document.Elements.FirstOrDefault(e => e.Id == source.Id);
            if (target is null) { CancelScrub(); return; }
            double value = Convert.ToDouble(PropertyValue(source, key), CultureInfo.InvariantCulture) + delta * (key == "StrokeWidth" ? .1 : 1);
            value = key switch
            {
                "W" or "H" => Math.Clamp(value, .01, 10000000), "StrokeWidth" => Math.Clamp(value, 0, 512),
                "CornerRadius" => Math.Clamp(value, 0, 10000000), "Angle" => Math.Clamp(value, -360000, 360000),
                _ => Math.Clamp(value, -10000000, 10000000)
            };
            var edited = source.Clone();
            switch (key)
            {
                case "X":
                    if(source.IsBillboard)edited.BillboardAnchor=new(value,source.BillboardAnchor.Y);
                    else edited.Transform = Affine2D.Translation(value - ElementGeometry.Bounds(source).Left, 0).Compose(source.Transform);break;
                case "Y":
                    if(source.IsBillboard)edited.BillboardAnchor=new(source.BillboardAnchor.X,value);
                    else edited.Transform = Affine2D.Translation(0, value - ElementGeometry.Bounds(source).Top).Compose(source.Transform);break;
                case "Angle": edited.Transform = Affine2D.Around(ElementGeometry.Bounds(source).Center, Affine2D.Rotation(value - ElementAngle(source))).Compose(source.Transform); break;
                case "W": case "H":
                    var size = GeometricSize(source); double factor = value / (key == "W" ? size.X : size.Y);
                    double sx = key == "W" || keepAspect.IsChecked == true ? factor : 1;
                    double sy = key == "H" || keepAspect.IsChecked == true ? factor : 1;
                    ResizeElement(edited, sx, sy); break;
                case "StrokeWidth": edited.Style.StrokeWidth = value; break;
                case "CornerRadius": edited.CornerRadius = value; break;
            }
            changes.Add((target, edited));
            if (source.Id == scrubSources[0].Id) displayValue = value;
        }
        var mapping = CanvasMapping.Create(session.Document);
        scrubUnsafe = changes.Any(change => !mapping.IsElementSafe(change.Edited));
        if (scrubCaption is not null) scrubCaption.Cursor = scrubUnsafe ? Input.Cursors.No : Input.Cursors.SizeWE;
        if (scrubUnsafe) { status.Text = L.Get("Perspective.InputOutsidePlane"); return; }
        status.Text = "";
        foreach (var (target, edited) in changes)
        {
            target.Transform = edited.Transform; target.Style = edited.Style; target.CornerRadius = edited.CornerRadius;
            target.Width = edited.Width; target.Height = edited.Height;
            target.TextFrameFixed = edited.TextFrameFixed;
            target.BillboardAnchor=edited.BillboardAnchor;target.BillboardHeight=edited.BillboardHeight;
        }
        session.NotifyChanged();
        refreshing = true;
        try { SetField(key, displayValue); }
        finally { refreshing = false; }
    }

    private void FinishScrub(bool commit)
    {
        var session = scrubSession;
        bool unsafePlacement = commit && scrubUnsafe;
        if (scrubCaption is not null) scrubCaption.Cursor = Input.Cursors.SizeWE;
        scrubCaption = null; scrubUnsafe = false;
        scrubSession = null; scrubSources = [];
        if (session is null) return;
        if (session.IsTransactionActive && session.TransactionId == scrubTransaction)
        {
            if (commit && !unsafePlacement && canMutate() && ReferenceEquals(session, getSession())) session.Commit(); else session.Cancel();
        }
        LoadSelection(true);
        if (unsafePlacement) status.Text = L.Get("Perspective.InputOutsidePlane");
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

    private static bool Supports(CanvasElement element, string key)
    {
        bool image = element.Kind == ElementKind.Image || ElementGeometry.IsLiveEffect(element.Kind), text = element.Kind == ElementKind.Text, number = element.Kind == ElementKind.Number;
        return key switch
        {
            "Stroke" or "ColorHint" => !image,
            "Fill" => number || ElementGeometry.IsBoxShape(element.Kind) && !element.Style.LinkColors,
            "LinkColors" => ElementGeometry.IsBoxShape(element.Kind),
            "StrokeWidth" or "StrokePattern" => !image && !text && !number,
            "CornerRadius" => element.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle,
            "FontFamily" => text || number,
            "FontSize" => text && !EmojiGlyphs.IsEmoji(element.Text, element.Style),
            "Text" or "TextAlignment" => text,
            "Number" => number,
            "BillboardHeight" => element.IsBillboard,
            "ArrowHeadStyle" => element.Kind is ElementKind.Arrow or ElementKind.DoubleArrow,
            "ArrowHeadSize" or "ArrowHint" or "StartArrowHead" or "EndArrowHead" => element.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow,
            _ => true
        };
    }

    private static object? PropertyValue(CanvasElement e, string key) => key switch
    {
        "X" => e.IsBillboard?e.BillboardAnchor.X:ElementGeometry.Bounds(e).Left, "Y" => e.IsBillboard?e.BillboardAnchor.Y:ElementGeometry.Bounds(e).Top,
        "BillboardHeight" => e.BillboardHeight,
        "W" => GeometricSize(e).X, "H" => GeometricSize(e).Y, "Angle" => ElementAngle(e),
        "Stroke" => e.Kind == ElementKind.Number && !e.NumberUsesForeground ? (e.Style.Fill >> 24) == 0 ? e.Style.Stroke : e.Style.Fill : e.Style.Stroke,
        "Fill" => e.Kind == ElementKind.Number && !e.NumberUsesForeground ? 0xFFFFFFFF : e.Style.Fill, "StrokeWidth" => e.Style.StrokeWidth,
        "CornerRadius" => ElementGeometry.CornerRadius(e), "Opacity" => e.Style.Opacity,
        "ArrowHeadStyle" => e.Style.ArrowHeadStyle, "ArrowHeadSize" => e.Style.ArrowHeadSize,
        "StartArrowHead" => ElementGeometry.ArrowStyle(e, reverse: true), "EndArrowHead" => ElementGeometry.ArrowStyle(e),
        "StrokePattern" => e.Style.StrokePattern, "FontFamily" => e.Style.FontFamily,
        "FontSize" => e.Style.FontSize, "Number" => e.Number, "Text" => e.Text, "TextAlignment" => e.Style.TextAlignment,
        _ => null
    };

    private void SetPropertyVisibility(CanvasElement[] selected)
    {
        var element = selected.FirstOrDefault();
        bool editable = selected.Length > 0 && selected.All(e => !e.Locked);
        foreach (var key in new[] { "W", "H" }) fieldContainers[key].ToolTip = selected.Any(e => e.ProjectivePlacement is not null)
            ? L.Get("Perspective.InspectorPlaneBounds") : null;
        propertyPanel.Visibility = editable ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        textSection.Visibility = editable && selected.All(e => Supports(e, "FontSize")) ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        foreach (var (key, container) in fieldContainers)
            container.Visibility = editable && selected.All(e => Supports(e, key)) ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        bool number = selected.Length > 0 && selected.All(e => e.Kind == ElementKind.Number);
        bool shape = selected.Length > 0 && selected.All(e => ElementGeometry.IsBoxShape(e.Kind));
        ((Controls.TextBlock)((Controls.StackPanel)fieldContainers["Stroke"]).Children[0]).Text = L.Get(number ? "Color.NumberShape" : shape ? selected.All(e => e.Style.LinkColors) ? "Color.Unified" : "Color.Outline" : "Inspector.Stroke");
        ((Controls.TextBlock)((Controls.StackPanel)fieldContainers["Fill"]).Children[0]).Text = L.Get(number ? "Color.NumberText" : "Color.Interior");
        bool perspective=getSession().Document.Canvas.Projection.Enabled;
        if(!perspective){fieldContainers["Placement"].Visibility=Wpf.Visibility.Collapsed;fieldContainers["BillboardHeight"].Visibility=Wpf.Visibility.Collapsed;}
        foreach(var key in new[]{"X","Y"})fieldContainers[key].ToolTip=selected.Any(e=>e.IsBillboard)?L.Get("Billboard.AnchorHint"):null;
        foreach (var row in propertyPanel.Children.OfType<Controls.Grid>())
            row.Visibility = row.Children.OfType<Wpf.FrameworkElement>().Any(child => child.Visibility == Wpf.Visibility.Visible) ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        ((Controls.TextBlock)fieldContainers["ColorHint"]).Text = element?.Kind == ElementKind.Text ? L.Get("Inspector.TextColorHint") : L.Get("Inspector.ColorHint");
    }

    private Controls.TextBox Field(string key, string label)
    {
        var box = new Controls.TextBox { MinHeight = 34, VerticalContentAlignment = Wpf.VerticalAlignment.Center,
            Padding = new Wpf.Thickness(5, 2, 5, 2), Margin = new Wpf.Thickness(0, 0, 5, 0) };
        Automation.AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) =>
        {
            if (refreshing) return;
            ChangeField(key);
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
        if (error) { status.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasErrorText"); status.Text = "⚠ " + text; }
        else status.SetResourceReference(Controls.TextBlock.ForegroundProperty, Wpf.SystemColors.GrayTextBrushKey);
    }

    private void HandleEscape()
    {
        if (scrubSession is not null) { CancelScrub(); return; }
        CompleteEdit();
        foreach (var field in colorFields.Values) if (field.TryClosePicker()) return;
        if (alignment.IsDropDownOpen) { alignment.IsDropDownOpen = false; return; }
        if (arrowStyle.IsDropDownOpen) { arrowStyle.IsDropDownOpen = false; return; }
        foreach (var picker in new[] { startArrow, endArrow, strokePattern, placement })
            if (picker.IsDropDownOpen) { picker.IsDropDownOpen = false; return; }
        SessionWindowCoordinator.Hide(this); Dismissed?.Invoke();
    }
    private bool RouteCommandKey(int key, bool control, bool textInput)
    {
        if (!CanRouteCommandKey(key, control, textInput)) return false;
        if (!canMutate()) { SetStatus(L.Get("Inspector.BusyDraftRetained"), true); return true; }
        CompletePendingEdit();
        CommandKey?.Invoke(key); return true;
    }
    private static bool CanRouteCommandKey(int key, bool control, bool textInput) =>
        EditorShortcutPolicy.ShouldRoute(key, control ? Input.ModifierKeys.Control : Input.ModifierKeys.None, null, localInput: textInput);
    internal static bool OwnsLocalEditingKeys(Wpf.DependencyObject? source)
    {
        return EditorShortcutPolicy.OwnsLocalEditingKeys(source) || EditorShortcutPolicy.OwnsMenu(source);
    }
    private static PointD GeometricSize(CanvasElement element)
    {
        if (!element.IsBillboard&&element.ProjectivePlacement is not null)
        {
            var bounds = ElementGeometry.Bounds(element, includeStroke: false);
            return new(bounds.Width, bounds.Height);
        }
        var local = ElementGeometry.LocalBounds(element); var t = element.Transform;
        return new(local.Width * Math.Sqrt(t.M11 * t.M11 + t.M21 * t.M21), local.Height * Math.Sqrt(t.M12 * t.M12 + t.M22 * t.M22));
    }

    private static void ResizeElement(CanvasElement element, double x, double y)
    {
        if(element.Kind==ElementKind.Text)
        {
            using var renderer=new SceneRenderer();
            TextFrameLayout.Resize(element,element.Clone(),x,y,renderer);
        }
        else if (!element.IsBillboard&&element.ProjectivePlacement is not null)
            element.Transform = Affine2D.Around(ElementGeometry.Bounds(element, includeStroke: false).Center,
                Affine2D.Scale(x, y)).Compose(element.Transform);
        else if (ElementGeometry.IsBoxShape(element.Kind)) ElementGeometry.ResizeShapeGeometry(element, x, y);
        else element.Transform = element.Transform.Compose(Affine2D.Around(ElementGeometry.LocalBounds(element).Center, Affine2D.Scale(x, y)));
    }

    private static double ElementAngle(CanvasElement element)
    {
        if (element.IsBillboard||element.ProjectivePlacement is null) return Angle(element.Transform);
        var mapping = ElementGeometry.LocalToPlane(element);
        var edge = mapping.TransformPoint(new(element.Width, 0)) - mapping.TransformPoint(new(0, 0));
        return Math.Atan2(edge.Y, edge.X) * 180 / Math.PI;
    }

    private static double Angle(Affine2D transform) => Math.Atan2(transform.M21, transform.M11) * 180 / Math.PI;
    internal static string ElementName(CanvasElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Name)) return element.Name;
        var name = element.Kind switch
        {
            ElementKind.Freehand => L.Get("Editor.ElementKind.Freehand"), ElementKind.Highlighter => L.Get("Editor.ElementKind.Highlighter"), ElementKind.Line => L.Get("Editor.ElementKind.Line"), ElementKind.Arrow => L.Get("Editor.ElementKind.Arrow"),
            ElementKind.DoubleArrow => L.Get("Editor.ElementKind.DoubleArrow"), ElementKind.Rectangle => L.Get("Editor.ElementKind.Rectangle"), ElementKind.RoundedRectangle => L.Get("Editor.ElementKind.RoundedRectangle"),
            ElementKind.Ellipse => L.Get("Editor.ElementKind.Ellipse"), ElementKind.Triangle => L.Get("Editor.ElementKind.Triangle"), ElementKind.Symbol => L.Get("Editor.ElementKind.Symbol"), ElementKind.Text => L.Get("Editor.ElementKind.Text"), ElementKind.Number => L.Get("Editor.ElementKind.Number"), ElementKind.Image => L.Get("Editor.ElementKind.Image"), ElementKind.LivePixelate => L.Get("Feedback4.Effect.LivePixelate"), ElementKind.LiveBlur => L.Get("Editor.ElementKind.LiveBlur"), _ => L.Get("Editor.ElementKind.Unknown")
        };
        if (element.Kind == ElementKind.Number) return L.Format("Inspector.NumberName", name, element.Number);
        if (element.Kind != ElementKind.Text) return name;
        var text = element.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return L.Format("Inspector.TextName", name, text.Length > 18 ? text[..18] + "…" : text);
    }

}
