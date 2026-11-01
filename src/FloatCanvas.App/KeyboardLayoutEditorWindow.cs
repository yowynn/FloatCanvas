using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Edits an owned physical-key layout draft. Apply is the only publication point.</summary>
internal sealed class KeyboardLayoutEditorWindow : Window
{
    private const double UnitPixels = 30, KeyGap = 2, Inset = 12;
    private static readonly IReadOnlyDictionary<string, string> PhysicalKeyNames = PresentationKeyboardLayout.CreateDefault().Keys.ToDictionary(key => key.Id, key => key.Label);
    private readonly Canvas preview = new();
    private readonly Dictionary<string, Thumb> keyViews = [];
    private readonly Dictionary<string, NumericValueField> coordinates = [];
    private readonly TextBox label = new();
    private readonly CheckBox enabled = new();
    private readonly TextBlock identity = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly TextBlock status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly Button apply = new() { MinWidth = 96, MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(10, 4, 10, 4), IsDefault = true };
    private readonly HashSet<string> invalidFields = [];
    private PresentationKeyboardLayout draft;
    private bool useDefault, refreshing;
    private string? selectedId;
    private PresentationKeyboardKey? movingStart;
    private bool movingWasDefault;
    private double movingX, movingY;
    private static ControlTemplate? keyTemplate;
    internal PresentationKeyboardLayout? ResultLayout { get; private set; }
    internal bool Accepted { get; private set; }
    internal PresentationKeyboardLayout Draft => draft.Clone();
    internal string? SelectedKeyId => selectedId;

    internal KeyboardLayoutEditorWindow(PresentationKeyboardLayout? source, Window? owner = null)
    {
        Owner = owner;
        draft = (source ?? PresentationKeyboardLayout.CreateDefault()).Clone(); useDefault = source is null;
        double standardPreviewWidth = PresentationKeyboardLayout.CreateDefault().Keys.Max(key => key.X + key.Width) * UnitPixels + Inset * 2;
        Title = L.Get("Presentation.Layout.Title");
        // Keep the complete standard keyboard beside the shared property column; smaller work areas retain scrolling.
        Width = Math.Max(1240, standardPreviewWidth + PropertyPanelLayout.ContentWidth + WindowLayout.ContentInset * 2 + 16 + 10 + 2 + WindowLayout.ScrollBarWidth);
        Height = 640; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Topmost = true; Icon = AppIcon.WindowImage;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin() };
        var hint = new TextBlock { Text = L.Get("Presentation.Layout.Hint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 12) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); DockPanel.SetDock(hint, Dock.Top); body.Children.Add(hint);
        var footer = new DockPanel { Margin = new(0, 12, 0, 0), LastChildFill = false };
        var reset = Button("Presentation.Layout.Reset", ResetToDefault); DockPanel.SetDock(reset, Dock.Left); footer.Children.Add(reset);
        var cancel = Button("Presentation.Layout.Cancel", () => { if (IsVisible) SessionWindowCoordinator.SetModalResult(this, false); }); cancel.IsCancel = true; cancel.MinWidth = 96;
        apply.Content = L.Get("Presentation.Layout.Apply"); apply.Click += (_, _) => Accept();
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(cancel); actions.Children.Add(apply); apply.Margin = new(8, 0, 0, 0);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new(PropertyPanelLayout.ContentWidth) });
        var viewport = new ScrollViewer { Content = preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var frame = new Border { Child = viewport, BorderThickness = new(1), CornerRadius = new(6), Padding = new(4), Margin = new(0, 0, 16, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "CanvasInput"); frame.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder"); columns.Children.Add(frame);
        var properties = new StackPanel(); PropertyPanelLayout.Scope(properties);
        var title = PropertyPanelLayout.SectionTitle(L.Get("Presentation.Layout.Selected")); title.Margin = new(0, 0, 0, 8); properties.Children.Add(title);
        identity.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); properties.Children.Add(identity);
        properties.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Presentation.Layout.Enabled"), enabled));
        enabled.Click += (_, _) => UpdateSelected(key => key.Enabled = enabled.IsChecked == true);
        AutomationProperties.SetName(enabled, L.Get("Presentation.Layout.Enabled"));
        label.MaxLength = 64; label.Height = PropertyPanelLayout.ControlHeight; AutomationProperties.SetName(label, L.Get("Presentation.Layout.KeyLabel"));
        label.TextChanged += (_, _) => { if (!refreshing) UpdateSelected(key => key.Label = label.Text, "Label"); };
        properties.Children.Add(PropertyPanelLayout.Row(new TextBlock { Text = L.Get("Presentation.Layout.KeyLabel") }, label));
        Coordinate(properties, "X", 0, 63.75, 0, 24); Coordinate(properties, "Y", 0, 63.75, 0, 8);
        Coordinate(properties, "Width", .25, 16, .25, 4); Coordinate(properties, "Height", .25, 16, .25, 4);
        var all = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 0) };
        all.Children.Add(Button("Presentation.Layout.EnableAll", () => SetAllEnabled(true)));
        var none = Button("Presentation.Layout.DisableAll", () => SetAllEnabled(false)); none.Margin = new(6, 0, 0, 0); all.Children.Add(none); properties.Children.Add(all);
        var units = new TextBlock { Text = L.Get("Presentation.Layout.UnitsHint"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 10, 0, 0) };
        units.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted"); properties.Children.Add(units);
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); properties.Children.Add(status);
        Grid.SetColumn(properties, 1); columns.Children.Add(new ScrollViewer { Content = properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Grid.SetColumn(columns.Children[^1], 1);
        body.Children.Add(columns); Content = body; AppWindowChrome.ApplyDialog(this);
        BuildPreview();
        if (draft.Keys.Count > 0) SelectKey(draft.Keys[0].Id);
        else { enabled.IsEnabled = label.IsEnabled = false; foreach (var field in coordinates.Values) field.IsEnabled = false; identity.Text = L.Get("Presentation.Layout.NoSelection"); }
    }

    private static Button Button(string key, Action action)
    {
        var button = new Button { Content = L.Get(key), MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(10, 4, 10, 4) };
        AutomationProperties.SetName(button, L.Get(key)); button.Click += (_, _) => action(); return button;
    }
    private void Coordinate(Panel body, string key, double minimum, double maximum, double railMinimum, double railMaximum)
    {
        var input = new TextBox(); AutomationProperties.SetName(input, L.Get("Presentation.Layout." + key));
        var field = new NumericValueField(input, minimum, maximum, railMinimum, railMaximum, .25) { Unit = L.Get("Presentation.Layout.Unit") };
        field.ValueChanged += _ => { if (!refreshing) UpdateCoordinates(); };
        field.InvalidInput += () => { if (!refreshing) { invalidFields.Add(key); RefreshStatus(); } };
        coordinates.Add(key, field); body.Children.Add(PropertyPanelLayout.Row(new TextBlock { Text = L.Get("Presentation.Layout." + key) }, field));
    }
    private static void SetCoordinate(PresentationKeyboardKey key, string field, double value)
    {
        switch (field) { case "X": key.X = value; break; case "Y": key.Y = value; break; case "Width": key.Width = value; break; case "Height": key.Height = value; break; }
    }
    private PresentationKeyboardKey? Selected => draft.Keys.FirstOrDefault(key => key.Id == selectedId);
    private void UpdateCoordinates()
    {
        if (Selected is not { } current) return;
        var next = current.Clone();
        foreach (var (key, field) in coordinates)
        {
            if (!field.TryGetValue(out double value)) { invalidFields.Add(key); RefreshStatus(); return; }
            SetCoordinate(next, key, value);
        }
        if (!next.IsValid()) { invalidFields.Add("Bounds"); RefreshStatus(); return; }
        draft.Keys[draft.Keys.FindIndex(key => key.Id == selectedId)] = next; useDefault = false;
        foreach (string key in coordinates.Keys) invalidFields.Remove(key); invalidFields.Remove("Bounds");
        RefreshPreview(); RefreshStatus();
    }
    internal void SelectKey(string id)
    {
        var selected = draft.Keys.FirstOrDefault(key => key.Id == id); if (selected is null) return;
        selectedId = id; invalidFields.Clear(); refreshing = true;
        enabled.IsEnabled = label.IsEnabled = true; foreach (var field in coordinates.Values) field.IsEnabled = true;
        try
        {
            enabled.IsChecked = selected.Enabled; label.Text = selected.Label;
            identity.Text = L.Format("Presentation.Layout.PhysicalKey", PhysicalKeyNames.GetValueOrDefault(selected.Id, L.Get("Presentation.Layout.CustomKey")));
            coordinates["X"].Value = selected.X; coordinates["Y"].Value = selected.Y; coordinates["Width"].Value = selected.Width; coordinates["Height"].Value = selected.Height;
        }
        finally { refreshing = false; }
        RefreshPreview(); RefreshStatus();
    }
    private void UpdateSelected(Action<PresentationKeyboardKey> change, string field = "")
    {
        if (refreshing || Selected is not { } current) return;
        var next = current.Clone(); change(next);
        if (!next.IsValid()) { invalidFields.Add(field); RefreshStatus(); return; }
        draft.Keys[draft.Keys.FindIndex(key => key.Id == selectedId)] = next; useDefault = false; invalidFields.Remove(field);
        RefreshPreview(); RefreshStatus();
    }
    private void RefreshStatus()
    { status.Text = invalidFields.Count == 0 ? "" : L.Get("Presentation.Layout.Invalid"); apply.IsEnabled = invalidFields.Count == 0 && draft.IsValid(); }
    private void BuildPreview()
    {
        preview.Children.Clear(); keyViews.Clear();
        keyTemplate ??= (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Thumb'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='4' Padding='2'><Viewbox Stretch='Uniform' StretchDirection='DownOnly'><TextBlock Name='KeyCaption' Text='{TemplateBinding Tag}' Foreground='{TemplateBinding Foreground}' TextAlignment='Center' TextWrapping='NoWrap' FontSize='10' VerticalAlignment='Center'/></Viewbox></Border></ControlTemplate>");
        foreach (var key in draft.Keys)
        {
            string id = key.Id; var view = new Thumb { Template = keyTemplate, Cursor = Cursors.SizeAll, Focusable = true };
            AutomationProperties.SetName(view, key.Label); view.PreviewMouseLeftButtonDown += (_, _) => SelectKey(id);
            view.GotKeyboardFocus += (_, _) => SelectKey(id);
            view.DragStarted += (_, _) => { SelectKey(id); movingStart = Selected!.Clone(); movingWasDefault = useDefault; movingX = movingStart.X; movingY = movingStart.Y; };
            view.DragDelta += (_, delta) =>
            {
                if (movingStart is null) return;
                movingX += delta.HorizontalChange / UnitPixels; movingY += delta.VerticalChange / UnitPixels;
                UpdateSelected(item => { item.X = Math.Clamp(Math.Round(movingX * 4) / 4, 0, 64 - item.Width); item.Y = Math.Clamp(Math.Round(movingY * 4) / 4, 0, 64 - item.Height); });
                RefreshCoordinates();
            };
            view.DragCompleted += (_, delta) =>
            {
                if (delta.Canceled && movingStart is { } previous) { draft.Keys[draft.Keys.FindIndex(item => item.Id == id)] = previous; useDefault = movingWasDefault; RefreshPreview(); RefreshCoordinates(); }
                movingStart = null;
            };
            keyViews.Add(id, view); preview.Children.Add(view);
        }
        RefreshPreview();
    }
    private void RefreshCoordinates()
    {
        if (Selected is not { } key) return; refreshing = true;
        try { coordinates["X"].Value = key.X; coordinates["Y"].Value = key.Y; coordinates["Width"].Value = key.Width; coordinates["Height"].Value = key.Height; }
        finally { refreshing = false; }
    }
    private void RefreshPreview()
    {
        preview.Width = Math.Max(400, draft.Keys.Select(key => key.X + key.Width).DefaultIfEmpty(0).Max() * UnitPixels + Inset * 2);
        preview.Height = Math.Max(220, draft.Keys.Select(key => key.Y + key.Height).DefaultIfEmpty(0).Max() * UnitPixels + Inset * 2);
        foreach (var key in draft.Keys)
        {
            var view = keyViews[key.Id]; view.Tag = key.Label; view.Width = key.Width * UnitPixels - KeyGap; view.Height = key.Height * UnitPixels - KeyGap;
            AutomationProperties.SetName(view, key.Label);
            view.Opacity = key.Enabled ? 1 : .35; view.SetResourceReference(Control.BackgroundProperty, key.Id == selectedId ? "CanvasHover" : "CanvasSurface");
            view.SetResourceReference(Control.ForegroundProperty, "CanvasText"); view.SetResourceReference(Control.BorderBrushProperty, key.Id == selectedId ? "CanvasAccent" : "CanvasBorder");
            Canvas.SetLeft(view, Inset + key.X * UnitPixels); Canvas.SetTop(view, Inset + key.Y * UnitPixels);
        }
    }
    private void SetAllEnabled(bool value)
    { foreach (var key in draft.Keys) key.Enabled = value; useDefault = false; RefreshPreview(); if (selectedId is { } id) SelectKey(id); }
    internal void ResetToDefault()
    { draft = PresentationKeyboardLayout.CreateDefault(); useDefault = true; invalidFields.Clear(); BuildPreview(); SelectKey(draft.Keys[0].Id); }
    internal void Accept()
    {
        if (invalidFields.Count > 0 || !draft.IsValid()) { RefreshStatus(); return; }
        ResultLayout = useDefault ? null : draft.Clone(); Accepted = true; if (IsVisible) SessionWindowCoordinator.SetModalResult(this, true);
    }
}
