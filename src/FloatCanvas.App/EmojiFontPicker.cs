using System.IO;
using System.Windows.Controls;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class EmojiFontPicker : OverlayComboBox
{
    private bool watchingSources;
    private int availabilityGeneration;
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
            Items.Add(new ComboBoxItem { Tag = option.Id, Content = L.Get(option.LabelKey) });
        DropDownOpened += async (_, _) => await RefreshAvailabilityAsync();
        Loaded += (_, _) =>
        {
            if (watchingSources) return;
            watchingSources = true;
            EmojiFontSources.Changed += SourcesChanged;
        };
        Unloaded += (_, _) =>
        {
            if (!watchingSources) return;
            watchingSources = false;
            EmojiFontSources.Changed -= SourcesChanged;
        };
    }

    internal string Value => EmojiFontIds.Normalize(SelectedValue as string);

    internal void SetValue(string? id)
    {
        string next = EmojiFontIds.Normalize(id);
        SelectedValue = next;
        _ = RefreshAvailabilityAsync(next);
    }

    internal void ClearValue() => SelectedIndex = -1;

    internal void RefreshAvailability(string? onlyId = null)
    {
        availabilityGeneration++;
        foreach (ComboBoxItem item in Items)
        {
            string id = (string)item.Tag;
            if (onlyId is not null && id != onlyId) continue;
            string key = Options.Single(option => option.Id == id).LabelKey;
            bool available = EmojiFontSources.IsAvailable(id);
            item.IsEnabled = available;
            item.Content = available ? L.Get(key) : L.Format("Emoji.Font.Unavailable", L.Get(key));
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
            foreach (ComboBoxItem item in Items)
            {
                string id = (string)item.Tag;
                if (!availability.ContainsKey(id)) continue;
                string key = Options.Single(option => option.Id == id).LabelKey;
                bool available = availability[id];
                item.IsEnabled = available;
                item.Content = available ? L.Get(key) : L.Format("Emoji.Font.Unavailable", L.Get(key));
            }
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
}
