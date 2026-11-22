using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One compact picker for a tool or a layer. Only visible tiles request artwork.</summary>
internal sealed class EmojiPickerPanel : StackPanel
{
    public static readonly DependencyProperty CurrentFontIdProperty = DependencyProperty.Register(
        nameof(CurrentFontId), typeof(string), typeof(EmojiPickerPanel), new PropertyMetadata(EmojiFontIds.Default));
    private static IReadOnlyList<EmojiEntry> Recent => EmojiHistoryStore.Shared.Entries;
    private readonly TextBox search = new();
    private readonly TextBlock searchHint = new() { IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox tones = new OverlayComboBox { MinWidth = 0, Width = 134 };
    private readonly ListBox grid = new() { Height = 215, BorderThickness = new(0), Padding = new(0), Background = Brushes.Transparent };
    private readonly UniformGrid navigation = new() { Rows = 1 };
    private readonly TextBlock heading = new() { FontWeight = FontWeights.SemiBold }, count = new(), empty = new() { TextWrapping = TextWrapping.Wrap };
    private readonly EmojiThumbnail preview = new() { Width = 30, Height = 30 };
    private readonly TextBlock name = new(), otherName = new() { FontSize = 11 };
    private readonly List<Button> categories = [];
    private EmojiEntry? pressed, current;
    private EmojiGroup? category;
    private string? renderedView;
    private bool pointerOverGrid;
    private bool resetting;
    internal event Action<EmojiEntry>? Picked;
    internal event Action? CloseRequested;
    internal bool HasOpenMenu => tones.IsDropDownOpen;
    public string CurrentFontId
    {
        get => (string)GetValue(CurrentFontIdProperty);
        private set => SetValue(CurrentFontIdProperty, EmojiFontIds.Normalize(value));
    }
    private static bool Chinese => L.CurrentLanguage == "zh-CN";
    internal static string DisplayName(EmojiEntry entry) => Chinese ? entry.ChineseName : entry.EnglishName;
    internal static void Remember(EmojiEntry entry)
    {
        EmojiHistoryStore.Shared.Remember(entry.Glyph);
    }

    internal EmojiPickerPanel(bool inline = false)
    {
        Width = inline ? double.NaN : 380;
        Margin = inline ? new(0,6,0,0) : new(8);
        HorizontalAlignment = inline ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        grid.Height = inline ? 252 : 215;
        var bar = new Grid { Margin = new(0, 0, 0, 6) };
        bar.ColumnDefinitions.Add(new()); bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var searchHost = new Grid();
        string prompt = L.Get("Emoji.Search");
        search.Padding = new(8, 0, 6, 0); search.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(search, prompt);
        searchHint.Text = prompt;
        searchHint.Margin = new(search.Padding.Left, 0, 5, 0);
        searchHint.VerticalAlignment = VerticalAlignment.Center;
        searchHint.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        searchHost.Children.Add(search); searchHost.Children.Add(searchHint); bar.Children.Add(searchHost);
        tones.Margin = new(6, 0, 0, 0); tones.Width = inline ? 148 : 134; tones.SelectedValuePath=nameof(ToneChoice.Tone);
        AutomationProperties.SetName(tones,L.Get("Emoji.SkinTone"));
        tones.ItemsSource=new[] { 0, 1, 2, 3, 4, 5 }.Select(tone =>
        {
            string glyph=tone switch {0=>"✋",1=>"✋🏻",2=>"✋🏼",3=>"✋🏽",4=>"✋🏾",_=>"✋🏿"};
            EmojiCatalog.TryFind(glyph,out var entry);
            return new ToneChoice(tone,entry,L.Get("Emoji.Tone"+tone));
        }).ToArray();
        var toneRow=new FrameworkElementFactory(typeof(StackPanel));
        toneRow.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
        toneRow.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        var toneImage=new FrameworkElementFactory(typeof(EmojiThumbnail));
        toneImage.SetBinding(FrameworkElement.DataContextProperty,new Binding(nameof(ToneChoice.Entry)));
        toneImage.SetValue(FrameworkElement.WidthProperty,18d); toneImage.SetValue(FrameworkElement.HeightProperty,18d);
        toneImage.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,5,0));
        var toneLabel=new FrameworkElementFactory(typeof(TextBlock));
        toneLabel.SetBinding(TextBlock.TextProperty,new Binding(nameof(ToneChoice.Label)));
        toneLabel.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        toneRow.AppendChild(toneImage); toneRow.AppendChild(toneLabel);
        tones.ItemTemplate=new DataTemplate { VisualTree=toneRow };
        tones.SelectedIndex = 0; Grid.SetColumn(tones,1); bar.Children.Add(tones);
        if (!inline)
        {
            var close = ActionIconButton.Create(PanelActionIcon.Close, L.Get("Window.Close"), () => CloseRequested?.Invoke());
            Grid.SetColumn(close,2); bar.Children.Add(close);
        }
        Children.Add(bar);
        AddCategory(null, "M2,3 V7 H6 M2.4,6.5 A6,6 0 1 1 3,11 M8,4 V8 L11,10");
        string[] icons = [
            LayerTypeIcon.Geometry(ElementKind.Emoji),
            "M3,8 V5 Q4,4 5,5 V8 V3 Q6,2 7,3 V7 V2 Q8,1 9,2 V7 V3 Q10,2 11,3 V8 L13,6 Q15,6 14,8 L12,13 Q9,17 5,13 Z",
            "M4,9 Q2,7 4,6 Q6,6 6,8 M7,5 Q6,2 8,2 Q10,2 9,5 M11,6 Q11,3 13,4 Q15,5 13,7 M4,12 Q8,5 11,10 Q16,15 11,14 Q8,13 6,14 Q2,15 4,12 Z",
            "M8,5 C3,1 0,7 4,13 Q6,16 8,14 Q11,16 13,12 C17,4 12,2 8,5 Z M8,5 Q8,1 12,1",
            "M1,8 L7,7 L12,2 Q15,0 14,3 L10,8 L13,13 L12,14 L8,11 L5,14 L3,13 L5,9 Z",
            "M4,2 H12 V6 Q12,10 8,10 Q4,10 4,6 Z M4,3 H1 Q1,7 5,7 M12,3 H15 Q15,7 11,7 M8,10 V14 M5,14 H11",
            "M5,10 C0,5 4,1 8,1 C13,1 16,6 11,10 L10,12 H6 Z M6,14 H10",
            "M6,2 L4,14 M12,2 L10,14 M2,6 H15 M1,10 H14",
            "M3,15 V2 Q6,0 9,2 Q12,4 15,2 V10 Q12,12 9,10 Q6,8 3,10"
        ];
        foreach (var group in Enum.GetValues<EmojiGroup>()) AddCategory(group, icons[(int)group]);
        Children.Add(navigation);
        var labels = new DockPanel { Margin = new(4,7,4,4) };
        count.SetResourceReference(TextBlock.ForegroundProperty,"CanvasMuted"); DockPanel.SetDock(count,Dock.Right);
        labels.Children.Add(count); labels.Children.Add(heading); Children.Add(labels);
        var tiles = new FrameworkElementFactory(typeof(VirtualizingTilePanel));
        tiles.SetValue(VirtualizingTilePanel.TileWidthProperty,35d); tiles.SetValue(VirtualizingTilePanel.RowHeightProperty,37d);
        grid.ItemsPanel = new ItemsPanelTemplate(tiles);
        ScrollViewer.SetCanContentScroll(grid,true); ScrollViewer.SetHorizontalScrollBarVisibility(grid,ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(grid,true); VirtualizingPanel.SetVirtualizationMode(grid,VirtualizationMode.Recycling);
        var image = new FrameworkElementFactory(typeof(EmojiThumbnail));
        image.SetValue(WidthProperty,26d); image.SetValue(HeightProperty,26d);
        image.SetValue(EmojiThumbnail.AllowFallbackProperty,false);
        image.SetBinding(EmojiThumbnail.FontIdProperty,new Binding(nameof(CurrentFontId)){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(EmojiPickerPanel),1)});
        grid.ItemTemplate = new DataTemplate { VisualTree = image };
        string display = Chinese ? nameof(EmojiEntry.ChineseName) : nameof(EmojiEntry.EnglishName);
        var style = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem)));
        style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(1))); style.Setters.Add(new Setter(MarginProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty,new Binding(display))); style.Setters.Add(new Setter(ToolTipProperty,new Binding(display)));
        style.Setters.Add(new EventSetter(UIElement.PreviewMouseLeftButtonDownEvent,new MouseButtonEventHandler((sender,_) =>
            pressed=(sender as ListBoxItem)?.Content as EmojiEntry)));
        style.Setters.Add(new EventSetter(UIElement.PreviewMouseLeftButtonUpEvent,new MouseButtonEventHandler((sender,e) =>
        {
            var released=(sender as ListBoxItem)?.Content as EmojiEntry;
            if (released is null || released!=pressed) return;
            e.Handled=true; pressed=null; Choose(released);
        })));
        grid.ItemContainerStyle = style; AutomationProperties.SetName(grid,L.Get("Tool.Emoji"));
        var body = new Grid(); body.Children.Add(grid);
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.IsHitTestVisible = false;
        empty.SetResourceReference(TextBlock.ForegroundProperty,"CanvasMuted"); body.Children.Add(empty); Children.Add(body);
        var footer = new DockPanel { Margin = new(4,7,4,0), MinHeight = 38 };
        preview.Margin = new(0,0,10,0); footer.Children.Add(preview);
        var names = new StackPanel { VerticalAlignment=VerticalAlignment.Center };
        otherName.SetResourceReference(TextBlock.ForegroundProperty,"CanvasMuted");
        name.TextTrimming = otherName.TextTrimming = TextTrimming.CharacterEllipsis;
        names.Children.Add(name); names.Children.Add(otherName); footer.Children.Add(names); Children.Add(footer);

        search.Loaded += (_,_)=>AlignSearchHint();
        search.SizeChanged += (_,_)=>AlignSearchHint();
        search.TextChanged += (_, _) => { AlignSearchHint(); searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refresh(); };
        tones.SelectionChanged += (_,_) => Refresh();
        search.PreviewKeyDown += (_,e) =>
        {
            if (grid.Items.Count == 0) return;
            if (e.Key == Key.Enter) { e.Handled = true; Choose((EmojiEntry)grid.Items[0]); }
            else if (e.Key == Key.Down) { e.Handled = true; grid.SelectedIndex=0; grid.ScrollIntoView(grid.SelectedItem); grid.UpdateLayout(); (grid.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(); }
        };
        grid.MouseEnter += (_,_) => { pointerOverGrid=true;ShowName(current,true); };
        grid.MouseMove += (_,e) =>
        {
            pointerOverGrid=true;
            var hovered=At(e.OriginalSource);
            ShowName(hovered ?? current,hovered is null);
        };
        grid.MouseLeave += (_,_) => { pointerOverGrid=false;ShowName(current,true); };
        grid.SelectionChanged += (_,_) => { if (!pointerOverGrid && grid.SelectedItem is EmojiEntry entry) ShowName(entry,true); };
        grid.PreviewKeyDown += (_,e) => { if (e.Key is Key.Enter or Key.Space && grid.SelectedItem is EmojiEntry entry) { e.Handled=true; Choose(entry); } };
    }

    private EmojiEntry? At(object source) => source is DependencyObject child && ItemsControl.ContainerFromElement(grid,child) is ListBoxItem item ? item.Content as EmojiEntry : null;
    private void AlignSearchHint()
    {
        if(!search.IsLoaded)return;
        search.UpdateLayout();
        double left=search.GetRectFromCharacterIndex(0,false).Left;
        if(!double.IsFinite(left)||left<0)left=search.Padding.Left;
        if(Math.Abs(searchHint.Margin.Left-left)>=.1)searchHint.Margin=new(left,0,5,0);
    }
    private void AddCategory(EmojiGroup? group, string icon)
    {
        string name = L.Get(group is null ? "Emoji.Recent" : "Emoji.Group." + group);
        var button = ActionIconButton.Create(LayersWindow.IconPath(icon),name,() =>
        {
            resetting=true; category=group; search.Clear(); renderedView=null; resetting=false; Refresh(true);
        });
        button.Width=double.NaN; button.Height=30; button.Margin=new(1); button.Tag=group;
        categories.Add(button);navigation.Children.Add(button);
    }
    internal void Begin(string? value, string? emojiFontId = null)
    {
        SetFont(emojiFontId);
        resetting=true; search.Clear(); category=null; renderedView=null; resetting=false;
        pointerOverGrid=false;
        SetCurrent(value);
        Refresh(true);
    }
    internal void SetFont(string? emojiFontId)
    {
        string next=EmojiFontIds.Normalize(emojiFontId);
        if(CurrentFontId==next)return;
        CurrentFontId=next;preview.SetFont(next);
        if(grid.ItemsSource is not null)Refresh(true);
    }
    internal void SetCurrent(string? value)
    {
        EmojiCatalog.TryFind(value,out current);
        if (grid.ItemsSource is not null) grid.SelectedItem=grid.Items.Contains(current)?current:null;
        if(!pointerOverGrid) ShowName(current,true);
    }
    internal void FocusSearch() => search.Focus();
    private void Choose(EmojiEntry entry)
    {
        bool recentVisible=category is null&&string.IsNullOrWhiteSpace(search.Text);
        current=entry;
        Remember(entry);
        if(recentVisible&&grid.ItemsSource is ObservableCollection<EmojiEntry> recent)
        {
            int index=recent.IndexOf(entry);
            if(index<0)recent.Insert(0,entry);
            else if(index>0)recent.Move(index,0);
            while(recent.Count>EmojiHistoryStore.MaximumEntries)recent.RemoveAt(recent.Count-1);
            grid.SelectedItem=entry;
        }
        ShowName(entry,true);
        Picked?.Invoke(entry);
    }
    private void Refresh(bool force=false)
    {
        if (resetting) return;
        bool searching=!string.IsNullOrWhiteSpace(search.Text);
        int tone=tones.SelectedValue is int selectedTone?selectedTone:0;
        string view=string.Join('\u001f',searching?search.Text.Trim():"",category?.ToString()??"recent",tone.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach(var button in categories)
            ActionIconButton.UpdateToggleAppearance(button,!searching && Equals(button.Tag,category));
        if(!force && view==renderedView && grid.ItemsSource is not null)
        {
            count.Text=L.Format("Emoji.Count",grid.Items.Count);
            if(!pointerOverGrid) ShowName(current,true);
            return;
        }
        renderedView=view;
        bool recentVisible=!searching&&category is null;
        var results=recentVisible ? Recent :
            EmojiCatalog.Search(search.Text,category ?? EmojiGroup.Smileys,tone);
        heading.Text=L.Get(searching?"Emoji.Results":category is null?"Emoji.Recent":"Emoji.Group."+category);
        count.Text=L.Format("Emoji.Count",results.Count);
        grid.ItemsSource=recentVisible?new ObservableCollection<EmojiEntry>(results):results; pressed=null;
        grid.SelectedItem=results.Contains(current)?current:null;
        if(results.Count>0) grid.ScrollIntoView(results[0]);
        empty.Text=L.Get(!searching && category is null ? "Emoji.NoRecent" : "Emoji.NoResults");
        empty.Visibility=results.Count==0?Visibility.Visible:Visibility.Collapsed;
        if(!pointerOverGrid) ShowName(current,true);
    }
    private void ShowName(EmojiEntry? entry,bool allowFallback)
    {
        preview.AllowFallback=allowFallback;
        if (ReferenceEquals(preview.DataContext,entry) && name.Text.Length>0) return;
        preview.SetFont(CurrentFontId);
        preview.DataContext=entry; name.Text=entry is null?"":DisplayName(entry);
        otherName.Text=entry is null?"":Chinese?entry.EnglishName:entry.ChineseName;
    }
    private sealed record ToneChoice(int Tone, EmojiEntry? Entry, string Label);
}

internal sealed class EmojiThumbnail : Image
{
    public static readonly DependencyProperty FontIdProperty = DependencyProperty.Register(
        nameof(FontId), typeof(string), typeof(EmojiThumbnail),
        new PropertyMetadata(EmojiFontIds.Default,(sender,_) => ((EmojiThumbnail)sender).Load()));
    public static readonly DependencyProperty AllowFallbackProperty = DependencyProperty.Register(
        nameof(AllowFallback), typeof(bool), typeof(EmojiThumbnail),
        new PropertyMetadata(true,(sender,_) => ((EmojiThumbnail)sender).Load()));
    private static readonly SemaphoreSlim Workers = new(2);
    private CancellationTokenSource? pending;
    public EmojiThumbnail()
    {
        IsHitTestVisible=false; Stretch=Stretch.Uniform;
        DataContextChanged += (_,_) => Load(); Loaded += (_,_) => Load();
        Unloaded += (_,_) => { pending?.Cancel(); Source=null; };
    }
    public string FontId
    {
        get => (string)GetValue(FontIdProperty);
        set => SetValue(FontIdProperty,EmojiFontIds.Normalize(value));
    }
    public bool AllowFallback
    {
        get => (bool)GetValue(AllowFallbackProperty);
        set => SetValue(AllowFallbackProperty,value);
    }
    internal void SetFont(string fontId) => FontId=EmojiFontIds.Normalize(fontId);
    private async void Load()
    {
        string fontId=FontId;
        bool allowFallback=AllowFallback;
        pending?.Cancel(); Source=null;
        if (!IsLoaded || DataContext is not EmojiEntry entry) return;
        using var request = new CancellationTokenSource(); pending=request;
        try
        {
            await Workers.WaitAsync(request.Token);
            try
            {
                var image=await Task.Run(()=>EmojiArtwork.Image(entry.Glyph,fontId,allowFallback),request.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!request.IsCancellationRequested) Source=image;
                });
            }
            finally { Workers.Release(); }
        }
        catch(OperationCanceledException) when(request.IsCancellationRequested) { }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or InvalidOperationException or OverflowException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!request.IsCancellationRequested) Source=EmojiArtwork.Placeholder;
            });
        }
        finally { if (pending==request) pending=null; }
    }
}
