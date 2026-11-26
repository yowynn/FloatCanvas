using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class EmojiPickerWindowTests
{
    internal static void Run(Action<string,bool> check)
    {
        if (PresentationDesktopAvailability.IsCurrentInputDesktop()) return;
        HistoryReordering(check);
        var language=L.Preference;
        var lastCategoryField=typeof(EmojiPickerPanel).GetField("lastCategory",BindingFlags.Static|BindingFlags.NonPublic)!;
        object? previousCategory=lastCategoryField.GetValue(null);
        try
        {
            string originalProfile=ApplicationPaths.ProfileDirectory;
            try
            {
                string isolatedProfile=Path.Combine(Path.GetTempPath(),"fc-emoji-font-lazy-"+Guid.NewGuid().ToString("N"));
                ApplicationPaths.Configure(["--profile",isolatedProfile]);
                var lazyPicker=new EmojiFontPicker();
                var notoItem=(ComboBoxItem)lazyPicker.Items[1];
                check("emoji font picker constructor defers availability probes",
                    Equals(notoItem.Content,L.Get("Emoji.Font.Noto")));
                lazyPicker.RefreshAvailability();
                check("emoji font picker refreshes missing styles when its dropdown is opened",
                    Equals(notoItem.Content,L.Format("Emoji.Font.Unavailable",L.Get("Emoji.Font.Noto")))&&!notoItem.IsEnabled);
            }
            finally { ApplicationPaths.Configure(["--profile",originalProfile]); }
            foreach(var (locale,theme) in new[]{("zh-CN","dark"),("en","light")})
            {
                L.Initialize(locale); AppTheme.Apply(theme);
                lastCategoryField.SetValue(null,null);
                var editor=new EditorController(new()) { Tool=EditorTool.Emoji };
                string tmp=Path.Combine(Path.GetTempPath(),"fc-emoji-picker-"+Guid.NewGuid().ToString("N"));
                var tool=new ToolOptionsPanel(editor,new(Path.Combine(tmp,"presets.json")),new(Path.Combine(tmp,"colors.json")));
                var owner=new Window{Content=tool,Width=699,Height=620,Left=40,Top=40,ShowActivated=false,ShowInTaskbar=false};
                AppWindowChrome.Apply(owner);
                try
                {
                    owner.Show();tool.PrepareForShow();Pump();
                    var field=Field<EmojiField>(tool,"emojiField");
                    var library=Field<EmojiPickerPanel>(tool,"emojiLibrary");
                    var inlineGrid=Field<ListBox>(library,"grid");
                    check(locale+" emoji tool embeds the browser without opening a child window",library.IsVisible &&
                        Field<Button>(field,"choose").Visibility==Visibility.Collapsed &&
                        owner.OwnedWindows.Cast<Window>().All(w=>!w.IsVisible));
                    var guardedField=new EmojiField();
                    var guardedWindow=new Window{Content=guardedField,Width=420,Height=220,ShowActivated=false,ShowInTaskbar=false};
                    guardedWindow.Show();guardedField.SetModel("😀",true);Pump();
                    var guardedChoose=Field<Button>(guardedField,"choose");
                    guardedChoose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                    check(locale+" programmatic and UI Automation button invocation opens the emoji picker",guardedField.IsOpen);
                    guardedField.Close();Until(()=>!guardedField.IsOpen);guardedWindow.Close();
                    var historyPanel=new EmojiPickerPanel(inline:true);
                    historyPanel.Begin("🚀");
                    var historyGrid=Field<ListBox>(historyPanel,"grid");
                    if(historyGrid.Items.Count>1)
                    {
                        object recentSource=historyGrid.ItemsSource;
                        var second=(EmojiEntry)historyGrid.Items[1];
                        typeof(EmojiPickerPanel).GetMethod("Choose",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(historyPanel,[second]);
                        Pump();
                        check(locale+" choosing from an open recent list reorders it in place",ReferenceEquals(historyGrid.ItemsSource,recentSource)&&
                            ReferenceEquals(historyGrid.Items[0],second));
                    }
                    Field<List<Button>>(library,"categories")[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                    check(locale+" embedded category browser is populated",inlineGrid.Items.Count>100);
                    string embeddedHeading=Field<TextBlock>(library,"heading").Text;
                    Until(()=>Visuals(inlineGrid).OfType<EmojiThumbnail>().Any() &&
                        Visuals(inlineGrid).OfType<EmojiThumbnail>().All(image=>image.Source is not null));
                    owner.Width=638;owner.Height=560;tool.PrepareForShow();owner.UpdateLayout();Pump();
                    check(locale+" embedded emoji browser remains usable at the minimum tool width",
                        library.ActualWidth>300&&inlineGrid.ActualHeight>150&&
                        Field<TextBox>(library,"search").ActualWidth>100);
                    owner.Width=699;owner.Height=620;tool.PrepareForShow();owner.UpdateLayout();Pump();
                    if(Environment.GetEnvironmentVariable("FLOATCANVAS_EMOJI_TEST_OUTPUT") is {Length:>0} inlineOutput)
                        Save(owner,Path.Combine(inlineOutput,"tool-inline-"+locale+".png"));
                    field.Open();Pump();
                    var window=owner.OwnedWindows.Cast<Window>().Single(w=>w.IsVisible);
                    var panel=Field<EmojiPickerPanel>(field,"picker");
                    var grid=Field<ListBox>(panel,"grid");var search=Field<TextBox>(panel,"search");
                    var tone=Field<ComboBox>(panel,"tones");
                    check(locale+" skin tone choices expose exactly the six rendered hand variants",tone.Items.Count==6 && Equals(tone.SelectedValue,0));
                    tone.IsDropDownOpen=true;Pump();
                    var tonePopup=(System.Windows.Controls.Primitives.Popup)tone.Template.FindName("PART_Popup",tone);
                    Until(()=>Visuals(tonePopup.Child).OfType<EmojiThumbnail>().Count()==6 &&
                        Visuals(tonePopup.Child).OfType<EmojiThumbnail>().All(image=>image.Source is not null));
                    check(locale+" all six skin tone menu items render color hand artwork",Visuals(tonePopup.Child).OfType<EmojiThumbnail>()
                        .Select(image=>image.DataContext).OfType<EmojiEntry>().Select(entry=>entry.Glyph).SequenceEqual(new[]{"✋","✋🏻","✋🏼","✋🏽","✋🏾","✋🏿"}));
                    tone.IsDropDownOpen=false;Pump();
                    Until(()=>Visuals(tone).OfType<EmojiThumbnail>().Any(image=>image.Source is not null));
                    check(locale+" the collapsed skin tone selector also keeps a color icon",Visuals(tone).OfType<EmojiThumbnail>().Any(image=>image.Source is not null));
                    check(locale+" opening a separate picker retains the last category",
                        Field<TextBlock>(panel,"heading").Text==embeddedHeading && embeddedHeading!=L.Get("Emoji.Recent"));
                    panel.SetCurrent(null);Pump();
                    check(locale+" footer stays empty when no emoji is current",Field<TextBlock>(panel,"name").Text.Length==0 &&
                        Field<EmojiThumbnail>(panel,"preview").DataContext is null);
                    panel.SetCurrent(editor.Emoji);Pump();
                    Until(()=>Visuals(grid).OfType<EmojiThumbnail>().Any(i=>i.Source!=null));
                    var selected=(Grid)window.Template.FindName("TitleBar",window);
                    int realizedTiles = Visuals(grid).OfType<ListBoxItem>().Count();
                    int tileColumns = Math.Max(1, (int)Math.Floor((grid.ActualWidth + .01) / 35));
                    int maximumRealizedRows = (int)Math.Ceiling(grid.ActualHeight / 37) + 2;
                    check(locale+" compact picker opens with its owner, one search row and bounded virtualized tiles",
                        selected.Visibility==Visibility.Collapsed && window.ActualWidth<430 &&
                        VirtualizingPanel.GetIsVirtualizing(grid) && grid.Items.Count>realizedTiles &&
                        realizedTiles<=tileColumns*maximumRealizedRows);
                    panel.UpdateLayout();
                    var searchHint=Field<TextBlock>(panel,"searchHint");
                    double textStart=search.GetRectFromCharacterIndex(0,false).Left;
                    double hintStart=searchHint.TranslatePoint(new Point(0,0),search).X;
                    check(locale+" search caret and placeholder start on the same baseline origin",Math.Abs(textStart-hintStart)<=1);
                    if(Environment.GetEnvironmentVariable("FLOATCANVAS_EMOJI_TEST_OUTPUT") is {Length:>0} output)
                    {
                        Until(()=>Visuals(grid).OfType<EmojiThumbnail>().All(i=>i.Source!=null));
                        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
                        bitmap.Render(window); var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory(output);
                        using var f=File.Create(Path.Combine(output,"picker-"+locale+".png"));png.Save(f);
                    }
                    var categories=Field<List<Button>>(panel,"categories");
                    tone.SelectedIndex=0;
                    int total=0;
                    foreach(var b in categories.Skip(1)){b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));total+=grid.Items.Count;}
                    int expected=Enum.GetValues<EmojiGroup>().Sum(group=>EmojiCatalog.Search("",group,0).Count);
                    check(locale+" category navigation matches the default-tone catalog",total==expected);
                    if(EmojiFontSources.IsAvailable(EmojiFontIds.NotoColor) && !EmojiGlyphs.CanRender("🫫",EmojiFontIds.SystemSegoe))
                    {
                        search.Text="🫫";grid.UpdateLayout();Pump();
                        Until(()=>grid.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item &&
                            Visuals(item).OfType<EmojiThumbnail>().Any(image=>ReferenceEquals(image.Source,EmojiArtwork.Placeholder)));
                        var unsupportedTile=(ListBoxItem)grid.ItemContainerGenerator.ContainerFromIndex(0)!;
                        var placeholder=EmojiArtwork.Placeholder;
                        int left=placeholder.PixelWidth,top=placeholder.PixelHeight,right=-1,bottom=-1;
                        var pixels=new byte[placeholder.PixelWidth*placeholder.PixelHeight*4];
                        placeholder.CopyPixels(pixels,placeholder.PixelWidth*4,0);
                        for(int y=5;y<placeholder.PixelHeight-5;y++)
                            for(int x=5;x<placeholder.PixelWidth-5;x++)
                            {
                                int offset=(y*placeholder.PixelWidth+x)*4;
                                if(pixels[offset]<0x70||pixels[offset+1]<0x80||pixels[offset+2]<0x90)continue;
                                left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
                            }
                        check(locale+" unsupported glyph uses a generic selectable placeholder in the current style",
                            unsupportedTile.IsEnabled && Visuals(unsupportedTile).OfType<EmojiThumbnail>()
                                .Any(image=>ReferenceEquals(image.Source,EmojiArtwork.Placeholder)) &&
                            right>=left && Math.Abs((left+right+1)/2d-placeholder.PixelWidth/2d)<=1 &&
                            Math.Abs((top+bottom+1)/2d-placeholder.PixelHeight/2d)<=1);
                        unsupportedTile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
                        unsupportedTile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});Pump();
                        var selectedImage=Field<EmojiThumbnail>(field,"image");
                        Until(()=>selectedImage.Source is not null);
                        check(locale+" selected unsupported glyph uses the fallback artwork outside the picker",editor.Emoji=="🫫"&&!field.IsOpen&&
                            !ReferenceEquals(selectedImage.Source,EmojiArtwork.Placeholder));
                        field.Open();Pump();
                    }
                    search.Text="not-a-real-emoji-948";check(locale+" no-match state",grid.Items.Count==0);
                    search.Text="rocket";check(locale+" English lookup works in either locale",((EmojiEntry)grid.Items[0]).Glyph=="🚀");
                    search.Text="火箭";KeyPress(search,Key.Enter);Pump();
                    check(locale+" Enter selects the top bilingual search result and returns to the tool",editor.Emoji=="🚀" && !field.IsOpen && owner.IsVisible);
                    field.Open();Pump();
                    check(locale+" reopening after a cross-category search shows the selected emoji's category",
                        Field<TextBlock>(panel,"heading").Text==L.Get("Emoji.Group."+EmojiGroup.Travel)&&
                        ((EmojiEntry)grid.SelectedItem).Glyph=="🚀");
                    categories[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(locale+" recent category remembers applied entries",((EmojiEntry)grid.Items[0]).Glyph=="🚀");
                    categories[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));grid.UpdateLayout();Pump();
                    var hovered=(EmojiEntry)grid.Items[0];
                    var hoveredTile=(ListBoxItem)grid.ItemContainerGenerator.ContainerFromIndex(0)!;
                    hoveredTile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseMoveEvent});
                    check(locale+" hovered emoji owns the footer preview",Field<TextBlock>(panel,"name").Text==EmojiPickerPanel.DisplayName(hovered));
                    grid.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseMoveEvent});Pump();
                    var selectedEmoji=EmojiCatalog.Search("🚀",EmojiGroup.Travel,0).Single();
                    check(locale+" moving off a tile restores the current emoji immediately",Field<TextBlock>(panel,"name").Text==EmojiPickerPanel.DisplayName(selectedEmoji));
                    grid.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseLeaveEvent});Pump();
                    check(locale+" leaving the grid restores the current emoji instead of the last hover",Field<TextBlock>(panel,"name").Text==EmojiPickerPanel.DisplayName(selectedEmoji));
                    search.Text="🫱🏻‍🫲🏿";grid.UpdateLayout();Pump();
                    var mixedEntry=(EmojiEntry)grid.Items[0];
                    var tile=(ListBoxItem)grid.ItemContainerGenerator.ContainerFromIndex(0)!;
                    tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
                    tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});Pump();
                    check(locale+" choosing a mixed-tone tile applies immediately",editor.Emoji=="🫱🏻‍🫲🏿"&&!field.IsOpen);
                    field.Open();Pump();KeyPress(search,Key.Escape,Keyboard.KeyDownEvent);Pump();
                    check(locale+" Escape closes only the picker",owner.IsVisible&&!field.IsOpen&&editor.Emoji=="🫱🏻‍🫲🏿");
                    check(locale+" reopening the picker retains the category used before it closed",
                        Field<TextBlock>(panel,"heading").Text==L.Get("Emoji.Group."+mixedEntry.Group));
                    field.Open();Pump();editor.Tool=EditorTool.Text;tool.PrepareForShow();Pump();
                    check(locale+" switching tools dismisses its previous picker",!field.IsOpen);
                }
                finally {owner.Close();Pump();}
            }
        }
        finally
        {
            lastCategoryField.SetValue(null,previousCategory);
            L.Initialize(language);
        }
    }

    private static void HistoryReordering(Action<string,bool> check)
    {
        string originalProfile=ApplicationPaths.ProfileDirectory;
        string profile=Path.Combine(Path.GetTempPath(),"fc-emoji-history-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ApplicationPaths.Configure(["--profile",profile]);
        string path=Path.Combine(profile,"emoji-history.json");
        var store=new EmojiHistoryStore(path);
        store.Remember("😀");store.Remember("🚀");store.Remember("🔥");
        var shared=typeof(EmojiHistoryStore).GetField("shared",BindingFlags.Static|BindingFlags.NonPublic)!;
        object? previous=shared.GetValue(null);
        var panel=new EmojiPickerPanel(inline:true);
        var window=new Window{Content=panel,Width=400,Height=420,Left=40,Top=40,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            shared.SetValue(null,store);
            window.Show();panel.Begin("😀");panel.UpdateLayout();Pump();
            var grid=Field<ListBox>(panel,"grid");
            Until(()=>grid.Items.Count==3&&grid.ItemContainerGenerator.ContainerFromIndex(1) is ListBoxItem);
            object source=grid.ItemsSource;
            var second=(EmojiEntry)grid.Items[1];
            var tile=(ListBoxItem)grid.ItemContainerGenerator.ContainerFromIndex(1)!;
            tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
            tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            panel.UpdateLayout();Pump();
            check("choosing a realized recent emoji atomically keeps its virtualized list usable",
                ReferenceEquals(grid.ItemsSource,source)&&ReferenceEquals(grid.Items[0],second)&&grid.Items.Count==3);
        }
        finally
        {
            window.Close();Pump();
            shared.SetValue(null,previous);
            store.Flush();
            ApplicationPaths.Configure(["--profile",originalProfile]);
        }
    }
    private static void KeyPress(UIElement element,Key key,RoutedEvent? route=null) =>
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(element),Environment.TickCount,key){RoutedEvent=route??Keyboard.PreviewKeyDownEvent});
    private static void Until(Func<bool> ready)
    {
        var time=Stopwatch.StartNew();while(!ready()&&time.ElapsedMilliseconds<6000){Pump();Thread.Sleep(2);}
        if(!ready())throw new TimeoutException("Emoji tiles did not finish loading");
    }
    private static void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    private static void Save(Window window,string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);
    }
    private static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var nested in Visuals(child))yield return nested;
        }
    }
}
