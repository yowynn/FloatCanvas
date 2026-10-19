using System.IO;
using System.Windows.Markup;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class StampShelfControl : Controls.UserControl
{
    private readonly StampLibrary library;
    private readonly Action<Guid> selectStamp;
    internal Func<Guid,int,Controls.ContextMenu>? EntryMenuFactory { get; set; }
    private readonly Controls.StackPanel slots = new() { Orientation = Controls.Orientation.Horizontal };
    private Guid? active;
    private long revision;
    private bool subscribed;
    private bool showShortcutHints = true;
    private static readonly Wpf.Controls.ControlTemplate SlotTemplate = (Wpf.Controls.ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Grid Background="Transparent"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid>
        </ControlTemplate>
        """);

    public StampShelfControl(StampLibrary library, Action<Guid> selectStamp)
    {
        this.library = library; this.selectStamp = selectStamp;
        Content = slots;
        Loaded += (_, _) => { if (!subscribed) { library.Changed += OnChanged; subscribed = true; } Refresh(); };
        Unloaded += (_, _) => { library.Changed -= OnChanged; subscribed = false; revision++; };
        Refresh();
    }
    public void SetActive(Guid? id) { if (active == id) return; active = id; Refresh(); }
    public void SetShortcutHintsVisible(bool visible)
    {
        showShortcutHints = visible;
        foreach (var hint in slots.Children.OfType<Controls.Button>().Select(button => button.Content).OfType<Controls.Grid>()
            .SelectMany(content => content.Children.OfType<Controls.TextBlock>()))
            hint.Visibility = visible ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
    }
    public void Refresh()
    {
        long version = ++revision;
        slots.Children.Clear();
        for (int index = 0; index < 10; index++)
        {
            int slotIndex = index;
            string digit = ((index + 1) % 10).ToString();
            var entry = library.Items.FirstOrDefault(item => item.Id == library.Pins[index]);
            var content = new Controls.Grid { Width = 26, Height = 26 };
            bool selected = active == entry?.Id && entry is not null;
            var frame = new Controls.Border { Width=26,Height=26,CornerRadius = new Wpf.CornerRadius(3), BorderThickness = new Wpf.Thickness(1), Opacity = selected ? 1 : entry is null ? .10 : .38 };
            frame.SetResourceReference(Controls.Border.BorderBrushProperty, selected ? "CanvasAccent" : "CanvasMuted");
            content.Children.Add(frame);
            var image = new Controls.Image { Width = 20, Height = 20, Stretch = Media.Stretch.Uniform,
                HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center };
            content.Children.Add(image);
            var number = ToolbarShortcutHint.Create(digit, lowerRow:true);
            number.Visibility=showShortcutHints?Wpf.Visibility.Visible:Wpf.Visibility.Collapsed;content.Children.Add(number);
            var button = new Controls.Button { Content = content, Width = 30, Height = 26, MinWidth = 30, MinHeight = 26,
                Padding = new Wpf.Thickness(0), Margin = new Wpf.Thickness(0), Template = SlotTemplate,
                ToolTip = entry is null ? digit + " · " + L.Get("Stamp.EmptySlot") : StampPreviewTooltip.Create(library, entry, digit) };
            button.FocusVisualStyle=null;Controls.ToolTipService.SetShowsToolTipOnKeyboardFocus(button,false);
            Wpf.Automation.AutomationProperties.SetName(button, digit + " " + (entry?.Name ?? L.Get("Stamp.EmptySlot")));
            if (entry is not null)
            {
                Guid id = entry.Id;
                button.Click += (_, _) => selectStamp(id);
                var menu = new Controls.ContextMenu();
                var clear = new Controls.MenuItem { Header = L.Get("Stamp.Unpin"), Icon = ActionIconButton.Glyph(PanelActionIcon.Unpin) };
                clear.Click += async (_, _) => { try { await library.PinAsync(slotIndex, null); } catch (Exception ex) { button.ToolTip = ex.Message; } };
                menu.Items.Add(clear); button.ContextMenu = menu;
                button.ContextMenuOpening+=(_,e)=>
                {
                    if(EntryMenuFactory is null)return;
                    e.Handled=true;
                    var actions=EntryMenuFactory(id,slotIndex);actions.PlacementTarget=button;
                    button.ContextMenu=actions;actions.IsOpen=true;
                };
                LoadThumbnail(entry, image, button, version);
            }
            slots.Children.Add(button);
        }
    }
    private void OnChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh(); else Dispatcher.BeginInvoke(Refresh);
    }
    private async void LoadThumbnail(StampEntry entry, Controls.Image image, Controls.Button button, long version)
    {
        try
        {
            byte[] bytes = await library.GetThumbnailAsync(entry.Id);
            if (version != revision || !StampPreviewTooltip.IsCurrent(library, entry)) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            image.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { if (version == revision) button.ToolTip = ex.Message; }
    }
}
