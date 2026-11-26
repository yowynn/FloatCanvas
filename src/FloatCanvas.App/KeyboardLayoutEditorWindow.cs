using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;
using KeyOverrides = FloatCanvas.Core.PresentationKeyStyleOverrides;

namespace FloatCanvas.App;

/// <summary>Edits an owned physical-key layout draft. Apply is the only publication point.</summary>
internal sealed class KeyboardLayoutEditorWindow : Window
{
    private enum KeyAlignment { Left, Center, Right, Top, Middle, Bottom }
    private const double UnitPixels = 30, Inset = 12, GridStep = .25, SnapDistance = .5;
    private const double TopInset = Inset + UnitPixels;
    private const string ResetLayoutIcon = "M12,4 A6,6 0 1 0 13,10 M9,4 H13 V0.5";
    private const string AlignLeftIcon = "M2,1 V15 M4,3 H13 V6 H4 Z M4,10 H10 V13 H4 Z";
    private const string AlignCenterIcon = "M8,1 V15 M3,3 H13 V6 H3 Z M5,10 H11 V13 H5 Z";
    private const string AlignRightIcon = "M14,1 V15 M3,3 H12 V6 H3 Z M6,10 H12 V13 H6 Z";
    private const string AlignTopIcon = "M1,2 H15 M3,4 V13 H6 V4 Z M10,4 V10 H13 V4 Z";
    private const string AlignMiddleIcon = "M1,8 H15 M3,3 V13 H6 V3 Z M10,5 V11 H13 V5 Z";
    private const string AlignBottomIcon = "M1,14 H15 M3,3 V12 H6 V3 Z M10,6 V12 H13 V6 Z";
    private const string SpaceHorizontalIcon = "M2,2 V14 M8,2 V14 M14,2 V14 M2,3 H8 V6 H2 Z M8,9 H14 V12 H8 Z";
    private const string SpaceVerticalIcon = "M2,2 H14 M2,8 H14 M2,14 H14 M3,2 V8 H6 V2 Z M9,8 V14 H12 V8 Z";
    private const string CaptureIcon = "M8,1 V4 M8,12 V15 M1,8 H4 M12,8 H15 M5,5 H11 V11 H5 Z";
    private const string CopyIcon = "M6,2 H14 V12 H6 Z M3,5 H2 V15 H10 V14";
    private const string PasteIcon = "M5,3 H2 V15 H14 V3 H11 M5,1 H11 V5 H5 Z M5,9 H11 M5,12 H9";
    private const string DeleteIcon = "M2,4 H14 M6,4 V1 H10 V4 M4,4 L5,15 H11 L12,4 M6.5,7 V12 M9.5,7 V12";
    private const string ShowAllIcon = "M1,8 Q8,-1 15,8 Q8,17 1,8 Z M8,5 A3,3 0 1 1 7.99,5";
    private const string HideAllIcon = "M1,8 Q8,-1 15,8 Q8,17 1,8 Z M3,3 L13,13 M8,5 A3,3 0 1 1 7.99,5";
    private static readonly IReadOnlyDictionary<string, string> PhysicalKeyNames = PresentationKeyboardLayout.CreateDefault().Keys.ToDictionary(key => key.Id, key => key.Label);
    private static readonly RectD DefaultBounds = KeyBounds(PresentationKeyboardLayout.CreateDefault().Keys);
    private const double MinimumPosition = PresentationKeyboardKey.MinimumPosition;
    private sealed record Snapshot(PresentationKeyboardLayout Layout, bool UseDefault, string[] SelectedIds);
    private sealed record KeyLegend(string Primary, string Secondary, double PrimarySize, double SecondarySize,
        Thickness SecondaryMargin, CornerRadius Radius, CornerRadius InnerRadius, Thickness Inset, Thickness Stroke, int PrimitiveCost);
    private readonly Canvas preview = new();
    private readonly Rectangle keyboardOutline = new() { IsHitTestVisible = false, StrokeThickness = 1, StrokeDashArray = new([4, 3]), RadiusX = 5, RadiusY = 5 };
    private readonly ScrollViewer previewViewport = new();
    private readonly ScaleTransform previewTransform = new(1, 1);
    private readonly TextBlock zoomStatus = new() { FontSize = 11, MinWidth = 42, Text = "100%", TextAlignment = System.Windows.TextAlignment.Center };
    private readonly StackPanel properties = new();
    private readonly StackPanel matchingSection = new();
    private readonly StackPanel lockIndicatorSection = new();
    private readonly StackPanel customStyleFields = new();
    private readonly Dictionary<KeyOverrides, CheckBox> styleOverrides = [];
    private readonly NumericValueField relativeFontSize;
    private Button? copyKeysButton, pasteKeysButton, deleteKeysButton, resetKeysButton;
    private readonly List<Button> multiKeyActions = [];
    private readonly FontPickerControl keyFont = new(previewSelectedFont: true);
    private readonly ColorField keyForeground = new(ColorRole.Foreground, showEyedropper: false);
    private readonly ColorField keyBackground = new(ColorRole.Background, showEyedropper: false);
    private readonly ElementStyle keyboardAppearance;
    private readonly SceneRenderer legendRenderer = new();
    private readonly Dictionary<string, PresentationKeyboardKey> originalKeys = [];
    private readonly Dictionary<string, Thumb> keyViews = [];
    private readonly Dictionary<string, NumericValueField> coordinates = [];
    private readonly TextBox label = new();
    private readonly TextBox secondaryLabel = new();
    private readonly CheckBox enabled = new();
    private readonly CheckBox matchRelatedKeys = new();
    private readonly CheckBox showLockIndicator = new();
    private readonly CheckBox snapGrid = new() { Content = Localized("Presentation.Layout.SnapGrid", "Snap to grid"), IsChecked = true };
    private readonly CheckBox snapGap = new() { Content = Localized("Presentation.Layout.SnapGap", "Snap key gaps") };
    private readonly Stack<Snapshot> undoStack = [], redoStack = [];
    private readonly List<PresentationKeyboardKey> clipboard = [];
    private readonly HashSet<NumericValueField> numericEditSessions = [];
    private readonly List<string> previewZOrder = [];
    private readonly TextBlock identity = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly TextBlock status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly Button apply = new() { MinWidth = 96, MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(10, 4, 10, 4) };
    private Button? captureKeyButton, undoButton, redoButton;
    private readonly HashSet<string> invalidFields = [];
    private PresentationKeyboardLayout draft;
    private bool useDefault, refreshing;
    private string? selectedId;
    private readonly HashSet<string> selectedIds = [];
    private PresentationKeyboardKey[]? movingStart;
    private bool movingWasDefault;
    private double movingX, movingY;
    private Point dragPointerStart;
    private RectD dragPreviewBounds;
    private RectD dragKeyBounds;
    private PointD dragAppliedOffset;
    private Snapshot? dragSnapshot;
    private bool capturingKey;
    private IDisposable? captureHotkeyScope;
    private int capturedVirtualKey;
    private MouseButton? capturedMouseButton;
    private readonly DispatcherTimer captureGamepadTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly ushort[] captureGamepadState = new ushort[PresentationDeviceButtons.GamepadSlots];
    private readonly Func<int, ushort> readGamepad;
    private double previewScale = 1;
    private string baselineSignature = "";
    private bool labelUndoRecorded, secondaryUndoRecorded;
    private static ControlTemplate? keyTemplate;
    internal PresentationKeyboardLayout? ResultLayout { get; private set; }
    internal bool Accepted { get; private set; }
    internal PresentationKeyboardLayout Draft => draft.Clone();
    internal string? SelectedKeyId => selectedId;
    internal IReadOnlySet<string> SelectedKeyIds => selectedIds;

    internal KeyboardLayoutEditorWindow(PresentationKeyboardLayout? source, Window? owner = null,
        ElementStyle? appearance = null, Func<int, ushort>? readGamepad = null)
    {
        Owner = owner;
        this.readGamepad = readGamepad ?? PresentationDeviceButtons.ReadGamepad;
        keyboardAppearance = appearance?.Clone() ?? new ElementStyle();
        draft = (source ?? PresentationKeyboardLayout.CreateDefault()).Clone(); useDefault = source is null;
        baselineSignature = Signature();
        double standardPreviewWidth = DefaultBounds.Width * UnitPixels + Inset * 2;
        Title = L.Get("Presentation.Layout.Title");
        double desiredWidth = Math.Max(1240,
            standardPreviewWidth + PropertyPanelLayout.ContentWidth + WindowLayout.ContentInset * 2 + 16 + 10 + 2 + WindowLayout.ScrollBarWidth);
        var (workWidth, workHeight) = OwnerWorkArea(owner);
        MinWidth = Math.Min(640, workWidth);
        MinHeight = Math.Min(420, workHeight);
        MaxWidth = Math.Max(MinWidth, workWidth);
        MaxHeight = Math.Max(MinHeight, workHeight);
        // The complete keyboard remains horizontally scrollable when the work area is narrow.
        Width = Math.Min(desiredWidth, MaxWidth);
        Height = Math.Min(640, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Topmost = true; Icon = AppIcon.WindowImage;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin() };
        var hint = new TextBlock { Text = L.Get("Presentation.Layout.Hint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 12) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); DockPanel.SetDock(hint, Dock.Top); body.Children.Add(hint);
        var operations = new RoundedSurfaceBorder { Margin = new(0, 0, 0, 12), Padding = new(10, 6, 10, 6), CornerRadius = new(6), BorderThickness = new(1), Child = LayoutOperations() };
        operations.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
        operations.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        AutomationProperties.SetAutomationId(operations, "LayoutOperations");
        DockPanel.SetDock(operations, Dock.Top);
        body.Children.Add(operations);
        var footer = new DockPanel { Margin = new(0, 12, 0, 0), LastChildFill = false };
        AutomationProperties.SetAutomationId(footer, "LayoutFooter");
        var footerTools = new StackPanel { Orientation = Orientation.Horizontal };
        var reset = IconButton("Presentation.Layout.Reset", L.Get("Presentation.Layout.Reset"), ResetLayoutIcon, ResetToDefault);
        reset.Width = reset.Height = PropertyPanelLayout.ControlHeight;
        reset.Padding = new(8); reset.Margin = new(0);
        footerTools.Children.Add(reset);
        footerTools.Children.Add(ToolbarSeparator());
        footerTools.Children.Add(IconButton("Presentation.Layout.EnableAll", L.Get("Presentation.Layout.EnableAll"), ShowAllIcon, () => SetAllEnabled(true)));
        footerTools.Children.Add(IconButton("Presentation.Layout.DisableAll", L.Get("Presentation.Layout.DisableAll"), HideAllIcon, () => SetAllEnabled(false)));
        DockPanel.SetDock(footerTools, Dock.Left); footer.Children.Add(footerTools);
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new(16, 0, 16, 0);
        DockPanel.SetDock(status, Dock.Left);
        footer.Children.Add(status);
        var cancel = Button("Presentation.Layout.Cancel", () => { if (IsVisible) SessionWindowCoordinator.SetModalResult(this, false); }); cancel.IsCancel = true; cancel.MinWidth = 96;
        apply.Content = L.Get("Presentation.Layout.Apply"); apply.Click += (_, _) => Accept();
        AppInputPolicy.AttachFormSubmit(this, apply, () => invalidFields.Count == 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(cancel); actions.Children.Add(apply); apply.Margin = new(8, 0, 0, 0);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new(PropertyPanelLayout.ContentWidth) });
        previewViewport.Content = preview;
        preview.LayoutTransform = previewTransform;
        previewViewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        previewViewport.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var previewPanel = new DockPanel();
        var previewHeader = new DockPanel { Margin = new(0, 0, 0, 8) };
        var previewTitle = PropertyPanelLayout.SectionTitle(Localized("Presentation.Layout.Preview", "Keyboard preview"));
        DockPanel.SetDock(previewTitle, Dock.Left); previewHeader.Children.Add(previewTitle);
        var zoomActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        zoomActions.Children.Add(IconButton("Presentation.Layout.ZoomOut", Localized("Presentation.Layout.ZoomOut", "Zoom out"),
            "M3,8 H13", () => ChangePreviewZoom(-.1)));
        zoomStatus.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        zoomStatus.VerticalAlignment = VerticalAlignment.Center;
        zoomActions.Children.Add(zoomStatus);
        zoomActions.Children.Add(IconButton("Presentation.Layout.ZoomIn", Localized("Presentation.Layout.ZoomIn", "Zoom in"),
            "M3,8 H13 M8,3 V13", () => ChangePreviewZoom(.1)));
        zoomActions.Children.Add(IconButton("Presentation.Layout.Fit", Localized("Presentation.Layout.Fit", "Fit keyboard"),
            "M1,5 V1 H5 M11,1 H15 V5 M15,11 V15 H11 M5,15 H1 V11", FitPreview));
        DockPanel.SetDock(zoomActions, Dock.Right); previewHeader.Children.Add(zoomActions);
        DockPanel.SetDock(previewHeader, Dock.Top); previewPanel.Children.Add(previewHeader);
        var viewportFrame = new RoundedSurfaceBorder { Child = previewViewport, BorderThickness = new(1), CornerRadius = new(5), Padding = new(4), Margin = new(0, 0, 0, 0) };
        viewportFrame.SetResourceReference(Border.BackgroundProperty, "CanvasInput");
        viewportFrame.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        previewPanel.Children.Add(viewportFrame);
        var frame = new RoundedSurfaceBorder { Child = previewPanel, BorderThickness = new(1), CornerRadius = new(8), Padding = new(10), Margin = new(0, 0, 16, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "CanvasInput"); frame.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder"); columns.Children.Add(frame);
        PropertyPanelLayout.Scope(properties);
        var selectedHeader = new DockPanel { Margin = new(0, 0, 0, 6), LastChildFill = true };
        var title = PropertyPanelLayout.SectionTitle(L.Get("Presentation.Layout.Selected")); DockPanel.SetDock(title, Dock.Left); selectedHeader.Children.Add(title);
        enabled.Content = null; enabled.SetResourceReference(StyleProperty, "CanvasPropertySwitch");
        enabled.HorizontalAlignment = HorizontalAlignment.Right; enabled.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(enabled, Dock.Right); selectedHeader.Children.Add(enabled);
        properties.Children.Add(selectedHeader);
        var identityFrame = new RoundedSurfaceBorder { Padding = new(8, 6, 8, 6), Margin = new(0, 2, 0, 8), CornerRadius = new(5), BorderThickness = new(1) };
        identityFrame.SetResourceReference(Border.BackgroundProperty, "CanvasInput");
        identityFrame.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        identity.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText");
        identity.FontSize = 12; identity.Margin = new(0); identityFrame.Child = identity; properties.Children.Add(identityFrame);
        enabled.Click += (_, _) => ToggleSelectedEnabled();
        AutomationProperties.SetName(enabled, L.Get("Presentation.Layout.Enabled"));
        Section(properties, "Presentation.Layout.TextSection", "Labels");
        label.MaxLength = 64; label.Height = PropertyPanelLayout.ControlHeight; AutomationProperties.SetName(label, L.Get("Presentation.Layout.KeyLabel"));
        label.GotKeyboardFocus += (_, _) => labelUndoRecorded = false;
        label.TextChanged += (_, _) =>
        {
            if (refreshing) return;
            RecordTextEdit(ref labelUndoRecorded);
            UpdateSelected(key => key.Label = label.Text, "Label", recordUndo: false);
        };
        properties.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("Presentation.Layout.KeyLabel")), label));
        secondaryLabel.MaxLength = 64; secondaryLabel.Height = PropertyPanelLayout.ControlHeight; AutomationProperties.SetName(secondaryLabel, Localized("Presentation.Layout.SecondaryLabel", "Secondary label"));
        secondaryLabel.GotKeyboardFocus += (_, _) => secondaryUndoRecorded = false;
        secondaryLabel.TextChanged += (_, _) =>
        {
            if (refreshing) return;
            RecordTextEdit(ref secondaryUndoRecorded);
            UpdateSelected(key => key.SecondaryLabel = string.IsNullOrEmpty(secondaryLabel.Text) ? null : secondaryLabel.Text,
                "SecondaryLabel", recordUndo: false);
        };
        properties.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(Localized("Presentation.Layout.SecondaryLabel", "Secondary label")), secondaryLabel));
        Section(matchingSection, "Presentation.Layout.MatchingSection", "Key matching");
        matchingSection.Children.Add(PropertyPanelLayout.BooleanRow(
            Localized("Presentation.Layout.MatchRelatedKeys", "Match related keys"),
            matchRelatedKeys));
        AutomationProperties.SetName(matchRelatedKeys, Localized("Presentation.Layout.MatchRelatedKeys", "Match related keys"));
        string relatedHint = Localized("Presentation.Layout.MatchRelatedKeysHint",
            "Match main-row and numpad numbers, Enter, and paired punctuation keys");
        matchRelatedKeys.ToolTip = relatedHint; AutomationProperties.SetHelpText(matchRelatedKeys, relatedHint);
        matchRelatedKeys.Click += (_, _) =>
        {
            if (refreshing) return;
            bool value = SelectedKeys().Any(key => !key.MatchRelatedKeys);
            UpdateSelected(key => key.MatchRelatedKeys = value);
            matchRelatedKeys.IsChecked = value;
        };
        properties.Children.Add(matchingSection);
        Section(lockIndicatorSection, "Presentation.Layout.LockIndicatorSection", "Lock indicator");
        lockIndicatorSection.Children.Add(PropertyPanelLayout.BooleanRow(
            Localized("Presentation.Layout.ShowLockIndicator", "Show lock indicator"),
            showLockIndicator));
        AutomationProperties.SetName(showLockIndicator, Localized("Presentation.Layout.ShowLockIndicator", "Show lock indicator"));
        string lockHint = Localized("Presentation.Layout.ShowLockIndicatorHint",
            "Show a dot in the key corner while this lock is active");
        showLockIndicator.ToolTip = lockHint; AutomationProperties.SetHelpText(showLockIndicator, lockHint);
        showLockIndicator.Click += (_, _) =>
        {
            if (refreshing) return;
            bool value = SelectedKeys().Any(key => !key.ShowLockIndicator);
            UpdateSelected(key => key.ShowLockIndicator = value);
            showLockIndicator.IsChecked = value;
        };
        properties.Children.Add(lockIndicatorSection);
        string sizeCaption = L.Get("Presentation.Layout.RelativeSize");
        var sizeInput = new TextBox();
        AutomationProperties.SetName(sizeInput, sizeCaption);
        relativeFontSize = new NumericValueField(sizeInput, PresentationKeyboardKeyStyle.MinimumSizePercent,
            PresentationKeyboardKeyStyle.MaximumSizePercent, 25, 400, 5) { Unit = "%" };
        relativeFontSize.SetScrubAnchor(NumericPropertyKind.FontSize, sizeCaption);
        relativeFontSize.EnableStepper();
        relativeFontSize.GestureStarted += () => { if (!refreshing) TrackNumericEdit(relativeFontSize); };
        relativeFontSize.GestureCompleted += _ => numericEditSessions.Remove(relativeFontSize);
        relativeFontSize.Input.GotKeyboardFocus += (_, _) => numericEditSessions.Remove(relativeFontSize);
        relativeFontSize.ValueChanged += value =>
        {
            if (refreshing) return;
            TrackNumericEdit(relativeFontSize);
            ChangeKeyStyle(style => style with { RelativeSize = PresentationKeyboardKeyStyle.RelativeSizeFromPercent(value) },
                "KeyRelativeSize", recordUndo: false);
        };
        relativeFontSize.InvalidInput += () => { if (!refreshing) { invalidFields.Add("KeyRelativeSize"); RefreshStatus(); } };
        keyFont.FontCommitted += name => ChangeKeyStyle(style => style with { FontFamily = name });
        WireKeyColor(keyForeground, (style, value) => style with { Foreground = value });
        WireKeyColor(keyBackground, (style, value) => style with { Background = value });
        keyForeground.SetColorLabel(L.Get("Presentation.Foreground"));
        keyBackground.SetColorLabel(L.Get("Presentation.Background"));
        AddStyleField("Presentation.Layout.RelativeSize", KeyOverrides.Size, relativeFontSize);
        AddStyleField("Inspector.FontFamily", KeyOverrides.Font, keyFont);
        AddStyleField("Presentation.Foreground", KeyOverrides.Foreground, keyForeground);
        AddStyleField("Presentation.Background", KeyOverrides.Background, keyBackground);
        void AddStyleField(string caption, KeyOverrides flag, FrameworkElement control)
        {
            var toggle = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new(PropertyPanelLayout.RowGap, 0, 0, 0) };
            toggle.SetResourceReference(StyleProperty, "CanvasPropertySwitch");
            AutomationProperties.SetName(toggle, L.Format("Presentation.Layout.OverrideField", L.Get(caption)));
            toggle.ToolTip = AutomationProperties.GetName(toggle);
            styleOverrides.Add(flag, toggle);
            toggle.Click += (_, _) => ToggleKeyOverride(flag);
            var group = new Grid();
            group.ColumnDefinitions.Add(new());
            group.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            group.Children.Add(control);
            Grid.SetColumn(toggle, 1);
            group.Children.Add(toggle);
            var label = new PropertyLabel(L.Get(caption));
            if (control is NumericValueField numeric) numeric.SetLabel(label);
            customStyleFields.Children.Add(PropertyPanelLayout.Row(label, group));
        }
        Section(properties, "Presentation.Layout.GeometrySection", "Position and size");
        properties.Children.Add(PropertyPanelLayout.Row(
            new PropertyLabel(L.Get("Property.Position")),
            PropertyPanelLayout.Pair(
                Coordinate("X", MinimumPosition, 63.75, -4, 24),
                Coordinate("Y", MinimumPosition, 63.75, -4, 8))));
        properties.Children.Add(PropertyPanelLayout.Row(
            new PropertyLabel(L.Get("Property.Size")),
            PropertyPanelLayout.Pair(
                Coordinate("Width", .25, 128, .25, 4),
                Coordinate("Height", .25, 128, .25, 4))));
        var units = new TextBlock { Text = L.Get("Presentation.Layout.UnitsHint"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 10, 0, 0) };
        units.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); properties.Children.Add(units);
        Section(properties, "Presentation.Layout.KeyAppearance", "Style overrides");
        properties.Children.Add(customStyleFields);
        Grid.SetColumn(properties, 1); columns.Children.Add(new ScrollViewer { Content = properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Grid.SetColumn(columns.Children[^1], 1);
        body.Children.Add(columns); Content = body; AppWindowChrome.ApplyDialog(this);
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(CaptureKeyMessage);
        };
        Deactivated += (_, _) => { if (capturingKey) CancelKeyCapture(); capturedVirtualKey = 0; capturedMouseButton = null; };
        IsVisibleChanged += (_, _) => { if (!IsVisible && capturingKey) CancelKeyCapture(); };
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(CaptureMouseDown), handledEventsToo: true);
        PreviewMouseWheel += (_, e) =>
        {
            if (!capturingKey || e.Delta == 0) return;
            e.Handled = true;
            CapturePhysicalKey(e.Delta > 0 ? 6 : 7, 0, false, PresentationKeyDevice.Mouse);
        };
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (capturedMouseButton != e.ChangedButton) return;
            capturedMouseButton = null; e.Handled = true;
        }), handledEventsToo: true);
        captureGamepadTimer.Tick += (_, _) => CaptureGamepad();
        Closed += (_, _) => { if (capturingKey) CancelKeyCapture(); legendRenderer.Dispose(); };
        PreviewKeyDown += (_, e) =>
        {
            if (capturingKey && e.OriginalSource is not TextBox)
            {
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
                    CapturePhysicalKey(virtualKey, (int)MapVirtualKey((uint)virtualKey, 0), IsExtendedVirtualKey(virtualKey));
                }
                return;
            }
            bool textEditing = IsTextEditingSource(e.OriginalSource);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                var focusedText = Keyboard.FocusedElement as TextBox;
                if (e.Key == Key.Z && !(textEditing && focusedText?.CanUndo == true))
                { Undo(); e.Handled = true; return; }
                if (e.Key == Key.Y && !(textEditing && focusedText?.CanRedo == true))
                { Redo(); e.Handled = true; return; }
                if (!textEditing && e.Key == Key.C) { CopySelectedKeys(); e.Handled = true; return; }
                if (!textEditing && e.Key == Key.V) { PasteKeys(); e.Handled = true; return; }
            }
            if (e.OriginalSource is not Thumb thumb || !keyViews.Values.Contains(thumb)) return;
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            { SelectKeys(draft.Keys.Select(key => key.Id)); e.Handled = true; }
            else if ((e.Key is Key.Left or Key.Right or Key.Up or Key.Down) && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
            {
                double step = Keyboard.Modifiers == ModifierKeys.Shift ? 1 : GridStep;
                NudgeSelected(e.Key switch { Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), _ => (0, step) });
                e.Handled = true;
            }
        };
        GotKeyboardFocus += (_, e) =>
        {
            if (capturingKey && IsTextEditingSource(e.NewFocus)) CancelKeyCapture();
        };
        BuildPreview();
        if (draft.Keys.Count > 0) SelectKey(draft.Keys[0].Id);
        else
        {
            enabled.IsEnabled = label.IsEnabled = false; foreach (var field in coordinates.Values) field.IsEnabled = false;
            identity.Text = L.Get("Presentation.Layout.NoSelection"); RefreshStatus();
        }
    }

    private static (double Width, double Height) OwnerWorkArea(Window? owner)
    {
        double width = SystemParameters.WorkArea.Width, height = SystemParameters.WorkArea.Height;
        if (owner is not null && owner.IsVisible && PresentationSource.FromVisual(owner) is not null)
        {
            var center = owner.PointToScreen(new Point(owner.ActualWidth / 2, owner.ActualHeight / 2));
            if (SessionWindowLayout.EditingWorkArea(owner, new((int)Math.Round(center.X), (int)Math.Round(center.Y))) is { } work)
            {
                var dpi = VisualTreeHelper.GetDpi(owner);
                width = work.Width * 96 / (96 * dpi.DpiScaleX);
                height = work.Height * 96 / (96 * dpi.DpiScaleY);
            }
        }
        return (Math.Max(1, width - 24), Math.Max(1, height - 24));
    }

    private FrameworkElement LayoutOperations()
    {
        var strip = new WrapPanel();
        StackPanel Group()
        {
            if (strip.Children.Count > 0)
                ((StackPanel)strip.Children[^1]).Children.Add(ToolbarSeparator());
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 2, 0, 2) };
            strip.Children.Add(group);
            return group;
        }
        var keyActions = Group();
        undoButton = IconButton("Edit.Undo", Localized("ToolbarV2.Undo", "Undo"), CommandGlyphs.Undo, Undo, "Ctrl+Z");
        redoButton = IconButton("Edit.Redo", Localized("ToolbarV2.Redo", "Redo"), CommandGlyphs.Redo, Redo, "Ctrl+Y");
        keyActions.Children.Add(undoButton); keyActions.Children.Add(redoButton);
        keyActions = Group();
        captureKeyButton = TextIconButton("Presentation.Layout.CaptureKey", Localized("Presentation.Layout.CaptureKey", "Capture key"), CaptureIcon, BeginKeyCapture);
        keyActions.Children.Add(captureKeyButton);
        keyActions.Children.Add(TextIconButton("Presentation.Layout.AddPlaceholder", Localized("Presentation.Layout.AddPlaceholder", "Add placeholder"), "M8,1 V15 M1,8 H15", AddPlaceholder, 6));
        keyActions = Group();
        copyKeysButton = IconButton("Presentation.Layout.CopyKeys", Localized("Presentation.Layout.CopyKeys", "Copy"), CopyIcon, CopySelectedKeys, "Ctrl+C");
        pasteKeysButton = IconButton("Presentation.Layout.PasteKeys", Localized("Presentation.Layout.PasteKeys", "Paste"), PasteIcon, PasteKeys, "Ctrl+V");
        deleteKeysButton = IconButton("Presentation.Layout.DeleteKeys", Localized("Presentation.Layout.DeleteKeys", "Delete"), DeleteIcon, DeleteSelectedKeys);
        keyActions.Children.Add(copyKeysButton); keyActions.Children.Add(pasteKeysButton); keyActions.Children.Add(deleteKeysButton);

        var selectionActions = Group();
        AddMultiKeyAction("Presentation.Layout.AlignLeft", AlignLeftIcon, () => AlignSelected(KeyAlignment.Left));
        AddMultiKeyAction("Presentation.Layout.AlignCenter", AlignCenterIcon, () => AlignSelected(KeyAlignment.Center));
        AddMultiKeyAction("Presentation.Layout.AlignRight", AlignRightIcon, () => AlignSelected(KeyAlignment.Right));
        selectionActions = Group();
        AddMultiKeyAction("Presentation.Layout.AlignTop", AlignTopIcon, () => AlignSelected(KeyAlignment.Top));
        AddMultiKeyAction("Presentation.Layout.AlignMiddle", AlignMiddleIcon, () => AlignSelected(KeyAlignment.Middle));
        AddMultiKeyAction("Presentation.Layout.AlignBottom", AlignBottomIcon, () => AlignSelected(KeyAlignment.Bottom));
        selectionActions = Group();
        AddMultiKeyAction("Presentation.Layout.SpaceHorizontal", SpaceHorizontalIcon, SpaceSelectedHorizontal);
        AddMultiKeyAction("Presentation.Layout.SpaceVertical", SpaceVerticalIcon, SpaceSelectedVertical);
        selectionActions = Group();
        resetKeysButton = AlignmentButton("Presentation.Layout.ResetKey", ResetLayoutIcon, ResetSelectedKey);
        selectionActions.Children.Add(resetKeysButton);

        void AddMultiKeyAction(string key, string geometry, Action action)
        {
            var button = AlignmentButton(key, geometry, action);
            selectionActions.Children.Add(button);
            multiKeyActions.Add(button);
        }

        var editingActions = Group();
        AddToggle(editingActions, snapGrid, Localized("Presentation.Layout.SnapGrid", "Snap to grid"));
        AddToggle(Group(), snapGap, Localized("Presentation.Layout.SnapGap", "Snap key gaps"));
        return strip;
    }

    private static void AddToggle(Panel parent, CheckBox toggle, string name, double margin = 0)
    {
        toggle.Margin = new(margin, 0, 0, 0);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(toggle, name);
        parent.Children.Add(toggle);
    }

    private static bool IsTextEditingSource(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = current is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(current))
            if (current is TextBox) return true;
        return false;
    }

    private static Button IconButton(string automationId, string label, string geometry, Action action, string? shortcut = null)
    {
        var button = ActionIconButton.Create(CommandGlyphs.Create(geometry), label, action, shortcut);
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static Button TextIconButton(string automationId, string label, string geometry, Action action, double left = 0)
    {
        var glyph = CommandGlyphs.Create(geometry); glyph.Width = glyph.Height = 14;
        var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new());
        content.Children.Add(glyph);
        var caption = new TextBlock { Text = label, Margin = new(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(caption, 1); content.Children.Add(caption);
        var button = new Button { Content = content, Height = 30, MinHeight = 0, Padding = new(7, 0, 8, 0), Margin = new(left, 0, 0, 0) };
        AutomationProperties.SetName(button, label); AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => action();
        return button;
    }

    private static Border ToolbarSeparator()
    {
        var separator = new Border { Width = 1, Margin = new(5, 4, 5, 4) };
        separator.SetResourceReference(Border.BackgroundProperty, "CanvasBorder");
        return separator;
    }

    private static Button AlignmentButton(string key, string geometry, Action action) =>
        IconButton(key, Localized(key, ButtonFallback(key)), geometry, action);

    private static void Section(StackPanel parent, string key, string fallback)
    {
        var divider = new Border { Height = 1, Margin = new(0, 10, 0, 8) };
        divider.SetResourceReference(Border.BackgroundProperty, "CanvasBorder");
        parent.Children.Add(divider);
        var title = PropertyPanelLayout.SectionTitle(Localized(key, fallback));
        title.Margin = new(0, 0, 0, 6);
        parent.Children.Add(title);
    }

    private string Signature() => JsonSerializer.Serialize(new { UseDefault = useDefault, Layout = draft });
    private void ChangePreviewZoom(double delta)
    {
        SetPreviewScale(previewScale + delta);
    }
    private void FitPreview()
    {
        var (contentWidth, contentHeight) = PreviewContentSize();
        double availableWidth = previewViewport.ViewportWidth > 0
            ? previewViewport.ViewportWidth
            : previewViewport.ActualWidth;
        double availableHeight = previewViewport.ViewportHeight > 0
            ? previewViewport.ViewportHeight
            : previewViewport.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0 || contentWidth <= 0 || contentHeight <= 0) return;
        double scale = Math.Min(availableWidth / contentWidth, availableHeight / contentHeight);
        SetPreviewScale(scale, preserveViewportCenter: false);
    }
    private void SetPreviewScale(double scale, bool preserveViewportCenter = true)
    {
        double next = Math.Clamp(scale, .25, 2);
        if (Math.Abs(next - previewScale) < .001)
        {
            zoomStatus.Text = Math.Round(next * 100) + "%";
            return;
        }
        double previousScale = previewScale;
        double centerX = previousScale > 0
            ? (previewViewport.HorizontalOffset + previewViewport.ViewportWidth / 2) / previousScale
            : 0;
        double centerY = previousScale > 0
            ? (previewViewport.VerticalOffset + previewViewport.ViewportHeight / 2) / previousScale
            : 0;
        previewScale = next;
        previewTransform.ScaleX = previewTransform.ScaleY = previewScale;
        zoomStatus.Text = Math.Round(previewScale * 100) + "%";
        if (preserveViewportCenter)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!previewViewport.IsLoaded) return;
                previewViewport.ScrollToHorizontalOffset(centerX * previewScale - previewViewport.ViewportWidth / 2);
                previewViewport.ScrollToVerticalOffset(centerY * previewScale - previewViewport.ViewportHeight / 2);
            }));
        }
        else
        {
            previewViewport.ScrollToLeftEnd();
            previewViewport.ScrollToTop();
        }
    }
    private static RectD KeyBounds(IEnumerable<PresentationKeyboardKey> keys) =>
        keys.Select(key => new RectD(key.X, key.Y, key.Width, key.Height)).Aggregate(RectD.Union);

    private RectD PreviewBounds() =>
        movingStart is not null ? dragPreviewBounds :
        draft.Keys.Count == 0 ? DefaultBounds : RectD.Union(DefaultBounds, KeyBounds(draft.Keys));

    private (double Width, double Height) PreviewContentSize()
    {
        var bounds = PreviewBounds();
        return (Math.Max(400, bounds.Width * UnitPixels + Inset * 2),
            Math.Max(220, bounds.Height * UnitPixels + TopInset + Inset));
    }

    private static Button Button(string key, Action action)
    {
        string text = Localized(key, ButtonFallback(key));
        var button = new Button { Content = text, MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(10, 4, 10, 4) };
        AutomationProperties.SetName(button, text); AutomationProperties.SetAutomationId(button, key);
        button.Click += (_, _) => action(); return button;
    }
    private static string Localized(string key, string fallback)
    {
        try { return L.Get(key); }
        catch (KeyNotFoundException) { return fallback; }
    }
    private static string ButtonFallback(string key) => key switch
    {
        "Presentation.Layout.CaptureKey" => "Capture key",
        "Presentation.Layout.AddPlaceholder" => "Add placeholder",
        "Presentation.Layout.CopyKeys" => "Copy",
        "Presentation.Layout.PasteKeys" => "Paste",
        "Presentation.Layout.DeleteKeys" => "Delete",
        "Presentation.Layout.AlignLeft" => "Align left",
        "Presentation.Layout.AlignCenter" => "Align center",
        "Presentation.Layout.AlignRight" => "Align right",
        "Presentation.Layout.AlignTop" => "Align top",
        "Presentation.Layout.AlignMiddle" => "Align middle",
        "Presentation.Layout.AlignBottom" => "Align bottom",
        "Presentation.Layout.SpaceHorizontal" => "Space across",
        "Presentation.Layout.SpaceVertical" => "Space down",
        "Presentation.Layout.ResetKey" => "Reset key",
        _ => key[(key.LastIndexOf('.') + 1)..]
    };
    private static Button MarginButton(string key, Action action, double left)
    {
        var button = Button(key, action); button.Margin = new(left, 0, 0, 0); return button;
    }
    private Snapshot CaptureSnapshot() => new(draft.Clone(), useDefault, selectedIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    private void RecordUndo(Snapshot? snapshot = null)
    {
        undoStack.Push(snapshot ?? CaptureSnapshot());
        if (undoStack.Count > 100)
        {
            var keep = undoStack.Take(100).ToArray(); undoStack.Clear();
            for (int index = keep.Length - 1; index >= 0; index--) undoStack.Push(keep[index]);
        }
        redoStack.Clear();
    }
    private void RecordTextEdit(ref bool recorded)
    {
        if (recorded) return;
        RecordUndo(); recorded = true;
    }
    internal bool CanUndo => undoStack.Count > 0;
    internal bool CanRedo => redoStack.Count > 0;
    internal void Undo()
    {
        if (undoStack.Count == 0) return;
        numericEditSessions.Clear(); redoStack.Push(CaptureSnapshot()); RestoreSnapshot(undoStack.Pop());
    }
    internal void Redo()
    {
        if (redoStack.Count == 0) return;
        numericEditSessions.Clear(); undoStack.Push(CaptureSnapshot()); RestoreSnapshot(redoStack.Pop());
    }
    private void RestoreSnapshot(Snapshot snapshot)
    {
        draft = snapshot.Layout.Clone(); useDefault = snapshot.UseDefault; invalidFields.Clear();
        labelUndoRecorded = secondaryUndoRecorded = false; numericEditSessions.Clear();
        previewZOrder.Clear();
        BuildPreview(); SelectKeys(snapshot.SelectedIds); RefreshCoordinates();
    }
    private void BeginKeyCapture()
    {
        if (!IsEnabled) return;
        if (capturingKey) { CancelKeyCapture(); return; }
        captureHotkeyScope = SessionWindowCoordinator.Find(this)?.SuspendHotkeysForRecording();
        EditorShortcutPolicy.RecordingWindows.Add(this);
        capturingKey = true; UpdateCaptureState(); RefreshStatus(); captureKeyButton?.Focus();
        for (int slot = 0; slot < captureGamepadState.Length; slot++)
            captureGamepadState[slot] = readGamepad(slot);
        captureGamepadTimer.Start();
    }
    private void CancelKeyCapture()
    {
        capturingKey = false;
        captureGamepadTimer.Stop();
        EditorShortcutPolicy.RecordingWindows.Remove(this);
        captureHotkeyScope?.Dispose();
        captureHotkeyScope = null;
        UpdateCaptureState();
        RefreshStatus();
    }
    private void UpdateCaptureState()
    {
        if (captureKeyButton is null) return;
        if (capturingKey)
        {
            captureKeyButton.SetResourceReference(Control.BackgroundProperty, "CanvasHover");
            captureKeyButton.SetResourceReference(Control.BorderBrushProperty, "CanvasAccent");
            captureKeyButton.BorderThickness = new(1);
        }
        else
        {
            captureKeyButton.SetResourceReference(Control.BackgroundProperty, "CanvasButton");
            captureKeyButton.BorderBrush = Brushes.Transparent;
            captureKeyButton.BorderThickness = new(0);
        }
    }
    private nint CaptureKeyMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is not (0x100 or 0x101 or 0x104 or 0x105)) return 0;
        int virtualKey = (int)wParam;
        if (capturedVirtualKey != 0 && virtualKey == capturedVirtualKey)
        {
            handled = true;
            if (message is 0x101 or 0x105) capturedVirtualKey = 0;
            return 0;
        }
        if (!capturingKey || message is 0x101 or 0x105) return 0;
        handled = true;
        uint flags = unchecked((uint)lParam);
        if ((flags & (1u << 30)) != 0) return 0;
        int scanCode = (int)(flags >> 16 & 0xFF);
        bool isExtended = (flags & (1u << 24)) != 0;
        capturedVirtualKey = virtualKey;
        virtualKey = virtualKey switch
        {
            0x10 => scanCode == 0x36 ? 0xA1 : 0xA0,
            0x11 => isExtended ? 0xA3 : 0xA2,
            0x12 => isExtended ? 0xA5 : 0xA4,
            _ => virtualKey
        };
        CapturePhysicalKey(virtualKey, scanCode, isExtended);
        return 0;
    }
    private void CaptureMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.ChangedButton == MouseButton.Left && EditorShortcutPolicy.Ancestors(e.OriginalSource as DependencyObject).Contains(captureKeyButton)) ||
            !capturingKey && capturedMouseButton != e.ChangedButton) return;
        e.Handled = true;
        if (!capturingKey) return;
        int button = e.ChangedButton switch
        {
            MouseButton.Left => 1, MouseButton.Right => 2, MouseButton.Middle => 3,
            MouseButton.XButton1 => 4, MouseButton.XButton2 => 5, _ => 0
        };
        if (button == 0) return;
        capturedMouseButton = e.ChangedButton;
        CapturePhysicalKey(button, 0, false, PresentationKeyDevice.Mouse);
    }

    private void CaptureGamepad()
    {
        if (!capturingKey) return;
        for (int slot = 0; slot < captureGamepadState.Length; slot++)
        {
            ushort buttons = readGamepad(slot);
            int pressed = buttons & ~captureGamepadState[slot];
            captureGamepadState[slot] = buttons;
            if (pressed == 0) continue;
            int code = System.Numerics.BitOperations.TrailingZeroCount((uint)pressed) + 1;
            CapturePhysicalKey(code, 0, false, PresentationKeyDevice.Gamepad);
            return;
        }
    }

    private void CapturePhysicalKey(int virtualKey, int scanCode, bool isExtended, PresentationKeyDevice device = PresentationKeyDevice.Keyboard)
    {
        CancelKeyCapture();
        if (virtualKey is <= 0 or >= 255) return;
        var added = new PresentationKeyboardKey
        {
            VirtualKey = virtualKey, ScanCode = scanCode, IsExtended = isExtended, DeviceType = device,
            Label = PresentationDeviceButtons.Label(device, virtualKey)
        };
        if ((draft.Keys.FirstOrDefault(item => item.Id == added.Id) ??
             (device == PresentationKeyDevice.Keyboard
                 ? draft.Keys.FirstOrDefault(item => item.MatchesPhysicalKey(virtualKey, scanCode, isExtended)) : null)) is { } existing)
        { SelectKey(existing.Id); RefreshStatus(); return; }
        if (draft.Keys.Count >= PresentationKeyboardLayout.MaximumKeys) { RefreshStatus(); return; }
        var (x, y) = FindFreePosition(1, 1);
        if (double.IsNaN(x)) { RefreshStatus(); return; }
        added.X = x; added.Y = y;
        if (!added.IsValid()) { RefreshStatus(); return; }
        RecordUndo(); draft.Keys.Add(added); useDefault = false;
        BuildPreview(); SelectKey(added.Id);
    }
    internal static bool IsExtendedVirtualKey(int virtualKey) =>
        virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
            0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or
            >= 0xA3 and <= 0xA5 or >= 0xA6 and <= 0xB7;
    internal void AddPlaceholder()
    {
        if (!IsEnabled || draft.Keys.Count >= PresentationKeyboardLayout.MaximumKeys) return;
        var (x, y) = FindFreePosition(1, 1);
        if (double.IsNaN(x)) return;
        var added = PresentationKeyboardLayout.CreatePlaceholder(L.Get("Presentation.Layout.CustomKey"));
        added.X = x; added.Y = y;
        RecordUndo(); draft.Keys.Add(added); useDefault = false;
        BuildPreview(); SelectKey(added.Id);
    }
    internal void DeleteSelectedKeys()
    {
        var selected = SelectedKeys();
        if (selected.Length == 0) return;
        RecordUndo();
        draft.Keys.RemoveAll(key => selectedIds.Contains(key.Id));
        useDefault = false; selectedIds.Clear(); selectedId = null;
        BuildPreview();
        if (draft.Keys.Count > 0) SelectKey(draft.Keys[0].Id); else RefreshSelection();
    }
    internal void CopySelectedKeys()
    {
        var selection = SelectedKeys();
        if (selection.Length == 0) return;
        clipboard.Clear(); clipboard.AddRange(selection.Select(key => key.Clone()));
        RefreshStatus();
    }
    internal void PasteKeys()
    {
        if (!IsEnabled || clipboard.Count == 0) return;
        var additions = new List<PresentationKeyboardKey>();
        foreach (var source in clipboard)
        {
            if (draft.Keys.Count + additions.Count >= PresentationKeyboardLayout.MaximumKeys) break;
            var copy = source.Clone();
            if (copy.IsPlaceholder || draft.Keys.Any(key => key.Id == copy.Id) || additions.Any(key => key.Id == copy.Id))
            {
                copy.IsPlaceholder = true; copy.CustomId = "custom:" + Guid.NewGuid().ToString("N");
                copy.VirtualKey = 0; copy.ScanCode = 0; copy.IsExtended = false;
                copy.DeviceType = PresentationKeyDevice.Keyboard;
                copy.MatchRelatedKeys = false; copy.ShowLockIndicator = false;
            }
            var (x, y) = FindFreePosition(copy.Width, copy.Height, additions);
            if (double.IsNaN(x)) break;
            copy.X = x; copy.Y = y; additions.Add(copy);
        }
        if (additions.Count == 0) return;
        RecordUndo(); draft.Keys.AddRange(additions); useDefault = false;
        BuildPreview(); SelectKeys(additions.Select(key => key.Id));
    }
    private (double X, double Y) FindFreePosition(double width, double height,
        IReadOnlyList<PresentationKeyboardKey>? pending = null)
    {
        var occupied = pending is { Count: > 0 } ? draft.Keys.Concat(pending).ToArray() : draft.Keys.ToArray();
        double maximum = PresentationKeyboardKey.MaximumPosition;
        // Preserve the usual top-to-bottom placement, then try negative space
        // outward from the origin before giving up on a packed layout.
        foreach (double yStep in new[] { GridStep, -GridStep })
            foreach (double xStep in new[] { GridStep, -GridStep })
                for (double y = yStep > 0 ? 0 : -GridStep; y >= MinimumPosition && y + height <= maximum; y += yStep)
                    for (double x = xStep > 0 ? 0 : -GridStep; x >= MinimumPosition && x + width <= maximum; x += xStep)
                        if (!occupied.Any(key => x < key.X + key.Width && x + width > key.X && y < key.Y + key.Height && y + height > key.Y))
                            return (x, y);
        return (double.NaN, double.NaN);
    }
    private PresentationKeyboardKey[] SelectedKeys() => draft.Keys.Where(key => selectedIds.Contains(key.Id)).ToArray();
    private void NudgeSelected((double X, double Y) delta)
    {
        var selected = SelectedKeys(); if (selected.Length == 0) return;
        var offset = ConstrainOffset(KeyBounds(selected), new(delta.X, delta.Y));
        if (offset == default) return;
        RecordUndo();
        foreach (var key in selected)
        { key.X += offset.X; key.Y += offset.Y; }
        RaiseOverlappingSelection(); useDefault = false; RefreshPreview(); RefreshCoordinates(); RefreshStatus();
    }
    private static PointD ConstrainOffset(RectD bounds, PointD offset) => new(
        Math.Clamp(offset.X, MinimumPosition - bounds.Left, PresentationKeyboardKey.MaximumPosition - bounds.Right),
        Math.Clamp(offset.Y, MinimumPosition - bounds.Top, PresentationKeyboardKey.MaximumPosition - bounds.Bottom));
    private void AlignSelected(KeyAlignment alignment)
    {
        var selected = SelectedKeys(); if (selected.Length < 2) return;
        double left = selected.Min(key => key.X), right = selected.Max(key => key.X + key.Width);
        double top = selected.Min(key => key.Y), bottom = selected.Max(key => key.Y + key.Height);
        RecordUndo();
        foreach (var key in selected)
        {
            key.X = alignment switch
            {
                KeyAlignment.Left => left,
                KeyAlignment.Center => (left + right - key.Width) / 2,
                KeyAlignment.Right => right - key.Width,
                _ => key.X
            };
            key.Y = alignment switch
            {
                KeyAlignment.Top => top,
                KeyAlignment.Middle => (top + bottom - key.Height) / 2,
                KeyAlignment.Bottom => bottom - key.Height,
                _ => key.Y
            };
            key.X = Math.Clamp(key.X, MinimumPosition, 64 - key.Width); key.Y = Math.Clamp(key.Y, MinimumPosition, 64 - key.Height);
        }
        useDefault = false; RefreshPreview(); RefreshCoordinates(); RefreshStatus();
    }
    private void SpaceSelectedHorizontal()
    {
        var selected = SelectedKeys().OrderBy(key => key.X).ToArray(); if (selected.Length < 2) return;
        double start = selected[0].X, end = selected[^1].X + selected[^1].Width;
        double gap = (end - start - selected.Sum(key => key.Width)) / (selected.Length - 1);
        RecordUndo(); double cursor = start;
        foreach (var key in selected)
        { key.X = Math.Clamp(cursor, MinimumPosition, 64 - key.Width); cursor += key.Width + gap; }
        useDefault = false; RefreshPreview(); RefreshCoordinates(); RefreshStatus();
    }
    private void SpaceSelectedVertical()
    {
        var selected = SelectedKeys().OrderBy(key => key.Y).ToArray(); if (selected.Length < 2) return;
        double start = selected[0].Y, end = selected[^1].Y + selected[^1].Height;
        double gap = (end - start - selected.Sum(key => key.Height)) / (selected.Length - 1);
        RecordUndo(); double cursor = start;
        foreach (var key in selected)
        { key.Y = Math.Clamp(cursor, MinimumPosition, 64 - key.Height); cursor += key.Height + gap; }
        useDefault = false; RefreshPreview(); RefreshCoordinates(); RefreshStatus();
    }
    private void ResetSelectedKey()
    {
        var selected = SelectedKeys(); if (selected.Length == 0) return;
        var defaults = PresentationKeyboardLayout.CreateDefault().Keys.ToDictionary(key => key.Id);
        RecordUndo();
        foreach (var key in selected)
        {
            var baseline = defaults.GetValueOrDefault(key.Id) ?? originalKeys[key.Id];
            draft.Keys[draft.Keys.FindIndex(item => item.Id == key.Id)] = baseline.Clone();
        }
        invalidFields.Clear();
        useDefault = false; RefreshSelection();
    }
    private double SnapGroupPosition(RectD moving, bool horizontal)
    {
        double value = horizontal ? moving.X : moving.Y, span = horizontal ? moving.Width : moving.Height;
        double best = value, distance = double.PositiveInfinity;
        if (snapGap.IsChecked == true)
        foreach (var other in draft.Keys.Where(key => key.Enabled && !selectedIds.Contains(key.Id)))
        {
            double otherCrossStart = horizontal ? other.Y : other.X;
            double otherCrossEnd = otherCrossStart + (horizontal ? other.Height : other.Width);
            double crossStart = horizontal ? moving.Top : moving.Left, crossEnd = horizontal ? moving.Bottom : moving.Right;
            if (otherCrossEnd < crossStart - GridStep || otherCrossStart > crossEnd + GridStep) continue;
            double start = horizontal ? other.X : other.Y, size = horizontal ? other.Width : other.Height;
            foreach (double candidate in new[] { start, start + size + GridStep, start - span - GridStep, start + size - span, start - span })
            {
                double current = Math.Abs(candidate - value);
                if (current < distance) { best = candidate; distance = current; }
            }
        }
        if (distance <= SnapDistance) return best;
        return snapGrid.IsChecked == true ? Math.Round(value / GridStep) * GridStep : value;
    }
    private NumericValueField Coordinate(string key, double minimum, double maximum, double railMinimum, double railMaximum)
    {
        var input = new TextBox(); AutomationProperties.SetName(input, L.Get("Presentation.Layout." + key));
        var field = new NumericValueField(input, minimum, maximum, railMinimum, railMaximum, .25) { Unit = L.Get("Presentation.Layout.Unit") };
        field.SetScrubAnchor(key switch { "X" => NumericPropertyKind.X, "Y" => NumericPropertyKind.Y, "Width" => NumericPropertyKind.Width, _ => NumericPropertyKind.Height }, L.Get("Presentation.Layout." + key));
        field.GestureStarted += () => { if (!refreshing) TrackNumericEdit(field); };
        field.GestureCompleted += _ => numericEditSessions.Remove(field);
        input.GotKeyboardFocus += (_, _) => numericEditSessions.Remove(field);
        field.ValueChanged += _ =>
        {
            if (refreshing) return;
            if (!field.IsInteracting) TrackNumericEdit(field);
            UpdateCoordinates(recordUndo: false);
        };
        field.InvalidInput += () => { if (!refreshing) { invalidFields.Add(key); RefreshStatus(); } };
        field.EnableStepper();
        coordinates.Add(key, field);
        return field;
    }
    private void UpdateCoordinates(bool recordUndo = true)
    {
        var selection = SelectedKeys();
        if (selection.Length == 0) return;
        var bounds = KeyBounds(selection);
        var values = new Dictionary<string, double>();
        foreach (var (key, field) in coordinates)
        {
            if (!field.TryGetValue(out double value)) { invalidFields.Add(key); RefreshStatus(); return; }
            values[key] = value;
        }
        double sx = values["Width"] / bounds.Width, sy = values["Height"] / bounds.Height;
        UpdateSelected(key =>
        {
            key.X = values["X"] + (key.X - bounds.X) * sx;
            key.Y = values["Y"] + (key.Y - bounds.Y) * sy;
            key.Width *= sx;
            key.Height *= sy;
        }, "Bounds", recordUndo);
        if (invalidFields.Contains("Bounds")) return;
        foreach (string key in coordinates.Keys) invalidFields.Remove(key); invalidFields.Remove("Bounds");
        if (values["X"] != bounds.X || values["Y"] != bounds.Y || sx != 1 || sy != 1)
            RaiseOverlappingSelection();
        RefreshCoordinateRanges(KeyBounds(SelectedKeys()));
        RefreshStatus();
    }
    internal void SelectKey(string id)
    {
        SelectKeys([id]);
    }
    internal void SelectKeys(IEnumerable<string> ids)
    {
        selectedIds.Clear(); selectedIds.UnionWith(ids.Where(id => draft.Keys.Any(key => key.Id == id)));
        selectedId = draft.Keys.FirstOrDefault(key => selectedIds.Contains(key.Id))?.Id;
        ApplyPreviewZOrder();
        invalidFields.Clear(); RefreshSelection();
    }
    private void RefreshSelection()
    {
        var selected = SelectedKeys(); refreshing = true; numericEditSessions.Clear();
        bool hasSelection = selected.Length != 0;
        enabled.IsEnabled = label.IsEnabled = secondaryLabel.IsEnabled = hasSelection;
        bool canMatchRelated = hasSelection && selected.All(key => !key.IsPlaceholder && key.SupportsRelatedMatch);
        matchingSection.Visibility = canMatchRelated ? Visibility.Visible : Visibility.Collapsed;
        matchRelatedKeys.IsEnabled = canMatchRelated;
        bool canIndicateLock = hasSelection && selected.All(key => key.SupportsLockIndicator);
        lockIndicatorSection.Visibility = canIndicateLock ? Visibility.Visible : Visibility.Collapsed;
        showLockIndicator.IsEnabled = canIndicateLock;
        foreach (var field in coordinates.Values) field.IsEnabled = hasSelection;
        try
        {
            enabled.IsChecked = SharedFlag(key => key.Enabled);
            label.Text = SharedText(key => key.Label);
            secondaryLabel.Text = SharedText(key => key.SecondaryLabel);
            matchRelatedKeys.IsChecked = canMatchRelated ? SharedFlag(key => key.MatchRelatedKeys) : false;
            showLockIndicator.IsChecked = canIndicateLock ? SharedFlag(key => key.ShowLockIndicator) : false;
            identity.Text = !hasSelection ? L.Get("Presentation.Layout.NoSelection") : selected.Length > 1 ? L.Format("Presentation.Layout.MultiSelection", selected.Length) :
                L.Format("Presentation.Layout.PhysicalKey", selected[0].IsPlaceholder ? L.Get("Presentation.Layout.CustomKey") :
                    PhysicalKeyNames.GetValueOrDefault(selected[0].Id) ?? PresentationDeviceButtons.Name(selected[0].DeviceType, selected[0].VirtualKey));
            if (hasSelection) SetDisplayedCoordinates(KeyBounds(selected));
        }
        finally { refreshing = false; }
        RefreshKeyStyleSelection();
        RefreshPreview(); RefreshStatus();

        bool? SharedFlag(Func<PresentationKeyboardKey, bool> read) =>
            hasSelection && selected.All(key => read(key) == read(selected[0])) ? read(selected[0]) : null;
        string SharedText(Func<PresentationKeyboardKey, string?> read) =>
            hasSelection && selected.All(key => read(key) == read(selected[0])) ? read(selected[0]) ?? "" : "";
    }
    private void ChangeKeyStyle(Func<PresentationKeyboardKeyStyle, PresentationKeyboardKeyStyle> change, string field = "KeyStyle", bool recordUndo = true) =>
        UpdateSelected(key => key.CustomStyle = change(key.CustomStyle ?? new()), field, recordUndo);

    private void ToggleKeyOverride(KeyOverrides flag)
    {
        if (refreshing) return;
        bool enable = SelectedKeys().Any(key => key.CustomStyle?.EffectiveOverrides.HasFlag(flag) != true);
        ChangeKeyStyle(style =>
        {
            var fields = style.EffectiveOverrides;
            var next = style with { Overrides = enable ? fields | flag : fields & ~flag };
            if (!enable) return next;
            return flag switch
            {
                KeyOverrides.Font => next with { FontFamily = style.FontFamily ?? keyboardAppearance.FontFamily },
                KeyOverrides.Foreground => next with { Foreground = style.Foreground ?? keyboardAppearance.Foreground },
                KeyOverrides.Background => next with { Background = style.Background ?? keyboardAppearance.Background },
                _ => next
            };
        });
        if (flag == KeyOverrides.Size) invalidFields.Remove("KeyRelativeSize");
        RefreshKeyStyleSelection();
        RefreshStatus();
    }

    private void WireKeyColor(ColorField field, Func<PresentationKeyboardKeyStyle, uint, PresentationKeyboardKeyStyle> change)
    {
        Snapshot? before = null;
        field.GestureStarted += () => before = CaptureSnapshot();
        field.ValueChanged += value => ChangeKeyStyle(style => change(style, value), recordUndo: before is null);
        field.GestureCompleted += commit =>
        {
            var snapshot = before;
            before = null;
            if (snapshot is null || SnapshotMatchesCurrent(snapshot)) return;
            if (commit) RecordUndo(snapshot); else RestoreSnapshot(snapshot);
            RefreshStatus();
        };
    }

    private void RefreshKeyStyleSelection()
    {
        var keys = SelectedKeys();
        bool previous = refreshing;
        refreshing = true;
        try
        {
            foreach (var (flag, toggle) in styleOverrides)
            {
                int count = keys.Count(key => key.CustomStyle?.EffectiveOverrides.HasFlag(flag) == true);
                toggle.IsEnabled = keys.Length > 0;
                toggle.IsChecked = count == 0 ? false : count == keys.Length ? true : null;
            }
            relativeFontSize.IsEnabled = styleOverrides[KeyOverrides.Size].IsChecked == true;
            keyFont.IsEnabled = styleOverrides[KeyOverrides.Font].IsChecked == true;
            keyForeground.IsEnabled = styleOverrides[KeyOverrides.Foreground].IsChecked == true;
            keyBackground.IsEnabled = styleOverrides[KeyOverrides.Background].IsChecked == true;
            var styles = keys.Select(key => key.ActiveStyle).ToArray();
            var sizes = styles.Select(style => style?.ScaleFont(100) ?? 100).Distinct().ToArray();
            relativeFontSize.Value = sizes.FirstOrDefault(100);
            if (sizes.Length > 1) relativeFontSize.ShowPlaceholder("");
            var fonts = styles.Select(style => style?.FontFamily ?? keyboardAppearance.FontFamily).Distinct().ToArray();
            if (fonts.Length == 1) keyFont.SelectFont(fonts[0]); else keyFont.ClearFontSelection();
            DisplayColor(keyForeground, styles.Select(style => style?.Foreground ?? keyboardAppearance.Foreground).DefaultIfEmpty(keyboardAppearance.Foreground));
            DisplayColor(keyBackground, styles.Select(style => style?.Background ?? keyboardAppearance.Background).DefaultIfEmpty(keyboardAppearance.Background));
        }
        finally { refreshing = previous; }

        static void DisplayColor(ColorField field, IEnumerable<uint> values)
        {
            var colors = values.Distinct().ToArray();
            field.Value = colors[0]; field.IsMixed = colors.Length > 1;
        }
    }
    internal void ToggleSelectedEnabled()
    {
        var selected = draft.Keys.Where(key => selectedIds.Contains(key.Id)).ToArray();
        if (selected.Length == 0) return;
        bool value = !selected.All(key => key.Enabled);
        RecordUndo();
        foreach (var key in selected) key.Enabled = value;
        useDefault = false; enabled.IsChecked = value; RefreshPreview(); RefreshStatus();
    }
    private void UpdateSelected(Action<PresentationKeyboardKey> change, string field = "", bool recordUndo = true)
    {
        if (refreshing || selectedIds.Count == 0) return;
        var replacements = SelectedKeys().Select(current => { var next = current.Clone(); change(next); return next; }).ToDictionary(key => key.Id);
        if (replacements.Values.Any(key => !key.IsValid())) { invalidFields.Add(field); RefreshStatus(); return; }
        if (recordUndo) RecordUndo();
        for (int i = 0; i < draft.Keys.Count; i++)
            if (replacements.TryGetValue(draft.Keys[i].Id, out var next)) draft.Keys[i] = next;
        useDefault = false; invalidFields.Remove(field);
        RefreshPreview(); RefreshStatus();
    }
    private void RefreshStatus()
    {
        undoButton?.SetCurrentValue(IsEnabledProperty, CanUndo);
        redoButton?.SetCurrentValue(IsEnabledProperty, CanRedo);
        int selectedCount = SelectedKeys().Length;
        copyKeysButton?.SetCurrentValue(IsEnabledProperty, selectedCount > 0);
        pasteKeysButton?.SetCurrentValue(IsEnabledProperty, clipboard.Count > 0 && draft.Keys.Count < PresentationKeyboardLayout.MaximumKeys);
        deleteKeysButton?.SetCurrentValue(IsEnabledProperty, selectedCount > 0);
        resetKeysButton?.SetCurrentValue(IsEnabledProperty, selectedCount > 0);
        foreach (var action in multiKeyActions) action.IsEnabled = selectedCount >= 2;
        bool hasEnabledKeys = draft.Keys.Any(key => key.Enabled);
        bool withinBudget = IsWithinLayoutBudget();
        if (capturingKey) SetStatus(Localized("Presentation.Layout.CaptureHint", "Press a keyboard, mouse, or controller button"), error: false);
        else if (!hasEnabledKeys) SetStatus(L.Get("Presentation.Layout.NoEnabledKeys"), error: true);
        else if (invalidFields.Count > 0) SetStatus(L.Get("Presentation.Layout.Invalid"), error: true);
        else if (!withinBudget) SetStatus(L.Get("Presentation.Layout.BudgetExceeded"), error: true);
        else if (Signature() != baselineSignature) SetStatus(Localized("Presentation.Layout.Unsaved", "Unsaved changes"), error: false);
        else SetStatus("", error: false);
        apply.IsEnabled = hasEnabledKeys && withinBudget && invalidFields.Count == 0 && draft.IsValid();
    }
    private bool IsWithinLayoutBudget() => draft.Keys.Where(key => key.Enabled).Sum(key =>
        keyViews.TryGetValue(key.Id, out var view) && view.DataContext is KeyLegend legend
            ? legend.PrimitiveCost : PresentationKeyboardLayout.MinimumPrimitiveCost(key)) <= PresentationLimits.MaximumPrimitives;
    private void SetStatus(string text, bool error)
    {
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, error ? "CanvasErrorText" : "CanvasMuted");
    }
    private void BuildPreview()
    {
        preview.Children.Clear(); keyViews.Clear();
        keyboardOutline.SetResourceReference(Shape.StrokeProperty, "CanvasBorder");
        Panel.SetZIndex(keyboardOutline, -1);
        preview.Children.Add(keyboardOutline);
        var ids = draft.Keys.Select(key => key.Id).ToHashSet();
        previewZOrder.RemoveAll(id => !ids.Contains(id));
        previewZOrder.AddRange(draft.Keys.Select(key => key.Id).Except(previewZOrder).ToArray());
        keyTemplate ??= (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:ui="clr-namespace:FloatCanvas.App;assembly=FloatCanvas" TargetType="Thumb">
              <Grid Background="Transparent">
                <ui:RoundedSurfaceBorder Background="{TemplateBinding Background}" CornerRadius="{Binding Radius}"/>
                <ui:RoundedSurfaceBorder BorderBrush="{TemplateBinding Foreground}" BorderThickness="{Binding Stroke}" CornerRadius="{Binding Radius}" IsHitTestVisible="False" Opacity=".65"/>
                <ui:RoundedSurfaceBorder Margin="{Binding Inset}" BorderBrush="{TemplateBinding Foreground}" BorderThickness="{Binding Stroke}" CornerRadius="{Binding InnerRadius}" IsHitTestVisible="False" Opacity=".65"/>
                <Viewbox Stretch="Uniform" StretchDirection="DownOnly" Margin="{Binding Inset}" IsHitTestVisible="False">
                  <StackPanel VerticalAlignment="Center">
                    <TextBlock Name="KeyCaption" Text="{Binding Primary}" FontFamily="{TemplateBinding FontFamily}" Foreground="{TemplateBinding Foreground}" TextAlignment="Center" TextWrapping="NoWrap" FontSize="{Binding PrimarySize}"/>
                    <TextBlock Name="KeySecondary" Text="{Binding Secondary}" FontFamily="{TemplateBinding FontFamily}" Foreground="{TemplateBinding Foreground}" TextAlignment="Center" TextWrapping="NoWrap" FontSize="{Binding SecondarySize}" Margin="{Binding SecondaryMargin}"/>
                  </StackPanel>
                </Viewbox>
                <ui:RoundedSurfaceBorder x:Name="Chrome" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{Binding Radius}" IsHitTestVisible="False"/>
              </Grid>
              <ControlTemplate.Triggers>
                <DataTrigger Binding="{Binding Secondary}" Value=""><Setter TargetName="KeySecondary" Property="Visibility" Value="Collapsed"/></DataTrigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        foreach (var key in draft.Keys)
        {
            originalKeys.TryAdd(key.Id, key.Clone());
            string id = key.Id; var view = new Thumb { Template = keyTemplate, Cursor = Cursors.SizeAll, Focusable = true, FocusVisualStyle = null };
            AutomationProperties.SetName(view, LegendName(key));
            view.PreviewMouseDoubleClick += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                SelectKey(id); ToggleSelectedEnabled(); view.Focus(); e.Handled = true;
            };
            view.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; return; }
                ActivatePreviewKey(id);
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
                {
                    var next = selectedIds.ToHashSet(); if (!next.Add(id)) next.Remove(id);
                    // Focus first: keyboard focus must not undo a Ctrl-click deselection.
                    view.Focus(); SelectKeys(next); ActivatePreviewKey(id); e.Handled = true;
                }
                else if (!selectedIds.Contains(id)) SelectKey(id);
            };
            view.GotKeyboardFocus += (_, _) =>
            {
                ActivatePreviewKey(id);
                if (!selectedIds.Contains(id)) SelectKey(id);
            };
            view.DragStarted += (_, _) =>
            {
                ActivatePreviewKey(id);
                if (!selectedIds.Contains(id)) SelectKey(id);
                dragPreviewBounds = PreviewBounds();
                dragPointerStart = Mouse.GetPosition(previewViewport);
                movingStart = draft.Keys.Where(item => selectedIds.Contains(item.Id)).Select(item => item.Clone()).ToArray();
                dragKeyBounds = KeyBounds(movingStart);
                dragAppliedOffset = default;
                movingWasDefault = useDefault; movingX = movingY = 0; dragSnapshot = CaptureSnapshot();
            };
            view.DragDelta += (_, delta) =>
            {
                if (movingStart is null) return;
                if (view.IsMouseCaptured)
                {
                    Vector displacement = Mouse.GetPosition(previewViewport) - dragPointerStart;
                    movingX = displacement.X / (previewScale * UnitPixels);
                    movingY = displacement.Y / (previewScale * UnitPixels);
                }
                else
                {
                    movingX += delta.HorizontalChange / UnitPixels;
                    movingY += delta.VerticalChange / UnitPixels;
                }
                var (left, top, width, height) = dragKeyBounds;
                var moving = new RectD(left + movingX, top + movingY, width, height);
                double targetX = SnapGroupPosition(moving, horizontal: true);
                double targetY = SnapGroupPosition(moving, horizontal: false);
                var offset = ConstrainOffset(dragKeyBounds, new(targetX - left, targetY - top));
                if (offset == dragAppliedOffset) return;
                dragAppliedOffset = offset;
                foreach (var original in movingStart)
                {
                    var item = draft.Keys.Single(item => item.Id == original.Id); item.X = original.X + offset.X; item.Y = original.Y + offset.Y;
                }
                useDefault = movingWasDefault && offset == default;
                RefreshPreview(); RefreshStatus();
                RefreshCoordinates();
            };
            view.DragCompleted += (_, delta) =>
            {
                if (delta.Canceled && movingStart is { } previous)
                { foreach (var item in previous) draft.Keys[draft.Keys.FindIndex(key => key.Id == item.Id)] = item; useDefault = movingWasDefault; RefreshCoordinates(); }
                else if (dragSnapshot is { } snapshot && !SnapshotMatchesCurrent(snapshot))
                {
                    if (dragAppliedOffset != default) RaiseOverlappingSelection();
                    RecordUndo(snapshot);
                }
                movingStart = null; dragSnapshot = null;
                RefreshPreview(); RefreshStatus();
            };
            keyViews.Add(id, view); preview.Children.Add(view);
        }
        ApplyPreviewZOrder();
        RefreshPreview(); RefreshStatus();
    }
    private void RaiseOverlappingSelection()
    {
        var selected = SelectedKeys().OrderBy(key => previewZOrder.IndexOf(key.Id)).ToArray();
        if (!selected.Any(key => key.Enabled && OverlapsUnselected(key))) return;
        draft.Keys.RemoveAll(key => selectedIds.Contains(key.Id));
        draft.Keys.AddRange(selected);
        ApplyPreviewZOrder();
    }
    private bool OverlapsUnselected(PresentationKeyboardKey key) => draft.Keys.Any(other =>
        other.Enabled && !selectedIds.Contains(other.Id) &&
        key.X < other.X + other.Width && key.X + key.Width > other.X &&
        key.Y < other.Y + other.Height && key.Y + key.Height > other.Y);
    private bool SnapshotMatchesCurrent(Snapshot snapshot) =>
        snapshot.UseDefault == useDefault &&
        snapshot.SelectedIds.SequenceEqual(selectedIds.OrderBy(id => id, StringComparer.Ordinal)) &&
        JsonSerializer.Serialize(snapshot.Layout) == JsonSerializer.Serialize(draft);
    private void ActivatePreviewKey(string id)
    {
        if (!keyViews.ContainsKey(id) || previewZOrder.LastOrDefault() == id) return;
        previewZOrder.Remove(id);
        previewZOrder.Add(id);
        ApplyPreviewZOrder();
    }
    private void ApplyPreviewZOrder()
    {
        int index = 0;
        foreach (string id in draft.Keys.Select(key => key.Id).Where(id => !selectedIds.Contains(id))
                     .Concat(previewZOrder.Where(selectedIds.Contains)))
            if (keyViews.TryGetValue(id, out var view)) Panel.SetZIndex(view, index++);
    }
    private void RefreshCoordinates()
    {
        var selected = SelectedKeys();
        if (selected.Length == 0) return;
        refreshing = true;
        try { SetDisplayedCoordinates(KeyBounds(selected)); }
        finally { refreshing = false; }
    }
    private void SetDisplayedCoordinates(RectD bounds)
    {
        RefreshCoordinateRanges(bounds);
        coordinates["X"].Value = bounds.X; coordinates["Y"].Value = bounds.Y;
        coordinates["Width"].Value = bounds.Width; coordinates["Height"].Value = bounds.Height;
    }
    private void RefreshCoordinateRanges(RectD bounds)
    {
        var selection = SelectedKeys();
        if (selection.Length == 0) return;
        double right = PresentationKeyboardKey.MaximumPosition, minimum = PresentationKeyboardKey.MinimumSize;
        double maximum = PresentationKeyboardKey.MaximumSize;
        coordinates["X"].SetInputRange(MinimumPosition, Math.Max(bounds.X, right - bounds.Width));
        coordinates["Y"].SetInputRange(MinimumPosition, Math.Max(bounds.Y, right - bounds.Height));
        coordinates["Width"].SetInputRange(
            Math.Min(bounds.Width, bounds.Width * minimum / selection.Min(key => key.Width)),
            Math.Max(bounds.Width, Math.Min(right - bounds.X, bounds.Width * maximum / selection.Max(key => key.Width))));
        coordinates["Height"].SetInputRange(
            Math.Min(bounds.Height, bounds.Height * minimum / selection.Min(key => key.Height)),
            Math.Max(bounds.Height, Math.Min(right - bounds.Y, bounds.Height * maximum / selection.Max(key => key.Height))));
    }
    private void RefreshPreview()
    {
        var (contentWidth, contentHeight) = PreviewContentSize();
        preview.Width = contentWidth;
        preview.Height = contentHeight;
        var bounds = PreviewBounds();
        double originX = Inset - bounds.X * UnitPixels, originY = TopInset - bounds.Y * UnitPixels;
        keyboardOutline.Width = DefaultBounds.Width * UnitPixels + 8;
        keyboardOutline.Height = DefaultBounds.Height * UnitPixels + 8;
        Canvas.SetLeft(keyboardOutline, originX + DefaultBounds.X * UnitPixels - 4);
        Canvas.SetTop(keyboardOutline, originY + DefaultBounds.Y * UnitPixels - 4);
        foreach (var key in draft.Keys)
        {
            var view = keyViews[key.Id];
            var custom = key.ActiveStyle;
            view.Width = (key.Width - PresentationLayout.KeyboardGap) * UnitPixels;
            view.Height = (key.Height - PresentationLayout.KeyboardGap) * UnitPixels;
            var face = new RectD(0, 0, view.Width, view.Height);
            var (radius, inset, stroke, _) = PresentationLayout.KeyEdges(face, UnitPixels);
            string family = custom?.FontFamily ?? keyboardAppearance.FontFamily;
            var state = (key.Label, key.SecondaryLabel, key.Width, key.Height, family, custom?.RelativeSize);
            if (!Equals(view.Tag, state))
            {
                var (font, primary, secondary) = legendRenderer.FitKeyboardLegend(key, keyboardAppearance, face, UnitPixels * PresentationLayout.KeyboardLabelRatio);
                int cost = PresentationLayout.KeyPrimitiveCost(key, primary, secondary);
                view.DataContext = new KeyLegend(key.Label, key.SecondaryLabel ?? "", font, font * PresentationLayout.KeySecondaryRatio,
                    new(0, font * .12, 0, 0), new(radius), new(radius * .7), new(inset), new(stroke), cost);
                view.Tag = state;
            }
            AutomationProperties.SetName(view, LegendName(key));
            AutomationProperties.SetItemStatus(view, KeyAutomationStatus(key));
            view.Opacity = key.Enabled ? 1 : .35;
            if (view.FontFamily.Source != family) view.FontFamily = new FontFamily(family);
            SetBrush(view, Control.BackgroundProperty, custom?.Background ?? keyboardAppearance.Background);
            SetBrush(view, Control.ForegroundProperty, custom?.Foreground ?? keyboardAppearance.Foreground);
            view.SetResourceReference(Control.BorderBrushProperty, selectedIds.Contains(key.Id) ? "CanvasAccent" : "CanvasBorder");
            view.BorderThickness = selectedIds.Contains(key.Id) ? new(2) : new(0);
            double gap = PresentationLayout.KeyboardGap * UnitPixels / 2;
            Canvas.SetLeft(view, originX + key.X * UnitPixels + gap); Canvas.SetTop(view, originY + key.Y * UnitPixels + gap);
        }
        zoomStatus.Text = Math.Round(previewScale * 100) + "%";

        static void SetBrush(Thumb view, DependencyProperty property, uint argb)
        {
            var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            if (view.ReadLocalValue(property) is SolidColorBrush brush && brush.Color == color) return;
            var replacement = new SolidColorBrush(color); replacement.Freeze();
            view.SetValue(property, replacement);
        }
    }
    private static string LegendName(PresentationKeyboardKey key) =>
        string.IsNullOrWhiteSpace(key.SecondaryLabel) ? key.Label : key.Label + " / " + key.SecondaryLabel;
    private string KeyAutomationStatus(PresentationKeyboardKey key)
    {
        var states = new List<string>();
        if (selectedIds.Contains(key.Id)) states.Add(Localized("Presentation.Layout.KeySelected", "Selected"));
        states.Add(Localized(
            key.Enabled ? "Presentation.Layout.KeyEnabled" : "Presentation.Layout.KeyDisabled",
            key.Enabled ? "Enabled" : "Disabled"));
        return string.Join(", ", states);
    }
    private void TrackNumericEdit(NumericValueField field)
    {
        if (numericEditSessions.Add(field)) RecordUndo();
    }
    private void SetAllEnabled(bool value)
    {
        if (draft.Keys.All(key => key.Enabled == value)) return;
        RecordUndo(); foreach (var key in draft.Keys) key.Enabled = value; useDefault = false; RefreshSelection();
    }
    internal void ResetToDefault()
    {
        RecordUndo();
        draft = PresentationKeyboardLayout.CreateDefault(); useDefault = true; invalidFields.Clear();
        previewZOrder.Clear();
        BuildPreview(); SelectKey(draft.Keys[0].Id);
    }
    internal void Accept()
    {
        if (invalidFields.Count > 0 || !draft.IsValid() || !IsWithinLayoutBudget())
        { RefreshStatus(); return; }
        ResultLayout = useDefault ? null : draft.Clone(); Accepted = true; if (IsVisible) SessionWindowCoordinator.SetModalResult(this, true);
    }
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
