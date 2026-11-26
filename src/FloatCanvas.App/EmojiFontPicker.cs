using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class EmojiFontPicker : OverlayComboBox
{
    private bool watchingSources;
    private bool assigningValue, revertingSelection;
    private int availabilityGeneration;
    private UIElement? dropDownContent;
    private Button? pressedDownload;
    private static readonly (string Id, string LabelKey)[] Options =
    [
        (EmojiFontIds.SystemSegoe, "Emoji.Font.System"),
        (EmojiFontIds.NotoColor, "Emoji.Font.Noto"),
        (EmojiFontIds.OpenMojiColor, "Emoji.Font.OpenMoji"),
        (EmojiFontIds.BlobmojiColor, "Emoji.Font.Blobmoji")
    ];

    internal EmojiFontPicker()
    {
        SelectedValuePath = nameof(ComboBoxItem.Tag);
        IsTextSearchEnabled = false;
        MinWidth = 210;
        ToolTip = L.Get("Emoji.Font.Hint");
        foreach (var option in Options)
            Items.Add(new FontOption(option.Id, L.Get(option.LabelKey)));
        DropDownOpened += async (_, _) =>
        {
            DetachDownloadInput();
            dropDownContent = Template?.FindName("PART_Popup", this) switch
            {
                OverlayPopup hosted => hosted.Child,
                Popup native => native.Child,
                _ => null
            };
            if (dropDownContent is { } content)
            {
                content.PreviewMouseLeftButtonDown += OnDownloadPress;
                content.PreviewMouseLeftButtonUp += OnDownloadRelease;
            }
            await RefreshAvailabilityAsync();
        };
        DropDownClosed += (_, _) => DetachDownloadInput();
        Loaded += (_, _) =>
        {
            if (watchingSources) return;
            watchingSources = true;
            EmojiFontSources.Changed += SourcesChanged;
        };
        Unloaded += (_, _) =>
        {
            DetachDownloadInput();
            if (!watchingSources) return;
            watchingSources = false;
            EmojiFontSources.Changed -= SourcesChanged;
        };
    }

    internal string Value => EmojiFontIds.Normalize((SelectedItem as ComboBoxItem)?.Tag as string);

    internal void SetValue(string? id)
    {
        string next = EmojiFontIds.Normalize(id);
        assigningValue = true;
        try { SelectedItem = Items.Cast<FontOption>().Single(item => Equals(item.Tag, next)); }
        finally { assigningValue = false; }
        _ = RefreshAvailabilityAsync(IsDropDownOpen ? null : next);
    }

    internal void ClearValue() => SelectedIndex = -1;

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        if (revertingSelection) return;
        if (!assigningValue && SelectedItem is FontOption { Available: false })
        {
            revertingSelection = true;
            try { SelectedItem = e.RemovedItems.Count == 0 ? null : e.RemovedItems[0]; }
            finally { revertingSelection = false; }
            return;
        }
        base.OnSelectionChanged(e);
    }

    internal void RefreshAvailability(string? onlyId = null)
    {
        availabilityGeneration++;
        foreach (FontOption item in Items)
        {
            string id = (string)item.Tag;
            if (onlyId is not null && id != onlyId) continue;
            item.SetAvailable(EmojiFontSources.IsAvailable(id));
        }
    }

    private async Task RefreshAvailabilityAsync(string? onlyId = null)
    {
        int generation = ++availabilityGeneration;
        try
        {
            var requested = onlyId is null ? Options : Options.Where(option => option.Id == onlyId).ToArray();
            var availability = await Task.Run(() => requested.ToDictionary(option => option.Id, option => EmojiFontSources.IsAvailable(option.Id)));
            if (!IsLoaded || generation != availabilityGeneration) return;
            foreach (FontOption item in Items)
                if (availability.TryGetValue((string)item.Tag, out bool available)) item.SetAvailable(available);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (generation == availabilityGeneration) RefreshAvailability();
        }
    }

    private void SourcesChanged()
    {
        if (Dispatcher.CheckAccess()) _ = RefreshAvailabilityAsync();
        else Dispatcher.BeginInvoke(() => _ = RefreshAvailabilityAsync());
    }

    private Button? DownloadTarget(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject hit) return null;
        return Items.Cast<FontOption>().Select(option => option.DownloadButton).FirstOrDefault(candidate =>
            candidate is { IsEnabled: true } && OverlayPopupWindow.BelongsTo(hit, candidate));
    }

    private void OnDownloadPress(object sender, MouseButtonEventArgs e)
    {
        pressedDownload = DownloadTarget(e);
        // The combo already captures the dropdown. Keep that capture through the
        // gesture instead of letting the nested button transfer native focus/capture.
        if (pressedDownload is not null) e.Handled = true;
    }

    private void OnDownloadRelease(object sender, MouseButtonEventArgs e)
    {
        var pressed = pressedDownload;
        pressedDownload = null;
        if (pressed is null) return;
        e.Handled = true;
        if (ReferenceEquals(pressed, DownloadTarget(e))) pressed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, pressed));
    }

    private void DetachDownloadInput()
    {
        if (dropDownContent is not null)
        {
            dropDownContent.PreviewMouseLeftButtonDown -= OnDownloadPress;
            dropDownContent.PreviewMouseLeftButtonUp -= OnDownloadRelease;
            dropDownContent = null;
        }
        pressedDownload = null;
    }

    private sealed class FontOption : ComboBoxItem
    {
        private readonly string id, label;
        private Button? button;
        private bool downloading;
        private string? downloadError;
        internal bool Available { get; private set; }
        internal bool RequiresDownload { get; private set; }
        internal Button? DownloadButton => RequiresDownload ? button : null;

        internal FontOption(string id, string label)
        {
            this.id = id; this.label = label;
            Tag = id; Content = label;
            SetResourceReference(StyleProperty, typeof(ComboBoxItem));
        }

        internal void SetAvailable(bool available)
        {
            Available = available;
            bool needsDownload = !available && EmojiFontDownloads.CanDownload(id);
            IsEnabled = available || needsDownload;
            Content = available ? label : L.Format("Emoji.Font.Unavailable", label);
            if (RequiresDownload == needsDownload) return;
            RequiresDownload = needsDownload;
            if (needsDownload) SetResourceReference(TemplateProperty, "CanvasEmojiFontDownloadItem");
            else ClearValue(TemplateProperty);
        }

        public override void OnApplyTemplate()
        {
            if (button is not null) button.Click -= Download;
            base.OnApplyTemplate();
            button = GetTemplateChild("Download") as Button;
            if (button is null) return;
            button.Click += Download;
            UpdateDownloadButton();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (RequiresDownload) e.Handled = true;
            else base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (RequiresDownload) e.Handled = true;
            else base.OnMouseLeftButtonUp(e);
        }

        private async void Download(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!RequiresDownload || downloading) return;
            downloading = true;
            downloadError = null;
            UpdateDownloadButton();
            try
            {
                await EmojiFontDownloads.InstallAsync(id);
                SetAvailable(EmojiFontSources.IsAvailable(id));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
            { downloadError = ex.Message; }
            finally
            {
                downloading = false;
                UpdateDownloadButton();
            }
        }

        private void UpdateDownloadButton()
        {
            if (button is null) return;
            button.IsEnabled = !downloading;
            button.Content = L.Get(downloading ? "Emoji.Font.Downloading" :
                downloadError is null ? "Emoji.Font.Download" : "Emoji.Font.DownloadFailed");
            button.ToolTip = downloadError;
            AutomationProperties.SetName(button, L.Format("Emoji.Font.DownloadNamed", label));
        }
    }
}
