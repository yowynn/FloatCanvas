using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class FontPickerControl : OverlayComboBox
{
    internal sealed record FontChoice(string Name, string DisplayName, FontFamily Family, bool Installed);
    private enum PickerMode { Display, Search, Browse }
    private readonly record struct EditorState(string Text, int Start, int Length);
    private static readonly MouseButtonEventHandler candidateMouseUp = CandidateMouseUp;

    private readonly List<FontChoice> catalog = [];
    private readonly ICollectionView choices;
    private readonly DispatcherTimer search = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private string query = "";
    private string? committed;
    private FontChoice? displayed, candidate;
    private TextBox? editor;
    private PickerMode mode;
    private int updating, inputVersion;
    private bool openingSearch, viewNeedsRefresh;
    internal bool IsSelectionRefresh { get; private set; }
    internal event Action<string>? FontCommitted;

    internal FontPickerControl()
    {
        MinHeight = 32; MaxDropDownHeight = 300; IsEditable = true; IsReadOnly = true;
        SetResourceReference(StyleProperty, typeof(ComboBox));
        IsTextSearchEnabled = false; IsSynchronizedWithCurrentItem = false; StaysOpenOnEdit = true;
        TextSearch.SetTextPath(this, nameof(FontChoice.DisplayName));
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var preview = new FrameworkElementFactory(typeof(TextBlock));
        preview.SetBinding(TextBlock.TextProperty, new Binding(nameof(FontChoice.DisplayName)));
        preview.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(FontChoice.Family)));
        preview.SetValue(TextBlock.FontSizeProperty, 14d);
        preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        ItemTemplate = new DataTemplate { VisualTree = preview };
        foreach (var font in Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase))
            catalog.Add(CreateChoice(font.Source, font));
        choices = CollectionViewSource.GetDefaultView(catalog);
        choices.Filter = value => value is FontChoice item &&
            (query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        ItemsSource = choices;
        // ComboBox handles the first editor press while forwarding focus. Observe
        // that press too, so entry from an empty focus still starts editing.
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PickerMouseDown), handledEventsToo: true);
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(EditorEntered), handledEventsToo: true);
        AddHandler(TextBoxBase.SelectionChangedEvent, new RoutedEventHandler((_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, editor)) ClearDisplaySelection();
        }));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
        {
            if (updating != 0 || IsReadOnly || e.OriginalSource is not TextBox box ||
                !ReferenceEquals(box.TemplatedParent, this) || !IsVisible || !box.IsKeyboardFocusWithin) return;
            mode = PickerMode.Search;
            search.Stop();
            if (IsLoaded) search.Start();
        }));
        search.Tick += (_, _) =>
        {
            search.Stop();
            if (mode != PickerMode.Search || !IsLoaded || !IsVisible || editor?.IsKeyboardFocusWithin != true) return;
            ApplySearch(editor.Text);
            OpenSearch();
        };
        Unloaded += (_, _) => RestoreDisplay(closePopup: true, refreshView: false, returnFocus: false);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) RestoreDisplay(closePopup: true, refreshView: false, returnFocus: false);
            else if (viewNeedsRefresh) RestoreDisplay(closePopup: false);
        };
    }

    public override void OnApplyTemplate()
    {
        updating++;
        try { base.OnApplyTemplate(); editor = Template.FindName("PART_EditableTextBox", this) as TextBox; }
        finally { updating--; }
    }

    // Explicit view helpers do not open a popup or publish a model change.
    internal void ApplySearch(string text)
    {
        var saved = CaptureEditor(text);
        updating++;
        try
        {
            search.Stop(); mode = PickerMode.Search; IsReadOnly = false;
            bool changed = query != text.Trim() || viewNeedsRefresh;
            SetQuery(text.Trim());
            candidate = !changed && candidate is not null && choices.Contains(candidate) ? candidate :
                query.Length == 0 ? null : choices.Cast<FontChoice>().FirstOrDefault();
            SelectedItem = candidate;
            RestoreEditor(saved);
        }
        finally { updating--; }
    }

    internal void ShowAllFonts()
    {
        var saved = CaptureEditor();
        updating++;
        try { search.Stop(); SetQuery(""); RestoreEditor(saved); }
        finally { updating--; }
    }

    internal void SelectFont(string name)
    {
        // A panel echo must not overwrite the active search or an unmatched empty
        // display. Only a different model value starts a new synchronization.
        if (StringComparer.OrdinalIgnoreCase.Equals(committed, name)) return;
        updating++;
        try
        {
            search.Stop(); inputVersion++;
            var item = catalog.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, name));
            if (item is null)
            {
                using var resolved = SkiaSharp.SKTypeface.FromFamilyName(name) ?? SkiaSharp.SKTypeface.FromFamilyName("Segoe UI");
                item = new(name, L.Format("Audit.MissingFont", name, resolved.FamilyName), new FontFamily("Segoe UI"), false);
                catalog.Insert(0, item); viewNeedsRefresh = true;
            }
            committed = name; displayed = candidate = item;
            mode = PickerMode.Display; IsReadOnly = true; CloseDropdown(returnFocus: false);
            SetQuery(""); SelectedItem = item; SetDisplayText();
        }
        finally { updating--; }
    }

    internal void ClearFontSelection()
    {
        committed = ""; displayed = candidate = null;
        RestoreDisplay(closePopup: true);
    }

    internal void ResetModelContext()
    {
        CancelDraft(returnFocus: false);
        committed = null; inputVersion++;
    }

    private void SetQuery(string value, bool refresh = true)
    {
        bool changed = query != value || viewNeedsRefresh;
        query = value;
        if (!changed) return;
        if (!refresh) { viewNeedsRefresh = true; return; }
        choices.Refresh(); viewNeedsRefresh = false;
    }

    private EditorState CaptureEditor(string? text = null)
    {
        string value = text ?? editor?.Text ?? Text;
        return editor is not null && editor.Text == value
            ? new(value, editor.SelectionStart, editor.SelectionLength) : new(value, value.Length, 0);
    }

    private void RestoreEditor(EditorState saved)
    {
        if (Text != saved.Text) SetCurrentValue(TextProperty, saved.Text);
        if (editor is null) return;
        if (editor.Text != saved.Text) editor.Text = saved.Text;
        int start = Math.Clamp(saved.Start, 0, saved.Text.Length);
        editor.Select(start, Math.Clamp(saved.Length, 0, saved.Text.Length - start));
    }

    private void SetDisplayText()
    {
        string text = displayed?.DisplayName ?? "";
        if (Text != text) SetCurrentValue(TextProperty, text);
        if (editor is not null && editor.Text != text) editor.Text = text;
        ClearDisplaySelection();
        System.Windows.Automation.AutomationProperties.SetHelpText(this, displayed is { Installed: false } ? displayed.DisplayName : "");
    }

    private void ClearDisplaySelection()
    {
        // WPF also selects all when focus returns after the dropdown closes.
        // Only the active search editor should expose a text selection.
        if (mode != PickerMode.Search && editor is { SelectionLength: > 0 }) editor.Select(editor.SelectionStart, 0);
    }

    private void CloseDropdown(bool returnFocus)
    {
        // An external focus destination already owns the next interaction.
        // Closing its old hosted popup must not asynchronously focus us again.
        if (!returnFocus && Template?.FindName("PART_Popup", this) is OverlayPopup popup) popup.Close(returnFocus: false);
        IsDropDownOpen = false;
    }

    private void RestoreDisplay(bool closePopup, bool refreshView = true, bool returnFocus = true)
    {
        updating++;
        try
        {
            search.Stop(); inputVersion++; mode = PickerMode.Display; IsReadOnly = true;
            if (closePopup) CloseDropdown(returnFocus);
            SetQuery("", refreshView);
            candidate = displayed;
            SelectedItem = displayed;
            SetDisplayText();
        }
        finally { updating--; }
    }

    private void CommitCandidate(bool closePopup = true, bool returnFocus = true)
    {
        if (mode == PickerMode.Search) ApplySearch(editor?.Text ?? Text);
        Commit(candidate, closePopup, returnFocus);
    }

    private void Commit(FontChoice? font, bool closePopup = true, bool returnFocus = true)
    {
        bool changed = font is not null && !StringComparer.OrdinalIgnoreCase.Equals(committed, font.Name);
        if (font is not null) committed = font.Name;
        displayed = candidate = font;
        RestoreDisplay(closePopup, returnFocus: returnFocus);
        // Candidate selection and model publication have separate lifetimes. In
        // particular, Enter may commit the already-selected first search result.
        if (changed) FontCommitted?.Invoke(font!.Name);
    }

    private void OpenSearch()
    {
        var saved = CaptureEditor();
        openingSearch = true; updating++;
        try { IsDropDownOpen = true; RestoreEditor(saved); }
        finally { updating--; openingSearch = false; }
    }

    private void BeginSearch()
    {
        if (updating != 0 || !IsLoaded || !IsVisible || editor is null) return;
        bool entering = mode != PickerMode.Search;
        inputVersion++;
        ApplySearch(editor.Text);
        OpenSearch();
        if (entering) editor.SelectAll();
    }

    private void EditorEntered(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Popup focus return is internal. Only a real entry from another control
        // (or an explicit editor click below) starts a fresh search session.
        // ComboBox may first focus itself, then forward to its editable TextBox.
        if ((ReferenceEquals(e.NewFocus, editor) || ReferenceEquals(e.NewFocus, this)) && e.OldFocus is DependencyObject previous &&
            !OwnsInput(previous) && mode != PickerMode.Browse) BeginSearch();
    }

    private void EnterBrowse()
    {
        if (mode == PickerMode.Search) CommitCandidate(closePopup: false);
        updating++;
        try
        {
            mode = PickerMode.Browse; IsReadOnly = true;
            ShowAllFonts(); candidate = displayed;
            SelectedItem = displayed; SetDisplayText();
        }
        finally { updating--; }
    }

    protected override DependencyObject GetContainerForItemOverride() => new FontChoiceItem();

    private sealed class FontChoiceItem : ComboBoxItem
    {
        protected override void OnContentChanged(object oldContent, object newContent)
        {
            if (ItemsControlFromItemContainer(this) is not FontPickerControl { mode: PickerMode.Search } picker)
            { base.OnContentChanged(oldContent, newContent); return; }
            // Recycling a selected container calls ComboBox.SelectedItemUpdated
            // directly, bypassing SelectionChanged. It must not replace the draft.
            var saved = picker.CaptureEditor();
            picker.updating++;
            try { base.OnContentChanged(oldContent, newContent); picker.RestoreEditor(saved); }
            finally { picker.updating--; }
        }
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem container && item is FontChoice font)
        {
            container.Tag = font.Name;
            container.ToolTip = NameHint(font.Name, font.Installed ? font.DisplayName : font.DisplayName + "\n" + L.Get("Audit.ReplaceFont"));
            TextSearch.SetText(container, font.DisplayName);
            container.RemoveHandler(MouseLeftButtonUpEvent, candidateMouseUp);
            container.AddHandler(MouseLeftButtonUpEvent, candidateMouseUp, handledEventsToo: true);
        }
    }

    private static void CandidateMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left && sender is ComboBoxItem { Content: FontChoice font } item &&
            ItemsControl.ItemsControlFromItemContainer(item) is FontPickerControl picker && picker.IsVisible)
            picker.Commit(font);
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        bool preview = updating != 0;
        var saved = CaptureEditor();
        bool wasRefresh = IsSelectionRefresh;
        IsSelectionRefresh = preview;
        updating++;
        try
        {
            base.OnSelectionChanged(e);
            if (preview && mode == PickerMode.Search) RestoreEditor(saved);
        }
        finally { updating--; IsSelectionRefresh = wasRefresh; }
        if (!preview && SelectedItem is FontChoice font && e.AddedItems.Count > 0) Commit(font);
    }

    protected override void OnDropDownOpened(EventArgs e)
    {
        if (!openingSearch) EnterBrowse();
        base.OnDropDownOpened(e);
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        if (updating == 0)
        {
            if (mode == PickerMode.Search && IsLoaded && IsVisible) CommitCandidate(closePopup: false);
            else if (mode != PickerMode.Display) RestoreDisplay(closePopup: false, refreshView: IsVisible);
        }
        base.OnDropDownClosed(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        // Commit before the destination's Click can change the active tool/model.
        // Focus within the list or its scrollbar is still part of this picker.
        if (updating == 0 && mode == PickerMode.Search && !OwnsInput(e.NewFocus as DependencyObject))
        {
            if (!IsLoaded || !IsVisible) RestoreDisplay(closePopup: true, refreshView: false, returnFocus: false);
            else if (e.NewFocus is not null) CommitCandidate(returnFocus: false);
            else
            {
                search.Stop(); int version = inputVersion;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (version == inputVersion && mode == PickerMode.Search && IsLoaded && IsVisible &&
                        !OwnsInput(Keyboard.FocusedElement as DependencyObject)) CommitCandidate(returnFocus: false);
                });
            }
        }
        base.OnLostKeyboardFocus(e);
    }

    private bool OwnsInput(DependencyObject? source)
    {
        if (source is null) return false;
        if (OverlayPopupWindow.BelongsTo(source, this)) return true;
        UIElement? child = Template?.FindName("PART_Popup", this) switch
        {
            Popup popup => popup.Child,
            OverlayPopup popup => popup.Child,
            _ => null
        };
        return child is not null && OverlayPopupWindow.BelongsTo(source, child);
    }

    internal bool CancelDraft(bool returnFocus = true)
    {
        if (mode == PickerMode.Display && !IsDropDownOpen && !search.IsEnabled) return false;
        RestoreDisplay(closePopup: true, returnFocus: returnFocus);
        return true;
    }

    internal static bool TryCancelForInput(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FontPickerControl picker) return picker.CancelDraft();
            source = source switch
            {
                ComboBoxItem item => ItemsControl.ItemsControlFromItemContainer(item),
                FrameworkElement { TemplatedParent: FontPickerControl ownerPicker } => ownerPicker,
                _ => source is Visual ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source)
            };
        }
        return false;
    }

    private void PickerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        for (var source = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject; source is not null && !ReferenceEquals(source, this);)
        {
            if (source is ToggleButton button && ReferenceEquals(button.TemplatedParent, this))
            {
                bool keepOpen = IsDropDownOpen && mode == PickerMode.Search;
                EnterBrowse();
                if (keepOpen) { e.Handled = true; return; }
                break;
            }
            if (ReferenceEquals(source, editor))
            {
                bool activating = mode != PickerMode.Search;
                if (mode == PickerMode.Browse) RestoreDisplay(closePopup: true);
                BeginSearch();
                if (activating) e.Handled = true;
                break;
            }
            source = source is Visual ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && IsEditable)
        {
            if (mode == PickerMode.Display) ApplySearch(editor?.Text ?? Text);
            CommitCandidate(); e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up && mode != PickerMode.Display && (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) == 0)
        {
            if (mode == PickerMode.Search) ApplySearch(editor?.Text ?? Text);
            int index = candidate is null ? -1 : Items.IndexOf(candidate);
            int next = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, Items.Count - 1));
            var saved = CaptureEditor();
            updating++;
            try
            {
                candidate = Items.Count == 0 ? null : Items[next] as FontChoice;
                SelectedItem = candidate; RestoreEditor(saved);
                if (mode == PickerMode.Search) OpenSearch();
            }
            finally { updating--; }
            e.Handled = true;
        }
        else if (e.Key is Key.PageDown or Key.PageUp && mode != PickerMode.Display && (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) == 0)
        {
            if (mode == PickerMode.Search) ApplySearch(editor?.Text ?? Text);
            var saved = CaptureEditor();
            updating++;
            try
            {
                // Keep WPF's viewport-aware paging, but treat its selection as a
                // candidate until Enter, a click or external focus commits it.
                base.OnKeyDown(e);
                candidate = SelectedItem as FontChoice;
                RestoreEditor(saved);
            }
            finally { updating--; }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && CancelDraft()) e.Handled = true;
        if (!e.Handled) base.OnPreviewKeyDown(e);
    }

    private static FontChoice CreateChoice(string name, FontFamily font)
    {
        string friendly = font.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag), out string? localized) ? localized : name;
        return new(name, friendly, font, true);
    }

    private static ToolTip NameHint(string name, string friendly)
    {
        var text = new TextBlock { Text = friendly == name ? name : friendly + "\n" + name,
            FontFamily = PropertyPanelLayout.LabelFontFamily, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "CanvasText");
        return new ToolTip { Content = text };
    }
}
